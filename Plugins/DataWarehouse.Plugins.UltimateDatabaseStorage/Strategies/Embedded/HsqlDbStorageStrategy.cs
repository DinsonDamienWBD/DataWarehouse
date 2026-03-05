using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Database;
using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies.Embedded;

/// <summary>
/// HSQLDB (HyperSQL) embedded database storage strategy with production-ready features:
/// - Pure Java relational database
/// - In-memory and file-based modes
/// - ANSI SQL support
/// - ACID transactions
/// - MVCC concurrency
/// - Small memory footprint
/// - Fast startup
/// </summary>
/// <remarks>
/// Note: This implementation uses JDBC through a .NET-Java bridge or HSQLDB server mode.
/// For pure .NET, consider using SQLite instead.
/// </remarks>
public sealed class HsqlDbStorageStrategy : DatabaseStorageStrategyBase
{
    private DbConnection? _connection;
    private string _databasePath = "mem:datawarehouse";
    private string _tableName = "storage";
    private readonly object _lock = new();

    public override string StrategyId => "hsqldb";
    public override string Name => "HyperSQL Embedded Storage";
    public override StorageTier Tier => StorageTier.Hot;
    public override DatabaseCategory DatabaseCategory => DatabaseCategory.Embedded;
    public override string Engine => "HSQLDB";

    public override StorageCapabilities Capabilities => new()
    {
        SupportsMetadata = true,
        SupportsStreaming = false,
        SupportsLocking = true,
        SupportsVersioning = false,
        SupportsTiering = false,
        SupportsEncryption = true,
        SupportsCompression = true,
        SupportsMultipart = false,
        MaxObjectSize = 1L * 1024 * 1024 * 1024, // 1GB BLOB limit
        MaxObjects = null,
        ConsistencyModel = ConsistencyModel.Strong
    };

    public override bool SupportsTransactions => true;
    public override bool SupportsSql => true;

    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        _databasePath = GetConfiguration("DatabasePath", "mem:datawarehouse");
        _tableName = GetConfiguration("TableName", "storage");

        await Task.CompletedTask;
    }

    protected override async Task ConnectCoreAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_connection != null && _connection.State == ConnectionState.Open)
                return;

            var connectionString = GetConnectionString();

            // HSQLDB can be accessed via JDBC or ODBC bridge
            _connection = CreateHsqlConnection(connectionString);
            _connection.Open();

            EnsureSchema();
        }

        await Task.CompletedTask;
    }

    private DbConnection CreateHsqlConnection(string connectionString)
    {
        // In production, use JDBC bridge or ODBC driver
        // HSQLDB also has an ODBC driver available
        return new System.Data.Odbc.OdbcConnection(connectionString);
    }

    private void EnsureSchema()
    {
        try
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = $@"
                CREATE TABLE IF NOT EXISTS {_tableName} (
                    key VARCHAR(1024) NOT NULL PRIMARY KEY,
                    data BLOB NOT NULL,
                    size BIGINT NOT NULL,
                    content_type VARCHAR(255),
                    etag VARCHAR(128) NOT NULL,
                    metadata CLOB,
                    created_at TIMESTAMP NOT NULL,
                    modified_at TIMESTAMP NOT NULL
                )";
            command.ExecuteNonQuery();
        }
        catch
        {

            // Table might already exist
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }

        try
        {
            using var indexCmd = _connection!.CreateCommand();
            indexCmd.CommandText = $"CREATE INDEX IF NOT EXISTS idx_{_tableName}_created ON {_tableName}(created_at)";
            indexCmd.ExecuteNonQuery();
        }
        catch
        {

            // Index might already exist
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }
    }

    protected override async Task DisconnectCoreAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_connection != null)
            {
                // HSQLDB requires SHUTDOWN for proper cleanup
                try
                {
                    using var command = _connection.CreateCommand();
                    command.CommandText = "SHUTDOWN";
                    command.ExecuteNonQuery();
                }
                catch
                {

                    // Ignore shutdown errors
                    System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
                }

                _connection.Dispose();
                _connection = null;
            }
        }

        await Task.CompletedTask;
    }

    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var etag = GenerateETag(data);
        var contentType = GetContentType(key);
        var metadataJson = metadata != null ? JsonSerializer.Serialize(metadata, JsonOptions) : null;

        lock (_lock)
        {
            using var command = _connection!.CreateCommand();

            // HSQLDB supports MERGE
            command.CommandText = $@"
                MERGE INTO {_tableName} AS t
                USING (VALUES (?, ?, ?, ?, ?, ?, ?, ?)) AS s(key, data, size, content_type, etag, metadata, created_at, modified_at)
                ON t.key = s.key
                WHEN MATCHED THEN UPDATE SET
                    data = s.data, size = s.size, content_type = s.content_type,
                    etag = s.etag, metadata = s.metadata, modified_at = s.modified_at
                WHEN NOT MATCHED THEN INSERT
                    (key, data, size, content_type, etag, metadata, created_at, modified_at)
                    VALUES (s.key, s.data, s.size, s.content_type, s.etag, s.metadata, s.created_at, s.modified_at)";

            AddParameter(command, key);
            AddParameter(command, data);
            AddParameter(command, data.LongLength);
            AddParameter(command, contentType);
            AddParameter(command, etag);
            AddParameter(command, metadataJson);
            AddParameter(command, now);
            AddParameter(command, now);

            command.ExecuteNonQuery();
        }

        return await Task.FromResult(new StorageObjectMetadata
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

    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct)
    {
        lock (_lock)
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = $"SELECT data FROM {_tableName} WHERE key = ?";
            AddParameter(command, key);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new FileNotFoundException($"Object not found: {key}");
            }

            return (byte[])reader["data"];
        }
    }

    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct)
    {
        lock (_lock)
        {
            using var sizeCommand = _connection!.CreateCommand();
            sizeCommand.CommandText = $"SELECT size FROM {_tableName} WHERE key = ?";
            AddParameter(sizeCommand, key);
            var size = Convert.ToInt64(sizeCommand.ExecuteScalar() ?? 0);

            using var deleteCommand = _connection.CreateCommand();
            deleteCommand.CommandText = $"DELETE FROM {_tableName} WHERE key = ?";
            AddParameter(deleteCommand, key);
            deleteCommand.ExecuteNonQuery();

            return size;
        }
    }

    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct)
    {
        lock (_lock)
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = $"SELECT 1 FROM {_tableName} WHERE key = ?";
            AddParameter(command, key);

            var result = command.ExecuteScalar();
            return result != null;
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
                : $"SELECT key, size, content_type, etag, metadata, created_at, modified_at FROM {_tableName} WHERE key LIKE ? ORDER BY key";

            if (!string.IsNullOrEmpty(prefix))
            {
                AddParameter(command, prefix + "%");
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
    }

    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct)
    {
        lock (_lock)
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = $"SELECT size, content_type, etag, metadata, created_at, modified_at FROM {_tableName} WHERE key = ?";
            AddParameter(command, key);

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

            return new StorageObjectMetadata
            {
                Key = key,
                Size = reader.GetInt64(0),
                ContentType = reader.IsDBNull(1) ? null : reader.GetString(1),
                ETag = reader.GetString(2),
                CustomMetadata = customMetadata,
                Created = reader.GetDateTime(4),
                Modified = reader.GetDateTime(5),
                Tier = Tier
            };
        }
    }

    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            try
            {
                using var command = _connection!.CreateCommand();
                command.CommandText = "CALL 1";
                command.ExecuteScalar();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private static void AddParameter(DbCommand command, object? value)
    {
        var param = command.CreateParameter();
        param.Value = value ?? DBNull.Value;
        command.Parameters.Add(param);
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        lock (_lock)
        {
            if (_connection != null)
            {
                try
                {
                    using var command = _connection.CreateCommand();
                    command.CommandText = "SHUTDOWN";
                    command.ExecuteNonQuery();
                }
                catch { /* Non-critical operation */ }

                _connection.Dispose();
                _connection = null;
            }
        }
        await base.DisposeAsyncCore();
    }
}
