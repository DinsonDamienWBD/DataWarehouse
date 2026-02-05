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
    /// Connection strategy for Azure IoT Hub.
    /// </summary>
    public class AzureIoTHubConnectionStrategy : IoTConnectionStrategyBase
    {
        public override string StrategyId => "azure-iot-hub";
        public override string DisplayName => "Azure IoT Hub";
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Azure IoT Hub for managed IoT device connectivity";
        public override string[] Tags => new[] { "azure", "iot", "cloud", "mqtt", "managed" };

        public AzureIoTHubConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var endpoint = config.ConnectionString;
            if (!endpoint.Contains(".azure-devices.net"))
                throw new ArgumentException("Azure IoT Hub endpoint required (*.azure-devices.net)");
            var client = new HttpClient { BaseAddress = new Uri($"https://{endpoint}") };
            await client.GetAsync("/", ct);
            return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["endpoint"] = endpoint, ["protocol"] = "Azure IoT Hub" });
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
            return new ConnectionHealth(isHealthy, isHealthy ? "Azure IoT Hub responsive" : "Azure IoT Hub unreachable", sw.Elapsed, DateTimeOffset.UtcNow);
        }

        public override Task<Dictionary<string, object>> ReadTelemetryAsync(IConnectionHandle handle, string deviceId, CancellationToken ct = default)
        {
            throw new NotSupportedException("Requires Azure IoT SDK");
        }

        public override Task<string> SendCommandAsync(IConnectionHandle handle, string deviceId, string command, Dictionary<string, object>? parameters = null, CancellationToken ct = default)
        {
            throw new NotSupportedException("Requires Azure IoT SDK");
        }
    }
}
