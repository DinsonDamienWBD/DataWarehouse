// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.RegularExpressions;

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
}

/// <summary>
/// Processes natural language input and converts it to structured commands.
/// Enables commands like: dw "backup my database to S3 with encryption"
/// </summary>
public sealed class NaturalLanguageProcessor
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

    /// <summary>
    /// Processes natural language input and returns the interpreted command intent.
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
        var normalizedInput = input.ToLowerInvariant();

        // Check for encryption flag
        var withEncryption = normalizedInput.Contains("encrypt");
        var withCompression = normalizedInput.Contains("compress");

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

                return new CommandIntent
                {
                    CommandName = pattern.CommandName,
                    Parameters = parameters,
                    OriginalInput = input,
                    Confidence = CalculateConfidence(match, input),
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

        return suggestions.Where(s => s.StartsWith(normalized, StringComparison.OrdinalIgnoreCase));
    }

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

    private sealed record IntentPattern(
        string Pattern,
        string CommandName,
        string[]? ParameterNames = null)
    {
        public Regex Regex { get; } = new(Pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        public string[] ParameterNames { get; } = ParameterNames ?? Array.Empty<string>();
    }
}
