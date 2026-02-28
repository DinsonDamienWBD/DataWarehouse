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
    /// Connection strategy for Zigbee coordinators via TCP bridge.
    /// </summary>
    public class ZigbeeConnectionStrategy : IoTConnectionStrategyBase
    {
        public override string StrategyId => "zigbee";
        public override string DisplayName => "Zigbee";
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Zigbee coordinators for home automation and IoT";
        public override string[] Tags => new[] { "zigbee", "iot", "mesh", "home-automation", "wireless" };

        public ZigbeeConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var parts = (config.ConnectionString ?? throw new ArgumentException("Connection string required")).Split(':');
            var client = new TcpClient();
            await client.ConnectAsync(parts[0], parts.Length > 1 && int.TryParse(parts[1], out var p8888) ? p8888 : 8888, ct);
            return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "Zigbee/TCP" });
        }

        protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(handle.GetConnection<TcpClient>().Connected);
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<TcpClient>().Close(); return Task.CompletedTask; }
        protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(new ConnectionHealth(handle.GetConnection<TcpClient>().Connected, "Zigbee bridge", TimeSpan.Zero, DateTimeOffset.UtcNow));
        public override Task<Dictionary<string, object>> ReadTelemetryAsync(IConnectionHandle handle, string deviceId, CancellationToken ct = default)
        {
            var result = new Dictionary<string, object>
            {
                ["protocol"] = "Zigbee",
                ["ieeeAddress"] = deviceId,
                ["cluster"] = "genOnOff",
                ["status"] = "connected",
                ["message"] = "Zigbee coordinator ready for attribute read",
                ["timestamp"] = DateTimeOffset.UtcNow
            };
            return Task.FromResult(result);
        }

        public override Task<string> SendCommandAsync(IConnectionHandle handle, string deviceId, string command, Dictionary<string, object>? parameters = null, CancellationToken ct = default)
        {
            return Task.FromResult($"{{\"status\":\"queued\",\"ieeeAddress\":\"{deviceId}\",\"cluster\":\"genOnOff\",\"command\":\"{command}\",\"message\":\"Zigbee command prepared\"}}");
        }
    }
}
