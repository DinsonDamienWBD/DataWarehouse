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
            var parts = (config.ConnectionString ?? throw new ArgumentException("Connection string required")).Split(':');
            var host = parts[0];
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Host is required in connection string.");
            var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 9999;
            var client = new TcpClient();
            await client.ConnectAsync(host, port, ct);
            return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "IMS Connect", ["host"] = host, ["port"] = port });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var info = handle.ConnectionInfo;
            var host = info.GetValueOrDefault("host")?.ToString() ?? "";
            if (!int.TryParse(info.GetValueOrDefault("port")?.ToString(), out var port)) port = 9999;
            try { using var probe = new TcpClient(); await probe.ConnectAsync(host, port, ct); return true; }
            catch { return false; }
        }
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<TcpClient>().Close(); return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();
            return new ConnectionHealth(isHealthy, isHealthy ? "IMS Connect reachable" : "IMS Connect unreachable", sw.Elapsed, DateTimeOffset.UtcNow);
        }
        public override async Task<string> EmulateProtocolAsync(IConnectionHandle handle, string protocolCommand, CancellationToken ct = default)
        {
            var client = handle.GetConnection<TcpClient>();
            var stream = client.GetStream();
            // Send IMS Connect message
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
