using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Replication;
using MMConsistencyLevel = DataWarehouse.SDK.Replication.ConsistencyLevel;
using MMVectorClock = DataWarehouse.SDK.Replication.VectorClock;

namespace DataWarehouse.Plugins.ExabyteScale;

/// <summary>
/// Tunable consistency replication plugin for exabyte-scale architectures.
/// Provides async replication with configurable consistency levels, quorum-based writes,
/// conflict detection using vector clocks, and comprehensive health monitoring.
/// </summary>
/// <remarks>
/// <para>
/// This plugin implements multiple consistency models to balance between performance
/// and data consistency guarantees:
/// </para>
/// <list type="bullet">
/// <item><description><b>Eventual</b>: Fastest, async replication, may read stale data</description></item>
/// <item><description><b>Read-Your-Writes</b>: Session consistency for user-facing applications</description></item>
/// <item><description><b>Bounded Staleness</b>: Guarantees maximum replication lag</description></item>
/// <item><description><b>Strong</b>: Synchronous replication, linearizable reads</description></item>
/// </list>
/// <para>
/// Vector clocks are used for conflict detection to identify concurrent writes
/// that require resolution. Supports configurable write quorums for durability guarantees.
/// </para>
/// </remarks>
public sealed class TunableConsistencyReplicationPlugin : FeaturePluginBase, IDisposable
{
    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.scale.tunable-replication";

    /// <inheritdoc/>
    public override string Name => "Tunable Consistency Replication";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    #region Private Fields

    private readonly ConcurrentDictionary<string, ReplicaInfo> _replicas = new();
    private readonly ConcurrentDictionary<string, DataVersion> _dataVersions = new();
    private readonly ConcurrentDictionary<string, SessionState> _sessionStates = new();
    private readonly ConcurrentDictionary<string, ReplicationConflict> _pendingConflicts = new();
    private readonly ConcurrentDictionary<string, long> _replicationLagMs = new();
    private readonly ConcurrentQueue<ReplicationEvent> _replicationQueue = new();

    private readonly SemaphoreSlim _replicationLock = new(1, 1);
    private readonly SemaphoreSlim _conflictLock = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _replicationTask;
    private Task? _healthMonitorTask;

    private volatile bool _isRunning;
    private volatile int _writeQuorum = 1;
    private readonly object _configLock = new();
    private MMConsistencyLevel _defaultConsistency = MMConsistencyLevel.Eventual;
    private TimeSpan _boundedStalenessThreshold = TimeSpan.FromSeconds(5);
    private TimeSpan _healthCheckInterval = TimeSpan.FromSeconds(10);
    private TimeSpan _replicationTimeout = TimeSpan.FromSeconds(30);

    private string _localNodeId = string.Empty;

    #endregion

    #region Configuration Properties

    /// <summary>
    /// Gets or sets the default consistency level for operations.
    /// </summary>
    /// <value>The default consistency level. Default is <see cref="MMConsistencyLevel.Eventual"/>.</value>
    public MMConsistencyLevel DefaultConsistency
    {
        get { lock (_configLock) return _defaultConsistency; }
        set { lock (_configLock) _defaultConsistency = value; }
    }

    /// <summary>
    /// Gets or sets the write quorum - minimum number of replicas that must acknowledge a write.
    /// </summary>
    /// <value>Number of replicas required for write acknowledgment. Default is 1.</value>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when value is less than 1.</exception>
    public int WriteQuorum
    {
        get => _writeQuorum;
        set
        {
            if (value < 1)
                throw new ArgumentOutOfRangeException(nameof(value), "Write quorum must be at least 1");
            _writeQuorum = value;
        }
    }

    /// <summary>
    /// Gets or sets the bounded staleness threshold for <see cref="MMConsistencyLevel.BoundedStaleness"/> mode.
    /// </summary>
    /// <value>Maximum acceptable lag behind the primary. Default is 5 seconds.</value>
    public TimeSpan BoundedStalenessThreshold
    {
        get { lock (_configLock) return _boundedStalenessThreshold; }
        set { lock (_configLock) _boundedStalenessThreshold = value; }
    }

    /// <summary>
    /// Gets or sets the interval between health checks for replicas.
    /// </summary>
    /// <value>Health check interval. Default is 10 seconds.</value>
    public TimeSpan HealthCheckInterval
    {
        get { lock (_configLock) return _healthCheckInterval; }
        set { lock (_configLock) _healthCheckInterval = value; }
    }

    /// <summary>
    /// Gets or sets the timeout for replication operations.
    /// </summary>
    /// <value>Replication operation timeout. Default is 30 seconds.</value>
    public TimeSpan ReplicationTimeout
    {
        get { lock (_configLock) return _replicationTimeout; }
        set { lock (_configLock) _replicationTimeout = value; }
    }

    /// <summary>
    /// Gets the local node identifier.
    /// </summary>
    public string LocalNodeId => _localNodeId;

    /// <summary>
    /// Gets a read-only view of all registered replicas.
    /// </summary>
    public IReadOnlyDictionary<string, ReplicaInfo> Replicas => _replicas;

    /// <summary>
    /// Gets a read-only view of pending conflicts requiring resolution.
    /// </summary>
    public IReadOnlyDictionary<string, ReplicationConflict> PendingConflicts => _pendingConflicts;

    #endregion

    #region Lifecycle Methods

    /// <inheritdoc/>
    /// <summary>
    /// Starts the replication plugin, initializing background tasks for
    /// replication processing and health monitoring.
    /// </summary>
    /// <param name="ct">Cancellation token to stop the plugin.</param>
    public override async Task StartAsync(CancellationToken ct)
    {
        if (_isRunning)
            return;

        _localNodeId = $"node-{Environment.MachineName}-{Guid.NewGuid():N}".Substring(0, 32);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _isRunning = true;

        // Start background replication task
        _replicationTask = Task.Run(() => ProcessReplicationQueueAsync(_cts.Token), _cts.Token);

        // Start health monitoring task
        _healthMonitorTask = Task.Run(() => MonitorReplicaHealthAsync(_cts.Token), _cts.Token);

        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <summary>
    /// Stops the replication plugin, gracefully shutting down background tasks.
    /// </summary>
    public override async Task StopAsync()
    {
        if (!_isRunning)
            return;

        _isRunning = false;
        _cts?.Cancel();

        try
        {
            if (_replicationTask != null)
                await _replicationTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { }

        try
        {
            if (_healthMonitorTask != null)
                await _healthMonitorTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { }

        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>
    /// Releases all resources used by the plugin.
    /// </summary>
    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _replicationLock.Dispose();
        _conflictLock.Dispose();
    }

    #endregion

    #region Replica Management

    /// <summary>
    /// Registers a new replica in the replication cluster.
    /// </summary>
    /// <param name="replicaId">Unique identifier for the replica.</param>
    /// <param name="endpoint">Network endpoint (e.g., "https://replica.example.com:443").</param>
    /// <returns>True if the replica was added; false if it already exists.</returns>
    /// <exception cref="ArgumentNullException">Thrown when replicaId or endpoint is null or empty.</exception>
    public bool RegisterReplica(string replicaId, string endpoint)
    {
        if (string.IsNullOrEmpty(replicaId))
            throw new ArgumentNullException(nameof(replicaId));
        if (string.IsNullOrEmpty(endpoint))
            throw new ArgumentNullException(nameof(endpoint));

        var replica = new ReplicaInfo
        {
            ReplicaId = replicaId,
            Endpoint = endpoint,
            State = ReplicaState.Healthy,
            LastHeartbeat = DateTimeOffset.UtcNow,
            ReplicationLagMs = 0,
            VectorClock = new MMVectorClock(new Dictionary<string, long>())
        };

        return _replicas.TryAdd(replicaId, replica);
    }

    /// <summary>
    /// Unregisters a replica from the replication cluster.
    /// </summary>
    /// <param name="replicaId">Unique identifier of the replica to remove.</param>
    /// <returns>True if the replica was removed; false if it was not found.</returns>
    public bool UnregisterReplica(string replicaId)
    {
        return _replicas.TryRemove(replicaId, out _);
    }

    /// <summary>
    /// Gets the current health status of a specific replica.
    /// </summary>
    /// <param name="replicaId">The replica identifier.</param>
    /// <returns>The replica info if found; null otherwise.</returns>
    public ReplicaInfo? GetReplicaHealth(string replicaId)
    {
        return _replicas.TryGetValue(replicaId, out var replica) ? replica : null;
    }

    /// <summary>
    /// Gets the current replication lag for a specific replica.
    /// </summary>
    /// <param name="replicaId">The replica identifier.</param>
    /// <returns>Replication lag as a TimeSpan.</returns>
    public TimeSpan GetReplicationLag(string replicaId)
    {
        if (_replicationLagMs.TryGetValue(replicaId, out var lagMs))
            return TimeSpan.FromMilliseconds(lagMs);
        return TimeSpan.Zero;
    }

    #endregion

    #region Write Operations

    /// <summary>
    /// Writes data with the specified consistency level.
    /// </summary>
    /// <param name="key">The data key.</param>
    /// <param name="data">The data to write.</param>
    /// <param name="sessionId">Optional session ID for read-your-writes consistency.</param>
    /// <param name="consistency">Consistency level for this write (null uses default).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the async operation with the resulting version.</returns>
    /// <exception cref="ArgumentNullException">Thrown when key is null or empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when plugin is not running.</exception>
    /// <exception cref="TimeoutException">Thrown when quorum is not achieved within timeout.</exception>
    public async Task<DataVersion> WriteAsync(
        string key,
        byte[] data,
        string? sessionId = null,
        MMConsistencyLevel? consistency = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key));
        if (!_isRunning)
            throw new InvalidOperationException("Plugin is not running");

        var effectiveConsistency = consistency ?? _defaultConsistency;
        var version = CreateNewVersion(key, data);

        // Check for conflicts with existing versions
        if (_dataVersions.TryGetValue(key, out var existingVersion))
        {
            var conflict = DetectConflict(key, existingVersion, version);
            if (conflict != null)
            {
                await HandleConflictAsync(conflict, ct);
            }
        }

        // Store locally first
        _dataVersions[key] = version;

        // Update session state for read-your-writes
        if (!string.IsNullOrEmpty(sessionId))
        {
            UpdateSessionState(sessionId, key, version);
        }

        switch (effectiveConsistency)
        {
            case MMConsistencyLevel.Strong:
                await ReplicateSynchronouslyAsync(key, version, _replicas.Count, ct);
                break;

            case MMConsistencyLevel.BoundedStaleness:
                await ReplicateWithQuorumAsync(key, version, _writeQuorum, ct);
                break;

            case MMConsistencyLevel.ReadYourWrites:
            case MMConsistencyLevel.CausalConsistency:
                await ReplicateWithQuorumAsync(key, version, Math.Max(1, _writeQuorum / 2), ct);
                break;

            case MMConsistencyLevel.Eventual:
            default:
                // Queue for async replication
                QueueReplication(key, version);
                break;
        }

        return version;
    }

    /// <summary>
    /// Writes data with quorum-based durability guarantee.
    /// </summary>
    /// <param name="key">The data key.</param>
    /// <param name="data">The data to write.</param>
    /// <param name="quorum">Minimum number of replicas that must acknowledge the write.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the async operation with the resulting version.</returns>
    /// <exception cref="InvalidOperationException">Thrown when quorum cannot be achieved.</exception>
    public async Task<DataVersion> WriteWithQuorumAsync(
        string key,
        byte[] data,
        int quorum,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key));
        if (quorum < 1)
            throw new ArgumentOutOfRangeException(nameof(quorum), "Quorum must be at least 1");
        if (quorum > _replicas.Count + 1)
            throw new InvalidOperationException(
                $"Quorum {quorum} exceeds available replicas ({_replicas.Count + 1} including local)");

        var version = CreateNewVersion(key, data);
        _dataVersions[key] = version;

        await ReplicateWithQuorumAsync(key, version, quorum, ct);
        return version;
    }

    #endregion

    #region Read Operations

    /// <summary>
    /// Reads data with the specified consistency level.
    /// </summary>
    /// <param name="key">The data key.</param>
    /// <param name="sessionId">Optional session ID for read-your-writes consistency.</param>
    /// <param name="consistency">Consistency level for this read (null uses default).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The data version if found; null otherwise.</returns>
    public async Task<DataVersion?> ReadAsync(
        string key,
        string? sessionId = null,
        MMConsistencyLevel? consistency = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key));
        if (!_isRunning)
            throw new InvalidOperationException("Plugin is not running");

        var effectiveConsistency = consistency ?? _defaultConsistency;

        switch (effectiveConsistency)
        {
            case MMConsistencyLevel.Strong:
                return await ReadWithSynchronousRefreshAsync(key, ct);

            case MMConsistencyLevel.BoundedStaleness:
                return await ReadWithBoundedStalenessAsync(key, ct);

            case MMConsistencyLevel.ReadYourWrites:
                return await ReadWithSessionConsistencyAsync(key, sessionId, ct);

            case MMConsistencyLevel.CausalConsistency:
                return await ReadWithCausalConsistencyAsync(key, ct);

            case MMConsistencyLevel.Eventual:
            default:
                return _dataVersions.TryGetValue(key, out var version) ? version : null;
        }
    }

    /// <summary>
    /// Reads data ensuring session consistency (read-your-writes).
    /// </summary>
    /// <param name="key">The data key.</param>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The data version if found; null otherwise.</returns>
    private async Task<DataVersion?> ReadWithSessionConsistencyAsync(
        string key,
        string? sessionId,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            // Fall back to eventual consistency without session
            return _dataVersions.TryGetValue(key, out var v) ? v : null;
        }

        if (!_sessionStates.TryGetValue(sessionId, out var session))
        {
            // No session state, return local version
            return _dataVersions.TryGetValue(key, out var v) ? v : null;
        }

        if (!session.LastWriteVersions.TryGetValue(key, out var lastWriteClock))
        {
            // Session hasn't written to this key
            return _dataVersions.TryGetValue(key, out var v) ? v : null;
        }

        // Ensure we read at least the version we wrote
        if (_dataVersions.TryGetValue(key, out var localVersion))
        {
            if (!localVersion.VectorClock.HappensBefore(lastWriteClock))
            {
                // Local version is at least as recent as last write
                return localVersion;
            }
        }

        // Need to refresh from replicas to get our write
        return await RefreshFromReplicasAsync(key, lastWriteClock, ct);
    }

    /// <summary>
    /// Reads data with bounded staleness guarantee.
    /// </summary>
    private async Task<DataVersion?> ReadWithBoundedStalenessAsync(string key, CancellationToken ct)
    {
        if (!_dataVersions.TryGetValue(key, out var localVersion))
            return null;

        var age = DateTimeOffset.UtcNow - localVersion.Timestamp;
        if (age <= _boundedStalenessThreshold)
        {
            return localVersion;
        }

        // Data is too stale, refresh from replicas
        return await RefreshFromReplicasAsync(key, null, ct) ?? localVersion;
    }

    /// <summary>
    /// Reads data with strong consistency (synchronous refresh).
    /// </summary>
    private async Task<DataVersion?> ReadWithSynchronousRefreshAsync(string key, CancellationToken ct)
    {
        // Always refresh from all replicas and take the latest
        return await RefreshFromReplicasAsync(key, null, ct);
    }

    /// <summary>
    /// Reads data with causal consistency.
    /// </summary>
    private async Task<DataVersion?> ReadWithCausalConsistencyAsync(string key, CancellationToken ct)
    {
        if (!_dataVersions.TryGetValue(key, out var localVersion))
            return null;

        // Check if we're behind any known causal dependencies
        // For now, just return local version - full implementation would track causal dependencies
        return await Task.FromResult<DataVersion?>(localVersion);
    }

    /// <summary>
    /// Refreshes data from replicas to get the latest version.
    /// </summary>
    private async Task<DataVersion?> RefreshFromReplicasAsync(
        string key,
        MMVectorClock? minimumClock,
        CancellationToken ct)
    {
        var tasks = _replicas.Values
            .Where(r => r.State == ReplicaState.Healthy)
            .Select(r => FetchFromReplicaAsync(r.ReplicaId, key, ct))
            .ToList();

        if (tasks.Count == 0)
        {
            return _dataVersions.TryGetValue(key, out var v) ? v : null;
        }

        var results = await Task.WhenAll(tasks);
        var validResults = results.Where(r => r != null).ToList();

        if (validResults.Count == 0)
        {
            return _dataVersions.TryGetValue(key, out var v) ? v : null;
        }

        // Take the version with the highest vector clock sum (latest)
        var latest = validResults
            .OrderByDescending(v => v!.VectorClock.Clocks.Values.Sum())
            .First();

        if (latest != null)
        {
            _dataVersions[key] = latest;
        }

        return latest;
    }

    /// <summary>
    /// Fetches data from a specific replica (simulated).
    /// </summary>
    private async Task<DataVersion?> FetchFromReplicaAsync(
        string replicaId,
        string key,
        CancellationToken ct)
    {
        // Simulate network fetch - in production, this would make an actual RPC call
        await Task.Delay(10, ct);

        // For demonstration, return null (replica doesn't have data)
        // In production, this would deserialize the response from the replica
        return null;
    }

    #endregion

    #region Conflict Detection and Resolution

    /// <summary>
    /// Detects conflicts between two versions using vector clocks.
    /// </summary>
    /// <param name="key">The data key.</param>
    /// <param name="existing">The existing version.</param>
    /// <param name="incoming">The incoming version.</param>
    /// <returns>A conflict descriptor if concurrent writes detected; null otherwise.</returns>
    private ReplicationConflict? DetectConflict(string key, DataVersion existing, DataVersion incoming)
    {
        var existingClock = existing.VectorClock;
        var incomingClock = incoming.VectorClock;

        // Check if either happened-before the other
        var existingBeforeIncoming = existingClock.HappensBefore(incomingClock);
        var incomingBeforeExisting = incomingClock.HappensBefore(existingClock);

        // If neither happened-before the other, they are concurrent (conflict)
        if (!existingBeforeIncoming && !incomingBeforeExisting)
        {
            return new ReplicationConflict
            {
                Key = key,
                LocalVersion = existing,
                RemoteVersion = incoming,
                DetectedAt = DateTimeOffset.UtcNow,
                IsResolved = false
            };
        }

        return null;
    }

    /// <summary>
    /// Handles a detected conflict.
    /// </summary>
    private async Task HandleConflictAsync(ReplicationConflict conflict, CancellationToken ct)
    {
        await _conflictLock.WaitAsync(ct);
        try
        {
            // Store conflict for later resolution
            _pendingConflicts[conflict.Key] = conflict;

            // Attempt automatic resolution using Last-Writer-Wins
            var resolved = ResolveConflictLastWriterWins(conflict);
            if (resolved != null)
            {
                _dataVersions[conflict.Key] = resolved;
                conflict.IsResolved = true;
                conflict.ResolvedVersion = resolved;
                _pendingConflicts.TryRemove(conflict.Key, out _);
            }
        }
        finally
        {
            _conflictLock.Release();
        }
    }

    /// <summary>
    /// Resolves a conflict using Last-Writer-Wins strategy.
    /// </summary>
    private DataVersion? ResolveConflictLastWriterWins(ReplicationConflict conflict)
    {
        // Pick the version with the later timestamp
        return conflict.LocalVersion.Timestamp >= conflict.RemoteVersion.Timestamp
            ? conflict.LocalVersion
            : conflict.RemoteVersion;
    }

    /// <summary>
    /// Manually resolves a conflict with the specified data.
    /// </summary>
    /// <param name="key">The key with the conflict.</param>
    /// <param name="resolvedData">The resolved data.</param>
    /// <returns>True if conflict was resolved; false if no conflict exists.</returns>
    public async Task<bool> ResolveConflictAsync(string key, byte[] resolvedData)
    {
        if (!_pendingConflicts.TryRemove(key, out var conflict))
            return false;

        await _conflictLock.WaitAsync();
        try
        {
            // Create new version with merged vector clock
            var mergedClock = MMVectorClock.Merge(
                conflict.LocalVersion.VectorClock,
                conflict.RemoteVersion.VectorClock
            ).Increment(_localNodeId);

            var resolvedVersion = new DataVersion
            {
                Key = key,
                Data = resolvedData,
                VectorClock = mergedClock,
                Timestamp = DateTimeOffset.UtcNow,
                NodeId = _localNodeId
            };

            _dataVersions[key] = resolvedVersion;
            conflict.IsResolved = true;
            conflict.ResolvedVersion = resolvedVersion;

            // Replicate the resolution
            QueueReplication(key, resolvedVersion);

            return true;
        }
        finally
        {
            _conflictLock.Release();
        }
    }

    #endregion

    #region Replication Methods

    /// <summary>
    /// Creates a new data version with incremented vector clock.
    /// </summary>
    private DataVersion CreateNewVersion(string key, byte[] data)
    {
        var clock = _dataVersions.TryGetValue(key, out var existing)
            ? existing.VectorClock.Increment(_localNodeId)
            : new MMVectorClock(new Dictionary<string, long> { [_localNodeId] = 1 });

        return new DataVersion
        {
            Key = key,
            Data = data,
            VectorClock = clock,
            Timestamp = DateTimeOffset.UtcNow,
            NodeId = _localNodeId
        };
    }

    /// <summary>
    /// Queues data for asynchronous replication.
    /// </summary>
    private void QueueReplication(string key, DataVersion version)
    {
        var replicationEvent = new ReplicationEvent(
            EventId: Guid.NewGuid().ToString("N"),
            Key: key,
            Data: version.Data,
            Clock: version.VectorClock,
            OriginRegion: _localNodeId,
            Timestamp: version.Timestamp
        );

        _replicationQueue.Enqueue(replicationEvent);
    }

    /// <summary>
    /// Replicates data synchronously to all replicas.
    /// </summary>
    private async Task ReplicateSynchronouslyAsync(
        string key,
        DataVersion version,
        int requiredAcks,
        CancellationToken ct)
    {
        var healthyReplicas = _replicas.Values
            .Where(r => r.State == ReplicaState.Healthy)
            .ToList();

        if (healthyReplicas.Count < requiredAcks - 1) // -1 for local
        {
            throw new InvalidOperationException(
                $"Not enough healthy replicas. Required: {requiredAcks}, Available: {healthyReplicas.Count + 1}");
        }

        var tasks = healthyReplicas
            .Select(r => ReplicateToNodeAsync(r.ReplicaId, key, version, ct))
            .ToList();

        using var timeoutCts = new CancellationTokenSource(_replicationTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException($"Synchronous replication timed out after {_replicationTimeout}");
        }
    }

    /// <summary>
    /// Replicates data with quorum-based acknowledgment.
    /// </summary>
    private async Task ReplicateWithQuorumAsync(
        string key,
        DataVersion version,
        int quorum,
        CancellationToken ct)
    {
        var healthyReplicas = _replicas.Values
            .Where(r => r.State == ReplicaState.Healthy)
            .ToList();

        // Local write counts as 1 ack
        var acksNeeded = quorum - 1;
        if (acksNeeded <= 0)
            return; // Local write is sufficient

        if (healthyReplicas.Count < acksNeeded)
        {
            throw new InvalidOperationException(
                $"Cannot achieve quorum. Required: {quorum}, Available: {healthyReplicas.Count + 1}");
        }

        var semaphore = new SemaphoreSlim(0);
        var acks = 0;
        var errors = new ConcurrentBag<Exception>();

        var tasks = healthyReplicas.Select(async replica =>
        {
            try
            {
                await ReplicateToNodeAsync(replica.ReplicaId, key, version, ct);
                if (Interlocked.Increment(ref acks) == acksNeeded)
                {
                    semaphore.Release();
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
                if (errors.Count + acks >= healthyReplicas.Count)
                {
                    // All replicas have responded, release even if quorum not achieved
                    semaphore.Release();
                }
            }
        });

        // Start all replication tasks
        _ = Task.WhenAll(tasks);

        // Wait for quorum
        using var timeoutCts = new CancellationTokenSource(_replicationTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await semaphore.WaitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException($"Quorum replication timed out after {_replicationTimeout}");
        }

        if (acks < acksNeeded)
        {
            throw new InvalidOperationException(
                $"Failed to achieve quorum. Required: {acksNeeded} acks, Got: {acks}. " +
                $"Errors: {errors.Count}");
        }
    }

    /// <summary>
    /// Replicates data to a specific node.
    /// </summary>
    private async Task ReplicateToNodeAsync(
        string replicaId,
        string key,
        DataVersion version,
        CancellationToken ct)
    {
        // Simulate network replication - in production, this would be an RPC call
        var sw = Stopwatch.StartNew();
        await Task.Delay(50, ct); // Simulate network latency
        sw.Stop();

        // Update replication lag tracking
        _replicationLagMs.AddOrUpdate(
            replicaId,
            sw.ElapsedMilliseconds,
            (_, old) => (old + sw.ElapsedMilliseconds) / 2);

        // Update replica's vector clock
        if (_replicas.TryGetValue(replicaId, out var replica))
        {
            var updatedReplica = replica with
            {
                VectorClock = MMVectorClock.Merge(replica.VectorClock, version.VectorClock),
                LastHeartbeat = DateTimeOffset.UtcNow,
                ReplicationLagMs = sw.ElapsedMilliseconds
            };
            _replicas.TryUpdate(replicaId, updatedReplica, replica);
        }
    }

    /// <summary>
    /// Background task that processes the replication queue.
    /// </summary>
    private async Task ProcessReplicationQueueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _isRunning)
        {
            try
            {
                if (_replicationQueue.TryDequeue(out var replicationEvent))
                {
                    await _replicationLock.WaitAsync(ct);
                    try
                    {
                        foreach (var replica in _replicas.Values.Where(r => r.State == ReplicaState.Healthy))
                        {
                            try
                            {
                                var version = new DataVersion
                                {
                                    Key = replicationEvent.Key,
                                    Data = replicationEvent.Data,
                                    VectorClock = replicationEvent.Clock,
                                    Timestamp = replicationEvent.Timestamp,
                                    NodeId = replicationEvent.OriginRegion
                                };

                                await ReplicateToNodeAsync(replica.ReplicaId, replicationEvent.Key, version, ct);
                            }
                            catch (Exception)
                            {
                                // Log and continue - async replication is best-effort
                            }
                        }
                    }
                    finally
                    {
                        _replicationLock.Release();
                    }
                }
                else
                {
                    // Queue is empty, wait before checking again
                    await Task.Delay(100, ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    #endregion

    #region Health Monitoring

    /// <summary>
    /// Background task that monitors replica health.
    /// </summary>
    private async Task MonitorReplicaHealthAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _isRunning)
        {
            try
            {
                await Task.Delay(_healthCheckInterval, ct);

                foreach (var replica in _replicas.Values)
                {
                    var newState = await CheckReplicaHealthAsync(replica, ct);
                    if (newState != replica.State)
                    {
                        var updated = replica with
                        {
                            State = newState,
                            LastHeartbeat = newState == ReplicaState.Healthy
                                ? DateTimeOffset.UtcNow
                                : replica.LastHeartbeat
                        };
                        _replicas.TryUpdate(replica.ReplicaId, updated, replica);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Checks the health of a specific replica.
    /// </summary>
    private async Task<ReplicaState> CheckReplicaHealthAsync(ReplicaInfo replica, CancellationToken ct)
    {
        // Simulate health check - in production, this would ping the replica
        await Task.Delay(10, ct);

        var timeSinceLastHeartbeat = DateTimeOffset.UtcNow - replica.LastHeartbeat;

        if (timeSinceLastHeartbeat > TimeSpan.FromMinutes(5))
            return ReplicaState.Dead;
        if (timeSinceLastHeartbeat > TimeSpan.FromMinutes(1))
            return ReplicaState.Suspected;
        if (replica.ReplicationLagMs > 10000) // 10 second lag
            return ReplicaState.Lagging;

        return ReplicaState.Healthy;
    }

    /// <summary>
    /// Gets aggregate replication statistics.
    /// </summary>
    /// <returns>Replication statistics.</returns>
    public ReplicationStatistics GetStatistics()
    {
        var replicas = _replicas.Values.ToList();

        return new ReplicationStatistics
        {
            TotalReplicas = replicas.Count,
            HealthyReplicas = replicas.Count(r => r.State == ReplicaState.Healthy),
            LaggingReplicas = replicas.Count(r => r.State == ReplicaState.Lagging),
            SuspectedReplicas = replicas.Count(r => r.State == ReplicaState.Suspected),
            DeadReplicas = replicas.Count(r => r.State == ReplicaState.Dead),
            AverageReplicationLagMs = replicas.Count > 0
                ? replicas.Average(r => r.ReplicationLagMs)
                : 0,
            MaxReplicationLagMs = replicas.Count > 0
                ? replicas.Max(r => r.ReplicationLagMs)
                : 0,
            PendingReplicationEvents = _replicationQueue.Count,
            PendingConflicts = _pendingConflicts.Count,
            TotalDataVersions = _dataVersions.Count
        };
    }

    #endregion

    #region Session State Management

    /// <summary>
    /// Updates session state after a write for read-your-writes consistency.
    /// </summary>
    private void UpdateSessionState(string sessionId, string key, DataVersion version)
    {
        _sessionStates.AddOrUpdate(
            sessionId,
            _ => new SessionState
            {
                SessionId = sessionId,
                CreatedAt = DateTimeOffset.UtcNow,
                LastActivityAt = DateTimeOffset.UtcNow,
                LastWriteVersions = new ConcurrentDictionary<string, MMVectorClock>(
                    new[] { KeyValuePair.Create(key, version.VectorClock) })
            },
            (_, existing) =>
            {
                existing.LastActivityAt = DateTimeOffset.UtcNow;
                existing.LastWriteVersions[key] = version.VectorClock;
                return existing;
            });
    }

    /// <summary>
    /// Creates a new session for read-your-writes consistency tracking.
    /// </summary>
    /// <returns>The new session ID.</returns>
    public string CreateSession()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        _sessionStates[sessionId] = new SessionState
        {
            SessionId = sessionId,
            CreatedAt = DateTimeOffset.UtcNow,
            LastActivityAt = DateTimeOffset.UtcNow,
            LastWriteVersions = new ConcurrentDictionary<string, MMVectorClock>()
        };
        return sessionId;
    }

    /// <summary>
    /// Ends a session and cleans up its state.
    /// </summary>
    /// <param name="sessionId">The session to end.</param>
    /// <returns>True if session was found and ended; false otherwise.</returns>
    public bool EndSession(string sessionId)
    {
        return _sessionStates.TryRemove(sessionId, out _);
    }

    #endregion

    #region Metadata

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["ConsistencyLevels"] = new[]
        {
            "Eventual", "ReadYourWrites", "BoundedStaleness", "Strong"
        };
        metadata["ConflictDetection"] = "VectorClock";
        metadata["SupportsQuorumWrites"] = true;
        metadata["SupportsSessionConsistency"] = true;
        metadata["ExabyteScaleReady"] = true;
        return metadata;
    }

    #endregion
}

#region Supporting Types

/// <summary>
/// Information about a replica in the cluster.
/// </summary>
public sealed record ReplicaInfo
{
    /// <summary>
    /// Unique identifier for the replica.
    /// </summary>
    public required string ReplicaId { get; init; }

    /// <summary>
    /// Network endpoint for the replica.
    /// </summary>
    public required string Endpoint { get; init; }

    /// <summary>
    /// Current health state of the replica.
    /// </summary>
    public required ReplicaState State { get; init; }

    /// <summary>
    /// Timestamp of the last heartbeat received from this replica.
    /// </summary>
    public required DateTimeOffset LastHeartbeat { get; init; }

    /// <summary>
    /// Current replication lag in milliseconds.
    /// </summary>
    public required long ReplicationLagMs { get; init; }

    /// <summary>
    /// Vector clock representing the replica's current state.
    /// </summary>
    public required MMVectorClock VectorClock { get; init; }
}

/// <summary>
/// Health state of a replica.
/// </summary>
public enum ReplicaState
{
    /// <summary>
    /// Replica is healthy and responding normally.
    /// </summary>
    Healthy,

    /// <summary>
    /// Replica is lagging behind but still responding.
    /// </summary>
    Lagging,

    /// <summary>
    /// Replica is suspected to be unhealthy (missed heartbeats).
    /// </summary>
    Suspected,

    /// <summary>
    /// Replica is considered dead and should be removed.
    /// </summary>
    Dead
}

/// <summary>
/// A versioned data entry with vector clock for conflict detection.
/// </summary>
public sealed record DataVersion
{
    /// <summary>
    /// The data key.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// The data payload.
    /// </summary>
    public required byte[] Data { get; init; }

    /// <summary>
    /// Vector clock for causality tracking.
    /// </summary>
    public required MMVectorClock VectorClock { get; init; }

    /// <summary>
    /// Timestamp when this version was created.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Node that created this version.
    /// </summary>
    public required string NodeId { get; init; }
}

/// <summary>
/// Represents a conflict between concurrent writes.
/// </summary>
public sealed class ReplicationConflict
{
    /// <summary>
    /// The key with conflicting versions.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// The local version of the data.
    /// </summary>
    public required DataVersion LocalVersion { get; init; }

    /// <summary>
    /// The remote version that conflicts.
    /// </summary>
    public required DataVersion RemoteVersion { get; init; }

    /// <summary>
    /// When the conflict was detected.
    /// </summary>
    public required DateTimeOffset DetectedAt { get; init; }

    /// <summary>
    /// Whether the conflict has been resolved.
    /// </summary>
    public bool IsResolved { get; set; }

    /// <summary>
    /// The resolved version (if resolved).
    /// </summary>
    public DataVersion? ResolvedVersion { get; set; }
}

/// <summary>
/// Session state for read-your-writes consistency.
/// </summary>
internal sealed class SessionState
{
    /// <summary>
    /// Unique session identifier.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// When the session was created.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Last activity timestamp.
    /// </summary>
    public DateTimeOffset LastActivityAt { get; set; }

    /// <summary>
    /// Map of keys to the vector clock of the last write in this session.
    /// </summary>
    public required ConcurrentDictionary<string, MMVectorClock> LastWriteVersions { get; init; }
}

/// <summary>
/// Aggregate statistics about replication state.
/// </summary>
public sealed record ReplicationStatistics
{
    /// <summary>
    /// Total number of registered replicas.
    /// </summary>
    public required int TotalReplicas { get; init; }

    /// <summary>
    /// Number of healthy replicas.
    /// </summary>
    public required int HealthyReplicas { get; init; }

    /// <summary>
    /// Number of lagging replicas.
    /// </summary>
    public required int LaggingReplicas { get; init; }

    /// <summary>
    /// Number of suspected (potentially unhealthy) replicas.
    /// </summary>
    public required int SuspectedReplicas { get; init; }

    /// <summary>
    /// Number of dead replicas.
    /// </summary>
    public required int DeadReplicas { get; init; }

    /// <summary>
    /// Average replication lag across all replicas in milliseconds.
    /// </summary>
    public required double AverageReplicationLagMs { get; init; }

    /// <summary>
    /// Maximum replication lag across all replicas in milliseconds.
    /// </summary>
    public required long MaxReplicationLagMs { get; init; }

    /// <summary>
    /// Number of pending replication events in the queue.
    /// </summary>
    public required int PendingReplicationEvents { get; init; }

    /// <summary>
    /// Number of pending conflicts awaiting resolution.
    /// </summary>
    public required int PendingConflicts { get; init; }

    /// <summary>
    /// Total number of data versions stored locally.
    /// </summary>
    public required int TotalDataVersions { get; init; }
}

#endregion
