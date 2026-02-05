using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using DataWarehouse.SDK.Contracts.Compression;
using SdkCompressionLevel = DataWarehouse.SDK.Contracts.Compression.CompressionLevel;

namespace DataWarehouse.Plugins.UltimateCompression.Strategies.Transform
{
    /// <summary>
    /// Compression strategy using the Brotli algorithm via <see cref="BrotliStream"/>.
    /// Brotli is a modern compression algorithm developed by Google, combining LZ77 with
    /// Huffman coding and a large static dictionary of common web content.
    /// </summary>
    /// <remarks>
    /// Brotli provides excellent compression ratios, especially for web content (HTML, CSS, JS),
    /// and is widely supported in modern browsers for HTTP content encoding. It achieves better
    /// compression than GZip with comparable decompression speed, making it ideal for web assets
    /// and text-heavy data. The algorithm uses a 16MB dictionary and context modeling to achieve
    /// its superior compression ratios.
    /// </remarks>
    public sealed class BrotliStrategy : CompressionStrategyBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="BrotliStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public BrotliStrategy() : base(SdkCompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "Brotli",
            TypicalCompressionRatio = 0.30,
            CompressionSpeed = 3,
            DecompressionSpeed = 8,
            CompressionMemoryUsage = 16 * 1024 * 1024,
            DecompressionMemoryUsage = 256 * 1024,
            SupportsStreaming = true,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 256,
            OptimalBlockSize = 128 * 1024
        };

        /// <inheritdoc/>
        protected override byte[] CompressCore(byte[] input)
        {
            if (input == null || input.Length == 0)
                return Array.Empty<byte>();

            // Use BrotliEncoder for better control over compression
            var encoder = new BrotliEncoder(quality: 11, window: 22);
            try
            {
                // Estimate output buffer size (worst case: input size + some overhead)
                int maxOutputSize = BrotliEncoder.GetMaxCompressedLength(input.Length);
                var output = new byte[maxOutputSize];

                int bytesConsumed = 0;
                int bytesWritten = 0;

                // Compress the data
                var status = encoder.Compress(
                    input.AsSpan(),
                    output.AsSpan(),
                    out bytesConsumed,
                    out bytesWritten,
                    isFinalBlock: true);

                if (status == OperationStatus.Done)
                {
                    // Trim to actual size
                    var result = new byte[bytesWritten];
                    Array.Copy(output, result, bytesWritten);
                    return result;
                }
                else if (status == OperationStatus.DestinationTooSmall)
                {
                    // Fallback to stream-based compression if buffer estimate was wrong
                    using var outputStream = new MemoryStream();
                    using (var brotli = new BrotliStream(outputStream, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                    {
                        brotli.Write(input, 0, input.Length);
                    }
                    return outputStream.ToArray();
                }
                else
                {
                    throw new InvalidOperationException($"Brotli compression failed with status: {status}");
                }
            }
            finally
            {
                encoder.Dispose();
            }
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            if (input == null || input.Length == 0)
                return Array.Empty<byte>();

            // Use BrotliDecoder for decompression
            var decoder = new BrotliDecoder();
            try
            {
                // Start with a reasonable output buffer size
                var output = new MemoryStream();
                var buffer = new byte[64 * 1024]; // 64KB buffer
                int inputOffset = 0;

                while (inputOffset < input.Length)
                {
                    int bytesConsumed = 0;
                    int bytesWritten = 0;

                    var status = decoder.Decompress(
                        input.AsSpan(inputOffset),
                        buffer.AsSpan(),
                        out bytesConsumed,
                        out bytesWritten);

                    if (bytesWritten > 0)
                    {
                        output.Write(buffer, 0, bytesWritten);
                    }

                    inputOffset += bytesConsumed;

                    if (status == OperationStatus.Done)
                    {
                        return output.ToArray();
                    }
                    else if (status == OperationStatus.NeedMoreData)
                    {
                        if (inputOffset >= input.Length)
                        {
                            throw new InvalidDataException("Unexpected end of compressed data");
                        }
                    }
                    else if (status == OperationStatus.InvalidData)
                    {
                        throw new InvalidDataException("Invalid Brotli compressed data");
                    }
                }

                return output.ToArray();
            }
            finally
            {
                decoder.Dispose();
            }
        }

        /// <inheritdoc/>
        protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
        {
            if (output == null)
                throw new ArgumentNullException(nameof(output));

            return new BrotliStream(output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen);
        }

        /// <inheritdoc/>
        protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));

            return new BrotliStream(input, System.IO.Compression.CompressionMode.Decompress, leaveOpen);
        }

        /// <inheritdoc/>
        public override long EstimateCompressedSize(long inputSize)
        {
            // Brotli can achieve very good compression ratios
            // Conservative estimate: 35% of input size + small overhead for metadata
            return (long)(inputSize * 0.35) + 128;
        }
    }
}
