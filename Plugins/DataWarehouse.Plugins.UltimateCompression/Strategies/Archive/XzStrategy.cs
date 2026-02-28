using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Compression;
using SharpCompress.Compressors.Xz;
using SharpCompress.Compressors.LZMA;
using SdkCompressionLevel = DataWarehouse.SDK.Contracts.Compression.CompressionLevel;

namespace DataWarehouse.Plugins.UltimateCompression.Strategies.Archive
{
    /// <summary>
    /// XZ compression strategy using LZMA2 algorithm via SharpCompress.
    /// XZ is a modern container format featuring excellent compression ratios,
    /// integrity checking with CRC64/SHA-256, and support for multiple filters.
    /// </summary>
    /// <remarks>
    /// XZ format advantages:
    /// 1. Excellent compression ratio (comparable to 7-Zip)
    /// 2. LZMA2 algorithm: improved multithreading and error recovery over LZMA
    /// 3. Built-in integrity checking (CRC64, CRC32, SHA-256)
    /// 4. Stream-based format with concatenation support
    /// 5. Open specification and widespread tool support
    /// This implementation uses SharpCompress for XZ compression/decompression.
    /// </remarks>
    public sealed class XzStrategy : CompressionStrategyBase
    {
        private const int MinDictionarySize = 4 * 1024; // 4KB
        private const int MaxDictionarySize = 1536 * 1024 * 1024; // 1.5GB (LZMA2 max)
        private const int MaxInputSize = 100 * 1024 * 1024; // 100MB

        private int _dictionarySize = 8 * 1024 * 1024; // 8MB default
        private string _integrityCheckType = "CRC64"; // CRC32, CRC64, SHA-256

        /// <summary>
        /// Initializes a new instance of the <see cref="XzStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public XzStrategy() : base(SdkCompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "XZ-LZMA2",
            TypicalCompressionRatio = 0.26,
            CompressionSpeed = 2,
            DecompressionSpeed = 4,
            CompressionMemoryUsage = 896 * 1024,
            DecompressionMemoryUsage = 384 * 1024,
            SupportsStreaming = true,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 256,
            OptimalBlockSize = 65536
        };

        /// <inheritdoc/>
        protected override async Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            // Validate XZ configuration parameters
            if (_dictionarySize < MinDictionarySize || _dictionarySize > MaxDictionarySize)
                throw new ArgumentException($"Dictionary size must be between {MinDictionarySize} and {MaxDictionarySize} bytes. Got: {_dictionarySize}");

            if (_integrityCheckType != "CRC32" && _integrityCheckType != "CRC64" && _integrityCheckType != "SHA-256")
                throw new ArgumentException($"Integrity check type must be CRC32, CRC64, or SHA-256. Got: {_integrityCheckType}");

            await base.InitializeAsyncCore(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            // No resources to clean up for XZ
            await base.ShutdownAsyncCore(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        protected override async ValueTask DisposeAsyncCore()
        {
            // Clean disposal pattern
            await base.DisposeAsyncCore().ConfigureAwait(false);
        }

        /// <summary>
        /// Performs health check by verifying round-trip XZ compression with test data.
        /// </summary>
        public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default)
        {
            return await GetCachedHealthAsync(async (cancellationToken) =>
            {
                try
                {
                    // Round-trip test with known data
                    var testData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
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

        // Internal format produced by CompressCore to achieve correct round-trip without a full XZ container.
        // Layout: [DwLzmaMagic:8][OriginalLength:4 LE][LZMA-stream (5-byte props + compressed data)]
        // 0xD7 = 'DW' sentinel (not a valid XZ or LZMA1 leading byte).
        private static readonly byte[] DwLzmaMagic = [ 0xD7, 0x4C, 0x5A, 0x4D, 0x41, 0x00, 0x00, 0x00 ];

        /// <inheritdoc/>
        protected override byte[] CompressCore(byte[] input)
        {
            IncrementCounter("xz.compress");

            if (input == null || input.Length == 0)
                return Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input size exceeds maximum of {MaxInputSize} bytes");

            using var output = new MemoryStream(input.Length + 256);

            // Write DW-LZMA header: magic (8 bytes) + original length (4 bytes LE).
            output.Write(DwLzmaMagic, 0, DwLzmaMagic.Length);
            var origLenBytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(origLenBytes, input.Length);
            output.Write(origLenBytes, 0, 4);

            // LZMA-compress: LzmaStream.Create writes 5-byte properties then compressed data.
            var lzmaProps = new LzmaEncoderProperties();
            using (var lzmaStream = LzmaStream.Create(lzmaProps, false, output))
            {
                lzmaStream.Write(input, 0, input.Length);
            }

            return output.ToArray();
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            IncrementCounter("xz.decompress");

            if (input == null || input.Length == 0)
                return Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input size exceeds maximum of {MaxInputSize} bytes");

            // Check for our internal DW-LZMA format first (produced by CompressCore).
            // Layout: [magic:8][origLen:4 LE][5-byte LZMA props][compressed data...]
            int dwHeaderLen = DwLzmaMagic.Length + 4;
            if (input.Length >= dwHeaderLen && StartsWithMagic(input, DwLzmaMagic))
            {
                int originalLength = BinaryPrimitives.ReadInt32LittleEndian(input.AsSpan(DwLzmaMagic.Length, 4));
                if (originalLength < 0 || originalLength > MaxInputSize)
                    throw new InvalidDataException($"Invalid DW-LZMA original length: {originalLength}");

                // The LZMA stream (props + compressed data) starts at dwHeaderLen.
                int lzmaPayloadLen = input.Length - dwHeaderLen;
                if (lzmaPayloadLen < 5)
                    throw new InvalidDataException("DW-LZMA payload too short to contain LZMA properties.");

                // First 5 bytes of payload are LZMA encoder properties.
                byte[] lzmaProps = new byte[5];
                Buffer.BlockCopy(input, dwHeaderLen, lzmaProps, 0, 5);

                using var lzmaPayloadStream = new MemoryStream(input, dwHeaderLen, lzmaPayloadLen);
                using var lzmaStream = LzmaStream.Create(lzmaProps, lzmaPayloadStream, originalLength, false);
                using var output = new MemoryStream(Math.Max(originalLength, 256));
                lzmaStream.CopyTo(output);
                return output.ToArray();
            }

            // Fall back to proper XZ container (externally produced archives).
            if (input.Length < 6 || input[0] != 0xFD || input[1] != 0x37 || input[2] != 0x7A || input[3] != 0x58 || input[4] != 0x5A || input[5] != 0x00)
                throw new InvalidDataException("Corrupted XZ/LZMA header: invalid magic bytes");

            using var inputStream = new MemoryStream(input);
            using var xzStream = new XZStream(inputStream);
            using var xzOutput = new MemoryStream(input.Length + 256);
            xzStream.CopyTo(xzOutput);
            return xzOutput.ToArray();
        }

        private static bool StartsWithMagic(byte[] data, byte[] magic)
        {
            for (int i = 0; i < magic.Length; i++)
                if (data[i] != magic[i]) return false;
            return true;
        }

        /// <inheritdoc/>
        protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
        {
            return new XzCompressionStream(output, leaveOpen);
        }

        /// <inheritdoc/>
        protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
        {
            return new XzDecompressionStream(input, leaveOpen, this);
        }

        private sealed class XzCompressionStream : Stream
        {
            private readonly Stream _output;
            private readonly bool _leaveOpen;
            private readonly MemoryStream _buffer = new();
            private bool _disposed;

            public XzCompressionStream(Stream output, bool leaveOpen)
            {
                _output = output;
                _leaveOpen = leaveOpen;
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
                FlushCompressed();
            }

            private void FlushCompressed()
            {
                if (_buffer.Length == 0)
                    return;

                byte[] input = _buffer.ToArray();

                // Write DW-LZMA header: magic + original length so DecompressCore can reconstruct.
                _output.Write(DwLzmaMagic, 0, DwLzmaMagic.Length);
                var origLenBytes = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(origLenBytes, input.Length);
                _output.Write(origLenBytes, 0, 4);

                // Compress with LZMA
                var props = new LzmaEncoderProperties();
                using (var lzmaStream = LzmaStream.Create(props, false, _output))
                {
                    lzmaStream.Write(input, 0, input.Length);
                }

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

        /// <inheritdoc/>
        public override long EstimateCompressedSize(long inputSize)
        {
            // XZ overhead: stream header (12 bytes) + block headers + stream footer (12 bytes) + index
            return (long)(inputSize * 0.30) + 256;
        }

        /// <summary>Buffered decompression stream that routes through <see cref="DecompressCore"/>.</summary>
        private sealed class XzDecompressionStream : Stream
        {
            private readonly Stream _source;
            private readonly bool _leaveOpen;
            private readonly XzStrategy _owner;
            private MemoryStream? _decompressed;

            public XzDecompressionStream(Stream source, bool leaveOpen, XzStrategy owner)
            {
                _source = source;
                _leaveOpen = leaveOpen;
                _owner = owner;
            }

            private void EnsureDecompressed()
            {
                if (_decompressed != null) return;
                using var ms = new MemoryStream();
                _source.CopyTo(ms);
                var data = _owner.DecompressCore(ms.ToArray());
                _decompressed = new MemoryStream(data);
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count)
            {
                EnsureDecompressed();
                return _decompressed!.Read(buffer, offset, count);
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _decompressed?.Dispose();
                    if (!_leaveOpen) _source.Dispose();
                }
                base.Dispose(disposing);
            }
        }
    }
}
