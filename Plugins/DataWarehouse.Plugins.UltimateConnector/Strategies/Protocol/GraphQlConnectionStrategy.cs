using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
    /// <remarks>
    /// <para>
    /// <strong>Security Controls (NET-08 - CVSS 5.3):</strong>
    /// <list type="bullet">
    /// <item><description>Query depth limiting: rejects queries exceeding configurable max depth (default: 10)</description></item>
    /// <item><description>Introspection control: disabled in production by default, configurable via EnableIntrospection</description></item>
    /// <item><description>Query complexity analysis: assigns cost based on nesting depth, rejects excessive queries</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    public class GraphQlConnectionStrategy : ConnectionStrategyBase
    {
        /// <summary>
        /// Maximum allowed query depth. Queries exceeding this depth are rejected (NET-08).
        /// </summary>
        private int _maxQueryDepth = 10;

        /// <summary>
        /// Maximum allowed query complexity cost. Queries exceeding this cost are rejected.
        /// </summary>
        private int _maxQueryComplexity = 1000;

        /// <summary>
        /// Whether introspection queries are allowed. Should be false in production (NET-08).
        /// </summary>
        private bool _enableIntrospection;

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
            "Connects to GraphQL APIs with query depth limiting, introspection control, and complexity analysis";

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

            // NET-08: Load security configuration
            _maxQueryDepth = GetConfiguration(config, "MaxQueryDepth", 10);
            _maxQueryComplexity = GetConfiguration(config, "MaxQueryComplexity", 1000);
            _enableIntrospection = GetConfiguration(config, "EnableIntrospection", false);

            if (_enableIntrospection)
            {
                System.Diagnostics.Trace.TraceWarning("GraphQL introspection is ENABLED. This should only be used in development environments (NET-08)");
            }

            var client = new HttpClient { BaseAddress = new Uri(endpoint) };

            var apiKey = GetConfiguration(config, "ApiKey", string.Empty);
            if (!string.IsNullOrEmpty(apiKey))
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            // Only test with introspection if enabled; otherwise use a safe health check query
            if (_enableIntrospection)
            {
                var introspectionQuery = @"{""query"":""{__schema { queryType { name } }}""}";
                var content = new StringContent(introspectionQuery, Encoding.UTF8, "application/json");
                var response = await client.PostAsync("", content, ct);
                response.EnsureSuccessStatusCode();
            }
            else
            {
                // Use __typename as a lightweight connectivity test (does not expose schema)
                var healthQuery = @"{""query"":""{__typename}""}";
                var content = new StringContent(healthQuery, Encoding.UTF8, "application/json");
                var response = await client.PostAsync("", content, ct);
                response.EnsureSuccessStatusCode();
            }

            var info = new Dictionary<string, object>
            {
                ["endpoint"] = endpoint,
                ["protocol"] = "GraphQL",
                ["connected_at"] = DateTimeOffset.UtcNow,
                ["max_query_depth"] = _maxQueryDepth,
                ["introspection_enabled"] = _enableIntrospection
            };

            return new DefaultConnectionHandle(client, info);
        }

        /// <summary>
        /// Validates a GraphQL query string against security controls (NET-08).
        /// Checks for query depth, introspection queries, and complexity limits.
        /// </summary>
        /// <param name="queryString">The GraphQL query to validate.</param>
        /// <exception cref="InvalidOperationException">Thrown when the query violates security controls.</exception>
        public void ValidateQuery(string queryString)
        {
            if (string.IsNullOrWhiteSpace(queryString))
                return;

            // NET-08: Block introspection queries in production
            if (!_enableIntrospection)
            {
                if (queryString.Contains("__schema", StringComparison.OrdinalIgnoreCase) ||
                    queryString.Contains("__type", StringComparison.OrdinalIgnoreCase))
                {
                    System.Diagnostics.Trace.TraceWarning("GraphQL introspection query blocked (NET-08): introspection is disabled in production");
                    throw new InvalidOperationException(
                        "GraphQL introspection queries are disabled in production. Set EnableIntrospection=true for development environments only.");
                }
            }

            // NET-08: Check query depth
            var depth = CalculateQueryDepth(queryString);
            if (depth > _maxQueryDepth)
            {
                System.Diagnostics.Trace.TraceWarning("GraphQL query depth {0} exceeds maximum {1} (NET-08)", depth.ToString(), _maxQueryDepth.ToString());
                throw new InvalidOperationException(
                    $"GraphQL query depth ({depth}) exceeds maximum allowed depth ({_maxQueryDepth}). Simplify the query or increase MaxQueryDepth.");
            }

            // NET-08: Check query complexity
            var complexity = EstimateQueryComplexity(queryString);
            if (complexity > _maxQueryComplexity)
            {
                System.Diagnostics.Trace.TraceWarning("GraphQL query complexity {0} exceeds maximum {1} (NET-08)", complexity.ToString(), _maxQueryComplexity.ToString());
                throw new InvalidOperationException(
                    $"GraphQL query complexity ({complexity}) exceeds maximum allowed complexity ({_maxQueryComplexity}). Simplify the query or increase MaxQueryComplexity.");
            }
        }

        /// <summary>
        /// Calculates the nesting depth of a GraphQL query by counting brace nesting levels.
        /// </summary>
        /// <param name="query">The GraphQL query string.</param>
        /// <returns>The maximum nesting depth found in the query.</returns>
        internal static int CalculateQueryDepth(string query)
        {
            int maxDepth = 0;
            int currentDepth = 0;
            bool inString = false;

            for (int i = 0; i < query.Length; i++)
            {
                var c = query[i];

                // Handle string literals
                if (c == '"' && (i == 0 || query[i - 1] != '\\'))
                {
                    inString = !inString;
                    continue;
                }

                if (inString) continue;

                if (c == '{')
                {
                    currentDepth++;
                    if (currentDepth > maxDepth)
                        maxDepth = currentDepth;
                }
                else if (c == '}')
                {
                    currentDepth--;
                }
            }

            return maxDepth;
        }

        /// <summary>
        /// Estimates query complexity based on nesting depth and field count.
        /// Each field at depth N contributes 10^(N-1) to complexity (exponential cost for deeply nested fields).
        /// </summary>
        /// <param name="query">The GraphQL query string.</param>
        /// <returns>Estimated complexity score.</returns>
        internal static int EstimateQueryComplexity(string query)
        {
            int complexity = 0;
            int currentDepth = 0;
            bool inString = false;
            bool inFieldName = false;

            for (int i = 0; i < query.Length; i++)
            {
                var c = query[i];

                if (c == '"' && (i == 0 || query[i - 1] != '\\'))
                {
                    inString = !inString;
                    continue;
                }

                if (inString) continue;

                if (c == '{')
                {
                    currentDepth++;
                    inFieldName = false;
                }
                else if (c == '}')
                {
                    currentDepth--;
                    inFieldName = false;
                }
                else if (char.IsLetterOrDigit(c) || c == '_')
                {
                    if (!inFieldName && currentDepth > 0)
                    {
                        // Each field contributes cost proportional to its depth
                        complexity += (int)Math.Pow(2, Math.Min(currentDepth - 1, 20));
                        inFieldName = true;
                    }
                }
                else if (c == ' ' || c == '\n' || c == '\r' || c == '\t' || c == ',')
                {
                    inFieldName = false;
                }
            }

            return complexity;
        }

        /// <inheritdoc/>
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            // Use __typename for health check (safe even with introspection disabled)
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
