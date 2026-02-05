using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using DataWarehouse.SDK.Contracts.Compression;

namespace DataWarehouse.Plugins.UltimateCompression.Strategies.ContextMixing
{
    /// <summary>
    /// Compression strategy implementing Prediction by Partial Matching (PPM) of order 5.
    /// PPM builds a context trie up to a maximum order, predicts the next symbol from the
    /// longest matching context, and uses escape symbols (Method C) to fall back to shorter
    /// contexts when novel symbols appear. The predictions are encoded via arithmetic coding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PPM was pioneered by Cleary and Witten (1984) and remains one of the strongest
    /// statistical compression methods. It adaptively models symbol probabilities conditioned
    /// on the preceding context of up to N bytes. When a symbol is novel in the current
    /// context, it emits an escape symbol and falls back to a shorter context.
    /// </para>
    /// <para>
    /// Method C escape estimation: the escape probability is set to the number of distinct
    /// symbols seen in the current context divided by the total count. This gives a fair
    /// estimate of the likelihood of encountering a new symbol.
    /// </para>
    /// <para>
    /// Format: [Magic:4][OrigLen:4][CompressedData]
    /// </para>
    /// </remarks>
    public sealed class PpmStrategy : CompressionStrategyBase
    {
        private static readonly byte[] Magic = { 0x50, 0x50, 0x4D, 0x35 }; // "PPM5"
        private const int MaxOrder = 5;

        /// <summary>
        /// Initializes a new instance of the <see cref="PpmStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public PpmStrategy() : base(CompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "PPM",
            TypicalCompressionRatio = 0.25,
            CompressionSpeed = 2,
            DecompressionSpeed = 2,
            CompressionMemoryUsage = 256L * 1024 * 1024,
            DecompressionMemoryUsage = 256L * 1024 * 1024,
            SupportsStreaming = false,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 64,
            OptimalBlockSize = 1024 * 1024
        };

        /// <inheritdoc/>
        protected override byte[] CompressCore(byte[] input)
        {
            using var output = new MemoryStream();
            output.Write(Magic, 0, 4);

            var lenBytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lenBytes, input.Length);
            output.Write(lenBytes, 0, 4);

            var encoder = new ArithmeticRangeEncoder(output);
            var trie = new PpmTrie(MaxOrder);

            for (int i = 0; i < input.Length; i++)
            {
                byte symbol = input[i];
                EncodeSymbol(encoder, trie, input, i, symbol);
                trie.AddContext(input, i);
            }

            encoder.Flush();
            return output.ToArray();
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            using var stream = new MemoryStream(input);

            var magicBuf = new byte[4];
            if (stream.Read(magicBuf, 0, 4) != 4 ||
                magicBuf[0] != Magic[0] || magicBuf[1] != Magic[1] ||
                magicBuf[2] != Magic[2] || magicBuf[3] != Magic[3])
            {
                throw new InvalidDataException("Invalid PPM header magic.");
            }

            var lenBuf = new byte[4];
            if (stream.Read(lenBuf, 0, 4) != 4)
                throw new InvalidDataException("Invalid PPM header length.");

            int originalLength = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
            if (originalLength < 0)
                throw new InvalidDataException("Invalid original length in PPM header.");

            var decoder = new ArithmeticRangeDecoder(stream);
            var trie = new PpmTrie(MaxOrder);
            var result = new byte[originalLength];

            for (int i = 0; i < originalLength; i++)
            {
                byte symbol = DecodeSymbol(decoder, trie, result, i);
                result[i] = symbol;
                trie.AddContext(result, i);
            }

            return result;
        }

        /// <summary>
        /// Encodes a symbol using PPM with escape mechanism (Method C).
        /// Tries the longest context first, falls back on escape.
        /// </summary>
        private static void EncodeSymbol(ArithmeticRangeEncoder encoder, PpmTrie trie,
            byte[] data, int position, byte symbol)
        {
            var excludedSymbols = new HashSet<byte>();

            for (int order = Math.Min(MaxOrder, position); order >= 0; order--)
            {
                var context = trie.GetContext(data, position, order);
                if (context == null || context.TotalCount == 0)
                    continue;

                int symbolCount = context.GetCount(symbol);
                if (symbolCount > 0 && !excludedSymbols.Contains(symbol))
                {
                    // Encode the symbol in this context
                    int total = context.TotalCount + context.NumDistinct; // +escape
                    int cumLow = context.GetCumulativeBelow(symbol, excludedSymbols);
                    encoder.EncodeRange(cumLow, cumLow + symbolCount, total);
                    return;
                }

                // Symbol not found — encode escape and try shorter context
                int escapeCount = context.NumDistinct; // Method C
                int totalWithEscape = context.TotalCount + context.NumDistinct;
                int escapeLow = context.TotalCount; // escape is at the end
                encoder.EncodeRange(escapeLow, escapeLow + escapeCount, totalWithEscape);

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
        /// Decodes a symbol using PPM with escape mechanism (Method C).
        /// </summary>
        private static byte DecodeSymbol(ArithmeticRangeDecoder decoder, PpmTrie trie,
            byte[] data, int position)
        {
            var excludedSymbols = new HashSet<byte>();

            for (int order = Math.Min(MaxOrder, position); order >= 0; order--)
            {
                var context = trie.GetContext(data, position, order);
                if (context == null || context.TotalCount == 0)
                    continue;

                int escapeCount = context.NumDistinct;
                int totalWithEscape = context.TotalCount + escapeCount;

                int target = decoder.GetTarget(totalWithEscape);

                if (target < context.TotalCount)
                {
                    // Decode the symbol
                    var (symbol, cumLow, count) = context.FindSymbol(target, excludedSymbols);
                    decoder.Decode(cumLow, cumLow + count, totalWithEscape);
                    return symbol;
                }

                // Escape: fall back to shorter context
                decoder.Decode(context.TotalCount, context.TotalCount + escapeCount, totalWithEscape);
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
            return (long)(inputSize * 0.32) + 64;
        }

        #region PPM Trie

        /// <summary>
        /// Trie-based context model for PPM. Each node stores symbol frequencies for the
        /// symbols that follow the context path leading to that node.
        /// </summary>
        private sealed class PpmTrie
        {
            private readonly int _maxOrder;
            private readonly Dictionary<long, ContextNode> _contexts = new();

            public PpmTrie(int maxOrder) => _maxOrder = maxOrder;

            /// <summary>
            /// Adds the symbol at data[position] to all context levels.
            /// </summary>
            public void AddContext(byte[] data, int position)
            {
                byte symbol = data[position];
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

            /// <summary>
            /// Gets the context node for a given order at the current position.
            /// Returns null if no such context has been seen.
            /// </summary>
            public ContextNode? GetContext(byte[] data, int position, int order)
            {
                if (order > position) return null;
                long key = ComputeKey(data, position, order);
                _contexts.TryGetValue(key, out var node);
                return node;
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
        /// Stores symbol frequency counts for a given context.
        /// Supports cumulative frequency queries needed by the arithmetic coder.
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

            /// <summary>
            /// Gets cumulative count of all symbols below the given symbol
            /// (in byte order), excluding the specified symbols.
            /// </summary>
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

            /// <summary>
            /// Finds the symbol at a given cumulative frequency target.
            /// Returns (symbol, cumulativeLow, count).
            /// </summary>
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
                // Fallback (should not reach here)
                return (0, 0, 1);
            }

            public IEnumerable<byte> GetSymbols() => _counts.Keys;
        }

        #endregion

        #region Arithmetic Range Coder

        /// <summary>
        /// Range-based arithmetic encoder that encodes symbol ranges (low, high, total).
        /// Uses 32-bit precision with byte-level normalization.
        /// </summary>
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

        /// <summary>
        /// Range-based arithmetic decoder that decodes symbol ranges.
        /// </summary>
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
                    _buffer = new MemoryStream();
                else
                {
                    using var temp = new MemoryStream();
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
