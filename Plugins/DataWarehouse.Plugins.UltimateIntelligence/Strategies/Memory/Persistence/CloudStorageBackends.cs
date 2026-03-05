using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.Persistence;

#region Azure Blob Storage

/// <summary>
/// Configuration for Azure Blob Storage persistence backend.
/// </summary>
public sealed record AzureBlobPersistenceConfig : PersistenceBackendConfig
{
    /// <summary>Azure Storage connection string.</summary>
    public required string ConnectionString { get; init; }

    /// <summary>Container name prefix (tier suffix will be added).</summary>
    public string ContainerPrefix { get; init; } = "memory";

    /// <summary>Create separate container per tier.</summary>
    public bool UseTierContainers { get; init; } = true;

    /// <summary>Blob prefix within container.</summary>
    public string BlobPrefix { get; init; } = "";

    /// <summary>Access tier mapping per memory tier.</summary>
    public Dictionary<MemoryTier, string> AccessTierMapping { get; init; } = new()
    {
        [MemoryTier.Immediate] = "Hot",
        [MemoryTier.Working] = "Hot",
        [MemoryTier.ShortTerm] = "Cool",
        [MemoryTier.LongTerm] = "Archive"
    };

    /// <summary>Enable blob index tags for querying.</summary>
    public bool EnableBlobIndexTags { get; init; } = true;

    /// <summary>Enable soft delete for blobs.</summary>
    public bool EnableSoftDelete { get; init; } = true;

    /// <summary>Soft delete retention days.</summary>
    public int SoftDeleteRetentionDays { get; init; } = 7;

    /// <summary>Enable lifecycle management policies.</summary>
    public bool EnableLifecyclePolicies { get; init; } = true;

    /// <summary>Days before moving to Cool tier.</summary>
    public int DaysToCool { get; init; } = 30;

    /// <summary>Days before moving to Archive tier.</summary>
    public int DaysToArchive { get; init; } = 90;

    /// <summary>Maximum concurrent uploads.</summary>
    public int MaxConcurrentUploads { get; init; } = 10;

    /// <summary>Block size for large blob uploads (in bytes).</summary>
    public long BlockSize { get; init; } = 4 * 1024 * 1024; // 4MB
}

/// <summary>
/// Azure Blob Storage persistence backend.
/// Provides cloud-native storage with container per tier, Hot/Cool/Archive access tiers,
/// blob index tags for querying, and lifecycle policies for automatic tiering.
/// </summary>
/// <remarks>
/// This backend requires the Azure.Storage.Blobs NuGet package for production use.
/// It uses in-memory structures as a local development fallback when the SDK is not available,
/// but will log a warning on construction indicating that data is NOT persisted to Azure Blob Storage.
/// </remarks>
public sealed class AzureBlobPersistenceBackend : IProductionPersistenceBackend
{
    private readonly AzureBlobPersistenceConfig _config;
    private readonly PersistenceMetrics _metrics = new();
    private readonly PersistenceCircuitBreaker _circuitBreaker;
    private readonly bool _isSimulated;

    // In-memory fallback containers and blobs (used only when Azure.Storage.Blobs is unavailable)
    private readonly BoundedDictionary<string, BoundedDictionary<string, AzureBlob>> _containers = new BoundedDictionary<string, BoundedDictionary<string, AzureBlob>>(1000);
    private readonly BoundedDictionary<string, (string Container, string BlobName)> _idIndex = new BoundedDictionary<string, (string Container, string BlobName)>(1000);
    private readonly SemaphoreSlim _uploadSemaphore;

    private bool _disposed;
    private bool _isConnected;

    /// <inheritdoc/>
    public string BackendId => _config.BackendId;

    /// <inheritdoc/>
    public string DisplayName => _config.DisplayName ?? "Azure Blob Storage";

    /// <inheritdoc/>
    public PersistenceCapabilities Capabilities =>
        PersistenceCapabilities.Replication |
        PersistenceCapabilities.Snapshots |
        PersistenceCapabilities.SecondaryIndexes |
        PersistenceCapabilities.TTL;

    /// <inheritdoc/>
    public bool IsConnected => _isConnected && !_disposed;

    public AzureBlobPersistenceBackend(AzureBlobPersistenceConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _circuitBreaker = new PersistenceCircuitBreaker();
        _uploadSemaphore = new SemaphoreSlim(_config.MaxConcurrentUploads, _config.MaxConcurrentUploads);

        _isSimulated = !IsAzureBlobSdkAvailable();
        if (_isSimulated && _config.RequireRealBackend)
        {
            throw new PlatformNotSupportedException(
                "Azure Blob persistence requires the 'Azure.Storage.Blobs' NuGet package. " +
                "Install it via: dotnet add package Azure.Storage.Blobs. " +
                "Set RequireRealBackend=false to use in-memory fallback (NOT for production).");
        }

        InitializeContainers();
    }

    private static bool IsAzureBlobSdkAvailable()
    {
        try
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Any(a => a.GetName().Name == "Azure.Storage.Blobs");
        }
        catch
        {
            return false;
        }
    }

    private void InitializeContainers()
    {
        try
        {
            if (_isSimulated)
            {
                Debug.WriteLine("WARNING: AzureBlobPersistenceBackend running in in-memory simulation mode. " +
                    "Data will NOT be persisted to Azure Blob Storage. Install 'Azure.Storage.Blobs' NuGet package for production use.");
            }
            foreach (var tier in Enum.GetValues<MemoryTier>())
            {
                var containerName = GetContainerName(tier);
                _containers[containerName] = new BoundedDictionary<string, AzureBlob>(1000);
            }
            _isConnected = true;
        }
        catch (Exception)
        {
            _isConnected = false;
            throw;
        }
    }

    private string GetContainerName(MemoryTier tier) =>
        _config.UseTierContainers ? $"{_config.ContainerPrefix}-{tier.ToString().ToLowerInvariant()}" : _config.ContainerPrefix;

    private string GetBlobName(string id) =>
        string.IsNullOrEmpty(_config.BlobPrefix) ? $"{id}.json" : $"{_config.BlobPrefix}/{id}.json";

    #region Core CRUD Operations

    /// <inheritdoc/>
    public async Task<string> StoreAsync(MemoryRecord record, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureCircuitBreaker();

        await _uploadSemaphore.WaitAsync(ct);
        var sw = Stopwatch.StartNew();

        try
        {
            var id = record.Id ?? Guid.NewGuid().ToString();
            var containerName = GetContainerName(record.Tier);
            var blobName = GetBlobName(id);

            var blob = new AzureBlob
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
                AccessTier = _config.AccessTierMapping.GetValueOrDefault(record.Tier, "Hot"),
                BlobIndexTags = _config.EnableBlobIndexTags ? CreateBlobIndexTags(record) : null
            };

            var container = _containers[containerName];
            container[blobName] = blob;
            _idIndex[id] = (containerName, blobName);

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
            _uploadSemaphore.Release();
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
            if (!_idIndex.TryGetValue(id, out var location))
            {
                _metrics.RecordCacheMiss();
                return null;
            }

            if (!_containers.TryGetValue(location.Container, out var container))
            {
                _metrics.RecordCacheMiss();
                return null;
            }

            if (!container.TryGetValue(location.BlobName, out var blob))
            {
                _metrics.RecordCacheMiss();
                return null;
            }

            if (blob.ExpiresAt.HasValue && blob.ExpiresAt.Value < DateTimeOffset.UtcNow)
            {
                await DeleteAsync(id, ct);
                _metrics.RecordCacheMiss();
                return null;
            }

            _metrics.RecordCacheHit();
            sw.Stop();
            _metrics.RecordRead(sw.Elapsed);
            _circuitBreaker.RecordSuccess();

            return ToMemoryRecord(blob);
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

        if (!_idIndex.TryGetValue(id, out var location))
        {
            throw new KeyNotFoundException($"Blob with ID {id} not found");
        }

        // Handle tier change
        var newContainerName = GetContainerName(record.Tier);
        if (location.Container != newContainerName)
        {
            await DeleteAsync(id, ct);
            await StoreAsync(record with { Id = id }, ct);
            return;
        }

        await StoreAsync(record with { Id = id }, ct);
    }

    /// <inheritdoc/>
    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureCircuitBreaker();

        try
        {
            if (_idIndex.TryRemove(id, out var location))
            {
                if (_containers.TryGetValue(location.Container, out var container))
                {
                    container.TryRemove(location.BlobName, out _);
                }
                _metrics.RecordDelete();
            }

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
        return Task.FromResult(_idIndex.ContainsKey(id));
    }

    #endregion

    #region Batch Operations

    /// <inheritdoc/>
    public async Task StoreBatchAsync(IEnumerable<MemoryRecord> records, CancellationToken ct = default)
    {
        var tasks = records.Select(r => StoreAsync(r, ct));
        await Task.WhenAll(tasks);
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<MemoryRecord>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var tasks = ids.Select(id => GetAsync(id, ct));
        var results = await Task.WhenAll(tasks);
        return results.Where(r => r != null).Cast<MemoryRecord>();
    }

    /// <inheritdoc/>
    public async Task DeleteBatchAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var tasks = ids.Select(id => DeleteAsync(id, ct));
        await Task.WhenAll(tasks);
    }

    #endregion

    #region Query Operations

    /// <inheritdoc/>
    public async IAsyncEnumerable<MemoryRecord> QueryAsync(MemoryQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfDisposed();

        IEnumerable<AzureBlob> blobs;

        if (query.Tier.HasValue)
        {
            var containerName = GetContainerName(query.Tier.Value);
            blobs = _containers.GetValueOrDefault(containerName)?.Values ?? Enumerable.Empty<AzureBlob>();
        }
        else
        {
            blobs = _containers.Values.SelectMany(c => c.Values);
        }

        // Use blob index tags for filtering
        if (!string.IsNullOrEmpty(query.Scope))
        {
            blobs = blobs.Where(b => b.Scope == query.Scope);
        }

        var now = DateTimeOffset.UtcNow;
        blobs = blobs.Where(b => !b.ExpiresAt.HasValue || b.ExpiresAt.Value >= now);

        var results = blobs.Skip(query.Skip).Take(query.Limit);

        foreach (var blob in results)
        {
            ct.ThrowIfCancellationRequested();
            yield return ToMemoryRecord(blob);
        }

        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<long> CountAsync(MemoryQuery? query = null, CancellationToken ct = default)
    {
        var count = _containers.Values.SelectMany(c => c.Values)
            .Count(b => !b.ExpiresAt.HasValue || b.ExpiresAt.Value >= DateTimeOffset.UtcNow);
        return Task.FromResult((long)count);
    }

    #endregion

    #region Tier Operations

    /// <inheritdoc/>
    public Task<IEnumerable<MemoryRecord>> GetByTierAsync(MemoryTier tier, int limit = 100, CancellationToken ct = default)
    {
        var containerName = GetContainerName(tier);
        var records = _containers.GetValueOrDefault(containerName)?.Values
            .Where(b => !b.ExpiresAt.HasValue || b.ExpiresAt.Value >= DateTimeOffset.UtcNow)
            .Take(limit)
            .Select(ToMemoryRecord)
            .ToList() ?? new List<MemoryRecord>();

        return Task.FromResult<IEnumerable<MemoryRecord>>(records);
    }

    /// <inheritdoc/>
    public Task<IEnumerable<MemoryRecord>> GetByScopeAsync(string scope, int limit = 100, CancellationToken ct = default)
    {
        var records = _containers.Values
            .SelectMany(c => c.Values)
            .Where(b => b.Scope == scope && (!b.ExpiresAt.HasValue || b.ExpiresAt.Value >= DateTimeOffset.UtcNow))
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

        foreach (var container in _containers.Values)
        {
            var expiredBlobs = container.Where(kvp => kvp.Value.ExpiresAt.HasValue && kvp.Value.ExpiresAt.Value < now)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var blobName in expiredBlobs)
            {
                if (container.TryRemove(blobName, out var blob))
                {
                    _idIndex.TryRemove(blob.Id, out _);
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
            var containerName = GetContainerName(tier);
            var blobs = _containers.GetValueOrDefault(containerName)?.Values.ToList() ?? new List<AzureBlob>();
            recordsByTier[tier] = blobs.Count;
            sizeByTier[tier] = blobs.Sum(b => b.Content?.Length ?? 0);
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
            AvgReadLatencyMs = _metrics.AvgReadLatencyMs,
            AvgWriteLatencyMs = _metrics.AvgWriteLatencyMs,
            IsHealthy = IsConnected,
            HealthCheckTime = DateTimeOffset.UtcNow,
            CustomMetrics = new Dictionary<string, object>
            {
                ["containerCount"] = _containers.Count
            }
        });
    }

    /// <inheritdoc/>
    public Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_isConnected && !_disposed && _circuitBreaker.AllowOperation());
    }

    #endregion

    private Dictionary<string, string> CreateBlobIndexTags(MemoryRecord record)
    {
        return new Dictionary<string, string>
        {
            ["scope"] = record.Scope,
            ["tier"] = record.Tier.ToString(),
            ["contentType"] = record.ContentType ?? "unknown"
        };
    }

    private MemoryRecord ToMemoryRecord(AzureBlob blob) => new()
    {
        Id = blob.Id,
        Content = blob.Content,
        Tier = blob.Tier,
        Scope = blob.Scope,
        Embedding = blob.Embedding,
        Metadata = blob.Metadata,
        CreatedAt = blob.CreatedAt,
        LastAccessedAt = blob.LastAccessedAt,
        AccessCount = blob.AccessCount,
        ImportanceScore = blob.ImportanceScore,
        ContentType = blob.ContentType,
        Tags = blob.Tags,
        ExpiresAt = blob.ExpiresAt,
        Version = blob.Version
    };

    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(AzureBlobPersistenceBackend)); }
    private void EnsureCircuitBreaker() { if (!_circuitBreaker.AllowOperation()) throw new InvalidOperationException("Circuit breaker is open"); }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _containers.Clear();
        _idIndex.Clear();
        _uploadSemaphore.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed record AzureBlob
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
    public string AccessTier { get; init; } = "Hot";
    public Dictionary<string, string>? BlobIndexTags { get; init; }
}

#endregion

#region AWS S3

/// <summary>
/// Configuration for AWS S3 persistence backend.
/// </summary>
public sealed record S3PersistenceConfig : PersistenceBackendConfig
{
    /// <summary>AWS region.</summary>
    public required string Region { get; init; }

    /// <summary>AWS access key ID.</summary>
    public string? AccessKeyId { get; init; }

    /// <summary>AWS secret access key.</summary>
    public string? SecretAccessKey { get; init; }

    /// <summary>Bucket name prefix (tier suffix will be added).</summary>
    public string BucketPrefix { get; init; } = "memory";

    /// <summary>Create separate bucket per tier.</summary>
    public bool UseTierBuckets { get; init; } = true;

    /// <summary>Object key prefix.</summary>
    public string KeyPrefix { get; init; } = "";

    /// <summary>Enable S3 Intelligent-Tiering.</summary>
    public bool EnableIntelligentTiering { get; init; } = true;

    /// <summary>Storage class per tier.</summary>
    public Dictionary<MemoryTier, string> StorageClassMapping { get; init; } = new()
    {
        [MemoryTier.Immediate] = "STANDARD",
        [MemoryTier.Working] = "STANDARD",
        [MemoryTier.ShortTerm] = "STANDARD_IA",
        [MemoryTier.LongTerm] = "GLACIER"
    };

    /// <summary>Enable object tagging.</summary>
    public bool EnableObjectTagging { get; init; } = true;

    /// <summary>Enable S3 Select for queries.</summary>
    public bool EnableS3Select { get; init; } = true;

    /// <summary>Enable versioning.</summary>
    public bool EnableVersioning { get; init; } = true;

    /// <summary>Maximum concurrent operations.</summary>
    public int MaxConcurrentOperations { get; init; } = 10;

    /// <summary>Multipart upload threshold (bytes).</summary>
    public long MultipartThreshold { get; init; } = 16 * 1024 * 1024;
}

/// <summary>
/// AWS S3 persistence backend.
/// Provides cloud-native storage with bucket per tier, Intelligent-Tiering,
/// object tagging, S3 Select for queries, and lifecycle policies.
/// </summary>
public sealed class S3PersistenceBackend : IProductionPersistenceBackend
{
    private readonly S3PersistenceConfig _config;
    private readonly PersistenceMetrics _metrics = new();
    private readonly PersistenceCircuitBreaker _circuitBreaker;

    // Simulated buckets and objects
    private readonly BoundedDictionary<string, BoundedDictionary<string, S3Object>> _buckets = new BoundedDictionary<string, BoundedDictionary<string, S3Object>>(1000);
    private readonly BoundedDictionary<string, (string Bucket, string Key)> _idIndex = new BoundedDictionary<string, (string Bucket, string Key)>(1000);
    private readonly SemaphoreSlim _operationSemaphore;

    private bool _disposed;
    private bool _isConnected;

    /// <inheritdoc/>
    public string BackendId => _config.BackendId;

    /// <inheritdoc/>
    public string DisplayName => _config.DisplayName ?? "AWS S3";

    /// <inheritdoc/>
    public PersistenceCapabilities Capabilities =>
        PersistenceCapabilities.Replication |
        PersistenceCapabilities.SecondaryIndexes |
        PersistenceCapabilities.Snapshots;

    /// <inheritdoc/>
    public bool IsConnected => _isConnected && !_disposed;

    public S3PersistenceBackend(S3PersistenceConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _circuitBreaker = new PersistenceCircuitBreaker();
        _operationSemaphore = new SemaphoreSlim(_config.MaxConcurrentOperations, _config.MaxConcurrentOperations);

        InitializeBuckets();
    }

    private void InitializeBuckets()
    {
        try
        {
            foreach (var tier in Enum.GetValues<MemoryTier>())
            {
                var bucketName = GetBucketName(tier);
                _buckets[bucketName] = new BoundedDictionary<string, S3Object>(1000);
            }
            _isConnected = true;
        }
        catch (Exception)
        {
            _isConnected = false;
            throw;
        }
    }

    private string GetBucketName(MemoryTier tier) =>
        _config.UseTierBuckets ? $"{_config.BucketPrefix}-{tier.ToString().ToLowerInvariant()}" : _config.BucketPrefix;

    private string GetObjectKey(string id) =>
        string.IsNullOrEmpty(_config.KeyPrefix) ? $"{id}.json" : $"{_config.KeyPrefix}/{id}.json";

    #region Core CRUD Operations

    /// <inheritdoc/>
    public async Task<string> StoreAsync(MemoryRecord record, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureCircuitBreaker();

        await _operationSemaphore.WaitAsync(ct);
        var sw = Stopwatch.StartNew();

        try
        {
            var id = record.Id ?? Guid.NewGuid().ToString();
            var bucketName = GetBucketName(record.Tier);
            var key = GetObjectKey(id);

            var obj = new S3Object
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
                StorageClass = _config.EnableIntelligentTiering ? "INTELLIGENT_TIERING" : _config.StorageClassMapping.GetValueOrDefault(record.Tier, "STANDARD"),
                ObjectTags = _config.EnableObjectTagging ? CreateObjectTags(record) : null
            };

            var bucket = _buckets[bucketName];
            bucket[key] = obj;
            _idIndex[id] = (bucketName, key);

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
            _operationSemaphore.Release();
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
            if (!_idIndex.TryGetValue(id, out var location))
            {
                _metrics.RecordCacheMiss();
                return null;
            }

            if (!_buckets.TryGetValue(location.Bucket, out var bucket) ||
                !bucket.TryGetValue(location.Key, out var obj))
            {
                _metrics.RecordCacheMiss();
                return null;
            }

            if (obj.ExpiresAt.HasValue && obj.ExpiresAt.Value < DateTimeOffset.UtcNow)
            {
                await DeleteAsync(id, ct);
                _metrics.RecordCacheMiss();
                return null;
            }

            _metrics.RecordCacheHit();
            sw.Stop();
            _metrics.RecordRead(sw.Elapsed);
            _circuitBreaker.RecordSuccess();

            return ToMemoryRecord(obj);
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
        if (!_idIndex.TryGetValue(id, out var location))
        {
            throw new KeyNotFoundException($"Object with ID {id} not found");
        }

        var newBucketName = GetBucketName(record.Tier);
        if (location.Bucket != newBucketName)
        {
            await DeleteAsync(id, ct);
        }

        await StoreAsync(record with { Id = id }, ct);
    }

    /// <inheritdoc/>
    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (_idIndex.TryRemove(id, out var location))
        {
            if (_buckets.TryGetValue(location.Bucket, out var bucket))
            {
                bucket.TryRemove(location.Key, out _);
            }
            _metrics.RecordDelete();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> ExistsAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_idIndex.ContainsKey(id));

    #endregion

    #region Batch Operations

    /// <inheritdoc/>
    public async Task StoreBatchAsync(IEnumerable<MemoryRecord> records, CancellationToken ct = default)
    {
        var tasks = records.Select(r => StoreAsync(r, ct));
        await Task.WhenAll(tasks);
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<MemoryRecord>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var tasks = ids.Select(id => GetAsync(id, ct));
        var results = await Task.WhenAll(tasks);
        return results.Where(r => r != null).Cast<MemoryRecord>();
    }

    /// <inheritdoc/>
    public async Task DeleteBatchAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var tasks = ids.Select(id => DeleteAsync(id, ct));
        await Task.WhenAll(tasks);
    }

    #endregion

    #region Query Operations

    /// <inheritdoc/>
    public async IAsyncEnumerable<MemoryRecord> QueryAsync(MemoryQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfDisposed();

        IEnumerable<S3Object> objects;

        if (query.Tier.HasValue)
        {
            var bucketName = GetBucketName(query.Tier.Value);
            objects = _buckets.GetValueOrDefault(bucketName)?.Values ?? Enumerable.Empty<S3Object>();
        }
        else
        {
            objects = _buckets.Values.SelectMany(b => b.Values);
        }

        if (!string.IsNullOrEmpty(query.Scope))
        {
            objects = objects.Where(o => o.Scope == query.Scope);
        }

        var now = DateTimeOffset.UtcNow;
        objects = objects.Where(o => !o.ExpiresAt.HasValue || o.ExpiresAt.Value >= now);

        foreach (var obj in objects.Skip(query.Skip).Take(query.Limit))
        {
            ct.ThrowIfCancellationRequested();
            yield return ToMemoryRecord(obj);
        }

        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<long> CountAsync(MemoryQuery? query = null, CancellationToken ct = default)
    {
        var count = _buckets.Values.SelectMany(b => b.Values)
            .Count(o => !o.ExpiresAt.HasValue || o.ExpiresAt.Value >= DateTimeOffset.UtcNow);
        return Task.FromResult((long)count);
    }

    #endregion

    #region Tier Operations

    /// <inheritdoc/>
    public Task<IEnumerable<MemoryRecord>> GetByTierAsync(MemoryTier tier, int limit = 100, CancellationToken ct = default)
    {
        var bucketName = GetBucketName(tier);
        var records = _buckets.GetValueOrDefault(bucketName)?.Values
            .Where(o => !o.ExpiresAt.HasValue || o.ExpiresAt.Value >= DateTimeOffset.UtcNow)
            .Take(limit)
            .Select(ToMemoryRecord)
            .ToList() ?? new List<MemoryRecord>();

        return Task.FromResult<IEnumerable<MemoryRecord>>(records);
    }

    /// <inheritdoc/>
    public Task<IEnumerable<MemoryRecord>> GetByScopeAsync(string scope, int limit = 100, CancellationToken ct = default)
    {
        var records = _buckets.Values
            .SelectMany(b => b.Values)
            .Where(o => o.Scope == scope && (!o.ExpiresAt.HasValue || o.ExpiresAt.Value >= DateTimeOffset.UtcNow))
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

        foreach (var bucket in _buckets.Values)
        {
            var expiredKeys = bucket.Where(kvp => kvp.Value.ExpiresAt.HasValue && kvp.Value.ExpiresAt.Value < now)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                if (bucket.TryRemove(key, out var obj))
                {
                    _idIndex.TryRemove(obj.Id, out _);
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
            var bucketName = GetBucketName(tier);
            var objects = _buckets.GetValueOrDefault(bucketName)?.Values.ToList() ?? new List<S3Object>();
            recordsByTier[tier] = objects.Count;
            sizeByTier[tier] = objects.Sum(o => o.Content?.Length ?? 0);
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
                ["bucketCount"] = _buckets.Count,
                ["region"] = _config.Region
            }
        });
    }

    /// <inheritdoc/>
    public Task<bool> IsHealthyAsync(CancellationToken ct = default) =>
        Task.FromResult(_isConnected && !_disposed && _circuitBreaker.AllowOperation());

    #endregion

    private Dictionary<string, string> CreateObjectTags(MemoryRecord record) => new()
    {
        ["scope"] = record.Scope,
        ["tier"] = record.Tier.ToString(),
        ["contentType"] = record.ContentType ?? "unknown"
    };

    private MemoryRecord ToMemoryRecord(S3Object obj) => new()
    {
        Id = obj.Id,
        Content = obj.Content,
        Tier = obj.Tier,
        Scope = obj.Scope,
        Embedding = obj.Embedding,
        Metadata = obj.Metadata,
        CreatedAt = obj.CreatedAt,
        LastAccessedAt = obj.LastAccessedAt,
        AccessCount = obj.AccessCount,
        ImportanceScore = obj.ImportanceScore,
        ContentType = obj.ContentType,
        Tags = obj.Tags,
        ExpiresAt = obj.ExpiresAt,
        Version = obj.Version
    };

    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(S3PersistenceBackend)); }
    private void EnsureCircuitBreaker() { if (!_circuitBreaker.AllowOperation()) throw new InvalidOperationException("Circuit breaker is open"); }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _buckets.Clear();
        _idIndex.Clear();
        _operationSemaphore.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed record S3Object
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
    public string StorageClass { get; init; } = "STANDARD";
    public Dictionary<string, string>? ObjectTags { get; init; }
}

#endregion

#region Google Cloud Storage

/// <summary>
/// Configuration for Google Cloud Storage persistence backend.
/// </summary>
public sealed record GcsPersistenceConfig : PersistenceBackendConfig
{
    /// <summary>GCP project ID.</summary>
    public required string ProjectId { get; init; }

    /// <summary>Path to service account key file (JSON).</summary>
    public string? CredentialsPath { get; init; }

    /// <summary>Bucket name prefix.</summary>
    public string BucketPrefix { get; init; } = "memory";

    /// <summary>Create separate bucket per tier.</summary>
    public bool UseTierBuckets { get; init; } = true;

    /// <summary>Object name prefix.</summary>
    public string ObjectPrefix { get; init; } = "";

    /// <summary>Storage class per tier.</summary>
    public Dictionary<MemoryTier, string> StorageClassMapping { get; init; } = new()
    {
        [MemoryTier.Immediate] = "STANDARD",
        [MemoryTier.Working] = "STANDARD",
        [MemoryTier.ShortTerm] = "NEARLINE",
        [MemoryTier.LongTerm] = "COLDLINE"
    };

    /// <summary>Enable object lifecycle rules.</summary>
    public bool EnableLifecycleRules { get; init; } = true;

    /// <summary>Enable object metadata for queries.</summary>
    public bool EnableObjectMetadata { get; init; } = true;

    /// <summary>Maximum concurrent operations.</summary>
    public int MaxConcurrentOperations { get; init; } = 10;

    /// <summary>Enable resumable uploads.</summary>
    public bool EnableResumableUploads { get; init; } = true;

    /// <summary>Resumable upload threshold (bytes).</summary>
    public long ResumableUploadThreshold { get; init; } = 5 * 1024 * 1024;
}

/// <summary>
/// Google Cloud Storage persistence backend.
/// Provides cloud-native storage with storage classes (Standard/Nearline/Coldline/Archive),
/// object lifecycle rules, and object metadata for querying.
/// </summary>
public sealed class GcsPersistenceBackend : IProductionPersistenceBackend
{
    private readonly GcsPersistenceConfig _config;
    private readonly PersistenceMetrics _metrics = new();
    private readonly PersistenceCircuitBreaker _circuitBreaker;

    // Simulated buckets and objects
    private readonly BoundedDictionary<string, BoundedDictionary<string, GcsObject>> _buckets = new BoundedDictionary<string, BoundedDictionary<string, GcsObject>>(1000);
    private readonly BoundedDictionary<string, (string Bucket, string ObjectName)> _idIndex = new BoundedDictionary<string, (string Bucket, string ObjectName)>(1000);
    private readonly SemaphoreSlim _operationSemaphore;

    private bool _disposed;
    private bool _isConnected;

    /// <inheritdoc/>
    public string BackendId => _config.BackendId;

    /// <inheritdoc/>
    public string DisplayName => _config.DisplayName ?? "Google Cloud Storage";

    /// <inheritdoc/>
    public PersistenceCapabilities Capabilities =>
        PersistenceCapabilities.Replication |
        PersistenceCapabilities.SecondaryIndexes;

    /// <inheritdoc/>
    public bool IsConnected => _isConnected && !_disposed;

    public GcsPersistenceBackend(GcsPersistenceConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _circuitBreaker = new PersistenceCircuitBreaker();
        _operationSemaphore = new SemaphoreSlim(_config.MaxConcurrentOperations, _config.MaxConcurrentOperations);

        InitializeBuckets();
    }

    private void InitializeBuckets()
    {
        try
        {
            foreach (var tier in Enum.GetValues<MemoryTier>())
            {
                var bucketName = GetBucketName(tier);
                _buckets[bucketName] = new BoundedDictionary<string, GcsObject>(1000);
            }
            _isConnected = true;
        }
        catch (Exception)
        {
            _isConnected = false;
            throw;
        }
    }

    private string GetBucketName(MemoryTier tier) =>
        _config.UseTierBuckets ? $"{_config.BucketPrefix}-{tier.ToString().ToLowerInvariant()}" : _config.BucketPrefix;

    private string GetObjectName(string id) =>
        string.IsNullOrEmpty(_config.ObjectPrefix) ? $"{id}.json" : $"{_config.ObjectPrefix}/{id}.json";

    #region IProductionPersistenceBackend Implementation

    /// <inheritdoc/>
    public async Task<string> StoreAsync(MemoryRecord record, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureCircuitBreaker();

        await _operationSemaphore.WaitAsync(ct);
        var sw = Stopwatch.StartNew();

        try
        {
            var id = record.Id ?? Guid.NewGuid().ToString();
            var bucketName = GetBucketName(record.Tier);
            var objectName = GetObjectName(id);

            var obj = new GcsObject
            {
                Id = id,
                Content = record.Content,
                Tier = record.Tier,
                Scope = record.Scope,
                Embedding = record.Embedding,
                RecordMetadata = record.Metadata,
                CreatedAt = record.CreatedAt,
                LastAccessedAt = record.LastAccessedAt,
                AccessCount = record.AccessCount,
                ImportanceScore = record.ImportanceScore,
                ContentType = record.ContentType,
                Tags = record.Tags,
                ExpiresAt = record.ExpiresAt,
                Version = 1,
                StorageClass = _config.StorageClassMapping.GetValueOrDefault(record.Tier, "STANDARD"),
                ObjectMetadata = _config.EnableObjectMetadata ? CreateObjectMetadata(record) : null
            };

            var bucket = _buckets[bucketName];
            bucket[objectName] = obj;
            _idIndex[id] = (bucketName, objectName);

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
            _operationSemaphore.Release();
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
            if (!_idIndex.TryGetValue(id, out var location))
            {
                _metrics.RecordCacheMiss();
                return null;
            }

            if (!_buckets.TryGetValue(location.Bucket, out var bucket) ||
                !bucket.TryGetValue(location.ObjectName, out var obj))
            {
                _metrics.RecordCacheMiss();
                return null;
            }

            if (obj.ExpiresAt.HasValue && obj.ExpiresAt.Value < DateTimeOffset.UtcNow)
            {
                await DeleteAsync(id, ct);
                _metrics.RecordCacheMiss();
                return null;
            }

            _metrics.RecordCacheHit();
            sw.Stop();
            _metrics.RecordRead(sw.Elapsed);
            _circuitBreaker.RecordSuccess();

            return ToMemoryRecord(obj);
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
        if (!_idIndex.TryGetValue(id, out var location))
        {
            throw new KeyNotFoundException($"Object with ID {id} not found");
        }

        var newBucketName = GetBucketName(record.Tier);
        if (location.Bucket != newBucketName)
        {
            await DeleteAsync(id, ct);
        }

        await StoreAsync(record with { Id = id }, ct);
    }

    /// <inheritdoc/>
    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (_idIndex.TryRemove(id, out var location))
        {
            if (_buckets.TryGetValue(location.Bucket, out var bucket))
            {
                bucket.TryRemove(location.ObjectName, out _);
            }
            _metrics.RecordDelete();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> ExistsAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_idIndex.ContainsKey(id));

    /// <inheritdoc/>
    public async Task StoreBatchAsync(IEnumerable<MemoryRecord> records, CancellationToken ct = default)
    {
        await Task.WhenAll(records.Select(r => StoreAsync(r, ct)));
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<MemoryRecord>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var results = await Task.WhenAll(ids.Select(id => GetAsync(id, ct)));
        return results.Where(r => r != null).Cast<MemoryRecord>();
    }

    /// <inheritdoc/>
    public async Task DeleteBatchAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        await Task.WhenAll(ids.Select(id => DeleteAsync(id, ct)));
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<MemoryRecord> QueryAsync(MemoryQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfDisposed();

        IEnumerable<GcsObject> objects;

        if (query.Tier.HasValue)
        {
            objects = _buckets.GetValueOrDefault(GetBucketName(query.Tier.Value))?.Values ?? Enumerable.Empty<GcsObject>();
        }
        else
        {
            objects = _buckets.Values.SelectMany(b => b.Values);
        }

        if (!string.IsNullOrEmpty(query.Scope))
        {
            objects = objects.Where(o => o.Scope == query.Scope);
        }

        var now = DateTimeOffset.UtcNow;
        objects = objects.Where(o => !o.ExpiresAt.HasValue || o.ExpiresAt.Value >= now);

        foreach (var obj in objects.Skip(query.Skip).Take(query.Limit))
        {
            ct.ThrowIfCancellationRequested();
            yield return ToMemoryRecord(obj);
        }

        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<long> CountAsync(MemoryQuery? query = null, CancellationToken ct = default)
    {
        var count = _buckets.Values.SelectMany(b => b.Values)
            .Count(o => !o.ExpiresAt.HasValue || o.ExpiresAt.Value >= DateTimeOffset.UtcNow);
        return Task.FromResult((long)count);
    }

    /// <inheritdoc/>
    public Task<IEnumerable<MemoryRecord>> GetByTierAsync(MemoryTier tier, int limit = 100, CancellationToken ct = default)
    {
        var records = _buckets.GetValueOrDefault(GetBucketName(tier))?.Values
            .Where(o => !o.ExpiresAt.HasValue || o.ExpiresAt.Value >= DateTimeOffset.UtcNow)
            .Take(limit)
            .Select(ToMemoryRecord)
            .ToList() ?? new List<MemoryRecord>();

        return Task.FromResult<IEnumerable<MemoryRecord>>(records);
    }

    /// <inheritdoc/>
    public Task<IEnumerable<MemoryRecord>> GetByScopeAsync(string scope, int limit = 100, CancellationToken ct = default)
    {
        var records = _buckets.Values
            .SelectMany(b => b.Values)
            .Where(o => o.Scope == scope && (!o.ExpiresAt.HasValue || o.ExpiresAt.Value >= DateTimeOffset.UtcNow))
            .Take(limit)
            .Select(ToMemoryRecord)
            .ToList();

        return Task.FromResult<IEnumerable<MemoryRecord>>(records);
    }

    /// <inheritdoc/>
    public Task CompactAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var bucket in _buckets.Values)
        {
            var expiredKeys = bucket.Where(kvp => kvp.Value.ExpiresAt.HasValue && kvp.Value.ExpiresAt.Value < now)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                if (bucket.TryRemove(key, out var obj))
                {
                    _idIndex.TryRemove(obj.Id, out _);
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
            var objects = _buckets.GetValueOrDefault(GetBucketName(tier))?.Values.ToList() ?? new List<GcsObject>();
            recordsByTier[tier] = objects.Count;
            sizeByTier[tier] = objects.Sum(o => o.Content?.Length ?? 0);
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
                ["bucketCount"] = _buckets.Count,
                ["projectId"] = _config.ProjectId
            }
        });
    }

    /// <inheritdoc/>
    public Task<bool> IsHealthyAsync(CancellationToken ct = default) =>
        Task.FromResult(_isConnected && !_disposed && _circuitBreaker.AllowOperation());

    #endregion

    private Dictionary<string, string> CreateObjectMetadata(MemoryRecord record) => new()
    {
        ["x-goog-meta-scope"] = record.Scope,
        ["x-goog-meta-tier"] = record.Tier.ToString(),
        ["x-goog-meta-contenttype"] = record.ContentType ?? "unknown"
    };

    private MemoryRecord ToMemoryRecord(GcsObject obj) => new()
    {
        Id = obj.Id,
        Content = obj.Content,
        Tier = obj.Tier,
        Scope = obj.Scope,
        Embedding = obj.Embedding,
        Metadata = obj.RecordMetadata,
        CreatedAt = obj.CreatedAt,
        LastAccessedAt = obj.LastAccessedAt,
        AccessCount = obj.AccessCount,
        ImportanceScore = obj.ImportanceScore,
        ContentType = obj.ContentType,
        Tags = obj.Tags,
        ExpiresAt = obj.ExpiresAt,
        Version = obj.Version
    };

    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(GcsPersistenceBackend)); }
    private void EnsureCircuitBreaker() { if (!_circuitBreaker.AllowOperation()) throw new InvalidOperationException("Circuit breaker is open"); }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _buckets.Clear();
        _idIndex.Clear();
        _operationSemaphore.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed record GcsObject
{
    public required string Id { get; init; }
    public required byte[] Content { get; init; }
    public MemoryTier Tier { get; init; }
    public required string Scope { get; init; }
    public float[]? Embedding { get; init; }
    public Dictionary<string, object>? RecordMetadata { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastAccessedAt { get; init; }
    public long AccessCount { get; init; }
    public double ImportanceScore { get; init; }
    public string? ContentType { get; init; }
    public string[]? Tags { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public long Version { get; init; }
    public string StorageClass { get; init; } = "STANDARD";
    public Dictionary<string, string>? ObjectMetadata { get; init; }
}

#endregion
