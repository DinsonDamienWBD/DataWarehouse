using Microsoft.Data.Sqlite;

namespace DataWarehouse.Plugins.DatabaseImport.Importers
{
    /// <summary>
    /// SQLite database importer.
    /// </summary>
    public class SqliteImporter : IDataImporter
    {
        private SqliteConnection? _connection;
        private bool _disposed;

        /// <inheritdoc/>
        public DatabaseType SupportedDatabaseType => DatabaseType.SQLite;

        /// <inheritdoc/>
        public async Task ConnectAsync(ImportConfiguration config, CancellationToken ct = default)
        {
            _connection = new SqliteConnection(config.ConnectionString);
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
        public Task<bool> TestConnectionAsync() => throw new NotImplementedException();

        /// <inheritdoc/>
        public Task<List<SchemaInfo>> DiscoverSchemasAsync(CancellationToken ct = default) => throw new NotImplementedException();

        /// <inheritdoc/>
        public Task<List<TableInfo>> DiscoverTablesAsync(string? schema = null, CancellationToken ct = default) => throw new NotImplementedException();

        /// <inheritdoc/>
        public Task<TableInfo> GetTableInfoAsync(string tableName, string? schema = null, CancellationToken ct = default) => throw new NotImplementedException();

        /// <inheritdoc/>
        public Task<TableImportStats> ImportTableAsync(TableMapping mapping, Action<long, long>? progressCallback = null, CancellationToken ct = default) => throw new NotImplementedException();

        /// <inheritdoc/>
        public IAsyncEnumerable<Dictionary<string, object?>> ReadRowsAsync(TableMapping mapping, CancellationToken ct = default) => throw new NotImplementedException();

        /// <inheritdoc/>
        public Task<long> GetRowCountAsync(TableMapping mapping, CancellationToken ct = default) => throw new NotImplementedException();

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
