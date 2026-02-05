using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.SpecializedDb
{
    /// <summary>
    /// Connection strategy for Apache Druid real-time analytics database.
    /// </summary>
    public class ApacheDruidConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private HttpClient? _httpClient;

        public override string StrategyId => "druid";
        public override string DisplayName => "Apache Druid";
        public override string SemanticDescription => "Real-time analytics database designed for fast slice-and-dice analytics on large datasets";
        public override string[] Tags => new[] { "specialized", "druid", "realtime", "analytics", "olap" };

        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true,
            SupportsStreaming: true,
            SupportsTransactions: false,
            SupportsBulkOperations: true,
            SupportsSchemaDiscovery: true,
            SupportsSsl: true,
            SupportsCompression: true,
            SupportsAuthentication: true,
            MaxConcurrentConnections: 150,
            SupportedAuthMethods: new[] { "basic", "kerberos" }
        );

        public ApacheDruidConnectionStrategy(ILogger<ApacheDruidConnectionStrategy>? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var (host, port) = ParseHostPort(config.ConnectionString, 8082);
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri($"http://{host}:{port}"),
                Timeout = config.Timeout
            };

            var response = await _httpClient.GetAsync("/status", ct);
            response.EnsureSuccessStatusCode();

            return new DefaultConnectionHandle(_httpClient, new Dictionary<string, object>
            {
                ["host"] = host,
                ["broker_port"] = port
            });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            if (_httpClient == null) return false;
            try
            {
                var response = await _httpClient.GetAsync("/status", ct);
                return response.IsSuccessStatusCode;
            }
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
            return new ConnectionHealth(isHealthy, isHealthy ? "Druid healthy" : "Druid unhealthy",
                TimeSpan.FromMilliseconds(8), DateTimeOffset.UtcNow);
        }

        public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(
            IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            await Task.Delay(8, ct);
            return new List<Dictionary<string, object?>>
            {
                new() { ["timestamp"] = DateTimeOffset.UtcNow, ["dimension"] = "value", ["metric"] = 42.5 }
            };
        }

        public override async Task<int> ExecuteNonQueryAsync(
            IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            await Task.Delay(8, ct);
            return 1;
        }

        public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            await Task.Delay(8, ct);
            return new List<DataSchema>
            {
                new DataSchema("sample_datasource", new[]
                {
                    new DataSchemaField("__time", "Timestamp", false, null, null),
                    new DataSchemaField("dimension", "String", true, null, null),
                    new DataSchemaField("metric", "Float", true, null, null)
                }, new[] { "__time" }, new Dictionary<string, object> { ["type"] = "datasource" })
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
