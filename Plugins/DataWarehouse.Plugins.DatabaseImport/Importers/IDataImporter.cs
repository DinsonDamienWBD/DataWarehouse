namespace DataWarehouse.Plugins.DatabaseImport.Importers
{
    /// <summary>
    /// Interface for database-specific importers.
    /// </summary>
    public interface IDataImporter : IDisposable
    {
        /// <summary>
        /// Database type this importer supports.
        /// </summary>
        DatabaseType SupportedDatabaseType { get; }

        /// <summary>
        /// Connect to the database.
        /// </summary>
        Task ConnectAsync(ImportConfiguration config, CancellationToken ct = default);

        /// <summary>
        /// Disconnect from the database.
        /// </summary>
        Task DisconnectAsync();

        /// <summary>
        /// Test the database connection.
        /// </summary>
        Task<bool> TestConnectionAsync();

        /// <summary>
        /// Discover all schemas in the database.
        /// </summary>
        Task<List<SchemaInfo>> DiscoverSchemasAsync(CancellationToken ct = default);

        /// <summary>
        /// Discover tables in a specific schema.
        /// </summary>
        Task<List<TableInfo>> DiscoverTablesAsync(string? schema = null, CancellationToken ct = default);

        /// <summary>
        /// Get detailed information about a specific table.
        /// </summary>
        Task<TableInfo> GetTableInfoAsync(string tableName, string? schema = null, CancellationToken ct = default);

        /// <summary>
        /// Import a table with the specified mapping.
        /// </summary>
        /// <param name="mapping">Table mapping configuration.</param>
        /// <param name="progressCallback">Callback for progress updates.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Import result.</returns>
        Task<TableImportStats> ImportTableAsync(
            TableMapping mapping,
            Action<long, long>? progressCallback = null,
            CancellationToken ct = default);

        /// <summary>
        /// Read rows from a table.
        /// </summary>
        IAsyncEnumerable<Dictionary<string, object?>> ReadRowsAsync(
            TableMapping mapping,
            CancellationToken ct = default);

        /// <summary>
        /// Get the count of rows that would be imported with the given mapping.
        /// </summary>
        Task<long> GetRowCountAsync(TableMapping mapping, CancellationToken ct = default);
    }
}
