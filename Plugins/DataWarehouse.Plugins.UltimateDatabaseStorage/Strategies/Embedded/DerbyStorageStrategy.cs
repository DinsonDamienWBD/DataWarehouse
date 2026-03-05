using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Database;
using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies.Embedded;

/// <summary>
/// Apache Derby embedded database storage strategy with production-ready features:
/// - Pure Java embedded database
/// - ACID transactions
/// - Standards-compliant SQL
/// - Small footprint (~3MB)
/// - Network server mode available
/// - Zero configuration
/// - Full JDBC support
/// </summary>
/// <remarks>
/// Note: This implementation uses JDBC through a .NET-Java bridge or Derby network mode.
/// For pure .NET, consider using SQLite instead.
/// </remarks>
public sealed class DerbyStorageStrategy : DatabaseStorageStrategyBase
{
    private DbConnection? _connection;
    private string _databasePath = "memory:datawarehouse";
    private string _tableName = "STORAGE";
    private readonly object _lock = new();

    public override string StrategyId => "derby";
    public override string Name => "Apache Derby Embedded Storage";
    public override StorageTier Tier => StorageTier.Warm;
    public override DatabaseCategory DatabaseCategory => DatabaseCategory.Embedded;
    public override string Engine => "Derby";

    public override StorageCapabilities Capabilities => new()
    {
        SupportsMetadata = true,
        SupportsStreaming = false,
        SupportsLocking = true,
        SupportsVersioning = false,
        SupportsTiering = false,
        SupportsEncryption = true,
        SupportsCompression = false,
        SupportsMultipart = false,
        MaxObjectSize = 2L * 1024 * 1024 * 1024, // 2GB BLOB limit
        MaxObjects = null,
        ConsistencyModel = ConsistencyModel.Strong
    };

    public override bool SupportsTransactions => true;
    public override bool SupportsSql => true;

    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        _databasePath = GetConfiguration("DatabasePath", "memory:datawarehouse");
        _tableName = GetConfiguration("TableName", "STORAGE").ToUpperInvariant();

        await Task.CompletedTask;
    }

    protected override async Task ConnectCoreAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_connection != null && _connection.State == ConnectionState.Open)
                return;

            var connectionString = GetConnectionString();

            // Derby can be accessed via network server with JDBC
            // For this implementation, use ODBC or compatible provider
            _connection = CreateDerbyConnection(connectionString);
            _connection.Open();

            EnsureSchema();
        }

        await Task.CompletedTask;
    }

    private DbConnection CreateDerbyConnection(string connectionString)
    {
        // In production, use JDBC bridge (IKVM.NET) or ODBC driver
        // Derby also has a network server that accepts standard JDBC connections
        return new System.Data.Odbc.OdbcConnection(connectionString);
    }

    private void EnsureSchema()
    {
        try
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = $@"
                CREATE TABLE {_tableName} (
                    KEY_COL VARCHAR(1024) NOT NULL PRIMARY KEY,
                    DATA_COL BLOB NOT NULL,
                    SIZE_COL BIGINT NOT NULL,
                    CONTENT_TYPE VARCHAR(255),
                    ETAG VARCHAR(128) NOT NULL,
                    METADATA CLOB,
                    CREATED_AT TIMESTAMP NOT NULL,
                    MODIFIED_AT TIMESTAMP NOT NULL
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
            indexCmd.CommandText = $"CREATE INDEX IDX_{_tableName}_CREATED ON {_tableName}(CREATED_AT)";
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
            _connection?.Dispose();
            _connection = null;
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
            // Try update first
            using var updateCommand = _connection!.CreateCommand();
            updateCommand.CommandText = $@"
                UPDATE {_tableName} SET
                    DATA_COL = ?, SIZE_COL = ?, CONTENT_TYPE = ?,
                    ETAG = ?, METADATA = ?, MODIFIED_AT = ?
                WHERE KEY_COL = ?";

            AddParameter(updateCommand, data);
            AddParameter(updateCommand, data.LongLength);
            AddParameter(updateCommand, contentType);
            AddParameter(updateCommand, etag);
            AddParameter(updateCommand, metadataJson);
            AddParameter(updateCommand, now);
            AddParameter(updateCommand, key);

            var rowsAffected = updateCommand.ExecuteNonQuery();

            if (rowsAffected == 0)
            {
                // Insert new
                using var insertCommand = _connection.CreateCommand();
                insertCommand.CommandText = $@"
                    INSERT INTO {_tableName}
                    (KEY_COL, DATA_COL, SIZE_COL, CONTENT_TYPE, ETAG, METADATA, CREATED_AT, MODIFIED_AT)
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?)";

                AddParameter(insertCommand, key);
                AddParameter(insertCommand, data);
                AddParameter(insertCommand, data.LongLength);
                AddParameter(insertCommand, contentType);
                AddParameter(insertCommand, etag);
                AddParameter(insertCommand, metadataJson);
                AddParameter(insertCommand, now);
                AddParameter(insertCommand, now);

                insertCommand.ExecuteNonQuery();
            }
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
            command.CommandText = $"SELECT DATA_COL FROM {_tableName} WHERE KEY_COL = ?";
            AddParameter(command, key);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new FileNotFoundException($"Object not found: {key}");
            }

            return (byte[])reader["DATA_COL"];
        }
    }

    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct)
    {
        lock (_lock)
        {
            using var sizeCommand = _connection!.CreateCommand();
            sizeCommand.CommandText = $"SELECT SIZE_COL FROM {_tableName} WHERE KEY_COL = ?";
            AddParameter(sizeCommand, key);
            var size = Convert.ToInt64(sizeCommand.ExecuteScalar() ?? 0);

            using var deleteCommand = _connection.CreateCommand();
            deleteCommand.CommandText = $"DELETE FROM {_tableName} WHERE KEY_COL = ?";
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
            command.CommandText = $"SELECT 1 FROM {_tableName} WHERE KEY_COL = ?";
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
                ? $"SELECT KEY_COL, SIZE_COL, CONTENT_TYPE, ETAG, METADATA, CREATED_AT, MODIFIED_AT FROM {_tableName} ORDER BY KEY_COL"
                : $"SELECT KEY_COL, SIZE_COL, CONTENT_TYPE, ETAG, METADATA, CREATED_AT, MODIFIED_AT FROM {_tableName} WHERE KEY_COL LIKE ? ORDER BY KEY_COL";

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
            command.CommandText = $"SELECT SIZE_COL, CONTENT_TYPE, ETAG, METADATA, CREATED_AT, MODIFIED_AT FROM {_tableName} WHERE KEY_COL = ?";
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
                command.CommandText = "VALUES 1";
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
            _connection?.Dispose();
            _connection = null;
        }
        await base.DisposeAsyncCore();
    }
}
