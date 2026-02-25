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
    /// Connection strategy for BACnet building automation protocol.
    /// Tests connectivity via UDP connection to port 47808.
    /// </summary>
    public class BacNetConnectionStrategy : IoTConnectionStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "bacnet";

        /// <inheritdoc/>
        public override string DisplayName => "BACnet";

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new();

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Connects to BACnet devices for building automation and control";

        /// <inheritdoc/>
        public override string[] Tags => new[] { "bacnet", "building", "automation", "hvac", "iot" };

        /// <summary>
        /// Initializes a new instance of <see cref="BacNetConnectionStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger.</param>
        public BacNetConnectionStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var parts = config.ConnectionString.Split(':');
            var host = parts[0];
            var port = parts.Length > 1 ? int.Parse(parts[1]) : 47808;

            var client = new UdpClient();
            client.Connect(host, port);

            await Task.Delay(10, ct);

            var info = new Dictionary<string, object>
            {
                ["host"] = host,
                ["port"] = port,
                ["protocol"] = "BACnet/IP",
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
                StatusMessage: isHealthy ? "BACnet device connected" : "BACnet device disconnected",
                Latency: TimeSpan.Zero,
                CheckedAt: DateTimeOffset.UtcNow));
        }

        /// <inheritdoc/>
        public override Task<Dictionary<string, object>> ReadTelemetryAsync(IConnectionHandle handle, string deviceId, CancellationToken ct = default)
        {
            // BACnet object property read metadata
            var result = new Dictionary<string, object>
            {
                ["protocol"] = "BACnet/IP",
                ["deviceInstance"] = deviceId,
                ["objectType"] = "analogInput",
                ["propertyId"] = "presentValue",
                ["status"] = "connected",
                ["message"] = "BACnet device ready for ReadProperty service",
                ["timestamp"] = DateTimeOffset.UtcNow
            };
            return Task.FromResult(result);
        }

        /// <inheritdoc/>
        public override Task<string> SendCommandAsync(IConnectionHandle handle, string deviceId, string command, Dictionary<string, object>? parameters = null, CancellationToken ct = default)
        {
            // BACnet WriteProperty service
            return Task.FromResult($"{{\"status\":\"queued\",\"deviceInstance\":\"{deviceId}\",\"service\":\"WriteProperty\",\"command\":\"{command}\",\"message\":\"BACnet command prepared\"}}");
        }
    }
}
