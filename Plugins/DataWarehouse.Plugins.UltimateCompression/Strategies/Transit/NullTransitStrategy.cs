using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Compression;

namespace DataWarehouse.Plugins.UltimateCompression.Strategies.Transit
{
    /// <summary>
    /// Null transit compression strategy - pass-through with no compression.
    /// Used when compression is disabled, data is incompressible, or for testing.
    /// </summary>
    public sealed class NullTransitStrategy : CompressionStrategyBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="NullTransitStrategy"/> class.
        /// </summary>
        /// <param name="level">The compression level (ignored for null strategy).</param>
        public NullTransitStrategy(CompressionLevel level) : base(level)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics => new()
        {
            AlgorithmName = "Null-Transit",
            TypicalCompressionRatio = 1.0, // No compression
            CompressionSpeed = 10,
            DecompressionSpeed = 10,
            CompressionMemoryUsage = 0,
            DecompressionMemoryUsage = 0,
            SupportsStreaming = true,
            SupportsParallelCompression = true,
            SupportsParallelDecompression = true,
            SupportsRandomAccess = true,
            MinimumRecommendedSize = 0,
            OptimalBlockSize = 1024 * 1024
        };

        /// <inheritdoc/>
        protected override byte[] CompressCore(byte[] input)
        {
            // Return copy to maintain immutability contract
            var result = new byte[input.Length];
            Array.Copy(input, result, input.Length);
            return result;
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            // Return copy to maintain immutability contract
            var result = new byte[input.Length];
            Array.Copy(input, result, input.Length);
            return result;
        }

        /// <inheritdoc/>
        protected override Task<byte[]> CompressAsyncCore(byte[] input, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CompressCore(input));
        }

        /// <inheritdoc/>
        protected override Task<byte[]> DecompressAsyncCore(byte[] input, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(DecompressCore(input));
        }

        /// <inheritdoc/>
        protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
        {
            // Return pass-through stream wrapper
            return new PassThroughStream(output, leaveOpen);
        }

        /// <inheritdoc/>
        protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
        {
            // Return pass-through stream wrapper
            return new PassThroughStream(input, leaveOpen);
        }

        /// <inheritdoc/>
        public override long EstimateCompressedSize(long inputSize)
        {
            // No compression - output size equals input size
            return inputSize;
        }

        /// <inheritdoc/>
        public override bool ShouldCompress(ReadOnlySpan<byte> input)
        {
            // Null strategy never compresses
            return false;
        }

        /// <summary>
        /// Pass-through stream that doesn't modify data.
        /// </summary>
        private sealed class PassThroughStream : Stream
        {
            private readonly Stream _baseStream;
            private readonly bool _leaveOpen;

            public PassThroughStream(Stream baseStream, bool leaveOpen)
            {
                _baseStream = baseStream ?? throw new ArgumentNullException(nameof(baseStream));
                _leaveOpen = leaveOpen;
            }

            public override bool CanRead => _baseStream.CanRead;
            public override bool CanSeek => _baseStream.CanSeek;
            public override bool CanWrite => _baseStream.CanWrite;
            public override long Length => _baseStream.Length;
            public override long Position
            {
                get => _baseStream.Position;
                set => _baseStream.Position = value;
            }

            public override void Flush() => _baseStream.Flush();
            public override Task FlushAsync(CancellationToken cancellationToken) => _baseStream.FlushAsync(cancellationToken);

            public override int Read(byte[] buffer, int offset, int count) => _baseStream.Read(buffer, offset, count);
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                _baseStream.ReadAsync(buffer, offset, count, cancellationToken);

            public override void Write(byte[] buffer, int offset, int count) => _baseStream.Write(buffer, offset, count);
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                _baseStream.WriteAsync(buffer, offset, count, cancellationToken);

            public override long Seek(long offset, SeekOrigin origin) => _baseStream.Seek(offset, origin);
            public override void SetLength(long value) => _baseStream.SetLength(value);

            protected override void Dispose(bool disposing)
            {
                if (disposing && !_leaveOpen)
                {
                    _baseStream.Dispose();
                }
                base.Dispose(disposing);
            }
        }
    }
}
