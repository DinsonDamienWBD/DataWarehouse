using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Compression;

namespace DataWarehouse.Plugins.UltimateCompression.Strategies.Transit
{
    /// <summary>
    /// GZip transit compression strategy - universal compatibility with all HTTP endpoints.
    /// Matches HTTP Content-Encoding: gzip, widest client support.
    /// </summary>
    public sealed class GZipTransitStrategy : CompressionStrategyBase
    {
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

        /// <inheritdoc/>
        protected override byte[] CompressCore(byte[] input)
        {
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
