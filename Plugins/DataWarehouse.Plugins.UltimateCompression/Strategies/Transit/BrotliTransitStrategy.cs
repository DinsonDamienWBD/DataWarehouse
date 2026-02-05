using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Compression;

namespace DataWarehouse.Plugins.UltimateCompression.Strategies.Transit
{
    /// <summary>
    /// Brotli transit compression strategy - HTTP-native compression with excellent text compression.
    /// Matches HTTP Content-Encoding: br, ideal for web APIs and JSON payloads.
    /// </summary>
    public sealed class BrotliTransitStrategy : CompressionStrategyBase
    {
        private readonly System.IO.Compression.CompressionLevel _brotliLevel;

        /// <summary>
        /// Initializes a new instance of the <see cref="BrotliTransitStrategy"/> class.
        /// </summary>
        /// <param name="level">The compression level.</param>
        public BrotliTransitStrategy(SDK.Contracts.Compression.CompressionLevel level) : base(level)
        {
            _brotliLevel = level switch
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

        /// <inheritdoc/>
        protected override byte[] CompressCore(byte[] input)
        {
            using var outputStream = new MemoryStream();
            using (var brotliStream = new BrotliStream(outputStream, _brotliLevel, leaveOpen: true))
            {
                brotliStream.Write(input, 0, input.Length);
            }
            return outputStream.ToArray();
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            using var inputStream = new MemoryStream(input);
            using var brotliStream = new BrotliStream(inputStream, System.IO.Compression.CompressionMode.Decompress);
            using var outputStream = new MemoryStream();

            brotliStream.CopyTo(outputStream);
            return outputStream.ToArray();
        }

        /// <inheritdoc/>
        protected override async Task<byte[]> CompressAsyncCore(byte[] input, CancellationToken cancellationToken)
        {
            using var outputStream = new MemoryStream();
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
            using var outputStream = new MemoryStream();

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
