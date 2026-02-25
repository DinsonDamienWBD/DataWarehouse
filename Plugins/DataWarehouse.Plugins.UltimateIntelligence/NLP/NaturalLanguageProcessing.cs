using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIntelligence.NLP;

// ==================================================================================
// T90.M: NATURAL LANGUAGE PROCESSING
// NLP components for query understanding, semantic search, and knowledge graphs.
// ==================================================================================

#region NLP Topics

/// <summary>
/// Message bus topics for NLP operations.
/// NLP components use these topics to request AI capabilities from the intelligence system.
/// </summary>
public static class NLPTopics
{
    // === M1: Query Understanding ===

    /// <summary>Parse a natural language query.</summary>
    public const string ParseQuery = "intelligence.nlp.parse-query";
    public const string ParseQueryResponse = "intelligence.nlp.parse-query.response";

    /// <summary>Detect user intent from query.</summary>
    public const string DetectIntent = "intelligence.nlp.detect-intent";
    public const string DetectIntentResponse = "intelligence.nlp.detect-intent.response";

    /// <summary>Extract entities from query.</summary>
    public const string ExtractEntities = "intelligence.nlp.extract-entities";
    public const string ExtractEntitiesResponse = "intelligence.nlp.extract-entities.response";

    /// <summary>Generate natural language response.</summary>
    public const string GenerateResponse = "intelligence.nlp.generate-response";
    public const string GenerateResponseResponse = "intelligence.nlp.generate-response.response";

    // === M2: Semantic Search ===

    /// <summary>Index content for semantic search.</summary>
    public const string SemanticIndex = "intelligence.nlp.semantic-index";
    public const string SemanticIndexResponse = "intelligence.nlp.semantic-index.response";

    /// <summary>Perform semantic search.</summary>
    public const string SemanticSearch = "intelligence.nlp.semantic-search";
    public const string SemanticSearchResponse = "intelligence.nlp.semantic-search.response";

    /// <summary>Get embeddings for text.</summary>
    public const string GetEmbeddings = "intelligence.nlp.get-embeddings";
    public const string GetEmbeddingsResponse = "intelligence.nlp.get-embeddings.response";

    // === M3: Knowledge Graph ===

    /// <summary>Query the knowledge graph.</summary>
    public const string GraphQuery = "intelligence.nlp.graph-query";
    public const string GraphQueryResponse = "intelligence.nlp.graph-query.response";

    /// <summary>Discover relationships in knowledge.</summary>
    public const string DiscoverRelationships = "intelligence.nlp.discover-relationships";
    public const string DiscoverRelationshipsResponse = "intelligence.nlp.discover-relationships.response";

    /// <summary>Add knowledge to graph.</summary>
    public const string AddKnowledge = "intelligence.nlp.add-knowledge";
    public const string AddKnowledgeResponse = "intelligence.nlp.add-knowledge.response";
}

#endregion

#region M1: Query Understanding

/// <summary>
/// User intent types for query understanding.
/// </summary>
public enum UserIntent
{
    /// <summary>User wants to query/retrieve information.</summary>
    Query,

    /// <summary>User wants to execute a command/action.</summary>
    Command,

    /// <summary>User is asking for clarification.</summary>
    Clarification,

    /// <summary>User is providing additional context.</summary>
    Context,

    /// <summary>User is confirming or denying something.</summary>
    Confirmation,

    /// <summary>User wants to cancel the current operation.</summary>
    Cancel,

    /// <summary>User is greeting or small talk.</summary>
    Greeting,

    /// <summary>User intent could not be determined.</summary>
    Unknown
}

/// <summary>
/// Parsed query result from the QueryParser.
/// </summary>
public sealed class ParsedQuery
{
    /// <summary>Gets or sets the original query text.</summary>
    public required string OriginalQuery { get; init; }

    /// <summary>Gets or sets the normalized query text.</summary>
    public string NormalizedQuery { get; init; } = string.Empty;

    /// <summary>Gets or sets extracted keywords.</summary>
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();

    /// <summary>Gets or sets identified phrases.</summary>
    public IReadOnlyList<QueryPhrase> Phrases { get; init; } = Array.Empty<QueryPhrase>();

    /// <summary>Gets or sets extracted entities.</summary>
    public IReadOnlyList<ExtractedEntity> Entities { get; init; } = Array.Empty<ExtractedEntity>();

    /// <summary>Gets or sets the detected intent.</summary>
    public UserIntent Intent { get; init; } = UserIntent.Unknown;

    /// <summary>Gets or sets intent confidence score (0-1).</summary>
    public double IntentConfidence { get; init; }

    /// <summary>Gets or sets optional query parameters.</summary>
    public Dictionary<string, object> Parameters { get; init; } = new();

    /// <summary>Gets or sets parse timestamp.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Gets or sets parse duration.</summary>
    public TimeSpan ParseDuration { get; init; }
}

/// <summary>
/// A phrase extracted from a query.
/// </summary>
public sealed class QueryPhrase
{
    /// <summary>Gets or sets the phrase text.</summary>
    public required string Text { get; init; }

    /// <summary>Gets or sets the phrase type.</summary>
    public string Type { get; init; } = "general";

    /// <summary>Gets or sets the start position in original query.</summary>
    public int StartPosition { get; init; }

    /// <summary>Gets or sets the end position in original query.</summary>
    public int EndPosition { get; init; }

    /// <summary>Gets or sets confidence score (0-1).</summary>
    public double Confidence { get; init; } = 1.0;
}

/// <summary>
/// An entity extracted from a query.
/// </summary>
public sealed class ExtractedEntity
{
    /// <summary>Gets or sets the entity value.</summary>
    public required string Value { get; init; }

    /// <summary>Gets or sets the entity type.</summary>
    public required EntityType Type { get; init; }

    /// <summary>Gets or sets the start position in original query.</summary>
    public int StartPosition { get; init; }

    /// <summary>Gets or sets the end position in original query.</summary>
    public int EndPosition { get; init; }

    /// <summary>Gets or sets confidence score (0-1).</summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>Gets or sets optional normalized value.</summary>
    public object? NormalizedValue { get; init; }

    /// <summary>Gets or sets additional metadata.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Entity types for extraction.
/// </summary>
public enum EntityType
{
    /// <summary>File name or path.</summary>
    FileName,

    /// <summary>Date or time reference.</summary>
    DateTime,

    /// <summary>Numeric size value (bytes, KB, MB, etc.).</summary>
    Size,

    /// <summary>Duration or time span.</summary>
    Duration,

    /// <summary>Numeric value.</summary>
    Number,

    /// <summary>Person name.</summary>
    Person,

    /// <summary>Organization name.</summary>
    Organization,

    /// <summary>Location or address.</summary>
    Location,

    /// <summary>Email address.</summary>
    Email,

    /// <summary>URL or URI.</summary>
    Url,

    /// <summary>Table or collection name.</summary>
    TableName,

    /// <summary>Column or field name.</summary>
    ColumnName,

    /// <summary>Database or schema name.</summary>
    Database,

    /// <summary>Tag or label.</summary>
    Tag,

    /// <summary>Custom or unknown entity type.</summary>
    Custom
}

/// <summary>
/// Parses natural language queries into structured representations.
/// </summary>
/// <remarks>
/// <para>
/// The QueryParser performs:
/// </para>
/// <list type="bullet">
///   <item>Text normalization and tokenization</item>
///   <item>Keyword extraction</item>
///   <item>Phrase detection</item>
///   <item>Entity recognition preparation</item>
/// </list>
/// <para>
/// For full NLP capabilities, the parser uses message bus to request AI-powered
/// processing from the intelligence system.
/// </para>
/// </remarks>
public sealed class QueryParser
{
    private readonly QueryParserOptions _options;
    private readonly Func<string, string, CancellationToken, Task<string?>>? _messageBusPublisher;
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex WordBoundaryRegex = new(@"\b\w+\b", RegexOptions.Compiled);
    private static readonly HashSet<string> DefaultStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "is", "are", "was", "were", "be", "been", "being",
        "have", "has", "had", "do", "does", "did", "will", "would", "could",
        "should", "may", "might", "can", "shall", "must", "need", "dare",
        "to", "of", "in", "for", "on", "with", "at", "by", "from", "as",
        "into", "through", "during", "before", "after", "above", "below",
        "and", "or", "but", "if", "then", "else", "when", "where", "why",
        "how", "all", "each", "every", "both", "few", "more", "most", "other",
        "some", "such", "no", "not", "only", "same", "so", "than", "too",
        "very", "just", "also", "now", "here", "there", "this", "that",
        "what", "which", "who", "whom", "whose", "i", "me", "my", "myself",
        "we", "our", "ours", "ourselves", "you", "your", "yours", "yourself"
    };

    /// <summary>
    /// Creates a new QueryParser with the specified options.
    /// </summary>
    /// <param name="options">Parser configuration options.</param>
    /// <param name="messageBusPublisher">Optional message bus publisher for AI-powered parsing.</param>
    public QueryParser(
        QueryParserOptions? options = null,
        Func<string, string, CancellationToken, Task<string?>>? messageBusPublisher = null)
    {
        _options = options ?? new QueryParserOptions();
        _messageBusPublisher = messageBusPublisher;
    }

    /// <summary>
    /// Parses a natural language query.
    /// </summary>
    /// <param name="query">The query text to parse.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The parsed query result.</returns>
    public async Task<ParsedQuery> ParseAsync(string query, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(query);

        var sw = Stopwatch.StartNew();

        // Normalize the query
        var normalized = NormalizeText(query);

        // Extract keywords
        var keywords = ExtractKeywords(normalized);

        // Extract phrases
        var phrases = ExtractPhrases(normalized, query);

        // Use AI for advanced parsing if available
        IReadOnlyList<ExtractedEntity> entities = Array.Empty<ExtractedEntity>();
        var intent = UserIntent.Unknown;
        double intentConfidence = 0;

        if (_messageBusPublisher != null && _options.EnableAIParsing)
        {
            try
            {
                var aiResult = await RequestAIParsingAsync(query, ct).ConfigureAwait(false);
                if (aiResult != null)
                {
                    entities = aiResult.Entities;
                    intent = aiResult.Intent;
                    intentConfidence = aiResult.IntentConfidence;
                }
            }
            catch
            {
                Debug.WriteLine($"Caught exception in NaturalLanguageProcessing.cs");
                // Fall back to rule-based parsing
                entities = ExtractEntitiesRuleBased(query);
                intent = DetectIntentRuleBased(query);
                intentConfidence = 0.5;
            }
        }
        else
        {
            // Rule-based parsing
            entities = ExtractEntitiesRuleBased(query);
            intent = DetectIntentRuleBased(query);
            intentConfidence = 0.5;
        }

        sw.Stop();

        return new ParsedQuery
        {
            OriginalQuery = query,
            NormalizedQuery = normalized,
            Keywords = keywords,
            Phrases = phrases,
            Entities = entities,
            Intent = intent,
            IntentConfidence = intentConfidence,
            ParseDuration = sw.Elapsed
        };
    }

    private string NormalizeText(string text)
    {
        // Lowercase if configured
        var result = _options.CaseSensitive ? text : text.ToLowerInvariant();

        // Normalize whitespace
        result = WhitespaceRegex.Replace(result, " ").Trim();

        return result;
    }

    private IReadOnlyList<string> ExtractKeywords(string normalizedText)
    {
        var words = WordBoundaryRegex.Matches(normalizedText)
            .Select(m => m.Value)
            .Where(w => w.Length >= _options.MinKeywordLength)
            .Where(w => !_options.FilterStopWords || !DefaultStopWords.Contains(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(_options.MaxKeywords)
            .ToList();

        return words;
    }

    private IReadOnlyList<QueryPhrase> ExtractPhrases(string normalizedText, string originalText)
    {
        var phrases = new List<QueryPhrase>();

        // Extract quoted phrases
        var quotedRegex = new Regex(@"""([^""]+)""|'([^']+)'", RegexOptions.Compiled);
        foreach (Match match in quotedRegex.Matches(originalText))
        {
            var phraseText = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            phrases.Add(new QueryPhrase
            {
                Text = phraseText,
                Type = "quoted",
                StartPosition = match.Index,
                EndPosition = match.Index + match.Length,
                Confidence = 1.0
            });
        }

        return phrases;
    }

    private IReadOnlyList<ExtractedEntity> ExtractEntitiesRuleBased(string query)
    {
        var entities = new List<ExtractedEntity>();

        // File path pattern
        var filePathRegex = new Regex(@"(?:[a-zA-Z]:\\|/)?(?:[\w.-]+[/\\])*[\w.-]+\.\w+", RegexOptions.Compiled);
        foreach (Match match in filePathRegex.Matches(query))
        {
            entities.Add(new ExtractedEntity
            {
                Value = match.Value,
                Type = EntityType.FileName,
                StartPosition = match.Index,
                EndPosition = match.Index + match.Length
            });
        }

        // Size pattern (e.g., 10MB, 5.2 GB, 100KB)
        var sizeRegex = new Regex(@"\b(\d+(?:\.\d+)?)\s*(bytes?|[KMGTPE]B|[KMGTPE]iB)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        foreach (Match match in sizeRegex.Matches(query))
        {
            entities.Add(new ExtractedEntity
            {
                Value = match.Value,
                Type = EntityType.Size,
                StartPosition = match.Index,
                EndPosition = match.Index + match.Length,
                NormalizedValue = ParseSizeToBytes(match.Groups[1].Value, match.Groups[2].Value)
            });
        }

        // Date patterns
        var dateRegex = new Regex(@"\b(\d{4}-\d{2}-\d{2}|\d{1,2}/\d{1,2}/\d{2,4}|today|yesterday|tomorrow|last\s+(?:week|month|year)|next\s+(?:week|month|year))\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        foreach (Match match in dateRegex.Matches(query))
        {
            entities.Add(new ExtractedEntity
            {
                Value = match.Value,
                Type = EntityType.DateTime,
                StartPosition = match.Index,
                EndPosition = match.Index + match.Length,
                NormalizedValue = ParseDateReference(match.Value)
            });
        }

        // Email pattern
        var emailRegex = new Regex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,}\b", RegexOptions.Compiled);
        foreach (Match match in emailRegex.Matches(query))
        {
            entities.Add(new ExtractedEntity
            {
                Value = match.Value,
                Type = EntityType.Email,
                StartPosition = match.Index,
                EndPosition = match.Index + match.Length
            });
        }

        // URL pattern
        var urlRegex = new Regex(@"https?://[^\s]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        foreach (Match match in urlRegex.Matches(query))
        {
            entities.Add(new ExtractedEntity
            {
                Value = match.Value,
                Type = EntityType.Url,
                StartPosition = match.Index,
                EndPosition = match.Index + match.Length
            });
        }

        return entities;
    }

    private UserIntent DetectIntentRuleBased(string query)
    {
        var lowerQuery = query.ToLowerInvariant();

        // Command indicators
        if (lowerQuery.StartsWith("delete") || lowerQuery.StartsWith("remove") ||
            lowerQuery.StartsWith("create") || lowerQuery.StartsWith("add") ||
            lowerQuery.StartsWith("update") || lowerQuery.StartsWith("modify") ||
            lowerQuery.StartsWith("execute") || lowerQuery.StartsWith("run") ||
            lowerQuery.StartsWith("start") || lowerQuery.StartsWith("stop"))
        {
            return UserIntent.Command;
        }

        // Query indicators
        if (lowerQuery.StartsWith("find") || lowerQuery.StartsWith("search") ||
            lowerQuery.StartsWith("show") || lowerQuery.StartsWith("list") ||
            lowerQuery.StartsWith("get") || lowerQuery.StartsWith("what") ||
            lowerQuery.StartsWith("which") || lowerQuery.StartsWith("where") ||
            lowerQuery.StartsWith("how many") || lowerQuery.StartsWith("count"))
        {
            return UserIntent.Query;
        }

        // Clarification indicators
        if (lowerQuery.StartsWith("what do you mean") || lowerQuery.StartsWith("can you explain") ||
            lowerQuery.StartsWith("i don't understand") || lowerQuery.Contains("?") && lowerQuery.Length < 50)
        {
            return UserIntent.Clarification;
        }

        // Confirmation indicators
        if (lowerQuery == "yes" || lowerQuery == "no" || lowerQuery == "ok" ||
            lowerQuery == "correct" || lowerQuery == "right" || lowerQuery == "confirmed")
        {
            return UserIntent.Confirmation;
        }

        // Cancel indicators
        if (lowerQuery == "cancel" || lowerQuery == "abort" || lowerQuery == "stop" ||
            lowerQuery == "nevermind" || lowerQuery == "forget it")
        {
            return UserIntent.Cancel;
        }

        // Greeting indicators
        if (lowerQuery.StartsWith("hello") || lowerQuery.StartsWith("hi") ||
            lowerQuery.StartsWith("hey") || lowerQuery.StartsWith("good morning") ||
            lowerQuery.StartsWith("good afternoon") || lowerQuery.StartsWith("good evening"))
        {
            return UserIntent.Greeting;
        }

        return UserIntent.Unknown;
    }

    private async Task<AIParseResult?> RequestAIParsingAsync(string query, CancellationToken ct)
    {
        if (_messageBusPublisher == null) return null;

        var payload = JsonSerializer.Serialize(new { query });
        var response = await _messageBusPublisher(NLPTopics.ParseQuery, payload, ct).ConfigureAwait(false);

        if (string.IsNullOrEmpty(response)) return null;

        return JsonSerializer.Deserialize<AIParseResult>(response);
    }

    private static long? ParseSizeToBytes(string value, string unit)
    {
        if (!double.TryParse(value, out var numValue)) return null;

        return unit.ToUpperInvariant() switch
        {
            "BYTE" or "BYTES" => (long)numValue,
            "KB" or "KIB" => (long)(numValue * 1024),
            "MB" or "MIB" => (long)(numValue * 1024 * 1024),
            "GB" or "GIB" => (long)(numValue * 1024 * 1024 * 1024),
            "TB" or "TIB" => (long)(numValue * 1024 * 1024 * 1024 * 1024),
            "PB" or "PIB" => (long)(numValue * 1024 * 1024 * 1024 * 1024 * 1024),
            _ => null
        };
    }

    private static DateTimeOffset? ParseDateReference(string reference)
    {
        var lower = reference.ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;

        return lower switch
        {
            "today" => now.Date,
            "yesterday" => now.Date.AddDays(-1),
            "tomorrow" => now.Date.AddDays(1),
            _ when lower.Contains("last week") => now.Date.AddDays(-7),
            _ when lower.Contains("last month") => now.Date.AddMonths(-1),
            _ when lower.Contains("last year") => now.Date.AddYears(-1),
            _ when lower.Contains("next week") => now.Date.AddDays(7),
            _ when lower.Contains("next month") => now.Date.AddMonths(1),
            _ when lower.Contains("next year") => now.Date.AddYears(1),
            _ when DateTimeOffset.TryParse(reference, out var parsed) => parsed,
            _ => null
        };
    }

    private sealed class AIParseResult
    {
        public IReadOnlyList<ExtractedEntity> Entities { get; set; } = Array.Empty<ExtractedEntity>();
        public UserIntent Intent { get; set; } = UserIntent.Unknown;
        public double IntentConfidence { get; set; }
    }
}

/// <summary>
/// Configuration options for QueryParser.
/// </summary>
public sealed class QueryParserOptions
{
    /// <summary>Gets or sets whether parsing is case-sensitive.</summary>
    public bool CaseSensitive { get; set; }

    /// <summary>Gets or sets whether to filter stop words from keywords.</summary>
    public bool FilterStopWords { get; set; } = true;

    /// <summary>Gets or sets the minimum keyword length.</summary>
    public int MinKeywordLength { get; set; } = 2;

    /// <summary>Gets or sets the maximum number of keywords to extract.</summary>
    public int MaxKeywords { get; set; } = 50;

    /// <summary>Gets or sets whether to enable AI-powered parsing.</summary>
    public bool EnableAIParsing { get; set; } = true;
}

/// <summary>
/// Detects user intent from natural language queries.
/// </summary>
/// <remarks>
/// <para>
/// The IntentDetector uses both rule-based and AI-powered methods to determine:
/// </para>
/// <list type="bullet">
///   <item>Primary intent (Query, Command, Clarification)</item>
///   <item>Intent confidence scores</item>
///   <item>Secondary/alternative intents</item>
///   <item>Intent parameters and slots</item>
/// </list>
/// </remarks>
public sealed class IntentDetector
{
    private readonly IntentDetectorOptions _options;
    private readonly Func<string, string, CancellationToken, Task<string?>>? _messageBusPublisher;

    /// <summary>
    /// Creates a new IntentDetector.
    /// </summary>
    /// <param name="options">Detector configuration options.</param>
    /// <param name="messageBusPublisher">Optional message bus publisher for AI-powered detection.</param>
    public IntentDetector(
        IntentDetectorOptions? options = null,
        Func<string, string, CancellationToken, Task<string?>>? messageBusPublisher = null)
    {
        _options = options ?? new IntentDetectorOptions();
        _messageBusPublisher = messageBusPublisher;
    }

    /// <summary>
    /// Detects the intent of a query.
    /// </summary>
    /// <param name="query">The query text.</param>
    /// <param name="context">Optional conversation context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The detected intent result.</returns>
    public async Task<IntentDetectionResult> DetectAsync(
        string query,
        ConversationContext? context = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(query);

        var sw = Stopwatch.StartNew();

        IntentDetectionResult result;

        if (_messageBusPublisher != null && _options.EnableAIDetection)
        {
            try
            {
                result = await DetectWithAIAsync(query, context, ct).ConfigureAwait(false);
            }
            catch
            {
                Debug.WriteLine($"Caught exception in NaturalLanguageProcessing.cs");
                result = DetectWithRules(query, context);
            }
        }
        else
        {
            result = DetectWithRules(query, context);
        }

        sw.Stop();
        result.DetectionDuration = sw.Elapsed;

        return result;
    }

    private async Task<IntentDetectionResult> DetectWithAIAsync(
        string query,
        ConversationContext? context,
        CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            query,
            context = context != null ? new
            {
                previousIntents = context.PreviousIntents,
                sessionId = context.SessionId,
                metadata = context.Metadata
            } : null
        });

        var response = await _messageBusPublisher!(NLPTopics.DetectIntent, payload, ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(response))
        {
            var aiResult = JsonSerializer.Deserialize<IntentDetectionResult>(response);
            if (aiResult != null)
            {
                return aiResult;
            }
        }

        return DetectWithRules(query, context);
    }

    private IntentDetectionResult DetectWithRules(string query, ConversationContext? context)
    {
        var lowerQuery = query.ToLowerInvariant().Trim();
        var alternatives = new List<AlternativeIntent>();

        // Primary intent detection with confidence scoring
        var (primaryIntent, confidence) = DetermineIntentWithConfidence(lowerQuery);

        // Consider context for follow-up intents
        if (context?.PreviousIntents.Count > 0)
        {
            var lastIntent = context.PreviousIntents.Last();
            if (IsFollowUp(lowerQuery, lastIntent))
            {
                alternatives.Add(new AlternativeIntent
                {
                    Intent = lastIntent,
                    Confidence = 0.6,
                    Reason = "Follow-up to previous intent"
                });
            }
        }

        // Add alternative intents
        if (primaryIntent != UserIntent.Query && ContainsQueryIndicators(lowerQuery))
        {
            alternatives.Add(new AlternativeIntent
            {
                Intent = UserIntent.Query,
                Confidence = 0.4,
                Reason = "Contains query indicators"
            });
        }

        if (primaryIntent != UserIntent.Command && ContainsCommandIndicators(lowerQuery))
        {
            alternatives.Add(new AlternativeIntent
            {
                Intent = UserIntent.Command,
                Confidence = 0.3,
                Reason = "Contains command indicators"
            });
        }

        return new IntentDetectionResult
        {
            PrimaryIntent = primaryIntent,
            Confidence = confidence,
            AlternativeIntents = alternatives,
            Query = query,
            RequiresClarification = confidence < _options.ClarificationThreshold
        };
    }

    private (UserIntent Intent, double Confidence) DetermineIntentWithConfidence(string query)
    {
        // Strong command indicators
        var commandPatterns = new[]
        {
            ("^delete\\b", 0.95),
            ("^remove\\b", 0.95),
            ("^create\\b", 0.9),
            ("^add\\b", 0.85),
            ("^update\\b", 0.9),
            ("^execute\\b", 0.95),
            ("^run\\b", 0.85),
            ("^please\\s+(delete|remove|create|add|update)", 0.85)
        };

        foreach (var (pattern, confidence) in commandPatterns)
        {
            if (Regex.IsMatch(query, pattern))
            {
                return (UserIntent.Command, confidence);
            }
        }

        // Strong query indicators
        var queryPatterns = new[]
        {
            ("^(what|which|where|when|who|how)\\b", 0.9),
            ("^find\\b", 0.9),
            ("^search\\b", 0.9),
            ("^show\\b", 0.85),
            ("^list\\b", 0.9),
            ("^get\\b", 0.8),
            ("^count\\b", 0.9),
            ("\\?$", 0.7)
        };

        foreach (var (pattern, confidence) in queryPatterns)
        {
            if (Regex.IsMatch(query, pattern))
            {
                return (UserIntent.Query, confidence);
            }
        }

        // Clarification
        if (query.Contains("what do you mean") || query.Contains("explain") ||
            query.Contains("don't understand") || query.Contains("clarify"))
        {
            return (UserIntent.Clarification, 0.85);
        }

        // Confirmation
        if (query is "yes" or "no" or "ok" or "okay" or "correct" or "right" or "confirmed" or "affirmative")
        {
            return (UserIntent.Confirmation, 0.95);
        }

        // Cancel
        if (query is "cancel" or "abort" or "stop" or "nevermind" or "forget it")
        {
            return (UserIntent.Cancel, 0.95);
        }

        // Greeting
        if (query.StartsWith("hello") || query.StartsWith("hi ") || query == "hi" ||
            query.StartsWith("hey") || query.Contains("good morning") ||
            query.Contains("good afternoon") || query.Contains("good evening"))
        {
            return (UserIntent.Greeting, 0.9);
        }

        return (UserIntent.Unknown, 0.3);
    }

    private bool IsFollowUp(string query, UserIntent lastIntent)
    {
        var followUpIndicators = new[] { "also", "and", "another", "more", "next", "then", "what about" };
        return followUpIndicators.Any(i => query.Contains(i));
    }

    private bool ContainsQueryIndicators(string query)
    {
        var indicators = new[] { "find", "search", "show", "list", "get", "where", "what", "which", "?" };
        return indicators.Any(i => query.Contains(i));
    }

    private bool ContainsCommandIndicators(string query)
    {
        var indicators = new[] { "delete", "remove", "create", "add", "update", "execute", "run" };
        return indicators.Any(i => query.Contains(i));
    }
}

/// <summary>
/// Result of intent detection.
/// </summary>
public sealed class IntentDetectionResult
{
    /// <summary>Gets or sets the primary detected intent.</summary>
    public UserIntent PrimaryIntent { get; init; }

    /// <summary>Gets or sets the confidence score (0-1).</summary>
    public double Confidence { get; init; }

    /// <summary>Gets or sets alternative intents.</summary>
    public IReadOnlyList<AlternativeIntent> AlternativeIntents { get; init; } = Array.Empty<AlternativeIntent>();

    /// <summary>Gets or sets the original query.</summary>
    public string Query { get; init; } = string.Empty;

    /// <summary>Gets or sets whether clarification is needed.</summary>
    public bool RequiresClarification { get; init; }

    /// <summary>Gets or sets detection duration.</summary>
    public TimeSpan DetectionDuration { get; set; }
}

/// <summary>
/// An alternative intent with lower confidence.
/// </summary>
public sealed class AlternativeIntent
{
    /// <summary>Gets or sets the intent.</summary>
    public UserIntent Intent { get; init; }

    /// <summary>Gets or sets the confidence score.</summary>
    public double Confidence { get; init; }

    /// <summary>Gets or sets the reason for this alternative.</summary>
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// Conversation context for intent detection.
/// </summary>
public sealed class ConversationContext
{
    /// <summary>Gets or sets the session ID.</summary>
    public string? SessionId { get; init; }

    /// <summary>Gets or sets previous intents in the conversation.</summary>
    public IReadOnlyList<UserIntent> PreviousIntents { get; init; } = Array.Empty<UserIntent>();

    /// <summary>Gets or sets additional metadata.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Configuration options for IntentDetector.
/// </summary>
public sealed class IntentDetectorOptions
{
    /// <summary>Gets or sets whether to enable AI-powered detection.</summary>
    public bool EnableAIDetection { get; set; } = true;

    /// <summary>Gets or sets the confidence threshold below which clarification is requested.</summary>
    public double ClarificationThreshold { get; set; } = 0.5;
}

/// <summary>
/// Extracts entities from natural language text.
/// </summary>
/// <remarks>
/// <para>
/// The EntityExtractor identifies and extracts structured entities including:
/// </para>
/// <list type="bullet">
///   <item>File names and paths</item>
///   <item>Dates, times, and durations</item>
///   <item>Sizes (bytes, KB, MB, GB)</item>
///   <item>Table and column names</item>
///   <item>Custom domain-specific entities</item>
/// </list>
/// </remarks>
public sealed class EntityExtractor
{
    private readonly EntityExtractorOptions _options;
    private readonly Func<string, string, CancellationToken, Task<string?>>? _messageBusPublisher;
    private readonly BoundedDictionary<string, Regex> _compiledPatterns = new BoundedDictionary<string, Regex>(1000);

    /// <summary>
    /// Creates a new EntityExtractor.
    /// </summary>
    /// <param name="options">Extractor configuration options.</param>
    /// <param name="messageBusPublisher">Optional message bus publisher for AI-powered extraction.</param>
    public EntityExtractor(
        EntityExtractorOptions? options = null,
        Func<string, string, CancellationToken, Task<string?>>? messageBusPublisher = null)
    {
        _options = options ?? new EntityExtractorOptions();
        _messageBusPublisher = messageBusPublisher;
        InitializePatterns();
    }

    private void InitializePatterns()
    {
        // Pre-compile common patterns
        _compiledPatterns["filePath"] = new Regex(@"(?:[a-zA-Z]:\\|/)?(?:[\w.-]+[/\\])*[\w.-]+\.\w+", RegexOptions.Compiled);
        _compiledPatterns["size"] = new Regex(@"\b(\d+(?:\.\d+)?)\s*(bytes?|[KMGTPE]B|[KMGTPE]iB)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        _compiledPatterns["date"] = new Regex(@"\b(\d{4}-\d{2}-\d{2}|\d{1,2}/\d{1,2}/\d{2,4})\b", RegexOptions.Compiled);
        _compiledPatterns["email"] = new Regex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,}\b", RegexOptions.Compiled);
        _compiledPatterns["url"] = new Regex(@"https?://[^\s]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        _compiledPatterns["number"] = new Regex(@"\b\d+(?:\.\d+)?\b", RegexOptions.Compiled);
    }

    /// <summary>
    /// Extracts entities from text.
    /// </summary>
    /// <param name="text">The text to extract entities from.</param>
    /// <param name="entityTypes">Optional filter for specific entity types.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of extracted entities.</returns>
    public async Task<IReadOnlyList<ExtractedEntity>> ExtractAsync(
        string text,
        EntityType[]? entityTypes = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);

        var entities = new List<ExtractedEntity>();

        // Use AI extraction if available
        if (_messageBusPublisher != null && _options.EnableAIExtraction)
        {
            try
            {
                var aiEntities = await ExtractWithAIAsync(text, entityTypes, ct).ConfigureAwait(false);
                entities.AddRange(aiEntities);
            }
            catch
            {
                Debug.WriteLine($"Caught exception in NaturalLanguageProcessing.cs");
                // Fall back to rule-based extraction
                entities.AddRange(ExtractWithRules(text, entityTypes));
            }
        }
        else
        {
            entities.AddRange(ExtractWithRules(text, entityTypes));
        }

        // Deduplicate and sort by position
        return entities
            .GroupBy(e => new { e.Value, e.Type })
            .Select(g => g.OrderByDescending(e => e.Confidence).First())
            .OrderBy(e => e.StartPosition)
            .ToList();
    }

    private async Task<IReadOnlyList<ExtractedEntity>> ExtractWithAIAsync(
        string text,
        EntityType[]? entityTypes,
        CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { text, entityTypes = entityTypes?.Select(t => t.ToString()) });
        var response = await _messageBusPublisher!(NLPTopics.ExtractEntities, payload, ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(response))
        {
            var result = JsonSerializer.Deserialize<List<ExtractedEntity>>(response);
            if (result != null) return result;
        }

        return Array.Empty<ExtractedEntity>();
    }

    private IReadOnlyList<ExtractedEntity> ExtractWithRules(string text, EntityType[]? entityTypes)
    {
        var entities = new List<ExtractedEntity>();
        var typesToExtract = entityTypes?.ToHashSet() ?? null;

        // File paths
        if (typesToExtract == null || typesToExtract.Contains(EntityType.FileName))
        {
            foreach (Match match in _compiledPatterns["filePath"].Matches(text))
            {
                entities.Add(new ExtractedEntity
                {
                    Value = match.Value,
                    Type = EntityType.FileName,
                    StartPosition = match.Index,
                    EndPosition = match.Index + match.Length,
                    Confidence = 0.8
                });
            }
        }

        // Sizes
        if (typesToExtract == null || typesToExtract.Contains(EntityType.Size))
        {
            foreach (Match match in _compiledPatterns["size"].Matches(text))
            {
                entities.Add(new ExtractedEntity
                {
                    Value = match.Value,
                    Type = EntityType.Size,
                    StartPosition = match.Index,
                    EndPosition = match.Index + match.Length,
                    Confidence = 0.9
                });
            }
        }

        // Dates
        if (typesToExtract == null || typesToExtract.Contains(EntityType.DateTime))
        {
            foreach (Match match in _compiledPatterns["date"].Matches(text))
            {
                entities.Add(new ExtractedEntity
                {
                    Value = match.Value,
                    Type = EntityType.DateTime,
                    StartPosition = match.Index,
                    EndPosition = match.Index + match.Length,
                    Confidence = 0.85
                });
            }
        }

        // Emails
        if (typesToExtract == null || typesToExtract.Contains(EntityType.Email))
        {
            foreach (Match match in _compiledPatterns["email"].Matches(text))
            {
                entities.Add(new ExtractedEntity
                {
                    Value = match.Value,
                    Type = EntityType.Email,
                    StartPosition = match.Index,
                    EndPosition = match.Index + match.Length,
                    Confidence = 0.95
                });
            }
        }

        // URLs
        if (typesToExtract == null || typesToExtract.Contains(EntityType.Url))
        {
            foreach (Match match in _compiledPatterns["url"].Matches(text))
            {
                entities.Add(new ExtractedEntity
                {
                    Value = match.Value,
                    Type = EntityType.Url,
                    StartPosition = match.Index,
                    EndPosition = match.Index + match.Length,
                    Confidence = 0.95
                });
            }
        }

        return entities;
    }
}

/// <summary>
/// Configuration options for EntityExtractor.
/// </summary>
public sealed class EntityExtractorOptions
{
    /// <summary>Gets or sets whether to enable AI-powered extraction.</summary>
    public bool EnableAIExtraction { get; set; } = true;

    /// <summary>Gets or sets custom entity patterns.</summary>
    public Dictionary<EntityType, string> CustomPatterns { get; set; } = new();
}

/// <summary>
/// Generates natural language responses from structured data.
/// </summary>
/// <remarks>
/// <para>
/// The ResponseGenerator creates human-readable responses including:
/// </para>
/// <list type="bullet">
///   <item>Query result summaries</item>
///   <item>Error explanations</item>
///   <item>Clarification requests</item>
///   <item>Confirmation messages</item>
/// </list>
/// </remarks>
public sealed class ResponseGenerator
{
    private readonly ResponseGeneratorOptions _options;
    private readonly Func<string, string, CancellationToken, Task<string?>>? _messageBusPublisher;

    /// <summary>
    /// Creates a new ResponseGenerator.
    /// </summary>
    /// <param name="options">Generator configuration options.</param>
    /// <param name="messageBusPublisher">Optional message bus publisher for AI-powered generation.</param>
    public ResponseGenerator(
        ResponseGeneratorOptions? options = null,
        Func<string, string, CancellationToken, Task<string?>>? messageBusPublisher = null)
    {
        _options = options ?? new ResponseGeneratorOptions();
        _messageBusPublisher = messageBusPublisher;
    }

    /// <summary>
    /// Generates a response from a response context.
    /// </summary>
    /// <param name="context">The response context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The generated response.</returns>
    public async Task<GeneratedResponse> GenerateAsync(ResponseContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_messageBusPublisher != null && _options.EnableAIGeneration)
        {
            try
            {
                return await GenerateWithAIAsync(context, ct).ConfigureAwait(false);
            }
            catch
            {
                Debug.WriteLine($"Caught exception in NaturalLanguageProcessing.cs");
                return GenerateWithTemplates(context);
            }
        }

        return GenerateWithTemplates(context);
    }

    private async Task<GeneratedResponse> GenerateWithAIAsync(ResponseContext context, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(context);
        var response = await _messageBusPublisher!(NLPTopics.GenerateResponse, payload, ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(response))
        {
            var result = JsonSerializer.Deserialize<GeneratedResponse>(response);
            if (result != null) return result;
        }

        return GenerateWithTemplates(context);
    }

    private GeneratedResponse GenerateWithTemplates(ResponseContext context)
    {
        var sb = new StringBuilder();

        switch (context.ResponseType)
        {
            case ResponseType.QueryResult:
                GenerateQueryResultResponse(sb, context);
                break;
            case ResponseType.CommandResult:
                GenerateCommandResultResponse(sb, context);
                break;
            case ResponseType.Error:
                GenerateErrorResponse(sb, context);
                break;
            case ResponseType.Clarification:
                GenerateClarificationResponse(sb, context);
                break;
            case ResponseType.Confirmation:
                GenerateConfirmationResponse(sb, context);
                break;
            default:
                sb.Append("I've processed your request.");
                break;
        }

        return new GeneratedResponse
        {
            Text = sb.ToString(),
            ResponseType = context.ResponseType,
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    private void GenerateQueryResultResponse(StringBuilder sb, ResponseContext context)
    {
        if (context.ResultCount == 0)
        {
            sb.Append(_options.NoResultsMessage);
            return;
        }

        if (context.ResultCount == 1)
        {
            sb.Append("I found 1 result");
        }
        else
        {
            sb.Append($"I found {context.ResultCount} results");
        }

        if (!string.IsNullOrEmpty(context.QuerySummary))
        {
            sb.Append($" for \"{context.QuerySummary}\"");
        }

        sb.Append('.');

        if (context.HasMoreResults)
        {
            sb.Append(" There are more results available. Would you like to see more?");
        }
    }

    private void GenerateCommandResultResponse(StringBuilder sb, ResponseContext context)
    {
        if (context.Success)
        {
            sb.Append("Done! ");
            if (!string.IsNullOrEmpty(context.CommandSummary))
            {
                sb.Append(context.CommandSummary);
            }
        }
        else
        {
            sb.Append("The operation could not be completed.");
            if (!string.IsNullOrEmpty(context.ErrorMessage))
            {
                sb.Append($" Error: {context.ErrorMessage}");
            }
        }
    }

    private void GenerateErrorResponse(StringBuilder sb, ResponseContext context)
    {
        sb.Append("I encountered an error: ");
        sb.Append(context.ErrorMessage ?? "An unknown error occurred.");

        if (!string.IsNullOrEmpty(context.SuggestedAction))
        {
            sb.Append($" {context.SuggestedAction}");
        }
    }

    private void GenerateClarificationResponse(StringBuilder sb, ResponseContext context)
    {
        sb.Append("I need some clarification. ");
        if (!string.IsNullOrEmpty(context.ClarificationQuestion))
        {
            sb.Append(context.ClarificationQuestion);
        }
        else
        {
            sb.Append("Could you please provide more details?");
        }
    }

    private void GenerateConfirmationResponse(StringBuilder sb, ResponseContext context)
    {
        if (!string.IsNullOrEmpty(context.ConfirmationMessage))
        {
            sb.Append(context.ConfirmationMessage);
        }
        else
        {
            sb.Append("Are you sure you want to proceed?");
        }
    }
}

/// <summary>
/// Response type for generation.
/// </summary>
public enum ResponseType
{
    /// <summary>Response to a query.</summary>
    QueryResult,

    /// <summary>Response to a command.</summary>
    CommandResult,

    /// <summary>Error response.</summary>
    Error,

    /// <summary>Clarification request.</summary>
    Clarification,

    /// <summary>Confirmation request.</summary>
    Confirmation,

    /// <summary>General information.</summary>
    Information
}

/// <summary>
/// Context for response generation.
/// </summary>
public sealed class ResponseContext
{
    /// <summary>Gets or sets the response type.</summary>
    public ResponseType ResponseType { get; init; }

    /// <summary>Gets or sets whether the operation was successful.</summary>
    public bool Success { get; init; } = true;

    /// <summary>Gets or sets the result count.</summary>
    public int ResultCount { get; init; }

    /// <summary>Gets or sets whether there are more results.</summary>
    public bool HasMoreResults { get; init; }

    /// <summary>Gets or sets the query summary.</summary>
    public string? QuerySummary { get; init; }

    /// <summary>Gets or sets the command summary.</summary>
    public string? CommandSummary { get; init; }

    /// <summary>Gets or sets the error message.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Gets or sets a suggested action.</summary>
    public string? SuggestedAction { get; init; }

    /// <summary>Gets or sets a clarification question.</summary>
    public string? ClarificationQuestion { get; init; }

    /// <summary>Gets or sets a confirmation message.</summary>
    public string? ConfirmationMessage { get; init; }

    /// <summary>Gets or sets additional metadata.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Generated response from ResponseGenerator.
/// </summary>
public sealed class GeneratedResponse
{
    /// <summary>Gets or sets the response text.</summary>
    public required string Text { get; init; }

    /// <summary>Gets or sets the response type.</summary>
    public ResponseType ResponseType { get; init; }

    /// <summary>Gets or sets the generation timestamp.</summary>
    public DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// Configuration options for ResponseGenerator.
/// </summary>
public sealed class ResponseGeneratorOptions
{
    /// <summary>Gets or sets whether to enable AI-powered generation.</summary>
    public bool EnableAIGeneration { get; set; } = true;

    /// <summary>Gets or sets the message for no results.</summary>
    public string NoResultsMessage { get; set; } = "I couldn't find any results matching your query.";

    /// <summary>Gets or sets the maximum response length.</summary>
    public int MaxResponseLength { get; set; } = 1000;
}

#endregion

#region M2: Semantic Search

/// <summary>
/// Unified vector store wrapper for cross-domain semantic operations.
/// </summary>
/// <remarks>
/// <para>
/// The UnifiedVectorStore provides:
/// </para>
/// <list type="bullet">
///   <item>Abstraction over multiple vector store backends</item>
///   <item>Namespace/domain isolation</item>
///   <item>Automatic embedding generation</item>
///   <item>Cross-domain search capabilities</item>
/// </list>
/// </remarks>
public sealed class UnifiedVectorStore : IAsyncDisposable
{
    private readonly UnifiedVectorStoreOptions _options;
    private readonly BoundedDictionary<string, IVectorStoreBackend> _backends = new BoundedDictionary<string, IVectorStoreBackend>(1000);
    private readonly Func<string, CancellationToken, Task<float[]>>? _embeddingProvider;
    private readonly Func<string, string, CancellationToken, Task<string?>>? _messageBusPublisher;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Creates a new UnifiedVectorStore.
    /// </summary>
    /// <param name="options">Store configuration options.</param>
    /// <param name="embeddingProvider">Function to generate embeddings from text.</param>
    /// <param name="messageBusPublisher">Optional message bus publisher.</param>
    public UnifiedVectorStore(
        UnifiedVectorStoreOptions? options = null,
        Func<string, CancellationToken, Task<float[]>>? embeddingProvider = null,
        Func<string, string, CancellationToken, Task<string?>>? messageBusPublisher = null)
    {
        _options = options ?? new UnifiedVectorStoreOptions();
        _embeddingProvider = embeddingProvider;
        _messageBusPublisher = messageBusPublisher;
    }

    /// <summary>
    /// Registers a vector store backend for a domain.
    /// </summary>
    /// <param name="domain">The domain name.</param>
    /// <param name="backend">The vector store backend.</param>
    public void RegisterBackend(string domain, IVectorStoreBackend backend)
    {
        ArgumentException.ThrowIfNullOrEmpty(domain);
        ArgumentNullException.ThrowIfNull(backend);

        _backends[domain] = backend;
    }

    /// <summary>
    /// Stores a vector in a specific domain.
    /// </summary>
    /// <param name="domain">The domain name.</param>
    /// <param name="id">Vector ID.</param>
    /// <param name="text">Text to embed and store.</param>
    /// <param name="metadata">Optional metadata.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task StoreAsync(
        string domain,
        string id,
        string text,
        Dictionary<string, object>? metadata = null,
        CancellationToken ct = default)
    {
        var embedding = await GetEmbeddingAsync(text, ct).ConfigureAwait(false);
        var backend = GetOrCreateBackend(domain);

        await backend.StoreAsync(id, embedding, metadata, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Searches across one or more domains.
    /// </summary>
    /// <param name="query">Search query text.</param>
    /// <param name="domains">Domains to search (null = all).</param>
    /// <param name="topK">Maximum results per domain.</param>
    /// <param name="minScore">Minimum similarity score.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Search results from all queried domains.</returns>
    public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string query,
        string[]? domains = null,
        int topK = 10,
        float minScore = 0.5f,
        CancellationToken ct = default)
    {
        var queryEmbedding = await GetEmbeddingAsync(query, ct).ConfigureAwait(false);

        var targetDomains = domains ?? _backends.Keys.ToArray();
        var allResults = new List<VectorSearchResult>();

        var searchTasks = targetDomains.Select(async domain =>
        {
            if (!_backends.TryGetValue(domain, out var backend))
            {
                return Enumerable.Empty<VectorSearchResult>();
            }

            try
            {
                var results = await backend.SearchAsync(queryEmbedding, topK, minScore, ct).ConfigureAwait(false);
                return results.Select(r => new VectorSearchResult
                {
                    Id = r.Id,
                    Score = r.Score,
                    Domain = domain,
                    Metadata = r.Metadata
                });
            }
            catch
            {
                Debug.WriteLine($"Caught exception in NaturalLanguageProcessing.cs");
                return Enumerable.Empty<VectorSearchResult>();
            }
        });

        var resultSets = await Task.WhenAll(searchTasks).ConfigureAwait(false);

        foreach (var results in resultSets)
        {
            allResults.AddRange(results);
        }

        return allResults
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .ToList();
    }

    /// <summary>
    /// Deletes a vector from a domain.
    /// </summary>
    /// <param name="domain">The domain name.</param>
    /// <param name="id">Vector ID to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task DeleteAsync(string domain, string id, CancellationToken ct = default)
    {
        if (_backends.TryGetValue(domain, out var backend))
        {
            await backend.DeleteAsync(id, ct).ConfigureAwait(false);
        }
    }

    private async Task<float[]> GetEmbeddingAsync(string text, CancellationToken ct)
    {
        if (_embeddingProvider != null)
        {
            return await _embeddingProvider(text, ct).ConfigureAwait(false);
        }

        if (_messageBusPublisher != null)
        {
            var payload = JsonSerializer.Serialize(new { text });
            var response = await _messageBusPublisher(NLPTopics.GetEmbeddings, payload, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(response))
            {
                var embedding = JsonSerializer.Deserialize<float[]>(response);
                if (embedding != null) return embedding;
            }
        }

        throw new InvalidOperationException("No embedding provider configured");
    }

    private IVectorStoreBackend GetOrCreateBackend(string domain)
    {
        return _backends.GetOrAdd(domain, _ =>
            _options.BackendFactory?.Invoke(domain) ??
            throw new InvalidOperationException($"No backend registered for domain '{domain}' and no factory configured"));
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var backend in _backends.Values)
        {
            if (backend is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (backend is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        _backends.Clear();
        _initLock.Dispose();
    }
}

/// <summary>
/// Vector search result.
/// </summary>
public sealed class VectorSearchResult
{
    /// <summary>Gets or sets the vector ID.</summary>
    public required string Id { get; init; }

    /// <summary>Gets or sets the similarity score.</summary>
    public float Score { get; init; }

    /// <summary>Gets or sets the domain.</summary>
    public required string Domain { get; init; }

    /// <summary>Gets or sets metadata.</summary>
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Vector store backend interface.
/// </summary>
public interface IVectorStoreBackend
{
    /// <summary>Stores a vector.</summary>
    Task StoreAsync(string id, float[] vector, Dictionary<string, object>? metadata, CancellationToken ct);

    /// <summary>Searches for similar vectors.</summary>
    Task<IReadOnlyList<VectorBackendResult>> SearchAsync(float[] query, int topK, float minScore, CancellationToken ct);

    /// <summary>Deletes a vector.</summary>
    Task DeleteAsync(string id, CancellationToken ct);
}

/// <summary>
/// Result from vector backend search.
/// </summary>
public sealed class VectorBackendResult
{
    /// <summary>Gets or sets the ID.</summary>
    public required string Id { get; init; }

    /// <summary>Gets or sets the score.</summary>
    public float Score { get; init; }

    /// <summary>Gets or sets metadata.</summary>
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Configuration options for UnifiedVectorStore.
/// </summary>
public sealed class UnifiedVectorStoreOptions
{
    /// <summary>Gets or sets the default embedding dimension.</summary>
    public int EmbeddingDimension { get; set; } = 1536;

    /// <summary>Gets or sets a factory for creating backends.</summary>
    public Func<string, IVectorStoreBackend>? BackendFactory { get; set; }
}

/// <summary>
/// Indexes content for semantic search across all knowledge domains.
/// </summary>
/// <remarks>
/// <para>
/// The SemanticIndexer provides:
/// </para>
/// <list type="bullet">
///   <item>Automatic content chunking</item>
///   <item>Incremental indexing</item>
///   <item>Index maintenance and optimization</item>
///   <item>Multi-domain index management</item>
/// </list>
/// </remarks>
public sealed class SemanticIndexer
{
    private readonly SemanticIndexerOptions _options;
    private readonly UnifiedVectorStore _vectorStore;
    private readonly BoundedDictionary<string, IndexStats> _indexStats = new BoundedDictionary<string, IndexStats>(1000);

    /// <summary>
    /// Creates a new SemanticIndexer.
    /// </summary>
    /// <param name="vectorStore">The unified vector store to use.</param>
    /// <param name="options">Indexer configuration options.</param>
    public SemanticIndexer(UnifiedVectorStore vectorStore, SemanticIndexerOptions? options = null)
    {
        _vectorStore = vectorStore ?? throw new ArgumentNullException(nameof(vectorStore));
        _options = options ?? new SemanticIndexerOptions();
    }

    /// <summary>
    /// Indexes a document.
    /// </summary>
    /// <param name="domain">The domain to index into.</param>
    /// <param name="documentId">Document ID.</param>
    /// <param name="content">Document content.</param>
    /// <param name="metadata">Optional metadata.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of chunks indexed.</returns>
    public async Task<int> IndexDocumentAsync(
        string domain,
        string documentId,
        string content,
        Dictionary<string, object>? metadata = null,
        CancellationToken ct = default)
    {
        var chunks = ChunkContent(content);
        var chunkCount = 0;

        foreach (var (chunk, index) in chunks.Select((c, i) => (c, i)))
        {
            var chunkId = $"{documentId}:chunk:{index}";
            var chunkMetadata = new Dictionary<string, object>
            {
                ["documentId"] = documentId,
                ["chunkIndex"] = index,
                ["totalChunks"] = chunks.Count
            };

            if (metadata != null)
            {
                foreach (var kvp in metadata)
                {
                    chunkMetadata[kvp.Key] = kvp.Value;
                }
            }

            await _vectorStore.StoreAsync(domain, chunkId, chunk, chunkMetadata, ct).ConfigureAwait(false);
            chunkCount++;
        }

        UpdateStats(domain, chunkCount);
        return chunkCount;
    }

    /// <summary>
    /// Removes a document from the index.
    /// </summary>
    /// <param name="domain">The domain.</param>
    /// <param name="documentId">Document ID to remove.</param>
    /// <param name="chunkCount">Number of chunks to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RemoveDocumentAsync(string domain, string documentId, int chunkCount, CancellationToken ct = default)
    {
        for (var i = 0; i < chunkCount; i++)
        {
            var chunkId = $"{documentId}:chunk:{i}";
            await _vectorStore.DeleteAsync(domain, chunkId, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets index statistics for a domain.
    /// </summary>
    /// <param name="domain">The domain.</param>
    /// <returns>Index statistics.</returns>
    public IndexStats GetStats(string domain)
    {
        return _indexStats.GetOrAdd(domain, _ => new IndexStats { Domain = domain });
    }

    private IReadOnlyList<string> ChunkContent(string content)
    {
        var chunks = new List<string>();
        var words = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var currentChunk = new StringBuilder();
        var currentWordCount = 0;

        foreach (var word in words)
        {
            if (currentWordCount >= _options.ChunkSize)
            {
                chunks.Add(currentChunk.ToString().Trim());
                currentChunk.Clear();
                currentWordCount = 0;

                // Add overlap
                var overlapWords = words.Skip(chunks.Count * (_options.ChunkSize - _options.ChunkOverlap))
                    .Take(_options.ChunkOverlap);
                foreach (var overlapWord in overlapWords)
                {
                    currentChunk.Append(overlapWord).Append(' ');
                    currentWordCount++;
                }
            }

            currentChunk.Append(word).Append(' ');
            currentWordCount++;
        }

        if (currentChunk.Length > 0)
        {
            chunks.Add(currentChunk.ToString().Trim());
        }

        return chunks;
    }

    private void UpdateStats(string domain, int chunksAdded)
    {
        var stats = _indexStats.GetOrAdd(domain, _ => new IndexStats { Domain = domain });
        Interlocked.Add(ref stats._totalChunks, chunksAdded);
        Interlocked.Increment(ref stats._totalDocuments);
        stats.LastUpdated = DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// Index statistics.
/// </summary>
public sealed class IndexStats
{
    internal long _totalChunks;
    internal long _totalDocuments;

    /// <summary>Gets or sets the domain.</summary>
    public required string Domain { get; init; }

    /// <summary>Gets the total chunks indexed.</summary>
    public long TotalChunks => Interlocked.Read(ref _totalChunks);

    /// <summary>Gets the total documents indexed.</summary>
    public long TotalDocuments => Interlocked.Read(ref _totalDocuments);

    /// <summary>Gets or sets the last update time.</summary>
    public DateTimeOffset LastUpdated { get; set; }
}

/// <summary>
/// Configuration options for SemanticIndexer.
/// </summary>
public sealed class SemanticIndexerOptions
{
    /// <summary>Gets or sets the chunk size in words.</summary>
    public int ChunkSize { get; set; } = 256;

    /// <summary>Gets or sets the overlap between chunks in words.</summary>
    public int ChunkOverlap { get; set; } = 32;
}

/// <summary>
/// Performs cross-domain semantic search over indexed knowledge.
/// </summary>
/// <remarks>
/// <para>
/// The SemanticSearch component provides:
/// </para>
/// <list type="bullet">
///   <item>Natural language query processing</item>
///   <item>Multi-domain search with ranking</item>
///   <item>Result aggregation and deduplication</item>
///   <item>Context-aware result boosting</item>
/// </list>
/// </remarks>
public sealed class SemanticSearch
{
    private readonly SemanticSearchOptions _options;
    private readonly UnifiedVectorStore _vectorStore;
    private readonly QueryParser? _queryParser;

    /// <summary>
    /// Creates a new SemanticSearch instance.
    /// </summary>
    /// <param name="vectorStore">The unified vector store.</param>
    /// <param name="queryParser">Optional query parser for preprocessing.</param>
    /// <param name="options">Search configuration options.</param>
    public SemanticSearch(
        UnifiedVectorStore vectorStore,
        QueryParser? queryParser = null,
        SemanticSearchOptions? options = null)
    {
        _vectorStore = vectorStore ?? throw new ArgumentNullException(nameof(vectorStore));
        _queryParser = queryParser;
        _options = options ?? new SemanticSearchOptions();
    }

    /// <summary>
    /// Performs a semantic search.
    /// </summary>
    /// <param name="query">Search query.</param>
    /// <param name="options">Search options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Search results.</returns>
    public async Task<SemanticSearchResults> SearchAsync(
        string query,
        SearchOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(query);

        var sw = Stopwatch.StartNew();
        options ??= new SearchOptions();

        // Parse query if parser available
        ParsedQuery? parsedQuery = null;
        if (_queryParser != null)
        {
            parsedQuery = await _queryParser.ParseAsync(query, ct).ConfigureAwait(false);
        }

        // Perform vector search
        var results = await _vectorStore.SearchAsync(
            query,
            options.Domains,
            options.TopK,
            options.MinScore,
            ct).ConfigureAwait(false);

        // Apply boosting and filtering
        var boostedResults = ApplyBoosts(results, parsedQuery, options);

        sw.Stop();

        return new SemanticSearchResults
        {
            Query = query,
            ParsedQuery = parsedQuery,
            Results = boostedResults,
            TotalResults = boostedResults.Count,
            SearchDuration = sw.Elapsed
        };
    }

    private IReadOnlyList<SemanticSearchResult> ApplyBoosts(
        IReadOnlyList<VectorSearchResult> results,
        ParsedQuery? parsedQuery,
        SearchOptions options)
    {
        var boostedResults = new List<SemanticSearchResult>();

        foreach (var result in results)
        {
            var boost = 1.0f;

            // Apply domain boost
            if (options.DomainBoosts?.TryGetValue(result.Domain, out var domainBoost) == true)
            {
                boost *= domainBoost;
            }

            // Apply recency boost
            if (options.RecencyBoost > 0 &&
                result.Metadata?.TryGetValue("timestamp", out var ts) == true &&
                ts is DateTimeOffset timestamp)
            {
                var age = (DateTimeOffset.UtcNow - timestamp).TotalDays;
                var recencyFactor = Math.Max(0, 1 - (age / 365.0)); // Decay over a year
                boost *= (float)(1 + options.RecencyBoost * recencyFactor);
            }

            boostedResults.Add(new SemanticSearchResult
            {
                Id = result.Id,
                Domain = result.Domain,
                Score = result.Score * boost,
                OriginalScore = result.Score,
                Metadata = result.Metadata
            });
        }

        return boostedResults
            .OrderByDescending(r => r.Score)
            .Take(options.TopK)
            .ToList();
    }
}

/// <summary>
/// Semantic search results.
/// </summary>
public sealed class SemanticSearchResults
{
    /// <summary>Gets or sets the original query.</summary>
    public required string Query { get; init; }

    /// <summary>Gets or sets the parsed query.</summary>
    public ParsedQuery? ParsedQuery { get; init; }

    /// <summary>Gets or sets the search results.</summary>
    public IReadOnlyList<SemanticSearchResult> Results { get; init; } = Array.Empty<SemanticSearchResult>();

    /// <summary>Gets or sets the total result count.</summary>
    public int TotalResults { get; init; }

    /// <summary>Gets or sets the search duration.</summary>
    public TimeSpan SearchDuration { get; init; }
}

/// <summary>
/// A single semantic search result.
/// </summary>
public sealed class SemanticSearchResult
{
    /// <summary>Gets or sets the result ID.</summary>
    public required string Id { get; init; }

    /// <summary>Gets or sets the domain.</summary>
    public required string Domain { get; init; }

    /// <summary>Gets or sets the boosted score.</summary>
    public float Score { get; init; }

    /// <summary>Gets or sets the original score before boosting.</summary>
    public float OriginalScore { get; init; }

    /// <summary>Gets or sets metadata.</summary>
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Options for semantic search.
/// </summary>
public sealed class SearchOptions
{
    /// <summary>Gets or sets domains to search.</summary>
    public string[]? Domains { get; set; }

    /// <summary>Gets or sets the number of results to return.</summary>
    public int TopK { get; set; } = 10;

    /// <summary>Gets or sets the minimum score threshold.</summary>
    public float MinScore { get; set; } = 0.5f;

    /// <summary>Gets or sets domain-specific boosts.</summary>
    public Dictionary<string, float>? DomainBoosts { get; set; }

    /// <summary>Gets or sets the recency boost factor.</summary>
    public float RecencyBoost { get; set; }
}

/// <summary>
/// Configuration options for SemanticSearch.
/// </summary>
public sealed class SemanticSearchOptions
{
    /// <summary>Gets or sets the default number of results.</summary>
    public int DefaultTopK { get; set; } = 10;

    /// <summary>Gets or sets the default minimum score.</summary>
    public float DefaultMinScore { get; set; } = 0.5f;
}

#endregion

#region M3: Knowledge Graph

/// <summary>
/// Unified knowledge graph for all knowledge relationships.
/// </summary>
/// <remarks>
/// <para>
/// The UnifiedKnowledgeGraph provides:
/// </para>
/// <list type="bullet">
///   <item>Cross-domain knowledge relationships</item>
///   <item>Natural language graph queries</item>
///   <item>Automatic relationship discovery</item>
///   <item>Graph-based reasoning support</item>
/// </list>
/// </remarks>
public sealed class UnifiedKnowledgeGraph : IAsyncDisposable
{
    private readonly UnifiedKnowledgeGraphOptions _options;
    private readonly BoundedDictionary<string, KnowledgeNode> _nodes = new BoundedDictionary<string, KnowledgeNode>(1000);
    private readonly BoundedDictionary<string, KnowledgeEdge> _edges = new BoundedDictionary<string, KnowledgeEdge>(1000);
    private readonly BoundedDictionary<string, HashSet<string>> _nodeEdges = new BoundedDictionary<string, HashSet<string>>(1000);
    private readonly Func<string, string, CancellationToken, Task<string?>>? _messageBusPublisher;
    private readonly SemaphoreSlim _modifyLock = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Creates a new UnifiedKnowledgeGraph.
    /// </summary>
    /// <param name="options">Graph configuration options.</param>
    /// <param name="messageBusPublisher">Optional message bus publisher for AI operations.</param>
    public UnifiedKnowledgeGraph(
        UnifiedKnowledgeGraphOptions? options = null,
        Func<string, string, CancellationToken, Task<string?>>? messageBusPublisher = null)
    {
        _options = options ?? new UnifiedKnowledgeGraphOptions();
        _messageBusPublisher = messageBusPublisher;
    }

    /// <summary>
    /// Adds a node to the graph.
    /// </summary>
    /// <param name="node">The node to add.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task AddNodeAsync(KnowledgeNode node, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(node);

        await _modifyLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _nodes[node.Id] = node;
            if (!_nodeEdges.ContainsKey(node.Id))
            {
                _nodeEdges[node.Id] = new HashSet<string>();
            }
        }
        finally
        {
            _modifyLock.Release();
        }
    }

    /// <summary>
    /// Adds an edge to the graph.
    /// </summary>
    /// <param name="edge">The edge to add.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task AddEdgeAsync(KnowledgeEdge edge, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(edge);

        await _modifyLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _edges[edge.Id] = edge;

            if (_nodeEdges.TryGetValue(edge.FromNodeId, out var fromEdges))
            {
                fromEdges.Add(edge.Id);
            }

            if (_nodeEdges.TryGetValue(edge.ToNodeId, out var toEdges))
            {
                toEdges.Add(edge.Id);
            }
        }
        finally
        {
            _modifyLock.Release();
        }
    }

    /// <summary>
    /// Gets a node by ID.
    /// </summary>
    /// <param name="nodeId">The node ID.</param>
    /// <returns>The node, or null if not found.</returns>
    public KnowledgeNode? GetNode(string nodeId)
    {
        return _nodes.TryGetValue(nodeId, out var node) ? node : null;
    }

    /// <summary>
    /// Gets all edges for a node.
    /// </summary>
    /// <param name="nodeId">The node ID.</param>
    /// <param name="direction">Edge direction filter.</param>
    /// <returns>Edges connected to the node.</returns>
    public IReadOnlyList<KnowledgeEdge> GetEdges(string nodeId, EdgeDirection direction = EdgeDirection.Both)
    {
        if (!_nodeEdges.TryGetValue(nodeId, out var edgeIds))
        {
            return Array.Empty<KnowledgeEdge>();
        }

        var edges = new List<KnowledgeEdge>();
        foreach (var edgeId in edgeIds)
        {
            if (_edges.TryGetValue(edgeId, out var edge))
            {
                if (direction == EdgeDirection.Both ||
                    (direction == EdgeDirection.Outgoing && edge.FromNodeId == nodeId) ||
                    (direction == EdgeDirection.Incoming && edge.ToNodeId == nodeId))
                {
                    edges.Add(edge);
                }
            }
        }

        return edges;
    }

    /// <summary>
    /// Finds nodes by label.
    /// </summary>
    /// <param name="label">The label to search for.</param>
    /// <returns>Matching nodes.</returns>
    public IReadOnlyList<KnowledgeNode> FindNodesByLabel(string label)
    {
        return _nodes.Values
            .Where(n => n.Labels.Contains(label, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Gets graph statistics.
    /// </summary>
    public KnowledgeGraphStats GetStats()
    {
        return new KnowledgeGraphStats
        {
            NodeCount = _nodes.Count,
            EdgeCount = _edges.Count,
            LabelCounts = _nodes.Values
                .SelectMany(n => n.Labels)
                .GroupBy(l => l, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count()),
            RelationshipCounts = _edges.Values
                .GroupBy(e => e.Relationship, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count())
        };
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        _nodes.Clear();
        _edges.Clear();
        _nodeEdges.Clear();
        _modifyLock.Dispose();

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Edge direction for graph queries.
/// </summary>
public enum EdgeDirection
{
    /// <summary>Outgoing edges from the node.</summary>
    Outgoing,

    /// <summary>Incoming edges to the node.</summary>
    Incoming,

    /// <summary>Both incoming and outgoing edges.</summary>
    Both
}

/// <summary>
/// A node in the knowledge graph.
/// </summary>
public sealed class KnowledgeNode
{
    /// <summary>Gets or sets the node ID.</summary>
    public required string Id { get; init; }

    /// <summary>Gets or sets node labels.</summary>
    public IReadOnlyList<string> Labels { get; init; } = Array.Empty<string>();

    /// <summary>Gets or sets node properties.</summary>
    public Dictionary<string, object> Properties { get; init; } = new();

    /// <summary>Gets or sets the source domain.</summary>
    public string? Domain { get; init; }

    /// <summary>Gets or sets creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// An edge in the knowledge graph.
/// </summary>
public sealed class KnowledgeEdge
{
    /// <summary>Gets or sets the edge ID.</summary>
    public required string Id { get; init; }

    /// <summary>Gets or sets the source node ID.</summary>
    public required string FromNodeId { get; init; }

    /// <summary>Gets or sets the target node ID.</summary>
    public required string ToNodeId { get; init; }

    /// <summary>Gets or sets the relationship type.</summary>
    public required string Relationship { get; init; }

    /// <summary>Gets or sets edge properties.</summary>
    public Dictionary<string, object> Properties { get; init; } = new();

    /// <summary>Gets or sets the confidence score.</summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>Gets or sets creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Knowledge graph statistics.
/// </summary>
public sealed class KnowledgeGraphStats
{
    /// <summary>Gets or sets the node count.</summary>
    public int NodeCount { get; init; }

    /// <summary>Gets or sets the edge count.</summary>
    public int EdgeCount { get; init; }

    /// <summary>Gets or sets label counts.</summary>
    public Dictionary<string, int> LabelCounts { get; init; } = new();

    /// <summary>Gets or sets relationship counts.</summary>
    public Dictionary<string, int> RelationshipCounts { get; init; } = new();
}

/// <summary>
/// Configuration options for UnifiedKnowledgeGraph.
/// </summary>
public sealed class UnifiedKnowledgeGraphOptions
{
    /// <summary>Gets or sets whether to enable automatic relationship discovery.</summary>
    public bool EnableAutoDiscovery { get; set; } = true;
}

/// <summary>
/// Discovers relationships between knowledge items.
/// </summary>
/// <remarks>
/// <para>
/// The RelationshipDiscovery component:
/// </para>
/// <list type="bullet">
///   <item>Analyzes content for implicit relationships</item>
///   <item>Uses AI to infer semantic connections</item>
///   <item>Maintains relationship confidence scores</item>
///   <item>Updates graph with discovered relationships</item>
/// </list>
/// </remarks>
public sealed class RelationshipDiscovery
{
    private readonly RelationshipDiscoveryOptions _options;
    private readonly UnifiedKnowledgeGraph _graph;
    private readonly Func<string, string, CancellationToken, Task<string?>>? _messageBusPublisher;

    /// <summary>
    /// Creates a new RelationshipDiscovery instance.
    /// </summary>
    /// <param name="graph">The knowledge graph to update.</param>
    /// <param name="options">Discovery configuration options.</param>
    /// <param name="messageBusPublisher">Optional message bus publisher for AI operations.</param>
    public RelationshipDiscovery(
        UnifiedKnowledgeGraph graph,
        RelationshipDiscoveryOptions? options = null,
        Func<string, string, CancellationToken, Task<string?>>? messageBusPublisher = null)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _options = options ?? new RelationshipDiscoveryOptions();
        _messageBusPublisher = messageBusPublisher;
    }

    /// <summary>
    /// Discovers relationships between two nodes.
    /// </summary>
    /// <param name="nodeA">First node.</param>
    /// <param name="nodeB">Second node.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Discovered relationships.</returns>
    public async Task<IReadOnlyList<DiscoveredRelationship>> DiscoverAsync(
        KnowledgeNode nodeA,
        KnowledgeNode nodeB,
        CancellationToken ct = default)
    {
        var relationships = new List<DiscoveredRelationship>();

        // Check for common labels
        var commonLabels = nodeA.Labels.Intersect(nodeB.Labels, StringComparer.OrdinalIgnoreCase).ToList();
        if (commonLabels.Count > 0)
        {
            relationships.Add(new DiscoveredRelationship
            {
                FromNodeId = nodeA.Id,
                ToNodeId = nodeB.Id,
                Relationship = "SHARES_LABEL",
                Confidence = 0.7,
                Metadata = new Dictionary<string, object> { ["commonLabels"] = commonLabels }
            });
        }

        // Check for same domain
        if (!string.IsNullOrEmpty(nodeA.Domain) && nodeA.Domain == nodeB.Domain)
        {
            relationships.Add(new DiscoveredRelationship
            {
                FromNodeId = nodeA.Id,
                ToNodeId = nodeB.Id,
                Relationship = "SAME_DOMAIN",
                Confidence = 0.6,
                Metadata = new Dictionary<string, object> { ["domain"] = nodeA.Domain }
            });
        }

        // Use AI for semantic relationship discovery
        if (_messageBusPublisher != null && _options.EnableAIDiscovery)
        {
            try
            {
                var aiRelationships = await DiscoverWithAIAsync(nodeA, nodeB, ct).ConfigureAwait(false);
                relationships.AddRange(aiRelationships);
            }
            catch
            {
                Debug.WriteLine($"Caught exception in NaturalLanguageProcessing.cs");
                // Continue with rule-based relationships only
            }
        }

        return relationships;
    }

    /// <summary>
    /// Discovers and adds relationships to the graph.
    /// </summary>
    /// <param name="nodeA">First node.</param>
    /// <param name="nodeB">Second node.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of relationships added.</returns>
    public async Task<int> DiscoverAndAddAsync(KnowledgeNode nodeA, KnowledgeNode nodeB, CancellationToken ct = default)
    {
        var relationships = await DiscoverAsync(nodeA, nodeB, ct).ConfigureAwait(false);
        var count = 0;

        foreach (var rel in relationships.Where(r => r.Confidence >= _options.MinConfidence))
        {
            var edge = new KnowledgeEdge
            {
                Id = $"{rel.FromNodeId}-{rel.Relationship}-{rel.ToNodeId}",
                FromNodeId = rel.FromNodeId,
                ToNodeId = rel.ToNodeId,
                Relationship = rel.Relationship,
                Confidence = rel.Confidence,
                Properties = rel.Metadata
            };

            await _graph.AddEdgeAsync(edge, ct).ConfigureAwait(false);
            count++;
        }

        return count;
    }

    private async Task<IReadOnlyList<DiscoveredRelationship>> DiscoverWithAIAsync(
        KnowledgeNode nodeA,
        KnowledgeNode nodeB,
        CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            nodeA = new { nodeA.Id, nodeA.Labels, nodeA.Properties },
            nodeB = new { nodeB.Id, nodeB.Labels, nodeB.Properties }
        });

        var response = await _messageBusPublisher!(NLPTopics.DiscoverRelationships, payload, ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(response))
        {
            var result = JsonSerializer.Deserialize<List<DiscoveredRelationship>>(response);
            if (result != null) return result;
        }

        return Array.Empty<DiscoveredRelationship>();
    }
}

/// <summary>
/// A discovered relationship between nodes.
/// </summary>
public sealed class DiscoveredRelationship
{
    /// <summary>Gets or sets the source node ID.</summary>
    public required string FromNodeId { get; init; }

    /// <summary>Gets or sets the target node ID.</summary>
    public required string ToNodeId { get; init; }

    /// <summary>Gets or sets the relationship type.</summary>
    public required string Relationship { get; init; }

    /// <summary>Gets or sets the confidence score.</summary>
    public double Confidence { get; init; }

    /// <summary>Gets or sets additional metadata.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Configuration options for RelationshipDiscovery.
/// </summary>
public sealed class RelationshipDiscoveryOptions
{
    /// <summary>Gets or sets whether to enable AI-powered discovery.</summary>
    public bool EnableAIDiscovery { get; set; } = true;

    /// <summary>Gets or sets the minimum confidence for adding relationships.</summary>
    public double MinConfidence { get; set; } = 0.5;
}

/// <summary>
/// Provides natural language queries over the knowledge graph.
/// </summary>
/// <remarks>
/// <para>
/// The GraphQuery component enables:
/// </para>
/// <list type="bullet">
///   <item>Natural language to graph query translation</item>
///   <item>Pattern matching and traversal</item>
///   <item>Result aggregation and formatting</item>
///   <item>Query optimization</item>
/// </list>
/// </remarks>
public sealed class GraphQuery
{
    private readonly GraphQueryOptions _options;
    private readonly UnifiedKnowledgeGraph _graph;
    private readonly QueryParser? _queryParser;
    private readonly Func<string, string, CancellationToken, Task<string?>>? _messageBusPublisher;

    /// <summary>
    /// Creates a new GraphQuery instance.
    /// </summary>
    /// <param name="graph">The knowledge graph to query.</param>
    /// <param name="queryParser">Optional query parser.</param>
    /// <param name="options">Query configuration options.</param>
    /// <param name="messageBusPublisher">Optional message bus publisher for AI operations.</param>
    public GraphQuery(
        UnifiedKnowledgeGraph graph,
        QueryParser? queryParser = null,
        GraphQueryOptions? options = null,
        Func<string, string, CancellationToken, Task<string?>>? messageBusPublisher = null)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _queryParser = queryParser;
        _options = options ?? new GraphQueryOptions();
        _messageBusPublisher = messageBusPublisher;
    }

    /// <summary>
    /// Executes a natural language query against the graph.
    /// </summary>
    /// <param name="query">Natural language query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Query results.</returns>
    public async Task<GraphQueryResult> QueryAsync(string query, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(query);

        var sw = Stopwatch.StartNew();

        // Parse the query
        ParsedQuery? parsedQuery = null;
        if (_queryParser != null)
        {
            parsedQuery = await _queryParser.ParseAsync(query, ct).ConfigureAwait(false);
        }

        // Convert to graph query using AI if available
        GraphQueryPlan? queryPlan = null;
        if (_messageBusPublisher != null)
        {
            try
            {
                queryPlan = await TranslateToGraphQueryAsync(query, parsedQuery, ct).ConfigureAwait(false);
            }
            catch
            {
                Debug.WriteLine($"Caught exception in NaturalLanguageProcessing.cs");
                // Fall back to keyword-based search
            }
        }

        // Execute query
        IReadOnlyList<KnowledgeNode> nodes;
        IReadOnlyList<KnowledgeEdge> edges;

        if (queryPlan != null)
        {
            (nodes, edges) = ExecuteQueryPlan(queryPlan);
        }
        else
        {
            // Keyword-based fallback
            var keywords = parsedQuery?.Keywords ?? query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            nodes = FindNodesByKeywords(keywords);
            edges = GetRelatedEdges(nodes);
        }

        sw.Stop();

        return new GraphQueryResult
        {
            Query = query,
            Nodes = nodes,
            Edges = edges,
            TotalNodes = nodes.Count,
            TotalEdges = edges.Count,
            QueryDuration = sw.Elapsed
        };
    }

    private async Task<GraphQueryPlan?> TranslateToGraphQueryAsync(string query, ParsedQuery? parsedQuery, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            query,
            parsedQuery = parsedQuery != null ? new
            {
                parsedQuery.Keywords,
                parsedQuery.Intent,
                entities = parsedQuery.Entities.Select(e => new { e.Value, Type = e.Type.ToString() })
            } : null
        });

        var response = await _messageBusPublisher!(NLPTopics.GraphQuery, payload, ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(response))
        {
            return JsonSerializer.Deserialize<GraphQueryPlan>(response);
        }

        return null;
    }

    private (IReadOnlyList<KnowledgeNode>, IReadOnlyList<KnowledgeEdge>) ExecuteQueryPlan(GraphQueryPlan plan)
    {
        var nodes = new List<KnowledgeNode>();
        var edges = new List<KnowledgeEdge>();

        // Execute label search
        foreach (var label in plan.Labels ?? Array.Empty<string>())
        {
            nodes.AddRange(_graph.FindNodesByLabel(label));
        }

        // Get related edges
        foreach (var node in nodes)
        {
            edges.AddRange(_graph.GetEdges(node.Id));
        }

        // Filter by relationships
        if (plan.Relationships?.Length > 0)
        {
            edges = edges
                .Where(e => plan.Relationships.Contains(e.Relationship, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        return (nodes.DistinctBy(n => n.Id).ToList(), edges.DistinctBy(e => e.Id).ToList());
    }

    private IReadOnlyList<KnowledgeNode> FindNodesByKeywords(IEnumerable<string> keywords)
    {
        var nodes = new List<KnowledgeNode>();
        var keywordSet = keywords.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var label in keywordSet)
        {
            nodes.AddRange(_graph.FindNodesByLabel(label));
        }

        return nodes.DistinctBy(n => n.Id).ToList();
    }

    private IReadOnlyList<KnowledgeEdge> GetRelatedEdges(IReadOnlyList<KnowledgeNode> nodes)
    {
        var edges = new List<KnowledgeEdge>();

        foreach (var node in nodes)
        {
            edges.AddRange(_graph.GetEdges(node.Id));
        }

        return edges.DistinctBy(e => e.Id).ToList();
    }
}

/// <summary>
/// Graph query execution plan.
/// </summary>
public sealed class GraphQueryPlan
{
    /// <summary>Gets or sets labels to search.</summary>
    public string[]? Labels { get; set; }

    /// <summary>Gets or sets relationships to filter.</summary>
    public string[]? Relationships { get; set; }

    /// <summary>Gets or sets property filters.</summary>
    public Dictionary<string, object>? PropertyFilters { get; set; }

    /// <summary>Gets or sets the maximum depth for traversal.</summary>
    public int MaxDepth { get; set; } = 3;
}

/// <summary>
/// Graph query result.
/// </summary>
public sealed class GraphQueryResult
{
    /// <summary>Gets or sets the original query.</summary>
    public required string Query { get; init; }

    /// <summary>Gets or sets matching nodes.</summary>
    public IReadOnlyList<KnowledgeNode> Nodes { get; init; } = Array.Empty<KnowledgeNode>();

    /// <summary>Gets or sets related edges.</summary>
    public IReadOnlyList<KnowledgeEdge> Edges { get; init; } = Array.Empty<KnowledgeEdge>();

    /// <summary>Gets or sets total node count.</summary>
    public int TotalNodes { get; init; }

    /// <summary>Gets or sets total edge count.</summary>
    public int TotalEdges { get; init; }

    /// <summary>Gets or sets query duration.</summary>
    public TimeSpan QueryDuration { get; init; }
}

/// <summary>
/// Configuration options for GraphQuery.
/// </summary>
public sealed class GraphQueryOptions
{
    /// <summary>Gets or sets the default maximum depth.</summary>
    public int DefaultMaxDepth { get; set; } = 3;

    /// <summary>Gets or sets the maximum results.</summary>
    public int MaxResults { get; set; } = 100;
}

#endregion
