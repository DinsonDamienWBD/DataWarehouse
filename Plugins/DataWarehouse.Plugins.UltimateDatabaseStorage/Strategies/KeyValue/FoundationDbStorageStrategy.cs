using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Database;
using FoundationDB.Client;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies.KeyValue;

/// <summary>
/// FoundationDB storage strategy with production-ready features:
/// - ACID transactions at scale
/// - Multi-model via layers
/// - Linear scalability
/// - Fault tolerance
/// - Strong consistency
/// - Ordered key-value store
/// - Cluster-wide transactions
/// </summary>
public sealed class FoundationDbStorageStrategy : DatabaseStorageStrategyBase
{
    private IFdbDatabase? _db;
    private Slice _dataPrefix;
    private Slice _metadataPrefix;
    private string _clusterFile = "";

    private Slice DataKey(string key) => _dataPrefix + Slice.FromString(key);
    private Slice MetadataKey(string key) => _metadataPrefix + Slice.FromString(key);

    public override string StrategyId => "foundationdb";
    public override string Name => "FoundationDB Storage";
    public override StorageTier Tier => StorageTier.Hot;
    public override DatabaseCategory DatabaseCategory => DatabaseCategory.KeyValue;
    public override string Engine => "FoundationDB";

    public override StorageCapabilities Capabilities => new()
    {
        SupportsMetadata = true,
        SupportsStreaming = false,
        SupportsLocking = true, // Via transactions
        SupportsVersioning = true, // Via versionstamps
        SupportsTiering = false,
        SupportsEncryption = true,
        SupportsCompression = false,
        SupportsMultipart = false,
        MaxObjectSize = 10L * 1024, // 10KB value limit
        MaxObjects = null,
        ConsistencyModel = ConsistencyModel.Strong
    };

    public override bool SupportsTransactions => true;
    public override bool SupportsSql => false;

    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        _clusterFile = GetConfiguration("ClusterFile", "");

        // Initialize FDB
        Fdb.Start(Fdb.GetDefaultApiVersion());

        await Task.CompletedTask;
    }

    protected override async Task ConnectCoreAsync(CancellationToken ct)
    {
        _db = await Fdb.OpenAsync(new FdbConnectionOptions
        {
            ClusterFile = string.IsNullOrEmpty(_clusterFile) ? null : _clusterFile
        }, ct);

        // Create key prefixes
        _dataPrefix = Slice.FromString("data/");
        _metadataPrefix = Slice.FromString("metadata/");
        await Task.CompletedTask;
    }

    protected override async Task DisconnectCoreAsync(CancellationToken ct)
    {
        _db?.Dispose();
        _db = null;
        _dataPrefix = Slice.Empty;
        _metadataPrefix = Slice.Empty;
        await Task.CompletedTask;
    }

    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var etag = GenerateETag(data);
        var contentType = GetContentType(key);

        var metadataDoc = new MetadataDocument
        {
            Size = data.LongLength,
            ContentType = contentType,
            ETag = etag,
            CustomMetadata = metadata?.ToDictionary(k => k.Key, v => v.Value),
            CreatedAt = now,
            ModifiedAt = now
        };

        var metadataJson = JsonSerializer.SerializeToUtf8Bytes(metadataDoc, JsonOptions);
        var keyBytes = Encoding.UTF8.GetBytes(key);

        await _db!.WriteAsync(tr =>
        {
            // For large data, use chunking
            if (data.Length <= 10000)
            {
                tr.Set(DataKey(key), data);
            }
            else
            {
                // Store in chunks
                var chunkSize = 10000;
                var chunks = (data.Length + chunkSize - 1) / chunkSize;

                for (int i = 0; i < chunks; i++)
                {
                    var offset = i * chunkSize;
                    var length = Math.Min(chunkSize, data.Length - offset);
                    var chunk = new byte[length];
                    Array.Copy(data, offset, chunk, 0, length);

                    tr.Set(DataKey($"{key}:chunk:{i:D8}"), chunk);
                }

                // Store chunk count
                tr.Set(DataKey($"{key}:chunks"), BitConverter.GetBytes(chunks));
            }

            tr.Set(MetadataKey(key), metadataJson);
        }, ct);

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
        var keyBytes = Encoding.UTF8.GetBytes(key);

        return await _db!.ReadAsync(async tr =>
        {
            // Check for chunked data
            var countSlice = await tr.GetAsync(DataKey($"{key}:chunks"));

            if (countSlice.IsPresent)
            {
                var chunks = BitConverter.ToInt32(countSlice.ToArray(), 0);
                using var ms = new MemoryStream();

                for (int i = 0; i < chunks; i++)
                {
                    var chunkSlice = await tr.GetAsync(DataKey($"{key}:chunk:{i:D8}"));
                    if (chunkSlice.IsPresent)
                    {
                        ms.Write(chunkSlice.ToArray());
                    }
                }

                return ms.ToArray();
            }

            var slice = await tr.GetAsync(DataKey(key));
            if (!slice.IsPresent)
            {
                throw new FileNotFoundException($"Object not found: {key}");
            }

            return slice.ToArray();
        }, ct);
    }

    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct)
    {
        var metadata = await GetMetadataCoreAsync(key, ct);
        var size = metadata.Size;

        var keyBytes = Encoding.UTF8.GetBytes(key);

        await _db!.WriteAsync(tr =>
        {
            // Delete main key
            tr.Clear(DataKey(key));

            // Delete chunks if any
            tr.Clear(DataKey($"{key}:chunks"));

            // Clear chunk range
            var startKey = DataKey($"{key}:chunk:");
            var endKey = DataKey($"{key}:chunk:\uffff");
            tr.ClearRange(startKey, endKey);

            // Delete metadata
            tr.Clear(MetadataKey(key));
        }, ct);

        return size;
    }

    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);

        return await _db!.ReadAsync(async tr =>
        {
            var slice = await tr.GetAsync(MetadataKey(key));
            return slice.IsPresent;
        }, ct);
    }

    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        var startKey = string.IsNullOrEmpty(prefix)
            ? MetadataKey("")
            : MetadataKey(prefix);

        var results = await _db!.ReadAsync(async tr =>
        {
            var items = new List<KeyValuePair<Slice, Slice>>();
            await foreach (var kv in tr.GetRange(KeyRange.StartsWith(startKey)))
            {
                items.Add(kv);
            }
            return items;
        }, ct);

        foreach (var kv in results)
        {
            ct.ThrowIfCancellationRequested();

            // Extract key by removing prefix
            var keyString = kv.Key.ToString();
            var key = keyString.StartsWith("metadata/") ? keyString.Substring(9) : keyString;

            if (!string.IsNullOrEmpty(prefix) && !key.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var metadata = JsonSerializer.Deserialize<MetadataDocument>(kv.Value.ToArray(), JsonOptions);
            if (metadata == null) continue;

            yield return new StorageObjectMetadata
            {
                Key = key,
                Size = metadata.Size,
                ContentType = metadata.ContentType,
                ETag = metadata.ETag,
                CustomMetadata = metadata.CustomMetadata as IReadOnlyDictionary<string, string>,
                Created = metadata.CreatedAt,
                Modified = metadata.ModifiedAt,
                Tier = Tier
            };
        }
    }

    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);

        var metadataBytes = await _db!.ReadAsync(async tr =>
        {
            var slice = await tr.GetAsync(MetadataKey(key));
            if (!slice.IsPresent)
            {
                throw new FileNotFoundException($"Object not found: {key}");
            }
            return slice.ToArray();
        }, ct);

        var metadata = JsonSerializer.Deserialize<MetadataDocument>(metadataBytes, JsonOptions)
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
            await _db!.ReadAsync(tr => tr.GetAsync(_metadataPrefix), ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    protected override async Task<IDatabaseTransaction> BeginTransactionCoreAsync(System.Data.IsolationLevel isolationLevel, CancellationToken ct)
    {
        var tr = _db!.BeginTransaction(ct);
        return new FdbTransaction(tr);
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        _db?.Dispose();
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

    private sealed class FdbTransaction : IDatabaseTransaction
    {
        private readonly IFdbTransaction _transaction;
        private bool _disposed;

        public string TransactionId { get; } = Guid.NewGuid().ToString("N");
        public System.Data.IsolationLevel IsolationLevel => System.Data.IsolationLevel.Serializable;

        public FdbTransaction(IFdbTransaction transaction)
        {
            _transaction = transaction;
        }

        public async Task CommitAsync(CancellationToken ct = default)
        {
            await _transaction.CommitAsync();
        }

        public Task RollbackAsync(CancellationToken ct = default)
        {
            _transaction.Cancel();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            _transaction.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
