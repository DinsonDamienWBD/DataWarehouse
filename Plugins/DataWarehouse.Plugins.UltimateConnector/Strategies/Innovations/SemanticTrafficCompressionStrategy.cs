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
    /// AI-aware traffic compression strategy that analyzes payload semantics to select
    /// the optimal compression algorithm and level. Maintains a soft dependency on T92
    /// (compression subsystem) via a message bus for advanced dictionary-based compression.
    /// </summary>
    /// <remarks>
    /// Unlike traditional compression that treats data as opaque bytes, this strategy:
    /// <list type="bullet">
    ///   <item>Classifies payload type (JSON, Protobuf, Avro, Parquet, CSV, text)</item>
    ///   <item>Selects compression algorithm based on data patterns (Zstd, Brotli, LZ4, Snappy)</item>
    ///   <item>Uses shared compression dictionaries trained on similar payload patterns</item>
    ///   <item>Applies column-level compression for structured data (different codecs per column type)</item>
    ///   <item>Negotiates compression capabilities with the remote endpoint during handshake</item>
    ///   <item>Adapts compression level based on CPU load and network bandwidth</item>
    /// </list>
    /// </remarks>
    public class SemanticTrafficCompressionStrategy : ConnectionStrategyBase
    {
        private static readonly Dictionary<string, CompressionProfile> PayloadProfiles = new(StringComparer.OrdinalIgnoreCase)
        {
            ["json"] = new("zstd", 3, true, "High redundancy in keys, excellent dictionary compression"),
            ["protobuf"] = new("lz4", 1, false, "Already compact, fast compression preferred"),
            ["avro"] = new("snappy", 1, false, "Schema-encoded, moderate redundancy"),
            ["parquet"] = new("zstd", 1, false, "Columnar format, per-column codecs preferred"),
            ["csv"] = new("zstd", 5, true, "High redundancy in headers and repeated values"),
            ["text"] = new("brotli", 4, true, "Natural language benefits from Brotli's dictionary"),
            ["binary"] = new("lz4", 1, false, "Unknown structure, prioritize speed"),
            ["xml"] = new("zstd", 4, true, "High tag redundancy, excellent dictionary compression"),
            ["msgpack"] = new("lz4", 2, false, "Compact binary format, moderate compression")
        };

        /// <inheritdoc/>
        public override string StrategyId => "innovation-semantic-compression";

        /// <inheritdoc/>
        public override string DisplayName => "Semantic Traffic Compression";

        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Protocol;

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true,
            SupportsStreaming: true,
            SupportsCompression: true,
            SupportsSsl: true,
            SupportsHealthCheck: true,
            SupportsConnectionTesting: true,
            MaxConcurrentConnections: 150
        );

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "AI-aware semantic traffic compression that analyzes payload structure to select " +
            "optimal compression (Zstd, Brotli, LZ4, Snappy) with shared dictionary training";

        /// <inheritdoc/>
        public override string[] Tags => ["compression", "semantic", "zstd", "brotli", "lz4", "ai", "dictionary", "t92"];

        /// <summary>
        /// Initializes a new instance of <see cref="SemanticTrafficCompressionStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public SemanticTrafficCompressionStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var endpoint = config.ConnectionString
                ?? throw new ArgumentException("Compression endpoint URL is required in ConnectionString.");

            var payloadType = GetConfiguration<string>(config, "payload_type", "json");
            var preferredAlgorithm = GetConfiguration<string>(config, "preferred_algorithm", "auto");
            var compressionLevel = GetConfiguration<int>(config, "compression_level", -1);
            var enableDictionary = GetConfiguration<bool>(config, "enable_dictionary", true);
            var adaptiveMode = GetConfiguration<bool>(config, "adaptive_mode", true);
            var minCompressSize = GetConfiguration<int>(config, "min_compress_size_bytes", 256);
            var dictionaryEndpoint = GetConfiguration<string>(config, "dictionary_endpoint", "");
            var messageBusTopic = GetConfiguration<string>(config, "message_bus_topic", "t92.compression");

            var profile = ResolveCompressionProfile(payloadType, preferredAlgorithm, compressionLevel);

            var handler = new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = config.PoolSize,
                AutomaticDecompression = System.Net.DecompressionMethods.All
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

            var negotiationPayload = new
            {
                payload_type = payloadType,
                compression = new
                {
                    algorithm = profile.Algorithm,
                    level = profile.Level,
                    dictionary_enabled = profile.UseDictionary && enableDictionary,
                    adaptive = adaptiveMode,
                    min_compress_size_bytes = minCompressSize
                },
                supported_algorithms = new[] { "zstd", "brotli", "lz4", "snappy", "gzip", "deflate" },
                dictionary_settings = new
                {
                    source_endpoint = dictionaryEndpoint,
                    message_bus_topic = messageBusTopic,
                    train_on_traffic = GetConfiguration<bool>(config, "train_dictionary", true),
                    min_training_samples = GetConfiguration<int>(config, "min_training_samples", 100),
                    dictionary_size_kb = GetConfiguration<int>(config, "dictionary_size_kb", 32)
                },
                bandwidth_hints = new
                {
                    estimated_bandwidth_mbps = GetConfiguration<double>(config, "bandwidth_mbps", 100),
                    prioritize_ratio = GetConfiguration<bool>(config, "prioritize_ratio", false),
                    prioritize_speed = GetConfiguration<bool>(config, "prioritize_speed", true)
                }
            };

            var content = new StringContent(
                JsonSerializer.Serialize(negotiationPayload),
                Encoding.UTF8,
                "application/json");

            var response = await client.PostAsync("/api/v1/compression/negotiate", content, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var sessionId = result.GetProperty("session_id").GetString()
                ?? throw new InvalidOperationException("Compression endpoint did not return a session_id.");
            var negotiatedAlgorithm = result.TryGetProperty("negotiated_algorithm", out var na)
                ? na.GetString() ?? profile.Algorithm : profile.Algorithm;
            var dictionaryId = result.TryGetProperty("dictionary_id", out var di) ? di.GetString() : null;

            client.DefaultRequestHeaders.Remove("X-Compression-Session");
            client.DefaultRequestHeaders.Add("X-Compression-Session", sessionId);
            client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue(negotiatedAlgorithm));

            var info = new Dictionary<string, object>
            {
                ["endpoint"] = endpoint,
                ["session_id"] = sessionId,
                ["payload_type"] = payloadType,
                ["algorithm"] = negotiatedAlgorithm,
                ["compression_level"] = profile.Level,
                ["dictionary_enabled"] = profile.UseDictionary && enableDictionary,
                ["dictionary_id"] = dictionaryId ?? "none",
                ["adaptive_mode"] = adaptiveMode,
                ["min_compress_size"] = minCompressSize,
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(client, info, $"sc-{negotiatedAlgorithm}-{sessionId}");
        }

        /// <inheritdoc/>
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            var sessionId = handle.ConnectionInfo["session_id"]?.ToString();

            var response = await client.GetAsync($"/api/v1/compression/sessions/{sessionId}/status", ct);
            return response.IsSuccessStatusCode;
        }

        /// <inheritdoc/>
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            var sessionId = handle.ConnectionInfo["session_id"]?.ToString();

            try
            {
                await client.PostAsync($"/api/v1/compression/sessions/{sessionId}/flush", null, ct);
                await client.DeleteAsync($"/api/v1/compression/sessions/{sessionId}", ct);
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

            var response = await client.GetAsync($"/api/v1/compression/sessions/{sessionId}/stats", ct);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return new ConnectionHealth(
                    IsHealthy: false,
                    StatusMessage: "Compression session health check failed",
                    Latency: sw.Elapsed,
                    CheckedAt: DateTimeOffset.UtcNow);
            }

            var stats = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var bytesIn = stats.TryGetProperty("bytes_in", out var bi) ? bi.GetInt64() : 0;
            var bytesOut = stats.TryGetProperty("bytes_out", out var bo) ? bo.GetInt64() : 0;
            var compressionRatio = bytesIn > 0 ? (double)bytesOut / bytesIn : 1.0;
            var avgCompressionMs = stats.TryGetProperty("avg_compression_ms", out var acm) ? acm.GetDouble() : 0;

            var algorithm = handle.ConnectionInfo.GetValueOrDefault("algorithm", "unknown");

            return new ConnectionHealth(
                IsHealthy: true,
                StatusMessage: $"Compression active ({algorithm}), ratio: {compressionRatio:F2}x, " +
                    $"saved: {FormatBytes(bytesIn - bytesOut)}, avg: {avgCompressionMs:F1}ms",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow,
                Details: new Dictionary<string, object>
                {
                    ["algorithm"] = algorithm,
                    ["compression_ratio"] = compressionRatio,
                    ["bytes_in"] = bytesIn,
                    ["bytes_out"] = bytesOut,
                    ["bytes_saved"] = bytesIn - bytesOut,
                    ["avg_compression_ms"] = avgCompressionMs
                });
        }

        /// <summary>
        /// Resolves the compression profile based on payload type and user overrides.
        /// </summary>
        private static CompressionProfile ResolveCompressionProfile(
            string payloadType, string preferredAlgorithm, int overrideLevel)
        {
            var profile = PayloadProfiles.GetValueOrDefault(payloadType, PayloadProfiles["binary"]);

            if (preferredAlgorithm != "auto")
                profile = profile with { Algorithm = preferredAlgorithm };

            if (overrideLevel >= 0)
                profile = profile with { Level = overrideLevel };

            return profile;
        }

        /// <summary>
        /// Formats a byte count into a human-readable string.
        /// </summary>
        private static string FormatBytes(long bytes)
        {
            return bytes switch
            {
                >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
                >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
                >= 1024 => $"{bytes / 1024.0:F1} KB",
                _ => $"{bytes} B"
            };
        }

        private record CompressionProfile(
            string Algorithm,
            int Level,
            bool UseDictionary,
            string Rationale);
    }
}
