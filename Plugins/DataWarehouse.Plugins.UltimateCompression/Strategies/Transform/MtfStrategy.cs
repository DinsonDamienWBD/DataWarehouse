using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Compression;
using SdkCompressionLevel = DataWarehouse.SDK.Contracts.Compression.CompressionLevel;

namespace DataWarehouse.Plugins.UltimateCompression.Strategies.Transform
{
    /// <summary>
    /// Compression strategy implementing Move-to-Front (MTF) encoding.
    /// MTF is a data transformation that exploits locality of reference by maintaining
    /// a list of symbols and moving recently used symbols to the front.
    /// </summary>
    /// <remarks>
    /// Move-to-Front encoding transforms data by maintaining an ordered list of all possible
    /// byte values (0-255). For each input byte, it outputs the current position of that byte
    /// in the list, then moves that byte to the front. This causes frequently occurring bytes
    /// to be encoded as small numbers (often 0), creating runs of small values that compress
    /// well with run-length encoding and entropy coding.
    ///
    /// MTF is particularly effective after Burrows-Wheeler Transform (BWT) because BWT creates
    /// long runs of identical characters. MTF is a core component of BZip2 compression.
    ///
    /// Note: Like BWT, MTF alone provides minimal compression - it's a preprocessing step that
    /// makes data more amenable to subsequent compression stages. The "compression ratio" here
    /// reflects that MTF output is roughly the same size as input but with better characteristics
    /// for entropy coding.
    /// </remarks>
    public sealed class MtfStrategy : CompressionStrategyBase
    {
        private const int MinAlphabetSize = 2;
        private const int MaxAlphabetSize = 65536;
        private const int MaxInputSize = 100 * 1024 * 1024; // 100MB

        private int _alphabetSize = 256;

        /// <summary>
        /// Initializes a new instance of the <see cref="MtfStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public MtfStrategy() : base(SdkCompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "MTF",
            TypicalCompressionRatio = 0.85,
            CompressionSpeed = 8,
            DecompressionSpeed = 8,
            CompressionMemoryUsage = 512,
            DecompressionMemoryUsage = 512,
            SupportsStreaming = true,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 16,
            OptimalBlockSize = 64 * 1024
        };

        /// <inheritdoc/>
        protected override async Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            // Validate alphabet size configuration
            if (_alphabetSize < MinAlphabetSize || _alphabetSize > MaxAlphabetSize)
                throw new ArgumentException($"Alphabet size must be between {MinAlphabetSize} and {MaxAlphabetSize}. Got: {_alphabetSize}");

            await base.InitializeAsyncCore(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            // No resources to clean up for MTF
            await base.ShutdownAsyncCore(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        protected override async ValueTask DisposeAsyncCore()
        {
            // Clean disposal pattern
            await base.DisposeAsyncCore().ConfigureAwait(false);
        }

        /// <summary>
        /// Performs health check by verifying round-trip encoding with test data.
        /// </summary>
        public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default)
        {
            return await GetCachedHealthAsync(async (cancellationToken) =>
            {
                try
                {
                    // Round-trip test with known data
                    var testData = new byte[] { 1, 1, 2, 2, 3, 3, 1, 1 };
                    var encoded = CompressCore(testData);
                    var decoded = DecompressCore(encoded);

                    if (testData.Length != decoded.Length)
                        return new StrategyHealthCheckResult(false, "Round-trip length mismatch");

                    for (int i = 0; i < testData.Length; i++)
                    {
                        if (testData[i] != decoded[i])
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
            IncrementCounter("mtf.encode");

            if (input == null || input.Length == 0)
                return Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input size exceeds maximum of {MaxInputSize} bytes");

            var output = new byte[input.Length];
            var alphabet = InitializeAlphabet();

            for (int i = 0; i < input.Length; i++)
            {
                byte symbol = input[i];

                // Find position of symbol in alphabet
                byte position = FindPosition(alphabet, symbol);
                output[i] = position;

                // Move symbol to front of alphabet
                MoveToFront(alphabet, position);
            }

            return output;
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            IncrementCounter("mtf.decode");

            if (input == null || input.Length == 0)
                return Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input size exceeds maximum of {MaxInputSize} bytes");

            var output = new byte[input.Length];
            var alphabet = InitializeAlphabet();

            for (int i = 0; i < input.Length; i++)
            {
                byte position = input[i];

                // Get symbol at this position
                byte symbol = alphabet[position];
                output[i] = symbol;

                // Move symbol to front of alphabet
                MoveToFront(alphabet, position);
            }

            return output;
        }

        /// <inheritdoc/>
        protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
        {
            return new MtfCompressionStream(output, leaveOpen);
        }

        /// <inheritdoc/>
        protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
        {
            return new MtfDecompressionStream(input, leaveOpen);
        }

        /// <inheritdoc/>
        public override long EstimateCompressedSize(long inputSize)
        {
            // MTF doesn't actually compress - output size equals input size
            // But it transforms the data to have better characteristics for compression
            return inputSize;
        }

        /// <summary>
        /// Initializes the alphabet with all 256 possible byte values in order.
        /// </summary>
        private static byte[] InitializeAlphabet()
        {
            var alphabet = new byte[256];
            for (int i = 0; i < 256; i++)
                alphabet[i] = (byte)i;
            return alphabet;
        }

        /// <summary>
        /// Finds the position of a symbol in the alphabet.
        /// </summary>
        private static byte FindPosition(byte[] alphabet, byte symbol)
        {
            for (byte i = 0; i < alphabet.Length; i++)
            {
                if (alphabet[i] == symbol)
                    return i;
            }
            throw new InvalidOperationException($"Symbol {symbol} not found in alphabet");
        }

        /// <summary>
        /// Moves the symbol at the given position to the front of the alphabet.
        /// </summary>
        private static void MoveToFront(byte[] alphabet, byte position)
        {
            if (position == 0)
                return; // Already at front

            byte symbol = alphabet[position];

            // Shift all elements before position one position to the right
            for (int i = position; i > 0; i--)
                alphabet[i] = alphabet[i - 1];

            // Place symbol at front
            alphabet[0] = symbol;
        }

        /// <summary>
        /// Stream wrapper for MTF compression (encoding).
        /// </summary>
        private sealed class MtfCompressionStream : Stream
        {
            private readonly Stream _baseStream;
            private readonly bool _leaveOpen;
            private readonly byte[] _alphabet;

            public MtfCompressionStream(Stream output, bool leaveOpen)
            {
                _baseStream = output ?? throw new ArgumentNullException(nameof(output));
                _leaveOpen = leaveOpen;
                _alphabet = InitializeAlphabet();
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Write(byte[] buffer, int offset, int count)
            {
                for (int i = 0; i < count; i++)
                {
                    byte symbol = buffer[offset + i];
                    byte position = FindPosition(_alphabet, symbol);
                    _baseStream.WriteByte(position);
                    MoveToFront(_alphabet, position);
                }
            }

            public override void Flush()
            {
                _baseStream.Flush();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    Flush();
                    if (!_leaveOpen)
                        _baseStream.Dispose();
                }
                base.Dispose(disposing);
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        /// <summary>
        /// Stream wrapper for MTF decompression (decoding).
        /// </summary>
        private sealed class MtfDecompressionStream : Stream
        {
            private readonly Stream _baseStream;
            private readonly bool _leaveOpen;
            private readonly byte[] _alphabet;

            public MtfDecompressionStream(Stream input, bool leaveOpen)
            {
                _baseStream = input ?? throw new ArgumentNullException(nameof(input));
                _leaveOpen = leaveOpen;
                _alphabet = InitializeAlphabet();
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int bytesRead = 0;

                for (int i = 0; i < count; i++)
                {
                    int b = _baseStream.ReadByte();
                    if (b == -1)
                        break;

                    byte position = (byte)b;
                    byte symbol = _alphabet[position];
                    buffer[offset + i] = symbol;
                    MoveToFront(_alphabet, position);
                    bytesRead++;
                }

                return bytesRead;
            }

            public override void Flush() => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    if (!_leaveOpen)
                        _baseStream.Dispose();
                }
                base.Dispose(disposing);
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
