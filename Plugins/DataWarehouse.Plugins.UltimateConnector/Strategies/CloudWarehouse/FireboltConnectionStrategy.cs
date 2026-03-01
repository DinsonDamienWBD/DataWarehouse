using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.CloudWarehouse
{
    public class FireboltConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private volatile HttpClient? _httpClient;
        public override string StrategyId => "firebolt";
        public override string DisplayName => "Firebolt";
        public override string SemanticDescription => "Cloud data warehouse designed for sub-second analytics on massive datasets";
        public override string[] Tags => new[] { "cloud", "firebolt", "warehouse", "high-performance", "analytics" };
        public override ConnectionStrategyCapabilities Capabilities => new(SupportsPooling: true, SupportsStreaming: true, SupportsTransactions: false, SupportsBulkOperations: true, SupportsSchemaDiscovery: true, SupportsSsl: true, SupportsCompression: true, SupportsAuthentication: true, MaxConcurrentConnections: 300, SupportedAuthMethods: new[] { "bearer" });
        public FireboltConnectionStrategy(ILogger<FireboltConnectionStrategy>? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var endpoint = config.ConnectionString.StartsWith("http") ? config.ConnectionString : $"https://{config.ConnectionString}";
            _httpClient = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = config.Timeout };
            if (!string.IsNullOrEmpty(config.AuthCredential))
                _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.AuthCredential);
            return new DefaultConnectionHandle(_httpClient, new Dictionary<string, object> { ["account"] = config.ConnectionString });
        }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            try
            {
                using var response = await client.GetAsync("/", ct);
                return response.IsSuccessStatusCode;
            }
            catch { return false; }
        }
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { _httpClient?.Dispose(); _httpClient = null; return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "Firebolt healthy" : "Firebolt unhealthy", sw.Elapsed, DateTimeOffset.UtcNow); }
        public override Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
            => throw new NotSupportedException("Firebolt query execution requires the Firebolt .NET SDK or the Firebolt REST API with a valid service account token. See https://docs.firebolt.io.");
        public override Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
            => throw new NotSupportedException("Firebolt DML execution requires the Firebolt .NET SDK or the Firebolt REST API with a valid service account token. See https://docs.firebolt.io.");
        public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
            => throw new NotSupportedException("Firebolt schema discovery requires the Firebolt .NET SDK or the Firebolt REST API with a valid service account token. See https://docs.firebolt.io.");
    }
}
