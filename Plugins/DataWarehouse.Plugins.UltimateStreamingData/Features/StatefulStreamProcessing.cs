using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateStreamingData.Features;

#region Stateful Stream Types

/// <summary>
/// Backend type for stream state storage.
/// </summary>
public enum StreamStateBackend
{
    /// <summary>In-memory state with ConcurrentDictionary. Fast but volatile.</summary>
    InMemory,

    /// <summary>File-based state persistence for recovery across restarts.</summary>
    FileSystem,

    /// <summary>Distributed state via message bus for multi-node scenarios.</summary>
    Distributed
}

/// <summary>
/// Configuration for stateful stream processing.
/// </summary>
public sealed record StatefulProcessingConfig
{
    /// <summary>Gets the state backend type.</summary>
    public StreamStateBackend Backend { get; init; } = StreamStateBackend.InMemory;

    /// <summary>Gets the checkpoint interval for state persistence.</summary>
    public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Gets the state TTL after which stale keys are evicted.</summary>
    public TimeSpan? StateTtl { get; init; }

    /// <summary>Gets the maximum state entries before eviction triggers.</summary>
    public int MaxStateEntries { get; init; } = 1_000_000;

    /// <summary>Gets the file path for file-system backend checkpoints.</summary>
    public string? CheckpointPath { get; init; }

    /// <summary>Gets whether to enable incremental checkpointing (only changed keys).</summary>
    public bool IncrementalCheckpoints { get; init; } = true;
}

/// <summary>
/// Represents a keyed state entry with metadata.
/// </summary>
public sealed record KeyedStateEntry
{
    /// <summary>Gets the state key.</summary>
    public required string Key { get; init; }

    /// <summary>Gets the serialized state value.</summary>
    public required byte[] Value { get; init; }

    /// <summary>Gets the timestamp when this entry was last updated.</summary>
    public DateTimeOffset LastUpdated { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Gets the SHA-256 checksum of the value for integrity verification.</summary>
    public string Checksum { get; init; } = string.Empty;
}

/// <summary>
/// Checkpoint metadata for state recovery.
/// </summary>
public sealed record StateCheckpoint
{
    /// <summary>Gets the unique checkpoint identifier.</summary>
    public required string CheckpointId { get; init; }

    /// <summary>Gets the total number of state entries at checkpoint time.</summary>
    public int EntryCount { get; init; }

    /// <summary>Gets when this checkpoint was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Gets the number of entries changed since the last checkpoint.</summary>
    public int ChangedEntries { get; init; }

    /// <summary>Gets whether this was an incremental checkpoint.</summary>
    public bool IsIncremental { get; init; }
}

/// <summary>
/// Result of a windowed aggregation.
/// </summary>
public sealed record WindowedAggregation<TResult>
{
    /// <summary>Gets the window key.</summary>
    public required string Key { get; init; }

    /// <summary>Gets the window start time.</summary>
    public required DateTimeOffset WindowStart { get; init; }

    /// <summary>Gets the window end time.</summary>
    public required DateTimeOffset WindowEnd { get; init; }

    /// <summary>Gets the aggregated result.</summary>
    public required TResult Result { get; init; }

    /// <summary>Gets the number of events in this window.</summary>
    public int EventCount { get; init; }
}

#endregion

/// <summary>
/// Stateful stream processing engine maintaining keyed state across tumbling and sliding windows.
/// Supports in-memory, file-system, and distributed state backends with periodic checkpointing
/// and incremental state persistence for fault tolerance.
/// </summary>
/// <remarks>
/// <b>MESSAGE BUS:</b> Publishes checkpoints to "streaming.state.checkpoint" topic.
/// <b>STATE ISOLATION:</b> Each processor instance maintains independent state keyed by a namespace.
/// Thread-safe for concurrent state access and modification.
/// </remarks>
internal sealed class StatefulStreamProcessing : IDisposable
{
    private readonly BoundedDictionary<string, StateEntry> _state = new BoundedDictionary<string, StateEntry>(1000);
    private readonly BoundedDictionary<string, bool> _dirtyKeys = new BoundedDictionary<string, bool>(1000);
    private readonly StatefulProcessingConfig _config;
    private readonly IMessageBus? _messageBus;
    private readonly string _namespace;
    private readonly Timer _checkpointTimer;
    private readonly Timer? _ttlTimer;
    private long _totalReads;
    private long _totalWrites;
    private long _checkpointCount;
    private long _evictions;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="StatefulStreamProcessing"/> class.
    /// </summary>
    /// <param name="processorNamespace">Namespace to isolate state for this processor.</param>
    /// <param name="config">State processing configuration.</param>
    /// <param name="messageBus">Optional message bus for checkpoint notifications.</param>
    public StatefulStreamProcessing(
        string processorNamespace,
        StatefulProcessingConfig? config = null,
        IMessageBus? messageBus = null)
    {
        if (string.IsNullOrWhiteSpace(processorNamespace))
            throw new ArgumentException("Processor namespace cannot be empty.", nameof(processorNamespace));

        _namespace = processorNamespace;
        _config = config ?? new StatefulProcessingConfig();
        _messageBus = messageBus;

        _checkpointTimer = new Timer(
            async _ => await TriggerCheckpointAsync(CancellationToken.None),
            null,
            _config.CheckpointInterval,
            _config.CheckpointInterval);

        if (_config.StateTtl.HasValue)
        {
            _ttlTimer = new Timer(
                EvictExpiredState,
                null,
                _config.StateTtl.Value,
                TimeSpan.FromSeconds(Math.Max(30, _config.StateTtl.Value.TotalSeconds / 2)));
        }
    }

    /// <summary>Gets the total number of state reads.</summary>
    public long TotalReads => Interlocked.Read(ref _totalReads);

    /// <summary>Gets the total number of state writes.</summary>
    public long TotalWrites => Interlocked.Read(ref _totalWrites);

    /// <summary>Gets the total number of checkpoints taken.</summary>
    public long CheckpointCount => Interlocked.Read(ref _checkpointCount);

    /// <summary>Gets the current state entry count.</summary>
    public int StateSize => _state.Count;

    /// <summary>
    /// Gets a value from keyed state.
    /// </summary>
    /// <param name="key">The state key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The state value bytes, or null if not found.</returns>
    public Task<byte[]?> GetStateAsync(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        Interlocked.Increment(ref _totalReads);

        var fullKey = $"{_namespace}:{key}";
        if (_state.TryGetValue(fullKey, out var entry) && !entry.IsExpired(_config.StateTtl))
        {
            return Task.FromResult<byte[]?>(entry.Value);
        }
        return Task.FromResult<byte[]?>(null);
    }

    /// <summary>
    /// Sets a value in keyed state.
    /// </summary>
    /// <param name="key">The state key.</param>
    /// <param name="value">The state value bytes.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task PutStateAsync(string key, byte[] value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        Interlocked.Increment(ref _totalWrites);

        var fullKey = $"{_namespace}:{key}";

        // Enforce max state size with LRU-style eviction
        if (_state.Count >= _config.MaxStateEntries && !_state.ContainsKey(fullKey))
        {
            EvictOldestEntry();
        }

        var checksum = ComputeChecksum(value);
        _state[fullKey] = new StateEntry(value, DateTimeOffset.UtcNow, checksum);
        _dirtyKeys[fullKey] = true;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Removes a key from state.
    /// </summary>
    /// <param name="key">The state key to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the key was removed.</returns>
    public Task<bool> RemoveStateAsync(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var fullKey = $"{_namespace}:{key}";
        var removed = _state.TryRemove(fullKey, out _);
        if (removed) _dirtyKeys.TryRemove(fullKey, out _);
        return Task.FromResult(removed);
    }

    /// <summary>
    /// Processes events within a tumbling window, applying an aggregation function.
    /// </summary>
    /// <typeparam name="TResult">The aggregation result type.</typeparam>
    /// <param name="events">The events to process.</param>
    /// <param name="keyExtractor">Extracts the grouping key from each event.</param>
    /// <param name="windowSize">The tumbling window duration.</param>
    /// <param name="aggregator">Aggregation function applied to events within each window/key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Windowed aggregation results.</returns>
    public async Task<IReadOnlyList<WindowedAggregation<TResult>>> ProcessTumblingWindowAsync<TResult>(
        IAsyncEnumerable<StreamEvent> events,
        Func<StreamEvent, string> keyExtractor,
        TimeSpan windowSize,
        Func<IReadOnlyList<StreamEvent>, TResult> aggregator,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keyExtractor);
        ArgumentNullException.ThrowIfNull(aggregator);

        var windows = new Dictionary<(string Key, long WindowId), List<StreamEvent>>();

        await foreach (var evt in events.WithCancellation(ct))
        {
            var key = keyExtractor(evt);
            var windowId = evt.Timestamp.Ticks / windowSize.Ticks;
            var windowKey = (key, windowId);

            if (!windows.TryGetValue(windowKey, out var bucket))
            {
                bucket = new List<StreamEvent>();
                windows[windowKey] = bucket;
            }
            bucket.Add(evt);

            // Persist window state
            var stateKey = $"window:{key}:{windowId}:count";
            var countBytes = BitConverter.GetBytes(bucket.Count);
            await PutStateAsync(stateKey, countBytes, ct);
        }

        var results = new List<WindowedAggregation<TResult>>();
        foreach (var ((key, windowId), evts) in windows)
        {
            var windowStart = new DateTimeOffset(windowId * windowSize.Ticks, TimeSpan.Zero);
            var windowEnd = windowStart + windowSize;

            results.Add(new WindowedAggregation<TResult>
            {
                Key = key,
                WindowStart = windowStart,
                WindowEnd = windowEnd,
                Result = aggregator(evts),
                EventCount = evts.Count
            });
        }

        return results;
    }

    /// <summary>
    /// Processes events within a sliding window, applying an aggregation function.
    /// </summary>
    /// <typeparam name="TResult">The aggregation result type.</typeparam>
    /// <param name="events">The events to process.</param>
    /// <param name="keyExtractor">Extracts the grouping key from each event.</param>
    /// <param name="windowSize">The window duration.</param>
    /// <param name="slideInterval">The slide interval (how often a new window starts).</param>
    /// <param name="aggregator">Aggregation function applied to events within each window/key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Windowed aggregation results.</returns>
    public async Task<IReadOnlyList<WindowedAggregation<TResult>>> ProcessSlidingWindowAsync<TResult>(
        IAsyncEnumerable<StreamEvent> events,
        Func<StreamEvent, string> keyExtractor,
        TimeSpan windowSize,
        TimeSpan slideInterval,
        Func<IReadOnlyList<StreamEvent>, TResult> aggregator,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keyExtractor);
        ArgumentNullException.ThrowIfNull(aggregator);

        var allEvents = new List<(string Key, StreamEvent Event)>();

        await foreach (var evt in events.WithCancellation(ct))
        {
            allEvents.Add((keyExtractor(evt), evt));
        }

        if (allEvents.Count == 0)
            return Array.Empty<WindowedAggregation<TResult>>();

        var minTime = allEvents.Min(e => e.Event.Timestamp);
        var maxTime = allEvents.Max(e => e.Event.Timestamp);
        var keys = allEvents.Select(e => e.Key).Distinct().ToList();

        var results = new List<WindowedAggregation<TResult>>();

        foreach (var key in keys)
        {
            var keyEvents = allEvents.Where(e => e.Key == key).Select(e => e.Event).ToList();

            var windowStart = minTime;
            while (windowStart <= maxTime)
            {
                var windowEnd = windowStart + windowSize;
                var windowEvents = keyEvents
                    .Where(e => e.Timestamp >= windowStart && e.Timestamp < windowEnd)
                    .ToList();

                if (windowEvents.Count > 0)
                {
                    results.Add(new WindowedAggregation<TResult>
                    {
                        Key = key,
                        WindowStart = windowStart,
                        WindowEnd = windowEnd,
                        Result = aggregator(windowEvents),
                        EventCount = windowEvents.Count
                    });
                }

                windowStart += slideInterval;
            }
        }

        return results;
    }

    /// <summary>
    /// Triggers a manual checkpoint of the current state.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Checkpoint metadata.</returns>
    public async Task<StateCheckpoint> TriggerCheckpointAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _checkpointCount);

        var changedKeys = _dirtyKeys.Keys.ToArray();
        _dirtyKeys.Clear();

        var checkpoint = new StateCheckpoint
        {
            CheckpointId = $"ckpt-{_namespace}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            EntryCount = _state.Count,
            ChangedEntries = changedKeys.Length,
            IsIncremental = _config.IncrementalCheckpoints && changedKeys.Length < _state.Count
        };

        // Persist to filesystem if configured
        if (_config.Backend == StreamStateBackend.FileSystem && _config.CheckpointPath != null)
        {
            await PersistCheckpointToFileAsync(checkpoint, changedKeys, ct);
        }

        // Notify via message bus
        if (_messageBus != null)
        {
            await PublishCheckpointNotificationAsync(checkpoint, ct);
        }

        return checkpoint;
    }

    /// <summary>
    /// Restores state from the last filesystem checkpoint.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of entries restored.</returns>
    public async Task<int> RestoreFromCheckpointAsync(CancellationToken ct = default)
    {
        if (_config.Backend != StreamStateBackend.FileSystem || _config.CheckpointPath == null)
            return 0;

        var checkpointFile = Path.Combine(_config.CheckpointPath, $"{_namespace}.state");
        if (!File.Exists(checkpointFile))
            return 0;

        var data = await File.ReadAllBytesAsync(checkpointFile, ct);
        var entries = JsonSerializer.Deserialize<Dictionary<string, CheckpointEntry>>(data);
        if (entries == null) return 0;

        int restored = 0;
        foreach (var (key, entry) in entries)
        {
            var value = Convert.FromBase64String(entry.ValueBase64);
            var checksum = ComputeChecksum(value);

            if (checksum == entry.Checksum)
            {
                _state[key] = new StateEntry(value, entry.Timestamp, checksum);
                restored++;
            }
        }

        return restored;
    }

    private async Task PersistCheckpointToFileAsync(
        StateCheckpoint checkpoint,
        string[] changedKeys,
        CancellationToken ct)
    {
        if (_config.CheckpointPath == null) return;

        Directory.CreateDirectory(_config.CheckpointPath);
        var checkpointFile = Path.Combine(_config.CheckpointPath, $"{_namespace}.state");

        var entriesToSave = checkpoint.IsIncremental
            ? _state.Where(kv => changedKeys.Contains(kv.Key))
            : _state.AsEnumerable();

        // If incremental, merge with existing file
        var existing = new Dictionary<string, CheckpointEntry>();
        if (checkpoint.IsIncremental && File.Exists(checkpointFile))
        {
            var existingData = await File.ReadAllBytesAsync(checkpointFile, ct);
            existing = JsonSerializer.Deserialize<Dictionary<string, CheckpointEntry>>(existingData)
                       ?? new Dictionary<string, CheckpointEntry>();
        }

        foreach (var (key, entry) in entriesToSave)
        {
            existing[key] = new CheckpointEntry
            {
                ValueBase64 = Convert.ToBase64String(entry.Value),
                Timestamp = entry.LastUpdated,
                Checksum = entry.Checksum
            };
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(existing);
        await File.WriteAllBytesAsync(checkpointFile, json, ct);
    }

    private async Task PublishCheckpointNotificationAsync(StateCheckpoint checkpoint, CancellationToken ct)
    {
        if (_messageBus == null) return;

        var message = new PluginMessage
        {
            Type = "state.checkpoint.completed",
            SourcePluginId = "com.datawarehouse.streaming.ultimate",
            Source = "StatefulStreamProcessing",
            Payload = new Dictionary<string, object>
            {
                ["Namespace"] = _namespace,
                ["CheckpointId"] = checkpoint.CheckpointId,
                ["EntryCount"] = checkpoint.EntryCount,
                ["ChangedEntries"] = checkpoint.ChangedEntries,
                ["IsIncremental"] = checkpoint.IsIncremental,
                ["CreatedAt"] = checkpoint.CreatedAt.ToString("O")
            }
        };

        try
        {
            await _messageBus.PublishAsync("streaming.state.checkpoint", message, ct);
        }
        catch (Exception ex)
        {

            // Non-critical: checkpoint notification failure does not affect processing
            System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void EvictExpiredState(object? state)
    {
        if (_config.StateTtl == null) return;

        var expired = _state
            .Where(kv => kv.Value.IsExpired(_config.StateTtl))
            .Select(kv => kv.Key)
            .ToArray();

        foreach (var key in expired)
        {
            if (_state.TryRemove(key, out _))
            {
                _dirtyKeys.TryRemove(key, out _);
                Interlocked.Increment(ref _evictions);
            }
        }
    }

    private void EvictOldestEntry()
    {
        var oldest = _state
            .OrderBy(kv => kv.Value.LastUpdated)
            .FirstOrDefault();

        if (oldest.Key != null)
        {
            _state.TryRemove(oldest.Key, out _);
            _dirtyKeys.TryRemove(oldest.Key, out _);
            Interlocked.Increment(ref _evictions);
        }
    }

    private static string ComputeChecksum(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _checkpointTimer.Dispose();
        _ttlTimer?.Dispose();
    }

    private sealed record StateEntry(byte[] Value, DateTimeOffset LastUpdated, string Checksum)
    {
        public bool IsExpired(TimeSpan? ttl) =>
            ttl.HasValue && DateTimeOffset.UtcNow - LastUpdated > ttl.Value;
    }

    private sealed record CheckpointEntry
    {
        public string ValueBase64 { get; init; } = string.Empty;
        public DateTimeOffset Timestamp { get; init; }
        public string Checksum { get; init; } = string.Empty;
    }
}
