using System;
using System.Collections.Generic;
using System.IO;
using DataWarehouse.SDK.Contracts.Compression;
using SdkCompressionLevel = DataWarehouse.SDK.Contracts.Compression.CompressionLevel;

using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
namespace DataWarehouse.Plugins.UltimateCompression.Strategies.Domain
{
    /// <summary>
    /// Genomic data compression strategy optimized for DNA-like sequences.
    /// Classifies bytes as ACGT-like (2-bit encoding) versus non-ACGT (Huffman coding).
    /// Produces a hybrid stream combining compact nucleotide encoding with general-purpose entropy coding.
    /// </summary>
    /// <remarks>
    /// DNA sequences consist primarily of four nucleotides (A, C, G, T) which can be efficiently
    /// encoded using 2 bits each. This strategy:
    /// 1. Scans input to identify ACGT-dominant regions
    /// 2. Encodes ACGT bytes with 2-bit representation
    /// 3. Falls back to Huffman coding for non-ACGT data
    /// 4. Maintains a mode stream to switch between encodings
    /// </remarks>
    public sealed class DnaCompressionStrategy : CompressionStrategyBase
    {
        private const int MaxInputSize = 100 * 1024 * 1024; // 100 MB

        private const uint MagicHeader = 0x444E4143; // 'DNAC'
        private const int BlockSize = 1024;

        /// <summary>
        /// Initializes a new instance of the <see cref="DnaCompressionStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public DnaCompressionStrategy() : base(SdkCompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "DNA-Compression",
            TypicalCompressionRatio = 0.45,
            CompressionSpeed = 6,
            DecompressionSpeed = 7,
            CompressionMemoryUsage = 128 * 1024,
            DecompressionMemoryUsage = 64 * 1024,
            SupportsStreaming = true,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 64,
            OptimalBlockSize = BlockSize
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
                        "DNA-Compression strategy healthy",
                        new Dictionary<string, object>
                        {
                            ["CompressOperations"] = GetCounter("dna-compression.compress"),
                            ["DecompressOperations"] = GetCounter("dna-compression.decompress")
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
            IncrementCounter("dna-compression.compress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for DNA-Compression");
            using var output = new MemoryStream(input.Length + 256);
            using var writer = new BinaryWriter(output);

            // Write header
            writer.Write(MagicHeader);
            writer.Write(input.Length);

            if (input.Length == 0)
                return output.ToArray();

            // Process in blocks
            for (int offset = 0; offset < input.Length; offset += BlockSize)
            {
                int count = Math.Min(BlockSize, input.Length - offset);
                CompressBlock(writer, input, offset, count);
            }

            return output.ToArray();
        }

        private static void CompressBlock(BinaryWriter writer, byte[] input, int offset, int count)
        {
            // Analyze block to determine if it's ACGT-dominant
            int acgtCount = 0;
            for (int i = 0; i < count; i++)
            {
                byte b = input[offset + i];
                if (IsAcgt(b))
                    acgtCount++;
            }

            bool useAcgtMode = acgtCount > count * 0.7; // 70% threshold
            writer.Write(useAcgtMode);
            writer.Write((ushort)count);

            if (useAcgtMode)
            {
                EncodeAcgtBlock(writer, input, offset, count);
            }
            else
            {
                EncodeHuffmanBlock(writer, input, offset, count);
            }
        }

        private static bool IsAcgt(byte b)
        {
            return b == (byte)'A' || b == (byte)'C' || b == (byte)'G' || b == (byte)'T' ||
                   b == (byte)'a' || b == (byte)'c' || b == (byte)'g' || b == (byte)'t';
        }

        private static byte Encode2Bit(byte b)
        {
            return b switch
            {
                (byte)'A' or (byte)'a' => 0,
                (byte)'C' or (byte)'c' => 1,
                (byte)'G' or (byte)'g' => 2,
                (byte)'T' or (byte)'t' => 3,
                _ => 0
            };
        }

        private static byte Decode2Bit(byte code, bool uppercase)
        {
            byte[] table = uppercase
                ? new byte[] { (byte)'A', (byte)'C', (byte)'G', (byte)'T' }
                : new byte[] { (byte)'a', (byte)'c', (byte)'g', (byte)'t' };
            return table[code & 0x03];
        }

        private static void EncodeAcgtBlock(BinaryWriter writer, byte[] input, int offset, int count)
        {
            // Pack 4 nucleotides per byte (2 bits each)
            using var bitStream = new MemoryStream(65536);
            var bitWriter = new BitPackWriter(bitStream);

            // First pass: encode case information (1 bit per byte)
            for (int i = 0; i < count; i++)
            {
                byte b = input[offset + i];
                bool isUpper = char.IsUpper((char)b);
                bitWriter.WriteBit(isUpper ? 1 : 0);
            }

            // Second pass: encode 2-bit nucleotides
            for (int i = 0; i < count; i++)
            {
                byte b = input[offset + i];
                if (IsAcgt(b))
                {
                    byte code = Encode2Bit(b);
                    bitWriter.WriteBits(code, 2);
                }
                else
                {
                    // Non-ACGT byte in ACGT mode - encode as 'A' (0)
                    bitWriter.WriteBits(0, 2);
                }
            }

            bitWriter.Flush();
            byte[] packed = bitStream.ToArray();
            writer.Write(packed.Length);
            writer.Write(packed);
        }

        private static void EncodeHuffmanBlock(BinaryWriter writer, byte[] input, int offset, int count)
        {
            // Build frequency table
            var frequencies = new Dictionary<byte, int>();
            for (int i = 0; i < count; i++)
            {
                byte b = input[offset + i];
                frequencies[b] = frequencies.GetValueOrDefault(b, 0) + 1;
            }

            // Build Huffman tree
            var tree = BuildHuffmanTree(frequencies);
            var codes = BuildHuffmanCodes(tree);

            // Write codebook
            writer.Write((ushort)codes.Count);
            foreach (var kvp in codes)
            {
                writer.Write(kvp.Key);
                writer.Write((byte)kvp.Value.Length);
                writer.Write(kvp.Value.Code);
            }

            // Encode data
            using var bitStream = new MemoryStream(65536);
            var bitWriter = new BitPackWriter(bitStream);

            for (int i = 0; i < count; i++)
            {
                byte b = input[offset + i];
                if (codes.TryGetValue(b, out var code))
                {
                    bitWriter.WriteBits(code.Code, code.Length);
                }
            }

            bitWriter.Flush();
            byte[] encoded = bitStream.ToArray();
            writer.Write(encoded.Length);
            writer.Write(encoded);
        }

        private static HuffmanNode BuildHuffmanTree(Dictionary<byte, int> frequencies)
        {
            var heap = new SortedSet<HuffmanNode>(Comparer<HuffmanNode>.Create((a, b) =>
            {
                int cmp = a.Frequency.CompareTo(b.Frequency);
                return cmp != 0 ? cmp : a.GetHashCode().CompareTo(b.GetHashCode());
            }));

            foreach (var kvp in frequencies)
                heap.Add(new HuffmanNode { Symbol = kvp.Key, Frequency = kvp.Value });

            while (heap.Count > 1)
            {
                var left = heap.Min!;
                if (left != null) heap.Remove(left);
                var right = heap.Min!;
                if (right != null) heap.Remove(right);

                var parent = new HuffmanNode
                {
                    Frequency = left!.Frequency + right!.Frequency,
                    Left = left,
                    Right = right
                };

                heap.Add(parent);
            }

            // P2-1600: Guard against empty heap (all-zero frequency input).
            if (heap.Count == 0)
                throw new InvalidDataException("Cannot build Huffman tree from empty frequency table — input contains no DNA bases.");
            return heap.Min!;
        }

        private static Dictionary<byte, HuffmanCode> BuildHuffmanCodes(HuffmanNode root)
        {
            var codes = new Dictionary<byte, HuffmanCode>();
            if (root.Left == null && root.Right == null)
            {
                // Single symbol
                codes[root.Symbol] = new HuffmanCode { Code = 0, Length = 1 };
            }
            else
            {
                BuildCodesRecursive(root, 0, 0, codes);
            }
            return codes;
        }

        private static void BuildCodesRecursive(HuffmanNode node, uint code, int length, Dictionary<byte, HuffmanCode> codes)
        {
            if (node.Left == null && node.Right == null)
            {
                codes[node.Symbol] = new HuffmanCode { Code = code, Length = length };
                return;
            }

            if (node.Left != null)
                BuildCodesRecursive(node.Left, code << 1, length + 1, codes);
            if (node.Right != null)
                BuildCodesRecursive(node.Right, (code << 1) | 1, length + 1, codes);
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            IncrementCounter("dna-compression.decompress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for DNA-Compression");
            using var stream = new MemoryStream(input);
            using var reader = new BinaryReader(stream);

            // Read header
            uint magic = reader.ReadUInt32();
            if (magic != MagicHeader)
                throw new InvalidDataException("Invalid DNA stream header.");

            int originalLength = reader.ReadInt32();
            if (originalLength == 0)
                return Array.Empty<byte>();

            using var output = new MemoryStream(input.Length + 256);

            while (output.Length < originalLength)
            {
                bool useAcgtMode = reader.ReadBoolean();
                ushort blockCount = reader.ReadUInt16();

                if (useAcgtMode)
                {
                    DecodeAcgtBlock(reader, output, blockCount);
                }
                else
                {
                    DecodeHuffmanBlock(reader, output, blockCount);
                }
            }

            return output.ToArray();
        }

        private static void DecodeAcgtBlock(BinaryReader reader, MemoryStream output, int count)
        {
            int packedLength = reader.ReadInt32();
            byte[] packed = reader.ReadBytes(packedLength);
            var bitReader = new BitPackReader(packed);

            // Read case information
            var caseInfo = new bool[count];
            for (int i = 0; i < count; i++)
                caseInfo[i] = bitReader.ReadBit() == 1;

            // Read 2-bit nucleotides
            for (int i = 0; i < count; i++)
            {
                byte code = (byte)bitReader.ReadBits(2);
                byte symbol = Decode2Bit(code, caseInfo[i]);
                output.WriteByte(symbol);
            }
        }

        private static void DecodeHuffmanBlock(BinaryReader reader, MemoryStream output, int count)
        {
            // Read codebook
            ushort codeCount = reader.ReadUInt16();
            var tree = new HuffmanNode();

            for (int i = 0; i < codeCount; i++)
            {
                byte symbol = reader.ReadByte();
                byte length = reader.ReadByte();
                uint code = reader.ReadUInt32();

                // Insert into tree
                var node = tree;
                for (int bit = length - 1; bit >= 0; bit--)
                {
                    bool isOne = ((code >> bit) & 1) == 1;
                    if (bit == 0)
                    {
                        if (isOne)
                            node.Right = new HuffmanNode { Symbol = symbol };
                        else
                            node.Left = new HuffmanNode { Symbol = symbol };
                    }
                    else
                    {
                        if (isOne)
                        {
                            node.Right ??= new HuffmanNode();
                            node = node.Right;
                        }
                        else
                        {
                            node.Left ??= new HuffmanNode();
                            node = node.Left;
                        }
                    }
                }
            }

            // Decode data
            int encodedLength = reader.ReadInt32();
            byte[] encoded = reader.ReadBytes(encodedLength);
            var bitReader = new BitPackReader(encoded);

            for (int i = 0; i < count; i++)
            {
                var node = tree;
                while (node.Left != null || node.Right != null)
                {
                    int bit = bitReader.ReadBit();
                    node = bit == 1 ? node.Right : node.Left;
                    if (node == null)
                        throw new InvalidDataException("Invalid Huffman code.");
                }
                output.WriteByte(node.Symbol);
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
            return (long)(inputSize * 0.50) + 128;
        }

        #region Helper Classes

        private class HuffmanNode
        {
            public byte Symbol { get; set; }
            public int Frequency { get; set; }
            public HuffmanNode? Left { get; set; }
            public HuffmanNode? Right { get; set; }
        }

        private struct HuffmanCode
        {
            public uint Code { get; set; }
            public int Length { get; set; }
        }

        private class BitPackWriter
        {
            private readonly Stream _stream;
            private byte _currentByte;
            private int _bitIndex;

            public BitPackWriter(Stream stream) => _stream = stream;

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

            public void WriteBits(uint value, int count)
            {
                for (int i = count - 1; i >= 0; i--)
                    WriteBit((int)((value >> i) & 1));
            }

            public void Flush()
            {
                if (_bitIndex > 0)
                    _stream.WriteByte(_currentByte);
            }
        }

        private class BitPackReader
        {
            private readonly byte[] _data;
            private int _byteIndex;
            private int _bitIndex;

            public BitPackReader(byte[] data) => _data = data;

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

            public uint ReadBits(int count)
            {
                uint value = 0;
                for (int i = 0; i < count; i++)
                    value = (value << 1) | (uint)ReadBit();
                return value;
            }
        }

        #endregion

        #region Stream Wrappers

        private sealed class BufferedCompressionStream : Stream
        {
            private readonly Stream _output;
            private readonly bool _leaveOpen;
            private readonly DnaCompressionStrategy _strategy;
            private readonly MemoryStream _buffer = new();
            private bool _disposed;

            public BufferedCompressionStream(Stream output, bool leaveOpen, DnaCompressionStrategy strategy)
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
            private readonly DnaCompressionStrategy _strategy;
            private byte[]? _decompressedData;
            private int _position;
            private bool _disposed;

            public BufferedDecompressionStream(Stream input, bool leaveOpen, DnaCompressionStrategy strategy)
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
