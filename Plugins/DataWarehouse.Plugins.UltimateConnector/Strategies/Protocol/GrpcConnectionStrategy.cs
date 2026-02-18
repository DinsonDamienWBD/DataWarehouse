using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Protocol
{
    /// <summary>
    /// Connection strategy for gRPC endpoints using HTTP/2 protocol.
    /// Tests connectivity via HTTP/2 connection to gRPC service endpoint.
    /// </summary>
    public class GrpcConnectionStrategy : ConnectionStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "grpc";

        /// <inheritdoc/>
        public override string DisplayName => "gRPC";

        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Protocol;

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new();

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Connects to gRPC services over HTTP/2 protocol for remote procedure calls";

        /// <inheritdoc/>
        public override string[] Tags => new[] { "grpc", "http2", "rpc", "protocol", "microservices" };

        /// <summary>
        /// Initializes a new instance of <see cref="GrpcConnectionStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger.</param>
        public GrpcConnectionStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var endpoint = config.ConnectionString;
            if (string.IsNullOrWhiteSpace(endpoint))
                throw new ArgumentException("gRPC endpoint URL is required in ConnectionString");

            var handler = new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
                KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
                EnableMultipleHttp2Connections = true
            };

            var client = new HttpClient(handler) { BaseAddress = new Uri(endpoint) };
            client.DefaultRequestVersion = new Version(2, 0);

            // Use proper gRPC health check endpoint instead of HTTP GET
            // Try the standard gRPC health checking protocol endpoint
            var healthRequest = new HttpRequestMessage(HttpMethod.Post, "/grpc.health.v1.Health/Check");
            healthRequest.Version = new Version(2, 0);
            healthRequest.Headers.Add("content-type", "application/grpc");

            try
            {
                var response = await client.SendAsync(healthRequest, ct);
                // For gRPC, we expect HTTP 200 with grpc-status header, or at least HTTP/2 connection success
                // Even if the health endpoint isn't implemented, HTTP/2 connection success is sufficient
                if (response.Version.Major < 2)
                {
                    throw new InvalidOperationException("gRPC requires HTTP/2 protocol support");
                }
            }
            catch (HttpRequestException)
            {
                // If health check fails, try a simple connection test
                var testRequest = new HttpRequestMessage(HttpMethod.Post, "/");
                testRequest.Version = new Version(2, 0);
                var testResponse = await client.SendAsync(testRequest, ct);
                if (testResponse.Version.Major < 2)
                {
                    throw new InvalidOperationException("gRPC requires HTTP/2 protocol support");
                }
            }

            var info = new Dictionary<string, object>
            {
                ["endpoint"] = endpoint,
                ["protocol"] = "HTTP/2",
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(client, info);
        }

        /// <inheritdoc/>
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            var healthRequest = new HttpRequestMessage(HttpMethod.Post, "/grpc.health.v1.Health/Check");
            healthRequest.Version = new Version(2, 0);
            healthRequest.Headers.Add("content-type", "application/grpc");

            try
            {
                var response = await client.SendAsync(healthRequest, ct);
                return response.Version.Major >= 2;
            }
            catch
            {
                return false;
            }
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
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();

            return new ConnectionHealth(
                IsHealthy: isHealthy,
                StatusMessage: isHealthy ? "gRPC endpoint responsive" : "gRPC endpoint unreachable",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow);
        }
    }
}
