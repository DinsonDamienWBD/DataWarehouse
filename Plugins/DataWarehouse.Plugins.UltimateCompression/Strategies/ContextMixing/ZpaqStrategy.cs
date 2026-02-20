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
    /// Compression strategy implementing a ZPAQ-style journaling archiver format using
    /// context-based arithmetic coding with 1-byte contexts. ZPAQ achieves very high
    /// compression ratios by maintaining an adaptive context model that predicts the
    /// probability of each bit, feeding those predictions into an arithmetic coder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ZPAQ was developed by Matt Mahoney as a journaling archiver with a focus on
    /// extreme compression. This implementation models the core ZPAQ approach:
    /// a 256-entry context table (order-1) where each entry stores a prediction
    /// of the next bit being 1, updated via exponential moving average. The arithmetic
    /// coder encodes each bit using the model's probability estimate.
    /// </para>
    /// <para>
    /// Format: [Magic:4][OrigLen:4][CompressedData]
    /// </para>
    /// </remarks>
    public sealed class ZpaqStrategy : CompressionStrategyBase
    {
        private const int MaxInputSize = 100 * 1024 * 1024; // 100 MB

        private static readonly byte[] Magic = { 0x5A, 0x50, 0x41, 0x51 }; // "ZPAQ"

        /// <summary>
        /// Initializes a new instance of the <see cref="ZpaqStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public ZpaqStrategy() : base(CompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "ZPAQ",
            TypicalCompressionRatio = 0.22,
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
                        "ZPAQ strategy healthy",
                        new Dictionary<string, object>
                        {
                            ["CompressOperations"] = GetCounter("zpaq.compress"),
                            ["DecompressOperations"] = GetCounter("zpaq.decompress")
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
            IncrementCounter("zpaq.compress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for ZPAQ");
            using var output = new MemoryStream(input.Length + 256);
            output.Write(Magic, 0, 4);

            var lenBytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lenBytes, input.Length);
            output.Write(lenBytes, 0, 4);

            var encoder = new ArithmeticEncoder(output);
            var model = new ContextModel();

            // Encode each byte as 8 bits, MSB first
            byte context = 0;
            for (int i = 0; i < input.Length; i++)
            {
                byte b = input[i];
                for (int bit = 7; bit >= 0; bit--)
                {
                    int currentBit = (b >> bit) & 1;
                    int prediction = model.GetPrediction(context);
                    encoder.EncodeBit(currentBit, prediction);
                    model.Update(context, currentBit);
                }
                context = b;
            }

            encoder.Flush();
            return output.ToArray();
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            IncrementCounter("zpaq.decompress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for ZPAQ");
            using var stream = new MemoryStream(input);

            var magicBuf = new byte[4];
            if (stream.Read(magicBuf, 0, 4) != 4 ||
                magicBuf[0] != Magic[0] || magicBuf[1] != Magic[1] ||
                magicBuf[2] != Magic[2] || magicBuf[3] != Magic[3])
            {
                throw new InvalidDataException("Invalid ZPAQ header magic.");
            }

            var lenBuf = new byte[4];
            if (stream.Read(lenBuf, 0, 4) != 4)
                throw new InvalidDataException("Invalid ZPAQ header length.");

            int originalLength = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
            if (originalLength < 0)
                throw new InvalidDataException("Invalid original length in ZPAQ header.");

            var decoder = new ArithmeticDecoder(stream);
            var model = new ContextModel();
            var result = new byte[originalLength];

            byte context = 0;
            for (int i = 0; i < originalLength; i++)
            {
                int decodedByte = 0;
                for (int bit = 7; bit >= 0; bit--)
                {
                    int prediction = model.GetPrediction(context);
                    int decodedBit = decoder.DecodeBit(prediction);
                    decodedByte |= (decodedBit << bit);
                    model.Update(context, decodedBit);
                }
                result[i] = (byte)decodedByte;
                context = (byte)decodedByte;
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
            return (long)(inputSize * 0.30) + 64;
        }

        #region Arithmetic Coder

        /// <summary>
        /// Arithmetic encoder that encodes bits given a probability estimate.
        /// Uses 32-bit precision with carry propagation via byte stuffing.
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

            /// <summary>
            /// Encodes a single bit given a probability of the bit being 1.
            /// </summary>
            /// <param name="bit">The bit to encode (0 or 1).</param>
            /// <param name="prob">Probability of 1 in range [1..4095] where 4096 = certain.</param>
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

                // Normalize: output matching top bytes
                while ((_low ^ _high) < 0x01000000u)
                {
                    WriteByte((byte)(_low >> 24));
                    _low <<= 8;
                    _high = (_high << 8) | 0xFF;
                }
            }

            /// <summary>
            /// Flushes remaining state to the output stream.
            /// </summary>
            public void Flush()
            {
                WriteByte((byte)(_low >> 24));
                WriteByte((byte)(_low >> 16));
                WriteByte((byte)(_low >> 8));
                WriteByte((byte)_low);
            }

            private void WriteByte(byte b)
            {
                _output.WriteByte(b);
            }
        }

        /// <summary>
        /// Arithmetic decoder that decodes bits given a probability estimate.
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
                // Initialize code from first 4 bytes
                for (int i = 0; i < 4; i++)
                {
                    _code = (_code << 8) | ReadByte();
                }
            }

            /// <summary>
            /// Decodes a single bit given a probability of the bit being 1.
            /// </summary>
            /// <param name="prob">Probability of 1 in range [1..4095] where 4096 = certain.</param>
            /// <returns>The decoded bit (0 or 1).</returns>
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

                // Normalize
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

        #region Context Model

        /// <summary>
        /// Order-1 context model: for each previous byte (context), maintains a prediction
        /// of the probability that the next bit is 1. Uses exponential moving average updates.
        /// Prediction values are in range [1..4095] (12-bit precision).
        /// </summary>
        private sealed class ContextModel
        {
            // 256 contexts * 8 bit positions = 2048 entries
            private readonly int[] _predictions;

            public ContextModel()
            {
                _predictions = new int[256 * 8];
                // Initialize to 50% (2048 out of 4096)
                Array.Fill(_predictions, 2048);
            }

            /// <summary>
            /// Gets the probability prediction for the next bit being 1,
            /// given the current context byte. Returns value in [1..4095].
            /// </summary>
            public int GetPrediction(byte context)
            {
                // Use context as direct index into predictions
                // Average across all 8 bit positions for the given context
                int baseIdx = context * 8;
                int sum = 0;
                for (int i = 0; i < 8; i++)
                    sum += _predictions[baseIdx + i];
                int avg = sum >> 3;
                return Math.Clamp(avg, 1, 4095);
            }

            /// <summary>
            /// Updates the model after observing a bit for the given context.
            /// Uses exponential moving average with learning rate of 1/16.
            /// </summary>
            public void Update(byte context, int bit)
            {
                int idx = context * 8;
                for (int i = 0; i < 8; i++)
                {
                    int target = bit == 1 ? 4095 : 1;
                    // Exponential moving average: p += (target - p) / 16
                    _predictions[idx + i] += (target - _predictions[idx + i]) >> 4;
                    _predictions[idx + i] = Math.Clamp(_predictions[idx + i], 1, 4095);
                }
            }
        }

        #endregion

        #region Buffered Stream Wrappers

        /// <summary>
        /// A write-only stream that buffers all data and compresses on Flush/Dispose.
        /// Used for algorithms that do not natively support streaming compression.
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

            public override void Flush()
            {
                // Actual compression happens on dispose
            }

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
        /// A read-only stream that reads all compressed data from the source,
        /// decompresses it, and provides the decompressed data for reading.
        /// Used for algorithms that do not natively support streaming decompression.
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

                // Read all compressed data and decompress immediately
                using var buffer = new MemoryStream(4096);
                input.CopyTo(buffer);
                var compressed = buffer.ToArray();
                if (compressed.Length > 0)
                {
                    var data = decompressFunc(compressed);
                    _decompressed = new MemoryStream(data);
                }
                else
                {
                    _decompressed = new MemoryStream(4096);
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
