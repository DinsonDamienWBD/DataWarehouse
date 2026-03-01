using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.FileSystem
{
    public sealed class SmbConnectionStrategy : ConnectionStrategyBase
    {
        public override string StrategyId => "smb";
        public override string DisplayName => "SMB/CIFS";
        public override ConnectorCategory Category => ConnectorCategory.FileSystem;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to SMB/CIFS network file shares";
        public override string[] Tags => new[] { "smb", "cifs", "filesystem", "network", "windows" };

        public SmbConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            // P2-2132: Use ParseHostPortSafe to correctly handle IPv6 addresses like [::1]:445
            var (host, port) = ParseHostPortSafe(config.ConnectionString ?? throw new ArgumentException("Connection string required"), 445);
            var client = new TcpClient();
            await client.ConnectAsync(host, port, ct);
            return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "SMB/CIFS", ["host"] = host, ["port"] = port });
        }

        // Finding 1919: Use live socket probe instead of stale Connected flag.
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<TcpClient>();
            if (!client.Connected) return false;
            try
            {
                await client.GetStream().WriteAsync(Array.Empty<byte>(), ct);
                return true;
            }
            catch { return false; }
        }

        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            handle.GetConnection<TcpClient>().Close();
            return Task.CompletedTask;
        }

        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();
            return new ConnectionHealth(isHealthy, isHealthy ? "SMB server connected" : "SMB server disconnected", sw.Elapsed, DateTimeOffset.UtcNow);
        }
    }
}
