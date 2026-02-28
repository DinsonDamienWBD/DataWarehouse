using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.SaaS
{
    public class MicrosoftDynamicsConnectionStrategy : SaaSConnectionStrategyBase
    {
        public override string StrategyId => "dynamics365";
        public override string DisplayName => "Microsoft Dynamics 365";
        public override ConnectorCategory Category => ConnectorCategory.SaaS;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Microsoft Dynamics 365 CRM/ERP using HTTPS REST API with Azure AD OAuth2.";
        public override string[] Tags => new[] { "microsoft", "dynamics", "crm", "erp", "rest-api" };

        private volatile string _clientId = "";
        private volatile string _clientSecret = "";
        private volatile string _tenantId = "";
        private volatile string _endpoint = "";

        public MicrosoftDynamicsConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var org = GetConfiguration<string>(config, "Organization", string.Empty);
            if (string.IsNullOrWhiteSpace(org))
                throw new ArgumentException("Required configuration key 'Organization' is missing or empty.", nameof(config));

            var region = GetConfiguration(config, "Region", "crm");
            var tenantId = GetConfiguration<string>(config, "TenantId", string.Empty);
            if (string.IsNullOrWhiteSpace(tenantId))
                throw new ArgumentException("Required configuration key 'TenantId' is missing. Provide the Azure AD tenant ID.", nameof(config));

            var clientId = GetConfiguration<string>(config, "ClientId", string.Empty);
            if (string.IsNullOrWhiteSpace(clientId))
                throw new ArgumentException("Required configuration key 'ClientId' is missing. Provide the Azure AD application (client) ID.", nameof(config));

            var clientSecret = GetConfiguration<string>(config, "ClientSecret", string.Empty)
                ?? config.AuthCredential
                ?? string.Empty;
            if (string.IsNullOrWhiteSpace(clientSecret))
                throw new ArgumentException("Required configuration key 'ClientSecret' (or AuthCredential) is missing.", nameof(config));

            _clientId = clientId;
            _clientSecret = clientSecret;
            _tenantId = tenantId;
            _endpoint = $"https://{org}.{region}.dynamics.com";

            // Acquire OAuth2 token via Azure AD client_credentials flow
            var bearerToken = await AcquireTokenAsync(tenantId, clientId, clientSecret, _endpoint, ct).ConfigureAwait(false);

            var httpClient = new HttpClient { BaseAddress = new Uri(_endpoint), Timeout = config.Timeout };
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["Organization"] = org, ["Endpoint"] = _endpoint });
        }

        private static async Task<string> AcquireTokenAsync(string tenantId, string clientId, string clientSecret, string resource, CancellationToken ct)
        {
            using var tokenClient = new HttpClient();
            var tokenUrl = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";
            var body = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("client_secret", clientSecret),
                new KeyValuePair<string, string>("scope", $"{resource}/.default"),
            });

            var response = await tokenClient.PostAsync(tokenUrl, body, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new InvalidOperationException($"Failed to acquire Azure AD token for Dynamics 365 (HTTP {(int)response.StatusCode}): {err}");
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("access_token").GetString()
                ?? throw new InvalidOperationException("Azure AD token response did not contain access_token.");
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { try { var response = await handle.GetConnection<HttpClient>().GetAsync("/api/data/v9.2/", ct); return response.IsSuccessStatusCode; } catch { return false; } }
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>()?.Dispose(); await Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "Dynamics 365 is reachable" : "Dynamics 365 is not responding", sw.Elapsed, DateTimeOffset.UtcNow); }

        protected override async Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            if (!string.IsNullOrEmpty(_tenantId) && !string.IsNullOrEmpty(_clientId) && !string.IsNullOrEmpty(_clientSecret))
            {
                var token = await AcquireTokenAsync(_tenantId, _clientId, _clientSecret, _endpoint, ct).ConfigureAwait(false);
                // Azure AD access tokens expire in ~1 hour
                return (token, DateTimeOffset.UtcNow.AddMinutes(55));
            }
            return (string.Empty, DateTimeOffset.UtcNow);
        }

        protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default) => AuthenticateAsync(handle, ct);
    }
}
