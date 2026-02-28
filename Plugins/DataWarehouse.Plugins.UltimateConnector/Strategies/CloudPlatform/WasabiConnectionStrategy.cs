using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.CloudPlatform
{
    public class WasabiConnectionStrategy : SaaSConnectionStrategyBase
    {
        public override string StrategyId => "wasabi";
        public override string DisplayName => "Wasabi";
        public override ConnectorCategory Category => ConnectorCategory.SaaS;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Wasabi using S3-compatible HTTPS REST API for hot cloud storage.";
        public override string[] Tags => new[] { "wasabi", "s3-compatible", "cloud", "object-storage", "rest-api" };
        public WasabiConnectionStrategy(ILogger? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct) { var region = GetConfiguration(config, "Region", "us-east-1"); var endpoint = $"https://s3.{region}.wasabisys.com"; var httpClient = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = config.Timeout }; return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["Region"] = region, ["Endpoint"] = endpoint }); }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            try
            {
                using var response = await handle.GetConnection<HttpClient>().GetAsync("/", ct);
                // Finding 1844: 403 Forbidden means credentials are invalid — do NOT report as healthy.
                // Only treat the endpoint as healthy when it returns 200 or an S3-expected redirect (301/307).
                return response.IsSuccessStatusCode ||
                       response.StatusCode == System.Net.HttpStatusCode.MovedPermanently ||
                       response.StatusCode == System.Net.HttpStatusCode.TemporaryRedirect;
            }
            catch { return false; }
        }
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>()?.Dispose(); await Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "Wasabi is reachable" : "Wasabi is not responding", sw.Elapsed, DateTimeOffset.UtcNow); }
        protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default)
            => throw new NotSupportedException(
                "Wasabi authentication uses AWS Signature V4 (Access Key + Secret Key). " +
                "Configure AccessKey and SecretKey via connection properties and use the " +
                "AWSSDK.S3 NuGet package pointed at the Wasabi endpoint for production connectivity.");
        protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default)
            => AuthenticateAsync(handle, ct);
    }
}
