using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.Persistence;

#region Kafka Persistence Backend

/// <summary>
/// Configuration for Kafka persistence backend.
/// </summary>
public sealed record KafkaPersistenceConfig : PersistenceBackendConfig
{
    /// <summary>Kafka bootstrap servers (comma-separated).</summary>
    public required string BootstrapServers { get; init; }

    /// <summary>Topic name prefix (tier suffix will be added).</summary>
    public string TopicPrefix { get; init; } = "memory";

    /// <summary>Create separate topic per tier.</summary>
    public bool UseTierTopics { get; init; } = true;

    /// <summary>Number of partitions per topic.</summary>
    public int NumPartitions { get; init; } = 6;

    /// <summary>Replication factor.</summary>
    public short ReplicationFactor { get; init; } = 3;

    /// <summary>Use compacted topics for latest state.</summary>
    public bool UseCompactedTopics { get; init; } = true;

    /// <summary>Consumer group ID.</summary>
    public string ConsumerGroupId { get; init; } = "memory-persistence";

    /// <summary>Auto offset reset (earliest, latest).</summary>
    public string AutoOffsetReset { get; init; } = "earliest";

    /// <summary>Enable Schema Registry integration.</summary>
    public bool EnableSchemaRegistry { get; init; } = true;

    /// <summary>Schema Registry URL.</summary>
    public string? SchemaRegistryUrl { get; init; }

    /// <summary>Producer acks (0, 1, all).</summary>
    public string Acks { get; init; } = "all";

    /// <summary>Producer batch size.</summary>
    public int BatchSize { get; init; } = 16384;

    /// <summary>Producer linger.ms.</summary>
    public int LingerMs { get; init; } = 5;

    /// <summary>Enable idempotent producer.</summary>
    public bool EnableIdempotence { get; init; } = true;

    /// <summary>SASL mechanism (PLAIN, SCRAM-SHA-256, etc.).</summary>
    public string? SaslMechanism { get; init; }

    /// <summary>SASL username.</summary>
    public string? SaslUsername { get; init; }

    /// <summary>SASL password.</summary>
    public string? SaslPassword { get; init; }

    /// <summary>Security protocol (PLAINTEXT, SSL, SASL_PLAINTEXT, SASL_SSL).</summary>
    public string SecurityProtocol { get; init; } = "PLAINTEXT";

    /// <summary>Message retention time in milliseconds.</summary>
    public long RetentionMs { get; init; } = 7 * 24 * 60 * 60 * 1000; // 7 days

    /// <summary>Cleanup policy for compacted topics.</summary>
    public string CleanupPolicy { get; init; } = "compact";
}

/// <summary>
/// Kafka-based event streaming persistence backend.
/// Provides event sourcing with compacted topics for latest state,
/// topic per tier, Schema Registry integration, and full replication support.
/// </summary>
/// <remarks>
/// This implementation simulates Kafka behavior using in-memory structures.
/// In production, use Confluent.Kafka package.
/// </remarks>
public sealed class KafkaPersistenceBackend : IProductionPersistenceBackend
{
    private readonly KafkaPersistenceConfig _config;
    private readonly PersistenceMetrics _metrics = new();
    private readonly PersistenceCircuitBreaker _circuitBreaker;

    // Simulated topics with partitions
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, List<KafkaMessage>>> _topics = new();
    private readonly ConcurrentDictionary<string, MemoryRecord> _compactedState = new();
    private readonly ConcurrentDictionary<string, MemoryTier> _idTierMap = new();

    private long _nextOffset;
    private bool _disposed;
    private bool _isConnected;

    /// <inheritdoc/>
    public string BackendId => _config.BackendId;

    /// <inheritdoc/>
    public string DisplayName => _config.DisplayName ?? "Kafka Event Streaming";

    /// <inheritdoc/>
    public PersistenceCapabilities Capabilities =>
        PersistenceCapabilities.Replication |
        PersistenceCapabilities.ChangeStreams |
        PersistenceCapabilities.AtomicBatch;

    /// <inheritdoc/>
    public bool IsConnected => _isConnected && !_disposed;

    public KafkaPersistenceBackend(KafkaPersistenceConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _circuitBreaker = new PersistenceCircuitBreaker();

        InitializeTopics();
    }

    private void InitializeTopics()
    {
        try
        {
            foreach (var tier in Enum.GetValues<MemoryTier>())
            {
                var topicName = GetTopicName(tier);
                var partitions = new ConcurrentDictionary<int, List<KafkaMessage>>();
                for (int i = 0; i < _config.NumPartitions; i++)
                {
                    partitions[i] = new List<KafkaMessage>();
                }
                _topics[topicName] = partitions;
            }
            _isConnected = true;
        }
        catch (Exception)
        {
            _isConnected = false;
            throw;
        }
    }

    private string GetTopicName(MemoryTier tier) =>
        _config.UseTierTopics ? $"{_config.TopicPrefix}-{tier.ToString().ToLowerInvariant()}" : _config.TopicPrefix;

    private int GetPartition(string key) => Math.Abs(key.GetHashCode()) % _config.NumPartitions;

    #region Core CRUD Operations

    /// <inheritdoc/>
    public async Task<string> StoreAsync(MemoryRecord record, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureCircuitBreaker();

        var sw = Stopwatch.StartNew();

        try
        {
            var id = record.Id ?? Guid.NewGuid().ToString();
            var topicName = GetTopicName(record.Tier);
            var partition = GetPartition(id);

            var message = new KafkaMessage
            {
                Key = id,
                Value = JsonSerializer.SerializeToUtf8Bytes(record with { Id = id }),
                Timestamp = DateTimeOffset.UtcNow,
                Offset = Interlocked.Increment(ref _nextOffset),
                Headers = new Dictionary<string, string>
                {
                    ["scope"] = record.Scope,
                    ["tier"] = record.Tier.ToString(),
                    ["operation"] = "upsert"
                }
            };

            // Produce to topic
            if (_topics.TryGetValue(topicName, out var partitions) &&
                partitions.TryGetValue(partition, out var messages))
            {
                lock (messages)
                {
                    messages.Add(message);
                }
            }

            // Update compacted state
            if (_config.UseCompactedTopics)
            {
                _compactedState[id] = record with { Id = id };
            }

            _idTierMap[id] = record.Tier;

            sw.Stop();
            _metrics.RecordWrite(sw.Elapsed);
            _circuitBreaker.RecordSuccess();

            return id;
        }
        catch (Exception)
        {
            _circuitBreaker.RecordFailure();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<MemoryRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureCircuitBreaker();

        var sw = Stopwatch.StartNew();

        try
        {
            // For compacted topics, get from latest state
            if (_config.UseCompactedTopics)
            {
                if (_compactedState.TryGetValue(id, out var record))
                {
                    if (record.ExpiresAt.HasValue && record.ExpiresAt.Value < DateTimeOffset.UtcNow)
                    {
                        await DeleteAsync(id, ct);
                        _metrics.RecordCacheMiss();
                        return null;
                    }

                    _metrics.RecordCacheHit();
                    sw.Stop();
                    _metrics.RecordRead(sw.Elapsed);
                    _circuitBreaker.RecordSuccess();
                    return record;
                }
            }
            else
            {
                // For non-compacted topics, scan through messages
                if (!_idTierMap.TryGetValue(id, out var tier))
                {
                    _metrics.RecordCacheMiss();
                    return null;
                }

                var topicName = GetTopicName(tier);
                var partition = GetPartition(id);

                if (_topics.TryGetValue(topicName, out var partitions) &&
                    partitions.TryGetValue(partition, out var messages))
                {
                    MemoryRecord? latestRecord = null;
                    lock (messages)
                    {
                        // Find the latest message for this key
                        for (int i = messages.Count - 1; i >= 0; i--)
                        {
                            if (messages[i].Key == id && !messages[i].IsTombstone)
                            {
                                latestRecord = JsonSerializer.Deserialize<MemoryRecord>(messages[i].Value);
                                break;
                            }
                        }
                    }

                    if (latestRecord != null)
                    {
                        _metrics.RecordCacheHit();
                        sw.Stop();
                        _metrics.RecordRead(sw.Elapsed);
                        _circuitBreaker.RecordSuccess();
                        return latestRecord;
                    }
                }
            }

            _metrics.RecordCacheMiss();
            sw.Stop();
            _metrics.RecordRead(sw.Elapsed);
            _circuitBreaker.RecordSuccess();
            return null;
        }
        catch (Exception)
        {
            _circuitBreaker.RecordFailure();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task UpdateAsync(string id, MemoryRecord record, CancellationToken ct = default)
    {
        // In Kafka, update is the same as store (event sourcing)
        await StoreAsync(record with { Id = id }, ct);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureCircuitBreaker();

        try
        {
            if (!_idTierMap.TryRemove(id, out var tier))
            {
                return;
            }

            var topicName = GetTopicName(tier);
            var partition = GetPartition(id);

            // Produce tombstone message (null value)
            var tombstone = new KafkaMessage
            {
                Key = id,
                Value = Array.Empty<byte>(),
                Timestamp = DateTimeOffset.UtcNow,
                Offset = Interlocked.Increment(ref _nextOffset),
                IsTombstone = true,
                Headers = new Dictionary<string, string>
                {
                    ["operation"] = "delete"
                }
            };

            if (_topics.TryGetValue(topicName, out var partitions) &&
                partitions.TryGetValue(partition, out var messages))
            {
                lock (messages)
                {
                    messages.Add(tombstone);
                }
            }

            // Remove from compacted state
            _compactedState.TryRemove(id, out _);

            _metrics.RecordDelete();
            _circuitBreaker.RecordSuccess();
        }
        catch (Exception)
        {
            _circuitBreaker.RecordFailure();
            throw;
        }
    }

    /// <inheritdoc/>
    public Task<bool> ExistsAsync(string id, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (_config.UseCompactedTopics)
        {
            return Task.FromResult(_compactedState.ContainsKey(id));
        }

        return Task.FromResult(_idTierMap.ContainsKey(id));
    }

    #endregion

    #region Batch Operations

    /// <inheritdoc/>
    public async Task StoreBatchAsync(IEnumerable<MemoryRecord> records, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureCircuitBreaker();

        try
        {
            // Kafka producer batching
            foreach (var record in records)
            {
                ct.ThrowIfCancellationRequested();
                await StoreAsync(record, ct);
            }

            _circuitBreaker.RecordSuccess();
        }
        catch (Exception)
        {
            _circuitBreaker.RecordFailure();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<MemoryRecord>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var results = new List<MemoryRecord>();

        foreach (var id in ids)
        {
            var record = await GetAsync(id, ct);
            if (record != null)
            {
                results.Add(record);
            }
        }

        return results;
    }

    /// <inheritdoc/>
    public async Task DeleteBatchAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        foreach (var id in ids)
        {
            await DeleteAsync(id, ct);
        }
    }

    #endregion

    #region Query Operations

    /// <inheritdoc/>
    public async IAsyncEnumerable<MemoryRecord> QueryAsync(MemoryQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfDisposed();

        // For compacted topics, query from state
        IEnumerable<MemoryRecord> records = _compactedState.Values;

        if (query.Tier.HasValue)
        {
            records = records.Where(r => r.Tier == query.Tier.Value);
        }

        if (!string.IsNullOrEmpty(query.Scope))
        {
            records = records.Where(r => r.Scope == query.Scope);
        }

        var now = DateTimeOffset.UtcNow;
        if (!query.IncludeExpired)
        {
            records = records.Where(r => !r.ExpiresAt.HasValue || r.ExpiresAt.Value >= now);
        }

        foreach (var record in records.Skip(query.Skip).Take(query.Limit))
        {
            ct.ThrowIfCancellationRequested();
            yield return record;
        }

        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<long> CountAsync(MemoryQuery? query = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var count = _compactedState.Values
            .Count(r => !r.ExpiresAt.HasValue || r.ExpiresAt.Value >= DateTimeOffset.UtcNow);

        return Task.FromResult((long)count);
    }

    #endregion

    #region Tier Operations

    /// <inheritdoc/>
    public Task<IEnumerable<MemoryRecord>> GetByTierAsync(MemoryTier tier, int limit = 100, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var now = DateTimeOffset.UtcNow;
        var records = _compactedState.Values
            .Where(r => r.Tier == tier && (!r.ExpiresAt.HasValue || r.ExpiresAt.Value >= now))
            .Take(limit)
            .ToList();

        return Task.FromResult<IEnumerable<MemoryRecord>>(records);
    }

    /// <inheritdoc/>
    public Task<IEnumerable<MemoryRecord>> GetByScopeAsync(string scope, int limit = 100, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var now = DateTimeOffset.UtcNow;
        var records = _compactedState.Values
            .Where(r => r.Scope == scope && (!r.ExpiresAt.HasValue || r.ExpiresAt.Value >= now))
            .Take(limit)
            .ToList();

        return Task.FromResult<IEnumerable<MemoryRecord>>(records);
    }

    #endregion

    #region Maintenance Operations

    /// <inheritdoc/>
    public Task CompactAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var now = DateTimeOffset.UtcNow;

        // Remove expired records from compacted state
        var expiredIds = _compactedState
            .Where(kvp => kvp.Value.ExpiresAt.HasValue && kvp.Value.ExpiresAt.Value < now)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var id in expiredIds)
        {
            _compactedState.TryRemove(id, out _);
            _idTierMap.TryRemove(id, out _);
        }

        // Compact topics (remove messages for deleted keys)
        foreach (var topic in _topics.Values)
        {
            foreach (var partition in topic.Values)
            {
                lock (partition)
                {
                    var keysToKeep = new Dictionary<string, int>();

                    // Find latest non-tombstone message for each key
                    for (int i = partition.Count - 1; i >= 0; i--)
                    {
                        var msg = partition[i];
                        if (!keysToKeep.ContainsKey(msg.Key))
                        {
                            if (!msg.IsTombstone)
                            {
                                keysToKeep[msg.Key] = i;
                            }
                            else
                            {
                                keysToKeep[msg.Key] = -1; // Tombstone
                            }
                        }
                    }

                    // Keep only latest messages
                    var indicesToKeep = keysToKeep.Values.Where(i => i >= 0).ToHashSet();
                    var newPartition = partition.Where((_, i) => indicesToKeep.Contains(i)).ToList();
                    partition.Clear();
                    partition.AddRange(newPartition);
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task FlushAsync(CancellationToken ct = default)
    {
        // In production, would call producer.Flush()
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<PersistenceStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var recordsByTier = new Dictionary<MemoryTier, long>();
        var sizeByTier = new Dictionary<MemoryTier, long>();

        foreach (var tier in Enum.GetValues<MemoryTier>())
        {
            var tierRecords = _compactedState.Values.Where(r => r.Tier == tier).ToList();
            recordsByTier[tier] = tierRecords.Count;
            sizeByTier[tier] = tierRecords.Sum(r => r.SizeBytes);
        }

        var totalMessages = _topics.Values
            .SelectMany(t => t.Values)
            .Sum(p => { lock (p) { return p.Count; } });

        return Task.FromResult(new PersistenceStatistics
        {
            BackendId = BackendId,
            TotalRecords = recordsByTier.Values.Sum(),
            TotalSizeBytes = sizeByTier.Values.Sum(),
            RecordsByTier = recordsByTier,
            SizeByTier = sizeByTier,
            TotalReads = _metrics.TotalReads,
            TotalWrites = _metrics.TotalWrites,
            TotalDeletes = _metrics.TotalDeletes,
            IsHealthy = IsConnected,
            HealthCheckTime = DateTimeOffset.UtcNow,
            CustomMetrics = new Dictionary<string, object>
            {
                ["topicCount"] = _topics.Count,
                ["totalMessages"] = totalMessages,
                ["compactedStateSize"] = _compactedState.Count,
                ["highestOffset"] = _nextOffset
            }
        });
    }

    /// <inheritdoc/>
    public Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_isConnected && !_disposed && _circuitBreaker.AllowOperation());
    }

    #endregion

    #region Consumer API

    /// <summary>
    /// Consumes messages from a topic starting at a specific offset.
    /// </summary>
    public async IAsyncEnumerable<KafkaMessage> ConsumeAsync(
        MemoryTier tier,
        long startOffset = 0,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var topicName = GetTopicName(tier);

        if (!_topics.TryGetValue(topicName, out var partitions))
        {
            yield break;
        }

        foreach (var partition in partitions.Values)
        {
            List<KafkaMessage> messages;
            lock (partition)
            {
                messages = partition.Where(m => m.Offset >= startOffset).ToList();
            }

            foreach (var message in messages)
            {
                ct.ThrowIfCancellationRequested();
                yield return message;
            }
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Gets the latest offset for a topic.
    /// </summary>
    public Task<long> GetLatestOffsetAsync(MemoryTier tier, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var topicName = GetTopicName(tier);

        if (!_topics.TryGetValue(topicName, out var partitions))
        {
            return Task.FromResult(0L);
        }

        var maxOffset = partitions.Values
            .SelectMany(p => { lock (p) { return p.ToList(); } })
            .Select(m => m.Offset)
            .DefaultIfEmpty(0)
            .Max();

        return Task.FromResult(maxOffset);
    }

    #endregion

    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(KafkaPersistenceBackend)); }
    private void EnsureCircuitBreaker() { if (!_circuitBreaker.AllowOperation()) throw new InvalidOperationException("Circuit breaker is open"); }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        _topics.Clear();
        _compactedState.Clear();
        _idTierMap.Clear();

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Kafka message representation.
/// </summary>
public sealed record KafkaMessage
{
    /// <summary>Message key.</summary>
    public required string Key { get; init; }

    /// <summary>Message value (serialized record).</summary>
    public required byte[] Value { get; init; }

    /// <summary>Message timestamp.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Partition offset.</summary>
    public long Offset { get; init; }

    /// <summary>Message headers.</summary>
    public Dictionary<string, string>? Headers { get; init; }

    /// <summary>Whether this is a tombstone (delete) message.</summary>
    public bool IsTombstone { get; init; }
}

#endregion

#region FoundationDB Persistence Backend

/// <summary>
/// Configuration for FoundationDB persistence backend.
/// </summary>
public sealed record FoundationDbPersistenceConfig : PersistenceBackendConfig
{
    /// <summary>FoundationDB cluster file path.</summary>
    public required string ClusterFilePath { get; init; }

    /// <summary>Directory/subspace prefix.</summary>
    public string DirectoryPrefix { get; init; } = "memory";

    /// <summary>Create separate subspace per tier.</summary>
    public bool UseTierSubspaces { get; init; } = true;

    /// <summary>Transaction retry limit.</summary>
    public int TransactionRetryLimit { get; init; } = 5;

    /// <summary>Transaction timeout in milliseconds.</summary>
    public int TransactionTimeoutMs { get; init; } = 5000;

    /// <summary>Enable watches for change notifications.</summary>
    public bool EnableWatches { get; init; } = true;

    /// <summary>Maximum concurrent transactions.</summary>
    public int MaxConcurrentTransactions { get; init; } = 100;

    /// <summary>Enable read-your-writes consistency.</summary>
    public bool EnableReadYourWrites { get; init; } = true;

    /// <summary>Enable snapshot reads for queries.</summary>
    public bool EnableSnapshotReads { get; init; } = true;

    /// <summary>Maximum key size in bytes.</summary>
    public int MaxKeySize { get; init; } = 10000;

    /// <summary>Maximum value size in bytes.</summary>
    public int MaxValueSize { get; init; } = 100000;
}

/// <summary>
/// FoundationDB-based distributed transactional KV persistence backend.
/// Provides ACID transactions, directory/subspace per tier, watches for changes,
/// and strong consistency guarantees.
/// </summary>
/// <remarks>
/// This implementation simulates FoundationDB behavior using in-memory structures.
/// In production, use the FoundationDB.Client package.
/// </remarks>
public sealed class FoundationDbPersistenceBackend : IProductionPersistenceBackend
{
    private readonly FoundationDbPersistenceConfig _config;
    private readonly PersistenceMetrics _metrics = new();
    private readonly PersistenceCircuitBreaker _circuitBreaker;

    // Simulated subspaces per tier
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, FdbRecord>> _subspaces = new();
    private readonly ConcurrentDictionary<string, string> _idSubspaceMap = new();

    // Watch notifications
    private readonly ConcurrentQueue<WatchNotification> _watchQueue = new();
    private readonly ConcurrentDictionary<string, List<Action<WatchNotification>>> _watchCallbacks = new();

    // Transaction management
    private readonly SemaphoreSlim _transactionSemaphore;
    private long _transactionCounter;

    private bool _disposed;
    private bool _isConnected;

    /// <inheritdoc/>
    public string BackendId => _config.BackendId;

    /// <inheritdoc/>
    public string DisplayName => _config.DisplayName ?? "FoundationDB Distributed KV";

    /// <inheritdoc/>
    public PersistenceCapabilities Capabilities =>
        PersistenceCapabilities.Transactions |
        PersistenceCapabilities.ChangeStreams |
        PersistenceCapabilities.OptimisticConcurrency |
        PersistenceCapabilities.AtomicBatch |
        PersistenceCapabilities.DistributedLocking;

    /// <inheritdoc/>
    public bool IsConnected => _isConnected && !_disposed;

    public FoundationDbPersistenceBackend(FoundationDbPersistenceConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _circuitBreaker = new PersistenceCircuitBreaker();
        _transactionSemaphore = new SemaphoreSlim(_config.MaxConcurrentTransactions, _config.MaxConcurrentTransactions);

        InitializeSubspaces();
    }

    private void InitializeSubspaces()
    {
        try
        {
            foreach (var tier in Enum.GetValues<MemoryTier>())
            {
                var subspaceName = GetSubspaceName(tier);
                _subspaces[subspaceName] = new ConcurrentDictionary<string, FdbRecord>();
            }
            _isConnected = true;
        }
        catch (Exception)
        {
            _isConnected = false;
            throw;
        }
    }

    private string GetSubspaceName(MemoryTier tier) =>
        _config.UseTierSubspaces ? $"{_config.DirectoryPrefix}/{tier.ToString().ToLowerInvariant()}" : _config.DirectoryPrefix;

    private string GetKey(string id) => id;

    #region Core CRUD Operations

    /// <inheritdoc/>
    public async Task<string> StoreAsync(MemoryRecord record, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureCircuitBreaker();

        await _transactionSemaphore.WaitAsync(ct);
        var sw = Stopwatch.StartNew();

        try
        {
            var txId = Interlocked.Increment(ref _transactionCounter);
            var id = record.Id ?? Guid.NewGuid().ToString();
            var subspaceName = GetSubspaceName(record.Tier);
            var key = GetKey(id);

            var fdbRecord = new FdbRecord
            {
                Id = id,
                Content = record.Content,
                Tier = record.Tier,
                Scope = record.Scope,
                Embedding = record.Embedding,
                Metadata = record.Metadata,
                CreatedAt = record.CreatedAt,
                LastAccessedAt = record.LastAccessedAt,
                AccessCount = record.AccessCount,
                ImportanceScore = record.ImportanceScore,
                ContentType = record.ContentType,
                Tags = record.Tags,
                ExpiresAt = record.ExpiresAt,
                Version = 1,
                Versionstamp = txId
            };

            if (!_subspaces.TryGetValue(subspaceName, out var subspace))
            {
                throw new InvalidOperationException($"Subspace {subspaceName} not found");
            }

            subspace[key] = fdbRecord;
            _idSubspaceMap[id] = subspaceName;

            // Notify watches
            if (_config.EnableWatches)
            {
                NotifyWatches(id, "set", fdbRecord);
            }

            sw.Stop();
            _metrics.RecordWrite(sw.Elapsed);
            _circuitBreaker.RecordSuccess();

            return id;
        }
        catch (Exception)
        {
            _circuitBreaker.RecordFailure();
            throw;
        }
        finally
        {
            _transactionSemaphore.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<MemoryRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureCircuitBreaker();

        var sw = Stopwatch.StartNew();

        try
        {
            if (!_idSubspaceMap.TryGetValue(id, out var subspaceName))
            {
                _metrics.RecordCacheMiss();
                return null;
            }

            if (!_subspaces.TryGetValue(subspaceName, out var subspace))
            {
                _metrics.RecordCacheMiss();
                return null;
            }

            var key = GetKey(id);
            if (!subspace.TryGetValue(key, out var fdbRecord))
            {
                _metrics.RecordCacheMiss();
                return null;
            }

            if (fdbRecord.ExpiresAt.HasValue && fdbRecord.ExpiresAt.Value < DateTimeOffset.UtcNow)
            {
                await DeleteAsync(id, ct);
                _metrics.RecordCacheMiss();
                return null;
            }

            _metrics.RecordCacheHit();
            sw.Stop();
            _metrics.RecordRead(sw.Elapsed);
            _circuitBreaker.RecordSuccess();

            return ToMemoryRecord(fdbRecord);
        }
        catch (Exception)
        {
            _circuitBreaker.RecordFailure();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task UpdateAsync(string id, MemoryRecord record, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureCircuitBreaker();

        await _transactionSemaphore.WaitAsync(ct);
        var sw = Stopwatch.StartNew();

        try
        {
            var txId = Interlocked.Increment(ref _transactionCounter);

            if (!_idSubspaceMap.TryGetValue(id, out var oldSubspaceName))
            {
                throw new KeyNotFoundException($"Record with ID {id} not found");
            }

            if (!_subspaces.TryGetValue(oldSubspaceName, out var oldSubspace))
            {
                throw new KeyNotFoundException($"Subspace {oldSubspaceName} not found");
            }

            var key = GetKey(id);
            if (!oldSubspace.TryGetValue(key, out var existingRecord))
            {
                throw new KeyNotFoundException($"Record with ID {id} not found");
            }

            // Optimistic concurrency check
            if (record.Version != existingRecord.Version)
            {
                throw new InvalidOperationException($"Version conflict: expected {existingRecord.Version}, got {record.Version}");
            }

            // Handle subspace change (tier change)
            var newSubspaceName = GetSubspaceName(record.Tier);
            if (oldSubspaceName != newSubspaceName)
            {
                oldSubspace.TryRemove(key, out _);
                _idSubspaceMap[id] = newSubspaceName;
            }

            if (!_subspaces.TryGetValue(newSubspaceName, out var newSubspace))
            {
                throw new InvalidOperationException($"Subspace {newSubspaceName} not found");
            }

            var updatedRecord = new FdbRecord
            {
                Id = id,
                Content = record.Content,
                Tier = record.Tier,
                Scope = record.Scope,
                Embedding = record.Embedding,
                Metadata = record.Metadata,
                CreatedAt = existingRecord.CreatedAt,
                LastAccessedAt = record.LastAccessedAt ?? DateTimeOffset.UtcNow,
                AccessCount = record.AccessCount,
                ImportanceScore = record.ImportanceScore,
                ContentType = record.ContentType,
                Tags = record.Tags,
                ExpiresAt = record.ExpiresAt,
                Version = existingRecord.Version + 1,
                Versionstamp = txId
            };

            newSubspace[key] = updatedRecord;

            // Notify watches
            if (_config.EnableWatches)
            {
                NotifyWatches(id, "update", updatedRecord);
            }

            sw.Stop();
            _metrics.RecordWrite(sw.Elapsed);
            _circuitBreaker.RecordSuccess();
        }
        catch (Exception)
        {
            _circuitBreaker.RecordFailure();
            throw;
        }
        finally
        {
            _transactionSemaphore.Release();
        }
    }

    /// <inheritdoc/>
    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureCircuitBreaker();

        try
        {
            if (!_idSubspaceMap.TryRemove(id, out var subspaceName))
            {
                return Task.CompletedTask;
            }

            if (_subspaces.TryGetValue(subspaceName, out var subspace))
            {
                var key = GetKey(id);
                subspace.TryRemove(key, out _);
            }

            // Notify watches
            if (_config.EnableWatches)
            {
                NotifyWatches(id, "clear", null);
            }

            _metrics.RecordDelete();
            _circuitBreaker.RecordSuccess();

            return Task.CompletedTask;
        }
        catch (Exception)
        {
            _circuitBreaker.RecordFailure();
            throw;
        }
    }

    /// <inheritdoc/>
    public Task<bool> ExistsAsync(string id, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return Task.FromResult(_idSubspaceMap.ContainsKey(id));
    }

    #endregion

    #region Batch Operations (Atomic Transaction)

    /// <inheritdoc/>
    public async Task StoreBatchAsync(IEnumerable<MemoryRecord> records, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureCircuitBreaker();

        await _transactionSemaphore.WaitAsync(ct);

        try
        {
            var txId = Interlocked.Increment(ref _transactionCounter);

            // All-or-nothing transaction semantics
            var operations = new List<(string Subspace, string Key, FdbRecord Record)>();

            foreach (var record in records)
            {
                ct.ThrowIfCancellationRequested();

                var id = record.Id ?? Guid.NewGuid().ToString();
                var subspaceName = GetSubspaceName(record.Tier);
                var key = GetKey(id);

                var fdbRecord = new FdbRecord
                {
                    Id = id,
                    Content = record.Content,
                    Tier = record.Tier,
                    Scope = record.Scope,
                    Embedding = record.Embedding,
                    Metadata = record.Metadata,
                    CreatedAt = record.CreatedAt,
                    LastAccessedAt = record.LastAccessedAt,
                    AccessCount = record.AccessCount,
                    ImportanceScore = record.ImportanceScore,
                    ContentType = record.ContentType,
                    Tags = record.Tags,
                    ExpiresAt = record.ExpiresAt,
                    Version = 1,
                    Versionstamp = txId
                };

                operations.Add((subspaceName, key, fdbRecord));
            }

            // Commit all operations atomically
            foreach (var (subspaceName, key, fdbRecord) in operations)
            {
                if (_subspaces.TryGetValue(subspaceName, out var subspace))
                {
                    subspace[key] = fdbRecord;
                    _idSubspaceMap[fdbRecord.Id] = subspaceName;

                    if (_config.EnableWatches)
                    {
                        NotifyWatches(fdbRecord.Id, "set", fdbRecord);
                    }
                }
            }

            _circuitBreaker.RecordSuccess();
        }
        catch (Exception)
        {
            _circuitBreaker.RecordFailure();
            throw;
        }
        finally
        {
            _transactionSemaphore.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<MemoryRecord>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var results = new List<MemoryRecord>();

        foreach (var id in ids)
        {
            var record = await GetAsync(id, ct);
            if (record != null)
            {
                results.Add(record);
            }
        }

        return results;
    }

    /// <inheritdoc/>
    public async Task DeleteBatchAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        foreach (var id in ids)
        {
            await DeleteAsync(id, ct);
        }
    }

    #endregion

    #region Query Operations

    /// <inheritdoc/>
    public async IAsyncEnumerable<MemoryRecord> QueryAsync(MemoryQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfDisposed();

        IEnumerable<FdbRecord> records;

        if (query.Tier.HasValue)
        {
            var subspaceName = GetSubspaceName(query.Tier.Value);
            records = _subspaces.GetValueOrDefault(subspaceName)?.Values ?? Enumerable.Empty<FdbRecord>();
        }
        else
        {
            records = _subspaces.Values.SelectMany(s => s.Values);
        }

        if (!string.IsNullOrEmpty(query.Scope))
        {
            records = records.Where(r => r.Scope == query.Scope);
        }

        var now = DateTimeOffset.UtcNow;
        if (!query.IncludeExpired)
        {
            records = records.Where(r => !r.ExpiresAt.HasValue || r.ExpiresAt.Value >= now);
        }

        foreach (var record in records.Skip(query.Skip).Take(query.Limit))
        {
            ct.ThrowIfCancellationRequested();
            yield return ToMemoryRecord(record);
        }

        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<long> CountAsync(MemoryQuery? query = null, CancellationToken ct = default)
    {
        var count = _subspaces.Values.SelectMany(s => s.Values)
            .Count(r => !r.ExpiresAt.HasValue || r.ExpiresAt.Value >= DateTimeOffset.UtcNow);
        return Task.FromResult((long)count);
    }

    #endregion

    #region Tier Operations

    /// <inheritdoc/>
    public Task<IEnumerable<MemoryRecord>> GetByTierAsync(MemoryTier tier, int limit = 100, CancellationToken ct = default)
    {
        var subspaceName = GetSubspaceName(tier);
        var records = _subspaces.GetValueOrDefault(subspaceName)?.Values
            .Where(r => !r.ExpiresAt.HasValue || r.ExpiresAt.Value >= DateTimeOffset.UtcNow)
            .Take(limit)
            .Select(ToMemoryRecord)
            .ToList() ?? new List<MemoryRecord>();

        return Task.FromResult<IEnumerable<MemoryRecord>>(records);
    }

    /// <inheritdoc/>
    public Task<IEnumerable<MemoryRecord>> GetByScopeAsync(string scope, int limit = 100, CancellationToken ct = default)
    {
        var records = _subspaces.Values
            .SelectMany(s => s.Values)
            .Where(r => r.Scope == scope && (!r.ExpiresAt.HasValue || r.ExpiresAt.Value >= DateTimeOffset.UtcNow))
            .Take(limit)
            .Select(ToMemoryRecord)
            .ToList();

        return Task.FromResult<IEnumerable<MemoryRecord>>(records);
    }

    #endregion

    #region Maintenance Operations

    /// <inheritdoc/>
    public Task CompactAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var subspace in _subspaces.Values)
        {
            var expiredKeys = subspace
                .Where(kvp => kvp.Value.ExpiresAt.HasValue && kvp.Value.ExpiresAt.Value < now)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                if (subspace.TryRemove(key, out var record))
                {
                    _idSubspaceMap.TryRemove(record.Id, out _);
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task<PersistenceStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        var recordsByTier = new Dictionary<MemoryTier, long>();
        var sizeByTier = new Dictionary<MemoryTier, long>();

        foreach (var tier in Enum.GetValues<MemoryTier>())
        {
            var subspaceName = GetSubspaceName(tier);
            var records = _subspaces.GetValueOrDefault(subspaceName)?.Values.ToList() ?? new List<FdbRecord>();
            recordsByTier[tier] = records.Count;
            sizeByTier[tier] = records.Sum(r => r.Content?.Length ?? 0);
        }

        return Task.FromResult(new PersistenceStatistics
        {
            BackendId = BackendId,
            TotalRecords = recordsByTier.Values.Sum(),
            TotalSizeBytes = sizeByTier.Values.Sum(),
            RecordsByTier = recordsByTier,
            SizeByTier = sizeByTier,
            TotalReads = _metrics.TotalReads,
            TotalWrites = _metrics.TotalWrites,
            TotalDeletes = _metrics.TotalDeletes,
            IsHealthy = IsConnected,
            HealthCheckTime = DateTimeOffset.UtcNow,
            CustomMetrics = new Dictionary<string, object>
            {
                ["subspaceCount"] = _subspaces.Count,
                ["transactionCount"] = _transactionCounter,
                ["watchQueueSize"] = _watchQueue.Count
            }
        });
    }

    /// <inheritdoc/>
    public Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_isConnected && !_disposed && _circuitBreaker.AllowOperation());
    }

    #endregion

    #region Watch API

    /// <summary>
    /// Registers a watch callback for key changes.
    /// </summary>
    public void Watch(string id, Action<WatchNotification> callback)
    {
        if (!_watchCallbacks.TryGetValue(id, out var callbacks))
        {
            callbacks = new List<Action<WatchNotification>>();
            _watchCallbacks[id] = callbacks;
        }

        lock (callbacks)
        {
            callbacks.Add(callback);
        }
    }

    /// <summary>
    /// Unregisters a watch callback.
    /// </summary>
    public void Unwatch(string id, Action<WatchNotification> callback)
    {
        if (_watchCallbacks.TryGetValue(id, out var callbacks))
        {
            lock (callbacks)
            {
                callbacks.Remove(callback);
            }
        }
    }

    private void NotifyWatches(string id, string operation, FdbRecord? record)
    {
        var notification = new WatchNotification
        {
            Id = id,
            Operation = operation,
            Record = record != null ? ToMemoryRecord(record) : null,
            Timestamp = DateTimeOffset.UtcNow
        };

        _watchQueue.Enqueue(notification);

        if (_watchCallbacks.TryGetValue(id, out var callbacks))
        {
            List<Action<WatchNotification>> callbacksCopy;
            lock (callbacks)
            {
                callbacksCopy = callbacks.ToList();
            }

            foreach (var callback in callbacksCopy)
            {
                try
                {
                    callback(notification);
                }
                catch { /* Ignore callback errors */ }
            }
        }
    }

    #endregion

    private MemoryRecord ToMemoryRecord(FdbRecord record) => new()
    {
        Id = record.Id,
        Content = record.Content,
        Tier = record.Tier,
        Scope = record.Scope,
        Embedding = record.Embedding,
        Metadata = record.Metadata,
        CreatedAt = record.CreatedAt,
        LastAccessedAt = record.LastAccessedAt,
        AccessCount = record.AccessCount,
        ImportanceScore = record.ImportanceScore,
        ContentType = record.ContentType,
        Tags = record.Tags,
        ExpiresAt = record.ExpiresAt,
        Version = record.Version
    };

    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(FoundationDbPersistenceBackend)); }
    private void EnsureCircuitBreaker() { if (!_circuitBreaker.AllowOperation()) throw new InvalidOperationException("Circuit breaker is open"); }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        _subspaces.Clear();
        _idSubspaceMap.Clear();
        _watchCallbacks.Clear();
        _transactionSemaphore.Dispose();

        return ValueTask.CompletedTask;
    }
}

internal sealed record FdbRecord
{
    public required string Id { get; init; }
    public required byte[] Content { get; init; }
    public MemoryTier Tier { get; init; }
    public required string Scope { get; init; }
    public float[]? Embedding { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastAccessedAt { get; init; }
    public long AccessCount { get; init; }
    public double ImportanceScore { get; init; }
    public string? ContentType { get; init; }
    public string[]? Tags { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public long Version { get; init; }
    public long Versionstamp { get; init; }
}

/// <summary>
/// Watch notification for key changes.
/// </summary>
public sealed record WatchNotification
{
    /// <summary>Key that changed.</summary>
    public required string Id { get; init; }

    /// <summary>Operation type (set, clear, update).</summary>
    public required string Operation { get; init; }

    /// <summary>Record data (null for clears).</summary>
    public MemoryRecord? Record { get; init; }

    /// <summary>Notification timestamp.</summary>
    public DateTimeOffset Timestamp { get; init; }
}

#endregion
