using System.Collections.Concurrent;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Compression;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompression
{
    /// <summary>
    /// Ultimate Compression plugin that consolidates 50+ compression algorithms as strategies.
    /// Provides automatic content-aware algorithm selection, parallel compression,
    /// and real-time benchmarking for optimal compression across all workloads.
    /// </summary>
    /// <remarks>
    /// Supported algorithm families:
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
    /// </remarks>
    public sealed class UltimateCompressionPlugin : PipelinePluginBase
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
        public override int DefaultOrder => 50;

        /// <inheritdoc/>
        public override bool AllowBypass => true;

        /// <inheritdoc/>
        public override int QualityLevel => 95;

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
        public override Stream OnWrite(Stream input, IKernelContext context, Dictionary<string, object> args)
        {
            // Read input stream to byte array
            using var ms = new MemoryStream();
            input.CopyTo(ms);
            var data = ms.ToArray();

            // Select strategy and compress
            var strategy = _activeStrategy ?? SelectBestStrategy(data.AsSpan(0, Math.Min(data.Length, 4096)));
            var compressed = strategy.Compress(data);

            // Return compressed stream
            return new MemoryStream(compressed);
        }

        /// <inheritdoc/>
        public override Stream OnRead(Stream stored, IKernelContext context, Dictionary<string, object> args)
        {
            // Read stored stream to byte array
            using var ms = new MemoryStream();
            stored.CopyTo(ms);
            var data = ms.ToArray();

            // Select strategy and decompress
            var strategy = _activeStrategy ?? SelectBestStrategy(data.AsSpan(0, Math.Min(data.Length, 4096)));
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
    }
}
