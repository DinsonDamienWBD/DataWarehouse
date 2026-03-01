using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.SaaS
{
    public class ZuoraConnectionStrategy : SaaSConnectionStrategyBase
    {
        private volatile string _clientId = "";
        private volatile string _clientSecret = "";

        public override string StrategyId => "zuora";
        public override string DisplayName => "Zuora";
        public override ConnectorCategory Category => ConnectorCategory.SaaS;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Zuora subscription billing platform using HTTPS REST API.";
        public override string[] Tags => new[] { "zuora", "billing", "subscription", "saas", "rest-api" };
        public ZuoraConnectionStrategy(ILogger? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            _clientId = GetConfiguration<string>(config, "ClientId", string.Empty);
            _clientSecret = GetConfiguration<string>(config, "ClientSecret", string.Empty);
            var httpClient = new HttpClient { BaseAddress = new Uri("https://rest.zuora.com"), Timeout = config.Timeout };
            // Zuora uses OAuth2 client_credentials. Obtain token at connect time.
            if (!string.IsNullOrEmpty(_clientId) && !string.IsNullOrEmpty(_clientSecret))
            {
                var form = new System.Collections.Generic.Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = _clientId,
                    ["client_secret"] = _clientSecret
                };
                using var tokenResponse = await httpClient.PostAsync("/oauth/token", new System.Net.Http.FormUrlEncodedContent(form), ct);
                if (tokenResponse.IsSuccessStatusCode)
                {
                    var tokenJson = await tokenResponse.Content.ReadAsStringAsync(ct);
                    using var doc = System.Text.Json.JsonDocument.Parse(tokenJson);
                    var accessToken = doc.RootElement.TryGetProperty("access_token", out var at) ? at.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(accessToken))
                        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                }
            }
            return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["Endpoint"] = "https://rest.zuora.com" });
        }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { try { var response = await handle.GetConnection<HttpClient>().GetAsync("/v1/catalog/products", ct); return response.IsSuccessStatusCode; } catch { return false; } }
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>()?.Dispose(); return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "Zuora is reachable" : "Zuora is not responding", sw.Elapsed, DateTimeOffset.UtcNow); }
        protected override async Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            // Zuora OAuth2 client_credentials — obtain fresh token from /oauth/token endpoint.
            if (!string.IsNullOrEmpty(_clientId) && !string.IsNullOrEmpty(_clientSecret))
            {
                using var tokenClient = new System.Net.Http.HttpClient { BaseAddress = new Uri("https://rest.zuora.com") };
                var form = new System.Collections.Generic.Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = _clientId,
                    ["client_secret"] = _clientSecret
                };
                using var tokenResponse = await tokenClient.PostAsync("/oauth/token", new System.Net.Http.FormUrlEncodedContent(form), ct);
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
