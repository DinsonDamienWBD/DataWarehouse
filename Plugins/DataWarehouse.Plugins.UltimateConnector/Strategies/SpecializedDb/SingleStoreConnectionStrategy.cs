using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.SpecializedDb
{
    public class SingleStoreConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private volatile TcpClient? _tcpClient;
        public override string StrategyId => "singlestore";
        public override string DisplayName => "SingleStore";
        public override string SemanticDescription => "Distributed SQL database combining transactions and analytics with MySQL compatibility";
        public override string[] Tags => new[] { "specialized", "singlestore", "distributed", "sql", "mysql-compatible" };
        public override ConnectionStrategyCapabilities Capabilities => new(SupportsPooling: true, SupportsStreaming: true, SupportsTransactions: true, SupportsBulkOperations: true, SupportsSchemaDiscovery: true, SupportsSsl: true, SupportsCompression: true, SupportsAuthentication: true, MaxConcurrentConnections: 200, SupportedAuthMethods: new[] { "password" });
        public SingleStoreConnectionStrategy(ILogger<SingleStoreConnectionStrategy>? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var (host, port) = ParseHostPort(config.ConnectionString, 3306);
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(host, port, ct);
            return new DefaultConnectionHandle(_tcpClient, new Dictionary<string, object> { ["host"] = host, ["port"] = port });
        }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { var client = handle.GetConnection<TcpClient>(); if (!client.Connected) return false; try { await client.GetStream().WriteAsync(Array.Empty<byte>(), 0, 0, ct).ConfigureAwait(false); return true; } catch { return false; } }
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { if (_tcpClient != null) { _tcpClient.Close(); _tcpClient.Dispose(); _tcpClient = null; } return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            // P2-2180: Measure actual latency with Stopwatch instead of hardcoded value.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();
            return new ConnectionHealth(isHealthy, isHealthy ? "SingleStore healthy" : "SingleStore unhealthy", sw.Elapsed, DateTimeOffset.UtcNow);
        }
        public override Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            // SingleStore (formerly MemSQL) uses the MySQL wire protocol (port 3306). Requires MySqlConnector or SingleStore.Data.
            // This connector provides TCP connectivity; integrate a MySQL-compatible ADO.NET driver for query execution.
            throw new InvalidOperationException("SingleStore query execution requires the SingleStore.Data or MySqlConnector ADO.NET driver. This connector provides TCP connectivity only.");
        }
        public override Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            throw new InvalidOperationException("SingleStore non-query execution requires the SingleStore.Data or MySqlConnector ADO.NET driver. This connector provides TCP connectivity only.");
        }
        public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            throw new InvalidOperationException("SingleStore schema discovery requires the SingleStore.Data or MySqlConnector ADO.NET driver. This connector provides TCP connectivity only.");
        }
        // P2-2203: Guard null/empty connectionString before Split to prevent empty-hostname SocketException.
        private static (string host, int port) ParseHostPort(string connectionString, int defaultPort)
        {
            if (string.IsNullOrEmpty(connectionString))
                throw new ArgumentException("ConnectionString must not be null or empty.", nameof(connectionString));
            // P2-2132: delegate to base class ParseHostPortSafe which handles IPv6 bracket notation
            return ParseHostPortSafe(connectionString, defaultPort);
        }
    }
}
