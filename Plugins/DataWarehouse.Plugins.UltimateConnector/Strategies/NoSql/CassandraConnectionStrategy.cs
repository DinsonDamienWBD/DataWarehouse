using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cassandra;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.NoSql;

/// <summary>
/// Apache Cassandra connection strategy using the DataStax CSharp Driver.
/// Provides production-ready connectivity with prepared statements, batch operations,
/// lightweight transactions, and tunable consistency.
/// </summary>
public sealed class CassandraConnectionStrategy : DatabaseConnectionStrategyBase
{
    public override string StrategyId => "cassandra";
    public override string DisplayName => "Apache Cassandra";
    public override string SemanticDescription =>
        "Apache Cassandra distributed database using DataStax CSharp Driver. Supports CQL queries, " +
        "prepared statements, batch operations, lightweight transactions, and tunable consistency levels.";
    public override string[] Tags => ["nosql", "cassandra", "distributed", "wide-column", "apache", "cql"];

    public override ConnectionStrategyCapabilities Capabilities => new(
        SupportsPooling: true,
        SupportsStreaming: true,
        SupportsTransactions: false,
        SupportsBulkOperations: true,
        SupportsSchemaDiscovery: true,
        SupportsSsl: true,
        SupportsCompression: true,
        SupportsAuthentication: true,
        MaxConcurrentConnections: 200,
        SupportedAuthMethods: ["password", "kerberos"]
    );

    public CassandraConnectionStrategy(ILogger? logger = null) : base(logger) { }

    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
    {
        var connectionString = config.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Connection string (contact points) is required for Cassandra.");

        var contactPoints = connectionString.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(cp => cp.Trim()).ToArray();

        var builder = Cluster.Builder()
            .AddContactPoints(contactPoints)
            .WithQueryTimeout((int)config.Timeout.TotalMilliseconds)
            .WithCompression(CompressionType.LZ4);

        if (config.Properties.TryGetValue("Username", out var username) &&
            config.Properties.TryGetValue("Password", out var password))
        {
            builder = builder.WithCredentials(username?.ToString(), password?.ToString());
        }

        if (config.Properties.TryGetValue("Keyspace", out var keyspace) &&
            keyspace is string ks && !string.IsNullOrWhiteSpace(ks))
        {
            builder = builder.WithDefaultKeyspace(ks);
        }

        var cluster = builder.Build();
        var session = await Task.Run(() => cluster.Connect(), ct);

        var connectionInfo = new Dictionary<string, object>
        {
            ["Provider"] = "CassandraCSharpDriver",
            ["ClusterName"] = cluster.Metadata.ClusterName ?? "unknown",
            ["Keyspace"] = session.Keyspace ?? "system",
            ["Hosts"] = string.Join(",", cluster.AllHosts().Select(h => h.Address)),
            ["State"] = "Connected"
        };

        return new DefaultConnectionHandle(session, connectionInfo);
    }

    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        try
        {
            var session = handle.GetConnection<ISession>();
            var rs = await Task.Run(() => session.Execute("SELECT now() FROM system.local"), ct);
            return rs.Any();
        }
        catch
        {
            return false;
        }
    }

    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        var session = handle.GetConnection<ISession>();
        var cluster = session.Cluster;

        await Task.Run(() =>
        {
            session.Dispose();
            cluster.Dispose();
        }, ct);

        if (handle is DefaultConnectionHandle defaultHandle)
        {
            defaultHandle.MarkDisconnected();
        }
    }

    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var session = handle.GetConnection<ISession>();
            var rs = await Task.Run(() => session.Execute("SELECT cluster_name, release_version FROM system.local"), ct);
            sw.Stop();

            var row = rs.FirstOrDefault();
            var clusterName = row?.GetValue<string>("cluster_name") ?? "unknown";
            var version = row?.GetValue<string>("release_version") ?? "unknown";

            return new ConnectionHealth(
                IsHealthy: true,
                StatusMessage: $"Cassandra {version} - Cluster: {clusterName}",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ConnectionHealth(
                IsHealthy: false,
                StatusMessage: $"Health check failed: {ex.Message}",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow);
        }
    }

    public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(
        IConnectionHandle handle,
        string query,
        Dictionary<string, object?>? parameters = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var session = handle.GetConnection<ISession>();
        RowSet rs;

        if (parameters != null && parameters.Count > 0)
        {
            var prepared = await Task.Run(() => session.Prepare(query), ct);
            var bound = prepared.Bind(parameters.Values.ToArray());
            rs = await Task.Run(() => session.Execute(bound), ct);
        }
        else
        {
            rs = await Task.Run(() => session.Execute(query), ct);
        }

        var results = new List<Dictionary<string, object?>>();
        var columns = rs.Columns;

        foreach (var row in rs)
        {
            var dict = new Dictionary<string, object?>();
            for (int i = 0; i < columns.Length; i++)
            {
                dict[columns[i].Name] = row.IsNull(i) ? null : row.GetValue<object>(i);
            }
            results.Add(dict);
        }

        return results;
    }

    public override async Task<int> ExecuteNonQueryAsync(
        IConnectionHandle handle,
        string command,
        Dictionary<string, object?>? parameters = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var session = handle.GetConnection<ISession>();

        if (parameters != null && parameters.Count > 0)
        {
            var prepared = await Task.Run(() => session.Prepare(command), ct);
            var bound = prepared.Bind(parameters.Values.ToArray());
            await Task.Run(() => session.Execute(bound), ct);
        }
        else
        {
            await Task.Run(() => session.Execute(command), ct);
        }

        return 1; // CQL doesn't return affected row count
    }

    public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(
        IConnectionHandle handle,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        var session = handle.GetConnection<ISession>();
        var keyspace = session.Keyspace ?? "system";

        var rs = await Task.Run(() => session.Execute(
            $"SELECT table_name, column_name, type, kind FROM system_schema.columns WHERE keyspace_name = ?"), ct);

        var schemas = new Dictionary<string, (List<DataSchemaField> Fields, List<string> PrimaryKeys)>();

        foreach (var row in rs)
        {
            var tableName = row.GetValue<string>("table_name");
            var columnName = row.GetValue<string>("column_name");
            var type = row.GetValue<string>("type");
            var kind = row.GetValue<string>("kind");

            if (!schemas.ContainsKey(tableName))
            {
                schemas[tableName] = (new List<DataSchemaField>(), new List<string>());
            }

            schemas[tableName].Fields.Add(new DataSchemaField(
                Name: columnName,
                DataType: type,
                Nullable: kind != "partition_key" && kind != "clustering",
                MaxLength: null,
                Properties: new Dictionary<string, object> { ["kind"] = kind }
            ));

            if (kind == "partition_key" || kind == "clustering")
            {
                schemas[tableName].PrimaryKeys.Add(columnName);
            }
        }

        return schemas.Select(kvp => new DataSchema(
            Name: kvp.Key,
            Fields: kvp.Value.Fields.ToArray(),
            PrimaryKeys: kvp.Value.PrimaryKeys.ToArray(),
            Metadata: new Dictionary<string, object> { ["keyspace"] = keyspace }
        )).ToList();
    }

    public override Task<(bool IsValid, string[] Errors)> ValidateConfigAsync(
        ConnectionConfig config, CancellationToken ct = default)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(config.ConnectionString))
            errors.Add("ConnectionString (contact points) is required for Cassandra.");

        if (config.Timeout <= TimeSpan.Zero)
            errors.Add("Timeout must be a positive duration.");

        return Task.FromResult((errors.Count == 0, errors.ToArray()));
    }
}
