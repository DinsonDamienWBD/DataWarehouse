using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
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
        public override string SemanticDescription => "Connects to Oracle Fusion Cloud Applications using HTTPS REST API with Basic authentication.";
        public override string[] Tags => new[] { "oracle", "fusion", "erp", "cloud", "rest-api" };

        private volatile string _encodedCredential = "";

        public OracleFusionConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var pod = GetConfiguration<string>(config, "Pod", string.Empty);
            if (string.IsNullOrWhiteSpace(pod))
                throw new ArgumentException("Required configuration key 'Pod' is missing or empty. Set it to your Oracle Cloud pod identifier (e.g. fa-xxxx).", nameof(config));

            var username = GetConfiguration<string>(config, "Username", string.Empty)
                ?? config.AuthSecondary
                ?? string.Empty;
            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentException("Required configuration key 'Username' (or AuthSecondary) is missing.", nameof(config));

            var password = GetConfiguration<string>(config, "Password", string.Empty)
                ?? config.AuthCredential
                ?? string.Empty;
            if (string.IsNullOrWhiteSpace(password))
                throw new ArgumentException("Required configuration key 'Password' (or AuthCredential) is missing.", nameof(config));

            // Oracle Fusion uses HTTP Basic Authentication
            _encodedCredential = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));

            var endpoint = $"https://{pod}.oraclecloud.com";
            var httpClient = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = config.Timeout };
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", _encodedCredential);
            return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["Pod"] = pod, ["Endpoint"] = endpoint });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { try { var response = await handle.GetConnection<HttpClient>().GetAsync("/fscmRestApi/resources/11.13.18.05/", ct); return response.IsSuccessStatusCode; } catch { return false; } }
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<HttpClient>()?.Dispose(); await Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "Oracle Fusion is reachable" : "Oracle Fusion is not responding", sw.Elapsed, DateTimeOffset.UtcNow); }

        protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            // Oracle Fusion Basic auth credentials do not expire on the client side.
            var encoded = _encodedCredential;
            if (!string.IsNullOrEmpty(encoded))
                return Task.FromResult((encoded, DateTimeOffset.UtcNow.AddDays(365)));
            return Task.FromResult((string.Empty, DateTimeOffset.UtcNow));
        }

        protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default) => AuthenticateAsync(handle, ct);
    }
}
