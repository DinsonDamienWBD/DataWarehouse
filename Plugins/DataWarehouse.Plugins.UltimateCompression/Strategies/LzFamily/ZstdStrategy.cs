using System;
using System.IO;
using DataWarehouse.SDK.Contracts.Compression;
using ZstdSharp;

using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;
namespace DataWarehouse.Plugins.UltimateCompression.Strategies.LzFamily
{
    /// <summary>
    /// Compression strategy using the Zstandard (Zstd) algorithm via ZstdSharp.
    /// Zstd provides an excellent balance between compression ratio and speed,
    /// making it suitable for general-purpose compression workloads.
    /// </summary>
    /// <remarks>
    /// Zstd was developed by Facebook and offers configurable compression levels (1-22),
    /// dictionary support, and extremely fast decompression. This implementation uses
    /// the default compression level (3) which provides a good ratio/speed trade-off.
    /// </remarks>
    public sealed class ZstdStrategy : CompressionStrategyBase
    {
        private const int MaxInputSize = 100 * 1024 * 1024; // 100 MB

        private const int DefaultCompressionLevel = 3;

        /// <summary>
        /// Initializes a new instance of the <see cref="ZstdStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public ZstdStrategy() : base(CompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "Zstd",
            TypicalCompressionRatio = 0.35,
            CompressionSpeed = 8,
            DecompressionSpeed = 9,
            CompressionMemoryUsage = 512 * 1024,
            DecompressionMemoryUsage = 128 * 1024,
            SupportsStreaming = true,
            SupportsParallelCompression = true,
            SupportsParallelDecompression = true,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 256,
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
                        "Zstd strategy healthy",
                        new Dictionary<string, object>
                        {
                            ["CompressOperations"] = GetCounter("zstd.compress"),
                            ["DecompressOperations"] = GetCounter("zstd.decompress")
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
            IncrementCounter("zstd.compress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for Zstd");
            using var compressor = new Compressor(DefaultCompressionLevel);
            return compressor.Wrap(input).ToArray();
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            IncrementCounter("zstd.decompress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for Zstd");
            using var decompressor = new Decompressor();
            return decompressor.Unwrap(input).ToArray();
        }

        /// <inheritdoc/>
        protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
        {
            return new CompressionStream(output, DefaultCompressionLevel, leaveOpen: leaveOpen);
        }

        /// <inheritdoc/>
        protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
        {
            return new DecompressionStream(input, leaveOpen: leaveOpen);
        }

        /// <inheritdoc/>
        public override long EstimateCompressedSize(long inputSize)
        {
            // Zstd worst case is slightly larger than input plus frame overhead
            return inputSize + (inputSize / 128) + 512;
        }
    }
}
