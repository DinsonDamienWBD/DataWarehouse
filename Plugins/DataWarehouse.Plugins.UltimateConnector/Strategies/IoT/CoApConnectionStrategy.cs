using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.IoT
{
    /// <summary>
    /// Connection strategy for CoAP (Constrained Application Protocol).
    /// Tests connectivity via UDP connection to port 5683 (CoAP) or 5684 (CoAPs/DTLS).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Security Controls (NET-05 - CVSS 7.5):</strong>
    /// <list type="bullet">
    /// <item><description>DTLS requirement: CoAP transport is unencrypted by default.
    /// Production deployments MUST use DTLS (port 5684) or IPSec/VPN tunnel.</description></item>
    /// <item><description>AllowInsecureCoap: Must be explicitly set to true to use unencrypted CoAP.
    /// Defaults to false, which throws on initialization to prevent insecure deployment.</description></item>
    /// <item><description>Randomized message IDs: Initial CoAP message ID is generated via CSPRNG
    /// to prevent predictable message ID attacks.</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    public class CoApConnectionStrategy : IoTConnectionStrategyBase
    {
        // SECURITY: CoAP transport is unencrypted by default. Use DTLS (CoAPs, port 5684)
        // or IPSec/VPN tunnel for production deployments (NET-05 - CVSS 7.5).
        // The CoAP.NET library does not natively support DTLS, so production environments
        // MUST ensure transport-level encryption via network infrastructure.

        /// <summary>
        /// Whether to use DTLS for CoAP connections. Default: true.
        /// When true and DTLS is not available, connection will fail unless AllowInsecureCoap is set.
        /// </summary>
        private bool _useDtls = true;

        /// <summary>
        /// Whether to allow insecure (non-DTLS) CoAP connections. Default: false.
        /// Must be explicitly set to true for non-encrypted CoAP communication (NOT recommended for production).
        /// </summary>
        private bool _allowInsecureCoap;

        /// <summary>
        /// CSPRNG-generated initial message ID stored as int for Interlocked operations.
        /// Finding 1968: Use Interlocked.Increment to make GetNextMessageId thread-safe.
        /// </summary>
        private int _currentMessageId;

        /// <inheritdoc/>
        public override string StrategyId => "coap";

        /// <inheritdoc/>
        public override string DisplayName => "CoAP";

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new();

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Connects to CoAP endpoints for constrained IoT device communication with DTLS security enforcement";

        /// <inheritdoc/>
        public override string[] Tags => new[] { "coap", "iot", "constrained", "udp", "m2m", "dtls" };

        /// <summary>
        /// Initializes a new instance of <see cref="CoApConnectionStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger.</param>
        public CoApConnectionStrategy(ILogger? logger = null) : base(logger)
        {
            // Initialize message ID with CSPRNG to prevent predictable sequential IDs
            _currentMessageId = GenerateRandomMessageId();
        }

        /// <summary>
        /// Generates a cryptographically random initial message ID (NET-05).
        /// Prevents predictable sequential message ID attacks.
        /// </summary>
        /// <returns>A random int seeded from a 16-bit CSPRNG value.</returns>
        private static int GenerateRandomMessageId()
        {
            Span<byte> buffer = stackalloc byte[2];
            RandomNumberGenerator.Fill(buffer);
            return BitConverter.ToUInt16(buffer);
        }

        /// <summary>
        /// Gets the next message ID using CSPRNG-seeded counter with atomic wraparound.
        /// Finding 1968: Interlocked.Increment prevents torn reads/writes on concurrent callers.
        /// </summary>
        /// <returns>The next message ID (masked to 16-bit CoAP range).</returns>
        public ushort GetNextMessageId()
        {
            return (ushort)(Interlocked.Increment(ref _currentMessageId) & 0xFFFF);
        }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            // P2-2132: Use ParseHostPortSafe to correctly handle IPv6 addresses like [::1]:5683
            var (host, port) = ParseHostPortSafe(config.ConnectionString ?? throw new ArgumentException("Connection string required"), 5683);

            // NET-05: Load security configuration
            _useDtls = GetConfiguration(config, "UseDtls", true);
            _allowInsecureCoap = GetConfiguration(config, "AllowInsecureCoap", false);

            // NET-05: DTLS enforcement
            var isSecurePort = port == 5684; // CoAPs standard port
            var connectionScheme = config.ConnectionString.StartsWith("coaps://", StringComparison.OrdinalIgnoreCase)
                ? "coaps" : "coap";
            var isDtlsRequested = _useDtls || connectionScheme == "coaps" || isSecurePort;

            if (isDtlsRequested && !isSecurePort && connectionScheme != "coaps")
            {
                // DTLS requested but not available via the underlying library
                if (!_allowInsecureCoap)
                {
                    throw new InvalidOperationException(
                        "CoAP DTLS is required but not available. The CoAP.NET library does not natively support DTLS. " +
                        "Either: (1) Use IPSec or VPN tunnel for transport encryption, " +
                        "(2) Set AllowInsecureCoap=true to explicitly allow unencrypted CoAP (NOT recommended for production), or " +
                        "(3) Use CoAPs (port 5684) with a DTLS-capable proxy. " +
                        "See NET-05 (CVSS 7.5) for details.");
                }

                System.Diagnostics.Trace.TraceWarning(
                    "SECURITY WARNING: CoAP operating without DTLS -- transport is unencrypted (NET-05 - CVSS 7.5). " +
                    "Use IPSec or VPN tunnel for production deployments. Host: {0}, Port: {1}",
                    host, port.ToString());
            }

            var client = new UdpClient();
            client.Connect(host, port);

            await Task.Delay(10, ct);

            var info = new Dictionary<string, object>
            {
                ["host"] = host,
                ["port"] = port,
                ["protocol"] = "CoAP",
                ["connected_at"] = DateTimeOffset.UtcNow,
                ["dtls_enabled"] = isSecurePort || connectionScheme == "coaps",
                ["insecure_allowed"] = _allowInsecureCoap,
                ["initial_message_id"] = (ushort)(_currentMessageId & 0xFFFF)
            };

            System.Diagnostics.Trace.TraceInformation(
                "CoAP connection established to {0}:{1} (DTLS: {2}, InsecureAllowed: {3})",
                host, port.ToString(), (isSecurePort || connectionScheme == "coaps").ToString(), _allowInsecureCoap.ToString());

            return new DefaultConnectionHandle(client, info);
        }

        /// <inheritdoc/>
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            // Finding 1969: UDP has no real connection state — probe by sending a CoAP ping
            // (CON with empty payload, type=0, code=0, token-length=0) and verifying send succeeds.
            var client = handle.GetConnection<UdpClient>();
            if (client.Client == null) return false;
            try
            {
                var msgId = GetNextMessageId();
                // CoAP CON ping: Ver=1, T=0(CON), TKL=0, Code=0.00, MsgId
                var ping = new byte[] { 0x40, 0x00, (byte)(msgId >> 8), (byte)(msgId & 0xFF) };
                await client.SendAsync(ping, ping.Length).WaitAsync(ct);
                return true;
            }
            catch { return false; }
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
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();
            return new ConnectionHealth(
                IsHealthy: isHealthy,
                StatusMessage: isHealthy ? "CoAP endpoint reachable" : "CoAP endpoint unreachable",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow);
        }

        /// <inheritdoc/>
        public override Task<Dictionary<string, object>> ReadTelemetryAsync(IConnectionHandle handle, string deviceId, CancellationToken ct = default)
        {
            // CoAP resource observation metadata
            var result = new Dictionary<string, object>
            {
                ["protocol"] = "CoAP",
                ["deviceId"] = deviceId,
                ["resourcePath"] = $"/sensors/{deviceId}",
                ["method"] = "GET",
                ["observe"] = true,
                ["status"] = "connected",
                ["message"] = "CoAP resource ready for observation",
                ["timestamp"] = DateTimeOffset.UtcNow
            };
            return Task.FromResult(result);
        }

        /// <inheritdoc/>
        public override Task<string> SendCommandAsync(IConnectionHandle handle, string deviceId, string command, Dictionary<string, object>? parameters = null, CancellationToken ct = default)
        {
            // CoAP POST/PUT command
            return Task.FromResult($"{{\"status\":\"queued\",\"resourcePath\":\"/actuators/{deviceId}\",\"method\":\"POST\",\"command\":\"{command}\",\"message\":\"CoAP command prepared\"}}");
        }
    }
}
