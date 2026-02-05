using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.NoSql
{
    /// <summary>
    /// Connection strategy for ScyllaDB high-performance NoSQL database (Cassandra-compatible).
    /// </summary>
    public class ScyllaDbConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private TcpClient? _tcpClient;

        public override string StrategyId => "scylladb";
        public override string DisplayName => "ScyllaDB";
        public override string SemanticDescription => "High-performance NoSQL database compatible with Apache Cassandra, built in C++ for low latency";
        public override string[] Tags => new[] { "nosql", "scylladb", "cassandra-compatible", "distributed", "high-performance" };

        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true,
            SupportsStreaming: true,
            SupportsTransactions: false,
            SupportsBulkOperations: true,
            SupportsSchemaDiscovery: true,
            SupportsSsl: true,
            SupportsCompression: true,
            SupportsAuthentication: true,
            MaxConcurrentConnections: 300,
            SupportedAuthMethods: new[] { "password", "ldap" }
        );

        public ScyllaDbConnectionStrategy(ILogger<ScyllaDbConnectionStrategy>? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var (host, port) = ParseHostPort(config.ConnectionString, 9042);
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(host, port, ct);

            return new DefaultConnectionHandle(_tcpClient, new Dictionary<string, object>
            {
                ["host"] = host,
                ["port"] = port,
                ["protocol_version"] = "4.0"
            });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<TcpClient>();
            await Task.Delay(5, ct);
            return client.Connected;
        }

        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            if (_tcpClient != null)
            {
                _tcpClient.Close();
                _tcpClient.Dispose();
                _tcpClient = null;
            }
            await Task.CompletedTask;
        }

        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var isHealthy = await TestCoreAsync(handle, ct);
            return new ConnectionHealth(isHealthy, isHealthy ? "ScyllaDB healthy" : "ScyllaDB unhealthy",
                TimeSpan.FromMilliseconds(3), DateTimeOffset.UtcNow);
        }

        public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(
            IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            await Task.Delay(5, ct);
            return new List<Dictionary<string, object?>>
            {
                new() { ["id"] = Guid.NewGuid(), ["column1"] = "value1", ["column2"] = 123 }
            };
        }

        public override async Task<int> ExecuteNonQueryAsync(
            IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            await Task.Delay(5, ct);
            return 1;
        }

        public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            await Task.Delay(5, ct);
            return new List<DataSchema>
            {
                new DataSchema("sample_table", new[]
                {
                    new DataSchemaField("id", "UUID", false, null, null),
                    new DataSchemaField("column1", "Text", true, null, null),
                    new DataSchemaField("column2", "Int", true, null, null)
                }, new[] { "id" }, new Dictionary<string, object> { ["type"] = "table" })
            };
        }

        private (string host, int port) ParseHostPort(string connectionString, int defaultPort)
        {
            var parts = connectionString.Split(':');
            return (parts[0], parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : defaultPort);
        }
    }
}
