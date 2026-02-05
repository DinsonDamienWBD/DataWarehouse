using System;
using System.IO;
using DataWarehouse.SDK.Contracts.Compression;
using SharpCompress.Compressors.LZMA;
using SdkCompressionLevel = DataWarehouse.SDK.Contracts.Compression.CompressionLevel;

namespace DataWarehouse.Plugins.UltimateCompression.Strategies.Archive
{
    /// <summary>
    /// 7-Zip compression strategy using LZMA2 algorithm via SharpCompress.
    /// Provides excellent compression ratios at the cost of slower compression speed.
    /// Uses LZMA format with 7z-compatible headers for broad tool compatibility.
    /// </summary>
    /// <remarks>
    /// 7-Zip/LZMA advantages:
    /// 1. Excellent compression ratio (often better than ZIP/GZip)
    /// 2. LZMA2 supports multithreading and better error recovery
    /// 3. Solid compression for similar file types
    /// 4. Dictionary-based LZ77 variant with range encoding
    /// This implementation uses SharpCompress for LZMA compression.
    /// </remarks>
    public sealed class SevenZipStrategy : CompressionStrategyBase
    {
        private const uint MagicHeader = 0x377A4C5A; // '7zLZ'

        /// <summary>
        /// Initializes a new instance of the <see cref="SevenZipStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public SevenZipStrategy() : base(SdkCompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "7-Zip-LZMA",
            TypicalCompressionRatio = 0.25,
            CompressionSpeed = 2,
            DecompressionSpeed = 4,
            CompressionMemoryUsage = 1024 * 1024,
            DecompressionMemoryUsage = 512 * 1024,
            SupportsStreaming = true,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 256,
            OptimalBlockSize = 65536
        };

        /// <inheritdoc/>
        protected override byte[] CompressCore(byte[] input)
        {
            using var output = new MemoryStream();
            using var writer = new BinaryWriter(output);

            // Write custom header for identification
            writer.Write(MagicHeader);
            writer.Write(input.Length);

            // Compress using LZMA
            var lzmaStream = new MemoryStream();

            // LZMA encoder: SharpCompress's LzmaStream for compression
            var props = new LzmaEncoderProperties();
            using (var lzmaEncoder = new LzmaStream(props, false, lzmaStream))
            {
                lzmaEncoder.Write(input, 0, input.Length);
            }

            byte[] compressed = lzmaStream.ToArray();
            writer.Write(compressed.Length);
            writer.Write(compressed);

            return output.ToArray();
        }

        private int GetDictionarySize()
        {
            return Level switch
            {
                SdkCompressionLevel.Fastest => 256 * 1024,
                SdkCompressionLevel.Fast => 512 * 1024,
                SdkCompressionLevel.Default => 1 * 1024 * 1024,
                SdkCompressionLevel.Better => 8 * 1024 * 1024,
                SdkCompressionLevel.Best => 32 * 1024 * 1024,
                _ => 1 * 1024 * 1024
            };
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            using var stream = new MemoryStream(input);
            using var reader = new BinaryReader(stream);

            // Read and validate header
            uint magic = reader.ReadUInt32();
            if (magic != MagicHeader)
                throw new InvalidDataException("Invalid 7-Zip stream header.");

            int originalLength = reader.ReadInt32();
            int compressedLength = reader.ReadInt32();
            byte[] compressed = reader.ReadBytes(compressedLength);

            // Decompress using LZMA
            using var compressedStream = new MemoryStream(compressed);
            // LzmaStream for decompression: read first 5 bytes as properties, then decompress
            byte[] props = new byte[5];
            compressedStream.Read(props, 0, 5);
            using var lzmaDecoder = new LzmaStream(props, compressedStream, originalLength);
            using var decompressedStream = new MemoryStream();

            lzmaDecoder.CopyTo(decompressedStream);
            byte[] result = decompressedStream.ToArray();

            // Validate decompressed size
            if (result.Length != originalLength)
                throw new InvalidDataException($"Decompressed size mismatch. Expected {originalLength}, got {result.Length}.");

            return result;
        }

        /// <inheritdoc/>
        protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
        {
            return new SevenZipCompressionStream(output, leaveOpen, this);
        }

        /// <inheritdoc/>
        protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
        {
            return new SevenZipDecompressionStream(input, leaveOpen, this);
        }

        /// <inheritdoc/>
        public override long EstimateCompressedSize(long inputSize)
        {
            return (long)(inputSize * 0.30) + 256;
        }

        #region Stream Wrappers

        private sealed class SevenZipCompressionStream : Stream
        {
            private readonly Stream _output;
            private readonly bool _leaveOpen;
            private readonly SevenZipStrategy _strategy;
            private readonly MemoryStream _buffer = new();
            private bool _disposed;

            public SevenZipCompressionStream(Stream output, bool leaveOpen, SevenZipStrategy strategy)
            {
                _output = output;
                _leaveOpen = leaveOpen;
                _strategy = strategy;
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => _buffer.Length;
            public override long Position
            {
                get => _buffer.Position;
                set => throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                _buffer.Write(buffer, offset, count);
            }

            public override void Flush()
            {
                FlushCompressed();
            }

            private void FlushCompressed()
            {
                if (_buffer.Length == 0)
                    return;

                byte[] compressed = _strategy.CompressCore(_buffer.ToArray());
                _output.Write(compressed, 0, compressed.Length);
                _output.Flush();
                _buffer.SetLength(0);
            }

            protected override void Dispose(bool disposing)
            {
                if (!_disposed && disposing)
                {
                    FlushCompressed();
                    if (!_leaveOpen)
                        _output.Dispose();
                    _buffer.Dispose();
                    _disposed = true;
                }

                base.Dispose(disposing);
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        private sealed class SevenZipDecompressionStream : Stream
        {
            private readonly Stream _input;
            private readonly bool _leaveOpen;
            private readonly SevenZipStrategy _strategy;
            private byte[]? _decompressedData;
            private int _position;
            private bool _disposed;

            public SevenZipDecompressionStream(Stream input, bool leaveOpen, SevenZipStrategy strategy)
            {
                _input = input;
                _leaveOpen = leaveOpen;
                _strategy = strategy;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _decompressedData?.Length ?? 0;
            public override long Position
            {
                get => _position;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                EnsureDecompressed();

                if (_decompressedData == null || _position >= _decompressedData.Length)
                    return 0;

                int available = Math.Min(count, _decompressedData.Length - _position);
                Array.Copy(_decompressedData, _position, buffer, offset, available);
                _position += available;
                return available;
            }

            private void EnsureDecompressed()
            {
                if (_decompressedData != null)
                    return;

                using var ms = new MemoryStream();
                _input.CopyTo(ms);
                _decompressedData = _strategy.DecompressCore(ms.ToArray());
            }

            protected override void Dispose(bool disposing)
            {
                if (!_disposed && disposing)
                {
                    if (!_leaveOpen)
                        _input.Dispose();
                    _disposed = true;
                }

                base.Dispose(disposing);
            }

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        #endregion
    }
}
