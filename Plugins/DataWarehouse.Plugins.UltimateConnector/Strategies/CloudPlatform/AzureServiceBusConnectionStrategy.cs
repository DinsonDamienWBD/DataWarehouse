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
    /// Azure Service Bus connection strategy using HTTPS REST API.
    /// </summary>
    public class AzureServiceBusConnectionStrategy : SaaSConnectionStrategyBase
    {
        public override string StrategyId => "azure-servicebus";
        public override string DisplayName => "Azure Service Bus";
        public override ConnectorCategory Category => ConnectorCategory.SaaS;

        public override ConnectionStrategyCapabilities Capabilities => new();

        public override string SemanticDescription => "Connects to Azure Service Bus using HTTPS REST API for reliable enterprise messaging.";
        public override string[] Tags => new[] { "azure", "service-bus", "messaging", "queue", "rest-api" };

        public AzureServiceBusConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var namespace_ = GetConfiguration<string>(config, "Namespace", null!);
            if (string.IsNullOrEmpty(namespace_))
                throw new InvalidOperationException("Azure Service Bus Namespace is required.");

            var endpoint = $"https://{namespace_}.servicebus.windows.net";

            var httpClient = new HttpClient
            {
                BaseAddress = new Uri(endpoint),
                Timeout = config.Timeout
            };

            var connectionInfo = new Dictionary<string, object>
            {
                ["Namespace"] = namespace_,
                ["Endpoint"] = endpoint
            };

            return new DefaultConnectionHandle(httpClient, connectionInfo);
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var httpClient = handle.GetConnection<HttpClient>();
            try
            {
                var response = await httpClient.GetAsync("/$Resources/Queues", ct);
                return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Unauthorized;
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
                StatusMessage: isHealthy ? "Azure Service Bus is reachable" : "Azure Service Bus is not responding",
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
