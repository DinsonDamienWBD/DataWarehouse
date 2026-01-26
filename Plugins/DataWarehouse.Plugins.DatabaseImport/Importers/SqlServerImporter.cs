using Microsoft.Data.SqlClient;
using System.Data;
using System.Runtime.CompilerServices;

namespace DataWarehouse.Plugins.DatabaseImport.Importers
{
    /// <summary>
    /// SQL Server database importer.
    /// </summary>
    public class SqlServerImporter : IDataImporter
    {
        private SqlConnection? _connection;
        private ImportConfiguration? _config;
        private bool _disposed;

        /// <inheritdoc/>
        public DatabaseType SupportedDatabaseType => DatabaseType.SqlServer;

        /// <inheritdoc/>
        public async Task ConnectAsync(ImportConfiguration config, CancellationToken ct = default)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));

            _connection = new SqlConnection(config.ConnectionString);
            await _connection.OpenAsync(ct);
        }

        /// <inheritdoc/>
        public async Task DisconnectAsync()
        {
            if (_connection != null)
            {
                await _connection.CloseAsync();
                await _connection.DisposeAsync();
                _connection = null;
            }
        }

        /// <inheritdoc/>
        public async Task<bool> TestConnectionAsync()
        {
            if (_connection == null)
                throw new InvalidOperationException("Not connected. Call ConnectAsync first.");

            try
            {
                using var cmd = new SqlCommand("SELECT 1", _connection);
                cmd.CommandTimeout = _config?.TimeoutSeconds ?? 30;
                var result = await cmd.ExecuteScalarAsync();
                return result != null;
            }
            catch
            {
                return false;
            }
        }

        /// <inheritdoc/>
        public async Task<List<SchemaInfo>> DiscoverSchemasAsync(CancellationToken ct = default)
        {
            EnsureConnected();

            var schemas = new List<SchemaInfo>();
            var query = @"
                SELECT
                    s.name AS SchemaName,
                    SUM(p.rows) AS TotalRows,
                    SUM(a.total_pages * 8 * 1024) AS TotalSizeBytes
                FROM sys.schemas s
                LEFT JOIN sys.tables t ON t.schema_id = s.schema_id
                LEFT JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
                LEFT JOIN sys.allocation_units a ON a.container_id = p.partition_id
                WHERE s.name NOT IN ('sys', 'INFORMATION_SCHEMA', 'guest')
                GROUP BY s.name
                ORDER BY s.name";

            using var cmd = new SqlCommand(query, _connection);
            cmd.CommandTimeout = _config?.TimeoutSeconds ?? 30;

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var schemaName = reader.GetString(0);
                var tables = await DiscoverTablesAsync(schemaName, ct);

                schemas.Add(new SchemaInfo
                {
                    SchemaName = schemaName,
                    Tables = tables,
                    EstimatedSizeBytes = reader.IsDBNull(2) ? 0 : reader.GetInt64(2)
                });
            }

            return schemas;
        }

        /// <inheritdoc/>
        public async Task<List<TableInfo>> DiscoverTablesAsync(string? schema = null, CancellationToken ct = default)
        {
            EnsureConnected();

            var tables = new List<TableInfo>();
            var query = @"
                SELECT
                    s.name AS SchemaName,
                    t.name AS TableName,
                    SUM(p.rows) AS RowCount,
                    SUM(a.total_pages * 8 * 1024) AS SizeBytes
                FROM sys.tables t
                INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
                LEFT JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
                LEFT JOIN sys.allocation_units a ON a.container_id = p.partition_id
                WHERE (@Schema IS NULL OR s.name = @Schema)
                GROUP BY s.name, t.name
                ORDER BY s.name, t.name";

            using var cmd = new SqlCommand(query, _connection);
            cmd.CommandTimeout = _config?.TimeoutSeconds ?? 30;
            cmd.Parameters.AddWithValue("@Schema", (object?)schema ?? DBNull.Value);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var schemaName = reader.GetString(0);
                var tableName = reader.GetString(1);

                tables.Add(new TableInfo
                {
                    SchemaName = schemaName,
                    TableName = tableName,
                    EstimatedRowCount = reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                    EstimatedSizeBytes = reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                    Columns = new List<ColumnInfo>(),
                    PrimaryKeyColumns = new List<string>()
                });
            }

            // Get column and primary key info for each table
            foreach (var table in tables)
            {
                var tableInfo = await GetTableInfoAsync(table.TableName, table.SchemaName, ct);
                table.Columns.AddRange(tableInfo.Columns);
                table.PrimaryKeyColumns.AddRange(tableInfo.PrimaryKeyColumns);
            }

            return tables;
        }

        /// <inheritdoc/>
        public async Task<TableInfo> GetTableInfoAsync(string tableName, string? schema = null, CancellationToken ct = default)
        {
            EnsureConnected();

            schema ??= "dbo";

            var tableInfo = new TableInfo
            {
                SchemaName = schema,
                TableName = tableName,
                Columns = new List<ColumnInfo>(),
                PrimaryKeyColumns = new List<string>()
            };

            // Get column information
            var columnQuery = @"
                SELECT
                    c.name AS ColumnName,
                    t.name AS DataType,
                    c.is_nullable AS IsNullable,
                    c.max_length AS MaxLength,
                    c.precision AS Precision,
                    c.scale AS Scale,
                    CASE WHEN pk.column_id IS NOT NULL THEN 1 ELSE 0 END AS IsPrimaryKey
                FROM sys.columns c
                INNER JOIN sys.types t ON c.user_type_id = t.user_type_id
                INNER JOIN sys.tables tbl ON c.object_id = tbl.object_id
                INNER JOIN sys.schemas s ON tbl.schema_id = s.schema_id
                LEFT JOIN (
                    SELECT ic.object_id, ic.column_id
                    FROM sys.index_columns ic
                    INNER JOIN sys.indexes i ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                    WHERE i.is_primary_key = 1
                ) pk ON c.object_id = pk.object_id AND c.column_id = pk.column_id
                WHERE tbl.name = @TableName AND s.name = @Schema
                ORDER BY c.column_id";

            using var cmd = new SqlCommand(columnQuery, _connection);
            cmd.CommandTimeout = _config?.TimeoutSeconds ?? 30;
            cmd.Parameters.AddWithValue("@TableName", tableName);
            cmd.Parameters.AddWithValue("@Schema", schema);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var columnName = reader.GetString(0);
                var isPrimaryKey = reader.GetInt32(6) == 1;

                var columnInfo = new ColumnInfo
                {
                    ColumnName = columnName,
                    DataType = reader.GetString(1),
                    IsNullable = reader.GetBoolean(2),
                    IsPrimaryKey = isPrimaryKey,
                    MaxLength = reader.IsDBNull(3) ? null : reader.GetInt16(3),
                    Precision = reader.IsDBNull(4) ? null : reader.GetByte(4),
                    Scale = reader.IsDBNull(5) ? null : reader.GetByte(5)
                };

                tableInfo.Columns.Add(columnInfo);

                if (isPrimaryKey)
                {
                    tableInfo.PrimaryKeyColumns.Add(columnName);
                }
            }

            return tableInfo;
        }

        /// <inheritdoc/>
        public async Task<TableImportStats> ImportTableAsync(
            TableMapping mapping,
            Action<long, long>? progressCallback = null,
            CancellationToken ct = default)
        {
            EnsureConnected();

            var stats = new TableImportStats
            {
                TableName = mapping.SourceTable,
                Errors = new List<string>()
            };

            var startTime = DateTime.UtcNow;
            long rowsImported = 0;
            long bytesImported = 0;

            try
            {
                // Get total row count first
                var totalRows = await GetRowCountAsync(mapping, ct);

                // Import rows in batches
                await foreach (var row in ReadRowsAsync(mapping, ct))
                {
                    rowsImported++;
                    bytesImported += EstimateRowSize(row);

                    progressCallback?.Invoke(rowsImported, totalRows);

                    if (ct.IsCancellationRequested)
                        break;
                }

                stats.RowsImported = rowsImported;
                stats.BytesImported = bytesImported;
                stats.ColumnCount = mapping.ColumnMappings?.Count ?? 0;
            }
            catch (Exception ex)
            {
                stats.Errors.Add($"Import failed: {ex.Message}");
            }

            return stats;
        }

        /// <inheritdoc/>
        public async IAsyncEnumerable<Dictionary<string, object?>> ReadRowsAsync(
            TableMapping mapping,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            EnsureConnected();

            var schema = mapping.SourceSchema ?? "dbo";
            var query = BuildSelectQuery(mapping);

            using var cmd = new SqlCommand(query, _connection);
            cmd.CommandTimeout = _config?.TimeoutSeconds ?? 30;

            using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);

            long rowCount = 0;
            while (await reader.ReadAsync(ct))
            {
                if (mapping.MaxRows > 0 && rowCount >= mapping.MaxRows)
                    break;

                var row = new Dictionary<string, object?>();

                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var columnName = reader.GetName(i);
                    var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    row[columnName] = value;
                }

                rowCount++;
                yield return row;
            }
        }

        /// <inheritdoc/>
        public async Task<long> GetRowCountAsync(TableMapping mapping, CancellationToken ct = default)
        {
            EnsureConnected();

            var schema = mapping.SourceSchema ?? "dbo";
            var whereClause = string.IsNullOrWhiteSpace(mapping.WhereClause) ? "" : $"WHERE {mapping.WhereClause}";
            var query = $"SELECT COUNT(*) FROM [{schema}].[{mapping.SourceTable}] {whereClause}";

            using var cmd = new SqlCommand(query, _connection);
            cmd.CommandTimeout = _config?.TimeoutSeconds ?? 30;

            var result = await cmd.ExecuteScalarAsync(ct);
            return result != null ? Convert.ToInt64(result) : 0;
        }

        private string BuildSelectQuery(TableMapping mapping)
        {
            var schema = mapping.SourceSchema ?? "dbo";
            var columns = mapping.ColumnMappings != null && mapping.ColumnMappings.Any()
                ? string.Join(", ", mapping.ColumnMappings.Keys.Select(c => $"[{c}]"))
                : "*";

            var whereClause = string.IsNullOrWhiteSpace(mapping.WhereClause) ? "" : $"WHERE {mapping.WhereClause}";

            var query = $"SELECT {columns} FROM [{schema}].[{mapping.SourceTable}] {whereClause}";

            if (mapping.MaxRows > 0)
            {
                query = $"SELECT TOP {mapping.MaxRows} {columns} FROM [{schema}].[{mapping.SourceTable}] {whereClause}";
            }

            return query;
        }

        private static long EstimateRowSize(Dictionary<string, object?> row)
        {
            long size = 0;
            foreach (var value in row.Values)
            {
                if (value == null) continue;

                size += value switch
                {
                    string s => s.Length * 2, // Unicode
                    byte[] b => b.Length,
                    int => 4,
                    long => 8,
                    short => 2,
                    decimal => 16,
                    double => 8,
                    float => 4,
                    bool => 1,
                    DateTime => 8,
                    Guid => 16,
                    _ => 8 // Estimate
                };
            }
            return size;
        }

        private void EnsureConnected()
        {
            if (_connection == null || _connection.State != ConnectionState.Open)
                throw new InvalidOperationException("Not connected. Call ConnectAsync first.");
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            DisconnectAsync().GetAwaiter().GetResult();
            GC.SuppressFinalize(this);
        }
    }
}
