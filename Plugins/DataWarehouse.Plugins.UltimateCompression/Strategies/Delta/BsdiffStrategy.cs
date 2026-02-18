using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using DataWarehouse.SDK.Contracts.Compression;

namespace DataWarehouse.Plugins.UltimateCompression.Strategies.Delta
{
    /// <summary>
    /// Compression strategy implementing Bsdiff binary diff algorithm.
    /// Uses suffix array construction for efficient match finding and encodes
    /// diff and extra blocks for optimal binary patching.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bsdiff, developed by Colin Percival (2003), is a binary diff tool that creates highly
    /// efficient patches between two binary files. It uses a suffix array to find the longest
    /// common substrings between the old and new files, then encodes the differences as a
    /// series of diff blocks (bytewise differences) and extra blocks (new data).
    /// </para>
    /// <para>
    /// This implementation operates in "standalone" mode where the source is the data itself
    /// (self-delta). The algorithm builds a suffix array to enable fast substring matching,
    /// then outputs control tuples [add,copy,seek] along with diff and extra data blocks.
    /// </para>
    /// <para>
    /// Format: [Magic:4][OrigLen:4][ControlLen:4][DiffLen:4][ControlBlock][DiffBlock][ExtraBlock]
    /// </para>
    /// </remarks>
    public sealed class BsdiffStrategy : CompressionStrategyBase
    {
        private static readonly byte[] Magic = { 0x42, 0x53, 0x44, 0x46 }; // "BSDF"
        private const int MinMatchLength = 8;

        /// <summary>
        /// Initializes a new instance of the <see cref="BsdiffStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public BsdiffStrategy() : base(CompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "Bsdiff",
            TypicalCompressionRatio = 0.40,
            CompressionSpeed = 2,
            DecompressionSpeed = 5,
            CompressionMemoryUsage = 512L * 1024 * 1024,
            DecompressionMemoryUsage = 64L * 1024 * 1024,
            SupportsStreaming = false,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 256,
            OptimalBlockSize = 2 * 1024 * 1024
        };

        /// <inheritdoc/>
        protected override byte[] CompressCore(byte[] input)
        {
            if (input.Length == 0)
                return CreateEmptyBlock();

            // Build suffix array for the input (treating it as "old" data)
            var suffixArray = BuildSuffixArray(input);

            using var controlStream = new MemoryStream(1024);
            using var diffStream = new MemoryStream(4096);
            using var extraStream = new MemoryStream(4096);

            int scan = 0;
            int lastMatch = 0;

            while (scan < input.Length)
            {
                // Find best match using suffix array
                int matchPos = -1;
                int matchLen = 0;

                if (scan >= MinMatchLength)
                {
                    (matchPos, matchLen) = FindMatch(input, input, suffixArray, scan, lastMatch);
                }

                if (matchLen >= MinMatchLength)
                {
                    // Compute diff block
                    int diffLen = matchLen;
                    for (int i = 0; i < diffLen && matchPos + i < scan; i++)
                    {
                        byte diff = (byte)(input[scan + i] - input[matchPos + i]);
                        diffStream.WriteByte(diff);
                    }

                    // Look for extra bytes after the match
                    int extraStart = scan + matchLen;
                    int extraLen = 0;
                    while (extraStart + extraLen < input.Length && extraLen < 128)
                    {
                        // Check if we find another match
                        if (extraStart + extraLen + MinMatchLength < input.Length)
                        {
                            var (nextPos, nextLen) = FindMatch(input, input, suffixArray,
                                extraStart + extraLen, matchPos);
                            if (nextLen >= MinMatchLength)
                                break;
                        }
                        extraLen++;
                    }

                    // Write extra bytes
                    for (int i = 0; i < extraLen && extraStart + i < input.Length; i++)
                    {
                        extraStream.WriteByte(input[extraStart + i]);
                    }

                    // Write control tuple [diffLen, extraLen, seekOffset]
                    WriteInt32(controlStream, diffLen);
                    WriteInt32(controlStream, extraLen);
                    WriteInt32(controlStream, (matchPos + matchLen) - (lastMatch + matchLen));

                    scan = extraStart + extraLen;
                    lastMatch = matchPos;
                }
                else
                {
                    // No match - emit as extra
                    extraStream.WriteByte(input[scan]);
                    WriteInt32(controlStream, 0);
                    WriteInt32(controlStream, 1);
                    WriteInt32(controlStream, 0);
                    scan++;
                }
            }

            // Build final output
            using var output = new MemoryStream(input.Length + 256);
            output.Write(Magic, 0, 4);

            var lenBytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lenBytes, input.Length);
            output.Write(lenBytes, 0, 4);

            var controlData = controlStream.ToArray();
            var diffData = diffStream.ToArray();

            BinaryPrimitives.WriteInt32LittleEndian(lenBytes, controlData.Length);
            output.Write(lenBytes, 0, 4);

            BinaryPrimitives.WriteInt32LittleEndian(lenBytes, diffData.Length);
            output.Write(lenBytes, 0, 4);

            output.Write(controlData, 0, controlData.Length);
            output.Write(diffData, 0, diffData.Length);
            output.Write(extraStream.ToArray(), 0, (int)extraStream.Length);

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
                throw new InvalidDataException("Invalid Bsdiff header magic.");
            }

            var lenBuf = new byte[4];
            if (stream.Read(lenBuf, 0, 4) != 4)
                throw new InvalidDataException("Invalid Bsdiff header length.");

            int originalLength = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
            if (originalLength < 0)
                throw new InvalidDataException("Invalid original length in Bsdiff header.");

            if (originalLength == 0)
                return Array.Empty<byte>();

            if (stream.Read(lenBuf, 0, 4) != 4)
                throw new InvalidDataException("Invalid control length.");
            int controlLen = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);

            if (stream.Read(lenBuf, 0, 4) != 4)
                throw new InvalidDataException("Invalid diff length.");
            int diffLen = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);

            var controlData = new byte[controlLen];
            var diffData = new byte[diffLen];
            var extraData = new byte[stream.Length - stream.Position - controlLen - diffLen];

            if (stream.Read(controlData, 0, controlLen) != controlLen)
                throw new InvalidDataException("Invalid control data.");
            if (stream.Read(diffData, 0, diffLen) != diffLen)
                throw new InvalidDataException("Invalid diff data.");
            if (stream.Read(extraData, 0, extraData.Length) != extraData.Length)
                throw new InvalidDataException("Invalid extra data.");

            // Reconstruct
            var result = new byte[originalLength];
            int newPos = 0;
            int oldPos = 0;
            int diffPos = 0;
            int extraPos = 0;

            using var controlReader = new MemoryStream(controlData);

            while (newPos < originalLength)
            {
                if (controlReader.Position >= controlLen)
                    break;

                int diffBytes = ReadInt32(controlReader);
                int extraBytes = ReadInt32(controlReader);
                int seekOffset = ReadInt32(controlReader);

                // Apply diff
                for (int i = 0; i < diffBytes && newPos < originalLength; i++)
                {
                    if (diffPos >= diffData.Length)
                        throw new InvalidDataException("Diff data exhausted.");

                    byte oldByte = (oldPos + i < result.Length) ? result[oldPos + i] : (byte)0;
                    result[newPos++] = (byte)(oldByte + diffData[diffPos++]);
                }

                // Copy extra
                for (int i = 0; i < extraBytes && newPos < originalLength; i++)
                {
                    if (extraPos >= extraData.Length)
                        throw new InvalidDataException("Extra data exhausted.");
                    result[newPos++] = extraData[extraPos++];
                }

                oldPos += seekOffset + diffBytes;
            }

            return result;
        }

        /// <summary>
        /// Builds a suffix array for fast substring searching.
        /// Simplified implementation using sorting.
        /// </summary>
        private static int[] BuildSuffixArray(byte[] data)
        {
            if (data.Length == 0)
                return Array.Empty<int>();

            var suffixes = Enumerable.Range(0, data.Length).ToArray();

            Array.Sort(suffixes, (a, b) =>
            {
                int len = Math.Min(data.Length - a, data.Length - b);
                for (int i = 0; i < len; i++)
                {
                    int cmp = data[a + i].CompareTo(data[b + i]);
                    if (cmp != 0) return cmp;
                }
                return (data.Length - a).CompareTo(data.Length - b);
            });

            return suffixes;
        }

        /// <summary>
        /// Finds the best match for a position using the suffix array.
        /// </summary>
        private static (int pos, int len) FindMatch(byte[] oldData, byte[] newData,
            int[] suffixArray, int newPos, int lastMatch)
        {
            if (newPos + MinMatchLength > newData.Length)
                return (-1, 0);

            int bestPos = -1;
            int bestLen = 0;

            // Simple linear search through suffix array (could be binary search + scan)
            for (int i = 0; i < suffixArray.Length; i++)
            {
                int oldPos = suffixArray[i];
                if (oldPos >= newPos) continue; // Only look backward in self-delta

                int len = 0;
                while (oldPos + len < oldData.Length && newPos + len < newData.Length &&
                       oldData[oldPos + len] == newData[newPos + len])
                {
                    len++;
                }

                if (len > bestLen)
                {
                    bestLen = len;
                    bestPos = oldPos;
                }

                // Early exit if we found a very good match
                if (bestLen > 128) break;
            }

            return (bestPos, bestLen);
        }

        private static void WriteInt32(Stream stream, int value)
        {
            var buf = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(buf, value);
            stream.Write(buf, 0, 4);
        }

        private static int ReadInt32(Stream stream)
        {
            var buf = new byte[4];
            if (stream.Read(buf, 0, 4) != 4)
                throw new InvalidDataException("Unexpected end while reading int32.");
            return BinaryPrimitives.ReadInt32LittleEndian(buf);
        }

        private static byte[] CreateEmptyBlock()
        {
            using var ms = new MemoryStream(4096);
            ms.Write(Magic, 0, 4);
            ms.Write(new byte[4], 0, 4); // length = 0
            ms.Write(new byte[4], 0, 4); // control length = 0
            ms.Write(new byte[4], 0, 4); // diff length = 0
            return ms.ToArray();
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
            return (long)(inputSize * 0.45) + 512;
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
