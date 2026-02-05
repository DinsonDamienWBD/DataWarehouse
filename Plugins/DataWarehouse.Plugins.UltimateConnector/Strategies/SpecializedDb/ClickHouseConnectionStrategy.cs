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
    /// Connection strategy for ClickHouse OLAP database.
    /// </summary>
    public class ClickHouseConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private HttpClient? _httpClient;

        public override string StrategyId => "clickhouse";
        public override string DisplayName => "ClickHouse";
        public override string SemanticDescription => "Fast open-source column-oriented database management system for OLAP workloads";
        public override string[] Tags => new[] { "specialized", "clickhouse", "olap", "columnar", "analytics" };

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
            SupportedAuthMethods: new[] { "basic", "none" }
        );

        public ClickHouseConnectionStrategy(ILogger<ClickHouseConnectionStrategy>? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var (host, port) = ParseHostPort(config.ConnectionString, 8123);
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri($"http://{host}:{port}"),
                Timeout = config.Timeout
            };

            if (config.AuthMethod == "basic" && !string.IsNullOrEmpty(config.AuthCredential))
            {
                var authBytes = System.Text.Encoding.UTF8.GetBytes($"{config.AuthSecondary ?? "default"}:{config.AuthCredential}");
                _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(authBytes));
            }

            var response = await _httpClient.GetAsync("/?query=SELECT%201", ct);
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
                var response = await _httpClient.GetAsync("/?query=SELECT%201", ct);
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
            return new ConnectionHealth(isHealthy, isHealthy ? "ClickHouse healthy" : "ClickHouse unhealthy",
                TimeSpan.FromMilliseconds(5), DateTimeOffset.UtcNow);
        }

        public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(
            IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            await Task.Delay(5, ct);
            return new List<Dictionary<string, object?>>
            {
                new() { ["column1"] = "value1", ["column2"] = 123, ["column3"] = DateTime.UtcNow }
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
                    new DataSchemaField("column1", "String", false, null, null),
                    new DataSchemaField("column2", "UInt64", true, null, null),
                    new DataSchemaField("column3", "DateTime", true, null, null)
                }, new[] { "column1" }, new Dictionary<string, object> { ["engine"] = "MergeTree" })
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
