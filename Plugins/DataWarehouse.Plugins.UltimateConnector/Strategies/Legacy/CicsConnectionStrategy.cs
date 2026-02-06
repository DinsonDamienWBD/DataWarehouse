using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
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
        public override async Task<string> EmulateProtocolAsync(IConnectionHandle handle, string protocolCommand, CancellationToken ct = default)
        {
            var client = handle.GetConnection<TcpClient>();
            var stream = client.GetStream();
            // EBCDIC encoding (IBM Code Page 37) - fallback to ASCII if not available
            var encoding = Encoding.GetEncoding(37, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
            // Send CICS transaction command
            var commandBytes = encoding.GetBytes(protocolCommand);
            await stream.WriteAsync(commandBytes, ct);
            await stream.FlushAsync(ct);
            // Read response
            var buffer = new byte[8192];
            var bytesRead = await stream.ReadAsync(buffer, ct);
            return encoding.GetString(buffer, 0, bytesRead);
        }

        public override Task<string> TranslateCommandAsync(IConnectionHandle handle, string modernCommand, CancellationToken ct = default)
        {
            // Translate modern REST-like commands to CICS transactions
            var parts = modernCommand.Split(' ');
            var transaction = parts[0].ToUpperInvariant();
            var translated = transaction switch
            {
                "GET" => $"CEMT I {(parts.Length > 1 ? parts[1] : "TASK")}",
                "SET" => $"CEMT S {(parts.Length > 1 ? parts[1] : "TASK")}",
                "START" => $"CEMT S TASK ENABLE",
                "STOP" => $"CEMT S TASK DISABLE",
                _ => modernCommand
            };
            return Task.FromResult($"{{\"original\":\"{modernCommand}\",\"translated\":\"{translated}\",\"protocol\":\"CICS\"}}");
        }
    }
}
