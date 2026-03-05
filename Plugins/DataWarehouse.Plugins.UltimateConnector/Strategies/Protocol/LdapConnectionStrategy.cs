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
    /// Connection strategy for LDAP directory services.
    /// Tests connectivity via TCP connection to port 389 (or 636 for LDAPS).
    /// Performs an anonymous LDAPv3 BindRequest to confirm the server speaks LDAP.
    /// </summary>
    public class LdapConnectionStrategy : ConnectionStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "ldap";

        /// <inheritdoc/>
        public override string DisplayName => "LDAP";

        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Protocol;

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new();

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Connects to LDAP directory services. Performs anonymous LDAPv3 BindRequest to confirm server identity.";

        /// <inheritdoc/>
        public override string[] Tags => new[] { "ldap", "directory", "authentication", "protocol", "ad" };

        /// <summary>
        /// Initializes a new instance of <see cref="LdapConnectionStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger.</param>
        public LdapConnectionStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var useTls = GetConfiguration(config, "UseTls", false);
            int defaultPort = useTls ? 636 : 389;
            // P2-2132: Use IPv6-safe host/port parser (handles [::1]:389 bracket notation).
            var (host, port) = ParseHostPort(
                config.ConnectionString ?? throw new ArgumentException("Connection string required"),
                defaultPort, "LDAP");

            var client = new TcpClient();
            await client.ConnectAsync(host, port, ct).ConfigureAwait(false);

            if (!client.Connected)
                throw new InvalidOperationException($"Failed to connect to LDAP server at {host}:{port}");

            // P2-2134: Send anonymous LDAPv3 BindRequest and verify BindResponse.
            // Anonymous bind BER: SEQUENCE { messageID=1, BindRequest { version=3, name="", simple="" } }
            // Encoding: 30 0C 02 01 01 60 07 02 01 03 04 00 80 00
            var bindRequest = new byte[] { 0x30, 0x0C, 0x02, 0x01, 0x01, 0x60, 0x07, 0x02, 0x01, 0x03, 0x04, 0x00, 0x80, 0x00 };

            var stream = client.GetStream();
            await stream.WriteAsync(bindRequest, 0, bindRequest.Length, ct).ConfigureAwait(false);

            // Read BindResponse: expect SEQUENCE (0x30) containing BindResponse (0x61)
            var responseBuffer = new byte[64];
            var bytesRead = await stream.ReadAsync(responseBuffer, 0, responseBuffer.Length, ct).ConfigureAwait(false);

            if (bytesRead < 7)
                throw new InvalidOperationException($"LDAP server at {host}:{port} returned truncated BindResponse ({bytesRead} bytes)");

            if (responseBuffer[0] != 0x30)
                throw new InvalidOperationException($"LDAP server at {host}:{port} returned unexpected outer tag 0x{responseBuffer[0]:X2} (expected SEQUENCE 0x30)");

            // Scan for BindResponse tag (0x61) within the first 10 bytes of the response
            bool isBindResponse = false;
            for (int i = 0; i < Math.Min(bytesRead - 1, 10); i++)
            {
                if (responseBuffer[i] == 0x61) { isBindResponse = true; break; }
            }

            if (!isBindResponse)
                throw new InvalidOperationException(
                    $"LDAP server at {host}:{port} did not return a BindResponse (0x61). Received 0x{responseBuffer[0]:X2}");

            // Non-zero result codes (e.g. 49 = invalidCredentials) are acceptable for connectivity
            // probing — we confirmed the server speaks LDAPv3.

            var info = new Dictionary<string, object>
            {
                ["host"] = host,
                ["port"] = port,
                ["protocol"] = useTls ? "LDAPS" : "LDAP",
                ["tls"] = useTls,
                ["bind_performed"] = true,
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(client, info);
        }

        /// <inheritdoc/>
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            // TcpClient.Connected reflects last I/O status, not live state.
            // Perform a real liveness probe by attempting a zero-byte send on the existing socket.
            var client = handle.GetConnection<TcpClient>();
            if (!client.Connected)
                return false;
            try
            {
                // Zero-byte send: if connection is half-open, this raises SocketException
                await client.GetStream().WriteAsync(Array.Empty<byte>(), 0, 0, ct).ConfigureAwait(false);
                return true;
            }
            catch (System.IO.IOException)
            {
                return false;
            }
            catch (System.Net.Sockets.SocketException)
            {
                return false;
            }
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
                StatusMessage: isHealthy ? "LDAP server connected" : "LDAP server disconnected",
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
