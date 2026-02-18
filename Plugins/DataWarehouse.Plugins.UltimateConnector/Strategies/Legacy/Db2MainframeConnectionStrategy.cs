using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Legacy
{
    public class Db2MainframeConnectionStrategy : LegacyConnectionStrategyBase
    {
        public override string StrategyId => "db2-mainframe";
        public override string DisplayName => "DB2 Mainframe";
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to DB2 on IBM mainframe systems";
        public override string[] Tags => new[] { "db2", "mainframe", "ibm", "legacy", "database" };

        public Db2MainframeConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var parts = config.ConnectionString.Split(':');
            var client = new TcpClient();
            await client.ConnectAsync(parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 50000, ct);
            return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "DB2/MVS" });
        }

        protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(handle.GetConnection<TcpClient>().Connected);
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<TcpClient>().Close(); return Task.CompletedTask; }
        protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(new ConnectionHealth(handle.GetConnection<TcpClient>().Connected, "DB2 mainframe", TimeSpan.Zero, DateTimeOffset.UtcNow));
        public override async Task<string> EmulateProtocolAsync(IConnectionHandle handle, string protocolCommand, CancellationToken ct = default)
        {
            var client = handle.GetConnection<TcpClient>();
            var stream = client.GetStream();
            // Send DB2 DRDA protocol command
            var commandBytes = System.Text.Encoding.ASCII.GetBytes(protocolCommand);
            await stream.WriteAsync(commandBytes, ct);
            await stream.FlushAsync(ct);
            // Read response
            var buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                var bytesRead = await stream.ReadAsync(buffer, ct);
                return System.Text.Encoding.ASCII.GetString(buffer, 0, bytesRead);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public override Task<string> TranslateCommandAsync(IConnectionHandle handle, string modernCommand, CancellationToken ct = default)
        {
            // Translate SQL to DB2/MVS dialect
            var translated = modernCommand
                .Replace("TOP", "FETCH FIRST")
                .Replace("LIMIT", "FETCH FIRST")
                .Replace("ISNULL", "COALESCE")
                .Replace("GETDATE()", "CURRENT TIMESTAMP");
            return Task.FromResult($"{{\"original\":\"{modernCommand}\",\"translated\":\"{translated}\",\"protocol\":\"DB2/MVS\"}}");
        }
    }
}
