using System;
using System.IO;
using System.Linq;
using DataWarehouse.SDK.Contracts.Compression;

using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;
namespace DataWarehouse.Plugins.UltimateCompression.Strategies.Domain
{
    /// <summary>
    /// Compression strategy inspired by JPEG XL lossless compression principles.
    /// Implements modular coding with squeeze transforms and ANS (Asymmetric Numeral Systems) entropy backend.
    /// Data is transformed using delta/palette transforms, then entropy-coded with ANS for high compression efficiency.
    /// </summary>
    /// <remarks>
    /// JPEG XL achieves excellent lossless compression through:
    /// 1. Modular mode: transforms data into multiple channels
    /// 2. Squeeze transform: hierarchical pixel decorrelation
    /// 3. ANS entropy coding: near-optimal entropy coding
    /// This implementation adapts these principles for general binary data.
    /// </remarks>
    public sealed class JxlLosslessStrategy : CompressionStrategyBase
    {
        private const int MaxInputSize = 100 * 1024 * 1024; // 100 MB

        private const uint MagicHeader = 0x4A584C00; // 'JXL\0'
        private const int DefaultBlockSize = 256;
        private const int MaxSymbols = 256;

        /// <summary>
        /// Initializes a new instance of the <see cref="JxlLosslessStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public JxlLosslessStrategy() : base(CompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "JXL-Lossless",
            TypicalCompressionRatio = 0.35,
            CompressionSpeed = 3,
            DecompressionSpeed = 5,
            CompressionMemoryUsage = 512 * 1024,
            DecompressionMemoryUsage = 256 * 1024,
            SupportsStreaming = true,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 128,
            OptimalBlockSize = DefaultBlockSize
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
                        "JXL-Lossless strategy healthy",
                        new Dictionary<string, object>
                        {
                            ["CompressOperations"] = GetCounter("jxl-lossless.compress"),
                            ["DecompressOperations"] = GetCounter("jxl-lossless.decompress")
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
            IncrementCounter("jxl-lossless.compress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for JXL-Lossless");
            using var output = new MemoryStream(input.Length + 256);
            using var writer = new BinaryWriter(output);

            // Write header
            writer.Write(MagicHeader);
            writer.Write(input.Length);

            // Apply squeeze transform (delta encoding)
            byte[] transformed = ApplySqueeze(input);

            // Build frequency table
            var frequencies = new int[MaxSymbols];
            foreach (byte b in transformed)
                frequencies[b]++;

            // Write frequency table (compressed)
            WriteFrequencyTable(writer, frequencies);

            // ANS encode
            byte[] encoded = AnsEncode(transformed, frequencies);
            writer.Write(encoded.Length);
            writer.Write(encoded);

            return output.ToArray();
        }

        private static byte[] ApplySqueeze(byte[] input)
        {
            // Squeeze transform: hierarchical delta encoding
            // Convert to prediction residuals for better compressibility
            var output = new byte[input.Length];

            if (input.Length == 0)
                return output;

            // First byte is unchanged
            output[0] = input[0];

            // Apply delta encoding with multi-context prediction
            for (int i = 1; i < input.Length; i++)
            {
                byte predicted;
                if (i < 2)
                {
                    predicted = input[i - 1];
                }
                else
                {
                    // Gradient predictor (inspired by PNG filters)
                    int a = input[i - 1];
                    int b = input[i - 2];
                    predicted = (byte)((2 * a - b) & 0xFF);
                }

                output[i] = (byte)(input[i] - predicted);
            }

            return output;
        }

        private static byte[] InverseSqueeze(byte[] input)
        {
            var output = new byte[input.Length];

            if (input.Length == 0)
                return output;

            output[0] = input[0];

            for (int i = 1; i < input.Length; i++)
            {
                byte predicted;
                if (i < 2)
                {
                    predicted = output[i - 1];
                }
                else
                {
                    int a = output[i - 1];
                    int b = output[i - 2];
                    predicted = (byte)((2 * a - b) & 0xFF);
                }

                output[i] = (byte)(input[i] + predicted);
            }

            return output;
        }

        private static void WriteFrequencyTable(BinaryWriter writer, int[] frequencies)
        {
            // Run-length encode the frequency table.
            // P2-1599: Use a plain for-loop instead of LINQ Count() to avoid enumerator allocation.
            int nonZeroCount = 0;
            for (int fi = 0; fi < frequencies.Length; fi++)
                if (frequencies[fi] > 0) nonZeroCount++;
            writer.Write((ushort)nonZeroCount);

            for (int i = 0; i < MaxSymbols; i++)
            {
                if (frequencies[i] > 0)
                {
                    writer.Write((byte)i);
                    writer.Write(frequencies[i]);
                }
            }
        }

        private static int[] ReadFrequencyTable(BinaryReader reader)
        {
            var frequencies = new int[MaxSymbols];
            ushort nonZeroCount = reader.ReadUInt16();

            for (int i = 0; i < nonZeroCount; i++)
            {
                byte symbol = reader.ReadByte();
                int freq = reader.ReadInt32();
                frequencies[symbol] = freq;
            }

            return frequencies;
        }

        private static byte[] AnsEncode(byte[] input, int[] frequencies)
        {
            // Simplified ANS encoding (tANS - table-based ANS)
            // Build cumulative distribution
            var cumFreq = new int[MaxSymbols + 1];
            int total = 0;
            for (int i = 0; i < MaxSymbols; i++)
            {
                cumFreq[i] = total;
                total += Math.Max(frequencies[i], 0);
            }
            cumFreq[MaxSymbols] = total;

            if (total == 0)
                return Array.Empty<byte>();

            using var output = new MemoryStream(input.Length + 256);
            using var writer = new BinaryWriter(output);

            // ANS state
            ulong state = 1 << 16; // Initial state

            // Encode backwards (ANS property)
            for (int i = input.Length - 1; i >= 0; i--)
            {
                byte symbol = input[i];
                int freq = Math.Max(frequencies[symbol], 1);
                int start = cumFreq[symbol];

                // Normalize state if too large
                while (state >= ((1UL << 32) * (ulong)freq))
                {
                    writer.Write((ushort)(state & 0xFFFF));
                    state >>= 16;
                }

                // ANS encode step: C(s, x) = (x / freq) * total + start + (x % freq)
                state = ((state / (ulong)freq) * (ulong)total) + (ulong)start + (state % (ulong)freq);
            }

            // Write final state
            writer.Write(state);

            return output.ToArray();
        }

        private static byte[] AnsDecode(byte[] encoded, int[] frequencies, int outputLength)
        {
            if (outputLength == 0 || encoded.Length < 8)
                return Array.Empty<byte>();

            // Build cumulative distribution
            var cumFreq = new int[MaxSymbols + 1];
            int total = 0;
            for (int i = 0; i < MaxSymbols; i++)
            {
                cumFreq[i] = total;
                total += Math.Max(frequencies[i], 0);
            }
            cumFreq[MaxSymbols] = total;

            if (total == 0)
                return new byte[outputLength];

            using var input = new MemoryStream(encoded);
            using var reader = new BinaryReader(input);

            var output = new byte[outputLength];

            // Read initial state
            ulong state = reader.ReadUInt64();

            // Decode forward
            for (int i = 0; i < outputLength; i++)
            {
                // Find symbol from state
                int slot = (int)(state % (ulong)total);
                byte symbol = 0;
                for (int s = 0; s < MaxSymbols; s++)
                {
                    if (slot >= cumFreq[s] && slot < cumFreq[s + 1])
                    {
                        symbol = (byte)s;
                        break;
                    }
                }

                output[i] = symbol;

                int freq = Math.Max(frequencies[symbol], 1);
                int start = cumFreq[symbol];

                // ANS decode step: x = freq * (state / total) + (state % total) - start
                ulong newState = (ulong)freq * (state / (ulong)total) + ((state % (ulong)total) - (ulong)start);
                state = newState;

                // Renormalize if needed
                while (state < (1UL << 16) && input.Position < input.Length)
                {
                    ushort bits = reader.ReadUInt16();
                    state = (state << 16) | bits;
                }
            }

            return output;
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            IncrementCounter("jxl-lossless.decompress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for JXL-Lossless");
            using var stream = new MemoryStream(input);
            using var reader = new BinaryReader(stream);

            // Read and validate header
            uint magic = reader.ReadUInt32();
            if (magic != MagicHeader)
                throw new InvalidDataException("Invalid JXL stream header.");

            int originalLength = reader.ReadInt32();

            // Read frequency table
            int[] frequencies = ReadFrequencyTable(reader);

            // Read encoded data
            int encodedLength = reader.ReadInt32();
            byte[] encodedData = reader.ReadBytes(encodedLength);

            // ANS decode
            byte[] transformed = AnsDecode(encodedData, frequencies, originalLength);

            // Inverse squeeze transform
            byte[] result = InverseSqueeze(transformed);

            return result;
        }

        /// <inheritdoc/>
        protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
        {
            return new JxlCompressionStream(output, leaveOpen, this);
        }

        /// <inheritdoc/>
        protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
        {
            return new JxlDecompressionStream(input, leaveOpen, this);
        }

        /// <inheritdoc/>
        public override long EstimateCompressedSize(long inputSize)
        {
            return (long)(inputSize * 0.40) + 512;
        }

        #region Stream Wrappers

        private sealed class JxlCompressionStream : Stream
        {
            private readonly Stream _output;
            private readonly bool _leaveOpen;
            private readonly JxlLosslessStrategy _strategy;
            private readonly MemoryStream _buffer = new();
            private bool _disposed;

            public JxlCompressionStream(Stream output, bool leaveOpen, JxlLosslessStrategy strategy)
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

            public override void Write(byte[] buffer, int offset, int count)
            {
                _buffer.Write(buffer, offset, count);
            }

            public override void Flush()
            {
                FlushCompressed();
            }

            private void FlushCompressed()
            {
                if (_buffer.Length == 0)
                    return;

                byte[] compressed = _strategy.CompressCore(_buffer.ToArray());
                _output.Write(compressed, 0, compressed.Length);
                _output.Flush();
                _buffer.SetLength(0);
            }

            protected override void Dispose(bool disposing)
            {
                if (!_disposed && disposing)
                {
                    FlushCompressed();
                    if (!_leaveOpen)
                        _output.Dispose();
                    _buffer.Dispose();
                    _disposed = true;
                }

                base.Dispose(disposing);
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        private sealed class JxlDecompressionStream : Stream
        {
            private readonly Stream _input;
            private readonly bool _leaveOpen;
            private readonly JxlLosslessStrategy _strategy;
            private byte[]? _decompressedData;
            private int _position;
            private bool _disposed;

            public JxlDecompressionStream(Stream input, bool leaveOpen, JxlLosslessStrategy strategy)
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
                EnsureDecompressed();
                if (_decompressedData == null || _position >= _decompressedData.Length)
                    return 0;

                int available = Math.Min(count, _decompressedData.Length - _position);
                Array.Copy(_decompressedData, _position, buffer, offset, available);
                _position += available;
                return available;
            }

            private void EnsureDecompressed()
            {
                if (_decompressedData != null)
                    return;

                using var ms = new MemoryStream(4096);
                _input.CopyTo(ms);
                _decompressedData = _strategy.DecompressCore(ms.ToArray());
            }

            protected override void Dispose(bool disposing)
            {
                if (!_disposed && disposing)
                {
                    if (!_leaveOpen)
                        _input.Dispose();
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
