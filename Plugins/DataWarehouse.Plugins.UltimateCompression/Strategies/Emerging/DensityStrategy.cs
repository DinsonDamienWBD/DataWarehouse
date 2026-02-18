using System;
using System.Collections.Generic;
using System.IO;
using DataWarehouse.SDK.Contracts.Compression;
using SdkCompressionLevel = DataWarehouse.SDK.Contracts.Compression.CompressionLevel;

namespace DataWarehouse.Plugins.UltimateCompression.Strategies.Emerging
{
    /// <summary>
    /// Density compression strategy with three compression modes: Chameleon, Cheetah, and Lion.
    /// Uses dictionary-based hash-chain matching with variable output encoding.
    /// Optimized for speed while maintaining competitive compression ratios.
    /// </summary>
    /// <remarks>
    /// Density algorithm features:
    /// 1. Chameleon: Fast compression with 16KB dictionary
    /// 2. Cheetah: Balanced mode with 64KB dictionary
    /// 3. Lion: Maximum compression with 256KB dictionary
    /// 4. Hash-chain matching for efficient pattern detection
    /// 5. Variable-length encoding for literals and matches
    /// This implementation provides high-speed compression for real-time applications.
    /// </remarks>
    public sealed class DensityStrategy : CompressionStrategyBase
    {
        private const uint MagicHeader = 0x44454E53; // 'DENS'
        private const int ChameleonDictSize = 16 * 1024;
        private const int CheetahDictSize = 64 * 1024;
        private const int LionDictSize = 256 * 1024;
        private const int MinMatchLength = 4;
        private const int MaxMatchLength = 256;

        private enum DensityMode : byte
        {
            Chameleon = 0,
            Cheetah = 1,
            Lion = 2
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DensityStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public DensityStrategy() : base(SdkCompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "Density",
            TypicalCompressionRatio = 0.50,
            CompressionSpeed = 8,
            DecompressionSpeed = 9,
            CompressionMemoryUsage = 384 * 1024,
            DecompressionMemoryUsage = 256 * 1024,
            SupportsStreaming = true,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 64,
            OptimalBlockSize = 8192
        };

        /// <inheritdoc/>
        protected override byte[] CompressCore(byte[] input)
        {
            using var output = new MemoryStream(input.Length + 256);
            using var writer = new BinaryWriter(output);

            // Write header
            writer.Write(MagicHeader);
            writer.Write(input.Length);

            // Select mode based on compression level
            DensityMode mode = Level switch
            {
                SdkCompressionLevel.Fastest => DensityMode.Chameleon,
                SdkCompressionLevel.Default => DensityMode.Lion,
                _ => DensityMode.Cheetah
            };

            writer.Write((byte)mode);

            // Compress using selected mode
            byte[] compressed = CompressWithMode(input, mode);
            writer.Write(compressed.Length);
            writer.Write(compressed);

            return output.ToArray();
        }

        private static byte[] CompressWithMode(byte[] input, DensityMode mode)
        {
            int dictSize = mode switch
            {
                DensityMode.Chameleon => ChameleonDictSize,
                DensityMode.Lion => LionDictSize,
                _ => CheetahDictSize
            };

            using var output = new MemoryStream(input.Length + 256);
            var hashTable = new Dictionary<uint, int>(dictSize / 4);
            int pos = 0;

            while (pos < input.Length)
            {
                // Try to find match
                if (pos + MinMatchLength <= input.Length)
                {
                    uint hash = ComputeHash(input, pos);
                    int matchPos = -1;
                    int matchLen = 0;

                    if (hashTable.TryGetValue(hash, out int candidatePos))
                    {
                        if (pos - candidatePos < dictSize)
                        {
                            matchLen = FindMatchLength(input, candidatePos, pos, MaxMatchLength);
                        }
                    }

                    if (matchLen >= MinMatchLength)
                    {
                        // Encode match: flag (1) + offset + length
                        output.WriteByte(1); // Match flag
                        int offset = pos - candidatePos;
                        WriteVarint(output, (uint)offset);
                        WriteVarint(output, (uint)matchLen);

                        // Update hash table for matched positions
                        for (int i = 0; i < matchLen && pos < input.Length; i++, pos++)
                        {
                            if (pos + MinMatchLength <= input.Length)
                            {
                                hashTable[ComputeHash(input, pos)] = pos;
                            }
                        }
                    }
                    else
                    {
                        // Encode literal: flag (0) + byte
                        output.WriteByte(0); // Literal flag
                        output.WriteByte(input[pos]);

                        hashTable[hash] = pos;
                        pos++;
                    }
                }
                else
                {
                    // Not enough bytes for match - encode as literal
                    output.WriteByte(0);
                    output.WriteByte(input[pos]);
                    pos++;
                }
            }

            return output.ToArray();
        }

        private static uint ComputeHash(byte[] data, int pos)
        {
            if (pos + 4 > data.Length)
                return 0;

            uint hash = data[pos];
            hash = (hash << 8) | data[pos + 1];
            hash = (hash << 8) | data[pos + 2];
            hash = (hash << 8) | data[pos + 3];

            // Simple hash mixing
            hash ^= hash >> 16;
            hash *= 0x85ebca6b;
            hash ^= hash >> 13;

            return hash;
        }

        private static int FindMatchLength(byte[] data, int pos1, int pos2, int maxLen)
        {
            int len = 0;
            while (len < maxLen && pos1 + len < pos2 && pos2 + len < data.Length
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
                    throw new InvalidDataException("Unexpected end of stream while reading varint.");

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
                throw new InvalidDataException("Invalid Density stream header.");

            int originalLength = reader.ReadInt32();
            DensityMode mode = (DensityMode)reader.ReadByte();
            int compressedLength = reader.ReadInt32();

            // Decompress
            using var output = new MemoryStream(originalLength);

            while (output.Length < originalLength && stream.Position < stream.Length)
            {
                byte flag = reader.ReadByte();

                if (flag == 0)
                {
                    // Literal
                    byte literal = reader.ReadByte();
                    output.WriteByte(literal);
                }
                else
                {
                    // Match
                    uint offset = ReadVarint(stream);
                    uint length = ReadVarint(stream);

                    long matchPos = output.Position - offset;
                    if (matchPos < 0)
                        throw new InvalidDataException("Invalid match offset.");

                    // Copy match
                    for (uint i = 0; i < length; i++)
                    {
                        long readPos = matchPos + i;
                        if (readPos >= output.Length)
                            throw new InvalidDataException("Invalid match position.");

                        output.Position = readPos;
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
            return (long)(inputSize * 0.55) + 128;
        }

        #region Stream Wrappers

        private sealed class BufferedCompressionStream : Stream
        {
            private readonly Stream _output;
            private readonly bool _leaveOpen;
            private readonly DensityStrategy _strategy;
            private readonly MemoryStream _buffer = new();
            private bool _disposed;

            public BufferedCompressionStream(Stream output, bool leaveOpen, DensityStrategy strategy)
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
            private readonly DensityStrategy _strategy;
            private byte[]? _decompressedData;
            private int _position;
            private bool _disposed;

            public BufferedDecompressionStream(Stream input, bool leaveOpen, DensityStrategy strategy)
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
