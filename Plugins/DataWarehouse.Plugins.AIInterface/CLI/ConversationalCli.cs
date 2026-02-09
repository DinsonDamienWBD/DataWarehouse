// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Plugins.AIInterface.Channels;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.AIInterface.CLI;

/// <summary>
/// Conversational CLI interface that enables natural language command interaction
/// when Intelligence is available, with graceful fallback to traditional commands.
/// </summary>
/// <remarks>
/// <para>
/// The ConversationalCli provides an AI-powered command-line experience:
/// <list type="bullet">
/// <item>"Upload this file" translates to <c>PUT /blobs</c> with AI-parsed parameters</item>
/// <item>"Find documents about Q4 sales" becomes a semantic search query</item>
/// <item>"Encrypt everything with military-grade" selects AES-256-GCM automatically</item>
/// <item>Context-aware follow-ups remember previous conversation state</item>
/// </list>
/// </para>
/// <para>
/// When Intelligence is unavailable, the CLI falls back to traditional command parsing
/// with helpful suggestions for expected command formats.
/// </para>
/// </remarks>
public sealed class ConversationalCli : IDisposable
{
    private readonly IMessageBus? _messageBus;
    private readonly CommandParser _commandParser;
    private readonly ConversationContext _conversationContext;
    private readonly CliAgentSpawner _agentSpawner;
    private readonly ConcurrentDictionary<string, CliSession> _sessions = new();
    private bool _intelligenceAvailable;
    private IntelligenceCapabilities _capabilities = IntelligenceCapabilities.None;
    private bool _disposed;

    /// <summary>
    /// Gets whether Intelligence is currently available for natural language processing.
    /// </summary>
    public bool IsIntelligenceAvailable => _intelligenceAvailable;

    /// <summary>
    /// Gets the current Intelligence capabilities.
    /// </summary>
    public IntelligenceCapabilities Capabilities => _capabilities;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConversationalCli"/> class.
    /// </summary>
    /// <param name="messageBus">Message bus for Intelligence communication.</param>
    /// <param name="config">Optional CLI configuration.</param>
    public ConversationalCli(IMessageBus? messageBus, ConversationalCliConfig? config = null)
    {
        _messageBus = messageBus;
        config ??= new ConversationalCliConfig();

        _commandParser = new CommandParser(messageBus, config.CommandMappings);
        _conversationContext = new ConversationContext(config.MaxHistorySize);
        _agentSpawner = new CliAgentSpawner(messageBus);
    }

    /// <summary>
    /// Initializes the CLI and discovers Intelligence availability.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task representing initialization completion.</returns>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_messageBus == null)
        {
            _intelligenceAvailable = false;
            return;
        }

        try
        {
            // Discover Intelligence via message bus
            var response = await _messageBus.RequestAsync(
                IntelligenceTopics.Discover,
                new Dictionary<string, object>
                {
                    ["requestorId"] = "cli.conversational",
                    ["requestorName"] = "ConversationalCli",
                    ["timestamp"] = DateTimeOffset.UtcNow
                },
                ct);

            if (response != null &&
                response.TryGetValue("available", out var available) && available is true)
            {
                _intelligenceAvailable = true;

                if (response.TryGetValue("capabilities", out var caps))
                {
                    if (caps is IntelligenceCapabilities ic)
                        _capabilities = ic;
                    else if (caps is long longVal)
                        _capabilities = (IntelligenceCapabilities)longVal;
                    else if (long.TryParse(caps?.ToString(), out var parsed))
                        _capabilities = (IntelligenceCapabilities)parsed;
                }
            }
            else
            {
                _intelligenceAvailable = false;
            }
        }
        catch
        {
            _intelligenceAvailable = false;
        }
    }

    /// <summary>
    /// Processes a natural language or traditional command input.
    /// </summary>
    /// <param name="input">The user input (natural language or command).</param>
    /// <param name="sessionId">Optional session ID for context tracking.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The CLI response containing result and any suggestions.</returns>
    public async Task<CliResponse> ProcessInputAsync(
        string input,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new CliResponse
            {
                Success = true,
                Message = "Enter a command or natural language request. Type 'help' for options."
            };
        }

        var session = GetOrCreateSession(sessionId ?? Guid.NewGuid().ToString("N"));
        var trimmedInput = input.Trim();

        // Check for built-in commands first
        var builtInResult = await HandleBuiltInCommandAsync(trimmedInput, session, ct);
        if (builtInResult != null)
            return builtInResult;

        // Check for agent mode commands
        if (IsAgentCommand(trimmedInput))
        {
            return await HandleAgentCommandAsync(trimmedInput, session, ct);
        }

        // If Intelligence is available, use NL processing
        if (_intelligenceAvailable && HasNlpCapability())
        {
            return await ProcessNaturalLanguageAsync(trimmedInput, session, ct);
        }

        // Fallback to traditional command parsing
        return await ProcessTraditionalCommandAsync(trimmedInput, session, ct);
    }

    /// <summary>
    /// Gets or creates a session for the given session ID.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <returns>The CLI session.</returns>
    public CliSession GetOrCreateSession(string sessionId)
    {
        return _sessions.GetOrAdd(sessionId, id => new CliSession
        {
            SessionId = id,
            CreatedAt = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Clears the session history.
    /// </summary>
    /// <param name="sessionId">The session ID to clear.</param>
    public void ClearSession(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            _conversationContext.ClearHistory(sessionId);
            session.LastInteraction = null;
            session.CurrentContext.Clear();
        }
    }

    /// <summary>
    /// Gets the conversation history for a session.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <returns>The conversation history.</returns>
    public IReadOnlyList<ConversationTurn> GetHistory(string sessionId)
    {
        return _conversationContext.GetHistory(sessionId);
    }

    private bool HasNlpCapability()
    {
        return (_capabilities & (IntelligenceCapabilities.NLP |
                                IntelligenceCapabilities.IntentRecognition |
                                IntelligenceCapabilities.Conversation)) != 0;
    }

    private bool IsAgentCommand(string input)
    {
        var lower = input.ToLowerInvariant();
        return lower.StartsWith("agent ") ||
               lower.StartsWith("organize ") ||
               lower.StartsWith("optimize ") ||
               lower.StartsWith("check ") ||
               lower.StartsWith("analyze ") ||
               lower.StartsWith("audit ");
    }

    private async Task<CliResponse?> HandleBuiltInCommandAsync(
        string input,
        CliSession session,
        CancellationToken ct)
    {
        var lower = input.ToLowerInvariant();

        if (lower == "help" || lower == "?")
        {
            return GetHelpResponse();
        }

        if (lower == "status" || lower == "intelligence status")
        {
            return GetIntelligenceStatusResponse();
        }

        if (lower == "history")
        {
            return GetHistoryResponse(session.SessionId);
        }

        if (lower == "clear" || lower == "clear history")
        {
            ClearSession(session.SessionId);
            return new CliResponse
            {
                Success = true,
                Message = "Conversation history cleared."
            };
        }

        if (lower == "exit" || lower == "quit")
        {
            return new CliResponse
            {
                Success = true,
                Message = "Goodbye!",
                ShouldExit = true
            };
        }

        return null;
    }

    private async Task<CliResponse> HandleAgentCommandAsync(
        string input,
        CliSession session,
        CancellationToken ct)
    {
        if (!_intelligenceAvailable)
        {
            return new CliResponse
            {
                Success = false,
                Message = "Agent mode requires Intelligence to be available. Use 'status' to check.",
                Suggestions = new[] { "Check your Intelligence plugin configuration." }
            };
        }

        if (!HasAgentCapability())
        {
            return new CliResponse
            {
                Success = false,
                Message = "Agent capabilities are not available in the current Intelligence configuration.",
                Suggestions = new[] { "Enable agent capabilities in the Intelligence plugin." }
            };
        }

        return await _agentSpawner.SpawnAgentAsync(input, session, ct);
    }

    private bool HasAgentCapability()
    {
        return (_capabilities & (IntelligenceCapabilities.TaskPlanning |
                                IntelligenceCapabilities.ToolUse |
                                IntelligenceCapabilities.ReasoningChain)) != 0;
    }

    private async Task<CliResponse> ProcessNaturalLanguageAsync(
        string input,
        CliSession session,
        CancellationToken ct)
    {
        if (_messageBus == null)
        {
            return new CliResponse
            {
                Success = false,
                Message = "Message bus not available."
            };
        }

        // Add to conversation history
        _conversationContext.AddUserTurn(session.SessionId, input);

        // Get conversation context for the request
        var history = _conversationContext.GetHistory(session.SessionId);
        var contextPayload = BuildConversationPayload(history);

        try
        {
            // Parse the natural language input to determine intent
            var parseResult = await _commandParser.ParseNaturalLanguageAsync(input, contextPayload, ct);

            if (!parseResult.Success)
            {
                var response = new CliResponse
                {
                    Success = false,
                    Message = parseResult.ErrorMessage ?? "Could not understand the request.",
                    Suggestions = parseResult.Suggestions
                };
                _conversationContext.AddAssistantTurn(session.SessionId, response.Message);
                return response;
            }

            // Execute the parsed command
            var executionResult = await ExecuteParsedCommandAsync(parseResult, session, ct);

            // Add to history
            _conversationContext.AddAssistantTurn(session.SessionId, executionResult.Message);

            // Update session context
            UpdateSessionContext(session, parseResult, executionResult);

            return executionResult;
        }
        catch (Exception ex)
        {
            var response = new CliResponse
            {
                Success = false,
                Message = $"Error processing request: {ex.Message}",
                Suggestions = new[] { "Try rephrasing your request.", "Use 'help' to see available commands." }
            };
            _conversationContext.AddAssistantTurn(session.SessionId, response.Message);
            return response;
        }
    }

    private async Task<CliResponse> ProcessTraditionalCommandAsync(
        string input,
        CliSession session,
        CancellationToken ct)
    {
        // Parse as traditional command
        var parseResult = _commandParser.ParseTraditionalCommand(input);

        if (!parseResult.Success)
        {
            return new CliResponse
            {
                Success = false,
                Message = parseResult.ErrorMessage ?? $"Unknown command: {input}",
                Suggestions = parseResult.Suggestions ?? GetCommandSuggestions(input)
            };
        }

        return await ExecuteParsedCommandAsync(parseResult, session, ct);
    }

    private async Task<CliResponse> ExecuteParsedCommandAsync(
        CommandParseResult parseResult,
        CliSession session,
        CancellationToken ct)
    {
        if (_messageBus == null)
        {
            return new CliResponse
            {
                Success = false,
                Message = "Message bus not configured."
            };
        }

        var payload = new Dictionary<string, object>
        {
            ["command"] = parseResult.Command,
            ["parameters"] = parseResult.Parameters,
            ["sessionId"] = session.SessionId
        };

        // Add context from session
        foreach (var kvp in session.CurrentContext)
        {
            if (!payload.ContainsKey(kvp.Key))
                payload[kvp.Key] = kvp.Value;
        }

        try
        {
            // Route to appropriate handler based on command type
            var topic = GetTopicForCommand(parseResult.Command);
            var response = await _messageBus.RequestAsync(topic, payload, ct);

            if (response == null)
            {
                return new CliResponse
                {
                    Success = false,
                    Message = $"No response from {parseResult.Command} handler."
                };
            }

            var success = !response.ContainsKey("error");
            var message = response.TryGetValue("message", out var msg) ? msg?.ToString()
                        : response.TryGetValue("result", out var res) ? res?.ToString()
                        : success ? $"Command '{parseResult.Command}' executed successfully."
                        : response.TryGetValue("error", out var err) ? err?.ToString()
                        : "Unknown error";

            return new CliResponse
            {
                Success = success,
                Message = message ?? string.Empty,
                Data = response.TryGetValue("data", out var data) ? data : null,
                Suggestions = ExtractSuggestions(response)
            };
        }
        catch (Exception ex)
        {
            return new CliResponse
            {
                Success = false,
                Message = $"Error executing command: {ex.Message}"
            };
        }
    }

    private Dictionary<string, object> BuildConversationPayload(IReadOnlyList<ConversationTurn> history)
    {
        var messages = history.Select(t => new Dictionary<string, object>
        {
            ["role"] = t.Role,
            ["content"] = t.Content,
            ["timestamp"] = t.Timestamp
        }).ToList();

        return new Dictionary<string, object>
        {
            ["history"] = messages,
            ["turnCount"] = history.Count
        };
    }

    private void UpdateSessionContext(
        CliSession session,
        CommandParseResult parseResult,
        CliResponse executionResult)
    {
        session.LastInteraction = DateTime.UtcNow;

        // Store context-relevant information for follow-ups
        if (parseResult.Command == "search" && executionResult.Success)
        {
            session.CurrentContext["lastSearchQuery"] = parseResult.Parameters.GetValueOrDefault("query", string.Empty);
            if (executionResult.Data != null)
                session.CurrentContext["lastSearchResults"] = executionResult.Data;
        }
        else if (parseResult.Command == "upload" && executionResult.Success)
        {
            session.CurrentContext["lastUploadedFile"] = parseResult.Parameters.GetValueOrDefault("path", string.Empty);
        }
        else if (parseResult.Command == "encrypt" && executionResult.Success)
        {
            session.CurrentContext["lastEncryptionCipher"] = parseResult.Parameters.GetValueOrDefault("cipher", string.Empty);
        }
    }

    private string GetTopicForCommand(string command)
    {
        return command.ToLowerInvariant() switch
        {
            "upload" or "put" => "storage.upload",
            "download" or "get" => "storage.download",
            "delete" or "remove" => "storage.delete",
            "list" or "ls" => "storage.list",
            "search" or "find" => IntelligenceTopics.RequestSemanticSearch,
            "encrypt" => "security.encrypt",
            "decrypt" => "security.decrypt",
            "compress" => "storage.compress",
            "tier" => "storage.tier",
            "metadata" or "meta" => "metadata.query",
            "audit" => "security.audit",
            "compliance" => IntelligenceTopics.RequestComplianceClassification,
            _ => $"cli.command.{command}"
        };
    }

    private string[]? ExtractSuggestions(Dictionary<string, object> response)
    {
        if (response.TryGetValue("suggestions", out var suggestions))
        {
            if (suggestions is string[] arr)
                return arr;
            if (suggestions is List<string> list)
                return list.ToArray();
            if (suggestions is IEnumerable<object> enumerable)
                return enumerable.Select(x => x?.ToString() ?? string.Empty).ToArray();
        }
        return null;
    }

    private string[] GetCommandSuggestions(string input)
    {
        var lower = input.ToLowerInvariant();
        var suggestions = new List<string>();

        if (lower.Contains("upload") || lower.Contains("put"))
            suggestions.Add("upload <filepath> - Upload a file");
        if (lower.Contains("find") || lower.Contains("search"))
            suggestions.Add("search \"<query>\" - Search for documents");
        if (lower.Contains("encrypt"))
            suggestions.Add("encrypt <filepath> --cipher aes-256-gcm");
        if (lower.Contains("download") || lower.Contains("get"))
            suggestions.Add("download <blobid> - Download a file");

        if (suggestions.Count == 0)
        {
            suggestions.Add("Type 'help' for available commands");
        }

        return suggestions.ToArray();
    }

    private CliResponse GetHelpResponse()
    {
        var help = new System.Text.StringBuilder();
        help.AppendLine("DataWarehouse CLI - Available Commands:");
        help.AppendLine();
        help.AppendLine("Basic Commands:");
        help.AppendLine("  help, ?            - Show this help");
        help.AppendLine("  status             - Show Intelligence status");
        help.AppendLine("  history            - Show conversation history");
        help.AppendLine("  clear              - Clear conversation history");
        help.AppendLine("  exit, quit         - Exit the CLI");
        help.AppendLine();
        help.AppendLine("Storage Commands:");
        help.AppendLine("  upload <path>      - Upload a file");
        help.AppendLine("  download <id>      - Download a file");
        help.AppendLine("  list [path]        - List files");
        help.AppendLine("  delete <id>        - Delete a file");
        help.AppendLine();
        help.AppendLine("Search Commands:");
        help.AppendLine("  search \"<query>\"   - Search for documents");
        help.AppendLine("  find <pattern>     - Find files by pattern");
        help.AppendLine();
        help.AppendLine("Security Commands:");
        help.AppendLine("  encrypt <path>     - Encrypt a file");
        help.AppendLine("  decrypt <path>     - Decrypt a file");
        help.AppendLine("  audit              - Run compliance audit");

        if (_intelligenceAvailable)
        {
            help.AppendLine();
            help.AppendLine("Natural Language (Intelligence Available):");
            help.AppendLine("  \"Upload my report\"            - Natural language upload");
            help.AppendLine("  \"Find documents about sales\" - Semantic search");
            help.AppendLine("  \"Encrypt with military-grade\" - Smart encryption");
            help.AppendLine();
            help.AppendLine("Agent Commands (Complex Tasks):");
            help.AppendLine("  agent organize my files       - AI organizes file structure");
            help.AppendLine("  agent optimize storage costs  - AI runs tiering analysis");
            help.AppendLine("  agent check compliance        - AI runs compliance audit");
        }

        return new CliResponse
        {
            Success = true,
            Message = help.ToString()
        };
    }

    private CliResponse GetIntelligenceStatusResponse()
    {
        var status = new System.Text.StringBuilder();
        status.AppendLine($"Intelligence Status: {(_intelligenceAvailable ? "AVAILABLE" : "UNAVAILABLE")}");

        if (_intelligenceAvailable)
        {
            status.AppendLine();
            status.AppendLine("Available Capabilities:");

            if ((_capabilities & IntelligenceCapabilities.NLP) != 0)
                status.AppendLine("  [x] Natural Language Processing");
            if ((_capabilities & IntelligenceCapabilities.IntentRecognition) != 0)
                status.AppendLine("  [x] Intent Recognition");
            if ((_capabilities & IntelligenceCapabilities.Conversation) != 0)
                status.AppendLine("  [x] Conversation/Context");
            if ((_capabilities & IntelligenceCapabilities.SemanticSearch) != 0)
                status.AppendLine("  [x] Semantic Search");
            if ((_capabilities & IntelligenceCapabilities.CipherRecommendation) != 0)
                status.AppendLine("  [x] Cipher Recommendation");
            if ((_capabilities & IntelligenceCapabilities.TaskPlanning) != 0)
                status.AppendLine("  [x] Task Planning (Agent Mode)");
            if ((_capabilities & IntelligenceCapabilities.ToolUse) != 0)
                status.AppendLine("  [x] Tool Use (Agent Mode)");
        }
        else
        {
            status.AppendLine();
            status.AppendLine("Natural language commands are unavailable.");
            status.AppendLine("Use traditional command syntax. Type 'help' for options.");
        }

        return new CliResponse
        {
            Success = true,
            Message = status.ToString()
        };
    }

    private CliResponse GetHistoryResponse(string sessionId)
    {
        var history = _conversationContext.GetHistory(sessionId);

        if (history.Count == 0)
        {
            return new CliResponse
            {
                Success = true,
                Message = "No conversation history."
            };
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Conversation History:");
        sb.AppendLine();

        foreach (var turn in history.TakeLast(10))
        {
            var prefix = turn.Role == "user" ? "You: " : "CLI: ";
            sb.AppendLine($"{prefix}{turn.Content}");
        }

        return new CliResponse
        {
            Success = true,
            Message = sb.ToString()
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _sessions.Clear();
        _conversationContext.Dispose();
        _agentSpawner.Dispose();
    }
}

/// <summary>
/// CLI response containing the result and optional suggestions.
/// </summary>
public sealed class CliResponse
{
    /// <summary>Gets or sets whether the operation succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Gets or sets the response message.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Gets or sets optional structured data.</summary>
    public object? Data { get; init; }

    /// <summary>Gets or sets suggested follow-up actions.</summary>
    public string[]? Suggestions { get; init; }

    /// <summary>Gets or sets whether the CLI should exit.</summary>
    public bool ShouldExit { get; init; }
}

/// <summary>
/// CLI session state for context tracking.
/// </summary>
public sealed class CliSession
{
    /// <summary>Gets or sets the session identifier.</summary>
    public string SessionId { get; init; } = string.Empty;

    /// <summary>Gets or sets when the session was created.</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Gets or sets the last interaction time.</summary>
    public DateTime? LastInteraction { get; set; }

    /// <summary>Gets or sets the current context for follow-up commands.</summary>
    public Dictionary<string, object> CurrentContext { get; init; } = new();
}

/// <summary>
/// Configuration for the conversational CLI.
/// </summary>
public sealed class ConversationalCliConfig
{
    /// <summary>Maximum number of conversation turns to keep in history.</summary>
    public int MaxHistorySize { get; init; } = 50;

    /// <summary>Custom command mappings from natural language patterns.</summary>
    public Dictionary<string, string>? CommandMappings { get; init; }

    /// <summary>Session timeout in minutes.</summary>
    public int SessionTimeoutMinutes { get; init; } = 30;

    /// <summary>Whether to enable verbose mode with debug output.</summary>
    public bool VerboseMode { get; init; }
}
