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
            // P2-2132: Use IPv6-safe host/port parser (handles [::1]:587 bracket notation).
            var (host, port) = ParseHostPort(
                config.ConnectionString ?? throw new ArgumentException("Connection string required"),
                587, "SMTP");

            // P2-2134: Perform a real SMTP handshake (EHLO/STARTTLS) instead of bare TCP.
            var client = new TcpClient();
            await client.ConnectAsync(host, port, ct).ConfigureAwait(false);

            if (!client.Connected)
                throw new InvalidOperationException($"Failed to connect to SMTP server at {host}:{port}");

            System.IO.Stream stream = client.GetStream();
            using var reader = new System.IO.StreamReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
            using var writer = new System.IO.StreamWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true) { AutoFlush = true };

            // Read server greeting (220 ...)
            var greeting = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (greeting == null || !greeting.StartsWith("220", StringComparison.Ordinal))
                throw new InvalidOperationException($"SMTP server did not send 220 greeting: {greeting}");

            // Send EHLO and read extended capability list
            await writer.WriteLineAsync($"EHLO {System.Net.Dns.GetHostName()}").ConfigureAwait(false);
            string? ehloLine;
            bool supportsStartTls = false;
            while ((ehloLine = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
            {
                if (ehloLine.Contains("STARTTLS", StringComparison.OrdinalIgnoreCase))
                    supportsStartTls = true;
                // Last EHLO response line has a space after the code (e.g. "250 SIZE 35882577")
                if (ehloLine.Length >= 4 && ehloLine[3] == ' ')
                    break;
            }

            // Upgrade to TLS if the server advertises STARTTLS (RFC 3207)
            if (supportsStartTls && port == 587)
            {
                await writer.WriteLineAsync("STARTTLS").ConfigureAwait(false);
                var tlsResponse = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (tlsResponse != null && tlsResponse.StartsWith("220", StringComparison.Ordinal))
                {
                    var sslStream = new System.Net.Security.SslStream(stream, leaveInnerStreamOpen: false);
                    await sslStream.AuthenticateAsClientAsync(host, null,
                        System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                        checkCertificateRevocation: true);
                    stream = sslStream; // replace plain stream with TLS stream
                }
            }

            var info = new Dictionary<string, object>
            {
                ["host"] = host,
                ["port"] = port,
                ["protocol"] = "SMTP",
                ["starttls"] = supportsStartTls,
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
            catch (System.IO.IOException) { return false; }
            catch (System.Net.Sockets.SocketException) { return false; }
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
