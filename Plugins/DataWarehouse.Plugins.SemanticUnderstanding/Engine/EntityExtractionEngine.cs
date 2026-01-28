using DataWarehouse.Plugins.SemanticUnderstanding.Models;
using DataWarehouse.SDK.AI;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.SemanticUnderstanding.Engine
{
    /// <summary>
    /// Engine for Named Entity Recognition (NER) using patterns and AI.
    /// </summary>
    public sealed class EntityExtractionEngine
    {
        private readonly IAIProvider? _aiProvider;
        private readonly ConcurrentDictionary<string, CustomEntityType> _customTypes;
        private readonly EntityPatterns _patterns;

        public EntityExtractionEngine(IAIProvider? aiProvider = null)
        {
            _aiProvider = aiProvider;
            _customTypes = new ConcurrentDictionary<string, CustomEntityType>();
            _patterns = new EntityPatterns();
        }

        /// <summary>
        /// Extracts entities from text.
        /// </summary>
        public async Task<EntityExtractionResult> ExtractAsync(
            string text,
            EntityExtractionOptions? options = null,
            CancellationToken ct = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            options ??= new EntityExtractionOptions();

            var entities = new List<ExtractedEntity>();
            var usedAI = false;

            // Pattern-based extraction
            var patternEntities = ExtractWithPatterns(text, options);
            entities.AddRange(patternEntities);

            // AI-based extraction for richer results
            if (options.UseAI && _aiProvider != null && _aiProvider.IsAvailable)
            {
                var aiEntities = await ExtractWithAIAsync(text, options, ct);
                if (aiEntities.Count > 0)
                {
                    usedAI = true;
                    // Merge AI entities with pattern entities, preferring AI
                    entities = MergeEntities(entities, aiEntities);
                }
            }

            // Custom type extraction
            foreach (var customType in _customTypes.Values)
            {
                var customEntities = ExtractCustomType(text, customType);
                entities.AddRange(customEntities);
            }

            // Deduplicate overlapping entities
            entities = DeduplicateEntities(entities);

            // Add context if requested
            if (options.IncludeContext)
            {
                entities = AddContext(text, entities, options.ContextWindowSize);
            }

            // Sort by position
            entities = entities.OrderBy(e => e.StartPosition).ToList();

            sw.Stop();

            return new EntityExtractionResult
            {
                Entities = entities,
                Duration = sw.Elapsed,
                UsedAI = usedAI
            };
        }

        /// <summary>
        /// Extracts entities using regex patterns.
        /// </summary>
        private List<ExtractedEntity> ExtractWithPatterns(string text, EntityExtractionOptions options)
        {
            var entities = new List<ExtractedEntity>();

            // Email
            if (options.EnabledTypes.Count == 0 || options.EnabledTypes.Contains(EntityType.Email))
            {
                entities.AddRange(ExtractPattern(text, EntityType.Email, _patterns.EmailPattern));
            }

            // URL
            if (options.EnabledTypes.Count == 0 || options.EnabledTypes.Contains(EntityType.Url))
            {
                entities.AddRange(ExtractPattern(text, EntityType.Url, _patterns.UrlPattern));
            }

            // Phone numbers
            if (options.EnabledTypes.Count == 0 || options.EnabledTypes.Contains(EntityType.PhoneNumber))
            {
                entities.AddRange(ExtractPattern(text, EntityType.PhoneNumber, _patterns.PhonePattern));
            }

            // IP addresses
            if (options.EnabledTypes.Count == 0 || options.EnabledTypes.Contains(EntityType.IpAddress))
            {
                entities.AddRange(ExtractPattern(text, EntityType.IpAddress, _patterns.IPAddressPattern));
            }

            // Dates
            if (options.EnabledTypes.Count == 0 || options.EnabledTypes.Contains(EntityType.Date))
            {
                entities.AddRange(ExtractPattern(text, EntityType.Date, _patterns.DatePattern));
            }

            // Money
            if (options.EnabledTypes.Count == 0 || options.EnabledTypes.Contains(EntityType.Money))
            {
                entities.AddRange(ExtractPattern(text, EntityType.Money, _patterns.MoneyPattern));
            }

            // Percentage
            if (options.EnabledTypes.Count == 0 || options.EnabledTypes.Contains(EntityType.Percentage))
            {
                entities.AddRange(ExtractPattern(text, EntityType.Percentage, _patterns.PercentagePattern));
            }

            // File paths
            if (options.EnabledTypes.Count == 0 || options.EnabledTypes.Contains(EntityType.FilePath))
            {
                entities.AddRange(ExtractPattern(text, EntityType.FilePath, _patterns.FilePathPattern));
            }

            return entities;
        }

        /// <summary>
        /// Extracts entities from a single pattern.
        /// </summary>
        private List<ExtractedEntity> ExtractPattern(string text, EntityType type, Regex pattern)
        {
            var entities = new List<ExtractedEntity>();

            try
            {
                var matches = pattern.Matches(text);
                foreach (Match match in matches)
                {
                    entities.Add(new ExtractedEntity
                    {
                        Text = match.Value,
                        NormalizedText = NormalizeEntity(match.Value, type),
                        Type = type,
                        Confidence = 0.9f, // High confidence for pattern matches
                        StartPosition = match.Index,
                        Length = match.Length
                    });
                }
            }
            catch { /* Invalid regex or match error */ }

            return entities;
        }

        /// <summary>
        /// Extracts entities using AI.
        /// </summary>
        private async Task<List<ExtractedEntity>> ExtractWithAIAsync(
            string text,
            EntityExtractionOptions options,
            CancellationToken ct)
        {
            if (_aiProvider == null || !_aiProvider.IsAvailable)
                return new List<ExtractedEntity>();

            // Truncate text to avoid token limits
            var truncatedText = text.Length > 4000 ? text[..4000] : text;

            var request = new AIRequest
            {
                Prompt = $@"Extract all named entities from the following text. For each entity, identify:
- The exact text
- Entity type (Person, Organization, Location, Date, Money, etc.)
- Confidence (0.0-1.0)

Text:
{truncatedText}

Format your response as a list:
ENTITY: [text] | TYPE: [type] | CONFIDENCE: [0.0-1.0]

Only include entities with confidence >= 0.5",
                SystemMessage = "You are a Named Entity Recognition system. Extract entities accurately with proper classification.",
                MaxTokens = 1000
            };

            var response = await _aiProvider.CompleteAsync(request, ct);

            if (!response.Success)
                return new List<ExtractedEntity>();

            return ParseAIEntities(response.Content, text);
        }

        /// <summary>
        /// Parses AI response into entities.
        /// </summary>
        private List<ExtractedEntity> ParseAIEntities(string response, string originalText)
        {
            var entities = new List<ExtractedEntity>();
            var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var match = Regex.Match(line, @"ENTITY:\s*(.+?)\s*\|\s*TYPE:\s*(\w+)\s*\|\s*CONFIDENCE:\s*([0-9.]+)");
                if (match.Success)
                {
                    var entityText = match.Groups[1].Value.Trim();
                    var typeStr = match.Groups[2].Value.Trim();
                    var confidence = float.TryParse(match.Groups[3].Value, out var c) ? c : 0.5f;

                    // Find position in original text
                    var position = originalText.IndexOf(entityText, StringComparison.OrdinalIgnoreCase);

                    var entityType = ParseEntityType(typeStr);

                    entities.Add(new ExtractedEntity
                    {
                        Text = entityText,
                        NormalizedText = NormalizeEntity(entityText, entityType),
                        Type = entityType,
                        Confidence = Math.Clamp(confidence, 0, 1),
                        StartPosition = position >= 0 ? position : 0,
                        Length = entityText.Length
                    });
                }
            }

            return entities;
        }

        /// <summary>
        /// Parses entity type from string.
        /// </summary>
        private EntityType ParseEntityType(string typeStr)
        {
            return typeStr.ToLowerInvariant() switch
            {
                "person" => EntityType.Person,
                "organization" or "org" or "company" => EntityType.Organization,
                "location" or "place" or "geo" => EntityType.Location,
                "country" => EntityType.Country,
                "city" => EntityType.City,
                "date" or "datetime" => EntityType.Date,
                "time" => EntityType.Time,
                "money" or "currency" or "amount" => EntityType.Money,
                "percentage" or "percent" => EntityType.Percentage,
                "phone" or "phonenumber" => EntityType.PhoneNumber,
                "email" => EntityType.Email,
                "url" or "website" or "link" => EntityType.Url,
                "address" => EntityType.Address,
                "product" => EntityType.Product,
                "event" => EntityType.Event,
                "skill" => EntityType.Skill,
                _ => EntityType.Unknown
            };
        }

        /// <summary>
        /// Normalizes entity text based on type.
        /// </summary>
        private string NormalizeEntity(string text, EntityType type)
        {
            return type switch
            {
                EntityType.Email => text.ToLowerInvariant().Trim(),
                EntityType.PhoneNumber => Regex.Replace(text, @"[^\d+]", ""),
                EntityType.Url => text.Trim().TrimEnd('/'),
                EntityType.Money => Regex.Replace(text, @"[^\d.,]", ""),
                _ => text.Trim()
            };
        }

        /// <summary>
        /// Merges entities from different sources.
        /// </summary>
        private List<ExtractedEntity> MergeEntities(
            List<ExtractedEntity> patternEntities,
            List<ExtractedEntity> aiEntities)
        {
            var merged = new List<ExtractedEntity>(aiEntities);

            // Add pattern entities that don't overlap with AI entities
            foreach (var pe in patternEntities)
            {
                var hasOverlap = aiEntities.Any(ae =>
                    (pe.StartPosition >= ae.StartPosition && pe.StartPosition < ae.StartPosition + ae.Length) ||
                    (ae.StartPosition >= pe.StartPosition && ae.StartPosition < pe.StartPosition + pe.Length));

                if (!hasOverlap)
                {
                    merged.Add(pe);
                }
            }

            return merged;
        }

        /// <summary>
        /// Deduplicates overlapping entities.
        /// </summary>
        private List<ExtractedEntity> DeduplicateEntities(List<ExtractedEntity> entities)
        {
            if (entities.Count <= 1)
                return entities;

            var sorted = entities.OrderBy(e => e.StartPosition).ThenByDescending(e => e.Confidence).ToList();
            var result = new List<ExtractedEntity>();
            var covered = new List<(int Start, int End)>();

            foreach (var entity in sorted)
            {
                var end = entity.StartPosition + entity.Length;
                var isOverlapping = covered.Any(c =>
                    (entity.StartPosition >= c.Start && entity.StartPosition < c.End) ||
                    (end > c.Start && end <= c.End));

                if (!isOverlapping)
                {
                    result.Add(entity);
                    covered.Add((entity.StartPosition, end));
                }
            }

            return result;
        }

        /// <summary>
        /// Adds surrounding context to entities.
        /// </summary>
        private List<ExtractedEntity> AddContext(
            string text,
            List<ExtractedEntity> entities,
            int windowSize)
        {
            return entities.Select(e =>
            {
                var contextStart = Math.Max(0, e.StartPosition - windowSize);
                var contextEnd = Math.Min(text.Length, e.StartPosition + e.Length + windowSize);
                var context = text[contextStart..contextEnd];

                return new ExtractedEntity
                {
                    Text = e.Text,
                    NormalizedText = e.NormalizedText,
                    Type = e.Type,
                    SubType = e.SubType,
                    Confidence = e.Confidence,
                    StartPosition = e.StartPosition,
                    Length = e.Length,
                    Context = context,
                    KnowledgeGraphId = e.KnowledgeGraphId,
                    KnowledgeGraphUrl = e.KnowledgeGraphUrl,
                    Metadata = e.Metadata,
                    Embedding = e.Embedding
                };
            }).ToList();
        }

        /// <summary>
        /// Extracts entities of a custom type.
        /// </summary>
        private List<ExtractedEntity> ExtractCustomType(string text, CustomEntityType customType)
        {
            var entities = new List<ExtractedEntity>();

            foreach (var pattern in customType.Patterns)
            {
                try
                {
                    var regex = new Regex(pattern, RegexOptions.IgnoreCase);
                    var matches = regex.Matches(text);

                    foreach (Match match in matches)
                    {
                        entities.Add(new ExtractedEntity
                        {
                            Text = match.Value,
                            NormalizedText = customType.EnableNormalization ? match.Value.Trim() : match.Value,
                            Type = EntityType.Custom,
                            SubType = customType.Id,
                            Confidence = 0.85f,
                            StartPosition = match.Index,
                            Length = match.Length,
                            Metadata = new Dictionary<string, object>
                            {
                                ["customTypeName"] = customType.Name
                            }
                        });
                    }
                }
                catch { /* Invalid regex */ }
            }

            return entities;
        }

        /// <summary>
        /// Registers a custom entity type.
        /// </summary>
        public void RegisterCustomType(CustomEntityType customType)
        {
            _customTypes[customType.Id] = customType;
        }

        /// <summary>
        /// Finds co-occurring entities.
        /// </summary>
        public List<EntityCoOccurrence> FindCoOccurrences(
            List<ExtractedEntity> entities,
            int maxDistance = 200)
        {
            var coOccurrences = new List<EntityCoOccurrence>();

            for (int i = 0; i < entities.Count; i++)
            {
                for (int j = i + 1; j < entities.Count; j++)
                {
                    var e1 = entities[i];
                    var e2 = entities[j];

                    var distance = Math.Abs(e1.StartPosition - e2.StartPosition);
                    if (distance <= maxDistance)
                    {
                        coOccurrences.Add(new EntityCoOccurrence
                        {
                            Entity1 = e1,
                            Entity2 = e2,
                            Count = 1,
                            AverageProximity = distance,
                            RelationshipStrength = 1.0f - (float)distance / maxDistance
                        });
                    }
                }
            }

            return coOccurrences;
        }
    }

    /// <summary>
    /// Common regex patterns for entity extraction.
    /// </summary>
    internal sealed class EntityPatterns
    {
        public Regex EmailPattern { get; } = new(
            @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,}\b",
            RegexOptions.Compiled);

        public Regex UrlPattern { get; } = new(
            @"https?://[^\s<>""{}|\\^`\[\]]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public Regex PhonePattern { get; } = new(
            @"(?:\+?1[-.\s]?)?\(?[0-9]{3}\)?[-.\s]?[0-9]{3}[-.\s]?[0-9]{4}",
            RegexOptions.Compiled);

        public Regex IPAddressPattern { get; } = new(
            @"\b(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\b",
            RegexOptions.Compiled);

        public Regex DatePattern { get; } = new(
            @"\b(?:\d{1,2}[-/]\d{1,2}[-/]\d{2,4}|\d{4}[-/]\d{1,2}[-/]\d{1,2}|(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\s+\d{1,2},?\s+\d{4})\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public Regex MoneyPattern { get; } = new(
            @"[$\u20AC\u00A3\u00A5]\s*\d+(?:[.,]\d+)?(?:\s*(?:million|billion|k|m|b))?\b|\b\d+(?:[.,]\d+)?\s*(?:USD|EUR|GBP|JPY|dollars?|euros?|pounds?)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public Regex PercentagePattern { get; } = new(
            @"\b\d+(?:\.\d+)?%\b",
            RegexOptions.Compiled);

        public Regex FilePathPattern { get; } = new(
            @"(?:[A-Za-z]:\\(?:[^\\\/:*?""<>|\r\n]+\\)*[^\\\/:*?""<>|\r\n]*|/(?:[^\0/]+/)*[^\0/]*)",
            RegexOptions.Compiled);
    }

    /// <summary>
    /// Options for entity extraction.
    /// </summary>
    public sealed class EntityExtractionOptions
    {
        /// <summary>Use AI for enhanced extraction.</summary>
        public bool UseAI { get; init; } = true;

        /// <summary>Entity types to extract (empty = all).</summary>
        public List<EntityType> EnabledTypes { get; init; } = new();

        /// <summary>Include surrounding context.</summary>
        public bool IncludeContext { get; init; } = true;

        /// <summary>Context window size in characters.</summary>
        public int ContextWindowSize { get; init; } = 50;

        /// <summary>Minimum confidence threshold.</summary>
        public float MinConfidence { get; init; } = 0.5f;

        /// <summary>Enable entity linking to knowledge graphs.</summary>
        public bool EnableLinking { get; init; } = false;
    }
}
