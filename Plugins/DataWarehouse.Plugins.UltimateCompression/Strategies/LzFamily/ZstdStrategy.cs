using System;
using System.IO;
using DataWarehouse.SDK.Contracts.Compression;
using ZstdSharp;

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

        /// <inheritdoc/>
        protected override byte[] CompressCore(byte[] input)
        {
            using var compressor = new Compressor(DefaultCompressionLevel);
            return compressor.Wrap(input).ToArray();
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
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
