// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.AI;

namespace DataWarehouse.Shared.Services;

/// <summary>
/// Represents a parsed natural language command intent.
/// </summary>
public sealed record CommandIntent
{
    /// <summary>The resolved command name.</summary>
    public required string CommandName { get; init; }

    /// <summary>Parsed parameters from the natural language input.</summary>
    public Dictionary<string, object?> Parameters { get; init; } = new();

    /// <summary>Confidence score (0.0 - 1.0).</summary>
    public double Confidence { get; init; }

    /// <summary>Original natural language input.</summary>
    public required string OriginalInput { get; init; }

    /// <summary>Explanation of the interpretation.</summary>
    public string? Explanation { get; init; }

    /// <summary>Whether this was processed by AI (vs pattern matching).</summary>
    public bool ProcessedByAI { get; init; }

    /// <summary>Session ID for conversational context.</summary>
    public string? SessionId { get; init; }
}

/// <summary>
/// Result of AI-powered help query.
/// </summary>
public sealed record AIHelpResult
{
    /// <summary>The answer to the help query.</summary>
    public required string Answer { get; init; }

    /// <summary>Suggested commands related to the query.</summary>
    public List<string> SuggestedCommands { get; init; } = new();

    /// <summary>Example usage patterns.</summary>
    public List<string> Examples { get; init; } = new();

    /// <summary>Related documentation topics.</summary>
    public List<string> RelatedTopics { get; init; } = new();

    /// <summary>Whether AI was used for this response.</summary>
    public bool UsedAI { get; init; }
}

/// <summary>
/// Processes natural language input and converts it to structured commands.
/// Supports pattern matching, AI fallback, conversational context, and learning.
/// </summary>
public sealed class NaturalLanguageProcessor : IDisposable
{
    private static readonly List<IntentPattern> _patterns = new()
    {
        // Storage commands
        new IntentPattern(@"(?:list|show|get)\s+(?:all\s+)?(?:storage\s+)?pools?", "storage.list"),
        new IntentPattern(@"create\s+(?:a\s+)?(?:new\s+)?(?:storage\s+)?pool\s+(?:named?\s+)?['""]?(\w+)['""]?", "storage.create", new[] { "name" }),
        new IntentPattern(@"delete\s+(?:the\s+)?(?:storage\s+)?pool\s+['""]?(\w+)['""]?", "storage.delete", new[] { "id" }),
        new IntentPattern(@"(?:show|get)\s+(?:info|details|information)\s+(?:for|about|on)\s+(?:pool\s+)?['""]?(\w+)['""]?", "storage.info", new[] { "id" }),
        new IntentPattern(@"(?:show|get)\s+storage\s+(?:stats|statistics)", "storage.stats"),

        // Backup commands
        new IntentPattern(@"(?:create|make|run)\s+(?:a\s+)?(?:new\s+)?backup\s+(?:named?\s+)?['""]?(\w+)['""]?(?:\s+to\s+['""]?(\S+)['""]?)?", "backup.create", new[] { "name", "destination" }),
        new IntentPattern(@"(?:backup)\s+(?:my\s+)?(?:database|data|files?)(?:\s+to\s+['""]?(\S+)['""]?)?(?:\s+with\s+encryption)?", "backup.create", new[] { "destination" }),
        new IntentPattern(@"(?:list|show)\s+(?:all\s+)?backups?", "backup.list"),
        new IntentPattern(@"restore\s+(?:from\s+)?(?:backup\s+)?['""]?(\S+)['""]?", "backup.restore", new[] { "id" }),
        new IntentPattern(@"verify\s+(?:backup\s+)?['""]?(\S+)['""]?", "backup.verify", new[] { "id" }),
        new IntentPattern(@"delete\s+(?:the\s+)?backup\s+['""]?(\S+)['""]?", "backup.delete", new[] { "id" }),

        // Plugin commands
        new IntentPattern(@"(?:list|show)\s+(?:all\s+)?plugins?", "plugin.list"),
        new IntentPattern(@"(?:enable|activate)\s+(?:the\s+)?plugin\s+['""]?(\w[\w-]*)['""]?", "plugin.enable", new[] { "id" }),
        new IntentPattern(@"(?:disable|deactivate)\s+(?:the\s+)?plugin\s+['""]?(\w[\w-]*)['""]?", "plugin.disable", new[] { "id" }),
        new IntentPattern(@"reload\s+(?:the\s+)?plugin\s+['""]?(\w[\w-]*)['""]?", "plugin.reload", new[] { "id" }),

        // Health commands
        new IntentPattern(@"(?:show|check|get)\s+(?:system\s+)?(?:health|status)", "health.status"),
        new IntentPattern(@"(?:show|get)\s+(?:system\s+)?metrics", "health.metrics"),
        new IntentPattern(@"(?:show|list)\s+(?:all\s+)?alerts?", "health.alerts"),
        new IntentPattern(@"(?:run|perform)\s+(?:a\s+)?health\s+check", "health.check"),

        // RAID commands
        new IntentPattern(@"(?:list|show)\s+(?:all\s+)?raid(?:\s+(?:arrays?|configs?|configurations?))?", "raid.list"),
        new IntentPattern(@"create\s+(?:a\s+)?(?:new\s+)?raid\s+(?:array\s+)?(?:named?\s+)?['""]?(\w+)['""]?(?:\s+(?:level|type)\s+(\d+))?", "raid.create", new[] { "name", "level" }),
        new IntentPattern(@"(?:show|get)\s+raid\s+status\s+(?:for\s+)?['""]?(\S+)['""]?", "raid.status", new[] { "id" }),
        new IntentPattern(@"rebuild\s+(?:the\s+)?raid\s+(?:array\s+)?['""]?(\S+)['""]?", "raid.rebuild", new[] { "id" }),
        new IntentPattern(@"(?:list|show)\s+(?:supported\s+)?raid\s+levels", "raid.levels"),

        // Config commands
        new IntentPattern(@"(?:show|get|display)\s+(?:the\s+)?(?:current\s+)?config(?:uration)?", "config.show"),
        new IntentPattern(@"set\s+(?:config|configuration)\s+['""]?(\S+)['""]?\s+(?:to\s+)?['""]?(\S+)['""]?", "config.set", new[] { "key", "value" }),
        new IntentPattern(@"get\s+(?:config|configuration)\s+['""]?(\S+)['""]?", "config.get", new[] { "key" }),
        new IntentPattern(@"export\s+config(?:uration)?\s+(?:to\s+)?['""]?(\S+)['""]?", "config.export", new[] { "path" }),
        new IntentPattern(@"import\s+config(?:uration)?\s+(?:from\s+)?['""]?(\S+)['""]?", "config.import", new[] { "path" }),

        // Server commands
        new IntentPattern(@"start\s+(?:the\s+)?server(?:\s+on\s+port\s+(\d+))?", "server.start", new[] { "port" }),
        new IntentPattern(@"stop\s+(?:the\s+)?server", "server.stop"),
        new IntentPattern(@"(?:show|get)\s+server\s+status", "server.status"),
        new IntentPattern(@"(?:show|get)\s+server\s+info(?:rmation)?", "server.info"),

        // Audit commands
        new IntentPattern(@"(?:show|list)\s+(?:the\s+)?audit\s+(?:log|entries)", "audit.list"),
        new IntentPattern(@"export\s+audit\s+(?:log\s+)?(?:to\s+)?['""]?(\S+)['""]?", "audit.export", new[] { "path" }),
        new IntentPattern(@"(?:show|get)\s+audit\s+(?:stats|statistics)", "audit.stats"),

        // Benchmark commands
        new IntentPattern(@"(?:run|execute)\s+(?:a\s+)?benchmark(?:\s+(\w+))?", "benchmark.run", new[] { "type" }),
        new IntentPattern(@"(?:show|get)\s+benchmark\s+(?:report|results?)", "benchmark.report"),

        // System commands
        new IntentPattern(@"(?:show|get)\s+(?:system\s+)?info(?:rmation)?", "system.info"),
        new IntentPattern(@"(?:show|get)\s+(?:instance\s+)?capabilities", "system.capabilities"),
        new IntentPattern(@"help(?:\s+(\S+))?", "help", new[] { "command" }),

        // Diagnostic queries
        new IntentPattern(@"why\s+is\s+(?:my\s+)?(\w+)\s+failing", "health.check", new[] { "component" }),
        new IntentPattern(@"what(?:'s|\s+is)\s+(?:the\s+)?(?:status|state)\s+of\s+(\w+)", "health.status", new[] { "component" }),
        new IntentPattern(@"how\s+much\s+(?:storage|space|disk)\s+(?:am\s+I\s+using|is\s+used)", "storage.stats"),
        new IntentPattern(@"(?:show|list)\s+(?:files?\s+)?(?:modified|changed)\s+(?:in\s+the\s+)?last\s+(\w+)", "storage.list"),
        new IntentPattern(@"(?:find|search)\s+(?:files?\s+)?(?:larger|bigger)\s+than\s+(\S+)", "storage.list"),
    };

    // Available commands for AI context
    private static readonly Dictionary<string, string> _commandDescriptions = new()
    {
        ["storage.list"] = "List all storage pools",
        ["storage.create"] = "Create a new storage pool with name",
        ["storage.delete"] = "Delete a storage pool by ID",
        ["storage.info"] = "Show detailed information about a storage pool",
        ["storage.stats"] = "Show storage statistics and usage",
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
        ["raid.list"] = "List RAID configurations",
        ["raid.create"] = "Create a new RAID array",
        ["raid.status"] = "Show RAID array status",
        ["raid.rebuild"] = "Start RAID rebuild",
        ["raid.levels"] = "List supported RAID levels",
        ["config.show"] = "Show current configuration",
        ["config.set"] = "Set a configuration value",
        ["config.get"] = "Get a configuration value",
        ["config.export"] = "Export configuration to file",
        ["config.import"] = "Import configuration from file",
        ["server.start"] = "Start the server on optional port",
        ["server.stop"] = "Stop the server",
        ["server.status"] = "Show server status",
        ["server.info"] = "Show server information",
        ["audit.list"] = "Show audit log entries",
        ["audit.export"] = "Export audit log to file",
        ["audit.stats"] = "Show audit statistics",
        ["benchmark.run"] = "Run performance benchmarks",
        ["benchmark.report"] = "Show benchmark report",
        ["system.info"] = "Show system information",
        ["system.capabilities"] = "Show instance capabilities",
        ["help"] = "Show help for a command"
    };

    private readonly IAIProviderRegistry? _aiRegistry;
    private readonly ConversationContextManager _contextManager;
    private readonly CLILearningStore _learningStore;
    private readonly double _aiConfidenceThreshold;
    private bool _disposed;

    /// <summary>
    /// Creates a new NaturalLanguageProcessor with optional AI support.
    /// </summary>
    /// <param name="aiRegistry">Optional AI provider registry for fallback processing.</param>
    /// <param name="learningStorePath">Optional path for persisting learned patterns.</param>
    /// <param name="aiConfidenceThreshold">Minimum pattern confidence before AI fallback (default 0.6).</param>
    public NaturalLanguageProcessor(
        IAIProviderRegistry? aiRegistry = null,
        string? learningStorePath = null,
        double aiConfidenceThreshold = 0.6)
    {
        _aiRegistry = aiRegistry;
        _contextManager = new ConversationContextManager();
        _learningStore = new CLILearningStore(learningStorePath);
        _aiConfidenceThreshold = aiConfidenceThreshold;
    }

    #region Pattern-Based Processing

    /// <summary>
    /// Processes natural language input using pattern matching only.
    /// </summary>
    /// <param name="input">Natural language input from user.</param>
    /// <returns>Parsed command intent.</returns>
    public CommandIntent Process(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new CommandIntent
            {
                CommandName = "help",
                OriginalInput = input ?? string.Empty,
                Confidence = 0.0,
                Explanation = "Empty input received"
            };
        }

        input = input.Trim();

        // First, resolve synonyms
        var resolvedInput = _learningStore.ResolveSynonyms(input);
        var normalizedInput = resolvedInput.ToLowerInvariant();

        // Check for encryption flag
        var withEncryption = normalizedInput.Contains("encrypt");
        var withCompression = normalizedInput.Contains("compress");

        // Check learned patterns first
        var learnedPattern = _learningStore.FindBestMatch(input);
        if (learnedPattern != null && learnedPattern.Confidence > 0.8)
        {
            _learningStore.RecordSuccess(input, learnedPattern.CommandName, learnedPattern.Parameters);
            return new CommandIntent
            {
                CommandName = learnedPattern.CommandName,
                Parameters = new Dictionary<string, object?>(learnedPattern.Parameters),
                OriginalInput = input,
                Confidence = learnedPattern.Confidence,
                Explanation = $"Matched learned pattern: {learnedPattern.InputPhrase}"
            };
        }

        // Try each pattern
        foreach (var pattern in _patterns)
        {
            var match = pattern.Regex.Match(normalizedInput);
            if (match.Success)
            {
                var parameters = new Dictionary<string, object?>();

                // Extract capture groups as parameters
                for (int i = 0; i < pattern.ParameterNames.Length && i + 1 < match.Groups.Count; i++)
                {
                    var value = match.Groups[i + 1].Value;
                    if (!string.IsNullOrEmpty(value))
                    {
                        parameters[pattern.ParameterNames[i]] = value;
                    }
                }

                // Add common flags
                if (withEncryption && pattern.CommandName.StartsWith("backup"))
                {
                    parameters["encrypt"] = true;
                }
                if (withCompression && pattern.CommandName.StartsWith("backup"))
                {
                    parameters["compress"] = true;
                }

                var confidence = CalculateConfidence(match, input);

                // Record success for learning
                if (confidence > 0.7)
                {
                    _learningStore.RecordSuccess(input, pattern.CommandName, parameters);
                }

                return new CommandIntent
                {
                    CommandName = pattern.CommandName,
                    Parameters = parameters,
                    OriginalInput = input,
                    Confidence = confidence,
                    Explanation = GenerateExplanation(pattern.CommandName, parameters)
                };
            }
        }

        // No pattern matched - return help suggestion
        return new CommandIntent
        {
            CommandName = "help",
            OriginalInput = input,
            Confidence = 0.1,
            Explanation = $"Could not understand: '{input}'. Try 'dw help' for available commands."
        };
    }

    #endregion

    #region AI-Enhanced Processing

    /// <summary>
    /// Processes input with AI fallback when pattern matching fails or has low confidence.
    /// </summary>
    /// <param name="input">Natural language input.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Parsed command intent, possibly AI-enhanced.</returns>
    public async Task<CommandIntent> ProcessWithAIFallbackAsync(string input, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Try pattern matching first (fast, no API cost)
        var patternResult = Process(input);

        // If confidence is high enough, return pattern result
        if (patternResult.Confidence >= _aiConfidenceThreshold)
        {
            return patternResult;
        }

        // Check if AI is available
        if (_aiRegistry == null)
        {
            return patternResult; // Return best pattern match if no AI
        }

        var provider = _aiRegistry.GetDefaultProvider();
        if (provider == null || !provider.IsAvailable)
        {
            return patternResult;
        }

        // Fall back to AI interpretation
        try
        {
            return await ProcessWithAIAsync(input, provider, ct);
        }
        catch
        {
            // AI failed - return pattern result
            return patternResult;
        }
    }

    /// <summary>
    /// Processes input using AI interpretation.
    /// </summary>
    private async Task<CommandIntent> ProcessWithAIAsync(
        string input,
        IAIProvider provider,
        CancellationToken ct)
    {
        var systemPrompt = BuildSystemPrompt();
        var userPrompt = $"Parse this command: \"{input}\"\n\nRespond with JSON only, no explanation.";

        var request = new AIRequest
        {
            Prompt = userPrompt,
            SystemMessage = systemPrompt,
            MaxTokens = 500,
            Temperature = 0.1f // Low temperature for consistent parsing
        };

        var response = await provider.CompleteAsync(request, ct);

        if (!response.Success || string.IsNullOrEmpty(response.Content))
        {
            return new CommandIntent
            {
                CommandName = "help",
                OriginalInput = input,
                Confidence = 0.1,
                Explanation = "AI interpretation failed",
                ProcessedByAI = true
            };
        }

        // Parse AI response
        try
        {
            var parsed = ParseAIResponse(response.Content, input);
            if (parsed != null)
            {
                // Record for learning
                _learningStore.RecordSuccess(input, parsed.CommandName, parsed.Parameters);
                return parsed;
            }
        }
        catch
        {
            // Failed to parse AI response
        }

        return new CommandIntent
        {
            CommandName = "help",
            OriginalInput = input,
            Confidence = 0.2,
            Explanation = "Could not parse AI response",
            ProcessedByAI = true
        };
    }

    private string BuildSystemPrompt()
    {
        var commands = string.Join("\n", _commandDescriptions.Select(kvp => $"- {kvp.Key}: {kvp.Value}"));

        return $@"You are a CLI command parser for DataWarehouse. Parse natural language into structured commands.

Available commands:
{commands}

Respond ONLY with valid JSON in this format:
{{
    ""command"": ""command.name"",
    ""parameters"": {{""param1"": ""value1""}},
    ""confidence"": 0.95,
    ""explanation"": ""Brief explanation""
}}

Rules:
1. Choose the most appropriate command from the list
2. Extract all relevant parameters from the input
3. Set confidence based on how clearly the input matches the command
4. If unsure, use ""help"" command with lower confidence";
    }

    private CommandIntent? ParseAIResponse(string response, string originalInput)
    {
        // Try to extract JSON from response
        var jsonStart = response.IndexOf('{');
        var jsonEnd = response.LastIndexOf('}');

        if (jsonStart < 0 || jsonEnd < jsonStart)
        {
            return null;
        }

        var json = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var command = root.GetProperty("command").GetString() ?? "help";
        var confidence = root.TryGetProperty("confidence", out var confProp)
            ? confProp.GetDouble()
            : 0.7;
        var explanation = root.TryGetProperty("explanation", out var explProp)
            ? explProp.GetString()
            : null;

        var parameters = new Dictionary<string, object?>();
        if (root.TryGetProperty("parameters", out var paramsProp))
        {
            foreach (var prop in paramsProp.EnumerateObject())
            {
                parameters[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => prop.Value.ToString()
                };
            }
        }

        return new CommandIntent
        {
            CommandName = command,
            Parameters = parameters,
            OriginalInput = originalInput,
            Confidence = confidence,
            Explanation = explanation ?? $"AI interpreted as: {command}",
            ProcessedByAI = true
        };
    }

    #endregion

    #region Conversational Processing

    /// <summary>
    /// Processes input in a conversational context, supporting multi-turn interactions.
    /// </summary>
    /// <param name="input">User input.</param>
    /// <param name="sessionId">Session ID for context tracking.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Parsed command intent with context.</returns>
    public async Task<CommandIntent> ProcessConversationalAsync(
        string input,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var session = _contextManager.GetOrCreateSession(sessionId);

        // Check for context reset
        if (ConversationalPatterns.IsContextReset(input))
        {
            session.ClearContext();
            return new CommandIntent
            {
                CommandName = "cli.context.clear",
                OriginalInput = input,
                Confidence = 1.0,
                Explanation = "Context cleared",
                SessionId = session.SessionId
            };
        }

        // Check if this is a follow-up that needs context
        var isFollowUp = ConversationalPatterns.IsFollowUp(input);
        var carryOverContext = session.GetCarryOverContext();

        // Resolve synonyms with context
        var context = session.LastCommandName?.Split('.').FirstOrDefault();
        var resolvedInput = _learningStore.ResolveSynonyms(input, context);

        // Try to enrich the input with context
        var enrichedInput = EnrichWithContext(resolvedInput, carryOverContext, isFollowUp);

        // Process the enriched input
        CommandIntent result;
        if (_aiRegistry != null)
        {
            result = await ProcessWithAIFallbackAsync(enrichedInput, ct);
        }
        else
        {
            result = Process(enrichedInput);
        }

        // Merge carry-over parameters for follow-ups
        if (isFollowUp && session.LastParameters != null)
        {
            foreach (var (key, value) in session.LastParameters)
            {
                if (!result.Parameters.ContainsKey(key))
                {
                    result.Parameters[key] = value;
                }
            }
        }

        // Apply time filter if detected
        var timeFilter = ConversationalPatterns.ExtractTimeFilter(input);
        if (timeFilter.HasValue)
        {
            result.Parameters["since"] = DateTime.UtcNow - timeFilter.Value;
        }

        // Record the turn
        session.AddTurn(input, result.CommandName, result.Parameters, true);

        return result with { SessionId = session.SessionId };
    }

    private string EnrichWithContext(
        string input,
        Dictionary<string, object?> context,
        bool isFollowUp)
    {
        if (!isFollowUp || context.Count == 0)
        {
            return input;
        }

        // Add referenced entities to input for pattern matching
        var enriched = input;

        // Handle pronoun resolution ("it", "that", "those")
        if (input.Contains(" it") || input.Contains(" that") || input.Contains("that one"))
        {
            // Try to find a relevant entity from context
            if (context.TryGetValue("currentPool", out var pool) && pool != null)
            {
                enriched = enriched
                    .Replace(" it", $" pool {pool}")
                    .Replace(" that", $" pool {pool}")
                    .Replace("that one", $"pool {pool}");
            }
            else if (context.TryGetValue("currentBackup", out var backup) && backup != null)
            {
                enriched = enriched
                    .Replace(" it", $" backup {backup}")
                    .Replace(" that", $" backup {backup}")
                    .Replace("that one", $"backup {backup}");
            }
        }

        return enriched;
    }

    #endregion

    #region AI-Powered Help

    /// <summary>
    /// Provides AI-powered help for natural language queries.
    /// </summary>
    /// <param name="query">The help query (e.g., "How do I backup to S3?").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>AI-generated help response.</returns>
    public async Task<AIHelpResult> GetAIHelpAsync(string query, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Find matching commands based on keywords
        var suggestedCommands = FindMatchingCommands(query);

        // If no AI, return pattern-based suggestions
        if (_aiRegistry == null)
        {
            return new AIHelpResult
            {
                Answer = GenerateBasicHelp(query, suggestedCommands),
                SuggestedCommands = suggestedCommands,
                Examples = GenerateExamples(suggestedCommands),
                UsedAI = false
            };
        }

        var provider = _aiRegistry.GetDefaultProvider();
        if (provider == null || !provider.IsAvailable)
        {
            return new AIHelpResult
            {
                Answer = GenerateBasicHelp(query, suggestedCommands),
                SuggestedCommands = suggestedCommands,
                Examples = GenerateExamples(suggestedCommands),
                UsedAI = false
            };
        }

        try
        {
            var request = new AIRequest
            {
                Prompt = $"User question about DataWarehouse CLI: \"{query}\"",
                SystemMessage = BuildHelpSystemPrompt(),
                MaxTokens = 800,
                Temperature = 0.5f
            };

            var response = await provider.CompleteAsync(request, ct);

            if (response.Success && !string.IsNullOrEmpty(response.Content))
            {
                return ParseHelpResponse(response.Content, query, suggestedCommands);
            }
        }
        catch
        {
            // AI failed - return basic help
        }

        return new AIHelpResult
        {
            Answer = GenerateBasicHelp(query, suggestedCommands),
            SuggestedCommands = suggestedCommands,
            Examples = GenerateExamples(suggestedCommands),
            UsedAI = false
        };
    }

    private string BuildHelpSystemPrompt()
    {
        var commands = string.Join("\n", _commandDescriptions.Select(kvp => $"- {kvp.Key}: {kvp.Value}"));

        return $@"You are a helpful assistant for the DataWarehouse CLI.

Available commands:
{commands}

Provide helpful, concise answers about how to use these commands.
Include specific command examples when relevant.
Format your response as:
1. A brief answer
2. Relevant command(s) to use
3. Example usage";
    }

    private List<string> FindMatchingCommands(string query)
    {
        var keywords = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = new List<(string Command, int Score)>();

        foreach (var (command, description) in _commandDescriptions)
        {
            var score = 0;
            var combined = $"{command} {description}".ToLowerInvariant();

            foreach (var keyword in keywords)
            {
                if (combined.Contains(keyword))
                {
                    score++;
                }
            }

            if (score > 0)
            {
                matches.Add((command, score));
            }
        }

        return matches
            .OrderByDescending(m => m.Score)
            .Take(3)
            .Select(m => m.Command)
            .ToList();
    }

    private string GenerateBasicHelp(string query, List<string> suggestedCommands)
    {
        if (suggestedCommands.Count == 0)
        {
            return "I couldn't find specific commands matching your query. Try 'dw help' for a list of available commands.";
        }

        var descriptions = suggestedCommands
            .Where(c => _commandDescriptions.ContainsKey(c))
            .Select(c => $"- {c}: {_commandDescriptions[c]}");

        return $"Based on your query, you might want to use:\n{string.Join("\n", descriptions)}";
    }

    private List<string> GenerateExamples(List<string> commands)
    {
        var examples = new List<string>();

        foreach (var command in commands)
        {
            var example = command switch
            {
                "backup.create" => "dw \"backup my database to /backups with encryption\"",
                "backup.list" => "dw \"show all backups\"",
                "storage.list" => "dw \"list storage pools\"",
                "storage.create" => "dw \"create storage pool named mypool\"",
                "health.status" => "dw \"check system health\"",
                "plugin.list" => "dw \"show plugins\"",
                "server.start" => "dw \"start server on port 8080\"",
                _ => null
            };

            if (example != null)
            {
                examples.Add(example);
            }
        }

        return examples;
    }

    private AIHelpResult ParseHelpResponse(string response, string query, List<string> suggestedCommands)
    {
        // Extract examples from response
        var examples = new List<string>();
        var exampleMatches = Regex.Matches(response, @"dw\s+[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        foreach (Match match in exampleMatches)
        {
            examples.Add($"dw \"{match.Groups[1].Value}\"");
        }

        return new AIHelpResult
        {
            Answer = response,
            SuggestedCommands = suggestedCommands,
            Examples = examples.Count > 0 ? examples : GenerateExamples(suggestedCommands),
            RelatedTopics = ExtractRelatedTopics(response),
            UsedAI = true
        };
    }

    private List<string> ExtractRelatedTopics(string response)
    {
        var topics = new List<string>();
        var keywords = new[] { "backup", "storage", "raid", "plugin", "health", "server", "config" };

        foreach (var keyword in keywords)
        {
            if (response.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                topics.Add(keyword);
            }
        }

        return topics.Distinct().Take(3).ToList();
    }

    #endregion

    #region Learning and Corrections

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

    /// <summary>
    /// Gets learning statistics.
    /// </summary>
    public LearningStatistics GetLearningStats()
    {
        return _learningStore.GetStatistics();
    }

    #endregion

    #region Session Management

    /// <summary>
    /// Gets or creates a conversation session.
    /// </summary>
    public ConversationSession GetSession(string? sessionId = null)
    {
        return _contextManager.GetOrCreateSession(sessionId);
    }

    /// <summary>
    /// Clears a session's context.
    /// </summary>
    public bool ClearSessionContext(string sessionId)
    {
        return _contextManager.ClearSessionContext(sessionId);
    }

    /// <summary>
    /// Ends a conversation session.
    /// </summary>
    public bool EndSession(string sessionId)
    {
        return _contextManager.EndSession(sessionId);
    }

    #endregion

    #region Completions

    /// <summary>
    /// Gets suggestions for completing a partial natural language input.
    /// </summary>
    /// <param name="partialInput">Partial input to complete.</param>
    /// <returns>List of completion suggestions.</returns>
    public IEnumerable<string> GetCompletions(string partialInput)
    {
        if (string.IsNullOrWhiteSpace(partialInput))
        {
            return new[]
            {
                "list storage pools",
                "show health status",
                "create backup",
                "list plugins",
                "show system info"
            };
        }

        var normalized = partialInput.ToLowerInvariant();

        var suggestions = new List<string>();

        if (normalized.StartsWith("show") || normalized.StartsWith("list") || normalized.StartsWith("get"))
        {
            suggestions.Add("show storage pools");
            suggestions.Add("show health status");
            suggestions.Add("show plugins");
            suggestions.Add("show backups");
            suggestions.Add("show raid status");
            suggestions.Add("show config");
        }
        else if (normalized.StartsWith("create") || normalized.StartsWith("make"))
        {
            suggestions.Add("create backup");
            suggestions.Add("create storage pool");
            suggestions.Add("create raid array");
        }
        else if (normalized.StartsWith("backup"))
        {
            suggestions.Add("backup my database");
            suggestions.Add("backup my database with encryption");
            suggestions.Add("backup my database to S3");
        }
        else if (normalized.StartsWith("delete") || normalized.StartsWith("remove"))
        {
            suggestions.Add("delete backup");
            suggestions.Add("delete storage pool");
        }
        else if (normalized.StartsWith("start") || normalized.StartsWith("stop"))
        {
            suggestions.Add("start server");
            suggestions.Add("stop server");
        }
        else if (normalized.StartsWith("how") || normalized.StartsWith("help"))
        {
            suggestions.Add("how do I backup to S3?");
            suggestions.Add("help backup");
            suggestions.Add("help storage");
        }

        // Add learned patterns as suggestions
        var learnedSuggestions = _learningStore.GetSimilarPatterns(partialInput, 3)
            .Select(p => p.InputPhrase);
        suggestions.AddRange(learnedSuggestions);

        return suggestions
            .Where(s => s.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .Take(10);
    }

    #endregion

    #region Helpers

    private static double CalculateConfidence(Match match, string input)
    {
        // Calculate confidence based on how much of the input was matched
        var matchedLength = match.Value.Length;
        var inputLength = input.Length;

        // Base confidence from match coverage
        var coverage = (double)matchedLength / inputLength;

        // Bonus for exact word boundaries
        var exactMatch = match.Value.Trim() == input.Trim().ToLowerInvariant();

        return Math.Min(1.0, coverage * 0.8 + (exactMatch ? 0.2 : 0));
    }

    private static string GenerateExplanation(string commandName, Dictionary<string, object?> parameters)
    {
        var paramStr = parameters.Count > 0
            ? $" with {string.Join(", ", parameters.Select(p => $"{p.Key}={p.Value}"))}"
            : "";

        return $"Interpreted as: {commandName}{paramStr}";
    }

    #endregion

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _contextManager.Dispose();
        _learningStore.Dispose();
    }

    private sealed record IntentPattern(
        string Pattern,
        string CommandName,
        string[]? ParameterNames = null)
    {
        public Regex Regex { get; } = new(Pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        public string[] ParameterNames { get; } = ParameterNames ?? Array.Empty<string>();
    }
}
