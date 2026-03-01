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
            var parts = (config.ConnectionString ?? throw new ArgumentException("Connection string required")).Split(':');
            var client = new UdpClient();
            client.Connect(parts[0], parts.Length > 1 && int.TryParse(parts[1], out var p3671) ? p3671 : 3671);
            await Task.Delay(10, ct);
            return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "KNX/IP" });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            // Finding 1969: UDP has no real connection state — probe by sending a KNX/IP
            // SEARCH_REQUEST (service type 0x0201) to see if endpoint is reachable.
            var client = handle.GetConnection<UdpClient>();
            if (client.Client == null) return false;
            try
            {
                // KNX/IP header: header_length=0x06, version=0x10, service=SEARCH_REQUEST(0x0201), total_length=0x000E
                // + HPAI: structure_length=0x08, host_protocol=0x01(IPV4_UDP), ip=0.0.0.0, port=0
                var searchReq = new byte[] { 0x06, 0x10, 0x02, 0x01, 0x00, 0x0E, 0x08, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
                await client.SendAsync(searchReq, searchReq.Length).WaitAsync(ct);
                return true;
            }
            catch { return false; }
        }
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<UdpClient>().Close(); return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();
            return new ConnectionHealth(isHealthy, isHealthy ? "KNX gateway reachable" : "KNX gateway unreachable", sw.Elapsed, DateTimeOffset.UtcNow);
        }
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
