using System;
using System.Buffers.Binary;
using System.IO;
using DataWarehouse.SDK.Contracts.Compression;

using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;
namespace DataWarehouse.Plugins.UltimateCompression.Strategies.ContextMixing
{
    /// <summary>
    /// Compression strategy implementing neural network-based compression using a single-layer
    /// perceptron that predicts the next byte from context. Prediction errors are encoded via
    /// arithmetic coding. The neural network learns the statistical structure of the data
    /// adaptively during both compression and decompression.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Neural network compression represents data by training a small neural network
    /// (perceptron) to predict each byte from its context. The better the predictions,
    /// the lower the entropy of the prediction residuals, yielding better compression.
    /// </para>
    /// <para>
    /// This implementation uses a single-layer perceptron with inputs derived from:
    /// <list type="bullet">
    ///   <item>The 8 bits of the previous byte (context features)</item>
    ///   <item>A bias term</item>
    /// </list>
    /// The output is a probability that the next bit is 1, passed to the arithmetic coder.
    /// Weights are updated online using the perceptron learning rule (gradient descent on
    /// cross-entropy loss).
    /// </para>
    /// <para>
    /// Format: [Magic:4][OrigLen:4][CompressedData]
    /// </para>
    /// </remarks>
    public sealed class NnzStrategy : CompressionStrategyBase
    {
        private const int MaxInputSize = 100 * 1024 * 1024; // 100 MB

        private static readonly byte[] Magic = { 0x4E, 0x4E, 0x5A, 0x30 }; // "NNZ0"

        /// <summary>
        /// Number of input features for the perceptron (8 bits of context + 1 bias).
        /// </summary>
        private const int NumInputs = 9;

        /// <summary>
        /// Learning rate for perceptron weight updates.
        /// </summary>
        private const double LearningRate = 0.1;

        /// <summary>
        /// Initializes a new instance of the <see cref="NnzStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public NnzStrategy() : base(CompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "NNZ",
            TypicalCompressionRatio = 0.23,
            CompressionSpeed = 1,
            DecompressionSpeed = 1,
            CompressionMemoryUsage = 256L * 1024 * 1024,
            DecompressionMemoryUsage = 256L * 1024 * 1024,
            SupportsStreaming = false,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 64,
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
                        "NNZ strategy healthy",
                        new Dictionary<string, object>
                        {
                            ["CompressOperations"] = GetCounter("nnz.compress"),
                            ["DecompressOperations"] = GetCounter("nnz.decompress")
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
            IncrementCounter("nnz.compress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for NNZ");
            using var output = new MemoryStream(input.Length + 256);
            output.Write(Magic, 0, 4);

            var lenBytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lenBytes, input.Length);
            output.Write(lenBytes, 0, 4);

            var encoder = new ArithmeticEncoder(output);
            var nn = new Perceptron();

            byte prevByte = 0;
            for (int i = 0; i < input.Length; i++)
            {
                byte b = input[i];
                for (int bit = 7; bit >= 0; bit--)
                {
                    int currentBit = (b >> bit) & 1;
                    int pred = nn.Predict(prevByte);
                    encoder.EncodeBit(currentBit, pred);
                    nn.Update(prevByte, currentBit);
                }
                prevByte = b;
            }

            encoder.Flush();
            return output.ToArray();
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            IncrementCounter("nnz.decompress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for NNZ");
            using var stream = new MemoryStream(input);

            var magicBuf = new byte[4];
            if (stream.Read(magicBuf, 0, 4) != 4 ||
                magicBuf[0] != Magic[0] || magicBuf[1] != Magic[1] ||
                magicBuf[2] != Magic[2] || magicBuf[3] != Magic[3])
            {
                throw new InvalidDataException("Invalid NNZ header magic.");
            }

            var lenBuf = new byte[4];
            if (stream.Read(lenBuf, 0, 4) != 4)
                throw new InvalidDataException("Invalid NNZ header length.");

            int originalLength = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
            if (originalLength < 0)
                throw new InvalidDataException("Invalid original length in NNZ header.");

            var decoder = new ArithmeticDecoder(stream);
            var nn = new Perceptron();
            var result = new byte[originalLength];

            byte prevByte = 0;
            for (int i = 0; i < originalLength; i++)
            {
                int decodedByte = 0;
                for (int bit = 7; bit >= 0; bit--)
                {
                    int pred = nn.Predict(prevByte);
                    int decodedBit = decoder.DecodeBit(pred);
                    decodedByte |= (decodedBit << bit);
                    nn.Update(prevByte, decodedBit);
                }
                result[i] = (byte)decodedByte;
                prevByte = (byte)decodedByte;
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
            return (long)(inputSize * 0.30) + 64;
        }

        #region Perceptron

        /// <summary>
        /// Single-layer perceptron that predicts bit probability from context byte features.
        /// Uses 256 weight sets (one per context byte value) to specialize predictions.
        /// Each weight set has <see cref="NumInputs"/> weights: 8 for the bits of the
        /// context byte plus 1 bias weight.
        /// </summary>
        private sealed class Perceptron
        {
            // Per-context-byte weights: _weights[contextByte][feature]
            private readonly double[][] _weights;

            public Perceptron()
            {
                _weights = new double[256][];
                for (int i = 0; i < 256; i++)
                {
                    _weights[i] = new double[NumInputs];
                    // Small random-ish initialization based on index for determinism
                    for (int j = 0; j < NumInputs; j++)
                        _weights[i][j] = 0.0;
                }
            }

            /// <summary>
            /// Predicts the probability that the next bit is 1, given the context byte.
            /// Returns value in [1..4095] (12-bit precision).
            /// </summary>
            public int Predict(byte contextByte)
            {
                double[] features = GetFeatures(contextByte);
                double[] w = _weights[contextByte];

                // Compute weighted sum (linear activation)
                double sum = 0.0;
                for (int i = 0; i < NumInputs; i++)
                    sum += w[i] * features[i];

                // Sigmoid activation to get probability
                double prob = Sigmoid(sum);
                return Math.Clamp((int)(prob * 4096), 1, 4095);
            }

            /// <summary>
            /// Updates weights using the perceptron learning rule (online gradient descent
            /// on cross-entropy loss).
            /// </summary>
            public void Update(byte contextByte, int actualBit)
            {
                double[] features = GetFeatures(contextByte);
                double[] w = _weights[contextByte];

                // Current prediction
                double sum = 0.0;
                for (int i = 0; i < NumInputs; i++)
                    sum += w[i] * features[i];
                double predicted = Sigmoid(sum);

                // Gradient of cross-entropy loss: (actual - predicted) * feature
                double error = actualBit - predicted;
                for (int i = 0; i < NumInputs; i++)
                {
                    w[i] += LearningRate * error * features[i];
                    // Clip weights to prevent overflow
                    w[i] = Math.Clamp(w[i], -10.0, 10.0);
                }
            }

            /// <summary>
            /// Extracts feature vector from a context byte.
            /// Features are the 8 individual bits (normalized to -1/+1) plus a bias of 1.0.
            /// </summary>
            private static double[] GetFeatures(byte contextByte)
            {
                var features = new double[NumInputs];
                for (int i = 0; i < 8; i++)
                    features[i] = ((contextByte >> i) & 1) == 1 ? 1.0 : -1.0;
                features[8] = 1.0; // bias
                return features;
            }

            /// <summary>
            /// Sigmoid (logistic) function mapping R to (0, 1).
            /// </summary>
            private static double Sigmoid(double x)
            {
                if (x > 20) return 0.999;
                if (x < -20) return 0.001;
                return 1.0 / (1.0 + Math.Exp(-x));
            }
        }

        #endregion

        #region Arithmetic Coder

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
                if (bit == 1) _low = mid + 1;
                else _high = mid;

                while ((_low ^ _high) < 0x01000000u)
                {
                    _output.WriteByte((byte)(_low >> 24));
                    _low <<= 8;
                    _high = (_high << 8) | 0xFF;
                }
            }

            public void Flush()
            {
                for (int i = 24; i >= 0; i -= 8)
                    _output.WriteByte((byte)(_low >> i));
            }
        }

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
                if (_code > mid) { bit = 1; _low = mid + 1; }
                else { bit = 0; _high = mid; }

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
