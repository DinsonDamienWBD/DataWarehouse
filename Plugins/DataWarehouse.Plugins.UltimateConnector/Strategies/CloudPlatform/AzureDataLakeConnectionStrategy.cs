using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.CloudPlatform
{
    public class AzureDataLakeConnectionStrategy : SaaSConnectionStrategyBase
    {
        public override string StrategyId => "azure-datalake";
        public override string DisplayName => "Azure Data Lake";
        public override ConnectorCategory Category => ConnectorCategory.SaaS;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Azure Data Lake Storage using HTTPS REST API for big data analytics.";
        public override string[] Tags => new[] { "azure", "datalake", "analytics", "storage", "rest-api" };
        public AzureDataLakeConnectionStrategy(ILogger? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var account = GetConfiguration<string>(config, "Account", string.Empty);
            var endpoint = $"https://{account}.dfs.core.windows.net";
            var httpClient = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = config.Timeout };
            return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["Account"] = account, ["Endpoint"] = endpoint });
        }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { try { var response = await handle.GetConnection<HttpClient>().GetAsync("/?resource=account", ct); return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Forbidden; } catch { return false; } }
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>()?.Dispose(); return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "Azure Data Lake is reachable" : "Azure Data Lake is not responding", sw.Elapsed, DateTimeOffset.UtcNow); }
        protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default) => Task.FromResult((Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddHours(1)));
        protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default) => AuthenticateAsync(handle, ct);
    }
}
