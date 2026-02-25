using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Database;
using Microsoft.Data.Sqlite;
using System.Data;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies.Relational;

/// <summary>
/// SQLite storage strategy with production-ready features:
/// - Zero-configuration embedded database
/// - Connection pooling with shared cache
/// - WAL mode for concurrent reads
/// - BLOB storage for binary data
/// - JSON1 extension for flexible metadata
/// - ACID transactions with serializable isolation
/// - Automatic schema creation and migrations
/// - Vacuum and optimization support
/// </summary>
public sealed class SqliteStorageStrategy : DatabaseStorageStrategyBase
{
    private SqliteConnection? _connection;
    private string _connectionString = string.Empty;
    private string _tableName = "data_warehouse_storage";
    private string _databasePath = "datawarehouse.db";
    private bool _useWalMode = true;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public override string StrategyId => "sqlite";
    public override string Name => "SQLite Embedded Storage";
    public override StorageTier Tier => StorageTier.Warm;
    public override DatabaseCategory DatabaseCategory => DatabaseCategory.Embedded;
    public override string Engine => "SQLite";

    public override StorageCapabilities Capabilities => new()
    {
        SupportsMetadata = true,
        SupportsStreaming = true,
        SupportsLocking = true,
        SupportsVersioning = false,
        SupportsTiering = false,
        SupportsEncryption = false, // Would need SQLCipher
        SupportsCompression = false,
        SupportsMultipart = false,
        MaxObjectSize = 1L * 1024 * 1024 * 1024, // 1GB practical limit
        MaxObjects = null,
        ConsistencyModel = ConsistencyModel.Strong
    };

    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        _tableName = GetConfiguration("TableName", "data_warehouse_storage");
        _databasePath = GetConfiguration("DatabasePath", "datawarehouse.db");
        _useWalMode = GetConfiguration("UseWalMode", true);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        };

        _connectionString = builder.ConnectionString;
        await EnsureSchemaCoreAsync(ct);
    }

    protected override async Task ConnectCoreAsync(CancellationToken ct)
    {
        _connection = new SqliteConnection(_connectionString);
        await _connection.OpenAsync(ct);

        // Enable WAL mode for better concurrency
        if (_useWalMode)
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Optimize for performance
        await using var pragmaCmd = _connection.CreateCommand();
        pragmaCmd.CommandText = @"
            PRAGMA synchronous=NORMAL;
            PRAGMA cache_size=-64000;
            PRAGMA temp_store=MEMORY;
            PRAGMA mmap_size=268435456;";
        await pragmaCmd.ExecuteNonQueryAsync(ct);
    }

    protected override async Task DisconnectCoreAsync(CancellationToken ct)
    {
        if (_connection != null)
        {
            await _connection.CloseAsync();
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var createTableSql = $@"
            CREATE TABLE IF NOT EXISTS {_tableName} (
                key TEXT PRIMARY KEY,
                data BLOB NOT NULL,
                size INTEGER NOT NULL,
                content_type TEXT,
                etag TEXT,
                metadata TEXT,
                created_at TEXT NOT NULL,
                modified_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_{_tableName}_created ON {_tableName} (created_at);
            CREATE INDEX IF NOT EXISTS idx_{_tableName}_modified ON {_tableName} (modified_at);
            CREATE INDEX IF NOT EXISTS idx_{_tableName}_size ON {_tableName} (size);
        ";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = createTableSql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var nowStr = now.ToString("o");
        var etag = GenerateETag(data);
        var contentType = GetContentType(key);
        var metadataJson = metadata != null ? JsonSerializer.Serialize(metadata, JsonOptions) : null;

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = $@"
                INSERT INTO {_tableName} (key, data, size, content_type, etag, metadata, created_at, modified_at)
                VALUES (@key, @data, @size, @content_type, @etag, @metadata, @created_at, @modified_at)
                ON CONFLICT(key) DO UPDATE SET
                    data = excluded.data,
                    size = excluded.size,
                    content_type = excluded.content_type,
                    etag = excluded.etag,
                    metadata = excluded.metadata,
                    modified_at = excluded.modified_at
                RETURNING created_at, modified_at";

            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@data", data);
            cmd.Parameters.AddWithValue("@size", data.LongLength);
            cmd.Parameters.AddWithValue("@content_type", (object?)contentType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@etag", etag);
            cmd.Parameters.AddWithValue("@metadata", (object?)metadataJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@created_at", nowStr);
            cmd.Parameters.AddWithValue("@modified_at", nowStr);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            DateTime createdAt = now, modifiedAt = now;
            if (await reader.ReadAsync(ct))
            {
                if (DateTime.TryParse(reader.GetString(0), out var created))
                    createdAt = created;
                if (DateTime.TryParse(reader.GetString(1), out var modified))
                    modifiedAt = modified;
            }

            return new StorageObjectMetadata
            {
                Key = key,
                Size = data.LongLength,
                Created = createdAt,
                Modified = modifiedAt,
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
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = $"SELECT data FROM {_tableName} WHERE key = @key";
        cmd.Parameters.AddWithValue("@key", key);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        return (byte[])reader["data"];
    }

    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);

            long size = 0;
            await using (var sizeCmd = conn.CreateCommand())
            {
                sizeCmd.CommandText = $"SELECT size FROM {_tableName} WHERE key = @key";
                sizeCmd.Parameters.AddWithValue("@key", key);
                var result = await sizeCmd.ExecuteScalarAsync(ct);
                if (result != null && result != DBNull.Value)
                {
                    size = Convert.ToInt64(result);
                }
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM {_tableName} WHERE key = @key";
            cmd.Parameters.AddWithValue("@key", key);
            await cmd.ExecuteNonQueryAsync(ct);

            return size;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = $"SELECT EXISTS(SELECT 1 FROM {_tableName} WHERE key = @key)";
        cmd.Parameters.AddWithValue("@key", key);

        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result) == 1;
    }

    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        if (string.IsNullOrEmpty(prefix))
        {
            cmd.CommandText = $"SELECT key, size, content_type, etag, metadata, created_at, modified_at FROM {_tableName} ORDER BY key";
        }
        else
        {
            cmd.CommandText = $"SELECT key, size, content_type, etag, metadata, created_at, modified_at FROM {_tableName} WHERE key LIKE @prefix ORDER BY key";
            cmd.Parameters.AddWithValue("@prefix", prefix + "%");
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var metadataJson = reader.IsDBNull(4) ? null : reader.GetString(4);
            Dictionary<string, string>? metadata = null;
            if (!string.IsNullOrEmpty(metadataJson))
            {
                metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson, JsonOptions);
            }

            DateTime.TryParse(reader.GetString(5), out var createdAt);
            DateTime.TryParse(reader.GetString(6), out var modifiedAt);

            yield return new StorageObjectMetadata
            {
                Key = reader.GetString(0),
                Size = reader.GetInt64(1),
                ContentType = reader.IsDBNull(2) ? null : reader.GetString(2),
                ETag = reader.IsDBNull(3) ? null : reader.GetString(3),
                CustomMetadata = metadata,
                Created = createdAt,
                Modified = modifiedAt,
                Tier = Tier
            };
        }
    }

    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = $"SELECT key, size, content_type, etag, metadata, created_at, modified_at FROM {_tableName} WHERE key = @key";
        cmd.Parameters.AddWithValue("@key", key);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        var metadataJson = reader.IsDBNull(4) ? null : reader.GetString(4);
        Dictionary<string, string>? metadata = null;
        if (!string.IsNullOrEmpty(metadataJson))
        {
            metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson, JsonOptions);
        }

        DateTime.TryParse(reader.GetString(5), out var createdAt);
        DateTime.TryParse(reader.GetString(6), out var modifiedAt);

        return new StorageObjectMetadata
        {
            Key = reader.GetString(0),
            Size = reader.GetInt64(1),
            ContentType = reader.IsDBNull(2) ? null : reader.GetString(2),
            ETag = reader.IsDBNull(3) ? null : reader.GetString(3),
            CustomMetadata = metadata,
            Created = createdAt,
            Modified = modifiedAt,
            Tier = Tier
        };
    }

    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1";
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result) == 1;
    }

    protected override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryCoreAsync(
        string query, IDictionary<string, object>? parameters, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = query;

        if (parameters != null)
        {
            foreach (var param in parameters)
            {
                cmd.Parameters.AddWithValue($"@{param.Key}", param.Value ?? DBNull.Value);
            }
        }

        var results = new List<Dictionary<string, object?>>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            results.Add(row);
        }

        return results;
    }

    protected override async Task<int> ExecuteNonQueryCoreAsync(
        string command, IDictionary<string, object>? parameters, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = command;

            if (parameters != null)
            {
                foreach (var param in parameters)
                {
                    cmd.Parameters.AddWithValue($"@{param.Key}", param.Value ?? DBNull.Value);
                }
            }

            return await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    protected override async Task<IDatabaseTransaction> BeginTransactionCoreAsync(IsolationLevel isolationLevel, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var transaction = conn.BeginTransaction(isolationLevel);
        return new SqliteDbTransaction(conn, transaction, _writeLock);
    }

    /// <summary>
    /// Runs VACUUM to reclaim space and optimize the database.
    /// </summary>
    public async Task VacuumAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "VACUUM";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Analyzes the database to optimize query planning.
    /// </summary>
    public async Task AnalyzeAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "ANALYZE";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        await DisconnectCoreAsync(CancellationToken.None);
        _writeLock?.Dispose();
        await base.DisposeAsyncCore();
    }

    private sealed class SqliteDbTransaction : IDatabaseTransaction
    {
        private readonly SqliteConnection _connection;
        private readonly SqliteTransaction _transaction;
        private readonly SemaphoreSlim _writeLock;
        private bool _disposed;

        public string TransactionId { get; } = Guid.NewGuid().ToString("N");
        public IsolationLevel IsolationLevel => _transaction.IsolationLevel;

        public SqliteDbTransaction(SqliteConnection connection, SqliteTransaction transaction, SemaphoreSlim writeLock)
        {
            _connection = connection;
            _transaction = transaction;
            _writeLock = writeLock;
        }

        public Task CommitAsync(CancellationToken ct = default)
        {
            _transaction.Commit();
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken ct = default)
        {
            _transaction.Rollback();
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            _transaction.Dispose();
            await _connection.DisposeAsync();
            _writeLock.Release();
        }
    }
}
