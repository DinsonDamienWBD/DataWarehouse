using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Legacy
{
    public class As400ConnectionStrategy : LegacyConnectionStrategyBase
    {
        public override string StrategyId => "as400";
        public override string DisplayName => "AS/400";
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to IBM AS/400 data queue systems";
        public override string[] Tags => new[] { "as400", "ibm", "legacy", "iseries", "dataqueue" };

        public As400ConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var parts = (config.ConnectionString ?? throw new ArgumentException("Connection string required")).Split(':');
            var host = parts[0];
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Host is required in connection string.");
            // Finding 2020: use TryParse to avoid FormatException on non-numeric port.
            var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 449;
            var client = new TcpClient();
            await client.ConnectAsync(host, port, ct);
            return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "AS/400", ["host"] = host, ["port"] = port });
        }

        // Finding 1919: TcpClient.Connected is stale; probe with fresh TCP connect.
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var info = handle.ConnectionInfo;
            var host = info.GetValueOrDefault("host")?.ToString() ?? "";
            if (!int.TryParse(info.GetValueOrDefault("port")?.ToString(), out var port)) port = 449;
            try { using var probe = new TcpClient(); await probe.ConnectAsync(host, port, ct); return true; }
            catch { return false; }
        }
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<TcpClient>().Close(); return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();
            return new ConnectionHealth(isHealthy, isHealthy ? "AS/400 system reachable" : "AS/400 system unreachable", sw.Elapsed, DateTimeOffset.UtcNow);
        }
        public override async Task<string> EmulateProtocolAsync(IConnectionHandle handle, string protocolCommand, CancellationToken ct = default)
        {
            var client = handle.GetConnection<TcpClient>();
            var stream = client.GetStream();
            // Send AS/400 command via data queue
            var commandBytes = System.Text.Encoding.ASCII.GetBytes(protocolCommand + "\r\n");
            await stream.WriteAsync(commandBytes, ct);
            await stream.FlushAsync(ct);
            // Read response
            var buffer = new byte[4096];
            var bytesRead = await stream.ReadAsync(buffer, ct);
            return System.Text.Encoding.ASCII.GetString(buffer, 0, bytesRead);
        }

        public override Task<string> TranslateCommandAsync(IConnectionHandle handle, string modernCommand, CancellationToken ct = default)
        {
            // Translate modern commands to AS/400 CL commands
            var parts = modernCommand.Split(' ');
            var action = parts[0].ToUpperInvariant();
            var translated = action switch
            {
                "LIST" => $"WRKOBJ OBJ({(parts.Length > 1 ? parts[1] : "*ALL")})",
                "READ" => $"DSPDTAARA DTAARA({(parts.Length > 1 ? parts[1] : "*LIBL")})",
                "WRITE" => $"CHGDTAARA DTAARA({(parts.Length > 1 ? parts[1] : "*LIBL")})",
                "CALL" => $"CALL PGM({(parts.Length > 1 ? parts[1] : "MYPROGRAM")})",
                _ => modernCommand
            };
            return Task.FromResult($"{{\"original\":\"{modernCommand}\",\"translated\":\"{translated}\",\"protocol\":\"AS/400\"}}");
        }
    }
}
