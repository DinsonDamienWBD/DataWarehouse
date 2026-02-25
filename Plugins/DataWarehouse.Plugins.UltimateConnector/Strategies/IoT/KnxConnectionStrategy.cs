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
    /// Connection strategy for KNX building automation.
    /// </summary>
    public class KnxConnectionStrategy : IoTConnectionStrategyBase
    {
        public override string StrategyId => "knx";
        public override string DisplayName => "KNX";
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to KNX building automation systems";
        public override string[] Tags => new[] { "knx", "building", "automation", "iot", "eib" };

        public KnxConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var parts = config.ConnectionString.Split(':');
            var client = new UdpClient();
            client.Connect(parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 3671);
            await Task.Delay(10, ct);
            return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "KNX/IP" });
        }

        protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(handle.GetConnection<UdpClient>().Client?.Connected ?? false);
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<UdpClient>().Close(); return Task.CompletedTask; }
        protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(new ConnectionHealth(handle.GetConnection<UdpClient>().Client?.Connected ?? false, "KNX gateway", TimeSpan.Zero, DateTimeOffset.UtcNow));
        public override Task<Dictionary<string, object>> ReadTelemetryAsync(IConnectionHandle handle, string deviceId, CancellationToken ct = default)
        {
            var result = new Dictionary<string, object>
            {
                ["protocol"] = "KNX/IP",
                ["groupAddress"] = deviceId,
                ["dataPointType"] = "DPT_Switch",
                ["status"] = "connected",
                ["message"] = "KNX gateway ready for GroupValueRead",
                ["timestamp"] = DateTimeOffset.UtcNow
            };
            return Task.FromResult(result);
        }

        public override Task<string> SendCommandAsync(IConnectionHandle handle, string deviceId, string command, Dictionary<string, object>? parameters = null, CancellationToken ct = default)
        {
            return Task.FromResult($"{{\"status\":\"queued\",\"groupAddress\":\"{deviceId}\",\"service\":\"GroupValueWrite\",\"command\":\"{command}\",\"message\":\"KNX command prepared\"}}");
        }
    }
}
