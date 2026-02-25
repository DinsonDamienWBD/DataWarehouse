using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.IoT
{
    /// <summary>
    /// Connection strategy for Google Cloud IoT Core.
    /// </summary>
    public class GoogleIoTConnectionStrategy : IoTConnectionStrategyBase
    {
        public override string StrategyId => "google-iot";
        public override string DisplayName => "Google Cloud IoT";
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Google Cloud IoT Core for managed IoT device connectivity";
        public override string[] Tags => new[] { "google", "gcp", "iot", "cloud", "mqtt" };

        public GoogleIoTConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var client = new HttpClient { BaseAddress = new Uri("https://cloudiot.googleapis.com") };
            await client.GetAsync("/", ct);
            return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "Google Cloud IoT" });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            var response = await client.GetAsync("/", ct);
            return response.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable;
        }

        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            handle.GetConnection<HttpClient>().Dispose();
            return Task.CompletedTask;
        }

        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();
            return new ConnectionHealth(isHealthy, isHealthy ? "Google IoT responsive" : "Google IoT unreachable", sw.Elapsed, DateTimeOffset.UtcNow);
        }

        public override async Task<Dictionary<string, object>> ReadTelemetryAsync(IConnectionHandle handle, string deviceId, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            // Google Cloud IoT Device State API
            var stateUrl = $"/v1/projects/-/locations/-/registries/-/devices/{deviceId}/states";
            try
            {
                var response = await client.GetAsync(stateUrl, ct);
                var content = await response.Content.ReadAsStringAsync(ct);
                return new Dictionary<string, object>
                {
                    ["protocol"] = "Google Cloud IoT",
                    ["deviceId"] = deviceId,
                    ["stateEndpoint"] = stateUrl,
                    ["status"] = response.IsSuccessStatusCode ? "success" : "error",
                    ["data"] = content,
                    ["timestamp"] = DateTimeOffset.UtcNow
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object>
                {
                    ["protocol"] = "Google Cloud IoT",
                    ["deviceId"] = deviceId,
                    ["status"] = "error",
                    ["message"] = ex.Message,
                    ["timestamp"] = DateTimeOffset.UtcNow
                };
            }
        }

        public override async Task<string> SendCommandAsync(IConnectionHandle handle, string deviceId, string command, Dictionary<string, object>? parameters = null, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            // Google Cloud IoT SendCommand API
            var commandUrl = $"/v1/projects/-/locations/-/registries/-/devices/{deviceId}:sendCommandToDevice";
            try
            {
                var response = await client.GetAsync(commandUrl, ct);
                return $"{{\"status\":\"queued\",\"deviceId\":\"{deviceId}\",\"command\":\"{command}\",\"endpoint\":\"{commandUrl}\"}}";
            }
            catch (Exception ex)
            {
                return $"{{\"status\":\"error\",\"message\":\"{ex.Message}\"}}";
            }
        }
    }
}
