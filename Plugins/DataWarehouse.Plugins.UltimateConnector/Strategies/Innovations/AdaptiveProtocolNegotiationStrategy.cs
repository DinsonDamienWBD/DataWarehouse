using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Innovations
{
    /// <summary>
    /// Adaptive protocol negotiation strategy that probes an endpoint with multiple
    /// wire protocols (HTTP/1.1, HTTP/2, HTTP/3, gRPC, WebSocket, REST) and selects
    /// the optimal protocol based on measured latency, throughput, and payload characteristics.
    /// </summary>
    /// <remarks>
    /// During connection establishment, the strategy performs a rapid probe sequence:
    /// <list type="number">
    ///   <item>Send OPTIONS/HEAD requests to detect supported protocols</item>
    ///   <item>Measure round-trip latency for each supported protocol</item>
    ///   <item>Evaluate payload size and structure to determine best fit</item>
    ///   <item>Select the protocol with the best composite score</item>
    /// </list>
    /// The negotiated protocol is stored in the connection metadata and used for all
    /// subsequent communication through the handle.
    /// </remarks>
    public class AdaptiveProtocolNegotiationStrategy : ConnectionStrategyBase
    {
        private static readonly string[] ProbeProtocols = ["http/1.1", "h2", "h3", "websocket", "grpc"];

        /// <inheritdoc/>
        public override string StrategyId => "innovation-adaptive-protocol";

        /// <inheritdoc/>
        public override string DisplayName => "Adaptive Protocol Negotiation";

        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Protocol;

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true,
            SupportsStreaming: true,
            SupportsSsl: true,
            SupportsCompression: true,
            SupportsHealthCheck: true,
            SupportsConnectionTesting: true,
            SupportsReconnection: true,
            MaxConcurrentConnections: 200
        );

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Single endpoint connection that auto-negotiates the optimal wire protocol " +
            "(HTTP/1.1, HTTP/2, HTTP/3, gRPC, WebSocket) based on network conditions and payload";

        /// <inheritdoc/>
        public override string[] Tags => ["adaptive", "protocol", "negotiation", "http2", "http3", "grpc", "websocket", "auto"];

        /// <summary>
        /// Initializes a new instance of <see cref="AdaptiveProtocolNegotiationStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public AdaptiveProtocolNegotiationStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var endpoint = config.ConnectionString
                ?? throw new ArgumentException("Endpoint URL is required in ConnectionString.");

            var probeTimeoutMs = GetConfiguration<int>(config, "probe_timeout_ms", 3000);
            var preferredProtocol = GetConfiguration<string>(config, "preferred_protocol", "auto");
            var payloadHint = GetConfiguration<string>(config, "payload_hint", "mixed");

            var baseUri = new Uri(endpoint);
            var probeResults = new Dictionary<string, ProbeResult>();

            if (preferredProtocol != "auto" && ProbeProtocols.Contains(preferredProtocol))
            {
                var result = await ProbeProtocolAsync(baseUri, preferredProtocol, probeTimeoutMs, ct);
                if (result.IsSupported)
                    probeResults[preferredProtocol] = result;
            }
            else
            {
                var probeTasks = ProbeProtocols.Select(async protocol =>
                {
                    try
                    {
                        return (protocol, result: await ProbeProtocolAsync(baseUri, protocol, probeTimeoutMs, ct));
                    }
                    catch
                    {
                        return (protocol, result: new ProbeResult(false, TimeSpan.MaxValue, 0));
                    }
                });

                var results = await Task.WhenAll(probeTasks);
                foreach (var (protocol, result) in results)
                {
                    if (result.IsSupported)
                        probeResults[protocol] = result;
                }
            }

            if (probeResults.Count == 0)
                throw new InvalidOperationException(
                    $"No supported protocols found at endpoint {endpoint}. " +
                    "Probed: " + string.Join(", ", ProbeProtocols));

            var selectedProtocol = SelectOptimalProtocol(probeResults, payloadHint);
            var selectedResult = probeResults[selectedProtocol];

            var handler = new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = config.PoolSize,
                EnableMultipleHttp2Connections = selectedProtocol is "h2" or "grpc"
            };

            var client = new HttpClient(handler)
            {
                BaseAddress = baseUri,
                Timeout = config.Timeout
            };

            if (selectedProtocol is "h2" or "grpc")
                client.DefaultRequestVersion = new Version(2, 0);
            else if (selectedProtocol == "h3")
                client.DefaultRequestVersion = new Version(3, 0);

            if (!string.IsNullOrEmpty(config.AuthCredential))
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", config.AuthCredential);

            var verifyResponse = await client.SendAsync(
                new HttpRequestMessage(HttpMethod.Head, "/"), ct);
            verifyResponse.EnsureSuccessStatusCode();

            var info = new Dictionary<string, object>
            {
                ["endpoint"] = endpoint,
                ["negotiated_protocol"] = selectedProtocol,
                ["probe_latency_ms"] = selectedResult.Latency.TotalMilliseconds,
                ["protocols_available"] = string.Join(",", probeResults.Keys),
                ["payload_hint"] = payloadHint,
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(client, info, $"adaptive-{selectedProtocol}-{Guid.NewGuid():N}");
        }

        /// <inheritdoc/>
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            // Finding 1924: Catch network errors and return false (health check contract).
            try
            {
                var client = handle.GetConnection<HttpClient>();
                using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/"), ct);
                return response.IsSuccessStatusCode;
            }
            catch (OperationCanceledException) { throw; }
            catch { return false; }
        }

        /// <inheritdoc/>
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            client.Dispose();
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();

            var protocol = handle.ConnectionInfo.GetValueOrDefault("negotiated_protocol", "unknown");

            return new ConnectionHealth(
                IsHealthy: isHealthy,
                StatusMessage: isHealthy
                    ? $"Endpoint responsive via {protocol}"
                    : $"Endpoint unreachable via {protocol}",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow,
                Details: new Dictionary<string, object>
                {
                    ["negotiated_protocol"] = protocol,
                    ["protocols_available"] = handle.ConnectionInfo.GetValueOrDefault("protocols_available", "")
                });
        }

        /// <summary>
        /// Probes a specific protocol to determine if it is supported and measure latency.
        /// </summary>
        private async Task<ProbeResult> ProbeProtocolAsync(
            Uri baseUri, string protocol, int timeoutMs, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            var handler = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromMilliseconds(timeoutMs)
            };

            using var probeClient = new HttpClient(handler)
            {
                BaseAddress = baseUri,
                Timeout = TimeSpan.FromMilliseconds(timeoutMs)
            };

            if (protocol is "h2" or "grpc")
                probeClient.DefaultRequestVersion = new Version(2, 0);
            else if (protocol == "h3")
                probeClient.DefaultRequestVersion = new Version(3, 0);

            var sw = Stopwatch.StartNew();
            HttpResponseMessage response;
            // Finding 1922: WebSocket probe must use Upgrade handshake, not OPTIONS.
            // OPTIONS cannot determine actual WebSocket support — server may accept OPTIONS but reject Upgrade.
            if (protocol == "websocket")
            {
                var wsRequest = new HttpRequestMessage(HttpMethod.Get, "/");
                wsRequest.Headers.TryAddWithoutValidation("Upgrade", "websocket");
                wsRequest.Headers.TryAddWithoutValidation("Connection", "Upgrade");
                wsRequest.Headers.TryAddWithoutValidation("Sec-WebSocket-Key", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)));
                wsRequest.Headers.TryAddWithoutValidation("Sec-WebSocket-Version", "13");
                response = await probeClient.SendAsync(wsRequest, cts.Token);
                // 101 Switching Protocols = WebSocket supported; 400/426 = rejected
                sw.Stop();
                var wsScore = CalculateProtocolScore(protocol, sw.Elapsed, response);
                return new ProbeResult((int)response.StatusCode == 101, sw.Elapsed, wsScore);
            }
            response = await probeClient.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, "/"), cts.Token);
            sw.Stop();

            var score = CalculateProtocolScore(protocol, sw.Elapsed, response);

            return new ProbeResult(response.IsSuccessStatusCode, sw.Elapsed, score);
        }

        /// <summary>
        /// Calculates a composite score for a protocol based on latency and capabilities.
        /// </summary>
        private static double CalculateProtocolScore(string protocol, TimeSpan latency, HttpResponseMessage response)
        {
            var latencyScore = Math.Max(0, 1000 - latency.TotalMilliseconds) / 1000.0;

            var protocolBonus = protocol switch
            {
                "h2" => 0.3,
                "grpc" => 0.25,
                "h3" => 0.35,
                "websocket" => 0.2,
                _ => 0.0
            };

            var multiplexingBonus = protocol is "h2" or "h3" or "grpc" ? 0.15 : 0.0;

            return latencyScore + protocolBonus + multiplexingBonus;
        }

        /// <summary>
        /// Selects the optimal protocol from probe results considering payload characteristics.
        /// </summary>
        private static string SelectOptimalProtocol(
            Dictionary<string, ProbeResult> probeResults, string payloadHint)
        {
            var payloadMultipliers = new Dictionary<string, Dictionary<string, double>>
            {
                ["streaming"] = new() { ["websocket"] = 1.5, ["h2"] = 1.3, ["grpc"] = 1.4 },
                ["large_binary"] = new() { ["h2"] = 1.4, ["h3"] = 1.5, ["grpc"] = 1.3 },
                ["small_json"] = new() { ["h2"] = 1.2, ["grpc"] = 1.4, ["http/1.1"] = 1.1 },
                ["mixed"] = new() { ["h2"] = 1.2, ["grpc"] = 1.1 }
            };

            var multipliers = payloadMultipliers.GetValueOrDefault(payloadHint, payloadMultipliers["mixed"]);

            return probeResults
                .OrderByDescending(kvp =>
                    kvp.Value.Score * multipliers.GetValueOrDefault(kvp.Key, 1.0))
                .First()
                .Key;
        }

        private record ProbeResult(bool IsSupported, TimeSpan Latency, double Score);
    }
}
