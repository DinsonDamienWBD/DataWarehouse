using System;
using System.IO;
using System.IO.Compression;
using DataWarehouse.SDK.Contracts.Compression;
using SdkCompressionLevel = DataWarehouse.SDK.Contracts.Compression.CompressionLevel;

using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;
namespace DataWarehouse.Plugins.UltimateCompression.Strategies.LzFamily
{
    /// <summary>
    /// Compression strategy using the GZip algorithm via <see cref="GZipStream"/>.
    /// GZip wraps DEFLATE with a standard header/trailer format (RFC 1952),
    /// providing CRC-32 integrity checking and widespread compatibility.
    /// </summary>
    /// <remarks>
    /// GZip is one of the most widely supported compression formats, used in HTTP
    /// content encoding, file archival (.gz), and Unix/Linux utilities. While not
    /// the fastest or most efficient, its universal support makes it a reliable choice
    /// for interoperability.
    /// </remarks>
    public sealed class GZipStrategy : CompressionStrategyBase
    {
        private const int MaxInputSize = 100 * 1024 * 1024; // 100 MB

        /// <summary>
        /// Initializes a new instance of the <see cref="GZipStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public GZipStrategy() : base(SdkCompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "GZip",
            TypicalCompressionRatio = 0.40,
            CompressionSpeed = 6,
            DecompressionSpeed = 7,
            CompressionMemoryUsage = 256 * 1024,
            DecompressionMemoryUsage = 64 * 1024,
            SupportsStreaming = true,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 128,
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
                        "GZip strategy healthy",
                        new Dictionary<string, object>
                        {
                            ["CompressOperations"] = GetCounter("gzip.compress"),
                            ["DecompressOperations"] = GetCounter("gzip.decompress")
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
            IncrementCounter("gzip.compress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for GZip");
            using var output = new MemoryStream(input.Length + 256);
            using (var gzip = new GZipStream(output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            {
                gzip.Write(input, 0, input.Length);
            }
            return output.ToArray();
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            IncrementCounter("gzip.decompress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for GZip");
            using var inputStream = new MemoryStream(input);
            using var gzip = new GZipStream(inputStream, System.IO.Compression.CompressionMode.Decompress);
            using var output = new MemoryStream(input.Length + 256);
            gzip.CopyTo(output);
            return output.ToArray();
        }

        /// <inheritdoc/>
        protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
        {
            return new GZipStream(output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen);
        }

        /// <inheritdoc/>
        protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
        {
            return new GZipStream(input, System.IO.Compression.CompressionMode.Decompress, leaveOpen);
        }

        /// <inheritdoc/>
        public override long EstimateCompressedSize(long inputSize)
        {
            // GZip adds ~18 bytes header/trailer + DEFLATE overhead
            return (long)(inputSize * 1.05) + 18 + 256;
        }
    }
}
