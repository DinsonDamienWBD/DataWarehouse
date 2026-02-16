using System;
using System.IO;
using DataWarehouse.SDK.Contracts.Compression;
using SharpCompress.Compressors.BZip2;
using SdkCompressionLevel = DataWarehouse.SDK.Contracts.Compression.CompressionLevel;

namespace DataWarehouse.Plugins.UltimateCompression.Strategies.Transform
{
    /// <summary>
    /// Compression strategy using the BZip2 algorithm via <see cref="BZip2Stream"/>.
    /// BZip2 uses the Burrows-Wheeler Transform (BWT) followed by Move-to-Front (MTF)
    /// encoding and Huffman coding to achieve excellent compression ratios.
    /// </summary>
    /// <remarks>
    /// BZip2 is particularly effective for text and structured data, often achieving better
    /// compression ratios than GZip at the cost of slower compression and decompression speeds.
    /// The algorithm operates on blocks of data (up to 900KB), applying BWT to create
    /// long runs of repeated characters that compress well with run-length encoding and Huffman coding.
    /// Widely used in Unix/Linux environments (.bz2 files) and tar archives (.tar.bz2).
    /// </remarks>
    public sealed class Bzip2Strategy : CompressionStrategyBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Bzip2Strategy"/> class
        /// with the default compression level.
        /// </summary>
        public Bzip2Strategy() : base(SdkCompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "BZip2",
            TypicalCompressionRatio = 0.32,
            CompressionSpeed = 3,
            DecompressionSpeed = 4,
            CompressionMemoryUsage = 4 * 1024 * 1024,
            DecompressionMemoryUsage = 2 * 1024 * 1024,
            SupportsStreaming = true,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 512,
            OptimalBlockSize = 900 * 1024
        };

        /// <inheritdoc/>
        protected override byte[] CompressCore(byte[] input)
        {
            if (input == null || input.Length == 0)
                return Array.Empty<byte>();

            using var output = new MemoryStream();
            using (var bzip2 = BZip2Stream.Create(output, SharpCompress.Compressors.CompressionMode.Compress, false, false))
            {
                bzip2.Write(input, 0, input.Length);
            }
            return output.ToArray();
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            if (input == null || input.Length == 0)
                return Array.Empty<byte>();

            // Verify BZip2 magic number
            if (input.Length < 4 || input[0] != 0x42 || input[1] != 0x5A || input[2] != 0x68)
                throw new InvalidDataException("Invalid BZip2 header magic");

            using var inputStream = new MemoryStream(input);
            using var bzip2 = BZip2Stream.Create(inputStream, SharpCompress.Compressors.CompressionMode.Decompress, false, false);
            using var output = new MemoryStream();

            bzip2.CopyTo(output);
            return output.ToArray();
        }

        /// <inheritdoc/>
        protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
        {
            if (output == null)
                throw new ArgumentNullException(nameof(output));

            return BZip2Stream.Create(output, SharpCompress.Compressors.CompressionMode.Compress, false, false);
        }

        /// <inheritdoc/>
        protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));

            return BZip2Stream.Create(input, SharpCompress.Compressors.CompressionMode.Decompress, false, false);
        }

        /// <inheritdoc/>
        public override long EstimateCompressedSize(long inputSize)
        {
            // BZip2 typically achieves 30-40% compression on text data
            // Add overhead for block headers (up to 50 bytes per 900KB block)
            long blocks = (inputSize / (900 * 1024)) + 1;
            return (long)(inputSize * 0.35) + (blocks * 50) + 64;
        }
    }
}
