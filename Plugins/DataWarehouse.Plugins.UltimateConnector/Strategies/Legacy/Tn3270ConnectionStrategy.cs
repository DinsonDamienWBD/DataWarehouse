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
    /// Connection strategy for TN3270 mainframe terminal emulation.
    /// </summary>
    public class Tn3270ConnectionStrategy : LegacyConnectionStrategyBase
    {
        public override string StrategyId => "tn3270";
        public override string DisplayName => "TN3270 Mainframe";
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to IBM mainframe systems via TN3270 terminal emulation";
        public override string[] Tags => new[] { "tn3270", "mainframe", "ibm", "legacy", "terminal" };

        public Tn3270ConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var parts = config.ConnectionString.Split(':');
            var client = new TcpClient();
            await client.ConnectAsync(parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 23, ct);
            return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "TN3270" });
        }

        protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(handle.GetConnection<TcpClient>().Connected);
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<TcpClient>().Close(); return Task.CompletedTask; }
        protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(new ConnectionHealth(handle.GetConnection<TcpClient>().Connected, "TN3270 mainframe", TimeSpan.Zero, DateTimeOffset.UtcNow));
        public override Task<string> EmulateProtocolAsync(IConnectionHandle handle, string protocolCommand, CancellationToken ct = default) => throw new NotSupportedException("Requires TN3270 emulation library");
        public override Task<string> TranslateCommandAsync(IConnectionHandle handle, string modernCommand, CancellationToken ct = default) => throw new NotSupportedException("Requires TN3270 translation library");
    }
}
