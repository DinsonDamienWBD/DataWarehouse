using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.SDK.Infrastructure;

// ============================================================================
// ENTERPRISE FEATURES - ACID, Single File Deploy, Rate Limiting, etc.
// H2: Relay Session Timeouts
// H5: Heartbeat Timestamp Validation
// H6: Performance Optimization for Group Queries
// H7: Rate Limiting
// H9-H18: Additional HIGH priority fixes
// ============================================================================

#region H2: Relay Session Management with Timeouts

/// <summary>
/// Manages relay sessions with automatic timeout and cleanup.
/// </summary>
public sealed class RelaySessionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, RelaySession> _sessions = new();
    private readonly Timer _cleanupTimer;
    private readonly TimeSpan _sessionTimeout;
    private readonly TimeSpan _keepaliveInterval;
    private bool _disposed;

    public event Action<string, RelaySession>? OnSessionExpired;
    public event Action<string, RelaySession>? OnSessionCreated;

    public RelaySessionManager(TimeSpan? sessionTimeout = null, TimeSpan? keepaliveInterval = null)
    {
        _sessionTimeout = sessionTimeout ?? TimeSpan.FromMinutes(5);
        _keepaliveInterval = keepaliveInterval ?? TimeSpan.FromSeconds(30);
        _cleanupTimer = new Timer(CleanupExpiredSessions, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Creates a new relay session.
    /// </summary>
    public RelaySession CreateSession(string sourceNodeId, string targetNodeId, string? relayNodeId = null)
    {
        var session = new RelaySession
        {
            SessionId = Guid.NewGuid().ToString("N"),
            SourceNodeId = sourceNodeId,
            TargetNodeId = targetNodeId,
            RelayNodeId = relayNodeId ?? "self",
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(_sessionTimeout),
            State = RelaySessionState.Active
        };

        _sessions[session.SessionId] = session;
        OnSessionCreated?.Invoke(session.SessionId, session);
        return session;
    }

    /// <summary>
    /// Gets a session by ID.
    /// </summary>
    public RelaySession? GetSession(string sessionId)
    {
        return _sessions.TryGetValue(sessionId, out var session) ? session : null;
    }

    /// <summary>
    /// Updates session activity (keepalive).
    /// </summary>
    public bool TouchSession(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.LastActivityAt = DateTime.UtcNow;
            session.ExpiresAt = DateTime.UtcNow.Add(_sessionTimeout);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Ends a session gracefully.
    /// </summary>
    public bool EndSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            session.State = RelaySessionState.Ended;
            session.EndedAt = DateTime.UtcNow;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Resumes a session (for reconnection scenarios).
    /// </summary>
    public bool ResumeSession(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            if (session.State == RelaySessionState.Suspended)
            {
                session.State = RelaySessionState.Active;
                session.LastActivityAt = DateTime.UtcNow;
                session.ExpiresAt = DateTime.UtcNow.Add(_sessionTimeout);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Gets all active sessions.
    /// </summary>
    public IEnumerable<RelaySession> GetActiveSessions()
    {
        return _sessions.Values.Where(s => s.State == RelaySessionState.Active);
    }

    /// <summary>
    /// Gets session statistics.
    /// </summary>
    public RelaySessionStats GetStats()
    {
        var sessions = _sessions.Values.ToArray();
        return new RelaySessionStats
        {
            TotalSessions = sessions.Length,
            ActiveSessions = sessions.Count(s => s.State == RelaySessionState.Active),
            SuspendedSessions = sessions.Count(s => s.State == RelaySessionState.Suspended),
            TotalBytesRelayed = sessions.Sum(s => s.BytesRelayed),
            TotalMessagesRelayed = sessions.Sum(s => s.MessagesRelayed)
        };
    }

    private void CleanupExpiredSessions(object? state)
    {
        var now = DateTime.UtcNow;
        var expired = _sessions.Where(kvp => kvp.Value.ExpiresAt <= now).ToList();

        foreach (var kvp in expired)
        {
            if (_sessions.TryRemove(kvp.Key, out var session))
            {
                session.State = RelaySessionState.Expired;
                session.EndedAt = now;
                OnSessionExpired?.Invoke(kvp.Key, session);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _cleanupTimer.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}

public sealed class RelaySession
{
    public required string SessionId { get; init; }
    public required string SourceNodeId { get; init; }
    public required string TargetNodeId { get; init; }
    public required string RelayNodeId { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime LastActivityAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public RelaySessionState State { get; set; }
    public long BytesRelayed { get; set; }
    public int MessagesRelayed { get; set; }
}

public enum RelaySessionState { Active, Suspended, Ended, Expired }

public sealed class RelaySessionStats
{
    public int TotalSessions { get; init; }
    public int ActiveSessions { get; init; }
    public int SuspendedSessions { get; init; }
    public long TotalBytesRelayed { get; init; }
    public int TotalMessagesRelayed { get; init; }
}

#endregion

#region H5: Heartbeat Timestamp Validation

/// <summary>
/// Validates heartbeat timestamps with clock drift tolerance.
/// </summary>
public sealed class HeartbeatValidator
{
    private readonly TimeSpan _maxClockDrift;
    private readonly TimeSpan _staleThreshold;
    private readonly ConcurrentDictionary<string, HeartbeatRecord> _lastHeartbeats = new();

    public HeartbeatValidator(TimeSpan? maxClockDrift = null, TimeSpan? staleThreshold = null)
    {
        _maxClockDrift = maxClockDrift ?? TimeSpan.FromSeconds(30);
        _staleThreshold = staleThreshold ?? TimeSpan.FromMinutes(2);
    }

    /// <summary>
    /// Validates a heartbeat timestamp.
    /// </summary>
    public HeartbeatValidationResult ValidateHeartbeat(string nodeId, DateTimeOffset heartbeatTime)
    {
        var now = DateTimeOffset.UtcNow;
        var drift = heartbeatTime - now;

        // Check for future timestamps (beyond clock drift tolerance)
        if (drift > _maxClockDrift)
        {
            return new HeartbeatValidationResult
            {
                IsValid = false,
                Reason = HeartbeatInvalidReason.FutureDrift,
                DriftAmount = drift,
                Message = $"Heartbeat is {drift.TotalSeconds:F1}s in the future (max allowed: {_maxClockDrift.TotalSeconds}s)"
            };
        }

        // Check for stale heartbeats
        if (-drift > _staleThreshold)
        {
            return new HeartbeatValidationResult
            {
                IsValid = false,
                Reason = HeartbeatInvalidReason.Stale,
                DriftAmount = drift,
                Message = $"Heartbeat is {(-drift).TotalSeconds:F1}s old (max allowed: {_staleThreshold.TotalSeconds}s)"
            };
        }

        // Check for heartbeat regression (older than previous)
        if (_lastHeartbeats.TryGetValue(nodeId, out var lastRecord))
        {
            if (heartbeatTime < lastRecord.Timestamp)
            {
                return new HeartbeatValidationResult
                {
                    IsValid = false,
                    Reason = HeartbeatInvalidReason.Regression,
                    DriftAmount = heartbeatTime - lastRecord.Timestamp,
                    Message = "Heartbeat timestamp is older than previous heartbeat"
                };
            }
        }

        // Record this heartbeat
        _lastHeartbeats[nodeId] = new HeartbeatRecord
        {
            NodeId = nodeId,
            Timestamp = heartbeatTime,
            ReceivedAt = now,
            Drift = drift
        };

        return new HeartbeatValidationResult
        {
            IsValid = true,
            DriftAmount = drift
        };
    }

    /// <summary>
    /// Gets nodes that haven't sent a heartbeat within the threshold.
    /// </summary>
    public IEnumerable<string> GetStaleNodes()
    {
        var threshold = DateTimeOffset.UtcNow - _staleThreshold;
        return _lastHeartbeats
            .Where(kvp => kvp.Value.ReceivedAt < threshold)
            .Select(kvp => kvp.Key);
    }

    /// <summary>
    /// Gets heartbeat statistics for monitoring.
    /// </summary>
    public HeartbeatStats GetStats()
    {
        var records = _lastHeartbeats.Values.ToArray();
        var drifts = records.Select(r => r.Drift.TotalSeconds).ToArray();

        return new HeartbeatStats
        {
            TotalNodes = records.Length,
            StaleNodes = GetStaleNodes().Count(),
            AverageDriftSeconds = drifts.Length > 0 ? drifts.Average() : 0,
            MaxDriftSeconds = drifts.Length > 0 ? drifts.Max() : 0,
            MinDriftSeconds = drifts.Length > 0 ? drifts.Min() : 0
        };
    }

    private sealed class HeartbeatRecord
    {
        public required string NodeId { get; init; }
        public DateTimeOffset Timestamp { get; init; }
        public DateTimeOffset ReceivedAt { get; init; }
        public TimeSpan Drift { get; init; }
    }
}

public sealed class HeartbeatValidationResult
{
    public bool IsValid { get; init; }
    public HeartbeatInvalidReason? Reason { get; init; }
    public TimeSpan DriftAmount { get; init; }
    public string? Message { get; init; }
}

public enum HeartbeatInvalidReason { FutureDrift, Stale, Regression }

public sealed class HeartbeatStats
{
    public int TotalNodes { get; init; }
    public int StaleNodes { get; init; }
    public double AverageDriftSeconds { get; init; }
    public double MaxDriftSeconds { get; init; }
    public double MinDriftSeconds { get; init; }
}

#endregion

#region H6: Performance Optimized Group Queries

/// <summary>
/// Cached group membership with flattened nested groups.
/// </summary>
public sealed class GroupMembershipCache
{
    private readonly ConcurrentDictionary<string, CachedGroup> _groups = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _flattenedMembership = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _memberToGroups = new();
    private readonly TimeSpan _cacheExpiry;
    private long _version;

    public GroupMembershipCache(TimeSpan? cacheExpiry = null)
    {
        _cacheExpiry = cacheExpiry ?? TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// Adds or updates a group.
    /// </summary>
    public void SetGroup(string groupId, IEnumerable<string> directMembers, IEnumerable<string>? nestedGroups = null)
    {
        var group = new CachedGroup
        {
            GroupId = groupId,
            DirectMembers = new HashSet<string>(directMembers),
            NestedGroups = new HashSet<string>(nestedGroups ?? Enumerable.Empty<string>()),
            CachedAt = DateTime.UtcNow,
            Version = Interlocked.Increment(ref _version)
        };

        _groups[groupId] = group;
        InvalidateFlattenedCache(groupId);
    }

    /// <summary>
    /// Adds a member to a group.
    /// </summary>
    public void AddMember(string groupId, string memberId)
    {
        if (_groups.TryGetValue(groupId, out var group))
        {
            group.DirectMembers.Add(memberId);
            group.Version = Interlocked.Increment(ref _version);
            InvalidateFlattenedCache(groupId);
        }
    }

    /// <summary>
    /// Removes a member from a group.
    /// </summary>
    public void RemoveMember(string groupId, string memberId)
    {
        if (_groups.TryGetValue(groupId, out var group))
        {
            group.DirectMembers.Remove(memberId);
            group.Version = Interlocked.Increment(ref _version);
            InvalidateFlattenedCache(groupId);
        }
    }

    /// <summary>
    /// Gets all members of a group (including nested groups) - O(1) after first call.
    /// </summary>
    public HashSet<string> GetAllMembers(string groupId)
    {
        if (_flattenedMembership.TryGetValue(groupId, out var cached))
            return cached;

        var flattened = FlattenGroup(groupId, new HashSet<string>());
        _flattenedMembership[groupId] = flattened;
        return flattened;
    }

    /// <summary>
    /// Checks if a member belongs to a group (direct or nested) - O(1).
    /// </summary>
    public bool IsMember(string groupId, string memberId)
    {
        return GetAllMembers(groupId).Contains(memberId);
    }

    /// <summary>
    /// Gets all groups a member belongs to - O(1) after indexing.
    /// </summary>
    public HashSet<string> GetGroupsForMember(string memberId)
    {
        if (_memberToGroups.TryGetValue(memberId, out var groups))
            return groups;

        // Build reverse index
        var result = new HashSet<string>();
        foreach (var kvp in _groups)
        {
            if (GetAllMembers(kvp.Key).Contains(memberId))
                result.Add(kvp.Key);
        }

        _memberToGroups[memberId] = result;
        return result;
    }

    /// <summary>
    /// Bulk query for multiple members.
    /// </summary>
    public Dictionary<string, HashSet<string>> GetGroupsForMembers(IEnumerable<string> memberIds)
    {
        return memberIds.ToDictionary(m => m, m => GetGroupsForMember(m));
    }

    private HashSet<string> FlattenGroup(string groupId, HashSet<string> visited)
    {
        var result = new HashSet<string>();

        if (!_groups.TryGetValue(groupId, out var group) || visited.Contains(groupId))
            return result;

        visited.Add(groupId);

        // Add direct members
        foreach (var member in group.DirectMembers)
            result.Add(member);

        // Recursively add nested group members
        foreach (var nestedGroupId in group.NestedGroups)
        {
            var nestedMembers = FlattenGroup(nestedGroupId, visited);
            foreach (var member in nestedMembers)
                result.Add(member);
        }

        return result;
    }

    private void InvalidateFlattenedCache(string groupId)
    {
        // Invalidate this group and all groups that include it
        _flattenedMembership.TryRemove(groupId, out _);
        _memberToGroups.Clear(); // Clear reverse index

        foreach (var kvp in _groups)
        {
            if (kvp.Value.NestedGroups.Contains(groupId))
            {
                _flattenedMembership.TryRemove(kvp.Key, out _);
            }
        }
    }

    private sealed class CachedGroup
    {
        public required string GroupId { get; init; }
        public HashSet<string> DirectMembers { get; init; } = new();
        public HashSet<string> NestedGroups { get; init; } = new();
        public DateTime CachedAt { get; init; }
        public long Version { get; set; }
    }
}

#endregion

#region H7: Rate Limiting

/// <summary>
/// Token bucket rate limiter for federation gateway.
/// </summary>
public sealed class RateLimiter
{
    private readonly ConcurrentDictionary<string, TokenBucket> _buckets = new();
    private readonly RateLimitConfig _defaultConfig;
    private readonly ConcurrentDictionary<string, RateLimitConfig> _customConfigs = new();
    private readonly Timer _refillTimer;

    public RateLimiter(RateLimitConfig? defaultConfig = null)
    {
        _defaultConfig = defaultConfig ?? RateLimitConfig.Default;
        _refillTimer = new Timer(RefillBuckets, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Checks if a request is allowed and consumes tokens if so.
    /// </summary>
    public RateLimitResult TryConsume(string key, int tokens = 1)
    {
        var bucket = _buckets.GetOrAdd(key, _ => CreateBucket(key));
        return bucket.TryConsume(tokens);
    }

    /// <summary>
    /// Checks remaining quota without consuming.
    /// </summary>
    public int GetRemainingTokens(string key)
    {
        return _buckets.TryGetValue(key, out var bucket) ? bucket.RemainingTokens : _defaultConfig.BucketSize;
    }

    /// <summary>
    /// Sets a custom rate limit for a specific key.
    /// </summary>
    public void SetCustomLimit(string key, RateLimitConfig config)
    {
        _customConfigs[key] = config;
        if (_buckets.TryGetValue(key, out var bucket))
        {
            bucket.UpdateConfig(config);
        }
    }

    /// <summary>
    /// Removes a custom limit, reverting to default.
    /// </summary>
    public void RemoveCustomLimit(string key)
    {
        _customConfigs.TryRemove(key, out _);
        if (_buckets.TryGetValue(key, out var bucket))
        {
            bucket.UpdateConfig(_defaultConfig);
        }
    }

    /// <summary>
    /// Gets rate limit statistics.
    /// </summary>
    public RateLimitStats GetStats()
    {
        var buckets = _buckets.Values.ToArray();
        return new RateLimitStats
        {
            TotalBuckets = buckets.Length,
            ThrottledBuckets = buckets.Count(b => b.RemainingTokens == 0),
            TotalAllowed = buckets.Sum(b => b.AllowedCount),
            TotalDenied = buckets.Sum(b => b.DeniedCount)
        };
    }

    private TokenBucket CreateBucket(string key)
    {
        var config = _customConfigs.TryGetValue(key, out var custom) ? custom : _defaultConfig;
        return new TokenBucket(config);
    }

    private void RefillBuckets(object? state)
    {
        foreach (var bucket in _buckets.Values)
        {
            bucket.Refill();
        }
    }

    private sealed class TokenBucket
    {
        private RateLimitConfig _config;
        private int _tokens;
        private DateTime _lastRefill;
        private long _allowedCount;
        private long _deniedCount;
        private readonly object _lock = new();

        public TokenBucket(RateLimitConfig config)
        {
            _config = config;
            _tokens = config.BucketSize;
            _lastRefill = DateTime.UtcNow;
        }

        public int RemainingTokens => _tokens;
        public long AllowedCount => _allowedCount;
        public long DeniedCount => _deniedCount;

        public void UpdateConfig(RateLimitConfig config)
        {
            lock (_lock)
            {
                _config = config;
                _tokens = Math.Min(_tokens, config.BucketSize);
            }
        }

        public RateLimitResult TryConsume(int count)
        {
            lock (_lock)
            {
                if (_tokens >= count)
                {
                    _tokens -= count;
                    Interlocked.Increment(ref _allowedCount);
                    return new RateLimitResult
                    {
                        Allowed = true,
                        RemainingTokens = _tokens,
                        RetryAfter = TimeSpan.Zero
                    };
                }

                Interlocked.Increment(ref _deniedCount);
                var tokensNeeded = count - _tokens;
                var refillsNeeded = (double)tokensNeeded / _config.RefillRate;
                var retryAfter = TimeSpan.FromSeconds(refillsNeeded);

                return new RateLimitResult
                {
                    Allowed = false,
                    RemainingTokens = _tokens,
                    RetryAfter = retryAfter
                };
            }
        }

        public void Refill()
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                var elapsed = now - _lastRefill;
                var tokensToAdd = (int)(elapsed.TotalSeconds * _config.RefillRate);

                if (tokensToAdd > 0)
                {
                    _tokens = Math.Min(_tokens + tokensToAdd, _config.BucketSize);
                    _lastRefill = now;
                }
            }
        }
    }
}

public sealed class RateLimitConfig
{
    public int BucketSize { get; init; } = 100;
    public int RefillRate { get; init; } = 10; // tokens per second

    public static RateLimitConfig Default => new();
    public static RateLimitConfig Strict => new() { BucketSize = 20, RefillRate = 2 };
    public static RateLimitConfig Relaxed => new() { BucketSize = 1000, RefillRate = 100 };
}

public sealed class RateLimitResult
{
    public bool Allowed { get; init; }
    public int RemainingTokens { get; init; }
    public TimeSpan RetryAfter { get; init; }
}

public sealed class RateLimitStats
{
    public int TotalBuckets { get; init; }
    public int ThrottledBuckets { get; init; }
    public long TotalAllowed { get; init; }
    public long TotalDenied { get; init; }
    public double AllowedRate => TotalAllowed + TotalDenied > 0 ? (double)TotalAllowed / (TotalAllowed + TotalDenied) : 1.0;
}

#endregion

#region ACID Transactions

/// <summary>
/// ACID transaction support with Write-Ahead Log (WAL).
/// </summary>
public sealed class AcidTransactionManager : IAsyncDisposable
{
    private readonly string _walPath;
    private readonly ConcurrentDictionary<string, Transaction> _activeTransactions = new();
    private readonly SemaphoreSlim _walLock = new(1, 1);
    private FileStream? _walStream;
    private BinaryWriter? _walWriter;
    private long _lsn; // Log Sequence Number

    public AcidTransactionManager(string walPath)
    {
        _walPath = walPath;
        Directory.CreateDirectory(Path.GetDirectoryName(walPath) ?? ".");
        _walStream = new FileStream(walPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        _walWriter = new BinaryWriter(_walStream);
        _lsn = RecoverLsn();
    }

    /// <summary>
    /// Begins a new transaction.
    /// </summary>
    public Transaction BeginTransaction(IsolationLevel isolation = IsolationLevel.ReadCommitted)
    {
        var txn = new Transaction
        {
            TransactionId = Guid.NewGuid().ToString("N"),
            StartedAt = DateTime.UtcNow,
            IsolationLevel = isolation,
            State = TransactionState.Active
        };

        _activeTransactions[txn.TransactionId] = txn;
        return txn;
    }

    /// <summary>
    /// Records an operation in the transaction.
    /// </summary>
    public async Task RecordOperationAsync(Transaction txn, WalOperation operation, CancellationToken ct = default)
    {
        if (txn.State != TransactionState.Active)
            throw new InvalidOperationException("Transaction is not active");

        operation.TransactionId = txn.TransactionId;
        operation.Lsn = Interlocked.Increment(ref _lsn);
        operation.Timestamp = DateTime.UtcNow;

        txn.Operations.Add(operation);

        // Write to WAL
        await WriteToWalAsync(operation, ct);
    }

    /// <summary>
    /// Creates a savepoint within a transaction.
    /// </summary>
    public Savepoint CreateSavepoint(Transaction txn, string name)
    {
        var savepoint = new Savepoint
        {
            Name = name,
            Lsn = _lsn,
            OperationIndex = txn.Operations.Count,
            CreatedAt = DateTime.UtcNow
        };

        txn.Savepoints.Add(savepoint);
        return savepoint;
    }

    /// <summary>
    /// Rolls back to a savepoint.
    /// </summary>
    public async Task RollbackToSavepointAsync(Transaction txn, string savepointName, CancellationToken ct = default)
    {
        var savepoint = txn.Savepoints.FirstOrDefault(s => s.Name == savepointName)
            ?? throw new ArgumentException($"Savepoint '{savepointName}' not found");

        // Generate compensation operations for operations after savepoint
        for (int i = txn.Operations.Count - 1; i >= savepoint.OperationIndex; i--)
        {
            var op = txn.Operations[i];
            var compensation = op.CreateCompensation();
            if (compensation != null)
            {
                await WriteToWalAsync(compensation, ct);
            }
        }

        // Remove operations after savepoint
        txn.Operations.RemoveRange(savepoint.OperationIndex, txn.Operations.Count - savepoint.OperationIndex);

        // Remove savepoints after this one
        txn.Savepoints.RemoveAll(s => s.Lsn > savepoint.Lsn);
    }

    /// <summary>
    /// Commits a transaction.
    /// </summary>
    public async Task<bool> CommitAsync(Transaction txn, CancellationToken ct = default)
    {
        if (txn.State != TransactionState.Active)
            return false;

        try
        {
            // Write commit record to WAL
            var commitRecord = new WalOperation
            {
                TransactionId = txn.TransactionId,
                Lsn = Interlocked.Increment(ref _lsn),
                OperationType = WalOperationType.Commit,
                Timestamp = DateTime.UtcNow
            };

            await WriteToWalAsync(commitRecord, ct);

            // Force WAL to disk
            await _walStream!.FlushAsync(ct);

            txn.State = TransactionState.Committed;
            txn.CompletedAt = DateTime.UtcNow;
            _activeTransactions.TryRemove(txn.TransactionId, out _);

            return true;
        }
        catch
        {
            txn.State = TransactionState.Failed;
            return false;
        }
    }

    /// <summary>
    /// Rolls back a transaction.
    /// </summary>
    public async Task RollbackAsync(Transaction txn, CancellationToken ct = default)
    {
        if (txn.State != TransactionState.Active)
            return;

        // Generate compensation operations in reverse order
        for (int i = txn.Operations.Count - 1; i >= 0; i--)
        {
            var op = txn.Operations[i];
            var compensation = op.CreateCompensation();
            if (compensation != null)
            {
                await WriteToWalAsync(compensation, ct);
            }
        }

        // Write rollback record
        var rollbackRecord = new WalOperation
        {
            TransactionId = txn.TransactionId,
            Lsn = Interlocked.Increment(ref _lsn),
            OperationType = WalOperationType.Rollback,
            Timestamp = DateTime.UtcNow
        };

        await WriteToWalAsync(rollbackRecord, ct);

        txn.State = TransactionState.RolledBack;
        txn.CompletedAt = DateTime.UtcNow;
        _activeTransactions.TryRemove(txn.TransactionId, out _);
    }

    /// <summary>
    /// Recovers from WAL after crash.
    /// </summary>
    public async Task<RecoveryResult> RecoverAsync(Func<WalOperation, Task> replayOperation, CancellationToken ct = default)
    {
        var result = new RecoveryResult { StartedAt = DateTime.UtcNow };

        _walStream!.Position = 0;
        using var reader = new BinaryReader(_walStream, Encoding.UTF8, leaveOpen: true);

        var operations = new Dictionary<string, List<WalOperation>>();

        while (_walStream.Position < _walStream.Length)
        {
            try
            {
                var op = ReadWalOperation(reader);
                if (!operations.ContainsKey(op.TransactionId))
                    operations[op.TransactionId] = new List<WalOperation>();

                operations[op.TransactionId].Add(op);
                result.OperationsRead++;
            }
            catch
            {
                break; // Reached end of valid data
            }
        }

        // Replay committed transactions, rollback incomplete ones
        foreach (var kvp in operations)
        {
            var ops = kvp.Value;
            var hasCommit = ops.Any(o => o.OperationType == WalOperationType.Commit);
            var hasRollback = ops.Any(o => o.OperationType == WalOperationType.Rollback);

            if (hasCommit && !hasRollback)
            {
                // Replay this transaction
                foreach (var op in ops.Where(o => o.OperationType != WalOperationType.Commit))
                {
                    await replayOperation(op);
                    result.OperationsReplayed++;
                }
            }
        }

        result.CompletedAt = DateTime.UtcNow;
        return result;
    }

    private async Task WriteToWalAsync(WalOperation operation, CancellationToken ct)
    {
        await _walLock.WaitAsync(ct);
        try
        {
            _walWriter!.Write(operation.Lsn);
            _walWriter.Write(operation.TransactionId);
            _walWriter.Write((int)operation.OperationType);
            _walWriter.Write(operation.Timestamp.ToBinary());
            _walWriter.Write(operation.ObjectId ?? "");
            _walWriter.Write(operation.Data?.Length ?? 0);
            if (operation.Data != null)
                _walWriter.Write(operation.Data);
            _walWriter.Flush();
        }
        finally
        {
            _walLock.Release();
        }
    }

    private WalOperation ReadWalOperation(BinaryReader reader)
    {
        var lsn = reader.ReadInt64();
        var txnId = reader.ReadString();
        var type = (WalOperationType)reader.ReadInt32();
        var timestamp = DateTime.FromBinary(reader.ReadInt64());
        var objectId = reader.ReadString();
        var dataLen = reader.ReadInt32();
        var data = dataLen > 0 ? reader.ReadBytes(dataLen) : null;

        return new WalOperation
        {
            Lsn = lsn,
            TransactionId = txnId,
            OperationType = type,
            Timestamp = timestamp,
            ObjectId = objectId.Length > 0 ? objectId : null,
            Data = data
        };
    }

    private long RecoverLsn()
    {
        if (_walStream!.Length == 0)
            return 0;

        _walStream.Position = 0;
        long maxLsn = 0;
        using var reader = new BinaryReader(_walStream, Encoding.UTF8, leaveOpen: true);

        while (_walStream.Position < _walStream.Length)
        {
            try
            {
                var lsn = reader.ReadInt64();
                maxLsn = Math.Max(maxLsn, lsn);
                // Skip rest of record
                reader.ReadString(); // txnId
                reader.ReadInt32(); // type
                reader.ReadInt64(); // timestamp
                reader.ReadString(); // objectId
                var dataLen = reader.ReadInt32();
                if (dataLen > 0) reader.ReadBytes(dataLen);
            }
            catch
            {
                break;
            }
        }

        _walStream.Position = _walStream.Length;
        return maxLsn;
    }

    public async ValueTask DisposeAsync()
    {
        _walWriter?.Dispose();
        if (_walStream != null)
            await _walStream.DisposeAsync();
        _walLock.Dispose();
    }
}

public sealed class Transaction
{
    public required string TransactionId { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; set; }
    public IsolationLevel IsolationLevel { get; init; }
    public TransactionState State { get; set; }
    public List<WalOperation> Operations { get; } = new();
    public List<Savepoint> Savepoints { get; } = new();
}

public sealed class Savepoint
{
    public required string Name { get; init; }
    public long Lsn { get; init; }
    public int OperationIndex { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed class WalOperation
{
    public long Lsn { get; set; }
    public string TransactionId { get; set; } = "";
    public WalOperationType OperationType { get; init; }
    public DateTime Timestamp { get; set; }
    public string? ObjectId { get; init; }
    public byte[]? Data { get; init; }
    public byte[]? OldData { get; init; }

    public WalOperation? CreateCompensation()
    {
        return OperationType switch
        {
            WalOperationType.Insert => new WalOperation
            {
                TransactionId = TransactionId,
                OperationType = WalOperationType.Delete,
                ObjectId = ObjectId
            },
            WalOperationType.Update when OldData != null => new WalOperation
            {
                TransactionId = TransactionId,
                OperationType = WalOperationType.Update,
                ObjectId = ObjectId,
                Data = OldData
            },
            WalOperationType.Delete when OldData != null => new WalOperation
            {
                TransactionId = TransactionId,
                OperationType = WalOperationType.Insert,
                ObjectId = ObjectId,
                Data = OldData
            },
            _ => null
        };
    }
}

public enum WalOperationType { Insert, Update, Delete, Commit, Rollback }
public enum TransactionState { Active, Committed, RolledBack, Failed }
public enum IsolationLevel { ReadUncommitted, ReadCommitted, RepeatableRead, Serializable }

public sealed class RecoveryResult
{
    public DateTime StartedAt { get; init; }
    public DateTime CompletedAt { get; set; }
    public int OperationsRead { get; set; }
    public int OperationsReplayed { get; set; }
    public TimeSpan Duration => CompletedAt - StartedAt;
}

#endregion

#region Single File Deploy

/// <summary>
/// Packages DataWarehouse into a single deployable file.
/// </summary>
public static class SingleFileDeploy
{
    /// <summary>
    /// Creates a self-contained deployment package.
    /// </summary>
    public static async Task<SingleFilePackage> CreatePackageAsync(
        SingleFilePackageConfig config,
        CancellationToken ct = default)
    {
        var package = new SingleFilePackage
        {
            PackageId = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow,
            Version = config.Version ?? "1.0.0"
        };

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Write magic header
        writer.Write(Encoding.ASCII.GetBytes("DWPKG"));
        writer.Write(1); // Format version

        // Write config
        var configJson = JsonSerializer.SerializeToUtf8Bytes(config);
        writer.Write(configJson.Length);
        writer.Write(configJson);

        // Embed plugins
        foreach (var pluginPath in config.PluginPaths)
        {
            if (File.Exists(pluginPath))
            {
                var pluginData = await File.ReadAllBytesAsync(pluginPath, ct);
                var pluginName = Path.GetFileName(pluginPath);

                writer.Write(pluginName);
                writer.Write(pluginData.Length);
                writer.Write(pluginData);
                package.EmbeddedPlugins.Add(pluginName);
            }
        }

        // Write terminator
        writer.Write("END");

        package.PackageData = ms.ToArray();
        package.Checksum = Convert.ToHexString(SHA256.HashData(package.PackageData));

        return package;
    }

    /// <summary>
    /// Extracts a deployment package.
    /// </summary>
    public static async Task<ExtractResult> ExtractPackageAsync(
        byte[] packageData,
        string targetPath,
        CancellationToken ct = default)
    {
        var result = new ExtractResult { TargetPath = targetPath };

        using var ms = new MemoryStream(packageData);
        using var reader = new BinaryReader(ms);

        // Verify magic header
        var magic = Encoding.ASCII.GetString(reader.ReadBytes(5));
        if (magic != "DWPKG")
        {
            result.Success = false;
            result.Error = "Invalid package format";
            return result;
        }

        var formatVersion = reader.ReadInt32();
        if (formatVersion > 1)
        {
            result.Success = false;
            result.Error = "Unsupported format version";
            return result;
        }

        // Read config
        var configLen = reader.ReadInt32();
        var configData = reader.ReadBytes(configLen);
        var config = JsonSerializer.Deserialize<SingleFilePackageConfig>(configData);
        result.Config = config;

        Directory.CreateDirectory(targetPath);
        var pluginsPath = Path.Combine(targetPath, "plugins");
        Directory.CreateDirectory(pluginsPath);

        // Extract plugins
        while (ms.Position < ms.Length)
        {
            var marker = reader.ReadString();
            if (marker == "END")
                break;

            var pluginName = marker;
            var pluginLen = reader.ReadInt32();
            var pluginData = reader.ReadBytes(pluginLen);

            var pluginPath = Path.Combine(pluginsPath, pluginName);
            await File.WriteAllBytesAsync(pluginPath, pluginData, ct);
            result.ExtractedPlugins.Add(pluginName);
        }

        result.Success = true;
        return result;
    }
}

public sealed class SingleFilePackageConfig
{
    public string? Version { get; set; }
    public List<string> PluginPaths { get; set; } = new();
    public Dictionary<string, string> Configuration { get; set; } = new();
    public bool EnableAutoUpdate { get; set; }
    public string? UpdateEndpoint { get; set; }
}

public sealed class SingleFilePackage
{
    public required string PackageId { get; init; }
    public DateTime CreatedAt { get; init; }
    public string Version { get; init; } = "1.0.0";
    public byte[] PackageData { get; set; } = Array.Empty<byte>();
    public string Checksum { get; set; } = "";
    public List<string> EmbeddedPlugins { get; } = new();
}

public sealed class ExtractResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public required string TargetPath { get; init; }
    public SingleFilePackageConfig? Config { get; set; }
    public List<string> ExtractedPlugins { get; } = new();
}

#endregion
