using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Protocol
{
    /// <summary>
    /// Connection strategy for GraphQL endpoints.
    /// Tests connectivity via HTTP POST with introspection query.
    /// </summary>
    public class GraphQlConnectionStrategy : ConnectionStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "graphql";

        /// <inheritdoc/>
        public override string DisplayName => "GraphQL";

        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Protocol;

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new();

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Connects to GraphQL APIs for flexible data querying with introspection support";

        /// <inheritdoc/>
        public override string[] Tags => new[] { "graphql", "api", "query", "protocol", "http" };

        /// <summary>
        /// Initializes a new instance of <see cref="GraphQlConnectionStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger.</param>
        public GraphQlConnectionStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var endpoint = config.ConnectionString;
            if (string.IsNullOrWhiteSpace(endpoint))
                throw new ArgumentException("GraphQL endpoint URL is required in ConnectionString");

            var client = new HttpClient { BaseAddress = new Uri(endpoint) };

            var apiKey = GetConfiguration(config, "ApiKey", string.Empty);
            if (!string.IsNullOrEmpty(apiKey))
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            var introspectionQuery = @"{""query"":""{__schema { queryType { name } }}""}";
            var content = new StringContent(introspectionQuery, Encoding.UTF8, "application/json");
            var response = await client.PostAsync("", content, ct);
            response.EnsureSuccessStatusCode();

            var info = new Dictionary<string, object>
            {
                ["endpoint"] = endpoint,
                ["protocol"] = "GraphQL",
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(client, info);
        }

        /// <inheritdoc/>
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            var query = @"{""query"":""{__typename}""}";
            var content = new StringContent(query, Encoding.UTF8, "application/json");
            var response = await client.PostAsync("", content, ct);
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
                StatusMessage: isHealthy ? "GraphQL endpoint responsive" : "GraphQL endpoint unreachable",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow);
        }
    }
}
