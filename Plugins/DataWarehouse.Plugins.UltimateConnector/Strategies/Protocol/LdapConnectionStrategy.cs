using System;
using System.Collections.Generic;
using System.IO;
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
            "Connects to LDAP directory services for user and resource directory queries";

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
            await client.ConnectAsync(host, port, ct);

            if (!client.Connected)
                throw new InvalidOperationException($"Failed to connect to LDAP server at {host}:{port}");

            var stream = client.GetStream();

            // P2-2134: Perform a real LDAP anonymous BindRequest (RFC 4511 §4.2).
            // BindRequest PDU (BER-encoded):
            //   LDAPMessage ::= SEQUENCE { messageID INTEGER, protocolOp BindRequest }
            //   BindRequest ::= [APPLICATION 0] SEQUENCE { version INTEGER (1..127), name LDAPDN, authentication AuthenticationChoice }
            //   Anonymous bind: version=3, name="", authentication=simple("")
            var bindRequest = BuildAnonymousBindRequest(messageId: 1);
            await stream.WriteAsync(bindRequest, 0, bindRequest.Length, ct).ConfigureAwait(false);

            // Read BindResponse — minimum 7 bytes (SEQUENCE tag + length + MessageID + BindResponse tag + length + resultCode + matchedDN + diagnosticMessage)
            var responseBuffer = new byte[256];
            var bytesRead = await ReadLdapMessageAsync(stream, responseBuffer, ct).ConfigureAwait(false);

            // Validate BindResponse resultCode (offset varies; we parse the BER envelope minimally)
            ParseAndValidateBindResponse(responseBuffer, bytesRead);

            var info = new Dictionary<string, object>
            {
                ["host"] = host,
                ["port"] = port,
                ["protocol"] = useTls ? "LDAPS" : "LDAP",
                ["tls"] = useTls,
                ["bind"] = "anonymous",
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
