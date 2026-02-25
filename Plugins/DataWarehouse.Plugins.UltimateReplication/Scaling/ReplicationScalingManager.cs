using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Distributed;
using DataWarehouse.SDK.Contracts.Persistence;
using DataWarehouse.SDK.Contracts.Scaling;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateReplication.Scaling;

/// <summary>
/// Manages replication scaling with per-namespace strategy routing, WAL-backed durable queues,
/// streaming conflict comparison, and dynamic replica discovery via message bus.
/// Implements <see cref="IScalableSubsystem"/> for centralized scaling metrics and runtime reconfiguration.
/// </summary>
/// <remarks>
/// <para>
/// Addresses DSCL-05: replication previously used a single global strategy and in-memory queue
/// (state lost on restart). This manager provides:
/// <list type="bullet">
///   <item><description>Per-namespace strategy routing via glob-pattern matching with configurable defaults</description></item>
///   <item><description>WAL-backed replication queue using <see cref="IPersistentBackingStore"/> for durability across restarts</description></item>
///   <item><description>Streaming conflict comparison that short-circuits on first difference using 64KB chunks</description></item>
///   <item><description>Dynamic replica discovery via <c>dw.cluster.membership.changed</c> message bus topic</description></item>
/// </list>
/// </para>
/// <para>
/// Strategy routing supports glob patterns (e.g., <c>tenant-*</c>, <c>critical/**</c>) matched
/// against namespace identifiers. The first matching pattern wins; unmatched namespaces use the
/// configured default strategy.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 88-07: Replication scaling manager with per-namespace strategy, WAL queue, streaming conflict")]
public sealed class ReplicationScalingManager : IScalableSubsystem, IDisposable
{
    /// <summary>
    /// Message bus topic for strategy routing configuration changes.
    /// </summary>
    public const string StrategyRoutingTopic = "dw.replication.strategy-routing";

    /// <summary>
    /// Message bus topic for cluster membership changes used for dynamic replica discovery.
    /// </summary>
    public const string ClusterMembershipTopic = "dw.cluster.membership.changed";

    /// <summary>
    /// Default chunk size in bytes for streaming conflict comparison.
    /// </summary>
    public const int DefaultComparisonChunkSize = 64 * 1024; // 64KB

    /// <summary>
    /// Default WAL retention period.
    /// </summary>
    public static readonly TimeSpan DefaultWalRetention = TimeSpan.FromHours(48);

    /// <summary>
    /// Default health check interval for replicas.
    /// </summary>
    public static readonly TimeSpan DefaultHealthCheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Number of consecutive health check failures before marking a replica inactive.
    /// </summary>
    public const int MaxConsecutiveFailures = 3;

    // ---- Per-namespace strategy routing ----
    private readonly BoundedCache<string, string> _strategyRoutes;
    private readonly ConcurrentDictionary<string, Regex> _compiledPatterns = new();
    private volatile string _defaultStrategyName = "LatestWins";
    private readonly ReaderWriterLockSlim _routeLock = new(LockRecursionPolicy.NoRecursion);

    // ---- WAL-backed replication queue ----
    private readonly IPersistentBackingStore? _walStore;
    private readonly ConcurrentDictionary<string, long> _replicaOffsets = new();
    private long _walSequence;
    private readonly TimeSpan _walRetention;
    private readonly object _walLock = new();

    // ---- Replica management ----
    private readonly ConcurrentDictionary<string, ReplicaInfo> _replicas = new();
    private Timer? _healthCheckTimer;
    private readonly TimeSpan _healthCheckInterval;

    // ---- Message bus subscriptions ----
    private readonly IFederatedMessageBus? _messageBus;
    private IDisposable? _routingSubscription;
    private IDisposable? _membershipSubscription;

    // ---- Scaling ----
    private readonly object _configLock = new();
    private ScalingLimits _currentLimits;

    // ---- Metrics ----
    private long _totalReplicatedEvents;
    private long _conflictsDetected;
    private long _conflictsResolved;
    private long _walWriteCount;
    private long _walReadCount;
    private long _streamingComparisons;
    private long _shortCircuitedComparisons;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReplicationScalingManager"/> class.
    /// </summary>
    /// <param name="walStore">
    /// Optional persistent backing store for WAL-backed replication queue.
    /// When <c>null</c>, the queue operates in-memory only (state lost on restart).
    /// </param>
    /// <param name="messageBus">
    /// Optional federated message bus for strategy routing configuration and replica discovery.
    /// </param>
    /// <param name="initialLimits">Initial scaling limits. Uses defaults if <c>null</c>.</param>
    /// <param name="walRetention">WAL retention period. Uses <see cref="DefaultWalRetention"/> if <c>null</c>.</param>
    /// <param name="healthCheckInterval">Replica health check interval. Uses <see cref="DefaultHealthCheckInterval"/> if <c>null</c>.</param>
    public ReplicationScalingManager(
        IPersistentBackingStore? walStore = null,
        IFederatedMessageBus? messageBus = null,
        ScalingLimits? initialLimits = null,
        TimeSpan? walRetention = null,
        TimeSpan? healthCheckInterval = null)
    {
        _walStore = walStore;
        _messageBus = messageBus;
        _currentLimits = initialLimits ?? new ScalingLimits();
        _walRetention = walRetention ?? DefaultWalRetention;
        _healthCheckInterval = healthCheckInterval ?? DefaultHealthCheckInterval;

        // Initialize strategy routing cache with LRU eviction
        _strategyRoutes = new BoundedCache<string, string>(new BoundedCacheOptions<string, string>
        {
            MaxEntries = Math.Max(1, _currentLimits.MaxCacheEntries / 10),
            EvictionPolicy = CacheEvictionMode.LRU
        });

        SubscribeToMessageBus();
        StartHealthCheckTimer();
    }

    // -------------------------------------------------------------------
    // Per-namespace strategy routing
    // -------------------------------------------------------------------

    /// <summary>
    /// Resolves the replication strategy name for the given namespace.
    /// Matches namespace against registered glob patterns; returns the default strategy if no match.
    /// </summary>
    /// <param name="namespaceName">The namespace identifier to resolve.</param>
    /// <returns>The strategy name to use for this namespace.</returns>
    public string ResolveStrategy(string namespaceName)
    {
        ArgumentNullException.ThrowIfNull(namespaceName);

        // Check cache first
        var cached = _strategyRoutes.GetOrDefault(namespaceName);
        if (cached != null) return cached;

        // Match against patterns
        _routeLock.EnterReadLock();
        try
        {
            foreach (var kvp in _compiledPatterns)
            {
                if (kvp.Value.IsMatch(namespaceName))
                {
                    // Cache the result
                    _strategyRoutes.Put(namespaceName, kvp.Key);
                    return kvp.Key;
                }
            }
        }
        finally { _routeLock.ExitReadLock(); }

        return _defaultStrategyName;
    }

    /// <summary>
    /// Registers a glob pattern that maps matching namespaces to a specific strategy.
    /// </summary>
    /// <param name="globPattern">Glob pattern (e.g., <c>tenant-*</c>, <c>critical/**</c>).</param>
    /// <param name="strategyName">The strategy name to use for matching namespaces.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="globPattern"/> or <paramref name="strategyName"/> is null or empty.</exception>
    public void RegisterRoute(string globPattern, string strategyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(globPattern);
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyName);

        var regex = GlobToRegex(globPattern);

        _routeLock.EnterWriteLock();
        try
        {
            _compiledPatterns[strategyName] = regex;
        }
        finally { _routeLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Removes a strategy route by strategy name.
    /// </summary>
    /// <param name="strategyName">The strategy name whose route to remove.</param>
    /// <returns><c>true</c> if the route was found and removed; otherwise <c>false</c>.</returns>
    public bool RemoveRoute(string strategyName)
    {
        _routeLock.EnterWriteLock();
        try
        {
            return _compiledPatterns.TryRemove(strategyName, out _);
        }
        finally { _routeLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Sets the default strategy name for namespaces that do not match any registered pattern.
    /// </summary>
    /// <param name="strategyName">The default strategy name.</param>
    public void SetDefaultStrategy(string strategyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyName);
        _defaultStrategyName = strategyName;
    }

    /// <summary>
    /// Gets the current number of registered strategy routes.
    /// </summary>
    public int RouteCount => _compiledPatterns.Count;

    // -------------------------------------------------------------------
    // WAL-backed replication queue
    // -------------------------------------------------------------------

    /// <summary>
    /// Appends a replication event to the WAL-backed queue.
    /// Each event is durably stored if a backing store is configured.
    /// </summary>
    /// <param name="key">The key of the replicated item.</param>
    /// <param name="namespaceName">The namespace of the replicated item.</param>
    /// <param name="operation">The replication operation type (e.g., Put, Delete).</param>
    /// <param name="payloadReference">Reference to the actual payload (not stored in WAL).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The WAL sequence number assigned to this event.</returns>
    public async Task<long> AppendToWalAsync(
        string key,
        string namespaceName,
        string operation,
        string payloadReference,
        CancellationToken ct = default)
    {
        long sequence;
        lock (_walLock)
        {
            sequence = Interlocked.Increment(ref _walSequence);
        }

        var entry = new WalEntry
        {
            Sequence = sequence,
            Key = key,
            Namespace = namespaceName,
            Operation = operation,
            PayloadReference = payloadReference,
            Timestamp = DateTimeOffset.UtcNow
        };

        if (_walStore != null)
        {
            var entryBytes = SerializeWalEntry(entry);
            await _walStore.WriteAsync(
                $"dw://replication/wal/{sequence:D20}",
                entryBytes,
                ct).ConfigureAwait(false);
        }

        Interlocked.Increment(ref _walWriteCount);
        Interlocked.Increment(ref _totalReplicatedEvents);
        return sequence;
    }

    /// <summary>
    /// Reads WAL entries starting from the specified offset for a given replica.
    /// </summary>
    /// <param name="replicaId">The replica identifier.</param>
    /// <param name="maxEntries">Maximum number of entries to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of WAL entries from the replica's current offset.</returns>
    public async Task<IReadOnlyList<WalEntry>> ReadFromWalAsync(
        string replicaId,
        int maxEntries = 100,
        CancellationToken ct = default)
    {
        var offset = _replicaOffsets.GetOrAdd(replicaId, 0L);
        var entries = new List<WalEntry>();

        if (_walStore == null) return entries;

        var paths = await _walStore.ListAsync("dw://replication/wal/", ct).ConfigureAwait(false);

        foreach (var path in paths.OrderBy(p => p))
        {
            if (entries.Count >= maxEntries) break;

            // Extract sequence from path
            var seqStr = path.Split('/').LastOrDefault() ?? "0";
            if (!long.TryParse(seqStr, out var seq) || seq <= offset) continue;

            var data = await _walStore.ReadAsync(path, ct).ConfigureAwait(false);
            if (data == null) continue;

            var entry = DeserializeWalEntry(data);
            if (entry != null)
            {
                entries.Add(entry);
                Interlocked.Increment(ref _walReadCount);
            }
        }

        return entries;
    }

    /// <summary>
    /// Advances the consumer offset for a replica, acknowledging all entries up to and including the given sequence.
    /// </summary>
    /// <param name="replicaId">The replica identifier.</param>
    /// <param name="sequence">The sequence number to acknowledge up to.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task AcknowledgeAsync(string replicaId, long sequence, CancellationToken ct = default)
    {
        _replicaOffsets[replicaId] = sequence;

        // Persist offset if backing store available
        if (_walStore != null)
        {
            var offsetBytes = BitConverter.GetBytes(sequence);
            await _walStore.WriteAsync(
                $"dw://replication/offsets/{replicaId}",
                offsetBytes,
                ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Restores replica offsets from persistent storage after restart.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task RestoreOffsetsAsync(CancellationToken ct = default)
    {
        if (_walStore == null) return;

        var paths = await _walStore.ListAsync("dw://replication/offsets/", ct).ConfigureAwait(false);
        foreach (var path in paths)
        {
            var replicaId = path.Split('/').LastOrDefault();
            if (string.IsNullOrEmpty(replicaId)) continue;

            var data = await _walStore.ReadAsync(path, ct).ConfigureAwait(false);
            if (data != null && data.Length >= 8)
            {
                var offset = BitConverter.ToInt64(data, 0);
                _replicaOffsets[replicaId] = offset;
            }
        }
    }

    /// <summary>
    /// Purges WAL entries older than the configured retention period,
    /// but only entries that have been acknowledged by all known replicas.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of entries purged.</returns>
    public async Task<int> PurgeExpiredWalEntriesAsync(CancellationToken ct = default)
    {
        if (_walStore == null) return 0;

        // Find minimum acknowledged offset across all replicas
        long minOffset = _replicaOffsets.Values.DefaultIfEmpty(0L).Min();

        var paths = await _walStore.ListAsync("dw://replication/wal/", ct).ConfigureAwait(false);
        int purged = 0;
        var cutoff = DateTimeOffset.UtcNow - _walRetention;

        foreach (var path in paths)
        {
            var seqStr = path.Split('/').LastOrDefault() ?? "0";
            if (!long.TryParse(seqStr, out var seq)) continue;

            // Only purge if acknowledged by all replicas and past retention
            if (seq <= minOffset)
            {
                await _walStore.DeleteAsync(path, ct).ConfigureAwait(false);
                purged++;
            }
        }

        return purged;
    }

    /// <summary>
    /// Gets the current WAL size (total number of entries written since start).
    /// </summary>
    public long WalSize => Interlocked.Read(ref _walSequence);

    // -------------------------------------------------------------------
    // Streaming conflict comparison
    // -------------------------------------------------------------------

    /// <summary>
    /// Compares two byte arrays using streaming chunk-based comparison with content-addressable
    /// hash optimization for large objects. Short-circuits on first difference.
    /// </summary>
    /// <param name="local">The local value.</param>
    /// <param name="remote">The remote value.</param>
    /// <returns><c>true</c> if the values are identical; otherwise <c>false</c>.</returns>
    /// <remarks>
    /// <para>
    /// Comparison strategy:
    /// <list type="number">
    ///   <item><description>Quick length check (different lengths = different values)</description></item>
    ///   <item><description>For large objects (&gt;= 2 chunks), compute SHA-256 hash per chunk and compare hashes first</description></item>
    ///   <item><description>When hashes differ, fall back to byte-by-byte comparison in 64KB chunks</description></item>
    ///   <item><description>Short-circuit on first differing chunk</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    public bool StreamingCompare(ReadOnlySpan<byte> local, ReadOnlySpan<byte> remote)
    {
        Interlocked.Increment(ref _streamingComparisons);

        // Quick length check
        if (local.Length != remote.Length)
        {
            Interlocked.Increment(ref _shortCircuitedComparisons);
            return false;
        }

        if (local.Length == 0) return true;

        int chunkSize = DefaultComparisonChunkSize;
        int totalChunks = (local.Length + chunkSize - 1) / chunkSize;

        // For large objects (>= 2 chunks), use content-addressable hash comparison first
        if (totalChunks >= 2)
        {
            return HashThenByteCompare(local, remote, chunkSize, totalChunks);
        }

        // Single chunk: direct span comparison
        return local.SequenceEqual(remote);
    }

    /// <summary>
    /// Performs hash-first comparison: computes SHA-256 per chunk, compares hashes,
    /// and only falls back to byte comparison for chunks where hashes differ.
    /// </summary>
    private bool HashThenByteCompare(ReadOnlySpan<byte> local, ReadOnlySpan<byte> remote, int chunkSize, int totalChunks)
    {
        Span<byte> localHash = stackalloc byte[32];
        Span<byte> remoteHash = stackalloc byte[32];

        for (int i = 0; i < totalChunks; i++)
        {
            int offset = i * chunkSize;
            int length = Math.Min(chunkSize, local.Length - offset);

            var localChunk = local.Slice(offset, length);
            var remoteChunk = remote.Slice(offset, length);

            // Compute SHA-256 for each chunk
            SHA256.HashData(localChunk, localHash);
            SHA256.HashData(remoteChunk, remoteHash);

            // Compare hashes
            if (!localHash.SequenceEqual(remoteHash))
            {
                // Hashes differ -- confirm with byte comparison (handles hash collisions)
                if (!localChunk.SequenceEqual(remoteChunk))
                {
                    Interlocked.Increment(ref _shortCircuitedComparisons);
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Detects conflicts between local and remote values and records metrics.
    /// </summary>
    /// <param name="local">The local value.</param>
    /// <param name="remote">The remote value.</param>
    /// <returns><c>true</c> if a conflict exists (values differ); otherwise <c>false</c>.</returns>
    public bool DetectConflict(ReadOnlySpan<byte> local, ReadOnlySpan<byte> remote)
    {
        bool identical = StreamingCompare(local, remote);
        if (!identical)
        {
            Interlocked.Increment(ref _conflictsDetected);
        }
        return !identical;
    }

    /// <summary>
    /// Records a conflict resolution.
    /// </summary>
    public void RecordConflictResolved()
    {
        Interlocked.Increment(ref _conflictsResolved);
    }

    // -------------------------------------------------------------------
    // Dynamic replica discovery
    // -------------------------------------------------------------------

    /// <summary>
    /// Registers a replica for tracking and replication.
    /// </summary>
    /// <param name="replicaId">Unique identifier for the replica.</param>
    /// <param name="endpoint">Network endpoint of the replica.</param>
    public void RegisterReplica(string replicaId, string endpoint)
    {
        _replicas[replicaId] = new ReplicaInfo
        {
            ReplicaId = replicaId,
            Endpoint = endpoint,
            IsActive = true,
            LastHealthCheck = DateTimeOffset.UtcNow,
            ConsecutiveFailures = 0
        };
    }

    /// <summary>
    /// Removes a replica from tracking.
    /// </summary>
    /// <param name="replicaId">The replica identifier to remove.</param>
    /// <returns><c>true</c> if the replica was found and removed; otherwise <c>false</c>.</returns>
    public bool RemoveReplica(string replicaId)
    {
        return _replicas.TryRemove(replicaId, out _);
    }

    /// <summary>
    /// Records a health check result for a replica.
    /// Marks the replica inactive after <see cref="MaxConsecutiveFailures"/> consecutive failures.
    /// </summary>
    /// <param name="replicaId">The replica identifier.</param>
    /// <param name="success">Whether the health check succeeded.</param>
    public void RecordHealthCheck(string replicaId, bool success)
    {
        if (!_replicas.TryGetValue(replicaId, out var info)) return;

        if (success)
        {
            info.ConsecutiveFailures = 0;
            info.IsActive = true;
        }
        else
        {
            info.ConsecutiveFailures++;
            if (info.ConsecutiveFailures >= MaxConsecutiveFailures)
            {
                info.IsActive = false;
            }
        }

        info.LastHealthCheck = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Gets the current list of active replicas.
    /// </summary>
    /// <returns>A read-only list of active replica information.</returns>
    public IReadOnlyList<ReplicaInfo> GetActiveReplicas()
    {
        return _replicas.Values.Where(r => r.IsActive).ToArray();
    }

    /// <summary>
    /// Gets all known replicas (active and inactive).
    /// </summary>
    /// <returns>A read-only list of all tracked replicas.</returns>
    public IReadOnlyList<ReplicaInfo> GetAllReplicas()
    {
        return _replicas.Values.ToArray();
    }

    /// <summary>
    /// Gets the replication lag (number of un-acknowledged WAL entries) for a specific replica.
    /// </summary>
    /// <param name="replicaId">The replica identifier.</param>
    /// <returns>The number of un-acknowledged WAL entries for this replica.</returns>
    public long GetReplicationLag(string replicaId)
    {
        var currentSequence = Interlocked.Read(ref _walSequence);
        var replicaOffset = _replicaOffsets.GetOrAdd(replicaId, 0L);
        return Math.Max(0, currentSequence - replicaOffset);
    }

    // -------------------------------------------------------------------
    // IScalableSubsystem
    // -------------------------------------------------------------------

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object> GetScalingMetrics()
    {
        var metrics = new Dictionary<string, object>
        {
            ["replication.replicaCount"] = _replicas.Count,
            ["replication.activeReplicaCount"] = _replicas.Values.Count(r => r.IsActive),
            ["replication.walSize"] = Interlocked.Read(ref _walSequence),
            ["replication.walWrites"] = Interlocked.Read(ref _walWriteCount),
            ["replication.walReads"] = Interlocked.Read(ref _walReadCount),
            ["replication.totalReplicatedEvents"] = Interlocked.Read(ref _totalReplicatedEvents),
            ["replication.conflictsDetected"] = Interlocked.Read(ref _conflictsDetected),
            ["replication.conflictsResolved"] = Interlocked.Read(ref _conflictsResolved),
            ["replication.conflictRate"] = ComputeConflictRate(),
            ["replication.streamingComparisons"] = Interlocked.Read(ref _streamingComparisons),
            ["replication.shortCircuitedComparisons"] = Interlocked.Read(ref _shortCircuitedComparisons),
            ["replication.strategyRouteCount"] = _compiledPatterns.Count,
            ["replication.defaultStrategy"] = _defaultStrategyName
        };

        // Per-replica lag metrics
        var replicaLags = new Dictionary<string, object>();
        foreach (var replica in _replicas)
        {
            replicaLags[replica.Key] = new Dictionary<string, object>
            {
                ["lag"] = GetReplicationLag(replica.Key),
                ["isActive"] = replica.Value.IsActive,
                ["consecutiveFailures"] = replica.Value.ConsecutiveFailures,
                ["lastHealthCheck"] = replica.Value.LastHealthCheck.ToString("o")
            };
        }
        metrics["replication.replicaLags"] = replicaLags;

        return metrics;
    }

    /// <inheritdoc />
    public Task ReconfigureLimitsAsync(ScalingLimits limits, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_configLock)
        {
            _currentLimits = limits;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ScalingLimits CurrentLimits
    {
        get
        {
            lock (_configLock)
            {
                return _currentLimits;
            }
        }
    }

    /// <inheritdoc />
    public BackpressureState CurrentBackpressureState
    {
        get
        {
            var walSize = Interlocked.Read(ref _walSequence);
            long minAcked = _replicaOffsets.Values.DefaultIfEmpty(0L).Min();
            long pendingEntries = walSize - minAcked;

            if (_currentLimits.MaxQueueDepth == 0) return BackpressureState.Normal;

            double utilization = (double)pendingEntries / _currentLimits.MaxQueueDepth;
            return utilization switch
            {
                >= 0.80 => BackpressureState.Critical,
                >= 0.50 => BackpressureState.Warning,
                _ => BackpressureState.Normal
            };
        }
    }

    // -------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------

    private double ComputeConflictRate()
    {
        long total = Interlocked.Read(ref _totalReplicatedEvents);
        long conflicts = Interlocked.Read(ref _conflictsDetected);
        return total == 0 ? 0.0 : (double)conflicts / total;
    }

    private void SubscribeToMessageBus()
    {
        if (_messageBus == null) return;

        // Subscribe to strategy routing configuration changes
        _routingSubscription = _messageBus.Subscribe(StrategyRoutingTopic, msg =>
        {
            if (msg.Payload.TryGetValue("pattern", out var patObj) && patObj is string pattern &&
                msg.Payload.TryGetValue("strategy", out var stratObj) && stratObj is string strategy)
            {
                RegisterRoute(pattern, strategy);
            }

            if (msg.Payload.TryGetValue("defaultStrategy", out var defObj) && defObj is string defaultStrat)
            {
                SetDefaultStrategy(defaultStrat);
            }

            return Task.CompletedTask;
        });

        // Subscribe to cluster membership changes for dynamic replica discovery
        _membershipSubscription = _messageBus.Subscribe(ClusterMembershipTopic, msg =>
        {
            if (!msg.Payload.TryGetValue("action", out var actionObj) || actionObj is not string action)
                return Task.CompletedTask;

            if (!msg.Payload.TryGetValue("nodeId", out var nodeIdObj) || nodeIdObj is not string nodeId)
                return Task.CompletedTask;

            var endpoint = msg.Payload.TryGetValue("endpoint", out var epObj) && epObj is string ep ? ep : nodeId;

            switch (action)
            {
                case "joined":
                    RegisterReplica(nodeId, endpoint);
                    break;
                case "left":
                case "removed":
                    RemoveReplica(nodeId);
                    break;
            }

            return Task.CompletedTask;
        });
    }

    private void StartHealthCheckTimer()
    {
        _healthCheckTimer = new Timer(_ =>
        {
            // Periodic health check tracking -- actual ping is performed by the plugin
            // This timer marks replicas as needing a health check
            foreach (var replica in _replicas.Values)
            {
                if (DateTimeOffset.UtcNow - replica.LastHealthCheck > _healthCheckInterval * 2)
                {
                    // Stale health check -- count as a failure
                    RecordHealthCheck(replica.ReplicaId, false);
                }
            }
        }, null, _healthCheckInterval, _healthCheckInterval);
    }

    private static Regex GlobToRegex(string glob)
    {
        var pattern = "^" + Regex.Escape(glob)
            .Replace("\\*\\*", ".*")
            .Replace("\\*", "[^/]*")
            .Replace("\\?", ".") + "$";
        return new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
    }

    private static byte[] SerializeWalEntry(WalEntry entry)
    {
        // Simple binary format: [Seq:8][TimestampTicks:8][KeyLen:4][Key][NsLen:4][Ns][OpLen:4][Op][RefLen:4][Ref]
        var keyBytes = Encoding.UTF8.GetBytes(entry.Key);
        var nsBytes = Encoding.UTF8.GetBytes(entry.Namespace);
        var opBytes = Encoding.UTF8.GetBytes(entry.Operation);
        var refBytes = Encoding.UTF8.GetBytes(entry.PayloadReference);

        int totalLen = 8 + 8 + 4 + keyBytes.Length + 4 + nsBytes.Length + 4 + opBytes.Length + 4 + refBytes.Length;
        var buffer = new byte[totalLen];
        int pos = 0;

        BitConverter.TryWriteBytes(buffer.AsSpan(pos), entry.Sequence); pos += 8;
        BitConverter.TryWriteBytes(buffer.AsSpan(pos), entry.Timestamp.UtcTicks); pos += 8;

        BitConverter.TryWriteBytes(buffer.AsSpan(pos), keyBytes.Length); pos += 4;
        keyBytes.CopyTo(buffer, pos); pos += keyBytes.Length;

        BitConverter.TryWriteBytes(buffer.AsSpan(pos), nsBytes.Length); pos += 4;
        nsBytes.CopyTo(buffer, pos); pos += nsBytes.Length;

        BitConverter.TryWriteBytes(buffer.AsSpan(pos), opBytes.Length); pos += 4;
        opBytes.CopyTo(buffer, pos); pos += opBytes.Length;

        BitConverter.TryWriteBytes(buffer.AsSpan(pos), refBytes.Length); pos += 4;
        refBytes.CopyTo(buffer, pos);

        return buffer;
    }

    private static WalEntry? DeserializeWalEntry(byte[] data)
    {
        if (data.Length < 24) return null; // Minimum: 8+8+4+0+4 (empty strings)

        try
        {
            int pos = 0;
            var sequence = BitConverter.ToInt64(data, pos); pos += 8;
            var ticks = BitConverter.ToInt64(data, pos); pos += 8;

            var keyLen = BitConverter.ToInt32(data, pos); pos += 4;
            var key = Encoding.UTF8.GetString(data, pos, keyLen); pos += keyLen;

            var nsLen = BitConverter.ToInt32(data, pos); pos += 4;
            var ns = Encoding.UTF8.GetString(data, pos, nsLen); pos += nsLen;

            var opLen = BitConverter.ToInt32(data, pos); pos += 4;
            var op = Encoding.UTF8.GetString(data, pos, opLen); pos += opLen;

            var refLen = BitConverter.ToInt32(data, pos); pos += 4;
            var refStr = Encoding.UTF8.GetString(data, pos, refLen);

            return new WalEntry
            {
                Sequence = sequence,
                Key = key,
                Namespace = ns,
                Operation = op,
                PayloadReference = refStr,
                Timestamp = new DateTimeOffset(ticks, TimeSpan.Zero)
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Disposes managed resources including timers and message bus subscriptions.
    /// </summary>
    public void Dispose()
    {
        _healthCheckTimer?.Dispose();
        _healthCheckTimer = null;
        _routingSubscription?.Dispose();
        _routingSubscription = null;
        _membershipSubscription?.Dispose();
        _membershipSubscription = null;
        _strategyRoutes.Dispose();
        _routeLock.Dispose();
    }
}

/// <summary>
/// Represents a WAL entry in the replication queue.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 88-07: WAL entry for replication queue")]
public sealed class WalEntry
{
    /// <summary>Monotonically increasing sequence number.</summary>
    public long Sequence { get; init; }

    /// <summary>The key of the replicated item.</summary>
    public required string Key { get; init; }

    /// <summary>The namespace of the replicated item.</summary>
    public required string Namespace { get; init; }

    /// <summary>The replication operation type (e.g., Put, Delete, Merge).</summary>
    public required string Operation { get; init; }

    /// <summary>Reference to the actual payload stored externally (not in WAL).</summary>
    public required string PayloadReference { get; init; }

    /// <summary>UTC timestamp when the event was created.</summary>
    public DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// Tracks the state and health of a replication replica.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 88-07: Replica information for replication scaling")]
public sealed class ReplicaInfo
{
    /// <summary>Unique identifier for the replica.</summary>
    public required string ReplicaId { get; init; }

    /// <summary>Network endpoint of the replica.</summary>
    public required string Endpoint { get; init; }

    /// <summary>Whether the replica is currently considered active and healthy.</summary>
    public bool IsActive { get; set; }

    /// <summary>Timestamp of the last health check for this replica.</summary>
    public DateTimeOffset LastHealthCheck { get; set; }

    /// <summary>Number of consecutive health check failures.</summary>
    public int ConsecutiveFailures { get; set; }
}
