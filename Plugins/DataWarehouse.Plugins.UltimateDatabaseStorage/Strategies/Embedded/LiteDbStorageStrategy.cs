using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Database;
using LiteDB;
using LiteDB.Async;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies.Embedded;

/// <summary>
/// LiteDB embedded storage strategy with production-ready features:
/// - Serverless .NET embedded NoSQL database
/// - Single file storage (SQLite-like deployment)
/// - BSON document storage
/// - LINQ query support
/// - Full-text search
/// - Encryption support
/// - Thread-safe operations
/// - No external dependencies
/// - Cross-platform support
/// </summary>
public sealed class LiteDbStorageStrategy : DatabaseStorageStrategyBase
{
    private LiteDatabaseAsync? _database;
    private ILiteCollectionAsync<StorageDocument>? _collection;
    private string _databasePath = "datawarehouse.db";
    private string _collectionName = "storage";
    private string? _password;
    private bool _useSharedMode;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public override string StrategyId => "litedb";
    public override string Name => "LiteDB Embedded Storage";
    public override StorageTier Tier => StorageTier.Hot;
    public override DatabaseCategory DatabaseCategory => DatabaseCategory.Embedded;
    public override string Engine => "LiteDB";

    public override StorageCapabilities Capabilities => new()
    {
        SupportsMetadata = true,
        SupportsStreaming = true, // FileStorage
        SupportsLocking = true,
        SupportsVersioning = false,
        SupportsTiering = false,
        SupportsEncryption = true, // Built-in AES encryption
        SupportsCompression = false,
        SupportsMultipart = true, // FileStorage for large files
        MaxObjectSize = null, // Unlimited with FileStorage
        MaxObjects = null,
        ConsistencyModel = ConsistencyModel.Strong
    };

    public override bool SupportsTransactions => true;
    public override bool SupportsSql => false;

    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        _databasePath = GetConfiguration("DatabasePath", "datawarehouse.db");
        _collectionName = GetConfiguration("CollectionName", "storage");
        _password = GetConfiguration<string?>("Password", null);
        _useSharedMode = GetConfiguration("UseSharedMode", false);

        await Task.CompletedTask;
    }

    protected override async Task ConnectCoreAsync(CancellationToken ct)
    {
        var connectionString = new ConnectionString
        {
            Filename = _databasePath,
            Connection = _useSharedMode ? ConnectionType.Shared : ConnectionType.Direct,
            ReadOnly = false
        };

        if (!string.IsNullOrEmpty(_password))
        {
            connectionString.Password = _password;
        }

        _database = new LiteDatabaseAsync(connectionString);
        _collection = _database.GetCollection<StorageDocument>(_collectionName);

        await EnsureSchemaCoreAsync(ct);
    }

    protected override async Task DisconnectCoreAsync(CancellationToken ct)
    {
        if (_database != null)
        {
            _database.Dispose();
            _database = null;
            _collection = null;
        }

        await Task.CompletedTask;
    }

    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct)
    {
        // Create indexes
        await _collection!.EnsureIndexAsync(x => x.Key, true);
        await _collection.EnsureIndexAsync(x => x.CreatedAt);
        await _collection.EnsureIndexAsync(x => x.ModifiedAt);
        await _collection.EnsureIndexAsync(x => x.Size);
    }

    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var etag = GenerateETag(data);
        var contentType = GetContentType(key);

        await _writeLock.WaitAsync(ct);
        try
        {
            var doc = new StorageDocument
            {
                Key = key,
                Data = data,
                Size = data.LongLength,
                ContentType = contentType,
                ETag = etag,
                Metadata = metadata?.ToDictionary(k => k.Key, v => v.Value),
                CreatedAt = now,
                ModifiedAt = now
            };

            // Check if exists for upsert
            var existing = await _collection!.FindOneAsync(x => x.Key == key);
            if (existing != null)
            {
                doc.Id = existing.Id;
                doc.CreatedAt = existing.CreatedAt;
                await _collection.UpdateAsync(doc);
            }
            else
            {
                await _collection.InsertAsync(doc);
            }

            return new StorageObjectMetadata
            {
                Key = key,
                Size = data.LongLength,
                Created = doc.CreatedAt,
                Modified = doc.ModifiedAt,
                ETag = etag,
                ContentType = contentType,
                CustomMetadata = metadata as IReadOnlyDictionary<string, string>,
                Tier = Tier
            };
        }
        finally
        {
            _writeLock.Release();
        }
    }

    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct)
    {
        var doc = await _collection!.FindOneAsync(x => x.Key == key);

        if (doc == null)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        return doc.Data ?? Array.Empty<byte>();
    }

    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var doc = await _collection!.FindOneAsync(x => x.Key == key);
            if (doc == null)
            {
                return 0;
            }

            var size = doc.Size;
            await _collection.DeleteAsync(doc.Id);

            return size;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct)
    {
        var count = await _collection!.CountAsync(x => x.Key == key);
        return count > 0;
    }

    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        IEnumerable<StorageDocument> documents;

        if (string.IsNullOrEmpty(prefix))
        {
            documents = await _collection!.FindAllAsync();
        }
        else
        {
            documents = await _collection!.FindAsync(x => x.Key.StartsWith(prefix));
        }

        foreach (var doc in documents.OrderBy(d => d.Key))
        {
            ct.ThrowIfCancellationRequested();

            yield return new StorageObjectMetadata
            {
                Key = doc.Key,
                Size = doc.Size,
                ContentType = doc.ContentType,
                ETag = doc.ETag,
                CustomMetadata = doc.Metadata as IReadOnlyDictionary<string, string>,
                Created = doc.CreatedAt,
                Modified = doc.ModifiedAt,
                Tier = Tier
            };
        }
    }

    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct)
    {
        var doc = await _collection!.FindOneAsync(x => x.Key == key);

        if (doc == null)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        return new StorageObjectMetadata
        {
            Key = doc.Key,
            Size = doc.Size,
            ContentType = doc.ContentType,
            ETag = doc.ETag,
            CustomMetadata = doc.Metadata as IReadOnlyDictionary<string, string>,
            Created = doc.CreatedAt,
            Modified = doc.ModifiedAt,
            Tier = Tier
        };
    }

    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct)
    {
        try
        {
            await _collection!.CountAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    protected override async Task<IDatabaseTransaction> BeginTransactionCoreAsync(System.Data.IsolationLevel isolationLevel, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        return new LiteDbTransaction(_database!, _writeLock);
    }

    /// <summary>
    /// Stores a large file using LiteDB FileStorage.
    /// </summary>
    public async Task<StorageObjectMetadata> StoreLargeFileAsync(string key, Stream data, IDictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var fileStorage = _database!.GetStorage<string>("files", "chunks");

        // Calculate size and ETag
        var tempPath = Path.GetTempFileName();
        try
        {
            using (var tempFile = File.Create(tempPath))
            {
                await data.CopyToAsync(tempFile, ct);
            }

            var fileInfo = new FileInfo(tempPath);
            var size = fileInfo.Length;

            // AD-11: Use file metadata for ETag (crypto hashing via UltimateDataIntegrity bus)
            var tempFileInfo = new FileInfo(tempPath);
            var etag = HashCode.Combine(tempFileInfo.Length, tempFileInfo.LastWriteTimeUtc.Ticks).ToString("x8");

            // Upload to FileStorage
            using (var fileStream = File.OpenRead(tempPath))
            {
                await fileStorage.UploadAsync($"storage/{key}", key, fileStream);
            }

            // Store metadata in collection
            var contentType = GetContentType(key);
            var doc = new StorageDocument
            {
                Key = key,
                Data = null, // Data stored in FileStorage
                Size = size,
                ContentType = contentType,
                ETag = etag,
                Metadata = metadata?.ToDictionary(k => k.Key, v => v.Value),
                CreatedAt = now,
                ModifiedAt = now,
                IsLargeFile = true
            };

            await _writeLock.WaitAsync(ct);
            try
            {
                var existing = await _collection!.FindOneAsync(x => x.Key == key);
                if (existing != null)
                {
                    doc.Id = existing.Id;
                    doc.CreatedAt = existing.CreatedAt;
                    await _collection.UpdateAsync(doc);
                }
                else
                {
                    await _collection.InsertAsync(doc);
                }
            }
            finally
            {
                _writeLock.Release();
            }

            return new StorageObjectMetadata
            {
                Key = key,
                Size = size,
                Created = doc.CreatedAt,
                Modified = doc.ModifiedAt,
                ETag = etag,
                ContentType = contentType,
                CustomMetadata = metadata as IReadOnlyDictionary<string, string>,
                Tier = Tier
            };
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    /// <summary>
    /// Retrieves a large file stored using FileStorage.
    /// </summary>
    public async Task<Stream> RetrieveLargeFileAsync(string key, CancellationToken ct = default)
    {
        var fileStorage = _database!.GetStorage<string>("files", "chunks");
        var fileInfo = await fileStorage.FindByIdAsync($"storage/{key}");

        if (fileInfo == null)
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        var ms = new MemoryStream(65536);
        await fileStorage.DownloadAsync(fileInfo.Id, ms);
        ms.Position = 0;
        return ms;
    }

    /// <summary>
    /// Compacts the database file to reclaim space.
    /// </summary>
    public async Task CompactAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await _database!.RebuildAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Creates a checkpoint for the database.
    /// </summary>
    public async Task CheckpointAsync(CancellationToken ct = default)
    {
        await _database!.CheckpointAsync();
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        _database?.Dispose();
        _writeLock?.Dispose();
        await base.DisposeAsyncCore();
    }

    private sealed class StorageDocument
    {
        public ObjectId Id { get; set; } = ObjectId.NewObjectId();
        public string Key { get; set; } = string.Empty;
        public byte[]? Data { get; set; }
        public long Size { get; set; }
        public string? ContentType { get; set; }
        public string? ETag { get; set; }
        public Dictionary<string, string>? Metadata { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ModifiedAt { get; set; }
        public bool IsLargeFile { get; set; }
    }

    private sealed class LiteDbTransaction : IDatabaseTransaction
    {
        private readonly LiteDatabaseAsync _database;
        private readonly SemaphoreSlim _writeLock;
        private bool _committed;
        private bool _disposed;

        public string TransactionId { get; } = Guid.NewGuid().ToString("N");
        public System.Data.IsolationLevel IsolationLevel => System.Data.IsolationLevel.Serializable;

        public LiteDbTransaction(LiteDatabaseAsync database, SemaphoreSlim writeLock)
        {
            _database = database;
            _writeLock = writeLock;
        }

        public async Task CommitAsync(CancellationToken ct = default)
        {
            if (!_committed)
            {
                await _database.CommitAsync();
                _committed = true;
            }
        }

        public async Task RollbackAsync(CancellationToken ct = default)
        {
            if (!_committed)
            {
                await _database.RollbackAsync();
            }
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;

            _writeLock.Release();
            return ValueTask.CompletedTask;
        }
    }
}
