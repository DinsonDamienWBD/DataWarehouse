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
    /// Compression strategy implementing VCDIFF (RFC 3284) delta encoding.
    /// Uses copy, add, and run instructions with a sliding window for efficient
    /// delta compression with self-referential matching.
    /// </summary>
    /// <remarks>
    /// <para>
    /// VCDIFF is a standardized delta encoding format (RFC 3284) that encodes differences
    /// between two files using three instruction types: COPY (reference previous data),
    /// ADD (insert literal bytes), and RUN (repeat a byte). It uses a sliding window
    /// over source data to find matches efficiently.
    /// </para>
    /// <para>
    /// This implementation operates in self-delta mode where the source dictionary is
    /// built from the data itself. A rolling hash table enables fast match detection
    /// within the sliding window. The instruction stream is compact, using variable-length
    /// integers for sizes and offsets.
    /// </para>
    /// <para>
    /// Format: [Magic:4][OrigLen:4][WindowSize:4][InstructionStream]
    /// Instructions: [OpCode:1][Size:varint][Offset:varint or Data:bytes]
    /// </para>
    /// </remarks>
    public sealed class VcdiffStrategy : CompressionStrategyBase
    {
        private const int MaxInputSize = 100 * 1024 * 1024; // 100 MB

        private static readonly byte[] Magic = { 0x56, 0x43, 0x44, 0x46 }; // "VCDF"
        private const int WindowSize = 16384;
        private const int MinMatchLength = 4;
        // P2-1596: Limit bucket depth to prevent unbounded memory and O(n) inner scan.
        private const int MaxBucketDepth = 16;
        private const byte OpAdd = 0x01;
        private const byte OpCopy = 0x02;
        private const byte OpRun = 0x03;

        /// <summary>
        /// Initializes a new instance of the <see cref="VcdiffStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public VcdiffStrategy() : base(CompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "VCDIFF",
            TypicalCompressionRatio = 0.48,
            CompressionSpeed = 5,
            DecompressionSpeed = 6,
            CompressionMemoryUsage = 96L * 1024 * 1024,
            DecompressionMemoryUsage = 48L * 1024 * 1024,
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
                        "VCDIFF strategy healthy",
                        new Dictionary<string, object>
                        {
                            ["CompressOperations"] = GetCounter("vcdiff.compress"),
                            ["DecompressOperations"] = GetCounter("vcdiff.decompress")
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
            IncrementCounter("vcdiff.compress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for VCDIFF");
            using var output = new MemoryStream(input.Length + 256);
            output.Write(Magic, 0, 4);

            var lenBytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lenBytes, input.Length);
            output.Write(lenBytes, 0, 4);

            BinaryPrimitives.WriteInt32LittleEndian(lenBytes, WindowSize);
            output.Write(lenBytes, 0, 4);

            if (input.Length == 0)
                return output.ToArray();

            // Build hash table for quick lookups
            var hashTable = new Dictionary<uint, List<int>>();
            int pos = 0;

            while (pos < input.Length)
            {
                // Check for run
                if (pos + 3 < input.Length)
                {
                    int runLen = CountRun(input, pos);
                    if (runLen >= 4)
                    {
                        output.WriteByte(OpRun);
                        WriteVarint(output, runLen);
                        output.WriteByte(input[pos]);
                        pos += runLen;
                        continue;
                    }
                }

                // Try to find a copy match
                int matchPos = -1;
                int matchLen = 0;

                if (pos >= MinMatchLength)
                {
                    uint hash = ComputeHash(input, pos, Math.Min(MinMatchLength, input.Length - pos));
                    if (hashTable.TryGetValue(hash, out var positions))
                    {
                        foreach (int candPos in positions)
                        {
                            if (candPos >= pos) break;
                            if (pos - candPos > WindowSize) continue;

                            int len = FindMatchLength(input, candPos, pos);
                            if (len > matchLen)
                            {
                                matchLen = len;
                                matchPos = candPos;
                            }
                        }
                    }
                }

                if (matchLen >= MinMatchLength)
                {
                    // Emit COPY instruction
                    output.WriteByte(OpCopy);
                    WriteVarint(output, matchLen);
                    WriteVarint(output, pos - matchPos);

                    // Add hashes for matched region
                    for (int i = 0; i < matchLen; i++)
                    {
                        if (pos + MinMatchLength <= input.Length)
                        {
                            uint h = ComputeHash(input, pos, MinMatchLength);
                            if (!hashTable.ContainsKey(h))
                                hashTable[h] = new List<int>(MaxBucketDepth);
                            var bucket = hashTable[h];
                            // P2-1596: Cap bucket depth to bound memory and inner-scan cost.
                            if (bucket.Count < MaxBucketDepth)
                                bucket.Add(pos);
                        }
                        pos++;
                    }
                }
                else
                {
                    // Accumulate ADD bytes
                    int addStart = pos;
                    while (pos < input.Length)
                    {
                        // Check if we find a match or run
                        bool foundMatch = false;

                        if (pos + 3 < input.Length && CountRun(input, pos) >= 4)
                        {
                            foundMatch = true;
                        }
                        else if (pos + MinMatchLength <= input.Length)
                        {
                            uint hash = ComputeHash(input, pos, MinMatchLength);
                            if (hashTable.TryGetValue(hash, out var positions))
                            {
                                foreach (int candPos in positions)
                                {
                                    if (candPos >= pos) break;
                                    if (pos - candPos > WindowSize) continue;
                                    if (FindMatchLength(input, candPos, pos) >= MinMatchLength)
                                    {
                                        foundMatch = true;
                                        break;
                                    }
                                }
                            }
                        }

                        if (foundMatch) break;

                        // Add hash for current position
                        if (pos + MinMatchLength <= input.Length)
                        {
                            uint h = ComputeHash(input, pos, MinMatchLength);
                            if (!hashTable.ContainsKey(h))
                                hashTable[h] = new List<int>(MaxBucketDepth);
                            var bkt = hashTable[h];
                            if (bkt.Count < MaxBucketDepth)
                                bkt.Add(pos);
                        }

                        pos++;
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
            IncrementCounter("vcdiff.decompress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for VCDIFF");
            using var stream = new MemoryStream(input);

            var magicBuf = new byte[4];
            if (stream.Read(magicBuf, 0, 4) != 4 ||
                magicBuf[0] != Magic[0] || magicBuf[1] != Magic[1] ||
                magicBuf[2] != Magic[2] || magicBuf[3] != Magic[3])
            {
                throw new InvalidDataException("Invalid VCDIFF header magic.");
            }

            var lenBuf = new byte[4];
            if (stream.Read(lenBuf, 0, 4) != 4)
                throw new InvalidDataException("Invalid VCDIFF header length.");

            int originalLength = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
            if (originalLength < 0)
                throw new InvalidDataException("Invalid original length in VCDIFF header.");

            if (stream.Read(lenBuf, 0, 4) != 4)
                throw new InvalidDataException("Invalid window size.");

            if (originalLength == 0)
                return Array.Empty<byte>();

            using var output = new MemoryStream(originalLength);

            while (output.Length < originalLength)
            {
                int opCode = stream.ReadByte();
                if (opCode < 0)
                    throw new InvalidDataException("Unexpected end of VCDIFF data.");

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

                    // Copy from earlier position (may overlap)
                    for (int i = 0; i < length; i++)
                    {
                        long savedPos = output.Position;
                        output.Position = copyPos + i;
                        int b = output.ReadByte();
                        if (b < 0)
                            throw new InvalidDataException("Invalid COPY source.");
                        output.Position = savedPos;
                        output.WriteByte((byte)b);
                    }
                }
                else if (opCode == OpRun)
                {
                    int length = ReadVarint(stream);
                    int value = stream.ReadByte();
                    if (value < 0)
                        throw new InvalidDataException("Unexpected end of RUN data.");

                    for (int i = 0; i < length; i++)
                        output.WriteByte((byte)value);
                }
                else
                {
                    throw new InvalidDataException($"Unknown VCDIFF opcode: {opCode}");
                }
            }

            return output.ToArray();
        }

        /// <summary>
        /// Counts the length of a run starting at the given position.
        /// </summary>
        private static int CountRun(byte[] data, int pos)
        {
            if (pos >= data.Length) return 0;

            byte value = data[pos];
            int len = 1;
            while (pos + len < data.Length && data[pos + len] == value)
            {
                len++;
            }
            return len;
        }

        /// <summary>
        /// Computes a simple hash for a block of bytes.
        /// </summary>
        private static uint ComputeHash(byte[] data, int offset, int length)
        {
            uint hash = 2166136261u;
            for (int i = 0; i < length; i++)
            {
                hash ^= data[offset + i];
                hash *= 16777619u;
            }
            return hash;
        }

        /// <summary>
        /// Finds the length of matching bytes between two positions.
        /// </summary>
        private static int FindMatchLength(byte[] data, int pos1, int pos2)
        {
            int len = 0;
            while (pos1 + len < pos2 && pos2 + len < data.Length &&
                   data[pos1 + len] == data[pos2 + len])
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
            return (long)(inputSize * 0.52) + 256;
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
