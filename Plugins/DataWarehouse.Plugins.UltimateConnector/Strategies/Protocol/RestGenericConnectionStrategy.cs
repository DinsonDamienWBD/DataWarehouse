using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Protocol
{
    /// <summary>
    /// Generic REST API connection strategy.
    /// Provides HTTP client for REST endpoints with configurable authentication.
    /// </summary>
    public class RestGenericConnectionStrategy : ConnectionStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "rest-generic";

        /// <inheritdoc/>
        public override string DisplayName => "REST (Generic)";

        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Protocol;

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new();

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Connects to generic REST APIs via HTTP/HTTPS with flexible authentication";

        /// <inheritdoc/>
        public override string[] Tags => new[] { "rest", "http", "api", "protocol", "generic" };

        /// <summary>
        /// Initializes a new instance of <see cref="RestGenericConnectionStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger.</param>
        public RestGenericConnectionStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var baseUrl = config.ConnectionString;
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new ArgumentException("Base URL is required in ConnectionString");

            var client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = config.Timeout };

            var apiKey = GetConfiguration(config, "ApiKey", string.Empty);
            if (!string.IsNullOrEmpty(apiKey))
            {
                var headerName = GetConfiguration(config, "ApiKeyHeader", "Authorization");
                var headerValue = GetConfiguration(config, "ApiKeyPrefix", "Bearer") + " " + apiKey;
                client.DefaultRequestHeaders.Add(headerName, headerValue);
            }

            var healthEndpoint = GetConfiguration(config, "HealthEndpoint", "/health");
            var response = await client.GetAsync(healthEndpoint, ct);
            response.EnsureSuccessStatusCode();

            var info = new Dictionary<string, object>
            {
                ["base_url"] = baseUrl,
                ["protocol"] = "REST/HTTP",
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(client, info);
        }

        /// <inheritdoc/>
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            var response = await client.GetAsync("/", ct);
            return response.IsSuccessStatusCode;
        }

        /// <inheritdoc/>
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            client.Dispose();
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();

            return new ConnectionHealth(
                IsHealthy: isHealthy,
                StatusMessage: isHealthy ? "REST endpoint responsive" : "REST endpoint unreachable",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow);
        }
    }
}
