using System;
using System.IO;
using System.Text;
using DataWarehouse.SDK.Contracts.Compression;

namespace DataWarehouse.Plugins.UltimateCompression.Strategies.Archive
{
    /// <summary>
    /// TAR (Tape Archive) format strategy implementing POSIX.1-1988 (ustar) standard.
    /// Creates archive containers without compression - pure archival wrapper.
    /// Each entry includes 512-byte header with metadata and padded data blocks.
    /// </summary>
    /// <remarks>
    /// TAR format characteristics:
    /// 1. Simple, well-documented format for file archiving
    /// 2. 512-byte header per entry (ustar format)
    /// 3. Data padded to 512-byte boundaries
    /// 4. No built-in compression (typically paired with gzip/bzip2)
    /// 5. Preserves Unix file permissions and metadata
    /// This implementation creates minimal single-entry TAR archives.
    /// </remarks>
    public sealed class TarStrategy : CompressionStrategyBase
    {
        private const int BlockSize = 512;
        private const string DefaultFileName = "data.bin";

        /// <summary>
        /// Initializes a new instance of the <see cref="TarStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public TarStrategy() : base(CompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "TAR",
            TypicalCompressionRatio = 1.0, // No compression, archive only
            CompressionSpeed = 10,
            DecompressionSpeed = 10,
            CompressionMemoryUsage = 16 * 1024,
            DecompressionMemoryUsage = 16 * 1024,
            SupportsStreaming = true,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = true,
            MinimumRecommendedSize = 1,
            OptimalBlockSize = BlockSize
        };

        /// <inheritdoc/>
        protected override byte[] CompressCore(byte[] input)
        {
            using var output = new MemoryStream();

            // Write TAR header
            WriteTarHeader(output, DefaultFileName, input.Length);

            // Write data
            output.Write(input, 0, input.Length);

            // Pad to 512-byte boundary
            int padding = (BlockSize - (input.Length % BlockSize)) % BlockSize;
            if (padding > 0)
            {
                byte[] paddingBytes = new byte[padding];
                output.Write(paddingBytes, 0, padding);
            }

            // Write end-of-archive marker (two zero blocks)
            byte[] endMarker = new byte[BlockSize * 2];
            output.Write(endMarker, 0, endMarker.Length);

            return output.ToArray();
        }

        private static void WriteTarHeader(Stream output, string fileName, int fileSize)
        {
            byte[] header = new byte[BlockSize];

            // File name (100 bytes)
            byte[] nameBytes = Encoding.ASCII.GetBytes(fileName);
            Array.Copy(nameBytes, 0, header, 0, Math.Min(nameBytes.Length, 100));

            // File mode (8 bytes) - octal "0000644\0"
            WriteOctalString(header, 100, "0000644", 8);

            // Owner UID (8 bytes) - octal "0000000\0"
            WriteOctalString(header, 108, "0000000", 8);

            // Owner GID (8 bytes) - octal "0000000\0"
            WriteOctalString(header, 116, "0000000", 8);

            // File size (12 bytes) - octal
            WriteOctalString(header, 124, Convert.ToString(fileSize, 8), 12);

            // Modification time (12 bytes) - octal Unix timestamp
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            WriteOctalString(header, 136, Convert.ToString(timestamp, 8), 12);

            // Checksum placeholder (8 bytes) - fill with spaces initially
            for (int i = 148; i < 156; i++)
                header[i] = (byte)' ';

            // Type flag (1 byte) - '0' for regular file
            header[156] = (byte)'0';

            // Link name (100 bytes) - empty for regular file

            // USTAR indicator (6 bytes) - "ustar\0"
            byte[] ustar = Encoding.ASCII.GetBytes("ustar");
            Array.Copy(ustar, 0, header, 257, ustar.Length);
            header[263] = 0;

            // USTAR version (2 bytes) - "00"
            header[264] = (byte)'0';
            header[265] = (byte)'0';

            // Owner user name (32 bytes) - "root"
            byte[] owner = Encoding.ASCII.GetBytes("root");
            Array.Copy(owner, 0, header, 265, owner.Length);

            // Owner group name (32 bytes) - "root"
            Array.Copy(owner, 0, header, 297, owner.Length);

            // Calculate and write checksum
            uint checksum = 0;
            for (int i = 0; i < BlockSize; i++)
                checksum += header[i];

            WriteOctalString(header, 148, Convert.ToString(checksum, 8), 7);
            header[155] = 0; // Null terminator for checksum

            output.Write(header, 0, BlockSize);
        }

        private static void WriteOctalString(byte[] buffer, int offset, string value, int fieldSize)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(value);
            int copyLength = Math.Min(bytes.Length, fieldSize - 1);
            Array.Copy(bytes, 0, buffer, offset, copyLength);

            // Pad with spaces if needed
            for (int i = copyLength; i < fieldSize - 1; i++)
                buffer[offset + i] = (byte)' ';

            // Null terminator or space
            buffer[offset + fieldSize - 1] = 0;
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            using var stream = new MemoryStream(input);

            // Read TAR header
            byte[] header = new byte[BlockSize];
            int read = stream.Read(header, 0, BlockSize);

            if (read != BlockSize)
                throw new InvalidDataException("Invalid TAR archive - incomplete header.");

            // Verify USTAR signature
            byte[] ustarCheck = new byte[6];
            Array.Copy(header, 257, ustarCheck, 0, 6);
            string ustarSignature = Encoding.ASCII.GetString(ustarCheck).TrimEnd('\0');

            if (ustarSignature != "ustar")
                throw new InvalidDataException("Not a valid USTAR TAR archive.");

            // Read file size from header (octal string at offset 124)
            string sizeOctal = Encoding.ASCII.GetString(header, 124, 12).TrimEnd('\0', ' ');
            int fileSize = Convert.ToInt32(sizeOctal, 8);

            // Read file data
            byte[] data = new byte[fileSize];
            read = stream.Read(data, 0, fileSize);

            if (read != fileSize)
                throw new InvalidDataException($"Invalid TAR archive - expected {fileSize} bytes, got {read}.");

            return data;
        }

        /// <inheritdoc/>
        protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
        {
            return new TarCompressionStream(output, leaveOpen, this);
        }

        /// <inheritdoc/>
        protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
        {
            return new TarDecompressionStream(input, leaveOpen, this);
        }

        /// <inheritdoc/>
        public override long EstimateCompressedSize(long inputSize)
        {
            // TAR overhead: 512-byte header + padding + 1024-byte end marker
            long padding = (BlockSize - (inputSize % BlockSize)) % BlockSize;
            return inputSize + BlockSize + padding + (BlockSize * 2);
        }

        #region Stream Wrappers

        private sealed class TarCompressionStream : Stream
        {
            private readonly Stream _output;
            private readonly bool _leaveOpen;
            private readonly TarStrategy _strategy;
            private readonly MemoryStream _buffer = new();
            private bool _disposed;

            public TarCompressionStream(Stream output, bool leaveOpen, TarStrategy strategy)
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
                FlushArchive();
            }

            private void FlushArchive()
            {
                if (_buffer.Length == 0)
                    return;

                byte[] archived = _strategy.CompressCore(_buffer.ToArray());
                _output.Write(archived, 0, archived.Length);
                _output.Flush();
                _buffer.SetLength(0);
            }

            protected override void Dispose(bool disposing)
            {
                if (!_disposed && disposing)
                {
                    FlushArchive();
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

        private sealed class TarDecompressionStream : Stream
        {
            private readonly Stream _input;
            private readonly bool _leaveOpen;
            private readonly TarStrategy _strategy;
            private byte[]? _decompressedData;
            private int _position;
            private bool _disposed;

            public TarDecompressionStream(Stream input, bool leaveOpen, TarStrategy strategy)
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
