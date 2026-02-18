using System;
using System.IO;
using DataWarehouse.SDK.Contracts.Compression;

namespace DataWarehouse.Plugins.UltimateCompression.Strategies.Domain
{
    /// <summary>
    /// Time-series compression strategy inspired by Facebook's Gorilla algorithm.
    /// Uses XOR encoding of consecutive 8-byte values with leading/trailing zero compression.
    /// Highly efficient for temporal data where consecutive values are similar.
    /// </summary>
    /// <remarks>
    /// Gorilla compression exploits the temporal locality in time-series data:
    /// 1. Treats input as stream of 64-bit values (timestamps or measurements)
    /// 2. XORs consecutive values to find differences
    /// 3. Compresses XOR results by encoding only changed bits
    /// 4. Uses variable-length encoding based on leading/trailing zero count
    /// Achieves excellent compression for monitoring metrics and sensor data.
    /// </remarks>
    public sealed class TimeSeriesStrategy : CompressionStrategyBase
    {
        private const uint MagicHeader = 0x474F5249; // 'GORI' (Gorilla)
        private const int ValueSize = 8; // 64-bit values

        /// <summary>
        /// Initializes a new instance of the <see cref="TimeSeriesStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public TimeSeriesStrategy() : base(CompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "Gorilla-TimeSeries",
            TypicalCompressionRatio = 0.50,
            CompressionSpeed = 7,
            DecompressionSpeed = 8,
            CompressionMemoryUsage = 64 * 1024,
            DecompressionMemoryUsage = 64 * 1024,
            SupportsStreaming = true,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 64,
            OptimalBlockSize = 4096
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

            // Convert to 64-bit values (pad if necessary)
            int valueCount = (input.Length + ValueSize - 1) / ValueSize;
            var values = new ulong[valueCount];

            for (int i = 0; i < valueCount; i++)
            {
                ulong value = 0;
                for (int j = 0; j < ValueSize; j++)
                {
                    int idx = i * ValueSize + j;
                    if (idx < input.Length)
                        value |= (ulong)input[idx] << (j * 8);
                }
                values[i] = value;
            }

            writer.Write(valueCount);

            // Compress using Gorilla algorithm
            using var bitStream = new MemoryStream();
            var bitWriter = new GorillaWriter(bitStream);

            if (valueCount > 0)
            {
                // First value stored uncompressed
                bitWriter.WriteUInt64(values[0]);

                ulong prevValue = values[0];
                int prevLeadingZeros = 0;
                int prevTrailingZeros = 0;

                for (int i = 1; i < valueCount; i++)
                {
                    ulong current = values[i];
                    ulong xor = prevValue ^ current;

                    if (xor == 0)
                    {
                        // Value unchanged - write single '0' bit
                        bitWriter.WriteBit(0);
                    }
                    else
                    {
                        // Value changed - write '1' bit
                        bitWriter.WriteBit(1);

                        int leadingZeros = CountLeadingZeros(xor);
                        int trailingZeros = CountTrailingZeros(xor);
                        int meaningfulBits = 64 - leadingZeros - trailingZeros;

                        if (leadingZeros >= prevLeadingZeros && trailingZeros >= prevTrailingZeros)
                        {
                            // Control bit '0' - reuse previous block info
                            bitWriter.WriteBit(0);
                            ulong meaningfulValue = xor >> prevTrailingZeros;
                            int prevMeaningfulBits = 64 - prevLeadingZeros - prevTrailingZeros;
                            bitWriter.WriteBits(meaningfulValue, prevMeaningfulBits);
                        }
                        else
                        {
                            // Control bit '1' - store new block info
                            bitWriter.WriteBit(1);

                            // 6 bits for leading zeros (0-63)
                            bitWriter.WriteBits((ulong)leadingZeros, 6);

                            // 6 bits for meaningful bits length (1-64)
                            bitWriter.WriteBits((ulong)(meaningfulBits - 1), 6);

                            // Write meaningful bits
                            ulong meaningfulValue = xor >> trailingZeros;
                            bitWriter.WriteBits(meaningfulValue, meaningfulBits);

                            prevLeadingZeros = leadingZeros;
                            prevTrailingZeros = trailingZeros;
                        }
                    }

                    prevValue = current;
                }
            }

            bitWriter.Flush();
            byte[] compressed = bitStream.ToArray();
            writer.Write(compressed.Length);
            writer.Write(compressed);

            return output.ToArray();
        }

        private static int CountLeadingZeros(ulong value)
        {
            if (value == 0) return 64;
            int count = 0;
            if ((value & 0xFFFFFFFF00000000UL) == 0) { count += 32; value <<= 32; }
            if ((value & 0xFFFF000000000000UL) == 0) { count += 16; value <<= 16; }
            if ((value & 0xFF00000000000000UL) == 0) { count += 8; value <<= 8; }
            if ((value & 0xF000000000000000UL) == 0) { count += 4; value <<= 4; }
            if ((value & 0xC000000000000000UL) == 0) { count += 2; value <<= 2; }
            if ((value & 0x8000000000000000UL) == 0) { count += 1; }
            return count;
        }

        private static int CountTrailingZeros(ulong value)
        {
            if (value == 0) return 64;
            int count = 0;
            if ((value & 0xFFFFFFFF) == 0) { count += 32; value >>= 32; }
            if ((value & 0xFFFF) == 0) { count += 16; value >>= 16; }
            if ((value & 0xFF) == 0) { count += 8; value >>= 8; }
            if ((value & 0xF) == 0) { count += 4; value >>= 4; }
            if ((value & 0x3) == 0) { count += 2; value >>= 2; }
            if ((value & 0x1) == 0) { count += 1; }
            return count;
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            using var stream = new MemoryStream(input);
            using var reader = new BinaryReader(stream);

            // Read header
            uint magic = reader.ReadUInt32();
            if (magic != MagicHeader)
                throw new InvalidDataException("Invalid Gorilla stream header.");

            int originalLength = reader.ReadInt32();
            if (originalLength == 0)
                return Array.Empty<byte>();

            int valueCount = reader.ReadInt32();
            int compressedLength = reader.ReadInt32();
            byte[] compressed = reader.ReadBytes(compressedLength);

            // Decompress
            var bitReader = new GorillaReader(compressed);
            var values = new ulong[valueCount];

            if (valueCount > 0)
            {
                // First value
                values[0] = bitReader.ReadUInt64();

                ulong prevValue = values[0];
                int prevLeadingZeros = 0;
                int prevTrailingZeros = 0;

                for (int i = 1; i < valueCount; i++)
                {
                    int controlBit = bitReader.ReadBit();

                    if (controlBit == 0)
                    {
                        // Value unchanged
                        values[i] = prevValue;
                    }
                    else
                    {
                        int blockInfoBit = bitReader.ReadBit();

                        int leadingZeros, trailingZeros, meaningfulBits;

                        if (blockInfoBit == 0)
                        {
                            // Reuse previous block info
                            leadingZeros = prevLeadingZeros;
                            trailingZeros = prevTrailingZeros;
                            meaningfulBits = 64 - leadingZeros - trailingZeros;
                        }
                        else
                        {
                            // Read new block info
                            leadingZeros = (int)bitReader.ReadBits(6);
                            meaningfulBits = (int)bitReader.ReadBits(6) + 1;
                            trailingZeros = 64 - leadingZeros - meaningfulBits;

                            prevLeadingZeros = leadingZeros;
                            prevTrailingZeros = trailingZeros;
                        }

                        ulong meaningfulValue = bitReader.ReadBits(meaningfulBits);
                        ulong xor = meaningfulValue << trailingZeros;
                        values[i] = prevValue ^ xor;
                    }

                    prevValue = values[i];
                }
            }

            // Convert back to bytes
            var result = new byte[originalLength];
            for (int i = 0; i < valueCount; i++)
            {
                ulong value = values[i];
                for (int j = 0; j < ValueSize; j++)
                {
                    int idx = i * ValueSize + j;
                    if (idx < originalLength)
                        result[idx] = (byte)((value >> (j * 8)) & 0xFF);
                }
            }

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
            return (long)(inputSize * 0.55) + 64;
        }

        #region Bit I/O Helpers

        private class GorillaWriter
        {
            private readonly Stream _stream;
            private byte _currentByte;
            private int _bitIndex;

            public GorillaWriter(Stream stream) => _stream = stream;

            public void WriteBit(int bit)
            {
                if (bit != 0)
                    _currentByte |= (byte)(1 << (7 - _bitIndex));
                if (++_bitIndex == 8)
                {
                    _stream.WriteByte(_currentByte);
                    _currentByte = 0;
                    _bitIndex = 0;
                }
            }

            public void WriteBits(ulong value, int count)
            {
                for (int i = count - 1; i >= 0; i--)
                    WriteBit((int)((value >> i) & 1));
            }

            public void WriteUInt64(ulong value)
            {
                WriteBits(value, 64);
            }

            public void Flush()
            {
                if (_bitIndex > 0)
                    _stream.WriteByte(_currentByte);
            }
        }

        private class GorillaReader
        {
            private readonly byte[] _data;
            private int _byteIndex;
            private int _bitIndex;

            public GorillaReader(byte[] data) => _data = data;

            public int ReadBit()
            {
                if (_byteIndex >= _data.Length)
                    return 0;
                int bit = (_data[_byteIndex] >> (7 - _bitIndex)) & 1;
                if (++_bitIndex == 8)
                {
                    _bitIndex = 0;
                    _byteIndex++;
                }
                return bit;
            }

            public ulong ReadBits(int count)
            {
                ulong value = 0;
                for (int i = 0; i < count; i++)
                    value = (value << 1) | (uint)ReadBit();
                return value;
            }

            public ulong ReadUInt64()
            {
                return ReadBits(64);
            }
        }

        #endregion

        #region Stream Wrappers

        private sealed class BufferedCompressionStream : Stream
        {
            private readonly Stream _output;
            private readonly bool _leaveOpen;
            private readonly TimeSeriesStrategy _strategy;
            private readonly MemoryStream _buffer = new();
            private bool _disposed;

            public BufferedCompressionStream(Stream output, bool leaveOpen, TimeSeriesStrategy strategy)
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
            private readonly TimeSeriesStrategy _strategy;
            private byte[]? _decompressedData;
            private int _position;
            private bool _disposed;

            public BufferedDecompressionStream(Stream input, bool leaveOpen, TimeSeriesStrategy strategy)
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
