using System;
using System.Buffers.Binary;
using System.IO;
using DataWarehouse.SDK.Contracts.Compression;

namespace DataWarehouse.Plugins.UltimateCompression.Strategies.EntropyCoding
{
    /// <summary>
    /// Compression strategy implementing Run-Length Encoding (RLE).
    /// Encodes sequences of repeated bytes as [escape byte, count, byte value] triplets,
    /// providing efficient compression for data with long runs of identical values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Run-Length Encoding (1967) is one of the simplest compression methods. It replaces
    /// runs of repeated bytes with a compact representation. This implementation uses an
    /// escape byte (0xFF) to mark run encodings, allowing non-run data to pass through
    /// unencoded except for the escape byte itself, which is doubled.
    /// </para>
    /// <para>
    /// RLE is highly effective for data with many repeated values (graphics, simple images,
    /// simple data patterns) but can expand data with no repetition. Modern usage includes
    /// BMP image compression and as a preprocessing step for other algorithms.
    /// </para>
    /// <para>
    /// Format: [Magic:4][OrigLen:4][EncodedData]
    /// EncodedData: Non-run bytes pass through, 0xFF becomes 0xFF 0xFF, runs become 0xFF count byte
    /// </para>
    /// </remarks>
    public sealed class RleStrategy : CompressionStrategyBase
    {
        private static readonly byte[] Magic = { 0x52, 0x4C, 0x45, 0x31 }; // "RLE1"
        private const byte EscapeByte = 0xFF;
        private const int MinRunLength = 3;

        /// <summary>
        /// Initializes a new instance of the <see cref="RleStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public RleStrategy() : base(CompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "RLE",
            TypicalCompressionRatio = 0.70,
            CompressionSpeed = 9,
            DecompressionSpeed = 10,
            CompressionMemoryUsage = 4L * 1024 * 1024,
            DecompressionMemoryUsage = 4L * 1024 * 1024,
            SupportsStreaming = true,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 16,
            OptimalBlockSize = 256 * 1024
        };

        /// <inheritdoc/>
        protected override byte[] CompressCore(byte[] input)
        {
            using var output = new MemoryStream();
            output.Write(Magic, 0, 4);

            var lenBytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lenBytes, input.Length);
            output.Write(lenBytes, 0, 4);

            if (input.Length == 0)
                return output.ToArray();

            int pos = 0;
            while (pos < input.Length)
            {
                byte currentByte = input[pos];

                // Count run length
                int runLength = 1;
                while (pos + runLength < input.Length &&
                       input[pos + runLength] == currentByte &&
                       runLength < 255)
                {
                    runLength++;
                }

                // Decide whether to encode as run
                if (runLength >= MinRunLength || currentByte == EscapeByte)
                {
                    // Encode as run
                    output.WriteByte(EscapeByte);
                    output.WriteByte((byte)runLength);
                    output.WriteByte(currentByte);
                    pos += runLength;
                }
                else
                {
                    // Output literal byte
                    output.WriteByte(currentByte);
                    pos++;
                }
            }

            return output.ToArray();
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            using var stream = new MemoryStream(input);

            var magicBuf = new byte[4];
            if (stream.Read(magicBuf, 0, 4) != 4 ||
                magicBuf[0] != Magic[0] || magicBuf[1] != Magic[1] ||
                magicBuf[2] != Magic[2] || magicBuf[3] != Magic[3])
            {
                throw new InvalidDataException("Invalid RLE header magic.");
            }

            var lenBuf = new byte[4];
            if (stream.Read(lenBuf, 0, 4) != 4)
                throw new InvalidDataException("Invalid RLE header length.");

            int originalLength = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
            if (originalLength < 0)
                throw new InvalidDataException("Invalid original length in RLE header.");

            if (originalLength == 0)
                return Array.Empty<byte>();

            using var output = new MemoryStream(originalLength);

            while (output.Length < originalLength)
            {
                int b = stream.ReadByte();
                if (b < 0)
                    throw new InvalidDataException("Unexpected end of RLE data.");

                if (b == EscapeByte)
                {
                    // Read run encoding
                    int count = stream.ReadByte();
                    if (count < 0)
                        throw new InvalidDataException("Unexpected end of RLE data (count).");

                    int value = stream.ReadByte();
                    if (value < 0)
                        throw new InvalidDataException("Unexpected end of RLE data (value).");

                    // Write run
                    for (int i = 0; i < count; i++)
                        output.WriteByte((byte)value);
                }
                else
                {
                    // Literal byte
                    output.WriteByte((byte)b);
                }
            }

            return output.ToArray();
        }

        /// <inheritdoc/>
        protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
        {
            return new RleCompressionStream(output, leaveOpen);
        }

        /// <inheritdoc/>
        protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
        {
            return new RleDecompressionStream(input, leaveOpen);
        }

        /// <inheritdoc/>
        public override long EstimateCompressedSize(long inputSize)
        {
            // Worst case: every byte is escaped (3 bytes per byte) + header
            return inputSize * 3 + 16;
        }

        #region Streaming Support

        /// <summary>
        /// Streaming compression for RLE.
        /// </summary>
        private sealed class RleCompressionStream : Stream
        {
            private readonly Stream _output;
            private readonly bool _leaveOpen;
            private readonly byte[] _buffer = new byte[1024];
            private int _bufferPos;
            private bool _disposed;
            private bool _headerWritten;

            public RleCompressionStream(Stream output, bool leaveOpen)
            {
                _output = output;
                _leaveOpen = leaveOpen;
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (!_headerWritten)
                {
                    _output.Write(Magic, 0, 4);
                    // Write placeholder length (will be incorrect for streaming)
                    _output.Write(new byte[4], 0, 4);
                    _headerWritten = true;
                }

                // Simple implementation: buffer and compress in blocks
                for (int i = 0; i < count; i++)
                {
                    _buffer[_bufferPos++] = buffer[offset + i];

                    if (_bufferPos >= _buffer.Length)
                    {
                        FlushBuffer();
                    }
                }
            }

            private void FlushBuffer()
            {
                if (_bufferPos == 0) return;

                int pos = 0;
                while (pos < _bufferPos)
                {
                    byte currentByte = _buffer[pos];
                    int runLength = 1;

                    while (pos + runLength < _bufferPos &&
                           _buffer[pos + runLength] == currentByte &&
                           runLength < 255)
                    {
                        runLength++;
                    }

                    if (runLength >= MinRunLength || currentByte == EscapeByte)
                    {
                        _output.WriteByte(EscapeByte);
                        _output.WriteByte((byte)runLength);
                        _output.WriteByte(currentByte);
                        pos += runLength;
                    }
                    else
                    {
                        _output.WriteByte(currentByte);
                        pos++;
                    }
                }

                _bufferPos = 0;
            }

            public override void Flush()
            {
                FlushBuffer();
                _output.Flush();
            }

            public override int Read(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) =>
                throw new NotSupportedException();
            public override void SetLength(long value) =>
                throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (!_disposed && disposing)
                {
                    _disposed = true;
                    FlushBuffer();
                    if (!_leaveOpen) _output.Dispose();
                }
                base.Dispose(disposing);
            }
        }

        /// <summary>
        /// Streaming decompression for RLE.
        /// </summary>
        private sealed class RleDecompressionStream : Stream
        {
            private readonly Stream _input;
            private readonly bool _leaveOpen;
            private readonly byte[] _runBuffer = new byte[256];
            private int _runBufferPos;
            private int _runBufferCount;
            private bool _disposed;
            private bool _headerRead;

            public RleDecompressionStream(Stream input, bool leaveOpen)
            {
                _input = input;
                _leaveOpen = leaveOpen;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (!_headerRead)
                {
                    var magicBuf = new byte[4];
                    if (_input.Read(magicBuf, 0, 4) != 4 ||
                        magicBuf[0] != Magic[0] || magicBuf[1] != Magic[1] ||
                        magicBuf[2] != Magic[2] || magicBuf[3] != Magic[3])
                    {
                        throw new InvalidDataException("Invalid RLE header magic.");
                    }
                    _input.Read(new byte[4], 0, 4); // Skip length
                    _headerRead = true;
                }

                int totalRead = 0;

                while (totalRead < count)
                {
                    // Read from run buffer if available
                    if (_runBufferPos < _runBufferCount)
                    {
                        int toCopy = Math.Min(count - totalRead, _runBufferCount - _runBufferPos);
                        Array.Copy(_runBuffer, _runBufferPos, buffer, offset + totalRead, toCopy);
                        _runBufferPos += toCopy;
                        totalRead += toCopy;
                        continue;
                    }

                    // Read next token
                    int b = _input.ReadByte();
                    if (b < 0) break;

                    if (b == EscapeByte)
                    {
                        int runCount = _input.ReadByte();
                        if (runCount < 0) break;

                        int value = _input.ReadByte();
                        if (value < 0) break;

                        // Fill run buffer
                        for (int i = 0; i < runCount; i++)
                            _runBuffer[i] = (byte)value;

                        _runBufferPos = 0;
                        _runBufferCount = runCount;
                    }
                    else
                    {
                        buffer[offset + totalRead++] = (byte)b;
                    }
                }

                return totalRead;
            }

            public override void Write(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) =>
                throw new NotSupportedException();
            public override void SetLength(long value) =>
                throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (!_disposed && disposing)
                {
                    _disposed = true;
                    if (!_leaveOpen) _input.Dispose();
                }
                base.Dispose(disposing);
            }
        }

        #endregion
    }
}
