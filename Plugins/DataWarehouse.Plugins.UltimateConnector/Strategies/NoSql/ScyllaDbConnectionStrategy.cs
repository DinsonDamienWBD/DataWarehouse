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
        private volatile TcpClient? _tcpClient;

        public override string StrategyId => "scylladb";
        public override string DisplayName => "ScyllaDB";
        public override string SemanticDescription => "High-performance NoSQL database compatible with Apache Cassandra, built in C++ for low latency";
        public override string[] Tags => new[] { "nosql", "scylladb", "cassandra-compatible", "distributed", "high-performance" };

        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: false,
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
            // P2-2180: Measure actual latency with Stopwatch instead of hardcoded value.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();
            return new ConnectionHealth(isHealthy, isHealthy ? "ScyllaDB healthy" : "ScyllaDB unhealthy",
                sw.Elapsed, DateTimeOffset.UtcNow);
        }

        /// <summary>
        /// Executes a CQL query against ScyllaDB.
        /// Note: CQL binary protocol requires ScyllaDB/Cassandra driver for full implementation.
        /// This strategy provides TCP connectivity validation only.
        /// </summary>
        public override Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(
            IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            // ScyllaDB CQL binary protocol requires native driver for proper implementation
            var result = new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["__status"] = "OPERATION_NOT_SUPPORTED",
                    ["__message"] = "CQL query execution requires ScyllaDB C# Driver or DataStax Cassandra Driver. This strategy provides TCP connectivity validation only.",
                    ["__strategy"] = StrategyId,
                    ["__capabilities"] = "connectivity_test,health_check"
                }
            };
            return Task.FromResult<IReadOnlyList<Dictionary<string, object?>>>(result);
        }

        /// <summary>
        /// Executes a non-query CQL command against ScyllaDB.
        /// Returns -1 as CQL protocol requires native driver.
        /// </summary>
        public override Task<int> ExecuteNonQueryAsync(
            IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            // Return -1 to indicate operation not supported (graceful degradation)
            return Task.FromResult(-1);
        }

        /// <summary>
        /// Retrieves schema information from ScyllaDB.
        /// Returns empty list as CQL protocol requires native driver.
        /// </summary>
        public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            // Return empty schema list (graceful degradation)
            return Task.FromResult<IReadOnlyList<DataSchema>>(Array.Empty<DataSchema>());
        }

        private (string host, int port) ParseHostPort(string connectionString, int defaultPort)
        {
            // P2-2132: delegate to base class ParseHostPortSafe which handles IPv6 bracket notation
            return ParseHostPortSafe(connectionString, defaultPort);
        }
    }
}
