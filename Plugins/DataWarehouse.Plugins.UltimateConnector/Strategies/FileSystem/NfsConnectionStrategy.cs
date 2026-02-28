using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.FileSystem
{
    public class NfsConnectionStrategy : ConnectionStrategyBase
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
            var parts = (config.ConnectionString ?? throw new ArgumentException("Connection string required")).Split(':');
            var client = new TcpClient();
            await client.ConnectAsync(parts[0], parts.Length > 1 && int.TryParse(parts[1], out var p2049) ? p2049 : 2049, ct);
            return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "NFS" });
        }

        protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(handle.GetConnection<TcpClient>().Connected);
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<TcpClient>().Close(); return Task.CompletedTask; }
        protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(new ConnectionHealth(handle.GetConnection<TcpClient>().Connected, "NFS server", TimeSpan.Zero, DateTimeOffset.UtcNow));
    }
}
