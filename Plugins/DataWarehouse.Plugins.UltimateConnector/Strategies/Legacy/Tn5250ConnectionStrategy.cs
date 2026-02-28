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
            if (string.IsNullOrWhiteSpace(config.ConnectionString))
                throw new ArgumentException("ConnectionString must be in 'host' or 'host:port' format for TN5250.", nameof(config));
            var parts = (config.ConnectionString ?? throw new ArgumentException("Connection string required")).Split(':');
            var host = parts[0];
            if (string.IsNullOrWhiteSpace(host))
                throw new ArgumentException("Host portion of ConnectionString is empty for TN5250.", nameof(config));
            var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 23;
            var client = new TcpClient();
            await client.ConnectAsync(host, port, ct);
            return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "TN5250", ["host"] = host, ["port"] = port });
        }

        protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(handle.GetConnection<TcpClient>().Connected);
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<TcpClient>().Close(); return Task.CompletedTask; }
        protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(new ConnectionHealth(handle.GetConnection<TcpClient>().Connected, "TN5250 AS/400", TimeSpan.Zero, DateTimeOffset.UtcNow));
        public override async Task<string> EmulateProtocolAsync(IConnectionHandle handle, string protocolCommand, CancellationToken ct = default)
        {
            var client = handle.GetConnection<TcpClient>();
            var stream = client.GetStream();
            // Send TN5250 command bytes
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
            // Map modern commands to TN5250 AID keys
            var translated = modernCommand.ToUpperInvariant() switch
            {
                "ENTER" => "\xf1",  // 5250 Enter
                "F3" => "\x33",     // F3/Exit
                "F12" => "\xbc",    // F12/Cancel
                "PAGEUP" => "\xf4", // Page Up
                "PAGEDOWN" => "\xf5", // Page Down
                _ => modernCommand
            };
            return Task.FromResult($"{{\"original\":\"{modernCommand}\",\"translated\":\"{translated}\",\"protocol\":\"TN5250\"}}");
        }
    }
}
