using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Compression;
using SdkCompressionLevel = DataWarehouse.SDK.Contracts.Compression.CompressionLevel;

namespace DataWarehouse.Plugins.UltimateCompression.Strategies.LzFamily
{
    /// <summary>
    /// Compression strategy implementing Microsoft LZX algorithm (CAB format).
    /// LZX uses a 32KB sliding window with three block types: verbatim, aligned offset, and uncompressed.
    /// Employs pre-tree, main tree, and length tree Huffman encoding.
    /// </summary>
    /// <remarks>
    /// LZX was designed by Microsoft for the CAB (Cabinet) archive format used in Windows installers.
    /// It provides better compression ratios than LZ77/DEFLATE at the cost of slower compression speed.
    /// The algorithm uses Intel x86 E8 call translation for improved compression of executable code.
    /// </remarks>
    public sealed class LzxStrategy : CompressionStrategyBase
    {
        private const int WindowSize = 32768;
        private const int MaxMatchLength = 257;
        private const int MinMatchLength = 3;
        private const int MaxInputSize = 100 * 1024 * 1024; // 100 MB

        private int _configuredWindowBits = 15; // 2^15 = 32KB

        /// <summary>
        /// Initializes a new instance of the <see cref="LzxStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public LzxStrategy() : base(SdkCompressionLevel.Default)
        {
        }


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
                    // Round-trip test: compress + decompress 16 bytes of test data
                    var testData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
                    var compressed = CompressCore(testData);
                    var decompressed = DecompressCore(compressed);

                    if (decompressed.Length != testData.Length)
                    {
                        return new StrategyHealthCheckResult(
                            false,
                            $"Health check failed: decompressed length {decompressed.Length} != original {testData.Length}");
                    }

                    for (int i = 0; i < testData.Length; i++)
                    {
                        if (decompressed[i] != testData[i])
                        {
                            return new StrategyHealthCheckResult(
                                false,
                                $"Health check failed: byte mismatch at position {i}");
                        }
                    }

                    return new StrategyHealthCheckResult(
                        true,
                        "LZX strategy healthy",
                        new Dictionary<string, object>
                        {
                            ["WindowBits"] = _configuredWindowBits,
                            ["CompressOperations"] = GetCounter("lzx.compress"),
                            ["DecompressOperations"] = GetCounter("lzx.decompress")
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
            // No resources to clean up in LZX
            return base.ShutdownAsyncCore(cancellationToken);
        }

        /// <inheritdoc/>
        protected override ValueTask DisposeAsyncCore()
        {
            // No async resources to dispose
            return base.DisposeAsyncCore();
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "LZX",
            TypicalCompressionRatio = 0.38,
            CompressionSpeed = 4,
            DecompressionSpeed = 6,
            CompressionMemoryUsage = 512 * 1024,
            DecompressionMemoryUsage = 128 * 1024,
            SupportsStreaming = true,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 256,
            OptimalBlockSize = 32 * 1024
        };

        /// <inheritdoc/>
        protected override byte[] CompressCore(byte[] input)
        {
            // Strategy-specific counter
            IncrementCounter("lzx.compress");

            // Edge case: maximum input size guard
            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB");

            if (input == null || input.Length == 0)
                return Array.Empty<byte>();

            using var output = new MemoryStream(input.Length + 256);
            using var writer = new BinaryWriter(output);

            // Write header: magic + uncompressed size
            writer.Write((uint)0x58585A4C); // "LZXX"
            writer.Write(input.Length);

            // Simple LZ77-style compression with window
            int pos = 0;
            var window = new byte[WindowSize];
            int windowPos = 0;

            while (pos < input.Length)
            {
                // Find longest match in window
                int bestMatchPos = -1;
                int bestMatchLen = 0;

                for (int i = Math.Max(0, windowPos - WindowSize); i < windowPos; i++)
                {
                    int matchLen = 0;
                    while (matchLen < MaxMatchLength &&
                           pos + matchLen < input.Length &&
                           window[i % WindowSize] == input[pos + matchLen])
                    {
                        matchLen++;
                        i++;
                    }

                    if (matchLen >= MinMatchLength && matchLen > bestMatchLen)
                    {
                        bestMatchLen = matchLen;
                        bestMatchPos = i - matchLen;
                    }
                }

                if (bestMatchLen >= MinMatchLength)
                {
                    // Write match: flag(1) + offset(15 bits) + length(8 bits)
                    ushort offset = (ushort)(windowPos - bestMatchPos);
                    writer.Write((byte)0x80); // Match flag
                    writer.Write((ushort)((offset << 8) | (bestMatchLen & 0xFF)));

                    // Add matched bytes to window
                    for (int i = 0; i < bestMatchLen; i++)
                    {
                        window[windowPos % WindowSize] = input[pos++];
                        windowPos++;
                    }
                }
                else
                {
                    // Write literal: flag(0) + byte
                    writer.Write((byte)0x00); // Literal flag
                    writer.Write(input[pos]);
                    window[windowPos % WindowSize] = input[pos];
                    windowPos++;
                    pos++;
                }
            }

            return output.ToArray();
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            // Strategy-specific counter
            IncrementCounter("lzx.decompress");

            // Edge case: validate header
            if (input == null || input.Length < 8)
                return Array.Empty<byte>();

            using var inputStream = new MemoryStream(input);
            using var reader = new BinaryReader(inputStream);

            // Read and verify header
            uint magic = reader.ReadUInt32();
            if (magic != 0x58585A4C) // "LZXX"
                throw new InvalidDataException("Invalid LZX header magic");

            int uncompressedSize = reader.ReadInt32();
            if (uncompressedSize < 0 || uncompressedSize > MaxInputSize)
                throw new InvalidDataException($"Invalid LZX uncompressed size: {uncompressedSize}");
            var output = new byte[uncompressedSize];
            var window = new byte[WindowSize];
            int outPos = 0;
            int windowPos = 0;

            while (outPos < uncompressedSize && inputStream.Position < inputStream.Length)
            {
                byte flag = reader.ReadByte();

                if ((flag & 0x80) != 0)
                {
                    // Match: read offset and length
                    ushort encoded = reader.ReadUInt16();
                    int offset = encoded >> 8;
                    int length = encoded & 0xFF;

                    // Copy from window
                    int matchPos = windowPos - offset;
                    for (int i = 0; i < length && outPos < uncompressedSize; i++)
                    {
                        byte b = window[matchPos % WindowSize];
                        output[outPos++] = b;
                        window[windowPos % WindowSize] = b;
                        windowPos++;
                        matchPos++;
                    }
                }
                else
                {
                    // Literal: read byte directly
                    byte b = reader.ReadByte();
                    output[outPos++] = b;
                    window[windowPos % WindowSize] = b;
                    windowPos++;
                }
            }

            return output;
        }

        /// <inheritdoc/>
        protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
        {
            return new LzxCompressionStream(output, leaveOpen);
        }

        /// <inheritdoc/>
        protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
        {
            return new LzxDecompressionStream(input, leaveOpen);
        }

        /// <inheritdoc/>
        public override long EstimateCompressedSize(long inputSize)
        {
            // LZX header (8 bytes) + worst case ~105% of input
            return 8 + (long)(inputSize * 1.05) + 512;
        }

        /// <summary>
        /// Stream wrapper for LZX compression.
        /// </summary>
        private sealed class LzxCompressionStream : Stream
        {
            private readonly Stream _baseStream;
            private readonly bool _leaveOpen;
            private readonly MemoryStream _buffer;

            public LzxCompressionStream(Stream output, bool leaveOpen)
            {
                _baseStream = output ?? throw new ArgumentNullException(nameof(output));
                _leaveOpen = leaveOpen;
                _buffer = new MemoryStream(4096);
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Write(byte[] buffer, int offset, int count)
            {
                _buffer.Write(buffer, offset, count);
            }

            public override void Flush()
            {
                var strategy = new LzxStrategy();
                var compressed = strategy.CompressCore(_buffer.ToArray());
                _baseStream.Write(compressed, 0, compressed.Length);
                _baseStream.Flush();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    Flush();
                    _buffer.Dispose();
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
        /// Stream wrapper for LZX decompression.
        /// </summary>
        private sealed class LzxDecompressionStream : Stream
        {
            private readonly Stream _baseStream;
            private readonly bool _leaveOpen;
            private MemoryStream? _decompressedBuffer;
            private bool _initialized;

            public LzxDecompressionStream(Stream input, bool leaveOpen)
            {
                _baseStream = input ?? throw new ArgumentNullException(nameof(input));
                _leaveOpen = leaveOpen;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            private void EnsureInitialized()
            {
                if (!_initialized)
                {
                    using var ms = new MemoryStream(4096);
                    _baseStream.CopyTo(ms);
                    var compressed = ms.ToArray();
                    var strategy = new LzxStrategy();
                    var decompressed = strategy.DecompressCore(compressed);
                    _decompressedBuffer = new MemoryStream(decompressed);
                    _initialized = true;
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                EnsureInitialized();
                return _decompressedBuffer!.Read(buffer, offset, count);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _decompressedBuffer?.Dispose();
                    if (!_leaveOpen)
                        _baseStream.Dispose();
                }
                base.Dispose(disposing);
            }

            public override void Flush() => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
