using System.Runtime.CompilerServices;
using System.Text;
using Cassandra;
using DataWarehouse.SDK.Connectors;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Plugins.DataConnectors;

/// <summary>
/// Production-ready Apache Cassandra database connector plugin.
/// Provides full CRUD operations with real database connectivity via CassandraCSharpDriver.
/// Supports keyspace/table discovery, parameterized CQL queries, batch statements, and connection pooling.
/// </summary>
public class CassandraConnectorPlugin : DatabaseConnectorPluginBase
{
    private ICluster? _cluster;
    private ISession? _session;
    private string? _keyspace;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly Dictionary<string, DataSchema> _schemaCache = new();
    private readonly Dictionary<string, PreparedStatement> _preparedStatementCache = new();
    private CassandraConnectorConfig _config = new();

    /// <inheritdoc />
    public override string Id => "datawarehouse.connector.cassandra";

    /// <inheritdoc />
    public override string Name => "Apache Cassandra Connector";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override string ConnectorId => "cassandra";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.InfrastructureProvider;

    /// <inheritdoc />
    public override ConnectorCapabilities Capabilities =>
        ConnectorCapabilities.Read |
        ConnectorCapabilities.Write |
        ConnectorCapabilities.Schema |
        ConnectorCapabilities.Transactions |
        ConnectorCapabilities.BulkOperations;

    /// <summary>
    /// Configures the connector with Cassandra-specific options.
    /// </summary>
    /// <param name="config">Cassandra-specific configuration.</param>
    public void Configure(CassandraConnectorConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <inheritdoc />
    protected override async Task<ConnectionResult> EstablishConnectionAsync(ConnectorConfig config, CancellationToken ct)
    {
        await _connectionLock.WaitAsync(ct);
        try
        {
            if (string.IsNullOrWhiteSpace(config.ConnectionString))
            {
                return new ConnectionResult(false, "Connection string is required", null);
            }

            // Parse connection string to extract contact points, keyspace, and credentials
            var connectionParams = ParseConnectionString(config.ConnectionString);

            if (!connectionParams.TryGetValue("ContactPoints", out var contactPointsStr))
            {
                return new ConnectionResult(false, "Contact points are required in connection string", null);
            }

            var contactPoints = contactPointsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            connectionParams.TryGetValue("Keyspace", out _keyspace);
            connectionParams.TryGetValue("Username", out var username);
            connectionParams.TryGetValue("Password", out var password);
            connectionParams.TryGetValue("Port", out var portStr);
            var port = int.TryParse(portStr, out var p) ? p : 9042;

            // Build cluster with connection pooling and retry policies
            var clusterBuilder = Cluster.Builder()
                .AddContactPoints(contactPoints)
                .WithPort(port);

            // Configure authentication
            if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
            {
                clusterBuilder.WithCredentials(username, password);
            }

            // Configure connection pool settings
            var poolingOptions = new PoolingOptions()
                .SetCoreConnectionsPerHost(HostDistance.Local, _config.CoreConnectionsPerHost)
                .SetMaxConnectionsPerHost(HostDistance.Local, _config.MaxConnectionsPerHost)
                .SetCoreConnectionsPerHost(HostDistance.Remote, _config.CoreConnectionsPerHost / 2)
                .SetMaxConnectionsPerHost(HostDistance.Remote, _config.MaxConnectionsPerHost / 2);

            clusterBuilder.WithPoolingOptions(poolingOptions);

            // Configure retry policy
            clusterBuilder.WithRetryPolicy(new DefaultRetryPolicy());

            // Configure query options
            var queryOptions = new QueryOptions()
                .SetConsistencyLevel(_config.DefaultConsistencyLevel)
                .SetPageSize(_config.PageSize);

            clusterBuilder.WithQueryOptions(queryOptions);

            // Configure socket options with timeout
            var socketOptions = new SocketOptions()
                .SetConnectTimeoutMillis(_config.ConnectTimeout)
                .SetReadTimeoutMillis(_config.ReadTimeout);

            clusterBuilder.WithSocketOptions(socketOptions);

            // Build and connect
            _cluster = clusterBuilder.Build();

            // Create session with or without keyspace
            if (!string.IsNullOrWhiteSpace(_keyspace))
            {
                _session = await Task.Run(() => _cluster.Connect(_keyspace), ct);
            }
            else
            {
                _session = await Task.Run(() => _cluster.Connect(), ct);
            }

            // Retrieve cluster and session info
            var serverInfo = new Dictionary<string, object>
            {
                ["ClusterName"] = _cluster.Metadata.ClusterName ?? "unknown",
                ["Keyspace"] = _keyspace ?? "none",
                ["ContactPoints"] = string.Join(", ", contactPoints),
                ["Port"] = port,
                ["ConnectedHosts"] = _cluster.AllHosts().Count(),
                ["ConnectionState"] = "Connected"
            };

            // Get Cassandra version from system tables
            var versionRow = await _session.ExecuteAsync(new SimpleStatement("SELECT release_version FROM system.local"));
            var versionResult = versionRow.FirstOrDefault();
            if (versionResult != null)
            {
                serverInfo["CassandraVersion"] = versionResult.GetValue<string>("release_version");
            }

            return new ConnectionResult(true, null, serverInfo);
        }
        catch (NoHostAvailableException ex)
        {
            return new ConnectionResult(false, $"No Cassandra hosts available: {ex.Message}", null);
        }
        catch (AuthenticationException ex)
        {
            return new ConnectionResult(false, $"Authentication failed: {ex.Message}", null);
        }
        catch (Exception ex)
        {
            return new ConnectionResult(false, $"Connection failed: {ex.Message}", null);
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
            _preparedStatementCache.Clear();
            _schemaCache.Clear();

            if (_session != null)
            {
                await Task.Run(() => _session.Dispose());
                _session = null;
            }

            if (_cluster != null)
            {
                await Task.Run(() => _cluster.Dispose());
                _cluster = null;
            }

            _keyspace = null;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task<bool> PingAsync()
    {
        if (_session == null || _cluster == null) return false;

        try
        {
            var result = await _session.ExecuteAsync(new SimpleStatement("SELECT now() FROM system.local").SetReadTimeoutMillis(5000));
            return result.Any();
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    protected override async Task<DataSchema> FetchSchemaAsync()
    {
        if (_session == null || _cluster == null)
            throw new InvalidOperationException("Not connected to database");

        var keyspaceToQuery = _keyspace ?? "system_schema";

        // Query system_schema for table and column metadata
        var cql = @"
            SELECT keyspace_name, table_name, column_name, type, kind
            FROM system_schema.columns
            WHERE keyspace_name = ?
            ORDER BY keyspace_name, table_name, position";

        var preparedStatement = await _session.PrepareAsync(cql);
        var boundStatement = preparedStatement.Bind(keyspaceToQuery);
        var rows = await _session.ExecuteAsync(boundStatement);

        var fields = new List<DataSchemaField>();
        var primaryKeys = new List<string>();
        var tableCount = 0;
        var currentTable = "";

        foreach (var row in rows)
        {
            var keyspaceName = row.GetValue<string>("keyspace_name");
            var tableName = row.GetValue<string>("table_name");
            var columnName = row.GetValue<string>("column_name");
            var dataType = row.GetValue<string>("type");
            var kind = row.GetValue<string>("kind");

            if (tableName != currentTable)
            {
                currentTable = tableName;
                tableCount++;
            }

            var isPrimaryKey = kind == "partition_key" || kind == "clustering";
            var isPartitionKey = kind == "partition_key";

            fields.Add(new DataSchemaField(
                columnName,
                MapCassandraType(dataType),
                kind == "regular", // Regular columns are nullable in Cassandra
                null,
                new Dictionary<string, object>
                {
                    ["keyspace"] = keyspaceName,
                    ["tableName"] = tableName,
                    ["cassandraType"] = dataType,
                    ["kind"] = kind,
                    ["isPrimaryKey"] = isPrimaryKey,
                    ["isPartitionKey"] = isPartitionKey
                }
            ));

            if (isPrimaryKey)
            {
                primaryKeys.Add(columnName);
            }
        }

        return new DataSchema(
            Name: keyspaceToQuery,
            Fields: fields.ToArray(),
            PrimaryKeys: primaryKeys.Distinct().ToArray(),
            Metadata: new Dictionary<string, object>
            {
                ["TableCount"] = tableCount,
                ["FieldCount"] = fields.Count,
                ["KeyspaceName"] = keyspaceToQuery,
                ["SchemaVersion"] = "1.0"
            }
        );
    }

    /// <summary>
    /// Gets the schema for a specific table in the current keyspace.
    /// </summary>
    /// <param name="tableName">Name of the table.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Table schema.</returns>
    public async Task<DataSchema> GetTableSchemaAsync(string tableName, CancellationToken ct = default)
    {
        if (_schemaCache.TryGetValue(tableName, out var cached))
            return cached;

        if (_session == null || _cluster == null)
            throw new InvalidOperationException("Not connected to database");

        var keyspaceToQuery = _keyspace ?? throw new InvalidOperationException("No keyspace selected");

        var cql = @"
            SELECT column_name, type, kind
            FROM system_schema.columns
            WHERE keyspace_name = ? AND table_name = ?
            ORDER BY position";

        var preparedStatement = await _session.PrepareAsync(cql);
        var boundStatement = preparedStatement.Bind(keyspaceToQuery, tableName);
        var rows = await _session.ExecuteAsync(boundStatement);

        var fields = new List<DataSchemaField>();
        var primaryKeys = new List<string>();

        foreach (var row in rows)
        {
            var columnName = row.GetValue<string>("column_name");
            var dataType = row.GetValue<string>("type");
            var kind = row.GetValue<string>("kind");

            var isPrimaryKey = kind == "partition_key" || kind == "clustering";

            fields.Add(new DataSchemaField(
                columnName,
                MapCassandraType(dataType),
                kind == "regular",
                null,
                new Dictionary<string, object>
                {
                    ["cassandraType"] = dataType,
                    ["kind"] = kind
                }
            ));

            if (isPrimaryKey)
                primaryKeys.Add(columnName);
        }

        var schema = new DataSchema(
            Name: tableName,
            Fields: fields.ToArray(),
            PrimaryKeys: primaryKeys.ToArray(),
            Metadata: new Dictionary<string, object>
            {
                ["TableName"] = tableName,
                ["Keyspace"] = keyspaceToQuery
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
        if (_session == null)
            throw new InvalidOperationException("Not connected to database");

        var cql = BuildSelectQuery(query);
        var statement = new SimpleStatement(cql);

        if (_config.ReadTimeout > 0)
            statement.SetReadTimeoutMillis(_config.ReadTimeout);

        // Set consistency level if specified
        statement.SetConsistencyLevel(_config.DefaultConsistencyLevel);

        // Set page size for automatic paging
        statement.SetPageSize(_config.PageSize);

        long position = query.Offset ?? 0;
        var rowSet = await _session.ExecuteAsync(statement);

        foreach (var row in rowSet)
        {
            if (ct.IsCancellationRequested) yield break;

            var values = new Dictionary<string, object?>();
            var columns = row.GetColumns();

            foreach (var column in columns)
            {
                var columnName = column.Name;
                var value = row.IsNull(columnName) ? null : row.GetValue<object>(column.Type, columnName);
                values[columnName] = value;
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
        if (_session == null)
            throw new InvalidOperationException("Not connected to database");

        long written = 0;
        long failed = 0;
        var errors = new List<string>();

        var tableName = options.TargetTable ?? throw new ArgumentException("TargetTable is required");

        // Get schema for the target table
        var schema = await GetTableSchemaAsync(tableName, ct);
        var columns = schema.Fields.Select(f => f.Name).ToArray();

        var batch = new List<DataRecord>();

        await foreach (var record in records.WithCancellation(ct))
        {
            batch.Add(record);

            if (batch.Count >= options.BatchSize)
            {
                var (w, f, e) = await WriteBatchAsync(tableName, columns, batch, options.Mode, ct);
                written += w;
                failed += f;
                errors.AddRange(e);
                batch.Clear();
            }
        }

        // Write remaining records
        if (batch.Count > 0)
        {
            var (w, f, e) = await WriteBatchAsync(tableName, columns, batch, options.Mode, ct);
            written += w;
            failed += f;
            errors.AddRange(e);
        }

        return new WriteResult(written, failed, errors.Count > 0 ? errors.ToArray() : null);
    }

    private async Task<(long written, long failed, List<string> errors)> WriteBatchAsync(
        string tableName,
        string[] columns,
        List<DataRecord> batch,
        SDK.Connectors.WriteMode mode,
        CancellationToken ct)
    {
        if (_session == null)
            throw new InvalidOperationException("Not connected to database");

        long written = 0;
        long failed = 0;
        var errors = new List<string>();

        // Use batch statement for multiple inserts (transaction-like behavior)
        var batchStatement = new BatchStatement();

        foreach (var record in batch)
        {
            try
            {
                var recordColumns = record.Values.Keys.Where(k => columns.Contains(k)).ToArray();
                var cql = BuildInsertStatement(tableName, recordColumns);

                // Get or prepare statement
                PreparedStatement preparedStatement;
                if (!_preparedStatementCache.TryGetValue(cql, out preparedStatement!))
                {
                    preparedStatement = await _session.PrepareAsync(cql);
                    _preparedStatementCache[cql] = preparedStatement;
                }

                var values = recordColumns.Select(c => record.Values[c]).ToArray();
                var boundStatement = preparedStatement.Bind(values);

                batchStatement.Add(boundStatement);
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"Record at position {record.Position}: {ex.Message}");
            }
        }

        // Execute batch statement
        if (batchStatement.Queries.Count > 0)
        {
            try
            {
                await _session.ExecuteAsync(batchStatement);
                written = batch.Count - failed;
            }
            catch (Exception ex)
            {
                failed = batch.Count;
                written = 0;
                errors.Add($"Batch execution failed: {ex.Message}");
            }
        }

        return (written, failed, errors);
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
        sb.Append(QuoteIdentifier(query.TableOrCollection ?? "data"));

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

        // Note: Cassandra doesn't support OFFSET in the same way as SQL databases
        // Offset handling is done via token-based pagination or ALLOW FILTERING

        return sb.ToString();
    }

    /// <inheritdoc />
    protected override string BuildInsertStatement(string table, string[] columns)
    {
        var columnList = string.Join(", ", columns.Select(QuoteIdentifier));
        var placeholders = string.Join(", ", Enumerable.Range(0, columns.Length).Select(_ => "?"));

        return $"INSERT INTO {QuoteIdentifier(table)} ({columnList}) VALUES ({placeholders})";
    }

    /// <summary>
    /// Executes a raw CQL query with parameterized values.
    /// </summary>
    /// <param name="cql">CQL query to execute.</param>
    /// <param name="parameters">Query parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Query results.</returns>
    public async IAsyncEnumerable<DataRecord> ExecuteRawQueryAsync(
        string cql,
        object[]? parameters = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_session == null)
            throw new InvalidOperationException("Not connected to database");

        PreparedStatement preparedStatement;
        if (!_preparedStatementCache.TryGetValue(cql, out preparedStatement!))
        {
            preparedStatement = await _session.PrepareAsync(cql);
            _preparedStatementCache[cql] = preparedStatement;
        }

        var boundStatement = parameters != null ? preparedStatement.Bind(parameters) : preparedStatement.Bind();
        boundStatement.SetConsistencyLevel(_config.DefaultConsistencyLevel);

        long position = 0;
        var rowSet = await _session.ExecuteAsync(boundStatement);

        foreach (var row in rowSet)
        {
            if (ct.IsCancellationRequested) yield break;

            var values = new Dictionary<string, object?>();
            var columns = row.GetColumns();

            foreach (var column in columns)
            {
                var columnName = column.Name;
                values[columnName] = row.IsNull(columnName) ? null : row.GetValue<object>(column.Type, columnName);
            }

            yield return new DataRecord(values, position++, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// Executes a non-query CQL command (INSERT, UPDATE, DELETE).
    /// </summary>
    /// <param name="cql">CQL command to execute.</param>
    /// <param name="parameters">Command parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Row set result.</returns>
    public async Task<RowSet> ExecuteNonQueryAsync(
        string cql,
        object[]? parameters = null,
        CancellationToken ct = default)
    {
        if (_session == null)
            throw new InvalidOperationException("Not connected to database");

        PreparedStatement preparedStatement;
        if (!_preparedStatementCache.TryGetValue(cql, out preparedStatement!))
        {
            preparedStatement = await _session.PrepareAsync(cql);
            _preparedStatementCache[cql] = preparedStatement;
        }

        var boundStatement = parameters != null ? preparedStatement.Bind(parameters) : preparedStatement.Bind();
        boundStatement.SetConsistencyLevel(_config.DefaultConsistencyLevel);

        return await _session.ExecuteAsync(boundStatement);
    }

    /// <summary>
    /// Executes a batch of CQL statements atomically.
    /// </summary>
    /// <param name="statements">List of CQL statements with their parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task representing the batch execution.</returns>
    public async Task ExecuteBatchAsync(
        IEnumerable<(string cql, object[] parameters)> statements,
        CancellationToken ct = default)
    {
        if (_session == null)
            throw new InvalidOperationException("Not connected to database");

        var batchStatement = new BatchStatement();

        foreach (var (cql, parameters) in statements)
        {
            PreparedStatement preparedStatement;
            if (!_preparedStatementCache.TryGetValue(cql, out preparedStatement!))
            {
                preparedStatement = await _session.PrepareAsync(cql);
                _preparedStatementCache[cql] = preparedStatement;
            }

            var boundStatement = preparedStatement.Bind(parameters);
            batchStatement.Add(boundStatement);
        }

        await _session.ExecuteAsync(batchStatement);
    }

    private static string QuoteIdentifier(string identifier)
    {
        // Cassandra uses double quotes for case-sensitive identifiers
        return $"\"{identifier.Replace("\"", "\"\"")}\"";
    }

    private static string MapCassandraType(string cassandraType)
    {
        // Handle parameterized types (e.g., list<text>, map<text,int>)
        var baseType = cassandraType.Contains('<')
            ? cassandraType.Substring(0, cassandraType.IndexOf('<'))
            : cassandraType;

        return baseType.ToLowerInvariant() switch
        {
            "int" => "int",
            "bigint" => "long",
            "smallint" => "short",
            "tinyint" => "byte",
            "varint" => "bigint",
            "float" => "float",
            "double" => "double",
            "decimal" => "decimal",
            "boolean" => "bool",
            "text" or "varchar" or "ascii" => "string",
            "blob" => "bytes",
            "timestamp" => "datetime",
            "date" => "date",
            "time" => "time",
            "uuid" or "timeuuid" => "uuid",
            "inet" => "ipaddress",
            "list" => "list",
            "set" => "set",
            "map" => "map",
            "tuple" => "tuple",
            "frozen" => "frozen",
            _ => cassandraType
        };
    }

    private static Dictionary<string, string> ParseConnectionString(string connectionString)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var keyValue = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (keyValue.Length == 2)
            {
                result[keyValue[0]] = keyValue[1];
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
/// Configuration options for the Cassandra connector.
/// </summary>
public class CassandraConnectorConfig
{
    /// <summary>
    /// Core number of connections per host in the connection pool.
    /// </summary>
    public int CoreConnectionsPerHost { get; set; } = 2;

    /// <summary>
    /// Maximum number of connections per host in the connection pool.
    /// </summary>
    public int MaxConnectionsPerHost { get; set; } = 8;

    /// <summary>
    /// Connect timeout in milliseconds.
    /// </summary>
    public int ConnectTimeout { get; set; } = 5000;

    /// <summary>
    /// Read timeout in milliseconds.
    /// </summary>
    public int ReadTimeout { get; set; } = 12000;

    /// <summary>
    /// Default consistency level for queries.
    /// </summary>
    public Cassandra.ConsistencyLevel DefaultConsistencyLevel { get; set; } = Cassandra.ConsistencyLevel.LocalQuorum;

    /// <summary>
    /// Page size for automatic paging of query results.
    /// </summary>
    public int PageSize { get; set; } = 5000;

    /// <summary>
    /// Whether to enable metrics collection.
    /// </summary>
    public bool EnableMetrics { get; set; } = true;
}
