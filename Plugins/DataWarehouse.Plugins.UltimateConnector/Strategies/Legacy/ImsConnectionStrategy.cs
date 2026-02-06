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
    /// Connection strategy for IBM IMS (Information Management System).
    /// </summary>
    public class ImsConnectionStrategy : LegacyConnectionStrategyBase
    {
        public override string StrategyId => "ims";
        public override string DisplayName => "IBM IMS";
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to IBM IMS hierarchical database and transaction systems";
        public override string[] Tags => new[] { "ims", "mainframe", "ibm", "legacy", "database" };

        public ImsConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var parts = config.ConnectionString.Split(':');
            var client = new TcpClient();
            await client.ConnectAsync(parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 9999, ct);
            return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "IMS Connect" });
        }

        protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(handle.GetConnection<TcpClient>().Connected);
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<TcpClient>().Close(); return Task.CompletedTask; }
        protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(new ConnectionHealth(handle.GetConnection<TcpClient>().Connected, "IMS Connect", TimeSpan.Zero, DateTimeOffset.UtcNow));
        public override async Task<string> EmulateProtocolAsync(IConnectionHandle handle, string protocolCommand, CancellationToken ct = default)
        {
            var client = handle.GetConnection<TcpClient>();
            var stream = client.GetStream();
            // Send IMS Connect message
            var commandBytes = System.Text.Encoding.ASCII.GetBytes(protocolCommand);
            await stream.WriteAsync(commandBytes, ct);
            await stream.FlushAsync(ct);
            // Read response
            var buffer = new byte[8192];
            var bytesRead = await stream.ReadAsync(buffer, ct);
            return System.Text.Encoding.ASCII.GetString(buffer, 0, bytesRead);
        }

        public override Task<string> TranslateCommandAsync(IConnectionHandle handle, string modernCommand, CancellationToken ct = default)
        {
            // Translate modern commands to IMS DL/I calls
            var parts = modernCommand.Split(' ');
            var operation = parts[0].ToUpperInvariant();
            var translated = operation switch
            {
                "GET" => $"GU {(parts.Length > 1 ? parts[1] : "SEGMENT")}",
                "INSERT" => $"ISRT {(parts.Length > 1 ? parts[1] : "SEGMENT")}",
                "UPDATE" => $"REPL {(parts.Length > 1 ? parts[1] : "SEGMENT")}",
                "DELETE" => $"DLET {(parts.Length > 1 ? parts[1] : "SEGMENT")}",
                _ => modernCommand
            };
            return Task.FromResult($"{{\"original\":\"{modernCommand}\",\"translated\":\"{translated}\",\"protocol\":\"IMS DL/I\"}}");
        }
    }
}
