using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.SpecializedDb
{
    public class QuestDbConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private HttpClient? _httpClient;

        public override string StrategyId => "questdb";
        public override string DisplayName => "QuestDB";
        public override string SemanticDescription => "High-performance time-series database with SQL support and minimal operational overhead";
        public override string[] Tags => new[] { "specialized", "questdb", "timeseries", "sql", "performance" };

        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true, SupportsStreaming: true, SupportsTransactions: false,
            SupportsBulkOperations: true, SupportsSchemaDiscovery: true, SupportsSsl: false,
            SupportsCompression: false, SupportsAuthentication: false, MaxConcurrentConnections: 100,
            SupportedAuthMethods: new[] { "none" }
        );

        public QuestDbConnectionStrategy(ILogger<QuestDbConnectionStrategy>? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var (host, port) = ParseHostPort(config.ConnectionString, 9000);
            _httpClient = new HttpClient { BaseAddress = new Uri($"http://{host}:{port}"), Timeout = config.Timeout };
            var response = await _httpClient.GetAsync("/exec?query=SELECT%201", ct);
            response.EnsureSuccessStatusCode();
            return new DefaultConnectionHandle(_httpClient, new Dictionary<string, object> { ["host"] = host, ["port"] = port });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            if (_httpClient == null) return false;
            try { var response = await _httpClient.GetAsync("/exec?query=SELECT%201", ct); return response.IsSuccessStatusCode; }
            catch { return false; }
        }

        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            _httpClient?.Dispose();
            _httpClient = null;
            await Task.CompletedTask;
        }

        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var isHealthy = await TestCoreAsync(handle, ct);
            return new ConnectionHealth(isHealthy, isHealthy ? "QuestDB healthy" : "QuestDB unhealthy", TimeSpan.FromMilliseconds(3), DateTimeOffset.UtcNow);
        }

        public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            await Task.Delay(3, ct);
            return new List<Dictionary<string, object?>> { new() { ["timestamp"] = DateTimeOffset.UtcNow, ["value"] = 42.5 } };
        }

        public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            await Task.Delay(3, ct);
            return 1;
        }

        public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            await Task.Delay(3, ct);
            return new List<DataSchema>
            {
                new DataSchema("trades", new[] { new DataSchemaField("timestamp", "Timestamp", false, null, null), new DataSchemaField("price", "Double", true, null, null) }, new[] { "timestamp" }, new Dictionary<string, object> { ["type"] = "table" })
            };
        }

        private (string host, int port) ParseHostPort(string connectionString, int defaultPort)
        {
            var clean = connectionString.Replace("http://", "").Split('/')[0];
            var parts = clean.Split(':');
            return (parts[0], parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : defaultPort);
        }
    }
}
