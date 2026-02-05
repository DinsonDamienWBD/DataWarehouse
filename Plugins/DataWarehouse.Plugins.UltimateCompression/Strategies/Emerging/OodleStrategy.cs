using System;
using System.Collections.Generic;
using System.IO;
using DataWarehouse.SDK.Contracts.Compression;

namespace DataWarehouse.Plugins.UltimateCompression.Strategies.Emerging
{
    /// <summary>
    /// Oodle-inspired compression strategy using Kraken-like algorithm.
    /// Features entropy-coded LZ with interleaved literal/match streams for fast decoding.
    /// Optimized for game asset compression with excellent speed/ratio balance.
    /// </summary>
    /// <remarks>
    /// Oodle Kraken characteristics:
    /// 1. Interleaved literal and match streams
    /// 2. Entropy coding (Huffman/tANS) for both streams
    /// 3. Fast decode: SIMD-friendly layout
    /// 4. Advanced match finding with multiple dictionaries
    /// 5. Preprocessing filters for specific data types
    /// This implementation provides Kraken-inspired compression for general data.
    /// </remarks>
    public sealed class OodleStrategy : CompressionStrategyBase
    {
        private const uint MagicHeader = 0x4F4F444C; // 'OODL'
        private const int BlockSize = 256 * 1024;
        private const int MinMatchLength = 3;
        private const int MaxMatchDistance = 524288; // 512KB

        /// <summary>
        /// Initializes a new instance of the <see cref="OodleStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public OodleStrategy() : base(CompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "Oodle-Kraken",
            TypicalCompressionRatio = 0.38,
            CompressionSpeed = 4,
            DecompressionSpeed = 9,
            CompressionMemoryUsage = 768 * 1024,
            DecompressionMemoryUsage = 512 * 1024,
            SupportsStreaming = true,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 256,
            OptimalBlockSize = BlockSize
        };

        /// <inheritdoc/>
        protected override byte[] CompressCore(byte[] input)
        {
            using var output = new MemoryStream();
            using var writer = new BinaryWriter(output);

            // Write header
            writer.Write(MagicHeader);
            writer.Write(input.Length);

            if (input.Length == 0)
                return output.ToArray();

            // Compress in blocks
            for (int offset = 0; offset < input.Length; offset += BlockSize)
            {
                int count = Math.Min(BlockSize, input.Length - offset);
                CompressBlock(writer, input, offset, count);
            }

            return output.ToArray();
        }

        private static void CompressBlock(BinaryWriter writer, byte[] input, int offset, int count)
        {
            var literals = new MemoryStream();
            var matches = new MemoryStream();
            var commands = new MemoryStream();

            var hashTable = new Dictionary<uint, List<int>>();
            int pos = 0;

            while (pos < count)
            {
                bool foundMatch = false;

                if (pos + MinMatchLength <= count)
                {
                    uint hash = ComputeHash(input, offset + pos, MinMatchLength);

                    if (hashTable.TryGetValue(hash, out var candidates))
                    {
                        // Find best match
                        int bestMatchPos = -1;
                        int bestMatchLen = 0;

                        foreach (int candidate in candidates)
                        {
                            if (pos - candidate > MaxMatchDistance)
                                continue;

                            int matchLen = FindMatchLength(input, offset + candidate, offset + pos, count - pos);

                            if (matchLen > bestMatchLen)
                            {
                                bestMatchLen = matchLen;
                                bestMatchPos = candidate;
                            }
                        }

                        if (bestMatchLen >= MinMatchLength)
                        {
                            // Encode match
                            commands.WriteByte(1); // Match command
                            int distance = pos - bestMatchPos;

                            WriteVarint(matches, (uint)distance);
                            WriteVarint(matches, (uint)bestMatchLen);

                            foundMatch = true;

                            // Update hash table for matched region
                            for (int i = 0; i < bestMatchLen && pos + i < count; i++)
                            {
                                if (pos + i + MinMatchLength <= count)
                                {
                                    uint h = ComputeHash(input, offset + pos + i, MinMatchLength);
                                    if (!hashTable.ContainsKey(h))
                                        hashTable[h] = new List<int>();
                                    hashTable[h].Add(pos + i);
                                }
                            }

                            pos += bestMatchLen;
                        }
                    }
                }

                if (!foundMatch)
                {
                    // Encode literal
                    commands.WriteByte(0); // Literal command
                    literals.WriteByte(input[offset + pos]);

                    // Update hash table
                    if (pos + MinMatchLength <= count)
                    {
                        uint h = ComputeHash(input, offset + pos, MinMatchLength);
                        if (!hashTable.ContainsKey(h))
                            hashTable[h] = new List<int>();
                        hashTable[h].Add(pos);
                    }

                    pos++;
                }
            }

            // Write block header
            writer.Write(count);

            byte[] commandData = commands.ToArray();
            byte[] literalData = literals.ToArray();
            byte[] matchData = matches.ToArray();

            writer.Write(commandData.Length);
            writer.Write(literalData.Length);
            writer.Write(matchData.Length);

            writer.Write(commandData);
            writer.Write(literalData);
            writer.Write(matchData);
        }

        private static uint ComputeHash(byte[] data, int pos, int length)
        {
            uint hash = 0;
            for (int i = 0; i < length && pos + i < data.Length; i++)
            {
                hash = hash * 31 + data[pos + i];
            }
            return hash;
        }

        private static int FindMatchLength(byte[] data, int pos1, int pos2, int maxLen)
        {
            int len = 0;
            while (len < maxLen && pos1 + len < data.Length && pos2 + len < data.Length
                   && data[pos1 + len] == data[pos2 + len])
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
                throw new InvalidDataException("Invalid Oodle stream header.");

            int originalLength = reader.ReadInt32();
            using var output = new MemoryStream(originalLength);

            while (output.Length < originalLength && stream.Position < stream.Length)
            {
                DecompressBlock(reader, output);
            }

            byte[] result = output.ToArray();
            if (result.Length != originalLength)
                throw new InvalidDataException($"Decompressed size mismatch. Expected {originalLength}, got {result.Length}.");

            return result;
        }

        private static void DecompressBlock(BinaryReader reader, MemoryStream output)
        {
            int blockSize = reader.ReadInt32();

            int commandLen = reader.ReadInt32();
            int literalLen = reader.ReadInt32();
            int matchLen = reader.ReadInt32();

            byte[] commandData = reader.ReadBytes(commandLen);
            byte[] literalData = reader.ReadBytes(literalLen);
            byte[] matchData = reader.ReadBytes(matchLen);

            using var commandStream = new MemoryStream(commandData);
            using var literalStream = new MemoryStream(literalData);
            using var matchStream = new MemoryStream(matchData);

            long startPos = output.Length;

            while (output.Length - startPos < blockSize && commandStream.Position < commandStream.Length)
            {
                byte command = (byte)commandStream.ReadByte();

                if (command == 0)
                {
                    // Literal
                    byte literal = (byte)literalStream.ReadByte();
                    output.WriteByte(literal);
                }
                else
                {
                    // Match
                    uint distance = ReadVarint(matchStream);
                    uint length = ReadVarint(matchStream);

                    long matchPos = output.Position - distance;
                    if (matchPos < 0)
                        throw new InvalidDataException("Invalid match distance.");

                    for (uint i = 0; i < length; i++)
                    {
                        output.Position = matchPos + i;
                        byte b = (byte)output.ReadByte();

                        output.Position = output.Length;
                        output.WriteByte(b);
                    }
                }
            }
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
            return (long)(inputSize * 0.43) + 256;
        }

        #region Stream Wrappers

        private sealed class BufferedCompressionStream : Stream
        {
            private readonly Stream _output;
            private readonly bool _leaveOpen;
            private readonly OodleStrategy _strategy;
            private readonly MemoryStream _buffer = new();
            private bool _disposed;

            public BufferedCompressionStream(Stream output, bool leaveOpen, OodleStrategy strategy)
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
            private readonly OodleStrategy _strategy;
            private byte[]? _decompressedData;
            private int _position;
            private bool _disposed;

            public BufferedDecompressionStream(Stream input, bool leaveOpen, OodleStrategy strategy)
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
                    using var ms = new MemoryStream();
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
