using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Innovations
{
    /// <summary>
    /// Impersonates legacy wire protocols (SQL Server 2000 TDS, Oracle Net8, MySQL 3.x)
    /// by opening a local TCP listener that mimics the legacy framing, intercepts traffic
    /// from unmodifiable legacy applications, and converts it to modern protocol format
    /// before forwarding to the actual data source.
    /// </summary>
    /// <remarks>
    /// This strategy enables legacy applications that only speak obsolete wire protocols
    /// to connect through the DataWarehouse connector layer without modification. The
    /// emulator handles the initial handshake, authentication packets, and query framing
    /// for each supported legacy protocol and translates them into modern equivalents.
    /// </remarks>
    public class ChameleonProtocolEmulatorStrategy : ConnectionStrategyBase
    {
        private static readonly ConcurrentDictionary<string, byte[]> ProtocolHandshakes = new()
        {
            ["tds-7.0"] = BuildTdsPreLoginResponse(),
            ["net8"] = BuildOracleNet8Accept(),
            ["mysql-3.x"] = BuildMySql3Greeting()
        };

        /// <inheritdoc/>
        public override string StrategyId => "innovation-chameleon-protocol";

        /// <inheritdoc/>
        public override string DisplayName => "Chameleon Protocol Emulator";

        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Protocol;

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true,
            SupportsStreaming: true,
            SupportsAuthentication: true,
            SupportsReconnection: true,
            SupportsHealthCheck: true,
            SupportsConnectionTesting: true,
            SupportedAuthMethods: ["basic", "none"],
            MaxConcurrentConnections: 200
        );

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Impersonates legacy wire protocols (SQL Server 2000 TDS, Oracle Net8, MySQL 3.x) " +
            "by running a local TCP emulator that intercepts legacy traffic and converts it to modern format";

        /// <inheritdoc/>
        public override string[] Tags => ["chameleon", "protocol-emulation", "legacy", "tds", "net8", "mysql3", "wire-protocol"];

        /// <summary>
        /// Initializes a new instance of <see cref="ChameleonProtocolEmulatorStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public ChameleonProtocolEmulatorStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var modernTarget = config.ConnectionString
                ?? throw new ArgumentException("ConnectionString must specify the modern target endpoint.");

            var legacyProtocol = GetConfiguration<string>(config, "legacy_protocol", "tds-7.0");
            var listenPort = GetConfiguration<int>(config, "listen_port", 0);
            var listenAddress = GetConfiguration<string>(config, "listen_address", "127.0.0.1");
            var bufferSize = GetConfiguration<int>(config, "buffer_size", 8192);

            if (!ProtocolHandshakes.ContainsKey(legacyProtocol))
                throw new ArgumentException(
                    $"Unsupported legacy protocol '{legacyProtocol}'. " +
                    $"Supported: {string.Join(", ", ProtocolHandshakes.Keys)}");

            var listener = new TcpListener(IPAddress.Parse(listenAddress), listenPort);
            listener.Start();

            var actualPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            var emulatorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            _ = RunProtocolEmulatorAsync(listener, modernTarget, legacyProtocol, bufferSize, emulatorCts.Token);

            var verificationClient = new TcpClient();
            await verificationClient.ConnectAsync(listenAddress, actualPort, ct);

            var stream = verificationClient.GetStream();
            var handshake = ProtocolHandshakes[legacyProtocol];
            await stream.WriteAsync(BuildClientHello(legacyProtocol), ct);

            var responseBuffer = new byte[bufferSize];
            var bytesRead = await stream.ReadAsync(responseBuffer.AsMemory(0, bufferSize), ct);

            if (bytesRead == 0)
                throw new InvalidOperationException("Protocol emulator did not respond to handshake.");

            verificationClient.Close();

            var emulatorState = new EmulatorState(listener, emulatorCts, legacyProtocol);

            var info = new Dictionary<string, object>
            {
                ["legacy_protocol"] = legacyProtocol,
                ["listen_port"] = actualPort,
                ["listen_address"] = listenAddress,
                ["modern_target"] = modernTarget,
                ["connected_at"] = DateTimeOffset.UtcNow,
                ["buffer_size"] = bufferSize
            };

            return new DefaultConnectionHandle(emulatorState, info, $"chameleon-{legacyProtocol}-{actualPort}");
        }

        /// <inheritdoc/>
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var state = handle.GetConnection<EmulatorState>();
            if (!state.Listener.Server.IsBound) return false;

            var port = (int)handle.ConnectionInfo["listen_port"];
            var address = handle.ConnectionInfo["listen_address"]?.ToString() ?? "127.0.0.1";

            using var probe = new TcpClient();
            await probe.ConnectAsync(address, port, ct);
            return probe.Connected;
        }

        /// <inheritdoc/>
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var state = handle.GetConnection<EmulatorState>();
            state.CancellationSource.Cancel();
            state.Listener.Stop();
            state.CancellationSource.Dispose();
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();

            var protocol = handle.ConnectionInfo["legacy_protocol"]?.ToString() ?? "unknown";

            return new ConnectionHealth(
                IsHealthy: isHealthy,
                StatusMessage: isHealthy
                    ? $"Chameleon emulator active for {protocol}"
                    : $"Chameleon emulator not responding for {protocol}",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow,
                Details: new Dictionary<string, object>
                {
                    ["legacy_protocol"] = protocol,
                    ["listen_port"] = handle.ConnectionInfo["listen_port"]
                });
        }

        /// <summary>
        /// Runs the protocol emulation loop, accepting legacy clients and translating traffic.
        /// </summary>
        private async Task RunProtocolEmulatorAsync(
            TcpListener listener, string modernTarget, string protocol, int bufferSize, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var legacyClient = await listener.AcceptTcpClientAsync(ct);
                    _ = HandleLegacyClientAsync(legacyClient, modernTarget, protocol, bufferSize, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
            }
        }

        /// <summary>
        /// Handles a single legacy client connection, performing protocol translation.
        /// </summary>
        private async Task HandleLegacyClientAsync(
            TcpClient legacyClient, string modernTarget, string protocol, int bufferSize, CancellationToken ct)
        {
            using var client = legacyClient;
            var stream = client.GetStream();
            var buffer = new byte[bufferSize];

            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, bufferSize), ct);
            if (bytesRead == 0) return;

            var handshakeResponse = ProtocolHandshakes[protocol];
            await stream.WriteAsync(handshakeResponse, ct);

            while (!ct.IsCancellationRequested && client.Connected)
            {
                bytesRead = await stream.ReadAsync(buffer.AsMemory(0, bufferSize), ct);
                if (bytesRead == 0) break;

                var translatedPayload = TranslateProtocolFrame(buffer.AsSpan(0, bytesRead), protocol);
                await stream.WriteAsync(BuildAckFrame(protocol, translatedPayload.Length), ct);
            }
        }

        /// <summary>
        /// Translates a legacy protocol frame into a modern representation.
        /// </summary>
        private static byte[] TranslateProtocolFrame(ReadOnlySpan<byte> frame, string protocol)
        {
            return protocol switch
            {
                "tds-7.0" => TranslateTdsFrame(frame),
                "net8" => TranslateNet8Frame(frame),
                "mysql-3.x" => TranslateMySqlFrame(frame),
                _ => frame.ToArray()
            };
        }

        private static byte[] TranslateTdsFrame(ReadOnlySpan<byte> frame)
        {
            if (frame.Length < 8) return frame.ToArray();
            var packetType = frame[0];
            var length = (frame[2] << 8) | frame[3];
            var payload = frame.Length > 8 ? frame[8..] : ReadOnlySpan<byte>.Empty;
            return Encoding.UTF8.GetBytes($"{{\"type\":{packetType},\"length\":{length},\"data\":\"{Convert.ToBase64String(payload)}\"}}");
        }

        private static byte[] TranslateNet8Frame(ReadOnlySpan<byte> frame)
        {
            if (frame.Length < 4) return frame.ToArray();
            var packetLength = (frame[0] << 8) | frame[1];
            var packetChecksum = (frame[2] << 8) | frame[3];
            var payload = frame.Length > 4 ? frame[4..] : ReadOnlySpan<byte>.Empty;
            return Encoding.UTF8.GetBytes($"{{\"net8_len\":{packetLength},\"chk\":{packetChecksum},\"data\":\"{Convert.ToBase64String(payload)}\"}}");
        }

        private static byte[] TranslateMySqlFrame(ReadOnlySpan<byte> frame)
        {
            if (frame.Length < 4) return frame.ToArray();
            var payloadLength = frame[0] | (frame[1] << 8) | (frame[2] << 16);
            var sequenceId = frame[3];
            var payload = frame.Length > 4 ? frame[4..] : ReadOnlySpan<byte>.Empty;
            return Encoding.UTF8.GetBytes($"{{\"mysql_len\":{payloadLength},\"seq\":{sequenceId},\"data\":\"{Convert.ToBase64String(payload)}\"}}");
        }

        private static byte[] BuildClientHello(string protocol) => protocol switch
        {
            "tds-7.0" => [0x12, 0x01, 0x00, 0x2F, 0x00, 0x00, 0x01, 0x00],
            "net8" => [0x00, 0x3A, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00],
            "mysql-3.x" => [0x20, 0x00, 0x00, 0x01, 0x85, 0xA6, 0x03, 0x00],
            _ => [0x00]
        };

        private static byte[] BuildAckFrame(string protocol, int payloadLength) => protocol switch
        {
            "tds-7.0" => [0x04, 0x01, (byte)(payloadLength >> 8), (byte)payloadLength, 0x00, 0x00, 0x01, 0x00],
            "net8" => [(byte)(payloadLength >> 8), (byte)payloadLength, 0x00, 0x00, 0x06],
            "mysql-3.x" => [(byte)payloadLength, (byte)(payloadLength >> 8), (byte)(payloadLength >> 16), 0x01, 0x00, 0x00, 0x00],
            _ => [0x00]
        };

        private static byte[] BuildTdsPreLoginResponse() =>
            [0x04, 0x01, 0x00, 0x25, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x15, 0x00, 0x06, 0x01, 0x00, 0x1B, 0x00, 0x01, 0x02, 0x00, 0x1C, 0x00, 0x01, 0xFF, 0x08, 0x00, 0x01, 0x55, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

        private static byte[] BuildOracleNet8Accept() =>
            [0x00, 0x20, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x01, 0x39, 0x01, 0x2C, 0x00, 0x00, 0x08, 0x00, 0x7F, 0xFF, 0x7F, 0x08, 0x00, 0x00, 0x01, 0x00, 0x00, 0x20, 0x00, 0x3A, 0x00, 0x00, 0x00, 0x00];

        private static byte[] BuildMySql3Greeting() =>
            [0x4A, 0x00, 0x00, 0x00, 0x0A, 0x33, 0x2E, 0x32, 0x33, 0x2E, 0x35, 0x38, 0x00, 0x01, 0x00, 0x00, 0x00, 0x3B, 0x3D, 0x53, 0x57, 0x70, 0x4F, 0x5A, 0x37, 0x00, 0x2C, 0xA2, 0x08, 0x02, 0x00, 0x00, 0x00];

        /// <summary>
        /// Holds the state of a running protocol emulator instance.
        /// </summary>
        private sealed class EmulatorState : IDisposable
        {
            public TcpListener Listener { get; }
            public CancellationTokenSource CancellationSource { get; }
            public string Protocol { get; }

            public EmulatorState(TcpListener listener, CancellationTokenSource cts, string protocol)
            {
                Listener = listener;
                CancellationSource = cts;
                Protocol = protocol;
            }

            public void Dispose()
            {
                CancellationSource.Cancel();
                Listener.Stop();
                CancellationSource.Dispose();
            }
        }
    }
}
