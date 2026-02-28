using System;
using System.IO;
using System.IO.Compression;
using DataWarehouse.SDK.Contracts.Compression;
using SharpCompress.Compressors.LZMA;

using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;
namespace DataWarehouse.Plugins.UltimateCompression.Strategies.LzFamily
{
    /// <summary>
    /// Compression strategy using the LZMA (Lempel-Ziv-Markov chain Algorithm) via SharpCompress.
    /// LZMA provides excellent compression ratios at the cost of slower compression speed,
    /// making it ideal for archival and storage-constrained scenarios.
    /// </summary>
    /// <remarks>
    /// LZMA was developed by Igor Pavlov for the 7-Zip archiver. It combines an improved
    /// LZ77 algorithm with range coding, using large dictionaries (up to 4 GB) and sophisticated
    /// context modeling. Decompression is significantly faster than compression.
    /// This implementation uses SharpCompress with default encoder properties.
    /// </remarks>
    public sealed class LzmaStrategy : CompressionStrategyBase
    {
        private const int MaxInputSize = 100 * 1024 * 1024; // 100 MB

        /// <summary>
        /// Initializes a new instance of the <see cref="LzmaStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public LzmaStrategy() : base(SDK.Contracts.Compression.CompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "LZMA",
            TypicalCompressionRatio = 0.28,
            CompressionSpeed = 2,
            DecompressionSpeed = 5,
            CompressionMemoryUsage = 16 * 1024 * 1024,
            DecompressionMemoryUsage = 2 * 1024 * 1024,
            SupportsStreaming = true,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 512,
            OptimalBlockSize = 256 * 1024
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
                        "LZMA strategy healthy",
                        new Dictionary<string, object>
                        {
                            ["CompressOperations"] = GetCounter("lzma.compress"),
                            ["DecompressOperations"] = GetCounter("lzma.decompress")
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
            IncrementCounter("lzma.compress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for LZMA");
            using var outputStream = new MemoryStream(input.Length + 256);

            // LZMA header: magic bytes + uncompressed size
            outputStream.WriteByte(0x4C); // 'L'
            outputStream.WriteByte(0x5A); // 'Z'
            outputStream.WriteByte(0x4D); // 'M'
            outputStream.WriteByte(0x41); // 'A'

            // Write uncompressed size (8 bytes LE)
            var sizeBytes = BitConverter.GetBytes((long)input.Length);
            outputStream.Write(sizeBytes, 0, 8);

            // Use SharpCompress LzmaStream for compression
            var props = new LzmaEncoderProperties();
            using var lzmaStream = LzmaStream.Create(props, false, outputStream);
            lzmaStream.Write(input, 0, input.Length);

            return outputStream.ToArray();
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            IncrementCounter("lzma.decompress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for LZMA");
            if (input.Length < 12) // 4 magic + 8 size bytes
                throw new InvalidDataException("LZMA compressed data is too short.");

            using var inputStream = new MemoryStream(input);

            // Read and verify magic bytes
            if (inputStream.ReadByte() != 0x4C || inputStream.ReadByte() != 0x5A ||
                inputStream.ReadByte() != 0x4D || inputStream.ReadByte() != 0x41)
                throw new InvalidDataException("Invalid LZMA magic bytes.");

            // Read uncompressed size (8 bytes LE)
            var sizeBytes = new byte[8];
            if (inputStream.Read(sizeBytes, 0, 8) != 8)
                throw new InvalidDataException("Failed to read LZMA uncompressed size.");

            long uncompressedSize = BitConverter.ToInt64(sizeBytes, 0);
            if (uncompressedSize < 0 || uncompressedSize > 256L * 1024 * 1024)
                throw new InvalidDataException($"Invalid LZMA uncompressed size: {uncompressedSize}");

            using var outputStream = new MemoryStream((int)uncompressedSize);

            // Use SharpCompress LzmaStream for decompression
            var remainingBytes = new byte[input.Length - 12];
            inputStream.Read(remainingBytes, 0, remainingBytes.Length);

            // Read properties (first 5 bytes of compressed data)
            using var compressedStream = new MemoryStream(remainingBytes);
            using var lzmaDecompStream = LzmaStream.Create(remainingBytes.AsSpan(0, 5).ToArray(), compressedStream, uncompressedSize, false);
            lzmaDecompStream.CopyTo(outputStream);

            return outputStream.ToArray();
        }

        /// <inheritdoc/>
        protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
        {
            return new BufferedAlgorithmCompressionStream(output, leaveOpen, CompressCore);
        }

        /// <inheritdoc/>
        protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
        {
            return new BufferedAlgorithmDecompressionStream(input, leaveOpen, DecompressCore);
        }

        /// <inheritdoc/>
        public override long EstimateCompressedSize(long inputSize)
        {
            // LZMA can achieve very high compression; worst case ~input + overhead
            return (long)(inputSize * 1.1) + 13 + 256;
        }
    }
}
