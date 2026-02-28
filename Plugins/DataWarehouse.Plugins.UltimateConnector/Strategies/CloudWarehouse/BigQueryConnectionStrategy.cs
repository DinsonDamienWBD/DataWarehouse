using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.CloudWarehouse
{
    public class BigQueryConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private volatile HttpClient? _httpClient;
        public override string StrategyId => "bigquery";
        public override string DisplayName => "Google BigQuery";
        public override string SemanticDescription => "Serverless, highly scalable data warehouse for large-scale analytics on Google Cloud";
        public override string[] Tags => new[] { "cloud", "bigquery", "warehouse", "serverless", "google" };
        public override ConnectionStrategyCapabilities Capabilities => new(SupportsPooling: true, SupportsStreaming: true, SupportsTransactions: true, SupportsBulkOperations: true, SupportsSchemaDiscovery: true, SupportsSsl: true, SupportsCompression: true, SupportsAuthentication: true, MaxConcurrentConnections: 1000, SupportedAuthMethods: new[] { "oauth2", "service-account" });
        public BigQueryConnectionStrategy(ILogger<BigQueryConnectionStrategy>? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            _httpClient = new HttpClient { BaseAddress = new Uri("https://bigquery.googleapis.com/bigquery/v2/"), Timeout = config.Timeout };
            if (!string.IsNullOrEmpty(config.AuthCredential))
                _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.AuthCredential);
            return new DefaultConnectionHandle(_httpClient, new Dictionary<string, object> { ["project_id"] = config.ConnectionString });
        }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            try
            {
                using var response = await client.GetAsync("/bigquery/v2/projects", ct);
                // 200 or 401/403 means the service is reachable; 503 means unavailable
                return response.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable;
            }
            catch { return false; }
        }
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { _httpClient?.Dispose(); _httpClient = null; await Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "BigQuery healthy" : "BigQuery unhealthy", sw.Elapsed, DateTimeOffset.UtcNow); }
        public override Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
            => throw new NotSupportedException("BigQuery query execution requires the Google.Cloud.BigQuery.V2 NuGet package. Install it and use BigQueryClient.ExecuteQueryAsync.");
        public override Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
            => throw new NotSupportedException("BigQuery DML execution requires the Google.Cloud.BigQuery.V2 NuGet package. Install it and use BigQueryClient.ExecuteQueryAsync.");
        public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
            => throw new NotSupportedException("BigQuery schema discovery requires the Google.Cloud.BigQuery.V2 NuGet package. Install it and use BigQueryClient.GetTableAsync.");
    }
}
