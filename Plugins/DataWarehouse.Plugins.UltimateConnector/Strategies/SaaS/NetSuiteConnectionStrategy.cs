using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.SaaS
{
    public class NetSuiteConnectionStrategy : SaaSConnectionStrategyBase
    {
        public override string StrategyId => "netsuite";
        public override string DisplayName => "NetSuite";
        public override ConnectorCategory Category => ConnectorCategory.SaaS;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Oracle NetSuite ERP using HTTPS REST API (SuiteTalk) with OAuth2 client credentials.";
        public override string[] Tags => new[] { "netsuite", "erp", "accounting", "saas", "rest-api" };

        private volatile string _accountId = "";
        private volatile string _clientId = "";
        private volatile string _clientSecret = "";

        public NetSuiteConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var accountId = GetConfiguration<string>(config, "AccountId", string.Empty);
            if (string.IsNullOrWhiteSpace(accountId))
                throw new ArgumentException("Required configuration key 'AccountId' is missing or empty.", nameof(config));

            var clientId = GetConfiguration<string>(config, "ClientId", string.Empty);
            if (string.IsNullOrWhiteSpace(clientId))
                throw new ArgumentException("Required configuration key 'ClientId' is missing. Provide the NetSuite integration client ID.", nameof(config));

            var clientSecret = GetConfiguration<string>(config, "ClientSecret", string.Empty)
                ?? config.AuthCredential
                ?? string.Empty;
            if (string.IsNullOrWhiteSpace(clientSecret))
                throw new ArgumentException("Required configuration key 'ClientSecret' (or AuthCredential) is missing.", nameof(config));

            _accountId = accountId;
            _clientId = clientId;
            _clientSecret = clientSecret;

            var bearerToken = await AcquireTokenAsync(accountId, clientId, clientSecret, ct).ConfigureAwait(false);

            var endpoint = $"https://{accountId}.suitetalk.api.netsuite.com";
            var httpClient = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = config.Timeout };
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["AccountId"] = accountId, ["Endpoint"] = endpoint });
        }

        private static async Task<string> AcquireTokenAsync(string accountId, string clientId, string clientSecret, CancellationToken ct)
        {
            using var tokenClient = new HttpClient();
            // NetSuite OAuth2 token endpoint uses account-specific subdomain
            var tokenUrl = $"https://{accountId}.suitetalk.api.netsuite.com/services/rest/auth/oauth2/v1/token";
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));

            var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            request.Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("client_assertion_type", "urn:ietf:params:oauth:client-assertion-type:jwt-bearer"),
            });

            var response = await tokenClient.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new InvalidOperationException($"Failed to acquire NetSuite OAuth2 token (HTTP {(int)response.StatusCode}): {err}");
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("access_token").GetString()
                ?? throw new InvalidOperationException("NetSuite token response did not contain access_token.");
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { try { var response = await handle.GetConnection<HttpClient>().GetAsync("/services/rest/record/v1/metadata-catalog", ct); return response.IsSuccessStatusCode; } catch { return false; } }
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>()?.Dispose(); await Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "NetSuite is reachable" : "NetSuite is not responding", sw.Elapsed, DateTimeOffset.UtcNow); }

        protected override async Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            if (!string.IsNullOrEmpty(_accountId) && !string.IsNullOrEmpty(_clientId) && !string.IsNullOrEmpty(_clientSecret))
            {
                var token = await AcquireTokenAsync(_accountId, _clientId, _clientSecret, ct).ConfigureAwait(false);
                return (token, DateTimeOffset.UtcNow.AddHours(1));
            }
            return (string.Empty, DateTimeOffset.UtcNow);
        }

        protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default) => AuthenticateAsync(handle, ct);
    }
}
