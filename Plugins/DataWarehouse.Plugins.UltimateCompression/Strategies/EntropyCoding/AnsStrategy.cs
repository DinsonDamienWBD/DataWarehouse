using System;
using System.Buffers.Binary;
using System.IO;
using DataWarehouse.SDK.Contracts.Compression;

namespace DataWarehouse.Plugins.UltimateCompression.Strategies.EntropyCoding
{
    /// <summary>
    /// Compression strategy implementing Asymmetric Numeral Systems (tANS/FSE).
    /// Uses state transitions based on normalized symbol frequencies to achieve
    /// near-optimal entropy coding with faster performance than arithmetic coding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asymmetric Numeral Systems (ANS), invented by Jarek Duda (2009), is a modern entropy
    /// coding method that matches the compression efficiency of arithmetic coding while offering
    /// faster encoding and decoding through table-based state transitions. tANS (tabled ANS)
    /// and FSE (Finite State Entropy) are practical implementations used in modern compressors
    /// like Zstandard and LZFSE.
    /// </para>
    /// <para>
    /// The encoder maintains a state value that is transformed through symbol-specific
    /// transitions. The state encodes information about all previously encoded symbols.
    /// Normalized frequency tables ensure that the state space is efficiently utilized.
    /// </para>
    /// <para>
    /// Format: [Magic:4][OrigLen:4][FreqTable:variable][CompressedData]
    /// </para>
    /// </remarks>
    public sealed class AnsStrategy : CompressionStrategyBase
    {
        private static readonly byte[] Magic = { 0x54, 0x41, 0x4E, 0x53 }; // "TANS"
        private const int TableLog = 11; // 2^11 = 2048 states
        private const int TableSize = 1 << TableLog;
        private const int MaxSymbolValue = 255;

        /// <summary>
        /// Initializes a new instance of the <see cref="AnsStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public AnsStrategy() : base(CompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "ANS",
            TypicalCompressionRatio = 0.52,
            CompressionSpeed = 8,
            DecompressionSpeed = 9,
            CompressionMemoryUsage = 24L * 1024 * 1024,
            DecompressionMemoryUsage = 16L * 1024 * 1024,
            SupportsStreaming = false,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 64,
            OptimalBlockSize = 512 * 1024
        };

        /// <inheritdoc/>
        protected override byte[] CompressCore(byte[] input)
        {
            using var output = new MemoryStream(input.Length + 256);
            output.Write(Magic, 0, 4);

            var lenBytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lenBytes, input.Length);
            output.Write(lenBytes, 0, 4);

            if (input.Length == 0)
                return output.ToArray();

            // Build frequency table
            var frequencies = new int[256];
            foreach (byte b in input)
                frequencies[b]++;

            // Normalize frequencies
            var normalized = NormalizeFrequencies(frequencies, TableSize);

            // Write frequency table
            WriteFrequencyTable(output, normalized);

            // Build encoding table
            var encTable = BuildEncodingTable(normalized);

            // Encode data (backwards for ANS)
            var compressedData = new MemoryStream(65536);
            uint state = TableSize;

            for (int i = input.Length - 1; i >= 0; i--)
            {
                byte symbol = input[i];
                EncodeSymbol(compressedData, ref state, symbol, encTable, normalized);
            }

            // Write final state
            var stateBuf = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(stateBuf, state);
            output.Write(stateBuf, 0, 4);

            // Write compressed data
            var compressed = compressedData.ToArray();
            output.Write(compressed, 0, compressed.Length);

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
                throw new InvalidDataException("Invalid ANS header magic.");
            }

            var lenBuf = new byte[4];
            if (stream.Read(lenBuf, 0, 4) != 4)
                throw new InvalidDataException("Invalid ANS header length.");

            int originalLength = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
            if (originalLength < 0)
                throw new InvalidDataException("Invalid original length in ANS header.");

            if (originalLength == 0)
                return Array.Empty<byte>();

            // Read frequency table
            var normalized = ReadFrequencyTable(stream);

            // Read initial state
            var stateBuf = new byte[4];
            if (stream.Read(stateBuf, 0, 4) != 4)
                throw new InvalidDataException("Invalid state in ANS data.");
            uint state = BinaryPrimitives.ReadUInt32LittleEndian(stateBuf);

            // Read compressed data
            var compressedData = new byte[stream.Length - stream.Position];
            stream.Read(compressedData, 0, compressedData.Length);

            // Build decoding table
            var decTable = BuildDecodingTable(normalized);

            // Decode data (forwards)
            var result = new byte[originalLength];
            int dataPos = compressedData.Length - 1;

            for (int i = 0; i < originalLength; i++)
            {
                var (symbol, newState) = DecodeSymbol(state, decTable, normalized);
                result[i] = symbol;
                state = newState;

                // Renormalize state if needed
                while (state < TableSize && dataPos >= 0)
                {
                    state = (state << 8) | compressedData[dataPos--];
                }
            }

            return result;
        }

        /// <summary>
        /// Normalizes frequency counts to sum to tableSize using proportional distribution.
        /// </summary>
        private static int[] NormalizeFrequencies(int[] frequencies, int tableSize)
        {
            var normalized = new int[256];
            long total = 0;
            int nonZeroCount = 0;

            for (int i = 0; i < 256; i++)
            {
                if (frequencies[i] > 0)
                {
                    total += frequencies[i];
                    nonZeroCount++;
                }
            }

            if (total == 0 || nonZeroCount == 0)
            {
                // Empty data - assign uniform
                normalized[0] = tableSize;
                return normalized;
            }

            int assigned = 0;
            for (int i = 0; i < 256; i++)
            {
                if (frequencies[i] > 0)
                {
                    int norm = Math.Max(1, (int)((long)frequencies[i] * tableSize / total));
                    normalized[i] = norm;
                    assigned += norm;
                }
            }

            // Adjust to exactly tableSize
            int diff = tableSize - assigned;
            if (diff != 0)
            {
                for (int i = 0; i < 256 && diff != 0; i++)
                {
                    if (normalized[i] > 0)
                    {
                        if (diff > 0)
                        {
                            normalized[i]++;
                            diff--;
                        }
                        else if (normalized[i] > 1)
                        {
                            normalized[i]--;
                            diff++;
                        }
                    }
                }
            }

            return normalized;
        }

        private static void WriteFrequencyTable(Stream output, int[] normalized)
        {
            using var ms = new MemoryStream(4096);
            for (int i = 0; i < 256; i++)
            {
                var buf = new byte[2];
                BinaryPrimitives.WriteInt16LittleEndian(buf, (short)normalized[i]);
                ms.Write(buf, 0, 2);
            }
            var data = ms.ToArray();
            output.Write(data, 0, data.Length);
        }

        private static int[] ReadFrequencyTable(Stream input)
        {
            var normalized = new int[256];
            var buf = new byte[2];
            for (int i = 0; i < 256; i++)
            {
                if (input.Read(buf, 0, 2) != 2)
                    throw new InvalidDataException("Invalid frequency table.");
                normalized[i] = BinaryPrimitives.ReadInt16LittleEndian(buf);
            }
            return normalized;
        }

        private static EncodingEntry[] BuildEncodingTable(int[] normalized)
        {
            var table = new EncodingEntry[256];
            int cumulative = 0;

            for (int symbol = 0; symbol < 256; symbol++)
            {
                if (normalized[symbol] > 0)
                {
                    table[symbol] = new EncodingEntry
                    {
                        Start = cumulative,
                        Frequency = normalized[symbol]
                    };
                    cumulative += normalized[symbol];
                }
            }

            return table;
        }

        private static DecodingEntry[] BuildDecodingTable(int[] normalized)
        {
            var table = new DecodingEntry[TableSize];
            int pos = 0;

            for (int symbol = 0; symbol < 256; symbol++)
            {
                for (int i = 0; i < normalized[symbol]; i++)
                {
                    table[pos++] = new DecodingEntry
                    {
                        Symbol = (byte)symbol,
                        Frequency = normalized[symbol],
                        Start = i
                    };
                }
            }

            return table;
        }

        private static void EncodeSymbol(Stream output, ref uint state, byte symbol,
            EncodingEntry[] encTable, int[] normalized)
        {
            var entry = encTable[symbol];
            int freq = entry.Frequency;

            // Renormalize if state would overflow
            while (state >= ((uint)freq << (31 - TableLog)))
            {
                output.WriteByte((byte)(state & 0xFF));
                state >>= 8;
            }

            // Update state
            int stateSlot = (int)(state & (TableSize - 1));
            state = (uint)(((state >> TableLog) * freq) + entry.Start + stateSlot % freq);
        }

        private static (byte symbol, uint newState) DecodeSymbol(uint state,
            DecodingEntry[] decTable, int[] normalized)
        {
            int slot = (int)(state & (TableSize - 1));
            var entry = decTable[slot];

            uint newState = (uint)((entry.Frequency * (state >> TableLog)) + (uint)entry.Start);
            return (entry.Symbol, newState);
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
            return (long)(inputSize * 0.58) + 1024;
        }

        #region Helper Structures

        private struct EncodingEntry
        {
            public int Start;
            public int Frequency;
        }

        private struct DecodingEntry
        {
            public byte Symbol;
            public int Frequency;
            public int Start;
        }

        #endregion

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
