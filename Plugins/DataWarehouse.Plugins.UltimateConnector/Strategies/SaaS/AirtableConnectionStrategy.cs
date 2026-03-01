using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.SaaS
{
    public class AirtableConnectionStrategy : SaaSConnectionStrategyBase
    {
        public override string StrategyId => "airtable";
        public override string DisplayName => "Airtable";
        public override ConnectorCategory Category => ConnectorCategory.SaaS;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Airtable cloud database using HTTPS REST API.";
        public override string[] Tags => new[] { "airtable", "database", "collaboration", "saas", "rest-api" };
        public AirtableConnectionStrategy(ILogger? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var apiKey = GetConfiguration<string?>(config, "ApiKey", null)
                ?? config.AuthCredential;
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException(
                    "Airtable requires a Personal Access Token. Supply it via config.Properties[\"ApiKey\"] or config.AuthCredential.");

            var httpClient = new HttpClient { BaseAddress = new Uri("https://api.airtable.com"), Timeout = config.Timeout };
            httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["Endpoint"] = "https://api.airtable.com" });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            try
            {
                var response = await handle.GetConnection<HttpClient>().GetAsync("/v0/meta/bases", ct);
                // 200 = success, 401/403 = auth failure (report unhealthy so the caller knows creds are wrong)
                return response.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>()?.Dispose(); return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "Airtable is reachable" : "Airtable is not responding", sw.Elapsed, DateTimeOffset.UtcNow); }

        protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default)
            => throw new NotSupportedException(
                "Airtable uses a Personal Access Token (PAT), not a rotating OAuth2 token. " +
                "Supply the PAT via config.Properties[\"ApiKey\"] at connection time.");
        protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default)
            => AuthenticateAsync(handle, ct);
    }
}
