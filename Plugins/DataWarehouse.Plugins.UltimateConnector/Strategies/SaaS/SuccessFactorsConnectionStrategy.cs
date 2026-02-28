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
        public override string StrategyId => "successfactors";
        public override string DisplayName => "SAP SuccessFactors";
        public override ConnectorCategory Category => ConnectorCategory.SaaS;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to SAP SuccessFactors HCM using HTTPS OData API.";
        public override string[] Tags => new[] { "sap", "successfactors", "hcm", "hr", "odata" };
        public SuccessFactorsConnectionStrategy(ILogger? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var datacenter = GetConfiguration<string>(config, "Datacenter", string.Empty);
            if (string.IsNullOrWhiteSpace(datacenter))
                throw new ArgumentException("Required configuration key 'Datacenter' is missing or empty.", nameof(config));
            var endpoint = $"https://api{datacenter}.successfactors.com";
            var httpClient = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = config.Timeout };
            return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["Datacenter"] = datacenter, ["Endpoint"] = endpoint });
        }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { try { var response = await handle.GetConnection<HttpClient>().GetAsync("/odata/v2/User", ct); return response.IsSuccessStatusCode; } catch { return false; } }
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>()?.Dispose(); await Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "SuccessFactors is reachable" : "SuccessFactors is not responding", sw.Elapsed, DateTimeOffset.UtcNow); }
        protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default) => Task.FromResult((Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddHours(8)));
        protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default) => AuthenticateAsync(handle, ct);
    }
}
