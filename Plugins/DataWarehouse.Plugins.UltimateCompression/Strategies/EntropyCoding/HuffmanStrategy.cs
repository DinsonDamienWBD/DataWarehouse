using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Compression;

namespace DataWarehouse.Plugins.UltimateCompression.Strategies.EntropyCoding
{
    /// <summary>
    /// Compression strategy implementing Canonical Huffman encoding.
    /// Builds a frequency table, constructs an optimal prefix-free code tree, and stores
    /// only the code lengths in the header for compact representation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Huffman coding (1952) assigns variable-length codes to symbols based on their frequencies,
    /// with more frequent symbols receiving shorter codes. This achieves optimal prefix-free
    /// compression for symbol-by-symbol encoding with known statistics.
    /// </para>
    /// <para>
    /// Canonical Huffman encoding standardizes the code assignment: codes of the same length
    /// are assigned in symbol order, allowing reconstruction of the entire codebook from just
    /// the code lengths. This reduces header size from O(n log n) to O(n) bits.
    /// </para>
    /// <para>
    /// Format: [Magic:4][OrigLen:4][CodeLengths:256*1][PaddingBits:1][CompressedData]
    /// </para>
    /// </remarks>
    public sealed class HuffmanStrategy : CompressionStrategyBase
    {
        private static readonly byte[] Magic = { 0x48, 0x55, 0x46, 0x46 }; // "HUFF"
        private const int MaxCodeLength = 15;
        private const int MaxInputSize = 100 * 1024 * 1024; // 100MB limit

        private int _maxTreeDepth = 15;
        private int _maxSymbolCount = 256;

        /// <summary>
        /// Initializes a new instance of the <see cref="HuffmanStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public HuffmanStrategy() : base(CompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "Huffman",
            TypicalCompressionRatio = 0.60,
            CompressionSpeed = 7,
            DecompressionSpeed = 8,
            CompressionMemoryUsage = 16L * 1024 * 1024,
            DecompressionMemoryUsage = 8L * 1024 * 1024,
            SupportsStreaming = false,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 32,
            OptimalBlockSize = 512 * 1024
        };

        /// <inheritdoc/>
        protected override async Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            // Validate configuration parameters
            if (_maxTreeDepth < 8 || _maxTreeDepth > 32)
                throw new ArgumentException($"Max tree depth must be between 8 and 32. Got: {_maxTreeDepth}");

            if (_maxSymbolCount < 2 || _maxSymbolCount > 65536)
                throw new ArgumentException($"Max symbol count must be between 2 and 65536. Got: {_maxSymbolCount}");

            await base.InitializeAsyncCore(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            // No resources to clean up for Huffman
            await base.ShutdownAsyncCore(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        protected override async ValueTask DisposeAsyncCore()
        {
            // Clean disposal pattern
            await base.DisposeAsyncCore().ConfigureAwait(false);
        }

        /// <summary>
        /// Performs health check by verifying round-trip compression with test data.
        /// </summary>
        public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default)
        {
            return await GetCachedHealthAsync(async (cancellationToken) =>
            {
                try
                {
                    // Round-trip test with known data
                    var testData = new byte[] { 1, 1, 1, 2, 2, 3 };
                    var compressed = CompressCore(testData);
                    var decompressed = DecompressCore(compressed);

                    if (testData.Length != decompressed.Length)
                        return new StrategyHealthCheckResult(false, "Round-trip length mismatch");

                    for (int i = 0; i < testData.Length; i++)
                    {
                        if (testData[i] != decompressed[i])
                            return new StrategyHealthCheckResult(false, "Round-trip data mismatch");
                    }

                    return new StrategyHealthCheckResult(true);
                }
                catch (Exception ex)
                {
                    return new StrategyHealthCheckResult(false, $"Health check failed: {ex.Message}");
                }
            }, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        protected override byte[] CompressCore(byte[] input)
        {
            IncrementCounter("huffman.compress");

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input size exceeds maximum of {MaxInputSize} bytes");

            if (input.Length == 0)
                return CreateEmptyCompressedBlock();

            // Build frequency table
            var frequencies = new int[256];
            foreach (byte b in input)
                frequencies[b]++;

            // Build Huffman tree and get code lengths
            var codeLengths = BuildCanonicalCodeLengths(frequencies);

            // Build canonical codes
            var codes = BuildCanonicalCodes(codeLengths);

            // Encode data
            using var output = new MemoryStream(input.Length + 256);
            output.Write(Magic, 0, 4);

            var lenBytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lenBytes, input.Length);
            output.Write(lenBytes, 0, 4);

            // Write code lengths (256 bytes)
            output.Write(codeLengths, 0, 256);

            // Encode data into bit stream
            var bitWriter = new BitWriter(output);
            foreach (byte symbol in input)
            {
                var (code, length) = codes[symbol];
                bitWriter.WriteBits(code, length);
            }

            byte paddingBits = bitWriter.Flush();
            output.WriteByte(paddingBits);

            return output.ToArray();
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            IncrementCounter("huffman.decompress");

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input size exceeds maximum of {MaxInputSize} bytes");

            using var stream = new MemoryStream(input);

            var magicBuf = new byte[4];
            if (stream.Read(magicBuf, 0, 4) != 4 ||
                magicBuf[0] != Magic[0] || magicBuf[1] != Magic[1] ||
                magicBuf[2] != Magic[2] || magicBuf[3] != Magic[3])
            {
                throw new InvalidDataException("Invalid Huffman header magic.");
            }

            var lenBuf = new byte[4];
            if (stream.Read(lenBuf, 0, 4) != 4)
                throw new InvalidDataException("Invalid Huffman header length.");

            int originalLength = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
            if (originalLength < 0)
                throw new InvalidDataException("Invalid original length in Huffman header.");

            if (originalLength == 0)
                return Array.Empty<byte>();

            // Read code lengths
            var codeLengths = new byte[256];
            if (stream.Read(codeLengths, 0, 256) != 256)
                throw new InvalidDataException("Invalid code lengths in Huffman header.");

            // Build decoding tree
            var decoder = BuildDecoder(codeLengths);

            // Read compressed data (all bytes except last which is padding info)
            long dataStart = stream.Position;
            long dataLength = stream.Length - dataStart - 1;
            var compressedData = new byte[dataLength];
            int totalRead = 0;
            while (totalRead < (int)dataLength)
            {
                int n = stream.Read(compressedData, totalRead, (int)dataLength - totalRead);
                if (n == 0) throw new InvalidDataException("Huffman stream truncated: expected more compressed data.");
                totalRead += n;
            }

            int paddingBits = stream.ReadByte();

            // Decode
            var bitReader = new BitReader(compressedData, paddingBits);
            var result = new byte[originalLength];
            for (int i = 0; i < originalLength; i++)
            {
                result[i] = decoder.DecodeSymbol(bitReader);
            }

            return result;
        }

        /// <summary>
        /// Builds canonical Huffman code lengths from frequency table using Huffman's algorithm.
        /// </summary>
        private static byte[] BuildCanonicalCodeLengths(int[] frequencies)
        {
            // Build Huffman tree
            var heap = new PriorityQueue<HuffmanNode, long>();
            for (int i = 0; i < 256; i++)
            {
                if (frequencies[i] > 0)
                {
                    heap.Enqueue(new HuffmanNode { Symbol = (byte)i, IsLeaf = true }, frequencies[i]);
                }
            }

            // If only one symbol, give it length 1
            if (heap.Count == 1)
            {
                var node = heap.Dequeue();
                var lengths = new byte[256];
                lengths[node.Symbol] = 1;
                return lengths;
            }

            // Build tree by merging two smallest nodes
            while (heap.Count > 1)
            {
                var left = heap.Dequeue();
                var leftFreq = left.Frequency;
                var right = heap.Dequeue();
                var rightFreq = right.Frequency;

                var parent = new HuffmanNode
                {
                    Left = left,
                    Right = right,
                    IsLeaf = false
                };
                long combinedFreq = leftFreq + rightFreq;
                parent.Frequency = combinedFreq;
                heap.Enqueue(parent, combinedFreq);
            }

            var root = heap.Dequeue();

            // Traverse tree to get code lengths
            var codeLengths = new byte[256];
            TraverseForLengths(root, 0, codeLengths);

            // Limit code lengths to MaxCodeLength
            LimitCodeLengths(codeLengths, MaxCodeLength);

            return codeLengths;
        }

        private static void TraverseForLengths(HuffmanNode node, int depth, byte[] codeLengths)
        {
            if (node.IsLeaf)
            {
                codeLengths[node.Symbol] = (byte)Math.Min(depth, MaxCodeLength);
            }
            else
            {
                if (node.Left != null)
                    TraverseForLengths(node.Left, depth + 1, codeLengths);
                if (node.Right != null)
                    TraverseForLengths(node.Right, depth + 1, codeLengths);
            }
        }

        private static void LimitCodeLengths(byte[] lengths, int maxLength)
        {
            // Simple approach: cap at max
            for (int i = 0; i < lengths.Length; i++)
            {
                if (lengths[i] > maxLength)
                    lengths[i] = (byte)maxLength;
            }
        }

        /// <summary>
        /// Builds canonical codes from code lengths.
        /// Returns array of (code, length) tuples.
        /// </summary>
        private static (uint code, int length)[] BuildCanonicalCodes(byte[] codeLengths)
        {
            var codes = new (uint code, int length)[256];

            // Sort symbols by code length, then by symbol value
            var symbols = new List<(byte symbol, int length)>();
            for (int i = 0; i < 256; i++)
            {
                if (codeLengths[i] > 0)
                    symbols.Add(((byte)i, codeLengths[i]));
            }
            symbols.Sort((a, b) =>
            {
                int cmp = a.length.CompareTo(b.length);
                if (cmp != 0) return cmp;
                return a.symbol.CompareTo(b.symbol);
            });

            // Assign canonical codes
            uint code = 0;
            int currentLength = 0;
            foreach (var (symbol, length) in symbols)
            {
                if (length > currentLength)
                {
                    code <<= (length - currentLength);
                    currentLength = length;
                }
                codes[symbol] = (code, length);
                code++;
            }

            return codes;
        }

        /// <summary>
        /// Builds a decoder from code lengths.
        /// </summary>
        private static HuffmanDecoder BuildDecoder(byte[] codeLengths)
        {
            var codes = BuildCanonicalCodes(codeLengths);
            return new HuffmanDecoder(codes, codeLengths);
        }

        private static byte[] CreateEmptyCompressedBlock()
        {
            using var ms = new MemoryStream(4096);
            ms.Write(Magic, 0, 4);
            ms.Write(new byte[4], 0, 4); // length = 0
            ms.Write(new byte[256], 0, 256); // code lengths all zero
            ms.WriteByte(0); // padding
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
            return (long)(inputSize * 0.65) + 512;
        }

        #region Helper Classes

        private sealed class HuffmanNode
        {
            public byte Symbol { get; set; }
            public bool IsLeaf { get; set; }
            public HuffmanNode? Left { get; set; }
            public HuffmanNode? Right { get; set; }
            public long Frequency { get; set; }
        }

        private sealed class HuffmanDecoder
        {
            private readonly Dictionary<(uint code, int length), byte> _table = new();

            public HuffmanDecoder((uint code, int length)[] codes, byte[] codeLengths)
            {
                for (int i = 0; i < 256; i++)
                {
                    if (codeLengths[i] > 0)
                    {
                        _table[codes[i]] = (byte)i;
                    }
                }
            }

            public byte DecodeSymbol(BitReader reader)
            {
                uint code = 0;
                for (int length = 1; length <= MaxCodeLength; length++)
                {
                    code = (code << 1) | (uint)reader.ReadBit();
                    if (_table.TryGetValue((code, length), out byte symbol))
                        return symbol;
                }
                throw new InvalidDataException("Invalid Huffman code encountered.");
            }
        }

        private sealed class BitWriter
        {
            private readonly Stream _output;
            private byte _currentByte;
            private int _bitPosition;

            public BitWriter(Stream output) => _output = output;

            public void WriteBits(uint value, int numBits)
            {
                for (int i = numBits - 1; i >= 0; i--)
                {
                    int bit = (int)((value >> i) & 1);
                    _currentByte = (byte)((_currentByte << 1) | bit);
                    _bitPosition++;

                    if (_bitPosition == 8)
                    {
                        _output.WriteByte(_currentByte);
                        _currentByte = 0;
                        _bitPosition = 0;
                    }
                }
            }

            public byte Flush()
            {
                if (_bitPosition > 0)
                {
                    int padding = 8 - _bitPosition;
                    _currentByte <<= padding;
                    _output.WriteByte(_currentByte);
                    return (byte)padding;
                }
                return 0;
            }
        }

        private sealed class BitReader
        {
            private readonly byte[] _data;
            private readonly int _paddingBits;
            private int _bytePosition;
            private int _bitPosition;

            public BitReader(byte[] data, int paddingBits)
            {
                _data = data;
                _paddingBits = paddingBits;
            }

            public int ReadBit()
            {
                if (_bytePosition >= _data.Length)
                    throw new InvalidDataException("Unexpected end of bit stream.");

                if (_bytePosition == _data.Length - 1 && _bitPosition >= 8 - _paddingBits)
                    throw new InvalidDataException("Unexpected end of bit stream (padding reached).");

                int bit = (_data[_bytePosition] >> (7 - _bitPosition)) & 1;
                _bitPosition++;

                if (_bitPosition == 8)
                {
                    _bitPosition = 0;
                    _bytePosition++;
                }

                return bit;
            }
        }

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
