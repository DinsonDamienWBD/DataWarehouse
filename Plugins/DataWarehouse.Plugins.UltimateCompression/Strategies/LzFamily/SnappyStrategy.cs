using System;
using System.IO;
using DataWarehouse.SDK.Contracts.Compression;
using Snappier;

using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;
namespace DataWarehouse.Plugins.UltimateCompression.Strategies.LzFamily
{
    /// <summary>
    /// Compression strategy using the Snappy algorithm via the Snappier library.
    /// Snappy prioritizes speed over compression ratio, targeting 250 MB/s compression
    /// and 500 MB/s decompression on commodity hardware.
    /// </summary>
    /// <remarks>
    /// Snappy was developed by Google for internal use in systems like Bigtable, MapReduce,
    /// and RPC. It does not aim for maximum compression but rather for very high speed
    /// with reasonable compression. The Snappier library provides a managed .NET implementation
    /// with hardware acceleration support.
    /// </remarks>
    public sealed class SnappyStrategy : CompressionStrategyBase
    {
        private const int MaxInputSize = 100 * 1024 * 1024; // 100 MB

        /// <summary>
        /// Initializes a new instance of the <see cref="SnappyStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public SnappyStrategy() : base(CompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "Snappy",
            TypicalCompressionRatio = 0.60,
            CompressionSpeed = 9,
            DecompressionSpeed = 10,
            CompressionMemoryUsage = 64 * 1024,
            DecompressionMemoryUsage = 64 * 1024,
            SupportsStreaming = false,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 64,
            OptimalBlockSize = 64 * 1024
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
                        "Snappy strategy healthy",
                        new Dictionary<string, object>
                        {
                            ["CompressOperations"] = GetCounter("snappy.compress"),
                            ["DecompressOperations"] = GetCounter("snappy.decompress")
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
            IncrementCounter("snappy.compress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for Snappy");
            return Snappy.CompressToArray(input);
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            IncrementCounter("snappy.decompress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for Snappy");
            return Snappy.DecompressToArray(input);
        }

        /// <inheritdoc/>
        protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
        {
            // Snappy does not natively support streaming; use a buffered wrapper
            return new SnappyBufferedCompressionStream(output, leaveOpen);
        }

        /// <inheritdoc/>
        protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
        {
            // Snappy does not natively support streaming; use a buffered wrapper
            return new SnappyBufferedDecompressionStream(input, leaveOpen);
        }

        /// <inheritdoc/>
        public override long EstimateCompressedSize(long inputSize)
        {
            return Snappy.GetMaxCompressedLength((int)Math.Min(inputSize, int.MaxValue));
        }

        /// <summary>
        /// Buffered stream that accumulates writes, then compresses via Snappy on flush/dispose.
        /// Data is prefixed with the 4-byte little-endian uncompressed length for decompression.
        /// </summary>
        private sealed class SnappyBufferedCompressionStream : Stream
        {
            private readonly Stream _output;
            private readonly bool _leaveOpen;
            private readonly MemoryStream _buffer = new();
            private bool _disposed;

            public SnappyBufferedCompressionStream(Stream output, bool leaveOpen)
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
                ObjectDisposedException.ThrowIf(_disposed, this);
                _buffer.Write(buffer, offset, count);
            }

            public override void Flush()
            {
                // Flushing only writes to the underlying stream on dispose
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
                    _buffer.Dispose();

                    var compressed = Snappy.CompressToArray(data);

                    // Write uncompressed length prefix (4 bytes LE) + compressed data
                    Span<byte> lengthPrefix = stackalloc byte[4];
                    BitConverter.TryWriteBytes(lengthPrefix, data.Length);
                    _output.Write(lengthPrefix);
                    _output.Write(compressed, 0, compressed.Length);
                    _output.Flush();

                    if (!_leaveOpen)
                        _output.Dispose();
                }
                base.Dispose(disposing);
            }
        }

        /// <summary>
        /// Buffered stream that reads all compressed data from the input, then
        /// decompresses via Snappy, exposing the decompressed data for reads.
        /// Expects the 4-byte little-endian uncompressed length prefix.
        /// </summary>
        private sealed class SnappyBufferedDecompressionStream : Stream
        {
            private readonly Stream _input;
            private readonly bool _leaveOpen;
            private MemoryStream? _decompressed;
            private bool _disposed;

            public SnappyBufferedDecompressionStream(Stream input, bool leaveOpen)
            {
                _input = input;
                _leaveOpen = leaveOpen;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => EnsureDecompressed().Length;
            public override long Position
            {
                get => EnsureDecompressed().Position;
                set => EnsureDecompressed().Position = value;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return EnsureDecompressed().Read(buffer, offset, count);
            }

            public override void Write(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();

            public override void Flush() { }

            public override long Seek(long offset, SeekOrigin origin) =>
                throw new NotSupportedException();

            public override void SetLength(long value) =>
                throw new NotSupportedException();

            private MemoryStream EnsureDecompressed()
            {
                if (_decompressed != null)
                    return _decompressed;

                // Read length prefix
                var lengthBytes = new byte[4];
                int read = 0;
                while (read < 4)
                {
                    int n = _input.Read(lengthBytes, read, 4 - read);
                    if (n == 0) throw new InvalidDataException("Unexpected end of stream reading Snappy length prefix.");
                    read += n;
                }

                // Read remaining compressed data
                using var compressedStream = new MemoryStream(65536);
                _input.CopyTo(compressedStream);
                var compressed = compressedStream.ToArray();

                var decompressed = Snappy.DecompressToArray(compressed);
                _decompressed = new MemoryStream(decompressed, writable: false);
                return _decompressed;
            }

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
    }
}
