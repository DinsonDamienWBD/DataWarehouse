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
            var useSsl = GetConfiguration<bool>(config, "UseSsl", false);
            var scheme = useSsl ? "https" : "http";
            var (host, port) = ParseHostPort(config.ConnectionString, useSsl ? 8443 : 8082);
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri($"{scheme}://{host}:{port}"),
                Timeout = config.Timeout
            };

            if (!string.IsNullOrEmpty(config.AuthCredential))
            {
                var creds = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{config.AuthSecondary ?? ""}:{config.AuthCredential}"));
                _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", creds);
            }

            using var response = await _httpClient.GetAsync("/status", ct);
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
                using var response = await _httpClient.GetAsync("/status", ct);
                return response.IsSuccessStatusCode;
            }
            catch { return false; /* Connection validation - failure acceptable */ }
        }

        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) {
            _httpClient?.Dispose();
            _httpClient = null; return Task.CompletedTask; }

        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            // P2-2180: Measure actual latency with Stopwatch instead of hardcoded value.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();
            return new ConnectionHealth(isHealthy, isHealthy ? "Druid healthy" : "Druid unhealthy",
                sw.Elapsed, DateTimeOffset.UtcNow);
        }

        public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(
            IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            if (_httpClient == null) return new List<Dictionary<string, object?>>();
            try
            {
                var requestBody = System.Text.Json.JsonSerializer.Serialize(new { query });
                var content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync("/druid/v2/sql", content, ct);
                if (!response.IsSuccessStatusCode)
                    return new List<Dictionary<string, object?>>();
                var json = await response.Content.ReadAsStringAsync(ct);
                var results = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(json);
                return results ?? new List<Dictionary<string, object?>>();
            }
            catch
            {
                return new List<Dictionary<string, object?>>();
            }
        }

        public override Task<int> ExecuteNonQueryAsync(
            IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            // Finding 2181: Apache Druid SQL (/druid/v2/sql) does not support DML (INSERT/UPDATE/DELETE).
            // Returning a fabricated row count silently discards data-mutation requests. Throw instead so
            // callers are informed that Druid is query-only and must use the Druid native ingestion API
            // (e.g. Kafka supervisor or native batch task) for data ingestion.
            throw new NotSupportedException(
                "Apache Druid SQL does not support DML (INSERT/UPDATE/DELETE). " +
                "Use the Druid native batch ingestion API or Kafka supervisor for data loading.");
        }

        public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            if (_httpClient == null) return new List<DataSchema>();
            try
            {
                using var response = await _httpClient.GetAsync("/druid/v2/datasources", ct);
                if (!response.IsSuccessStatusCode)
                    return new List<DataSchema>();
                var json = await response.Content.ReadAsStringAsync(ct);
                var datasources = System.Text.Json.JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
                var schemas = new List<DataSchema>();
                foreach (var ds in datasources)
                {
                    schemas.Add(new DataSchema(ds, new[]
                    {
                        new DataSchemaField("__time", "Timestamp", false, null, null)
                    }, new[] { "__time" }, new Dictionary<string, object> { ["type"] = "datasource" }));
                }
                return schemas;
            }
            catch
            {
                return new List<DataSchema>();
            }
        }

        private (string host, int port) ParseHostPort(string connectionString, int defaultPort)
        {
            var clean = connectionString.Replace("https://", "").Replace("http://", "").Split('/')[0];
            var parts = clean.Split(':');
            return (parts[0], parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : defaultPort);
        }
    }
}
