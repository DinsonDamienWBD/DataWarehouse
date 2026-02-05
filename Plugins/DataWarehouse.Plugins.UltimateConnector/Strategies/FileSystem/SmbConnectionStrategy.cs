using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.FileSystem
{
    public class SmbConnectionStrategy : ConnectionStrategyBase
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
            var parts = config.ConnectionString.Split(':');
            var client = new TcpClient();
            await client.ConnectAsync(parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 445, ct);
            return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "SMB/CIFS" });
        }

        protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(handle.GetConnection<TcpClient>().Connected);
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<TcpClient>().Close(); return Task.CompletedTask; }
        protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(new ConnectionHealth(handle.GetConnection<TcpClient>().Connected, "SMB server", TimeSpan.Zero, DateTimeOffset.UtcNow));
    }
}
