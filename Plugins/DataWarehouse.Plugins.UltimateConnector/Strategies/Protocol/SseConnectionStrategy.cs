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
    /// Connection strategy for Server-Sent Events (SSE) endpoints.
    /// Establishes HTTP streaming connection for real-time event reception.
    /// </summary>
    public class SseConnectionStrategy : ConnectionStrategyBase
    {
        private static readonly HttpClient _sharedTestClient = new HttpClient();

        /// <inheritdoc/>
        public override string StrategyId => "sse";

        /// <inheritdoc/>
        public override string DisplayName => "Server-Sent Events (SSE)";

        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Protocol;

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new();

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Connects to Server-Sent Events streams for server-push real-time updates";

        /// <inheritdoc/>
        public override string[] Tags => new[] { "sse", "streaming", "events", "protocol", "http" };

        /// <summary>
        /// Initializes a new instance of <see cref="SseConnectionStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger.</param>
        public SseConnectionStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var endpoint = config.ConnectionString;
            if (string.IsNullOrWhiteSpace(endpoint))
                throw new ArgumentException("SSE endpoint URL is required in ConnectionString");

            var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.Remove("Accept");
            client.DefaultRequestHeaders.Add("Accept", "text/event-stream");
            client.DefaultRequestHeaders.Remove("Cache-Control");
            client.DefaultRequestHeaders.Add("Cache-Control", "no-cache");

            var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType != "text/event-stream")
                throw new InvalidOperationException($"Invalid content type: {contentType}. Expected text/event-stream");

            var info = new Dictionary<string, object>
            {
                ["endpoint"] = endpoint,
                ["protocol"] = "SSE",
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(client, info);
        }

        /// <inheritdoc/>
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            var baseUri = client.BaseAddress?.ToString() ?? string.Empty;
            var response = await _sharedTestClient.GetAsync(baseUri, ct);
            return response.IsSuccessStatusCode;
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
                StatusMessage: isHealthy ? "SSE endpoint responsive" : "SSE endpoint unreachable",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow);
        }
    }
}
