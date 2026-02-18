using System;
using System.Buffers.Binary;
using System.IO;
using DataWarehouse.SDK.Contracts.Compression;
using SdkCompressionLevel = DataWarehouse.SDK.Contracts.Compression.CompressionLevel;

namespace DataWarehouse.Plugins.UltimateCompression.Strategies.EntropyCoding
{
    /// <summary>
    /// Compression strategy implementing Arithmetic Coding with adaptive order-0 model.
    /// Encodes the entire input as a single fractional number within an interval that
    /// is recursively subdivided based on symbol probabilities.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Arithmetic coding (1976) achieves near-optimal entropy coding by representing the entire
    /// message as a single number in [0,1). Each symbol narrows the interval proportionally to
    /// its probability. This avoids the inefficiency of Huffman coding when symbol probabilities
    /// don't align with powers of 1/2.
    /// </para>
    /// <para>
    /// This implementation uses an adaptive order-0 model: symbol frequencies are updated after
    /// each symbol is encoded/decoded. The 256-symbol frequency table starts uniform and adapts
    /// to the data stream. Interval arithmetic is performed with 32-bit unsigned integers and
    /// carry propagation ensures numerical stability.
    /// </para>
    /// <para>
    /// Format: [Magic:4][OrigLen:4][CompressedData]
    /// </para>
    /// </remarks>
    public sealed class ArithmeticStrategy : CompressionStrategyBase
    {
        private static readonly byte[] Magic = { 0x41, 0x52, 0x49, 0x54 }; // "ARIT"
        private const int InitialFrequency = 1;
        private const int MaxFrequency = 16383; // Keep total under 2^14 for precision

        /// <summary>
        /// Initializes a new instance of the <see cref="ArithmeticStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public ArithmeticStrategy() : base(SdkCompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "Arithmetic",
            TypicalCompressionRatio = 0.55,
            CompressionSpeed = 4,
            DecompressionSpeed = 4,
            CompressionMemoryUsage = 8L * 1024 * 1024,
            DecompressionMemoryUsage = 8L * 1024 * 1024,
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

            var encoder = new ArithmeticEncoder(output);
            var model = new AdaptiveModel();

            for (int i = 0; i < input.Length; i++)
            {
                byte symbol = input[i];
                var (cumLow, cumHigh, total) = model.GetRange(symbol);
                encoder.Encode(cumLow, cumHigh, total);
                model.Update(symbol);
            }

            encoder.Flush();
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
                throw new InvalidDataException("Invalid Arithmetic header magic.");
            }

            var lenBuf = new byte[4];
            if (stream.Read(lenBuf, 0, 4) != 4)
                throw new InvalidDataException("Invalid Arithmetic header length.");

            int originalLength = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
            if (originalLength < 0)
                throw new InvalidDataException("Invalid original length in Arithmetic header.");

            if (originalLength == 0)
                return Array.Empty<byte>();

            var decoder = new ArithmeticDecoder(stream);
            var model = new AdaptiveModel();
            var result = new byte[originalLength];

            for (int i = 0; i < originalLength; i++)
            {
                int target = decoder.GetTarget(model.TotalFrequency);
                byte symbol = model.FindSymbol(target);
                var (cumLow, cumHigh, total) = model.GetRange(symbol);
                decoder.Decode(cumLow, cumHigh, total);
                model.Update(symbol);
                result[i] = symbol;
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
            return (long)(inputSize * 0.60) + 128;
        }

        #region Adaptive Model

        /// <summary>
        /// Adaptive order-0 frequency model for arithmetic coding.
        /// Maintains cumulative frequency table for all 256 symbols.
        /// </summary>
        private sealed class AdaptiveModel
        {
            private readonly int[] _frequencies = new int[256];
            private readonly int[] _cumulativeFreq = new int[257];

            public int TotalFrequency { get; private set; }

            public AdaptiveModel()
            {
                // Initialize with uniform distribution
                for (int i = 0; i < 256; i++)
                    _frequencies[i] = InitialFrequency;

                TotalFrequency = 256 * InitialFrequency;
                UpdateCumulativeTable();
            }

            /// <summary>
            /// Gets the cumulative frequency range for a symbol.
            /// Returns (cumLow, cumHigh, total).
            /// </summary>
            public (int cumLow, int cumHigh, int total) GetRange(byte symbol)
            {
                int cumLow = _cumulativeFreq[symbol];
                int cumHigh = _cumulativeFreq[symbol + 1];
                return (cumLow, cumHigh, TotalFrequency);
            }

            /// <summary>
            /// Finds the symbol corresponding to a cumulative frequency target.
            /// </summary>
            public byte FindSymbol(int target)
            {
                // Binary search in cumulative frequency table
                int low = 0, high = 255;
                while (low <= high)
                {
                    int mid = (low + high) / 2;
                    if (_cumulativeFreq[mid + 1] <= target)
                        low = mid + 1;
                    else if (_cumulativeFreq[mid] > target)
                        high = mid - 1;
                    else
                        return (byte)mid;
                }
                return 0;
            }

            /// <summary>
            /// Updates the model after encoding/decoding a symbol.
            /// </summary>
            public void Update(byte symbol)
            {
                _frequencies[symbol]++;
                TotalFrequency++;

                // Rescale if total gets too large
                if (TotalFrequency > MaxFrequency)
                {
                    Rescale();
                }

                UpdateCumulativeTable();
            }

            private void Rescale()
            {
                TotalFrequency = 0;
                for (int i = 0; i < 256; i++)
                {
                    _frequencies[i] = (_frequencies[i] + 1) / 2;
                    TotalFrequency += _frequencies[i];
                }
            }

            private void UpdateCumulativeTable()
            {
                _cumulativeFreq[0] = 0;
                for (int i = 0; i < 256; i++)
                {
                    _cumulativeFreq[i + 1] = _cumulativeFreq[i] + _frequencies[i];
                }
            }
        }

        #endregion

        #region Arithmetic Encoder/Decoder

        /// <summary>
        /// Arithmetic encoder using interval subdivision and carry propagation.
        /// </summary>
        private sealed class ArithmeticEncoder
        {
            private readonly Stream _output;
            private uint _low;
            private uint _high = 0xFFFFFFFF;
            private int _underflowBits;

            public ArithmeticEncoder(Stream output) => _output = output;

            public void Encode(int cumLow, int cumHigh, int total)
            {
                // Narrow the interval [_low, _high] proportionally
                uint range = _high - _low + 1;
                _high = _low + (uint)((range * cumHigh) / total) - 1;
                _low = _low + (uint)((range * cumLow) / total);

                // Normalize: output matching leading bits
                while (true)
                {
                    if (_high < 0x80000000u)
                    {
                        // Both in lower half: output 0
                        OutputBit(0);
                    }
                    else if (_low >= 0x80000000u)
                    {
                        // Both in upper half: output 1
                        OutputBit(1);
                        _low -= 0x80000000u;
                        _high -= 0x80000000u;
                    }
                    else if (_low >= 0x40000000u && _high < 0xC0000000u)
                    {
                        // Underflow: interval in middle
                        _underflowBits++;
                        _low -= 0x40000000u;
                        _high -= 0x40000000u;
                    }
                    else
                    {
                        break;
                    }

                    _low <<= 1;
                    _high = (_high << 1) | 1;
                }
            }

            public void Flush()
            {
                // Output final bits
                _underflowBits++;
                if (_low < 0x40000000u)
                    OutputBit(0);
                else
                    OutputBit(1);
            }

            private void OutputBit(int bit)
            {
                OutputBitRaw(bit);
                while (_underflowBits > 0)
                {
                    OutputBitRaw(1 - bit);
                    _underflowBits--;
                }
            }

            private int _bitBuffer;
            private int _bitsInBuffer;

            private void OutputBitRaw(int bit)
            {
                _bitBuffer = (_bitBuffer << 1) | bit;
                _bitsInBuffer++;

                if (_bitsInBuffer == 8)
                {
                    _output.WriteByte((byte)_bitBuffer);
                    _bitBuffer = 0;
                    _bitsInBuffer = 0;
                }
            }
        }

        /// <summary>
        /// Arithmetic decoder using interval subdivision.
        /// </summary>
        private sealed class ArithmeticDecoder
        {
            private readonly Stream _input;
            private uint _low;
            private uint _high = 0xFFFFFFFF;
            private uint _value;
            private int _bitBuffer;
            private int _bitsInBuffer;

            public ArithmeticDecoder(Stream input)
            {
                _input = input;

                // Read initial 32 bits into value
                for (int i = 0; i < 32; i++)
                {
                    _value = (_value << 1) | (uint)ReadBit();
                }
            }

            public int GetTarget(int total)
            {
                uint range = _high - _low + 1;
                return (int)(((ulong)(_value - _low + 1) * (ulong)total - 1) / range);
            }

            public void Decode(int cumLow, int cumHigh, int total)
            {
                uint range = _high - _low + 1;
                _high = _low + (uint)((range * cumHigh) / total) - 1;
                _low = _low + (uint)((range * cumLow) / total);

                // Normalize
                while (true)
                {
                    if (_high < 0x80000000u)
                    {
                        // Do nothing
                    }
                    else if (_low >= 0x80000000u)
                    {
                        _value -= 0x80000000u;
                        _low -= 0x80000000u;
                        _high -= 0x80000000u;
                    }
                    else if (_low >= 0x40000000u && _high < 0xC0000000u)
                    {
                        _value -= 0x40000000u;
                        _low -= 0x40000000u;
                        _high -= 0x40000000u;
                    }
                    else
                    {
                        break;
                    }

                    _low <<= 1;
                    _high = (_high << 1) | 1;
                    _value = (_value << 1) | (uint)ReadBit();
                }
            }

            private int ReadBit()
            {
                if (_bitsInBuffer == 0)
                {
                    int b = _input.ReadByte();
                    if (b < 0) return 0;
                    _bitBuffer = b;
                    _bitsInBuffer = 8;
                }

                _bitsInBuffer--;
                return (_bitBuffer >> _bitsInBuffer) & 1;
            }
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
