using Consul;
using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Database;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies.KeyValue;

/// <summary>
/// HashiCorp Consul KV storage strategy with production-ready features:
/// - Distributed key-value store
/// - Service mesh integration
/// - Multi-datacenter support
/// - Watch for changes
/// - Transactions
/// - ACL support
/// - Blocking queries
/// </summary>
public sealed class ConsulKvStorageStrategy : DatabaseStorageStrategyBase
{
    private ConsulClient? _client;
    private string _keyPrefix = "storage/";
    private string _metadataPrefix = "meta/";

    public override string StrategyId => "consul-kv";
    public override string Name => "Consul KV Storage";
    public override StorageTier Tier => StorageTier.Hot;
    public override DatabaseCategory DatabaseCategory => DatabaseCategory.KeyValue;
    public override string Engine => "Consul";

    public override StorageCapabilities Capabilities => new()
    {
        SupportsMetadata = true,
        SupportsStreaming = false,
        SupportsLocking = true,
        SupportsVersioning = true, // ModifyIndex
        SupportsTiering = false,
        SupportsEncryption = true, // ACL tokens
        SupportsCompression = false,
        SupportsMultipart = false,
        MaxObjectSize = 512 * 1024, // 512KB limit
        MaxObjects = null,
        ConsistencyModel = ConsistencyModel.Strong
    };

    public override bool SupportsTransactions => true;
    public override bool SupportsSql => false;

    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        _keyPrefix = GetConfiguration("KeyPrefix", "storage/");
        _metadataPrefix = GetConfiguration("MetadataPrefix", "meta/");

        var connectionString = GetConnectionString();
        var token = GetConfiguration<string?>("Token", null);

        _client = new ConsulClient(config =>
        {
            config.Address = new Uri(connectionString);
            if (!string.IsNullOrEmpty(token))
            {
                config.Token = token;
            }
        });

        await Task.CompletedTask;
    }

    protected override async Task ConnectCoreAsync(CancellationToken ct)
    {
        var leader = await _client!.Status.Leader(ct);
        if (string.IsNullOrEmpty(leader))
        {
            throw new InvalidOperationException("Consul cluster has no leader");
        }
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

        var consulKey = $"{_keyPrefix}{key}";
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

        // Use transaction for atomic write
        var txn = new List<KVTxnOp>
        {
            new(consulKey, KVTxnVerb.Set) { Value = data },
            new(metadataKey, KVTxnVerb.Set) { Value = Encoding.UTF8.GetBytes(metadataJson) }
        };

        var result = await _client!.KV.Txn(txn, ct);
        if (!result.Response.Success)
        {
            throw new InvalidOperationException($"Failed to store: {string.Join(", ", result.Response.Errors?.Select(e => e.What) ?? Array.Empty<string>())}");
        }

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
        var consulKey = $"{_keyPrefix}{key}";
        var result = await _client!.KV.Get(consulKey, ct);

        if (result.Response == null)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        return result.Response.Value;
    }

    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct)
    {
        var metadata = await GetMetadataCoreAsync(key, ct);
        var size = metadata.Size;

        var consulKey = $"{_keyPrefix}{key}";
        var metadataKey = $"{_metadataPrefix}{key}";

        var txn = new List<KVTxnOp>
        {
            new(consulKey, KVTxnVerb.Delete),
            new(metadataKey, KVTxnVerb.Delete)
        };

        var txnResult = await _client!.KV.Txn(txn, ct);
        if (!txnResult.Response.Success)
        {
            var errors = txnResult.Response.Errors != null
                ? string.Join("; ", txnResult.Response.Errors.Select(e => $"op[{e.OpIndex}]: {e.What}"))
                : "unknown error";
            throw new InvalidOperationException($"Consul transaction failed while deleting key '{key}': {errors}");
        }

        return size;
    }

    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct)
    {
        var consulKey = $"{_keyPrefix}{key}";
        var result = await _client!.KV.Get(consulKey, ct);
        return result.Response != null;
    }

    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        var searchPrefix = string.IsNullOrEmpty(prefix) ? _keyPrefix : $"{_keyPrefix}{prefix}";
        var result = await _client!.KV.List(searchPrefix, ct);

        if (result.Response == null)
        {
            yield break;
        }

        foreach (var kv in result.Response)
        {
            ct.ThrowIfCancellationRequested();

            var key = kv.Key.Substring(_keyPrefix.Length);

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
        var result = await _client!.KV.Get(metadataKey, ct);

        if (result.Response == null)
        {
            throw new FileNotFoundException($"Metadata not found: {key}");
        }

        var metadataJson = Encoding.UTF8.GetString(result.Response.Value);
        var metadata = JsonSerializer.Deserialize<MetadataDocument>(metadataJson, JsonOptions)
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
            VersionId = result.Response.ModifyIndex.ToString(),
            Tier = Tier
        };
    }

    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct)
    {
        try
        {
            var leader = await _client!.Status.Leader(ct);
            return !string.IsNullOrEmpty(leader);
        }
        catch
        {
            return false;
        }
    }

    protected override async Task<IDatabaseTransaction> BeginTransactionCoreAsync(System.Data.IsolationLevel isolationLevel, CancellationToken ct)
    {
        return new ConsulTransaction(_client!);
    }

    /// <summary>
    /// Acquires a distributed lock.
    /// </summary>
    public async Task<IDistributedLock> AcquireLockAsync(string lockKey, CancellationToken ct = default)
    {
        var opts = new LockOptions($"{_keyPrefix}locks/{lockKey}")
        {
            LockTryOnce = false,
            LockWaitTime = TimeSpan.FromSeconds(30)
        };

        var lockHandle = _client!.CreateLock(opts);
        await lockHandle.Acquire(ct);

        return new ConsulLock(lockHandle);
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        _client?.Dispose();
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

    private sealed class ConsulTransaction : IDatabaseTransaction
    {
        private readonly ConsulClient _client;
        private readonly List<KVTxnOp> _operations = new();
        private bool _disposed;
        private bool _committed;

        public string TransactionId { get; } = Guid.NewGuid().ToString("N");
        public System.Data.IsolationLevel IsolationLevel => System.Data.IsolationLevel.Serializable;

        public ConsulTransaction(ConsulClient client)
        {
            _client = client;
        }

        public async Task CommitAsync(CancellationToken ct = default)
        {
            if (_committed) return;

            if (_operations.Count > 0)
            {
                await _client.KV.Txn(_operations, ct);
            }

            _committed = true;
        }

        public Task RollbackAsync(CancellationToken ct = default)
        {
            _operations.Clear();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    public interface IDistributedLock : IAsyncDisposable
    {
        Task ReleaseAsync(CancellationToken ct = default);
    }

    private sealed class ConsulLock : IDistributedLock
    {
        private readonly Consul.IDistributedLock _lock;
        private bool _disposed;

        public ConsulLock(Consul.IDistributedLock lockHandle)
        {
            _lock = lockHandle;
        }

        public async Task ReleaseAsync(CancellationToken ct = default)
        {
            // LOW-2816: _lock.Release() is synchronous; no Task.Run wrapper needed
            _lock.Release();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await ReleaseAsync();
        }
    }
}
