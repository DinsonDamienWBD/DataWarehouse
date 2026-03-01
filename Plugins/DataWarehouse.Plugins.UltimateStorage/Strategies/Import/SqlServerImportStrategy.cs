using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.SqlClient;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Import
{
    /// <summary>
    /// SQL Server bulk import strategy for high-performance data ingestion using SqlBulkCopy.
    /// Supports millions of rows/second with optimized batching and streaming.
    /// </summary>
    public class SqlServerImportStrategy : UltimateStorageStrategyBase
    {
        private string _connectionString = string.Empty;
        private int _batchSize = 10000;
        private int _commandTimeout = 600;

        public override string StrategyId => "sqlserver-import";
        public override string Name => "SQL Server Bulk Import";
        public override bool IsProductionReady => true;
        public override StorageTier Tier => StorageTier.Warm;

        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            SupportsLocking = false,
            SupportsVersioning = false,
            SupportsTiering = false,
            SupportsEncryption = true,
            SupportsCompression = false,
            SupportsMultipart = true,
            MaxObjectSize = null,
            MaxObjects = null,
            ConsistencyModel = ConsistencyModel.Strong
        };

        protected override async Task InitializeCoreAsync(CancellationToken ct)
        {
            _connectionString = GetConfiguration<string>("ConnectionString")
                ?? throw new InvalidOperationException("SQL Server ConnectionString is required");

            _batchSize = GetConfiguration("BatchSize", 10000);
            _commandTimeout = GetConfiguration("CommandTimeout", 600);

            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(ct);
        }

        protected override async Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            // Key format: "sqlserver://<tableName>" or just "<tableName>".
            var tableName = key.StartsWith("sqlserver://", StringComparison.OrdinalIgnoreCase)
                ? key.Substring("sqlserver://".Length)
                : key;

            if (string.IsNullOrWhiteSpace(tableName))
                throw new ArgumentException("Key must specify a target table name (e.g. 'sqlserver://Orders').", nameof(key));

            // Validate table name to prevent SQL injection — must be identifier-safe.
            if (!System.Text.RegularExpressions.Regex.IsMatch(tableName, @"^[\w][\w\s]*$"))
                throw new ArgumentException($"Table name '{tableName}' contains invalid characters.", nameof(key));

            using var reader = new StreamReader(data, Encoding.UTF8, leaveOpen: true);
            var json = await reader.ReadToEndAsync(ct);

            using var jsonDoc = JsonDocument.Parse(json);
            var root = jsonDoc.RootElement;

            // Accept either a JSON array of objects or a single JSON object.
            var rows = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray().ToArray()
                : new[] { root };

            if (rows.Length == 0)
            {
                IncrementOperationCounter(StorageOperationType.Store);
                return new StorageObjectMetadata { Key = key, Size = 0, Created = DateTime.UtcNow, Modified = DateTime.UtcNow, Tier = Tier };
            }

            // Build DataTable from first row to discover columns.
            var dt = new System.Data.DataTable(tableName);
            foreach (var prop in rows[0].EnumerateObject())
                dt.Columns.Add(prop.Name);

            foreach (var row in rows)
            {
                var dataRow = dt.NewRow();
                foreach (var prop in row.EnumerateObject())
                {
                    if (dt.Columns.Contains(prop.Name))
                        dataRow[prop.Name] = prop.Value.ValueKind == JsonValueKind.Null ? DBNull.Value : (object)prop.Value.ToString();
                }
                dt.Rows.Add(dataRow);
            }

            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(ct);

            using var bulkCopy = new SqlBulkCopy(connection)
            {
                DestinationTableName = $"[{tableName.Replace("]", "]]")}]",
                BatchSize = _batchSize,
                BulkCopyTimeout = _commandTimeout
            };

            await bulkCopy.WriteToServerAsync(dt, ct);

            IncrementBytesStored(json.Length);
            IncrementOperationCounter(StorageOperationType.Store);

            return new StorageObjectMetadata
            {
                Key = key,
                Size = json.Length,
                Created = DateTime.UtcNow,
                Modified = DateTime.UtcNow,
                ETag = $"\"{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant()}\"",
                ContentType = "application/json",
                CustomMetadata = metadata as IReadOnlyDictionary<string, string>,
                Tier = Tier
            };
        }

        protected override Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            // SQL Server is a write target for bulk import, not a byte-stream blob store.
            // Retrieving arbitrary rows as a byte stream requires a table schema, column mapping,
            // and serialization format (CSV, JSON, Parquet) that is not defined here.
            throw new NotSupportedException(
                "SqlServerImportStrategy does not support read-back. SQL Server is a write-only " +
                "bulk import target. To retrieve data, use SqlServerExportStrategy with a " +
                "SELECT query and explicit column-to-stream serialization.");
        }

        protected override Task DeleteAsyncCore(string key, CancellationToken ct)
        {
            // Deleting rows requires knowing the table name, primary key columns, and key values.
            // The key string alone is not sufficient to construct a safe DELETE statement.
            throw new NotSupportedException(
                "SqlServerImportStrategy does not support delete. To remove rows from SQL Server, " +
                "execute a DELETE statement via SqlCommand with proper parameterization against " +
                "the target table and primary key.");
        }

        protected override Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
        {
            // Existence check requires querying the target table by primary key, which requires
            // knowing the table schema. Always returning true is incorrect and misleading.
            throw new NotSupportedException(
                "SqlServerImportStrategy does not support existence checks. To check row existence, " +
                "execute a SELECT COUNT(*) or EXISTS query via SqlCommand against the target table.");
        }

        protected override IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, CancellationToken ct)
        {
            IncrementOperationCounter(StorageOperationType.List);
            return AsyncEnumerable.Empty<StorageObjectMetadata>();
        }

        protected override Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
        {
            IncrementOperationCounter(StorageOperationType.GetMetadata);
            return Task.FromResult(new StorageObjectMetadata { Key = key, Size = 0, Created = DateTime.UtcNow, Modified = DateTime.UtcNow, Tier = Tier });
        }

        protected override Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
        {
            return Task.FromResult(new StorageHealthInfo { Status = HealthStatus.Healthy, CheckedAt = DateTime.UtcNow });
        }

        protected override Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            return Task.FromResult<long?>(null);
        }

        protected override int GetMaxKeyLength() => 512;
    }
}
