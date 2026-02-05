using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.CloudPlatform
{
    /// <summary>
    /// Azure Blob Storage connection strategy using HTTPS REST API.
    /// </summary>
    public class AzureBlobConnectionStrategy : SaaSConnectionStrategyBase
    {
        public override string StrategyId => "azure-blob";
        public override string DisplayName => "Azure Blob Storage";
        public override ConnectorCategory Category => ConnectorCategory.SaaS;

        public override ConnectionStrategyCapabilities Capabilities => new();

        public override string SemanticDescription => "Connects to Azure Blob Storage using HTTPS REST API for scalable object storage.";
        public override string[] Tags => new[] { "azure", "blob-storage", "cloud", "object-storage", "rest-api" };

        public AzureBlobConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var accountName = GetConfiguration<string>(config, "AccountName", null!);
            if (string.IsNullOrEmpty(accountName))
                throw new InvalidOperationException("Azure AccountName is required.");

            var endpoint = $"https://{accountName}.blob.core.windows.net";

            var httpClient = new HttpClient
            {
                BaseAddress = new Uri(endpoint),
                Timeout = config.Timeout
            };

            var connectionInfo = new Dictionary<string, object>
            {
                ["AccountName"] = accountName,
                ["Endpoint"] = endpoint
            };

            return new DefaultConnectionHandle(httpClient, connectionInfo);
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var httpClient = handle.GetConnection<HttpClient>();
            try
            {
                var response = await httpClient.GetAsync("/?comp=list", ct);
                return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Forbidden;
            }
            catch
            {
                return false;
            }
        }

        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var httpClient = handle.GetConnection<HttpClient>();
            httpClient?.Dispose();
            await Task.CompletedTask;
        }

        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();

            return new ConnectionHealth(
                IsHealthy: isHealthy,
                StatusMessage: isHealthy ? "Azure Blob Storage is reachable" : "Azure Blob Storage is not responding",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow
            );
        }

        protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            var token = Guid.NewGuid().ToString("N");
            var expiry = DateTimeOffset.UtcNow.AddHours(1);
            return Task.FromResult((token, expiry));
        }

        protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default)
        {
            return AuthenticateAsync(handle, ct);
        }
    }
}
