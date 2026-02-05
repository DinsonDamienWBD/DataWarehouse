using System;
using System.IO;
using System.IO.Compression;
using DataWarehouse.SDK.Contracts.Compression;
using SdkCompressionLevel = DataWarehouse.SDK.Contracts.Compression.CompressionLevel;

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

        /// <inheritdoc/>
        protected override byte[] CompressCore(byte[] input)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            {
                gzip.Write(input, 0, input.Length);
            }
            return output.ToArray();
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            using var inputStream = new MemoryStream(input);
            using var gzip = new GZipStream(inputStream, System.IO.Compression.CompressionMode.Decompress);
            using var output = new MemoryStream();
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
