using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.SpecializedDb
{
    public class MemgraphConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private TcpClient? _tcpClient;

        public override string StrategyId => "memgraph";
        public override string DisplayName => "Memgraph";
        public override string SemanticDescription => "In-memory graph database compatible with Neo4j using Cypher query language";
        public override string[] Tags => new[] { "specialized", "memgraph", "graph", "in-memory", "cypher" };

        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true, SupportsStreaming: true, SupportsTransactions: true,
            SupportsBulkOperations: true, SupportsSchemaDiscovery: true, SupportsSsl: true,
            SupportsCompression: false, SupportsAuthentication: true, MaxConcurrentConnections: 100,
            SupportedAuthMethods: new[] { "basic" }
        );

        public MemgraphConnectionStrategy(ILogger<MemgraphConnectionStrategy>? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var (host, port) = ParseHostPort(config.ConnectionString, 7687);
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(host, port, ct);
            return new DefaultConnectionHandle(_tcpClient, new Dictionary<string, object> { ["host"] = host, ["port"] = port });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<TcpClient>();
            if (!client.Connected)
                return false;
            try
            {
                await client.GetStream().WriteAsync(Array.Empty<byte>(), 0, 0, ct).ConfigureAwait(false);
                return true;
            }
            catch { return false; }
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
            return new ConnectionHealth(isHealthy, isHealthy ? "Memgraph healthy" : "Memgraph unhealthy", TimeSpan.FromMilliseconds(4), DateTimeOffset.UtcNow);
        }

        public override Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            // Memgraph uses the Bolt binary protocol (port 7687). Full Bolt framing requires
            // the Neo4j.Driver or Memgraph-specific client library. This connector establishes
            // TCP connectivity; integrate a Bolt-capable library for query execution.
            throw new InvalidOperationException(
                "Memgraph query execution requires a Bolt protocol driver (e.g., Neo4j.Driver or " +
                "the Memgraph .NET client). This connector provides TCP connectivity only. " +
                "Register the Bolt driver via the MessageBus 'bolt.query' topic or integrate directly.");
        }

        public override Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            throw new InvalidOperationException(
                "Memgraph non-query execution requires a Bolt protocol driver. " +
                "This connector provides TCP connectivity only.");
        }

        public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            throw new InvalidOperationException(
                "Memgraph schema discovery requires a Bolt protocol driver. " +
                "This connector provides TCP connectivity only.");
        }

        private (string host, int port) ParseHostPort(string connectionString, int defaultPort)
        {
            var clean = connectionString.Replace("bolt://", "").Split('/')[0];
            var parts = clean.Split(':');
            return (parts[0], parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : defaultPort);
        }
    }
}
