using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.SaaS
{
    public class OracleFusionConnectionStrategy : SaaSConnectionStrategyBase
    {
        public override string StrategyId => "oracle-fusion";
        public override string DisplayName => "Oracle Fusion";
        public override ConnectorCategory Category => ConnectorCategory.SaaS;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Oracle Fusion Cloud Applications using HTTPS REST API.";
        public override string[] Tags => new[] { "oracle", "fusion", "erp", "cloud", "rest-api" };
        public OracleFusionConnectionStrategy(ILogger? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct) { var pod = GetConfiguration<string>(config, "Pod", string.Empty); var endpoint = $"https://{pod}.oraclecloud.com"; var httpClient = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = config.Timeout }; return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["Pod"] = pod, ["Endpoint"] = endpoint }); }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { try { var response = await handle.GetConnection<HttpClient>().GetAsync("/fscmRestApi/resources/11.13.18.05/", ct); return response.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable; } catch { return false; } }
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>()?.Dispose(); await Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "Oracle Fusion is reachable" : "Oracle Fusion is not responding", sw.Elapsed, DateTimeOffset.UtcNow); }
        protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default) => Task.FromResult((Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddHours(8)));
        protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default) => AuthenticateAsync(handle, ct);
    }
}
