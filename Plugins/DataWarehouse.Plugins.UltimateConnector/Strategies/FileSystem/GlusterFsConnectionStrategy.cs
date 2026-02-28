using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.FileSystem
{
    /// <summary>
    /// GlusterFS connection strategy using REST management API (glusterd2).
    /// Connects to GlusterFS cluster via HTTP-based management API.
    /// </summary>
    public class GlusterFsConnectionStrategy : ConnectionStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "fs-glusterfs";

        /// <inheritdoc/>
        public override string DisplayName => "GlusterFS";

        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.FileSystem;

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new();

        /// <inheritdoc/>
        public override string SemanticDescription => "Connects to GlusterFS cluster via REST management API";

        /// <inheritdoc/>
        public override string[] Tags => new[] { "glusterfs", "distributed", "filesystem", "cluster", "storage" };

        /// <summary>
        /// Initializes a new instance of <see cref="GlusterFsConnectionStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public GlusterFsConnectionStrategy(ILogger? logger = null) : base(logger) { }

        /// <summary>
        /// Establishes a connection to GlusterFS management API.
        /// ConnectionString format: endpoint:port (e.g., "https://localhost:24007")
        /// Properties: "VolumeName" (optional, for volume-specific operations)
        /// </summary>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var endpoint = config.ConnectionString;
            var volumeName = GetConfiguration<string>(config, "VolumeName", "");

            if (!endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                !endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                endpoint = $"https://{endpoint}";
            }

            var httpClient = new HttpClient
            {
                BaseAddress = new Uri(endpoint),
                Timeout = config.Timeout
            };

            // Test connection by querying volumes endpoint
            using var response = await httpClient.GetAsync("/v1/volumes", ct);
            response.EnsureSuccessStatusCode();

            var connectionInfo = new Dictionary<string, object>
            {
                ["protocol"] = "GlusterFS-REST",
                ["endpoint"] = endpoint,
                ["apiVersion"] = "v1"
            };

            if (!string.IsNullOrWhiteSpace(volumeName))
            {
                connectionInfo["volumeName"] = volumeName;
            }

            return new DefaultConnectionHandle(httpClient, connectionInfo);
        }

        /// <summary>
        /// Tests the GlusterFS connection by querying the volumes endpoint.
        /// </summary>
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            try
            {
                var httpClient = handle.GetConnection<HttpClient>();
                using var response = await httpClient.GetAsync("/v1/volumes", ct);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Disconnects from the GlusterFS management API by disposing the HTTP client.
        /// </summary>
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            handle.GetConnection<HttpClient>().Dispose();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Retrieves health status of the GlusterFS connection.
        /// </summary>
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var startTime = DateTimeOffset.UtcNow;
            try
            {
                var httpClient = handle.GetConnection<HttpClient>();
                var volumeName = handle.ConnectionInfo.TryGetValue("volumeName", out var volName)
                    ? volName.ToString()
                    : null;

                var start = DateTimeOffset.UtcNow;
                HttpResponseMessage response;

                // If a specific volume is configured, check its status
                if (!string.IsNullOrWhiteSpace(volumeName))
                {
                    response = await httpClient.GetAsync($"/v1/volumes/{volumeName}", ct);
                }
                else
                {
                    // Otherwise, check cluster-wide volumes endpoint
                    response = await httpClient.GetAsync("/v1/volumes", ct);
                }

                var latency = DateTimeOffset.UtcNow - start;

                return new ConnectionHealth(
                    IsHealthy: response.IsSuccessStatusCode,
                    StatusMessage: response.IsSuccessStatusCode
                        ? "GlusterFS cluster healthy"
                        : $"GlusterFS returned {response.StatusCode}",
                    Latency: latency,
                    CheckedAt: DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                return new ConnectionHealth(
                    IsHealthy: false,
                    StatusMessage: $"GlusterFS health check failed: {ex.Message}",
                    Latency: DateTimeOffset.UtcNow - startTime,
                    CheckedAt: DateTimeOffset.UtcNow);
            }
        }
    }
}
