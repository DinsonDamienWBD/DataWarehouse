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
    public class DocuSignConnectionStrategy : SaaSConnectionStrategyBase
    {
        public override string StrategyId => "docusign";
        public override string DisplayName => "DocuSign";
        public override ConnectorCategory Category => ConnectorCategory.SaaS;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to DocuSign electronic signature platform using HTTPS REST API with OAuth2.";
        public override string[] Tags => new[] { "docusign", "esignature", "documents", "saas", "rest-api" };

        private volatile string _accountId = "";
        private volatile string _clientId = "";
        private volatile string _clientSecret = "";

        public DocuSignConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var account = GetConfiguration<string>(config, "AccountId", string.Empty);
            if (string.IsNullOrWhiteSpace(account))
                throw new ArgumentException("Required configuration key 'AccountId' is missing or empty. Set it in ConnectionConfig.Options.", nameof(config));

            var clientId = GetConfiguration<string>(config, "ClientId", string.Empty)
                ?? config.AuthSecondary
                ?? string.Empty;
            if (string.IsNullOrWhiteSpace(clientId))
                throw new ArgumentException("Required configuration key 'ClientId' (or AuthSecondary) is missing. Provide the DocuSign integration key.", nameof(config));

            var clientSecret = GetConfiguration<string>(config, "ClientSecret", string.Empty)
                ?? config.AuthCredential
                ?? string.Empty;
            if (string.IsNullOrWhiteSpace(clientSecret))
                throw new ArgumentException("Required configuration key 'ClientSecret' (or AuthCredential) is missing.", nameof(config));

            _accountId = account;
            _clientId = clientId;
            _clientSecret = clientSecret;

            var bearerToken = await AcquireTokenAsync(clientId, clientSecret, ct).ConfigureAwait(false);

            var endpoint = $"https://{account}.docusign.net";
            var httpClient = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = config.Timeout };
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["AccountId"] = account, ["Endpoint"] = endpoint });
        }

        private static async Task<string> AcquireTokenAsync(string clientId, string clientSecret, CancellationToken ct)
        {
            using var tokenClient = new HttpClient();
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));

            var request = new HttpRequestMessage(HttpMethod.Post, "https://account.docusign.com/oauth/token");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            request.Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("scope", "signature"),
            });

            var response = await tokenClient.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new InvalidOperationException($"Failed to acquire DocuSign OAuth2 token (HTTP {(int)response.StatusCode}): {err}");
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("access_token").GetString()
                ?? throw new InvalidOperationException("DocuSign token response did not contain access_token.");
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { try { var response = await handle.GetConnection<HttpClient>().GetAsync("/restapi/v2.1/accounts", ct); return response.IsSuccessStatusCode; } catch { return false; } }
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>()?.Dispose(); return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "DocuSign is reachable" : "DocuSign is not responding", sw.Elapsed, DateTimeOffset.UtcNow); }

        protected override async Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            if (!string.IsNullOrEmpty(_clientId) && !string.IsNullOrEmpty(_clientSecret))
            {
                var token = await AcquireTokenAsync(_clientId, _clientSecret, ct).ConfigureAwait(false);
                return (token, DateTimeOffset.UtcNow.AddHours(8));
            }
            return (string.Empty, DateTimeOffset.UtcNow);
        }

        protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default) => AuthenticateAsync(handle, ct);
    }
}
