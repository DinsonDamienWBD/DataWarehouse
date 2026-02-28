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
    public class VsamConnectionStrategy : LegacyConnectionStrategyBase
    {
        public override string StrategyId => "vsam";
        public override string DisplayName => "VSAM";
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to VSAM file systems via TCP bridge";
        public override string[] Tags => new[] { "vsam", "mainframe", "ibm", "legacy", "files" };

        public VsamConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(config.ConnectionString))
                throw new ArgumentException("ConnectionString must be in 'host' or 'host:port' format for VSAM.", nameof(config));
            var parts = (config.ConnectionString ?? throw new ArgumentException("Connection string required")).Split(':');
            var host = parts[0];
            if (string.IsNullOrWhiteSpace(host))
                throw new ArgumentException("Host portion of ConnectionString is empty for VSAM.", nameof(config));
            var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 8000;
            var client = new TcpClient();
            await client.ConnectAsync(host, port, ct);
            return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "VSAM Bridge", ["host"] = host, ["port"] = port });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var info = handle.ConnectionInfo;
            var host = info.GetValueOrDefault("host")?.ToString() ?? "";
            if (!int.TryParse(info.GetValueOrDefault("port")?.ToString(), out var port)) port = 8000;
            try { using var probe = new TcpClient(); await probe.ConnectAsync(host, port, ct); return true; }
            catch { return false; }
        }
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<TcpClient>().Close(); return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();
            return new ConnectionHealth(isHealthy, isHealthy ? "VSAM bridge reachable" : "VSAM bridge unreachable", sw.Elapsed, DateTimeOffset.UtcNow);
        }
        public override async Task<string> EmulateProtocolAsync(IConnectionHandle handle, string protocolCommand, CancellationToken ct = default)
        {
            var client = handle.GetConnection<TcpClient>();
            var stream = client.GetStream();
            // Send VSAM bridge command
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
            // Translate modern commands to VSAM file operations
            var parts = modernCommand.Split(' ');
            var operation = parts[0].ToUpperInvariant();
            var translated = operation switch
            {
                "READ" => $"GET {(parts.Length > 1 ? parts[1] : "RECORD")} KEY({(parts.Length > 2 ? parts[2] : "*")})",
                "WRITE" => $"PUT {(parts.Length > 1 ? parts[1] : "RECORD")}",
                "UPDATE" => $"REWRITE {(parts.Length > 1 ? parts[1] : "RECORD")}",
                "DELETE" => $"DELETE {(parts.Length > 1 ? parts[1] : "RECORD")} KEY({(parts.Length > 2 ? parts[2] : "*")})",
                _ => modernCommand
            };
            return Task.FromResult($"{{\"original\":\"{modernCommand}\",\"translated\":\"{translated}\",\"protocol\":\"VSAM\"}}");
        }
    }
}
