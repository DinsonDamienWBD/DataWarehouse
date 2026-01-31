using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Replication;

namespace DataWarehouse.Plugins.MultiMaster;

/// <summary>
/// Multi-master replication plugin with configurable conflict resolution.
/// Supports Last-Writer-Wins, Vector Clocks, and CRDT-based automatic merge.
/// </summary>
public class DefaultMultiMasterPlugin : MultiMasterReplicationPluginBase
{
    /// <inheritdoc />
    public override string Id => "com.datawarehouse.replication.multimaster";

    /// <inheritdoc />
    public override string Name => "Default Multi-Master Replication";

    private readonly string _localRegion;
    private readonly List<string> _connectedRegions = new();
    private readonly ConcurrentDictionary<string, (byte[] Data, VectorClock Clock, DateTimeOffset Timestamp)> _store = new();
    private readonly ConcurrentDictionary<string, ReplicationConflict> _pendingConflicts = new();
    private readonly ConcurrentQueue<ReplicationEvent> _eventLog = new();
    private readonly SemaphoreSlim _replicationLock = new(1, 1);

    /// <inheritdoc />
    public override string LocalRegion => _localRegion;

    /// <inheritdoc />
    public override IReadOnlyList<string> ConnectedRegions => _connectedRegions.AsReadOnly();

    /// <summary>
    /// Initializes a new instance of the DefaultMultiMasterPlugin.
    /// </summary>
    /// <param name="localRegion">Local region identifier (default: "region-1").</param>
    public DefaultMultiMasterPlugin(string localRegion = "region-1")
    {
        _localRegion = localRegion;
    }

    /// <summary>
    /// Add a connected region for replication.
    /// </summary>
    /// <param name="regionId">Region identifier to add.</param>
    public void AddRegion(string regionId)
    {
        if (!_connectedRegions.Contains(regionId))
            _connectedRegions.Add(regionId);
    }

    /// <summary>
    /// Performs the actual write operation to local storage.
    /// Detects and handles conflicts based on vector clock comparison.
    /// </summary>
    /// <param name="key">Data key to write.</param>
    /// <param name="data">Data payload.</param>
    /// <param name="options">Write options.</param>
    /// <param name="clock">Vector clock for this write.</param>
    /// <returns>ReplicationEvent representing the write.</returns>
    protected override async Task<ReplicationEvent> PerformWriteAsync(string key, byte[] data, WriteOptions options, VectorClock clock)
    {
        await _replicationLock.WaitAsync();
        try
        {
            var eventId = $"EVT-{Guid.NewGuid():N}";
            var timestamp = DateTimeOffset.UtcNow;

            // Check for conflict
            if (_store.TryGetValue(key, out var existing))
            {
                var existingClock = existing.Clock;

                // Concurrent writes detected (neither happens-before the other)
                if (!clock.HappensBefore(existingClock) && !existingClock.HappensBefore(clock))
                {
                    var conflict = new ReplicationConflict(
                        key,
                        new ReplicationEvent(
                            $"EVT-existing", key, existing.Data, existingClock,
                            LocalRegion, existing.Timestamp),
                        new ReplicationEvent(eventId, key, data, clock, LocalRegion, timestamp),
                        DefaultConflictResolution,
                        DateTimeOffset.UtcNow
                    );

                    // Resolve based on strategy
                    var resolution = await ResolveConflictInternalAsync(conflict);

                    if (resolution.RequiresHumanReview)
                    {
                        _pendingConflicts[key] = conflict;
                        throw new InvalidOperationException($"Conflict on key {key} requires manual resolution");
                    }

                    data = resolution.ResolvedData ?? data;
                    clock = resolution.ResolvedClock;
                }
            }

            // Store the write
            _store[key] = (data, clock, timestamp);

            var evt = new ReplicationEvent(eventId, key, data, clock, LocalRegion, timestamp);
            _eventLog.Enqueue(evt);

            return evt;
        }
        finally { _replicationLock.Release(); }
    }

    /// <summary>
    /// Performs the actual read operation from storage.
    /// Checks for staleness based on consistency requirements.
    /// </summary>
    /// <param name="key">Data key to read.</param>
    /// <param name="options">Read options.</param>
    /// <returns>ReadResult with data and metadata.</returns>
    protected override Task<ReadResult> PerformReadAsync(string key, ReadOptions options)
    {
        var consistency = options.Consistency ?? DefaultConsistency;

        if (_store.TryGetValue(key, out var item))
        {
            var isStale = false;

            if (options.MaxStaleness.HasValue)
            {
                var age = DateTimeOffset.UtcNow - item.Timestamp;
                isStale = age > options.MaxStaleness.Value;
            }

            return Task.FromResult(new ReadResult(
                key,
                item.Data,
                item.Clock,
                LocalRegion,
                isStale
            ));
        }

        return Task.FromResult(new ReadResult(key, null, new VectorClock(new Dictionary<string, long>()), LocalRegion, false));
    }

    /// <summary>
    /// Replicates a write event to specified target regions.
    /// In production, this would send the event to remote regions via gRPC, HTTP, or message queue.
    /// </summary>
    /// <param name="evt">Replication event to propagate.</param>
    /// <param name="regions">Target regions to replicate to.</param>
    protected override async Task ReplicateToRegionsAsync(ReplicationEvent evt, string[] regions)
    {
        // In production, this would send the event to remote regions
        // For now, we just log the intent
        foreach (var region in regions)
        {
            if (region != LocalRegion)
            {
                // Simulate replication by just logging
                await Task.Yield();
            }
        }
    }

    /// <summary>
    /// Fetches all pending conflicts from storage.
    /// </summary>
    /// <returns>List of unresolved conflicts.</returns>
    protected override Task<IReadOnlyList<ReplicationConflict>> FetchPendingConflictsAsync()
        => Task.FromResult<IReadOnlyList<ReplicationConflict>>(_pendingConflicts.Values.ToList());

    /// <summary>
    /// Stores the resolved conflict data to all regions.
    /// Removes the conflict from the pending queue.
    /// </summary>
    /// <param name="key">Key to store resolution for.</param>
    /// <param name="data">Resolved data.</param>
    /// <param name="clock">Merged vector clock.</param>
    protected override Task StoreResolutionAsync(string key, byte[] data, VectorClock clock)
    {
        _store[key] = (data, clock, DateTimeOffset.UtcNow);
        _pendingConflicts.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Measures current replication lag to a specific region.
    /// In production, this would measure actual lag via heartbeats or version comparison.
    /// </summary>
    /// <param name="region">Target region to measure lag for.</param>
    /// <returns>Estimated replication lag.</returns>
    protected override Task<TimeSpan> MeasureReplicationLagAsync(string region)
    {
        // In production, this would measure actual lag to the region
        return Task.FromResult(TimeSpan.FromMilliseconds(50)); // Simulated
    }

    /// <summary>
    /// Gets the event stream for replication events.
    /// Returns an async stream of events matching the optional key pattern.
    /// </summary>
    /// <param name="pattern">Optional key pattern filter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async enumerable of replication events.</returns>
    protected override async IAsyncEnumerable<ReplicationEvent> GetEventStreamAsync(
        string? pattern,
        [EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_eventLog.TryDequeue(out var evt))
            {
                if (pattern == null || evt.Key.Contains(pattern))
                    yield return evt;
            }
            else
            {
                await Task.Delay(100, ct);
            }
        }
    }

    /// <summary>
    /// Starts the plugin.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public override Task StartAsync(CancellationToken ct)
    {
        // No background tasks to start for this simple implementation
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the plugin.
    /// </summary>
    public override Task StopAsync()
    {
        // Clean up resources
        _replicationLock.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets plugin metadata including replication configuration.
    /// </summary>
    /// <returns>Metadata dictionary.</returns>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "MultiMasterReplication";
        metadata["SemanticDescription"] = "Global multi-master replication with vector clocks and conflict resolution";
        metadata["SemanticTags"] = new[] { "replication", "multi-master", "crdt", "vector-clock", "distributed" };
        return metadata;
    }
}
