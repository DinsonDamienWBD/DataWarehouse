using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.Persistence;

#region Backend Registry

/// <summary>
/// Registry for discovering and managing persistence backends.
/// Supports auto-discovery and manual registration.
/// </summary>
public sealed class PersistenceBackendRegistry : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, IProductionPersistenceBackend> _backends = new();
    private readonly ConcurrentDictionary<string, Type> _backendTypes = new();
    private readonly ConcurrentDictionary<string, PersistenceBackendConfig> _backendConfigs = new();
    private bool _disposed;

    /// <summary>
    /// Gets all registered backend IDs.
    /// </summary>
    public IEnumerable<string> RegisteredBackendIds => _backends.Keys;

    /// <summary>
    /// Gets all registered backend types.
    /// </summary>
    public IEnumerable<string> RegisteredBackendTypes => _backendTypes.Keys;

    /// <summary>
    /// Creates a new persistence backend registry with auto-discovery.
    /// </summary>
    public PersistenceBackendRegistry()
    {
        RegisterBuiltInTypes();
    }

    private void RegisterBuiltInTypes()
    {
        // Register all built-in backend types
        RegisterType<RocksDbPersistenceBackend>("rocksdb");
        RegisterType<RedisPersistenceBackend>("redis");
        RegisterType<CassandraPersistenceBackend>("cassandra");
        RegisterType<MongoDbPersistenceBackend>("mongodb");
        RegisterType<PostgresPersistenceBackend>("postgres");
        RegisterType<AzureBlobPersistenceBackend>("azure-blob");
        RegisterType<S3PersistenceBackend>("s3");
        RegisterType<GcsPersistenceBackend>("gcs");
        RegisterType<KafkaPersistenceBackend>("kafka");
        RegisterType<FoundationDbPersistenceBackend>("foundationdb");
    }

    /// <summary>
    /// Registers a backend type for later instantiation.
    /// </summary>
    /// <typeparam name="T">Backend type.</typeparam>
    /// <param name="typeName">Type name for registration.</param>
    public void RegisterType<T>(string typeName) where T : IProductionPersistenceBackend
    {
        _backendTypes[typeName.ToLowerInvariant()] = typeof(T);
    }

    /// <summary>
    /// Registers an existing backend instance.
    /// </summary>
    /// <param name="backend">Backend instance.</param>
    public void Register(IProductionPersistenceBackend backend)
    {
        _backends[backend.BackendId] = backend;
    }

    /// <summary>
    /// Registers a backend with configuration.
    /// </summary>
    /// <param name="backendId">Backend ID.</param>
    /// <param name="config">Backend configuration.</param>
    public void RegisterConfig(string backendId, PersistenceBackendConfig config)
    {
        _backendConfigs[backendId] = config;
    }

    /// <summary>
    /// Gets a registered backend by ID.
    /// </summary>
    /// <param name="backendId">Backend ID.</param>
    /// <returns>Backend instance, or null if not found.</returns>
    public IProductionPersistenceBackend? Get(string backendId)
    {
        _backends.TryGetValue(backendId, out var backend);
        return backend;
    }

    /// <summary>
    /// Gets all registered backends.
    /// </summary>
    /// <returns>All backend instances.</returns>
    public IEnumerable<IProductionPersistenceBackend> GetAll()
    {
        return _backends.Values;
    }

    /// <summary>
    /// Gets backends by capability.
    /// </summary>
    /// <param name="requiredCapabilities">Required capabilities.</param>
    /// <returns>Matching backends.</returns>
    public IEnumerable<IProductionPersistenceBackend> GetByCapabilities(PersistenceCapabilities requiredCapabilities)
    {
        return _backends.Values.Where(b => (b.Capabilities & requiredCapabilities) == requiredCapabilities);
    }

    /// <summary>
    /// Gets the configuration for a backend.
    /// </summary>
    /// <param name="backendId">Backend ID.</param>
    /// <returns>Configuration, or null if not found.</returns>
    public PersistenceBackendConfig? GetConfig(string backendId)
    {
        _backendConfigs.TryGetValue(backendId, out var config);
        return config;
    }

    /// <summary>
    /// Unregisters a backend.
    /// </summary>
    /// <param name="backendId">Backend ID.</param>
    public async Task UnregisterAsync(string backendId)
    {
        if (_backends.TryRemove(backendId, out var backend))
        {
            await backend.DisposeAsync();
        }
        _backendConfigs.TryRemove(backendId, out _);
    }

    /// <summary>
    /// Gets a backend type by name.
    /// </summary>
    /// <param name="typeName">Type name.</param>
    /// <returns>Backend type, or null if not found.</returns>
    public Type? GetType(string typeName)
    {
        _backendTypes.TryGetValue(typeName.ToLowerInvariant(), out var type);
        return type;
    }

    /// <summary>
    /// Gets registry statistics.
    /// </summary>
    public RegistryStatistics GetStatistics()
    {
        var stats = new RegistryStatistics
        {
            TotalRegisteredBackends = _backends.Count,
            TotalRegisteredTypes = _backendTypes.Count,
            TotalConfigurations = _backendConfigs.Count,
            BackendsByType = new Dictionary<string, int>()
        };

        foreach (var backend in _backends.Values)
        {
            var typeName = backend.GetType().Name;
            stats.BackendsByType.TryGetValue(typeName, out var count);
            stats.BackendsByType[typeName] = count + 1;
        }

        return stats;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var backend in _backends.Values)
        {
            await backend.DisposeAsync();
        }

        _backends.Clear();
        _backendConfigs.Clear();
    }
}

/// <summary>
/// Statistics for the backend registry.
/// </summary>
public sealed record RegistryStatistics
{
    public int TotalRegisteredBackends { get; init; }
    public int TotalRegisteredTypes { get; init; }
    public int TotalConfigurations { get; init; }
    public Dictionary<string, int> BackendsByType { get; init; } = new();
}

#endregion

#region Backend Factory

/// <summary>
/// Factory for creating persistence backends from configuration.
/// </summary>
public sealed class PersistenceBackendFactory
{
    private readonly PersistenceBackendRegistry _registry;

    /// <summary>
    /// Creates a new persistence backend factory.
    /// </summary>
    /// <param name="registry">Backend registry.</param>
    public PersistenceBackendFactory(PersistenceBackendRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>
    /// Creates a backend from configuration.
    /// </summary>
    /// <typeparam name="TConfig">Configuration type.</typeparam>
    /// <param name="config">Backend configuration.</param>
    /// <returns>Created backend.</returns>
    public IProductionPersistenceBackend Create<TConfig>(TConfig config) where TConfig : PersistenceBackendConfig
    {
        IProductionPersistenceBackend backend = config switch
        {
            RocksDbPersistenceConfig rocksDb => new RocksDbPersistenceBackend(rocksDb),
            RedisPersistenceConfig redis => new RedisPersistenceBackend(redis),
            CassandraPersistenceConfig cassandra => new CassandraPersistenceBackend(cassandra),
            MongoDbPersistenceConfig mongo => new MongoDbPersistenceBackend(mongo),
            PostgresPersistenceConfig postgres => new PostgresPersistenceBackend(postgres),
            AzureBlobPersistenceConfig azure => new AzureBlobPersistenceBackend(azure),
            S3PersistenceConfig s3 => new S3PersistenceBackend(s3),
            GcsPersistenceConfig gcs => new GcsPersistenceBackend(gcs),
            KafkaPersistenceConfig kafka => new KafkaPersistenceBackend(kafka),
            FoundationDbPersistenceConfig fdb => new FoundationDbPersistenceBackend(fdb),
            _ => throw new NotSupportedException($"Unknown configuration type: {config.GetType().Name}")
        };

        _registry.Register(backend);
        _registry.RegisterConfig(config.BackendId, config);

        return backend;
    }

    /// <summary>
    /// Creates a backend by type name and configuration dictionary.
    /// </summary>
    /// <param name="typeName">Backend type name.</param>
    /// <param name="configuration">Configuration dictionary.</param>
    /// <returns>Created backend.</returns>
    public IProductionPersistenceBackend Create(string typeName, Dictionary<string, object> configuration)
    {
        var backendType = _registry.GetType(typeName);
        if (backendType == null)
        {
            throw new NotSupportedException($"Unknown backend type: {typeName}");
        }

        var configType = GetConfigType(typeName);
        if (configType == null)
        {
            throw new NotSupportedException($"No configuration type found for: {typeName}");
        }

        var json = JsonSerializer.Serialize(configuration);
        var config = JsonSerializer.Deserialize(json, configType) as PersistenceBackendConfig;
        if (config == null)
        {
            throw new InvalidOperationException($"Failed to deserialize configuration for: {typeName}");
        }

        var backend = (IProductionPersistenceBackend)Activator.CreateInstance(backendType, config)!;

        _registry.Register(backend);
        _registry.RegisterConfig(config.BackendId, config);

        return backend;
    }

    /// <summary>
    /// Creates a RocksDB backend with default configuration.
    /// </summary>
    /// <param name="backendId">Backend ID.</param>
    /// <param name="dataPath">Data path.</param>
    /// <returns>Created backend.</returns>
    public RocksDbPersistenceBackend CreateRocksDb(string backendId, string dataPath)
    {
        var config = new RocksDbPersistenceConfig
        {
            BackendId = backendId,
            DataPath = dataPath
        };

        return (RocksDbPersistenceBackend)Create(config);
    }

    /// <summary>
    /// Creates a Redis backend with default configuration.
    /// </summary>
    /// <param name="backendId">Backend ID.</param>
    /// <param name="connectionString">Connection string.</param>
    /// <returns>Created backend.</returns>
    public RedisPersistenceBackend CreateRedis(string backendId, string connectionString)
    {
        var config = new RedisPersistenceConfig
        {
            BackendId = backendId,
            ConnectionString = connectionString
        };

        return (RedisPersistenceBackend)Create(config);
    }

    private Type? GetConfigType(string typeName)
    {
        return typeName.ToLowerInvariant() switch
        {
            "rocksdb" => typeof(RocksDbPersistenceConfig),
            "redis" => typeof(RedisPersistenceConfig),
            "cassandra" => typeof(CassandraPersistenceConfig),
            "mongodb" => typeof(MongoDbPersistenceConfig),
            "postgres" => typeof(PostgresPersistenceConfig),
            "azure-blob" => typeof(AzureBlobPersistenceConfig),
            "s3" => typeof(S3PersistenceConfig),
            "gcs" => typeof(GcsPersistenceConfig),
            "kafka" => typeof(KafkaPersistenceConfig),
            "foundationdb" => typeof(FoundationDbPersistenceConfig),
            _ => null
        };
    }
}

#endregion

#region Tiered Persistence Manager

/// <summary>
/// Configuration for the tiered persistence manager.
/// </summary>
public sealed record TieredPersistenceManagerConfig
{
    /// <summary>Backend to use for each tier.</summary>
    public Dictionary<MemoryTier, string> TierBackendMapping { get; init; } = new();

    /// <summary>Fallback backend if tier-specific backend is unavailable.</summary>
    public string? FallbackBackendId { get; init; }

    /// <summary>Enable automatic failover.</summary>
    public bool EnableFailover { get; init; } = true;

    /// <summary>Health check interval in seconds.</summary>
    public int HealthCheckIntervalSeconds { get; init; } = 30;

    /// <summary>Enable write-ahead logging for durability.</summary>
    public bool EnableWAL { get; init; } = true;

    /// <summary>WAL storage path.</summary>
    public string? WalPath { get; init; }
}

/// <summary>
/// Manages multiple persistence backends for different memory tiers.
/// Provides unified access with automatic tier routing, failover, and WAL.
/// </summary>
public sealed class TieredPersistenceManager : IProductionPersistenceBackend
{
    private readonly TieredPersistenceManagerConfig _config;
    private readonly PersistenceBackendRegistry _registry;
    private readonly WriteAheadLog? _wal;
    private readonly PersistenceMetrics _metrics = new();
    private readonly ConcurrentDictionary<MemoryTier, IProductionPersistenceBackend> _tierBackends = new();
    private readonly Timer? _healthCheckTimer;

    private bool _disposed;
    private bool _isConnected;

    /// <inheritdoc/>
    public string BackendId => "tiered-manager";

    /// <inheritdoc/>
    public string DisplayName => "Tiered Persistence Manager";

    /// <inheritdoc/>
    public PersistenceCapabilities Capabilities
    {
        get
        {
            var combined = PersistenceCapabilities.None;
            foreach (var backend in _tierBackends.Values)
            {
                combined |= backend.Capabilities;
            }
            return combined;
        }
    }

    /// <inheritdoc/>
    public bool IsConnected => _isConnected && !_disposed;

    /// <summary>
    /// Creates a new tiered persistence manager.
    /// </summary>
    /// <param name="config">Manager configuration.</param>
    /// <param name="registry">Backend registry.</param>
    public TieredPersistenceManager(TieredPersistenceManagerConfig config, PersistenceBackendRegistry registry)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));

        // Initialize WAL if enabled
        if (_config.EnableWAL && !string.IsNullOrEmpty(_config.WalPath))
        {
            _wal = new WriteAheadLog(_config.WalPath);
        }

        // Initialize tier backends
        InitializeTierBackends();

        // Start health check timer
        if (_config.EnableFailover && _config.HealthCheckIntervalSeconds > 0)
        {
            _healthCheckTimer = new Timer(
                _ => _ = PerformHealthCheckAsync(),
                null,
                TimeSpan.FromSeconds(_config.HealthCheckIntervalSeconds),
                TimeSpan.FromSeconds(_config.HealthCheckIntervalSeconds));
        }
    }

    private void InitializeTierBackends()
    {
        try
        {
            foreach (var (tier, backendId) in _config.TierBackendMapping)
            {
                var backend = _registry.Get(backendId);
                if (backend != null)
                {
                    _tierBackends[tier] = backend;
                }
            }

            _isConnected = _tierBackends.Values.All(b => b.IsConnected);
        }
        catch (Exception)
        {
            _isConnected = false;
            throw;
        }
    }

    private IProductionPersistenceBackend GetBackendForTier(MemoryTier tier)
    {
        if (_tierBackends.TryGetValue(tier, out var backend) && backend.IsConnected)
        {
            return backend;
        }

        // Failover to fallback
        if (_config.EnableFailover && !string.IsNullOrEmpty(_config.FallbackBackendId))
        {
            var fallback = _registry.Get(_config.FallbackBackendId);
            if (fallback != null && fallback.IsConnected)
            {
                return fallback;
            }
        }

        throw new InvalidOperationException($"No healthy backend available for tier {tier}");
    }

    #region IProductionPersistenceBackend Implementation

    /// <inheritdoc/>
    public async Task<string> StoreAsync(MemoryRecord record, CancellationToken ct = default)
    {
        var backend = GetBackendForTier(record.Tier);

        // Write to WAL first
        if (_wal != null)
        {
            await _wal.AppendAsync(new WalRecord
            {
                Operation = WalOperation.Put,
                RecordId = record.Id ?? Guid.NewGuid().ToString(),
                Record = record,
                Tier = record.Tier,
                Timestamp = DateTimeOffset.UtcNow
            }, ct);
        }

        return await backend.StoreAsync(record, ct);
    }

    /// <inheritdoc/>
    public async Task<MemoryRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        // Search all tier backends
        foreach (var backend in _tierBackends.Values)
        {
            var record = await backend.GetAsync(id, ct);
            if (record != null)
            {
                return record;
            }
        }

        return null;
    }

    /// <inheritdoc/>
    public async Task UpdateAsync(string id, MemoryRecord record, CancellationToken ct = default)
    {
        var backend = GetBackendForTier(record.Tier);

        if (_wal != null)
        {
            await _wal.AppendAsync(new WalRecord
            {
                Operation = WalOperation.Put,
                RecordId = id,
                Record = record,
                Tier = record.Tier,
                Timestamp = DateTimeOffset.UtcNow
            }, ct);
        }

        await backend.UpdateAsync(id, record, ct);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        // Find and delete from appropriate backend
        foreach (var (tier, backend) in _tierBackends)
        {
            if (await backend.ExistsAsync(id, ct))
            {
                if (_wal != null)
                {
                    await _wal.AppendAsync(new WalRecord
                    {
                        Operation = WalOperation.Delete,
                        RecordId = id,
                        Tier = tier,
                        Timestamp = DateTimeOffset.UtcNow
                    }, ct);
                }

                await backend.DeleteAsync(id, ct);
                return;
            }
        }
    }

    /// <inheritdoc/>
    public async Task<bool> ExistsAsync(string id, CancellationToken ct = default)
    {
        foreach (var backend in _tierBackends.Values)
        {
            if (await backend.ExistsAsync(id, ct))
            {
                return true;
            }
        }
        return false;
    }

    /// <inheritdoc/>
    public async Task StoreBatchAsync(IEnumerable<MemoryRecord> records, CancellationToken ct = default)
    {
        // Group by tier and batch to appropriate backends
        var byTier = records.GroupBy(r => r.Tier);

        foreach (var group in byTier)
        {
            var backend = GetBackendForTier(group.Key);
            await backend.StoreBatchAsync(group, ct);
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

    /// <inheritdoc/>
    public async IAsyncEnumerable<MemoryRecord> QueryAsync(MemoryQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (query.Tier.HasValue)
        {
            var backend = GetBackendForTier(query.Tier.Value);
            await foreach (var record in backend.QueryAsync(query, ct))
            {
                yield return record;
            }
        }
        else
        {
            // Query all backends
            foreach (var backend in _tierBackends.Values)
            {
                await foreach (var record in backend.QueryAsync(query, ct))
                {
                    yield return record;
                }
            }
        }
    }

    /// <inheritdoc/>
    public async Task<long> CountAsync(MemoryQuery? query = null, CancellationToken ct = default)
    {
        long total = 0;

        if (query?.Tier.HasValue == true)
        {
            var backend = GetBackendForTier(query.Tier.Value);
            return await backend.CountAsync(query, ct);
        }

        foreach (var backend in _tierBackends.Values)
        {
            total += await backend.CountAsync(query, ct);
        }

        return total;
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<MemoryRecord>> GetByTierAsync(MemoryTier tier, int limit = 100, CancellationToken ct = default)
    {
        var backend = GetBackendForTier(tier);
        return await backend.GetByTierAsync(tier, limit, ct);
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<MemoryRecord>> GetByScopeAsync(string scope, int limit = 100, CancellationToken ct = default)
    {
        var results = new List<MemoryRecord>();

        foreach (var backend in _tierBackends.Values)
        {
            var records = await backend.GetByScopeAsync(scope, limit - results.Count, ct);
            results.AddRange(records);

            if (results.Count >= limit)
            {
                break;
            }
        }

        return results.Take(limit);
    }

    /// <inheritdoc/>
    public async Task CompactAsync(CancellationToken ct = default)
    {
        foreach (var backend in _tierBackends.Values)
        {
            await backend.CompactAsync(ct);
        }

        if (_wal != null)
        {
            await _wal.CompactAsync(ct);
        }
    }

    /// <inheritdoc/>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        foreach (var backend in _tierBackends.Values)
        {
            await backend.FlushAsync(ct);
        }

        if (_wal != null)
        {
            await _wal.FlushAsync(ct);
        }
    }

    /// <inheritdoc/>
    public async Task<PersistenceStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        var aggregated = new PersistenceStatistics
        {
            BackendId = BackendId,
            TotalRecords = 0,
            TotalSizeBytes = 0,
            RecordsByTier = new Dictionary<MemoryTier, long>(),
            SizeByTier = new Dictionary<MemoryTier, long>(),
            IsHealthy = IsConnected,
            HealthCheckTime = DateTimeOffset.UtcNow,
            CustomMetrics = new Dictionary<string, object>
            {
                ["tierBackendCount"] = _tierBackends.Count,
                ["walEnabled"] = _wal != null
            }
        };

        foreach (var (tier, backend) in _tierBackends)
        {
            var stats = await backend.GetStatisticsAsync(ct);
            aggregated = aggregated with
            {
                TotalRecords = aggregated.TotalRecords + stats.TotalRecords,
                TotalSizeBytes = aggregated.TotalSizeBytes + stats.TotalSizeBytes,
                TotalReads = aggregated.TotalReads + stats.TotalReads,
                TotalWrites = aggregated.TotalWrites + stats.TotalWrites,
                TotalDeletes = aggregated.TotalDeletes + stats.TotalDeletes
            };

            foreach (var (t, count) in stats.RecordsByTier)
            {
                aggregated.RecordsByTier.TryGetValue(t, out var existing);
                aggregated.RecordsByTier[t] = existing + count;
            }

            foreach (var (t, size) in stats.SizeByTier)
            {
                aggregated.SizeByTier.TryGetValue(t, out var existing);
                aggregated.SizeByTier[t] = existing + size;
            }
        }

        return aggregated;
    }

    /// <inheritdoc/>
    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        foreach (var backend in _tierBackends.Values)
        {
            if (!await backend.IsHealthyAsync(ct))
            {
                return false;
            }
        }
        return true;
    }

    #endregion

    private async Task PerformHealthCheckAsync()
    {
        foreach (var (tier, backend) in _tierBackends)
        {
            try
            {
                var isHealthy = await backend.IsHealthyAsync();
                if (!isHealthy && _config.EnableFailover)
                {
                    // Try to find alternative backend
                    // Log warning about unhealthy backend
                }
            }
            catch
            {
                // Log error
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _healthCheckTimer?.Dispose();

        if (_wal != null)
        {
            await _wal.DisposeAsync();
        }

        // Note: Don't dispose backends - registry owns them
        _tierBackends.Clear();
    }
}

#endregion

#region Write-Ahead Log

/// <summary>
/// WAL record for persistence operations.
/// </summary>
public sealed record WalRecord
{
    public WalOperation Operation { get; init; }
    public required string RecordId { get; init; }
    public MemoryRecord? Record { get; init; }
    public MemoryTier Tier { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public long SequenceNumber { get; init; }
}

/// <summary>
/// Write-ahead log for durability across persistence backends.
/// Ensures data is not lost during failures.
/// </summary>
public sealed class WriteAheadLog : IAsyncDisposable
{
    private readonly string _walPath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentQueue<WalRecord> _pendingRecords = new();
    private readonly List<WalRecord> _persistedRecords = new();

    private long _sequenceNumber;
    private bool _disposed;

    /// <summary>
    /// Gets the current sequence number.
    /// </summary>
    public long CurrentSequenceNumber => Interlocked.Read(ref _sequenceNumber);

    /// <summary>
    /// Gets the number of pending records.
    /// </summary>
    public int PendingCount => _pendingRecords.Count;

    /// <summary>
    /// Creates a new write-ahead log.
    /// </summary>
    /// <param name="walPath">Path to WAL storage.</param>
    public WriteAheadLog(string walPath)
    {
        _walPath = walPath ?? throw new ArgumentNullException(nameof(walPath));
        Directory.CreateDirectory(_walPath);

        // Load existing WAL records
        LoadExistingRecords();
    }

    private void LoadExistingRecords()
    {
        var walFiles = Directory.GetFiles(_walPath, "*.wal").OrderBy(f => f);

        foreach (var file in walFiles)
        {
            try
            {
                var content = File.ReadAllText(file);
                var records = JsonSerializer.Deserialize<List<WalRecord>>(content);
                if (records != null)
                {
                    _persistedRecords.AddRange(records);
                    foreach (var record in records)
                    {
                        if (record.SequenceNumber > _sequenceNumber)
                        {
                            _sequenceNumber = record.SequenceNumber;
                        }
                    }
                }
            }
            catch
            {
                // Corrupted WAL file - skip
            }
        }
    }

    /// <summary>
    /// Appends a record to the WAL.
    /// </summary>
    /// <param name="record">WAL record.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task AppendAsync(WalRecord record, CancellationToken ct = default)
    {
        var seqNum = Interlocked.Increment(ref _sequenceNumber);
        var recordWithSeq = record with { SequenceNumber = seqNum };

        _pendingRecords.Enqueue(recordWithSeq);

        // Auto-flush if too many pending
        if (_pendingRecords.Count > 1000)
        {
            await FlushAsync(ct);
        }
    }

    /// <summary>
    /// Flushes pending records to disk.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var records = new List<WalRecord>();
            while (_pendingRecords.TryDequeue(out var record))
            {
                records.Add(record);
            }

            if (records.Count == 0) return;

            var fileName = Path.Combine(_walPath, $"wal_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.wal");
            var json = JsonSerializer.Serialize(records);
            await File.WriteAllTextAsync(fileName, json, ct);

            _persistedRecords.AddRange(records);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Compacts the WAL by removing old records.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task CompactAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            // Keep only last 10000 records
            if (_persistedRecords.Count > 10000)
            {
                _persistedRecords.RemoveRange(0, _persistedRecords.Count - 10000);
            }

            // Delete old WAL files
            var walFiles = Directory.GetFiles(_walPath, "*.wal").OrderBy(f => f).ToList();
            if (walFiles.Count > 10)
            {
                foreach (var file in walFiles.Take(walFiles.Count - 10))
                {
                    try { File.Delete(file); } catch { /* Best-effort cleanup — failure is non-fatal */ }
                }
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Gets records for replay after failure.
    /// </summary>
    /// <param name="afterSequence">Start sequence number.</param>
    /// <returns>Records to replay.</returns>
    public IEnumerable<WalRecord> GetRecordsForReplay(long afterSequence = 0)
    {
        return _persistedRecords.Where(r => r.SequenceNumber > afterSequence);
    }

    /// <summary>
    /// Truncates the WAL up to a sequence number.
    /// </summary>
    /// <param name="upToSequence">Sequence number to truncate to.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task TruncateAsync(long upToSequence, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            _persistedRecords.RemoveAll(r => r.SequenceNumber <= upToSequence);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await FlushAsync();
        _writeLock.Dispose();
    }
}

#endregion

#region Persistence Encryption

/// <summary>
/// Configuration for persistence encryption.
/// </summary>
public sealed record PersistenceEncryptionConfig
{
    /// <summary>Master encryption key (base64 encoded, 256-bit).</summary>
    public required string MasterKey { get; init; }

    /// <summary>Encryption algorithm.</summary>
    public EncryptionAlgorithm Algorithm { get; init; } = EncryptionAlgorithm.AES256GCM;

    /// <summary>Key derivation iterations.</summary>
    public int KeyDerivationIterations { get; init; } = 10000;

    /// <summary>Enable automatic key rotation.</summary>
    public bool EnableKeyRotation { get; init; }

    /// <summary>Key rotation interval in days.</summary>
    public int KeyRotationDays { get; init; } = 90;

    /// <summary>Cache decrypted records in memory.</summary>
    public bool EnableDecryptionCache { get; init; } = true;

    /// <summary>Decryption cache size.</summary>
    public int DecryptionCacheSize { get; init; } = 1000;
}

/// <summary>
/// Encryption algorithms.
/// </summary>
public enum EncryptionAlgorithm
{
    /// <summary>AES-256 with GCM mode.</summary>
    AES256GCM,

    /// <summary>AES-256 with CBC mode.</summary>
    AES256CBC,

    /// <summary>ChaCha20-Poly1305.</summary>
    ChaCha20Poly1305
}

/// <summary>
/// Encryption-at-rest wrapper for persistence backends.
/// Transparently encrypts data before storage and decrypts on retrieval.
/// </summary>
public sealed class PersistenceEncryption : IProductionPersistenceBackend
{
    private readonly IProductionPersistenceBackend _innerBackend;
    private readonly PersistenceEncryptionConfig _config;
    private readonly byte[] _masterKey;
    private readonly ConcurrentDictionary<string, MemoryRecord>? _decryptionCache;

    private bool _disposed;

    /// <inheritdoc/>
    public string BackendId => $"encrypted-{_innerBackend.BackendId}";

    /// <inheritdoc/>
    public string DisplayName => $"Encrypted {_innerBackend.DisplayName}";

    /// <inheritdoc/>
    public PersistenceCapabilities Capabilities =>
        _innerBackend.Capabilities | PersistenceCapabilities.Encryption;

    /// <inheritdoc/>
    public bool IsConnected => _innerBackend.IsConnected && !_disposed;

    /// <summary>
    /// Creates a new encryption wrapper for a backend.
    /// </summary>
    /// <param name="innerBackend">Backend to wrap.</param>
    /// <param name="config">Encryption configuration.</param>
    public PersistenceEncryption(IProductionPersistenceBackend innerBackend, PersistenceEncryptionConfig config)
    {
        _innerBackend = innerBackend ?? throw new ArgumentNullException(nameof(innerBackend));
        _config = config ?? throw new ArgumentNullException(nameof(config));

        _masterKey = Convert.FromBase64String(config.MasterKey);

        if (config.EnableDecryptionCache)
        {
            _decryptionCache = new ConcurrentDictionary<string, MemoryRecord>();
        }
    }

    #region IProductionPersistenceBackend Implementation

    /// <inheritdoc/>
    public async Task<string> StoreAsync(MemoryRecord record, CancellationToken ct = default)
    {
        var encryptedRecord = EncryptRecord(record);
        var id = await _innerBackend.StoreAsync(encryptedRecord, ct);

        // Cache decrypted version
        _decryptionCache?.TryAdd(id, record with { Id = id });

        return id;
    }

    /// <inheritdoc/>
    public async Task<MemoryRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        // Check cache first
        if (_decryptionCache?.TryGetValue(id, out var cached) == true)
        {
            return cached;
        }

        var encryptedRecord = await _innerBackend.GetAsync(id, ct);
        if (encryptedRecord == null) return null;

        var decrypted = DecryptRecord(encryptedRecord);

        // Cache decrypted version
        if (_decryptionCache != null)
        {
            if (_decryptionCache.Count >= _config.DecryptionCacheSize)
            {
                // Simple eviction - remove first
                var firstKey = _decryptionCache.Keys.FirstOrDefault();
                if (firstKey != null)
                {
                    _decryptionCache.TryRemove(firstKey, out _);
                }
            }
            _decryptionCache.TryAdd(id, decrypted);
        }

        return decrypted;
    }

    /// <inheritdoc/>
    public async Task UpdateAsync(string id, MemoryRecord record, CancellationToken ct = default)
    {
        var encryptedRecord = EncryptRecord(record);
        await _innerBackend.UpdateAsync(id, encryptedRecord, ct);

        // Update cache
        if (_decryptionCache != null)
        {
            _decryptionCache[id] = record with { Id = id };
        }
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await _innerBackend.DeleteAsync(id, ct);
        _decryptionCache?.TryRemove(id, out _);
    }

    /// <inheritdoc/>
    public Task<bool> ExistsAsync(string id, CancellationToken ct = default) =>
        _innerBackend.ExistsAsync(id, ct);

    /// <inheritdoc/>
    public async Task StoreBatchAsync(IEnumerable<MemoryRecord> records, CancellationToken ct = default)
    {
        var encrypted = records.Select(r => EncryptRecord(r));
        await _innerBackend.StoreBatchAsync(encrypted, ct);
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<MemoryRecord>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var encrypted = await _innerBackend.GetBatchAsync(ids, ct);
        return encrypted.Select(r => DecryptRecord(r));
    }

    /// <inheritdoc/>
    public Task DeleteBatchAsync(IEnumerable<string> ids, CancellationToken ct = default) =>
        _innerBackend.DeleteBatchAsync(ids, ct);

    /// <inheritdoc/>
    public async IAsyncEnumerable<MemoryRecord> QueryAsync(MemoryQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var encrypted in _innerBackend.QueryAsync(query, ct))
        {
            yield return DecryptRecord(encrypted);
        }
    }

    /// <inheritdoc/>
    public Task<long> CountAsync(MemoryQuery? query = null, CancellationToken ct = default) =>
        _innerBackend.CountAsync(query, ct);

    /// <inheritdoc/>
    public async Task<IEnumerable<MemoryRecord>> GetByTierAsync(MemoryTier tier, int limit = 100, CancellationToken ct = default)
    {
        var encrypted = await _innerBackend.GetByTierAsync(tier, limit, ct);
        return encrypted.Select(r => DecryptRecord(r));
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<MemoryRecord>> GetByScopeAsync(string scope, int limit = 100, CancellationToken ct = default)
    {
        var encrypted = await _innerBackend.GetByScopeAsync(scope, limit, ct);
        return encrypted.Select(r => DecryptRecord(r));
    }

    /// <inheritdoc/>
    public Task CompactAsync(CancellationToken ct = default) =>
        _innerBackend.CompactAsync(ct);

    /// <inheritdoc/>
    public Task FlushAsync(CancellationToken ct = default) =>
        _innerBackend.FlushAsync(ct);

    /// <inheritdoc/>
    public Task<PersistenceStatistics> GetStatisticsAsync(CancellationToken ct = default) =>
        _innerBackend.GetStatisticsAsync(ct);

    /// <inheritdoc/>
    public Task<bool> IsHealthyAsync(CancellationToken ct = default) =>
        _innerBackend.IsHealthyAsync(ct);

    #endregion

    #region Encryption/Decryption

    private MemoryRecord EncryptRecord(MemoryRecord record)
    {
        using var aes = Aes.Create();
        aes.Key = _masterKey;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var encrypted = encryptor.TransformFinalBlock(record.Content, 0, record.Content.Length);

        // Prepend IV to encrypted data
        var result = new byte[aes.IV.Length + encrypted.Length];
        aes.IV.CopyTo(result, 0);
        encrypted.CopyTo(result, aes.IV.Length);

        return record with
        {
            Content = result,
            ContentHash = Convert.ToBase64String(SHA256.HashData(record.Content))
        };
    }

    private MemoryRecord DecryptRecord(MemoryRecord record)
    {
        using var aes = Aes.Create();
        aes.Key = _masterKey;

        // Extract IV from content
        var iv = new byte[16];
        var encrypted = new byte[record.Content.Length - 16];
        Array.Copy(record.Content, 0, iv, 0, 16);
        Array.Copy(record.Content, 16, encrypted, 0, encrypted.Length);

        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        var decrypted = decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);

        return record with { Content = decrypted };
    }

    #endregion

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _decryptionCache?.Clear();
        await _innerBackend.DisposeAsync();
    }
}

#endregion
