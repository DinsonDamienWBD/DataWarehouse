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
    /// Raw Deflate transit compression strategy - minimal overhead without GZip framing.
    /// Matches HTTP Content-Encoding: deflate, useful for WebSocket payloads.
    /// </summary>
    public sealed class DeflateTransitStrategy : CompressionStrategyBase
    {
        private const int MaxInputSize = 100 * 1024 * 1024; // 100 MB

        private readonly System.IO.Compression.CompressionLevel _deflateLevel;

        /// <summary>
        /// Initializes a new instance of the <see cref="DeflateTransitStrategy"/> class.
        /// </summary>
        /// <param name="level">The compression level.</param>
        public DeflateTransitStrategy(SDK.Contracts.Compression.CompressionLevel level) : base(level)
        {
            _deflateLevel = level switch
            {
                SDK.Contracts.Compression.CompressionLevel.Fastest => System.IO.Compression.CompressionLevel.Fastest,
                SDK.Contracts.Compression.CompressionLevel.Fast => System.IO.Compression.CompressionLevel.Fastest,
                SDK.Contracts.Compression.CompressionLevel.Default => System.IO.Compression.CompressionLevel.Optimal,
                SDK.Contracts.Compression.CompressionLevel.Better => System.IO.Compression.CompressionLevel.Optimal,
                SDK.Contracts.Compression.CompressionLevel.Best => System.IO.Compression.CompressionLevel.SmallestSize,
                _ => System.IO.Compression.CompressionLevel.Optimal
            };
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics => new()
        {
            AlgorithmName = "Deflate-Transit",
            TypicalCompressionRatio = 0.42,
            CompressionSpeed = _deflateLevel == System.IO.Compression.CompressionLevel.Fastest ? 8 :
                               _deflateLevel == System.IO.Compression.CompressionLevel.Optimal ? 6 : 4,
            DecompressionSpeed = 9,
            CompressionMemoryUsage = 6 * 1024 * 1024,
            DecompressionMemoryUsage = 1 * 1024 * 1024,
            SupportsStreaming = true,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 512,
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
                        "Deflate-Transit strategy healthy",
                        new Dictionary<string, object>
                        {
                            ["CompressOperations"] = GetCounter("deflate-transit.compress"),
                            ["DecompressOperations"] = GetCounter("deflate-transit.decompress")
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
            IncrementCounter("deflate-transit.compress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for Deflate-Transit");
            using var outputStream = new MemoryStream(input.Length + 256);
            using (var deflateStream = new DeflateStream(outputStream, _deflateLevel, leaveOpen: true))
            {
                deflateStream.Write(input, 0, input.Length);
            }
            return outputStream.ToArray();
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            IncrementCounter("deflate-transit.decompress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for Deflate-Transit");
            using var inputStream = new MemoryStream(input);
            using var deflateStream = new DeflateStream(inputStream, System.IO.Compression.CompressionMode.Decompress);
            using var outputStream = new MemoryStream(input.Length + 256);

            deflateStream.CopyTo(outputStream);
            return outputStream.ToArray();
        }

        /// <inheritdoc/>
        protected override async Task<byte[]> CompressAsyncCore(byte[] input, CancellationToken cancellationToken)
        {
            using var outputStream = new MemoryStream(input.Length + 256);
            using (var deflateStream = new DeflateStream(outputStream, _deflateLevel, leaveOpen: true))
            {
                await deflateStream.WriteAsync(input, 0, input.Length, cancellationToken).ConfigureAwait(false);
            }
            return outputStream.ToArray();
        }

        /// <inheritdoc/>
        protected override async Task<byte[]> DecompressAsyncCore(byte[] input, CancellationToken cancellationToken)
        {
            using var inputStream = new MemoryStream(input);
            using var deflateStream = new DeflateStream(inputStream, System.IO.Compression.CompressionMode.Decompress);
            using var outputStream = new MemoryStream(input.Length + 256);

            await deflateStream.CopyToAsync(outputStream, 81920, cancellationToken).ConfigureAwait(false);
            return outputStream.ToArray();
        }

        /// <inheritdoc/>
        protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
        {
            return new DeflateStream(output, _deflateLevel, leaveOpen);
        }

        /// <inheritdoc/>
        protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
        {
            return new DeflateStream(input, System.IO.Compression.CompressionMode.Decompress, leaveOpen);
        }

        /// <inheritdoc/>
        public override long EstimateCompressedSize(long inputSize)
        {
            // Deflate worst-case: input + 0.1% + 5 bytes per 16KB block
            var blocks = (inputSize / 16384) + 1;
            return (long)(inputSize * 1.001) + (blocks * 5);
        }
    }
}
