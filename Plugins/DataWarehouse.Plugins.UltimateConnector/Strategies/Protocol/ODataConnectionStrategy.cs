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
    /// Connection strategy for OData endpoints.
    /// Tests connectivity via HTTP GET to $metadata endpoint.
    /// </summary>
    public class ODataConnectionStrategy : ConnectionStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "odata";

        /// <inheritdoc/>
        public override string DisplayName => "OData";

        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Protocol;

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new();

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Connects to OData services for standardized REST data access with metadata";

        /// <inheritdoc/>
        public override string[] Tags => new[] { "odata", "rest", "api", "protocol", "metadata" };

        /// <summary>
        /// Initializes a new instance of <see cref="ODataConnectionStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger.</param>
        public ODataConnectionStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var endpoint = config.ConnectionString;
            if (string.IsNullOrWhiteSpace(endpoint))
                throw new ArgumentException("OData endpoint URL is required in ConnectionString");

            var client = new HttpClient { BaseAddress = new Uri(endpoint) };

            var apiKey = GetConfiguration(config, "ApiKey", string.Empty);
            if (!string.IsNullOrEmpty(apiKey))
                client.DefaultRequestHeaders.Remove("Authorization");
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            var metadataResponse = await client.GetAsync("$metadata", ct);
            metadataResponse.EnsureSuccessStatusCode();

            var metadata = await metadataResponse.Content.ReadAsStringAsync(ct);

            var info = new Dictionary<string, object>
            {
                ["endpoint"] = endpoint,
                ["protocol"] = "OData",
                ["has_metadata"] = !string.IsNullOrEmpty(metadata),
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(client, info);
        }

        /// <inheritdoc/>
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            var response = await client.GetAsync("$metadata", ct);
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
                StatusMessage: isHealthy ? "OData endpoint responsive" : "OData endpoint unreachable",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow);
        }
    }
}
