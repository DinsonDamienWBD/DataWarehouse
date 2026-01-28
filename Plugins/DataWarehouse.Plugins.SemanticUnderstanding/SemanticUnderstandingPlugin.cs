using DataWarehouse.Plugins.SemanticUnderstanding.Analyzers;
using DataWarehouse.Plugins.SemanticUnderstanding.Engine;
using DataWarehouse.Plugins.SemanticUnderstanding.Models;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.SemanticUnderstanding
{
    /// <summary>
    /// Production-ready semantic data understanding plugin for DataWarehouse.
    /// Extends SemanticAnalyzerBase to provide AI-powered categorization, entity extraction,
    /// relationship discovery, duplicate detection, summarization, language detection,
    /// sentiment analysis, and PII detection.
    ///
    /// All operations are provider-agnostic and work with any configured AI provider.
    /// The plugin registers as a service that other plugins can consume via message bus.
    /// </summary>
    public sealed class SemanticUnderstandingPlugin : PluginBase
    {
        #region Plugin Identity

        /// <inheritdoc/>
        public override string Id => "datawarehouse.semanticunderstanding";

        /// <inheritdoc/>
        public override string Name => "Semantic Data Understanding";

        /// <inheritdoc/>
        public override string Version => "2.0.0";

        /// <inheritdoc/>
        public override PluginCategory Category => PluginCategory.AIProvider;

        #endregion

        #region Fields

        private SemanticAnalyzerService? _analyzer;
        private IKernelContext? _kernelContext;

        // Specialized engines (extend base functionality)
        private CategorizationEngine? _categorizationEngine;
        private RelationshipEngine? _relationshipEngine;
        private DuplicateDetectionEngine? _duplicateEngine;

        // Analyzers for different content types
        private readonly TextAnalyzer _textAnalyzer;
        private readonly DocumentAnalyzer _documentAnalyzer;
        private ImageAnalyzer? _imageAnalyzer;
        private AudioAnalyzer? _audioAnalyzer;

        // Supported message topics
        private static readonly string[] SupportedTopics = new[]
        {
            "semantic.categorize",
            "semantic.categorize.train",
            "semantic.entity.extract",
            "semantic.relationship.discover",
            "semantic.duplicate.detect",
            "semantic.summarize",
            "semantic.language.detect",
            "semantic.sentiment.analyze",
            "semantic.pii.detect",
            "semantic.pii.redact",
            "semantic.analyze.batch",
            "semantic.analyze.full",
            "semantic.query.parse",
            "semantic.explain",
            "semantic.statistics"
        };

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new instance of the semantic understanding plugin.
        /// </summary>
        public SemanticUnderstandingPlugin()
        {
            _textAnalyzer = new TextAnalyzer();
            _documentAnalyzer = new DocumentAnalyzer();
        }

        #endregion

        #region Configuration

        /// <summary>
        /// Configures the AI provider for enhanced semantic operations.
        /// </summary>
        public void SetAIProvider(IAIProvider aiProvider)
        {
            EnsureAnalyzer();
            _analyzer!.SetAIProvider(aiProvider);
            ReinitializeEngines();
        }

        /// <summary>
        /// Configures the vector store for embedding-based operations.
        /// </summary>
        public void SetVectorStore(IVectorStore vectorStore)
        {
            EnsureAnalyzer();
            _analyzer!.SetVectorStore(vectorStore);
            ReinitializeEngines();
        }

        /// <summary>
        /// Configures the knowledge graph for relationship operations.
        /// </summary>
        public void SetKnowledgeGraph(IKnowledgeGraph knowledgeGraph)
        {
            EnsureAnalyzer();
            _analyzer!.SetKnowledgeGraph(knowledgeGraph);
            ReinitializeEngines();
        }

        /// <summary>
        /// Configures the kernel context for logging and service access.
        /// </summary>
        public void SetKernelContext(IKernelContext kernelContext)
        {
            _kernelContext = kernelContext;
        }

        #endregion

        #region Plugin Lifecycle

        /// <inheritdoc/>
        public override Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
        {
            EnsureAnalyzer();
            InitializeEngines();
            InitializeDefaultCategories();

            _kernelContext?.LogInfo("SemanticUnderstandingPlugin v2.0 initialized with SDK base class");

            return base.OnHandshakeAsync(request);
        }

        /// <inheritdoc/>
        public override async Task OnMessageAsync(PluginMessage message)
        {
            if (!string.IsNullOrEmpty(message.Type) && SupportedTopics.Contains(message.Type))
            {
                try
                {
                    var result = await HandleMessageAsync(message.Type, message.Payload, CancellationToken.None);
                    // Results are returned via the message handler pattern
                }
                catch (Exception ex)
                {
                    _kernelContext?.LogError($"Error handling message on type {message.Type}: {ex.Message}", ex);
                }
            }
            else
            {
                await base.OnMessageAsync(message);
            }
        }

        private void EnsureAnalyzer()
        {
            _analyzer ??= new SemanticAnalyzerService();
        }

        private void ReinitializeEngines()
        {
            InitializeEngines();
        }

        private void InitializeEngines()
        {
            var categoryHierarchy = new CategoryHierarchy();
            categoryHierarchy.InitializeDefaults();

            // Initialize specialized engines that extend base functionality
            _categorizationEngine = new CategorizationEngine(
                _analyzer?.GetAIProvider(),
                _analyzer?.GetVectorStore(),
                categoryHierarchy);

            _relationshipEngine = new RelationshipEngine(
                _analyzer?.GetAIProvider(),
                _analyzer?.GetVectorStore(),
                _analyzer?.GetKnowledgeGraph());

            _duplicateEngine = new DuplicateDetectionEngine(
                _analyzer?.GetAIProvider(),
                _analyzer?.GetVectorStore());

            // Initialize content type analyzers
            _imageAnalyzer = new ImageAnalyzer(_analyzer?.GetAIProvider());
            _audioAnalyzer = new AudioAnalyzer(_analyzer?.GetAIProvider());
        }

        private void InitializeDefaultCategories()
        {
            // Register common document categories
            var defaultCategories = new[]
            {
                new SDK.AI.CategoryDefinition { Id = "document", Name = "Document", Keywords = new[] { "document", "file", "report" } },
                new SDK.AI.CategoryDefinition { Id = "document.legal", Name = "Legal Document", ParentId = "document", Keywords = new[] { "contract", "agreement", "legal", "terms", "policy" } },
                new SDK.AI.CategoryDefinition { Id = "document.financial", Name = "Financial Document", ParentId = "document", Keywords = new[] { "invoice", "receipt", "statement", "budget", "financial" } },
                new SDK.AI.CategoryDefinition { Id = "document.technical", Name = "Technical Document", ParentId = "document", Keywords = new[] { "specification", "technical", "manual", "guide", "documentation" } },
                new SDK.AI.CategoryDefinition { Id = "communication", Name = "Communication", Keywords = new[] { "email", "message", "chat", "letter" } },
                new SDK.AI.CategoryDefinition { Id = "media", Name = "Media", Keywords = new[] { "image", "video", "audio", "photo" } },
                new SDK.AI.CategoryDefinition { Id = "code", Name = "Source Code", Keywords = new[] { "code", "script", "program", "function", "class" } }
            };

            foreach (var category in defaultCategories)
            {
                _analyzer?.RegisterCategory(category);
            }
        }

        #endregion

        #region Message Handlers

        private async Task<object?> HandleMessageAsync(string topic, object? payload, CancellationToken ct)
        {
            EnsureAnalyzer();

            return topic switch
            {
                "semantic.categorize" => await HandleCategorizeAsync(payload, ct),
                "semantic.categorize.train" => await HandleTrainCategoryAsync(payload, ct),
                "semantic.entity.extract" => await HandleEntityExtractionAsync(payload, ct),
                "semantic.relationship.discover" => await HandleRelationshipDiscoveryAsync(payload, ct),
                "semantic.duplicate.detect" => await HandleDuplicateDetectionAsync(payload, ct),
                "semantic.summarize" => await HandleSummarizationAsync(payload, ct),
                "semantic.language.detect" => await HandleLanguageDetectionAsync(payload, ct),
                "semantic.sentiment.analyze" => await HandleSentimentAnalysisAsync(payload, ct),
                "semantic.pii.detect" => await HandlePIIDetectionAsync(payload, ct),
                "semantic.pii.redact" => await HandlePIIRedactionAsync(payload, ct),
                "semantic.analyze.batch" => await HandleBatchAnalysisAsync(payload, ct),
                "semantic.analyze.full" => await HandleFullAnalysisAsync(payload, ct),
                "semantic.query.parse" => await HandleQueryParseAsync(payload, ct),
                "semantic.explain" => await HandleExplainAsync(payload, ct),
                "semantic.statistics" => GetStatistics(),
                _ => throw new NotSupportedException($"Topic not supported: {topic}")
            };
        }

        private async Task<SDK.AI.CategorizationResult> HandleCategorizeAsync(object? payload, CancellationToken ct)
        {
            var request = DeserializePayload<CategorizationRequest>(payload);

            if (string.IsNullOrEmpty(request.Text))
                throw new ArgumentException("Text is required for categorization");

            return await _analyzer!.CategorizeAsync(
                request.Text,
                new SDK.AI.CategorizationOptions
                {
                    PreferAI = request.UseAI ?? true,
                    MinConfidence = request.MinConfidence ?? 0.3f
                },
                ct);
        }

        private async Task<bool> HandleTrainCategoryAsync(object? payload, CancellationToken ct)
        {
            var request = DeserializePayload<CategoryTrainingRequest>(payload);

            if (string.IsNullOrEmpty(request.CategoryId) || request.Examples == null)
                throw new ArgumentException("CategoryId and Examples are required");

            await _analyzer!.TrainCategoryAsync(request.CategoryId, request.Examples, ct);
            return true;
        }

        private async Task<SDK.AI.EntityExtractionResult> HandleEntityExtractionAsync(object? payload, CancellationToken ct)
        {
            var request = DeserializePayload<EntityExtractionRequest>(payload);

            if (string.IsNullOrEmpty(request.Text))
                throw new ArgumentException("Text is required for entity extraction");

            return await _analyzer!.ExtractEntitiesAsync(
                request.Text,
                new SDK.AI.EntityExtractionOptions
                {
                    UseAI = request.UseAI ?? true,
                    IncludeContext = request.IncludeContext ?? true,
                    ContextWindowSize = request.ContextWindowSize ?? 50
                },
                ct);
        }

        private async Task<SDK.AI.RelationshipDiscoveryResult> HandleRelationshipDiscoveryAsync(object? payload, CancellationToken ct)
        {
            var request = DeserializePayload<RelationshipDiscoveryRequest>(payload);

            if (request.Document == null)
                throw new ArgumentException("Document is required for relationship discovery");

            var doc = new SDK.AI.ContentItem
            {
                Id = request.Document.Id,
                Text = request.Document.Text ?? "",
                Embedding = request.Document.Embedding
            };

            var corpus = request.Corpus?.Select(d => new SDK.AI.ContentItem
            {
                Id = d.Id,
                Text = d.Text ?? "",
                Embedding = d.Embedding
            }) ?? Enumerable.Empty<SDK.AI.ContentItem>();

            return await _analyzer!.DiscoverRelationshipsAsync(doc, corpus, null, ct);
        }

        private async Task<SDK.AI.DuplicateDetectionResult> HandleDuplicateDetectionAsync(object? payload, CancellationToken ct)
        {
            var request = DeserializePayload<DuplicateDetectionRequest>(payload);

            if (string.IsNullOrEmpty(request.DocumentId) || string.IsNullOrEmpty(request.Text))
                throw new ArgumentException("DocumentId and Text are required for duplicate detection");

            var corpus = request.Corpus?.Select(c => new SDK.AI.ContentItem
            {
                Id = c.Id,
                Text = c.Text,
                Embedding = c.Embedding
            }) ?? Enumerable.Empty<SDK.AI.ContentItem>();

            return await _analyzer!.DetectDuplicatesAsync(
                request.Text,
                request.DocumentId,
                corpus,
                new SDK.AI.DuplicateDetectionOptions
                {
                    EnableSemanticDetection = request.EnableSemantic ?? true,
                    ExactDuplicateThreshold = request.ExactThreshold ?? 0.95f,
                    NearDuplicateThreshold = request.NearThreshold ?? 0.8f
                },
                ct);
        }

        private async Task<SDK.AI.SummaryResult> HandleSummarizationAsync(object? payload, CancellationToken ct)
        {
            var request = DeserializePayload<SummarizationRequest>(payload);

            if (string.IsNullOrEmpty(request.Text) && (request.Texts == null || request.Texts.Count == 0))
                throw new ArgumentException("Text or Texts is required for summarization");

            var options = new SDK.AI.SummaryOptions
            {
                TargetLengthWords = request.TargetLength ?? 150,
                PreferAbstractive = request.UseAI ?? true,
                FocusAspects = request.FocusAspects
            };

            if (request.Texts != null && request.Texts.Count > 1)
            {
                return await _analyzer!.SummarizeMultipleAsync(request.Texts, options, ct);
            }

            return await _analyzer!.SummarizeAsync(request.Text!, options, ct);
        }

        private async Task<SDK.AI.LanguageDetectionResult> HandleLanguageDetectionAsync(object? payload, CancellationToken ct)
        {
            var request = DeserializePayload<LanguageDetectionRequest>(payload);

            if (string.IsNullOrEmpty(request.Text))
                throw new ArgumentException("Text is required for language detection");

            return await _analyzer!.DetectLanguageAsync(
                request.Text,
                new SDK.AI.LanguageDetectionOptions
                {
                    UseAI = request.UseAI ?? true,
                    DetectMultiple = request.DetectMultiple ?? true
                },
                ct);
        }

        private async Task<SDK.AI.SentimentResult> HandleSentimentAnalysisAsync(object? payload, CancellationToken ct)
        {
            var request = DeserializePayload<SentimentAnalysisRequest>(payload);

            if (string.IsNullOrEmpty(request.Text))
                throw new ArgumentException("Text is required for sentiment analysis");

            return await _analyzer!.AnalyzeSentimentAsync(
                request.Text,
                new SDK.AI.SentimentOptions
                {
                    UseAI = request.UseAI ?? true,
                    AnalyzeSentences = request.IncludeSentences ?? true,
                    AnalyzeAspects = request.EnableAspects ?? true,
                    DetectEmotions = request.DetectEmotions ?? true,
                    Aspects = request.Aspects
                },
                ct);
        }

        private async Task<SDK.AI.PIIDetectionResult> HandlePIIDetectionAsync(object? payload, CancellationToken ct)
        {
            var request = DeserializePayload<PIIDetectionRequest>(payload);

            if (string.IsNullOrEmpty(request.Text))
                throw new ArgumentException("Text is required for PII detection");

            return await _analyzer!.DetectPIIAsync(
                request.Text,
                new SDK.AI.PIIDetectionOptions
                {
                    UseAI = request.UseAI ?? true,
                    IncludeContext = request.IncludeContext ?? true,
                    ComplianceRegulations = request.Regulations
                },
                ct);
        }

        private async Task<SDK.AI.PIIRedactionResult> HandlePIIRedactionAsync(object? payload, CancellationToken ct)
        {
            var request = DeserializePayload<PIIRedactionRequest>(payload);

            if (string.IsNullOrEmpty(request.Text))
                throw new ArgumentException("Text is required for PII redaction");

            return await _analyzer!.RedactPIIAsync(
                request.Text,
                new SDK.AI.PIIRedactionOptions
                {
                    Strategy = SDK.AI.RedactionStrategy.Mask,
                    PreserveFormat = true
                },
                ct);
        }

        private async Task<BatchAnalysisResult> HandleBatchAnalysisAsync(object? payload, CancellationToken ct)
        {
            var request = DeserializePayload<BatchAnalysisRequest>(payload);

            if (request.Documents == null || request.Documents.Count == 0)
                throw new ArgumentException("Documents are required for batch analysis");

            var results = new ConcurrentDictionary<string, DocumentAnalysisResult>();
            var sw = Stopwatch.StartNew();

            await Parallel.ForEachAsync(
                request.Documents,
                new ParallelOptions { MaxDegreeOfParallelism = request.MaxParallelism ?? 4, CancellationToken = ct },
                async (doc, token) =>
                {
                    var result = await AnalyzeDocumentAsync(doc, request, token);
                    results[doc.Id] = result;
                });

            sw.Stop();

            return new BatchAnalysisResult
            {
                Results = results.ToDictionary(kv => kv.Key, kv => kv.Value),
                TotalDocuments = request.Documents.Count,
                SuccessCount = results.Values.Count(r => r.Success),
                Duration = sw.Elapsed
            };
        }

        private async Task<SDK.AI.SemanticAnalysisResult> HandleFullAnalysisAsync(object? payload, CancellationToken ct)
        {
            var request = DeserializePayload<FullAnalysisRequest>(payload);

            if (string.IsNullOrEmpty(request.Text))
                throw new ArgumentException("Text is required for full analysis");

            return await _analyzer!.AnalyzeAsync(
                request.Text,
                new SDK.AI.SemanticAnalysisOptions
                {
                    IncludeCategorization = request.IncludeCategorization ?? true,
                    IncludeEntities = request.IncludeEntities ?? true,
                    IncludeSentiment = request.IncludeSentiment ?? true,
                    IncludeLanguage = request.IncludeLanguage ?? true,
                    IncludeSummary = request.IncludeSummary ?? true,
                    IncludePII = request.IncludePII ?? false,
                    PreferAI = request.UseAI ?? true
                },
                ct);
        }

        private async Task<SDK.AI.QueryParseResult> HandleQueryParseAsync(object? payload, CancellationToken ct)
        {
            var request = DeserializePayload<QueryParseRequest>(payload);

            if (string.IsNullOrEmpty(request.Query))
                throw new ArgumentException("Query is required");

            return await _analyzer!.ParseQueryAsync(
                request.Query,
                new SDK.AI.QueryParseOptions
                {
                    UseAI = request.UseAI ?? true,
                    ConversationContext = request.Context
                },
                ct);
        }

        private async Task<string> HandleExplainAsync(object? payload, CancellationToken ct)
        {
            var request = DeserializePayload<ExplainRequest>(payload);

            if (string.IsNullOrEmpty(request.Context) || string.IsNullOrEmpty(request.Question))
                throw new ArgumentException("Context and Question are required");

            return await _analyzer!.ExplainAsync(request.Context, request.Question, ct);
        }

        private async Task<DocumentAnalysisResult> AnalyzeDocumentAsync(
            BatchDocument doc,
            BatchAnalysisRequest request,
            CancellationToken ct)
        {
            var result = new DocumentAnalysisResult { DocumentId = doc.Id };

            try
            {
                var fullResult = await _analyzer!.AnalyzeAsync(
                    doc.Text,
                    new SDK.AI.SemanticAnalysisOptions
                    {
                        IncludeLanguage = request.IncludeLanguage ?? true,
                        IncludeCategorization = request.IncludeCategorization ?? true,
                        IncludeEntities = request.IncludeEntities ?? true,
                        IncludeSummary = request.IncludeSummary ?? true,
                        IncludeSentiment = request.IncludeSentiment ?? true,
                        IncludePII = request.IncludePII ?? true,
                        PreferAI = true
                    },
                    ct);

                result.Success = fullResult.Success;
                result.Error = fullResult.Error;
                result.Language = fullResult.Language;
                result.Category = fullResult.Categorization;
                result.Entities = fullResult.Entities;
                result.Summary = fullResult.Summary;
                result.Sentiment = fullResult.Sentiment;
                result.PII = fullResult.PII;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }

            return result;
        }

        #endregion

        #region Public API Methods

        /// <summary>
        /// Analyzes content and returns comprehensive semantic information.
        /// </summary>
        public async Task<SDK.AI.SemanticAnalysisResult> AnalyzeContentAsync(
            Stream content,
            string contentType,
            SDK.AI.SemanticAnalysisOptions? options = null,
            CancellationToken ct = default)
        {
            EnsureAnalyzer();

            // Select appropriate analyzer based on content type
            if (_imageAnalyzer!.CanAnalyze(contentType))
            {
                var imageResult = await _imageAnalyzer.AnalyzeAsync(content, contentType, null, ct);
                return ConvertToSemanticResult(imageResult);
            }

            if (_audioAnalyzer!.CanAnalyze(contentType))
            {
                var audioResult = await _audioAnalyzer.AnalyzeAsync(content, contentType, null, ct);
                return ConvertToSemanticResult(audioResult);
            }

            if (_documentAnalyzer.CanAnalyze(contentType))
            {
                var docResult = await _documentAnalyzer.AnalyzeAsync(content, contentType, null, ct);
                return ConvertToSemanticResult(docResult);
            }

            // Default to text analysis
            return await _analyzer!.AnalyzeStreamAsync(content, contentType, options, ct);
        }

        /// <summary>
        /// Categorizes text content using the SDK base implementation.
        /// </summary>
        public Task<SDK.AI.CategorizationResult> CategorizeAsync(
            string text,
            SDK.AI.CategorizationOptions? options = null,
            CancellationToken ct = default)
        {
            EnsureAnalyzer();
            return _analyzer!.CategorizeAsync(text, options, ct);
        }

        /// <summary>
        /// Extracts entities from text using the SDK base implementation.
        /// </summary>
        public Task<SDK.AI.EntityExtractionResult> ExtractEntitiesAsync(
            string text,
            SDK.AI.EntityExtractionOptions? options = null,
            CancellationToken ct = default)
        {
            EnsureAnalyzer();
            return _analyzer!.ExtractEntitiesAsync(text, options, ct);
        }

        /// <summary>
        /// Generates a summary of the text using the SDK base implementation.
        /// </summary>
        public Task<SDK.AI.SummaryResult> SummarizeAsync(
            string text,
            SDK.AI.SummaryOptions? options = null,
            CancellationToken ct = default)
        {
            EnsureAnalyzer();
            return _analyzer!.SummarizeAsync(text, options, ct);
        }

        /// <summary>
        /// Detects the language of the text using the SDK base implementation.
        /// </summary>
        public Task<SDK.AI.LanguageDetectionResult> DetectLanguageAsync(
            string text,
            SDK.AI.LanguageDetectionOptions? options = null,
            CancellationToken ct = default)
        {
            EnsureAnalyzer();
            return _analyzer!.DetectLanguageAsync(text, options, ct);
        }

        /// <summary>
        /// Analyzes sentiment of the text using the SDK base implementation.
        /// </summary>
        public Task<SDK.AI.SentimentResult> AnalyzeSentimentAsync(
            string text,
            SDK.AI.SentimentOptions? options = null,
            CancellationToken ct = default)
        {
            EnsureAnalyzer();
            return _analyzer!.AnalyzeSentimentAsync(text, options, ct);
        }

        /// <summary>
        /// Detects PII in the text using the SDK base implementation.
        /// </summary>
        public Task<SDK.AI.PIIDetectionResult> DetectPIIAsync(
            string text,
            SDK.AI.PIIDetectionOptions? options = null,
            CancellationToken ct = default)
        {
            EnsureAnalyzer();
            return _analyzer!.DetectPIIAsync(text, options, ct);
        }

        /// <summary>
        /// Detects duplicate documents using the SDK base implementation.
        /// </summary>
        public Task<SDK.AI.DuplicateDetectionResult> DetectDuplicatesAsync(
            string documentId,
            string text,
            IEnumerable<SDK.AI.ContentItem> corpus,
            SDK.AI.DuplicateDetectionOptions? options = null,
            CancellationToken ct = default)
        {
            EnsureAnalyzer();
            return _analyzer!.DetectDuplicatesAsync(text, documentId, corpus, options, ct);
        }

        /// <summary>
        /// Discovers relationships between documents using the SDK base implementation.
        /// </summary>
        public Task<SDK.AI.RelationshipDiscoveryResult> DiscoverRelationshipsAsync(
            SDK.AI.ContentItem document,
            IEnumerable<SDK.AI.ContentItem> corpus,
            SDK.AI.RelationshipDiscoveryOptions? options = null,
            CancellationToken ct = default)
        {
            EnsureAnalyzer();
            return _analyzer!.DiscoverRelationshipsAsync(document, corpus, options, ct);
        }

        /// <summary>
        /// Adds a custom category using the SDK base implementation.
        /// </summary>
        public void AddCategory(SDK.AI.CategoryDefinition category)
        {
            EnsureAnalyzer();
            _analyzer!.RegisterCategory(category);
        }

        /// <summary>
        /// Registers a custom entity type using the SDK base implementation.
        /// </summary>
        public void RegisterEntityType(SDK.AI.CustomEntityTypeDefinition entityType)
        {
            EnsureAnalyzer();
            _analyzer!.RegisterEntityType(entityType);
        }

        /// <summary>
        /// Registers a custom PII pattern using the SDK base implementation.
        /// </summary>
        public void RegisterPIIPattern(SDK.AI.CustomPIIPatternDefinition pattern)
        {
            EnsureAnalyzer();
            _analyzer!.RegisterPIIPattern(pattern);
        }

        /// <summary>
        /// Gets plugin statistics.
        /// </summary>
        public SemanticUnderstandingStatistics GetStatistics()
        {
            EnsureAnalyzer();
            var baseStats = _analyzer!.GetStatistics();

            return new SemanticUnderstandingStatistics
            {
                DocumentsProcessed = baseStats.DocumentsProcessed,
                EntitiesExtracted = baseStats.EntitiesExtracted,
                PIIDetected = baseStats.PIIDetected,
                AIProviderAvailable = baseStats.AIProviderAvailable,
                VectorStoreAvailable = baseStats.VectorStoreAvailable,
                KnowledgeGraphAvailable = baseStats.KnowledgeGraphAvailable,
                RegisteredCategories = baseStats.RegisteredCategories,
                CustomEntityTypes = baseStats.CustomEntityTypes,
                CustomPIIPatterns = baseStats.CustomPIIPatterns
            };
        }

        #endregion

        #region Helper Methods

        private T DeserializePayload<T>(object? payload) where T : new()
        {
            if (payload == null)
                return new T();

            if (payload is T typed)
                return typed;

            if (payload is JsonElement json)
                return json.Deserialize<T>() ?? new T();

            if (payload is string str)
                return JsonSerializer.Deserialize<T>(str) ?? new T();

            return new T();
        }

        private SDK.AI.SemanticAnalysisResult ConvertToSemanticResult(ContentAnalysisResult result)
        {
            return new SDK.AI.SemanticAnalysisResult
            {
                Success = true,
                Summary = result.Summary != null ? new SDK.AI.SummaryResult { Summary = result.Summary } : null,
                Language = result.Language != null ? new SDK.AI.LanguageDetectionResult
                {
                    PrimaryLanguage = new SDK.AI.DetectedLanguage
                    {
                        Code = result.Language.PrimaryLanguage,
                        Confidence = result.Language.Confidence
                    }
                } : null
            };
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["SupportedTopics"] = SupportedTopics;
            metadata["Features"] = new[]
            {
                "Categorization",
                "EntityExtraction",
                "RelationshipDiscovery",
                "DuplicateDetection",
                "Summarization",
                "LanguageDetection",
                "SentimentAnalysis",
                "PIIDetection",
                "QueryParsing",
                "Explanation"
            };
            metadata["SDKBaseClass"] = "SemanticAnalyzerBase";

            var stats = GetStatistics();
            metadata["AIProviderAvailable"] = stats.AIProviderAvailable;
            metadata["VectorStoreAvailable"] = stats.VectorStoreAvailable;
            metadata["DocumentsProcessed"] = stats.DocumentsProcessed;

            return metadata;
        }

        #endregion
    }

    #region Internal Service Class

    /// <summary>
    /// Internal service that wraps SemanticAnalyzerBase for plugin use.
    /// </summary>
    internal sealed class SemanticAnalyzerService : SemanticAnalyzerBase
    {
        public IAIProvider? GetAIProvider() => AIProvider;
        public IVectorStore? GetVectorStore() => VectorStore;
        public IKnowledgeGraph? GetKnowledgeGraph() => KnowledgeGraph;

        public new void SetAIProvider(IAIProvider? provider) => base.SetAIProvider(provider);
        public new void SetVectorStore(IVectorStore? store) => base.SetVectorStore(store);
        public new void SetKnowledgeGraph(IKnowledgeGraph? graph) => base.SetKnowledgeGraph(graph);
    }

    #endregion

    #region Request/Response DTOs

    public sealed class CategorizationRequest
    {
        public string? Text { get; init; }
        public bool? UseAI { get; init; }
        public float? MinConfidence { get; init; }
    }

    public sealed class CategoryTrainingRequest
    {
        public string? CategoryId { get; init; }
        public List<string>? Examples { get; init; }
    }

    public sealed class EntityExtractionRequest
    {
        public string? Text { get; init; }
        public bool? UseAI { get; init; }
        public bool? IncludeContext { get; init; }
        public int? ContextWindowSize { get; init; }
    }

    public sealed class RelationshipDiscoveryRequest
    {
        public DocumentInfo? Document { get; init; }
        public List<DocumentInfo>? Corpus { get; init; }
    }

    public sealed class DuplicateDetectionRequest
    {
        public string? DocumentId { get; init; }
        public string? Text { get; init; }
        public List<CorpusItem>? Corpus { get; init; }
        public bool? EnableSemantic { get; init; }
        public float? ExactThreshold { get; init; }
        public float? NearThreshold { get; init; }
    }

    public sealed class CorpusItem
    {
        public string Id { get; init; } = "";
        public string Text { get; init; } = "";
        public float[]? Embedding { get; init; }
    }

    public sealed class SummarizationRequest
    {
        public string? Text { get; init; }
        public List<string>? Texts { get; init; }
        public int? TargetLength { get; init; }
        public bool? UseAI { get; init; }
        public List<string>? FocusAspects { get; init; }
    }

    public sealed class LanguageDetectionRequest
    {
        public string? Text { get; init; }
        public bool? UseAI { get; init; }
        public bool? DetectMultiple { get; init; }
    }

    public sealed class SentimentAnalysisRequest
    {
        public string? Text { get; init; }
        public bool? UseAI { get; init; }
        public bool? IncludeSentences { get; init; }
        public bool? EnableAspects { get; init; }
        public bool? DetectEmotions { get; init; }
        public List<string>? Aspects { get; init; }
    }

    public sealed class PIIDetectionRequest
    {
        public string? Text { get; init; }
        public bool? UseAI { get; init; }
        public bool? IncludeContext { get; init; }
        public List<string>? Regulations { get; init; }
    }

    public sealed class PIIRedactionRequest
    {
        public string? Text { get; init; }
        public bool? UseAI { get; init; }
    }

    public sealed class BatchAnalysisRequest
    {
        public List<BatchDocument>? Documents { get; init; }
        public int? MaxParallelism { get; init; }
        public bool? IncludeLanguage { get; init; }
        public bool? IncludeCategorization { get; init; }
        public bool? IncludeEntities { get; init; }
        public bool? IncludeSummary { get; init; }
        public bool? IncludeSentiment { get; init; }
        public bool? IncludePII { get; init; }
    }

    public sealed class FullAnalysisRequest
    {
        public string? Text { get; init; }
        public bool? UseAI { get; init; }
        public bool? IncludeCategorization { get; init; }
        public bool? IncludeEntities { get; init; }
        public bool? IncludeSentiment { get; init; }
        public bool? IncludeLanguage { get; init; }
        public bool? IncludeSummary { get; init; }
        public bool? IncludePII { get; init; }
    }

    public sealed class QueryParseRequest
    {
        public string? Query { get; init; }
        public bool? UseAI { get; init; }
        public List<string>? Context { get; init; }
    }

    public sealed class ExplainRequest
    {
        public string? Context { get; init; }
        public string? Question { get; init; }
    }

    public sealed class BatchDocument
    {
        public required string Id { get; init; }
        public required string Text { get; init; }
        public string? ContentType { get; init; }
        public Dictionary<string, object>? Metadata { get; init; }
    }

    public sealed class BatchAnalysisResult
    {
        public Dictionary<string, DocumentAnalysisResult> Results { get; init; } = new();
        public int TotalDocuments { get; init; }
        public int SuccessCount { get; init; }
        public TimeSpan Duration { get; init; }
    }

    public sealed class DocumentAnalysisResult
    {
        public string? DocumentId { get; set; }
        public bool Success { get; set; }
        public string? Error { get; set; }
        public SDK.AI.LanguageDetectionResult? Language { get; set; }
        public SDK.AI.CategorizationResult? Category { get; set; }
        public SDK.AI.EntityExtractionResult? Entities { get; set; }
        public SDK.AI.SummaryResult? Summary { get; set; }
        public SDK.AI.SentimentResult? Sentiment { get; set; }
        public SDK.AI.PIIDetectionResult? PII { get; set; }
    }

    public sealed class SemanticUnderstandingStatistics
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

    #endregion
}
