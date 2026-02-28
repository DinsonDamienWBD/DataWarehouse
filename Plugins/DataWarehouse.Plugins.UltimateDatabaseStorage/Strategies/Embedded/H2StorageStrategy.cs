using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Database;
using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies.Embedded;

/// <summary>
/// H2 embedded database storage strategy with production-ready features:
/// - Pure Java/JVM embedded database
/// - In-memory and persistent modes
/// - SQL support (ANSI SQL 2011)
/// - ACID transactions
/// - Clustering support
/// - Small footprint
/// - Compatible with most JDBC tools
/// </summary>
/// <remarks>
/// Note: This implementation uses JDBC through a .NET-Java bridge or H2 server mode.
/// For pure .NET, consider using SQLite or DuckDB instead.
/// </remarks>
public sealed class H2StorageStrategy : DatabaseStorageStrategyBase
{
    private DbConnection? _connection;
    private string _databasePath = "mem:datawarehouse";
    private string _tableName = "storage";
    private readonly object _lock = new();

    public override string StrategyId => "h2";
    public override bool IsProductionReady => false; // H2 is a JVM-based database; this implementation uses a .NET-Java bridge which is not yet production-ready
    public override string Name => "H2 Embedded Storage";
    public override StorageTier Tier => StorageTier.Hot;
    public override DatabaseCategory DatabaseCategory => DatabaseCategory.Embedded;
    public override string Engine => "H2";

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
        MaxObjectSize = 1L * 1024 * 1024 * 1024,
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

            // H2 can be accessed via TCP server mode with ODBC/JDBC bridge
            // For this implementation, we use the TCP server mode connection
            var connectionString = GetConnectionString();

            // Use a generic ODBC/ADO.NET provider that supports JDBC
            // In production, use appropriate JDBC bridge like IKVM.NET
            _connection = CreateH2Connection(connectionString);
            _connection.Open();

            EnsureSchema();
        }

        await Task.CompletedTask;
    }

    private DbConnection CreateH2Connection(string connectionString)
    {
        // In a real implementation, this would use JDBC bridge
        // For now, simulate with a compatible provider
        // H2 supports PostgreSQL wire protocol in recent versions
        var builder = new System.Data.Common.DbConnectionStringBuilder
        {
            ConnectionString = connectionString
        };

        // Default to Npgsql if H2 is in PostgreSQL mode
        return new Npgsql.NpgsqlConnection(connectionString);
    }

    private void EnsureSchema()
    {
        using var command = _connection!.CreateCommand();
        command.CommandText = $@"
            CREATE TABLE IF NOT EXISTS {_tableName} (
                key VARCHAR(1024) PRIMARY KEY,
                data BLOB NOT NULL,
                size BIGINT NOT NULL,
                content_type VARCHAR(255),
                etag VARCHAR(128) NOT NULL,
                metadata CLOB,
                created_at TIMESTAMP NOT NULL,
                modified_at TIMESTAMP NOT NULL
            )";
        command.ExecuteNonQuery();

        // Create index
        using var indexCmd = _connection.CreateCommand();
        indexCmd.CommandText = $"CREATE INDEX IF NOT EXISTS idx_{_tableName}_created ON {_tableName}(created_at)";
        indexCmd.ExecuteNonQuery();
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
            using var command = _connection!.CreateCommand();

            command.CommandText = $@"
                MERGE INTO {_tableName} (key, data, size, content_type, etag, metadata, created_at, modified_at)
                VALUES (@key, @data, @size, @contentType, @etag, @metadata, @createdAt, @modifiedAt)";

            AddParameter(command, "@key", key);
            AddParameter(command, "@data", data);
            AddParameter(command, "@size", data.LongLength);
            AddParameter(command, "@contentType", contentType);
            AddParameter(command, "@etag", etag);
            AddParameter(command, "@metadata", metadataJson);
            AddParameter(command, "@createdAt", now);
            AddParameter(command, "@modifiedAt", now);

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
            command.CommandText = $"SELECT data FROM {_tableName} WHERE key = @key";
            AddParameter(command, "@key", key);

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
            // Get size first
            using var sizeCommand = _connection!.CreateCommand();
            sizeCommand.CommandText = $"SELECT size FROM {_tableName} WHERE key = @key";
            AddParameter(sizeCommand, "@key", key);
            var size = Convert.ToInt64(sizeCommand.ExecuteScalar() ?? 0);

            // Delete
            using var deleteCommand = _connection.CreateCommand();
            deleteCommand.CommandText = $"DELETE FROM {_tableName} WHERE key = @key";
            AddParameter(deleteCommand, "@key", key);
            deleteCommand.ExecuteNonQuery();

            return size;
        }
    }

    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct)
    {
        lock (_lock)
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = $"SELECT 1 FROM {_tableName} WHERE key = @key";
            AddParameter(command, "@key", key);

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
                : $"SELECT key, size, content_type, etag, metadata, created_at, modified_at FROM {_tableName} WHERE key LIKE @prefix ORDER BY key";

            if (!string.IsNullOrEmpty(prefix))
            {
                AddParameter(command, "@prefix", prefix + "%");
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
            command.CommandText = $"SELECT size, content_type, etag, metadata, created_at, modified_at FROM {_tableName} WHERE key = @key";
            AddParameter(command, "@key", key);

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
                command.CommandText = "SELECT 1";
                command.ExecuteScalar();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var param = command.CreateParameter();
        param.ParameterName = name;
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
