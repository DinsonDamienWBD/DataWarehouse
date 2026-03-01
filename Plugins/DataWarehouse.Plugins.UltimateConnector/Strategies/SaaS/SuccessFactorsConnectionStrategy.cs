using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.SaaS
{
    public class SuccessFactorsConnectionStrategy : SaaSConnectionStrategyBase
    {
        private volatile string _datacenter = "";
        private volatile string _username = "";
        private volatile string _password = "";
        private volatile string _companyId = "";

        public override string StrategyId => "successfactors";
        public override string DisplayName => "SAP SuccessFactors";
        public override ConnectorCategory Category => ConnectorCategory.SaaS;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to SAP SuccessFactors HCM using HTTPS OData API.";
        public override string[] Tags => new[] { "sap", "successfactors", "hcm", "hr", "odata" };
        public SuccessFactorsConnectionStrategy(ILogger? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            _datacenter = GetConfiguration<string>(config, "Datacenter", string.Empty);
            if (string.IsNullOrWhiteSpace(_datacenter))
                throw new ArgumentException("Required configuration key 'Datacenter' is missing or empty.", nameof(config));
            _username = GetConfiguration<string>(config, "Username", string.Empty);
            _password = GetConfiguration<string>(config, "Password", string.Empty);
            _companyId = GetConfiguration<string>(config, "CompanyId", string.Empty);
            var endpoint = $"https://api{_datacenter}.successfactors.com";
            var httpClient = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = config.Timeout };
            // SuccessFactors Basic auth uses companyId@username format
            if (!string.IsNullOrEmpty(_username) && !string.IsNullOrEmpty(_companyId))
            {
                var creds = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{_companyId}@{_username}:{_password}"));
                httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", creds);
            }
            return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["Datacenter"] = _datacenter, ["Endpoint"] = endpoint });
        }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { try { var response = await handle.GetConnection<HttpClient>().GetAsync("/odata/v2/User?$top=1", ct); return response.IsSuccessStatusCode; } catch { return false; } }
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>()?.Dispose(); return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "SuccessFactors is reachable" : "SuccessFactors is not responding", sw.Elapsed, DateTimeOffset.UtcNow); }
        protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            // SuccessFactors Basic auth: companyId@username:password Base64-encoded.
            if (!string.IsNullOrEmpty(_username) && !string.IsNullOrEmpty(_companyId))
            {
                var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{_companyId}@{_username}:{_password}"));
                return Task.FromResult((encoded, DateTimeOffset.UtcNow.AddHours(8)));
            }
            return Task.FromResult((string.Empty, DateTimeOffset.UtcNow));
        }
        protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default) => AuthenticateAsync(handle, ct);
    }
}
