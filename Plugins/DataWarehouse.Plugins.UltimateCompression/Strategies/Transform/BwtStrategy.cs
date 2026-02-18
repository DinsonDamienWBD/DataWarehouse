using System;
using System.IO;
using System.Linq;
using DataWarehouse.SDK.Contracts.Compression;
using SdkCompressionLevel = DataWarehouse.SDK.Contracts.Compression.CompressionLevel;

namespace DataWarehouse.Plugins.UltimateCompression.Strategies.Transform
{
    /// <summary>
    /// Compression strategy implementing the Burrows-Wheeler Transform (BWT).
    /// BWT is a reversible permutation that clusters similar characters together,
    /// creating long runs that are highly compressible by subsequent encoders.
    /// </summary>
    /// <remarks>
    /// The Burrows-Wheeler Transform rearranges a string into runs of similar characters
    /// without losing information needed for reversal. It works by generating all cyclic
    /// rotations of the input, sorting them lexicographically, and taking the last column.
    /// The transform outputs this last column plus the index of the original string in the
    /// sorted list (primary index). This is the core transform used in BZip2 compression.
    ///
    /// Note: BWT alone does not compress data - it's a pre-processing step that makes
    /// data more compressible. In practice, it's combined with Move-to-Front encoding
    /// and entropy coding (like Huffman or arithmetic coding) for actual compression.
    /// </remarks>
    public sealed class BwtStrategy : CompressionStrategyBase
    {
        private const int MaxBlockSize = 900000; // BWT is expensive O(n log n), limit block size

        /// <summary>
        /// Initializes a new instance of the <see cref="BwtStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public BwtStrategy() : base(SdkCompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "BWT",
            TypicalCompressionRatio = 0.45,
            CompressionSpeed = 3,
            DecompressionSpeed = 3,
            CompressionMemoryUsage = 8 * 1024 * 1024,
            DecompressionMemoryUsage = 4 * 1024 * 1024,
            SupportsStreaming = false,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 64,
            OptimalBlockSize = 256 * 1024
        };

        /// <inheritdoc/>
        protected override byte[] CompressCore(byte[] input)
        {
            if (input == null || input.Length == 0)
                return Array.Empty<byte>();

            // Limit input size to prevent excessive memory usage
            if (input.Length > MaxBlockSize)
                throw new ArgumentException($"BWT input size limited to {MaxBlockSize} bytes", nameof(input));

            int n = input.Length;
            var rotations = new int[n];

            // Initialize rotation indices
            for (int i = 0; i < n; i++)
                rotations[i] = i;

            // Sort rotations lexicographically using custom comparison
            Array.Sort(rotations, (a, b) =>
            {
                for (int i = 0; i < n; i++)
                {
                    int cmp = input[(a + i) % n].CompareTo(input[(b + i) % n]);
                    if (cmp != 0)
                        return cmp;
                }
                return 0;
            });

            // Build output: last column of sorted rotations
            var output = new byte[n];
            int primaryIndex = -1;

            for (int i = 0; i < n; i++)
            {
                // Last character of rotation starting at rotations[i]
                output[i] = input[(rotations[i] + n - 1) % n];

                // Find primary index (original string position in sorted list)
                if (rotations[i] == 0)
                    primaryIndex = i;
            }

            // Create final output with header: primary index (4 bytes) + transformed data
            using var result = new MemoryStream(65536);
            using var writer = new BinaryWriter(result);

            writer.Write(primaryIndex);
            writer.Write(output);

            return result.ToArray();
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            if (input == null || input.Length <= 4)
                return Array.Empty<byte>();

            using var inputStream = new MemoryStream(input);
            using var reader = new BinaryReader(inputStream);

            // Read header
            int primaryIndex = reader.ReadInt32();
            int n = input.Length - 4;
            var lastColumn = reader.ReadBytes(n);

            // Inverse BWT using LF-mapping
            // First, compute the first column (F) by sorting last column (L)
            var firstColumn = new byte[n];
            Array.Copy(lastColumn, firstColumn, n);
            Array.Sort(firstColumn);

            // Build LF-mapping: for each position i in L, find where it appears in F
            var lfMap = new int[n];
            var counts = new int[256];

            // Count occurrences of each byte in first column
            for (int i = 0; i < n; i++)
                counts[firstColumn[i]]++;

            // Compute cumulative counts (starting positions for each byte value)
            var cumulative = new int[256];
            int sum = 0;
            for (int i = 0; i < 256; i++)
            {
                cumulative[i] = sum;
                sum += counts[i];
            }

            // Build LF-mapping
            var tempCounts = new int[256];
            Array.Copy(cumulative, tempCounts, 256);

            for (int i = 0; i < n; i++)
            {
                byte b = lastColumn[i];
                lfMap[i] = tempCounts[b]++;
            }

            // Reconstruct original string by following LF-mapping from primary index
            var output = new byte[n];
            int idx = primaryIndex;

            for (int i = n - 1; i >= 0; i--)
            {
                output[i] = lastColumn[idx];
                idx = lfMap[idx];
            }

            return output;
        }

        /// <inheritdoc/>
        protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
        {
            return new BwtCompressionStream(output, leaveOpen);
        }

        /// <inheritdoc/>
        protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
        {
            return new BwtDecompressionStream(input, leaveOpen);
        }

        /// <inheritdoc/>
        public override long EstimateCompressedSize(long inputSize)
        {
            // BWT itself doesn't compress - it just rearranges
            // Output size is input size + 4 bytes for primary index
            return inputSize + 4;
        }

        /// <summary>
        /// Stream wrapper for BWT compression.
        /// </summary>
        private sealed class BwtCompressionStream : Stream
        {
            private readonly Stream _baseStream;
            private readonly bool _leaveOpen;
            private readonly MemoryStream _buffer;

            public BwtCompressionStream(Stream output, bool leaveOpen)
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
                var strategy = new BwtStrategy();
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
        /// Stream wrapper for BWT decompression.
        /// </summary>
        private sealed class BwtDecompressionStream : Stream
        {
            private readonly Stream _baseStream;
            private readonly bool _leaveOpen;
            private MemoryStream _decompressedBuffer = new MemoryStream(65536);
            private bool _initialized;

            public BwtDecompressionStream(Stream input, bool leaveOpen)
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
                    var compressed = new byte[_baseStream.Length];
                    _baseStream.ReadExactly(compressed, 0, compressed.Length);
                    var strategy = new BwtStrategy();
                    var decompressed = strategy.DecompressCore(compressed);
                    _decompressedBuffer = new MemoryStream(decompressed);
                    _initialized = true;
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                EnsureInitialized();
                return _decompressedBuffer.Read(buffer, offset, count);
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
