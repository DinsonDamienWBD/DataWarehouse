using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Innovations
{
    /// <summary>
    /// Semantic intent-based service discovery strategy that resolves natural language connection
    /// intents (e.g., "read-replica PostgreSQL in EU-WEST with less than 50ms latency") to optimal
    /// endpoints using a service mesh catalog with constraint satisfaction.
    /// </summary>
    /// <remarks>
    /// The discovery process:
    /// <list type="bullet">
    ///   <item>Parses natural language intent into structured constraints (type, region, latency, role)</item>
    ///   <item>Queries the service catalog with extracted constraints</item>
    ///   <item>Ranks matching endpoints by composite fitness score</item>
    ///   <item>Validates selected endpoint meets all hard constraints before connecting</item>
    ///   <item>Supports fallback resolution when no exact match is found</item>
    ///   <item>Caches resolved intents for fast reconnection</item>
    /// </list>
    /// </remarks>
    public class IntentBasedServiceDiscoveryStrategy : ConnectionStrategyBase
    {
        private static readonly Dictionary<string, string> IntentKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            ["read-replica"] = "role:read-replica",
            ["read replica"] = "role:read-replica",
            ["primary"] = "role:primary",
            ["master"] = "role:primary",
            ["leader"] = "role:primary",
            ["follower"] = "role:read-replica",
            ["low-latency"] = "latency:low",
            ["high-throughput"] = "throughput:high",
            ["us-east"] = "region:us-east",
            ["us-west"] = "region:us-west",
            ["eu-west"] = "region:eu-west",
            ["eu-central"] = "region:eu-central",
            ["ap-southeast"] = "region:ap-southeast",
            ["postgresql"] = "type:postgresql",
            ["postgres"] = "type:postgresql",
            ["mysql"] = "type:mysql",
            ["redis"] = "type:redis",
            ["mongodb"] = "type:mongodb",
            ["kafka"] = "type:kafka",
            ["elasticsearch"] = "type:elasticsearch"
        };

        /// <inheritdoc/>
        public override string StrategyId => "innovation-intent-discovery";

        /// <inheritdoc/>
        public override string DisplayName => "Intent-Based Service Discovery";

        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Protocol;

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true,
            SupportsStreaming: false,
            SupportsSsl: true,
            SupportsAuthentication: true,
            SupportsSchemaDiscovery: true,
            SupportsHealthCheck: true,
            SupportsConnectionTesting: true,
            SupportsReconnection: true,
            MaxConcurrentConnections: 100,
            SupportedAuthMethods: ["bearer", "oauth2", "apikey"]
        );

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Semantic intent-based endpoint resolution that translates natural language connection " +
            "intents into optimal endpoint selection with constraint satisfaction and ranking";

        /// <inheritdoc/>
        public override string[] Tags => ["intent", "discovery", "service-mesh", "semantic", "catalog", "resolution", "nlp"];

        /// <summary>
        /// Initializes a new instance of <see cref="IntentBasedServiceDiscoveryStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public IntentBasedServiceDiscoveryStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var catalogEndpoint = config.ConnectionString
                ?? throw new ArgumentException("Service catalog endpoint URL is required in ConnectionString.");

            var intent = GetConfiguration<string>(config, "intent", "")
                ?? throw new ArgumentException("intent property is required.");
            var maxLatencyMs = GetConfiguration<int>(config, "max_latency_ms", 0);
            var preferredRegion = GetConfiguration<string>(config, "preferred_region", "");
            var serviceType = GetConfiguration<string>(config, "service_type", "");
            var allowFallback = GetConfiguration<bool>(config, "allow_fallback", true);
            var cacheResolution = GetConfiguration<bool>(config, "cache_resolution", true);

            if (string.IsNullOrWhiteSpace(intent))
                throw new ArgumentException("intent property is required for intent-based discovery.");

            var constraints = ParseIntent(intent, maxLatencyMs, preferredRegion, serviceType);

            var handler = new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = config.PoolSize
            };

            var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(catalogEndpoint),
                Timeout = config.Timeout,
                DefaultRequestVersion = new Version(2, 0)
            };

            if (!string.IsNullOrEmpty(config.AuthCredential))
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", config.AuthCredential);

            var discoveryPayload = new
            {
                intent,
                constraints,
                options = new
                {
                    allow_fallback = allowFallback,
                    cache_resolution = cacheResolution,
                    max_candidates = GetConfiguration<int>(config, "max_candidates", 10),
                    ranking_strategy = GetConfiguration<string>(config, "ranking_strategy", "composite_fitness"),
                    include_metrics = true
                }
            };

            var content = new StringContent(
                JsonSerializer.Serialize(discoveryPayload),
                Encoding.UTF8,
                "application/json");

            var response = await client.PostAsync("/api/v1/discovery/resolve", content, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var resolvedEndpoint = result.GetProperty("resolved_endpoint").GetString()
                ?? throw new InvalidOperationException("Service catalog did not return a resolved_endpoint.");
            var resolutionId = result.GetProperty("resolution_id").GetString() ?? Guid.NewGuid().ToString("N");
            var fitnessScore = result.TryGetProperty("fitness_score", out var fs) ? fs.GetDouble() : 0;
            var candidatesEvaluated = result.TryGetProperty("candidates_evaluated", out var ce) ? ce.GetInt32() : 0;
            var isFallback = result.TryGetProperty("is_fallback", out var fb) && fb.GetBoolean();
            var resolvedServiceType = result.TryGetProperty("service_type", out var rst)
                ? rst.GetString() ?? "unknown" : "unknown";
            var resolvedRegion = result.TryGetProperty("region", out var rr)
                ? rr.GetString() ?? "unknown" : "unknown";

            var verifyResponse = await client.GetAsync(
                $"/api/v1/discovery/resolutions/{resolutionId}/verify", ct);
            verifyResponse.EnsureSuccessStatusCode();

            client.DefaultRequestHeaders.Remove("X-Resolution-Id");
            client.DefaultRequestHeaders.Add("X-Resolution-Id", resolutionId);
            client.DefaultRequestHeaders.Remove("X-Resolved-Endpoint");
            client.DefaultRequestHeaders.Add("X-Resolved-Endpoint", resolvedEndpoint);

            var info = new Dictionary<string, object>
            {
                ["catalog_endpoint"] = catalogEndpoint,
                ["resolved_endpoint"] = resolvedEndpoint,
                ["resolution_id"] = resolutionId,
                ["intent"] = intent,
                ["fitness_score"] = fitnessScore,
                ["candidates_evaluated"] = candidatesEvaluated,
                ["is_fallback"] = isFallback,
                ["service_type"] = resolvedServiceType,
                ["region"] = resolvedRegion,
                ["constraints"] = JsonSerializer.Serialize(constraints),
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(client, info, $"intent-{resolutionId}");
        }

        /// <inheritdoc/>
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            var resolutionId = handle.ConnectionInfo["resolution_id"]?.ToString();

            var response = await client.GetAsync(
                $"/api/v1/discovery/resolutions/{resolutionId}/status", ct);
            if (!response.IsSuccessStatusCode) return false;

            var status = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            return status.TryGetProperty("endpoint_healthy", out var healthy) && healthy.GetBoolean();
        }

        /// <inheritdoc/>
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            var resolutionId = handle.ConnectionInfo["resolution_id"]?.ToString();

            try
            {
                await client.DeleteAsync($"/api/v1/discovery/resolutions/{resolutionId}", ct);
            }
            finally
            {
                client.Dispose();
            }
        }

        /// <inheritdoc/>
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var client = handle.GetConnection<HttpClient>();
            var resolutionId = handle.ConnectionInfo["resolution_id"]?.ToString();

            var response = await client.GetAsync(
                $"/api/v1/discovery/resolutions/{resolutionId}/health", ct);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return new ConnectionHealth(
                    IsHealthy: false,
                    StatusMessage: "Intent resolution health check failed",
                    Latency: sw.Elapsed,
                    CheckedAt: DateTimeOffset.UtcNow);
            }

            var healthData = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var endpointHealthy = healthData.TryGetProperty("endpoint_healthy", out var eh) && eh.GetBoolean();
            var currentLatencyMs = healthData.TryGetProperty("current_latency_ms", out var cl) ? cl.GetDouble() : 0;
            var constraintsMet = healthData.TryGetProperty("constraints_met", out var cm) && cm.GetBoolean();

            return new ConnectionHealth(
                IsHealthy: endpointHealthy && constraintsMet,
                StatusMessage: endpointHealthy
                    ? constraintsMet
                        ? $"Resolved endpoint healthy, latency: {currentLatencyMs:F0}ms, constraints satisfied"
                        : $"Endpoint healthy but constraints violated (latency: {currentLatencyMs:F0}ms)"
                    : "Resolved endpoint unhealthy",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow,
                Details: new Dictionary<string, object>
                {
                    ["endpoint_healthy"] = endpointHealthy,
                    ["constraints_met"] = constraintsMet,
                    ["current_latency_ms"] = currentLatencyMs,
                    ["resolved_endpoint"] = handle.ConnectionInfo.GetValueOrDefault("resolved_endpoint", "unknown"),
                    ["fitness_score"] = handle.ConnectionInfo.GetValueOrDefault("fitness_score", 0.0),
                    ["is_fallback"] = handle.ConnectionInfo.GetValueOrDefault("is_fallback", false)
                });
        }

        /// <summary>
        /// Parses a natural language intent string into structured discovery constraints.
        /// </summary>
        private static Dictionary<string, object> ParseIntent(
            string intent, int maxLatencyMs, string preferredRegion, string serviceType)
        {
            var constraints = new Dictionary<string, object>();
            var hardConstraints = new List<Dictionary<string, object>>();
            var softConstraints = new List<Dictionary<string, object>>();

            foreach (var (keyword, constraintStr) in IntentKeywords)
            {
                if (intent.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    var parts = constraintStr.Split(':');
                    softConstraints.Add(new Dictionary<string, object>
                    {
                        ["field"] = parts[0],
                        ["value"] = parts[1],
                        ["weight"] = 1.0
                    });
                }
            }

            if (maxLatencyMs > 0)
            {
                hardConstraints.Add(new Dictionary<string, object>
                {
                    ["field"] = "latency_ms",
                    ["operator"] = "lte",
                    ["value"] = maxLatencyMs
                });
            }

            if (!string.IsNullOrEmpty(preferredRegion))
            {
                softConstraints.Add(new Dictionary<string, object>
                {
                    ["field"] = "region",
                    ["value"] = preferredRegion,
                    ["weight"] = 2.0
                });
            }

            if (!string.IsNullOrEmpty(serviceType))
            {
                hardConstraints.Add(new Dictionary<string, object>
                {
                    ["field"] = "type",
                    ["operator"] = "eq",
                    ["value"] = serviceType
                });
            }

            // Extract numeric latency constraint from intent like "<50ms"
            var latencyMatch = System.Text.RegularExpressions.Regex.Match(intent, @"<\s*(\d+)\s*ms");
            if (latencyMatch.Success && int.TryParse(latencyMatch.Groups[1].Value, out var parsedLatency))
            {
                hardConstraints.Add(new Dictionary<string, object>
                {
                    ["field"] = "latency_ms",
                    ["operator"] = "lte",
                    ["value"] = parsedLatency
                });
            }

            constraints["hard"] = hardConstraints;
            constraints["soft"] = softConstraints;
            constraints["original_intent"] = intent;

            return constraints;
        }
    }
}
