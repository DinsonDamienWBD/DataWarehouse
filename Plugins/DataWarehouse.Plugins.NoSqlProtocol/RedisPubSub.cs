using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.NoSqlProtocol;

/// <summary>
/// Represents a Pub/Sub subscription.
/// </summary>
public sealed class RedisSubscription
{
    /// <summary>The channel or pattern.</summary>
    public required string Channel { get; init; }
    /// <summary>Whether this is a pattern subscription.</summary>
    public bool IsPattern { get; init; }
    /// <summary>Compiled regex for pattern matching.</summary>
    public Regex? PatternRegex { get; init; }
    /// <summary>Callback for received messages.</summary>
    public required Action<string, string, byte[]> OnMessage { get; init; }
}

/// <summary>
/// Redis Pub/Sub manager.
/// Thread-safe implementation supporting channels and pattern subscriptions.
/// </summary>
public sealed class RedisPubSubManager
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, RedisSubscription>> _channelSubscriptions = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, RedisSubscription>> _patternSubscriptions = new();
    private readonly object _lock = new();

    /// <summary>
    /// Subscribes to a channel.
    /// </summary>
    /// <param name="connectionId">The connection ID.</param>
    /// <param name="channel">The channel name.</param>
    /// <param name="onMessage">Callback receiving (channel, message type, data).</param>
    /// <returns>The number of subscriptions for this connection.</returns>
    public int Subscribe(string connectionId, string channel, Action<string, string, byte[]> onMessage)
    {
        var subscription = new RedisSubscription
        {
            Channel = channel,
            IsPattern = false,
            OnMessage = onMessage
        };

        var channelSubs = _channelSubscriptions.GetOrAdd(channel, _ => new ConcurrentDictionary<string, RedisSubscription>());
        channelSubs[connectionId] = subscription;

        return GetSubscriptionCount(connectionId);
    }

    /// <summary>
    /// Subscribes to a pattern.
    /// </summary>
    /// <param name="connectionId">The connection ID.</param>
    /// <param name="pattern">The pattern (supports * and ?).</param>
    /// <param name="onMessage">Callback receiving (original channel, message type, data).</param>
    /// <returns>The number of subscriptions for this connection.</returns>
    public int PSubscribe(string connectionId, string pattern, Action<string, string, byte[]> onMessage)
    {
        var regex = PatternToRegex(pattern);
        var subscription = new RedisSubscription
        {
            Channel = pattern,
            IsPattern = true,
            PatternRegex = regex,
            OnMessage = onMessage
        };

        var patternSubs = _patternSubscriptions.GetOrAdd(pattern, _ => new ConcurrentDictionary<string, RedisSubscription>());
        patternSubs[connectionId] = subscription;

        return GetSubscriptionCount(connectionId);
    }

    /// <summary>
    /// Unsubscribes from a channel.
    /// </summary>
    /// <param name="connectionId">The connection ID.</param>
    /// <param name="channel">The channel name. If null, unsubscribes from all channels.</param>
    /// <returns>The remaining subscription count for this connection.</returns>
    public int Unsubscribe(string connectionId, string? channel = null)
    {
        if (channel == null)
        {
            // Unsubscribe from all channels
            foreach (var channelSubs in _channelSubscriptions.Values)
            {
                channelSubs.TryRemove(connectionId, out _);
            }
        }
        else
        {
            if (_channelSubscriptions.TryGetValue(channel, out var subs))
            {
                subs.TryRemove(connectionId, out _);
            }
        }

        return GetSubscriptionCount(connectionId);
    }

    /// <summary>
    /// Unsubscribes from a pattern.
    /// </summary>
    /// <param name="connectionId">The connection ID.</param>
    /// <param name="pattern">The pattern. If null, unsubscribes from all patterns.</param>
    /// <returns>The remaining subscription count for this connection.</returns>
    public int PUnsubscribe(string connectionId, string? pattern = null)
    {
        if (pattern == null)
        {
            foreach (var patternSubs in _patternSubscriptions.Values)
            {
                patternSubs.TryRemove(connectionId, out _);
            }
        }
        else
        {
            if (_patternSubscriptions.TryGetValue(pattern, out var subs))
            {
                subs.TryRemove(connectionId, out _);
            }
        }

        return GetSubscriptionCount(connectionId);
    }

    /// <summary>
    /// Publishes a message to a channel.
    /// </summary>
    /// <param name="channel">The channel name.</param>
    /// <param name="message">The message data.</param>
    /// <returns>The number of subscribers that received the message.</returns>
    public int Publish(string channel, byte[] message)
    {
        int count = 0;

        // Notify channel subscribers
        if (_channelSubscriptions.TryGetValue(channel, out var channelSubs))
        {
            foreach (var sub in channelSubs.Values)
            {
                try
                {
                    sub.OnMessage(channel, "message", message);
                    count++;
                }
                catch
                {
                    // Ignore subscriber errors
                }
            }
        }

        // Notify pattern subscribers
        foreach (var patternKvp in _patternSubscriptions)
        {
            foreach (var sub in patternKvp.Value.Values)
            {
                if (sub.PatternRegex?.IsMatch(channel) == true)
                {
                    try
                    {
                        sub.OnMessage(channel, "pmessage", message);
                        count++;
                    }
                    catch
                    {
                        // Ignore subscriber errors
                    }
                }
            }
        }

        return count;
    }

    /// <summary>
    /// Gets the number of subscribers for a channel.
    /// </summary>
    public long NumSub(string channel)
    {
        if (_channelSubscriptions.TryGetValue(channel, out var subs))
        {
            return subs.Count;
        }
        return 0;
    }

    /// <summary>
    /// Gets the number of pattern subscriptions.
    /// </summary>
    public long NumPat()
    {
        return _patternSubscriptions.Values.Sum(v => v.Count);
    }

    /// <summary>
    /// Gets all active channels.
    /// </summary>
    /// <param name="pattern">Optional pattern to filter channels.</param>
    public string[] Channels(string? pattern = null)
    {
        var channels = _channelSubscriptions
            .Where(kvp => kvp.Value.Count > 0)
            .Select(kvp => kvp.Key);

        if (!string.IsNullOrEmpty(pattern))
        {
            var regex = PatternToRegex(pattern);
            channels = channels.Where(c => regex.IsMatch(c));
        }

        return channels.ToArray();
    }

    /// <summary>
    /// Removes all subscriptions for a connection.
    /// </summary>
    public void CleanupConnection(string connectionId)
    {
        foreach (var channelSubs in _channelSubscriptions.Values)
        {
            channelSubs.TryRemove(connectionId, out _);
        }

        foreach (var patternSubs in _patternSubscriptions.Values)
        {
            patternSubs.TryRemove(connectionId, out _);
        }
    }

    private int GetSubscriptionCount(string connectionId)
    {
        int count = 0;

        foreach (var channelSubs in _channelSubscriptions.Values)
        {
            if (channelSubs.ContainsKey(connectionId)) count++;
        }

        foreach (var patternSubs in _patternSubscriptions.Values)
        {
            if (patternSubs.ContainsKey(connectionId)) count++;
        }

        return count;
    }

    private static Regex PatternToRegex(string pattern)
    {
        var escaped = Regex.Escape(pattern);
        var regexPattern = escaped
            .Replace("\\*", ".*")
            .Replace("\\?", ".");
        return new Regex($"^{regexPattern}$", RegexOptions.Compiled);
    }
}

/// <summary>
/// Redis transaction state.
/// </summary>
public enum RedisTransactionState
{
    /// <summary>Not in a transaction.</summary>
    None,
    /// <summary>In a transaction (MULTI started).</summary>
    InTransaction,
    /// <summary>Transaction aborted due to error.</summary>
    Aborted
}

/// <summary>
/// Represents a queued command in a transaction.
/// </summary>
public sealed class RedisQueuedCommand
{
    /// <summary>The command name.</summary>
    public required string Command { get; init; }
    /// <summary>The command arguments.</summary>
    public required string[] Args { get; init; }
}

/// <summary>
/// Redis transaction manager.
/// Thread-safe implementation supporting MULTI/EXEC/DISCARD.
/// </summary>
public sealed class RedisTransactionManager
{
    private readonly ConcurrentDictionary<string, List<RedisQueuedCommand>> _queues = new();
    private readonly ConcurrentDictionary<string, RedisTransactionState> _states = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _watchedKeys = new();

    /// <summary>
    /// Starts a new transaction.
    /// </summary>
    /// <param name="connectionId">The connection ID.</param>
    public void Multi(string connectionId)
    {
        _states[connectionId] = RedisTransactionState.InTransaction;
        _queues[connectionId] = new List<RedisQueuedCommand>();
    }

    /// <summary>
    /// Queues a command in the current transaction.
    /// </summary>
    /// <param name="connectionId">The connection ID.</param>
    /// <param name="command">The command name.</param>
    /// <param name="args">The command arguments.</param>
    public void QueueCommand(string connectionId, string command, string[] args)
    {
        if (_queues.TryGetValue(connectionId, out var queue))
        {
            queue.Add(new RedisQueuedCommand { Command = command, Args = args });
        }
    }

    /// <summary>
    /// Executes the transaction.
    /// </summary>
    /// <param name="connectionId">The connection ID.</param>
    /// <returns>The queued commands, or null if transaction was aborted.</returns>
    public List<RedisQueuedCommand>? Exec(string connectionId)
    {
        _states.TryRemove(connectionId, out var state);
        _queues.TryRemove(connectionId, out var queue);
        _watchedKeys.TryRemove(connectionId, out _);

        if (state == RedisTransactionState.Aborted)
            return null;

        return queue;
    }

    /// <summary>
    /// Discards the current transaction.
    /// </summary>
    /// <param name="connectionId">The connection ID.</param>
    public void Discard(string connectionId)
    {
        _states.TryRemove(connectionId, out _);
        _queues.TryRemove(connectionId, out _);
        _watchedKeys.TryRemove(connectionId, out _);
    }

    /// <summary>
    /// Watches keys for optimistic locking.
    /// </summary>
    /// <param name="connectionId">The connection ID.</param>
    /// <param name="keys">The keys to watch.</param>
    public void Watch(string connectionId, params string[] keys)
    {
        var watched = _watchedKeys.GetOrAdd(connectionId, _ => new HashSet<string>());
        foreach (var key in keys)
        {
            watched.Add(key);
        }
    }

    /// <summary>
    /// Unwatches all keys for a connection.
    /// </summary>
    /// <param name="connectionId">The connection ID.</param>
    public void Unwatch(string connectionId)
    {
        _watchedKeys.TryRemove(connectionId, out _);
    }

    /// <summary>
    /// Checks if a connection is in a transaction.
    /// </summary>
    /// <param name="connectionId">The connection ID.</param>
    public bool IsInTransaction(string connectionId)
    {
        return _states.TryGetValue(connectionId, out var state) &&
               state == RedisTransactionState.InTransaction;
    }

    /// <summary>
    /// Gets the transaction state for a connection.
    /// </summary>
    /// <param name="connectionId">The connection ID.</param>
    public RedisTransactionState GetState(string connectionId)
    {
        return _states.TryGetValue(connectionId, out var state) ? state : RedisTransactionState.None;
    }

    /// <summary>
    /// Notifies that a key was modified, aborting transactions watching it.
    /// </summary>
    /// <param name="key">The modified key.</param>
    public void NotifyKeyModified(string key)
    {
        foreach (var kvp in _watchedKeys)
        {
            if (kvp.Value.Contains(key))
            {
                _states[kvp.Key] = RedisTransactionState.Aborted;
            }
        }
    }

    /// <summary>
    /// Cleans up all state for a connection.
    /// </summary>
    /// <param name="connectionId">The connection ID.</param>
    public void CleanupConnection(string connectionId)
    {
        _states.TryRemove(connectionId, out _);
        _queues.TryRemove(connectionId, out _);
        _watchedKeys.TryRemove(connectionId, out _);
    }
}

/// <summary>
/// Simple Lua script interface for Redis.
/// Provides basic script caching and execution context.
/// </summary>
public sealed class RedisScriptManager
{
    private readonly ConcurrentDictionary<string, string> _scripts = new();

    /// <summary>
    /// Loads a script and returns its SHA1 hash.
    /// </summary>
    /// <param name="script">The Lua script source.</param>
    /// <returns>The SHA1 hash of the script.</returns>
    public string Load(string script)
    {
        var sha = ComputeSha1(script);
        _scripts[sha] = script;
        return sha;
    }

    /// <summary>
    /// Checks if a script exists by SHA1 hash.
    /// </summary>
    /// <param name="sha">The SHA1 hash.</param>
    /// <returns>True if the script exists.</returns>
    public bool Exists(string sha)
    {
        return _scripts.ContainsKey(sha);
    }

    /// <summary>
    /// Gets a script by SHA1 hash.
    /// </summary>
    /// <param name="sha">The SHA1 hash.</param>
    /// <returns>The script source or null if not found.</returns>
    public string? Get(string sha)
    {
        return _scripts.TryGetValue(sha, out var script) ? script : null;
    }

    /// <summary>
    /// Flushes all cached scripts.
    /// </summary>
    public void Flush()
    {
        _scripts.Clear();
    }

    /// <summary>
    /// Computes SHA1 hash for script caching.
    /// </summary>
    public static string ComputeSha1(string script)
    {
        using var sha1 = System.Security.Cryptography.SHA1.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(script);
        var hash = sha1.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
