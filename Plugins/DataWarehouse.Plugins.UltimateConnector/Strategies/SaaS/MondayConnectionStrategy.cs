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
    public class MondayConnectionStrategy : SaaSConnectionStrategyBase
    {
        public override string StrategyId => "monday";
        public override string DisplayName => "Monday.com";
        public override ConnectorCategory Category => ConnectorCategory.SaaS;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Monday.com work management platform using HTTPS GraphQL API.";
        public override string[] Tags => new[] { "monday", "work-management", "collaboration", "saas", "graphql" };

        private volatile string _apiToken = "";

        public MondayConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var token = GetConfiguration<string>(config, "ApiToken", string.Empty)
                ?? config.AuthCredential
                ?? string.Empty;
            if (string.IsNullOrWhiteSpace(token))
                throw new ArgumentException("Required configuration key 'ApiToken' (or AuthCredential) is missing. Provide a Monday.com API token.", nameof(config));

            _apiToken = token;

            var httpClient = new HttpClient { BaseAddress = new Uri("https://api.monday.com"), Timeout = config.Timeout };
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["Endpoint"] = "https://api.monday.com" });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { try { var response = await handle.GetConnection<HttpClient>().PostAsync("/v2", new StringContent("{\"query\":\"{ me { id } }\"}", System.Text.Encoding.UTF8, "application/json"), ct); return response.IsSuccessStatusCode; } catch { return false; } }
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>()?.Dispose(); return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "Monday.com is reachable" : "Monday.com is not responding", sw.Elapsed, DateTimeOffset.UtcNow); }

        protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            var token = _apiToken;
            if (!string.IsNullOrEmpty(token))
                return Task.FromResult((token, DateTimeOffset.UtcNow.AddDays(365)));
            return Task.FromResult((string.Empty, DateTimeOffset.UtcNow));
        }

        protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default) => AuthenticateAsync(handle, ct);
    }
}
