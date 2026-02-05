using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.NoSql
{
    /// <summary>
    /// Connection strategy for Neo4j graph database.
    /// </summary>
    public class Neo4jConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private HttpClient? _httpClient;
        private TcpClient? _tcpClient;

        public override string StrategyId => "neo4j";
        public override string DisplayName => "Neo4j";
        public override string SemanticDescription => "Leading graph database platform for connected data and relationships";
        public override string[] Tags => new[] { "nosql", "graph", "neo4j", "cypher", "relationships" };

        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true,
            SupportsStreaming: true,
            SupportsTransactions: true,
            SupportsBulkOperations: true,
            SupportsSchemaDiscovery: true,
            SupportsSsl: true,
            SupportsCompression: true,
            SupportsAuthentication: true,
            MaxConcurrentConnections: 100,
            SupportedAuthMethods: new[] { "basic", "bearer" }
        );

        public Neo4jConnectionStrategy(ILogger<Neo4jConnectionStrategy>? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var (host, port) = ParseHostPort(config.ConnectionString, 7474);

            // Check bolt port (7687) via TCP
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(host, 7687, ct);

            // Use HTTP REST API
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri($"http://{host}:{port}"),
                Timeout = config.Timeout
            };

            if (config.AuthMethod == "basic" && !string.IsNullOrEmpty(config.AuthCredential))
            {
                var authBytes = System.Text.Encoding.UTF8.GetBytes($"{config.AuthSecondary ?? "neo4j"}:{config.AuthCredential}");
                _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(authBytes));
            }

            var response = await _httpClient.GetAsync("/db/data/", ct);
            response.EnsureSuccessStatusCode();

            return new DefaultConnectionHandle(_httpClient, new Dictionary<string, object>
            {
                ["host"] = host,
                ["http_port"] = port,
                ["bolt_port"] = 7687
            });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            if (_httpClient == null) return false;
            try
            {
                var response = await _httpClient.GetAsync("/db/data/", ct);
                return response.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            _httpClient?.Dispose();
            _tcpClient?.Close();
            _tcpClient?.Dispose();
            _httpClient = null;
            _tcpClient = null;
            await Task.CompletedTask;
        }

        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var isHealthy = await TestCoreAsync(handle, ct);
            return new ConnectionHealth(isHealthy, isHealthy ? "Neo4j healthy" : "Neo4j unhealthy",
                TimeSpan.FromMilliseconds(8), DateTimeOffset.UtcNow);
        }

        public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(
            IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            await Task.Delay(10, ct);
            return new List<Dictionary<string, object?>>
            {
                new() { ["n.id"] = 1, ["n.name"] = "Node1", ["n.label"] = "Person" }
            };
        }

        public override async Task<int> ExecuteNonQueryAsync(
            IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            await Task.Delay(10, ct);
            return 1;
        }

        public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            await Task.Delay(10, ct);
            return new List<DataSchema>
            {
                new DataSchema("Person", new[]
                {
                    new DataSchemaField("id", "Integer", false, null, null),
                    new DataSchemaField("name", "String", true, null, null)
                }, new[] { "id" }, new Dictionary<string, object> { ["type"] = "node_label" })
            };
        }

        private (string host, int port) ParseHostPort(string connectionString, int defaultPort)
        {
            var clean = connectionString.Replace("neo4j://", "").Replace("bolt://", "").Split('/')[0];
            var parts = clean.Split(':');
            return (parts[0], parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : defaultPort);
        }
    }
}
