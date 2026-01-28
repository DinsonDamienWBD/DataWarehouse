// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using System.Text.Json;

namespace DataWarehouse.Shared.Services;

/// <summary>
/// Represents a single conversational turn (user input and system response).
/// </summary>
public sealed record ConversationTurn
{
    /// <summary>Turn sequence number.</summary>
    public int TurnNumber { get; init; }

    /// <summary>Original user input.</summary>
    public required string UserInput { get; init; }

    /// <summary>Interpreted command.</summary>
    public required string CommandName { get; init; }

    /// <summary>Parameters extracted from the input.</summary>
    public Dictionary<string, object?> Parameters { get; init; } = new();

    /// <summary>Whether the command succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Any entities or context extracted from this turn.</summary>
    public Dictionary<string, object?> ExtractedContext { get; init; } = new();

    /// <summary>Timestamp of this turn.</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Represents a conversational session with multi-turn context.
/// </summary>
public sealed class ConversationSession
{
    /// <summary>Unique session identifier.</summary>
    public string SessionId { get; }

    /// <summary>When the session was created.</summary>
    public DateTime CreatedAt { get; }

    /// <summary>When the session was last accessed.</summary>
    public DateTime LastAccessedAt { get; private set; }

    /// <summary>All turns in this conversation.</summary>
    public List<ConversationTurn> Turns { get; } = new();

    /// <summary>Accumulated context from all turns (entities, preferences, filters).</summary>
    public Dictionary<string, object?> AccumulatedContext { get; } = new();

    /// <summary>Last command executed (for follow-up context).</summary>
    public string? LastCommandName { get; private set; }

    /// <summary>Last parameters used (for filter/refinement context).</summary>
    public Dictionary<string, object?>? LastParameters { get; private set; }

    /// <summary>Creates a new conversation session.</summary>
    public ConversationSession(string? sessionId = null)
    {
        SessionId = sessionId ?? Guid.NewGuid().ToString("N")[..12];
        CreatedAt = DateTime.UtcNow;
        LastAccessedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Adds a new turn to the conversation.
    /// </summary>
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

        // Extract and accumulate context
        ExtractContext(turn);
    }

    /// <summary>
    /// Gets context to carry over to the next command.
    /// </summary>
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
                    break;
                case "backupid":
                case "backup":
                    AccumulatedContext["currentBackup"] = value;
                    break;
                case "name":
                    // Context-dependent - use command to determine
                    if (turn.CommandName.Contains("pool", StringComparison.OrdinalIgnoreCase))
                        AccumulatedContext["currentPool"] = value;
                    else if (turn.CommandName.Contains("backup", StringComparison.OrdinalIgnoreCase))
                        AccumulatedContext["currentBackup"] = value;
                    break;
                case "destination":
                    AccumulatedContext["lastDestination"] = value;
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

/// <summary>
/// Manages conversational context across multiple sessions.
/// Provides session lifecycle, cleanup, and context carryover.
/// </summary>
public sealed class ConversationContextManager : IDisposable
{
    private readonly ConcurrentDictionary<string, ConversationSession> _sessions = new();
    private readonly Timer _cleanupTimer;
    private readonly TimeSpan _sessionTimeout;
    private readonly int _maxSessions;
    private bool _disposed;

    /// <summary>
    /// Creates a new ConversationContextManager.
    /// </summary>
    /// <param name="sessionTimeout">How long before inactive sessions expire.</param>
    /// <param name="maxSessions">Maximum number of concurrent sessions.</param>
    public ConversationContextManager(
        TimeSpan? sessionTimeout = null,
        int maxSessions = 1000)
    {
        _sessionTimeout = sessionTimeout ?? TimeSpan.FromHours(2);
        _maxSessions = maxSessions;

        // Cleanup every 5 minutes
        _cleanupTimer = new Timer(
            CleanupExpiredSessions,
            null,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Gets or creates a session by ID.
    /// </summary>
    public ConversationSession GetOrCreateSession(string? sessionId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrEmpty(sessionId))
        {
            sessionId = Guid.NewGuid().ToString("N")[..12];
        }

        var session = _sessions.GetOrAdd(sessionId, id => new ConversationSession(id));
        session.Touch();
        return session;
    }

    /// <summary>
    /// Gets an existing session (returns null if not found).
    /// </summary>
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
    public bool ClearSessionContext(string sessionId)
    {
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
    public bool EndSession(string sessionId)
    {
        return _sessions.TryRemove(sessionId, out _);
    }

    /// <summary>
    /// Gets all active session IDs.
    /// </summary>
    public IEnumerable<string> GetActiveSessionIds()
    {
        return _sessions.Keys.ToList();
    }

    /// <summary>
    /// Gets session statistics.
    /// </summary>
    public (int ActiveSessions, int TotalTurns, TimeSpan OldestSession) GetStats()
    {
        var sessions = _sessions.Values.ToList();
        var totalTurns = sessions.Sum(s => s.Turns.Count);
        var oldestSession = sessions.Count > 0
            ? DateTime.UtcNow - sessions.Min(s => s.CreatedAt)
            : TimeSpan.Zero;

        return (sessions.Count, totalTurns, oldestSession);
    }

    private void CleanupExpiredSessions(object? state)
    {
        if (_disposed) return;

        var now = DateTime.UtcNow;
        var expiredSessions = _sessions
            .Where(kvp => now - kvp.Value.LastAccessedAt > _sessionTimeout)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var sessionId in expiredSessions)
        {
            _sessions.TryRemove(sessionId, out _);
        }

        // Enforce max sessions by removing oldest
        while (_sessions.Count > _maxSessions)
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
/// Follow-up command patterns for conversational context.
/// </summary>
public static class ConversationalPatterns
{
    /// <summary>
    /// Patterns that indicate a follow-up/refinement to previous command.
    /// </summary>
    public static readonly string[] FollowUpIndicators =
    {
        "filter", "show only", "just", "only", "but", "and also",
        "from last", "from those", "of those", "the same",
        "more details", "details", "info", "information",
        "delete that", "remove that", "it", "that one", "this one",
        "again", "retry", "redo"
    };

    /// <summary>
    /// Patterns that indicate time-based filtering.
    /// </summary>
    public static readonly (string Pattern, TimeSpan Duration)[] TimePatterns =
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
    public static readonly string[] ContextResetIndicators =
    {
        "new", "different", "start over", "forget", "clear context", "reset"
    };

    /// <summary>
    /// Checks if input indicates a follow-up command.
    /// </summary>
    public static bool IsFollowUp(string input)
    {
        var normalized = input.ToLowerInvariant();
        return FollowUpIndicators.Any(p => normalized.Contains(p));
    }

    /// <summary>
    /// Checks if input indicates a context reset.
    /// </summary>
    public static bool IsContextReset(string input)
    {
        var normalized = input.ToLowerInvariant();
        return ContextResetIndicators.Any(p => normalized.Contains(p));
    }

    /// <summary>
    /// Extracts time filter from input if present.
    /// </summary>
    public static TimeSpan? ExtractTimeFilter(string input)
    {
        var normalized = input.ToLowerInvariant();
        foreach (var (pattern, duration) in TimePatterns)
        {
            if (normalized.Contains(pattern))
            {
                return duration;
            }
        }
        return null;
    }
}
