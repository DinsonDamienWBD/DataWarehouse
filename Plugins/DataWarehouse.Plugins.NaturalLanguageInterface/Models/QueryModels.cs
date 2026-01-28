// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.NaturalLanguageInterface.Models;

/// <summary>
/// Intent extracted from natural language query.
/// </summary>
public sealed class QueryIntent
{
    /// <summary>Primary intent type.</summary>
    public IntentType Type { get; init; }

    /// <summary>Sub-intent for more specific classification.</summary>
    public string? SubType { get; init; }

    /// <summary>Confidence score (0.0 - 1.0).</summary>
    public double Confidence { get; init; }

    /// <summary>Extracted entities.</summary>
    public List<ExtractedEntity> Entities { get; init; } = new();

    /// <summary>Extracted parameters for the intent.</summary>
    public Dictionary<string, object> Parameters { get; init; } = new();

    /// <summary>Original query text.</summary>
    public string OriginalQuery { get; init; } = string.Empty;

    /// <summary>Normalized/cleaned query.</summary>
    public string NormalizedQuery { get; init; } = string.Empty;

    /// <summary>Detected language.</summary>
    public string Language { get; init; } = "en";

    /// <summary>Keywords extracted from query.</summary>
    public List<string> Keywords { get; init; } = new();

    /// <summary>Suggested DataWarehouse command to execute.</summary>
    public string? SuggestedCommand { get; init; }

    /// <summary>Whether this is a follow-up to previous query.</summary>
    public bool IsFollowUp { get; init; }
}

/// <summary>
/// Primary intent types for DataWarehouse queries.
/// </summary>
public enum IntentType
{
    // Search intents
    Search,
    FindFiles,
    FindByMetadata,
    FindByContent,
    FindByDate,
    FindBySize,
    FindByType,

    // Information intents
    GetInfo,
    GetStatus,
    GetStats,
    GetUsage,
    GetHistory,
    GetQuota,

    // Action intents
    Backup,
    Restore,
    Delete,
    Archive,
    Tier,
    Share,

    // Explanation intents
    Explain,
    WhyQuestion,
    HowQuestion,
    Compare,

    // Storytelling intents
    Summarize,
    Report,
    Trend,
    Forecast,

    // System intents
    Help,
    Configure,
    Monitor,

    // Conversational
    Greeting,
    Goodbye,
    Thanks,
    Clarification,
    Confirmation,
    Cancellation,

    // Unknown
    Unknown
}

/// <summary>
/// Entity extracted from natural language.
/// </summary>
public sealed class ExtractedEntity
{
    /// <summary>Entity type.</summary>
    public EntityType Type { get; init; }

    /// <summary>Entity value.</summary>
    public string Value { get; init; } = string.Empty;

    /// <summary>Normalized value.</summary>
    public string NormalizedValue { get; init; } = string.Empty;

    /// <summary>Confidence score.</summary>
    public double Confidence { get; init; }

    /// <summary>Start position in original text.</summary>
    public int StartIndex { get; init; }

    /// <summary>End position in original text.</summary>
    public int EndIndex { get; init; }

    /// <summary>Additional metadata about the entity.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Entity types that can be extracted from queries.
/// </summary>
public enum EntityType
{
    // Time entities
    DateTime,
    DateRange,
    RelativeTime,
    Duration,

    // File entities
    FileName,
    FilePath,
    FileExtension,
    FileType,
    MimeType,

    // Size entities
    FileSize,
    SizeComparison,

    // Location entities
    StoragePool,
    Bucket,
    Container,
    Region,

    // Metadata entities
    Tag,
    Label,
    Category,
    Owner,
    Creator,

    // Quantity entities
    Number,
    Percentage,
    Count,

    // Text entities
    Keyword,
    Phrase,
    Pattern,
    Regex,

    // System entities
    PluginName,
    PolicyName,
    TierName,

    // Other
    Unknown
}

/// <summary>
/// Query translation result to DataWarehouse filter.
/// </summary>
public sealed class QueryTranslation
{
    /// <summary>Whether translation was successful.</summary>
    public bool Success { get; init; }

    /// <summary>DataWarehouse command to execute.</summary>
    public string Command { get; init; } = string.Empty;

    /// <summary>Parameters for the command.</summary>
    public Dictionary<string, object?> Parameters { get; init; } = new();

    /// <summary>SQL-like filter expression.</summary>
    public string? FilterExpression { get; init; }

    /// <summary>Human-readable explanation of what the query will do.</summary>
    public string Explanation { get; init; } = string.Empty;

    /// <summary>Confidence score.</summary>
    public double Confidence { get; init; }

    /// <summary>Warnings or suggestions.</summary>
    public List<string> Warnings { get; init; } = new();

    /// <summary>Error message if translation failed.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Natural language search request.
/// </summary>
public sealed class NLSearchRequest
{
    /// <summary>Natural language query.</summary>
    public string Query { get; init; } = string.Empty;

    /// <summary>User ID making the request.</summary>
    public string? UserId { get; init; }

    /// <summary>Conversation ID for context.</summary>
    public string? ConversationId { get; init; }

    /// <summary>Maximum results to return.</summary>
    public int MaxResults { get; init; } = 50;

    /// <summary>Language hint.</summary>
    public string? LanguageHint { get; init; }

    /// <summary>Platform source.</summary>
    public string Platform { get; init; } = "api";

    /// <summary>Include explanation in response.</summary>
    public bool IncludeExplanation { get; init; } = true;

    /// <summary>Additional context.</summary>
    public Dictionary<string, object> Context { get; init; } = new();
}

/// <summary>
/// Natural language search response.
/// </summary>
public sealed class NLSearchResponse
{
    /// <summary>Whether the search was successful.</summary>
    public bool Success { get; init; }

    /// <summary>Human-readable response.</summary>
    public string Response { get; init; } = string.Empty;

    /// <summary>Extracted intent.</summary>
    public QueryIntent? Intent { get; init; }

    /// <summary>Query translation used.</summary>
    public QueryTranslation? Translation { get; init; }

    /// <summary>Search results.</summary>
    public List<NLSearchResult> Results { get; init; } = new();

    /// <summary>Total matching count.</summary>
    public int TotalCount { get; init; }

    /// <summary>Clarification needed.</summary>
    public ClarificationRequest? Clarification { get; init; }

    /// <summary>Suggested follow-up queries.</summary>
    public List<string> SuggestedFollowUps { get; init; } = new();

    /// <summary>Execution time in milliseconds.</summary>
    public long ExecutionTimeMs { get; init; }

    /// <summary>Error message if failed.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Individual search result.
/// </summary>
public sealed class NLSearchResult
{
    /// <summary>Item ID.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Item name/title.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Item path.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Item type.</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Item size.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Last modified.</summary>
    public DateTime? ModifiedAt { get; init; }

    /// <summary>Relevance score.</summary>
    public double Score { get; init; }

    /// <summary>Why this item matched (snippet).</summary>
    public string? MatchReason { get; init; }

    /// <summary>Highlighted content snippet.</summary>
    public string? Snippet { get; init; }

    /// <summary>Additional metadata.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Language detection result.
/// </summary>
public sealed class LanguageDetectionResult
{
    /// <summary>Detected language code (ISO 639-1).</summary>
    public string LanguageCode { get; init; } = "en";

    /// <summary>Language name.</summary>
    public string LanguageName { get; init; } = "English";

    /// <summary>Confidence score.</summary>
    public double Confidence { get; init; }

    /// <summary>Alternative detections.</summary>
    public List<(string Code, double Confidence)> Alternatives { get; init; } = new();
}
