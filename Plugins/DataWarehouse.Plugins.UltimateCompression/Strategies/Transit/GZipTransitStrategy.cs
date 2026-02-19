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
    /// GZip transit compression strategy - universal compatibility with all HTTP endpoints.
    /// Matches HTTP Content-Encoding: gzip, widest client support.
    /// </summary>
    public sealed class GZipTransitStrategy : CompressionStrategyBase
    {
        private const int MaxInputSize = 100 * 1024 * 1024; // 100 MB

        private readonly System.IO.Compression.CompressionLevel _gzipLevel;

        /// <summary>
        /// Initializes a new instance of the <see cref="GZipTransitStrategy"/> class.
        /// </summary>
        /// <param name="level">The compression level.</param>
        public GZipTransitStrategy(SDK.Contracts.Compression.CompressionLevel level) : base(level)
        {
            _gzipLevel = level switch
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
            AlgorithmName = "GZip-Transit",
            TypicalCompressionRatio = 0.40,
            CompressionSpeed = _gzipLevel == System.IO.Compression.CompressionLevel.Fastest ? 8 :
                               _gzipLevel == System.IO.Compression.CompressionLevel.Optimal ? 6 : 4,
            DecompressionSpeed = 9,
            CompressionMemoryUsage = 8 * 1024 * 1024,
            DecompressionMemoryUsage = 2 * 1024 * 1024,
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
                        "GZip-Transit strategy healthy",
                        new Dictionary<string, object>
                        {
                            ["CompressOperations"] = GetCounter("gzip-transit.compress"),
                            ["DecompressOperations"] = GetCounter("gzip-transit.decompress")
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
            IncrementCounter("gzip-transit.compress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for GZip-Transit");
            using var outputStream = new MemoryStream(input.Length + 256);
            using (var gzipStream = new GZipStream(outputStream, _gzipLevel, leaveOpen: true))
            {
                gzipStream.Write(input, 0, input.Length);
            }
            return outputStream.ToArray();
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            IncrementCounter("gzip-transit.decompress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for GZip-Transit");
            using var inputStream = new MemoryStream(input);
            using var gzipStream = new GZipStream(inputStream, System.IO.Compression.CompressionMode.Decompress);
            using var outputStream = new MemoryStream(input.Length + 256);

            gzipStream.CopyTo(outputStream);
            return outputStream.ToArray();
        }

        /// <inheritdoc/>
        protected override async Task<byte[]> CompressAsyncCore(byte[] input, CancellationToken cancellationToken)
        {
            using var outputStream = new MemoryStream(input.Length + 256);
            using (var gzipStream = new GZipStream(outputStream, _gzipLevel, leaveOpen: true))
            {
                await gzipStream.WriteAsync(input, 0, input.Length, cancellationToken).ConfigureAwait(false);
            }
            return outputStream.ToArray();
        }

        /// <inheritdoc/>
        protected override async Task<byte[]> DecompressAsyncCore(byte[] input, CancellationToken cancellationToken)
        {
            using var inputStream = new MemoryStream(input);
            using var gzipStream = new GZipStream(inputStream, System.IO.Compression.CompressionMode.Decompress);
            using var outputStream = new MemoryStream(input.Length + 256);

            await gzipStream.CopyToAsync(outputStream, 81920, cancellationToken).ConfigureAwait(false);
            return outputStream.ToArray();
        }

        /// <inheritdoc/>
        protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
        {
            return new GZipStream(output, _gzipLevel, leaveOpen);
        }

        /// <inheritdoc/>
        protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
        {
            return new GZipStream(input, System.IO.Compression.CompressionMode.Decompress, leaveOpen);
        }

        /// <inheritdoc/>
        public override long EstimateCompressedSize(long inputSize)
        {
            // GZip worst-case: input + 0.1% + 12 bytes
            return (long)(inputSize * 1.001) + 18; // 18 = 10 header + 8 trailer
        }
    }
}
