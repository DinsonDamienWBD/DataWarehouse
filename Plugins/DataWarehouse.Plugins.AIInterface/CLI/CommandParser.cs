// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Plugins.AIInterface.Channels;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.AIInterface.CLI;

/// <summary>
/// AI-powered command parser that interprets natural language input into structured commands.
/// Falls back to traditional command parsing when Intelligence is unavailable.
/// </summary>
/// <remarks>
/// <para>
/// The CommandParser handles the following transformations:
/// <list type="bullet">
/// <item>"Upload this file" translates to <c>PUT /blobs</c></item>
/// <item>"Find documents about Q4 sales" becomes semantic search</item>
/// <item>"Encrypt with military-grade" selects AES-256-GCM</item>
/// <item>"Download the latest report" resolves references from context</item>
/// </list>
/// </para>
/// </remarks>
public sealed class CommandParser
{
    private readonly IMessageBus? _messageBus;
    private readonly Dictionary<string, string> _commandMappings;
    private readonly Dictionary<string, CommandDefinition> _knownCommands;

    /// <summary>
    /// Encryption strength mappings from natural language to cipher specifications.
    /// </summary>
    private static readonly Dictionary<string, EncryptionSpec> EncryptionMappings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["military-grade"] = new EncryptionSpec("AES-256-GCM", 256, "Military/government grade encryption"),
        ["military grade"] = new EncryptionSpec("AES-256-GCM", 256, "Military/government grade encryption"),
        ["top secret"] = new EncryptionSpec("AES-256-GCM", 256, "Top secret classification encryption"),
        ["maximum"] = new EncryptionSpec("AES-256-GCM", 256, "Maximum strength encryption"),
        ["max"] = new EncryptionSpec("AES-256-GCM", 256, "Maximum strength encryption"),
        ["strong"] = new EncryptionSpec("AES-256-CBC", 256, "Strong encryption"),
        ["secure"] = new EncryptionSpec("AES-256-CBC", 256, "Secure encryption"),
        ["standard"] = new EncryptionSpec("AES-128-GCM", 128, "Standard encryption"),
        ["normal"] = new EncryptionSpec("AES-128-GCM", 128, "Standard encryption"),
        ["default"] = new EncryptionSpec("AES-128-GCM", 128, "Default encryption"),
        ["light"] = new EncryptionSpec("ChaCha20-Poly1305", 256, "Lightweight but secure"),
        ["fast"] = new EncryptionSpec("ChaCha20-Poly1305", 256, "Fast encryption"),
        ["fips"] = new EncryptionSpec("AES-256-GCM", 256, "FIPS 140-2 compliant"),
        ["hipaa"] = new EncryptionSpec("AES-256-GCM", 256, "HIPAA compliant encryption"),
        ["pci"] = new EncryptionSpec("AES-256-GCM", 256, "PCI-DSS compliant encryption")
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="CommandParser"/> class.
    /// </summary>
    /// <param name="messageBus">Message bus for Intelligence requests.</param>
    /// <param name="customMappings">Optional custom command mappings.</param>
    public CommandParser(IMessageBus? messageBus, Dictionary<string, string>? customMappings = null)
    {
        _messageBus = messageBus;
        _commandMappings = customMappings ?? new Dictionary<string, string>();
        _knownCommands = BuildKnownCommands();
    }

    /// <summary>
    /// Parses a natural language input using Intelligence.
    /// </summary>
    /// <param name="input">The natural language input.</param>
    /// <param name="context">Conversation context for reference resolution.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The parsed command result.</returns>
    public async Task<CommandParseResult> ParseNaturalLanguageAsync(
        string input,
        Dictionary<string, object>? context = null,
        CancellationToken ct = default)
    {
        // First try pattern-based parsing (fast path)
        var patternResult = TryPatternParsing(input);
        if (patternResult != null && patternResult.Success)
            return patternResult;

        // If no message bus, fall back to traditional parsing
        if (_messageBus == null)
        {
            return ParseTraditionalCommand(input);
        }

        try
        {
            // Request intent recognition from Intelligence
            var intentPayload = new Dictionary<string, object>
            {
                ["text"] = input,
                ["context"] = context ?? new Dictionary<string, object>(),
                ["domain"] = "datawarehouse_cli",
                ["intents"] = GetSupportedIntents()
            };

            var intentResponse = await _messageBus.RequestAsync(
                IntelligenceTopics.RequestIntent,
                intentPayload,
                ct);

            if (intentResponse != null &&
                intentResponse.TryGetValue("intent", out var intentObj) &&
                intentObj is string intent &&
                !string.IsNullOrEmpty(intent))
            {
                return await ProcessRecognizedIntentAsync(intent, input, intentResponse, context, ct);
            }

            // If intent recognition fails, try entity extraction
            return await ParseWithEntityExtractionAsync(input, context, ct);
        }
        catch (Exception ex)
        {
            return new CommandParseResult
            {
                Success = false,
                ErrorMessage = $"Failed to parse natural language: {ex.Message}",
                Suggestions = new[] { "Try using traditional command syntax.", "Type 'help' for available commands." }
            };
        }
    }

    /// <summary>
    /// Parses a traditional command line input.
    /// </summary>
    /// <param name="input">The command line input.</param>
    /// <returns>The parsed command result.</returns>
    public CommandParseResult ParseTraditionalCommand(string input)
    {
        var tokens = TokenizeCommand(input);
        if (tokens.Length == 0)
        {
            return new CommandParseResult
            {
                Success = false,
                ErrorMessage = "Empty command."
            };
        }

        var command = tokens[0].ToLowerInvariant();

        // Check if command exists
        if (!_knownCommands.TryGetValue(command, out var definition))
        {
            // Check for aliases
            var alias = FindCommandAlias(command);
            if (alias != null && _knownCommands.TryGetValue(alias, out definition))
            {
                command = alias;
            }
            else
            {
                return new CommandParseResult
                {
                    Success = false,
                    ErrorMessage = $"Unknown command: {command}",
                    Suggestions = GetSimilarCommands(command)
                };
            }
        }

        // Parse parameters
        var parameters = ParseParameters(tokens.Skip(1).ToArray(), definition);

        return new CommandParseResult
        {
            Success = true,
            Command = command,
            Parameters = parameters,
            OriginalInput = input
        };
    }

    private CommandParseResult? TryPatternParsing(string input)
    {
        var lower = input.ToLowerInvariant().Trim();

        // Upload patterns
        if (TryMatchUploadPattern(lower, out var uploadParams))
        {
            return new CommandParseResult
            {
                Success = true,
                Command = "upload",
                Parameters = uploadParams,
                OriginalInput = input,
                InterpretedAs = "Upload file"
            };
        }

        // Search patterns
        if (TryMatchSearchPattern(lower, out var searchParams))
        {
            return new CommandParseResult
            {
                Success = true,
                Command = "search",
                Parameters = searchParams,
                OriginalInput = input,
                InterpretedAs = "Semantic search"
            };
        }

        // Encrypt patterns
        if (TryMatchEncryptPattern(lower, out var encryptParams))
        {
            return new CommandParseResult
            {
                Success = true,
                Command = "encrypt",
                Parameters = encryptParams,
                OriginalInput = input,
                InterpretedAs = $"Encrypt with {encryptParams.GetValueOrDefault("cipher", "AES-256-GCM")}"
            };
        }

        // Download patterns
        if (TryMatchDownloadPattern(lower, out var downloadParams))
        {
            return new CommandParseResult
            {
                Success = true,
                Command = "download",
                Parameters = downloadParams,
                OriginalInput = input,
                InterpretedAs = "Download file"
            };
        }

        // List/show patterns
        if (TryMatchListPattern(lower, out var listParams))
        {
            return new CommandParseResult
            {
                Success = true,
                Command = "list",
                Parameters = listParams,
                OriginalInput = input,
                InterpretedAs = "List files"
            };
        }

        // Delete patterns
        if (TryMatchDeletePattern(lower, out var deleteParams))
        {
            return new CommandParseResult
            {
                Success = true,
                Command = "delete",
                Parameters = deleteParams,
                OriginalInput = input,
                InterpretedAs = "Delete file"
            };
        }

        return null;
    }

    private bool TryMatchUploadPattern(string input, out Dictionary<string, object> parameters)
    {
        parameters = new Dictionary<string, object>();

        // "upload this file", "put my file", "store the document"
        var patterns = new[]
        {
            @"(?:upload|put|store|save)\s+(?:this|the|my|a)?\s*(?:file|document|data)?\s*[""']?([^""']+)[""']?",
            @"(?:upload|put|store|save)\s+[""']?([^""']+)[""']?",
            @"send\s+[""']?([^""']+)[""']?\s+to\s+(?:storage|warehouse|cloud)"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(input, pattern, RegexOptions.IgnoreCase);
            if (match.Success && match.Groups.Count > 1)
            {
                var path = match.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(path))
                {
                    parameters["path"] = path;
                    return true;
                }
            }
        }

        // Simple upload check
        if (input.Contains("upload") || input.Contains("put ") || input.StartsWith("store "))
        {
            // Extract file path if quoted
            var quoted = Regex.Match(input, @"[""']([^""']+)[""']");
            if (quoted.Success)
            {
                parameters["path"] = quoted.Groups[1].Value;
                return true;
            }
        }

        return false;
    }

    private bool TryMatchSearchPattern(string input, out Dictionary<string, object> parameters)
    {
        parameters = new Dictionary<string, object>();

        // "find documents about X", "search for Y", "look for Z"
        var patterns = new[]
        {
            @"(?:find|search|look)\s+(?:for\s+)?(?:documents?|files?|data)?\s*(?:about|related to|regarding|containing|with)?\s*[""']?(.+?)[""']?\s*$",
            @"(?:search|find)\s+[""'](.+?)[""']",
            @"(?:search|find)\s+(.+)"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(input, pattern, RegexOptions.IgnoreCase);
            if (match.Success && match.Groups.Count > 1)
            {
                var query = match.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(query))
                {
                    parameters["query"] = query;
                    parameters["semantic"] = true;
                    return true;
                }
            }
        }

        return false;
    }

    private bool TryMatchEncryptPattern(string input, out Dictionary<string, object> parameters)
    {
        parameters = new Dictionary<string, object>();

        // Check for encryption strength keywords
        foreach (var mapping in EncryptionMappings)
        {
            if (input.Contains(mapping.Key))
            {
                parameters["cipher"] = mapping.Value.Cipher;
                parameters["keySize"] = mapping.Value.KeySize;
                parameters["description"] = mapping.Value.Description;

                // Try to extract file path
                var pathMatch = Regex.Match(input, @"(?:encrypt|protect)\s+[""']?([^""']+?)[""']?\s+(?:with|using)", RegexOptions.IgnoreCase);
                if (pathMatch.Success)
                {
                    parameters["path"] = pathMatch.Groups[1].Value.Trim();
                }

                return true;
            }
        }

        // "encrypt file X", "protect document Y"
        var patterns = new[]
        {
            @"(?:encrypt|protect|secure)\s+(?:the\s+)?(?:file|document|data)?\s*[""']?([^""']+)[""']?",
            @"(?:encrypt|protect|secure)\s+[""']?([^""']+)[""']?"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(input, pattern, RegexOptions.IgnoreCase);
            if (match.Success && match.Groups.Count > 1)
            {
                var path = match.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(path) && !EncryptionMappings.ContainsKey(path))
                {
                    parameters["path"] = path;
                    parameters["cipher"] = "AES-256-GCM"; // Default to military-grade
                    parameters["keySize"] = 256;
                    return true;
                }
            }
        }

        // Check if "encrypt everything" or "encrypt all"
        if (input.Contains("encrypt everything") || input.Contains("encrypt all"))
        {
            parameters["scope"] = "all";
            parameters["cipher"] = "AES-256-GCM";
            parameters["keySize"] = 256;
            return true;
        }

        return false;
    }

    private bool TryMatchDownloadPattern(string input, out Dictionary<string, object> parameters)
    {
        parameters = new Dictionary<string, object>();

        var patterns = new[]
        {
            @"(?:download|get|fetch|retrieve)\s+(?:the\s+)?(?:file|document|blob)?\s*[""']?([^""']+)[""']?",
            @"(?:download|get|fetch)\s+[""']?([^""']+)[""']?"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(input, pattern, RegexOptions.IgnoreCase);
            if (match.Success && match.Groups.Count > 1)
            {
                var id = match.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(id))
                {
                    parameters["id"] = id;
                    return true;
                }
            }
        }

        return false;
    }

    private bool TryMatchListPattern(string input, out Dictionary<string, object> parameters)
    {
        parameters = new Dictionary<string, object>();

        var patterns = new[]
        {
            @"(?:list|show|display|view)\s+(?:all\s+)?(?:files?|documents?|blobs?|data)?(?:\s+in\s+)?[""']?([^""']*)[""']?",
            @"(?:ls|dir)\s*[""']?([^""']*)[""']?"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(input, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var path = match.Groups.Count > 1 ? match.Groups[1].Value.Trim() : "";
                parameters["path"] = path;
                return true;
            }
        }

        if (input.Contains("list") || input.Contains("show files") || input.Contains("what files"))
        {
            parameters["path"] = "";
            return true;
        }

        return false;
    }

    private bool TryMatchDeletePattern(string input, out Dictionary<string, object> parameters)
    {
        parameters = new Dictionary<string, object>();

        var patterns = new[]
        {
            @"(?:delete|remove|erase|destroy)\s+(?:the\s+)?(?:file|document|blob)?\s*[""']?([^""']+)[""']?",
            @"(?:rm|del)\s+[""']?([^""']+)[""']?"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(input, pattern, RegexOptions.IgnoreCase);
            if (match.Success && match.Groups.Count > 1)
            {
                var id = match.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(id))
                {
                    parameters["id"] = id;
                    return true;
                }
            }
        }

        return false;
    }

    private async Task<CommandParseResult> ProcessRecognizedIntentAsync(
        string intent,
        string input,
        Dictionary<string, object> response,
        Dictionary<string, object>? context,
        CancellationToken ct)
    {
        var parameters = new Dictionary<string, object>();

        // Extract entities from the response
        if (response.TryGetValue("entities", out var entitiesObj) &&
            entitiesObj is Dictionary<string, object> entities)
        {
            foreach (var entity in entities)
            {
                parameters[entity.Key] = entity.Value;
            }
        }

        // Map intent to command
        var command = MapIntentToCommand(intent);

        // Post-process based on intent type
        switch (intent.ToLowerInvariant())
        {
            case "encrypt":
            case "encrypt_file":
            case "protect":
                EnrichEncryptionParameters(input, parameters);
                break;

            case "search":
            case "find":
            case "query":
                if (!parameters.ContainsKey("query"))
                {
                    // Try to extract query from response
                    if (response.TryGetValue("query", out var queryObj))
                        parameters["query"] = queryObj;
                    else
                        parameters["query"] = ExtractSearchQuery(input);
                }
                parameters["semantic"] = true;
                break;

            case "upload":
            case "store":
                // Resolve file references from context
                await ResolveFileReferenceAsync(parameters, context, ct);
                break;
        }

        // Resolve context references (e.g., "that file", "the result")
        await ResolveContextReferencesAsync(parameters, context, ct);

        return new CommandParseResult
        {
            Success = true,
            Command = command,
            Parameters = parameters,
            OriginalInput = input,
            InterpretedAs = $"Intent: {intent}"
        };
    }

    private async Task<CommandParseResult> ParseWithEntityExtractionAsync(
        string input,
        Dictionary<string, object>? context,
        CancellationToken ct)
    {
        if (_messageBus == null)
        {
            return ParseTraditionalCommand(input);
        }

        try
        {
            var payload = new Dictionary<string, object>
            {
                ["text"] = input,
                ["entityTypes"] = new[] { "FILE_PATH", "BLOB_ID", "QUERY", "ENCRYPTION", "COMMAND" }
            };

            var response = await _messageBus.RequestAsync(
                IntelligenceTopics.RequestEntityExtraction,
                payload,
                ct);

            if (response != null &&
                response.TryGetValue("entities", out var entitiesObj))
            {
                // Determine command from entities or fall back to keyword analysis
                var command = DetermineCommandFromInput(input);
                var parameters = ExtractParametersFromEntities(entitiesObj, input);

                return new CommandParseResult
                {
                    Success = true,
                    Command = command,
                    Parameters = parameters,
                    OriginalInput = input
                };
            }
        }
        catch
        {
            // Fall back to traditional parsing
        }

        return ParseTraditionalCommand(input);
    }

    private void EnrichEncryptionParameters(string input, Dictionary<string, object> parameters)
    {
        // Check for encryption strength keywords
        foreach (var mapping in EncryptionMappings)
        {
            if (input.Contains(mapping.Key, StringComparison.OrdinalIgnoreCase))
            {
                parameters["cipher"] = mapping.Value.Cipher;
                parameters["keySize"] = mapping.Value.KeySize;
                return;
            }
        }

        // Default to military-grade if not specified
        if (!parameters.ContainsKey("cipher"))
        {
            parameters["cipher"] = "AES-256-GCM";
            parameters["keySize"] = 256;
        }
    }

    private string ExtractSearchQuery(string input)
    {
        // Remove common search prefixes
        var prefixes = new[] { "find", "search", "look for", "search for", "find documents about", "search documents" };
        var result = input;

        foreach (var prefix in prefixes)
        {
            if (result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                result = result.Substring(prefix.Length).Trim();
                break;
            }
        }

        // Remove quotes
        result = result.Trim('"', '\'');

        return result;
    }

    private async Task ResolveFileReferenceAsync(
        Dictionary<string, object> parameters,
        Dictionary<string, object>? context,
        CancellationToken ct)
    {
        if (!parameters.ContainsKey("path") && context != null)
        {
            // Check for "this file", "that file" references
            if (context.TryGetValue("lastUploadedFile", out var lastFile))
            {
                parameters["path"] = lastFile;
            }
            else if (context.TryGetValue("currentFile", out var currentFile))
            {
                parameters["path"] = currentFile;
            }
        }
    }

    private async Task ResolveContextReferencesAsync(
        Dictionary<string, object> parameters,
        Dictionary<string, object>? context,
        CancellationToken ct)
    {
        if (context == null) return;

        // Resolve "it", "that", "the result" references
        foreach (var key in parameters.Keys.ToList())
        {
            if (parameters[key] is string value)
            {
                var lower = value.ToLowerInvariant();
                if (lower == "it" || lower == "that" || lower == "the result" || lower == "them")
                {
                    // Try to resolve from context
                    if (context.TryGetValue("lastSearchResults", out var results))
                    {
                        parameters[key] = results;
                    }
                    else if (context.TryGetValue("lastUploadedFile", out var file))
                    {
                        parameters[key] = file;
                    }
                }
            }
        }
    }

    private string MapIntentToCommand(string intent)
    {
        return intent.ToLowerInvariant() switch
        {
            "upload" or "store" or "put" or "save" => "upload",
            "download" or "get" or "fetch" or "retrieve" => "download",
            "search" or "find" or "query" or "look" => "search",
            "list" or "show" or "display" or "view" => "list",
            "delete" or "remove" or "erase" => "delete",
            "encrypt" or "protect" or "secure" or "encrypt_file" => "encrypt",
            "decrypt" or "unprotect" or "unlock" => "decrypt",
            "compress" or "zip" or "compact" => "compress",
            "metadata" or "info" or "properties" => "metadata",
            "audit" or "compliance" or "check" => "audit",
            _ => intent.ToLowerInvariant()
        };
    }

    private string DetermineCommandFromInput(string input)
    {
        var lower = input.ToLowerInvariant();

        if (lower.Contains("upload") || lower.Contains("put ") || lower.Contains("store"))
            return "upload";
        if (lower.Contains("download") || lower.Contains("get ") || lower.Contains("fetch"))
            return "download";
        if (lower.Contains("search") || lower.Contains("find ") || lower.Contains("look"))
            return "search";
        if (lower.Contains("list") || lower.Contains("show") || lower.Contains("display"))
            return "list";
        if (lower.Contains("delete") || lower.Contains("remove"))
            return "delete";
        if (lower.Contains("encrypt") || lower.Contains("protect"))
            return "encrypt";
        if (lower.Contains("decrypt") || lower.Contains("unlock"))
            return "decrypt";

        return "unknown";
    }

    private Dictionary<string, object> ExtractParametersFromEntities(object entitiesObj, string input)
    {
        var parameters = new Dictionary<string, object>();

        if (entitiesObj is IEnumerable<object> entities)
        {
            foreach (var entity in entities)
            {
                if (entity is Dictionary<string, object> entityDict)
                {
                    var type = entityDict.GetValueOrDefault("type", "")?.ToString()?.ToLowerInvariant();
                    var value = entityDict.GetValueOrDefault("value", entityDict.GetValueOrDefault("text", ""));

                    switch (type)
                    {
                        case "file_path":
                        case "path":
                            parameters["path"] = value ?? string.Empty;
                            break;
                        case "blob_id":
                        case "id":
                            parameters["id"] = value ?? string.Empty;
                            break;
                        case "query":
                        case "search_term":
                            parameters["query"] = value ?? string.Empty;
                            break;
                    }
                }
            }
        }

        return parameters;
    }

    private string[] GetSupportedIntents()
    {
        return new[]
        {
            "upload", "download", "search", "list", "delete",
            "encrypt", "decrypt", "compress", "decompress",
            "metadata", "audit", "compliance", "tier",
            "help", "status"
        };
    }

    private string[] TokenizeCommand(string input)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var quoteChar = ' ';

        foreach (var c in input)
        {
            if ((c == '"' || c == '\'') && !inQuotes)
            {
                inQuotes = true;
                quoteChar = c;
            }
            else if (c == quoteChar && inQuotes)
            {
                inQuotes = false;
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens.ToArray();
    }

    private Dictionary<string, object> ParseParameters(string[] args, CommandDefinition definition)
    {
        var parameters = new Dictionary<string, object>();
        var positionalIndex = 0;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg.StartsWith("--"))
            {
                var key = arg.Substring(2);
                if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                {
                    parameters[key] = args[++i];
                }
                else
                {
                    parameters[key] = true;
                }
            }
            else if (arg.StartsWith("-"))
            {
                var key = arg.Substring(1);
                if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                {
                    parameters[key] = args[++i];
                }
                else
                {
                    parameters[key] = true;
                }
            }
            else
            {
                // Positional argument
                if (positionalIndex < definition.PositionalParams.Count)
                {
                    parameters[definition.PositionalParams[positionalIndex]] = arg;
                    positionalIndex++;
                }
                else
                {
                    parameters[$"arg{positionalIndex}"] = arg;
                    positionalIndex++;
                }
            }
        }

        return parameters;
    }

    private string? FindCommandAlias(string command)
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["put"] = "upload",
            ["get"] = "download",
            ["ls"] = "list",
            ["dir"] = "list",
            ["rm"] = "delete",
            ["del"] = "delete",
            ["find"] = "search",
            ["protect"] = "encrypt",
            ["secure"] = "encrypt",
            ["unlock"] = "decrypt",
            ["unprotect"] = "decrypt",
            ["zip"] = "compress",
            ["unzip"] = "decompress",
            ["info"] = "metadata",
            ["meta"] = "metadata",
            ["?"] = "help"
        };

        return aliases.TryGetValue(command, out var alias) ? alias : null;
    }

    private string[] GetSimilarCommands(string command)
    {
        var suggestions = new List<string>();
        var commands = _knownCommands.Keys.ToList();

        foreach (var known in commands)
        {
            if (LevenshteinDistance(command, known) <= 2)
            {
                suggestions.Add($"Did you mean '{known}'?");
            }
        }

        if (suggestions.Count == 0)
        {
            suggestions.Add("Type 'help' to see available commands.");
        }

        return suggestions.ToArray();
    }

    private static int LevenshteinDistance(string s1, string s2)
    {
        var m = s1.Length;
        var n = s2.Length;
        var d = new int[m + 1, n + 1];

        for (var i = 0; i <= m; i++) d[i, 0] = i;
        for (var j = 0; j <= n; j++) d[0, j] = j;

        for (var i = 1; i <= m; i++)
        {
            for (var j = 1; j <= n; j++)
            {
                var cost = s1[i - 1] == s2[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[m, n];
    }

    private Dictionary<string, CommandDefinition> BuildKnownCommands()
    {
        return new Dictionary<string, CommandDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["upload"] = new CommandDefinition
            {
                Name = "upload",
                Description = "Upload a file to storage",
                PositionalParams = new List<string> { "path" },
                NamedParams = new List<string> { "container", "metadata", "encrypt" }
            },
            ["download"] = new CommandDefinition
            {
                Name = "download",
                Description = "Download a file from storage",
                PositionalParams = new List<string> { "id" },
                NamedParams = new List<string> { "output", "decrypt" }
            },
            ["list"] = new CommandDefinition
            {
                Name = "list",
                Description = "List files in storage",
                PositionalParams = new List<string> { "path" },
                NamedParams = new List<string> { "recursive", "format", "filter" }
            },
            ["search"] = new CommandDefinition
            {
                Name = "search",
                Description = "Search for files",
                PositionalParams = new List<string> { "query" },
                NamedParams = new List<string> { "semantic", "limit", "filter" }
            },
            ["delete"] = new CommandDefinition
            {
                Name = "delete",
                Description = "Delete a file from storage",
                PositionalParams = new List<string> { "id" },
                NamedParams = new List<string> { "force", "recursive" }
            },
            ["encrypt"] = new CommandDefinition
            {
                Name = "encrypt",
                Description = "Encrypt a file",
                PositionalParams = new List<string> { "path" },
                NamedParams = new List<string> { "cipher", "keySize", "output" }
            },
            ["decrypt"] = new CommandDefinition
            {
                Name = "decrypt",
                Description = "Decrypt a file",
                PositionalParams = new List<string> { "path" },
                NamedParams = new List<string> { "output", "key" }
            },
            ["compress"] = new CommandDefinition
            {
                Name = "compress",
                Description = "Compress a file",
                PositionalParams = new List<string> { "path" },
                NamedParams = new List<string> { "algorithm", "level", "output" }
            },
            ["metadata"] = new CommandDefinition
            {
                Name = "metadata",
                Description = "View or edit file metadata",
                PositionalParams = new List<string> { "id" },
                NamedParams = new List<string> { "set", "get", "format" }
            },
            ["audit"] = new CommandDefinition
            {
                Name = "audit",
                Description = "Run compliance audit",
                PositionalParams = new List<string> { "scope" },
                NamedParams = new List<string> { "framework", "output", "severity" }
            },
            ["help"] = new CommandDefinition
            {
                Name = "help",
                Description = "Show help information",
                PositionalParams = new List<string> { "command" },
                NamedParams = new List<string>()
            }
        };
    }
}

/// <summary>
/// Result of command parsing.
/// </summary>
public sealed class CommandParseResult
{
    /// <summary>Gets or sets whether parsing was successful.</summary>
    public bool Success { get; init; }

    /// <summary>Gets or sets the parsed command name.</summary>
    public string Command { get; init; } = string.Empty;

    /// <summary>Gets or sets the parsed parameters.</summary>
    public Dictionary<string, object> Parameters { get; init; } = new();

    /// <summary>Gets or sets the original input.</summary>
    public string OriginalInput { get; init; } = string.Empty;

    /// <summary>Gets or sets the interpretation description.</summary>
    public string? InterpretedAs { get; init; }

    /// <summary>Gets or sets the error message if parsing failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Gets or sets suggestions for the user.</summary>
    public string[]? Suggestions { get; init; }
}

/// <summary>
/// Definition of a known command.
/// </summary>
internal sealed class CommandDefinition
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public List<string> PositionalParams { get; init; } = new();
    public List<string> NamedParams { get; init; } = new();
}

/// <summary>
/// Encryption specification for natural language mapping.
/// </summary>
internal sealed record EncryptionSpec(string Cipher, int KeySize, string Description);
