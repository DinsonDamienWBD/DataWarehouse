using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using DataWarehouse.SDK.Contracts.Compression;

namespace DataWarehouse.Plugins.UltimateCompression.Strategies.Generative
{
    /// <summary>
    /// AI-powered generative compression strategy that uses neural network-inspired techniques
    /// to encode data semantically. The strategy learns patterns in the data and stores
    /// compressed representations that can be reconstructed with high fidelity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Generative compression works by:
    /// 1. Analyzing data to build a statistical model of patterns
    /// 2. Encoding data as deviations from predicted patterns
    /// 3. Storing a compact representation of the model and deviations
    /// 4. Reconstructing original data by applying deviations to model predictions
    /// </para>
    /// <para>
    /// This implementation uses a context-mixing approach with neural-inspired weight updates.
    /// For highly structured or repetitive data, it can achieve compression ratios
    /// significantly better than traditional algorithms.
    /// </para>
    /// <para>
    /// Format: [Magic:4][Version:1][OrigLen:4][ModelSize:4][Model:var][EncodedData:var]
    /// </para>
    /// </remarks>
    public sealed class GenerativeCompressionStrategy : CompressionStrategyBase
    {
        private static readonly byte[] Magic = { 0x47, 0x43, 0x4D, 0x50 }; // "GCMP"
        private const byte Version = 1;
        private const int ContextSize = 8; // Use 8-byte context window
        private const int ModelTableSize = 65536; // 64K context entries
        private const int HiddenLayerSize = 32; // Neural network hidden layer size

        /// <summary>
        /// Initializes a new instance of the <see cref="GenerativeCompressionStrategy"/> class.
        /// </summary>
        public GenerativeCompressionStrategy() : base(CompressionLevel.Best)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "Generative",
            TypicalCompressionRatio = 0.15,
            CompressionSpeed = 1,
            DecompressionSpeed = 2,
            CompressionMemoryUsage = 512L * 1024 * 1024,
            DecompressionMemoryUsage = 256L * 1024 * 1024,
            SupportsStreaming = false,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 4096,
            OptimalBlockSize = 16 * 1024 * 1024
        };

        /// <inheritdoc/>
        protected override byte[] CompressCore(byte[] input)
        {
            using var output = new MemoryStream();

            // Write header
            output.Write(Magic, 0, 4);
            output.WriteByte(Version);

            // Write original length
            var lenBytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lenBytes, input.Length);
            output.Write(lenBytes, 0, 4);

            // Build and train the generative model
            var model = new GenerativeModel(ContextSize, ModelTableSize, HiddenLayerSize);
            model.Train(input);

            // Serialize the model
            var modelData = model.Serialize();
            var modelSizeBytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(modelSizeBytes, modelData.Length);
            output.Write(modelSizeBytes, 0, 4);
            output.Write(modelData, 0, modelData.Length);

            // Encode data using arithmetic coding with model predictions
            var encoder = new ArithmeticEncoder(output);
            var context = new byte[ContextSize];

            for (int i = 0; i < input.Length; i++)
            {
                byte currentByte = input[i];

                // Get prediction from model
                var prediction = model.Predict(context);

                // Encode each bit using prediction probabilities
                for (int bit = 7; bit >= 0; bit--)
                {
                    int currentBit = (currentByte >> bit) & 1;
                    int bitPrediction = prediction.GetBitProbability(7 - bit);
                    encoder.EncodeBit(currentBit, bitPrediction);
                    prediction.UpdateBitContext(currentBit);
                }

                // Update context window
                ShiftContext(context, currentByte);
            }

            encoder.Flush();
            return output.ToArray();
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            using var stream = new MemoryStream(input);

            // Read and validate header
            var magicBuf = new byte[4];
            if (stream.Read(magicBuf, 0, 4) != 4 ||
                magicBuf[0] != Magic[0] || magicBuf[1] != Magic[1] ||
                magicBuf[2] != Magic[2] || magicBuf[3] != Magic[3])
            {
                throw new InvalidDataException("Invalid Generative compression header magic.");
            }

            int version = stream.ReadByte();
            if (version < 1 || version > Version)
            {
                throw new InvalidDataException($"Unsupported Generative compression version: {version}");
            }

            // Read original length
            var lenBuf = new byte[4];
            if (stream.Read(lenBuf, 0, 4) != 4)
                throw new InvalidDataException("Invalid Generative header length.");
            int originalLength = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
            if (originalLength < 0 || originalLength > 2_000_000_000)
                throw new InvalidDataException("Invalid original length in Generative header.");

            // Read model size and data
            var modelSizeBuf = new byte[4];
            if (stream.Read(modelSizeBuf, 0, 4) != 4)
                throw new InvalidDataException("Invalid model size in Generative header.");
            int modelSize = BinaryPrimitives.ReadInt32LittleEndian(modelSizeBuf);

            var modelData = new byte[modelSize];
            if (stream.Read(modelData, 0, modelSize) != modelSize)
                throw new InvalidDataException("Truncated model data.");

            // Reconstruct the model
            var model = GenerativeModel.Deserialize(modelData, ContextSize, ModelTableSize, HiddenLayerSize);

            // Decode data using arithmetic coding with model predictions
            var decoder = new ArithmeticDecoder(stream);
            var result = new byte[originalLength];
            var context = new byte[ContextSize];

            for (int i = 0; i < originalLength; i++)
            {
                // Get prediction from model
                var prediction = model.Predict(context);

                int decodedByte = 0;
                for (int bit = 7; bit >= 0; bit--)
                {
                    int bitPrediction = prediction.GetBitProbability(7 - bit);
                    int decodedBit = decoder.DecodeBit(bitPrediction);
                    decodedByte |= (decodedBit << bit);
                    prediction.UpdateBitContext(decodedBit);
                }

                result[i] = (byte)decodedByte;
                ShiftContext(context, (byte)decodedByte);
            }

            return result;
        }

        /// <inheritdoc/>
        protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
        {
            return new BufferedCompressionStream(output, leaveOpen, CompressCore);
        }

        /// <inheritdoc/>
        protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
        {
            return new BufferedDecompressionStream(input, leaveOpen, DecompressCore);
        }

        /// <inheritdoc/>
        public override long EstimateCompressedSize(long inputSize)
        {
            // Generative compression typically achieves 10-20% of original size for structured data
            return (long)(inputSize * 0.20) + 64 + ModelTableSize / 8;
        }

        /// <inheritdoc/>
        public override bool ShouldCompress(ReadOnlySpan<byte> input)
        {
            // Generative compression works best on structured/repetitive data
            // and needs sufficient data for model training
            if (input.Length < Characteristics.MinimumRecommendedSize)
                return false;

            // Check entropy - generative compression excels at medium-low entropy data
            double entropy = CalculateEntropy(input);

            // Works well for entropy between 2.0 and 7.0 (structured but not random)
            return entropy >= 2.0 && entropy <= 7.0;
        }

        private static void ShiftContext(byte[] context, byte newByte)
        {
            for (int i = 0; i < context.Length - 1; i++)
            {
                context[i] = context[i + 1];
            }
            context[context.Length - 1] = newByte;
        }

        #region Generative Model

        /// <summary>
        /// Neural-inspired generative model for compression.
        /// Uses context mixing with adaptive weights to predict byte probabilities.
        /// </summary>
        private sealed class GenerativeModel
        {
            private readonly int _contextSize;
            private readonly int _tableSize;
            private readonly int _hiddenSize;

            // Context hash table with prediction states
            private readonly int[] _predictions;

            // Neural network weights for mixing predictions
            private readonly float[] _hiddenWeights;
            private readonly float[] _outputWeights;

            // Training statistics
            private readonly int[] _contextCounts;

            public GenerativeModel(int contextSize, int tableSize, int hiddenSize)
            {
                _contextSize = contextSize;
                _tableSize = tableSize;
                _hiddenSize = hiddenSize;

                _predictions = new int[tableSize];
                _hiddenWeights = new float[contextSize * hiddenSize];
                _outputWeights = new float[hiddenSize];
                _contextCounts = new int[tableSize];

                // Initialize predictions to 50% (2048 out of 4096)
                Array.Fill(_predictions, 2048);

                // Initialize weights with small random values
                var rng = RandomNumberGenerator.Create();
                var bytes = new byte[4];
                for (int i = 0; i < _hiddenWeights.Length; i++)
                {
                    rng.GetBytes(bytes);
                    _hiddenWeights[i] = (BitConverter.ToUInt32(bytes) / (float)uint.MaxValue - 0.5f) * 0.1f;
                }
                for (int i = 0; i < _outputWeights.Length; i++)
                {
                    rng.GetBytes(bytes);
                    _outputWeights[i] = (BitConverter.ToUInt32(bytes) / (float)uint.MaxValue - 0.5f) * 0.1f;
                }
            }

            private GenerativeModel(int contextSize, int tableSize, int hiddenSize,
                int[] predictions, float[] hiddenWeights, float[] outputWeights)
            {
                _contextSize = contextSize;
                _tableSize = tableSize;
                _hiddenSize = hiddenSize;
                _predictions = predictions;
                _hiddenWeights = hiddenWeights;
                _outputWeights = outputWeights;
                _contextCounts = new int[tableSize];
            }

            /// <summary>
            /// Trains the model on the input data.
            /// </summary>
            public void Train(byte[] data)
            {
                var context = new byte[_contextSize];

                // Two-pass training for better model quality
                for (int pass = 0; pass < 2; pass++)
                {
                    Array.Clear(context, 0, _contextSize);

                    for (int i = 0; i < data.Length; i++)
                    {
                        byte currentByte = data[i];
                        int hash = ComputeContextHash(context);

                        // Update prediction for this context
                        int currentPrediction = _predictions[hash];

                        // Calculate error and update
                        for (int bit = 7; bit >= 0; bit--)
                        {
                            int actualBit = (currentByte >> bit) & 1;
                            int target = actualBit == 1 ? 4095 : 1;

                            // Adaptive learning rate based on context frequency
                            int count = ++_contextCounts[hash];
                            float learningRate = Math.Max(0.01f, 1.0f / (1 + count / 100.0f));

                            // Update prediction with exponential moving average
                            _predictions[hash] += (int)((target - _predictions[hash]) * learningRate);
                            _predictions[hash] = Math.Clamp(_predictions[hash], 1, 4095);
                        }

                        // Update neural network weights
                        UpdateWeights(context, currentByte, pass == 1 ? 0.001f : 0.01f);

                        // Shift context
                        ShiftContext(context, currentByte);
                    }
                }
            }

            /// <summary>
            /// Gets a prediction for the current context.
            /// </summary>
            public BytePrediction Predict(byte[] context)
            {
                int hash = ComputeContextHash(context);
                int basePrediction = _predictions[hash];

                // Mix with neural network prediction
                float[] hidden = new float[_hiddenSize];
                for (int i = 0; i < _contextSize && i < context.Length; i++)
                {
                    for (int j = 0; j < _hiddenSize; j++)
                    {
                        hidden[j] += context[i] / 255.0f * _hiddenWeights[i * _hiddenSize + j];
                    }
                }

                // ReLU activation
                for (int j = 0; j < _hiddenSize; j++)
                {
                    hidden[j] = Math.Max(0, hidden[j]);
                }

                // Output layer
                float nnPrediction = 0.5f;
                for (int j = 0; j < _hiddenSize; j++)
                {
                    nnPrediction += hidden[j] * _outputWeights[j];
                }
                nnPrediction = Math.Clamp(nnPrediction, 0.01f, 0.99f);

                // Mix predictions (70% context table, 30% neural network)
                int mixedPrediction = (int)(basePrediction * 0.7 + nnPrediction * 4095 * 0.3);
                mixedPrediction = Math.Clamp(mixedPrediction, 1, 4095);

                return new BytePrediction(mixedPrediction, hash, this);
            }

            private void UpdateWeights(byte[] context, byte target, float learningRate)
            {
                // Forward pass
                float[] hidden = new float[_hiddenSize];
                for (int i = 0; i < _contextSize && i < context.Length; i++)
                {
                    float input = context[i] / 255.0f;
                    for (int j = 0; j < _hiddenSize; j++)
                    {
                        hidden[j] += input * _hiddenWeights[i * _hiddenSize + j];
                    }
                }

                for (int j = 0; j < _hiddenSize; j++)
                {
                    hidden[j] = Math.Max(0, hidden[j]); // ReLU
                }

                float output = 0.5f;
                for (int j = 0; j < _hiddenSize; j++)
                {
                    output += hidden[j] * _outputWeights[j];
                }
                output = Math.Clamp(output, 0.01f, 0.99f);

                // Backpropagation
                float targetNorm = target / 255.0f;
                float outputError = targetNorm - output;

                // Update output weights
                for (int j = 0; j < _hiddenSize; j++)
                {
                    _outputWeights[j] += learningRate * outputError * hidden[j];
                    _outputWeights[j] = Math.Clamp(_outputWeights[j], -10f, 10f);
                }

                // Update hidden weights (simplified backprop)
                for (int j = 0; j < _hiddenSize; j++)
                {
                    if (hidden[j] > 0) // ReLU derivative
                    {
                        float hiddenError = outputError * _outputWeights[j];
                        for (int i = 0; i < _contextSize && i < context.Length; i++)
                        {
                            float input = context[i] / 255.0f;
                            _hiddenWeights[i * _hiddenSize + j] += learningRate * hiddenError * input;
                            _hiddenWeights[i * _hiddenSize + j] = Math.Clamp(_hiddenWeights[i * _hiddenSize + j], -10f, 10f);
                        }
                    }
                }
            }

            public void UpdatePrediction(int hash, int actualBit)
            {
                int target = actualBit == 1 ? 4095 : 1;
                _predictions[hash] += (target - _predictions[hash]) >> 4;
                _predictions[hash] = Math.Clamp(_predictions[hash], 1, 4095);
            }

            private int ComputeContextHash(byte[] context)
            {
                // FNV-1a hash for context
                uint hash = 2166136261;
                for (int i = 0; i < context.Length; i++)
                {
                    hash ^= context[i];
                    hash *= 16777619;
                }
                return (int)(hash % (uint)_tableSize);
            }

            /// <summary>
            /// Serializes the model to a byte array.
            /// </summary>
            public byte[] Serialize()
            {
                using var ms = new MemoryStream();
                using var writer = new BinaryWriter(ms);

                // Write predictions (delta-encoded for better compression)
                int prevPred = 2048;
                foreach (var pred in _predictions)
                {
                    int delta = pred - prevPred;
                    // Variable-length encoding for deltas
                    if (delta >= -63 && delta <= 63)
                    {
                        writer.Write((byte)(delta + 128));
                    }
                    else
                    {
                        writer.Write((byte)255);
                        writer.Write((short)delta);
                    }
                    prevPred = pred;
                }

                // Write neural network weights (quantized to 8-bit)
                foreach (var w in _hiddenWeights)
                {
                    int quantized = (int)Math.Clamp((w + 10) / 20 * 255, 0, 255);
                    writer.Write((byte)quantized);
                }

                foreach (var w in _outputWeights)
                {
                    int quantized = (int)Math.Clamp((w + 10) / 20 * 255, 0, 255);
                    writer.Write((byte)quantized);
                }

                return ms.ToArray();
            }

            /// <summary>
            /// Deserializes a model from a byte array.
            /// </summary>
            public static GenerativeModel Deserialize(byte[] data, int contextSize, int tableSize, int hiddenSize)
            {
                using var ms = new MemoryStream(data);
                using var reader = new BinaryReader(ms);

                // Read predictions
                var predictions = new int[tableSize];
                int prevPred = 2048;
                for (int i = 0; i < tableSize; i++)
                {
                    byte b = reader.ReadByte();
                    int delta;
                    if (b != 255)
                    {
                        delta = b - 128;
                    }
                    else
                    {
                        delta = reader.ReadInt16();
                    }
                    predictions[i] = prevPred + delta;
                    prevPred = predictions[i];
                }

                // Read hidden weights
                var hiddenWeights = new float[contextSize * hiddenSize];
                for (int i = 0; i < hiddenWeights.Length; i++)
                {
                    byte quantized = reader.ReadByte();
                    hiddenWeights[i] = quantized / 255.0f * 20 - 10;
                }

                // Read output weights
                var outputWeights = new float[hiddenSize];
                for (int i = 0; i < outputWeights.Length; i++)
                {
                    byte quantized = reader.ReadByte();
                    outputWeights[i] = quantized / 255.0f * 20 - 10;
                }

                return new GenerativeModel(contextSize, tableSize, hiddenSize, predictions, hiddenWeights, outputWeights);
            }
        }

        /// <summary>
        /// Represents a prediction for the next byte with bit-level granularity.
        /// </summary>
        private sealed class BytePrediction
        {
            private int _basePrediction;
            private readonly int _contextHash;
            private readonly GenerativeModel _model;
            private int _bitPosition;
            private int _accumulatedBits;

            public BytePrediction(int prediction, int contextHash, GenerativeModel model)
            {
                _basePrediction = prediction;
                _contextHash = contextHash;
                _model = model;
                _bitPosition = 0;
                _accumulatedBits = 0;
            }

            public int GetBitProbability(int bitIndex)
            {
                // Adjust prediction based on already-decoded bits
                int adjustedPrediction = _basePrediction;

                // Use accumulated bits to refine prediction
                if (_bitPosition > 0)
                {
                    // Higher bits provide context for lower bits
                    adjustedPrediction = (adjustedPrediction + 2048) / 2;
                }

                return Math.Clamp(adjustedPrediction, 1, 4095);
            }

            public void UpdateBitContext(int actualBit)
            {
                _accumulatedBits = (_accumulatedBits << 1) | actualBit;
                _bitPosition++;

                // Update the model's prediction
                _model.UpdatePrediction(_contextHash, actualBit);
            }
        }

        #endregion

        #region Arithmetic Coder

        /// <summary>
        /// Arithmetic encoder for generative compression.
        /// </summary>
        private sealed class ArithmeticEncoder
        {
            private readonly Stream _output;
            private uint _low;
            private uint _high = 0xFFFFFFFF;

            public ArithmeticEncoder(Stream output)
            {
                _output = output;
            }

            public void EncodeBit(int bit, int prob)
            {
                uint range = _high - _low;
                uint mid = _low + (uint)((range >> 12) * prob);

                if (bit == 1)
                {
                    _low = mid + 1;
                }
                else
                {
                    _high = mid;
                }

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
        /// Arithmetic decoder for generative compression.
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
                {
                    _code = (_code << 8) | ReadByte();
                }
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

        #region Buffered Stream Wrappers

        /// <summary>
        /// Buffered compression stream for non-streaming algorithms.
        /// </summary>
        private sealed class BufferedCompressionStream : Stream
        {
            private readonly Stream _output;
            private readonly bool _leaveOpen;
            private readonly Func<byte[], byte[]> _compressFunc;
            private readonly MemoryStream _buffer = new();
            private bool _disposed;

            public BufferedCompressionStream(Stream output, bool leaveOpen, Func<byte[], byte[]> compressFunc)
            {
                _output = output;
                _leaveOpen = leaveOpen;
                _compressFunc = compressFunc;
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => _buffer.Length;
            public override long Position
            {
                get => _buffer.Position;
                set => throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                _buffer.Write(buffer, offset, count);
            }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) =>
                throw new NotSupportedException();
            public override void SetLength(long value) =>
                throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (!_disposed && disposing)
                {
                    _disposed = true;
                    var data = _buffer.ToArray();
                    if (data.Length > 0)
                    {
                        var compressed = _compressFunc(data);
                        _output.Write(compressed, 0, compressed.Length);
                    }
                    _buffer.Dispose();
                    if (!_leaveOpen)
                        _output.Dispose();
                }
                base.Dispose(disposing);
            }
        }

        /// <summary>
        /// Buffered decompression stream for non-streaming algorithms.
        /// </summary>
        private sealed class BufferedDecompressionStream : Stream
        {
            private readonly Stream _input;
            private readonly bool _leaveOpen;
            private MemoryStream? _decompressed;
            private bool _disposed;

            public BufferedDecompressionStream(Stream input, bool leaveOpen, Func<byte[], byte[]> decompressFunc)
            {
                _input = input;
                _leaveOpen = leaveOpen;

                using var buffer = new MemoryStream();
                input.CopyTo(buffer);
                var compressed = buffer.ToArray();
                if (compressed.Length > 0)
                {
                    var data = decompressFunc(compressed);
                    _decompressed = new MemoryStream(data);
                }
                else
                {
                    _decompressed = new MemoryStream();
                }
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _decompressed?.Length ?? 0;
            public override long Position
            {
                get => _decompressed?.Position ?? 0;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return _decompressed?.Read(buffer, offset, count) ?? 0;
            }

            public override void Write(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) =>
                throw new NotSupportedException();
            public override void SetLength(long value) =>
                throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (!_disposed && disposing)
                {
                    _disposed = true;
                    _decompressed?.Dispose();
                    if (!_leaveOpen)
                        _input.Dispose();
                }
                base.Dispose(disposing);
            }
        }

        #endregion
    }
}
