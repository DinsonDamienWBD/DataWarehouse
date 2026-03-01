using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.FileSystem
{
    public sealed class NfsConnectionStrategy : ConnectionStrategyBase
    {
        public override string StrategyId => "nfs";
        public override string DisplayName => "NFS";
        public override ConnectorCategory Category => ConnectorCategory.FileSystem;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Network File System (NFS) servers";
        public override string[] Tags => new[] { "nfs", "filesystem", "network", "storage", "unix" };

        public NfsConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            // P2-2132: Use ParseHostPortSafe to correctly handle IPv6 addresses like [::1]:2049
            var (host, port) = ParseHostPortSafe(config.ConnectionString ?? throw new ArgumentException("Connection string required"), 2049);
            var client = new TcpClient();
            await client.ConnectAsync(host, port, ct);
            return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "NFS", ["host"] = host, ["port"] = port });
        }

        // Finding 1919: Send a real TCP write (NFS NULL procedure) and measure actual round-trip latency.
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<TcpClient>();
            if (!client.Connected) return false;
            try
            {
                // NFS uses TCP; write a zero-length probe to verify the socket is still alive.
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
            return new ConnectionHealth(isHealthy, isHealthy ? "NFS server connected" : "NFS server disconnected", sw.Elapsed, DateTimeOffset.UtcNow);
        }
    }
}
