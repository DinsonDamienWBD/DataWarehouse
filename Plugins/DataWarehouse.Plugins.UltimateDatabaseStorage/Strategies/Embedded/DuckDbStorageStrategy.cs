using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Database;
using DuckDB.NET.Data;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies.Embedded;

/// <summary>
/// DuckDB embedded analytical storage strategy with production-ready features:
/// - OLAP-optimized columnar storage
/// - In-process analytics
/// - Parquet/CSV/JSON file support
/// - SQL support with extensions
/// - Vectorized query execution
/// - No external dependencies
/// - Memory-efficient for large datasets
/// </summary>
public sealed class DuckDbStorageStrategy : DatabaseStorageStrategyBase
{
    private DuckDBConnection? _connection;
    private string _databasePath = ":memory:";
    private string _tableName = "storage";
    private readonly object _lock = new();

    public override string StrategyId => "duckdb";
    public override string Name => "DuckDB Analytical Storage";
    public override StorageTier Tier => StorageTier.Hot;
    public override DatabaseCategory DatabaseCategory => DatabaseCategory.Embedded;
    public override string Engine => "DuckDB";

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
        MaxObjectSize = 1L * 1024 * 1024 * 1024, // 1GB practical limit
        MaxObjects = null,
        ConsistencyModel = ConsistencyModel.Strong
    };

    public override bool SupportsTransactions => true;
    public override bool SupportsSql => true;

    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        _databasePath = GetConfiguration("DatabasePath", ":memory:");
        _tableName = GetConfiguration("TableName", "storage");

        await Task.CompletedTask;
    }

    protected override Task ConnectCoreAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_connection != null) return Task.CompletedTask;

            _connection = new DuckDBConnection($"Data Source={_databasePath}");
            _connection.Open();

            EnsureSchema();
        }

        return Task.CompletedTask;
    }

    private void EnsureSchema()
    {
        using var command = _connection!.CreateCommand();
        command.CommandText = $@"
            CREATE TABLE IF NOT EXISTS {_tableName} (
                key VARCHAR PRIMARY KEY,
                data BLOB NOT NULL,
                size BIGINT NOT NULL,
                content_type VARCHAR,
                etag VARCHAR NOT NULL,
                metadata JSON,
                created_at TIMESTAMP NOT NULL,
                modified_at TIMESTAMP NOT NULL
            )";
        command.ExecuteNonQuery();
    }

    protected override Task DisconnectCoreAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            _connection?.Dispose();
            _connection = null;
        }

        return Task.CompletedTask;
    }

    protected override Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var etag = GenerateETag(data);
        var contentType = GetContentType(key);
        var metadataJson = metadata != null ? JsonSerializer.Serialize(metadata, JsonOptions) : null;

        lock (_lock)
        {
            using var command = _connection!.CreateCommand();

            // DuckDB uses INSERT OR REPLACE
            command.CommandText = $@"
                INSERT OR REPLACE INTO {_tableName}
                (key, data, size, content_type, etag, metadata, created_at, modified_at)
                VALUES ($key, $data, $size, $contentType, $etag, $metadata, $createdAt, $modifiedAt)";

            command.Parameters.Add(new DuckDBParameter("$key", key));
            command.Parameters.Add(new DuckDBParameter("$data", data));
            command.Parameters.Add(new DuckDBParameter("$size", data.LongLength));
            command.Parameters.Add(new DuckDBParameter("$contentType", contentType));
            command.Parameters.Add(new DuckDBParameter("$etag", etag));
            command.Parameters.Add(new DuckDBParameter("$metadata", metadataJson));
            command.Parameters.Add(new DuckDBParameter("$createdAt", now));
            command.Parameters.Add(new DuckDBParameter("$modifiedAt", now));

            command.ExecuteNonQuery();
        }

        return Task.FromResult(new StorageObjectMetadata
        {
            Key = key,
            Size = data.LongLength,
            Created = now,
            Modified = now,
            ETag = etag,
            ContentType = contentType,
            CustomMetadata = metadata as IReadOnlyDictionary<string, string>,
            Tier = Tier
        });
    }

    protected override Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct)
    {
        lock (_lock)
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = $"SELECT data FROM {_tableName} WHERE key = $key";
            command.Parameters.Add(new DuckDBParameter("$key", key));

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new FileNotFoundException($"Object not found: {key}");
            }

            return Task.FromResult((byte[])reader["data"]);
        }
    }

    protected override Task<long> DeleteCoreAsync(string key, CancellationToken ct)
    {
        lock (_lock)
        {
            // Get size first
            using var sizeCommand = _connection!.CreateCommand();
            sizeCommand.CommandText = $"SELECT size FROM {_tableName} WHERE key = $key";
            sizeCommand.Parameters.Add(new DuckDBParameter("$key", key));
            var size = Convert.ToInt64(sizeCommand.ExecuteScalar() ?? 0);

            // Delete
            using var deleteCommand = _connection.CreateCommand();
            deleteCommand.CommandText = $"DELETE FROM {_tableName} WHERE key = $key";
            deleteCommand.Parameters.Add(new DuckDBParameter("$key", key));
            deleteCommand.ExecuteNonQuery();

            return Task.FromResult(size);
        }
    }

    protected override Task<bool> ExistsCoreAsync(string key, CancellationToken ct)
    {
        lock (_lock)
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = $"SELECT 1 FROM {_tableName} WHERE key = $key LIMIT 1";
            command.Parameters.Add(new DuckDBParameter("$key", key));

            var result = command.ExecuteScalar();
            return Task.FromResult(result != null);
        }
    }

    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        List<StorageObjectMetadata> results;

        lock (_lock)
        {
            results = new List<StorageObjectMetadata>();

            using var command = _connection!.CreateCommand();
            command.CommandText = string.IsNullOrEmpty(prefix)
                ? $"SELECT key, size, content_type, etag, metadata, created_at, modified_at FROM {_tableName} ORDER BY key"
                : $"SELECT key, size, content_type, etag, metadata, created_at, modified_at FROM {_tableName} WHERE key LIKE $prefix ORDER BY key";

            if (!string.IsNullOrEmpty(prefix))
            {
                command.Parameters.Add(new DuckDBParameter("$prefix", prefix + "%"));
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();

                var metadataJson = reader.IsDBNull(4) ? null : reader.GetString(4);
                Dictionary<string, string>? customMetadata = null;
                if (!string.IsNullOrEmpty(metadataJson))
                {
                    customMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson, JsonOptions);
                }

                results.Add(new StorageObjectMetadata
                {
                    Key = reader.GetString(0),
                    Size = reader.GetInt64(1),
                    ContentType = reader.IsDBNull(2) ? null : reader.GetString(2),
                    ETag = reader.GetString(3),
                    CustomMetadata = customMetadata,
                    Created = reader.GetDateTime(5),
                    Modified = reader.GetDateTime(6),
                    Tier = Tier
                });
            }
        }

        foreach (var result in results)
        {
            yield return result;
        }

        await Task.CompletedTask;
    }

    protected override Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct)
    {
        lock (_lock)
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = $"SELECT size, content_type, etag, metadata, created_at, modified_at FROM {_tableName} WHERE key = $key";
            command.Parameters.Add(new DuckDBParameter("$key", key));

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new FileNotFoundException($"Object not found: {key}");
            }

            var metadataJson = reader.IsDBNull(3) ? null : reader.GetString(3);
            Dictionary<string, string>? customMetadata = null;
            if (!string.IsNullOrEmpty(metadataJson))
            {
                customMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson, JsonOptions);
            }

            return Task.FromResult(new StorageObjectMetadata
            {
                Key = key,
                Size = reader.GetInt64(0),
                ContentType = reader.IsDBNull(1) ? null : reader.GetString(1),
                ETag = reader.GetString(2),
                CustomMetadata = customMetadata,
                Created = reader.GetDateTime(4),
                Modified = reader.GetDateTime(5),
                Tier = Tier
            });
        }
    }

    protected override Task<bool> CheckHealthCoreAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            try
            {
                using var command = _connection!.CreateCommand();
                command.CommandText = "SELECT 1";
                command.ExecuteScalar();
                return Task.FromResult(true);
            }
            catch
            {
                return Task.FromResult(false);
            }
        }
    }

    protected override Task<IDatabaseTransaction> BeginTransactionCoreAsync(System.Data.IsolationLevel isolationLevel, CancellationToken ct)
    {
        var transaction = _connection!.BeginTransaction();
        return Task.FromResult<IDatabaseTransaction>(new DuckDbTransaction(transaction));
    }

    /// <summary>
    /// Exports data to Parquet format.
    /// </summary>
    public async Task ExportToParquetAsync(string filePath, CancellationToken ct = default)
    {
        lock (_lock)
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = $"COPY {_tableName} TO '{filePath}' (FORMAT PARQUET)";
            command.ExecuteNonQuery();
        }

        await Task.CompletedTask;
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        lock (_lock)
        {
            _connection?.Dispose();
            _connection = null;
        }
        await base.DisposeAsyncCore();
    }

    private sealed class DuckDbTransaction : IDatabaseTransaction
    {
        private readonly DuckDBTransaction _transaction;
        private bool _disposed;

        public string TransactionId { get; } = Guid.NewGuid().ToString("N");
        public System.Data.IsolationLevel IsolationLevel => _transaction.IsolationLevel;

        public DuckDbTransaction(DuckDBTransaction transaction)
        {
            _transaction = transaction;
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

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            _transaction.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
