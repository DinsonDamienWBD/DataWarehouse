using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Compression;
using Snappier;

namespace DataWarehouse.Plugins.UltimateCompression.Strategies.Transit
{
    /// <summary>
    /// Google Snappy transit compression strategy - ultra-low-latency compression for RPC.
    /// Used in gRPC and internal protocols where latency is more critical than compression ratio.
    /// </summary>
    public sealed class SnappyTransitStrategy : CompressionStrategyBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SnappyTransitStrategy"/> class.
        /// </summary>
        /// <param name="level">The compression level (Snappy has fixed level).</param>
        public SnappyTransitStrategy(CompressionLevel level) : base(level)
        {
            // Snappy has no compression levels, always fastest
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics => new()
        {
            AlgorithmName = "Snappy-Transit",
            TypicalCompressionRatio = 0.55,
            CompressionSpeed = 10,
            DecompressionSpeed = 10,
            CompressionMemoryUsage = 1 * 1024 * 1024,
            DecompressionMemoryUsage = 512 * 1024,
            SupportsStreaming = true,
            SupportsParallelCompression = true,
            SupportsParallelDecompression = true,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 128,
            OptimalBlockSize = 32 * 1024
        };

        /// <inheritdoc/>
        protected override byte[] CompressCore(byte[] input)
        {
            return Snappy.CompressToArray(input);
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            return Snappy.DecompressToArray(input);
        }

        /// <inheritdoc/>
        protected override async Task<byte[]> CompressAsyncCore(byte[] input, CancellationToken cancellationToken)
        {
            // Snappy is extremely fast, run on thread pool
            return await Task.Run(() => CompressCore(input), cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        protected override async Task<byte[]> DecompressAsyncCore(byte[] input, CancellationToken cancellationToken)
        {
            return await Task.Run(() => DecompressCore(input), cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
        {
            // Snappier provides streaming support via SnappyStream
            return new SnappyStream(output, System.IO.Compression.CompressionMode.Compress, leaveOpen);
        }

        /// <inheritdoc/>
        protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
        {
            return new SnappyStream(input, System.IO.Compression.CompressionMode.Decompress, leaveOpen);
        }

        /// <inheritdoc/>
        public override long EstimateCompressedSize(long inputSize)
        {
            // Snappy worst-case: 32 + input + input/6
            return 32 + inputSize + (inputSize / 6);
        }

        /// <inheritdoc/>
        public override bool ShouldCompress(ReadOnlySpan<byte> input)
        {
            // Snappy is so fast that it's almost always worth trying
            if (input.Length < Characteristics.MinimumRecommendedSize)
                return false;

            // Skip only highly compressed/encrypted data
            var entropy = CalculateEntropy(input);
            return entropy < 7.8;
        }
    }
}
