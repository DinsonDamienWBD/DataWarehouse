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
    /// LLM-based protocol translation strategy that uses neural models to translate between
    /// incompatible API protocols in real-time (REST to SOAP, GraphQL to SQL, gRPC to REST,
    /// etc.). Maintains a soft dependency on T90 (AI/LLM subsystem) via a message bus.
    /// </summary>
    /// <remarks>
    /// The translation engine supports:
    /// <list type="bullet">
    ///   <item>REST to SOAP: Generates WSDL-compliant XML from JSON payloads</item>
    ///   <item>GraphQL to SQL: Translates GraphQL queries into optimized SQL</item>
    ///   <item>gRPC to REST: Maps proto service definitions to RESTful endpoints</item>
    ///   <item>OData to GraphQL: Converts OData filters to GraphQL arguments</item>
    ///   <item>Custom protocol pairs via user-defined translation templates</item>
    /// </list>
    /// The soft dependency on T90 means translation falls back to rule-based mapping
    /// when the LLM service is unavailable.
    /// </remarks>
    public class NeuralProtocolTranslationStrategy : ConnectionStrategyBase
    {
        private static readonly Dictionary<string, string[]> SupportedTranslations = new()
        {
            ["rest"] = ["soap", "graphql", "grpc", "odata"],
            ["soap"] = ["rest", "graphql"],
            ["graphql"] = ["rest", "sql", "odata"],
            ["grpc"] = ["rest", "graphql"],
            ["odata"] = ["rest", "graphql", "sql"],
            ["sql"] = ["graphql", "rest"]
        };

        /// <inheritdoc/>
        public override string StrategyId => "innovation-neural-translation";

        /// <inheritdoc/>
        public override string DisplayName => "Neural Protocol Translation";

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
            SupportsSchemaDiscovery: true,
            MaxConcurrentConnections: 50
        );

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "LLM-based protocol translation engine that converts between incompatible API protocols " +
            "(REST/SOAP/GraphQL/gRPC/SQL/OData) in real-time with neural and rule-based fallback";

        /// <inheritdoc/>
        public override string[] Tags => ["neural", "translation", "protocol", "llm", "rest", "soap", "graphql", "grpc", "t90"];

        /// <summary>
        /// Initializes a new instance of <see cref="NeuralProtocolTranslationStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public NeuralProtocolTranslationStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var endpoint = config.ConnectionString
                ?? throw new ArgumentException("Translation endpoint URL is required in ConnectionString.");

            var sourceProtocol = GetConfiguration<string>(config, "source_protocol", "rest");
            var targetProtocol = GetConfiguration<string>(config, "target_protocol", "")
                ?? throw new ArgumentException("target_protocol property is required.");
            var llmEndpoint = GetConfiguration<string>(config, "llm_endpoint", "");
            var useLlmTranslation = GetConfiguration<bool>(config, "use_llm_translation", true);
            var fallbackToRules = GetConfiguration<bool>(config, "fallback_to_rules", true);
            var cacheTranslations = GetConfiguration<bool>(config, "cache_translations", true);
            var maxTokens = GetConfiguration<int>(config, "max_tokens", 4096);

            if (string.IsNullOrWhiteSpace(targetProtocol))
                throw new ArgumentException("target_protocol property is required for neural translation.");

            ValidateTranslationPair(sourceProtocol, targetProtocol);

            var handler = new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = config.PoolSize,
                EnableMultipleHttp2Connections = true
            };

            var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(endpoint),
                Timeout = config.Timeout,
                DefaultRequestVersion = new Version(2, 0)
            };

            if (!string.IsNullOrEmpty(config.AuthCredential))
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", config.AuthCredential);

            var translationPayload = new
            {
                source_protocol = sourceProtocol,
                target_protocol = targetProtocol,
                translation_engine = new
                {
                    use_llm = useLlmTranslation,
                    llm_endpoint = llmEndpoint,
                    fallback_to_rules = fallbackToRules,
                    max_tokens = maxTokens,
                    temperature = GetConfiguration<double>(config, "llm_temperature", 0.1),
                    model = GetConfiguration<string>(config, "llm_model", "gpt-4"),
                    message_bus_topic = "t90.translation.requests"
                },
                caching = new
                {
                    enabled = cacheTranslations,
                    ttl_seconds = GetConfiguration<int>(config, "cache_ttl_seconds", 3600),
                    max_entries = GetConfiguration<int>(config, "cache_max_entries", 10000)
                },
                validation = new
                {
                    validate_output = true,
                    strict_mode = GetConfiguration<bool>(config, "strict_validation", false),
                    schema_validation = true
                }
            };

            var content = new StringContent(
                JsonSerializer.Serialize(translationPayload),
                Encoding.UTF8,
                "application/json");

            var response = await client.PostAsync("/api/v1/translate/sessions", content, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var sessionId = result.GetProperty("session_id").GetString()
                ?? throw new InvalidOperationException("Translation endpoint did not return a session_id.");
            var engineMode = result.TryGetProperty("engine_mode", out var em)
                ? em.GetString() ?? "rule_based" : "rule_based";
            var llmAvailable = result.TryGetProperty("llm_available", out var la) && la.GetBoolean();

            client.DefaultRequestHeaders.Add("X-Translation-Session", sessionId);
            client.DefaultRequestHeaders.Add("X-Source-Protocol", sourceProtocol);
            client.DefaultRequestHeaders.Add("X-Target-Protocol", targetProtocol);

            var info = new Dictionary<string, object>
            {
                ["endpoint"] = endpoint,
                ["session_id"] = sessionId,
                ["source_protocol"] = sourceProtocol,
                ["target_protocol"] = targetProtocol,
                ["engine_mode"] = engineMode,
                ["llm_available"] = llmAvailable,
                ["cache_enabled"] = cacheTranslations,
                ["fallback_enabled"] = fallbackToRules,
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(client, info, $"npt-{sourceProtocol}-{targetProtocol}-{sessionId}");
        }

        /// <inheritdoc/>
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            var sessionId = handle.ConnectionInfo["session_id"]?.ToString();

            var response = await client.GetAsync($"/api/v1/translate/sessions/{sessionId}/status", ct);
            if (!response.IsSuccessStatusCode) return false;

            var status = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            return status.TryGetProperty("active", out var active) && active.GetBoolean();
        }

        /// <inheritdoc/>
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            var sessionId = handle.ConnectionInfo["session_id"]?.ToString();

            try
            {
                await client.DeleteAsync($"/api/v1/translate/sessions/{sessionId}", ct);
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
            var sessionId = handle.ConnectionInfo["session_id"]?.ToString();

            var response = await client.GetAsync($"/api/v1/translate/sessions/{sessionId}/health", ct);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return new ConnectionHealth(
                    IsHealthy: false,
                    StatusMessage: "Translation session health check failed",
                    Latency: sw.Elapsed,
                    CheckedAt: DateTimeOffset.UtcNow);
            }

            var healthData = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var isActive = healthData.TryGetProperty("active", out var a) && a.GetBoolean();
            var translationsCompleted = healthData.TryGetProperty("translations_completed", out var tc) ? tc.GetInt64() : 0;
            var cacheHitRate = healthData.TryGetProperty("cache_hit_rate", out var chr) ? chr.GetDouble() : 0;
            var avgTranslationMs = healthData.TryGetProperty("avg_translation_ms", out var at) ? at.GetDouble() : 0;

            var sourceProto = handle.ConnectionInfo.GetValueOrDefault("source_protocol", "unknown");
            var targetProto = handle.ConnectionInfo.GetValueOrDefault("target_protocol", "unknown");

            return new ConnectionHealth(
                IsHealthy: isActive,
                StatusMessage: isActive
                    ? $"Translating {sourceProto}->{targetProto}, {translationsCompleted} done, cache: {cacheHitRate:P0}"
                    : "Translation session inactive",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow,
                Details: new Dictionary<string, object>
                {
                    ["active"] = isActive,
                    ["translations_completed"] = translationsCompleted,
                    ["cache_hit_rate"] = cacheHitRate,
                    ["avg_translation_ms"] = avgTranslationMs,
                    ["engine_mode"] = handle.ConnectionInfo.GetValueOrDefault("engine_mode", "unknown"),
                    ["llm_available"] = handle.ConnectionInfo.GetValueOrDefault("llm_available", false)
                });
        }

        /// <summary>
        /// Validates that the requested source/target protocol translation pair is supported.
        /// </summary>
        private static void ValidateTranslationPair(string source, string target)
        {
            if (!SupportedTranslations.TryGetValue(source.ToLowerInvariant(), out var targets))
                throw new ArgumentException(
                    $"Unsupported source protocol: {source}. Supported: {string.Join(", ", SupportedTranslations.Keys)}");

            if (!Array.Exists(targets, t => t.Equals(target, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException(
                    $"Unsupported translation: {source} -> {target}. " +
                    $"Supported targets for {source}: {string.Join(", ", targets)}");
        }
    }
}
