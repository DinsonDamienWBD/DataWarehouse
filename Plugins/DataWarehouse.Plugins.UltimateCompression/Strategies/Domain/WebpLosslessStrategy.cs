using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using DataWarehouse.SDK.Contracts.Compression;
using SdkCompressionLevel = DataWarehouse.SDK.Contracts.Compression.CompressionLevel;

namespace DataWarehouse.Plugins.UltimateCompression.Strategies.Domain
{
    /// <summary>
    /// Compression strategy inspired by WebP lossless principles for general data.
    /// Implements a predict-subtract-entropy encode pipeline with 4 predictor modes,
    /// transform coding, and a Huffman-backed entropy coding backend.
    /// </summary>
    /// <remarks>
    /// WebP lossless achieves high compression ratios by combining spatial prediction
    /// (removing local correlation), color transforms, and entropy coding. This adaptation
    /// applies similar principles to arbitrary byte data: prediction removes local redundancy,
    /// a subtract/transform step creates a residual stream, and Huffman coding (via Deflate)
    /// compresses the residuals efficiently.
    ///
    /// Predictor modes:
    /// <list type="bullet">
    ///   <item>Mode 0: No prediction (raw bytes)</item>
    ///   <item>Mode 1: Left predictor (byte[i] - byte[i-1])</item>
    ///   <item>Mode 2: Top predictor (using a configurable stride)</item>
    ///   <item>Mode 3: Average of left and top</item>
    /// </list>
    /// </remarks>
    public sealed class WebpLosslessStrategy : CompressionStrategyBase
    {
        private const int BlockSize = 256;

        /// <summary>
        /// Initializes a new instance of the <see cref="WebpLosslessStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public WebpLosslessStrategy() : base(SdkCompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "WebPLossless",
            TypicalCompressionRatio = 0.40,
            CompressionSpeed = 4,
            DecompressionSpeed = 5,
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
            using var output = new MemoryStream(input.Length + 256);
            using var writer = new BinaryWriter(output);

            // Header: magic + original length + stride
            writer.Write((byte)'W');
            writer.Write((byte)'P');
            writer.Write((byte)'L');
            writer.Write((byte)'S');
            writer.Write(input.Length);

            int stride = (int)Math.Ceiling(Math.Sqrt(input.Length));
            if (stride < 1) stride = 1;
            writer.Write(stride);

            // Process in blocks, selecting best predictor per block
            int blockCount = (input.Length + BlockSize - 1) / BlockSize;
            writer.Write(blockCount);

            var allResiduals = new MemoryStream();

            for (int b = 0; b < blockCount; b++)
            {
                int offset = b * BlockSize;
                int length = Math.Min(BlockSize, input.Length - offset);

                byte bestMode = 0;
                long bestCost = long.MaxValue;
                byte[]? bestResidual = null;

                for (byte mode = 0; mode < 4; mode++)
                {
                    var residual = PredictBlock(input, offset, length, stride, mode);
                    long cost = EntropyEstimate(residual);
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        bestMode = mode;
                        bestResidual = residual;
                    }
                }

                // Write block mode
                allResiduals.WriteByte(bestMode);
                allResiduals.Write(bestResidual!, 0, bestResidual!.Length);
            }

            // Entropy-encode all residuals with Deflate (Huffman backend)
            byte[] residualData = allResiduals.ToArray();
            using (var deflate = new DeflateStream(output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(residualData, 0, residualData.Length);
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
            if (m1 != (byte)'W' || m2 != (byte)'P' || m3 != (byte)'L' || m4 != (byte)'S')
                throw new InvalidDataException("Invalid WebPLossless stream header.");

            int originalLength = reader.ReadInt32();
            int stride = reader.ReadInt32();
            int blockCount = reader.ReadInt32();

            // Decompress residuals
            using var deflate = new DeflateStream(stream, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true);
            using var residualStream = new MemoryStream();
            deflate.CopyTo(residualStream);
            byte[] residualData = residualStream.ToArray();

            var result = new byte[originalLength];
            int residualOffset = 0;

            for (int b = 0; b < blockCount; b++)
            {
                int dataOffset = b * BlockSize;
                int length = Math.Min(BlockSize, originalLength - dataOffset);

                byte mode = residualData[residualOffset++];
                var residual = new byte[length];
                Array.Copy(residualData, residualOffset, residual, 0, length);
                residualOffset += length;

                ReconstructBlock(result, dataOffset, length, stride, mode, residual);
            }

            return result;
        }

        /// <summary>
        /// Applies prediction to a block and returns the residual.
        /// </summary>
        private static byte[] PredictBlock(byte[] data, int offset, int length, int stride, byte mode)
        {
            var residual = new byte[length];

            for (int i = 0; i < length; i++)
            {
                int globalIdx = offset + i;
                byte actual = data[globalIdx];
                byte predicted = mode switch
                {
                    0 => 0, // No prediction
                    1 => globalIdx > 0 ? data[globalIdx - 1] : (byte)0, // Left
                    2 => globalIdx >= stride ? data[globalIdx - stride] : (byte)0, // Top
                    3 => (byte)((
                        (globalIdx > 0 ? data[globalIdx - 1] : 0) +
                        (globalIdx >= stride ? data[globalIdx - stride] : 0)) / 2), // Average
                    _ => 0
                };

                residual[i] = (byte)(actual - predicted);
            }

            return residual;
        }

        /// <summary>
        /// Reconstructs a block from its residual and prediction mode.
        /// </summary>
        private static void ReconstructBlock(byte[] data, int offset, int length, int stride, byte mode, byte[] residual)
        {
            for (int i = 0; i < length; i++)
            {
                int globalIdx = offset + i;
                byte predicted = mode switch
                {
                    0 => 0,
                    1 => globalIdx > 0 ? data[globalIdx - 1] : (byte)0,
                    2 => globalIdx >= stride ? data[globalIdx - stride] : (byte)0,
                    3 => (byte)((
                        (globalIdx > 0 ? data[globalIdx - 1] : 0) +
                        (globalIdx >= stride ? data[globalIdx - stride] : 0)) / 2),
                    _ => 0
                };

                data[globalIdx] = (byte)(residual[i] + predicted);
            }
        }

        /// <summary>
        /// Estimates the entropy cost of a byte array (lower is more compressible).
        /// </summary>
        private static long EntropyEstimate(byte[] data)
        {
            if (data.Length == 0) return 0;

            Span<int> freq = stackalloc int[256];
            freq.Clear();
            foreach (byte b in data) freq[b]++;

            long cost = 0;
            foreach (int f in freq)
            {
                if (f > 0)
                {
                    // Approximate bits: -log2(f/n) * f
                    double p = (double)f / data.Length;
                    cost += (long)(f * (-Math.Log2(p)));
                }
            }

            return cost;
        }

        /// <inheritdoc/>
        protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
        {
            return new BufferedCodecStream(output, leaveOpen, CompressCore, isCompression: true);
        }

        /// <inheritdoc/>
        protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
        {
            return new BufferedCodecStream(input, leaveOpen, DecompressCore, isCompression: false);
        }

        /// <inheritdoc/>
        public override long EstimateCompressedSize(long inputSize)
        {
            return (long)(inputSize * 0.50) + 256;
        }

        #region Stream Wrapper

        private sealed class BufferedCodecStream : Stream
        {
            private readonly Stream _inner;
            private readonly bool _leaveOpen;
            private readonly Func<byte[], byte[]> _codec;
            private readonly bool _isCompression;
            private readonly MemoryStream _buffer = new();
            private byte[]? _readData;
            private int _readPos;
            private bool _disposed;

            public BufferedCodecStream(Stream inner, bool leaveOpen, Func<byte[], byte[]> codec, bool isCompression)
            {
                _inner = inner;
                _leaveOpen = leaveOpen;
                _codec = codec;
                _isCompression = isCompression;
            }

            public override bool CanRead => !_isCompression;
            public override bool CanSeek => false;
            public override bool CanWrite => _isCompression;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (!_isCompression) throw new NotSupportedException();
                _buffer.Write(buffer, offset, count);
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_isCompression) throw new NotSupportedException();
                EnsureDecoded();
                if (_readData == null || _readPos >= _readData.Length) return 0;
                int n = Math.Min(count, _readData.Length - _readPos);
                Array.Copy(_readData, _readPos, buffer, offset, n);
                _readPos += n;
                return n;
            }

            private void EnsureDecoded()
            {
                if (_readData != null) return;
                using var ms = new MemoryStream(4096);
                _inner.CopyTo(ms);
                _readData = _codec(ms.ToArray());
            }

            public override void Flush()
            {
                if (!_isCompression || _buffer.Length == 0) return;
                byte[] result = _codec(_buffer.ToArray());
                _inner.Write(result, 0, result.Length);
                _inner.Flush();
                _buffer.SetLength(0);
            }

            protected override void Dispose(bool disposing)
            {
                if (!_disposed && disposing)
                {
                    if (_isCompression) Flush();
                    if (!_leaveOpen) _inner.Dispose();
                    _buffer.Dispose();
                    _disposed = true;
                }
                base.Dispose(disposing);
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        #endregion
    }
}
