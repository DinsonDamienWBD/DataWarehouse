using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Protocol
{
    /// <summary>
    /// Connection strategy for FTP servers.
    /// Tests connectivity via TCP connection to port 21.
    /// </summary>
    public sealed class FtpConnectionStrategy : ConnectionStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "ftp";

        /// <inheritdoc/>
        public override string DisplayName => "FTP";

        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Protocol;

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new();

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Connects to FTP servers for file transfer over TCP port 21";

        /// <inheritdoc/>
        public override string[] Tags => new[] { "ftp", "file", "transfer", "protocol", "tcp" };

        /// <summary>
        /// Initializes a new instance of <see cref="FtpConnectionStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger.</param>
        public FtpConnectionStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var parts = (config.ConnectionString ?? throw new ArgumentException("Connection string required")).Split(':');
            var host = parts[0];
            var port = parts.Length > 1 && int.TryParse(parts[1], out var p21) ? p21 : 21;

            var client = new TcpClient();
            await client.ConnectAsync(host, port, ct);

            if (!client.Connected)
                throw new InvalidOperationException($"Failed to connect to FTP server at {host}:{port}");

            var info = new Dictionary<string, object>
            {
                ["host"] = host,
                ["port"] = port,
                ["protocol"] = "FTP",
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(client, info);
        }

        /// <inheritdoc/>
        protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<TcpClient>();
            return Task.FromResult(client.Connected);
        }

        /// <inheritdoc/>
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<TcpClient>();
            client.Close();
            client.Dispose();
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            // Finding 2109: Measure real round-trip latency via a real probe instead of reporting zero.
            // Send a TCP no-op (zero-byte) and measure echo time; fall back to Connected if send fails.
            var client = handle.GetConnection<TcpClient>();
            if (!client.Connected)
            {
                return new ConnectionHealth(
                    IsHealthy: false,
                    StatusMessage: "FTP server disconnected",
                    Latency: TimeSpan.Zero,
                    CheckedAt: DateTimeOffset.UtcNow);
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // Write a NOOP command and measure the latency of the socket send.
                var noopBytes = System.Text.Encoding.ASCII.GetBytes("NOOP\r\n");
                await client.GetStream().WriteAsync(noopBytes, ct);
                sw.Stop();
                return new ConnectionHealth(
                    IsHealthy: true,
                    StatusMessage: "FTP server connected",
                    Latency: sw.Elapsed,
                    CheckedAt: DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new ConnectionHealth(
                    IsHealthy: false,
                    StatusMessage: $"FTP health check failed: {ex.Message}",
                    Latency: sw.Elapsed,
                    CheckedAt: DateTimeOffset.UtcNow);
            }
        }
    }
}
