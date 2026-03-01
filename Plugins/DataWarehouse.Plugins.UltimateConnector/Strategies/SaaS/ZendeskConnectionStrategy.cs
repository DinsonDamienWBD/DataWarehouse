using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.SaaS
{
    public class ZendeskConnectionStrategy : SaaSConnectionStrategyBase
    {
        private volatile string _email = "";
        private volatile string _apiToken = "";

        public override string StrategyId => "zendesk";
        public override string DisplayName => "Zendesk";
        public override ConnectorCategory Category => ConnectorCategory.SaaS;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Zendesk customer service platform using HTTPS REST API.";
        public override string[] Tags => new[] { "zendesk", "support", "ticketing", "saas", "rest-api" };
        public ZendeskConnectionStrategy(ILogger? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var subdomain = GetConfiguration<string>(config, "Subdomain", "");
            if (string.IsNullOrEmpty(subdomain))
                throw new ArgumentException("Required configuration key 'Subdomain' is missing or empty.", nameof(config));
            _email = GetConfiguration<string>(config, "Email", "");
            _apiToken = GetConfiguration<string>(config, "ApiToken", "");
            var endpoint = $"https://{subdomain}.zendesk.com";
            var httpClient = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = config.Timeout };
            // Zendesk API token auth: {email}/token:{apiToken} Base64-encoded
            if (!string.IsNullOrEmpty(_email) && !string.IsNullOrEmpty(_apiToken))
            {
                var creds = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{_email}/token:{_apiToken}"));
                httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", creds);
            }
            return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["Subdomain"] = subdomain, ["Endpoint"] = endpoint });
        }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { try { var response = await handle.GetConnection<HttpClient>().GetAsync("/api/v2/users/me.json", ct); return response.IsSuccessStatusCode; } catch { return false; } }
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>()?.Dispose(); return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "Zendesk is reachable" : "Zendesk is not responding", sw.Elapsed, DateTimeOffset.UtcNow); }
        protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            // Zendesk API token auth: {email}/token:{apiToken} Base64-encoded.
            if (!string.IsNullOrEmpty(_email) && !string.IsNullOrEmpty(_apiToken))
            {
                var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{_email}/token:{_apiToken}"));
                return Task.FromResult((encoded, DateTimeOffset.UtcNow.AddDays(365)));
            }
            return Task.FromResult((string.Empty, DateTimeOffset.UtcNow));
        }
        protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default) => AuthenticateAsync(handle, ct);
    }
}
