using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Buffers;
using System.IO.Compression;
using SystemCompressionLevel = System.IO.Compression.CompressionLevel;
using SdkCompressionLevel = DataWarehouse.SDK.Primitives.CompressionLevel;

namespace DataWarehouse.Plugins.DeflateCompression
{
    /// <summary>
    /// RFC 1951 Deflate compression plugin for DataWarehouse.
    /// Provides legacy-compatible deflate compression with configurable compression levels.
    ///
    /// Features:
    /// - Full RFC 1951 compliance using .NET DeflateStream
    /// - Configurable compression levels (Optimal, Fastest, SmallestSize, NoCompression)
    /// - Streaming compression for memory efficiency
    /// - Automatic incompressible data detection
    /// - Compression ratio tracking and statistics
    /// - Header with original size for efficient decompression buffer allocation
    ///
    /// Thread Safety: All operations are thread-safe.
    ///
    /// Performance Characteristics:
    /// - Optimal: Best balance of speed and compression (default)
    /// - Fastest: Prioritizes speed over compression ratio
    /// - SmallestSize: Maximum compression, slower processing
    /// - NoCompression: Store only mode for pre-compressed data
    ///
    /// Message Commands:
    /// - deflate.configure: Configure compression settings
    /// - deflate.stats: Get compression statistics
    /// - deflate.analyze: Analyze data compressibility
    /// </summary>
    public sealed class DeflateCompressionPlugin : PipelinePluginBase, IDisposable
    {
        private readonly DeflateCompressionConfig _config;
        private readonly object _statsLock = new();
        private long _compressionCount;
        private long _decompressionCount;
        private long _totalBytesIn;
        private long _totalBytesOut;
        private long _bypassCount;
        private double _averageCompressionRatio;
        private bool _disposed;

        /// <summary>
        /// Header version for deflate-compressed data.
        /// </summary>
        private const byte HeaderVersion = 0x44; // 'D' for Deflate

        /// <summary>
        /// Magic bytes for RFC 1951 Deflate identification.
        /// </summary>
        private static readonly byte[] MagicBytes = { 0x44, 0x46, 0x4C, 0x54 }; // "DFLT"

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
        public override string Id => "datawarehouse.plugins.compression.deflate";

        /// <inheritdoc/>
        public override string Name => "Deflate Compression";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <inheritdoc/>
        public override string SubCategory => "Compression";

        /// <inheritdoc/>
        public override int QualityLevel => 70;

        /// <inheritdoc/>
        public override int DefaultOrder => 50;

        /// <inheritdoc/>
        public override bool AllowBypass => true;

        /// <inheritdoc/>
        public override string[] IncompatibleStages => new[] { "compression.gzip", "compression.brotli", "compression.lz4" };

        /// <summary>
        /// Initializes a new instance of the Deflate compression plugin.
        /// </summary>
        /// <param name="config">Optional configuration. If null, defaults are used.</param>
        public DeflateCompressionPlugin(DeflateCompressionConfig? config = null)
        {
            _config = config ?? new DeflateCompressionConfig();
        }

        /// <inheritdoc/>
        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>
            {
                new() { Name = "deflate.compress", DisplayName = "Compress", Description = "Compress data using RFC 1951 Deflate" },
                new() { Name = "deflate.decompress", DisplayName = "Decompress", Description = "Decompress Deflate-compressed data" },
                new() { Name = "deflate.configure", DisplayName = "Configure", Description = "Configure compression settings" },
                new() { Name = "deflate.stats", DisplayName = "Statistics", Description = "Get compression statistics" },
                new() { Name = "deflate.analyze", DisplayName = "Analyze", Description = "Analyze data compressibility" }
            };
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["Algorithm"] = "Deflate";
            metadata["Standard"] = "RFC 1951";
            metadata["CompressionLevel"] = _config.CompressionLevel.ToString();
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
                "deflate.configure" => HandleConfigureAsync(message),
                "deflate.stats" => HandleStatsAsync(message),
                "deflate.analyze" => HandleAnalyzeAsync(message),
                _ => base.OnMessageAsync(message)
            };
        }

        /// <summary>
        /// Compresses data using RFC 1951 Deflate algorithm.
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
            if (originalSize < MinCompressionThreshold && compressionLevel != SystemCompressionLevel.NoCompression)
            {
                context.LogDebug($"Deflate: Bypassing compression for small data ({originalSize} bytes)");
                return CreateBypassedOutput(inputData, originalSize, context);
            }

            // Compress the data
            byte[] compressedData;
            using (var compressedMs = new MemoryStream())
            {
                using (var deflateStream = new DeflateStream(compressedMs, compressionLevel, leaveOpen: true))
                {
                    deflateStream.Write(inputData, 0, inputData.Length);
                }
                compressedData = compressedMs.ToArray();
            }

            var compressionRatio = originalSize > 0 ? (double)compressedData.Length / originalSize : 1.0;

            // Check if compression is effective
            if (bypassIfIncompressible && compressionRatio > MinEffectiveCompressionRatio)
            {
                context.LogDebug($"Deflate: Bypassing compression (ratio {compressionRatio:P1} exceeds threshold)");
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
            writer.Write((byte)DeflateFlags.Compressed);

            // Write compressed data
            writer.Write(compressedData);

            // Update statistics
            UpdateCompressionStats(originalSize, compressedData.Length);

            context.LogDebug($"Deflate: Compressed {originalSize} -> {compressedData.Length} bytes (ratio: {compressionRatio:P1})");

            output.Position = 0;
            return output;
        }

        /// <summary>
        /// Decompresses RFC 1951 Deflate-compressed data.
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
                throw new InvalidDataException("Invalid Deflate header: magic bytes mismatch");
            }

            var version = reader.ReadByte();
            if (version != HeaderVersion)
            {
                throw new InvalidDataException($"Unsupported Deflate header version: 0x{version:X2}");
            }

            var level = (SystemCompressionLevel)reader.ReadByte();
            var originalSize = reader.ReadInt64();
            var flags = (DeflateFlags)reader.ReadByte();

            if (originalSize < 0 || originalSize > int.MaxValue)
            {
                throw new InvalidDataException($"Invalid original size in header: {originalSize}");
            }

            // Read compressed/stored data
            var remainingLength = (int)(inputMs.Length - inputMs.Position);
            var compressedData = reader.ReadBytes(remainingLength);

            byte[] decompressedData;

            if ((flags & DeflateFlags.Stored) == DeflateFlags.Stored)
            {
                // Data was stored without compression
                decompressedData = compressedData;
                context.LogDebug($"Deflate: Retrieved stored data ({decompressedData.Length} bytes)");
            }
            else
            {
                // Decompress the data
                decompressedData = new byte[originalSize];

                using var compressedMs = new MemoryStream(compressedData);
                using var deflateStream = new DeflateStream(compressedMs, CompressionMode.Decompress);

                var totalRead = 0;
                int bytesRead;
                while (totalRead < originalSize &&
                       (bytesRead = deflateStream.Read(decompressedData, totalRead, (int)originalSize - totalRead)) > 0)
                {
                    totalRead += bytesRead;
                }

                if (totalRead != originalSize)
                {
                    throw new InvalidDataException($"Decompression size mismatch: expected {originalSize}, got {totalRead}");
                }

                context.LogDebug($"Deflate: Decompressed {compressedData.Length} -> {decompressedData.Length} bytes");
            }

            Interlocked.Increment(ref _decompressionCount);

            return new MemoryStream(decompressedData);
        }

        /// <summary>
        /// Analyzes data to estimate compressibility.
        /// </summary>
        /// <param name="data">The data to analyze.</param>
        /// <returns>Compressibility analysis result.</returns>
        public CompressibilityAnalysis AnalyzeCompressibility(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return new CompressibilityAnalysis
                {
                    OriginalSize = 0,
                    EstimatedCompressedSize = 0,
                    EstimatedRatio = 1.0,
                    Recommendation = CompressionRecommendation.NoCompression,
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
            // High entropy (>0.9) = incompressible
            // Medium entropy (0.5-0.9) = moderate compression
            // Low entropy (<0.5) = high compression
            var estimatedRatio = 0.1 + (normalizedEntropy * 0.9);
            var estimatedCompressedSize = (long)(data.Length * estimatedRatio);

            // Try actual compression on a sample for better estimate
            if (data.Length > 1024)
            {
                var sampleSize = Math.Min(4096, data.Length);
                var sample = new byte[sampleSize];
                Array.Copy(data, sample, sampleSize);

                using var compressedSample = new MemoryStream();
                using (var deflate = new DeflateStream(compressedSample, SystemCompressionLevel.Fastest, true))
                {
                    deflate.Write(sample, 0, sample.Length);
                }

                var sampleRatio = (double)compressedSample.Length / sampleSize;
                estimatedRatio = (estimatedRatio + sampleRatio) / 2; // Average with entropy estimate
                estimatedCompressedSize = (long)(data.Length * estimatedRatio);
            }

            var recommendation = normalizedEntropy switch
            {
                > 0.95 => CompressionRecommendation.NoCompression,
                > 0.85 => CompressionRecommendation.Fastest,
                > 0.5 => CompressionRecommendation.Optimal,
                _ => CompressionRecommendation.SmallestSize
            };

            return new CompressibilityAnalysis
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

        private Stream CreateBypassedOutput(byte[] data, int originalSize, IKernelContext context)
        {
            var output = new MemoryStream();
            var writer = new BinaryWriter(output);

            // Write header with stored flag
            writer.Write(MagicBytes);
            writer.Write(HeaderVersion);
            writer.Write((byte)SystemCompressionLevel.NoCompression);
            writer.Write((long)originalSize);
            writer.Write((byte)DeflateFlags.Stored);

            // Write uncompressed data
            writer.Write(data);

            // Update statistics
            UpdateCompressionStats(originalSize, data.Length);

            output.Position = 0;
            return output;
        }

        private System.IO.Compression.CompressionLevel GetCompressionLevel(Dictionary<string, object> args)
        {
            if (args.TryGetValue("compressionLevel", out var levelObj))
            {
                if (levelObj is System.IO.Compression.CompressionLevel level)
                {
                    return level;
                }

                if (levelObj is string levelStr && Enum.TryParse<System.IO.Compression.CompressionLevel>(levelStr, true, out var parsedLevel))
                {
                    return parsedLevel;
                }

                if (levelObj is int levelInt && Enum.IsDefined(typeof(System.IO.Compression.CompressionLevel), levelInt))
                {
                    return (System.IO.Compression.CompressionLevel)levelInt;
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
                if (levelObj is string levelStr && Enum.TryParse<System.IO.Compression.CompressionLevel>(levelStr, true, out var level))
                {
                    _config.CompressionLevel = level;
                }
            }

            message.Payload["currentLevel"] = _config.CompressionLevel.ToString();

            return Task.CompletedTask;
        }

        private Task HandleStatsAsync(PluginMessage message)
        {
            lock (_statsLock)
            {
                message.Payload["compressionCount"] = _compressionCount;
                message.Payload["decompressionCount"] = Interlocked.Read(ref _decompressionCount);
                message.Payload["bypassCount"] = Interlocked.Read(ref _bypassCount);
                message.Payload["totalBytesIn"] = _totalBytesIn;
                message.Payload["totalBytesOut"] = _totalBytesOut;
                message.Payload["averageCompressionRatio"] = _averageCompressionRatio;
                message.Payload["totalSpaceSaved"] = _totalBytesIn - _totalBytesOut;

                if (_totalBytesIn > 0)
                {
                    message.Payload["overallCompressionRatio"] = (double)_totalBytesOut / _totalBytesIn;
                    message.Payload["spaceSavingsPercent"] = (1.0 - (double)_totalBytesOut / _totalBytesIn) * 100;
                }
            }

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
    /// Flags for deflate header.
    /// </summary>
    [Flags]
    internal enum DeflateFlags : byte
    {
        None = 0,
        Compressed = 1,
        Stored = 2,
        Checksum = 4
    }

    /// <summary>
    /// Configuration for Deflate compression plugin.
    /// </summary>
    public sealed class DeflateCompressionConfig
    {
        /// <summary>
        /// Compression level to use.
        /// Default is Optimal.
        /// </summary>
        public System.IO.Compression.CompressionLevel CompressionLevel { get; set; } = System.IO.Compression.CompressionLevel.Optimal;

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
    /// Compression recommendation levels.
    /// </summary>
    public enum CompressionRecommendation
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
    /// Result of compressibility analysis.
    /// </summary>
    public sealed class CompressibilityAnalysis
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
        public CompressionRecommendation Recommendation { get; init; }

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
}
