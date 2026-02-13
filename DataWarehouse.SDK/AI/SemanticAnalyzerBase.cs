using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.AI
{
    /// <summary>
    /// Abstract base class for semantic analyzers providing common implementations.
    /// Plugins should extend this class and override specific methods as needed.
    /// </summary>
    public abstract class SemanticAnalyzerBase : ISemanticAnalyzer, IContentCategorizer, IEntityExtractor,
        IPIIDetector, IContentSummarizer, ILanguageDetector, ISentimentAnalyzer, IDuplicateDetector,
        IRelationshipDiscoverer, INaturalLanguageQueryParser, IDisposable
    {
        #region Fields

        /// <summary>AI provider for enhanced analysis (optional).</summary>
        protected IAIProvider? AIProvider { get; private set; }

        /// <summary>Vector store for embedding operations (optional).</summary>
        protected IVectorStore? VectorStore { get; private set; }

        /// <summary>Knowledge graph for relationships (optional).</summary>
        protected IKnowledgeGraph? KnowledgeGraph { get; private set; }

        /// <summary>Vector operations helper.</summary>
        protected IVectorOperations VectorOps { get; } = new DefaultVectorOperations();

        // Category registry
        private readonly ConcurrentDictionary<string, CategoryDefinition> _categories = new();
        private readonly ConcurrentDictionary<string, List<float[]>> _categoryEmbeddings = new();

        // Custom entity types
        private readonly ConcurrentDictionary<string, CustomEntityTypeDefinition> _customEntityTypes = new();

        // Custom PII patterns
        private readonly List<CustomPIIPatternDefinition> _customPIIPatterns = new();
        private readonly object _piiPatternLock = new();

        // Statistics
        private long _documentsProcessed;
        private long _entitiesExtracted;
        private long _piiDetected;

        private bool _disposed;

        #endregion

        #region Configuration

        /// <summary>
        /// Configures the AI provider for enhanced analysis.
        /// </summary>
        public virtual void SetAIProvider(IAIProvider? provider)
        {
            AIProvider = provider;
        }

        /// <summary>
        /// Configures the vector store for embedding operations.
        /// </summary>
        public virtual void SetVectorStore(IVectorStore? store)
        {
            VectorStore = store;
        }

        /// <summary>
        /// Configures the knowledge graph for relationship operations.
        /// </summary>
        public virtual void SetKnowledgeGraph(IKnowledgeGraph? graph)
        {
            KnowledgeGraph = graph;
        }

        /// <inheritdoc/>
        public virtual bool IsAIAvailable => AIProvider?.IsAvailable ?? false;

        #endregion

        #region ISemanticAnalyzer Implementation

        /// <inheritdoc/>
        public virtual async Task<SemanticAnalysisResult> AnalyzeAsync(
            string content,
            SemanticAnalysisOptions? options = null,
            CancellationToken ct = default)
        {
            options ??= new SemanticAnalysisOptions();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var usedAI = false;

            try
            {
                var result = new SemanticAnalysisResult { Success = true };

                // Run analyses in parallel where possible
                var tasks = new List<Task>();

                CategorizationResult? categorization = null;
                EntityExtractionResult? entities = null;
                SentimentResult? sentiment = null;
                LanguageDetectionResult? language = null;
                SummaryResult? summary = null;
                PIIDetectionResult? pii = null;

                if (options.IncludeLanguage)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        language = await DetectLanguageAsync(content, new LanguageDetectionOptions { UseAI = options.PreferAI }, ct);
                        if (language.UsedAI) usedAI = true;
                    }, ct));
                }

                if (options.IncludeCategorization)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        categorization = await CategorizeAsync(content, new CategorizationOptions { PreferAI = options.PreferAI, MinConfidence = options.MinConfidence }, ct);
                        if (categorization.UsedAI) usedAI = true;
                    }, ct));
                }

                if (options.IncludeEntities)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        entities = await ExtractEntitiesAsync(content, new EntityExtractionOptions { UseAI = options.PreferAI, MinConfidence = options.MinConfidence }, ct);
                        if (entities.UsedAI) usedAI = true;
                    }, ct));
                }

                if (options.IncludeSentiment)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        sentiment = await AnalyzeSentimentAsync(content, new SentimentOptions { UseAI = options.PreferAI }, ct);
                        if (sentiment.UsedAI) usedAI = true;
                    }, ct));
                }

                if (options.IncludeSummary)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        summary = await SummarizeAsync(content, new SummaryOptions { PreferAbstractive = options.PreferAI }, ct);
                        if (summary.UsedAI) usedAI = true;
                    }, ct));
                }

                if (options.IncludePII)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        pii = await DetectPIIAsync(content, new PIIDetectionOptions { UseAI = options.PreferAI, MinConfidence = options.MinConfidence }, ct);
                        if (pii.UsedAI) usedAI = true;
                    }, ct));
                }

                await Task.WhenAll(tasks);

                Interlocked.Increment(ref _documentsProcessed);
                sw.Stop();

                return result with
                {
                    Language = language,
                    Categorization = categorization,
                    Entities = entities,
                    Sentiment = sentiment,
                    Summary = summary,
                    PII = pii,
                    Duration = sw.Elapsed,
                    UsedAI = usedAI
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new SemanticAnalysisResult
                {
                    Success = false,
                    Error = ex.Message,
                    Duration = sw.Elapsed
                };
            }
        }

        /// <inheritdoc/>
        public virtual async Task<SemanticAnalysisResult> AnalyzeStreamAsync(
            Stream content,
            string contentType,
            SemanticAnalysisOptions? options = null,
            CancellationToken ct = default)
        {
            // Default implementation reads stream as text
            using var reader = new StreamReader(content);
            var text = await reader.ReadToEndAsync(ct);
            return await AnalyzeAsync(text, options, ct);
        }

        /// <inheritdoc/>
        public virtual async Task<IReadOnlyList<SemanticAnalysisResult>> AnalyzeBatchAsync(
            IEnumerable<string> contents,
            SemanticAnalysisOptions? options = null,
            CancellationToken ct = default)
        {
            var results = new ConcurrentBag<(int Index, SemanticAnalysisResult Result)>();
            var contentList = contents.ToList();

            await Parallel.ForEachAsync(
                contentList.Select((c, i) => (Content: c, Index: i)),
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                async (item, token) =>
                {
                    var result = await AnalyzeAsync(item.Content, options, token);
                    results.Add((item.Index, result));
                });

            return results.OrderBy(r => r.Index).Select(r => r.Result).ToList();
        }

        #endregion

        #region IContentCategorizer Implementation

        /// <inheritdoc/>
        public virtual async Task<CategorizationResult> CategorizeAsync(
            string content,
            CategorizationOptions? options = null,
            CancellationToken ct = default)
        {
            options ??= new CategorizationOptions();
            var usedAI = false;

            // Try AI-based categorization first if preferred and available
            if (options.PreferAI && IsAIAvailable)
            {
                try
                {
                    var aiResult = await CategorizeWithAIAsync(content, options, ct);
                    if (aiResult != null && aiResult.Categories.Count > 0)
                    {
                        return aiResult with { UsedAI = true };
                    }
                }
                catch
                {
                    // Fall back to embedding-based
                }
            }

            // Embedding-based categorization
            if (VectorStore != null && _categoryEmbeddings.Any())
            {
                try
                {
                    var result = await CategorizeWithEmbeddingsAsync(content, options, ct);
                    if (result.Categories.Count > 0)
                        return result;
                }
                catch
                {
                    // Fall back to keyword-based
                }
            }

            // Keyword-based categorization (always available)
            return CategorizeWithKeywords(content, options);
        }

        /// <summary>
        /// AI-based categorization. Override in derived class for custom implementation.
        /// </summary>
        protected virtual async Task<CategorizationResult?> CategorizeWithAIAsync(
            string content,
            CategorizationOptions options,
            CancellationToken ct)
        {
            if (AIProvider == null) return null;

            var categoryList = string.Join(", ", _categories.Values.Take(50).Select(c => c.Name));
            var prompt = $"Categorize the following content into one or more of these categories: {categoryList}\n\nContent:\n{content.Substring(0, Math.Min(2000, content.Length))}\n\nRespond with category names and confidence scores (0-1) in format: CategoryName:0.95";

            var response = await AIProvider.CompleteAsync(new AIRequest
            {
                Prompt = prompt,
                MaxTokens = 200,
                Temperature = 0.3f
            }, ct);

            if (!response.Success) return null;

            var matches = ParseCategoryResponse(response.Content);
            return new CategorizationResult
            {
                PrimaryCategory = matches.FirstOrDefault(),
                Categories = matches,
                Confidence = matches.FirstOrDefault()?.Confidence ?? 0,
                UsedAI = true
            };
        }

        /// <summary>
        /// Embedding-based categorization using vector similarity.
        /// </summary>
        protected virtual async Task<CategorizationResult> CategorizeWithEmbeddingsAsync(
            string content,
            CategorizationOptions options,
            CancellationToken ct)
        {
            if (AIProvider == null || !_categoryEmbeddings.Any())
                return new CategorizationResult();

            // Get content embedding
            var embedding = await AIProvider.GetEmbeddingsAsync(content.Substring(0, Math.Min(8000, content.Length)), ct);

            // Find most similar categories
            var matches = new List<CategoryMatch>();
            foreach (var (categoryId, embeddings) in _categoryEmbeddings)
            {
                if (!_categories.TryGetValue(categoryId, out var category))
                    continue;

                // Average similarity across category examples
                var avgSimilarity = embeddings.Average(e => VectorOps.CosineSimilarity(embedding, e));

                if (avgSimilarity >= options.MinConfidence)
                {
                    matches.Add(new CategoryMatch
                    {
                        Id = categoryId,
                        Name = category.Name,
                        Path = GetCategoryPath(categoryId),
                        Confidence = avgSimilarity
                    });
                }
            }

            matches = matches.OrderByDescending(m => m.Confidence).Take(options.MaxCategories).ToList();

            return new CategorizationResult
            {
                PrimaryCategory = matches.FirstOrDefault(),
                Categories = matches,
                Confidence = matches.FirstOrDefault()?.Confidence ?? 0
            };
        }

        /// <summary>
        /// Keyword-based categorization (fallback).
        /// </summary>
        protected virtual CategorizationResult CategorizeWithKeywords(string content, CategorizationOptions options)
        {
            var contentLower = content.ToLowerInvariant();
            var matches = new List<CategoryMatch>();

            foreach (var category in _categories.Values)
            {
                if (category.Keywords == null || category.Keywords.Count == 0)
                    continue;

                var matchCount = category.Keywords.Count(k => contentLower.Contains(k.ToLowerInvariant()));
                if (matchCount > 0)
                {
                    var confidence = (float)matchCount / category.Keywords.Count;
                    if (confidence >= options.MinConfidence)
                    {
                        matches.Add(new CategoryMatch
                        {
                            Id = category.Id,
                            Name = category.Name,
                            Path = GetCategoryPath(category.Id),
                            Confidence = confidence
                        });
                    }
                }
            }

            matches = matches.OrderByDescending(m => m.Confidence).Take(options.MaxCategories).ToList();

            return new CategorizationResult
            {
                PrimaryCategory = matches.FirstOrDefault(),
                Categories = matches,
                Confidence = matches.FirstOrDefault()?.Confidence ?? 0,
                UsedAI = false
            };
        }

        /// <inheritdoc/>
        public virtual async Task TrainCategoryAsync(
            string categoryId,
            IEnumerable<string> examples,
            CancellationToken ct = default)
        {
            if (AIProvider == null || !AIProvider.Capabilities.HasFlag(AICapabilities.Embeddings))
                return;

            var embeddings = new List<float[]>();
            foreach (var example in examples)
            {
                var embedding = await AIProvider.GetEmbeddingsAsync(example, ct);
                embeddings.Add(embedding);
            }

            _categoryEmbeddings[categoryId] = embeddings;
        }

        /// <inheritdoc/>
        public virtual void RegisterCategory(CategoryDefinition category)
        {
            _categories[category.Id] = category;
        }

        /// <inheritdoc/>
        public virtual IReadOnlyList<CategoryDefinition> GetCategories()
        {
            return _categories.Values.ToList();
        }

        private IReadOnlyList<string> GetCategoryPath(string categoryId)
        {
            var path = new List<string>();
            var current = categoryId;

            while (_categories.TryGetValue(current, out var category))
            {
                path.Insert(0, category.Name);
                if (string.IsNullOrEmpty(category.ParentId))
                    break;
                current = category.ParentId;
            }

            return path;
        }

        private List<CategoryMatch> ParseCategoryResponse(string response)
        {
            var matches = new List<CategoryMatch>();
            var pattern = @"(\w+[\w\s]*):?\s*(\d*\.?\d+)";

            foreach (Match match in Regex.Matches(response, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100)))
            {
                var categoryName = match.Groups[1].Value.Trim();
                if (float.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var confidence))
                {
                    var category = _categories.Values.FirstOrDefault(c =>
                        c.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase));

                    if (category != null)
                    {
                        matches.Add(new CategoryMatch
                        {
                            Id = category.Id,
                            Name = category.Name,
                            Path = GetCategoryPath(category.Id),
                            Confidence = Math.Min(1f, confidence)
                        });
                    }
                }
            }

            return matches;
        }

        #endregion

        #region IEntityExtractor Implementation

        // Standard entity patterns
        private static readonly Dictionary<string, Regex> EntityPatterns = new()
        {
            ["EMAIL"] = new Regex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,}\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100)),
            ["PHONE"] = new Regex(@"\b(?:\+?1[-.\s]?)?\(?\d{3}\)?[-.\s]?\d{3}[-.\s]?\d{4}\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100)),
            ["DATE"] = new Regex(@"\b(?:\d{1,2}[-/]\d{1,2}[-/]\d{2,4}|\d{4}[-/]\d{1,2}[-/]\d{1,2}|(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\.?\s+\d{1,2},?\s+\d{4})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)),
            ["URL"] = new Regex(@"\bhttps?://[^\s<>""]+\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100)),
            ["MONEY"] = new Regex(@"\$\s*\d+(?:,\d{3})*(?:\.\d{2})?\b|\b\d+(?:,\d{3})*(?:\.\d{2})?\s*(?:USD|EUR|GBP|JPY)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)),
            ["PERCENTAGE"] = new Regex(@"\b\d+(?:\.\d+)?%\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100))
        };

        /// <inheritdoc/>
        public virtual async Task<EntityExtractionResult> ExtractEntitiesAsync(
            string content,
            EntityExtractionOptions? options = null,
            CancellationToken ct = default)
        {
            options ??= new EntityExtractionOptions();
            var entities = new List<ExtractedEntity>();
            var usedAI = false;

            // Pattern-based extraction (always run)
            var patternEntities = ExtractEntitiesWithPatterns(content, options);
            entities.AddRange(patternEntities);

            // AI-enhanced extraction
            if (options.UseAI && IsAIAvailable)
            {
                try
                {
                    var aiEntities = await ExtractEntitiesWithAIAsync(content, options, ct);
                    usedAI = true;

                    // Merge AI entities (avoid duplicates)
                    foreach (var aiEntity in aiEntities)
                    {
                        if (!entities.Any(e => e.Text == aiEntity.Text && e.Type == aiEntity.Type))
                        {
                            entities.Add(aiEntity);
                        }
                    }
                }
                catch
                {
                    // Continue with pattern-based results
                }
            }

            // Filter by confidence and requested types
            if (options.EntityTypes != null && options.EntityTypes.Count > 0)
            {
                entities = entities.Where(e => options.EntityTypes.Contains(e.Type)).ToList();
            }
            entities = entities.Where(e => e.Confidence >= options.MinConfidence).ToList();

            Interlocked.Add(ref _entitiesExtracted, entities.Count);

            var countsByType = entities.GroupBy(e => e.Type)
                .ToDictionary(g => g.Key, g => g.Count());

            return new EntityExtractionResult
            {
                Entities = entities,
                CountsByType = countsByType,
                UsedAI = usedAI
            };
        }

        /// <summary>
        /// Pattern-based entity extraction.
        /// </summary>
        protected virtual List<ExtractedEntity> ExtractEntitiesWithPatterns(string content, EntityExtractionOptions options)
        {
            var entities = new List<ExtractedEntity>();

            foreach (var (type, pattern) in EntityPatterns)
            {
                foreach (Match match in pattern.Matches(content))
                {
                    var context = options.IncludeContext
                        ? GetContext(content, match.Index, match.Length, options.ContextWindowSize)
                        : null;

                    entities.Add(new ExtractedEntity
                    {
                        Text = match.Value,
                        Type = type,
                        StartIndex = match.Index,
                        EndIndex = match.Index + match.Length,
                        Confidence = 0.9f,
                        Context = context
                    });
                }
            }

            // Custom entity types
            foreach (var customType in _customEntityTypes.Values)
            {
                if (customType.Patterns == null) continue;

                foreach (var patternStr in customType.Patterns)
                {
                    try
                    {
                        var regex = new Regex(patternStr, RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
                        foreach (Match match in regex.Matches(content))
                        {
                            entities.Add(new ExtractedEntity
                            {
                                Text = match.Value,
                                Type = customType.Name,
                                StartIndex = match.Index,
                                EndIndex = match.Index + match.Length,
                                Confidence = 0.85f,
                                Context = options.IncludeContext
                                    ? GetContext(content, match.Index, match.Length, options.ContextWindowSize)
                                    : null
                            });
                        }
                    }
                    catch { /* Invalid regex pattern */ }
                }
            }

            return entities;
        }

        /// <summary>
        /// AI-enhanced entity extraction. Override for custom implementation.
        /// </summary>
        protected virtual async Task<List<ExtractedEntity>> ExtractEntitiesWithAIAsync(
            string content,
            EntityExtractionOptions options,
            CancellationToken ct)
        {
            if (AIProvider == null) return new List<ExtractedEntity>();

            var prompt = $@"Extract named entities from the following text. For each entity, provide:
- Text: the exact text
- Type: PERSON, ORGANIZATION, LOCATION, DATE, MONEY, PRODUCT, EVENT, or OTHER
- Confidence: 0.0 to 1.0

Text:
{content.Substring(0, Math.Min(3000, content.Length))}

Format each entity as: [Text]|[Type]|[Confidence]
One entity per line.";

            var response = await AIProvider.CompleteAsync(new AIRequest
            {
                Prompt = prompt,
                MaxTokens = 500,
                Temperature = 0.2f
            }, ct);

            if (!response.Success) return new List<ExtractedEntity>();

            var entities = new List<ExtractedEntity>();
            foreach (var line in response.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('|');
                if (parts.Length >= 2)
                {
                    var text = parts[0].Trim().Trim('[', ']');
                    var type = parts[1].Trim().Trim('[', ']').ToUpperInvariant();
                    var confidence = parts.Length >= 3 && float.TryParse(parts[2].Trim().Trim('[', ']'), out var c) ? c : 0.7f;

                    var index = content.IndexOf(text, StringComparison.OrdinalIgnoreCase);
                    if (index >= 0)
                    {
                        entities.Add(new ExtractedEntity
                        {
                            Text = text,
                            Type = type,
                            StartIndex = index,
                            EndIndex = index + text.Length,
                            Confidence = confidence,
                            Context = options.IncludeContext
                                ? GetContext(content, index, text.Length, options.ContextWindowSize)
                                : null
                        });
                    }
                }
            }

            return entities;
        }

        /// <inheritdoc/>
        public virtual void RegisterEntityType(CustomEntityTypeDefinition entityType)
        {
            _customEntityTypes[entityType.Name] = entityType;
        }

        /// <inheritdoc/>
        public virtual IReadOnlyList<string> GetSupportedEntityTypes()
        {
            var types = EntityPatterns.Keys.ToList();
            types.AddRange(_customEntityTypes.Keys);
            return types;
        }

        #endregion

        #region IPIIDetector Implementation

        // Standard PII patterns
        private static readonly Dictionary<PIIType, Regex> PIIPatterns = new()
        {
            [PIIType.Email] = new Regex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,}\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100)),
            [PIIType.Phone] = new Regex(@"\b(?:\+?1[-.\s]?)?\(?\d{3}\)?[-.\s]?\d{3}[-.\s]?\d{4}\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100)),
            [PIIType.SSN] = new Regex(@"\b\d{3}[-\s]?\d{2}[-\s]?\d{4}\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100)),
            [PIIType.CreditCard] = new Regex(@"\b(?:\d{4}[-\s]?){3}\d{4}\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100)),
            [PIIType.IPAddress] = new Regex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100)),
            [PIIType.DateOfBirth] = new Regex(@"\b(?:DOB|Date of Birth|Birthday)[:\s]*\d{1,2}[-/]\d{1,2}[-/]\d{2,4}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)),
            [PIIType.DriversLicense] = new Regex(@"\b(?:DL|Driver'?s?\s*License)[:\s#]*[A-Z0-9]{5,15}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)),
            [PIIType.Passport] = new Regex(@"\b(?:Passport)[:\s#]*[A-Z0-9]{6,12}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100))
        };

        /// <inheritdoc/>
        public virtual async Task<PIIDetectionResult> DetectPIIAsync(
            string content,
            PIIDetectionOptions? options = null,
            CancellationToken ct = default)
        {
            options ??= new PIIDetectionOptions();
            var items = new List<DetectedPII>();
            var usedAI = false;

            // Pattern-based detection (always run)
            var patternPII = DetectPIIWithPatterns(content, options);
            items.AddRange(patternPII);

            // AI-enhanced detection
            if (options.UseAI && IsAIAvailable)
            {
                try
                {
                    var aiPII = await DetectPIIWithAIAsync(content, options, ct);
                    usedAI = true;

                    // Merge (avoid duplicates)
                    foreach (var pii in aiPII)
                    {
                        if (!items.Any(p => p.StartIndex == pii.StartIndex && p.Type == pii.Type))
                        {
                            items.Add(pii);
                        }
                    }
                }
                catch { }
            }

            // Custom patterns
            lock (_piiPatternLock)
            {
                foreach (var customPattern in _customPIIPatterns)
                {
                    try
                    {
                        var regex = new Regex(customPattern.Pattern, RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
                        foreach (Match match in regex.Matches(content))
                        {
                            var isValid = customPattern.Validator?.Invoke(match.Value) ?? true;
                            if (isValid)
                            {
                                items.Add(new DetectedPII
                                {
                                    Text = match.Value,
                                    Type = customPattern.Type,
                                    StartIndex = match.Index,
                                    EndIndex = match.Index + match.Length,
                                    Confidence = 0.8f + customPattern.ConfidenceBoost,
                                    SuggestedRedaction = GenerateRedaction(match.Value, customPattern.Type)
                                });
                            }
                        }
                    }
                    catch { }
                }
            }

            // Filter by requested types
            if (options.PIITypes != null && options.PIITypes.Count > 0)
            {
                items = items.Where(p => options.PIITypes.Contains(p.Type)).ToList();
            }
            items = items.Where(p => p.Confidence >= options.MinConfidence).ToList();

            Interlocked.Add(ref _piiDetected, items.Count);

            var countsByType = items.GroupBy(p => p.Type)
                .ToDictionary(g => g.Key, g => g.Count());

            var riskLevel = CalculateRiskLevel(items);
            var violations = options.ComplianceRegulations != null
                ? CheckCompliance(items, options.ComplianceRegulations)
                : Array.Empty<ComplianceViolation>();

            return new PIIDetectionResult
            {
                Items = items,
                CountsByType = countsByType,
                RiskLevel = riskLevel,
                ComplianceViolations = violations,
                UsedAI = usedAI
            };
        }

        /// <summary>
        /// Pattern-based PII detection.
        /// </summary>
        protected virtual List<DetectedPII> DetectPIIWithPatterns(string content, PIIDetectionOptions options)
        {
            var items = new List<DetectedPII>();

            foreach (var (type, pattern) in PIIPatterns)
            {
                foreach (Match match in pattern.Matches(content))
                {
                    if (!ValidatePII(match.Value, type))
                        continue;

                    items.Add(new DetectedPII
                    {
                        Text = match.Value,
                        Type = type,
                        StartIndex = match.Index,
                        EndIndex = match.Index + match.Length,
                        Confidence = 0.9f,
                        Context = options.IncludeContext
                            ? GetContext(content, match.Index, match.Length, 30)
                            : null,
                        SuggestedRedaction = GenerateRedaction(match.Value, type)
                    });
                }
            }

            return items;
        }

        /// <summary>
        /// AI-enhanced PII detection. Override for custom implementation.
        /// </summary>
        protected virtual async Task<List<DetectedPII>> DetectPIIWithAIAsync(
            string content,
            PIIDetectionOptions options,
            CancellationToken ct)
        {
            if (AIProvider == null) return new List<DetectedPII>();

            var prompt = $@"Identify all personally identifiable information (PII) in the following text.
Types to look for: names, addresses, phone numbers, emails, SSN, credit cards, dates of birth, medical info.

Text:
{content.Substring(0, Math.Min(2000, content.Length))}

For each PII found, output: [Text]|[Type]|[Confidence]
Types: NAME, EMAIL, PHONE, ADDRESS, SSN, CREDIT_CARD, DOB, MEDICAL
One per line.";

            var response = await AIProvider.CompleteAsync(new AIRequest
            {
                Prompt = prompt,
                MaxTokens = 400,
                Temperature = 0.1f
            }, ct);

            if (!response.Success) return new List<DetectedPII>();

            var items = new List<DetectedPII>();
            foreach (var line in response.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('|');
                if (parts.Length >= 2)
                {
                    var text = parts[0].Trim().Trim('[', ']');
                    var typeStr = parts[1].Trim().Trim('[', ']').ToUpperInvariant();
                    var confidence = parts.Length >= 3 && float.TryParse(parts[2].Trim().Trim('[', ']'), out var c) ? c : 0.7f;

                    var type = typeStr switch
                    {
                        "NAME" => PIIType.Name,
                        "EMAIL" => PIIType.Email,
                        "PHONE" => PIIType.Phone,
                        "ADDRESS" => PIIType.Address,
                        "SSN" => PIIType.SSN,
                        "CREDIT_CARD" => PIIType.CreditCard,
                        "DOB" => PIIType.DateOfBirth,
                        "MEDICAL" => PIIType.MedicalRecordNumber,
                        _ => PIIType.Unknown
                    };

                    var index = content.IndexOf(text, StringComparison.OrdinalIgnoreCase);
                    if (index >= 0)
                    {
                        items.Add(new DetectedPII
                        {
                            Text = text,
                            Type = type,
                            StartIndex = index,
                            EndIndex = index + text.Length,
                            Confidence = confidence,
                            SuggestedRedaction = GenerateRedaction(text, type)
                        });
                    }
                }
            }

            return items;
        }

        /// <inheritdoc/>
        public virtual async Task<PIIRedactionResult> RedactPIIAsync(
            string content,
            PIIRedactionOptions? options = null,
            CancellationToken ct = default)
        {
            options ??= new PIIRedactionOptions();

            var detectionResult = await DetectPIIAsync(content, new PIIDetectionOptions
            {
                PIITypes = options.PIITypes
            }, ct);

            var redactions = new List<RedactionDetail>();
            var redactedContent = new StringBuilder(content);

            // Sort by position descending to avoid index shifts
            var sortedPII = detectionResult.Items.OrderByDescending(p => p.StartIndex).ToList();

            foreach (var pii in sortedPII)
            {
                var replacement = options.Strategy switch
                {
                    RedactionStrategy.Mask => options.PreserveFormat
                        ? GenerateMaskedPreserveFormat(pii.Text, options.MaskCharacter)
                        : new string(options.MaskCharacter, pii.Text.Length),
                    RedactionStrategy.TypeLabel => $"[{pii.Type}]",
                    RedactionStrategy.Hash => ComputeHash(pii.Text).Substring(0, 8),
                    RedactionStrategy.Remove => "",
                    RedactionStrategy.Synthetic => GenerateSyntheticValue(pii.Type),
                    _ => new string('*', pii.Text.Length)
                };

                redactedContent.Remove(pii.StartIndex, pii.EndIndex - pii.StartIndex);
                redactedContent.Insert(pii.StartIndex, replacement);

                redactions.Add(new RedactionDetail
                {
                    OriginalText = pii.Text,
                    RedactedText = replacement,
                    Type = pii.Type,
                    Position = pii.StartIndex
                });
            }

            return new PIIRedactionResult
            {
                OriginalText = content,
                RedactedText = redactedContent.ToString(),
                RedactionCount = redactions.Count,
                Redactions = redactions
            };
        }

        /// <inheritdoc/>
        public virtual void RegisterPIIPattern(CustomPIIPatternDefinition pattern)
        {
            lock (_piiPatternLock)
            {
                _customPIIPatterns.Add(pattern);
            }
        }

        /// <inheritdoc/>
        public virtual IReadOnlyList<PIITypeInfo> GetSupportedPIITypes()
        {
            return Enum.GetValues<PIIType>().Select(t => new PIITypeInfo
            {
                Type = t,
                Name = t.ToString(),
                Regulations = GetRegulationsForPIIType(t)
            }).ToList();
        }

        private bool ValidatePII(string value, PIIType type)
        {
            return type switch
            {
                PIIType.SSN => ValidateSSN(value),
                PIIType.CreditCard => ValidateCreditCard(value),
                PIIType.IPAddress => ValidateIPAddress(value),
                _ => true
            };
        }

        private bool ValidateSSN(string value)
        {
            var digits = Regex.Replace(value, @"[^\d]", "", RegexOptions.None, TimeSpan.FromMilliseconds(100));
            if (digits.Length != 9) return false;
            // Reject known invalid patterns
            if (digits.StartsWith("000") || digits.StartsWith("666") || digits.StartsWith("9")) return false;
            if (digits.Substring(3, 2) == "00" || digits.Substring(5, 4) == "0000") return false;
            return true;
        }

        private bool ValidateCreditCard(string value)
        {
            var digits = Regex.Replace(value, @"[^\d]", "", RegexOptions.None, TimeSpan.FromMilliseconds(100));
            if (digits.Length < 13 || digits.Length > 19) return false;
            // Luhn algorithm
            int sum = 0;
            bool alternate = false;
            for (int i = digits.Length - 1; i >= 0; i--)
            {
                int n = int.Parse(digits[i].ToString());
                if (alternate)
                {
                    n *= 2;
                    if (n > 9) n -= 9;
                }
                sum += n;
                alternate = !alternate;
            }
            return sum % 10 == 0;
        }

        private bool ValidateIPAddress(string value)
        {
            var parts = value.Split('.');
            if (parts.Length != 4) return false;
            return parts.All(p => int.TryParse(p, out var n) && n >= 0 && n <= 255);
        }

        private string GenerateRedaction(string value, PIIType type)
        {
            return type switch
            {
                PIIType.Email => "[EMAIL]",
                PIIType.Phone => "[PHONE]",
                PIIType.SSN => "XXX-XX-XXXX",
                PIIType.CreditCard => "XXXX-XXXX-XXXX-XXXX",
                PIIType.Name => "[NAME]",
                PIIType.Address => "[ADDRESS]",
                _ => new string('X', value.Length)
            };
        }

        private string GenerateMaskedPreserveFormat(string value, char maskChar)
        {
            var result = new StringBuilder();
            foreach (var c in value)
            {
                result.Append(char.IsLetterOrDigit(c) ? maskChar : c);
            }
            return result.ToString();
        }

        private string GenerateSyntheticValue(PIIType type)
        {
            return type switch
            {
                PIIType.Email => "user@example.com",
                PIIType.Phone => "(555) 555-5555",
                PIIType.SSN => "000-00-0000",
                PIIType.Name => "John Doe",
                PIIType.Address => "123 Main St, Anytown, ST 12345",
                _ => "[REDACTED]"
            };
        }

        private string ComputeHash(string value)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private PIIRiskLevel CalculateRiskLevel(List<DetectedPII> items)
        {
            if (items.Count == 0) return PIIRiskLevel.None;

            var highRiskTypes = new[] { PIIType.SSN, PIIType.CreditCard, PIIType.BankAccount, PIIType.Password, PIIType.MedicalRecordNumber };
            var mediumRiskTypes = new[] { PIIType.Email, PIIType.Phone, PIIType.Address, PIIType.DateOfBirth };

            if (items.Any(p => highRiskTypes.Contains(p.Type))) return PIIRiskLevel.High;
            if (items.Count > 5 || items.Any(p => mediumRiskTypes.Contains(p.Type))) return PIIRiskLevel.Medium;
            return PIIRiskLevel.Low;
        }

        private IReadOnlyList<ComplianceViolation> CheckCompliance(List<DetectedPII> items, IReadOnlyList<string> regulations)
        {
            var violations = new List<ComplianceViolation>();

            foreach (var regulation in regulations)
            {
                var relatedPII = items.Where(p => GetRegulationsForPIIType(p.Type).Contains(regulation)).ToList();
                if (relatedPII.Any())
                {
                    violations.Add(new ComplianceViolation
                    {
                        Regulation = regulation,
                        Description = $"Found {relatedPII.Count} PII items subject to {regulation}",
                        Severity = relatedPII.Any(p => p.Type == PIIType.SSN || p.Type == PIIType.CreditCard)
                            ? ViolationSeverity.Severe
                            : ViolationSeverity.Moderate,
                        RelatedPII = relatedPII
                    });
                }
            }

            return violations;
        }

        private IReadOnlyList<string> GetRegulationsForPIIType(PIIType type)
        {
            return type switch
            {
                PIIType.SSN => new[] { "GDPR", "CCPA", "HIPAA" },
                PIIType.CreditCard => new[] { "PCI-DSS", "GDPR", "CCPA" },
                PIIType.MedicalRecordNumber => new[] { "HIPAA", "GDPR" },
                PIIType.Email or PIIType.Phone or PIIType.Address => new[] { "GDPR", "CCPA" },
                PIIType.Name or PIIType.DateOfBirth => new[] { "GDPR", "CCPA", "HIPAA" },
                _ => Array.Empty<string>()
            };
        }

        #endregion

        #region IContentSummarizer Implementation

        /// <inheritdoc/>
        public virtual async Task<SummaryResult> SummarizeAsync(
            string content,
            SummaryOptions? options = null,
            CancellationToken ct = default)
        {
            options ??= new SummaryOptions();

            // Try AI-based abstractive summarization
            if (options.PreferAbstractive && IsAIAvailable)
            {
                try
                {
                    return await SummarizeWithAIAsync(content, options, ct);
                }
                catch { }
            }

            // Fallback to extractive summarization
            return SummarizeExtractive(content, options);
        }

        /// <summary>
        /// AI-based abstractive summarization.
        /// </summary>
        protected virtual async Task<SummaryResult> SummarizeWithAIAsync(
            string content,
            SummaryOptions options,
            CancellationToken ct)
        {
            if (AIProvider == null) throw new InvalidOperationException("AI provider not configured");

            var focusClause = options.FocusAspects?.Count > 0
                ? $" Focus on: {string.Join(", ", options.FocusAspects)}."
                : "";

            var prompt = $"Summarize the following text in about {options.TargetLengthWords} words.{focusClause}\n\nText:\n{content}";

            var response = await AIProvider.CompleteAsync(new AIRequest
            {
                Prompt = prompt,
                MaxTokens = options.TargetLengthWords * 2,
                Temperature = 0.5f
            }, ct);

            if (!response.Success)
                throw new Exception(response.ErrorMessage);

            var keyPhrases = options.IncludeKeyPhrases
                ? ExtractKeyPhrases(content)
                : Array.Empty<string>();

            return new SummaryResult
            {
                Summary = response.Content.Trim(),
                Type = SummaryType.Abstractive,
                KeyPhrases = keyPhrases,
                CompressionRatio = (float)response.Content.Length / content.Length,
                UsedAI = true
            };
        }

        /// <summary>
        /// Extractive summarization (no AI required).
        /// </summary>
        protected virtual SummaryResult SummarizeExtractive(string content, SummaryOptions options)
        {
            var sentences = SplitSentences(content);
            if (sentences.Count == 0)
                return new SummaryResult { Summary = content, Type = SummaryType.Extractive };

            // Score sentences by position and keyword presence
            var keyPhrases = ExtractKeyPhrases(content);
            var scored = sentences.Select((s, i) => (
                Sentence: s,
                Score: ScoreSentence(s, i, sentences.Count, keyPhrases)
            )).ToList();

            // Select top sentences
            var targetCharacters = options.TargetLengthWords * 6; // ~6 chars per word
            var selected = new List<string>();
            var currentLength = 0;

            foreach (var item in scored.OrderByDescending(s => s.Score))
            {
                if (currentLength + item.Sentence.Length > targetCharacters)
                    break;
                selected.Add(item.Sentence);
                currentLength += item.Sentence.Length;
            }

            // Reorder by original position
            var summary = string.Join(" ", selected.OrderBy(s => sentences.IndexOf(s)));

            return new SummaryResult
            {
                Summary = summary,
                Type = SummaryType.Extractive,
                KeyPhrases = keyPhrases,
                CompressionRatio = (float)summary.Length / content.Length,
                UsedAI = false
            };
        }

        /// <inheritdoc/>
        public virtual async Task<SummaryResult> SummarizeMultipleAsync(
            IEnumerable<string> contents,
            SummaryOptions? options = null,
            CancellationToken ct = default)
        {
            options ??= new SummaryOptions();
            var combined = string.Join("\n\n---\n\n", contents);
            return await SummarizeAsync(combined, options with { TargetLengthWords = options.TargetLengthWords * 2 }, ct);
        }

        private List<string> SplitSentences(string text)
        {
            return Regex.Split(text, @"(?<=[.!?])\s+", RegexOptions.None, TimeSpan.FromMilliseconds(100))
                .Where(s => !string.IsNullOrWhiteSpace(s) && s.Length > 10)
                .ToList();
        }

        private float ScoreSentence(string sentence, int position, int totalSentences, IReadOnlyList<string> keyPhrases)
        {
            var score = 0f;

            // Position score (first and last sentences more important)
            if (position == 0) score += 0.3f;
            else if (position < 3) score += 0.2f;
            else if (position >= totalSentences - 2) score += 0.15f;

            // Length score (prefer medium-length sentences)
            var wordCount = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (wordCount >= 10 && wordCount <= 30) score += 0.2f;

            // Keyword presence
            var sentenceLower = sentence.ToLowerInvariant();
            var keywordMatches = keyPhrases.Count(kp => sentenceLower.Contains(kp.ToLowerInvariant()));
            score += keywordMatches * 0.1f;

            return Math.Min(1f, score);
        }

        private IReadOnlyList<string> ExtractKeyPhrases(string content)
        {
            // Simple TF-based key phrase extraction
            var words = Regex.Split(content.ToLowerInvariant(), @"\W+", RegexOptions.None, TimeSpan.FromMilliseconds(100))
                .Where(w => w.Length > 3)
                .GroupBy(w => w)
                .OrderByDescending(g => g.Count())
                .Take(10)
                .Select(g => g.Key)
                .ToList();

            return words;
        }

        #endregion

        #region ILanguageDetector Implementation

        // Language trigram profiles (simplified)
        private static readonly Dictionary<string, string[]> LanguageTrigrams = new()
        {
            ["en"] = new[] { "the", "and", "ing", "ion", "tio", "ent", "ati", "for", "her", "ter" },
            ["es"] = new[] { "que", "ent", "ión", "con", "los", "del", "las", "una", "est", "ado" },
            ["fr"] = new[] { "les", "ent", "que", "ion", "tio", "ais", "ait", "des", "est", "eur" },
            ["de"] = new[] { "der", "die", "und", "den", "sch", "ein", "ich", "ung", "das", "ist" },
            ["pt"] = new[] { "que", "ção", "ent", "ade", "con", "dos", "com", "est", "men", "par" },
            ["it"] = new[] { "che", "ion", "del", "ell", "ent", "ato", "per", "con", "one", "lla" },
            ["zh"] = new[] { "的", "是", "了", "不", "在", "有", "我", "他", "这", "个" },
            ["ja"] = new[] { "の", "に", "は", "を", "た", "が", "で", "て", "と", "し" },
            ["ko"] = new[] { "의", "이", "는", "을", "가", "에", "로", "다", "한", "고" },
            ["ru"] = new[] { "ого", "ени", "ать", "ост", "что", "ние", "про", "ова", "ста", "при" },
            ["ar"] = new[] { "ال", "وال", "من", "في", "على", "إلى", "هذا", "أن", "لا", "مع" }
        };

        /// <inheritdoc/>
        public virtual async Task<LanguageDetectionResult> DetectLanguageAsync(
            string content,
            LanguageDetectionOptions? options = null,
            CancellationToken ct = default)
        {
            options ??= new LanguageDetectionOptions();

            // AI-based detection
            if (options.UseAI && IsAIAvailable)
            {
                try
                {
                    return await DetectLanguageWithAIAsync(content, options, ct);
                }
                catch { }
            }

            // Statistical detection
            return DetectLanguageStatistical(content, options);
        }

        /// <summary>
        /// AI-based language detection.
        /// </summary>
        protected virtual async Task<LanguageDetectionResult> DetectLanguageWithAIAsync(
            string content,
            LanguageDetectionOptions options,
            CancellationToken ct)
        {
            if (AIProvider == null) throw new InvalidOperationException("AI provider not configured");

            var prompt = $"Identify the language(s) of this text. Respond with ISO 639-1 codes and percentages. Format: code:percentage\n\nText:\n{content.Substring(0, Math.Min(500, content.Length))}";

            var response = await AIProvider.CompleteAsync(new AIRequest
            {
                Prompt = prompt,
                MaxTokens = 100,
                Temperature = 0.1f
            }, ct);

            if (!response.Success)
                throw new Exception(response.ErrorMessage);

            var languages = ParseLanguageResponse(response.Content);

            return new LanguageDetectionResult
            {
                PrimaryLanguage = languages.FirstOrDefault() ?? new DetectedLanguage { Code = "en", Name = "English", Confidence = 0.5f },
                Languages = languages,
                IsMultilingual = languages.Count > 1,
                UsedAI = true
            };
        }

        /// <summary>
        /// Statistical language detection using trigrams.
        /// </summary>
        protected virtual LanguageDetectionResult DetectLanguageStatistical(string content, LanguageDetectionOptions options)
        {
            var contentLower = content.ToLowerInvariant();
            var scores = new Dictionary<string, float>();

            // Detect script first
            var script = DetectScript(content);

            // Score each language
            foreach (var (langCode, trigrams) in LanguageTrigrams)
            {
                var matchCount = trigrams.Count(t => contentLower.Contains(t));
                scores[langCode] = (float)matchCount / trigrams.Length;
            }

            var sorted = scores.OrderByDescending(s => s.Value).Take(3).ToList();
            var languages = sorted.Select((s, i) => new DetectedLanguage
            {
                Code = s.Key,
                Name = GetLanguageName(s.Key),
                Script = script,
                Confidence = s.Value,
                Percentage = i == 0 ? 100f : 0f
            }).ToList();

            return new LanguageDetectionResult
            {
                PrimaryLanguage = languages.FirstOrDefault() ?? new DetectedLanguage { Code = "en", Name = "English" },
                Languages = languages,
                IsMultilingual = languages.Count > 1 && languages[1].Confidence > 0.3f,
                UsedAI = false
            };
        }

        /// <inheritdoc/>
        public virtual IReadOnlyList<LanguageInfo> GetSupportedLanguages()
        {
            return LanguageTrigrams.Keys.Select(code => new LanguageInfo
            {
                Code = code,
                Name = GetLanguageName(code)
            }).ToList();
        }

        private string DetectScript(string text)
        {
            var latinCount = text.Count(c => c >= 'A' && c <= 'z');
            var cyrillicCount = text.Count(c => c >= 0x0400 && c <= 0x04FF);
            var cjkCount = text.Count(c => c >= 0x4E00 && c <= 0x9FFF);
            var arabicCount = text.Count(c => c >= 0x0600 && c <= 0x06FF);

            var max = Math.Max(Math.Max(latinCount, cyrillicCount), Math.Max(cjkCount, arabicCount));

            if (max == latinCount) return "Latin";
            if (max == cyrillicCount) return "Cyrillic";
            if (max == cjkCount) return "Han";
            if (max == arabicCount) return "Arabic";
            return "Unknown";
        }

        private string GetLanguageName(string code)
        {
            return code switch
            {
                "en" => "English",
                "es" => "Spanish",
                "fr" => "French",
                "de" => "German",
                "pt" => "Portuguese",
                "it" => "Italian",
                "zh" => "Chinese",
                "ja" => "Japanese",
                "ko" => "Korean",
                "ru" => "Russian",
                "ar" => "Arabic",
                _ => code.ToUpperInvariant()
            };
        }

        private List<DetectedLanguage> ParseLanguageResponse(string response)
        {
            var languages = new List<DetectedLanguage>();
            var pattern = @"([a-z]{2})[:\s]+(\d+(?:\.\d+)?)[%]?";

            foreach (Match match in Regex.Matches(response.ToLowerInvariant(), pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100)))
            {
                if (float.TryParse(match.Groups[2].Value, out var percentage))
                {
                    languages.Add(new DetectedLanguage
                    {
                        Code = match.Groups[1].Value,
                        Name = GetLanguageName(match.Groups[1].Value),
                        Confidence = percentage / 100f,
                        Percentage = percentage
                    });
                }
            }

            return languages.OrderByDescending(l => l.Confidence).ToList();
        }

        #endregion

        #region ISentimentAnalyzer Implementation

        // Sentiment lexicon (simplified)
        private static readonly Dictionary<string, float> SentimentLexicon = new()
        {
            // Positive words
            ["good"] = 0.5f, ["great"] = 0.8f, ["excellent"] = 0.9f, ["amazing"] = 0.9f,
            ["wonderful"] = 0.8f, ["fantastic"] = 0.9f, ["love"] = 0.8f, ["happy"] = 0.7f,
            ["perfect"] = 1.0f, ["best"] = 0.9f, ["beautiful"] = 0.7f, ["awesome"] = 0.8f,
            // Negative words
            ["bad"] = -0.5f, ["terrible"] = -0.9f, ["horrible"] = -0.9f, ["awful"] = -0.8f,
            ["poor"] = -0.5f, ["worst"] = -1.0f, ["hate"] = -0.8f, ["disappointing"] = -0.6f,
            ["angry"] = -0.7f, ["sad"] = -0.6f, ["frustrated"] = -0.6f, ["annoying"] = -0.5f
        };

        /// <inheritdoc/>
        public virtual async Task<SentimentResult> AnalyzeSentimentAsync(
            string content,
            SentimentOptions? options = null,
            CancellationToken ct = default)
        {
            options ??= new SentimentOptions();

            // AI-based sentiment analysis
            if (options.UseAI && IsAIAvailable)
            {
                try
                {
                    return await AnalyzeSentimentWithAIAsync(content, options, ct);
                }
                catch { }
            }

            // Lexicon-based analysis
            return AnalyzeSentimentLexicon(content, options);
        }

        /// <summary>
        /// AI-based sentiment analysis.
        /// </summary>
        protected virtual async Task<SentimentResult> AnalyzeSentimentWithAIAsync(
            string content,
            SentimentOptions options,
            CancellationToken ct)
        {
            if (AIProvider == null) throw new InvalidOperationException("AI provider not configured");

            var prompt = $"Analyze the sentiment of this text. Respond with:\n- Sentiment: VERY_NEGATIVE, NEGATIVE, NEUTRAL, POSITIVE, or VERY_POSITIVE\n- Score: -1.0 to 1.0\n- Confidence: 0.0 to 1.0\n\nText:\n{content.Substring(0, Math.Min(1000, content.Length))}";

            var response = await AIProvider.CompleteAsync(new AIRequest
            {
                Prompt = prompt,
                MaxTokens = 100,
                Temperature = 0.1f
            }, ct);

            if (!response.Success)
                throw new Exception(response.ErrorMessage);

            var (sentiment, score, confidence) = ParseSentimentResponse(response.Content);

            return new SentimentResult
            {
                OverallSentiment = sentiment,
                Score = score,
                Confidence = confidence,
                UsedAI = true
            };
        }

        /// <summary>
        /// Lexicon-based sentiment analysis.
        /// </summary>
        protected virtual SentimentResult AnalyzeSentimentLexicon(string content, SentimentOptions options)
        {
            var words = Regex.Split(content.ToLowerInvariant(), @"\W+", RegexOptions.None, TimeSpan.FromMilliseconds(100));
            var totalScore = 0f;
            var matchCount = 0;

            foreach (var word in words)
            {
                if (SentimentLexicon.TryGetValue(word, out var score))
                {
                    totalScore += score;
                    matchCount++;
                }
            }

            var avgScore = matchCount > 0 ? totalScore / matchCount : 0;
            var sentiment = avgScore switch
            {
                < -0.5f => Sentiment.VeryNegative,
                < -0.1f => Sentiment.Negative,
                < 0.1f => Sentiment.Neutral,
                < 0.5f => Sentiment.Positive,
                _ => Sentiment.VeryPositive
            };

            return new SentimentResult
            {
                OverallSentiment = sentiment,
                Score = avgScore,
                Confidence = matchCount > 5 ? 0.7f : 0.4f,
                UsedAI = false
            };
        }

        private (Sentiment sentiment, float score, float confidence) ParseSentimentResponse(string response)
        {
            var sentiment = Sentiment.Neutral;
            var score = 0f;
            var confidence = 0.5f;

            var responseLower = response.ToLowerInvariant();

            if (responseLower.Contains("very_negative") || responseLower.Contains("very negative"))
                sentiment = Sentiment.VeryNegative;
            else if (responseLower.Contains("negative"))
                sentiment = Sentiment.Negative;
            else if (responseLower.Contains("very_positive") || responseLower.Contains("very positive"))
                sentiment = Sentiment.VeryPositive;
            else if (responseLower.Contains("positive"))
                sentiment = Sentiment.Positive;

            var scoreMatch = Regex.Match(response, @"score[:\s]+(-?\d+\.?\d*)", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
            if (scoreMatch.Success && float.TryParse(scoreMatch.Groups[1].Value, out var s))
                score = Math.Clamp(s, -1f, 1f);

            var confMatch = Regex.Match(response, @"confidence[:\s]+(\d+\.?\d*)", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
            if (confMatch.Success && float.TryParse(confMatch.Groups[1].Value, out var c))
                confidence = Math.Clamp(c, 0f, 1f);

            return (sentiment, score, confidence);
        }

        #endregion

        #region IDuplicateDetector Implementation

        /// <inheritdoc/>
        public virtual async Task<DuplicateDetectionResult> DetectDuplicatesAsync(
            string content,
            string contentId,
            IEnumerable<ContentItem> corpus,
            DuplicateDetectionOptions? options = null,
            CancellationToken ct = default)
        {
            options ??= new DuplicateDetectionOptions();
            var exactDuplicates = new List<DuplicateMatch>();
            var nearDuplicates = new List<DuplicateMatch>();

            var contentHash = ComputeHash(content);
            var contentNormalized = NormalizeForComparison(content);
            float[]? contentEmbedding = null;

            // Get embedding for semantic comparison
            if (options.EnableSemanticDetection && AIProvider != null)
            {
                try
                {
                    contentEmbedding = await AIProvider.GetEmbeddingsAsync(content.Substring(0, Math.Min(8000, content.Length)), ct);
                }
                catch { }
            }

            foreach (var item in corpus)
            {
                if (item.Id == contentId) continue;

                // Exact duplicate check (hash)
                var itemHash = ComputeHash(item.Text);
                if (itemHash == contentHash)
                {
                    exactDuplicates.Add(new DuplicateMatch
                    {
                        ContentId = item.Id,
                        Similarity = 1.0f,
                        Type = DuplicateType.Exact
                    });
                    continue;
                }

                // Near-duplicate check (normalized text similarity)
                var itemNormalized = NormalizeForComparison(item.Text);
                var textSimilarity = ComputeTextSimilarity(contentNormalized, itemNormalized);
                if (textSimilarity >= options.ExactDuplicateThreshold)
                {
                    exactDuplicates.Add(new DuplicateMatch
                    {
                        ContentId = item.Id,
                        Similarity = textSimilarity,
                        Type = DuplicateType.Near
                    });
                    continue;
                }

                // Semantic similarity check
                if (contentEmbedding != null && options.EnableSemanticDetection)
                {
                    float[]? itemEmbedding = item.Embedding;
                    if (itemEmbedding == null && AIProvider != null)
                    {
                        try
                        {
                            itemEmbedding = await AIProvider.GetEmbeddingsAsync(item.Text.Substring(0, Math.Min(8000, item.Text.Length)), ct);
                        }
                        catch { continue; }
                    }

                    if (itemEmbedding != null)
                    {
                        var semanticSimilarity = VectorOps.CosineSimilarity(contentEmbedding, itemEmbedding);
                        if (semanticSimilarity >= options.NearDuplicateThreshold)
                        {
                            nearDuplicates.Add(new DuplicateMatch
                            {
                                ContentId = item.Id,
                                Similarity = semanticSimilarity,
                                Type = DuplicateType.Semantic
                            });
                        }
                    }
                }
            }

            return new DuplicateDetectionResult
            {
                ExactDuplicates = exactDuplicates.OrderByDescending(d => d.Similarity).Take(options.MaxResults).ToList(),
                NearDuplicates = nearDuplicates.OrderByDescending(d => d.Similarity).Take(options.MaxResults).ToList(),
                UsedSemanticDetection = contentEmbedding != null
            };
        }

        /// <inheritdoc/>
        public virtual async Task<float> ComputeSimilarityAsync(
            string content1,
            string content2,
            CancellationToken ct = default)
        {
            if (AIProvider != null)
            {
                try
                {
                    var embeddings = await AIProvider.GetEmbeddingsBatchAsync(new[] { content1, content2 }, ct);
                    return VectorOps.CosineSimilarity(embeddings[0], embeddings[1]);
                }
                catch { }
            }

            // Fallback to text similarity
            return ComputeTextSimilarity(
                NormalizeForComparison(content1),
                NormalizeForComparison(content2));
        }

        private string NormalizeForComparison(string text)
        {
            return Regex.Replace(text.ToLowerInvariant(), @"\s+", " ", RegexOptions.None, TimeSpan.FromMilliseconds(100)).Trim();
        }

        private float ComputeTextSimilarity(string text1, string text2)
        {
            var words1 = new HashSet<string>(text1.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            var words2 = new HashSet<string>(text2.Split(' ', StringSplitOptions.RemoveEmptyEntries));

            var intersection = words1.Intersect(words2).Count();
            var union = words1.Union(words2).Count();

            return union > 0 ? (float)intersection / union : 0;
        }

        #endregion

        #region IRelationshipDiscoverer Implementation

        /// <inheritdoc/>
        public virtual async Task<RelationshipDiscoveryResult> DiscoverRelationshipsAsync(
            ContentItem document,
            IEnumerable<ContentItem> corpus,
            RelationshipDiscoveryOptions? options = null,
            CancellationToken ct = default)
        {
            options ??= new RelationshipDiscoveryOptions();
            var relationships = new List<DiscoveredRelationship>();

            // Get document embedding
            float[]? docEmbedding = document.Embedding;
            if (docEmbedding == null && AIProvider != null)
            {
                try
                {
                    docEmbedding = await AIProvider.GetEmbeddingsAsync(document.Text.Substring(0, Math.Min(8000, document.Text.Length)), ct);
                }
                catch { }
            }

            foreach (var item in corpus)
            {
                if (item.Id == document.Id) continue;

                // Similarity relationship
                float similarity = 0;
                if (docEmbedding != null)
                {
                    float[]? itemEmbedding = item.Embedding;
                    if (itemEmbedding == null && AIProvider != null)
                    {
                        try
                        {
                            itemEmbedding = await AIProvider.GetEmbeddingsAsync(item.Text.Substring(0, Math.Min(8000, item.Text.Length)), ct);
                        }
                        catch { continue; }
                    }

                    if (itemEmbedding != null)
                    {
                        similarity = VectorOps.CosineSimilarity(docEmbedding, itemEmbedding);
                    }
                }

                if (similarity >= options.MinSimilarity)
                {
                    relationships.Add(new DiscoveredRelationship
                    {
                        SourceId = document.Id,
                        TargetId = item.Id,
                        Type = RelationshipType.Similar,
                        Strength = similarity
                    });
                }

                // Reference detection (simple - looks for citations)
                if (document.Text.Contains(item.Id) ||
                    (item.Metadata?.TryGetValue("title", out var title) == true &&
                     document.Text.Contains(title?.ToString() ?? "")))
                {
                    relationships.Add(new DiscoveredRelationship
                    {
                        SourceId = document.Id,
                        TargetId = item.Id,
                        Type = RelationshipType.References,
                        Strength = 0.9f
                    });
                }
            }

            // Store in knowledge graph if requested
            if (options.StoreInKnowledgeGraph && KnowledgeGraph != null)
            {
                foreach (var rel in relationships)
                {
                    try
                    {
                        await KnowledgeGraph.AddEdgeAsync(rel.SourceId, rel.TargetId, rel.Type.ToString());
                    }
                    catch { }
                }
            }

            return new RelationshipDiscoveryResult
            {
                Relationships = relationships
                    .OrderByDescending(r => r.Strength)
                    .Take(options.MaxRelationshipsPerType * 3)
                    .ToList()
            };
        }

        #endregion

        #region INaturalLanguageQueryParser Implementation

        /// <inheritdoc/>
        public virtual async Task<QueryParseResult> ParseQueryAsync(
            string query,
            QueryParseOptions? options = null,
            CancellationToken ct = default)
        {
            options ??= new QueryParseOptions();

            // AI-based parsing
            if (options.UseAI && IsAIAvailable)
            {
                try
                {
                    return await ParseQueryWithAIAsync(query, options, ct);
                }
                catch { }
            }

            // Pattern-based parsing
            return ParseQueryWithPatterns(query, options);
        }

        /// <summary>
        /// AI-based query parsing.
        /// </summary>
        protected virtual async Task<QueryParseResult> ParseQueryWithAIAsync(
            string query,
            QueryParseOptions options,
            CancellationToken ct)
        {
            if (AIProvider == null) throw new InvalidOperationException("AI provider not configured");

            var contextClause = options.ConversationContext?.Count > 0
                ? $"\n\nPrevious queries: {string.Join("; ", options.ConversationContext)}"
                : "";

            var prompt = $@"Parse this natural language query into structured form:
Query: {query}{contextClause}

Extract:
- Intent (search, filter, aggregate, compare, etc.)
- Entities/parameters as key:value pairs
- Confidence (0-1)

Format:
Intent: [intent]
Parameters: key1=value1, key2=value2
Confidence: [0-1]";

            var response = await AIProvider.CompleteAsync(new AIRequest
            {
                Prompt = prompt,
                MaxTokens = 200,
                Temperature = 0.2f
            }, ct);

            if (!response.Success)
                throw new Exception(response.ErrorMessage);

            return ParseQueryParseResponse(response.Content);
        }

        /// <summary>
        /// Pattern-based query parsing.
        /// </summary>
        protected virtual QueryParseResult ParseQueryWithPatterns(string query, QueryParseOptions options)
        {
            var queryLower = query.ToLowerInvariant();
            var parameters = new Dictionary<string, object>();
            var intent = "search"; // default

            // Detect intent from keywords
            if (queryLower.StartsWith("find") || queryLower.StartsWith("search") || queryLower.StartsWith("show me"))
                intent = "search";
            else if (queryLower.StartsWith("count") || queryLower.StartsWith("how many"))
                intent = "count";
            else if (queryLower.StartsWith("delete") || queryLower.StartsWith("remove"))
                intent = "delete";
            else if (queryLower.StartsWith("compare"))
                intent = "compare";
            else if (queryLower.Contains("average") || queryLower.Contains("sum") || queryLower.Contains("total"))
                intent = "aggregate";

            // Extract date ranges
            var dateMatch = Regex.Match(query, @"(?:from|since|after)\s+(\d{4}[-/]\d{1,2}[-/]\d{1,2}|\w+\s+\d{4})", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
            if (dateMatch.Success)
                parameters["dateFrom"] = dateMatch.Groups[1].Value;

            var dateToMatch = Regex.Match(query, @"(?:to|until|before)\s+(\d{4}[-/]\d{1,2}[-/]\d{1,2}|\w+\s+\d{4})", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
            if (dateToMatch.Success)
                parameters["dateTo"] = dateToMatch.Groups[1].Value;

            // Extract filters
            var filterMatch = Regex.Match(query, @"(?:where|with|containing)\s+(.+?)(?:\s+and|\s*$)", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
            if (filterMatch.Success)
                parameters["filter"] = filterMatch.Groups[1].Value.Trim();

            return new QueryParseResult
            {
                Intent = intent,
                Confidence = 0.6f,
                Parameters = parameters,
                UsedAI = false
            };
        }

        /// <inheritdoc/>
        public virtual async Task<string> ExplainAsync(
            string context,
            string question,
            CancellationToken ct = default)
        {
            if (AIProvider == null)
                return "Explanation requires an AI provider to be configured.";

            var prompt = $@"Given this context:
{context}

Answer this question in a clear, concise way:
{question}";

            var response = await AIProvider.CompleteAsync(new AIRequest
            {
                Prompt = prompt,
                MaxTokens = 300,
                Temperature = 0.5f
            }, ct);

            return response.Success ? response.Content : "Unable to generate explanation.";
        }

        private QueryParseResult ParseQueryParseResponse(string response)
        {
            var intent = "search";
            var confidence = 0.5f;
            var parameters = new Dictionary<string, object>();

            var intentMatch = Regex.Match(response, @"intent[:\s]+(\w+)", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
            if (intentMatch.Success)
                intent = intentMatch.Groups[1].Value.ToLowerInvariant();

            var confMatch = Regex.Match(response, @"confidence[:\s]+(\d+\.?\d*)", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
            if (confMatch.Success && float.TryParse(confMatch.Groups[1].Value, out var c))
                confidence = Math.Clamp(c, 0f, 1f);

            var paramsMatch = Regex.Match(response, @"parameters[:\s]+(.+?)(?:\n|$)", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
            if (paramsMatch.Success)
            {
                var pairs = paramsMatch.Groups[1].Value.Split(',');
                foreach (var pair in pairs)
                {
                    var parts = pair.Split('=');
                    if (parts.Length == 2)
                    {
                        parameters[parts[0].Trim()] = parts[1].Trim();
                    }
                }
            }

            return new QueryParseResult
            {
                Intent = intent,
                Confidence = confidence,
                Parameters = parameters,
                UsedAI = true
            };
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Gets surrounding context for a match.
        /// </summary>
        protected static string GetContext(string content, int index, int length, int windowSize)
        {
            var start = Math.Max(0, index - windowSize);
            var end = Math.Min(content.Length, index + length + windowSize);
            return content.Substring(start, end - start);
        }

        /// <summary>
        /// Gets analysis statistics.
        /// </summary>
        public virtual SemanticAnalyzerStatistics GetStatistics()
        {
            return new SemanticAnalyzerStatistics
            {
                DocumentsProcessed = Interlocked.Read(ref _documentsProcessed),
                EntitiesExtracted = Interlocked.Read(ref _entitiesExtracted),
                PIIDetected = Interlocked.Read(ref _piiDetected),
                AIProviderAvailable = IsAIAvailable,
                VectorStoreAvailable = VectorStore != null,
                KnowledgeGraphAvailable = KnowledgeGraph != null,
                RegisteredCategories = _categories.Count,
                CustomEntityTypes = _customEntityTypes.Count,
                CustomPIIPatterns = _customPIIPatterns.Count
            };
        }

        #endregion

        #region IDisposable

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Dispose managed resources
                }
                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion
    }

    /// <summary>
    /// Statistics for semantic analyzer.
    /// </summary>
    public record SemanticAnalyzerStatistics
    {
        public long DocumentsProcessed { get; init; }
        public long EntitiesExtracted { get; init; }
        public long PIIDetected { get; init; }
        public bool AIProviderAvailable { get; init; }
        public bool VectorStoreAvailable { get; init; }
        public bool KnowledgeGraphAvailable { get; init; }
        public int RegisteredCategories { get; init; }
        public int CustomEntityTypes { get; init; }
        public int CustomPIIPatterns { get; init; }
    }
}
