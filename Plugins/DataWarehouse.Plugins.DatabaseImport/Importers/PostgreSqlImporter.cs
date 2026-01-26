using Npgsql;
using System.Data;
using System.Runtime.CompilerServices;

namespace DataWarehouse.Plugins.DatabaseImport.Importers
{
    /// <summary>
    /// PostgreSQL database importer.
    /// </summary>
    public class PostgreSqlImporter : IDataImporter
    {
        private NpgsqlConnection? _connection;
        private ImportConfiguration? _config;
        private bool _disposed;

        /// <inheritdoc/>
        public DatabaseType SupportedDatabaseType => DatabaseType.PostgreSQL;

        /// <inheritdoc/>
        public async Task ConnectAsync(ImportConfiguration config, CancellationToken ct = default)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _connection = new NpgsqlConnection(config.ConnectionString);
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
                throw new InvalidOperationException("Not connected");

            try
            {
                using var cmd = new NpgsqlCommand("SELECT 1", _connection);
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
                SELECT schema_name
                FROM information_schema.schemata
                WHERE schema_name NOT IN ('pg_catalog', 'information_schema')
                ORDER BY schema_name";

            using var cmd = new NpgsqlCommand(query, _connection);
            using var reader = await cmd.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                var schemaName = reader.GetString(0);
                schemas.Add(new SchemaInfo
                {
                    SchemaName = schemaName,
                    Tables = await DiscoverTablesAsync(schemaName, ct)
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
                SELECT table_schema, table_name
                FROM information_schema.tables
                WHERE table_type = 'BASE TABLE'
                AND (@Schema::text IS NULL OR table_schema = @Schema)
                ORDER BY table_schema, table_name";

            using var cmd = new NpgsqlCommand(query, _connection);
            cmd.Parameters.AddWithValue("Schema", (object?)schema ?? DBNull.Value);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var tableInfo = await GetTableInfoAsync(reader.GetString(1), reader.GetString(0), ct);
                tables.Add(tableInfo);
            }

            return tables;
        }

        /// <inheritdoc/>
        public async Task<TableInfo> GetTableInfoAsync(string tableName, string? schema = null, CancellationToken ct = default)
        {
            EnsureConnected();
            schema ??= "public";

            var tableInfo = new TableInfo
            {
                SchemaName = schema,
                TableName = tableName,
                Columns = new List<ColumnInfo>(),
                PrimaryKeyColumns = new List<string>()
            };

            var query = @"
                SELECT column_name, data_type, is_nullable, character_maximum_length
                FROM information_schema.columns
                WHERE table_schema = @Schema AND table_name = @Table
                ORDER BY ordinal_position";

            using var cmd = new NpgsqlCommand(query, _connection);
            cmd.Parameters.AddWithValue("Schema", schema);
            cmd.Parameters.AddWithValue("Table", tableName);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                tableInfo.Columns.Add(new ColumnInfo
                {
                    ColumnName = reader.GetString(0),
                    DataType = reader.GetString(1),
                    IsNullable = reader.GetString(2) == "YES",
                    MaxLength = reader.IsDBNull(3) ? null : reader.GetInt32(3)
                });
            }

            return tableInfo;
        }

        /// <inheritdoc/>
        public Task<TableImportStats> ImportTableAsync(
            TableMapping mapping,
            Action<long, long>? progressCallback = null,
            CancellationToken ct = default)
        {
            throw new NotImplementedException("PostgreSQL import will be implemented");
        }

        /// <inheritdoc/>
        public IAsyncEnumerable<Dictionary<string, object?>> ReadRowsAsync(
            TableMapping mapping,
            CancellationToken ct = default)
        {
            throw new NotImplementedException("PostgreSQL read will be implemented");
        }

        /// <inheritdoc/>
        public async Task<long> GetRowCountAsync(TableMapping mapping, CancellationToken ct = default)
        {
            EnsureConnected();
            var schema = mapping.SourceSchema ?? "public";
            var query = $"SELECT COUNT(*) FROM \"{schema}\".\"{mapping.SourceTable}\"";

            using var cmd = new NpgsqlCommand(query, _connection);
            var result = await cmd.ExecuteScalarAsync(ct);
            return result != null ? Convert.ToInt64(result) : 0;
        }

        private void EnsureConnected()
        {
            if (_connection == null || _connection.State != ConnectionState.Open)
                throw new InvalidOperationException("Not connected");
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
