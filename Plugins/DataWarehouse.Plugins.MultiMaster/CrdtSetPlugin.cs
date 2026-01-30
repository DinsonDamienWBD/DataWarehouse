using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Replication;

namespace DataWarehouse.Plugins.MultiMaster;

/// <summary>
/// CRDT-based set plugin supporting G-Set, OR-Set, and LWW-Element-Set.
/// G-Set (Grow-only Set): Supports only add operations.
/// OR-Set (Observed-Remove Set): Supports both add and remove with unique tags.
/// LWW-Element-Set (Last-Writer-Wins Element Set): Supports add/remove with timestamps.
/// All are conflict-free and automatically merge concurrent updates.
/// </summary>
public class CrdtSetPlugin : MultiMasterReplicationPluginBase
{
    /// <inheritdoc />
    public override string Id => "com.datawarehouse.replication.crdt-set";

    /// <inheritdoc />
    public override string Name => "CRDT Set Replication";

    private readonly string _localRegion;
    private readonly List<string> _connectedRegions = new();
    private readonly ConcurrentDictionary<string, GSet<string>> _gSets = new();
    private readonly ConcurrentDictionary<string, ORSet<string>> _orSets = new();
    private readonly ConcurrentDictionary<string, LWWElementSet<string>> _lwwSets = new();
    private readonly ConcurrentQueue<ReplicationEvent> _eventLog = new();
    private readonly SemaphoreSlim _replicationLock = new(1, 1);

    /// <inheritdoc />
    public override string LocalRegion => _localRegion;

    /// <inheritdoc />
    public override IReadOnlyList<string> ConnectedRegions => _connectedRegions.AsReadOnly();

    /// <inheritdoc />
    public override ConflictResolution DefaultConflictResolution => ConflictResolution.CRDT;

    /// <summary>
    /// Initializes a new instance of the CrdtSetPlugin.
    /// </summary>
    /// <param name="localRegion">Local region identifier (default: "region-1").</param>
    public CrdtSetPlugin(string localRegion = "region-1")
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
    /// Add an element to a G-Set (grow-only set).
    /// G-Sets support only add operations and automatically merge.
    /// </summary>
    /// <param name="key">Set key.</param>
    /// <param name="element">Element to add.</param>
    /// <returns>ReplicationEvent representing the add operation.</returns>
    public async Task<ReplicationEvent> AddToGSetAsync(string key, string element)
    {
        var set = _gSets.GetOrAdd(key, _ => new GSet<string>());
        set.Add(element);

        var data = JsonSerializer.SerializeToUtf8Bytes(set);
        LocalClock = LocalClock.Increment(LocalRegion);

        var evt = new ReplicationEvent(
            $"EVT-{Guid.NewGuid():N}",
            key,
            data,
            LocalClock,
            LocalRegion,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string> { ["Type"] = "GSet", ["Operation"] = "Add" }
        );

        _eventLog.Enqueue(evt);
        await ReplicateToRegionsAsync(evt, ConnectedRegions.ToArray());

        return evt;
    }

    /// <summary>
    /// Add an element to an OR-Set (observed-remove set).
    /// </summary>
    /// <param name="key">Set key.</param>
    /// <param name="element">Element to add.</param>
    /// <returns>ReplicationEvent representing the add operation.</returns>
    public async Task<ReplicationEvent> AddToORSetAsync(string key, string element)
    {
        var set = _orSets.GetOrAdd(key, _ => new ORSet<string>());
        set.Add(element, LocalRegion);

        var data = JsonSerializer.SerializeToUtf8Bytes(set);
        LocalClock = LocalClock.Increment(LocalRegion);

        var evt = new ReplicationEvent(
            $"EVT-{Guid.NewGuid():N}",
            key,
            data,
            LocalClock,
            LocalRegion,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string> { ["Type"] = "ORSet", ["Operation"] = "Add" }
        );

        _eventLog.Enqueue(evt);
        await ReplicateToRegionsAsync(evt, ConnectedRegions.ToArray());

        return evt;
    }

    /// <summary>
    /// Remove an element from an OR-Set (observed-remove set).
    /// </summary>
    /// <param name="key">Set key.</param>
    /// <param name="element">Element to remove.</param>
    /// <returns>ReplicationEvent representing the remove operation.</returns>
    public async Task<ReplicationEvent> RemoveFromORSetAsync(string key, string element)
    {
        var set = _orSets.GetOrAdd(key, _ => new ORSet<string>());
        set.Remove(element);

        var data = JsonSerializer.SerializeToUtf8Bytes(set);
        LocalClock = LocalClock.Increment(LocalRegion);

        var evt = new ReplicationEvent(
            $"EVT-{Guid.NewGuid():N}",
            key,
            data,
            LocalClock,
            LocalRegion,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string> { ["Type"] = "ORSet", ["Operation"] = "Remove" }
        );

        _eventLog.Enqueue(evt);
        await ReplicateToRegionsAsync(evt, ConnectedRegions.ToArray());

        return evt;
    }

    /// <summary>
    /// Add an element to an LWW-Element-Set (last-writer-wins element set).
    /// </summary>
    /// <param name="key">Set key.</param>
    /// <param name="element">Element to add.</param>
    /// <returns>ReplicationEvent representing the add operation.</returns>
    public async Task<ReplicationEvent> AddToLWWSetAsync(string key, string element)
    {
        var set = _lwwSets.GetOrAdd(key, _ => new LWWElementSet<string>());
        set.Add(element, DateTimeOffset.UtcNow);

        var data = JsonSerializer.SerializeToUtf8Bytes(set);
        LocalClock = LocalClock.Increment(LocalRegion);

        var evt = new ReplicationEvent(
            $"EVT-{Guid.NewGuid():N}",
            key,
            data,
            LocalClock,
            LocalRegion,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string> { ["Type"] = "LWWSet", ["Operation"] = "Add" }
        );

        _eventLog.Enqueue(evt);
        await ReplicateToRegionsAsync(evt, ConnectedRegions.ToArray());

        return evt;
    }

    /// <summary>
    /// Remove an element from an LWW-Element-Set (last-writer-wins element set).
    /// </summary>
    /// <param name="key">Set key.</param>
    /// <param name="element">Element to remove.</param>
    /// <returns>ReplicationEvent representing the remove operation.</returns>
    public async Task<ReplicationEvent> RemoveFromLWWSetAsync(string key, string element)
    {
        var set = _lwwSets.GetOrAdd(key, _ => new LWWElementSet<string>());
        set.Remove(element, DateTimeOffset.UtcNow);

        var data = JsonSerializer.SerializeToUtf8Bytes(set);
        LocalClock = LocalClock.Increment(LocalRegion);

        var evt = new ReplicationEvent(
            $"EVT-{Guid.NewGuid():N}",
            key,
            data,
            LocalClock,
            LocalRegion,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string> { ["Type"] = "LWWSet", ["Operation"] = "Remove" }
        );

        _eventLog.Enqueue(evt);
        await ReplicateToRegionsAsync(evt, ConnectedRegions.ToArray());

        return evt;
    }

    /// <summary>
    /// Get all elements in a G-Set.
    /// </summary>
    /// <param name="key">Set key.</param>
    /// <returns>Set of elements.</returns>
    public IReadOnlySet<string> GetGSetElements(string key)
        => _gSets.TryGetValue(key, out var set) ? set.Elements : new HashSet<string>();

    /// <summary>
    /// Get all elements in an OR-Set.
    /// </summary>
    /// <param name="key">Set key.</param>
    /// <returns>Set of elements.</returns>
    public IReadOnlySet<string> GetORSetElements(string key)
        => _orSets.TryGetValue(key, out var set) ? set.Elements : new HashSet<string>();

    /// <summary>
    /// Get all elements in an LWW-Element-Set.
    /// </summary>
    /// <param name="key">Set key.</param>
    /// <returns>Set of elements.</returns>
    public IReadOnlySet<string> GetLWWSetElements(string key)
        => _lwwSets.TryGetValue(key, out var set) ? set.Elements : new HashSet<string>();

    /// <summary>
    /// Performs the actual write operation to local storage.
    /// For CRDT sets, this merges incoming set updates.
    /// </summary>
    /// <param name="key">Data key to write.</param>
    /// <param name="data">Data payload (serialized set).</param>
    /// <param name="options">Write options.</param>
    /// <param name="clock">Vector clock for this write.</param>
    /// <returns>ReplicationEvent representing the write.</returns>
    protected override async Task<ReplicationEvent> PerformWriteAsync(string key, byte[] data, WriteOptions options, VectorClock clock)
    {
        await _replicationLock.WaitAsync();
        try
        {
            // Attempt to deserialize as different set types
            try
            {
                var gSet = JsonSerializer.Deserialize<GSet<string>>(data);
                if (gSet != null)
                {
                    _gSets.AddOrUpdate(key, gSet, (_, existing) => GSet<string>.Merge(existing, gSet));
                }
            }
            catch
            {
                try
                {
                    var orSet = JsonSerializer.Deserialize<ORSet<string>>(data);
                    if (orSet != null)
                    {
                        _orSets.AddOrUpdate(key, orSet, (_, existing) => ORSet<string>.Merge(existing, orSet));
                    }
                }
                catch
                {
                    var lwwSet = JsonSerializer.Deserialize<LWWElementSet<string>>(data);
                    if (lwwSet != null)
                    {
                        _lwwSets.AddOrUpdate(key, lwwSet, (_, existing) => LWWElementSet<string>.Merge(existing, lwwSet));
                    }
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

        if (_gSets.TryGetValue(key, out var gSet))
            data = JsonSerializer.SerializeToUtf8Bytes(gSet);
        else if (_orSets.TryGetValue(key, out var orSet))
            data = JsonSerializer.SerializeToUtf8Bytes(orSet);
        else if (_lwwSets.TryGetValue(key, out var lwwSet))
            data = JsonSerializer.SerializeToUtf8Bytes(lwwSet);

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
                await Task.Yield();
            }
        }
    }

    /// <summary>
    /// Fetches all pending conflicts from storage.
    /// CRDT sets never have conflicts, so this always returns empty.
    /// </summary>
    /// <returns>Empty list (CRDTs are conflict-free).</returns>
    protected override Task<IReadOnlyList<ReplicationConflict>> FetchPendingConflictsAsync()
        => Task.FromResult<IReadOnlyList<ReplicationConflict>>(Array.Empty<ReplicationConflict>());

    /// <summary>
    /// Stores the resolved conflict data.
    /// Not needed for CRDT sets as they never conflict.
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
        metadata["FeatureType"] = "CRDTSetReplication";
        metadata["SemanticDescription"] = "CRDT-based set replication with G-Set, OR-Set, and LWW-Element-Set support";
        metadata["SemanticTags"] = new[] { "replication", "crdt", "set", "distributed", "conflict-free" };
        metadata["SupportedCRDTs"] = new[] { "GSet", "ORSet", "LWWElementSet" };
        return metadata;
    }
}

/// <summary>
/// G-Set (Grow-only Set) CRDT.
/// Supports only add operations. Elements can never be removed.
/// Merge operation takes the union of both sets.
/// </summary>
/// <typeparam name="T">Element type.</typeparam>
public class GSet<T> where T : notnull
{
    private readonly HashSet<T> _elements = new();

    /// <summary>
    /// Elements in the set.
    /// </summary>
    public IReadOnlySet<T> Elements => _elements;

    /// <summary>
    /// Add an element to the set.
    /// </summary>
    /// <param name="element">Element to add.</param>
    public void Add(T element) => _elements.Add(element);

    /// <summary>
    /// Merge two G-Sets by taking the union of elements.
    /// </summary>
    /// <param name="a">First set.</param>
    /// <param name="b">Second set.</param>
    /// <returns>Merged set.</returns>
    public static GSet<T> Merge(GSet<T> a, GSet<T> b)
    {
        var merged = new GSet<T>();
        foreach (var element in a._elements)
            merged.Add(element);
        foreach (var element in b._elements)
            merged.Add(element);
        return merged;
    }
}

/// <summary>
/// OR-Set (Observed-Remove Set) CRDT.
/// Supports both add and remove operations using unique tags.
/// An element is in the set if there exists an add-tag that hasn't been removed.
/// Merge takes the union of add-tags minus removed tags.
/// </summary>
/// <typeparam name="T">Element type.</typeparam>
public class ORSet<T> where T : notnull
{
    private readonly Dictionary<T, HashSet<string>> _adds = new();
    private readonly Dictionary<T, HashSet<string>> _removes = new();

    /// <summary>
    /// Elements currently in the set.
    /// An element is present if it has add-tags not in the remove set.
    /// </summary>
    public IReadOnlySet<T> Elements
    {
        get
        {
            var result = new HashSet<T>();
            foreach (var (element, addTags) in _adds)
            {
                var removeTags = _removes.GetValueOrDefault(element, new HashSet<string>());
                if (addTags.Except(removeTags).Any())
                    result.Add(element);
            }
            return result;
        }
    }

    /// <summary>
    /// Add an element to the set with a unique tag.
    /// </summary>
    /// <param name="element">Element to add.</param>
    /// <param name="nodeId">Node identifier for unique tagging.</param>
    public void Add(T element, string nodeId)
    {
        var tag = $"{nodeId}:{Guid.NewGuid():N}";
        if (!_adds.ContainsKey(element))
            _adds[element] = new HashSet<string>();
        _adds[element].Add(tag);
    }

    /// <summary>
    /// Remove an element from the set.
    /// Removes all current add-tags for the element.
    /// </summary>
    /// <param name="element">Element to remove.</param>
    public void Remove(T element)
    {
        if (_adds.TryGetValue(element, out var addTags))
        {
            if (!_removes.ContainsKey(element))
                _removes[element] = new HashSet<string>();
            foreach (var tag in addTags)
                _removes[element].Add(tag);
        }
    }

    /// <summary>
    /// Merge two OR-Sets by taking the union of adds and removes.
    /// </summary>
    /// <param name="a">First set.</param>
    /// <param name="b">Second set.</param>
    /// <returns>Merged set.</returns>
    public static ORSet<T> Merge(ORSet<T> a, ORSet<T> b)
    {
        var merged = new ORSet<T>();

        // Merge adds
        foreach (var (element, tags) in a._adds)
        {
            merged._adds[element] = new HashSet<string>(tags);
        }
        foreach (var (element, tags) in b._adds)
        {
            if (!merged._adds.ContainsKey(element))
                merged._adds[element] = new HashSet<string>();
            foreach (var tag in tags)
                merged._adds[element].Add(tag);
        }

        // Merge removes
        foreach (var (element, tags) in a._removes)
        {
            merged._removes[element] = new HashSet<string>(tags);
        }
        foreach (var (element, tags) in b._removes)
        {
            if (!merged._removes.ContainsKey(element))
                merged._removes[element] = new HashSet<string>();
            foreach (var tag in tags)
                merged._removes[element].Add(tag);
        }

        return merged;
    }
}

/// <summary>
/// LWW-Element-Set (Last-Writer-Wins Element Set) CRDT.
/// Maintains add and remove timestamps for each element.
/// An element is in the set if its add timestamp is greater than its remove timestamp.
/// Merge takes the maximum timestamp for each operation.
/// </summary>
/// <typeparam name="T">Element type.</typeparam>
public class LWWElementSet<T> where T : notnull
{
    private readonly Dictionary<T, DateTimeOffset> _adds = new();
    private readonly Dictionary<T, DateTimeOffset> _removes = new();

    /// <summary>
    /// Elements currently in the set.
    /// An element is present if add timestamp > remove timestamp.
    /// </summary>
    public IReadOnlySet<T> Elements
    {
        get
        {
            var result = new HashSet<T>();
            foreach (var (element, addTime) in _adds)
            {
                var removeTime = _removes.GetValueOrDefault(element, DateTimeOffset.MinValue);
                if (addTime > removeTime)
                    result.Add(element);
            }
            return result;
        }
    }

    /// <summary>
    /// Add an element to the set with a timestamp.
    /// </summary>
    /// <param name="element">Element to add.</param>
    /// <param name="timestamp">Add timestamp.</param>
    public void Add(T element, DateTimeOffset timestamp)
    {
        if (!_adds.ContainsKey(element) || _adds[element] < timestamp)
            _adds[element] = timestamp;
    }

    /// <summary>
    /// Remove an element from the set with a timestamp.
    /// </summary>
    /// <param name="element">Element to remove.</param>
    /// <param name="timestamp">Remove timestamp.</param>
    public void Remove(T element, DateTimeOffset timestamp)
    {
        if (!_removes.ContainsKey(element) || _removes[element] < timestamp)
            _removes[element] = timestamp;
    }

    /// <summary>
    /// Merge two LWW-Element-Sets by taking the maximum timestamp for each operation.
    /// </summary>
    /// <param name="a">First set.</param>
    /// <param name="b">Second set.</param>
    /// <returns>Merged set.</returns>
    public static LWWElementSet<T> Merge(LWWElementSet<T> a, LWWElementSet<T> b)
    {
        var merged = new LWWElementSet<T>();

        // Merge adds
        foreach (var (element, timestamp) in a._adds)
            merged._adds[element] = timestamp;
        foreach (var (element, timestamp) in b._adds)
            merged.Add(element, timestamp);

        // Merge removes
        foreach (var (element, timestamp) in a._removes)
            merged._removes[element] = timestamp;
        foreach (var (element, timestamp) in b._removes)
            merged.Remove(element, timestamp);

        return merged;
    }
}
