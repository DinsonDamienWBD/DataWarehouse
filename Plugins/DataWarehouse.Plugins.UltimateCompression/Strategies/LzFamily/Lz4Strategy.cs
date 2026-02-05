using System;
using System.IO;
using DataWarehouse.SDK.Contracts.Compression;
using K4os.Compression.LZ4;
using K4os.Compression.LZ4.Streams;

namespace DataWarehouse.Plugins.UltimateCompression.Strategies.LzFamily
{
    /// <summary>
    /// Compression strategy using the LZ4 algorithm via K4os.Compression.LZ4.
    /// LZ4 is designed for extremely fast compression and decompression at the expense
    /// of compression ratio, making it ideal for real-time and in-memory workloads.
    /// </summary>
    /// <remarks>
    /// LZ4 was developed by Yann Collet and is one of the fastest lossless compression
    /// algorithms available. It typically achieves decompression speeds exceeding 4 GB/s
    /// per core. This implementation uses the default acceleration level for maximum speed.
    /// </remarks>
    public sealed class Lz4Strategy : CompressionStrategyBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Lz4Strategy"/> class
        /// with the default compression level.
        /// </summary>
        public Lz4Strategy() : base(CompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "LZ4",
            TypicalCompressionRatio = 0.55,
            CompressionSpeed = 10,
            DecompressionSpeed = 10,
            CompressionMemoryUsage = 64 * 1024,
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
            return LZ4Pickler.Pickle(input, LZ4Level.L00_FAST);
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            return LZ4Pickler.Unpickle(input);
        }

        /// <inheritdoc/>
        protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
        {
            var settings = new LZ4EncoderSettings
            {
                CompressionLevel = LZ4Level.L00_FAST
            };
            return LZ4Stream.Encode(output, settings, leaveOpen);
        }

        /// <inheritdoc/>
        protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
        {
            return LZ4Stream.Decode(input, leaveOpen: leaveOpen);
        }

        /// <inheritdoc/>
        public override long EstimateCompressedSize(long inputSize)
        {
            // LZ4 worst case bound
            return LZ4Codec.MaximumOutputSize((int)Math.Min(inputSize, int.MaxValue)) + 16;
        }
    }
}
