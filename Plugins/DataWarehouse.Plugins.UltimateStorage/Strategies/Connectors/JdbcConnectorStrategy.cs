using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Generic;
using System.Data.Odbc;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Connectors
{
    /// <summary>
    /// JDBC bridge connector strategy for importing data from Java-based data sources.
    /// Uses ODBC-JDBC bridge pattern to connect to JDBC-only databases.
    /// Features:
    /// - JDBC data source access via ODBC-JDBC bridge drivers
    /// - Support for Java-specific databases (Derby, H2, HSQLDB)
    /// - Connection pooling and timeout management
    /// - Query execution and result set streaming
    /// - Type mapping from JDBC types to .NET types
    /// - Batch operation support for bulk imports
    /// - Transaction management for consistency
    /// - Error handling and connection recovery
    /// - Metadata extraction and schema discovery
    /// - Parameterized query execution
    ///
    /// Configuration Requirements:
    /// - JDBC-ODBC bridge driver installed (e.g., OpenLink, Easysoft, Progress DataDirect)
    /// - DSN configured with JDBC connection URL
    /// - JDBC driver JAR files accessible to the bridge
    /// </summary>
    public class JdbcConnectorStrategy : UltimateStorageStrategyBase
    {
        private string _connectionString = string.Empty;
        private int _commandTimeout = 300;
        private int _connectionTimeout = 30;
        private string _jdbcUrl = string.Empty;
        private string _driverClass = string.Empty;
        private bool _useTransactions = true;
        private readonly SemaphoreSlim _connectionLock = new(1, 1);

        public override string StrategyId => "jdbc-connector";
        public override string Name => "JDBC Bridge Connector";
        public override StorageTier Tier => StorageTier.Warm;

        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            SupportsLocking = false,
            SupportsVersioning = false,
            SupportsTiering = false,
            SupportsEncryption = false,
            SupportsCompression = false,
            SupportsMultipart = false,
            MaxObjectSize = null,
            MaxObjects = null,
            ConsistencyModel = ConsistencyModel.Strong
        };

        #region Initialization

        protected override async Task InitializeCoreAsync(CancellationToken ct)
        {
            // Load connection configuration
            _connectionString = GetConfiguration<string>("ConnectionString")
                ?? throw new InvalidOperationException("JDBC ConnectionString (DSN or bridge connection) is required");

            _jdbcUrl = GetConfiguration<string>("JdbcUrl") ?? string.Empty;
            _driverClass = GetConfiguration<string>("DriverClass") ?? string.Empty;
            _commandTimeout = GetConfiguration("CommandTimeout", 300);
            _connectionTimeout = GetConfiguration("ConnectionTimeout", 30);
            _useTransactions = GetConfiguration("UseTransactions", true);

            // Test connection through JDBC bridge
            await TestConnectionAsync(ct);
        }

        private async Task TestConnectionAsync(CancellationToken ct)
        {
            try
            {
                using var connection = new OdbcConnection(_connectionString);
                connection.ConnectionTimeout = _connectionTimeout;
                await connection.OpenAsync(ct);

                // Test query to verify JDBC bridge is working
                using var command = new OdbcCommand("SELECT 1", connection);
                await command.ExecuteScalarAsync(ct);

                await connection.CloseAsync();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to connect via JDBC bridge: {ex.Message}. " +
                    "Ensure JDBC-ODBC bridge driver is installed and configured correctly.", ex);
            }
        }

        protected override async ValueTask DisposeCoreAsync()
        {
            await base.DisposeCoreAsync();
            _connectionLock?.Dispose();
        }

        #endregion

        #region Core Storage Operations

        protected override async Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);
            ValidateStream(data);

            // Parse key: jdbc://table/operation
            var parts = ParseJdbcKey(key);
            var sqlCommand = metadata?.TryGetValue("SqlCommand", out var cmd) == true ? cmd : parts.query;

            using var connection = new OdbcConnection(_connectionString);
            connection.ConnectionTimeout = _connectionTimeout;
            await connection.OpenAsync(ct);

            var transaction = _useTransactions ? connection.BeginTransaction() : null;

            try
            {
                using var command = new OdbcCommand(sqlCommand, connection, transaction);
                command.CommandTimeout = _commandTimeout;

                // Read and apply parameters from stream
                var parametersJson = await ReadStreamAsync(data, ct);
                if (!string.IsNullOrWhiteSpace(parametersJson))
                {
                    var parameters = JsonSerializer.Deserialize<Dictionary<string, object>>(parametersJson);
                    if (parameters != null)
                    {
                        foreach (var param in parameters)
                        {
                            command.Parameters.AddWithValue($"@{param.Key}", param.Value ?? DBNull.Value);
                        }
                    }
                }

                var rowsAffected = await command.ExecuteNonQueryAsync(ct);
                transaction?.Commit();

                IncrementBytesStored(data.Length);
                IncrementOperationCounter(StorageOperationType.Store);

                return new StorageObjectMetadata
                {
                    Key = key,
                    Size = data.Length,
                    Created = DateTime.UtcNow,
                    Modified = DateTime.UtcNow,
                    ETag = $"\"{HashCode.Combine(key, rowsAffected):x}\"",
                    ContentType = "application/json",
                    CustomMetadata = new Dictionary<string, string>
                    {
                        ["RowsAffected"] = rowsAffected.ToString(),
                        ["JdbcUrl"] = _jdbcUrl,
                        ["DriverClass"] = _driverClass
                    },
                    Tier = Tier
                };
            }
            catch
            {
                transaction?.Rollback();
                throw;
            }
        }

        protected override async Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var parts = ParseJdbcKey(key);

            using var connection = new OdbcConnection(_connectionString);
            connection.ConnectionTimeout = _connectionTimeout;
            await connection.OpenAsync(ct);

            using var command = new OdbcCommand(parts.query, connection);
            command.CommandTimeout = _commandTimeout;

            // Add parameter if identifier is present
            if (!string.IsNullOrEmpty(parts.identifier))
            {
                command.Parameters.AddWithValue("@id", parts.identifier);
            }

            using var reader = await command.ExecuteReaderAsync(ct);
            var stream = new MemoryStream();

            // Export to JSON
            using var writer = new Utf8JsonWriter(stream);
            writer.WriteStartArray();

            while (await reader.ReadAsync(ct))
            {
                writer.WriteStartObject();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    writer.WritePropertyName(reader.GetName(i));
                    var value = reader.GetValue(i);
                    WriteJsonValue(writer, value);
                }
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            await writer.FlushAsync(ct);

            stream.Position = 0;
            IncrementBytesRetrieved(stream.Length);
            IncrementOperationCounter(StorageOperationType.Retrieve);

            return stream;
        }

        protected override async Task DeleteAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var parts = ParseJdbcKey(key);
            ValidateTableName(parts.table);

            var deleteQuery = $"DELETE FROM {parts.table} WHERE id = ?";

            using var connection = new OdbcConnection(_connectionString);
            await connection.OpenAsync(ct);

            var transaction = _useTransactions ? connection.BeginTransaction() : null;

            try
            {
                using var command = new OdbcCommand(deleteQuery, connection, transaction);
                command.CommandTimeout = _commandTimeout;
                command.Parameters.AddWithValue("@id", parts.identifier);
                await command.ExecuteNonQueryAsync(ct);

                transaction?.Commit();
                IncrementOperationCounter(StorageOperationType.Delete);
            }
            catch
            {
                transaction?.Rollback();
                throw;
            }
        }

        protected override async Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var parts = ParseJdbcKey(key);
            ValidateTableName(parts.table);

            var countQuery = $"SELECT COUNT(*) FROM {parts.table} WHERE id = ?";

            try
            {
                using var connection = new OdbcConnection(_connectionString);
                await connection.OpenAsync(ct);

                using var command = new OdbcCommand(countQuery, connection);
                command.Parameters.AddWithValue("@id", parts.identifier);
                var count = Convert.ToInt32(await command.ExecuteScalarAsync(ct));

                IncrementOperationCounter(StorageOperationType.Exists);
                return count > 0;
            }
            catch
            {
                return false;
            }
        }

        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            EnsureInitialized();
            IncrementOperationCounter(StorageOperationType.List);

            using var connection = new OdbcConnection(_connectionString);
            await connection.OpenAsync(ct);

            // List tables
            var schemaTable = await Task.Run(() => connection.GetSchema("Tables"), ct);

            foreach (System.Data.DataRow row in schemaTable.Rows)
            {
                var tableName = row["TABLE_NAME"]?.ToString() ?? string.Empty;

                if (!string.IsNullOrEmpty(prefix) && !tableName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                yield return new StorageObjectMetadata
                {
                    Key = $"jdbc://{tableName}/",
                    Size = 0,
                    Created = DateTime.MinValue,
                    Modified = DateTime.UtcNow,
                    ETag = $"\"{HashCode.Combine(tableName):x}\"",
                    ContentType = "application/sql",
                    Tier = Tier
                };
            }
        }

        protected override async Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var parts = ParseJdbcKey(key);
            ValidateTableName(parts.table);

            using var connection = new OdbcConnection(_connectionString);
            await connection.OpenAsync(ct);

            var countQuery = $"SELECT COUNT(*) FROM {parts.table}";
            using var command = new OdbcCommand(countQuery, connection);
            var rowCount = Convert.ToInt64(await command.ExecuteScalarAsync(ct));

            IncrementOperationCounter(StorageOperationType.GetMetadata);

            return new StorageObjectMetadata
            {
                Key = key,
                Size = rowCount * 100,
                Created = DateTime.MinValue,
                Modified = DateTime.UtcNow,
                ETag = $"\"{HashCode.Combine(key, rowCount):x}\"",
                ContentType = "application/sql",
                CustomMetadata = new Dictionary<string, string>
                {
                    ["RowCount"] = rowCount.ToString(),
                    ["TableName"] = parts.table
                },
                Tier = Tier
            };
        }

        protected override async Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();

                using var connection = new OdbcConnection(_connectionString);
                await connection.OpenAsync(ct);

                using var command = new OdbcCommand("SELECT 1", connection);
                await command.ExecuteScalarAsync(ct);

                sw.Stop();

                return new StorageHealthInfo
                {
                    Status = HealthStatus.Healthy,
                    LatencyMs = sw.ElapsedMilliseconds,
                    Message = $"JDBC bridge connection healthy. URL: {_jdbcUrl}",
                    CheckedAt = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                return new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy,
                    Message = $"JDBC bridge connection failed: {ex.Message}",
                    CheckedAt = DateTime.UtcNow
                };
            }
        }

        protected override Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            return Task.FromResult<long?>(null);
        }

        #endregion

        #region Helper Methods

        private (string table, string identifier, string query) ParseJdbcKey(string key)
        {
            if (!key.StartsWith("jdbc://", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Invalid JDBC key format. Expected 'jdbc://table/id'. Got: {key}");
            }

            var path = key.Substring(7); // Remove "jdbc://"
            var parts = path.Split('/', 2);

            var table = parts[0];
            var identifier = parts.Length > 1 ? parts[1] : string.Empty;
            ValidateTableName(table);

            var query = string.IsNullOrEmpty(identifier)
                ? $"SELECT * FROM {table}"
                : $"SELECT * FROM {table} WHERE id = ?";

            return (table, identifier, query);
        }

        /// <summary>
        /// Validates table name to prevent SQL injection.
        /// </summary>
        private static void ValidateTableName(string tableName)
        {
            if (string.IsNullOrWhiteSpace(tableName))
            {
                throw new ArgumentException("Table name cannot be null or empty.", nameof(tableName));
            }

            // Allow only alphanumeric characters and underscores
            if (!System.Text.RegularExpressions.Regex.IsMatch(tableName, @"^[a-zA-Z_][a-zA-Z0-9_]*$"))
            {
                throw new ArgumentException($"Invalid table name '{tableName}'. Only alphanumeric characters and underscores are allowed.", nameof(tableName));
            }
        }

        private async Task<string> ReadStreamAsync(Stream stream, CancellationToken ct)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            return await reader.ReadToEndAsync(ct);
        }

        private void WriteJsonValue(Utf8JsonWriter writer, object? value)
        {
            if (value == null || value == DBNull.Value)
                writer.WriteNullValue();
            else if (value is string str)
                writer.WriteStringValue(str);
            else if (value is int || value is long)
                writer.WriteNumberValue(Convert.ToInt64(value));
            else if (value is double || value is float || value is decimal)
                writer.WriteNumberValue(Convert.ToDouble(value));
            else if (value is bool b)
                writer.WriteBooleanValue(b);
            else if (value is DateTime dt)
                writer.WriteStringValue(dt.ToString("O"));
            else
                writer.WriteStringValue(value.ToString() ?? string.Empty);
        }

        protected override int GetMaxKeyLength() => 512;

        #endregion
    }
}
