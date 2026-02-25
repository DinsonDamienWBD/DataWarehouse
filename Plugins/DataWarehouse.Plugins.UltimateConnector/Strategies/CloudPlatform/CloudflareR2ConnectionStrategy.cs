using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.CloudPlatform
{
    public class CloudflareR2ConnectionStrategy : SaaSConnectionStrategyBase
    {
        public override string StrategyId => "cloudflare-r2";
        public override string DisplayName => "Cloudflare R2";
        public override ConnectorCategory Category => ConnectorCategory.SaaS;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Cloudflare R2 using S3-compatible HTTPS REST API for zero-egress object storage.";
        public override string[] Tags => new[] { "cloudflare", "r2", "s3-compatible", "object-storage", "rest-api" };
        public CloudflareR2ConnectionStrategy(ILogger? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct) { var accountId = GetConfiguration<string>(config, "AccountId", string.Empty); var endpoint = $"https://{accountId}.r2.cloudflarestorage.com"; var httpClient = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = config.Timeout }; return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["AccountId"] = accountId, ["Endpoint"] = endpoint }); }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { try { var response = await handle.GetConnection<HttpClient>().GetAsync("/", ct); return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Forbidden; } catch { return false; } }
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>()?.Dispose(); await Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "Cloudflare R2 is reachable" : "Cloudflare R2 is not responding", sw.Elapsed, DateTimeOffset.UtcNow); }
        protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default) => Task.FromResult((Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddHours(1)));
        protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default) => AuthenticateAsync(handle, ct);
    }
}
