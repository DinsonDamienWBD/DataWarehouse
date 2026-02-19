using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using DataWarehouse.SDK.Contracts.Compression;

using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
namespace DataWarehouse.Plugins.UltimateCompression.Strategies.Delta
{
    /// <summary>
    /// Compression strategy implementing Xdelta3-style delta compression.
    /// Uses a rolling Adler32 hash with a sliding window to find matching blocks,
    /// encoding the data as copy and add instructions similar to VCDIFF.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Xdelta is a binary delta compression tool originally designed for creating patches
    /// between file versions. This implementation uses it in "self-delta" mode where the
    /// source dictionary is built from the data itself using a sliding window. The algorithm
    /// uses rolling hash (Adler32) for fast match detection.
    /// </para>
    /// <para>
    /// The encoder emits two types of instructions: COPY (reference to earlier data) and
    /// ADD (literal new data). A hash table maps hash values to positions, enabling quick
    /// lookup of potential matches. This approach is effective for data with repeated
    /// patterns or blocks.
    /// </para>
    /// <para>
    /// Format: [Magic:4][OrigLen:4][InstructionStream]
    /// Instructions: [OpCode:1][Length:varint][Data/Offset:variable]
    /// </para>
    /// </remarks>
    public sealed class XdeltaStrategy : CompressionStrategyBase
    {
        private const int MaxInputSize = 100 * 1024 * 1024; // 100 MB

        private static readonly byte[] Magic = { 0x58, 0x44, 0x4C, 0x54 }; // "XDLT"
        private const int WindowSize = 8192;
        private const int BlockSize = 32;
        private const byte OpAdd = 0x01;
        private const byte OpCopy = 0x02;

        /// <summary>
        /// Initializes a new instance of the <see cref="XdeltaStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public XdeltaStrategy() : base(CompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "Xdelta",
            TypicalCompressionRatio = 0.45,
            CompressionSpeed = 6,
            DecompressionSpeed = 7,
            CompressionMemoryUsage = 64L * 1024 * 1024,
            DecompressionMemoryUsage = 32L * 1024 * 1024,
            SupportsStreaming = false,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 128,
            OptimalBlockSize = 1024 * 1024
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
                        "Xdelta strategy healthy",
                        new Dictionary<string, object>
                        {
                            ["CompressOperations"] = GetCounter("xdelta.compress"),
                            ["DecompressOperations"] = GetCounter("xdelta.decompress")
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
            IncrementCounter("xdelta.compress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for Xdelta");
            using var output = new MemoryStream(input.Length + 256);
            output.Write(Magic, 0, 4);

            var lenBytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lenBytes, input.Length);
            output.Write(lenBytes, 0, 4);

            if (input.Length == 0)
                return output.ToArray();

            // Build hash table for sliding window
            var hashTable = new Dictionary<uint, List<int>>();
            int pos = 0;

            while (pos < input.Length)
            {
                // Try to find a match in the window
                int matchPos = -1;
                int matchLen = 0;

                if (pos >= BlockSize)
                {
                    uint hash = ComputeHash(input, pos, Math.Min(BlockSize, input.Length - pos));
                    if (hashTable.TryGetValue(hash, out var positions))
                    {
                        // Find longest match
                        foreach (int candPos in positions)
                        {
                            if (candPos >= pos) break; // Only look backward
                            if (pos - candPos > WindowSize) continue; // Outside window

                            int len = FindMatchLength(input, candPos, pos, input.Length - pos);
                            if (len > matchLen)
                            {
                                matchLen = len;
                                matchPos = candPos;
                            }
                        }
                    }
                }

                if (matchLen >= 4)
                {
                    // Emit COPY instruction
                    output.WriteByte(OpCopy);
                    WriteVarint(output, matchLen);
                    WriteVarint(output, pos - matchPos);

                    // Add hashes for this range
                    for (int i = 0; i < matchLen && pos < input.Length; i++)
                    {
                        if (pos + BlockSize <= input.Length)
                        {
                            uint h = ComputeHash(input, pos, BlockSize);
                            if (!hashTable.ContainsKey(h))
                                hashTable[h] = new List<int>();
                            hashTable[h].Add(pos);
                        }
                        pos++;
                    }
                }
                else
                {
                    // Emit ADD instruction - accumulate literals
                    int addStart = pos;
                    while (pos < input.Length)
                    {
                        // Check if we can find a match
                        bool foundMatch = false;
                        if (pos >= BlockSize && pos + BlockSize <= input.Length)
                        {
                            uint hash = ComputeHash(input, pos, BlockSize);
                            if (hashTable.TryGetValue(hash, out var positions))
                            {
                                foreach (int candPos in positions)
                                {
                                    if (candPos >= pos) break;
                                    if (pos - candPos > WindowSize) continue;

                                    int len = FindMatchLength(input, candPos, pos, input.Length - pos);
                                    if (len >= 4)
                                    {
                                        foundMatch = true;
                                        break;
                                    }
                                }
                            }
                        }

                        if (foundMatch) break;

                        // Add hash for current position
                        if (pos + BlockSize <= input.Length)
                        {
                            uint h = ComputeHash(input, pos, BlockSize);
                            if (!hashTable.ContainsKey(h))
                                hashTable[h] = new List<int>();
                            hashTable[h].Add(pos);
                        }

                        pos++;

                        // Limit ADD length
                        if (pos - addStart >= 255) break;
                    }

                    int addLen = pos - addStart;
                    output.WriteByte(OpAdd);
                    WriteVarint(output, addLen);
                    output.Write(input, addStart, addLen);
                }
            }

            return output.ToArray();
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            IncrementCounter("xdelta.decompress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for Xdelta");
            using var stream = new MemoryStream(input);

            var magicBuf = new byte[4];
            if (stream.Read(magicBuf, 0, 4) != 4 ||
                magicBuf[0] != Magic[0] || magicBuf[1] != Magic[1] ||
                magicBuf[2] != Magic[2] || magicBuf[3] != Magic[3])
            {
                throw new InvalidDataException("Invalid Xdelta header magic.");
            }

            var lenBuf = new byte[4];
            if (stream.Read(lenBuf, 0, 4) != 4)
                throw new InvalidDataException("Invalid Xdelta header length.");

            int originalLength = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
            if (originalLength < 0)
                throw new InvalidDataException("Invalid original length in Xdelta header.");

            if (originalLength == 0)
                return Array.Empty<byte>();

            using var output = new MemoryStream(originalLength);

            while (output.Length < originalLength)
            {
                int opCode = stream.ReadByte();
                if (opCode < 0)
                    throw new InvalidDataException("Unexpected end of Xdelta data.");

                if (opCode == OpAdd)
                {
                    int length = ReadVarint(stream);
                    var data = ArrayPool<byte>.Shared.Rent(length);
                    try
                    {
                        if (stream.Read(data, 0, length) != length)
                            throw new InvalidDataException("Unexpected end of ADD data.");
                        output.Write(data, 0, length);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(data);
                    }
                }
                else if (opCode == OpCopy)
                {
                    int length = ReadVarint(stream);
                    int offset = ReadVarint(stream);

                    long copyPos = output.Position - offset;
                    if (copyPos < 0)
                        throw new InvalidDataException("Invalid COPY offset.");

                    // Copy from earlier position
                    for (int i = 0; i < length; i++)
                    {
                        long savedPos = output.Position;
                        output.Position = copyPos + i;
                        int b = output.ReadByte();
                        output.Position = savedPos;
                        output.WriteByte((byte)b);
                    }
                }
                else
                {
                    throw new InvalidDataException($"Unknown opcode: {opCode}");
                }
            }

            return output.ToArray();
        }

        /// <summary>
        /// Computes a simple rolling hash (Adler32-style) for a block.
        /// </summary>
        private static uint ComputeHash(byte[] data, int offset, int length)
        {
            uint a = 1, b = 0;
            for (int i = 0; i < length; i++)
            {
                a = (a + data[offset + i]) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }

        /// <summary>
        /// Finds the length of matching bytes between two positions.
        /// </summary>
        private static int FindMatchLength(byte[] data, int pos1, int pos2, int maxLen)
        {
            int len = 0;
            while (len < maxLen && pos1 + len < pos2 && data[pos1 + len] == data[pos2 + len])
            {
                len++;
            }
            return len;
        }

        /// <summary>
        /// Writes a variable-length integer.
        /// </summary>
        private static void WriteVarint(Stream stream, int value)
        {
            while (value >= 128)
            {
                stream.WriteByte((byte)((value & 0x7F) | 0x80));
                value >>= 7;
            }
            stream.WriteByte((byte)value);
        }

        /// <summary>
        /// Reads a variable-length integer.
        /// </summary>
        private static int ReadVarint(Stream stream)
        {
            int result = 0;
            int shift = 0;

            while (true)
            {
                int b = stream.ReadByte();
                if (b < 0)
                    throw new InvalidDataException("Unexpected end while reading varint.");

                result |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                    break;

                shift += 7;
            }

            return result;
        }

        /// <inheritdoc/>
        protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
        {
            return new BufferedTransformStream(output, leaveOpen, CompressCore, true);
        }

        /// <inheritdoc/>
        protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
        {
            return new BufferedTransformStream(input, leaveOpen, DecompressCore, false);
        }

        /// <inheritdoc/>
        public override long EstimateCompressedSize(long inputSize)
        {
            return (long)(inputSize * 0.50) + 256;
        }

        #region Buffered Stream Wrapper

        private sealed class BufferedTransformStream : Stream
        {
            private readonly Stream _inner;
            private readonly bool _leaveOpen;
            private readonly Func<byte[], byte[]> _transform;
            private readonly bool _isCompression;
            private readonly MemoryStream _buffer;
            private bool _disposed;

            public BufferedTransformStream(Stream inner, bool leaveOpen,
                Func<byte[], byte[]> transform, bool isCompression)
            {
                _inner = inner;
                _leaveOpen = leaveOpen;
                _transform = transform;
                _isCompression = isCompression;

                if (isCompression)
                    _buffer = new MemoryStream(4096);
                else
                {
                    using var temp = new MemoryStream(4096);
                    inner.CopyTo(temp);
                    var compressed = temp.ToArray();
                    var decompressed = compressed.Length > 0 ? transform(compressed) : Array.Empty<byte>();
                    _buffer = new MemoryStream(decompressed);
                }
            }

            public override bool CanRead => !_isCompression;
            public override bool CanSeek => false;
            public override bool CanWrite => _isCompression;
            public override long Length => _buffer.Length;
            public override long Position
            {
                get => _buffer.Position;
                set => throw new NotSupportedException();
            }
            public override int Read(byte[] buffer, int offset, int count) =>
                _isCompression ? throw new NotSupportedException() : _buffer.Read(buffer, offset, count);
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (!_isCompression) throw new NotSupportedException();
                _buffer.Write(buffer, offset, count);
            }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (!_disposed && disposing)
                {
                    _disposed = true;
                    if (_isCompression)
                    {
                        var data = _buffer.ToArray();
                        if (data.Length > 0)
                        {
                            var compressed = _transform(data);
                            _inner.Write(compressed, 0, compressed.Length);
                        }
                    }
                    _buffer.Dispose();
                    if (!_leaveOpen) _inner.Dispose();
                }
                base.Dispose(disposing);
            }
        }

        #endregion
    }
}
