using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Compression;

using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;
namespace DataWarehouse.Plugins.UltimateCompression.Strategies.Transit
{
    /// <summary>
    /// Brotli transit compression strategy - HTTP-native compression with excellent text compression.
    /// Matches HTTP Content-Encoding: br, ideal for web APIs and JSON payloads.
    /// Default level maps to Fastest (~Q4-6) rather than Optimal (~Q11) for 10-25x faster speed
    /// with ~90% of maximum compression ratio.
    /// </summary>
    public sealed class BrotliTransitStrategy : CompressionStrategyBase
    {
        private const int MaxInputSize = 100 * 1024 * 1024; // 100 MB

        private readonly System.IO.Compression.CompressionLevel _brotliLevel;

        /// <summary>
        /// Initializes a new instance of the <see cref="BrotliTransitStrategy"/> class.
        /// </summary>
        /// <param name="level">The compression level.</param>
        public BrotliTransitStrategy(SDK.Contracts.Compression.CompressionLevel level) : base(level)
        {
            // Default maps to Fastest (~Q4-6) not Optimal (~Q11) for transit use:
            // Transit compression prioritizes speed since it's on the critical path.
            // Q6 provides ~90% of Q11 ratio at 10-25x faster speed.
            _brotliLevel = level switch
            {
                SDK.Contracts.Compression.CompressionLevel.Fastest => System.IO.Compression.CompressionLevel.Fastest,
                SDK.Contracts.Compression.CompressionLevel.Fast => System.IO.Compression.CompressionLevel.Fastest,
                SDK.Contracts.Compression.CompressionLevel.Default => System.IO.Compression.CompressionLevel.Fastest,
                SDK.Contracts.Compression.CompressionLevel.Better => System.IO.Compression.CompressionLevel.Optimal,
                SDK.Contracts.Compression.CompressionLevel.Best => System.IO.Compression.CompressionLevel.SmallestSize,
                _ => System.IO.Compression.CompressionLevel.Fastest
            };
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics => new()
        {
            AlgorithmName = "Brotli-Transit",
            TypicalCompressionRatio = 0.30,
            CompressionSpeed = _brotliLevel == System.IO.Compression.CompressionLevel.Fastest ? 7 :
                               _brotliLevel == System.IO.Compression.CompressionLevel.Optimal ? 5 : 3,
            DecompressionSpeed = 8,
            CompressionMemoryUsage = 16 * 1024 * 1024,
            DecompressionMemoryUsage = 4 * 1024 * 1024,
            SupportsStreaming = true,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 1024,
            OptimalBlockSize = 128 * 1024
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
                        "Brotli-Transit strategy healthy",
                        new Dictionary<string, object>
                        {
                            ["CompressOperations"] = GetCounter("brotli-transit.compress"),
                            ["DecompressOperations"] = GetCounter("brotli-transit.decompress")
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
            IncrementCounter("brotli-transit.compress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for Brotli-Transit");
            using var outputStream = new MemoryStream(input.Length + 256);
            using (var brotliStream = new BrotliStream(outputStream, _brotliLevel, leaveOpen: true))
            {
                brotliStream.Write(input, 0, input.Length);
            }
            return outputStream.ToArray();
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            IncrementCounter("brotli-transit.decompress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for Brotli-Transit");
            using var inputStream = new MemoryStream(input);
            using var brotliStream = new BrotliStream(inputStream, System.IO.Compression.CompressionMode.Decompress);
            using var outputStream = new MemoryStream(input.Length + 256);

            brotliStream.CopyTo(outputStream);
            return outputStream.ToArray();
        }

        /// <inheritdoc/>
        protected override async Task<byte[]> CompressAsyncCore(byte[] input, CancellationToken cancellationToken)
        {
            using var outputStream = new MemoryStream(input.Length + 256);
            using (var brotliStream = new BrotliStream(outputStream, _brotliLevel, leaveOpen: true))
            {
                await brotliStream.WriteAsync(input, 0, input.Length, cancellationToken).ConfigureAwait(false);
            }
            return outputStream.ToArray();
        }

        /// <inheritdoc/>
        protected override async Task<byte[]> DecompressAsyncCore(byte[] input, CancellationToken cancellationToken)
        {
            using var inputStream = new MemoryStream(input);
            using var brotliStream = new BrotliStream(inputStream, System.IO.Compression.CompressionMode.Decompress);
            using var outputStream = new MemoryStream(input.Length + 256);

            await brotliStream.CopyToAsync(outputStream, 81920, cancellationToken).ConfigureAwait(false);
            return outputStream.ToArray();
        }

        /// <inheritdoc/>
        protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
        {
            return new BrotliStream(output, _brotliLevel, leaveOpen);
        }

        /// <inheritdoc/>
        protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
        {
            return new BrotliStream(input, System.IO.Compression.CompressionMode.Decompress, leaveOpen);
        }

        /// <inheritdoc/>
        public override long EstimateCompressedSize(long inputSize)
        {
            // Brotli worst-case: approximately input size + 2% + overhead
            return (long)(inputSize * 1.02) + 512;
        }

        /// <inheritdoc/>
        public override bool ShouldCompress(ReadOnlySpan<byte> input)
        {
            // Brotli excels at text and structured data
            var contentType = DetectContentType(input);
            return contentType == ContentType.Text ||
                   contentType == ContentType.Structured ||
                   (contentType == ContentType.Binary && input.Length >= Characteristics.MinimumRecommendedSize);
        }
    }
}
