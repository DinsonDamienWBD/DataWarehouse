using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.SaaS
{
    public class NotionConnectionStrategy : SaaSConnectionStrategyBase
    {
        public override string StrategyId => "notion";
        public override string DisplayName => "Notion";
        public override ConnectorCategory Category => ConnectorCategory.SaaS;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Notion workspace platform using HTTPS REST API.";
        public override string[] Tags => new[] { "notion", "workspace", "collaboration", "saas", "rest-api" };

        private volatile string _integrationToken = "";

        public NotionConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var token = GetConfiguration<string>(config, "IntegrationToken", string.Empty)
                ?? config.AuthCredential
                ?? string.Empty;
            if (string.IsNullOrWhiteSpace(token))
                throw new ArgumentException("Required configuration key 'IntegrationToken' (or AuthCredential) is missing. Provide a Notion Internal Integration Token (secret_...).", nameof(config));

            _integrationToken = token;

            var httpClient = new HttpClient { BaseAddress = new Uri("https://api.notion.com"), Timeout = config.Timeout };
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            httpClient.DefaultRequestHeaders.Add("Notion-Version", "2022-06-28");
            return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["Endpoint"] = "https://api.notion.com" });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { try { var response = await handle.GetConnection<HttpClient>().GetAsync("/v1/users/me", ct); return response.IsSuccessStatusCode; } catch { return false; } }
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>()?.Dispose(); return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "Notion is reachable" : "Notion is not responding", sw.Elapsed, DateTimeOffset.UtcNow); }

        protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            // Notion integration tokens do not expire.
            var token = _integrationToken;
            if (!string.IsNullOrEmpty(token))
                return Task.FromResult((token, DateTimeOffset.UtcNow.AddDays(365)));
            return Task.FromResult((string.Empty, DateTimeOffset.UtcNow));
        }

        protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default) => AuthenticateAsync(handle, ct);
    }
}
