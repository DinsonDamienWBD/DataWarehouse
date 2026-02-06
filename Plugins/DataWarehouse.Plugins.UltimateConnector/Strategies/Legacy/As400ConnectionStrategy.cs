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
            var parts = config.ConnectionString.Split(':');
            var client = new TcpClient();
            await client.ConnectAsync(parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 449, ct);
            return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "AS/400" });
        }

        protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(handle.GetConnection<TcpClient>().Connected);
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<TcpClient>().Close(); return Task.CompletedTask; }
        protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(new ConnectionHealth(handle.GetConnection<TcpClient>().Connected, "AS/400 system", TimeSpan.Zero, DateTimeOffset.UtcNow));
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
