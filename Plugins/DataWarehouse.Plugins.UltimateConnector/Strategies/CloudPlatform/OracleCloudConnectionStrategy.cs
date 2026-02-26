using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.CloudPlatform
{
    public class OracleCloudConnectionStrategy : SaaSConnectionStrategyBase
    {
        public override string StrategyId => "oracle-cloud";
        public override string DisplayName => "Oracle Cloud Storage";
        public override ConnectorCategory Category => ConnectorCategory.SaaS;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Oracle Cloud Object Storage using HTTPS REST API.";
        public override string[] Tags => new[] { "oracle", "cloud", "object-storage", "oci", "rest-api" };
        public OracleCloudConnectionStrategy(ILogger? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct) { var region = GetConfiguration(config, "Region", "us-ashburn-1"); var namespace_ = GetConfiguration<string>(config, "Namespace", string.Empty); var endpoint = $"https://objectstorage.{region}.oraclecloud.com"; var httpClient = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = config.Timeout }; return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["Region"] = region, ["Namespace"] = namespace_, ["Endpoint"] = endpoint }); }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { try { var response = await handle.GetConnection<HttpClient>().GetAsync("/n/", ct); return response.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable; } catch { return false; } }
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>()?.Dispose(); await Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "Oracle Cloud Storage is reachable" : "Oracle Cloud Storage is not responding", sw.Elapsed, DateTimeOffset.UtcNow); }
        protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default)
            => throw new NotSupportedException(
                "Oracle Cloud authentication requires OCI API Key signing (RSA private key + tenancy/user OCIDs). " +
                "Configure TenancyOcid, UserOcid, and PrivateKeyPath and use the OCI .NET SDK for production connectivity.");
        protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default)
            => AuthenticateAsync(handle, ct);
    }
}
