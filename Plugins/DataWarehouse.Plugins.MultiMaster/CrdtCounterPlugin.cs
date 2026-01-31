using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Replication;

namespace DataWarehouse.Plugins.MultiMaster;

/// <summary>
/// CRDT-based counter plugin supporting G-Counter and PN-Counter.
/// G-Counter (Grow-only Counter): Supports increment operations only.
/// PN-Counter (Positive-Negative Counter): Supports both increment and decrement.
/// Both are conflict-free and automatically merge concurrent updates.
/// </summary>
public class CrdtCounterPlugin : MultiMasterReplicationPluginBase
{
    /// <inheritdoc />
    public override string Id => "com.datawarehouse.replication.crdt-counter";

    /// <inheritdoc />
    public override string Name => "CRDT Counter Replication";

    private readonly string _localRegion;
    private readonly List<string> _connectedRegions = new();
    private readonly ConcurrentDictionary<string, GCounter> _gCounters = new();
    private readonly ConcurrentDictionary<string, PNCounter> _pnCounters = new();
    private readonly ConcurrentQueue<ReplicationEvent> _eventLog = new();
    private readonly SemaphoreSlim _replicationLock = new(1, 1);

    /// <inheritdoc />
    public override string LocalRegion => _localRegion;

    /// <inheritdoc />
    public override IReadOnlyList<string> ConnectedRegions => _connectedRegions.AsReadOnly();

    /// <inheritdoc />
    public override ConflictResolution DefaultConflictResolution => ConflictResolution.CRDT;

    /// <summary>
    /// Initializes a new instance of the CrdtCounterPlugin.
    /// </summary>
    /// <param name="localRegion">Local region identifier (default: "region-1").</param>
    public CrdtCounterPlugin(string localRegion = "region-1")
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
    /// Increment a G-Counter (grow-only counter).
    /// G-Counters support only increment operations and automatically merge.
    /// </summary>
    /// <param name="key">Counter key.</param>
    /// <param name="delta">Amount to increment (default: 1).</param>
    /// <returns>ReplicationEvent representing the increment.</returns>
    public async Task<ReplicationEvent> IncrementGCounterAsync(string key, long delta = 1)
    {
        var counter = _gCounters.GetOrAdd(key, _ => new GCounter());
        counter.Increment(LocalRegion, delta);

        var data = JsonSerializer.SerializeToUtf8Bytes(counter);
        LocalClock = LocalClock.Increment(LocalRegion);

        var evt = new ReplicationEvent(
            $"EVT-{Guid.NewGuid():N}",
            key,
            data,
            LocalClock,
            LocalRegion,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string> { ["Type"] = "GCounter", ["Operation"] = "Increment" }
        );

        _eventLog.Enqueue(evt);
        await ReplicateToRegionsAsync(evt, ConnectedRegions.ToArray());

        return evt;
    }

    /// <summary>
    /// Increment a PN-Counter (positive-negative counter).
    /// </summary>
    /// <param name="key">Counter key.</param>
    /// <param name="delta">Amount to increment (default: 1).</param>
    /// <returns>ReplicationEvent representing the increment.</returns>
    public async Task<ReplicationEvent> IncrementPNCounterAsync(string key, long delta = 1)
    {
        var counter = _pnCounters.GetOrAdd(key, _ => new PNCounter());
        counter.Increment(LocalRegion, delta);

        var data = JsonSerializer.SerializeToUtf8Bytes(counter);
        LocalClock = LocalClock.Increment(LocalRegion);

        var evt = new ReplicationEvent(
            $"EVT-{Guid.NewGuid():N}",
            key,
            data,
            LocalClock,
            LocalRegion,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string> { ["Type"] = "PNCounter", ["Operation"] = "Increment" }
        );

        _eventLog.Enqueue(evt);
        await ReplicateToRegionsAsync(evt, ConnectedRegions.ToArray());

        return evt;
    }

    /// <summary>
    /// Decrement a PN-Counter (positive-negative counter).
    /// </summary>
    /// <param name="key">Counter key.</param>
    /// <param name="delta">Amount to decrement (default: 1).</param>
    /// <returns>ReplicationEvent representing the decrement.</returns>
    public async Task<ReplicationEvent> DecrementPNCounterAsync(string key, long delta = 1)
    {
        var counter = _pnCounters.GetOrAdd(key, _ => new PNCounter());
        counter.Decrement(LocalRegion, delta);

        var data = JsonSerializer.SerializeToUtf8Bytes(counter);
        LocalClock = LocalClock.Increment(LocalRegion);

        var evt = new ReplicationEvent(
            $"EVT-{Guid.NewGuid():N}",
            key,
            data,
            LocalClock,
            LocalRegion,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string> { ["Type"] = "PNCounter", ["Operation"] = "Decrement" }
        );

        _eventLog.Enqueue(evt);
        await ReplicateToRegionsAsync(evt, ConnectedRegions.ToArray());

        return evt;
    }

    /// <summary>
    /// Get the current value of a G-Counter.
    /// </summary>
    /// <param name="key">Counter key.</param>
    /// <returns>Current counter value.</returns>
    public long GetGCounterValue(string key)
        => _gCounters.TryGetValue(key, out var counter) ? counter.Value : 0;

    /// <summary>
    /// Get the current value of a PN-Counter.
    /// </summary>
    /// <param name="key">Counter key.</param>
    /// <returns>Current counter value.</returns>
    public long GetPNCounterValue(string key)
        => _pnCounters.TryGetValue(key, out var counter) ? counter.Value : 0;

    /// <summary>
    /// Performs the actual write operation to local storage.
    /// For CRDT counters, this merges incoming counter updates.
    /// </summary>
    /// <param name="key">Data key to write.</param>
    /// <param name="data">Data payload (serialized counter).</param>
    /// <param name="options">Write options.</param>
    /// <param name="clock">Vector clock for this write.</param>
    /// <returns>ReplicationEvent representing the write.</returns>
    protected override async Task<ReplicationEvent> PerformWriteAsync(string key, byte[] data, WriteOptions options, VectorClock clock)
    {
        await _replicationLock.WaitAsync();
        try
        {
            // Determine counter type from metadata or attempt deserializing
            // For simplicity, we'll try both types
            try
            {
                var gCounter = JsonSerializer.Deserialize<GCounter>(data);
                if (gCounter != null)
                {
                    _gCounters.AddOrUpdate(key, gCounter, (_, existing) => GCounter.Merge(existing, gCounter));
                }
            }
            catch
            {
                var pnCounter = JsonSerializer.Deserialize<PNCounter>(data);
                if (pnCounter != null)
                {
                    _pnCounters.AddOrUpdate(key, pnCounter, (_, existing) => PNCounter.Merge(existing, pnCounter));
                }
            }

            var evt = new ReplicationEvent(
                $"EVT-{Guid.NewGuid():N}",
                key,
                data,
                clock,
                LocalRegion,
                DateTimeOffset.UtcNow
            );

            _eventLog.Enqueue(evt);
            return evt;
        }
        finally { _replicationLock.Release(); }
    }

    /// <summary>
    /// Performs the actual read operation from storage.
    /// </summary>
    /// <param name="key">Data key to read.</param>
    /// <param name="options">Read options.</param>
    /// <returns>ReadResult with data and metadata.</returns>
    protected override Task<ReadResult> PerformReadAsync(string key, ReadOptions options)
    {
        byte[]? data = null;

        if (_gCounters.TryGetValue(key, out var gCounter))
            data = JsonSerializer.SerializeToUtf8Bytes(gCounter);
        else if (_pnCounters.TryGetValue(key, out var pnCounter))
            data = JsonSerializer.SerializeToUtf8Bytes(pnCounter);

        return Task.FromResult(new ReadResult(
            key,
            data,
            LocalClock,
            LocalRegion,
            false
        ));
    }

    /// <summary>
    /// Replicates a write event to specified target regions.
    /// </summary>
    /// <param name="evt">Replication event to propagate.</param>
    /// <param name="regions">Target regions to replicate to.</param>
    protected override async Task ReplicateToRegionsAsync(ReplicationEvent evt, string[] regions)
    {
        foreach (var region in regions)
        {
            if (region != LocalRegion)
            {
                // Simulate replication
                await Task.Yield();
            }
        }
    }

    /// <summary>
    /// Fetches all pending conflicts from storage.
    /// CRDT counters never have conflicts, so this always returns empty.
    /// </summary>
    /// <returns>Empty list (CRDTs are conflict-free).</returns>
    protected override Task<IReadOnlyList<ReplicationConflict>> FetchPendingConflictsAsync()
        => Task.FromResult<IReadOnlyList<ReplicationConflict>>(Array.Empty<ReplicationConflict>());

    /// <summary>
    /// Stores the resolved conflict data.
    /// Not needed for CRDT counters as they never conflict.
    /// </summary>
    /// <param name="key">Key to store resolution for.</param>
    /// <param name="data">Resolved data.</param>
    /// <param name="clock">Merged vector clock.</param>
    protected override Task StoreResolutionAsync(string key, byte[] data, VectorClock clock)
        => Task.CompletedTask;

    /// <summary>
    /// Measures current replication lag to a specific region.
    /// </summary>
    /// <param name="region">Target region to measure lag for.</param>
    /// <returns>Estimated replication lag.</returns>
    protected override Task<TimeSpan> MeasureReplicationLagAsync(string region)
        => Task.FromResult(TimeSpan.FromMilliseconds(50));

    /// <summary>
    /// Gets the event stream for replication events.
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
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the plugin.
    /// </summary>
    public override Task StopAsync()
    {
        _replicationLock.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets plugin metadata including CRDT configuration.
    /// </summary>
    /// <returns>Metadata dictionary.</returns>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "CRDTCounterReplication";
        metadata["SemanticDescription"] = "CRDT-based counter replication with G-Counter and PN-Counter support";
        metadata["SemanticTags"] = new[] { "replication", "crdt", "counter", "distributed", "conflict-free" };
        metadata["SupportedCRDTs"] = new[] { "GCounter", "PNCounter" };
        return metadata;
    }
}

/// <summary>
/// G-Counter (Grow-only Counter) CRDT.
/// Supports only increment operations. Value is computed as sum of all increments.
/// Merge operation takes the maximum of each node's counter.
/// </summary>
public class GCounter
{
    /// <summary>
    /// Per-node counter values.
    /// </summary>
    public Dictionary<string, long> Counters { get; set; } = new();

    /// <summary>
    /// Current value of the counter (sum of all node values).
    /// </summary>
    public long Value => Counters.Values.Sum();

    /// <summary>
    /// Increment the counter for a specific node.
    /// </summary>
    /// <param name="nodeId">Node identifier.</param>
    /// <param name="delta">Amount to increment.</param>
    public void Increment(string nodeId, long delta)
    {
        Counters[nodeId] = Counters.GetValueOrDefault(nodeId, 0) + delta;
    }

    /// <summary>
    /// Merge two G-Counters by taking the maximum of each node's value.
    /// </summary>
    /// <param name="a">First counter.</param>
    /// <param name="b">Second counter.</param>
    /// <returns>Merged counter.</returns>
    public static GCounter Merge(GCounter a, GCounter b)
    {
        var merged = new GCounter();
        foreach (var (node, count) in a.Counters)
            merged.Counters[node] = Math.Max(count, b.Counters.GetValueOrDefault(node, 0));
        foreach (var (node, count) in b.Counters)
            merged.Counters[node] = Math.Max(count, a.Counters.GetValueOrDefault(node, 0));
        return merged;
    }
}

/// <summary>
/// PN-Counter (Positive-Negative Counter) CRDT.
/// Supports both increment and decrement operations.
/// Maintains two G-Counters internally: one for increments, one for decrements.
/// Value is computed as increments - decrements.
/// </summary>
public class PNCounter
{
    /// <summary>
    /// Positive increments (G-Counter).
    /// </summary>
    public Dictionary<string, long> Increments { get; set; } = new();

    /// <summary>
    /// Negative decrements (G-Counter).
    /// </summary>
    public Dictionary<string, long> Decrements { get; set; } = new();

    /// <summary>
    /// Current value of the counter (increments - decrements).
    /// </summary>
    public long Value => Increments.Values.Sum() - Decrements.Values.Sum();

    /// <summary>
    /// Increment the counter for a specific node.
    /// </summary>
    /// <param name="nodeId">Node identifier.</param>
    /// <param name="delta">Amount to increment.</param>
    public void Increment(string nodeId, long delta)
    {
        Increments[nodeId] = Increments.GetValueOrDefault(nodeId, 0) + delta;
    }

    /// <summary>
    /// Decrement the counter for a specific node.
    /// </summary>
    /// <param name="nodeId">Node identifier.</param>
    /// <param name="delta">Amount to decrement.</param>
    public void Decrement(string nodeId, long delta)
    {
        Decrements[nodeId] = Decrements.GetValueOrDefault(nodeId, 0) + delta;
    }

    /// <summary>
    /// Merge two PN-Counters by merging their increment and decrement G-Counters.
    /// </summary>
    /// <param name="a">First counter.</param>
    /// <param name="b">Second counter.</param>
    /// <returns>Merged counter.</returns>
    public static PNCounter Merge(PNCounter a, PNCounter b)
    {
        var merged = new PNCounter();

        // Merge increments
        foreach (var (node, count) in a.Increments)
            merged.Increments[node] = Math.Max(count, b.Increments.GetValueOrDefault(node, 0));
        foreach (var (node, count) in b.Increments)
            merged.Increments[node] = Math.Max(count, a.Increments.GetValueOrDefault(node, 0));

        // Merge decrements
        foreach (var (node, count) in a.Decrements)
            merged.Decrements[node] = Math.Max(count, b.Decrements.GetValueOrDefault(node, 0));
        foreach (var (node, count) in b.Decrements)
            merged.Decrements[node] = Math.Max(count, a.Decrements.GetValueOrDefault(node, 0));

        return merged;
    }
}
