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
    /// Connection strategy for LoRaWAN network servers.
    /// </summary>
    public class LoRaWanConnectionStrategy : IoTConnectionStrategyBase
    {
        public override string StrategyId => "lorawan";
        public override string DisplayName => "LoRaWAN";
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to LoRaWAN network servers for long-range IoT communication";
        public override string[] Tags => new[] { "lorawan", "lpwan", "iot", "wireless", "long-range" };

        public LoRaWanConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var parts = config.ConnectionString.Split(':');
            var host = parts[0];
            var port = parts.Length > 1 ? int.Parse(parts[1]) : 1700;
            var client = new TcpClient();
            await client.ConnectAsync(host, port, ct);
            return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["host"] = host, ["port"] = port, ["protocol"] = "LoRaWAN" });
        }

        protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(handle.GetConnection<TcpClient>().Connected);
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<TcpClient>().Close(); return Task.CompletedTask; }
        protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(new ConnectionHealth(handle.GetConnection<TcpClient>().Connected, "LoRaWAN network server", TimeSpan.Zero, DateTimeOffset.UtcNow));
        public override Task<Dictionary<string, object>> ReadTelemetryAsync(IConnectionHandle handle, string deviceId, CancellationToken ct = default) => throw new NotSupportedException("Requires LoRaWAN SDK");
        public override Task<string> SendCommandAsync(IConnectionHandle handle, string deviceId, string command, Dictionary<string, object>? parameters = null, CancellationToken ct = default) => throw new NotSupportedException("Requires LoRaWAN SDK");
    }
}
