using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Legacy
{
    /// <summary>
    /// Connection strategy for IBM CICS transaction server.
    /// </summary>
    public class CicsConnectionStrategy : LegacyConnectionStrategyBase
    {
        public override string StrategyId => "cics";
        public override string DisplayName => "IBM CICS";
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to IBM CICS transaction processing systems";
        public override string[] Tags => new[] { "cics", "mainframe", "ibm", "legacy", "transactions" };

        public CicsConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var parts = config.ConnectionString.Split(':');
            var client = new TcpClient();
            await client.ConnectAsync(parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 1435, ct);
            return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "CICS" });
        }

        protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(handle.GetConnection<TcpClient>().Connected);
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<TcpClient>().Close(); return Task.CompletedTask; }
        protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(new ConnectionHealth(handle.GetConnection<TcpClient>().Connected, "CICS server", TimeSpan.Zero, DateTimeOffset.UtcNow));
        public override Task<string> EmulateProtocolAsync(IConnectionHandle handle, string protocolCommand, CancellationToken ct = default) => throw new NotSupportedException("Requires CICS library");
        public override Task<string> TranslateCommandAsync(IConnectionHandle handle, string modernCommand, CancellationToken ct = default) => throw new NotSupportedException("Requires CICS library");
    }
}
