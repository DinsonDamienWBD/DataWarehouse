using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Compression;

namespace DataWarehouse.Plugins.UltimateCompression.Strategies.LzFamily
{
    /// <summary>
    /// Compression strategy implementing the LZH (LHarc -lh5-) algorithm in managed C#.
    /// LZH combines a sliding window LZ77 front-end with adaptive Huffman coding for
    /// output encoding, providing good compression ratios for general-purpose data.
    /// </summary>
    /// <remarks>
    /// LZH was developed by Haruyasu Yoshizaki (Yoshi) for the LHarc/LHA archiver,
    /// widely used in Japan and the Amiga community. The -lh5- method uses a 8KB sliding
    /// window with adaptive Huffman coding of literal bytes and (distance, length) pairs.
    /// This implementation uses a simplified but functional adaptive Huffman encoder.
    /// </remarks>
    public sealed class LzhStrategy : CompressionStrategyBase
    {
        private const int WindowSize = 8192;       // 8 KB sliding window (-lh5-)
        private const int MaxMatch = 256;
        private const int MinMatch = 3;
        private const int HashSize = 4096;
        private const int LiteralBase = 256;
        private const int SymbolCount = LiteralBase + MaxMatch - MinMatch + 1;
        private const int MaxInputSize = 100 * 1024 * 1024; // 100 MB

        private int _configuredWindowSize = WindowSize;
        private int _configuredMaxMatch = MaxMatch;

        /// <summary>
        /// Initializes a new instance of the <see cref="LzhStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public LzhStrategy() : base(CompressionLevel.Default)
        {
        }


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
                    // Round-trip test: compress + decompress 16 bytes of test data
                    var testData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
                    var compressed = CompressCore(testData);
                    var decompressed = DecompressCore(compressed);

                    if (decompressed.Length != testData.Length)
                    {
                        return new StrategyHealthCheckResult(
                            false,
                            $"Health check failed: decompressed length {decompressed.Length} != original {testData.Length}");
                    }

                    for (int i = 0; i < testData.Length; i++)
                    {
                        if (decompressed[i] != testData[i])
                        {
                            return new StrategyHealthCheckResult(
                                false,
                                $"Health check failed: byte mismatch at position {i}");
                        }
                    }

                    return new StrategyHealthCheckResult(
                        true,
                        "LZH strategy healthy",
                        new Dictionary<string, object>
                        {
                            ["WindowSize"] = _configuredWindowSize,
                            ["MaxMatchLength"] = _configuredMaxMatch,
                            ["CompressOperations"] = GetCounter("lzh.compress"),
                            ["DecompressOperations"] = GetCounter("lzh.decompress")
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
            // No resources to clean up in LZH
            return base.ShutdownAsyncCore(cancellationToken);
        }

        /// <inheritdoc/>
        protected override ValueTask DisposeAsyncCore()
        {
            // No async resources to dispose
            return base.DisposeAsyncCore();
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "LZH",
            TypicalCompressionRatio = 0.43,
            CompressionSpeed = 4,
            DecompressionSpeed = 5,
            CompressionMemoryUsage = 128 * 1024,
            DecompressionMemoryUsage = 64 * 1024,
            SupportsStreaming = false,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 256,
            OptimalBlockSize = 32 * 1024
        };

        /// <inheritdoc/>
        protected override byte[] CompressCore(byte[] input)
        {
            // Strategy-specific counter
            IncrementCounter("lzh.compress");

            // Edge case: maximum input size guard
            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB");

            if (input.Length < MinMatch)
                return StoreUncompressed(input);

            using var output = new MemoryStream(input.Length);
            var bitWriter = new LzhBitWriter(output);

            // Header
            bitWriter.WriteBytes(new byte[] { 0x4C, 0x5A, 0x48, 0x35 }); // "LZH5"
            bitWriter.WriteInt32(input.Length);

            var hashTable = new int[HashSize];
            Array.Fill(hashTable, -1);

            // Build frequency tables for Huffman coding
            var literalFreq = new int[SymbolCount];
            var distFreq = new int[16]; // distance encoded as 4-bit position + offset bits

            // First pass: collect statistics
            var tokens = new System.Collections.Generic.List<(bool isMatch, int value, int distance)>();
            int pos = 0;

            while (pos < input.Length)
            {
                int bestLen = MinMatch - 1;
                int bestDist = 0;

                if (pos + MinMatch <= input.Length)
                {
                    int hash = HashLzh(input, pos);
                    int mp = hashTable[hash];
                    hashTable[hash] = pos;

                    if (mp >= 0 && pos - mp <= WindowSize)
                    {
                        int maxLen = Math.Min(MaxMatch, input.Length - pos);
                        int matchLen = 0;
                        while (matchLen < maxLen && input[mp + matchLen] == input[pos + matchLen])
                            matchLen++;

                        if (matchLen >= MinMatch)
                        {
                            bestLen = matchLen;
                            bestDist = pos - mp;
                        }
                    }
                }

                if (bestLen >= MinMatch)
                {
                    int lengthSym = LiteralBase + bestLen - MinMatch;
                    literalFreq[lengthSym]++;
                    int distSlot = HighBit(bestDist);
                    distFreq[distSlot]++;
                    tokens.Add((true, bestLen, bestDist));

                    for (int i = 1; i < bestLen && pos + i + 2 < input.Length; i++)
                        hashTable[HashLzh(input, pos + i)] = pos + i;

                    pos += bestLen;
                }
                else
                {
                    literalFreq[input[pos]]++;
                    tokens.Add((false, input[pos], 0));
                    pos++;
                }
            }

            // Build Huffman code lengths (simplified: use frequency-based bit lengths)
            var litCodeLen = BuildCodeLengths(literalFreq, SymbolCount, 15);
            var distCodeLen = BuildCodeLengths(distFreq, 16, 7);

            // Build canonical Huffman codes
            var litCodes = BuildCanonicalCodes(litCodeLen, SymbolCount);
            var distCodes = BuildCanonicalCodes(distCodeLen, 16);

            // Write code length tables
            bitWriter.WriteByte((byte)CountNonZero(litCodeLen, SymbolCount));
            for (int i = 0; i < SymbolCount; i++)
            {
                if (litCodeLen[i] > 0)
                {
                    bitWriter.WriteBits(i, 10);
                    bitWriter.WriteBits(litCodeLen[i], 4);
                }
            }

            bitWriter.WriteByte((byte)CountNonZero(distCodeLen, 16));
            for (int i = 0; i < 16; i++)
            {
                if (distCodeLen[i] > 0)
                {
                    bitWriter.WriteBits(i, 4);
                    bitWriter.WriteBits(distCodeLen[i], 3);
                }
            }

            // Write token count
            bitWriter.WriteInt32(tokens.Count);

            // Second pass: encode tokens
            foreach (var (isMatch, value, distance) in tokens)
            {
                if (isMatch)
                {
                    int sym = LiteralBase + value - MinMatch;
                    bitWriter.WriteBits(litCodes[sym], litCodeLen[sym]);

                    int distSlot = HighBit(distance);
                    bitWriter.WriteBits(distCodes[distSlot], distCodeLen[distSlot]);

                    // Write extra distance bits
                    if (distSlot > 0)
                    {
                        int extraBits = distSlot;
                        int extraValue = distance - (1 << distSlot);
                        bitWriter.WriteBits(extraValue, extraBits);
                    }
                }
                else
                {
                    bitWriter.WriteBits(litCodes[value], litCodeLen[value]);
                }
            }

            bitWriter.Flush();
            return output.ToArray();
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            // Strategy-specific counter
            IncrementCounter("lzh.decompress");

            // Edge case: validate header
            if (input.Length < 8)
                throw new InvalidDataException("LZH data too short.");

            // Check for uncompressed marker
            if (input[0] == 0x4C && input[1] == 0x5A && input[2] == 0x48 && input[3] == 0x30) // "LZH0"
            {
                int len = BinaryPrimitives.ReadInt32LittleEndian(input.AsSpan(4));
                if (len < 0 || len > MaxInputSize)
                    throw new InvalidDataException($"Invalid LZH uncompressed block length: {len}");
                var result = new byte[len];
                Buffer.BlockCopy(input, 8, result, 0, Math.Min(len, input.Length - 8));
                return result;
            }

            if (input[0] != 0x4C || input[1] != 0x5A || input[2] != 0x48 || input[3] != 0x35)
                throw new InvalidDataException("Invalid LZH magic.");

            using var stream = new MemoryStream(input);
            stream.Position = 4;
            var bitReader = new LzhBitReader(stream);

            int uncompressedLen = bitReader.ReadInt32();
            if (uncompressedLen < 0 || uncompressedLen > MaxInputSize)
                throw new InvalidDataException($"Invalid LZH uncompressed length: {uncompressedLen}");

            // Read literal code table
            int litCount = bitReader.ReadByte();
            var litCodeLen = new int[SymbolCount];
            for (int i = 0; i < litCount; i++)
            {
                int sym = bitReader.ReadBits(10);
                int len = bitReader.ReadBits(4);
                if (sym < SymbolCount)
                    litCodeLen[sym] = len;
            }

            // Read distance code table
            int distCount = bitReader.ReadByte();
            var distCodeLen = new int[16];
            for (int i = 0; i < distCount; i++)
            {
                int sym = bitReader.ReadBits(4);
                int len = bitReader.ReadBits(3);
                if (sym < 16)
                    distCodeLen[sym] = len;
            }

            // Build decoding tables
            var litDecoder = BuildDecodingTable(litCodeLen, SymbolCount);
            var distDecoder = BuildDecodingTable(distCodeLen, 16);

            int tokenCount = bitReader.ReadInt32();

            var output = new byte[uncompressedLen];
            int op = 0;

            for (int t = 0; t < tokenCount && op < uncompressedLen; t++)
            {
                int sym = DecodeSymbol(bitReader, litDecoder, litCodeLen, SymbolCount);

                if (sym < LiteralBase)
                {
                    output[op++] = (byte)sym;
                }
                else
                {
                    int matchLen = sym - LiteralBase + MinMatch;
                    int distSlot = DecodeSymbol(bitReader, distDecoder, distCodeLen, 16);
                    int distance;
                    if (distSlot == 0)
                    {
                        distance = 1;
                    }
                    else
                    {
                        int extraBits = distSlot;
                        int extra = bitReader.ReadBits(extraBits);
                        distance = (1 << distSlot) + extra;
                    }

                    if (distance <= 0 || op - distance < 0)
                        throw new InvalidDataException("Invalid LZH match distance.");

                    int srcPos = op - distance;
                    for (int i = 0; i < matchLen && op < uncompressedLen; i++)
                        output[op++] = output[srcPos + i];
                }
            }

            return output;
        }

        /// <inheritdoc/>
        protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
        {
            return new BufferedAlgorithmCompressionStream(output, leaveOpen, CompressCore);
        }

        /// <inheritdoc/>
        protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
        {
            return new BufferedAlgorithmDecompressionStream(input, leaveOpen, DecompressCore);
        }

        private static int HashLzh(byte[] data, int pos)
        {
            return ((data[pos] << 4) ^ data[pos + 1] ^ (data[pos + 2] << 2)) & (HashSize - 1);
        }

        private static int HighBit(int value)
        {
            if (value <= 0) return 0;
            int bit = 0;
            int v = value;
            while (v > 1) { v >>= 1; bit++; }
            return bit;
        }

        private static int[] BuildCodeLengths(int[] freq, int count, int maxLen)
        {
            var lengths = new int[count];
            int totalFreq = 0;
            foreach (var f in freq) totalFreq += f;
            if (totalFreq == 0) return lengths;

            for (int i = 0; i < count; i++)
            {
                if (freq[i] > 0)
                {
                    double probability = (double)freq[i] / totalFreq;
                    int idealLen = Math.Max(1, (int)Math.Ceiling(-Math.Log2(probability)));
                    lengths[i] = Math.Min(idealLen, maxLen);
                }
            }
            return lengths;
        }

        private static int[] BuildCanonicalCodes(int[] codeLengths, int count)
        {
            var codes = new int[count];
            var blCount = new int[32];
            for (int i = 0; i < count; i++)
                if (codeLengths[i] > 0)
                    blCount[codeLengths[i]]++;

            int code = 0;
            var nextCode = new int[32];
            for (int bits = 1; bits < 32; bits++)
            {
                code = (code + blCount[bits - 1]) << 1;
                nextCode[bits] = code;
            }

            for (int i = 0; i < count; i++)
            {
                int len = codeLengths[i];
                if (len > 0)
                {
                    codes[i] = nextCode[len]++;
                }
            }
            return codes;
        }

        private static (int code, int symbol)[] BuildDecodingTable(int[] codeLengths, int count)
        {
            var codes = BuildCanonicalCodes(codeLengths, count);
            var table = new (int code, int symbol)[count];
            for (int i = 0; i < count; i++)
                table[i] = (codes[i], i);
            return table;
        }

        private static int DecodeSymbol(LzhBitReader reader, (int code, int symbol)[] table, int[] codeLengths, int count)
        {
            int code = 0;
            for (int len = 1; len <= 15; len++)
            {
                code = (code << 1) | reader.ReadBits(1);
                for (int i = 0; i < count; i++)
                {
                    if (codeLengths[i] == len && table[i].code == code)
                        return table[i].symbol;
                }
            }
            // Fallback: return 0 (should not happen with valid data)
            return 0;
        }

        private static int CountNonZero(int[] arr, int count)
        {
            int c = 0;
            for (int i = 0; i < count; i++)
                if (arr[i] > 0) c++;
            return Math.Min(c, 255);
        }

        private static byte[] StoreUncompressed(byte[] input)
        {
            var output = new byte[8 + input.Length];
            output[0] = 0x4C; output[1] = 0x5A; output[2] = 0x48; output[3] = 0x30; // "LZH0"
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(4), input.Length);
            Buffer.BlockCopy(input, 0, output, 8, input.Length);
            return output;
        }

        /// <summary>
        /// Bit-level writer for LZH compression.
        /// </summary>
        private sealed class LzhBitWriter
        {
            private readonly Stream _stream;
            private int _bitBuffer;
            private int _bitsInBuffer;

            public LzhBitWriter(Stream stream) => _stream = stream;

            public void WriteBits(int value, int numBits)
            {
                for (int i = numBits - 1; i >= 0; i--)
                {
                    _bitBuffer = (_bitBuffer << 1) | ((value >> i) & 1);
                    _bitsInBuffer++;
                    if (_bitsInBuffer == 8)
                    {
                        _stream.WriteByte((byte)_bitBuffer);
                        _bitBuffer = 0;
                        _bitsInBuffer = 0;
                    }
                }
            }

            public void WriteByte(byte value) => _stream.WriteByte(value);

            public void WriteBytes(byte[] data) => _stream.Write(data, 0, data.Length);

            public void WriteInt32(int value)
            {
                Flush(); // Align to byte boundary before writing int
                Span<byte> buf = stackalloc byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(buf, value);
                _stream.Write(buf);
            }

            public void Flush()
            {
                if (_bitsInBuffer > 0)
                {
                    _bitBuffer <<= (8 - _bitsInBuffer);
                    _stream.WriteByte((byte)_bitBuffer);
                    _bitBuffer = 0;
                    _bitsInBuffer = 0;
                }
            }
        }

        /// <summary>
        /// Bit-level reader for LZH decompression.
        /// </summary>
        private sealed class LzhBitReader
        {
            private readonly Stream _stream;
            private int _bitBuffer;
            private int _bitsInBuffer;

            public LzhBitReader(Stream stream) => _stream = stream;

            public int ReadBits(int numBits)
            {
                int result = 0;
                for (int i = 0; i < numBits; i++)
                {
                    if (_bitsInBuffer == 0)
                    {
                        int b = _stream.ReadByte();
                        if (b < 0) return result;
                        _bitBuffer = b;
                        _bitsInBuffer = 8;
                    }
                    _bitsInBuffer--;
                    result = (result << 1) | ((_bitBuffer >> _bitsInBuffer) & 1);
                }
                return result;
            }

            public byte ReadByte()
            {
                // Discard remaining bits and read aligned byte
                _bitsInBuffer = 0;
                int b = _stream.ReadByte();
                return b < 0 ? (byte)0 : (byte)b;
            }

            public int ReadInt32()
            {
                _bitsInBuffer = 0; // Align
                Span<byte> buf = stackalloc byte[4];
                for (int i = 0; i < 4; i++)
                {
                    int b = _stream.ReadByte();
                    buf[i] = b >= 0 ? (byte)b : (byte)0;
                }
                return BinaryPrimitives.ReadInt32LittleEndian(buf);
            }
        }
    }
}
