using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Compression;

namespace DataWarehouse.Plugins.UltimateCompression.Strategies.EntropyCoding
{
    /// <summary>
    /// Compression strategy implementing Range ANS (rANS).
    /// A variant of Asymmetric Numeral Systems that uses range-based encoding
    /// with normalized frequency tables for efficient entropy coding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Range ANS (rANS) is a practical variant of ANS that processes symbols in forward order
    /// (unlike tANS which processes backwards). It maintains a state that represents the
    /// compressed message as an integer, updating it through range-based arithmetic similar
    /// to arithmetic coding but with simpler operations.
    /// </para>
    /// <para>
    /// rANS achieves the same compression efficiency as tANS and arithmetic coding while
    /// offering good performance through table lookups and integer arithmetic. It's used in
    /// modern compressors like Draco and various game engines for its balance of speed
    /// and compression ratio.
    /// </para>
    /// <para>
    /// Format: [Magic:4][OrigLen:4][FreqTable:512][FinalState:4][CompressedData]
    /// </para>
    /// </remarks>
    public sealed class RansStrategy : CompressionStrategyBase
    {
        private static readonly byte[] Magic = { 0x52, 0x41, 0x4E, 0x53 }; // "RANS"
        // Default table log when _precisionBits is not configured
        private const int DefaultTableLog = 11;
        private const int MaxInputSize = 100 * 1024 * 1024; // 100MB limit

        // Configurable rANS parameters; stored and used during compression
        private int _interleavedStreams = 1;
        private int _precisionBits = DefaultTableLog;

        // Derived constants computed from configurable parameters
        private int TableLog => _precisionBits;
        private int TableSize => 1 << TableLog;
        private uint RansL => (uint)TableSize;

        /// <summary>
        /// Initializes a new instance of the <see cref="RansStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public RansStrategy() : base(CompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "rANS",
            TypicalCompressionRatio = 0.53,
            CompressionSpeed = 8,
            DecompressionSpeed = 9,
            CompressionMemoryUsage = 20L * 1024 * 1024,
            DecompressionMemoryUsage = 16L * 1024 * 1024,
            SupportsStreaming = false,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 64,
            OptimalBlockSize = 512 * 1024
        };

        /// <inheritdoc/>
        protected override async Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            // Validate configuration parameters
            if (_interleavedStreams < 1 || _interleavedStreams > 64)
                throw new ArgumentException($"Number of interleaved streams must be between 1 and 64. Got: {_interleavedStreams}");

            if (_precisionBits < 8 || _precisionBits > 32)
                throw new ArgumentException($"Precision bits must be between 8 and 32. Got: {_precisionBits}");

            await base.InitializeAsyncCore(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            // No resources to clean up for rANS
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
                    var testData = new byte[] { 50, 51, 50, 52, 50, 51 };
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
            IncrementCounter("rans.compress");

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input size exceeds maximum of {MaxInputSize} bytes");

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
            int tableLog = TableLog;
            int tableSize = TableSize;
            uint ransL = RansL;
            var (normalized, cumulative) = NormalizeFrequencies(frequencies, tableSize);

            // Write frequency table
            WriteFrequencyTable(output, normalized);

            // Encode data
            var compressedData = new MemoryStream(65536);
            uint state = ransL;

            for (int i = 0; i < input.Length; i++)
            {
                byte symbol = input[i];
                EncodeSymbol(compressedData, ref state, symbol, normalized, cumulative, ransL, tableLog, tableSize);
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
            IncrementCounter("rans.decompress");

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input size exceeds maximum of {MaxInputSize} bytes");

            using var stream = new MemoryStream(input);

            var magicBuf = new byte[4];
            if (stream.Read(magicBuf, 0, 4) != 4 ||
                magicBuf[0] != Magic[0] || magicBuf[1] != Magic[1] ||
                magicBuf[2] != Magic[2] || magicBuf[3] != Magic[3])
            {
                throw new InvalidDataException("Invalid rANS header magic.");
            }

            var lenBuf = new byte[4];
            if (stream.Read(lenBuf, 0, 4) != 4)
                throw new InvalidDataException("Invalid rANS header length.");

            int originalLength = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
            if (originalLength < 0)
                throw new InvalidDataException("Invalid original length in rANS header.");

            if (originalLength == 0)
                return Array.Empty<byte>();

            // Read frequency table
            var (normalized, cumulative) = ReadFrequencyTable(stream);

            // Read initial state
            var stateBuf = new byte[4];
            if (stream.Read(stateBuf, 0, 4) != 4)
                throw new InvalidDataException("Invalid state in rANS data.");
            uint state = BinaryPrimitives.ReadUInt32LittleEndian(stateBuf);

            // Read compressed data — check return value to detect partial reads (truncated stream)
            var compressedData = new byte[stream.Length - stream.Position];
            int totalRead = 0;
            while (totalRead < compressedData.Length)
            {
                int n = stream.Read(compressedData, totalRead, compressedData.Length - totalRead);
                if (n == 0) throw new InvalidDataException("rANS stream truncated: expected more compressed data.");
                totalRead += n;
            }

            // Decode data
            var result = new byte[originalLength];
            int dataPos = 0;

            int tableLog = TableLog;
            int tableSize = TableSize;
            uint ransL = RansL;
            for (int i = 0; i < originalLength; i++)
            {
                var (symbol, newState) = DecodeSymbol(state, normalized, cumulative, tableLog);
                result[i] = symbol;
                state = newState;

                // Renormalize state
                while (state < ransL && dataPos < compressedData.Length)
                {
                    state = (state << 8) | compressedData[dataPos++];
                }
            }

            return result;
        }

        /// <summary>
        /// Normalizes frequencies to sum to TableSize and builds cumulative table.
        /// </summary>
        private static (int[] normalized, int[] cumulative) NormalizeFrequencies(int[] frequencies, int tableSize)
        {
            var normalized = new int[256];
            var cumulative = new int[257];
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
                normalized[0] = tableSize;
                cumulative[0] = 0;
                cumulative[1] = tableSize;
                return (normalized, cumulative);
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

            // Build cumulative table
            cumulative[0] = 0;
            for (int i = 0; i < 256; i++)
            {
                cumulative[i + 1] = cumulative[i] + normalized[i];
            }

            return (normalized, cumulative);
        }

        private static void WriteFrequencyTable(Stream output, int[] normalized)
        {
            for (int i = 0; i < 256; i++)
            {
                var buf = new byte[2];
                BinaryPrimitives.WriteInt16LittleEndian(buf, (short)normalized[i]);
                output.Write(buf, 0, 2);
            }
        }

        private static (int[] normalized, int[] cumulative) ReadFrequencyTable(Stream input)
        {
            var normalized = new int[256];
            var cumulative = new int[257];
            var buf = new byte[2];

            for (int i = 0; i < 256; i++)
            {
                if (input.Read(buf, 0, 2) != 2)
                    throw new InvalidDataException("Invalid frequency table.");
                normalized[i] = BinaryPrimitives.ReadInt16LittleEndian(buf);
            }

            cumulative[0] = 0;
            for (int i = 0; i < 256; i++)
            {
                cumulative[i + 1] = cumulative[i] + normalized[i];
            }

            return (normalized, cumulative);
        }

        private static void EncodeSymbol(Stream output, ref uint state, byte symbol,
            int[] normalized, int[] cumulative, uint ransL, int tableLog, int tableSize)
        {
            int freq = normalized[symbol];
            int start = cumulative[symbol];

            // Renormalize if needed
            uint maxState = ((ransL >> tableLog) << 16) * (uint)freq;
            while (state >= maxState)
            {
                output.WriteByte((byte)(state & 0xFF));
                state >>= 8;
            }

            // Encode symbol: state = (state / freq) * tableSize + (state % freq) + start
            state = ((state / (uint)freq) << tableLog) + (state % (uint)freq) + (uint)start;
        }

        private static (byte symbol, uint newState) DecodeSymbol(uint state,
            int[] normalized, int[] cumulative, int tableLog)
        {
            // Extract slot from state
            uint slot = state & (uint)((1 << tableLog) - 1);

            // Find symbol via linear search in cumulative table
            byte symbol = 0;
            for (int s = 0; s < 256; s++)
            {
                if (cumulative[s] <= slot && slot < cumulative[s + 1])
                {
                    symbol = (byte)s;
                    break;
                }
            }

            int freq = normalized[symbol];
            int start = cumulative[symbol];

            // Decode: newState = (state >> tableLog) * freq + (slot - start)
            uint newState = (state >> tableLog) * (uint)freq + (slot - (uint)start);

            return (symbol, newState);
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
