using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using DataWarehouse.SDK.Contracts.Compression;

using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
namespace DataWarehouse.Plugins.UltimateCompression.Strategies.ContextMixing
{
    /// <summary>
    /// Compression strategy implementing PPMd variant H with Secondary Escape Estimation (SEE).
    /// PPMd builds on PPM by using more efficient escape probability estimation through a table
    /// of secondary escape contexts, resulting in better compression ratios and faster adaptation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PPMd variant H was developed by Dmitry Shkarin and introduces Secondary Escape Estimation
    /// (SEE), which maintains statistics for escape frequencies in different contexts. This allows
    /// more accurate escape probability predictions than Method C used in classic PPM.
    /// </para>
    /// <para>
    /// SEE divides contexts into bins based on their characteristics (number of symbols seen,
    /// total count) and maintains separate escape statistics for each bin. When a context needs
    /// to emit an escape symbol, it looks up the appropriate SEE bin to get a better estimate
    /// of the escape probability than the simple Method C formula.
    /// </para>
    /// <para>
    /// Format: [Magic:4][OrigLen:4][MaxOrder:1][CompressedData]
    /// </para>
    /// </remarks>
    public sealed class PpmdStrategy : CompressionStrategyBase
    {
        private const int MaxInputSize = 100 * 1024 * 1024; // 100 MB

        private static readonly byte[] Magic = { 0x50, 0x50, 0x4D, 0x44 }; // "PPMD"
        private const int MaxOrder = 6;
        private const int NumSeeBins = 16;

        /// <summary>
        /// Initializes a new instance of the <see cref="PpmdStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public PpmdStrategy() : base(CompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "PPMd",
            TypicalCompressionRatio = 0.24,
            CompressionSpeed = 2,
            DecompressionSpeed = 2,
            CompressionMemoryUsage = 320L * 1024 * 1024,
            DecompressionMemoryUsage = 320L * 1024 * 1024,
            SupportsStreaming = false,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 128,
            OptimalBlockSize = 2 * 1024 * 1024
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
                        "PPMd strategy healthy",
                        new Dictionary<string, object>
                        {
                            ["CompressOperations"] = GetCounter("ppmd.compress"),
                            ["DecompressOperations"] = GetCounter("ppmd.decompress")
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
            IncrementCounter("ppmd.compress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for PPMd");
            using var output = new MemoryStream(input.Length + 256);
            output.Write(Magic, 0, 4);

            var lenBytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lenBytes, input.Length);
            output.Write(lenBytes, 0, 4);
            output.WriteByte(MaxOrder);

            var encoder = new ArithmeticRangeEncoder(output);
            var model = new PpmdModel(MaxOrder);

            for (int i = 0; i < input.Length; i++)
            {
                byte symbol = input[i];
                EncodeSymbol(encoder, model, input, i, symbol);
                model.UpdateModel(input, i, symbol);
            }

            encoder.Flush();
            return output.ToArray();
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            IncrementCounter("ppmd.decompress");

            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB for PPMd");
            using var stream = new MemoryStream(input);

            var magicBuf = new byte[4];
            if (stream.Read(magicBuf, 0, 4) != 4 ||
                magicBuf[0] != Magic[0] || magicBuf[1] != Magic[1] ||
                magicBuf[2] != Magic[2] || magicBuf[3] != Magic[3])
            {
                throw new InvalidDataException("Invalid PPMd header magic.");
            }

            var lenBuf = new byte[4];
            if (stream.Read(lenBuf, 0, 4) != 4)
                throw new InvalidDataException("Invalid PPMd header length.");

            int originalLength = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
            if (originalLength < 0)
                throw new InvalidDataException("Invalid original length in PPMd header.");

            int maxOrder = stream.ReadByte();
            if (maxOrder < 0)
                throw new InvalidDataException("Invalid max order in PPMd header.");

            var decoder = new ArithmeticRangeDecoder(stream);
            var model = new PpmdModel(maxOrder);
            var result = new byte[originalLength];

            for (int i = 0; i < originalLength; i++)
            {
                byte symbol = DecodeSymbol(decoder, model, result, i);
                result[i] = symbol;
                model.UpdateModel(result, i, symbol);
            }

            return result;
        }

        /// <summary>
        /// Encodes a symbol using PPMd with SEE-based escape estimation.
        /// </summary>
        private static void EncodeSymbol(ArithmeticRangeEncoder encoder, PpmdModel model,
            byte[] data, int position, byte symbol)
        {
            var excludedSymbols = new HashSet<byte>();

            for (int order = Math.Min(model.MaxOrder, position); order >= 0; order--)
            {
                var context = model.GetContext(data, position, order);
                if (context == null || context.TotalCount == 0)
                    continue;

                int symbolCount = context.GetCount(symbol);
                if (symbolCount > 0 && !excludedSymbols.Contains(symbol))
                {
                    // Encode the symbol in this context
                    int escapeCount = model.GetEscapeCount(context, order);
                    int total = context.TotalCount + escapeCount;
                    int cumLow = context.GetCumulativeBelow(symbol, excludedSymbols);
                    encoder.EncodeRange(cumLow, cumLow + symbolCount, total);
                    model.UpdateSee(context, order, false);
                    return;
                }

                // Symbol not found - encode escape and try shorter context
                int escCount = model.GetEscapeCount(context, order);
                int totalWithEscape = context.TotalCount + escCount;
                int escapeLow = context.TotalCount;
                encoder.EncodeRange(escapeLow, escapeLow + escCount, totalWithEscape);
                model.UpdateSee(context, order, true);

                // Add seen symbols to exclusion set
                foreach (var seen in context.GetSymbols())
                    excludedSymbols.Add(seen);
            }

            // Order -1: uniform distribution over remaining symbols
            int remaining = 256 - excludedSymbols.Count;
            if (remaining <= 0) remaining = 1;
            int rank = 0;
            for (int s = 0; s < symbol; s++)
            {
                if (!excludedSymbols.Contains((byte)s))
                    rank++;
            }
            encoder.EncodeRange(rank, rank + 1, remaining);
        }

        /// <summary>
        /// Decodes a symbol using PPMd with SEE-based escape estimation.
        /// </summary>
        private static byte DecodeSymbol(ArithmeticRangeDecoder decoder, PpmdModel model,
            byte[] data, int position)
        {
            var excludedSymbols = new HashSet<byte>();

            for (int order = Math.Min(model.MaxOrder, position); order >= 0; order--)
            {
                var context = model.GetContext(data, position, order);
                if (context == null || context.TotalCount == 0)
                    continue;

                int escapeCount = model.GetEscapeCount(context, order);
                int totalWithEscape = context.TotalCount + escapeCount;

                int target = decoder.GetTarget(totalWithEscape);

                if (target < context.TotalCount)
                {
                    // Decode the symbol
                    var (symbol, cumLow, count) = context.FindSymbol(target, excludedSymbols);
                    decoder.Decode(cumLow, cumLow + count, totalWithEscape);
                    model.UpdateSee(context, order, false);
                    return symbol;
                }

                // Escape: fall back to shorter context
                decoder.Decode(context.TotalCount, context.TotalCount + escapeCount, totalWithEscape);
                model.UpdateSee(context, order, true);
                foreach (var seen in context.GetSymbols())
                    excludedSymbols.Add(seen);
            }

            // Order -1: uniform decode
            int remaining = 256 - excludedSymbols.Count;
            if (remaining <= 0) remaining = 1;
            int idx = decoder.GetTarget(remaining);
            int rank = 0;
            for (int s = 0; s < 256; s++)
            {
                if (!excludedSymbols.Contains((byte)s))
                {
                    if (rank == idx)
                    {
                        decoder.Decode(rank, rank + 1, remaining);
                        return (byte)s;
                    }
                    rank++;
                }
            }

            decoder.Decode(0, 1, remaining);
            return 0;
        }

        /// <inheritdoc/>
        protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
        {
            return new BufferedTransformStream(output, leaveOpen, CompressCore, true);
        }

        /// <inheritdoc/>
        protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
        {
            return new BufferedTransformStream(input, leaveOpen, DecompressCore, false);
        }

        /// <inheritdoc/>
        public override long EstimateCompressedSize(long inputSize)
        {
            return (long)(inputSize * 0.28) + 64;
        }

        #region PPMd Model with SEE

        /// <summary>
        /// PPMd model with Secondary Escape Estimation (SEE).
        /// Maintains context trie and SEE bins for escape probability estimation.
        /// </summary>
        private sealed class PpmdModel
        {
            private readonly int _maxOrder;
            private readonly Dictionary<long, ContextNode> _contexts = new();
            private readonly SeeBin[] _seeBins = new SeeBin[NumSeeBins];

            public int MaxOrder => _maxOrder;

            public PpmdModel(int maxOrder)
            {
                _maxOrder = maxOrder;
                for (int i = 0; i < NumSeeBins; i++)
                    _seeBins[i] = new SeeBin();
            }

            public void UpdateModel(byte[] data, int position, byte symbol)
            {
                for (int order = 0; order <= Math.Min(_maxOrder, position); order++)
                {
                    long key = ComputeKey(data, position, order);
                    if (!_contexts.TryGetValue(key, out var node))
                    {
                        node = new ContextNode();
                        _contexts[key] = node;
                    }
                    node.Increment(symbol);
                }
            }

            public ContextNode? GetContext(byte[] data, int position, int order)
            {
                if (order > position) return null;
                long key = ComputeKey(data, position, order);
                _contexts.TryGetValue(key, out var node);
                return node;
            }

            public int GetEscapeCount(ContextNode context, int order)
            {
                int binIndex = ComputeSeeBinIndex(context, order);
                var bin = _seeBins[binIndex];
                return Math.Max(1, bin.EscapeCount * 2 / Math.Max(1, bin.TotalCount));
            }

            public void UpdateSee(ContextNode context, int order, bool wasEscape)
            {
                int binIndex = ComputeSeeBinIndex(context, order);
                var bin = _seeBins[binIndex];
                if (wasEscape)
                    bin.EscapeCount++;
                bin.TotalCount++;
            }

            private static int ComputeSeeBinIndex(ContextNode context, int order)
            {
                // Bin based on context characteristics
                int numSymbols = context.NumDistinct;
                int total = context.TotalCount;
                int hash = (order * 37 + numSymbols * 7 + (total >> 4)) & (NumSeeBins - 1);
                return hash;
            }

            private static long ComputeKey(byte[] data, int position, int order)
            {
                long hash = order * 0x100000000L;
                for (int i = position - order; i < position; i++)
                {
                    hash = hash * 257 + data[i] + 1;
                }
                return hash;
            }
        }

        /// <summary>
        /// Secondary Escape Estimation bin that tracks escape statistics.
        /// </summary>
        private sealed class SeeBin
        {
            public int EscapeCount { get; set; } = 1;
            public int TotalCount { get; set; } = 2;
        }

        /// <summary>
        /// Stores symbol frequency counts for a given context.
        /// </summary>
        private sealed class ContextNode
        {
            private readonly Dictionary<byte, int> _counts = new();

            public int TotalCount { get; private set; }
            public int NumDistinct => _counts.Count;

            public void Increment(byte symbol)
            {
                _counts.TryGetValue(symbol, out int count);
                _counts[symbol] = count + 1;
                TotalCount++;
            }

            public int GetCount(byte symbol)
            {
                _counts.TryGetValue(symbol, out int count);
                return count;
            }

            public int GetCumulativeBelow(byte symbol, HashSet<byte> excluded)
            {
                int cum = 0;
                for (int s = 0; s < symbol; s++)
                {
                    if (excluded.Contains((byte)s)) continue;
                    if (_counts.TryGetValue((byte)s, out int c))
                        cum += c;
                }
                return cum;
            }

            public (byte symbol, int cumLow, int count) FindSymbol(int target, HashSet<byte> excluded)
            {
                int cum = 0;
                for (int s = 0; s < 256; s++)
                {
                    if (excluded.Contains((byte)s)) continue;
                    if (_counts.TryGetValue((byte)s, out int c))
                    {
                        if (cum + c > target)
                            return ((byte)s, cum, c);
                        cum += c;
                    }
                }
                return (0, 0, 1);
            }

            public IEnumerable<byte> GetSymbols() => _counts.Keys;
        }

        #endregion

        #region Arithmetic Range Coder

        private sealed class ArithmeticRangeEncoder
        {
            private readonly Stream _output;
            private uint _low;
            private uint _high = 0xFFFFFFFF;

            public ArithmeticRangeEncoder(Stream output) => _output = output;

            public void EncodeRange(int cumLow, int cumHigh, int total)
            {
                uint range = _high - _low;
                _high = _low + (uint)(range / total * cumHigh) - 1;
                _low = _low + (uint)(range / total * cumLow);

                while ((_low ^ _high) < 0x01000000u)
                {
                    _output.WriteByte((byte)(_low >> 24));
                    _low <<= 8;
                    _high = (_high << 8) | 0xFF;
                }
            }

            public void Flush()
            {
                for (int i = 24; i >= 0; i -= 8)
                    _output.WriteByte((byte)(_low >> i));
            }
        }

        private sealed class ArithmeticRangeDecoder
        {
            private readonly Stream _input;
            private uint _low;
            private uint _high = 0xFFFFFFFF;
            private uint _code;

            public ArithmeticRangeDecoder(Stream input)
            {
                _input = input;
                for (int i = 0; i < 4; i++)
                    _code = (_code << 8) | ReadByte();
            }

            public int GetTarget(int total)
            {
                uint range = _high - _low;
                return (int)(((ulong)(_code - _low) * (ulong)total) / (range + 1));
            }

            public void Decode(int cumLow, int cumHigh, int total)
            {
                uint range = _high - _low;
                _high = _low + (uint)(range / total * cumHigh) - 1;
                _low = _low + (uint)(range / total * cumLow);

                while ((_low ^ _high) < 0x01000000u)
                {
                    _low <<= 8;
                    _high = (_high << 8) | 0xFF;
                    _code = (_code << 8) | ReadByte();
                }
            }

            private uint ReadByte()
            {
                int b = _input.ReadByte();
                return (uint)(b < 0 ? 0 : b);
            }
        }

        #endregion

        #region Buffered Stream Wrapper

        private sealed class BufferedTransformStream : Stream
        {
            private readonly Stream _inner;
            private readonly bool _leaveOpen;
            private readonly Func<byte[], byte[]> _transform;
            private readonly bool _isCompression;
            private readonly MemoryStream _buffer;
            private bool _disposed;

            public BufferedTransformStream(Stream inner, bool leaveOpen,
                Func<byte[], byte[]> transform, bool isCompression)
            {
                _inner = inner;
                _leaveOpen = leaveOpen;
                _transform = transform;
                _isCompression = isCompression;

                if (isCompression)
                    _buffer = new MemoryStream(4096);
                else
                {
                    using var temp = new MemoryStream(4096);
                    inner.CopyTo(temp);
                    var compressed = temp.ToArray();
                    var decompressed = compressed.Length > 0 ? transform(compressed) : Array.Empty<byte>();
                    _buffer = new MemoryStream(decompressed);
                }
            }

            public override bool CanRead => !_isCompression;
            public override bool CanSeek => false;
            public override bool CanWrite => _isCompression;
            public override long Length => _buffer.Length;
            public override long Position
            {
                get => _buffer.Position;
                set => throw new NotSupportedException();
            }
            public override int Read(byte[] buffer, int offset, int count) =>
                _isCompression ? throw new NotSupportedException() : _buffer.Read(buffer, offset, count);
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (!_isCompression) throw new NotSupportedException();
                _buffer.Write(buffer, offset, count);
            }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (!_disposed && disposing)
                {
                    _disposed = true;
                    if (_isCompression)
                    {
                        var data = _buffer.ToArray();
                        if (data.Length > 0)
                        {
                            var compressed = _transform(data);
                            _inner.Write(compressed, 0, compressed.Length);
                        }
                    }
                    _buffer.Dispose();
                    if (!_leaveOpen) _inner.Dispose();
                }
                base.Dispose(disposing);
            }
        }

        #endregion
    }
}
