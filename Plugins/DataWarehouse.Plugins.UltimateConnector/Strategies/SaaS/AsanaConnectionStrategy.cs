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
    public class AsanaConnectionStrategy : SaaSConnectionStrategyBase
    {
        public override string StrategyId => "asana";
        public override string DisplayName => "Asana";
        public override ConnectorCategory Category => ConnectorCategory.SaaS;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Asana project management platform using HTTPS REST API.";
        public override string[] Tags => new[] { "asana", "project-management", "collaboration", "saas", "rest-api" };

        private volatile string _accessToken = "";

        public AsanaConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var token = GetConfiguration<string>(config, "AccessToken", string.Empty)
                ?? config.AuthCredential
                ?? string.Empty;
            if (string.IsNullOrWhiteSpace(token))
                throw new ArgumentException("Required configuration key 'AccessToken' (or AuthCredential) is missing. Provide a Personal Access Token from Asana.", nameof(config));

            _accessToken = token;

            var httpClient = new HttpClient { BaseAddress = new Uri("https://app.asana.com"), Timeout = config.Timeout };
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["Endpoint"] = "https://app.asana.com" });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { try { var response = await handle.GetConnection<HttpClient>().GetAsync("/api/1.0/users/me", ct); return response.IsSuccessStatusCode; } catch { return false; } }
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>()?.Dispose(); return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "Asana is reachable" : "Asana is not responding", sw.Elapsed, DateTimeOffset.UtcNow); }

        protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            // Asana PATs do not expire; return the stored token.
            var token = _accessToken;
            if (!string.IsNullOrEmpty(token))
                return Task.FromResult((token, DateTimeOffset.UtcNow.AddDays(365)));
            return Task.FromResult((string.Empty, DateTimeOffset.UtcNow));
        }

        protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default) => AuthenticateAsync(handle, ct);
    }
}
