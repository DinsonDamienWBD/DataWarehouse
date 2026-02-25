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
    /// Connection strategy for SFTP servers.
    /// Tests connectivity via TCP connection to port 22 (SSH).
    /// </summary>
    public class SftpConnectionStrategy : ConnectionStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "sftp";

        /// <inheritdoc/>
        public override string DisplayName => "SFTP";

        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Protocol;

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new();

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Connects to SFTP servers for secure file transfer over SSH port 22";

        /// <inheritdoc/>
        public override string[] Tags => new[] { "sftp", "ssh", "file", "transfer", "protocol", "secure" };

        /// <summary>
        /// Initializes a new instance of <see cref="SftpConnectionStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger.</param>
        public SftpConnectionStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var parts = config.ConnectionString.Split(':');
            var host = parts[0];
            var port = parts.Length > 1 ? int.Parse(parts[1]) : 22;

            var client = new TcpClient();
            await client.ConnectAsync(host, port, ct);

            if (!client.Connected)
                throw new InvalidOperationException($"Failed to connect to SFTP server at {host}:{port}");

            var info = new Dictionary<string, object>
            {
                ["host"] = host,
                ["port"] = port,
                ["protocol"] = "SFTP/SSH",
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
        protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<TcpClient>();
            var isHealthy = client.Connected;

            return Task.FromResult(new ConnectionHealth(
                IsHealthy: isHealthy,
                StatusMessage: isHealthy ? "SFTP server connected" : "SFTP server disconnected",
                Latency: TimeSpan.Zero,
                CheckedAt: DateTimeOffset.UtcNow));
        }
    }
}
