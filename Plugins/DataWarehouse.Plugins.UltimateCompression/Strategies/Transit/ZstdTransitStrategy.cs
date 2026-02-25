using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Compression;
using ZstdSharp;

using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;
namespace DataWarehouse.Plugins.UltimateCompression.Strategies.Transit
{
    /// <summary>
    /// Zstandard transit compression strategy - best balance of speed and compression ratio.
    /// Uses multiple compression levels (1-22) with dictionary support for repeated payloads.
    /// </summary>
    public sealed class ZstdTransitStrategy : CompressionStrategyBase
    {
        private const int MaxInputSize = 100 * 1024 * 1024; // 100 MB

        private readonly int _zstdLevel;

        /// <summary>
        /// Initializes a new instance of the <see cref="ZstdTransitStrategy"/> class.
        /// </summary>
        /// <param name="level">The compression level.</param>
        public ZstdTransitStrategy(CompressionLevel level) : base(level)
        {
            _zstdLevel = level switch
            {
                CompressionLevel.Fastest => 1,
                CompressionLevel.Fast => 3,
                CompressionLevel.Default => 9,
                CompressionLevel.Better => 15,
                CompressionLevel.Best => 19,
                _ => 9
            };
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics => new()
        {
            AlgorithmName = "Zstd-Transit",
            TypicalCompressionRatio = 0.35,
            CompressionSpeed = _zstdLevel <= 3 ? 9 : _zstdLevel <= 9 ? 7 : 4,
            DecompressionSpeed = 10,
            CompressionMemoryUsage = _zstdLevel <= 9 ? 8 * 1024 * 1024 : 64 * 1024 * 1024,
            DecompressionMemoryUsage = 2 * 1024 * 1024,
            SupportsStreaming = true,
            SupportsParallelCompression = true,
            SupportsParallelDecompression = true,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 512,
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
                        "Zstd-Transit strategy healthy",
                        new Dictionary<string, object>
                        {
                            ["CompressOperations"] = GetCounter("zstd-transit.compress"),
                            ["DecompressOperations"] = GetCounter("zstd-transit.decompress")
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
            IncrementCounter("zstd-transit.compress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for Zstd-Transit");
            using var compressor = new Compressor(_zstdLevel);
            return compressor.Wrap(input).ToArray();
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            IncrementCounter("zstd-transit.decompress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for Zstd-Transit");
            using var decompressor = new Decompressor();
            return decompressor.Unwrap(input).ToArray();
        }

        /// <inheritdoc/>
        protected override async Task<byte[]> CompressAsyncCore(byte[] input, CancellationToken cancellationToken)
        {
            // Zstd compression is CPU-bound, run on thread pool
            return await Task.Run(() => CompressCore(input), cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        protected override async Task<byte[]> DecompressAsyncCore(byte[] input, CancellationToken cancellationToken)
        {
            // Zstd decompression is CPU-bound, run on thread pool
            return await Task.Run(() => DecompressCore(input), cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
        {
            return new CompressionStream(output, _zstdLevel, leaveOpen: leaveOpen);
        }

        /// <inheritdoc/>
        protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
        {
            return new DecompressionStream(input, leaveOpen: leaveOpen);
        }

        /// <inheritdoc/>
        public override long EstimateCompressedSize(long inputSize)
        {
            // Zstd worst-case: input + header + minimal overhead
            return (long)(inputSize * 1.01) + 256;
        }
    }
}
