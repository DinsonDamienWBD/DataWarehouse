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
            // P2-2132: Use IPv6-safe host/port parser (handles [::1]:514 bracket notation).
            var (host, port) = ParseHostPort(
                config.ConnectionString ?? throw new ArgumentException("Connection string required"),
                514, "Syslog");

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
        // P2-2132: IPv6-safe host/port parser. Handles "[::1]:port", "host:port", and "host" forms.
        private static (string host, int port) ParseHostPort(string cs, int defaultPort, string protocol)
        {
            cs = cs.Trim();
            if (cs.StartsWith('['))
            {
                // IPv6 bracket notation: [addr]:port or [addr]
                var closeBracket = cs.IndexOf(']');
                if (closeBracket < 0)
                    throw new ArgumentException($"Malformed IPv6 address in {protocol} connection string: missing ']'.", nameof(cs));
                var ipv6Host = cs.Substring(1, closeBracket - 1);
                if (closeBracket + 1 < cs.Length && cs[closeBracket + 1] == ':')
                {
                    var portStr = cs.Substring(closeBracket + 2);
                    if (!int.TryParse(portStr, out var p6) || p6 < 1 || p6 > 65535)
                        throw new ArgumentException($"Invalid port '{portStr}' in {protocol} connection string.", nameof(cs));
                    return (ipv6Host, p6);
                }
                return (ipv6Host, defaultPort);
            }
            var colonIdx = cs.IndexOf(':');
            if (colonIdx >= 0)
            {
                var portPart = cs.Substring(colonIdx + 1);
                if (!int.TryParse(portPart, out var p) || p < 1 || p > 65535)
                    throw new ArgumentException($"Invalid port '{portPart}' in {protocol} connection string. Expected a number between 1 and 65535.", nameof(cs));
                return (cs.Substring(0, colonIdx), p);
            }
            return (cs, defaultPort);
        }
    }
}
