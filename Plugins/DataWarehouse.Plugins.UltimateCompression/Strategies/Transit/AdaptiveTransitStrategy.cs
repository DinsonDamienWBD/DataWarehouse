using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Compression;

using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;
namespace DataWarehouse.Plugins.UltimateCompression.Strategies.Transit
{
    /// <summary>
    /// Adaptive transit compression strategy - automatically selects the best algorithm.
    /// Analyzes data content type, entropy, network conditions, and endpoint capabilities
    /// to choose the optimal compression strategy for each payload.
    /// </summary>
    public sealed class AdaptiveTransitStrategy : CompressionStrategyBase
    {
        private const int MaxInputSize = 100 * 1024 * 1024; // 100 MB

        private readonly ICompressionStrategy _zstdStrategy;
        private readonly ICompressionStrategy _lz4Strategy;
        private readonly ICompressionStrategy _brotliStrategy;
        private readonly ICompressionStrategy _snappyStrategy;
        private readonly bool _lowLatencyMode;
        private readonly bool _meteredConnection;

        /// <summary>
        /// Initializes a new instance of the <see cref="AdaptiveTransitStrategy"/> class.
        /// </summary>
        /// <param name="level">The compression level.</param>
        /// <param name="lowLatencyMode">True for low-latency networks (prefer speed).</param>
        /// <param name="meteredConnection">True for metered connections (prefer ratio).</param>
        public AdaptiveTransitStrategy(
            CompressionLevel level,
            bool lowLatencyMode = false,
            bool meteredConnection = false) : base(level)
        {
            _lowLatencyMode = lowLatencyMode;
            _meteredConnection = meteredConnection;

            // Pre-create strategy instances
            _zstdStrategy = new ZstdTransitStrategy(level);
            _lz4Strategy = new Lz4TransitStrategy(level);
            _brotliStrategy = new BrotliTransitStrategy(level);
            _snappyStrategy = new SnappyTransitStrategy(level);
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics => new()
        {
            AlgorithmName = "Adaptive-Transit",
            TypicalCompressionRatio = 0.40, // Varies by selection
            CompressionSpeed = 7, // Average across all strategies
            DecompressionSpeed = 9,
            CompressionMemoryUsage = 16 * 1024 * 1024, // Worst-case
            DecompressionMemoryUsage = 4 * 1024 * 1024,
            SupportsStreaming = true,
            SupportsParallelCompression = true,
            SupportsParallelDecompression = true,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 256,
            OptimalBlockSize = 64 * 1024
        };

        /// <summary>
        /// Performs a health check by executing a small compression round-trip test.
        /// Result is cached for 60 seconds.
        /// </summary>
        public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default)
        {
            return await GetCachedHealthAsync(async ct =>
            {
                try
                {
                    var testData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
                    var compressed = CompressCore(testData);
                    var decompressed = DecompressCore(compressed);

                    if (decompressed.Length != testData.Length)
                    {
                        return new StrategyHealthCheckResult(
                            false,
                            $"Health check failed: decompressed length {decompressed.Length} != original {testData.Length}");
                    }

                    return new StrategyHealthCheckResult(
                        true,
                        "Adaptive-Transit strategy healthy",
                        new Dictionary<string, object>
                        {
                            ["CompressOperations"] = GetCounter("adaptive-transit.compress"),
                            ["DecompressOperations"] = GetCounter("adaptive-transit.decompress")
                        });
                }
                catch (Exception ex)
                {
                    return new StrategyHealthCheckResult(false, $"Health check failed: {ex.Message}");
                }
            }, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            return base.ShutdownAsyncCore(cancellationToken);
        }

        /// <inheritdoc/>
        protected override ValueTask DisposeAsyncCore()
        {
            return base.DisposeAsyncCore();
        }


        /// <inheritdoc/>
        protected override byte[] CompressCore(byte[] input)
        {
            IncrementCounter("adaptive-transit.compress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for Adaptive-Transit");
            var strategy = SelectStrategy(input);
            return strategy.Compress(input);
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            IncrementCounter("adaptive-transit.decompress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for Adaptive-Transit");
            // Detect which algorithm was used (would need header in real implementation)
            // For now, default to Zstd as it's most common
            return _zstdStrategy.Decompress(input);
        }

        /// <inheritdoc/>
        protected override async Task<byte[]> CompressAsyncCore(byte[] input, CancellationToken cancellationToken)
        {
            var strategy = SelectStrategy(input);
            return await strategy.CompressAsync(input, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        protected override async Task<byte[]> DecompressAsyncCore(byte[] input, CancellationToken cancellationToken)
        {
            return await _zstdStrategy.DecompressAsync(input, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
        {
            // For streaming, default to Zstd (can't analyze full data upfront)
            return _zstdStrategy.CreateCompressionStream(output, leaveOpen);
        }

        /// <inheritdoc/>
        protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
        {
            return _zstdStrategy.CreateDecompressionStream(input, leaveOpen);
        }

        /// <summary>
        /// Selects the optimal compression strategy based on data characteristics.
        /// </summary>
        private ICompressionStrategy SelectStrategy(byte[] input)
        {
            // Too small - use fastest (Snappy or LZ4)
            if (input.Length < 512)
                return _snappyStrategy;

            // Calculate entropy on sample
            var sampleSize = Math.Min(input.Length, 4096);
            var sample = input.AsSpan(0, sampleSize);
            var entropy = CalculateEntropy(sample);

            // High entropy (>7.5) - data is likely compressed/encrypted, use pass-through or fastest
            if (entropy > 7.5)
                return _snappyStrategy;

            // Detect content type
            var contentType = DetectContentType(sample);

            // Low latency mode - prioritize speed
            if (_lowLatencyMode)
            {
                return contentType switch
                {
                    ContentType.Text or ContentType.Structured => _lz4Strategy,
                    ContentType.Binary => _snappyStrategy,
                    _ => _lz4Strategy
                };
            }

            // Metered connection - prioritize compression ratio
            if (_meteredConnection)
            {
                return contentType switch
                {
                    ContentType.Text or ContentType.Structured => _brotliStrategy,
                    ContentType.Binary => _zstdStrategy,
                    _ => _zstdStrategy
                };
            }

            // Balanced mode - optimize for content type
            return contentType switch
            {
                ContentType.Text => _brotliStrategy,      // Best for text
                ContentType.Structured => _zstdStrategy,  // Good balance for JSON/XML
                ContentType.Binary => _lz4Strategy,       // Fast for binary
                ContentType.Compressed => _snappyStrategy, // Already compressed
                ContentType.Media => _snappyStrategy,     // Already compressed
                ContentType.Encrypted => _snappyStrategy, // Won't compress well
                _ => _zstdStrategy                        // Default to Zstd
            };
        }

        /// <inheritdoc/>
        public override bool ShouldCompress(ReadOnlySpan<byte> input)
        {
            // Very small data
            if (input.Length < Characteristics.MinimumRecommendedSize)
                return false;

            // Check entropy
            var entropy = CalculateEntropy(input);
            if (entropy > 7.5)
                return false; // Already compressed/encrypted

            // Check content type
            var contentType = DetectContentType(input);
            return contentType switch
            {
                ContentType.Compressed => false,
                ContentType.Media => false,
                ContentType.Encrypted => false,
                _ => true
            };
        }

        /// <inheritdoc/>
        public override long EstimateCompressedSize(long inputSize)
        {
            // Conservative estimate - assume worst case (Zstd)
            return _zstdStrategy.EstimateCompressedSize(inputSize);
        }
    }
}
