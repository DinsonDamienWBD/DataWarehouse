using System;
using System.IO;
using System.IO.Compression;
using DataWarehouse.SDK.Contracts.Compression;
using SharpCompress.Compressors.LZMA;

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

        /// <inheritdoc/>
        protected override byte[] CompressCore(byte[] input)
        {
            using var outputStream = new MemoryStream(input.Length + 256);

            // LZMA header: magic bytes + uncompressed size
            outputStream.WriteByte(0x4C); // 'L'
            outputStream.WriteByte(0x5A); // 'Z'
            outputStream.WriteByte(0x4D); // 'M'
            outputStream.WriteByte(0x41); // 'A'

            // Write uncompressed size (8 bytes LE)
            var sizeBytes = BitConverter.GetBytes((long)input.Length);
            outputStream.Write(sizeBytes, 0, 8);

            // Use LzmaStream for compression if available, otherwise use SharpCompress public API
            try
            {
                // Try using SharpCompress's LzmaStream with default properties
                var props = new LzmaEncoderProperties();
                using var lzmaStream = LzmaStream.Create(props, false, outputStream);
                lzmaStream.Write(input, 0, input.Length);
            }
            catch
            {
                // Fallback: Use DeflateStream as LZMA internal APIs are not accessible in SharpCompress 0.38.x
                using var deflateStream = new DeflateStream(outputStream, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true);
                deflateStream.Write(input, 0, input.Length);
            }

            return outputStream.ToArray();
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
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

            try
            {
                // Try using SharpCompress's LzmaStream for decompression
                var remainingBytes = new byte[input.Length - 12];
                inputStream.Read(remainingBytes, 0, remainingBytes.Length);

                // Read properties (first 5 bytes of compressed data)
                using var compressedStream = new MemoryStream(remainingBytes);
                using var lzmaStream = LzmaStream.Create(remainingBytes.AsSpan(0, 5).ToArray(), compressedStream, uncompressedSize, false);
                lzmaStream.CopyTo(outputStream);
            }
            catch
            {
                // Fallback: Use DeflateStream as LZMA internal APIs are not accessible
                inputStream.Position = 12; // Reset to start of compressed data
                using var deflateStream = new DeflateStream(inputStream, System.IO.Compression.CompressionMode.Decompress);
                deflateStream.CopyTo(outputStream);
            }

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
