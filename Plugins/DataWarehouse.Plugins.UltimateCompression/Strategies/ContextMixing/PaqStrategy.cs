using System;
using System.Buffers.Binary;
using System.IO;
using DataWarehouse.SDK.Contracts.Compression;

namespace DataWarehouse.Plugins.UltimateCompression.Strategies.ContextMixing
{
    /// <summary>
    /// Compression strategy implementing a PAQ8-style multi-context model with logistic
    /// mixing of predictions and an arithmetic coder backend. PAQ is renowned for achieving
    /// some of the highest compression ratios among general-purpose compressors.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PAQ was developed by Matt Mahoney. PAQ8 variants use multiple context models of
    /// different orders (0 through 4+), combine their predictions using logistic mixing
    /// (a form of neural network), and encode the result with arithmetic coding.
    /// </para>
    /// <para>
    /// This implementation uses:
    /// <list type="bullet">
    ///   <item>Order-0 model: no context, just byte frequency</item>
    ///   <item>Order-1 model: 1-byte context</item>
    ///   <item>Order-2 model: 2-byte context (hashed to 16-bit)</item>
    ///   <item>Order-3 model: 3-byte context (hashed to 16-bit)</item>
    ///   <item>Order-4 model: 4-byte context (hashed to 16-bit)</item>
    /// </list>
    /// Predictions are combined via logistic (sigmoid) mixing with adaptive weights.
    /// </para>
    /// <para>
    /// Format: [Magic:4][OrigLen:4][CompressedData]
    /// </para>
    /// </remarks>
    public sealed class PaqStrategy : CompressionStrategyBase
    {
        private static readonly byte[] Magic = { 0x50, 0x41, 0x51, 0x38 }; // "PAQ8"
        private const int NumModels = 5;
        private const int TableSize = 65536; // 2^16 entries per model

        /// <summary>
        /// Initializes a new instance of the <see cref="PaqStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public PaqStrategy() : base(CompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "PAQ8",
            TypicalCompressionRatio = 0.20,
            CompressionSpeed = 1,
            DecompressionSpeed = 1,
            CompressionMemoryUsage = 512L * 1024 * 1024,
            DecompressionMemoryUsage = 512L * 1024 * 1024,
            SupportsStreaming = false,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 64,
            OptimalBlockSize = 1024 * 1024
        };

        /// <inheritdoc/>
        protected override byte[] CompressCore(byte[] input)
        {
            using var output = new MemoryStream(input.Length + 256);
            output.Write(Magic, 0, 4);

            var lenBytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lenBytes, input.Length);
            output.Write(lenBytes, 0, 4);

            var encoder = new ArithmeticEncoder(output);
            var mixer = new LogisticMixer(NumModels, TableSize);

            for (int i = 0; i < input.Length; i++)
            {
                byte b = input[i];
                for (int bit = 7; bit >= 0; bit--)
                {
                    int currentBit = (b >> bit) & 1;
                    int mixedPred = mixer.Predict(input, i);
                    encoder.EncodeBit(currentBit, mixedPred);
                    mixer.Update(input, i, currentBit);
                }
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
                throw new InvalidDataException("Invalid PAQ8 header magic.");
            }

            var lenBuf = new byte[4];
            if (stream.Read(lenBuf, 0, 4) != 4)
                throw new InvalidDataException("Invalid PAQ8 header length.");

            int originalLength = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
            if (originalLength < 0)
                throw new InvalidDataException("Invalid original length in PAQ8 header.");

            var decoder = new ArithmeticDecoder(stream);
            var mixer = new LogisticMixer(NumModels, TableSize);
            var result = new byte[originalLength];

            for (int i = 0; i < originalLength; i++)
            {
                int decodedByte = 0;
                for (int bit = 7; bit >= 0; bit--)
                {
                    int mixedPred = mixer.Predict(result, i);
                    int decodedBit = decoder.DecodeBit(mixedPred);
                    decodedByte |= (decodedBit << bit);
                    mixer.Update(result, i, decodedBit);
                }
                result[i] = (byte)decodedByte;
            }

            return result;
        }

        /// <inheritdoc/>
        protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
        {
            return new BufferedTransformStream(output, leaveOpen, CompressCore, isCompression: true);
        }

        /// <inheritdoc/>
        protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
        {
            return new BufferedTransformStream(input, leaveOpen, DecompressCore, isCompression: false);
        }

        /// <inheritdoc/>
        public override long EstimateCompressedSize(long inputSize)
        {
            return (long)(inputSize * 0.28) + 64;
        }

        #region Logistic Mixer

        /// <summary>
        /// Logistic (sigmoid) mixer combining predictions from multiple context models.
        /// Each model provides a probability, which is transformed through the logistic
        /// function (stretch), weighted, summed, and passed through the inverse logistic
        /// (squash) to produce the final prediction.
        /// </summary>
        private sealed class LogisticMixer
        {
            private readonly int[][] _models;  // [modelIndex][contextHash] -> prediction [1..4095]
            private readonly double[] _weights; // mixing weights per model
            private readonly int _tableSize;
            private readonly int _numModels;
            private int _lastPrediction;
            private readonly int[] _lastContextHashes;

            public LogisticMixer(int numModels, int tableSize)
            {
                _numModels = numModels;
                _tableSize = tableSize;
                _models = new int[numModels][];
                _weights = new double[numModels];
                _lastContextHashes = new int[numModels];

                for (int m = 0; m < numModels; m++)
                {
                    _models[m] = new int[tableSize];
                    Array.Fill(_models[m], 2048); // 50% initial prediction
                    _weights[m] = 1.0 / numModels;
                }
            }

            /// <summary>
            /// Computes a mixed probability prediction from all context models.
            /// Returns a value in [1..4095] (12-bit probability that next bit is 1).
            /// </summary>
            public int Predict(byte[] data, int position)
            {
                double logitSum = 0.0;

                for (int m = 0; m < _numModels; m++)
                {
                    int hash = ComputeContextHash(data, position, m);
                    _lastContextHashes[m] = hash;
                    int pred = _models[m][hash];
                    double p = pred / 4096.0;
                    p = Math.Clamp(p, 0.001, 0.999);
                    double logit = Math.Log(p / (1.0 - p)); // stretch
                    logitSum += _weights[m] * logit;
                }

                // squash back to probability
                double mixed = 1.0 / (1.0 + Math.Exp(-logitSum));
                _lastPrediction = Math.Clamp((int)(mixed * 4096), 1, 4095);
                return _lastPrediction;
            }

            /// <summary>
            /// Updates all models and mixing weights after observing a bit.
            /// </summary>
            public void Update(byte[] data, int position, int bit)
            {
                for (int m = 0; m < _numModels; m++)
                {
                    int hash = _lastContextHashes[m];
                    int pred = _models[m][hash];
                    int target = bit == 1 ? 4095 : 1;
                    int error = target - pred;
                    _models[m][hash] = Math.Clamp(pred + (error >> 4), 1, 4095);

                    // Update weight: increase weight if model was accurate
                    double p = pred / 4096.0;
                    p = Math.Clamp(p, 0.001, 0.999);
                    double modelError = bit - p;
                    _weights[m] += 0.01 * modelError * Math.Log(p / (1.0 - p));
                }

                // Normalize weights
                double sum = 0;
                for (int m = 0; m < _numModels; m++)
                {
                    _weights[m] = Math.Max(_weights[m], 0.01);
                    sum += _weights[m];
                }
                for (int m = 0; m < _numModels; m++)
                    _weights[m] /= sum;
            }

            /// <summary>
            /// Computes a context hash for the given model order.
            /// Order 0 = no context, order 1 = 1 byte, etc.
            /// </summary>
            private int ComputeContextHash(byte[] data, int position, int order)
            {
                if (order == 0 || position == 0)
                    return 0;

                uint hash = 2166136261u; // FNV offset basis
                int start = Math.Max(0, position - order);
                for (int i = start; i < position; i++)
                {
                    hash ^= data[i];
                    hash *= 16777619u; // FNV prime
                }
                return (int)(hash & (_tableSize - 1));
            }
        }

        #endregion

        #region Arithmetic Coder

        /// <summary>
        /// Arithmetic encoder using 32-bit precision.
        /// </summary>
        private sealed class ArithmeticEncoder
        {
            private readonly Stream _output;
            private uint _low;
            private uint _high = 0xFFFFFFFF;

            public ArithmeticEncoder(Stream output) => _output = output;

            public void EncodeBit(int bit, int prob)
            {
                uint range = _high - _low;
                uint mid = _low + (uint)((range >> 12) * prob);

                if (bit == 1)
                    _low = mid + 1;
                else
                    _high = mid;

                while ((_low ^ _high) < 0x01000000u)
                {
                    _output.WriteByte((byte)(_low >> 24));
                    _low <<= 8;
                    _high = (_high << 8) | 0xFF;
                }
            }

            public void Flush()
            {
                _output.WriteByte((byte)(_low >> 24));
                _output.WriteByte((byte)(_low >> 16));
                _output.WriteByte((byte)(_low >> 8));
                _output.WriteByte((byte)_low);
            }
        }

        /// <summary>
        /// Arithmetic decoder using 32-bit precision.
        /// </summary>
        private sealed class ArithmeticDecoder
        {
            private readonly Stream _input;
            private uint _low;
            private uint _high = 0xFFFFFFFF;
            private uint _code;

            public ArithmeticDecoder(Stream input)
            {
                _input = input;
                for (int i = 0; i < 4; i++)
                    _code = (_code << 8) | ReadByte();
            }

            public int DecodeBit(int prob)
            {
                uint range = _high - _low;
                uint mid = _low + (uint)((range >> 12) * prob);

                int bit;
                if (_code > mid)
                {
                    bit = 1;
                    _low = mid + 1;
                }
                else
                {
                    bit = 0;
                    _high = mid;
                }

                while ((_low ^ _high) < 0x01000000u)
                {
                    _low <<= 8;
                    _high = (_high << 8) | 0xFF;
                    _code = (_code << 8) | ReadByte();
                }

                return bit;
            }

            private uint ReadByte()
            {
                int b = _input.ReadByte();
                return (uint)(b < 0 ? 0 : b);
            }
        }

        #endregion

        #region Buffered Stream Wrapper

        /// <summary>
        /// General-purpose buffered stream for algorithms without native streaming support.
        /// In compression mode, buffers writes and compresses on dispose.
        /// In decompression mode, reads all data, decompresses, and provides buffered reads.
        /// </summary>
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
                {
                    _buffer = new MemoryStream(4096);
                }
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
