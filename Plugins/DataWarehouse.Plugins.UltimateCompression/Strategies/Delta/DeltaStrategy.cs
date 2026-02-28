using System;
using System.Buffers.Binary;
using System.IO;
using DataWarehouse.SDK.Contracts.Compression;

using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;
namespace DataWarehouse.Plugins.UltimateCompression.Strategies.Delta
{
    /// <summary>
    /// Compression strategy implementing basic Delta encoding.
    /// Stores the first byte and then encodes each subsequent byte as the difference
    /// from the previous byte. Effective for data with gradual changes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Delta encoding transforms a sequence of values into a sequence of differences between
    /// consecutive values. When the differences are small, subsequent entropy coding or
    /// compression can achieve better results. This is particularly effective for audio,
    /// sensor data, and other signals with temporal or spatial correlation.
    /// </para>
    /// <para>
    /// This implementation uses simple byte-wise delta encoding with wraparound arithmetic.
    /// The inverse transform reconstructs the original by accumulating the deltas. Delta
    /// encoding is often used as a preprocessing step before Huffman or other entropy coders.
    /// </para>
    /// <para>
    /// Format: [Magic:4][OrigLen:4][FirstByte:1][DeltaBytes:n-1]
    /// </para>
    /// </remarks>
    public sealed class DeltaStrategy : CompressionStrategyBase
    {
        private const int MaxInputSize = 100 * 1024 * 1024; // 100 MB

        private static readonly byte[] Magic = { 0x44, 0x4C, 0x54, 0x41 }; // "DLTA"

        /// <summary>
        /// Initializes a new instance of the <see cref="DeltaStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public DeltaStrategy() : base(CompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "Delta",
            TypicalCompressionRatio = 0.65,
            CompressionSpeed = 9,
            DecompressionSpeed = 10,
            CompressionMemoryUsage = 4L * 1024 * 1024,
            DecompressionMemoryUsage = 4L * 1024 * 1024,
            SupportsStreaming = false, // Header length placeholder is never back-patched in streaming mode
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 8,
            OptimalBlockSize = 256 * 1024
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
                        "Delta strategy healthy",
                        new Dictionary<string, object>
                        {
                            ["CompressOperations"] = GetCounter("delta.compress"),
                            ["DecompressOperations"] = GetCounter("delta.decompress")
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
            IncrementCounter("delta.compress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for Delta");
            using var output = new MemoryStream(input.Length + 256);
            output.Write(Magic, 0, 4);

            var lenBytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lenBytes, input.Length);
            output.Write(lenBytes, 0, 4);

            if (input.Length == 0)
                return output.ToArray();

            // Write first byte as-is
            output.WriteByte(input[0]);

            // Write deltas
            for (int i = 1; i < input.Length; i++)
            {
                byte delta = (byte)(input[i] - input[i - 1]);
                output.WriteByte(delta);
            }

            return output.ToArray();
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            IncrementCounter("delta.decompress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for Delta");
            using var stream = new MemoryStream(input);

            var magicBuf = new byte[4];
            if (stream.Read(magicBuf, 0, 4) != 4 ||
                magicBuf[0] != Magic[0] || magicBuf[1] != Magic[1] ||
                magicBuf[2] != Magic[2] || magicBuf[3] != Magic[3])
            {
                throw new InvalidDataException("Invalid Delta header magic.");
            }

            var lenBuf = new byte[4];
            if (stream.Read(lenBuf, 0, 4) != 4)
                throw new InvalidDataException("Invalid Delta header length.");

            int originalLength = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
            if (originalLength < 0)
                throw new InvalidDataException("Invalid original length in Delta header.");

            if (originalLength == 0)
                return Array.Empty<byte>();

            var result = new byte[originalLength];

            // Read first byte
            int firstByte = stream.ReadByte();
            if (firstByte < 0)
                throw new InvalidDataException("Unexpected end of Delta data.");

            result[0] = (byte)firstByte;

            // Reconstruct from deltas
            for (int i = 1; i < originalLength; i++)
            {
                int delta = stream.ReadByte();
                if (delta < 0)
                    throw new InvalidDataException("Unexpected end of Delta data.");

                result[i] = (byte)(result[i - 1] + delta);
            }

            return result;
        }

        /// <inheritdoc/>
        protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
        {
            return new DeltaCompressionStream(output, leaveOpen);
        }

        /// <inheritdoc/>
        protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
        {
            return new DeltaDecompressionStream(input, leaveOpen);
        }

        /// <inheritdoc/>
        public override long EstimateCompressedSize(long inputSize)
        {
            // Delta encoding doesn't compress, just transforms
            return inputSize + 16;
        }

        #region Streaming Support

        /// <summary>
        /// Streaming compression for Delta encoding.
        /// </summary>
        private sealed class DeltaCompressionStream : Stream
        {
            private readonly Stream _output;
            private readonly bool _leaveOpen;
            private byte _previousByte;
            private bool _firstByte = true;
            private bool _disposed;
            private bool _headerWritten;

            public DeltaCompressionStream(Stream output, bool leaveOpen)
            {
                _output = output;
                _leaveOpen = leaveOpen;
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (!_headerWritten)
                {
                    _output.Write(Magic, 0, 4);
                    // Write placeholder length (will be incorrect for streaming)
                    _output.Write(new byte[4], 0, 4);
                    _headerWritten = true;
                }

                for (int i = 0; i < count; i++)
                {
                    byte currentByte = buffer[offset + i];

                    if (_firstByte)
                    {
                        _output.WriteByte(currentByte);
                        _previousByte = currentByte;
                        _firstByte = false;
                    }
                    else
                    {
                        byte delta = (byte)(currentByte - _previousByte);
                        _output.WriteByte(delta);
                        _previousByte = currentByte;
                    }
                }
            }

            public override void Flush() => _output.Flush();
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
                    if (!_leaveOpen) _output.Dispose();
                }
                base.Dispose(disposing);
            }
        }

        /// <summary>
        /// Streaming decompression for Delta encoding.
        /// </summary>
        private sealed class DeltaDecompressionStream : Stream
        {
            private readonly Stream _input;
            private readonly bool _leaveOpen;
            private byte _previousByte;
            private bool _firstByte = true;
            private bool _disposed;
            private bool _headerRead;

            public DeltaDecompressionStream(Stream input, bool leaveOpen)
            {
                _input = input;
                _leaveOpen = leaveOpen;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (!_headerRead)
                {
                    var magicBuf = new byte[4];
                    if (_input.Read(magicBuf, 0, 4) != 4 ||
                        magicBuf[0] != Magic[0] || magicBuf[1] != Magic[1] ||
                        magicBuf[2] != Magic[2] || magicBuf[3] != Magic[3])
                    {
                        throw new InvalidDataException("Invalid Delta header magic.");
                    }
                    _input.ReadExactly(new byte[4], 0, 4); // Skip length
                    _headerRead = true;
                }

                int totalRead = 0;

                for (int i = 0; i < count; i++)
                {
                    int b = _input.ReadByte();
                    if (b < 0) break;

                    if (_firstByte)
                    {
                        buffer[offset + i] = (byte)b;
                        _previousByte = (byte)b;
                        _firstByte = false;
                    }
                    else
                    {
                        byte reconstructed = (byte)(_previousByte + b);
                        buffer[offset + i] = reconstructed;
                        _previousByte = reconstructed;
                    }

                    totalRead++;
                }

                return totalRead;
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
                    if (!_leaveOpen) _input.Dispose();
                }
                base.Dispose(disposing);
            }
        }

        #endregion
    }
}
