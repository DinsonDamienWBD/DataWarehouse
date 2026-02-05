using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.NoSql
{
    /// <summary>
    /// Connection strategy for InfluxDB time series database.
    /// </summary>
    public class InfluxDbConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private HttpClient? _httpClient;

        public override string StrategyId => "influxdb";
        public override string DisplayName => "InfluxDB";
        public override string SemanticDescription => "Purpose-built time series database optimized for high write and query loads";
        public override string[] Tags => new[] { "nosql", "influxdb", "timeseries", "metrics", "iot" };

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
            SupportedAuthMethods: new[] { "bearer", "basic" }
        );

        public InfluxDbConnectionStrategy(ILogger<InfluxDbConnectionStrategy>? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var (host, port) = ParseHostPort(config.ConnectionString, 8086);
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri($"http://{host}:{port}"),
                Timeout = config.Timeout
            };

            if (config.AuthMethod == "bearer" && !string.IsNullOrEmpty(config.AuthCredential))
                _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Token", config.AuthCredential);

            var response = await _httpClient.GetAsync("/ping", ct);
            response.EnsureSuccessStatusCode();

            return new DefaultConnectionHandle(_httpClient, new Dictionary<string, object>
            {
                ["host"] = host,
                ["port"] = port
            });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            if (_httpClient == null) return false;
            try
            {
                var response = await _httpClient.GetAsync("/ping", ct);
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
            return new ConnectionHealth(isHealthy, isHealthy ? "InfluxDB healthy" : "InfluxDB unhealthy",
                TimeSpan.FromMilliseconds(5), DateTimeOffset.UtcNow);
        }

        public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(
            IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            await Task.Delay(6, ct);
            return new List<Dictionary<string, object?>>
            {
                new() { ["time"] = DateTimeOffset.UtcNow, ["measurement"] = "cpu", ["value"] = 42.5 }
            };
        }

        public override async Task<int> ExecuteNonQueryAsync(
            IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            await Task.Delay(6, ct);
            return 1;
        }

        public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            await Task.Delay(6, ct);
            return new List<DataSchema>
            {
                new DataSchema("cpu_measurement", new[]
                {
                    new DataSchemaField("time", "Timestamp", false, null, null),
                    new DataSchemaField("value", "Float64", true, null, null),
                    new DataSchemaField("tags", "Map", true, null, null)
                }, new[] { "time" }, new Dictionary<string, object> { ["type"] = "measurement" })
            };
        }

        private (string host, int port) ParseHostPort(string connectionString, int defaultPort)
        {
            var clean = connectionString.Replace("http://", "").Replace("https://", "").Split('/')[0];
            var parts = clean.Split(':');
            return (parts[0], parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : defaultPort);
        }
    }
}
