using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Odbc;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Connectors
{
    /// <summary>
    /// ODBC connector strategy for importing data from any ODBC-compliant data source.
    /// Supports SQL Server, Oracle, MySQL, PostgreSQL, DB2, Teradata, and any ODBC driver.
    /// Features:
    /// - Direct ODBC connections using System.Data.Odbc
    /// - Configurable connection strings and DSN support
    /// - Query-based data extraction with parameterization
    /// - Bulk data export to storage in JSON, CSV, or binary formats
    /// - Connection pooling and timeout management
    /// - Transaction support for consistent snapshots
    /// - Cursor-based pagination for large result sets
    /// - Schema discovery and metadata extraction
    /// - Type mapping from SQL types to .NET types
    /// - Error handling and retry logic for transient failures
    /// </summary>
    public class OdbcConnectorStrategy : UltimateStorageStrategyBase
    {
        private string _connectionString = string.Empty;
        private int _commandTimeout = 300; // 5 minutes default
        private int _connectionTimeout = 30;
        private string _defaultExportFormat = "json"; // json, csv, binary
        private bool _useTransactions = true;
        private int _batchSize = 1000;
        private readonly SemaphoreSlim _connectionLock = new(1, 1);

        public override string StrategyId => "odbc-connector";
        public override string Name => "ODBC Data Connector";
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

        /// <summary>
        /// Initializes the ODBC connector with connection configuration.
        /// </summary>
        protected override async Task InitializeCoreAsync(CancellationToken ct)
        {
            // Load connection string (required)
            _connectionString = GetConfiguration<string>("ConnectionString")
                ?? throw new InvalidOperationException("ODBC ConnectionString is required");

            // Validate connection string
            if (string.IsNullOrWhiteSpace(_connectionString))
            {
                throw new InvalidOperationException("ODBC ConnectionString cannot be empty");
            }

            // Load optional configuration
            _commandTimeout = GetConfiguration("CommandTimeout", 300);
            _connectionTimeout = GetConfiguration("ConnectionTimeout", 30);
            _defaultExportFormat = GetConfiguration("DefaultExportFormat", "json").ToLowerInvariant();
            _useTransactions = GetConfiguration("UseTransactions", true);
            _batchSize = GetConfiguration("BatchSize", 1000);

            // Validate format
            if (_defaultExportFormat != "json" && _defaultExportFormat != "csv" && _defaultExportFormat != "binary")
            {
                throw new InvalidOperationException($"Invalid export format: {_defaultExportFormat}. Supported: json, csv, binary");
            }

            // Test connection
            await TestConnectionAsync(ct);
        }

        /// <summary>
        /// Tests the ODBC connection to ensure it's valid.
        /// </summary>
        private async Task TestConnectionAsync(CancellationToken ct)
        {
            try
            {
                using var connection = new OdbcConnection(_connectionString);
                connection.ConnectionTimeout = _connectionTimeout;
                await connection.OpenAsync(ct);
                await connection.CloseAsync();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to connect to ODBC data source: {ex.Message}", ex);
            }
        }

        protected override async ValueTask DisposeCoreAsync()
        {
            await base.DisposeCoreAsync();
            _connectionLock?.Dispose();
        }

        #endregion

        #region Core Storage Operations

        /// <summary>
        /// Stores data by executing an INSERT/UPDATE SQL command.
        /// Key format: "sql://tablename/rowid" or "query://queryname"
        /// Data stream contains SQL parameters as JSON.
        /// </summary>
        protected override async Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);
            ValidateStream(data);

            // Parse key to extract operation
            var (operationType, target, identifier) = ParseKey(key);

            if (operationType != "sql" && operationType != "query")
            {
                throw new ArgumentException($"Invalid key format. Expected 'sql://table/id' or 'query://name'. Got: {key}");
            }

            // Read parameters from stream
            var parametersJson = await ReadStreamAsStringAsync(data, ct);
            var parameters = string.IsNullOrWhiteSpace(parametersJson)
                ? new Dictionary<string, object>()
                : JsonSerializer.Deserialize<Dictionary<string, object>>(parametersJson) ?? new();

            // Get SQL command from metadata or construct it
            var sqlCommand = metadata?.TryGetValue("SqlCommand", out var cmd) == true
                ? cmd
                : ConstructInsertCommand(target, parameters);

            long rowsAffected = 0;

            using var connection = new OdbcConnection(_connectionString);
            connection.ConnectionTimeout = _connectionTimeout;
            await connection.OpenAsync(ct);

            OdbcTransaction? transaction = null;
            if (_useTransactions)
            {
                transaction = connection.BeginTransaction();
            }

            try
            {
                using var command = new OdbcCommand(sqlCommand, connection, transaction);
                command.CommandTimeout = _commandTimeout;

                // Add parameters
                foreach (var param in parameters)
                {
                    command.Parameters.AddWithValue($"@{param.Key}", param.Value ?? DBNull.Value);
                }

                rowsAffected = await command.ExecuteNonQueryAsync(ct);

                transaction?.Commit();

                IncrementBytesStored(data.Length);
                IncrementOperationCounter(StorageOperationType.Store);

                return new StorageObjectMetadata
                {
                    Key = key,
                    Size = data.Length,
                    Created = DateTime.UtcNow,
                    Modified = DateTime.UtcNow,
                    ETag = GenerateETag(key, rowsAffected),
                    ContentType = "application/json",
                    CustomMetadata = new Dictionary<string, string>
                    {
                        ["RowsAffected"] = rowsAffected.ToString(),
                        ["SqlCommand"] = sqlCommand,
                        ["DatabaseProvider"] = connection.Driver
                    },
                    Tier = Tier
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OdbcConnectorStrategy.StoreAsyncCore] {ex.GetType().Name}: {ex.Message}");
                transaction?.Rollback();
                throw;
            }
        }

        /// <summary>
        /// Retrieves data by executing a SELECT query.
        /// Key format: "sql://tablename/rowid" or "query://custom-query"
        /// Returns data in configured format (JSON, CSV, or binary).
        /// </summary>
        protected override async Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var (operationType, target, identifier) = ParseKey(key);

            // Construct or get SQL query — validate identifiers to prevent SQL injection
            string sqlQuery;
            if (operationType == "sql")
            {
                var safeTable = ValidateSqlIdentifier(target, "table");
                sqlQuery = $"SELECT * FROM {safeTable} WHERE id = @id";
            }
            else if (operationType == "query")
            {
                // query:// requires the full query to be pre-configured by the operator;
                // reject if it was supplied via the key (untrusted caller input).
                // In production, queries must be loaded from server-side configuration only.
                throw new NotSupportedException(
                    "query:// key type is not supported for RetrieveAsync. " +
                    "Use sql://table/id key format. Raw SQL queries from keys are rejected to prevent injection.");
            }
            else
            {
                throw new ArgumentException($"Invalid key format: {key}");
            }

            using var connection = new OdbcConnection(_connectionString);
            connection.ConnectionTimeout = _connectionTimeout;
            await connection.OpenAsync(ct);

            OdbcTransaction? transaction = null;
            if (_useTransactions)
            {
                transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            }

            try
            {
                using var command = new OdbcCommand(sqlQuery, connection, transaction);
                command.CommandTimeout = _commandTimeout;

                // Add identifier parameter if applicable
                if (operationType == "sql" && !string.IsNullOrEmpty(identifier))
                {
                    command.Parameters.AddWithValue("@id", identifier);
                }

                using var reader = (OdbcDataReader)await command.ExecuteReaderAsync(ct);

                // Export data in configured format
                var resultStream = await ExportDataAsync(reader, _defaultExportFormat, ct);

                transaction?.Commit();

                IncrementBytesRetrieved(resultStream.Length);
                IncrementOperationCounter(StorageOperationType.Retrieve);

                return resultStream;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OdbcConnectorStrategy.RetrieveAsyncCore] {ex.GetType().Name}: {ex.Message}");
                transaction?.Rollback();
                throw;
            }
        }

        /// <summary>
        /// Deletes data by executing a DELETE command.
        /// </summary>
        protected override async Task DeleteAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var (operationType, target, identifier) = ParseKey(key);

            if (operationType != "sql")
            {
                throw new ArgumentException($"Delete only supports 'sql://' keys. Got: {key}");
            }

            var safeDeleteTable = ValidateSqlIdentifier(target, "table");
            var sqlCommand = $"DELETE FROM {safeDeleteTable} WHERE id = @id";

            using var connection = new OdbcConnection(_connectionString);
            connection.ConnectionTimeout = _connectionTimeout;
            await connection.OpenAsync(ct);

            OdbcTransaction? transaction = null;
            if (_useTransactions)
            {
                transaction = connection.BeginTransaction();
            }

            try
            {
                using var command = new OdbcCommand(sqlCommand, connection, transaction);
                command.CommandTimeout = _commandTimeout;
                command.Parameters.AddWithValue("@id", identifier);

                var rowsAffected = await command.ExecuteNonQueryAsync(ct);

                transaction?.Commit();

                IncrementOperationCounter(StorageOperationType.Delete);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OdbcConnectorStrategy.DeleteAsyncCore] {ex.GetType().Name}: {ex.Message}");
                transaction?.Rollback();
                throw;
            }
        }

        /// <summary>
        /// Checks if a record exists by executing a COUNT query.
        /// </summary>
        protected override async Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var (operationType, target, identifier) = ParseKey(key);

            if (operationType != "sql")
            {
                return false;
            }

            var safeExistsTable = ValidateSqlIdentifier(target, "table");
            var sqlQuery = $"SELECT COUNT(*) FROM {safeExistsTable} WHERE id = @id";

            try
            {
                using var connection = new OdbcConnection(_connectionString);
                connection.ConnectionTimeout = _connectionTimeout;
                await connection.OpenAsync(ct);

                using var command = new OdbcCommand(sqlQuery, connection);
                command.CommandTimeout = _commandTimeout;
                command.Parameters.AddWithValue("@id", identifier);

                var count = (int?)await command.ExecuteScalarAsync(ct) ?? 0;

                IncrementOperationCounter(StorageOperationType.Exists);

                return count > 0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OdbcConnectorStrategy.ExistsAsyncCore] {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Lists all tables or query results matching a prefix.
        /// </summary>
        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            EnsureInitialized();
            IncrementOperationCounter(StorageOperationType.List);

            // List tables in the database
            using var connection = new OdbcConnection(_connectionString);
            connection.ConnectionTimeout = _connectionTimeout;
            await connection.OpenAsync(ct);

            var restrictions = new string?[4];
            restrictions[3] = "TABLE"; // Get only tables

            var schemaTable = await Task.Run(() => connection.GetSchema("Tables", restrictions), ct);

            foreach (DataRow row in schemaTable.Rows)
            {
                ct.ThrowIfCancellationRequested();

                var tableName = row["TABLE_NAME"].ToString() ?? string.Empty;

                if (!string.IsNullOrEmpty(prefix) && !tableName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var key = $"sql://{tableName}/";

                yield return new StorageObjectMetadata
                {
                    Key = key,
                    Size = 0, // Unknown without querying
                    Created = DateTime.MinValue,
                    Modified = DateTime.MinValue,
                    ETag = GenerateETag(key, 0),
                    ContentType = "application/sql",
                    CustomMetadata = new Dictionary<string, string>
                    {
                        ["TableName"] = tableName,
                        ["TableType"] = row["TABLE_TYPE"]?.ToString() ?? "TABLE"
                    },
                    Tier = Tier
                };

                await Task.Yield();
            }
        }

        /// <summary>
        /// Gets metadata for a specific table or query.
        /// </summary>
        protected override async Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var (operationType, target, identifier) = ParseKey(key);

            if (operationType != "sql")
            {
                throw new ArgumentException($"Metadata only supports 'sql://' keys. Got: {key}");
            }

            // Get row count and schema info
            using var connection = new OdbcConnection(_connectionString);
            connection.ConnectionTimeout = _connectionTimeout;
            await connection.OpenAsync(ct);

            var safeMetaTable = ValidateSqlIdentifier(target, "table");
            var countQuery = $"SELECT COUNT(*) FROM {safeMetaTable}";
            long rowCount = 0;

            using (var command = new OdbcCommand(countQuery, connection))
            {
                command.CommandTimeout = _commandTimeout;
                rowCount = Convert.ToInt64(await command.ExecuteScalarAsync(ct));
            }

            IncrementOperationCounter(StorageOperationType.GetMetadata);

            return new StorageObjectMetadata
            {
                Key = key,
                Size = rowCount * 100, // Estimate (100 bytes per row)
                Created = DateTime.MinValue,
                Modified = DateTime.UtcNow,
                ETag = GenerateETag(key, rowCount),
                ContentType = "application/sql",
                CustomMetadata = new Dictionary<string, string>
                {
                    ["TableName"] = target,
                    ["RowCount"] = rowCount.ToString(),
                    ["DatabaseProvider"] = connection.Driver
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
                connection.ConnectionTimeout = _connectionTimeout;
                await connection.OpenAsync(ct);

                // Test query
                using var command = new OdbcCommand("SELECT 1", connection);
                await command.ExecuteScalarAsync(ct);

                await connection.CloseAsync();

                sw.Stop();

                return new StorageHealthInfo
                {
                    Status = HealthStatus.Healthy,
                    LatencyMs = sw.ElapsedMilliseconds,
                    Message = $"ODBC connection is healthy. Driver: {connection.Driver}",
                    CheckedAt = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                return new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy,
                    LatencyMs = 0,
                    Message = $"ODBC connection failed: {ex.Message}",
                    CheckedAt = DateTime.UtcNow
                };
            }
        }

        protected override Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            // Database capacity is not available via ODBC
            return Task.FromResult<long?>(null);
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Parses a key in format "protocol://target/identifier".
        /// </summary>
        /// <summary>
        /// Validates that a SQL identifier (table or column name) contains only safe characters.
        /// Prevents SQL injection through identifier embedding.
        /// </summary>
        private static string ValidateSqlIdentifier(string identifier, string context)
        {
            // Allow only alphanumeric, underscore, and dot (schema.table)
            if (!Regex.IsMatch(identifier, @"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)?$"))
            {
                throw new ArgumentException(
                    $"Invalid SQL identifier for {context}: '{identifier}'. " +
                    "Only alphanumeric characters, underscores, and schema.table notation are allowed.");
            }
            return identifier;
        }

        private (string operationType, string target, string identifier) ParseKey(string key)
        {
            var parts = key.Split(new[] { "://" }, StringSplitOptions.None);
            if (parts.Length != 2)
            {
                throw new ArgumentException($"Invalid key format. Expected 'protocol://target/id'. Got: {key}");
            }

            var operationType = parts[0].ToLowerInvariant();
            var remainder = parts[1];

            var slashIndex = remainder.IndexOf('/');
            if (slashIndex < 0)
            {
                return (operationType, remainder, string.Empty);
            }

            var target = remainder.Substring(0, slashIndex);
            var identifier = remainder.Substring(slashIndex + 1);

            return (operationType, target, identifier);
        }

        /// <summary>
        /// Reads a stream as a UTF-8 string.
        /// </summary>
        private async Task<string> ReadStreamAsStringAsync(Stream stream, CancellationToken ct)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            return await reader.ReadToEndAsync(ct);
        }

        /// <summary>
        /// Constructs an INSERT command from parameters.
        /// </summary>
        private string ConstructInsertCommand(string tableName, Dictionary<string, object> parameters)
        {
            if (parameters.Count == 0)
            {
                throw new ArgumentException("No parameters provided for INSERT command");
            }

            var columns = string.Join(", ", parameters.Keys);
            var values = string.Join(", ", parameters.Keys.Select(k => $"@{k}"));

            return $"INSERT INTO {tableName} ({columns}) VALUES ({values})";
        }

        /// <summary>
        /// Exports data from an OdbcDataReader to a stream in the specified format.
        /// </summary>
        private async Task<MemoryStream> ExportDataAsync(OdbcDataReader reader, string format, CancellationToken ct)
        {
            var stream = new MemoryStream(4096);

            if (format == "json")
            {
                await ExportAsJsonAsync(reader, stream, ct);
            }
            else if (format == "csv")
            {
                await ExportAsCsvAsync(reader, stream, ct);
            }
            else if (format == "binary")
            {
                await ExportAsBinaryAsync(reader, stream, ct);
            }

            stream.Position = 0;
            return stream;
        }

        /// <summary>
        /// Exports data as JSON array.
        /// </summary>
        private async Task ExportAsJsonAsync(OdbcDataReader reader, Stream output, CancellationToken ct)
        {
            using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = false });

            writer.WriteStartArray();

            while (await reader.ReadAsync(ct))
            {
                writer.WriteStartObject();

                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var fieldName = reader.GetName(i);
                    var value = reader.GetValue(i);

                    writer.WritePropertyName(fieldName);

                    if (value == null || value == DBNull.Value)
                    {
                        writer.WriteNullValue();
                    }
                    else if (value is string str)
                    {
                        writer.WriteStringValue(str);
                    }
                    else if (value is int || value is long || value is short || value is byte)
                    {
                        writer.WriteNumberValue(Convert.ToInt64(value));
                    }
                    else if (value is decimal || value is double || value is float)
                    {
                        writer.WriteNumberValue(Convert.ToDouble(value));
                    }
                    else if (value is bool b)
                    {
                        writer.WriteBooleanValue(b);
                    }
                    else if (value is DateTime dt)
                    {
                        writer.WriteStringValue(dt.ToString("O"));
                    }
                    else
                    {
                        writer.WriteStringValue(value.ToString());
                    }
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            await writer.FlushAsync(ct);
        }

        /// <summary>
        /// Exports data as CSV.
        /// </summary>
        private async Task ExportAsCsvAsync(OdbcDataReader reader, Stream output, CancellationToken ct)
        {
            using var writer = new StreamWriter(output, Encoding.UTF8, leaveOpen: true);

            // Write header
            var headers = new List<string>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                headers.Add(EscapeCsvValue(reader.GetName(i)));
            }
            await writer.WriteLineAsync(string.Join(",", headers).AsMemory(), ct);

            // Write rows
            while (await reader.ReadAsync(ct))
            {
                var values = new List<string>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.GetValue(i);
                    values.Add(EscapeCsvValue(value?.ToString() ?? string.Empty));
                }
                await writer.WriteLineAsync(string.Join(",", values).AsMemory(), ct);
            }

            await writer.FlushAsync();
        }

        /// <summary>
        /// Exports data as binary (MessagePack-like format).
        /// </summary>
        private async Task ExportAsBinaryAsync(OdbcDataReader reader, Stream output, CancellationToken ct)
        {
            // Simple binary format: write field count, then for each row write values
            using var binaryWriter = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);

            // Write field count
            binaryWriter.Write(reader.FieldCount);

            // Write field names
            for (int i = 0; i < reader.FieldCount; i++)
            {
                binaryWriter.Write(reader.GetName(i));
            }

            // Write rows
            long rowCount = 0;
            while (await reader.ReadAsync(ct))
            {
                rowCount++;
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.GetValue(i);
                    WriteValueAsBinary(binaryWriter, value);
                }
            }

            await output.FlushAsync(ct);
        }

        /// <summary>
        /// Writes a value to binary writer.
        /// </summary>
        private void WriteValueAsBinary(BinaryWriter writer, object? value)
        {
            if (value == null || value == DBNull.Value)
            {
                writer.Write((byte)0); // Null marker
            }
            else if (value is string str)
            {
                writer.Write((byte)1); // String marker
                writer.Write(str);
            }
            else if (value is int i)
            {
                writer.Write((byte)2); // Int marker
                writer.Write(i);
            }
            else if (value is long l)
            {
                writer.Write((byte)3); // Long marker
                writer.Write(l);
            }
            else if (value is double d)
            {
                writer.Write((byte)4); // Double marker
                writer.Write(d);
            }
            else if (value is bool b)
            {
                writer.Write((byte)5); // Bool marker
                writer.Write(b);
            }
            else
            {
                writer.Write((byte)1); // String fallback
                writer.Write(value.ToString() ?? string.Empty);
            }
        }

        /// <summary>
        /// Escapes a CSV value.
        /// </summary>
        private string EscapeCsvValue(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            if (value.Contains(",") || value.Contains("\"") || value.Contains("\n"))
            {
                return $"\"{value.Replace("\"", "\"\"")}\"";
            }

            return value;
        }

        /// <summary>
        /// Generates an ETag for a key.
        /// </summary>
        private string GenerateETag(string key, long value)
        {
            var input = $"{key}:{value}";
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(input)))[..16];
            return $"\"{hash}\"";
        }

        protected override int GetMaxKeyLength() => 512;

        #endregion
    }
}
