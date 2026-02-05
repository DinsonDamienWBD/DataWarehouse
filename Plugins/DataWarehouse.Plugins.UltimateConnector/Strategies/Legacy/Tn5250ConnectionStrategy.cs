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
    /// Connection strategy for TN5250 AS/400 terminal emulation.
    /// </summary>
    public class Tn5250ConnectionStrategy : LegacyConnectionStrategyBase
    {
        public override string StrategyId => "tn5250";
        public override string DisplayName => "TN5250 AS/400";
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to IBM AS/400 systems via TN5250 terminal emulation";
        public override string[] Tags => new[] { "tn5250", "as400", "ibm", "legacy", "terminal" };

        public Tn5250ConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var parts = config.ConnectionString.Split(':');
            var client = new TcpClient();
            await client.ConnectAsync(parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 23, ct);
            return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "TN5250" });
        }

        protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(handle.GetConnection<TcpClient>().Connected);
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<TcpClient>().Close(); return Task.CompletedTask; }
        protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(new ConnectionHealth(handle.GetConnection<TcpClient>().Connected, "TN5250 AS/400", TimeSpan.Zero, DateTimeOffset.UtcNow));
        public override Task<string> EmulateProtocolAsync(IConnectionHandle handle, string protocolCommand, CancellationToken ct = default) => throw new NotSupportedException("Requires TN5250 emulation library");
        public override Task<string> TranslateCommandAsync(IConnectionHandle handle, string modernCommand, CancellationToken ct = default) => throw new NotSupportedException("Requires TN5250 translation library");
    }
}
