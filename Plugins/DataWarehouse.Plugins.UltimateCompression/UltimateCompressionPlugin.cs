using System.Collections.Concurrent;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Compression;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Primitives;
using HierarchyCompressionPluginBase = DataWarehouse.SDK.Contracts.Hierarchy.CompressionPluginBase;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompression
{
    /// <summary>
    /// Ultimate Compression plugin that consolidates 50+ compression algorithms as strategies.
    /// Provides automatic content-aware algorithm selection, parallel compression,
    /// and real-time benchmarking for optimal compression across all workloads.
    /// Intelligence-aware for AI-powered compression recommendations.
    /// </summary>
    /// <remarks>
    /// <para><b>MIGRATION GUIDE:</b></para>
    /// <para>
    /// This plugin replaces the following individual compression plugins which have been deprecated:
    /// </para>
    /// <list type="table">
    ///   <listheader><term>Old Plugin</term><description>Replacement Strategy</description></listheader>
    ///   <item><term>BrotliCompression</term><description><see cref="Strategies.Transform.BrotliStrategy"/></description></item>
    ///   <item><term>DeflateCompression</term><description><see cref="Strategies.LzFamily.DeflateStrategy"/></description></item>
    ///   <item><term>GZipCompression</term><description><see cref="Strategies.LzFamily.GZipStrategy"/></description></item>
    ///   <item><term>Lz4Compression</term><description><see cref="Strategies.LzFamily.Lz4Strategy"/></description></item>
    ///   <item><term>ZstdCompression</term><description><see cref="Strategies.LzFamily.ZstdStrategy"/></description></item>
    ///   <item><term>Compression (base)</term><description>Merged into <see cref="UltimateCompressionPlugin"/> orchestrator</description></item>
    /// </list>
    /// <para><b>Migration steps:</b></para>
    /// <list type="number">
    ///   <item>Replace direct plugin references with UltimateCompression plugin registration.</item>
    ///   <item>Use <see cref="GetStrategy(string)"/> to access individual algorithms by name.</item>
    ///   <item>Use <see cref="SelectBestStrategy(ReadOnlySpan{byte}, bool)"/> for content-aware automatic selection.</item>
    ///   <item>Old plugin configurations are read via the same algorithm names (case-insensitive).</item>
    /// </list>
    /// <para><b>Supported algorithm families:</b></para>
    /// <list type="bullet">
    ///   <item>LZ-family: Zstd, LZ4, GZip, Deflate, Snappy, LZO, LZ77/78, LZMA/LZMA2, LZFSE, LZH, LZX</item>
    ///   <item>Transform-based: Brotli, BZip2, BWT, MTF</item>
    ///   <item>Context mixing: ZPAQ, PAQ, CMix, NNZ, PPM, PPMd</item>
    ///   <item>Entropy coders: Huffman, Arithmetic, ANS/FSE, rANS, RLE</item>
    ///   <item>Delta encoding: Delta, Xdelta, Bsdiff, VCDIFF, Zdelta</item>
    ///   <item>Domain-specific: FLAC, APNG, WebP, JPEG XL, AVIF, DNA, Time-series</item>
    ///   <item>Archive formats: ZIP, 7z, RAR (read), TAR, XZ</item>
    ///   <item>Emerging: Density, Lizard, Oodle, Zling, Gipfeli</item>
    /// </list>
    /// <para><b>Backward Compatibility:</b> All algorithm names from deprecated plugins are recognized
    /// case-insensitively. Existing pipeline configurations referencing old plugin algorithm names
    /// will continue to work during the transition period. Old compression plugins are deprecated
    /// and scheduled for file deletion in Phase 18.</para>
    /// </remarks>
    public sealed class UltimateCompressionPlugin : HierarchyCompressionPluginBase
    {
        private readonly ConcurrentDictionary<string, ICompressionStrategy> _strategies = new(StringComparer.OrdinalIgnoreCase);
        private ICompressionStrategy? _activeStrategy;

        // Statistics tracking
        private long _totalCompressions;
        private long _totalDecompressions;
        private long _totalBytesCompressed;
        private long _totalBytesDecompressed;

        /// <inheritdoc/>
        public override string Id => "com.datawarehouse.compression.ultimate";

        /// <inheritdoc/>
        public override string Name => "Ultimate Compression";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <inheritdoc/>
        public override string SubCategory => "Compression";


        /// <inheritdoc/>
        public override int DefaultPipelineOrder => 50;

        /// <inheritdoc/>
        public override bool AllowBypass => true;

        /// <inheritdoc/>
        public override int QualityLevel => 95;

        /// <inheritdoc/>
        public override string CompressionAlgorithm => _activeStrategy?.Characteristics.AlgorithmName ?? "Zstd";

        /// <summary>
        /// Registers a compression strategy with the plugin.
        /// </summary>
        /// <param name="strategy">The strategy to register.</param>
        public void RegisterStrategy(ICompressionStrategy strategy)
        {
            ArgumentNullException.ThrowIfNull(strategy);
            _strategies[strategy.Characteristics.AlgorithmName] = strategy;
        }

        /// <summary>
        /// Gets a strategy by algorithm name.
        /// </summary>
        /// <param name="algorithmName">The algorithm name (case-insensitive).</param>
        /// <returns>The matching strategy, or null if not found.</returns>
        public ICompressionStrategy? GetStrategy(string algorithmName)
        {
            _strategies.TryGetValue(algorithmName, out var strategy);
            return strategy;
        }

        /// <summary>
        /// Gets all registered strategy names.
        /// </summary>
        public IReadOnlyCollection<string> GetRegisteredAlgorithms() => _strategies.Keys.ToArray();

        /// <summary>
        /// Selects the best compression strategy for the given data based on content analysis.
        /// </summary>
        /// <param name="sample">A sample of the data to compress (first 4096 bytes recommended).</param>
        /// <param name="preferSpeed">If true, prefer faster algorithms over better ratios.</param>
        /// <returns>The recommended strategy.</returns>
        public ICompressionStrategy SelectBestStrategy(ReadOnlySpan<byte> sample, bool preferSpeed = false)
        {
            if (_strategies.IsEmpty)
                throw new InvalidOperationException("No compression strategies registered.");

            // Calculate entropy to determine compressibility
            double entropy = CalculateEntropy(sample);

            // High entropy = already compressed or encrypted, use fast pass-through
            if (entropy > 7.8)
                return GetStrategy("LZ4") ?? _strategies.Values.First();

            // Low entropy (text, structured) = use high-ratio algorithms
            if (entropy < 4.0 && !preferSpeed)
                return GetStrategy("Zstd") ?? GetStrategy("Brotli") ?? _strategies.Values.First();

            // Medium entropy with speed preference
            if (preferSpeed)
                return GetStrategy("LZ4") ?? GetStrategy("Snappy") ?? _strategies.Values.First();

            // Default: balanced Zstd
            return GetStrategy("Zstd") ?? GetStrategy("GZip") ?? _strategies.Values.First();
        }

        /// <summary>
        /// Sets the active strategy for pipeline compression/decompression.
        /// </summary>
        /// <param name="algorithmName">The algorithm name to activate.</param>
        public void SetActiveStrategy(string algorithmName)
        {
            if (!_strategies.TryGetValue(algorithmName, out var strategy))
                throw new ArgumentException($"Unknown algorithm: {algorithmName}");
            _activeStrategy = strategy;
        }

        /// <inheritdoc/>
        public override async Task<Stream> OnWriteAsync(Stream input, IKernelContext context, Dictionary<string, object> args, CancellationToken ct = default)
        {
            // Read input stream to byte array
            using var ms = new MemoryStream(65536);
            await input.CopyToAsync(ms, ct);
            var data = ms.ToArray();

            // Select strategy and compress
            var strategy = _activeStrategy ?? SelectBestStrategy(data.AsSpan(0, Math.Min(data.Length, 4096)));
            var compressed = strategy.Compress(data);

            // Store the algorithm name in args for decompression
            args["compression.algorithm"] = strategy.Characteristics.AlgorithmName;

            // Return compressed stream
            return new MemoryStream(compressed);
        }

        /// <inheritdoc/>
        public override async Task<Stream> OnReadAsync(Stream stored, IKernelContext context, Dictionary<string, object> args, CancellationToken ct = default)
        {
            // Read stored stream to byte array
            using var ms = new MemoryStream(65536);
            await stored.CopyToAsync(ms, ct);
            var data = ms.ToArray();

            // Use stored algorithm from metadata instead of guessing via entropy
            ICompressionStrategy? strategy = null;
            if (args.TryGetValue("compression.algorithm", out var algorithmObj) && algorithmObj is string algorithmName)
            {
                strategy = GetStrategy(algorithmName);
            }

            // Fallback to active strategy or default
            strategy ??= _activeStrategy ?? GetStrategy("Zstd") ?? _strategies.Values.FirstOrDefault();

            if (strategy == null)
            {
                throw new InvalidOperationException("No compression strategy available for decompression");
            }

            var decompressed = strategy.Decompress(data);

            // Return decompressed stream
            return new MemoryStream(decompressed);
        }

        /// <summary>
        /// Initializes the plugin and discovers compression strategies.
        /// </summary>
        public UltimateCompressionPlugin()
        {
            DiscoverAndRegisterStrategies();
        }

        /// <summary>
        /// Discovers and registers all compression strategies via reflection.
        /// </summary>
        private void DiscoverAndRegisterStrategies()
        {
            var strategyTypes = GetType().Assembly
                .GetTypes()
                .Where(t => !t.IsAbstract && typeof(CompressionStrategyBase).IsAssignableFrom(t));

            foreach (var strategyType in strategyTypes)
            {
                try
                {
                    if (Activator.CreateInstance(strategyType) is CompressionStrategyBase strategy)
                    {
                        _strategies[strategy.Characteristics.AlgorithmName] = strategy;
                    }
                }
                catch
                {
                    // Strategy failed to instantiate, skip
                }
            }
        }

        /// <summary>
        /// Calculates Shannon entropy of data (0-8 bits per byte).
        /// </summary>
        private static double CalculateEntropy(ReadOnlySpan<byte> data)
        {
            if (data.Length == 0) return 0;

            Span<int> freq = stackalloc int[256];
            freq.Clear();
            foreach (var b in data) freq[b]++;

            double entropy = 0.0;
            double len = data.Length;
            for (int i = 0; i < 256; i++)
            {
                if (freq[i] == 0) continue;
                double p = freq[i] / len;
                entropy -= p * Math.Log2(p);
            }
            return entropy;
        }

        /// <inheritdoc/>
        protected override IReadOnlyList<SDK.Contracts.RegisteredCapability> DeclaredCapabilities
        {
            get
            {
                var capabilities = new List<SDK.Contracts.RegisteredCapability>();

                capabilities.Add(new SDK.Contracts.RegisteredCapability
                {
                    CapabilityId = $"{Id}.compress",
                    DisplayName = $"{Name} - Compress",
                    Description = "Compress data",
                    Category = SDK.Contracts.CapabilityCategory.Compression,
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = new[] { "compression", "data-transformation" }
                });

                capabilities.Add(new SDK.Contracts.RegisteredCapability
                {
                    CapabilityId = $"{Id}.decompress",
                    DisplayName = $"{Name} - Decompress",
                    Description = "Decompress data",
                    Category = SDK.Contracts.CapabilityCategory.Compression,
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = new[] { "compression", "data-transformation" }
                });

                foreach (var kvp in _strategies)
                {
                    var strategy = kvp.Value;
                    var chars = strategy.Characteristics;
                    var tags = new List<string> { "compression", "strategy", chars.AlgorithmName.ToLowerInvariant() };
                    if (chars.SupportsStreaming) tags.Add("streaming");
                    if (chars.SupportsParallelCompression) tags.Add("parallel");

                    capabilities.Add(new SDK.Contracts.RegisteredCapability
                    {
                        CapabilityId = $"{Id}.strategy.{chars.AlgorithmName.ToLowerInvariant()}",
                        DisplayName = $"{chars.AlgorithmName} - {strategy.Level}",
                        Description = $"{chars.AlgorithmName} compression at {strategy.Level} level",
                        Category = SDK.Contracts.CapabilityCategory.Compression,
                        SubCategory = chars.AlgorithmName,
                        PluginId = Id,
                        PluginName = Name,
                        PluginVersion = Version,
                        Tags = tags.ToArray(),
                        Priority = chars.SupportsStreaming ? 60 : 50,
                        Metadata = new Dictionary<string, object>
                        {
                            ["algorithm"] = chars.AlgorithmName,
                            ["supportsStreaming"] = chars.SupportsStreaming,
                            ["supportsParallelCompression"] = chars.SupportsParallelCompression,
                            ["compressionRatio"] = chars.TypicalCompressionRatio,
                            ["compressionSpeed"] = chars.CompressionSpeed,
                            ["decompressionSpeed"] = chars.DecompressionSpeed
                        },
                        SemanticDescription = $"Compress using {chars.AlgorithmName} algorithm"
                    });
                }

                return capabilities;
            }
        }

        /// <inheritdoc/>
        protected override IReadOnlyList<SDK.AI.KnowledgeObject> GetStaticKnowledge()
        {
            var knowledge = new List<SDK.AI.KnowledgeObject>(base.GetStaticKnowledge());

            var strategies = _strategies.Values.ToList();
            if (strategies.Any())
            {
                knowledge.Add(new SDK.AI.KnowledgeObject
                {
                    Id = $"{Id}.strategies",
                    Topic = "compression.strategies",
                    SourcePluginId = Id,
                    SourcePluginName = Name,
                    KnowledgeType = "capability",
                    Description = $"{strategies.Count} compression strategies available",
                    Payload = new Dictionary<string, object>
                    {
                        ["count"] = strategies.Count,
                        ["algorithms"] = strategies.Select(s => s.Characteristics.AlgorithmName).Distinct().ToArray(),
                        ["streamingCount"] = strategies.Count(s => s.Characteristics.SupportsStreaming),
                        ["parallelCount"] = strategies.Count(s => s.Characteristics.SupportsParallelCompression)
                    },
                    Tags = new[] { "compression", "strategies", "summary" }
                });
            }

            return knowledge;
        }

        /// <inheritdoc/>
        public override Task OnMessageAsync(PluginMessage message)
        {
            switch (message.Type)
            {
                case "compression.ultimate.list":
                    // Return list of available algorithms
                    // Note: Reply mechanism would need to be handled by caller
                    break;

                case "compression.ultimate.select":
                    if (message.Payload.TryGetValue("algorithm", out var algoObj) && algoObj is string algoName)
                        SetActiveStrategy(algoName);
                    break;
            }

            return base.OnMessageAsync(message);
        }

        #region Intelligence Integration

        /// <summary>
        /// Semantic description for AI discovery.
        /// </summary>
        public string SemanticDescription =>
            "Ultimate compression plugin providing 50+ compression algorithms. " +
            "Supports automatic content-aware algorithm selection, parallel compression, " +
            "and real-time benchmarking for optimal compression across all workloads.";

        /// <summary>
        /// Semantic tags for AI discovery.
        /// </summary>
        public string[] SemanticTags => new[]
        {
            "compression", "lz4", "zstd", "brotli", "gzip", "deflate",
            "streaming", "parallel", "content-aware"
        };

        /// <summary>
        /// Called when Intelligence becomes available - register compression capabilities.
        /// </summary>
        protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct)
        {
            await base.OnStartWithIntelligenceAsync(ct);

            // Register compression capabilities with Intelligence
            if (MessageBus != null)
            {
                var strategies = _strategies.Values.ToList();

                await MessageBus.PublishAsync(IntelligenceTopics.QueryCapability, new PluginMessage
                {
                    Type = "capability.register",
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["pluginId"] = Id,
                        ["pluginName"] = Name,
                        ["pluginType"] = "compression",
                        ["capabilities"] = new Dictionary<string, object>
                        {
                            ["strategyCount"] = strategies.Count,
                            ["algorithms"] = strategies.Select(s => s.Characteristics.AlgorithmName).Distinct().ToArray(),
                            ["streamingCount"] = strategies.Count(s => s.Characteristics.SupportsStreaming),
                            ["parallelCount"] = strategies.Count(s => s.Characteristics.SupportsParallelCompression),
                            ["supportsCompressionRecommendation"] = true
                        },
                        ["semanticDescription"] = SemanticDescription,
                        ["tags"] = SemanticTags
                    }
                }, ct);

                // Subscribe to compression recommendation requests
                SubscribeToCompressionRecommendationRequests();
            }
        }

        /// <summary>
        /// Subscribes to Intelligence compression recommendation requests.
        /// </summary>
        private void SubscribeToCompressionRecommendationRequests()
        {
            if (MessageBus == null) return;

            MessageBus.Subscribe(IntelligenceTopics.RequestCompressionRecommendation, async msg =>
            {
                if (msg.Payload.TryGetValue("contentType", out var ctObj) && ctObj is string contentType &&
                    msg.Payload.TryGetValue("contentSize", out var csObj) && csObj is long contentSize)
                {
                    var preferSpeed = msg.Payload.TryGetValue("preferSpeed", out var psObj) && psObj is true;
                    var recommendation = RecommendCompressionStrategy(contentType, contentSize, preferSpeed);

                    await MessageBus.PublishAsync(IntelligenceTopics.RequestCompressionRecommendationResponse, new PluginMessage
                    {
                        Type = "compression-recommendation.response",
                        CorrelationId = msg.CorrelationId,
                        Source = Id,
                        Payload = new Dictionary<string, object>
                        {
                            ["success"] = true,
                            ["algorithm"] = recommendation.Algorithm,
                            ["compressionLevel"] = recommendation.Level,
                            ["estimatedRatio"] = recommendation.EstimatedRatio,
                            ["reasoning"] = recommendation.Reasoning,
                            ["isAlreadyCompressed"] = recommendation.IsAlreadyCompressed,
                            ["shouldSkip"] = recommendation.ShouldSkip
                        }
                    });
                }
            });
        }

        /// <summary>
        /// Recommends a compression strategy based on content characteristics.
        /// </summary>
        private (string Algorithm, int Level, double EstimatedRatio, string Reasoning, bool IsAlreadyCompressed, bool ShouldSkip)
            RecommendCompressionStrategy(string contentType, long contentSize, bool preferSpeed)
        {
            // Check for already-compressed formats
            var compressedTypes = new[] { "image/jpeg", "image/png", "image/gif", "video/", "audio/", "application/zip", "application/x-gzip", "application/x-7z" };
            if (compressedTypes.Any(t => contentType.Contains(t, StringComparison.OrdinalIgnoreCase)))
            {
                return ("None", 0, 1.0, "Content is already compressed - skipping to avoid expansion", true, true);
            }

            // Very small files: skip compression
            if (contentSize < 100)
            {
                return ("None", 0, 1.0, "File too small for effective compression", false, true);
            }

            // Speed preference
            if (preferSpeed)
            {
                return ("LZ4", 1, 0.6, "LZ4 selected for maximum speed with acceptable compression ratio", false, false);
            }

            // Text/JSON: high compression with Brotli
            if (contentType.Contains("text") || contentType.Contains("json") || contentType.Contains("xml"))
            {
                return ("Brotli", 6, 0.3, "Brotli selected for excellent text/structured data compression", false, false);
            }

            // Large files: balanced Zstd
            if (contentSize > 10 * 1024 * 1024) // > 10MB
            {
                return ("Zstd", 3, 0.45, "Zstd selected for large files - good speed/ratio balance", false, false);
            }

            // Default: Zstd at medium level
            return ("Zstd", 3, 0.5, "Zstd selected as balanced general-purpose algorithm", false, false);
        }

        /// <inheritdoc/>
        protected override Task OnStartCoreAsync(CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        #endregion
    }
}
