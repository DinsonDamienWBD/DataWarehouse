using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using DataWarehouse.SDK.Contracts.Compression;
using SdkCompressionLevel = DataWarehouse.SDK.Contracts.Compression.CompressionLevel;

using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;
namespace DataWarehouse.Plugins.UltimateCompression.Strategies.Transform
{
    /// <summary>
    /// Compression strategy using the Brotli algorithm via <see cref="BrotliStream"/>.
    /// Brotli is a modern compression algorithm developed by Google, combining LZ77 with
    /// Huffman coding and a large static dictionary of common web content.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Brotli provides excellent compression ratios, especially for web content (HTML, CSS, JS),
    /// and is widely supported in modern browsers for HTTP content encoding. It achieves better
    /// compression than GZip with comparable decompression speed, making it ideal for web assets
    /// and text-heavy data. The algorithm uses a 16MB dictionary and context modeling to achieve
    /// its superior compression ratios.
    /// </para>
    /// <para>
    /// Default quality is Q6 (not Q11). Q6 provides ~90% of Q11 compression ratio at 10-25x
    /// faster speed, making it the optimal default for general-purpose use. Users can override
    /// via <see cref="Quality"/> if maximum compression is needed.
    /// </para>
    /// </remarks>
    public sealed class BrotliStrategy : CompressionStrategyBase
    {
        private const int MaxInputSize = 100 * 1024 * 1024; // 100 MB

        /// <summary>
        /// Default Brotli quality level. Q6 provides ~90% of Q11 compression ratio at 10-25x faster speed.
        /// </summary>
        private const int DefaultQuality = 6;

        /// <summary>
        /// Default Brotli window size (22 = 4MB window).
        /// </summary>
        private const int DefaultWindow = 22;

        /// <summary>
        /// Gets or sets the Brotli quality level (0-11). Default is 6.
        /// Q6 provides ~90% of Q11 compression ratio at 10-25x faster speed.
        /// Set to 11 only when maximum compression is required and speed is not a concern.
        /// </summary>
        public int Quality { get; set; } = DefaultQuality;

        /// <summary>
        /// Initializes a new instance of the <see cref="BrotliStrategy"/> class
        /// with the default compression level (Q6).
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
                        "Brotli strategy healthy",
                        new Dictionary<string, object>
                        {
                            ["CompressOperations"] = GetCounter("brotli.compress"),
                            ["DecompressOperations"] = GetCounter("brotli.decompress")
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
            IncrementCounter("brotli.compress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for Brotli");
            if (input == null || input.Length == 0)
                return Array.Empty<byte>();

            // Use BrotliEncoder for better control over compression
            // Q6 default: ~90% of Q11 ratio at 10-25x faster speed
            var encoder = new BrotliEncoder(quality: Quality, window: DefaultWindow);
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
                    // Use Fastest for stream fallback (Q6 encoder path is primary)
                    using var outputStream = new MemoryStream(input.Length + 256);
                    using (var brotli = new BrotliStream(outputStream, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
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
            IncrementCounter("brotli.decompress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for Brotli");
            if (input == null || input.Length == 0)
                return Array.Empty<byte>();

            // Use BrotliDecoder for decompression
            var decoder = new BrotliDecoder();
            try
            {
                // Start with a reasonable output buffer size
                var output = new MemoryStream(65536);
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

            // Use Fastest level for streaming (BrotliStream doesn't support arbitrary quality)
            // For precise quality control, use CompressCore which uses BrotliEncoder with Q6
            return new BrotliStream(output, System.IO.Compression.CompressionLevel.Fastest, leaveOpen);
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
