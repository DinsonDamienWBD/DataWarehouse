using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.IO.Compression;

namespace DataWarehouse.Plugins.BrotliCompression
{
    /// <summary>
    /// RFC 7932 Brotli compression plugin for DataWarehouse.
    /// Provides high-ratio compression optimized for web content and general storage.
    ///
    /// Features:
    /// - Full RFC 7932 compliance using .NET BrotliStream
    /// - Configurable compression levels (Optimal, Fastest, SmallestSize, NoCompression)
    /// - Configurable buffer size (default 81920 bytes)
    /// - Streaming compression for memory efficiency
    /// - Automatic incompressible data detection
    /// - Compression ratio tracking and statistics
    /// - Header with original size for efficient decompression buffer allocation
    ///
    /// Thread Safety: All operations are thread-safe with atomic statistics tracking.
    ///
    /// Performance Characteristics:
    /// - Optimal: Best balance of speed and compression (default)
    /// - Fastest: Prioritizes speed over compression ratio
    /// - SmallestSize: Maximum compression, slower processing (best ratios)
    /// - NoCompression: Store only mode for pre-compressed data
    ///
    /// Message Commands:
    /// - compression.brotli.configure: Configure compression settings
    /// - compression.brotli.stats: Get compression statistics
    /// - compression.brotli.analyze: Analyze data compressibility
    /// </summary>
    public sealed class BrotliCompressionPlugin : PipelinePluginBase, IDisposable
    {
        private readonly BrotliCompressionConfig _config;
        private readonly object _statsLock = new();
        private long _compressionCount;
        private long _decompressionCount;
        private long _totalBytesIn;
        private long _totalBytesOut;
        private long _bypassCount;
        private double _averageCompressionRatio;
        private bool _disposed;

        /// <summary>
        /// Header version for Brotli-compressed data.
        /// </summary>
        private const byte HeaderVersion = 0x42; // 'B' for Brotli

        /// <summary>
        /// Magic bytes for RFC 7932 Brotli identification.
        /// </summary>
        private static readonly byte[] MagicBytes = { 0x42, 0x52, 0x54, 0x4C }; // "BRTL"

        /// <summary>
        /// Header size: Magic(4) + Version(1) + Level(1) + OriginalSize(8) + Flags(1)
        /// </summary>
        private const int HeaderSize = 15;

        /// <summary>
        /// Minimum size to attempt compression (smaller data may expand).
        /// </summary>
        private const int MinCompressionThreshold = 64;

        /// <summary>
        /// Threshold for compression ratio below which we skip compression.
        /// </summary>
        private const double MinEffectiveCompressionRatio = 0.95;

        /// <inheritdoc/>
        public override string Id => "datawarehouse.plugins.compression.brotli";

        /// <inheritdoc/>
        public override string Name => "Brotli Compression";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <inheritdoc/>
        public override string SubCategory => "Compression";

        /// <inheritdoc/>
        public override int QualityLevel => _config.CompressionLevel switch
        {
            CompressionLevel.Fastest => 40,
            CompressionLevel.Optimal => 80,
            CompressionLevel.SmallestSize => 95,
            _ => 70
        };

        /// <inheritdoc/>
        public override int DefaultOrder => 50;

        /// <inheritdoc/>
        public override bool AllowBypass => true;

        /// <inheritdoc/>
        public override string[] IncompatibleStages => new[] { "compression.gzip", "compression.deflate", "compression.lz4" };

        /// <summary>
        /// Initializes a new instance of the Brotli compression plugin.
        /// </summary>
        /// <param name="config">Optional configuration. If null, defaults are used.</param>
        public BrotliCompressionPlugin(BrotliCompressionConfig? config = null)
        {
            _config = config ?? new BrotliCompressionConfig();
        }

        /// <inheritdoc/>
        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>
            {
                new() { Name = "compression.brotli.compress", DisplayName = "Compress", Description = "Compress data using RFC 7932 Brotli" },
                new() { Name = "compression.brotli.decompress", DisplayName = "Decompress", Description = "Decompress Brotli-compressed data" },
                new() { Name = "compression.brotli.configure", DisplayName = "Configure", Description = "Configure compression settings" },
                new() { Name = "compression.brotli.stats", DisplayName = "Statistics", Description = "Get compression statistics" },
                new() { Name = "compression.brotli.analyze", DisplayName = "Analyze", Description = "Analyze data compressibility" }
            };
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["Algorithm"] = "Brotli";
            metadata["RFC"] = "7932";
            metadata["CompressionLevel"] = _config.CompressionLevel.ToString();
            metadata["BufferSize"] = _config.BufferSize;
            metadata["SupportsStreaming"] = true;
            metadata["SupportsBypass"] = true;
            metadata["MinCompressionThreshold"] = MinCompressionThreshold;

            lock (_statsLock)
            {
                metadata["AverageCompressionRatio"] = _averageCompressionRatio;
            }

            return metadata;
        }

        /// <inheritdoc/>
        public override Task OnMessageAsync(PluginMessage message)
        {
            return message.Type switch
            {
                "compression.brotli.configure" => HandleConfigureAsync(message),
                "compression.brotli.stats" => HandleStatsAsync(message),
                "compression.brotli.analyze" => HandleAnalyzeAsync(message),
                _ => base.OnMessageAsync(message)
            };
        }

        /// <summary>
        /// Compresses data using RFC 7932 Brotli algorithm.
        /// </summary>
        /// <param name="input">The uncompressed input stream.</param>
        /// <param name="context">The kernel context.</param>
        /// <param name="args">
        /// Optional arguments:
        /// - compressionLevel: CompressionLevel override
        /// - bypassIfIncompressible: Whether to bypass compression for incompressible data
        /// </param>
        /// <returns>Compressed data stream with header.</returns>
        public override Stream OnWrite(Stream input, IKernelContext context, Dictionary<string, object> args)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var compressionLevel = GetCompressionLevel(args);
            var bypassIfIncompressible = !args.TryGetValue("bypassIfIncompressible", out var bypass) || bypass is not false;

            // Read input data
            byte[] inputData;
            using (var inputMs = new MemoryStream())
            {
                input.CopyTo(inputMs);
                inputData = inputMs.ToArray();
            }

            var originalSize = inputData.Length;

            // Check if compression should be bypassed for small data
            if (originalSize < MinCompressionThreshold && compressionLevel != CompressionLevel.NoCompression)
            {
                context.LogDebug($"Brotli: Bypassing compression for small data ({originalSize} bytes)");
                return CreateBypassedOutput(inputData, originalSize, context);
            }

            // Compress the data
            byte[] compressedData;
            using (var compressedMs = new MemoryStream())
            {
                using (var brotliStream = new BrotliStream(compressedMs, compressionLevel, leaveOpen: true))
                {
                    brotliStream.Write(inputData, 0, inputData.Length);
                }
                compressedData = compressedMs.ToArray();
            }

            var compressionRatio = originalSize > 0 ? (double)compressedData.Length / originalSize : 1.0;

            // Check if compression is effective
            if (bypassIfIncompressible && compressionRatio > MinEffectiveCompressionRatio)
            {
                context.LogDebug($"Brotli: Bypassing compression (ratio {compressionRatio:P1} exceeds threshold)");
                Interlocked.Increment(ref _bypassCount);
                return CreateBypassedOutput(inputData, originalSize, context);
            }

            // Build output with header
            var output = new MemoryStream();
            var writer = new BinaryWriter(output);

            // Write header
            writer.Write(MagicBytes);
            writer.Write(HeaderVersion);
            writer.Write((byte)compressionLevel);
            writer.Write((long)originalSize);
            writer.Write((byte)BrotliFlags.Compressed);

            // Write compressed data
            writer.Write(compressedData);

            // Update statistics
            UpdateCompressionStats(originalSize, compressedData.Length);

            context.LogDebug($"Brotli: Compressed {originalSize} -> {compressedData.Length} bytes (ratio: {compressionRatio:P1})");

            output.Position = 0;
            return output;
        }

        /// <summary>
        /// Decompresses RFC 7932 Brotli-compressed data.
        /// </summary>
        /// <param name="stored">The compressed input stream.</param>
        /// <param name="context">The kernel context.</param>
        /// <param name="args">Optional arguments (none currently used).</param>
        /// <returns>Decompressed data stream.</returns>
        public override Stream OnRead(Stream stored, IKernelContext context, Dictionary<string, object> args)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            using var inputMs = new MemoryStream();
            stored.CopyTo(inputMs);
            inputMs.Position = 0;

            var reader = new BinaryReader(inputMs);

            // Read and validate header
            var magic = reader.ReadBytes(4);
            if (!magic.SequenceEqual(MagicBytes))
            {
                throw new InvalidDataException("Invalid Brotli header: magic bytes mismatch");
            }

            var version = reader.ReadByte();
            if (version != HeaderVersion)
            {
                throw new InvalidDataException($"Unsupported Brotli header version: 0x{version:X2}");
            }

            var level = (CompressionLevel)reader.ReadByte();
            var originalSize = reader.ReadInt64();
            var flags = (BrotliFlags)reader.ReadByte();

            if (originalSize < 0 || originalSize > int.MaxValue)
            {
                throw new InvalidDataException($"Invalid original size in header: {originalSize}");
            }

            // Read compressed/stored data
            var remainingLength = (int)(inputMs.Length - inputMs.Position);
            var compressedData = reader.ReadBytes(remainingLength);

            byte[] decompressedData;

            if ((flags & BrotliFlags.Stored) == BrotliFlags.Stored)
            {
                // Data was stored without compression
                decompressedData = compressedData;
                context.LogDebug($"Brotli: Retrieved stored data ({decompressedData.Length} bytes)");
            }
            else
            {
                // Decompress the data using configurable buffer size
                decompressedData = new byte[originalSize];

                using var compressedMs = new MemoryStream(compressedData);
                using var brotliStream = new BrotliStream(compressedMs, CompressionMode.Decompress);

                var totalRead = 0;
                int bytesRead;
                var bufferSize = Math.Min(_config.BufferSize, (int)originalSize - totalRead);

                while (totalRead < originalSize &&
                       (bytesRead = brotliStream.Read(decompressedData, totalRead, Math.Min(bufferSize, (int)originalSize - totalRead))) > 0)
                {
                    totalRead += bytesRead;
                }

                if (totalRead != originalSize)
                {
                    throw new InvalidDataException($"Decompression size mismatch: expected {originalSize}, got {totalRead}");
                }

                context.LogDebug($"Brotli: Decompressed {compressedData.Length} -> {decompressedData.Length} bytes");
            }

            Interlocked.Increment(ref _decompressionCount);

            return new MemoryStream(decompressedData);
        }

        /// <summary>
        /// Compresses data asynchronously using RFC 7932 Brotli algorithm.
        /// </summary>
        /// <param name="data">The uncompressed data.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Compressed data.</returns>
        public async Task<byte[]> CompressAsync(byte[] data, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            using var output = new MemoryStream();
            await using (var brotliStream = new BrotliStream(output, _config.CompressionLevel, leaveOpen: true))
            {
                await brotliStream.WriteAsync(data, cancellationToken);
            }

            var compressedData = output.ToArray();

            lock (_statsLock)
            {
                _totalBytesIn += data.Length;
                _totalBytesOut += compressedData.Length;
                _compressionCount++;
            }

            return compressedData;
        }

        /// <summary>
        /// Decompresses data asynchronously using RFC 7932 Brotli algorithm.
        /// </summary>
        /// <param name="data">The compressed data.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Decompressed data.</returns>
        public async Task<byte[]> DecompressAsync(byte[] data, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            using var input = new MemoryStream(data);
            using var output = new MemoryStream();
            await using (var brotliStream = new BrotliStream(input, CompressionMode.Decompress))
            {
                await brotliStream.CopyToAsync(output, _config.BufferSize, cancellationToken);
            }

            var decompressedData = output.ToArray();

            Interlocked.Increment(ref _decompressionCount);

            return decompressedData;
        }

        /// <summary>
        /// Analyzes data to estimate compressibility.
        /// </summary>
        /// <param name="data">The data to analyze.</param>
        /// <returns>Compressibility analysis result.</returns>
        public BrotliCompressibilityAnalysis AnalyzeCompressibility(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return new BrotliCompressibilityAnalysis
                {
                    OriginalSize = 0,
                    EstimatedCompressedSize = 0,
                    EstimatedRatio = 1.0,
                    Recommendation = BrotliCompressionRecommendation.NoCompression,
                    Entropy = 0
                };
            }

            // Calculate byte frequency distribution
            var frequencies = new int[256];
            foreach (var b in data)
            {
                frequencies[b]++;
            }

            // Calculate Shannon entropy
            double entropy = 0;
            var length = data.Length;
            for (var i = 0; i < 256; i++)
            {
                if (frequencies[i] > 0)
                {
                    var probability = (double)frequencies[i] / length;
                    entropy -= probability * Math.Log2(probability);
                }
            }

            // Normalize entropy (0-8 for bytes)
            var normalizedEntropy = entropy / 8.0;

            // Estimate compression ratio based on entropy
            // Brotli typically achieves better ratios than deflate
            // High entropy (>0.9) = incompressible
            // Medium entropy (0.5-0.9) = moderate compression
            // Low entropy (<0.5) = high compression
            var estimatedRatio = 0.08 + (normalizedEntropy * 0.85); // Brotli is more efficient
            var estimatedCompressedSize = (long)(data.Length * estimatedRatio);

            // Try actual compression on a sample for better estimate
            if (data.Length > 1024)
            {
                var sampleSize = Math.Min(4096, data.Length);
                var sample = new byte[sampleSize];
                Array.Copy(data, sample, sampleSize);

                using var compressedSample = new MemoryStream();
                using (var brotli = new BrotliStream(compressedSample, CompressionLevel.Fastest, true))
                {
                    brotli.Write(sample, 0, sample.Length);
                }

                var sampleRatio = (double)compressedSample.Length / sampleSize;
                estimatedRatio = (estimatedRatio + sampleRatio) / 2; // Average with entropy estimate
                estimatedCompressedSize = (long)(data.Length * estimatedRatio);
            }

            var recommendation = normalizedEntropy switch
            {
                > 0.95 => BrotliCompressionRecommendation.NoCompression,
                > 0.85 => BrotliCompressionRecommendation.Fastest,
                > 0.5 => BrotliCompressionRecommendation.Optimal,
                _ => BrotliCompressionRecommendation.SmallestSize
            };

            return new BrotliCompressibilityAnalysis
            {
                OriginalSize = data.Length,
                EstimatedCompressedSize = estimatedCompressedSize,
                EstimatedRatio = estimatedRatio,
                Recommendation = recommendation,
                Entropy = entropy,
                NormalizedEntropy = normalizedEntropy,
                UniqueByteCount = frequencies.Count(f => f > 0)
            };
        }

        /// <summary>
        /// Gets current compression statistics.
        /// </summary>
        /// <returns>Statistics snapshot.</returns>
        public BrotliCompressionStatistics GetStatistics()
        {
            lock (_statsLock)
            {
                return new BrotliCompressionStatistics
                {
                    CompressionCount = _compressionCount,
                    DecompressionCount = Interlocked.Read(ref _decompressionCount),
                    BypassCount = Interlocked.Read(ref _bypassCount),
                    TotalBytesIn = _totalBytesIn,
                    TotalBytesOut = _totalBytesOut,
                    AverageCompressionRatio = _averageCompressionRatio,
                    TotalSpaceSaved = _totalBytesIn - _totalBytesOut,
                    OverallCompressionRatio = _totalBytesIn > 0 ? (double)_totalBytesOut / _totalBytesIn : 1.0,
                    SpaceSavingsPercent = _totalBytesIn > 0 ? (1.0 - (double)_totalBytesOut / _totalBytesIn) * 100 : 0
                };
            }
        }

        /// <summary>
        /// Resets compression statistics.
        /// </summary>
        public void ResetStatistics()
        {
            lock (_statsLock)
            {
                _compressionCount = 0;
                _decompressionCount = 0;
                _totalBytesIn = 0;
                _totalBytesOut = 0;
                _bypassCount = 0;
                _averageCompressionRatio = 0;
            }
        }

        private Stream CreateBypassedOutput(byte[] data, int originalSize, IKernelContext context)
        {
            var output = new MemoryStream();
            var writer = new BinaryWriter(output);

            // Write header with stored flag
            writer.Write(MagicBytes);
            writer.Write(HeaderVersion);
            writer.Write((byte)CompressionLevel.NoCompression);
            writer.Write((long)originalSize);
            writer.Write((byte)BrotliFlags.Stored);

            // Write uncompressed data
            writer.Write(data);

            // Update statistics
            UpdateCompressionStats(originalSize, data.Length);

            output.Position = 0;
            return output;
        }

        private CompressionLevel GetCompressionLevel(Dictionary<string, object> args)
        {
            if (args.TryGetValue("compressionLevel", out var levelObj))
            {
                if (levelObj is CompressionLevel level)
                {
                    return level;
                }

                if (levelObj is string levelStr && Enum.TryParse<CompressionLevel>(levelStr, true, out var parsedLevel))
                {
                    return parsedLevel;
                }

                if (levelObj is int levelInt && Enum.IsDefined(typeof(CompressionLevel), levelInt))
                {
                    return (CompressionLevel)levelInt;
                }
            }

            return _config.CompressionLevel;
        }

        private void UpdateCompressionStats(long bytesIn, long bytesOut)
        {
            lock (_statsLock)
            {
                _compressionCount++;
                _totalBytesIn += bytesIn;
                _totalBytesOut += bytesOut;

                // Update running average compression ratio
                var currentRatio = bytesIn > 0 ? (double)bytesOut / bytesIn : 1.0;
                _averageCompressionRatio = (_averageCompressionRatio * (_compressionCount - 1) + currentRatio) / _compressionCount;
            }
        }

        private Task HandleConfigureAsync(PluginMessage message)
        {
            if (message.Payload.TryGetValue("compressionLevel", out var levelObj))
            {
                if (levelObj is string levelStr && Enum.TryParse<CompressionLevel>(levelStr, true, out var level))
                {
                    _config.CompressionLevel = level;
                }
            }

            if (message.Payload.TryGetValue("bufferSize", out var bufferObj))
            {
                if (bufferObj is int bufferSize && bufferSize > 0)
                {
                    _config.BufferSize = bufferSize;
                }
                else if (bufferObj is string bufferStr && int.TryParse(bufferStr, out var parsedBuffer) && parsedBuffer > 0)
                {
                    _config.BufferSize = parsedBuffer;
                }
            }

            message.Payload["currentLevel"] = _config.CompressionLevel.ToString();
            message.Payload["currentBufferSize"] = _config.BufferSize;

            return Task.CompletedTask;
        }

        private Task HandleStatsAsync(PluginMessage message)
        {
            var stats = GetStatistics();

            message.Payload["compressionCount"] = stats.CompressionCount;
            message.Payload["decompressionCount"] = stats.DecompressionCount;
            message.Payload["bypassCount"] = stats.BypassCount;
            message.Payload["totalBytesIn"] = stats.TotalBytesIn;
            message.Payload["totalBytesOut"] = stats.TotalBytesOut;
            message.Payload["averageCompressionRatio"] = stats.AverageCompressionRatio;
            message.Payload["totalSpaceSaved"] = stats.TotalSpaceSaved;
            message.Payload["overallCompressionRatio"] = stats.OverallCompressionRatio;
            message.Payload["spaceSavingsPercent"] = stats.SpaceSavingsPercent;

            return Task.CompletedTask;
        }

        private Task HandleAnalyzeAsync(PluginMessage message)
        {
            if (!message.Payload.TryGetValue("data", out var dataObj) || dataObj is not byte[] data)
            {
                throw new ArgumentException("Missing 'data' parameter");
            }

            var analysis = AnalyzeCompressibility(data);

            message.Payload["originalSize"] = analysis.OriginalSize;
            message.Payload["estimatedCompressedSize"] = analysis.EstimatedCompressedSize;
            message.Payload["estimatedRatio"] = analysis.EstimatedRatio;
            message.Payload["recommendation"] = analysis.Recommendation.ToString();
            message.Payload["entropy"] = analysis.Entropy;
            message.Payload["normalizedEntropy"] = analysis.NormalizedEntropy;
            message.Payload["uniqueByteCount"] = analysis.UniqueByteCount;

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }

    /// <summary>
    /// Flags for Brotli header.
    /// </summary>
    [Flags]
    internal enum BrotliFlags : byte
    {
        None = 0,
        Compressed = 1,
        Stored = 2,
        Checksum = 4
    }

    /// <summary>
    /// Configuration for Brotli compression plugin.
    /// </summary>
    public sealed class BrotliCompressionConfig
    {
        /// <summary>
        /// Compression level to use.
        /// Default is Optimal.
        /// </summary>
        public CompressionLevel CompressionLevel { get; set; } = CompressionLevel.Optimal;

        /// <summary>
        /// Whether to bypass compression for incompressible data.
        /// Default is true.
        /// </summary>
        public bool BypassIncompressible { get; set; } = true;

        /// <summary>
        /// Buffer size for streaming operations.
        /// Default is 81920 (80KB).
        /// </summary>
        public int BufferSize { get; set; } = 81920;
    }

    /// <summary>
    /// Brotli compression recommendation levels.
    /// </summary>
    public enum BrotliCompressionRecommendation
    {
        /// <summary>
        /// Data should not be compressed (high entropy).
        /// </summary>
        NoCompression,

        /// <summary>
        /// Use fastest compression for speed.
        /// </summary>
        Fastest,

        /// <summary>
        /// Use optimal compression for balance.
        /// </summary>
        Optimal,

        /// <summary>
        /// Use maximum compression for smallest size.
        /// </summary>
        SmallestSize
    }

    /// <summary>
    /// Result of Brotli compressibility analysis.
    /// </summary>
    public sealed class BrotliCompressibilityAnalysis
    {
        /// <summary>
        /// Original data size in bytes.
        /// </summary>
        public long OriginalSize { get; init; }

        /// <summary>
        /// Estimated compressed size in bytes.
        /// </summary>
        public long EstimatedCompressedSize { get; init; }

        /// <summary>
        /// Estimated compression ratio (compressed/original).
        /// </summary>
        public double EstimatedRatio { get; init; }

        /// <summary>
        /// Recommended compression approach.
        /// </summary>
        public BrotliCompressionRecommendation Recommendation { get; init; }

        /// <summary>
        /// Shannon entropy of the data (0-8 for bytes).
        /// </summary>
        public double Entropy { get; init; }

        /// <summary>
        /// Normalized entropy (0-1, where 1 is random).
        /// </summary>
        public double NormalizedEntropy { get; init; }

        /// <summary>
        /// Number of unique byte values in the data.
        /// </summary>
        public int UniqueByteCount { get; init; }

        /// <summary>
        /// Estimated space savings percentage.
        /// </summary>
        public double EstimatedSavingsPercent => (1.0 - EstimatedRatio) * 100;
    }

    /// <summary>
    /// Brotli compression statistics snapshot.
    /// </summary>
    public sealed class BrotliCompressionStatistics
    {
        /// <summary>
        /// Total number of compression operations.
        /// </summary>
        public long CompressionCount { get; init; }

        /// <summary>
        /// Total number of decompression operations.
        /// </summary>
        public long DecompressionCount { get; init; }

        /// <summary>
        /// Number of times compression was bypassed.
        /// </summary>
        public long BypassCount { get; init; }

        /// <summary>
        /// Total bytes input for compression.
        /// </summary>
        public long TotalBytesIn { get; init; }

        /// <summary>
        /// Total bytes output after compression.
        /// </summary>
        public long TotalBytesOut { get; init; }

        /// <summary>
        /// Running average compression ratio.
        /// </summary>
        public double AverageCompressionRatio { get; init; }

        /// <summary>
        /// Total space saved in bytes.
        /// </summary>
        public long TotalSpaceSaved { get; init; }

        /// <summary>
        /// Overall compression ratio across all operations.
        /// </summary>
        public double OverallCompressionRatio { get; init; }

        /// <summary>
        /// Space savings as a percentage.
        /// </summary>
        public double SpaceSavingsPercent { get; init; }

        /// <summary>
        /// Total operations (compression + decompression).
        /// </summary>
        public long TotalOperations => CompressionCount + DecompressionCount;
    }
}
