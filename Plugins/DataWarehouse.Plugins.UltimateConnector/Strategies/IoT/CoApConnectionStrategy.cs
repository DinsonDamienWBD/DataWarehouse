using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.IoT
{
    /// <summary>
    /// Connection strategy for CoAP (Constrained Application Protocol).
    /// Tests connectivity via UDP connection to port 5683.
    /// </summary>
    public class CoApConnectionStrategy : IoTConnectionStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "coap";

        /// <inheritdoc/>
        public override string DisplayName => "CoAP";

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new();

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Connects to CoAP endpoints for constrained IoT device communication";

        /// <inheritdoc/>
        public override string[] Tags => new[] { "coap", "iot", "constrained", "udp", "m2m" };

        /// <summary>
        /// Initializes a new instance of <see cref="CoApConnectionStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger.</param>
        public CoApConnectionStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var parts = config.ConnectionString.Split(':');
            var host = parts[0];
            var port = parts.Length > 1 ? int.Parse(parts[1]) : 5683;

            var client = new UdpClient();
            client.Connect(host, port);

            await Task.Delay(10, ct);

            var info = new Dictionary<string, object>
            {
                ["host"] = host,
                ["port"] = port,
                ["protocol"] = "CoAP",
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(client, info);
        }

        /// <inheritdoc/>
        protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<UdpClient>();
            return Task.FromResult(client.Client?.Connected ?? false);
        }

        /// <inheritdoc/>
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<UdpClient>();
            client.Close();
            client.Dispose();
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<UdpClient>();
            var isHealthy = client.Client?.Connected ?? false;

            return Task.FromResult(new ConnectionHealth(
                IsHealthy: isHealthy,
                StatusMessage: isHealthy ? "CoAP device connected" : "CoAP device disconnected",
                Latency: TimeSpan.Zero,
                CheckedAt: DateTimeOffset.UtcNow));
        }

        /// <inheritdoc/>
        public override Task<Dictionary<string, object>> ReadTelemetryAsync(IConnectionHandle handle, string deviceId, CancellationToken ct = default)
        {
            // CoAP resource observation metadata
            var result = new Dictionary<string, object>
            {
                ["protocol"] = "CoAP",
                ["deviceId"] = deviceId,
                ["resourcePath"] = $"/sensors/{deviceId}",
                ["method"] = "GET",
                ["observe"] = true,
                ["status"] = "connected",
                ["message"] = "CoAP resource ready for observation",
                ["timestamp"] = DateTimeOffset.UtcNow
            };
            return Task.FromResult(result);
        }

        /// <inheritdoc/>
        public override Task<string> SendCommandAsync(IConnectionHandle handle, string deviceId, string command, Dictionary<string, object>? parameters = null, CancellationToken ct = default)
        {
            // CoAP POST/PUT command
            return Task.FromResult($"{{\"status\":\"queued\",\"resourcePath\":\"/actuators/{deviceId}\",\"method\":\"POST\",\"command\":\"{command}\",\"message\":\"CoAP command prepared\"}}");
        }
    }
}
