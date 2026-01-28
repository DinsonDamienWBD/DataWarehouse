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
/// Engine for managing multi-turn conversations with context preservation.
/// Handles session management, pronoun resolution, and follow-up detection.
/// </summary>
/// <remarks>
/// <para>
/// The ConversationContextEngine provides:
/// </para>
/// <list type="bullet">
/// <item><description>Session management per user with automatic cleanup</description></item>
/// <item><description>Context preservation across conversation turns</description></item>
/// <item><description>Pronoun resolution ("it", "that", "those")</description></item>
/// <item><description>Follow-up detection and context application</description></item>
/// <item><description>Entity tracking across turns</description></item>
/// </list>
/// <para>
/// Sessions automatically expire after a configurable timeout and have a maximum
/// turn count to prevent unbounded memory growth.
/// </para>
/// </remarks>
public sealed class ConversationContextEngine : IDisposable
{
    private readonly ConcurrentDictionary<string, ConversationSession> _sessions = new();
    private readonly Timer _cleanupTimer;
    private readonly ConversationConfig _config;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConversationContextEngine"/> class.
    /// </summary>
    /// <param name="config">Optional configuration for conversation management.</param>
    public ConversationContextEngine(ConversationConfig? config = null)
    {
        _config = config ?? new ConversationConfig();

        // Cleanup expired sessions periodically
        _cleanupTimer = new Timer(
            CleanupExpiredSessions,
            null,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(5));
    }

    #region Session Management

    /// <summary>
    /// Gets or creates a conversation session.
    /// </summary>
    /// <param name="sessionId">Optional session ID. If null, a new session is created.</param>
    /// <returns>The conversation session.</returns>
    public ConversationSession GetOrCreateSession(string? sessionId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrEmpty(sessionId))
        {
            sessionId = Guid.NewGuid().ToString("N")[..12];
        }

        var session = _sessions.GetOrAdd(sessionId, id => new ConversationSession(id, _config.MaxTurnsPerSession));
        session.Touch();

        // Enforce max sessions
        EnforceMaxSessions();

        return session;
    }

    /// <summary>
    /// Gets an existing session by ID.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <returns>The session, or null if not found.</returns>
    public ConversationSession? GetSession(string sessionId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.Touch();
            return session;
        }
        return null;
    }

    /// <summary>
    /// Clears a session's context without removing the session.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <returns>True if the session was found and cleared.</returns>
    public bool ClearSessionContext(string sessionId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.ClearContext();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Ends and removes a session.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <returns>True if the session was found and removed.</returns>
    public bool EndSession(string sessionId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _sessions.TryRemove(sessionId, out _);
    }

    /// <summary>
    /// Gets all active session IDs.
    /// </summary>
    /// <returns>Collection of active session IDs.</returns>
    public IEnumerable<string> GetActiveSessionIds()
    {
        return _sessions.Keys.ToList();
    }

    /// <summary>
    /// Gets session statistics.
    /// </summary>
    /// <returns>Tuple of (ActiveSessions, TotalTurns, OldestSession).</returns>
    public (int ActiveSessions, int TotalTurns, TimeSpan OldestSession) GetStats()
    {
        var sessions = _sessions.Values.ToList();
        var totalTurns = sessions.Sum(s => s.Turns.Count);
        var oldestSession = sessions.Count > 0
            ? DateTime.UtcNow - sessions.Min(s => s.CreatedAt)
            : TimeSpan.Zero;

        return (sessions.Count, totalTurns, oldestSession);
    }

    #endregion

    #region Follow-Up Processing

    /// <summary>
    /// Processes a follow-up query in context.
    /// </summary>
    /// <param name="session">The conversation session.</param>
    /// <param name="followUpQuery">The follow-up query.</param>
    /// <param name="provider">Optional AI provider for enhanced resolution.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The follow-up result with resolved query and context.</returns>
    public async Task<NLPFollowUpResult> ProcessFollowUpAsync(
        ConversationSession session,
        string followUpQuery,
        IExtendedAIProvider? provider = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(followUpQuery);

        var isFollowUp = IsFollowUp(followUpQuery);
        var context = session.GetCarryOverContext();

        // Check for context reset commands
        if (IsContextReset(followUpQuery))
        {
            session.ClearContext();
            return new NLPFollowUpResult
            {
                IsFollowUp = false,
                ResolvedQuery = followUpQuery,
                Explanation = "Context cleared. Starting fresh conversation.",
                AppliedContext = new Dictionary<string, object?>()
            };
        }

        // Resolve pronouns
        var resolvedQuery = ResolvePronounsInQuery(followUpQuery, context);

        // Enrich with context
        var (enrichedQuery, appliedContext) = EnrichWithContext(resolvedQuery, context, isFollowUp);

        // If AI provider available, use enhanced resolution
        NLPCommandIntent? resolvedIntent = null;
        TokenUsage? usage = null;

        if (provider != null && isFollowUp)
        {
            var (aiResolvedQuery, aiIntent, aiUsage) = await ResolveWithAIAsync(
                followUpQuery,
                enrichedQuery,
                context,
                session.GetRecentTurns(3),
                provider,
                ct);

            if (aiResolvedQuery != null)
            {
                enrichedQuery = aiResolvedQuery;
                resolvedIntent = aiIntent;
                usage = aiUsage;
            }
        }

        // Record the turn
        if (resolvedIntent != null)
        {
            session.AddTurn(followUpQuery, resolvedIntent.CommandName, resolvedIntent.Parameters, true);
        }

        return new NLPFollowUpResult
        {
            IsFollowUp = isFollowUp,
            ResolvedQuery = enrichedQuery,
            ResolvedIntent = resolvedIntent,
            AppliedContext = appliedContext,
            Explanation = GenerateResolutionExplanation(followUpQuery, enrichedQuery, appliedContext),
            TokenUsage = usage
        };
    }

    #endregion

    #region Pronoun Resolution

    /// <summary>
    /// Resolves pronouns in a query using conversation context.
    /// </summary>
    /// <param name="query">The query with pronouns.</param>
    /// <param name="context">The carry-over context.</param>
    /// <returns>The query with pronouns resolved.</returns>
    public string ResolvePronounsInQuery(string query, Dictionary<string, object?> context)
    {
        if (context.Count == 0)
            return query;

        var resolved = query;

        // Handle "it", "that", "this"
        if (ContainsPronouns(query, "it", "that", "this"))
        {
            // Try to find the most relevant entity reference
            if (context.TryGetValue("currentPool", out var pool) && pool != null)
            {
                resolved = ReplacePronouns(resolved, $"pool {pool}", "it", "that", "this");
            }
            else if (context.TryGetValue("currentBackup", out var backup) && backup != null)
            {
                resolved = ReplacePronouns(resolved, $"backup {backup}", "it", "that", "this");
            }
            else if (context.TryGetValue("lastFile", out var file) && file != null)
            {
                resolved = ReplacePronouns(resolved, $"file {file}", "it", "that", "this");
            }
            else if (context.TryGetValue("lastEntity", out var entity) && entity != null)
            {
                resolved = ReplacePronouns(resolved, entity.ToString() ?? "", "it", "that", "this");
            }
        }

        // Handle "them", "those", "these"
        if (ContainsPronouns(query, "them", "those", "these"))
        {
            if (context.TryGetValue("currentResults", out var results) && results != null)
            {
                // For plural references, we typically don't replace but note the context
                // The calling code will use the context appropriately
            }
        }

        // Handle "there"
        if (ContainsPronouns(query, "there"))
        {
            if (context.TryGetValue("lastDestination", out var dest) && dest != null)
            {
                resolved = ReplacePronouns(resolved, dest.ToString() ?? "", "there");
            }
            else if (context.TryGetValue("currentPath", out var path) && path != null)
            {
                resolved = ReplacePronouns(resolved, path.ToString() ?? "", "there");
            }
        }

        return resolved;
    }

    private bool ContainsPronouns(string text, params string[] pronouns)
    {
        var lower = text.ToLowerInvariant();
        return pronouns.Any(p =>
            Regex.IsMatch(lower, $@"\b{p}\b"));
    }

    private string ReplacePronouns(string text, string replacement, params string[] pronouns)
    {
        var result = text;
        foreach (var pronoun in pronouns)
        {
            // Replace "it" but not in words like "with"
            result = Regex.Replace(
                result,
                $@"(?<!\w){pronoun}(?!\w)",
                replacement,
                RegexOptions.IgnoreCase);
        }
        return result;
    }

    #endregion

    #region Follow-Up Detection

    /// <summary>
    /// Patterns that indicate a follow-up/refinement to previous command.
    /// </summary>
    private static readonly string[] FollowUpIndicators =
    {
        "filter", "show only", "just", "only", "but", "and also",
        "from last", "from those", "of those", "the same",
        "more details", "details", "info", "information",
        "delete that", "remove that", "it", "that one", "this one",
        "again", "retry", "redo", "also", "more", "another",
        "what about", "how about", "same but", "instead",
        "only the", "just the", "more like", "similar to"
    };

    /// <summary>
    /// Patterns that indicate time-based filtering.
    /// </summary>
    private static readonly (string Pattern, TimeSpan Duration)[] TimePatterns =
    {
        ("last hour", TimeSpan.FromHours(1)),
        ("last day", TimeSpan.FromDays(1)),
        ("last week", TimeSpan.FromDays(7)),
        ("last month", TimeSpan.FromDays(30)),
        ("today", TimeSpan.FromHours(24)),
        ("yesterday", TimeSpan.FromDays(1)),
        ("this week", TimeSpan.FromDays(7)),
        ("recent", TimeSpan.FromHours(24)),
    };

    /// <summary>
    /// Patterns that indicate context reset.
    /// </summary>
    private static readonly string[] ContextResetIndicators =
    {
        "new", "different", "start over", "forget", "clear context", "reset",
        "never mind", "scratch that", "something else"
    };

    /// <summary>
    /// Checks if input indicates a follow-up command.
    /// </summary>
    /// <param name="input">The input to check.</param>
    /// <returns>True if this appears to be a follow-up.</returns>
    public bool IsFollowUp(string input)
    {
        var normalized = input.ToLowerInvariant();
        return FollowUpIndicators.Any(p => normalized.Contains(p));
    }

    /// <summary>
    /// Checks if input indicates a context reset.
    /// </summary>
    /// <param name="input">The input to check.</param>
    /// <returns>True if this appears to be a context reset request.</returns>
    public bool IsContextReset(string input)
    {
        var normalized = input.ToLowerInvariant();

        // Check if it's clearly a reset command
        if (normalized.Contains("clear context") ||
            normalized.Contains("start over") ||
            normalized.Contains("forget everything"))
        {
            return true;
        }

        // "New" at the start often indicates a reset
        if (Regex.IsMatch(normalized, @"^(?:let's\s+)?(?:do\s+)?(?:something\s+)?new\b"))
        {
            return true;
        }

        // Check other reset indicators only if they're standalone or start the query
        return ContextResetIndicators.Any(p =>
            normalized.StartsWith(p) ||
            Regex.IsMatch(normalized, $@"^\s*{Regex.Escape(p)}\b"));
    }

    /// <summary>
    /// Extracts time filter from input if present.
    /// </summary>
    /// <param name="input">The input to check.</param>
    /// <returns>The time span, or null if no time filter found.</returns>
    public TimeSpan? ExtractTimeFilter(string input)
    {
        var normalized = input.ToLowerInvariant();
        foreach (var (pattern, duration) in TimePatterns)
        {
            if (normalized.Contains(pattern))
            {
                return duration;
            }
        }

        // Check for explicit time patterns like "last 3 days"
        var match = Regex.Match(normalized, @"last\s+(\d+)\s+(hours?|days?|weeks?|months?)");
        if (match.Success)
        {
            var count = int.Parse(match.Groups[1].Value);
            var unit = match.Groups[2].Value.ToLowerInvariant();

            return unit switch
            {
                var u when u.StartsWith("hour") => TimeSpan.FromHours(count),
                var u when u.StartsWith("day") => TimeSpan.FromDays(count),
                var u when u.StartsWith("week") => TimeSpan.FromDays(count * 7),
                var u when u.StartsWith("month") => TimeSpan.FromDays(count * 30),
                _ => null
            };
        }

        return null;
    }

    #endregion

    #region Context Enrichment

    private (string EnrichedQuery, Dictionary<string, object?> AppliedContext) EnrichWithContext(
        string query,
        Dictionary<string, object?> context,
        bool isFollowUp)
    {
        var appliedContext = new Dictionary<string, object?>();

        if (!isFollowUp || context.Count == 0)
        {
            return (query, appliedContext);
        }

        var enriched = query;

        // Apply time filter from context if query mentions time-related filtering
        var timeFilter = ExtractTimeFilter(query);
        if (timeFilter.HasValue)
        {
            appliedContext["since"] = DateTime.UtcNow - timeFilter.Value;
        }

        // Check for filter refinements
        if (Regex.IsMatch(query, @"\b(?:only|just|filter)\b", RegexOptions.IgnoreCase))
        {
            // Carry over relevant context
            if (context.TryGetValue("_previousCommand", out var prevCmd))
            {
                appliedContext["_previousCommand"] = prevCmd;
            }

            // Carry over last parameters
            foreach (var (key, value) in context)
            {
                if (key.StartsWith("_last_") && value != null)
                {
                    var paramName = key["_last_".Length..];
                    if (!appliedContext.ContainsKey(paramName))
                    {
                        appliedContext[paramName] = value;
                    }
                }
            }
        }

        // Check for "more" or "also" - add to existing parameters
        if (Regex.IsMatch(query, @"\b(?:also|more|another)\b", RegexOptions.IgnoreCase))
        {
            foreach (var (key, value) in context)
            {
                if (!key.StartsWith("_") && value != null)
                {
                    appliedContext[$"inherited_{key}"] = value;
                }
            }
        }

        return (enriched, appliedContext);
    }

    #endregion

    #region AI-Enhanced Resolution

    private async Task<(string? ResolvedQuery, NLPCommandIntent? Intent, TokenUsage? Usage)> ResolveWithAIAsync(
        string originalQuery,
        string enrichedQuery,
        Dictionary<string, object?> context,
        IEnumerable<ConversationTurn> recentTurns,
        IExtendedAIProvider provider,
        CancellationToken ct)
    {
        try
        {
            var turnsContext = string.Join("\n", recentTurns.Select(t =>
                $"- User: \"{t.UserInput}\" -> Command: {t.CommandName}"));

            var contextJson = context.Count > 0
                ? JsonSerializer.Serialize(context.Where(kv => !kv.Key.StartsWith("_")))
                : "{}";

            var systemPrompt = @"You are a follow-up query resolver. Given conversation history and context,
resolve the user's follow-up query into a complete, standalone query.

Output JSON:
{
    ""resolvedQuery"": ""the complete resolved query"",
    ""command"": ""suggested.command"",
    ""parameters"": {""key"": ""value""},
    ""explanation"": ""why this interpretation""
}";

            var userPrompt = $@"Conversation history:
{turnsContext}

Current context:
{contextJson}

Follow-up query: ""{originalQuery}""
After pronoun resolution: ""{enrichedQuery}""

Resolve this follow-up query.";

            var request = new AIRequest
            {
                Prompt = userPrompt,
                SystemMessage = systemPrompt,
                MaxTokens = 400,
                Temperature = 0.2f
            };

            var response = await provider.CompleteAsync(request, ct);

            if (!response.Success || string.IsNullOrEmpty(response.Content))
            {
                return (null, null, ConvertUsage(response.Usage));
            }

            // Parse AI response
            var jsonStart = response.Content.IndexOf('{');
            var jsonEnd = response.Content.LastIndexOf('}');

            if (jsonStart < 0 || jsonEnd <= jsonStart)
            {
                return (null, null, ConvertUsage(response.Usage));
            }

            var json = response.Content.Substring(jsonStart, jsonEnd - jsonStart + 1);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var resolvedQuery = root.TryGetProperty("resolvedQuery", out var rq)
                ? rq.GetString()
                : null;

            var command = root.TryGetProperty("command", out var cmd)
                ? cmd.GetString()
                : null;

            NLPCommandIntent? intent = null;
            if (!string.IsNullOrEmpty(command))
            {
                var parameters = new Dictionary<string, object?>();
                if (root.TryGetProperty("parameters", out var paramsProp))
                {
                    foreach (var prop in paramsProp.EnumerateObject())
                    {
                        parameters[prop.Name] = ExtractJsonValue(prop.Value);
                    }
                }

                var explanation = root.TryGetProperty("explanation", out var exp)
                    ? exp.GetString()
                    : null;

                intent = new NLPCommandIntent
                {
                    CommandName = command,
                    Parameters = parameters,
                    OriginalInput = originalQuery,
                    Confidence = 0.85,
                    Explanation = explanation,
                    ProcessedByAI = true
                };
            }

            return (resolvedQuery, intent, ConvertUsage(response.Usage));
        }
        catch
        {
            return (null, null, null);
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

    private string GenerateResolutionExplanation(
        string original,
        string resolved,
        Dictionary<string, object?> appliedContext)
    {
        if (original == resolved && appliedContext.Count == 0)
        {
            return "Query processed as-is (no context needed).";
        }

        var parts = new List<string>();

        if (original != resolved)
        {
            parts.Add($"Resolved pronouns: '{original}' -> '{resolved}'");
        }

        if (appliedContext.Count > 0)
        {
            var contextKeys = string.Join(", ", appliedContext.Keys.Take(3));
            parts.Add($"Applied context: {contextKeys}");
        }

        return string.Join("; ", parts);
    }

    #endregion

    #region Cleanup

    private void CleanupExpiredSessions(object? state)
    {
        if (_disposed) return;

        var expiry = DateTime.UtcNow.AddMinutes(-_config.SessionTimeoutMinutes);
        var expiredSessions = _sessions
            .Where(kvp => kvp.Value.LastAccessedAt < expiry)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var sessionId in expiredSessions)
        {
            _sessions.TryRemove(sessionId, out _);
        }
    }

    private void EnforceMaxSessions()
    {
        while (_sessions.Count > _config.MaxSessions)
        {
            var oldest = _sessions
                .OrderBy(kvp => kvp.Value.LastAccessedAt)
                .FirstOrDefault();

            if (!string.IsNullOrEmpty(oldest.Key))
            {
                _sessions.TryRemove(oldest.Key, out _);
            }
            else
            {
                break;
            }
        }
    }

    #endregion

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cleanupTimer.Dispose();
        _sessions.Clear();
    }
}

/// <summary>
/// Configuration for the conversation context engine.
/// </summary>
public sealed class ConversationConfig
{
    /// <summary>
    /// Gets or sets the session timeout in minutes.
    /// Sessions inactive for longer than this are automatically cleaned up.
    /// </summary>
    public int SessionTimeoutMinutes { get; init; } = 120;

    /// <summary>
    /// Gets or sets the maximum number of concurrent sessions.
    /// Oldest sessions are removed when this limit is exceeded.
    /// </summary>
    public int MaxSessions { get; init; } = 1000;

    /// <summary>
    /// Gets or sets the maximum number of turns per session.
    /// Older turns are removed when this limit is exceeded.
    /// </summary>
    public int MaxTurnsPerSession { get; init; } = 50;
}

/// <summary>
/// Represents a conversational turn (user input and system response).
/// </summary>
public sealed record ConversationTurn
{
    /// <summary>Gets the turn sequence number.</summary>
    public int TurnNumber { get; init; }

    /// <summary>Gets the original user input.</summary>
    public required string UserInput { get; init; }

    /// <summary>Gets the interpreted command.</summary>
    public required string CommandName { get; init; }

    /// <summary>Gets the parameters extracted from the input.</summary>
    public Dictionary<string, object?> Parameters { get; init; } = new();

    /// <summary>Gets whether the command succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Gets any entities extracted from this turn.</summary>
    public Dictionary<string, object?> ExtractedContext { get; init; } = new();

    /// <summary>Gets the timestamp of this turn.</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Represents a conversational session with multi-turn context.
/// </summary>
public sealed class ConversationSession
{
    private readonly int _maxTurns;

    /// <summary>Gets the unique session identifier.</summary>
    public string SessionId { get; }

    /// <summary>Gets when the session was created.</summary>
    public DateTime CreatedAt { get; }

    /// <summary>Gets when the session was last accessed.</summary>
    public DateTime LastAccessedAt { get; private set; }

    /// <summary>Gets all turns in this conversation.</summary>
    public List<ConversationTurn> Turns { get; } = new();

    /// <summary>Gets accumulated context from all turns.</summary>
    public Dictionary<string, object?> AccumulatedContext { get; } = new();

    /// <summary>Gets the last command executed.</summary>
    public string? LastCommandName { get; private set; }

    /// <summary>Gets the last parameters used.</summary>
    public Dictionary<string, object?>? LastParameters { get; private set; }

    /// <summary>
    /// Creates a new conversation session.
    /// </summary>
    /// <param name="sessionId">Optional session ID.</param>
    /// <param name="maxTurns">Maximum turns to keep in history.</param>
    public ConversationSession(string? sessionId = null, int maxTurns = 50)
    {
        SessionId = sessionId ?? Guid.NewGuid().ToString("N")[..12];
        CreatedAt = DateTime.UtcNow;
        LastAccessedAt = DateTime.UtcNow;
        _maxTurns = maxTurns;
    }

    /// <summary>
    /// Adds a new turn to the conversation.
    /// </summary>
    /// <param name="userInput">The user's input.</param>
    /// <param name="commandName">The interpreted command.</param>
    /// <param name="parameters">The extracted parameters.</param>
    /// <param name="success">Whether the command succeeded.</param>
    public void AddTurn(string userInput, string commandName, Dictionary<string, object?> parameters, bool success)
    {
        var turn = new ConversationTurn
        {
            TurnNumber = Turns.Count + 1,
            UserInput = userInput,
            CommandName = commandName,
            Parameters = new Dictionary<string, object?>(parameters),
            Success = success,
            Timestamp = DateTime.UtcNow
        };

        Turns.Add(turn);
        LastCommandName = commandName;
        LastParameters = new Dictionary<string, object?>(parameters);
        LastAccessedAt = DateTime.UtcNow;

        // Enforce max turns
        while (Turns.Count > _maxTurns)
        {
            Turns.RemoveAt(0);
        }

        // Extract and accumulate context
        ExtractContext(turn);
    }

    /// <summary>
    /// Gets context to carry over to the next command.
    /// </summary>
    /// <returns>Dictionary of carry-over context.</returns>
    public Dictionary<string, object?> GetCarryOverContext()
    {
        Touch();
        var context = new Dictionary<string, object?>();

        // Add last command reference
        if (!string.IsNullOrEmpty(LastCommandName))
        {
            context["_previousCommand"] = LastCommandName;
        }

        // Add accumulated context (entities like pool names, backup IDs, etc.)
        foreach (var (key, value) in AccumulatedContext)
        {
            context[key] = value;
        }

        // Add last parameters for refinement
        if (LastParameters != null)
        {
            foreach (var (key, value) in LastParameters)
            {
                if (!context.ContainsKey(key))
                {
                    context[$"_last_{key}"] = value;
                }
            }
        }

        return context;
    }

    /// <summary>
    /// Gets recent turns for context building.
    /// </summary>
    /// <param name="count">Number of recent turns to get.</param>
    /// <returns>Collection of recent turns.</returns>
    public IEnumerable<ConversationTurn> GetRecentTurns(int count)
    {
        return Turns.TakeLast(count);
    }

    /// <summary>
    /// Clears the accumulated context (but keeps history).
    /// </summary>
    public void ClearContext()
    {
        AccumulatedContext.Clear();
        LastCommandName = null;
        LastParameters = null;
        Touch();
    }

    /// <summary>
    /// Updates last accessed time.
    /// </summary>
    public void Touch()
    {
        LastAccessedAt = DateTime.UtcNow;
    }

    private void ExtractContext(ConversationTurn turn)
    {
        // Extract entity references from parameters
        foreach (var (key, value) in turn.Parameters)
        {
            if (value == null) continue;

            // Track entity references by type
            switch (key.ToLowerInvariant())
            {
                case "id":
                case "poolid":
                case "pool":
                    AccumulatedContext["currentPool"] = value;
                    AccumulatedContext["lastEntity"] = value;
                    break;
                case "backupid":
                case "backup":
                    AccumulatedContext["currentBackup"] = value;
                    AccumulatedContext["lastEntity"] = value;
                    break;
                case "name":
                    // Context-dependent - use command to determine
                    if (turn.CommandName.Contains("pool", StringComparison.OrdinalIgnoreCase))
                    {
                        AccumulatedContext["currentPool"] = value;
                        AccumulatedContext["lastEntity"] = value;
                    }
                    else if (turn.CommandName.Contains("backup", StringComparison.OrdinalIgnoreCase))
                    {
                        AccumulatedContext["currentBackup"] = value;
                        AccumulatedContext["lastEntity"] = value;
                    }
                    break;
                case "destination":
                    AccumulatedContext["lastDestination"] = value;
                    break;
                case "path":
                    AccumulatedContext["currentPath"] = value;
                    break;
                case "file":
                case "filename":
                    AccumulatedContext["lastFile"] = value;
                    AccumulatedContext["lastEntity"] = value;
                    break;
                case "encrypt":
                    AccumulatedContext["useEncryption"] = value;
                    break;
                case "compress":
                    AccumulatedContext["useCompression"] = value;
                    break;
            }
        }

        // Track command category for follow-ups
        var category = turn.CommandName.Split('.').FirstOrDefault();
        if (!string.IsNullOrEmpty(category))
        {
            AccumulatedContext["lastCategory"] = category;
        }
    }
}
