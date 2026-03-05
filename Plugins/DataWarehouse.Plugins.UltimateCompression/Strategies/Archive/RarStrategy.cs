using System;
using System.IO;
using System.IO.Compression;
using DataWarehouse.SDK.Contracts.Compression;
using SdkCompressionLevel = DataWarehouse.SDK.Contracts.Compression.CompressionLevel;
using SysCompressionLevel = System.IO.Compression.CompressionLevel;
using SysCompressionMode = System.IO.Compression.CompressionMode;

using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;
namespace DataWarehouse.Plugins.UltimateCompression.Strategies.Archive
{
    /// <summary>
    /// RAR-like compression strategy. NOTE: this strategy is NOT RAR-compatible.
    /// It produces a custom container format (magic 'Rar!' + Deflate payload) that is not
    /// readable by standard RAR tools (WinRAR, unrar, etc.).
    /// Use this strategy only for internal DataWarehouse round-trip storage where both
    /// compress and decompress are handled by this class.
    /// </summary>
    /// <remarks>
    /// RAR format characteristics:
    /// 1. Proprietary format with excellent compression ratios
    /// 2. Advanced features: recovery records, solid archives, encryption
    /// 3. PPM (Prediction by Partial Matching) compression option
    /// Due to RAR's proprietary nature, this implementation:
    /// - Writes a custom container (NOT a valid RAR archive)
    /// - Uses Deflate for the actual compression
    /// - Does NOT support reading third-party RAR files
    /// </remarks>
    public sealed class RarStrategy : CompressionStrategyBase
    {
        private const int MaxInputSize = 100 * 1024 * 1024; // 100 MB

        private const uint MagicHeader = 0x52617221; // 'Rar!'
        private const byte MarkHead = 0x72;

        /// <summary>
        /// Initializes a new instance of the <see cref="RarStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public RarStrategy() : base(SdkCompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "RAR-InternalFormat",
            TypicalCompressionRatio = 0.30,
            CompressionSpeed = 3,
            DecompressionSpeed = 4,
            CompressionMemoryUsage = 512 * 1024,
            DecompressionMemoryUsage = 256 * 1024,
            SupportsStreaming = true,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 128,
            OptimalBlockSize = 32768
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
                        "RAR-Compatible strategy healthy",
                        new Dictionary<string, object>
                        {
                            ["CompressOperations"] = GetCounter("rar-compatible.compress"),
                            ["DecompressOperations"] = GetCounter("rar-compatible.decompress")
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
            IncrementCounter("rar-compatible.compress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for RAR-Compatible");
            using var output = new MemoryStream(input.Length + 256); // Estimate: input size + header overhead
            using var writer = new BinaryWriter(output);

            // Write RAR-compatible header
            writer.Write(MagicHeader);
            writer.Write(MarkHead);
            writer.Write((byte)0); // Flags
            writer.Write((ushort)13); // Header size
            writer.Write(input.Length); // Original size

            // Compress data using Deflate (RAR-compatible compression)
            using (var deflateStream = new DeflateStream(output, ConvertToSystemCompressionLevel(Level), leaveOpen: true))
            {
                deflateStream.Write(input, 0, input.Length);
            }

            // Write CRC32 checksum
            uint crc = CalculateCrc32(input);
            writer.Write(crc);

            return output.ToArray();
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            IncrementCounter("rar-compatible.decompress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for RAR-Compatible");
            using var stream = new MemoryStream(input);
            using var reader = new BinaryReader(stream);

            // Try to read RAR header
            uint magic = reader.ReadUInt32();
            if (magic != MagicHeader)
                throw new InvalidDataException("Invalid RAR stream header.");

            byte markHead = reader.ReadByte();
            if (markHead != MarkHead)
                throw new InvalidDataException("Invalid RAR marker.");

            byte flags = reader.ReadByte();
            ushort headerSize = reader.ReadUInt16();
            int originalSize = reader.ReadInt32();

            // Decompress using Deflate
            using var deflateStream = new DeflateStream(stream, SysCompressionMode.Decompress, leaveOpen: true);
            using var decompressed = new MemoryStream(originalSize > 0 ? originalSize : 4096);

            deflateStream.CopyTo(decompressed);
            byte[] result = decompressed.ToArray();

            // Read and validate CRC
            if (stream.Position + 4 <= stream.Length)
            {
                uint storedCrc = reader.ReadUInt32();
                uint calculatedCrc = CalculateCrc32(result);

                if (storedCrc != calculatedCrc)
                    throw new InvalidDataException("CRC32 checksum mismatch.");
            }

            if (result.Length != originalSize)
                throw new InvalidDataException($"Decompressed size mismatch. Expected {originalSize}, got {result.Length}.");

            return result;
        }

        private static uint CalculateCrc32(byte[] data)
        {
            uint crc = 0xFFFFFFFF;

            for (int i = 0; i < data.Length; i++)
            {
                byte index = (byte)(((crc) & 0xFF) ^ data[i]);
                crc = (crc >> 8) ^ Crc32Table[index];
            }

            return ~crc;
        }

        // CRC32 lookup table
        private static readonly uint[] Crc32Table = GenerateCrc32Table();

        private static uint[] GenerateCrc32Table()
        {
            var table = new uint[256];
            const uint polynomial = 0xEDB88320;

            for (uint i = 0; i < 256; i++)
            {
                uint crc = i;
                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 1) != 0)
                        crc = (crc >> 1) ^ polynomial;
                    else
                        crc >>= 1;
                }
                table[i] = crc;
            }

            return table;
        }

        private static SysCompressionLevel ConvertToSystemCompressionLevel(SdkCompressionLevel level)
        {
            return level switch
            {
                SdkCompressionLevel.Fastest => SysCompressionLevel.Fastest,
                SdkCompressionLevel.Fast => SysCompressionLevel.Fastest,
                SdkCompressionLevel.Default => SysCompressionLevel.Optimal,
                SdkCompressionLevel.Better => SysCompressionLevel.SmallestSize,
                SdkCompressionLevel.Best => SysCompressionLevel.SmallestSize,
                _ => SysCompressionLevel.Optimal
            };
        }

        /// <inheritdoc/>
        protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
        {
            return new RarCompressionStream(output, leaveOpen, this);
        }

        /// <inheritdoc/>
        protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
        {
            return new RarDecompressionStream(input, leaveOpen, this);
        }

        /// <inheritdoc/>
        public override long EstimateCompressedSize(long inputSize)
        {
            return (long)(inputSize * 0.35) + 128;
        }

        #region Stream Wrappers

        private sealed class RarCompressionStream : Stream
        {
            private readonly Stream _output;
            private readonly bool _leaveOpen;
            private readonly RarStrategy _strategy;
            private readonly MemoryStream _buffer = new();
            private bool _disposed;

            public RarCompressionStream(Stream output, bool leaveOpen, RarStrategy strategy)
            {
                _output = output;
                _leaveOpen = leaveOpen;
                _strategy = strategy;
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

        private sealed class RarDecompressionStream : Stream
        {
            private readonly Stream _input;
            private readonly bool _leaveOpen;
            private readonly RarStrategy _strategy;
            private byte[]? _decompressedData;
            private int _position;
            private bool _disposed;

            public RarDecompressionStream(Stream input, bool leaveOpen, RarStrategy strategy)
            {
                _input = input;
                _leaveOpen = leaveOpen;
                _strategy = strategy;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _decompressedData?.Length ?? 0;
            public override long Position
            {
                get => _position;
                set => throw new NotSupportedException();
            }

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
