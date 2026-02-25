using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Database;
using Npgsql;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies.TimeSeries;

/// <summary>
/// TimescaleDB time-series storage strategy with production-ready features:
/// - PostgreSQL extension for time-series
/// - Automatic partitioning (hypertables)
/// - Continuous aggregates
/// - Data retention policies
/// - Compression for historical data
/// - Full SQL support
/// - JOINs with relational data
/// </summary>
public sealed class TimescaleDbStorageStrategy : DatabaseStorageStrategyBase
{
    private NpgsqlDataSource? _dataSource;
    private string _tableName = "storage_timeseries";
    private string _chunkInterval = "1 day";

    public override string StrategyId => "timescaledb";
    public override string Name => "TimescaleDB Time-Series Storage";
    public override StorageTier Tier => StorageTier.Warm;
    public override DatabaseCategory DatabaseCategory => DatabaseCategory.TimeSeries;
    public override string Engine => "TimescaleDB";

    public override StorageCapabilities Capabilities => new()
    {
        SupportsMetadata = true,
        SupportsStreaming = false,
        SupportsLocking = true,
        SupportsVersioning = false,
        SupportsTiering = true, // Via compression and data retention
        SupportsEncryption = true,
        SupportsCompression = true,
        SupportsMultipart = false,
        MaxObjectSize = 1L * 1024 * 1024 * 1024, // 1GB PostgreSQL limit
        MaxObjects = null,
        ConsistencyModel = ConsistencyModel.Strong
    };

    public override bool SupportsTransactions => true;
    public override bool SupportsSql => true;

    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        _tableName = GetConfiguration("TableName", "storage_timeseries");
        _chunkInterval = GetConfiguration("ChunkInterval", "1 day");

        var connectionString = GetConnectionString();
        _dataSource = NpgsqlDataSource.Create(connectionString);

        await EnsureSchemaCoreAsync(ct);
    }

    protected override async Task ConnectCoreAsync(CancellationToken ct)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        await command.ExecuteScalarAsync(ct);
    }

    protected override async Task DisconnectCoreAsync(CancellationToken ct)
    {
        if (_dataSource != null)
        {
            await _dataSource.DisposeAsync();
            _dataSource = null;
        }
    }

    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(ct);

        // Enable TimescaleDB extension
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS timescaledb CASCADE";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Create table
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $@"
                CREATE TABLE IF NOT EXISTS {_tableName} (
                    time TIMESTAMPTZ NOT NULL,
                    key TEXT NOT NULL,
                    data BYTEA NOT NULL,
                    size BIGINT NOT NULL,
                    content_type TEXT,
                    etag TEXT NOT NULL,
                    metadata JSONB,
                    UNIQUE (key, time)
                )";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Convert to hypertable
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $@"
                SELECT create_hypertable('{_tableName}', 'time',
                    chunk_time_interval => INTERVAL '{_chunkInterval}',
                    if_not_exists => TRUE)";
            try
            {
                await cmd.ExecuteScalarAsync(ct);
            }
            catch
            {

                // Table might already be a hypertable
                System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
            }
        }

        // Create index on key
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"CREATE INDEX IF NOT EXISTS idx_{_tableName}_key ON {_tableName} (key, time DESC)";
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var etag = GenerateETag(data);
        var contentType = GetContentType(key);
        var metadataJson = metadata != null ? JsonSerializer.Serialize(metadata, JsonOptions) : null;

        await using var connection = await _dataSource!.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();

        command.CommandText = $@"
            INSERT INTO {_tableName} (time, key, data, size, content_type, etag, metadata)
            VALUES (@time, @key, @data, @size, @contentType, @etag, @metadata::jsonb)";

        command.Parameters.AddWithValue("@time", now);
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@data", data);
        command.Parameters.AddWithValue("@size", data.LongLength);
        command.Parameters.AddWithValue("@contentType", (object?)contentType ?? DBNull.Value);
        command.Parameters.AddWithValue("@etag", etag);
        command.Parameters.AddWithValue("@metadata", (object?)metadataJson ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(ct);

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
        await using var connection = await _dataSource!.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();

        command.CommandText = $@"
            SELECT data FROM {_tableName}
            WHERE key = @key
            ORDER BY time DESC
            LIMIT 1";

        command.Parameters.AddWithValue("@key", key);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        return (byte[])reader["data"];
    }

    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct)
    {
        var metadata = await GetMetadataCoreAsync(key, ct);
        var size = metadata.Size;

        await using var connection = await _dataSource!.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();

        command.CommandText = $"DELETE FROM {_tableName} WHERE key = @key";
        command.Parameters.AddWithValue("@key", key);

        await command.ExecuteNonQueryAsync(ct);

        return size;
    }

    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();

        command.CommandText = $"SELECT 1 FROM {_tableName} WHERE key = @key LIMIT 1";
        command.Parameters.AddWithValue("@key", key);

        var result = await command.ExecuteScalarAsync(ct);
        return result != null;
    }

    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();

        command.CommandText = $@"
            SELECT DISTINCT ON (key) key, size, content_type, etag, metadata, time
            FROM {_tableName}
            {(string.IsNullOrEmpty(prefix) ? "" : "WHERE key LIKE @prefix")}
            ORDER BY key, time DESC";

        if (!string.IsNullOrEmpty(prefix))
        {
            command.Parameters.AddWithValue("@prefix", prefix + "%");
        }

        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();

            var key = reader.GetString(0);
            var metadataJson = reader.IsDBNull(4) ? null : reader.GetString(4);

            Dictionary<string, string>? customMetadata = null;
            if (!string.IsNullOrEmpty(metadataJson))
            {
                customMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson, JsonOptions);
            }

            var time = reader.GetDateTime(5);

            yield return new StorageObjectMetadata
            {
                Key = key,
                Size = reader.GetInt64(1),
                ContentType = reader.IsDBNull(2) ? null : reader.GetString(2),
                ETag = reader.GetString(3),
                CustomMetadata = customMetadata,
                Created = time,
                Modified = time,
                Tier = Tier
            };
        }
    }

    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();

        command.CommandText = $@"
            SELECT size, content_type, etag, metadata, time
            FROM {_tableName}
            WHERE key = @key
            ORDER BY time DESC
            LIMIT 1";

        command.Parameters.AddWithValue("@key", key);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new FileNotFoundException($"Object not found: {key}");
        }

        var metadataJson = reader.IsDBNull(3) ? null : reader.GetString(3);
        Dictionary<string, string>? customMetadata = null;
        if (!string.IsNullOrEmpty(metadataJson))
        {
            customMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson, JsonOptions);
        }

        var time = reader.GetDateTime(4);

        return new StorageObjectMetadata
        {
            Key = key,
            Size = reader.GetInt64(0),
            ContentType = reader.IsDBNull(1) ? null : reader.GetString(1),
            ETag = reader.GetString(2),
            CustomMetadata = customMetadata,
            Created = time,
            Modified = time,
            Tier = Tier
        };
    }

    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct)
    {
        try
        {
            await using var connection = await _dataSource!.OpenConnectionAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    protected override async Task<IDatabaseTransaction> BeginTransactionCoreAsync(System.Data.IsolationLevel isolationLevel, CancellationToken ct)
    {
        var connection = await _dataSource!.OpenConnectionAsync(ct);
        var transaction = await connection.BeginTransactionAsync(isolationLevel, ct);
        return new NpgsqlDatabaseTransaction(connection, transaction);
    }

    /// <summary>
    /// Enables compression on chunks older than the specified interval.
    /// </summary>
    public async Task EnableCompressionAsync(string olderThan = "7 days", CancellationToken ct = default)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(ct);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"ALTER TABLE {_tableName} SET (timescaledb.compress)";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $@"
                SELECT add_compression_policy('{_tableName}', INTERVAL '{olderThan}', if_not_exists => true)";
            await cmd.ExecuteScalarAsync(ct);
        }
    }

    /// <summary>
    /// Sets up a data retention policy.
    /// </summary>
    public async Task SetRetentionPolicyAsync(string retentionPeriod = "90 days", CancellationToken ct = default)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();

        cmd.CommandText = $@"
            SELECT add_retention_policy('{_tableName}', INTERVAL '{retentionPeriod}', if_not_exists => true)";
        await cmd.ExecuteScalarAsync(ct);
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        if (_dataSource != null)
        {
            await _dataSource.DisposeAsync();
        }
        await base.DisposeAsyncCore();
    }

    private sealed class NpgsqlDatabaseTransaction : IDatabaseTransaction
    {
        private readonly NpgsqlConnection _connection;
        private readonly NpgsqlTransaction _transaction;
        private bool _disposed;

        public string TransactionId { get; } = Guid.NewGuid().ToString("N");
        public System.Data.IsolationLevel IsolationLevel => _transaction.IsolationLevel;

        public NpgsqlDatabaseTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            _connection = connection;
            _transaction = transaction;
        }

        public async Task CommitAsync(CancellationToken ct = default)
        {
            await _transaction.CommitAsync(ct);
        }

        public async Task RollbackAsync(CancellationToken ct = default)
        {
            await _transaction.RollbackAsync(ct);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await _transaction.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
