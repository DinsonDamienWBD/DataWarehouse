// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.SDK.AI;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using DataWarehouse.Plugins.AIAgents.Capabilities;
using IExtendedAIProvider = DataWarehouse.Plugins.AIAgents.IExtendedAIProvider;

namespace DataWarehouse.Plugins.AIAgents.Capabilities.NLP;

/// <summary>
/// Engine for parsing natural language queries into structured command intents.
/// Supports both pattern-based parsing (fallback) and AI-enhanced parsing.
/// </summary>
/// <remarks>
/// <para>
/// The QueryParsingEngine provides the core NLP functionality for:
/// </para>
/// <list type="bullet">
/// <item><description>Converting natural language to structured queries</description></item>
/// <item><description>Intent detection with confidence scoring</description></item>
/// <item><description>Entity extraction from queries</description></item>
/// <item><description>Command extraction from text</description></item>
/// <item><description>Learning from user corrections</description></item>
/// </list>
/// <para>
/// When an AI provider is available, parsing is enhanced with LLM capabilities.
/// When no provider is available, the engine falls back to comprehensive pattern matching.
/// </para>
/// </remarks>
public sealed class QueryParsingEngine : IDisposable
{
    #region Intent Patterns

    /// <summary>
    /// Intent patterns for rule-based fallback parsing.
    /// </summary>
    private static readonly List<IntentPattern> _intentPatterns = new()
    {
        // Storage commands
        new(@"(?:list|show|get)\s+(?:all\s+)?(?:storage\s+)?pools?", "storage.list", 0.85),
        new(@"create\s+(?:a\s+)?(?:new\s+)?(?:storage\s+)?pool\s+(?:named?\s+)?['""]?(\w+)['""]?", "storage.create", 0.9, new[] { "name" }),
        new(@"delete\s+(?:the\s+)?(?:storage\s+)?pool\s+['""]?(\w+)['""]?", "storage.delete", 0.9, new[] { "id" }),
        new(@"(?:show|get)\s+(?:info|details|information)\s+(?:for|about|on)\s+(?:pool\s+)?['""]?(\w+)['""]?", "storage.info", 0.85, new[] { "id" }),
        new(@"(?:show|get)\s+storage\s+(?:stats|statistics)", "storage.stats", 0.9),

        // Backup commands
        new(@"(?:create|make|run)\s+(?:a\s+)?(?:new\s+)?backup\s+(?:named?\s+)?['""]?(\w+)['""]?(?:\s+to\s+['""]?(\S+)['""]?)?", "backup.create", 0.9, new[] { "name", "destination" }),
        new(@"(?:backup)\s+(?:my\s+)?(?:database|data|files?)(?:\s+to\s+['""]?(\S+)['""]?)?(?:\s+with\s+encryption)?", "backup.create", 0.85, new[] { "destination" }),
        new(@"(?:list|show)\s+(?:all\s+)?backups?", "backup.list", 0.9),
        new(@"restore\s+(?:from\s+)?(?:backup\s+)?['""]?(\S+)['""]?", "backup.restore", 0.9, new[] { "id" }),
        new(@"verify\s+(?:backup\s+)?['""]?(\S+)['""]?", "backup.verify", 0.85, new[] { "id" }),
        new(@"delete\s+(?:the\s+)?backup\s+['""]?(\S+)['""]?", "backup.delete", 0.9, new[] { "id" }),

        // Plugin commands
        new(@"(?:list|show)\s+(?:all\s+)?plugins?", "plugin.list", 0.9),
        new(@"(?:enable|activate)\s+(?:the\s+)?plugin\s+['""]?(\w[\w-]*)['""]?", "plugin.enable", 0.9, new[] { "id" }),
        new(@"(?:disable|deactivate)\s+(?:the\s+)?plugin\s+['""]?(\w[\w-]*)['""]?", "plugin.disable", 0.9, new[] { "id" }),
        new(@"reload\s+(?:the\s+)?plugin\s+['""]?(\w[\w-]*)['""]?", "plugin.reload", 0.9, new[] { "id" }),

        // Health commands
        new(@"(?:show|check|get)\s+(?:system\s+)?(?:health|status)", "health.status", 0.9),
        new(@"(?:show|get)\s+(?:system\s+)?metrics", "health.metrics", 0.85),
        new(@"(?:show|list)\s+(?:all\s+)?alerts?", "health.alerts", 0.85),
        new(@"(?:run|perform)\s+(?:a\s+)?health\s+check", "health.check", 0.9),

        // RAID commands
        new(@"(?:list|show)\s+(?:all\s+)?raid(?:\s+(?:arrays?|configs?|configurations?))?", "raid.list", 0.9),
        new(@"create\s+(?:a\s+)?(?:new\s+)?raid\s+(?:array\s+)?(?:named?\s+)?['""]?(\w+)['""]?(?:\s+(?:level|type)\s+(\d+))?", "raid.create", 0.85, new[] { "name", "level" }),
        new(@"(?:show|get)\s+raid\s+status\s+(?:for\s+)?['""]?(\S+)['""]?", "raid.status", 0.85, new[] { "id" }),
        new(@"rebuild\s+(?:the\s+)?raid\s+(?:array\s+)?['""]?(\S+)['""]?", "raid.rebuild", 0.85, new[] { "id" }),

        // Config commands
        new(@"(?:show|get|display)\s+(?:the\s+)?(?:current\s+)?config(?:uration)?", "config.show", 0.9),
        new(@"set\s+(?:config|configuration)\s+['""]?(\S+)['""]?\s+(?:to\s+)?['""]?(\S+)['""]?", "config.set", 0.9, new[] { "key", "value" }),
        new(@"get\s+(?:config|configuration)\s+['""]?(\S+)['""]?", "config.get", 0.85, new[] { "key" }),

        // Server commands
        new(@"start\s+(?:the\s+)?server(?:\s+on\s+port\s+(\d+))?", "server.start", 0.9, new[] { "port" }),
        new(@"stop\s+(?:the\s+)?server", "server.stop", 0.9),
        new(@"(?:show|get)\s+server\s+(?:status|info)", "server.status", 0.85),

        // Search commands
        new(@"(?:find|search|look\s+for)\s+(?:files?\s+)?(?:named?\s+)?['""]?([^'""]+)['""]?", "storage.search", 0.8, new[] { "query" }),
        new(@"(?:search|find)\s+(?:files?\s+)?(?:containing|with\s+content)\s+['""]?([^'""]+)['""]?", "storage.search", 0.8, new[] { "content" }),
        new(@"(?:find|show)\s+(?:files?\s+)?(?:larger|bigger)\s+than\s+(\S+)", "storage.search", 0.75, new[] { "minSize" }),
        new(@"(?:find|show)\s+(?:files?\s+)?(?:from|since|modified\s+(?:in\s+the\s+)?last)\s+(\S+)", "storage.search", 0.75, new[] { "since" }),

        // Help commands
        new(@"^help(?:\s+(\S+))?$", "help", 0.95, new[] { "command" }),
        new(@"(?:what\s+can\s+you\s+do|how\s+do\s+I|show\s+commands)", "help", 0.8),

        // Diagnostic queries
        new(@"why\s+is\s+(?:my\s+)?(\w+)\s+failing", "health.diagnose", 0.7, new[] { "component" }),
        new(@"what(?:'s|\s+is)\s+(?:the\s+)?(?:status|state)\s+of\s+(\w+)", "health.status", 0.75, new[] { "component" }),
        new(@"how\s+much\s+(?:storage|space|disk)\s+(?:am\s+I\s+using|is\s+used)", "storage.stats", 0.85),
    };

    #endregion

    #region Entity Patterns

    /// <summary>
    /// Entity extraction patterns.
    /// </summary>
    private static readonly List<EntityPattern> _entityPatterns = new()
    {
        // Date/Time patterns
        new(@"(?:from|since|after)\s+(\d{4}-\d{2}-\d{2}|\d{1,2}/\d{1,2}/\d{2,4})", "DateTime", 1),
        new(@"(?:to|until|before)\s+(\d{4}-\d{2}-\d{2}|\d{1,2}/\d{1,2}/\d{2,4})", "DateTime", 1),
        new(@"(last\s+(?:week|month|year|day|\d+\s+days?))", "RelativeTime", 1),
        new(@"(yesterday|today|this\s+(?:week|month|year))", "RelativeTime", 1),
        new(@"(last\s+hour|\d+\s+hours?\s+ago)", "RelativeTime", 1),

        // File patterns
        new(@"(?:file(?:s)?(?:\s+(?:named?|called))?\s+)['""]?([^'""'\s,]+)['""]?", "FileName", 1),
        new(@"(?:in\s+(?:folder|directory|path)\s+)['""]?([^\s'""]+)['""]?", "FilePath", 1),
        new(@"\.(\w{2,5})\s+files?", "FileExtension", 1),

        // Size patterns
        new(@"(\d+(?:\.\d+)?)\s*(?:GB|MB|KB|TB|bytes?)", "FileSize", 0),
        new(@"(?:larger|bigger|greater)\s+than\s+(\d+(?:\.\d+)?)\s*(?:GB|MB|KB|TB)?", "SizeComparison", 0),
        new(@"(?:smaller|less)\s+than\s+(\d+(?:\.\d+)?)\s*(?:GB|MB|KB|TB)?", "SizeComparison", 0),

        // Metadata patterns
        new(@"(?:tag(?:ged)?(?:\s+(?:with|as))?\s+)['""]?([^'""'\s,]+)['""]?", "Tag", 1),
        new(@"(?:owned?\s+by|owner\s+(?:is)?)\s+['""]?(\w+)['""]?", "Owner", 1),

        // Quantity patterns
        new(@"(?:top|first|last)\s+(\d+)", "Count", 1),
        new(@"(\d+)\s+(?:files?|items?|results?)", "Count", 1),

        // Storage patterns
        new(@"(?:in\s+(?:pool|storage)\s+)['""]?([^'""'\s]+)['""]?", "StoragePool", 1),
        new(@"(?:in\s+bucket\s+)['""]?([^'""'\s]+)['""]?", "Bucket", 1),
    };

    #endregion

    #region Command Descriptions

    /// <summary>
    /// Available commands with descriptions for AI context.
    /// </summary>
    private static readonly Dictionary<string, string> _commandDescriptions = new()
    {
        ["storage.list"] = "List all storage pools",
        ["storage.create"] = "Create a new storage pool with name",
        ["storage.delete"] = "Delete a storage pool by ID",
        ["storage.info"] = "Show detailed information about a storage pool",
        ["storage.stats"] = "Show storage statistics and usage",
        ["storage.search"] = "Search for files by name, content, or metadata",
        ["backup.create"] = "Create a backup with optional destination and encryption",
        ["backup.list"] = "List all backups",
        ["backup.restore"] = "Restore from a backup by ID",
        ["backup.verify"] = "Verify backup integrity",
        ["backup.delete"] = "Delete a backup by ID",
        ["plugin.list"] = "List all plugins",
        ["plugin.enable"] = "Enable a plugin by ID",
        ["plugin.disable"] = "Disable a plugin by ID",
        ["plugin.reload"] = "Reload a plugin by ID",
        ["health.status"] = "Show system health status",
        ["health.metrics"] = "Show system metrics",
        ["health.alerts"] = "Show active alerts",
        ["health.check"] = "Run a health check",
        ["health.diagnose"] = "Diagnose issues with a component",
        ["raid.list"] = "List RAID configurations",
        ["raid.create"] = "Create a new RAID array",
        ["raid.status"] = "Show RAID array status",
        ["raid.rebuild"] = "Start RAID rebuild",
        ["config.show"] = "Show current configuration",
        ["config.set"] = "Set a configuration value",
        ["config.get"] = "Get a configuration value",
        ["server.start"] = "Start the server on optional port",
        ["server.stop"] = "Stop the server",
        ["server.status"] = "Show server status",
        ["help"] = "Show help for a command"
    };

    #endregion

    private readonly QueryParsingConfig _config;
    private readonly CLILearningStore _learningStore;
    private readonly ConcurrentDictionary<string, string> _synonyms;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryParsingEngine"/> class.
    /// </summary>
    /// <param name="config">Optional configuration for query parsing.</param>
    public QueryParsingEngine(QueryParsingConfig? config = null)
    {
        _config = config ?? new QueryParsingConfig();
        _learningStore = new CLILearningStore(_config.LearningStorePath);
        _synonyms = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        InitializeDefaultSynonyms();
    }

    #region Pattern-Based Parsing

    /// <summary>
    /// Parses a natural language query using pattern matching.
    /// </summary>
    /// <param name="query">The natural language query to parse.</param>
    /// <param name="context">Optional carry-over context from previous turns.</param>
    /// <returns>The parsed command intent.</returns>
    public NLPCommandIntent ParseWithPatterns(string query, Dictionary<string, object?>? context = null)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return CreateEmptyIntent(query ?? string.Empty);
        }

        var normalizedQuery = NormalizeQuery(query);
        var resolvedQuery = ResolveSynonyms(normalizedQuery);

        // Check learned patterns first
        var learnedMatch = _learningStore.FindBestMatch(resolvedQuery);
        if (learnedMatch != null && learnedMatch.Confidence > _config.MinLearnedConfidence)
        {
            return new NLPCommandIntent
            {
                CommandName = learnedMatch.CommandName,
                Parameters = new Dictionary<string, object?>(learnedMatch.Parameters),
                OriginalInput = query,
                Confidence = learnedMatch.Confidence,
                Explanation = $"Matched learned pattern: {learnedMatch.InputPhrase}",
                ProcessedByAI = false,
                Entities = ExtractEntitiesFromText(query),
                SuggestedFollowUps = GetSuggestedFollowUps(learnedMatch.CommandName)
            };
        }

        // Try intent patterns
        foreach (var pattern in _intentPatterns)
        {
            var match = pattern.Regex.Match(resolvedQuery);
            if (match.Success)
            {
                var parameters = ExtractParameters(match, pattern.ParameterNames);
                ApplyContextParameters(parameters, context);
                ApplyFlagParameters(parameters, normalizedQuery);

                var confidence = CalculateConfidence(match, normalizedQuery, pattern.BaseConfidence);

                return new NLPCommandIntent
                {
                    CommandName = pattern.CommandName,
                    Parameters = parameters,
                    OriginalInput = query,
                    Confidence = confidence,
                    Explanation = GenerateExplanation(pattern.CommandName, parameters),
                    ProcessedByAI = false,
                    Entities = ExtractEntitiesFromText(query),
                    SuggestedFollowUps = GetSuggestedFollowUps(pattern.CommandName)
                };
            }
        }

        // No pattern matched
        return CreateUnknownIntent(query);
    }

    /// <summary>
    /// Detects intent from input using pattern matching.
    /// </summary>
    /// <param name="input">The input text to analyze.</param>
    /// <returns>The detected intent.</returns>
    public NLPDetectedIntent DetectIntentWithPatterns(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new NLPDetectedIntent
            {
                IntentType = "Unknown",
                Confidence = 0,
                OriginalInput = input ?? string.Empty
            };
        }

        var normalizedInput = NormalizeQuery(input);
        var resolvedInput = ResolveSynonyms(normalizedInput);
        var isFollowUp = IsFollowUpQuery(resolvedInput);

        foreach (var pattern in _intentPatterns)
        {
            var match = pattern.Regex.Match(resolvedInput);
            if (match.Success)
            {
                var intentType = pattern.CommandName.Split('.').First();
                var subType = pattern.CommandName.Contains('.')
                    ? pattern.CommandName.Split('.').Last()
                    : null;

                return new NLPDetectedIntent
                {
                    IntentType = intentType,
                    SubType = subType,
                    Confidence = pattern.BaseConfidence,
                    OriginalInput = input,
                    Entities = ExtractEntitiesFromText(input),
                    Keywords = ExtractKeywords(input),
                    IsFollowUp = isFollowUp,
                    Language = DetectLanguage(input)
                };
            }
        }

        return new NLPDetectedIntent
        {
            IntentType = "Unknown",
            Confidence = 0.3,
            OriginalInput = input,
            Entities = ExtractEntitiesFromText(input),
            Keywords = ExtractKeywords(input),
            IsFollowUp = isFollowUp,
            Language = DetectLanguage(input)
        };
    }

    /// <summary>
    /// Extracts commands from text using pattern matching.
    /// </summary>
    /// <param name="text">The text to extract commands from.</param>
    /// <returns>The extracted commands.</returns>
    public NLPExtractedCommands ExtractCommandsWithPatterns(string text)
    {
        var commands = new List<NLPCommandIntent>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(text))
        {
            return new NLPExtractedCommands
            {
                OriginalText = text ?? string.Empty,
                Commands = commands,
                ProcessedByAI = false,
                Warnings = warnings
            };
        }

        // Split by common separators
        var sentences = SplitIntoSentences(text);

        foreach (var sentence in sentences)
        {
            var intent = ParseWithPatterns(sentence.Trim(), null);
            if (intent.CommandName != "help" && intent.Confidence > 0.5)
            {
                commands.Add(intent);
            }
        }

        if (commands.Count == 0)
        {
            // Try parsing the whole text as a single command
            var wholeIntent = ParseWithPatterns(text, null);
            if (wholeIntent.Confidence > 0.3)
            {
                commands.Add(wholeIntent);
            }
            else
            {
                warnings.Add("Could not extract any clear commands from the text.");
            }
        }

        return new NLPExtractedCommands
        {
            OriginalText = text,
            Commands = commands,
            ProcessedByAI = false,
            Warnings = warnings
        };
    }

    #endregion

    #region AI-Enhanced Parsing

    /// <summary>
    /// Parses a natural language query using AI enhancement.
    /// </summary>
    /// <param name="query">The natural language query to parse.</param>
    /// <param name="provider">The AI provider to use.</param>
    /// <param name="context">Optional carry-over context from previous turns.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A tuple of the parsed command intent and token usage.</returns>
    public async Task<(NLPCommandIntent Result, TokenUsage? Usage)> ParseWithAIAsync(
        string query,
        IExtendedAIProvider provider,
        Dictionary<string, object?>? context = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        if (string.IsNullOrWhiteSpace(query))
        {
            return (CreateEmptyIntent(query ?? string.Empty), null);
        }

        // First try pattern matching - if high confidence, skip AI
        var patternResult = ParseWithPatterns(query, context);
        if (patternResult.Confidence >= _config.AIConfidenceThreshold)
        {
            return (patternResult, null);
        }

        try
        {
            var systemPrompt = BuildParsingSystemPrompt();
            var contextInfo = context != null && context.Count > 0
                ? $"\n\nContext from previous turn:\n{JsonSerializer.Serialize(context)}"
                : "";
            var userPrompt = $"Parse this command: \"{query}\"{contextInfo}\n\nRespond with JSON only.";

            var request = new AIRequest
            {
                Prompt = userPrompt,
                SystemMessage = systemPrompt,
                MaxTokens = _config.MaxParsingTokens,
                Temperature = _config.ParsingTemperature
            };

            var response = await provider.CompleteAsync(request, ct);

            if (!response.Success || string.IsNullOrEmpty(response.Content))
            {
                return (patternResult, ConvertUsage(response.Usage));
            }

            var aiIntent = ParseAIResponse(response.Content, query, context);
            if (aiIntent != null && aiIntent.Confidence > patternResult.Confidence)
            {
                _learningStore.RecordSuccess(query, aiIntent.CommandName, aiIntent.Parameters);
                return (aiIntent, ConvertUsage(response.Usage));
            }

            return (patternResult, ConvertUsage(response.Usage));
        }
        catch
        {
            // AI failed - return pattern result
            return (patternResult, null);
        }
    }

    /// <summary>
    /// Detects intent from input using AI enhancement.
    /// </summary>
    /// <param name="input">The input text to analyze.</param>
    /// <param name="provider">The AI provider to use.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A tuple of the detected intent and token usage.</returns>
    public async Task<(NLPDetectedIntent Result, TokenUsage? Usage)> DetectIntentWithAIAsync(
        string input,
        IExtendedAIProvider provider,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var patternResult = DetectIntentWithPatterns(input);

        if (patternResult.Confidence >= _config.AIConfidenceThreshold)
        {
            return (patternResult, null);
        }

        try
        {
            var systemPrompt = @"You are an intent classifier. Analyze the user's input and identify:
1. Primary intent type (storage, backup, health, plugin, config, help, unknown)
2. Sub-intent (create, delete, list, show, search, etc.)
3. Confidence (0.0-1.0)
4. Key entities mentioned

Respond with JSON only:
{
    ""intentType"": ""storage"",
    ""subType"": ""list"",
    ""confidence"": 0.9,
    ""entities"": [{""type"": ""StoragePool"", ""value"": ""mypool""}],
    ""keywords"": [""list"", ""pools""],
    ""isFollowUp"": false
}";

            var request = new AIRequest
            {
                Prompt = $"Classify intent: \"{input}\"",
                SystemMessage = systemPrompt,
                MaxTokens = 300,
                Temperature = 0.1f
            };

            var response = await provider.CompleteAsync(request, ct);

            if (response.Success && !string.IsNullOrEmpty(response.Content))
            {
                var aiResult = ParseIntentResponse(response.Content, input);
                if (aiResult != null && aiResult.Confidence > patternResult.Confidence)
                {
                    return (aiResult, ConvertUsage(response.Usage));
                }
            }

            return (patternResult, ConvertUsage(response.Usage));
        }
        catch
        {
            return (patternResult, null);
        }
    }

    /// <summary>
    /// Extracts commands from text using AI enhancement.
    /// </summary>
    /// <param name="text">The text to extract commands from.</param>
    /// <param name="provider">The AI provider to use.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A tuple of the extracted commands and token usage.</returns>
    public async Task<(NLPExtractedCommands Result, TokenUsage? Usage)> ExtractCommandsWithAIAsync(
        string text,
        IExtendedAIProvider provider,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var patternResult = ExtractCommandsWithPatterns(text);

        if (patternResult.Commands.Count > 0 &&
            patternResult.Commands.All(c => c.Confidence >= _config.AIConfidenceThreshold))
        {
            return (patternResult, null);
        }

        try
        {
            var commandList = string.Join("\n", _commandDescriptions.Select(kv => $"- {kv.Key}: {kv.Value}"));
            var systemPrompt = $@"Extract all commands from the user's text. Available commands:
{commandList}

Respond with JSON array:
[
    {{""command"": ""backup.create"", ""parameters"": {{""name"": ""daily""}}, ""confidence"": 0.9}}
]";

            var request = new AIRequest
            {
                Prompt = $"Extract commands from: \"{text}\"",
                SystemMessage = systemPrompt,
                MaxTokens = 500,
                Temperature = 0.1f
            };

            var response = await provider.CompleteAsync(request, ct);

            if (response.Success && !string.IsNullOrEmpty(response.Content))
            {
                var aiResult = ParseCommandsResponse(response.Content, text);
                if (aiResult != null && aiResult.Commands.Count > 0)
                {
                    aiResult = aiResult with { ProcessedByAI = true };
                    return (aiResult, ConvertUsage(response.Usage));
                }
            }

            return (patternResult, ConvertUsage(response.Usage));
        }
        catch
        {
            return (patternResult, null);
        }
    }

    #endregion

    #region Completions and Learning

    /// <summary>
    /// Gets completion suggestions for partial input.
    /// </summary>
    /// <param name="partialInput">The partial input to complete.</param>
    /// <param name="maxSuggestions">Maximum number of suggestions.</param>
    /// <returns>List of completion suggestions.</returns>
    public IEnumerable<string> GetCompletions(string partialInput, int maxSuggestions = 10)
    {
        if (string.IsNullOrWhiteSpace(partialInput))
        {
            return GetDefaultSuggestions().Take(maxSuggestions);
        }

        var suggestions = new List<string>();
        var normalized = partialInput.ToLowerInvariant();

        // Add context-aware suggestions
        if (normalized.StartsWith("show") || normalized.StartsWith("list") || normalized.StartsWith("get"))
        {
            suggestions.AddRange(new[]
            {
                "show storage pools",
                "show health status",
                "show plugins",
                "show backups",
                "show raid status",
                "show config"
            });
        }
        else if (normalized.StartsWith("create") || normalized.StartsWith("make"))
        {
            suggestions.AddRange(new[]
            {
                "create backup",
                "create storage pool",
                "create raid array"
            });
        }
        else if (normalized.StartsWith("backup"))
        {
            suggestions.AddRange(new[]
            {
                "backup my database",
                "backup my database with encryption",
                "backup to S3"
            });
        }
        else if (normalized.StartsWith("delete") || normalized.StartsWith("remove"))
        {
            suggestions.AddRange(new[]
            {
                "delete backup",
                "delete storage pool"
            });
        }
        else if (normalized.StartsWith("find") || normalized.StartsWith("search"))
        {
            suggestions.AddRange(new[]
            {
                "find files modified today",
                "find files larger than 100MB",
                "search for documents"
            });
        }

        // Add learned patterns
        var learnedSuggestions = _learningStore.GetSimilarPatterns(partialInput, 5)
            .Select(p => p.InputPhrase);
        suggestions.AddRange(learnedSuggestions);

        return suggestions
            .Where(s => s.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .Take(maxSuggestions);
    }

    /// <summary>
    /// Gets learning statistics.
    /// </summary>
    /// <returns>Statistics about learned patterns.</returns>
    public NLPLearningStats GetLearningStats()
    {
        var stats = _learningStore.GetStatistics();
        return new NLPLearningStats
        {
            TotalPatterns = stats.TotalPatterns,
            CorrectionsLearned = stats.CorrectionsLearned,
            TotalSuccesses = stats.TotalSuccesses,
            TotalFailures = stats.TotalFailures,
            AverageConfidence = stats.AverageConfidence,
            SynonymCount = _synonyms.Count
        };
    }

    /// <summary>
    /// Records a user correction for learning.
    /// </summary>
    /// <param name="input">The original input.</param>
    /// <param name="incorrectCommand">What was incorrectly interpreted.</param>
    /// <param name="correctCommand">What it should have been.</param>
    /// <param name="correctParameters">Correct parameters if any.</param>
    public void RecordCorrection(
        string input,
        string incorrectCommand,
        string correctCommand,
        Dictionary<string, object?>? correctParameters = null)
    {
        _learningStore.RecordCorrection(input, incorrectCommand, correctCommand, correctParameters);
    }

    #endregion

    #region Helper Methods

    private string NormalizeQuery(string query)
    {
        // Remove extra whitespace
        var normalized = Regex.Replace(query.Trim(), @"\s+", " ");

        // Expand contractions
        normalized = Regex.Replace(normalized, @"what's", "what is", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"where's", "where is", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"how's", "how is", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"don't", "do not", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"can't", "cannot", RegexOptions.IgnoreCase);

        return normalized;
    }

    private string ResolveSynonyms(string input)
    {
        var result = input;
        foreach (var (synonym, canonical) in _synonyms)
        {
            result = Regex.Replace(result, $@"\b{Regex.Escape(synonym)}\b", canonical, RegexOptions.IgnoreCase);
        }
        return result;
    }

    private void InitializeDefaultSynonyms()
    {
        _synonyms["volumes"] = "pools";
        _synonyms["volume"] = "pool";
        _synonyms["drives"] = "disks";
        _synonyms["drive"] = "disk";
        _synonyms["settings"] = "config";
        _synonyms["configuration"] = "config";
        _synonyms["extensions"] = "plugins";
        _synonyms["addons"] = "plugins";
        _synonyms["addon"] = "plugin";
        _synonyms["check"] = "status";
        _synonyms["display"] = "show";
        _synonyms["view"] = "show";
    }

    private Dictionary<string, object?> ExtractParameters(Match match, string[]? parameterNames)
    {
        var parameters = new Dictionary<string, object?>();

        if (parameterNames == null || parameterNames.Length == 0)
            return parameters;

        for (int i = 0; i < parameterNames.Length && i + 1 < match.Groups.Count; i++)
        {
            var value = match.Groups[i + 1].Value;
            if (!string.IsNullOrEmpty(value))
            {
                parameters[parameterNames[i]] = value;
            }
        }

        return parameters;
    }

    private void ApplyContextParameters(Dictionary<string, object?> parameters, Dictionary<string, object?>? context)
    {
        if (context == null) return;

        // Apply context entities if not already specified
        foreach (var (key, value) in context)
        {
            if (!key.StartsWith("_") && !parameters.ContainsKey(key) && value != null)
            {
                // Only inherit relevant context
                if (key is "currentPool" or "currentBackup" or "lastDestination")
                {
                    parameters[$"context_{key}"] = value;
                }
            }
        }
    }

    private void ApplyFlagParameters(Dictionary<string, object?> parameters, string query)
    {
        var lower = query.ToLowerInvariant();

        if (lower.Contains("encrypt"))
            parameters["encrypt"] = true;
        if (lower.Contains("compress"))
            parameters["compress"] = true;
        if (lower.Contains("recursive"))
            parameters["recursive"] = true;
        if (lower.Contains("force"))
            parameters["force"] = true;
        if (lower.Contains("verbose"))
            parameters["verbose"] = true;
    }

    private double CalculateConfidence(Match match, string input, double baseConfidence)
    {
        var coverage = (double)match.Value.Length / input.Length;
        var bonus = match.Value.Trim().Equals(input.Trim(), StringComparison.OrdinalIgnoreCase) ? 0.1 : 0;
        return Math.Min(1.0, baseConfidence * coverage + bonus);
    }

    private string GenerateExplanation(string commandName, Dictionary<string, object?> parameters)
    {
        var paramStr = parameters.Count > 0
            ? $" with {string.Join(", ", parameters.Where(p => !p.Key.StartsWith("context_")).Select(p => $"{p.Key}={p.Value}"))}"
            : "";

        var description = _commandDescriptions.TryGetValue(commandName, out var desc)
            ? desc
            : commandName;

        return $"Interpreted as '{commandName}': {description}{paramStr}";
    }

    private List<NLPEntity> ExtractEntitiesFromText(string text)
    {
        var entities = new List<NLPEntity>();

        foreach (var pattern in _entityPatterns)
        {
            var matches = pattern.Regex.Matches(text);
            foreach (Match match in matches)
            {
                var value = pattern.ValueGroup > 0 && match.Groups.Count > pattern.ValueGroup
                    ? match.Groups[pattern.ValueGroup].Value
                    : match.Value;

                entities.Add(new NLPEntity
                {
                    Type = pattern.EntityType,
                    Value = value,
                    NormalizedValue = NormalizeEntityValue(value, pattern.EntityType),
                    Confidence = 0.85,
                    StartIndex = match.Index,
                    EndIndex = match.Index + match.Length
                });
            }
        }

        return entities;
    }

    private string? NormalizeEntityValue(string value, string entityType)
    {
        return entityType switch
        {
            "FileSize" => NormalizeSize(value),
            "RelativeTime" => NormalizeRelativeTime(value),
            "FileExtension" => value.TrimStart('.').ToLowerInvariant(),
            _ => value.Trim()
        };
    }

    private string NormalizeSize(string size)
    {
        var match = Regex.Match(size, @"(\d+(?:\.\d+)?)\s*(GB|MB|KB|TB|bytes?)?", RegexOptions.IgnoreCase);
        if (!match.Success) return size;

        if (!double.TryParse(match.Groups[1].Value, out var number)) return size;

        var unit = match.Groups[2].Value.ToUpperInvariant();
        var bytes = unit switch
        {
            "TB" => number * 1024L * 1024 * 1024 * 1024,
            "GB" => number * 1024L * 1024 * 1024,
            "MB" => number * 1024L * 1024,
            "KB" => number * 1024L,
            _ => number
        };

        return ((long)bytes).ToString();
    }

    private string NormalizeRelativeTime(string relativeTime)
    {
        var lower = relativeTime.ToLowerInvariant();
        return lower switch
        {
            "yesterday" => "1d",
            "today" => "0d",
            "this week" => "7d",
            "this month" => "30d",
            "last week" => "7d",
            "last month" => "30d",
            "last year" => "365d",
            _ when lower.Contains("week") => ExtractDays(lower, 7),
            _ when lower.Contains("month") => ExtractDays(lower, 30),
            _ when lower.Contains("day") => ExtractDays(lower, 1),
            _ when lower.Contains("hour") => ExtractHours(lower),
            _ => relativeTime
        };
    }

    private string ExtractDays(string text, int multiplier)
    {
        var numMatch = Regex.Match(text, @"\d+");
        if (numMatch.Success && int.TryParse(numMatch.Value, out var num))
        {
            return $"{num * multiplier}d";
        }
        return $"{multiplier}d";
    }

    private string ExtractHours(string text)
    {
        var numMatch = Regex.Match(text, @"\d+");
        if (numMatch.Success && int.TryParse(numMatch.Value, out var num))
        {
            return $"{num}h";
        }
        return "1h";
    }

    private List<string> ExtractKeywords(string query)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "is", "are", "was", "were", "be", "been", "being",
            "have", "has", "had", "do", "does", "did", "will", "would", "could",
            "should", "may", "might", "must", "to", "of", "in", "for", "on",
            "with", "at", "by", "from", "as", "into", "through", "during", "before",
            "after", "above", "below", "between", "under", "and", "but", "if", "or",
            "because", "until", "while", "me", "my", "i", "you", "your", "he", "him",
            "his", "she", "her", "it", "its", "we", "our", "they", "them", "their"
        };

        return Regex.Split(query.ToLowerInvariant(), @"[\s,;.!?]+")
            .Where(w => w.Length > 2 && !stopWords.Contains(w))
            .Distinct()
            .Take(10)
            .ToList();
    }

    private string DetectLanguage(string text)
    {
        // Simple language detection based on character ranges and common words
        if (Regex.IsMatch(text, @"[\u4e00-\u9fff]"))
            return "zh"; // Chinese
        if (Regex.IsMatch(text, @"[\u3040-\u309f\u30a0-\u30ff]"))
            return "ja"; // Japanese
        if (Regex.IsMatch(text, @"\b(el|la|los|las|buscar|mostrar)\b", RegexOptions.IgnoreCase))
            return "es"; // Spanish
        if (Regex.IsMatch(text, @"\b(le|la|les|trouver|montrer)\b", RegexOptions.IgnoreCase))
            return "fr"; // French
        if (Regex.IsMatch(text, @"\b(der|die|das|finden|zeigen)\b", RegexOptions.IgnoreCase))
            return "de"; // German

        return "en"; // Default to English
    }

    private bool IsFollowUpQuery(string query)
    {
        var followUpIndicators = new[]
        {
            "filter", "show only", "just", "only", "but", "and also",
            "from last", "from those", "of those", "the same",
            "more details", "details", "info", "information",
            "delete that", "remove that", "it", "that one", "this one",
            "again", "retry", "redo", "also", "more", "another"
        };

        var lower = query.ToLowerInvariant();
        return followUpIndicators.Any(p => lower.Contains(p));
    }

    private string[] SplitIntoSentences(string text)
    {
        // Split by common sentence terminators and conjunctions
        return Regex.Split(text, @"(?<=[.!?])\s+|(?:,?\s+(?:and|then|after that|also)\s+)", RegexOptions.IgnoreCase)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();
    }

    private List<string> GetSuggestedFollowUps(string commandName)
    {
        return commandName.Split('.').First() switch
        {
            "storage" => new List<string>
            {
                "Show storage statistics",
                "Filter by file type",
                "Show files larger than 100MB"
            },
            "backup" => new List<string>
            {
                "List all backups",
                "Verify the backup",
                "Restore from backup"
            },
            "health" => new List<string>
            {
                "Show detailed metrics",
                "List recent alerts",
                "Run health check"
            },
            "plugin" => new List<string>
            {
                "Show plugin details",
                "Enable/disable plugin"
            },
            _ => new List<string>()
        };
    }

    private IEnumerable<string> GetDefaultSuggestions()
    {
        return new[]
        {
            "list storage pools",
            "show health status",
            "create backup",
            "list plugins",
            "show system info",
            "find files modified today"
        };
    }

    private NLPCommandIntent CreateEmptyIntent(string input)
    {
        return new NLPCommandIntent
        {
            CommandName = "help",
            OriginalInput = input,
            Confidence = 0.0,
            Explanation = "Empty input received",
            ProcessedByAI = false
        };
    }

    private NLPCommandIntent CreateUnknownIntent(string query)
    {
        return new NLPCommandIntent
        {
            CommandName = "help",
            OriginalInput = query,
            Confidence = 0.1,
            Explanation = $"Could not understand: '{query}'. Try 'help' for available commands.",
            ProcessedByAI = false,
            Entities = ExtractEntitiesFromText(query),
            SuggestedFollowUps = new List<string>
            {
                "Show available commands",
                "Help with backups",
                "Help with storage"
            }
        };
    }

    private string BuildParsingSystemPrompt()
    {
        var commands = string.Join("\n", _commandDescriptions.Select(kvp => $"- {kvp.Key}: {kvp.Value}"));

        return $@"You are a CLI command parser for DataWarehouse. Parse natural language into structured commands.

Available commands:
{commands}

Respond ONLY with valid JSON:
{{
    ""command"": ""command.name"",
    ""parameters"": {{""param1"": ""value1""}},
    ""confidence"": 0.95,
    ""explanation"": ""Brief explanation"",
    ""entities"": [{{""type"": ""EntityType"", ""value"": ""value""}}],
    ""suggestedFollowUps"": [""Suggested action 1""]
}}

Rules:
1. Choose the most appropriate command
2. Extract all relevant parameters
3. Set confidence based on clarity of intent
4. If unsure, use ""help"" command with lower confidence";
    }

    private NLPCommandIntent? ParseAIResponse(string response, string originalInput, Dictionary<string, object?>? context)
    {
        try
        {
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');

            if (jsonStart < 0 || jsonEnd <= jsonStart)
                return null;

            var json = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var command = root.GetProperty("command").GetString() ?? "help";
            var confidence = root.TryGetProperty("confidence", out var confProp) ? confProp.GetDouble() : 0.7;
            var explanation = root.TryGetProperty("explanation", out var explProp) ? explProp.GetString() : null;

            var parameters = new Dictionary<string, object?>();
            if (root.TryGetProperty("parameters", out var paramsProp))
            {
                foreach (var prop in paramsProp.EnumerateObject())
                {
                    parameters[prop.Name] = ExtractJsonValue(prop.Value);
                }
            }

            var entities = new List<NLPEntity>();
            if (root.TryGetProperty("entities", out var entitiesProp))
            {
                foreach (var entity in entitiesProp.EnumerateArray())
                {
                    entities.Add(new NLPEntity
                    {
                        Type = entity.TryGetProperty("type", out var t) ? t.GetString() ?? "Unknown" : "Unknown",
                        Value = entity.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "",
                        Confidence = 0.9
                    });
                }
            }

            var followUps = new List<string>();
            if (root.TryGetProperty("suggestedFollowUps", out var followUpsProp))
            {
                foreach (var followUp in followUpsProp.EnumerateArray())
                {
                    var text = followUp.GetString();
                    if (!string.IsNullOrEmpty(text))
                        followUps.Add(text);
                }
            }

            ApplyContextParameters(parameters, context);

            return new NLPCommandIntent
            {
                CommandName = command,
                Parameters = parameters,
                OriginalInput = originalInput,
                Confidence = confidence,
                Explanation = explanation ?? GenerateExplanation(command, parameters),
                ProcessedByAI = true,
                Entities = entities,
                SuggestedFollowUps = followUps
            };
        }
        catch
        {
            return null;
        }
    }

    private NLPDetectedIntent? ParseIntentResponse(string response, string originalInput)
    {
        try
        {
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');

            if (jsonStart < 0 || jsonEnd <= jsonStart)
                return null;

            var json = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var intentType = root.TryGetProperty("intentType", out var it) ? it.GetString() ?? "Unknown" : "Unknown";
            var subType = root.TryGetProperty("subType", out var st) ? st.GetString() : null;
            var confidence = root.TryGetProperty("confidence", out var conf) ? conf.GetDouble() : 0.7;
            var isFollowUp = root.TryGetProperty("isFollowUp", out var ifu) && ifu.GetBoolean();

            var entities = new List<NLPEntity>();
            if (root.TryGetProperty("entities", out var entitiesProp))
            {
                foreach (var entity in entitiesProp.EnumerateArray())
                {
                    entities.Add(new NLPEntity
                    {
                        Type = entity.TryGetProperty("type", out var t) ? t.GetString() ?? "Unknown" : "Unknown",
                        Value = entity.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "",
                        Confidence = 0.9
                    });
                }
            }

            var keywords = new List<string>();
            if (root.TryGetProperty("keywords", out var keywordsProp))
            {
                foreach (var kw in keywordsProp.EnumerateArray())
                {
                    var text = kw.GetString();
                    if (!string.IsNullOrEmpty(text))
                        keywords.Add(text);
                }
            }

            return new NLPDetectedIntent
            {
                IntentType = intentType,
                SubType = subType,
                Confidence = confidence,
                OriginalInput = originalInput,
                Entities = entities,
                Keywords = keywords,
                IsFollowUp = isFollowUp,
                Language = DetectLanguage(originalInput)
            };
        }
        catch
        {
            return null;
        }
    }

    private NLPExtractedCommands? ParseCommandsResponse(string response, string originalText)
    {
        try
        {
            var jsonStart = response.IndexOf('[');
            var jsonEnd = response.LastIndexOf(']');

            if (jsonStart < 0 || jsonEnd <= jsonStart)
                return null;

            var json = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var commands = new List<NLPCommandIntent>();
            foreach (var item in root.EnumerateArray())
            {
                var command = item.TryGetProperty("command", out var c) ? c.GetString() ?? "help" : "help";
                var confidence = item.TryGetProperty("confidence", out var conf) ? conf.GetDouble() : 0.7;

                var parameters = new Dictionary<string, object?>();
                if (item.TryGetProperty("parameters", out var paramsProp))
                {
                    foreach (var prop in paramsProp.EnumerateObject())
                    {
                        parameters[prop.Name] = ExtractJsonValue(prop.Value);
                    }
                }

                commands.Add(new NLPCommandIntent
                {
                    CommandName = command,
                    Parameters = parameters,
                    OriginalInput = originalText,
                    Confidence = confidence,
                    Explanation = GenerateExplanation(command, parameters),
                    ProcessedByAI = true
                });
            }

            return new NLPExtractedCommands
            {
                OriginalText = originalText,
                Commands = commands,
                ProcessedByAI = true,
                Warnings = new List<string>()
            };
        }
        catch
        {
            return null;
        }
    }

    private object? ExtractJsonValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText()
        };
    }

    private TokenUsage? ConvertUsage(AIUsage? usage)
    {
        if (usage == null) return null;
        return new TokenUsage
        {
            InputTokens = usage.PromptTokens,
            OutputTokens = usage.CompletionTokens
        };
    }

    #endregion

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _learningStore.Dispose();
    }

    #region Nested Types

    private sealed record IntentPattern(
        string Pattern,
        string CommandName,
        double BaseConfidence,
        string[]? ParameterNames = null)
    {
        public Regex Regex { get; } = new(Pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        public string[] ParameterNames { get; } = ParameterNames ?? Array.Empty<string>();
    }

    private sealed record EntityPattern(
        string Pattern,
        string EntityType,
        int ValueGroup)
    {
        public Regex Regex { get; } = new(Pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    #endregion
}

/// <summary>
/// Configuration for the query parsing engine.
/// </summary>
public sealed class QueryParsingConfig
{
    /// <summary>
    /// Gets or sets the path for storing learned patterns.
    /// </summary>
    public string? LearningStorePath { get; init; }

    /// <summary>
    /// Gets or sets the minimum confidence threshold for AI parsing.
    /// If pattern matching exceeds this threshold, AI parsing is skipped.
    /// </summary>
    public double AIConfidenceThreshold { get; init; } = 0.8;

    /// <summary>
    /// Gets or sets the minimum confidence for learned patterns to be used.
    /// </summary>
    public double MinLearnedConfidence { get; init; } = 0.85;

    /// <summary>
    /// Gets or sets the maximum tokens for AI parsing requests.
    /// </summary>
    public int MaxParsingTokens { get; init; } = 500;

    /// <summary>
    /// Gets or sets the temperature for AI parsing.
    /// Lower values produce more consistent results.
    /// </summary>
    public float ParsingTemperature { get; init; } = 0.1f;
}

/// <summary>
/// Learning store for CLI patterns. Persists successful interpretations and corrections.
/// </summary>
internal sealed class CLILearningStore : IDisposable
{
    private readonly ConcurrentDictionary<string, LearnedPattern> _patterns = new();
    private readonly ConcurrentDictionary<string, int> _successCounts = new();
    private readonly ConcurrentDictionary<string, int> _failureCounts = new();
    private readonly string? _storagePath;
    private bool _disposed;

    public CLILearningStore(string? storagePath = null)
    {
        _storagePath = storagePath;
        if (!string.IsNullOrEmpty(storagePath))
        {
            LoadFromStorage();
        }
    }

    public LearnedPattern? FindBestMatch(string input)
    {
        var normalized = input.ToLowerInvariant().Trim();

        // Exact match first
        if (_patterns.TryGetValue(normalized, out var exact))
            return exact;

        // Fuzzy match
        LearnedPattern? bestMatch = null;
        var bestScore = 0.0;

        foreach (var (pattern, learned) in _patterns)
        {
            var score = CalculateSimilarity(normalized, pattern);
            if (score > bestScore && score > 0.7)
            {
                bestScore = score;
                bestMatch = learned with { Confidence = learned.Confidence * score };
            }
        }

        return bestMatch;
    }

    public void RecordSuccess(string input, string commandName, Dictionary<string, object?> parameters)
    {
        var normalized = input.ToLowerInvariant().Trim();
        var key = $"{commandName}:{normalized}";
        _successCounts.AddOrUpdate(key, 1, (_, c) => c + 1);

        var pattern = new LearnedPattern
        {
            InputPhrase = normalized,
            CommandName = commandName,
            Parameters = parameters,
            Confidence = 0.9,
            LearnedAt = DateTime.UtcNow
        };

        _patterns[normalized] = pattern;
    }

    public void RecordCorrection(
        string input,
        string incorrectCommand,
        string correctCommand,
        Dictionary<string, object?>? correctParameters)
    {
        var normalized = input.ToLowerInvariant().Trim();
        var incorrectKey = $"{incorrectCommand}:{normalized}";
        _failureCounts.AddOrUpdate(incorrectKey, 1, (_, c) => c + 1);

        var pattern = new LearnedPattern
        {
            InputPhrase = normalized,
            CommandName = correctCommand,
            Parameters = correctParameters ?? new Dictionary<string, object?>(),
            Confidence = 0.95, // Higher confidence for corrections
            LearnedAt = DateTime.UtcNow
        };

        _patterns[normalized] = pattern;
    }

    public IEnumerable<LearnedPattern> GetSimilarPatterns(string input, int limit)
    {
        var normalized = input.ToLowerInvariant().Trim();

        return _patterns.Values
            .Select(p => new { Pattern = p, Score = CalculateSimilarity(normalized, p.InputPhrase) })
            .Where(x => x.Score > 0.3)
            .OrderByDescending(x => x.Score)
            .Take(limit)
            .Select(x => x.Pattern);
    }

    public LearningStatistics GetStatistics()
    {
        return new LearningStatistics
        {
            TotalPatterns = _patterns.Count,
            CorrectionsLearned = _patterns.Values.Count(p => p.Confidence >= 0.95),
            TotalSuccesses = _successCounts.Values.Sum(),
            TotalFailures = _failureCounts.Values.Sum(),
            AverageConfidence = _patterns.Values.Any()
                ? _patterns.Values.Average(p => p.Confidence)
                : 0
        };
    }

    private double CalculateSimilarity(string s1, string s2)
    {
        if (s1 == s2) return 1.0;
        if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2)) return 0.0;

        // Simple token-based similarity
        var tokens1 = s1.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var tokens2 = s2.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        var intersection = tokens1.Intersect(tokens2).Count();
        var union = tokens1.Union(tokens2).Count();

        return union > 0 ? (double)intersection / union : 0.0;
    }

    private void LoadFromStorage()
    {
        // TODO: Implement persistence
    }

    private void SaveToStorage()
    {
        // TODO: Implement persistence
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SaveToStorage();
    }
}

/// <summary>
/// Represents a learned pattern from user interactions.
/// </summary>
internal sealed record LearnedPattern
{
    public required string InputPhrase { get; init; }
    public required string CommandName { get; init; }
    public Dictionary<string, object?> Parameters { get; init; } = new();
    public double Confidence { get; init; }
    public DateTime LearnedAt { get; init; }
}

/// <summary>
/// Statistics about the learning store.
/// </summary>
internal sealed record LearningStatistics
{
    public int TotalPatterns { get; init; }
    public int CorrectionsLearned { get; init; }
    public int TotalSuccesses { get; init; }
    public int TotalFailures { get; init; }
    public double AverageConfidence { get; init; }
}
