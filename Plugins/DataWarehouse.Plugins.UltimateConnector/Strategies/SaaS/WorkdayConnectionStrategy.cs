using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.SaaS
{
    public class WorkdayConnectionStrategy : SaaSConnectionStrategyBase
    {
        private volatile string _clientId = "";
        private volatile string _clientSecret = "";
        private volatile string _tenant = "";

        public override string StrategyId => "workday";
        public override string DisplayName => "Workday";
        public override ConnectorCategory Category => ConnectorCategory.SaaS;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Workday HCM and financial management using HTTPS REST API.";
        public override string[] Tags => new[] { "workday", "hcm", "hr", "finance", "rest-api" };
        public WorkdayConnectionStrategy(ILogger? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            _tenant = GetConfiguration<string>(config, "Tenant", string.Empty);
            if (string.IsNullOrWhiteSpace(_tenant))
                throw new ArgumentException("Required configuration key 'Tenant' is missing or empty.", nameof(config));
            _clientId = GetConfiguration<string>(config, "ClientId", string.Empty);
            _clientSecret = GetConfiguration<string>(config, "ClientSecret", string.Empty);
            var endpoint = $"https://wd2-impl-services1.workday.com/ccx/service/{_tenant}";
            var httpClient = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = config.Timeout };
            // If client credentials provided, obtain OAuth2 Bearer token
            if (!string.IsNullOrEmpty(_clientId) && !string.IsNullOrEmpty(_clientSecret))
            {
                var tokenEndpoint = $"https://wd2-impl-services1.workday.com/ccx/oauth2/{_tenant}/token";
                var tokenClient = new HttpClient();
                var form = new System.Collections.Generic.Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = _clientId,
                    ["client_secret"] = _clientSecret
                };
                using var tokenResponse = await tokenClient.PostAsync(tokenEndpoint, new System.Net.Http.FormUrlEncodedContent(form), ct);
                if (tokenResponse.IsSuccessStatusCode)
                {
                    var tokenJson = await tokenResponse.Content.ReadAsStringAsync(ct);
                    using var doc = System.Text.Json.JsonDocument.Parse(tokenJson);
                    var accessToken = doc.RootElement.TryGetProperty("access_token", out var at) ? at.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(accessToken))
                        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                }
                tokenClient.Dispose();
            }
            return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["Tenant"] = _tenant, ["Endpoint"] = endpoint });
        }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { try { var response = await handle.GetConnection<HttpClient>().GetAsync("/", ct); return response.IsSuccessStatusCode; } catch { return false; } }
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>()?.Dispose(); return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "Workday is reachable" : "Workday is not responding", sw.Elapsed, DateTimeOffset.UtcNow); }
        protected override async Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            // Workday uses OAuth2 client_credentials flow. Obtain fresh token.
            if (!string.IsNullOrEmpty(_clientId) && !string.IsNullOrEmpty(_clientSecret))
            {
                var tokenEndpoint = $"https://wd2-impl-services1.workday.com/ccx/oauth2/{_tenant}/token";
                using var tokenClient = new System.Net.Http.HttpClient();
                var form = new System.Collections.Generic.Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = _clientId,
                    ["client_secret"] = _clientSecret
                };
                using var tokenResponse = await tokenClient.PostAsync(tokenEndpoint, new System.Net.Http.FormUrlEncodedContent(form), ct);
                if (tokenResponse.IsSuccessStatusCode)
                {
                    var tokenJson = await tokenResponse.Content.ReadAsStringAsync(ct);
                    using var doc = System.Text.Json.JsonDocument.Parse(tokenJson);
                    var accessToken = doc.RootElement.TryGetProperty("access_token", out var at) ? at.GetString() ?? "" : "";
                    var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600;
                    return (accessToken, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
                }
            }
            return (string.Empty, DateTimeOffset.UtcNow);
        }
        protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default) => AuthenticateAsync(handle, ct);
    }
}
