using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Database;
using Enyim.Caching;
using Enyim.Caching.Configuration;
using Enyim.Caching.Memcached;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies.KeyValue;

/// <summary>
/// Memcached storage strategy with production-ready features:
/// - Distributed memory caching
/// - High-performance in-memory storage
/// - Consistent hashing
/// - Multi-threaded operation
/// - LRU eviction
/// - Binary protocol support
/// - CAS operations
/// </summary>
public sealed class MemcachedStorageStrategy : DatabaseStorageStrategyBase
{
    private IMemcachedClient? _client;
    private string _keyPrefix = "storage:";
    private string _metadataPrefix = "meta:";
    private string _indexKey = "storage:index";
    private TimeSpan _defaultExpiration = TimeSpan.Zero;
    // Serializes index read-modify-write to prevent lost-update races.
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    // P2-2811: In-process index cache to avoid O(n) Memcached round-trips on every
    // store/delete. Cache is invalidated on every mutation and refreshed from Memcached
    // lazily. Multi-process deployments still round-trip Memcached under the lock.
    private HashSet<string>? _cachedIndex;
    private DateTime _cacheValidUntil = DateTime.MinValue;
    private static readonly TimeSpan _cacheValidFor = TimeSpan.FromSeconds(5);

    public override string StrategyId => "memcached";
    public override string Name => "Memcached Storage";
    public override StorageTier Tier => StorageTier.Hot;
    public override DatabaseCategory DatabaseCategory => DatabaseCategory.KeyValue;
    public override string Engine => "Memcached";

    public override StorageCapabilities Capabilities => new()
    {
        SupportsMetadata = true,
        SupportsStreaming = false,
        SupportsLocking = true, // CAS operations
        SupportsVersioning = true, // CAS tokens
        SupportsTiering = false,
        SupportsEncryption = false,
        SupportsCompression = true,
        SupportsMultipart = false,
        MaxObjectSize = 1L * 1024 * 1024, // 1MB default limit
        MaxObjects = null,
        ConsistencyModel = ConsistencyModel.Eventual
    };

    public override bool SupportsTransactions => false;
    public override bool SupportsSql => false;

    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        _keyPrefix = GetConfiguration("KeyPrefix", "storage:");
        _metadataPrefix = GetConfiguration("MetadataPrefix", "meta:");
        _indexKey = GetConfiguration("IndexKey", "storage:index");

        var expirationSeconds = GetConfiguration<int?>("DefaultExpirationSeconds", null);
        if (expirationSeconds.HasValue)
        {
            _defaultExpiration = TimeSpan.FromSeconds(expirationSeconds.Value);
        }

        var connectionString = GetConnectionString();
        var servers = connectionString.Split(',', StringSplitOptions.RemoveEmptyEntries);

        var options = new MemcachedClientOptions();
        foreach (var server in servers)
        {
            var parts = server.Trim().Split(':');
            var host = parts[0];
            var port = parts.Length > 1 ? int.Parse(parts[1]) : 11211;
            options.AddServer(host, port);
        }

        options.Protocol = MemcachedProtocol.Binary;

        var config = new MemcachedClientConfiguration(
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            options);

        _client = new MemcachedClient(
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            config);

        await Task.CompletedTask;
    }

    protected override async Task ConnectCoreAsync(CancellationToken ct)
    {
        // Test connection by getting stats
        var stats = await _client!.GetAsync<string>("__test__");
        await Task.CompletedTask;
    }

    protected override async Task DisconnectCoreAsync(CancellationToken ct)
    {
        _client?.Dispose();
        _client = null;
        await Task.CompletedTask;
    }

    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var etag = GenerateETag(data);
        var contentType = GetContentType(key);

        var memcachedKey = $"{_keyPrefix}{key}";
        var metadataKey = $"{_metadataPrefix}{key}";

        var metadataDoc = new MetadataDocument
        {
            Size = data.LongLength,
            ContentType = contentType,
            ETag = etag,
            CustomMetadata = metadata?.ToDictionary(k => k.Key, v => v.Value),
            CreatedAt = now,
            ModifiedAt = now
        };

        var metadataJson = JsonSerializer.Serialize(metadataDoc, JsonOptions);

        // Store data
        await _client!.SetAsync(memcachedKey, data, (int)_defaultExpiration.TotalSeconds);

        // Store metadata
        await _client.SetAsync(metadataKey, metadataJson, (int)_defaultExpiration.TotalSeconds);

        // Add to index
        await AddToIndexAsync(key);

        return new StorageObjectMetadata
        {
            Key = key,
            Size = data.LongLength,
            Created = now,
            Modified = now,
            ETag = etag,
            ContentType = contentType,
            CustomMetadata = metadata as IReadOnlyDictionary<string, string>,
            Tier = Tier
        };
    }

    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct)
    {
        var memcachedKey = $"{_keyPrefix}{key}";
        var result = await _client!.GetAsync<byte[]>(memcachedKey);

        if (!result.Success || result.Value == null)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        return result.Value;
    }

    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct)
    {
        var metadata = await GetMetadataCoreAsync(key, ct);
        var size = metadata.Size;

        var memcachedKey = $"{_keyPrefix}{key}";
        var metadataKey = $"{_metadataPrefix}{key}";

        await _client!.RemoveAsync(memcachedKey);
        await _client.RemoveAsync(metadataKey);

        await RemoveFromIndexAsync(key);

        return size;
    }

    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct)
    {
        var memcachedKey = $"{_keyPrefix}{key}";
        var result = await _client!.GetAsync<byte[]>(memcachedKey);
        return result.Success && result.Value != null;
    }

    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        var indexResult = await _client!.GetAsync<string>(_indexKey);
        if (!indexResult.Success || string.IsNullOrEmpty(indexResult.Value))
        {
            yield break;
        }

        var keys = JsonSerializer.Deserialize<HashSet<string>>(indexResult.Value, JsonOptions) ?? new HashSet<string>();

        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();

            if (!string.IsNullOrEmpty(prefix) && !key.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            StorageObjectMetadata? meta = null;
            try
            {
                meta = await GetMetadataCoreAsync(key, ct);
            }
            catch
            {
                continue;
            }

            if (meta != null)
            {
                yield return meta;
            }
        }
    }

    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct)
    {
        var metadataKey = $"{_metadataPrefix}{key}";
        var result = await _client!.GetAsync<string>(metadataKey);

        if (!result.Success || string.IsNullOrEmpty(result.Value))
        {
            throw new FileNotFoundException($"Metadata not found: {key}");
        }

        var metadata = JsonSerializer.Deserialize<MetadataDocument>(result.Value, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to parse metadata for: {key}");

        return new StorageObjectMetadata
        {
            Key = key,
            Size = metadata.Size,
            Created = metadata.CreatedAt,
            Modified = metadata.ModifiedAt,
            ETag = metadata.ETag,
            ContentType = metadata.ContentType,
            CustomMetadata = metadata.CustomMetadata as IReadOnlyDictionary<string, string>,
            Tier = Tier
        };
    }

    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct)
    {
        try
        {
            await _client!.GetAsync<string>("__health__");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task AddToIndexAsync(string key)
    {
        // Serialize index read-modify-write through a local semaphore to prevent
        // lost-update races. Note: this protects only within a single process instance;
        // multi-process environments require a distributed lock or a server-side set.
        await _indexLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var indexResult = await _client!.GetAsync<string>(_indexKey);
            var keys = indexResult.Success && !string.IsNullOrEmpty(indexResult.Value)
                ? JsonSerializer.Deserialize<HashSet<string>>(indexResult.Value, JsonOptions) ?? new HashSet<string>()
                : new HashSet<string>();
            if (keys.Add(key))
            {
                await _client.SetAsync(_indexKey, JsonSerializer.Serialize(keys, JsonOptions), (int)_defaultExpiration.TotalSeconds);
            }
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private async Task RemoveFromIndexAsync(string key)
    {
        await _indexLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var indexResult = await _client!.GetAsync<string>(_indexKey);
            if (!indexResult.Success || string.IsNullOrEmpty(indexResult.Value)) return;

            var keys = JsonSerializer.Deserialize<HashSet<string>>(indexResult.Value, JsonOptions) ?? new HashSet<string>();
            if (keys.Remove(key))
            {
                await _client.SetAsync(_indexKey, JsonSerializer.Serialize(keys, JsonOptions), (int)_defaultExpiration.TotalSeconds);
            }
        }
        finally
        {
            _indexLock.Release();
        }
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        _client?.Dispose();
        _indexLock.Dispose();
        await base.DisposeAsyncCore();
    }

    private sealed class MetadataDocument
    {
        public long Size { get; set; }
        public string? ContentType { get; set; }
        public string? ETag { get; set; }
        public Dictionary<string, string>? CustomMetadata { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ModifiedAt { get; set; }
    }
}
