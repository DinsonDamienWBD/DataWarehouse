using System;
using System.IO;
using System.IO.Compression;
using DataWarehouse.SDK.Contracts.Compression;
using SdkCompressionLevel = DataWarehouse.SDK.Contracts.Compression.CompressionLevel;

namespace DataWarehouse.Plugins.UltimateCompression.Strategies.LzFamily
{
    /// <summary>
    /// Compression strategy using the DEFLATE algorithm via <see cref="DeflateStream"/>.
    /// DEFLATE combines LZ77 and Huffman coding (RFC 1951) and is the foundation
    /// for GZip, ZIP, and PNG compression formats.
    /// </summary>
    /// <remarks>
    /// DEFLATE is the raw compression format without GZip headers or CRC-32 checksums,
    /// resulting in slightly smaller output than GZip. It is suitable when a minimal
    /// framing overhead is desired and integrity checking is handled externally.
    /// </remarks>
    public sealed class DeflateStrategy : CompressionStrategyBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="DeflateStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public DeflateStrategy() : base(SdkCompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "Deflate",
            TypicalCompressionRatio = 0.42,
            CompressionSpeed = 6,
            DecompressionSpeed = 7,
            CompressionMemoryUsage = 256 * 1024,
            DecompressionMemoryUsage = 64 * 1024,
            SupportsStreaming = true,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 64,
            OptimalBlockSize = 64 * 1024
        };

        /// <inheritdoc/>
        protected override byte[] CompressCore(byte[] input)
        {
            using var output = new MemoryStream();
            using (var deflate = new DeflateStream(output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(input, 0, input.Length);
            }
            return output.ToArray();
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            using var inputStream = new MemoryStream(input);
            using var deflate = new DeflateStream(inputStream, System.IO.Compression.CompressionMode.Decompress);
            using var output = new MemoryStream();
            deflate.CopyTo(output);
            return output.ToArray();
        }

        /// <inheritdoc/>
        protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
        {
            return new DeflateStream(output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen);
        }

        /// <inheritdoc/>
        protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
        {
            return new DeflateStream(input, System.IO.Compression.CompressionMode.Decompress, leaveOpen);
        }

        /// <inheritdoc/>
        public override long EstimateCompressedSize(long inputSize)
        {
            // DEFLATE worst case: input + 5 bytes per 65535-byte block + small overhead
            return inputSize + (inputSize / 65535 + 1) * 5 + 64;
        }
    }
}
