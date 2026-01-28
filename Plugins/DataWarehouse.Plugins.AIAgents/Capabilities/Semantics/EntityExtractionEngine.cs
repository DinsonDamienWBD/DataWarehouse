// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.SDK.AI;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using IExtendedAIProvider = DataWarehouse.Plugins.AIAgents.IExtendedAIProvider;

namespace DataWarehouse.Plugins.AIAgents.Capabilities.Semantics;

/// <summary>
/// Engine for named entity recognition (NER) using AI and pattern-based methods.
/// Supports extraction of persons, organizations, locations, dates, monetary values,
/// and custom entity types with configurable patterns.
/// </summary>
public sealed class EntityExtractionEngine
{
    private IExtendedAIProvider? _aiProvider;
    private readonly ConcurrentDictionary<string, CustomEntityType> _customTypes;
    private readonly Dictionary<EntityType, List<Regex>> _builtInPatterns;

    /// <summary>
    /// Initializes a new instance of the <see cref="EntityExtractionEngine"/> class.
    /// </summary>
    /// <param name="aiProvider">Optional AI provider for enhanced entity extraction.</param>
    public EntityExtractionEngine(IExtendedAIProvider? aiProvider = null)
    {
        _aiProvider = aiProvider;
        _customTypes = new ConcurrentDictionary<string, CustomEntityType>();
        _builtInPatterns = InitializeBuiltInPatterns();
    }

    /// <summary>
    /// Sets or updates the AI provider.
    /// </summary>
    /// <param name="provider">The AI provider to use.</param>
    public void SetProvider(IExtendedAIProvider? provider)
    {
        _aiProvider = provider;
    }

    /// <summary>
    /// Gets whether an AI provider is available.
    /// </summary>
    public bool IsAIAvailable => _aiProvider?.IsAvailable ?? false;

    /// <summary>
    /// Extracts named entities from text.
    /// </summary>
    /// <param name="text">Text to analyze.</param>
    /// <param name="options">Extraction options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Entity extraction result.</returns>
    public async Task<EntityExtractionResult> ExtractAsync(
        string text,
        EntityExtractionOptions? options = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        options ??= new EntityExtractionOptions();

        var entities = new List<ExtractedEntity>();

        try
        {
            // Try AI extraction first if available and preferred
            if (options.PreferAI && IsAIAvailable)
            {
                var aiEntities = await ExtractWithAIAsync(text, options, ct);
                entities.AddRange(aiEntities);
            }
            else
            {
                // Use pattern-based extraction
                entities.AddRange(ExtractWithPatterns(text, options));
            }

            // Always supplement with pattern extraction for high-confidence patterns
            if (options.PreferAI && entities.Count > 0)
            {
                var patternEntities = ExtractWithPatterns(text, options);

                // Merge pattern entities that don't overlap with AI entities
                foreach (var patternEntity in patternEntities)
                {
                    var overlaps = entities.Any(e =>
                        (patternEntity.StartPosition >= e.StartPosition &&
                         patternEntity.StartPosition < e.StartPosition + e.Length) ||
                        (e.StartPosition >= patternEntity.StartPosition &&
                         e.StartPosition < patternEntity.StartPosition + patternEntity.Length));

                    if (!overlaps)
                    {
                        entities.Add(patternEntity);
                    }
                }
            }

            // Apply confidence filtering
            entities = entities
                .Where(e => e.Confidence >= options.MinConfidence)
                .OrderBy(e => e.StartPosition)
                .ToList();

            // Add context if requested
            if (options.IncludeContext)
            {
                entities = entities
                    .Select(e => e with { Context = GetContext(text, e.StartPosition, e.Length, options.ContextWindowSize) })
                    .ToList();
            }

            sw.Stop();

            return new EntityExtractionResult
            {
                Entities = entities,
                Duration = sw.Elapsed,
                UsedAI = options.PreferAI && IsAIAvailable
            };
        }
        catch (Exception)
        {
            sw.Stop();
            return new EntityExtractionResult
            {
                Entities = ExtractWithPatterns(text, options),
                Duration = sw.Elapsed,
                UsedAI = false
            };
        }
    }

    /// <summary>
    /// Registers a custom entity type.
    /// </summary>
    /// <param name="entityType">Custom entity type definition.</param>
    public void RegisterCustomType(CustomEntityType entityType)
    {
        _customTypes[entityType.Id] = entityType;
    }

    /// <summary>
    /// Unregisters a custom entity type.
    /// </summary>
    /// <param name="entityTypeId">ID of the entity type to remove.</param>
    public void UnregisterCustomType(string entityTypeId)
    {
        _customTypes.TryRemove(entityTypeId, out _);
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

        var entityTypes = options.EnabledTypes.Count > 0
            ? string.Join(", ", options.EnabledTypes)
            : "Person, Organization, Location, Date, Time, Money, Percentage, PhoneNumber, Email, Url";

        var prompt = $@"Extract named entities from the following text.
Entity types to extract: {entityTypes}

Text:
{TruncateText(text, 3000)}

For each entity found, respond in this exact format (one per line):
ENTITY: [exact text] | TYPE: [entity type] | CONFIDENCE: [0.0-1.0] | POSITION: [start char position]

Only include entities you are confident about. Do not include partial matches or uncertain extractions.";

        var response = await _aiProvider.CompleteAsync(new AIRequest
        {
            Prompt = prompt,
            SystemMessage = "You are a named entity recognition system. Extract entities precisely as they appear in the text.",
            MaxTokens = 1000,
            Temperature = 0.1f
        }, ct);

        if (!response.Success)
            return new List<ExtractedEntity>();

        return ParseAIEntities(response.Content, text);
    }

    /// <summary>
    /// Extracts entities using pattern matching.
    /// </summary>
    private List<ExtractedEntity> ExtractWithPatterns(string text, EntityExtractionOptions options)
    {
        var entities = new List<ExtractedEntity>();

        // Extract with built-in patterns
        foreach (var (entityType, patterns) in _builtInPatterns)
        {
            if (options.EnabledTypes.Count > 0 && !options.EnabledTypes.Contains(entityType))
                continue;

            foreach (var pattern in patterns)
            {
                try
                {
                    var matches = pattern.Matches(text);
                    foreach (Match match in matches)
                    {
                        entities.Add(new ExtractedEntity
                        {
                            Text = match.Value,
                            Type = entityType,
                            Confidence = GetPatternConfidence(entityType, match.Value),
                            StartPosition = match.Index,
                            Length = match.Length
                        });
                    }
                }
                catch { /* Invalid regex or timeout */ }
            }
        }

        // Extract with custom patterns
        foreach (var customType in _customTypes.Values)
        {
            foreach (var patternStr in customType.Patterns)
            {
                try
                {
                    var pattern = new Regex(patternStr, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
                    var matches = pattern.Matches(text);
                    foreach (Match match in matches)
                    {
                        entities.Add(new ExtractedEntity
                        {
                            Text = match.Value,
                            Type = customType.ParentType ?? EntityType.Custom,
                            SubType = customType.Name,
                            Confidence = 0.85f,
                            StartPosition = match.Index,
                            Length = match.Length
                        });
                    }
                }
                catch { /* Invalid regex or timeout */ }
            }
        }

        // Remove duplicates and overlaps (keep highest confidence)
        return RemoveOverlappingEntities(entities);
    }

    private Dictionary<EntityType, List<Regex>> InitializeBuiltInPatterns()
    {
        return new Dictionary<EntityType, List<Regex>>
        {
            [EntityType.Email] = new()
            {
                CreateRegex(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}")
            },
            [EntityType.Url] = new()
            {
                CreateRegex(@"https?://[^\s<>\""']+"),
                CreateRegex(@"www\.[^\s<>\""']+")
            },
            [EntityType.PhoneNumber] = new()
            {
                CreateRegex(@"\+?1?[-.\s]?\(?[0-9]{3}\)?[-.\s]?[0-9]{3}[-.\s]?[0-9]{4}"),
                CreateRegex(@"\+[0-9]{1,3}[-.\s]?[0-9]{6,14}")
            },
            [EntityType.Date] = new()
            {
                CreateRegex(@"\b\d{1,2}[/-]\d{1,2}[/-]\d{2,4}\b"),
                CreateRegex(@"\b\d{4}[/-]\d{1,2}[/-]\d{1,2}\b"),
                CreateRegex(@"\b(?:January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{1,2}(?:st|nd|rd|th)?,?\s+\d{4}\b", RegexOptions.IgnoreCase),
                CreateRegex(@"\b\d{1,2}\s+(?:January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{4}\b", RegexOptions.IgnoreCase)
            },
            [EntityType.Time] = new()
            {
                CreateRegex(@"\b\d{1,2}:\d{2}(?::\d{2})?\s*(?:AM|PM|am|pm)?\b"),
                CreateRegex(@"\b\d{1,2}\s*(?:AM|PM|am|pm)\b")
            },
            [EntityType.Money] = new()
            {
                CreateRegex(@"\$[\d,]+(?:\.\d{2})?"),
                CreateRegex(@"[\d,]+(?:\.\d{2})?\s*(?:USD|EUR|GBP|JPY|CAD|AUD|CHF|CNY)"),
                CreateRegex(@"(?:USD|EUR|GBP)\s*[\d,]+(?:\.\d{2})?"),
                CreateRegex(@"[\u20AC\u00A3\u00A5][\d,]+(?:\.\d{2})?") // Euro, Pound, Yen symbols
            },
            [EntityType.Percentage] = new()
            {
                CreateRegex(@"\d+(?:\.\d+)?%"),
                CreateRegex(@"\d+(?:\.\d+)?\s*percent", RegexOptions.IgnoreCase)
            },
            [EntityType.IpAddress] = new()
            {
                CreateRegex(@"\b(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\b"),
                CreateRegex(@"\b(?:[0-9a-fA-F]{1,4}:){7}[0-9a-fA-F]{1,4}\b") // IPv6
            },
            [EntityType.FilePath] = new()
            {
                CreateRegex(@"[A-Z]:\\(?:[^\\/:*?""<>|\r\n]+\\)*[^\\/:*?""<>|\r\n]*"), // Windows
                CreateRegex(@"/(?:[^/\0]+/?)+") // Unix-like
            },
            [EntityType.AccountNumber] = new()
            {
                CreateRegex(@"\b\d{8,17}\b") // Generic account numbers
            },
            [EntityType.Version] = new()
            {
                CreateRegex(@"\bv?\d+\.\d+(?:\.\d+)?(?:-[a-zA-Z0-9]+)?\b")
            }
        };
    }

    private Regex CreateRegex(string pattern, RegexOptions additionalOptions = RegexOptions.None)
    {
        return new Regex(pattern, RegexOptions.Compiled | additionalOptions, TimeSpan.FromSeconds(1));
    }

    private List<ExtractedEntity> ParseAIEntities(string response, string originalText)
    {
        var entities = new List<ExtractedEntity>();
        var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            if (!line.Contains("ENTITY:"))
                continue;

            try
            {
                var parts = line.Split('|');
                if (parts.Length < 2)
                    continue;

                var entityText = parts[0].Replace("ENTITY:", "").Trim();
                var typeStr = parts.Length > 1 ? parts[1].Replace("TYPE:", "").Trim() : "Unknown";
                var confidenceStr = parts.Length > 2 ? parts[2].Replace("CONFIDENCE:", "").Trim() : "0.8";

                if (!Enum.TryParse<EntityType>(typeStr, true, out var entityType))
                    entityType = EntityType.Unknown;

                if (!float.TryParse(confidenceStr, out var confidence))
                    confidence = 0.8f;

                // Find position in original text
                var position = originalText.IndexOf(entityText, StringComparison.Ordinal);
                if (position < 0)
                    position = originalText.IndexOf(entityText, StringComparison.OrdinalIgnoreCase);

                if (position >= 0)
                {
                    entities.Add(new ExtractedEntity
                    {
                        Text = entityText,
                        Type = entityType,
                        Confidence = Math.Clamp(confidence, 0, 1),
                        StartPosition = position,
                        Length = entityText.Length
                    });
                }
            }
            catch { /* Skip malformed lines */ }
        }

        return entities;
    }

    private float GetPatternConfidence(EntityType type, string value)
    {
        // Higher confidence for well-structured patterns
        return type switch
        {
            EntityType.Email => 0.95f,
            EntityType.Url => 0.95f,
            EntityType.PhoneNumber => ValidatePhoneNumber(value) ? 0.90f : 0.70f,
            EntityType.Date => 0.85f,
            EntityType.Time => 0.85f,
            EntityType.Money => 0.90f,
            EntityType.Percentage => 0.95f,
            EntityType.IpAddress => 0.95f,
            EntityType.FilePath => 0.80f,
            EntityType.Version => 0.85f,
            _ => 0.75f
        };
    }

    private bool ValidatePhoneNumber(string phone)
    {
        // Remove all non-digit characters
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        return digits.Length >= 10 && digits.Length <= 15;
    }

    private List<ExtractedEntity> RemoveOverlappingEntities(List<ExtractedEntity> entities)
    {
        var sorted = entities.OrderBy(e => e.StartPosition).ThenByDescending(e => e.Confidence).ToList();
        var result = new List<ExtractedEntity>();

        foreach (var entity in sorted)
        {
            var overlaps = result.Any(e =>
                (entity.StartPosition >= e.StartPosition && entity.StartPosition < e.StartPosition + e.Length) ||
                (e.StartPosition >= entity.StartPosition && e.StartPosition < entity.StartPosition + entity.Length));

            if (!overlaps)
            {
                result.Add(entity);
            }
        }

        return result;
    }

    private string GetContext(string text, int position, int length, int windowSize)
    {
        var start = Math.Max(0, position - windowSize);
        var end = Math.Min(text.Length, position + length + windowSize);
        return text[start..end];
    }

    private static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text ?? string.Empty;
        return text[..maxLength];
    }
}

/// <summary>
/// Options for entity extraction.
/// </summary>
public sealed record EntityExtractionOptions
{
    /// <summary>Prefer AI over pattern-based methods.</summary>
    public bool PreferAI { get; init; } = true;

    /// <summary>Minimum confidence threshold.</summary>
    public float MinConfidence { get; init; } = 0.5f;

    /// <summary>Entity types to extract (empty = all).</summary>
    public HashSet<EntityType> EnabledTypes { get; init; } = new();

    /// <summary>Include surrounding context.</summary>
    public bool IncludeContext { get; init; } = true;

    /// <summary>Context window size (characters).</summary>
    public int ContextWindowSize { get; init; } = 50;

    /// <summary>Enable entity linking to knowledge graphs.</summary>
    public bool EnableLinking { get; init; } = false;
}

/// <summary>
/// Result of entity extraction.
/// </summary>
public sealed class EntityExtractionResult
{
    /// <summary>Extracted entities.</summary>
    public List<ExtractedEntity> Entities { get; init; } = new();

    /// <summary>Total entity count.</summary>
    public int TotalCount => Entities.Count;

    /// <summary>Count by entity type.</summary>
    public Dictionary<EntityType, int> CountByType =>
        Entities.GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>Processing duration.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Whether AI was used.</summary>
    public bool UsedAI { get; init; }
}

/// <summary>
/// An extracted entity.
/// </summary>
public sealed record ExtractedEntity
{
    /// <summary>Entity text as found.</summary>
    public required string Text { get; init; }

    /// <summary>Normalized form of the entity.</summary>
    public string? NormalizedText { get; init; }

    /// <summary>Entity type.</summary>
    public EntityType Type { get; init; }

    /// <summary>Sub-type for custom entities.</summary>
    public string? SubType { get; init; }

    /// <summary>Confidence score (0-1).</summary>
    public float Confidence { get; init; }

    /// <summary>Start position in source.</summary>
    public int StartPosition { get; init; }

    /// <summary>Length in source.</summary>
    public int Length { get; init; }

    /// <summary>Surrounding context.</summary>
    public string? Context { get; init; }

    /// <summary>Knowledge graph ID if linked.</summary>
    public string? KnowledgeGraphId { get; init; }

    /// <summary>Additional metadata.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Entity types for NER.
/// </summary>
public enum EntityType
{
    /// <summary>Unknown type.</summary>
    Unknown = 0,

    // People and organizations
    /// <summary>Person name.</summary>
    Person,
    /// <summary>Organization/company.</summary>
    Organization,
    /// <summary>Product/brand.</summary>
    Product,

    // Locations
    /// <summary>Geographic location.</summary>
    Location,
    /// <summary>Country.</summary>
    Country,
    /// <summary>City.</summary>
    City,
    /// <summary>Address.</summary>
    Address,

    // Time
    /// <summary>Date reference.</summary>
    Date,
    /// <summary>Time reference.</summary>
    Time,
    /// <summary>Duration.</summary>
    Duration,

    // Numeric
    /// <summary>Monetary value.</summary>
    Money,
    /// <summary>Percentage.</summary>
    Percentage,
    /// <summary>Quantity.</summary>
    Quantity,
    /// <summary>Phone number.</summary>
    PhoneNumber,

    // Digital
    /// <summary>Email address.</summary>
    Email,
    /// <summary>URL.</summary>
    Url,
    /// <summary>IP address.</summary>
    IpAddress,
    /// <summary>File path.</summary>
    FilePath,

    // Documents
    /// <summary>Document ID.</summary>
    DocumentId,
    /// <summary>Invoice number.</summary>
    InvoiceNumber,
    /// <summary>Account number.</summary>
    AccountNumber,

    // Technical
    /// <summary>Code identifier.</summary>
    CodeIdentifier,
    /// <summary>Technical term.</summary>
    TechnicalTerm,
    /// <summary>Version number.</summary>
    Version,

    // Events and concepts
    /// <summary>Event name.</summary>
    Event,
    /// <summary>Concept/topic.</summary>
    Concept,

    // Custom
    /// <summary>Custom type.</summary>
    Custom
}

/// <summary>
/// Custom entity type definition.
/// </summary>
public sealed class CustomEntityType
{
    /// <summary>Unique identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable name.</summary>
    public required string Name { get; init; }

    /// <summary>Description.</summary>
    public string? Description { get; init; }

    /// <summary>Regex patterns.</summary>
    public List<string> Patterns { get; init; } = new();

    /// <summary>Keywords for detection.</summary>
    public List<string> Keywords { get; init; } = new();

    /// <summary>Example entities.</summary>
    public List<string> Examples { get; init; } = new();

    /// <summary>Parent entity type.</summary>
    public EntityType? ParentType { get; init; }

    /// <summary>Enable normalization.</summary>
    public bool EnableNormalization { get; init; } = true;
}
