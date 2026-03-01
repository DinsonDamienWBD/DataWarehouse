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
    /// Compression strategy implementing Zdelta algorithm.
    /// Uses LZ-based delta compression with rolling hash and variable-length
    /// instruction codes for efficient self-referential compression.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Zdelta, developed by Dmitry V. Levin, combines LZ77-style compression with delta
    /// encoding optimizations. It uses a rolling hash over a sliding window to quickly
    /// identify matching blocks, then encodes copy and literal instructions using
    /// variable-length codes for compactness.
    /// </para>
    /// <para>
    /// This implementation operates in self-delta mode where matches are found within the
    /// data stream itself. The rolling hash (based on Rabin fingerprinting) enables efficient
    /// detection of repeated sequences. Instructions are encoded with minimal overhead using
    /// variable-length integers.
    /// </para>
    /// <para>
    /// Format: [Magic:4][OrigLen:4][HashBits:1][InstructionStream]
    /// Instructions: [OpCode:varint][Length:varint][Offset:varint or Data:bytes]
    /// </para>
    /// </remarks>
    public sealed class ZdeltaStrategy : CompressionStrategyBase
    {
        private const int MaxInputSize = 100 * 1024 * 1024; // 100 MB

        private static readonly byte[] Magic = { 0x5A, 0x44, 0x4C, 0x54 }; // "ZDLT"
        private const int WindowSize = 32768;
        private const int MinMatchLength = 4;
        private const int HashBits = 14;
        private const int HashSize = 1 << HashBits;
        private const int OpLiteral = 0;
        private const int OpCopy = 1;

        /// <summary>
        /// Initializes a new instance of the <see cref="ZdeltaStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public ZdeltaStrategy() : base(CompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "Zdelta",
            TypicalCompressionRatio = 0.47,
            CompressionSpeed = 5,
            DecompressionSpeed = 6,
            CompressionMemoryUsage = 80L * 1024 * 1024,
            DecompressionMemoryUsage = 40L * 1024 * 1024,
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
                        "Zdelta strategy healthy",
                        new Dictionary<string, object>
                        {
                            ["CompressOperations"] = GetCounter("zdelta.compress"),
                            ["DecompressOperations"] = GetCounter("zdelta.decompress")
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
            IncrementCounter("zdelta.compress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for Zdelta");
            using var output = new MemoryStream(input.Length + 256);
            output.Write(Magic, 0, 4);

            var lenBytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lenBytes, input.Length);
            output.Write(lenBytes, 0, 4);
            output.WriteByte(HashBits);

            if (input.Length == 0)
                return output.ToArray();

            // Initialize hash table with lazy-allocated lists.
            // Each bucket is capped at MaxBucketDepth to bound memory: older entries are dropped.
            // P2-1595: Use null-initialized array to avoid 16K heap allocations upfront;
            // buckets are created on first use so sparse inputs only pay for touched slots.
            const int MaxBucketDepth = 8;
            var hashTable = new List<int>?[HashSize];

            int pos = 0;

            while (pos < input.Length)
            {
                // Try to find a match
                int matchPos = -1;
                int matchLen = 0;

                if (pos + MinMatchLength <= input.Length)
                {
                    uint hash = RollingHash(input, pos, MinMatchLength);
                    int hashIdx = (int)(hash & (HashSize - 1));
                    var scanBucket = hashTable[hashIdx];

                    if (scanBucket != null)
                    {
                        foreach (int candPos in scanBucket)
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
                    int opCode = (OpCopy << 1);
                    WriteVarint(output, opCode);
                    WriteVarint(output, matchLen);
                    WriteVarint(output, pos - matchPos);

                    // Add hash entries for the matched region
                    for (int i = 0; i < matchLen; i++)
                    {
                        if (pos + MinMatchLength <= input.Length)
                        {
                            uint h = RollingHash(input, pos, MinMatchLength);
                            int hIdx = (int)(h & (HashSize - 1));
                            var zBucket = hashTable[hIdx] ??= new List<int>(MaxBucketDepth);
                            if (zBucket.Count >= MaxBucketDepth) zBucket.RemoveAt(0);
                            zBucket.Add(pos);
                        }
                        pos++;
                    }
                }
                else
                {
                    // Accumulate literal bytes
                    int litStart = pos;

                    while (pos < input.Length)
                    {
                        // Check if we can find a match
                        bool foundMatch = false;
                        if (pos + MinMatchLength <= input.Length)
                        {
                            uint hash = RollingHash(input, pos, MinMatchLength);
                            int hashIdx = (int)(hash & (HashSize - 1));
                            var scanBkt = hashTable[hashIdx];

                            if (scanBkt != null)
                            {
                                foreach (int candPos in scanBkt)
                                {
                                    if (candPos >= pos) break;
                                    if (pos - candPos > WindowSize) continue;

                                    int len = FindMatchLength(input, candPos, pos);
                                    if (len >= MinMatchLength)
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
                            uint h = RollingHash(input, pos, MinMatchLength);
                            int hIdx = (int)(h & (HashSize - 1));
                            var zBucket = hashTable[hIdx] ??= new List<int>(MaxBucketDepth);
                            if (zBucket.Count >= MaxBucketDepth) zBucket.RemoveAt(0);
                            zBucket.Add(pos);
                        }

                        pos++;

                        // Limit literal length
                        if (pos - litStart >= 16384) break;
                    }

                    int litLen = pos - litStart;
                    int opCode = (OpLiteral << 1) | (litLen > 127 ? 1 : 0);
                    WriteVarint(output, opCode);
                    WriteVarint(output, litLen);
                    output.Write(input, litStart, litLen);
                }
            }

            return output.ToArray();
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            IncrementCounter("zdelta.decompress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for Zdelta");
            using var stream = new MemoryStream(input);

            var magicBuf = new byte[4];
            if (stream.Read(magicBuf, 0, 4) != 4 ||
                magicBuf[0] != Magic[0] || magicBuf[1] != Magic[1] ||
                magicBuf[2] != Magic[2] || magicBuf[3] != Magic[3])
            {
                throw new InvalidDataException("Invalid Zdelta header magic.");
            }

            var lenBuf = new byte[4];
            if (stream.Read(lenBuf, 0, 4) != 4)
                throw new InvalidDataException("Invalid Zdelta header length.");

            int originalLength = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
            if (originalLength < 0)
                throw new InvalidDataException("Invalid original length in Zdelta header.");

            int hashBits = stream.ReadByte();
            if (hashBits < 0)
                throw new InvalidDataException("Invalid hash bits.");

            if (originalLength == 0)
                return Array.Empty<byte>();

            using var output = new MemoryStream(originalLength);

            while (output.Length < originalLength)
            {
                int opCode = ReadVarint(stream);
                int op = opCode >> 1;

                if (op == OpLiteral)
                {
                    int length = ReadVarint(stream);
                    var data = ArrayPool<byte>.Shared.Rent(length);
                    try
                    {
                        if (stream.Read(data, 0, length) != length)
                            throw new InvalidDataException("Unexpected end of literal data.");
                        output.Write(data, 0, length);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(data);
                    }
                }
                else if (op == OpCopy)
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
                else
                {
                    throw new InvalidDataException($"Unknown Zdelta opcode: {op}");
                }
            }

            return output.ToArray();
        }

        /// <summary>
        /// Computes a rolling hash (Rabin fingerprint style) for a block of bytes.
        /// </summary>
        private static uint RollingHash(byte[] data, int offset, int length)
        {
            uint hash = 0;
            const uint prime = 31;

            for (int i = 0; i < length && offset + i < data.Length; i++)
            {
                hash = hash * prime + data[offset + i];
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
        /// Writes a variable-length integer using continuation bits.
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
        /// Reads a variable-length integer using continuation bits.
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
