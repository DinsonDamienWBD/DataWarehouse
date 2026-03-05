using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Compression;

namespace DataWarehouse.Plugins.UltimateCompression.Strategies.LzFamily
{
    /// <summary>
    /// Compression strategy implementing the LZ78 dictionary-based algorithm.
    /// Uses a trie-based dictionary with 12-bit codes to represent repeated sequences.
    /// Unlike LZ77, LZ78 builds an explicit dictionary rather than using a sliding window.
    /// </summary>
    /// <remarks>
    /// LZ78 (Lempel-Ziv 1978) was the successor to LZ77 and introduced explicit dictionary
    /// construction. The algorithm builds a trie of previously seen strings and outputs
    /// (dictionary-index, next-character) pairs. This implementation uses 12-bit codes,
    /// allowing up to 4096 dictionary entries before resetting.
    /// </remarks>
    public sealed class Lz78Strategy : CompressionStrategyBase
    {
        private const int MaxDictSize = 4096;
        private const int CodeBits = 12;
        private const int MaxInputSize = 100 * 1024 * 1024; // 100 MB

        private int _configuredDictSize = MaxDictSize;

        /// <summary>
        /// Initializes a new instance of the <see cref="Lz78Strategy"/> class
        /// with the default compression level.
        /// </summary>
        public Lz78Strategy() : base(CompressionLevel.Default)
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
                        "LZ78 strategy healthy",
                        new Dictionary<string, object>
                        {
                            ["DictionarySize"] = _configuredDictSize,
                            ["CompressOperations"] = GetCounter("lz78.compress"),
                            ["DecompressOperations"] = GetCounter("lz78.decompress")
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
            // No resources to clean up in LZ78
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
            AlgorithmName = "LZ78",
            TypicalCompressionRatio = 0.50,
            CompressionSpeed = 6,
            DecompressionSpeed = 7,
            CompressionMemoryUsage = 128 * 1024,
            DecompressionMemoryUsage = 128 * 1024,
            SupportsStreaming = false,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 128,
            OptimalBlockSize = 32 * 1024
        };

        /// <inheritdoc/>
        protected override byte[] CompressCore(byte[] input)
        {
            // Strategy-specific counter
            IncrementCounter("lz78.compress");

            // Edge case: maximum input size guard
            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB");

            using var output = new MemoryStream(input.Length + 256);
            var writer = new BinaryWriter(output);

            // Header: uncompressed length
            writer.Write(input.Length);

            // Trie-based dictionary: key = (parentIndex, byte) -> childIndex
            var dict = new Dictionary<(int parent, byte symbol), int>();
            int nextCode = 1; // 0 is root/empty
            var bitWriter = new BitWriter(output);

            int currentNode = 0;

            for (int i = 0; i < input.Length; i++)
            {
                byte symbol = input[i];
                var key = (currentNode, symbol);

                if (dict.TryGetValue(key, out int childNode))
                {
                    // Extend the current match
                    currentNode = childNode;
                }
                else
                {
                    // Output (currentNode, symbol)
                    bitWriter.WriteBits(currentNode, CodeBits);
                    bitWriter.WriteBits(symbol, 8);

                    // Add new entry to dictionary
                    if (nextCode < MaxDictSize)
                    {
                        dict[key] = nextCode++;
                    }
                    else
                    {
                        // Dictionary full, reset
                        dict.Clear();
                        nextCode = 1;
                    }

                    currentNode = 0;
                }
            }

            // Output final node if any
            if (currentNode != 0)
            {
                // We ended mid-match; emit the current node with a sentinel
                bitWriter.WriteBits(currentNode, CodeBits);
                bitWriter.WriteBits(0x100, 9); // sentinel: 9-bit value > 255
            }

            // End-of-data marker
            bitWriter.WriteBits(0, CodeBits);
            bitWriter.WriteBits(0x1FF, 9); // end marker

            bitWriter.FlushBits();

            return output.ToArray();
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            // Strategy-specific counter
            IncrementCounter("lz78.decompress");

            // Edge case: validate header
            if (input.Length < 4)
                throw new InvalidDataException("LZ78 compressed data is too short.");

            using var stream = new MemoryStream(input);
            var reader = new BinaryReader(stream);

            int uncompressedLen = reader.ReadInt32();
            if (uncompressedLen < 0 || uncompressedLen > MaxInputSize)
                throw new InvalidDataException($"Invalid LZ78 uncompressed length: {uncompressedLen}");

            // P2-1621: Use MemoryStream instead of List<byte> to avoid O(n) AddRange copies per entry.
            using var output = new MemoryStream(uncompressedLen);
            var bitReader = new BitReader(stream);

            // Dictionary: index -> string (stored as byte arrays)
            var dict = new List<byte[]> { Array.Empty<byte>() }; // index 0 = empty

            while (output.Length < uncompressedLen)
            {
                int code = bitReader.ReadBits(CodeBits);

                // Try reading a symbol (8 bits) or sentinel (9 bits)
                int symbolOrSentinel = bitReader.ReadBits(8);

                // Check for end marker or sentinel scenarios
                if (code == 0 && symbolOrSentinel == 0xFF)
                {
                    // Might be end marker, check if we have enough data
                    int extraBit = bitReader.ReadBits(1);
                    if (extraBit == 1) break; // end marker: 0x1FF (9 bits)
                    // Otherwise it was just symbol 0xFF with code 0
                    symbolOrSentinel = 0xFF;
                }

                if (code >= dict.Count)
                    throw new InvalidDataException($"Invalid LZ78 dictionary reference: {code}");

                byte[] prefix = dict[code];

                // Check if this is a mid-match end (sentinel)
                if (symbolOrSentinel >= 256)
                {
                    // The symbol was a sentinel; just output the prefix
                    output.Write(prefix, 0, prefix.Length);
                    break;
                }

                byte symbol = (byte)symbolOrSentinel;

                // Output: prefix + symbol
                output.Write(prefix, 0, prefix.Length);
                if (output.Length < uncompressedLen)
                {
                    output.WriteByte(symbol);
                }

                // Build new dictionary entry
                var newEntry = new byte[prefix.Length + 1];
                Buffer.BlockCopy(prefix, 0, newEntry, 0, prefix.Length);
                newEntry[prefix.Length] = symbol;

                if (dict.Count < MaxDictSize)
                {
                    dict.Add(newEntry);
                }
                else
                {
                    dict.Clear();
                    dict.Add(Array.Empty<byte>()); // reset
                }
            }

            // Truncate to exact uncompressed length if we wrote more than expected
            var result = output.ToArray();
            return result.Length > uncompressedLen
                ? result[..uncompressedLen]
                : result;
        }

        /// <inheritdoc/>
        protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
        {
            return new BufferedAlgorithmCompressionStream(output, leaveOpen, CompressCore);
        }

        /// <inheritdoc/>
        protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
        {
            return new BufferedAlgorithmDecompressionStream(input, leaveOpen, DecompressCore);
        }

        /// <summary>
        /// Bitwise writer that packs variable-width fields into a byte stream.
        /// </summary>
        private sealed class BitWriter
        {
            private readonly Stream _stream;
            private int _bitBuffer;
            private int _bitsInBuffer;

            public BitWriter(Stream stream)
            {
                _stream = stream;
            }

            public void WriteBits(int value, int numBits)
            {
                _bitBuffer |= (value & ((1 << numBits) - 1)) << _bitsInBuffer;
                _bitsInBuffer += numBits;

                while (_bitsInBuffer >= 8)
                {
                    _stream.WriteByte((byte)(_bitBuffer & 0xFF));
                    _bitBuffer >>= 8;
                    _bitsInBuffer -= 8;
                }
            }

            public void FlushBits()
            {
                if (_bitsInBuffer > 0)
                {
                    _stream.WriteByte((byte)(_bitBuffer & 0xFF));
                    _bitBuffer = 0;
                    _bitsInBuffer = 0;
                }
            }
        }

        /// <summary>
        /// Bitwise reader that unpacks variable-width fields from a byte stream.
        /// </summary>
        private sealed class BitReader
        {
            private readonly Stream _stream;
            private int _bitBuffer;
            private int _bitsInBuffer;

            public BitReader(Stream stream)
            {
                _stream = stream;
            }

            public int ReadBits(int numBits)
            {
                while (_bitsInBuffer < numBits)
                {
                    int b = _stream.ReadByte();
                    if (b < 0) return 0;
                    _bitBuffer |= b << _bitsInBuffer;
                    _bitsInBuffer += 8;
                }

                int value = _bitBuffer & ((1 << numBits) - 1);
                _bitBuffer >>= numBits;
                _bitsInBuffer -= numBits;
                return value;
            }
        }
    }
}
