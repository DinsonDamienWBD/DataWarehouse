// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.AIInterface.CLI;

/// <summary>
/// Manages conversation history and context for multi-turn CLI interactions.
/// Enables context-aware follow-up commands by maintaining session state.
/// </summary>
/// <remarks>
/// <para>
/// The ConversationContext supports:
/// <list type="bullet">
/// <item>Multi-session conversation history tracking</item>
/// <item>Context-aware reference resolution ("that file", "the result")</item>
/// <item>Automatic history pruning to manage memory</item>
/// <item>Session-scoped working state for complex operations</item>
/// </list>
/// </para>
/// <para>
/// Example interaction flow:
/// <code>
/// User: "Find documents about Q4 sales"
/// CLI: "Found 5 documents matching Q4 sales..."
/// User: "Encrypt them with military-grade security"  // "them" resolves to search results
/// CLI: "Encrypting 5 documents with AES-256-GCM..."
/// </code>
/// </para>
/// </remarks>
public sealed class ConversationContext : IDisposable
{
    private readonly int _maxHistorySize;
    private readonly ConcurrentDictionary<string, SessionHistory> _sessions = new();
    private readonly Timer _cleanupTimer;
    private readonly TimeSpan _sessionTimeout = TimeSpan.FromMinutes(30);
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConversationContext"/> class.
    /// </summary>
    /// <param name="maxHistorySize">Maximum number of turns to keep per session.</param>
    public ConversationContext(int maxHistorySize = 50)
    {
        _maxHistorySize = maxHistorySize;

        // Cleanup stale sessions every 5 minutes
        _cleanupTimer = new Timer(CleanupStaleSessions, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Adds a user turn to the conversation history.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="content">The user's input.</param>
    public void AddUserTurn(string sessionId, string content)
    {
        AddTurn(sessionId, "user", content);
    }

    /// <summary>
    /// Adds an assistant turn to the conversation history.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="content">The assistant's response.</param>
    public void AddAssistantTurn(string sessionId, string content)
    {
        AddTurn(sessionId, "assistant", content);
    }

    /// <summary>
    /// Adds a system message to the conversation history.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="content">The system message.</param>
    public void AddSystemTurn(string sessionId, string content)
    {
        AddTurn(sessionId, "system", content);
    }

    /// <summary>
    /// Gets the conversation history for a session.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <returns>The list of conversation turns.</returns>
    public IReadOnlyList<ConversationTurn> GetHistory(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            lock (session.Lock)
            {
                return session.Turns.ToList().AsReadOnly();
            }
        }
        return Array.Empty<ConversationTurn>();
    }

    /// <summary>
    /// Gets the last N turns from the conversation history.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="count">Number of turns to retrieve.</param>
    /// <returns>The last N conversation turns.</returns>
    public IReadOnlyList<ConversationTurn> GetRecentHistory(string sessionId, int count)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            lock (session.Lock)
            {
                return session.Turns.TakeLast(count).ToList().AsReadOnly();
            }
        }
        return Array.Empty<ConversationTurn>();
    }

    /// <summary>
    /// Clears the conversation history for a session.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    public void ClearHistory(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            lock (session.Lock)
            {
                session.Turns.Clear();
                session.WorkingState.Clear();
            }
        }
    }

    /// <summary>
    /// Stores a working state value for a session.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="key">The state key.</param>
    /// <param name="value">The state value.</param>
    public void SetWorkingState(string sessionId, string key, object value)
    {
        var session = GetOrCreateSession(sessionId);
        lock (session.Lock)
        {
            session.WorkingState[key] = new StateEntry
            {
                Value = value,
                CreatedAt = DateTime.UtcNow
            };
        }
    }

    /// <summary>
    /// Retrieves a working state value for a session.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="key">The state key.</param>
    /// <returns>The state value, or null if not found.</returns>
    public object? GetWorkingState(string sessionId, string key)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            lock (session.Lock)
            {
                if (session.WorkingState.TryGetValue(key, out var entry))
                {
                    return entry.Value;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Retrieves a working state value with type conversion.
    /// </summary>
    /// <typeparam name="T">The expected type.</typeparam>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="key">The state key.</param>
    /// <returns>The state value, or default if not found.</returns>
    public T? GetWorkingState<T>(string sessionId, string key)
    {
        var value = GetWorkingState(sessionId, key);
        if (value is T typedValue)
        {
            return typedValue;
        }
        return default;
    }

    /// <summary>
    /// Removes a working state value.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="key">The state key.</param>
    /// <returns>True if the value was removed.</returns>
    public bool RemoveWorkingState(string sessionId, string key)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            lock (session.Lock)
            {
                return session.WorkingState.Remove(key);
            }
        }
        return false;
    }

    /// <summary>
    /// Gets all working state keys for a session.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <returns>The list of state keys.</returns>
    public IReadOnlyList<string> GetWorkingStateKeys(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            lock (session.Lock)
            {
                return session.WorkingState.Keys.ToList().AsReadOnly();
            }
        }
        return Array.Empty<string>();
    }

    /// <summary>
    /// Stores the result of the last command for reference resolution.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="command">The command that was executed.</param>
    /// <param name="result">The command result.</param>
    public void StoreLastCommandResult(string sessionId, string command, object? result)
    {
        var session = GetOrCreateSession(sessionId);
        lock (session.Lock)
        {
            session.LastCommand = command;
            session.LastResult = result;
            session.LastCommandTime = DateTime.UtcNow;

            // Store in working state for reference
            session.WorkingState["_lastCommand"] = new StateEntry { Value = command, CreatedAt = DateTime.UtcNow };
            if (result != null)
            {
                session.WorkingState["_lastResult"] = new StateEntry { Value = result, CreatedAt = DateTime.UtcNow };
            }

            // Store command-specific references
            switch (command.ToLowerInvariant())
            {
                case "search":
                case "find":
                    if (result != null)
                        session.WorkingState["_searchResults"] = new StateEntry { Value = result, CreatedAt = DateTime.UtcNow };
                    break;
                case "upload":
                    if (result != null)
                        session.WorkingState["_uploadedFiles"] = new StateEntry { Value = result, CreatedAt = DateTime.UtcNow };
                    break;
                case "download":
                    if (result != null)
                        session.WorkingState["_downloadedFiles"] = new StateEntry { Value = result, CreatedAt = DateTime.UtcNow };
                    break;
                case "list":
                    if (result != null)
                        session.WorkingState["_listedFiles"] = new StateEntry { Value = result, CreatedAt = DateTime.UtcNow };
                    break;
            }
        }
    }

    /// <summary>
    /// Resolves a reference like "them", "it", "that", "the result" using context.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="reference">The reference to resolve.</param>
    /// <returns>The resolved value, or null if not resolvable.</returns>
    public object? ResolveReference(string sessionId, string reference)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return null;

        lock (session.Lock)
        {
            var lower = reference.ToLowerInvariant().Trim();

            // Direct references
            if (lower == "it" || lower == "that" || lower == "the result" || lower == "result")
            {
                return session.LastResult;
            }

            if (lower == "them" || lower == "those" || lower == "these" || lower == "the files" || lower == "the documents")
            {
                // Try search results first
                if (session.WorkingState.TryGetValue("_searchResults", out var searchResults))
                    return searchResults.Value;
                if (session.WorkingState.TryGetValue("_listedFiles", out var listedFiles))
                    return listedFiles.Value;
                return session.LastResult;
            }

            if (lower == "the file" || lower == "that file" || lower == "this file")
            {
                if (session.WorkingState.TryGetValue("_uploadedFiles", out var uploadedFiles))
                    return uploadedFiles.Value;
                if (session.WorkingState.TryGetValue("_downloadedFiles", out var downloadedFiles))
                    return downloadedFiles.Value;
                return session.LastResult;
            }

            // Named references from working state
            foreach (var key in session.WorkingState.Keys)
            {
                if (lower.Contains(key.TrimStart('_').ToLowerInvariant()))
                {
                    return session.WorkingState[key].Value;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Builds a context summary for AI consumption.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <returns>A dictionary containing context information.</returns>
    public Dictionary<string, object> BuildContextSummary(string sessionId)
    {
        var summary = new Dictionary<string, object>();

        if (!_sessions.TryGetValue(sessionId, out var session))
            return summary;

        lock (session.Lock)
        {
            // Recent history
            var recentTurns = session.Turns.TakeLast(5).Select(t => new Dictionary<string, object>
            {
                ["role"] = t.Role,
                ["content"] = t.Content
            }).ToList();
            summary["recentHistory"] = recentTurns;

            // Last command info
            if (!string.IsNullOrEmpty(session.LastCommand))
            {
                summary["lastCommand"] = session.LastCommand;
                summary["lastCommandTime"] = session.LastCommandTime ?? DateTime.UtcNow;
            }

            // Active working state
            var activeState = session.WorkingState
                .Where(kv => DateTime.UtcNow - kv.Value.CreatedAt < TimeSpan.FromMinutes(10))
                .ToDictionary(kv => kv.Key.TrimStart('_'), kv => kv.Value.Value);

            if (activeState.Count > 0)
            {
                summary["workingState"] = activeState;
            }

            // Session metadata
            summary["sessionId"] = sessionId;
            summary["turnCount"] = session.Turns.Count;
        }

        return summary;
    }

    /// <summary>
    /// Exports the full session state for persistence or transfer.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <returns>The exported session state.</returns>
    public SessionExport? ExportSession(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return null;

        lock (session.Lock)
        {
            return new SessionExport
            {
                SessionId = sessionId,
                Turns = session.Turns.ToList(),
                WorkingState = session.WorkingState.ToDictionary(
                    kv => kv.Key,
                    kv => new ExportedStateEntry { Value = kv.Value.Value, CreatedAt = kv.Value.CreatedAt }),
                LastCommand = session.LastCommand,
                LastResult = session.LastResult,
                LastCommandTime = session.LastCommandTime,
                CreatedAt = session.CreatedAt,
                LastAccessed = session.LastAccessed
            };
        }
    }

    /// <summary>
    /// Imports a session state from an export.
    /// </summary>
    /// <param name="export">The session export to import.</param>
    public void ImportSession(SessionExport export)
    {
        var session = new SessionHistory
        {
            CreatedAt = export.CreatedAt,
            LastAccessed = DateTime.UtcNow,
            LastCommand = export.LastCommand,
            LastResult = export.LastResult,
            LastCommandTime = export.LastCommandTime
        };

        foreach (var turn in export.Turns)
        {
            session.Turns.Add(turn);
        }

        foreach (var kv in export.WorkingState)
        {
            session.WorkingState[kv.Key] = new StateEntry
            {
                Value = kv.Value.Value,
                CreatedAt = kv.Value.CreatedAt
            };
        }

        _sessions[export.SessionId] = session;
    }

    private void AddTurn(string sessionId, string role, string content)
    {
        var session = GetOrCreateSession(sessionId);

        lock (session.Lock)
        {
            session.Turns.Add(new ConversationTurn
            {
                Role = role,
                Content = content,
                Timestamp = DateTime.UtcNow
            });

            session.LastAccessed = DateTime.UtcNow;

            // Prune if over limit
            while (session.Turns.Count > _maxHistorySize)
            {
                session.Turns.RemoveAt(0);
            }
        }
    }

    private SessionHistory GetOrCreateSession(string sessionId)
    {
        return _sessions.GetOrAdd(sessionId, _ => new SessionHistory
        {
            CreatedAt = DateTime.UtcNow,
            LastAccessed = DateTime.UtcNow
        });
    }

    private void CleanupStaleSessions(object? state)
    {
        var now = DateTime.UtcNow;
        var staleIds = _sessions
            .Where(kv => now - kv.Value.LastAccessed > _sessionTimeout)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var id in staleIds)
        {
            _sessions.TryRemove(id, out _);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cleanupTimer.Dispose();
        _sessions.Clear();
    }
}

/// <summary>
/// A single turn in a conversation.
/// </summary>
public sealed class ConversationTurn
{
    /// <summary>Gets or sets the role (user, assistant, system).</summary>
    public string Role { get; init; } = string.Empty;

    /// <summary>Gets or sets the content of the turn.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>Gets or sets when the turn occurred.</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>Gets or sets optional metadata for the turn.</summary>
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Internal session history storage.
/// </summary>
internal sealed class SessionHistory
{
    public object Lock { get; } = new object();
    public List<ConversationTurn> Turns { get; } = new();
    public Dictionary<string, StateEntry> WorkingState { get; } = new();
    public string? LastCommand { get; set; }
    public object? LastResult { get; set; }
    public DateTime? LastCommandTime { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime LastAccessed { get; set; }
}

/// <summary>
/// Internal state entry with timestamp.
/// </summary>
internal sealed class StateEntry
{
    public object Value { get; init; } = null!;
    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// Exported session state for persistence.
/// </summary>
public sealed class SessionExport
{
    /// <summary>Gets or sets the session identifier.</summary>
    public string SessionId { get; init; } = string.Empty;

    /// <summary>Gets or sets the conversation turns.</summary>
    public List<ConversationTurn> Turns { get; init; } = new();

    /// <summary>Gets or sets the working state.</summary>
    public Dictionary<string, ExportedStateEntry> WorkingState { get; init; } = new();

    /// <summary>Gets or sets the last command.</summary>
    public string? LastCommand { get; init; }

    /// <summary>Gets or sets the last result.</summary>
    public object? LastResult { get; init; }

    /// <summary>Gets or sets the last command time.</summary>
    public DateTime? LastCommandTime { get; init; }

    /// <summary>Gets or sets when the session was created.</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>Gets or sets when the session was last accessed.</summary>
    public DateTime LastAccessed { get; init; }
}

/// <summary>
/// Exported state entry for serialization.
/// </summary>
public sealed class ExportedStateEntry
{
    /// <summary>Gets or sets the value.</summary>
    public object Value { get; init; } = null!;

    /// <summary>Gets or sets when the entry was created.</summary>
    public DateTime CreatedAt { get; init; }
}
