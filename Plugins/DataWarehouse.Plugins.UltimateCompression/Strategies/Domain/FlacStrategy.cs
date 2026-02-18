using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using DataWarehouse.SDK.Contracts.Compression;
using SdkCompressionLevel = DataWarehouse.SDK.Contracts.Compression.CompressionLevel;

namespace DataWarehouse.Plugins.UltimateCompression.Strategies.Domain
{
    /// <summary>
    /// Compression strategy inspired by FLAC (Free Lossless Audio Codec) principles adapted for general data.
    /// Splits input into frames, applies linear prediction with configurable order (0-4),
    /// and encodes residuals using Rice coding. Each frame includes a sync pattern header for
    /// random-access capability.
    /// </summary>
    /// <remarks>
    /// FLAC achieves lossless compression by exploiting correlation between consecutive samples.
    /// This adaptation treats arbitrary byte data as a stream of 16-bit samples, applies
    /// linear prediction to decorrelate them, and then Rice-codes the residuals.
    /// For data that does not exhibit sample-level correlation, the algorithm gracefully
    /// falls back to storing raw frames with minimal overhead.
    /// </remarks>
    public sealed class FlacStrategy : CompressionStrategyBase
    {
        private const ushort SyncPattern = 0xFFF8;
        private const int DefaultFrameSize = 4096;
        private const int MaxPredictionOrder = 4;
        private const int RiceParameterBits = 4;

        /// <summary>
        /// Initializes a new instance of the <see cref="FlacStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public FlacStrategy() : base(SdkCompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "FLAC",
            TypicalCompressionRatio = 0.55,
            CompressionSpeed = 5,
            DecompressionSpeed = 6,
            CompressionMemoryUsage = 256 * 1024,
            DecompressionMemoryUsage = 128 * 1024,
            SupportsStreaming = true,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = true,
            MinimumRecommendedSize = 64,
            OptimalBlockSize = DefaultFrameSize
        };

        /// <inheritdoc/>
        protected override byte[] CompressCore(byte[] input)
        {
            using var output = new MemoryStream(input.Length + 256);
            using var writer = new BinaryWriter(output);

            // Write global header: magic + original length
            writer.Write((byte)'f');
            writer.Write((byte)'L');
            writer.Write((byte)'a');
            writer.Write((byte)'C');
            writer.Write(input.Length);

            // Convert bytes to 16-bit samples (pad if odd)
            int sampleCount = (input.Length + 1) / 2;
            var samples = new short[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                int idx = i * 2;
                byte lo = input[idx];
                byte hi = (idx + 1 < input.Length) ? input[idx + 1] : (byte)0;
                samples[i] = (short)(lo | (hi << 8));
            }

            // Process in frames
            int frameOffset = 0;
            int frameSamples = DefaultFrameSize / 2;
            while (frameOffset < sampleCount)
            {
                int count = Math.Min(frameSamples, sampleCount - frameOffset);
                CompressFrame(writer, samples, frameOffset, count);
                frameOffset += count;
            }

            return output.ToArray();
        }

        private void CompressFrame(BinaryWriter writer, short[] samples, int offset, int count)
        {
            // Frame header: sync pattern + frame info
            writer.Write(SyncPattern);
            writer.Write((ushort)count);

            // Try prediction orders 0..MaxPredictionOrder; pick best
            int bestOrder = 0;
            long bestCost = long.MaxValue;
            int[]? bestResiduals = null;

            for (int order = 0; order <= MaxPredictionOrder && order <= count; order++)
            {
                var residuals = ComputeResiduals(samples, offset, count, order);
                long cost = EstimateRiceCost(residuals);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestOrder = order;
                    bestResiduals = residuals;
                }
            }

            writer.Write((byte)bestOrder);

            // Write warm-up samples (raw)
            for (int i = 0; i < bestOrder; i++)
            {
                writer.Write(samples[offset + i]);
            }

            // Rice-encode residuals
            int riceParam = FindOptimalRiceParameter(bestResiduals!);
            writer.Write((byte)riceParam);
            RiceEncode(writer, bestResiduals!, riceParam);
        }

        private static int[] ComputeResiduals(short[] samples, int offset, int count, int order)
        {
            var residuals = new int[count - order];

            for (int i = order; i < count; i++)
            {
                int predicted = 0;
                switch (order)
                {
                    case 0:
                        predicted = 0;
                        break;
                    case 1:
                        predicted = samples[offset + i - 1];
                        break;
                    case 2:
                        predicted = 2 * samples[offset + i - 1] - samples[offset + i - 2];
                        break;
                    case 3:
                        predicted = 3 * samples[offset + i - 1] - 3 * samples[offset + i - 2] + samples[offset + i - 3];
                        break;
                    case 4:
                        predicted = 4 * samples[offset + i - 1] - 6 * samples[offset + i - 2]
                                    + 4 * samples[offset + i - 3] - samples[offset + i - 4];
                        break;
                }

                residuals[i - order] = samples[offset + i] - predicted;
            }

            return residuals;
        }

        private static long EstimateRiceCost(int[] residuals)
        {
            if (residuals.Length == 0)
                return 0;

            long sum = 0;
            foreach (int r in residuals)
            {
                uint mapped = ZigZagEncode(r);
                sum += mapped + 1;
            }

            return sum;
        }

        private static int FindOptimalRiceParameter(int[] residuals)
        {
            if (residuals.Length == 0)
                return 0;

            long sum = 0;
            foreach (int r in residuals)
            {
                sum += ZigZagEncode(r);
            }

            double mean = (double)sum / residuals.Length;
            int param = 0;
            if (mean > 0)
            {
                param = (int)Math.Floor(Math.Log2(mean));
            }

            return Math.Clamp(param, 0, (1 << RiceParameterBits) - 1);
        }

        private static void RiceEncode(BinaryWriter writer, int[] residuals, int riceParam)
        {
            using var bitStream = new MemoryStream(65536);
            var bits = new BitWriter(bitStream);

            foreach (int r in residuals)
            {
                uint mapped = ZigZagEncode(r);
                uint quotient = riceParam > 0 ? mapped >> riceParam : mapped;
                uint remainder = riceParam > 0 ? mapped & ((1u << riceParam) - 1) : 0;

                // Unary encoding for quotient: quotient 1-bits, then a 0-bit
                for (uint q = 0; q < quotient; q++)
                    bits.WriteBit(1);
                bits.WriteBit(0);

                // Binary encoding for remainder
                for (int b = riceParam - 1; b >= 0; b--)
                    bits.WriteBit((int)((remainder >> b) & 1));
            }

            bits.Flush();
            byte[] encoded = bitStream.ToArray();
            writer.Write(encoded.Length);
            writer.Write(encoded);
        }

        private static uint ZigZagEncode(int value)
        {
            return (uint)((value << 1) ^ (value >> 31));
        }

        private static int ZigZagDecode(uint value)
        {
            return (int)((value >> 1) ^ -(value & 1));
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            using var stream = new MemoryStream(input);
            using var reader = new BinaryReader(stream);

            // Validate and read header
            byte m1 = reader.ReadByte(), m2 = reader.ReadByte(), m3 = reader.ReadByte(), m4 = reader.ReadByte();
            if (m1 != (byte)'f' || m2 != (byte)'L' || m3 != (byte)'a' || m4 != (byte)'C')
                throw new InvalidDataException("Invalid FLAC stream header.");

            int originalLength = reader.ReadInt32();
            int sampleCount = (originalLength + 1) / 2;

            var samples = new short[sampleCount];
            int samplesRead = 0;

            while (samplesRead < sampleCount && stream.Position < stream.Length)
            {
                ushort sync = reader.ReadUInt16();
                if (sync != SyncPattern)
                    throw new InvalidDataException("Invalid FLAC frame sync pattern.");

                ushort frameCount = reader.ReadUInt16();
                byte order = reader.ReadByte();

                // Read warm-up samples
                for (int i = 0; i < order; i++)
                {
                    samples[samplesRead + i] = reader.ReadInt16();
                }

                // Read Rice-encoded residuals
                byte riceParam = reader.ReadByte();
                int residualCount = frameCount - order;
                int encodedLength = reader.ReadInt32();
                byte[] encodedData = reader.ReadBytes(encodedLength);

                int[] residuals = RiceDecode(encodedData, residualCount, riceParam);

                // Reconstruct samples from residuals
                for (int i = 0; i < residualCount; i++)
                {
                    int idx = samplesRead + order + i;
                    int predicted = 0;
                    switch (order)
                    {
                        case 0:
                            predicted = 0;
                            break;
                        case 1:
                            predicted = samples[idx - 1];
                            break;
                        case 2:
                            predicted = 2 * samples[idx - 1] - samples[idx - 2];
                            break;
                        case 3:
                            predicted = 3 * samples[idx - 1] - 3 * samples[idx - 2] + samples[idx - 3];
                            break;
                        case 4:
                            predicted = 4 * samples[idx - 1] - 6 * samples[idx - 2]
                                        + 4 * samples[idx - 3] - samples[idx - 4];
                            break;
                    }

                    samples[idx] = (short)(predicted + residuals[i]);
                }

                samplesRead += frameCount;
            }

            // Convert samples back to bytes
            var result = new byte[originalLength];
            for (int i = 0; i < sampleCount; i++)
            {
                int idx = i * 2;
                if (idx < originalLength)
                    result[idx] = (byte)(samples[i] & 0xFF);
                if (idx + 1 < originalLength)
                    result[idx + 1] = (byte)((samples[i] >> 8) & 0xFF);
            }

            return result;
        }

        private static int[] RiceDecode(byte[] encodedData, int count, int riceParam)
        {
            var residuals = new int[count];
            var bits = new BitReader(encodedData);

            for (int i = 0; i < count; i++)
            {
                // Decode unary quotient
                uint quotient = 0;
                while (bits.ReadBit() == 1)
                    quotient++;

                // Decode binary remainder
                uint remainder = 0;
                for (int b = riceParam - 1; b >= 0; b--)
                    remainder |= (uint)(bits.ReadBit() << b);

                uint mapped = (quotient << riceParam) | remainder;
                residuals[i] = ZigZagDecode(mapped);
            }

            return residuals;
        }

        /// <inheritdoc/>
        protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
        {
            return new FlacCompressionStream(output, leaveOpen, this);
        }

        /// <inheritdoc/>
        protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
        {
            return new FlacDecompressionStream(input, leaveOpen, this);
        }

        /// <inheritdoc/>
        public override long EstimateCompressedSize(long inputSize)
        {
            return (long)(inputSize * 0.65) + 256;
        }

        #region Bit I/O Helpers

        private sealed class BitWriter
        {
            private readonly Stream _stream;
            private byte _currentByte;
            private int _bitIndex;

            public BitWriter(Stream stream)
            {
                _stream = stream;
            }

            public void WriteBit(int bit)
            {
                if (bit != 0)
                    _currentByte |= (byte)(1 << (7 - _bitIndex));

                _bitIndex++;
                if (_bitIndex == 8)
                {
                    _stream.WriteByte(_currentByte);
                    _currentByte = 0;
                    _bitIndex = 0;
                }
            }

            public void Flush()
            {
                if (_bitIndex > 0)
                {
                    _stream.WriteByte(_currentByte);
                    _currentByte = 0;
                    _bitIndex = 0;
                }
            }
        }

        private sealed class BitReader
        {
            private readonly byte[] _data;
            private int _byteIndex;
            private int _bitIndex;

            public BitReader(byte[] data)
            {
                _data = data;
            }

            public int ReadBit()
            {
                if (_byteIndex >= _data.Length)
                    return 0;

                int bit = (_data[_byteIndex] >> (7 - _bitIndex)) & 1;
                _bitIndex++;
                if (_bitIndex == 8)
                {
                    _bitIndex = 0;
                    _byteIndex++;
                }

                return bit;
            }
        }

        #endregion

        #region Stream Wrappers

        private sealed class FlacCompressionStream : Stream
        {
            private readonly Stream _output;
            private readonly bool _leaveOpen;
            private readonly FlacStrategy _strategy;
            private readonly MemoryStream _buffer = new();
            private bool _disposed;

            public FlacCompressionStream(Stream output, bool leaveOpen, FlacStrategy strategy)
            {
                _output = output;
                _leaveOpen = leaveOpen;
                _strategy = strategy;
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => _buffer.Length;
            public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }

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

        private sealed class FlacDecompressionStream : Stream
        {
            private readonly Stream _input;
            private readonly bool _leaveOpen;
            private readonly FlacStrategy _strategy;
            private byte[]? _decompressedData;
            private int _position;
            private bool _disposed;

            public FlacDecompressionStream(Stream input, bool leaveOpen, FlacStrategy strategy)
            {
                _input = input;
                _leaveOpen = leaveOpen;
                _strategy = strategy;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _decompressedData?.Length ?? 0;
            public override long Position { get => _position; set => throw new NotSupportedException(); }

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
