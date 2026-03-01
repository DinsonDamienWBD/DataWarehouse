using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Protocol
{
    /// <summary>
    /// Connection strategy for SMTP mail servers.
    /// Establishes a TCP connection, reads the server banner, sends EHLO,
    /// and (on port 587) confirms STARTTLS capability per RFC 3207.
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
            "Connects to SMTP mail servers. Performs EHLO handshake and STARTTLS capability check on port 587.";

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
            // P2-2132: Use IPv6-safe host/port parser (handles [::1]:587 bracket notation).
            var (host, port) = ParseHostPort(
                config.ConnectionString ?? throw new ArgumentException("Connection string required"),
                587, "SMTP");

            var client = new TcpClient();
            await client.ConnectAsync(host, port, ct).ConfigureAwait(false);

            if (!client.Connected)
                throw new InvalidOperationException($"Failed to connect to SMTP server at {host}:{port}");

            var stream = client.GetStream();

            // P2-2134: Perform SMTP banner read + EHLO handshake + STARTTLS capability check (RFC 3207).
            var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };

            // Read greeting banner (220 ...)
            var banner = await reader.ReadLineAsync(ct).ConfigureAwait(false)
                         ?? throw new InvalidOperationException("SMTP server sent empty banner");
            if (!banner.StartsWith("220", StringComparison.Ordinal))
                throw new InvalidOperationException($"SMTP server returned unexpected greeting: {banner}");

            // Send EHLO
            await writer.WriteLineAsync("EHLO datawarehouse-probe").ConfigureAwait(false);

            // Read EHLO response (may be multi-line: "250-..." lines followed by "250 ...")
            bool starttlsAdvertised = false;
            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
            {
                if (line.Length > 4 && line.Substring(4).Equals("STARTTLS", StringComparison.OrdinalIgnoreCase))
                    starttlsAdvertised = true;
                // End of multi-line EHLO response: "250 " (space, not dash)
                if (line.Length >= 4 && line[3] == ' ')
                    break;
                if (!line.StartsWith("250", StringComparison.Ordinal))
                    throw new InvalidOperationException($"SMTP EHLO failed: {line}");
            }

            // Send QUIT to cleanly end the probe session
            try { await writer.WriteLineAsync("QUIT").ConfigureAwait(false); } catch { /* best-effort */ }

            var info = new Dictionary<string, object>
            {
                ["host"] = host,
                ["port"] = port,
                ["protocol"] = "SMTP",
                ["starttls_advertised"] = starttlsAdvertised,
                ["banner"] = banner,
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(client, info);
        }

        /// <inheritdoc/>
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<TcpClient>();
            if (!client.Connected)
                return false;
            try
            {
                await client.GetStream().WriteAsync(Array.Empty<byte>(), 0, 0, ct).ConfigureAwait(false);
                return true;
            }
            catch (IOException) { return false; }
            catch (SocketException) { return false; }
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
            var isHealthy = await TestCoreAsync(handle, ct).ConfigureAwait(false);

            return new ConnectionHealth(
                IsHealthy: isHealthy,
                StatusMessage: isHealthy ? "SMTP server connected" : "SMTP server disconnected",
                Latency: TimeSpan.Zero,
                CheckedAt: DateTimeOffset.UtcNow);
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
