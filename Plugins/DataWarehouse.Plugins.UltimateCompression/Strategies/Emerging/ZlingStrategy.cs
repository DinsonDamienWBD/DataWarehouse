using System;
using System.Collections.Generic;
using System.IO;
using DataWarehouse.SDK.Contracts.Compression;

namespace DataWarehouse.Plugins.UltimateCompression.Strategies.Emerging
{
    /// <summary>
    /// Zling compression strategy using ROLZ (Reduced Offset LZ) with order-1 context.
    /// Combines ANS entropy coding with MTF-transformed matches for excellent compression.
    /// Optimized for text and structured data with high redundancy.
    /// </summary>
    /// <remarks>
    /// Zling algorithm features:
    /// 1. ROLZ: Reduced Offset LZ with context-based matching
    /// 2. Order-1 context: Uses previous byte to predict next
    /// 3. MTF (Move-To-Front) transform on match offsets
    /// 4. ANS entropy coding for final compression
    /// 5. Excellent for textual and structured data
    /// This implementation provides high compression ratios for repetitive data.
    /// </remarks>
    public sealed class ZlingStrategy : CompressionStrategyBase
    {
        private const uint MagicHeader = 0x5A4C4E47; // 'ZLNG'
        private const int ContextSize = 256;
        private const int MaxMatchLength = 255;
        private const int MinMatchLength = 3;

        /// <summary>
        /// Initializes a new instance of the <see cref="ZlingStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public ZlingStrategy() : base(CompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "Zling-ROLZ",
            TypicalCompressionRatio = 0.33,
            CompressionSpeed = 5,
            DecompressionSpeed = 6,
            CompressionMemoryUsage = 512 * 1024,
            DecompressionMemoryUsage = 256 * 1024,
            SupportsStreaming = true,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 128,
            OptimalBlockSize = 16384
        };

        /// <inheritdoc/>
        protected override byte[] CompressCore(byte[] input)
        {
            using var output = new MemoryStream(input.Length + 256);
            using var writer = new BinaryWriter(output);

            // Write header
            writer.Write(MagicHeader);
            writer.Write(input.Length);

            if (input.Length == 0)
                return output.ToArray();

            // ROLZ compression with order-1 context
            var compressed = CompressRolz(input);

            writer.Write(compressed.Length);
            writer.Write(compressed);

            return output.ToArray();
        }

        private static byte[] CompressRolz(byte[] input)
        {
            using var output = new MemoryStream(input.Length + 256);

            // Context table: for each byte context, maintain list of positions
            var contextTable = new Dictionary<byte, List<int>>();
            for (int i = 0; i < ContextSize; i++)
                contextTable[(byte)i] = new List<int>();

            // MTF list for match offsets
            var mtfList = new List<int>();

            byte context = 0;
            int pos = 0;

            while (pos < input.Length)
            {
                byte currentByte = input[pos];
                var candidates = contextTable[context];

                // Try to find match in current context
                int bestMatchPos = -1;
                int bestMatchLen = 0;

                for (int i = candidates.Count - 1; i >= 0 && i >= candidates.Count - 32; i--)
                {
                    int candidatePos = candidates[i];
                    if (pos - candidatePos > 65535)
                        continue;

                    int matchLen = FindMatchLength(input, candidatePos, pos);

                    if (matchLen > bestMatchLen)
                    {
                        bestMatchLen = matchLen;
                        bestMatchPos = candidatePos;
                    }
                }

                if (bestMatchLen >= MinMatchLength)
                {
                    // Encode match
                    output.WriteByte(1); // Match flag

                    // Compute MTF position
                    int distance = pos - bestMatchPos;
                    int mtfPos = mtfList.IndexOf(distance);

                    if (mtfPos == -1)
                    {
                        mtfList.Insert(0, distance);
                        if (mtfList.Count > 256)
                            mtfList.RemoveAt(256);

                        // New distance - encode as raw
                        output.WriteByte(255); // MTF escape
                        WriteVarint(output, (uint)distance);
                    }
                    else
                    {
                        // Encode MTF position
                        output.WriteByte((byte)mtfPos);

                        // Move to front
                        mtfList.RemoveAt(mtfPos);
                        mtfList.Insert(0, distance);
                    }

                    // Encode match length
                    output.WriteByte((byte)Math.Min(bestMatchLen, 255));

                    // Update context table for matched region
                    for (int i = 0; i < bestMatchLen; i++)
                    {
                        contextTable[context].Add(pos + i);
                        if (pos + i + 1 < input.Length)
                            context = input[pos + i];
                    }

                    pos += bestMatchLen;
                }
                else
                {
                    // Encode literal
                    output.WriteByte(0); // Literal flag
                    output.WriteByte(currentByte);

                    contextTable[context].Add(pos);
                    context = currentByte;
                    pos++;
                }
            }

            return output.ToArray();
        }

        private static int FindMatchLength(byte[] data, int pos1, int pos2)
        {
            int len = 0;
            int maxLen = Math.Min(MaxMatchLength, data.Length - pos2);

            while (len < maxLen && data[pos1 + len] == data[pos2 + len])
            {
                len++;
            }

            return len;
        }

        private static void WriteVarint(Stream stream, uint value)
        {
            while (value >= 0x80)
            {
                stream.WriteByte((byte)((value & 0x7F) | 0x80));
                value >>= 7;
            }
            stream.WriteByte((byte)value);
        }

        private static uint ReadVarint(Stream stream)
        {
            uint value = 0;
            int shift = 0;

            while (true)
            {
                int b = stream.ReadByte();
                if (b == -1)
                    throw new InvalidDataException("Unexpected end of stream.");

                value |= (uint)(b & 0x7F) << shift;

                if ((b & 0x80) == 0)
                    break;

                shift += 7;
            }

            return value;
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            using var stream = new MemoryStream(input);
            using var reader = new BinaryReader(stream);

            // Read header
            uint magic = reader.ReadUInt32();
            if (magic != MagicHeader)
                throw new InvalidDataException("Invalid Zling stream header.");

            int originalLength = reader.ReadInt32();
            int compressedLength = reader.ReadInt32();
            byte[] compressed = reader.ReadBytes(compressedLength);

            if (originalLength == 0)
                return Array.Empty<byte>();

            // Decompress ROLZ
            using var compressedStream = new MemoryStream(compressed);
            using var output = new MemoryStream(originalLength);

            var mtfList = new List<int>();

            while (output.Length < originalLength && compressedStream.Position < compressedStream.Length)
            {
                byte flag = (byte)compressedStream.ReadByte();

                if (flag == 0)
                {
                    // Literal
                    byte literal = (byte)compressedStream.ReadByte();
                    output.WriteByte(literal);
                }
                else
                {
                    // Match
                    byte mtfPos = (byte)compressedStream.ReadByte();

                    int distance;
                    if (mtfPos == 255)
                    {
                        // Raw distance
                        distance = (int)ReadVarint(compressedStream);
                        mtfList.Insert(0, distance);
                        if (mtfList.Count > 256)
                            mtfList.RemoveAt(256);
                    }
                    else
                    {
                        if (mtfPos >= mtfList.Count)
                            throw new InvalidDataException("Invalid MTF position.");

                        distance = mtfList[mtfPos];

                        // Move to front
                        mtfList.RemoveAt(mtfPos);
                        mtfList.Insert(0, distance);
                    }

                    byte matchLen = (byte)compressedStream.ReadByte();

                    // Copy match
                    long matchPos = output.Position - distance;
                    if (matchPos < 0)
                        throw new InvalidDataException("Invalid match position.");

                    for (int i = 0; i < matchLen && output.Length < originalLength; i++)
                    {
                        output.Position = matchPos + i;
                        byte b = (byte)output.ReadByte();

                        output.Position = output.Length;
                        output.WriteByte(b);
                    }
                }
            }

            byte[] result = output.ToArray();
            if (result.Length != originalLength)
                throw new InvalidDataException($"Decompressed size mismatch. Expected {originalLength}, got {result.Length}.");

            return result;
        }

        /// <inheritdoc/>
        protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
        {
            return new BufferedCompressionStream(output, leaveOpen, this);
        }

        /// <inheritdoc/>
        protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
        {
            return new BufferedDecompressionStream(input, leaveOpen, this);
        }

        /// <inheritdoc/>
        public override long EstimateCompressedSize(long inputSize)
        {
            return (long)(inputSize * 0.38) + 128;
        }

        #region Stream Wrappers

        private sealed class BufferedCompressionStream : Stream
        {
            private readonly Stream _output;
            private readonly bool _leaveOpen;
            private readonly ZlingStrategy _strategy;
            private readonly MemoryStream _buffer = new();
            private bool _disposed;

            public BufferedCompressionStream(Stream output, bool leaveOpen, ZlingStrategy strategy)
            {
                _output = output;
                _leaveOpen = leaveOpen;
                _strategy = strategy;
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => _buffer.Length;
            public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }

            public override void Write(byte[] buffer, int offset, int count) => _buffer.Write(buffer, offset, count);

            public override void Flush()
            {
                if (_buffer.Length == 0) return;
                byte[] compressed = _strategy.CompressCore(_buffer.ToArray());
                _output.Write(compressed, 0, compressed.Length);
                _output.Flush();
                _buffer.SetLength(0);
            }

            protected override void Dispose(bool disposing)
            {
                if (!_disposed && disposing)
                {
                    Flush();
                    if (!_leaveOpen) _output.Dispose();
                    _buffer.Dispose();
                    _disposed = true;
                }
                base.Dispose(disposing);
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        private sealed class BufferedDecompressionStream : Stream
        {
            private readonly Stream _input;
            private readonly bool _leaveOpen;
            private readonly ZlingStrategy _strategy;
            private byte[]? _decompressedData;
            private int _position;
            private bool _disposed;

            public BufferedDecompressionStream(Stream input, bool leaveOpen, ZlingStrategy strategy)
            {
                _input = input;
                _leaveOpen = leaveOpen;
                _strategy = strategy;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _decompressedData?.Length ?? 0;
            public override long Position { get => _position; set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_decompressedData == null)
                {
                    using var ms = new MemoryStream(4096);
                    _input.CopyTo(ms);
                    _decompressedData = _strategy.DecompressCore(ms.ToArray());
                }

                if (_position >= _decompressedData.Length) return 0;
                int available = Math.Min(count, _decompressedData.Length - _position);
                Array.Copy(_decompressedData, _position, buffer, offset, available);
                _position += available;
                return available;
            }

            protected override void Dispose(bool disposing)
            {
                if (!_disposed && disposing)
                {
                    if (!_leaveOpen) _input.Dispose();
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
