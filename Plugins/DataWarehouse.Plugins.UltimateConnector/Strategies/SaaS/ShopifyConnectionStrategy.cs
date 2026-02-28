using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.SaaS
{
    public class ShopifyConnectionStrategy : SaaSConnectionStrategyBase
    {
        private volatile string _accessToken = "";

        public override string StrategyId => "shopify";
        public override string DisplayName => "Shopify";
        public override ConnectorCategory Category => ConnectorCategory.SaaS;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Shopify ecommerce platform using HTTPS REST API.";
        public override string[] Tags => new[] { "shopify", "ecommerce", "retail", "saas", "rest-api" };
        public ShopifyConnectionStrategy(ILogger? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var shop = GetConfiguration<string>(config, "Shop", string.Empty);
            if (string.IsNullOrWhiteSpace(shop))
                throw new ArgumentException("Required configuration key 'Shop' is missing or empty.", nameof(config));
            _accessToken = GetConfiguration<string>(config, "AccessToken", string.Empty);
            var endpoint = $"https://{shop}.myshopify.com";
            var httpClient = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = config.Timeout };
            if (!string.IsNullOrEmpty(_accessToken))
                httpClient.DefaultRequestHeaders.Add("X-Shopify-Access-Token", _accessToken);
            return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["Shop"] = shop, ["Endpoint"] = endpoint });
        }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { try { var response = await handle.GetConnection<HttpClient>().GetAsync("/admin/api/2024-01/shop.json", ct); return response.IsSuccessStatusCode; } catch { return false; } }
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>()?.Dispose(); await Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "Shopify is reachable" : "Shopify is not responding", sw.Elapsed, DateTimeOffset.UtcNow); }
        protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            // Shopify Admin API uses X-Shopify-Access-Token header. Return stored access token.
            return Task.FromResult((_accessToken, DateTimeOffset.UtcNow.AddDays(365)));
        }
        protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default) => AuthenticateAsync(handle, ct);
    }
}
