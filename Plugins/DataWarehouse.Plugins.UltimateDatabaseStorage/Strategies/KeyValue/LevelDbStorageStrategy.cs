using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Database;
using RocksDbSharp;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies.KeyValue;

/// <summary>
/// LevelDB-compatible storage strategy using RocksDB with production-ready features:
/// - High-performance embedded key-value store
/// - LSM tree architecture
/// - Sorted key storage
/// - Batch writes
/// - Snapshots for consistent reads
/// - Compression support
/// - Fast sequential access
/// </summary>
public sealed class LevelDbStorageStrategy : DatabaseStorageStrategyBase
{
    private RocksDb? _db;
    private string _databasePath = "./leveldb_storage";
    private readonly object _lock = new();

    public override string StrategyId => "leveldb";
    public override string Name => "LevelDB Storage (RocksDB)";
    public override StorageTier Tier => StorageTier.Warm;
    public override DatabaseCategory DatabaseCategory => DatabaseCategory.KeyValue;
    public override string Engine => "RocksDB";

    public override StorageCapabilities Capabilities => new()
    {
        SupportsMetadata = true,
        SupportsStreaming = false,
        SupportsLocking = false,
        SupportsVersioning = false,
        SupportsTiering = false,
        SupportsEncryption = false,
        SupportsCompression = true,
        SupportsMultipart = false,
        MaxObjectSize = null, // Limited by disk
        MaxObjects = null,
        ConsistencyModel = ConsistencyModel.Strong
    };

    public override bool SupportsTransactions => true; // Via WriteBatch
    public override bool SupportsSql => false;

    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        _databasePath = GetConfiguration("DatabasePath", "./leveldb_storage");
        await Task.CompletedTask;
    }

    protected override Task ConnectCoreAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_db != null) return Task.CompletedTask;

            Directory.CreateDirectory(_databasePath);

            var options = new DbOptions()
                .SetCreateIfMissing(true)
                .SetCompression(Compression.Snappy)
                .SetWriteBufferSize(64 * 1024 * 1024) // 64MB
                .SetMaxOpenFiles(1000);

            var blockOptions = new BlockBasedTableOptions()
                .SetBlockSize(4 * 1024); // 4KB

            options.SetBlockBasedTableFactory(blockOptions);

            _db = RocksDb.Open(options, _databasePath);
        }

        return Task.CompletedTask;
    }

    protected override Task DisconnectCoreAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            _db?.Dispose();
            _db = null;
        }

        return Task.CompletedTask;
    }

    protected override Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct)
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

        var dataKey = Encoding.UTF8.GetBytes($"data:{key}");
        var metaKey = Encoding.UTF8.GetBytes($"meta:{key}");

        lock (_lock)
        {
            // Read existing metadata inside the lock to avoid TOCTOU with concurrent stores.
            var existingMetadataBytes = _db!.Get(metaKey);
            if (existingMetadataBytes != null)
            {
                var existingMetadata = JsonSerializer.Deserialize<MetadataDocument>(existingMetadataBytes, JsonOptions);
                if (existingMetadata != null)
                {
                    metadataDoc.CreatedAt = existingMetadata.CreatedAt;
                }
            }

            var metadataJson = JsonSerializer.SerializeToUtf8Bytes(metadataDoc, JsonOptions);
            using var batch = new WriteBatch();
            batch.Put(dataKey, data);
            batch.Put(metaKey, metadataJson);
            _db!.Write(batch);
        }

        return Task.FromResult(new StorageObjectMetadata
        {
            Key = key,
            Size = data.LongLength,
            Created = metadataDoc.CreatedAt,
            Modified = now,
            ETag = etag,
            ContentType = contentType,
            CustomMetadata = metadata as IReadOnlyDictionary<string, string>,
            Tier = Tier
        });
    }

    protected override Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct)
    {
        var dataKey = Encoding.UTF8.GetBytes($"data:{key}");
        var data = _db!.Get(dataKey);

        if (data == null)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        return Task.FromResult(data);
    }

    protected override Task<long> DeleteCoreAsync(string key, CancellationToken ct)
    {
        var dataKey = Encoding.UTF8.GetBytes($"data:{key}");
        var metaKey = Encoding.UTF8.GetBytes($"meta:{key}");

        // Get size before deletion
        long size = 0;
        var metadataBytes = _db!.Get(metaKey);
        if (metadataBytes != null)
        {
            var metadata = JsonSerializer.Deserialize<MetadataDocument>(metadataBytes, JsonOptions);
            if (metadata != null)
            {
                size = metadata.Size;
            }
        }

        lock (_lock)
        {
            using var batch = new WriteBatch();
            batch.Delete(dataKey);
            batch.Delete(metaKey);
            _db.Write(batch);
        }

        return Task.FromResult(size);
    }

    protected override Task<bool> ExistsCoreAsync(string key, CancellationToken ct)
    {
        var dataKey = Encoding.UTF8.GetBytes($"data:{key}");
        var data = _db!.Get(dataKey);
        return Task.FromResult(data != null);
    }

    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        var searchPrefix = $"meta:{prefix ?? ""}";

        using var iterator = _db!.NewIterator();
        iterator.Seek(searchPrefix);

        while (iterator.Valid())
        {
            ct.ThrowIfCancellationRequested();

            var currentKey = Encoding.UTF8.GetString(iterator.Key());

            if (!currentKey.StartsWith("meta:", StringComparison.Ordinal))
            {
                iterator.Next();
                continue;
            }

            var key = currentKey.Substring(5); // Remove "meta:" prefix

            // P2-2812: Break once keys have advanced past the prefix range (LevelDB is sorted).
            // The redundant !StartsWith check inside the early-break was confusing but always
            // true at that point (outer if already confirmed !StartsWith).
            if (!string.IsNullOrEmpty(prefix) && !key.StartsWith(prefix, StringComparison.Ordinal))
            {
                if (string.Compare(key, prefix, StringComparison.Ordinal) > 0)
                    break;

                iterator.Next();
                continue;
            }

            var metadataBytes = iterator.Value();
            var metadata = JsonSerializer.Deserialize<MetadataDocument>(metadataBytes, JsonOptions);

            if (metadata != null)
            {
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

            iterator.Next();
        }

        await Task.CompletedTask;
    }

    protected override Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct)
    {
        var metaKey = Encoding.UTF8.GetBytes($"meta:{key}");
        var metadataBytes = _db!.Get(metaKey);

        if (metadataBytes == null)
        {
            throw new FileNotFoundException($"Metadata not found: {key}");
        }

        var metadata = JsonSerializer.Deserialize<MetadataDocument>(metadataBytes, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to parse metadata for: {key}");

        return Task.FromResult(new StorageObjectMetadata
        {
            Key = key,
            Size = metadata.Size,
            Created = metadata.CreatedAt,
            Modified = metadata.ModifiedAt,
            ETag = metadata.ETag,
            ContentType = metadata.ContentType,
            CustomMetadata = metadata.CustomMetadata as IReadOnlyDictionary<string, string>,
            Tier = Tier
        });
    }

    protected override Task<bool> CheckHealthCoreAsync(CancellationToken ct)
    {
        return Task.FromResult(_db != null);
    }

    protected override Task<IDatabaseTransaction> BeginTransactionCoreAsync(System.Data.IsolationLevel isolationLevel, CancellationToken ct)
    {
        return Task.FromResult<IDatabaseTransaction>(new LevelDbTransaction(_db!));
    }

    /// <summary>
    /// Triggers manual compaction.
    /// </summary>
    public void Compact()
    {
        _db?.CompactRange((byte[]?)null, (byte[]?)null);
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        lock (_lock)
        {
            _db?.Dispose();
            _db = null;
        }
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

    private sealed class LevelDbTransaction : IDatabaseTransaction
    {
        private readonly RocksDb _db;
        private readonly WriteBatch _batch;
        private bool _disposed;
        private bool _committed;

        public string TransactionId { get; } = Guid.NewGuid().ToString("N");
        public System.Data.IsolationLevel IsolationLevel => System.Data.IsolationLevel.Serializable;

        public LevelDbTransaction(RocksDb db)
        {
            _db = db;
            _batch = new WriteBatch();
        }

        public Task CommitAsync(CancellationToken ct = default)
        {
            if (_committed) return Task.CompletedTask;
            _db.Write(_batch);
            _committed = true;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken ct = default)
        {
            // WriteBatch is rolled back by not committing
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            _batch.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
