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
    /// Connection strategy for SMTP mail servers.
    /// Tests connectivity via TCP connection to port 25 or 587.
    /// </summary>
    public class SmtpConnectionStrategy : ConnectionStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "smtp";

        /// <inheritdoc/>
        public override string DisplayName => "SMTP";

        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Protocol;

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new();

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Connects to SMTP mail servers for email transmission";

        /// <inheritdoc/>
        public override string[] Tags => new[] { "smtp", "email", "mail", "protocol", "tcp" };

        /// <summary>
        /// Initializes a new instance of <see cref="SmtpConnectionStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger.</param>
        public SmtpConnectionStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var parts = config.ConnectionString.Split(':');
            var host = parts[0];
            var port = parts.Length > 1 ? int.Parse(parts[1]) : 587;

            var client = new TcpClient();
            await client.ConnectAsync(host, port, ct);

            if (!client.Connected)
                throw new InvalidOperationException($"Failed to connect to SMTP server at {host}:{port}");

            var info = new Dictionary<string, object>
            {
                ["host"] = host,
                ["port"] = port,
                ["protocol"] = "SMTP",
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
                StatusMessage: isHealthy ? "SMTP server connected" : "SMTP server disconnected",
                Latency: TimeSpan.Zero,
                CheckedAt: DateTimeOffset.UtcNow));
        }
    }
}
