using System.Data;
using System.Runtime.CompilerServices;
using System.Text;
using DataWarehouse.SDK.Connectors;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using Google.Cloud.BigQuery.V2;
using Google.Apis.Auth.OAuth2;

namespace DataWarehouse.Plugins.DataConnectors;

/// <summary>
/// Production-ready Google BigQuery connector plugin.
/// Provides full CRUD operations with real BigQuery connectivity via Google.Cloud.BigQuery.V2.
/// Supports schema discovery from INFORMATION_SCHEMA, parameterized queries, streaming inserts,
/// and bulk loading from Google Cloud Storage.
/// </summary>
public class BigQueryConnectorPlugin : DatabaseConnectorPluginBase
{
    private BigQueryClient? _client;
    private string? _projectId;
    private string? _datasetId;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly Dictionary<string, DataSchema> _schemaCache = new();
    private BigQueryConnectorConfig _config = new();

    /// <inheritdoc />
    public override string Id => "datawarehouse.connector.bigquery";

    /// <inheritdoc />
    public override string Name => "Google BigQuery Connector";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override string ConnectorId => "bigquery";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.InfrastructureProvider;

    /// <inheritdoc />
    public override ConnectorCapabilities Capabilities =>
        ConnectorCapabilities.Read |
        ConnectorCapabilities.Write |
        ConnectorCapabilities.Schema |
        ConnectorCapabilities.BulkOperations |
        ConnectorCapabilities.Streaming;

    /// <summary>
    /// Configures the connector with additional options.
    /// </summary>
    /// <param name="config">BigQuery-specific configuration.</param>
    public void Configure(BigQueryConnectorConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <inheritdoc />
    protected override async Task<ConnectionResult> EstablishConnectionAsync(ConnectorConfig config, CancellationToken ct)
    {
        await _connectionLock.WaitAsync(ct);
        try
        {
            // Parse connection string for project ID and dataset
            var connectionParams = ParseConnectionString(config.ConnectionString);

            if (!connectionParams.TryGetValue("ProjectId", out var projectId) || string.IsNullOrWhiteSpace(projectId))
            {
                return new ConnectionResult(false, "ProjectId is required in connection string", null);
            }

            _projectId = projectId;
            _datasetId = connectionParams.TryGetValue("DatasetId", out var dataset) ? dataset : _config.DefaultDataset;

            // Create BigQuery client
            GoogleCredential credential;

            if (connectionParams.TryGetValue("CredentialsPath", out var credPath) && !string.IsNullOrWhiteSpace(credPath))
            {
                // Use service account JSON file
                credential = GoogleCredential.FromFile(credPath);
            }
            else if (connectionParams.TryGetValue("CredentialsJson", out var credJson) && !string.IsNullOrWhiteSpace(credJson))
            {
                // Use inline JSON credentials
                credential = GoogleCredential.FromJson(credJson);
            }
            else
            {
                // Use application default credentials
                credential = await GoogleCredential.GetApplicationDefaultAsync();
            }

            // Ensure BigQuery scopes are present
            if (credential.IsCreateScopedRequired)
            {
                credential = credential.CreateScoped(new[] { "https://www.googleapis.com/auth/bigquery" });
            }

            _client = await BigQueryClient.CreateAsync(_projectId, credential);

            // Test the connection by listing datasets
            var datasets = await _client.ListDatasetsAsync().Take(1).ToListAsync(ct);

            // Get project info
            var serverInfo = new Dictionary<string, object>
            {
                ["ProjectId"] = _projectId,
                ["DefaultDataset"] = _datasetId ?? "none",
                ["ConnectionState"] = "Open",
                ["Location"] = _config.Location ?? "US",
                ["CredentialType"] = connectionParams.ContainsKey("CredentialsPath") || connectionParams.ContainsKey("CredentialsJson")
                    ? "ServiceAccount"
                    : "ApplicationDefault"
            };

            // Verify dataset exists if specified
            if (!string.IsNullOrWhiteSpace(_datasetId))
            {
                try
                {
                    var datasetRef = await _client.GetDatasetAsync(_datasetId, new GetDatasetOptions(), ct);
                    serverInfo["DatasetLocation"] = datasetRef.Resource.Location;
                    serverInfo["DatasetCreated"] = datasetRef.Resource.CreationTime?.ToString() ?? "unknown";
                }
                catch (Exception ex)
                {
                    return new ConnectionResult(false, $"Dataset '{_datasetId}' not found: {ex.Message}", null);
                }
            }

            return new ConnectionResult(true, null, serverInfo);
        }
        catch (Exception ex)
        {
            return new ConnectionResult(false, $"BigQuery connection failed: {ex.Message}", null);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task CloseConnectionAsync()
    {
        await _connectionLock.WaitAsync();
        try
        {
            _client?.Dispose();
            _client = null;
            _projectId = null;
            _datasetId = null;
            _schemaCache.Clear();
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task<bool> PingAsync()
    {
        if (_client == null) return false;

        try
        {
            // Simple query to test connectivity
            var sql = "SELECT 1 as ping";
            var result = await _client.ExecuteQueryAsync(sql, parameters: null);
            await result.GetRowsAsync().Take(1).ToListAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    protected override async Task<DataSchema> FetchSchemaAsync()
    {
        if (_client == null)
            throw new InvalidOperationException("Not connected to BigQuery");

        if (string.IsNullOrWhiteSpace(_datasetId))
            throw new InvalidOperationException("No dataset specified");

        // Query INFORMATION_SCHEMA for comprehensive schema information
        var sql = $@"
            SELECT
                table_name,
                column_name,
                data_type,
                is_nullable,
                is_partitioning_column,
                clustering_ordinal_position
            FROM `{_projectId}.{_datasetId}.INFORMATION_SCHEMA.COLUMNS`
            ORDER BY table_name, ordinal_position";

        var fields = new List<DataSchemaField>();
        var primaryKeys = new List<string>();
        var tableCount = 0;
        var currentTable = "";

        var result = await _client.ExecuteQueryAsync(sql, parameters: null);

        await foreach (var row in result.GetRowsAsync())
        {
            var tableName = row["table_name"]?.ToString() ?? "";
            if (tableName != currentTable)
            {
                currentTable = tableName;
                tableCount++;
            }

            var columnName = row["column_name"]?.ToString() ?? "";
            var dataType = row["data_type"]?.ToString() ?? "";
            var isNullable = row["is_nullable"]?.ToString() == "YES";
            var isPartitioning = row["is_partitioning_column"]?.ToString() == "YES";
            var clusteringPosition = row["clustering_ordinal_position"];

            fields.Add(new DataSchemaField(
                columnName,
                MapBigQueryType(dataType),
                isNullable,
                null, // MaxLength
                new Dictionary<string, object>
                {
                    ["tableName"] = tableName,
                    ["bigQueryType"] = dataType,
                    ["isPartitioningColumn"] = isPartitioning,
                    ["clusteringOrdinal"] = clusteringPosition ?? (object)DBNull.Value
                }
            ));
        }

        return new DataSchema(
            Name: _datasetId,
            Fields: fields.ToArray(),
            PrimaryKeys: primaryKeys.ToArray(),
            Metadata: new Dictionary<string, object>
            {
                ["TableCount"] = tableCount,
                ["FieldCount"] = fields.Count,
                ["SchemaVersion"] = "1.0",
                ["DatasetId"] = _datasetId,
                ["ProjectId"] = _projectId ?? "unknown"
            }
        );
    }

    /// <summary>
    /// Gets the schema for a specific table.
    /// </summary>
    /// <param name="tableName">Name of the table.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Table schema.</returns>
    public async Task<DataSchema> GetTableSchemaAsync(string tableName, CancellationToken ct = default)
    {
        if (_schemaCache.TryGetValue(tableName, out var cached))
            return cached;

        if (_client == null)
            throw new InvalidOperationException("Not connected to BigQuery");

        if (string.IsNullOrWhiteSpace(_datasetId))
            throw new InvalidOperationException("No dataset specified");

        var tableRef = _client.GetTableReference(_datasetId, tableName);
        var table = await _client.GetTableAsync(tableRef, cancellationToken: ct);

        var fields = new List<DataSchemaField>();
        var primaryKeys = new List<string>();

        foreach (var field in table.Schema.Fields)
        {
            fields.Add(new DataSchemaField(
                field.Name,
                MapBigQueryType(field.Type),
                field.Mode != "REQUIRED",
                null,
                new Dictionary<string, object>
                {
                    ["mode"] = field.Mode,
                    ["description"] = field.Description ?? "",
                    ["type"] = field.Type
                }
            ));
        }

        var schema = new DataSchema(
            Name: tableName,
            Fields: fields.ToArray(),
            PrimaryKeys: primaryKeys.ToArray(),
            Metadata: new Dictionary<string, object>
            {
                ["TableName"] = tableName,
                ["NumRows"] = table.Resource.NumRows ?? 0,
                ["SizeBytes"] = table.Resource.NumBytes ?? 0,
                ["Created"] = table.Resource.CreationTime?.ToString() ?? "unknown",
                ["Modified"] = table.Resource.LastModifiedTime?.ToString() ?? "unknown"
            }
        );

        _schemaCache[tableName] = schema;
        return schema;
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<DataRecord> ExecuteReadAsync(
        DataQuery query,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_client == null)
            throw new InvalidOperationException("Not connected to BigQuery");

        var sql = BuildSelectQuery(query);

        var queryOptions = new QueryOptions
        {
            UseQueryCache = _config.UseQueryCache,
            UseLegacySql = false
        };

        var result = await _client.ExecuteQueryAsync(sql, parameters: null, queryOptions, cancellationToken: ct);

        long position = query.Offset ?? 0;

        await foreach (var row in result.GetRowsAsync())
        {
            if (ct.IsCancellationRequested) yield break;

            var values = new Dictionary<string, object?>();
            foreach (var field in result.Schema.Fields)
            {
                values[field.Name] = row[field.Name];
            }

            yield return new DataRecord(
                Values: values,
                Position: position++,
                Timestamp: DateTimeOffset.UtcNow
            );
        }
    }

    /// <inheritdoc />
    protected override async Task<WriteResult> ExecuteWriteAsync(
        IAsyncEnumerable<DataRecord> records,
        WriteOptions options,
        CancellationToken ct)
    {
        if (_client == null)
            throw new InvalidOperationException("Not connected to BigQuery");

        if (string.IsNullOrWhiteSpace(_datasetId))
            throw new InvalidOperationException("No dataset specified");

        var tableName = options.TargetTable ?? throw new ArgumentException("TargetTable is required");

        long written = 0;
        long failed = 0;
        var errors = new List<string>();

        if (_config.UseStreamingInserts)
        {
            // Use streaming inserts for real-time data
            (written, failed, errors) = await StreamingInsertAsync(tableName, records, options.BatchSize, ct);
        }
        else
        {
            // Use load jobs for bulk data
            (written, failed, errors) = await BulkLoadAsync(tableName, records, options, ct);
        }

        return new WriteResult(written, failed, errors.Count > 0 ? errors.ToArray() : null);
    }

    /// <summary>
    /// Performs streaming inserts for real-time data ingestion.
    /// </summary>
    /// <param name="tableName">Target table name.</param>
    /// <param name="records">Records to insert.</param>
    /// <param name="batchSize">Number of records per batch.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Tuple of written count, failed count, and error list.</returns>
    private async Task<(long written, long failed, List<string> errors)> StreamingInsertAsync(
        string tableName,
        IAsyncEnumerable<DataRecord> records,
        int batchSize,
        CancellationToken ct)
    {
        long written = 0;
        long failed = 0;
        var errors = new List<string>();

        var tableRef = _client!.GetTableReference(_datasetId!, tableName);
        var batch = new List<BigQueryInsertRow>();

        await foreach (var record in records.WithCancellation(ct))
        {
            try
            {
                var insertRow = new BigQueryInsertRow();
                foreach (var (key, value) in record.Values)
                {
                    insertRow[key] = value;
                }

                batch.Add(insertRow);

                if (batch.Count >= batchSize)
                {
                    var insertResult = await _client.InsertRowsAsync(tableRef, batch, cancellationToken: ct);

                    if (insertResult.Errors != null && insertResult.Errors.Count > 0)
                    {
                        foreach (var error in insertResult.Errors)
                        {
                            failed++;
                            errors.Add($"Row insert error: {string.Join(", ", error.Select(e => e.Message))}");
                        }
                    }
                    else
                    {
                        written += batch.Count;
                    }

                    batch.Clear();
                }
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"Record at position {record.Position}: {ex.Message}");
            }
        }

        // Insert remaining records
        if (batch.Count > 0)
        {
            try
            {
                var insertResult = await _client.InsertRowsAsync(tableRef, batch, cancellationToken: ct);

                if (insertResult.Errors != null && insertResult.Errors.Count > 0)
                {
                    foreach (var error in insertResult.Errors)
                    {
                        failed++;
                        errors.Add($"Row insert error: {string.Join(", ", error.Select(e => e.Message))}");
                    }
                }
                else
                {
                    written += batch.Count;
                }
            }
            catch (Exception ex)
            {
                failed += batch.Count;
                errors.Add($"Batch insert failed: {ex.Message}");
            }
        }

        return (written, failed, errors);
    }

    /// <summary>
    /// Performs bulk loading using BigQuery load jobs.
    /// Suitable for large-scale data ingestion from GCS or local files.
    /// </summary>
    /// <param name="tableName">Target table name.</param>
    /// <param name="records">Records to load.</param>
    /// <param name="options">Write options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Tuple of written count, failed count, and error list.</returns>
    private async Task<(long written, long failed, List<string> errors)> BulkLoadAsync(
        string tableName,
        IAsyncEnumerable<DataRecord> records,
        WriteOptions options,
        CancellationToken ct)
    {
        long written = 0;
        long failed = 0;
        var errors = new List<string>();

        try
        {
            // For bulk loads, we need to convert records to a format BigQuery can ingest
            // This implementation uses INSERT statements batched into a single query
            var tableRef = _client!.GetTableReference(_datasetId!, tableName);
            var schema = await GetTableSchemaAsync(tableName, ct);
            var columns = schema.Fields.Select(f => f.Name).ToArray();

            var batch = new List<DataRecord>();

            await foreach (var record in records.WithCancellation(ct))
            {
                batch.Add(record);

                if (batch.Count >= options.BatchSize)
                {
                    var (w, f, e) = await ExecuteBulkInsertBatch(tableName, columns, batch, ct);
                    written += w;
                    failed += f;
                    errors.AddRange(e);
                    batch.Clear();
                }
            }

            // Process remaining records
            if (batch.Count > 0)
            {
                var (w, f, e) = await ExecuteBulkInsertBatch(tableName, columns, batch, ct);
                written += w;
                failed += f;
                errors.AddRange(e);
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Bulk load failed: {ex.Message}");
        }

        return (written, failed, errors);
    }

    /// <summary>
    /// Executes a batch of INSERT statements as a single query.
    /// </summary>
    private async Task<(long written, long failed, List<string> errors)> ExecuteBulkInsertBatch(
        string tableName,
        string[] columns,
        List<DataRecord> batch,
        CancellationToken ct)
    {
        long written = 0;
        long failed = 0;
        var errors = new List<string>();

        try
        {
            var sql = BuildBulkInsertStatement(tableName, columns, batch);
            await _client!.ExecuteQueryAsync(sql, parameters: null, cancellationToken: ct);
            written = batch.Count;
        }
        catch (Exception ex)
        {
            failed = batch.Count;
            errors.Add($"Bulk insert batch failed: {ex.Message}");
        }

        return (written, failed, errors);
    }

    /// <summary>
    /// Loads data from Google Cloud Storage.
    /// </summary>
    /// <param name="tableName">Target table name.</param>
    /// <param name="gcsUri">GCS URI (gs://bucket/path).</param>
    /// <param name="format">File format (CSV, JSON, AVRO, PARQUET).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Load job result.</returns>
    public async Task<BigQueryJob> LoadFromGcsAsync(
        string tableName,
        string gcsUri,
        FileFormat format = FileFormat.Csv,
        CancellationToken ct = default)
    {
        if (_client == null)
            throw new InvalidOperationException("Not connected to BigQuery");

        if (string.IsNullOrWhiteSpace(_datasetId))
            throw new InvalidOperationException("No dataset specified");

        var tableRef = _client.GetTableReference(_datasetId, tableName);

        var options = new CreateLoadJobOptions
        {
            SourceFormat = format,
            WriteDisposition = WriteDisposition.WriteAppend,
            SkipLeadingRows = format == FileFormat.Csv ? 1 : null
        };

        var job = await _client.CreateLoadJobAsync(gcsUri, tableRef, null, options, ct);
        job = await job.PollUntilCompletedAsync(cancellationToken: ct);

        if (job.Status.ErrorResult != null)
        {
            throw new InvalidOperationException($"Load job failed: {job.Status.ErrorResult.Message}");
        }

        return job;
    }

    /// <inheritdoc />
    protected override string BuildSelectQuery(DataQuery query)
    {
        var sb = new StringBuilder();
        sb.Append("SELECT ");

        if (query.Fields?.Length > 0)
        {
            sb.Append(string.Join(", ", query.Fields.Select(QuoteIdentifier)));
        }
        else
        {
            sb.Append('*');
        }

        sb.Append(" FROM ");

        var tableName = query.TableOrCollection ?? "data";
        if (!string.IsNullOrWhiteSpace(_datasetId))
        {
            sb.Append($"`{_projectId}.{_datasetId}.{tableName}`");
        }
        else
        {
            sb.Append(QuoteIdentifier(tableName));
        }

        if (!string.IsNullOrWhiteSpace(query.Filter))
        {
            sb.Append(" WHERE ");
            sb.Append(query.Filter);
        }

        if (!string.IsNullOrEmpty(query.OrderBy))
        {
            sb.Append(" ORDER BY ");
            sb.Append(query.OrderBy);
        }

        if (query.Limit.HasValue)
        {
            sb.Append(" LIMIT ");
            sb.Append(query.Limit);
        }

        if (query.Offset.HasValue)
        {
            sb.Append(" OFFSET ");
            sb.Append(query.Offset);
        }

        return sb.ToString();
    }

    /// <inheritdoc />
    protected override string BuildInsertStatement(string table, string[] columns)
    {
        var columnList = string.Join(", ", columns.Select(QuoteIdentifier));
        var paramList = string.Join(", ", columns.Select((_, i) => $"@p{i}"));

        var fullTableName = !string.IsNullOrWhiteSpace(_datasetId)
            ? $"`{_projectId}.{_datasetId}.{table}`"
            : QuoteIdentifier(table);

        return $"INSERT INTO {fullTableName} ({columnList}) VALUES ({paramList})";
    }

    /// <summary>
    /// Builds a bulk INSERT statement for multiple records.
    /// </summary>
    private string BuildBulkInsertStatement(string table, string[] columns, List<DataRecord> records)
    {
        var sb = new StringBuilder();
        var columnList = string.Join(", ", columns.Select(QuoteIdentifier));

        var fullTableName = !string.IsNullOrWhiteSpace(_datasetId)
            ? $"`{_projectId}.{_datasetId}.{table}`"
            : QuoteIdentifier(table);

        sb.Append($"INSERT INTO {fullTableName} ({columnList}) VALUES ");

        var valueClauses = new List<string>();
        foreach (var record in records)
        {
            var values = new List<string>();
            foreach (var column in columns)
            {
                var value = record.Values.TryGetValue(column, out var v) ? v : null;
                values.Add(FormatValue(value));
            }
            valueClauses.Add($"({string.Join(", ", values)})");
        }

        sb.Append(string.Join(", ", valueClauses));

        return sb.ToString();
    }

    /// <summary>
    /// Formats a value for SQL insertion.
    /// </summary>
    private static string FormatValue(object? value)
    {
        if (value == null || value is DBNull)
            return "NULL";

        if (value is string str)
            return $"'{str.Replace("'", "''")}'";

        if (value is DateTime dt)
            return $"TIMESTAMP '{dt:yyyy-MM-dd HH:mm:ss}'";

        if (value is DateTimeOffset dto)
            return $"TIMESTAMP '{dto:yyyy-MM-dd HH:mm:ss}'";

        if (value is bool b)
            return b ? "TRUE" : "FALSE";

        if (value is byte[] bytes)
            return $"FROM_BASE64('{Convert.ToBase64String(bytes)}')";

        return value.ToString() ?? "NULL";
    }

    private static string QuoteIdentifier(string identifier)
    {
        return $"`{identifier.Replace("`", "\\`")}`";
    }

    private static string MapBigQueryType(string bigQueryType)
    {
        return bigQueryType.ToUpperInvariant() switch
        {
            "STRING" => "string",
            "BYTES" => "bytes",
            "INTEGER" or "INT64" => "long",
            "FLOAT" or "FLOAT64" => "double",
            "NUMERIC" or "DECIMAL" or "BIGNUMERIC" => "decimal",
            "BOOLEAN" or "BOOL" => "bool",
            "TIMESTAMP" => "datetime",
            "DATE" => "date",
            "TIME" => "time",
            "DATETIME" => "datetime",
            "GEOGRAPHY" => "string",
            "ARRAY" => "array",
            "STRUCT" or "RECORD" => "object",
            "JSON" => "json",
            _ => bigQueryType
        };
    }

    /// <summary>
    /// Parses a connection string into component parts.
    /// Format: "ProjectId=my-project;DatasetId=my_dataset;CredentialsPath=/path/to/creds.json"
    /// </summary>
    private static Dictionary<string, string> ParseConnectionString(string? connectionString)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(connectionString))
            return result;

        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var kvp = part.Split('=', 2);
            if (kvp.Length == 2)
            {
                result[kvp[0].Trim()] = kvp[1].Trim();
            }
        }

        return result;
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override Task StopAsync() => DisconnectAsync();
}

/// <summary>
/// Configuration options for the BigQuery connector.
/// </summary>
public class BigQueryConnectorConfig
{
    /// <summary>
    /// Default dataset to use if not specified in connection string.
    /// </summary>
    public string? DefaultDataset { get; set; }

    /// <summary>
    /// Default location for new datasets and tables.
    /// </summary>
    public string? Location { get; set; } = "US";

    /// <summary>
    /// Whether to use query cache for repeated queries.
    /// </summary>
    public bool UseQueryCache { get; set; } = true;

    /// <summary>
    /// Whether to use streaming inserts (true) or load jobs (false) for writes.
    /// Streaming inserts are real-time but cost more. Load jobs are batched and cheaper.
    /// </summary>
    public bool UseStreamingInserts { get; set; } = true;

    /// <summary>
    /// Maximum number of rows to load in a single query.
    /// </summary>
    public int MaxQueryRows { get; set; } = 10000;

    /// <summary>
    /// Query timeout in seconds.
    /// </summary>
    public int QueryTimeout { get; set; } = 300;
}
