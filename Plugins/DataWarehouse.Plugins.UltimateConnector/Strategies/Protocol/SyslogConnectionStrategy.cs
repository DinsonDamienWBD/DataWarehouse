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
    /// Connection strategy for Syslog servers.
    /// Tests connectivity via UDP connection to port 514.
    /// </summary>
    public class SyslogConnectionStrategy : ConnectionStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "syslog";

        /// <inheritdoc/>
        public override string DisplayName => "Syslog";

        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Protocol;

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new();

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Connects to Syslog servers for system logging and event transmission";

        /// <inheritdoc/>
        public override string[] Tags => new[] { "syslog", "logging", "events", "protocol", "udp" };

        /// <summary>
        /// Initializes a new instance of <see cref="SyslogConnectionStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger.</param>
        public SyslogConnectionStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var parts = (config.ConnectionString ?? throw new ArgumentException("Connection string required")).Split(':');
            var host = parts[0];
            int port;
            if (parts.Length > 1)
            {
                if (!int.TryParse(parts[1], out port) || port < 1 || port > 65535)
                    throw new ArgumentException($"Invalid port '{parts[1]}' in Syslog connection string. Expected a number between 1 and 65535.", nameof(config));
            }
            else
            {
                port = 514;
            }

            var client = new UdpClient();
            client.Connect(host, port);

            // Send a minimal RFC 5424 syslog test message to verify network path
            var testMsg = System.Text.Encoding.UTF8.GetBytes("<14>1 - - - - - - DataWarehouse Syslog connectivity probe");
            try
            {
                await client.SendAsync(testMsg, testMsg.Length).ConfigureAwait(false);
            }
            catch
            {
                // UDP send failure is non-fatal — syslog servers may be one-way
            }

            var info = new Dictionary<string, object>
            {
                ["host"] = host,
                ["port"] = port,
                ["protocol"] = "Syslog/UDP",
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(client, info);
        }

        /// <inheritdoc/>
        protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            // UdpClient.Client.Connected is always true after Connect() — not a liveness indicator.
            // Verify the socket is open and bound (not disposed/closed).
            var client = handle.GetConnection<UdpClient>();
            return Task.FromResult(client.Client != null && client.Client.IsBound);
        }

        /// <inheritdoc/>
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<UdpClient>();
            client.Close();
            client.Dispose();
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<UdpClient>();
            var isHealthy = client.Client != null && client.Client.IsBound;

            return Task.FromResult(new ConnectionHealth(
                IsHealthy: isHealthy,
                StatusMessage: isHealthy ? "Syslog server socket bound" : "Syslog server socket closed",
                Latency: TimeSpan.Zero,
                CheckedAt: DateTimeOffset.UtcNow));
        }
    }
}
