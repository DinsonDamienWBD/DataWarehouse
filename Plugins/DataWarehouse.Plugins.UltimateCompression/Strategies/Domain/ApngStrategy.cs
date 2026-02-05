using System;
using System.IO;
using System.IO.Compression;
using DataWarehouse.SDK.Contracts.Compression;
using SdkCompressionLevel = DataWarehouse.SDK.Contracts.Compression.CompressionLevel;

namespace DataWarehouse.Plugins.UltimateCompression.Strategies.Domain
{
    /// <summary>
    /// Compression strategy inspired by APNG/PNG-style filtering and compression for general data.
    /// Applies PNG prediction filters (None, Sub, Up, Average, Paeth) per row of data,
    /// then compresses the filtered output with Deflate. Data is arranged into 256-byte rows
    /// to enable row-based prediction filtering.
    /// </summary>
    /// <remarks>
    /// PNG achieves high compression by applying reversible prediction filters that exploit
    /// spatial redundancy before entropy coding. This adaptation arranges arbitrary data into
    /// a 2D grid (256 bytes per row), selects the optimal filter per row by testing all five
    /// PNG filter types and choosing the one with minimum absolute sum, then Deflate-compresses
    /// the filtered output. This approach is particularly effective for data with local
    /// byte-level correlations.
    /// </remarks>
    public sealed class ApngStrategy : CompressionStrategyBase
    {
        private const int RowWidth = 256;

        /// <summary>
        /// Initializes a new instance of the <see cref="ApngStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public ApngStrategy() : base(SdkCompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "APNG",
            TypicalCompressionRatio = 0.38,
            CompressionSpeed = 4,
            DecompressionSpeed = 6,
            CompressionMemoryUsage = 512 * 1024,
            DecompressionMemoryUsage = 256 * 1024,
            SupportsStreaming = true,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 64,
            OptimalBlockSize = 64 * 1024
        };

        /// <inheritdoc/>
        protected override byte[] CompressCore(byte[] input)
        {
            int rowCount = (input.Length + RowWidth - 1) / RowWidth;
            // Each filtered row: 1 byte filter type + RowWidth bytes of filtered data
            var filtered = new byte[rowCount * (1 + RowWidth)];

            var prevRow = new byte[RowWidth];
            var currentRow = new byte[RowWidth];

            for (int row = 0; row < rowCount; row++)
            {
                // Extract current row (may be partial at end)
                int srcOffset = row * RowWidth;
                int srcLen = Math.Min(RowWidth, input.Length - srcOffset);
                Array.Clear(currentRow, 0, RowWidth);
                Array.Copy(input, srcOffset, currentRow, 0, srcLen);

                // Try all filter types, choose the one with minimum absolute sum
                byte bestFilter = 0;
                long bestSum = long.MaxValue;
                byte[]? bestFiltered = null;

                for (byte filterType = 0; filterType <= 4; filterType++)
                {
                    var candidate = ApplyFilter(filterType, currentRow, prevRow);
                    long sum = AbsoluteSum(candidate);
                    if (sum < bestSum)
                    {
                        bestSum = sum;
                        bestFilter = filterType;
                        bestFiltered = candidate;
                    }
                }

                int destOffset = row * (1 + RowWidth);
                filtered[destOffset] = bestFilter;
                Array.Copy(bestFiltered!, 0, filtered, destOffset + 1, RowWidth);

                Array.Copy(currentRow, prevRow, RowWidth);
            }

            // Compress filtered data with Deflate
            using var output = new MemoryStream();
            using var writer = new BinaryWriter(output);

            // Header: magic + original length + row count
            writer.Write((byte)'A');
            writer.Write((byte)'P');
            writer.Write((byte)'N');
            writer.Write((byte)'G');
            writer.Write(input.Length);
            writer.Write(rowCount);

            using (var deflate = new DeflateStream(output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(filtered, 0, filtered.Length);
            }

            return output.ToArray();
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            using var stream = new MemoryStream(input);
            using var reader = new BinaryReader(stream);

            // Validate header
            byte m1 = reader.ReadByte(), m2 = reader.ReadByte(), m3 = reader.ReadByte(), m4 = reader.ReadByte();
            if (m1 != (byte)'A' || m2 != (byte)'P' || m3 != (byte)'N' || m4 != (byte)'G')
                throw new InvalidDataException("Invalid APNG stream header.");

            int originalLength = reader.ReadInt32();
            int rowCount = reader.ReadInt32();

            // Decompress filtered data
            using var deflate = new DeflateStream(stream, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true);
            var filtered = new byte[rowCount * (1 + RowWidth)];
            int totalRead = 0;
            while (totalRead < filtered.Length)
            {
                int bytesRead = deflate.Read(filtered, totalRead, filtered.Length - totalRead);
                if (bytesRead == 0) break;
                totalRead += bytesRead;
            }

            // Reverse filters
            var result = new byte[originalLength];
            var prevRow = new byte[RowWidth];

            for (int row = 0; row < rowCount; row++)
            {
                int srcOffset = row * (1 + RowWidth);
                byte filterType = filtered[srcOffset];
                var filteredRow = new byte[RowWidth];
                Array.Copy(filtered, srcOffset + 1, filteredRow, 0, RowWidth);

                var reconstructed = ReverseFilter(filterType, filteredRow, prevRow);

                int destOffset = row * RowWidth;
                int copyLen = Math.Min(RowWidth, originalLength - destOffset);
                if (copyLen > 0)
                    Array.Copy(reconstructed, 0, result, destOffset, copyLen);

                Array.Copy(reconstructed, prevRow, RowWidth);
            }

            return result;
        }

        /// <summary>
        /// Applies the specified PNG filter to a row of data.
        /// </summary>
        private static byte[] ApplyFilter(byte filterType, byte[] row, byte[] prevRow)
        {
            var result = new byte[RowWidth];

            for (int x = 0; x < RowWidth; x++)
            {
                byte raw = row[x];
                byte a = x > 0 ? row[x - 1] : (byte)0;       // Left
                byte b = prevRow[x];                             // Up
                byte c = x > 0 ? prevRow[x - 1] : (byte)0;    // Upper-left

                result[x] = filterType switch
                {
                    0 => raw,                                                    // None
                    1 => (byte)(raw - a),                                        // Sub
                    2 => (byte)(raw - b),                                        // Up
                    3 => (byte)(raw - (byte)((a + b) / 2)),                      // Average
                    4 => (byte)(raw - PaethPredictor(a, b, c)),                  // Paeth
                    _ => raw
                };
            }

            return result;
        }

        /// <summary>
        /// Reverses the specified PNG filter on a row of filtered data.
        /// </summary>
        private static byte[] ReverseFilter(byte filterType, byte[] filteredRow, byte[] prevRow)
        {
            var result = new byte[RowWidth];

            for (int x = 0; x < RowWidth; x++)
            {
                byte filtered = filteredRow[x];
                byte a = x > 0 ? result[x - 1] : (byte)0;      // Left (already reconstructed)
                byte b = prevRow[x];                              // Up
                byte c = x > 0 ? prevRow[x - 1] : (byte)0;     // Upper-left

                result[x] = filterType switch
                {
                    0 => filtered,                                               // None
                    1 => (byte)(filtered + a),                                   // Sub
                    2 => (byte)(filtered + b),                                   // Up
                    3 => (byte)(filtered + (byte)((a + b) / 2)),                 // Average
                    4 => (byte)(filtered + PaethPredictor(a, b, c)),             // Paeth
                    _ => filtered
                };
            }

            return result;
        }

        /// <summary>
        /// PNG Paeth predictor function: selects the neighbor (a, b, or c) closest
        /// to the linear prediction p = a + b - c.
        /// </summary>
        private static byte PaethPredictor(byte a, byte b, byte c)
        {
            int p = a + b - c;
            int pa = Math.Abs(p - a);
            int pb = Math.Abs(p - b);
            int pc = Math.Abs(p - c);

            if (pa <= pb && pa <= pc) return a;
            if (pb <= pc) return b;
            return c;
        }

        private static long AbsoluteSum(byte[] data)
        {
            long sum = 0;
            foreach (byte b in data)
            {
                int signed = (sbyte)b;
                sum += Math.Abs(signed);
            }
            return sum;
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
            return (long)(inputSize * 0.50) + 256;
        }

        #region Stream Wrappers

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
            public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }

            public override void Write(byte[] buffer, int offset, int count) => _buffer.Write(buffer, offset, count);

            public override void Flush()
            {
                if (_buffer.Length == 0) return;
                byte[] compressed = _compressFunc(_buffer.ToArray());
                _output.Write(compressed, 0, compressed.Length);
                _output.Flush();
                _buffer.SetLength(0);
            }

            protected override void Dispose(bool disposing)
            {
                if (!_disposed && disposing)
                {
                    Flush();
                    if (!_leaveOpen) _output.Dispose();
                    _buffer.Dispose();
                    _disposed = true;
                }
                base.Dispose(disposing);
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        private sealed class BufferedDecompressionStream : Stream
        {
            private readonly Stream _input;
            private readonly bool _leaveOpen;
            private readonly Func<byte[], byte[]> _decompressFunc;
            private byte[]? _data;
            private int _position;
            private bool _disposed;

            public BufferedDecompressionStream(Stream input, bool leaveOpen, Func<byte[], byte[]> decompressFunc)
            {
                _input = input;
                _leaveOpen = leaveOpen;
                _decompressFunc = decompressFunc;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _data?.Length ?? 0;
            public override long Position { get => _position; set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count)
            {
                EnsureDecompressed();
                if (_data == null || _position >= _data.Length) return 0;
                int available = Math.Min(count, _data.Length - _position);
                Array.Copy(_data, _position, buffer, offset, available);
                _position += available;
                return available;
            }

            private void EnsureDecompressed()
            {
                if (_data != null) return;
                using var ms = new MemoryStream();
                _input.CopyTo(ms);
                _data = _decompressFunc(ms.ToArray());
            }

            protected override void Dispose(bool disposing)
            {
                if (!_disposed && disposing)
                {
                    if (!_leaveOpen) _input.Dispose();
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
