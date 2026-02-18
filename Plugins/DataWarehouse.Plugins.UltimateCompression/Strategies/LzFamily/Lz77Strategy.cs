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
    /// Compression strategy implementing the classic LZ77 sliding window algorithm.
    /// Uses a 32KB window, up to 258-byte matches, and a hash chain for efficient
    /// match finding. Output is encoded as a sequence of literal and match tokens.
    /// </summary>
    /// <remarks>
    /// LZ77 (Lempel-Ziv 1977) is the foundational algorithm for most modern LZ-family
    /// compressors. It scans input data using a sliding window and replaces repeated
    /// occurrences with back-references (distance, length pairs). This implementation
    /// uses hash chains indexed by 3-byte sequences for efficient match lookup.
    /// </remarks>
    public sealed class Lz77Strategy : CompressionStrategyBase
    {
        private const int WindowSize = 32768;     // 32 KB sliding window
        private const int MaxMatchLength = 258;
        private const int MinMatch = 3;
        private const int HashBits = 15;
        private const int HashSize = 1 << HashBits;
        private const int HashMask = HashSize - 1;
        private const int MaxChainLength = 128;
        private const int MaxInputSize = 100 * 1024 * 1024; // 100 MB

        private int _configuredWindowSize = WindowSize;
        private int _configuredMaxChainLength = MaxChainLength;

        /// <summary>
        /// Initializes a new instance of the <see cref="Lz77Strategy"/> class
        /// with the default compression level.
        /// </summary>
        public Lz77Strategy() : base(CompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        protected override async Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            await base.InitializeAsyncCore(cancellationToken).ConfigureAwait(false);

            // Load and validate LZ77 configuration
            if (SystemConfiguration.PluginSettings.TryGetValue("UltimateCompression:LZ77:WindowSize", out var windowSizeObj)
                && windowSizeObj is int windowSize)
            {
                if (windowSize < 1024 || windowSize > 65536 || (windowSize & (windowSize - 1)) != 0)
                    throw new ArgumentException($"LZ77 window size must be a power of 2 between 1024 and 65536, got {windowSize}");
                _configuredWindowSize = windowSize;
            }

            if (SystemConfiguration.PluginSettings.TryGetValue("UltimateCompression:LZ77:MaxChainLength", out var maxChainLenObj)
                && maxChainLenObj is int maxChainLen)
            {
                if (maxChainLen < 1 || maxChainLen > 4096)
                    throw new ArgumentException($"LZ77 max chain length must be between 1 and 4096, got {maxChainLen}");
                _configuredMaxChainLength = maxChainLen;
            }
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
                        "LZ77 strategy healthy",
                        new Dictionary<string, object>
                        {
                            ["WindowSize"] = _configuredWindowSize,
                            ["MaxChainLength"] = _configuredMaxChainLength,
                            ["CompressOperations"] = GetCounter("lz77.compress"),
                            ["DecompressOperations"] = GetCounter("lz77.decompress")
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
            // No resources to clean up in LZ77
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
            AlgorithmName = "LZ77",
            TypicalCompressionRatio = 0.45,
            CompressionSpeed = 5,
            DecompressionSpeed = 8,
            CompressionMemoryUsage = 256 * 1024,
            DecompressionMemoryUsage = 64 * 1024,
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
            IncrementCounter("lz77.compress");

            // Edge case: maximum input size guard
            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {MaxInputSize / (1024 * 1024)} MB");

            if (input.Length < MinMatch)
                return CreateLiteralBlock(input);

            using var output = new MemoryStream(input.Length);
            var writer = new BinaryWriter(output);

            // Header: uncompressed length
            writer.Write(input.Length);

            var head = new int[HashSize];
            var prev = new int[WindowSize];
            Array.Fill(head, -1);

            int pos = 0;
            var literals = new MemoryStream(65536);

            while (pos < input.Length)
            {
                int bestLen = MinMatch - 1;
                int bestDist = 0;

                if (pos + MinMatch <= input.Length)
                {
                    int hash = HashFunc(input, pos);
                    int chainPos = head[hash];

                    // Update hash chain
                    prev[pos & (WindowSize - 1)] = head[hash];
                    head[hash] = pos;

                    int chainCount = 0;
                    while (chainPos >= 0 && chainPos >= pos - WindowSize && chainCount < MaxChainLength)
                    {
                        int matchLen = 0;
                        int maxLen = Math.Min(MaxMatchLength, input.Length - pos);

                        while (matchLen < maxLen && input[chainPos + matchLen] == input[pos + matchLen])
                            matchLen++;

                        if (matchLen > bestLen)
                        {
                            bestLen = matchLen;
                            bestDist = pos - chainPos;
                            if (matchLen == MaxMatchLength) break;
                        }

                        chainPos = prev[chainPos & (WindowSize - 1)];
                        chainCount++;
                    }
                }

                if (bestLen >= MinMatch)
                {
                    // Flush any pending literals
                    FlushLiterals(writer, literals);

                    // Write match token: 0xFF marker, then 2-byte length, 2-byte distance
                    writer.Write((byte)0xFF);
                    writer.Write((ushort)bestLen);
                    writer.Write((ushort)bestDist);

                    // Update hash chain for skipped positions
                    for (int i = 1; i < bestLen && pos + i + MinMatch <= input.Length; i++)
                    {
                        int h = HashFunc(input, pos + i);
                        prev[(pos + i) & (WindowSize - 1)] = head[h];
                        head[h] = pos + i;
                    }

                    pos += bestLen;
                }
                else
                {
                    literals.WriteByte(input[pos]);
                    pos++;
                }
            }

            FlushLiterals(writer, literals);
            literals.Dispose();

            return output.ToArray();
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            // Strategy-specific counter
            IncrementCounter("lz77.decompress");

            // Edge case: validate header
            if (input.Length < 4)
                throw new InvalidDataException("LZ77 compressed data is too short.");

            using var stream = new MemoryStream(input);
            var reader = new BinaryReader(stream);

            int uncompressedLen = reader.ReadInt32();
            if (uncompressedLen < 0 || uncompressedLen > MaxInputSize)
                throw new InvalidDataException($"Invalid LZ77 uncompressed length: {uncompressedLen}");

            var output = new byte[uncompressedLen];
            int op = 0;

            while (stream.Position < stream.Length && op < uncompressedLen)
            {
                byte tag = reader.ReadByte();

                if (tag == 0xFF)
                {
                    // Match token
                    if (stream.Position + 4 > stream.Length)
                        throw new InvalidDataException("Truncated LZ77 match token.");

                    int matchLen = reader.ReadUInt16();
                    int distance = reader.ReadUInt16();

                    if (distance == 0 || op - distance < 0)
                        throw new InvalidDataException("Invalid LZ77 match distance.");

                    int srcPos = op - distance;
                    for (int i = 0; i < matchLen && op < uncompressedLen; i++)
                    {
                        output[op++] = output[srcPos + i];
                    }
                }
                else if (tag == 0xFE)
                {
                    // Literal run
                    if (stream.Position + 2 > stream.Length)
                        throw new InvalidDataException("Truncated LZ77 literal header.");

                    int literalLen = reader.ReadUInt16();
                    if (stream.Position + literalLen > stream.Length)
                        throw new InvalidDataException("Truncated LZ77 literal data.");

                    int bytesRead = reader.Read(output, op, Math.Min(literalLen, uncompressedLen - op));
                    op += bytesRead;
                }
                else
                {
                    // Single literal byte (tag < 0xFE)
                    output[op++] = tag;
                }
            }

            return output;
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

        private static int HashFunc(byte[] data, int pos)
        {
            uint v = (uint)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16));
            return (int)((v * 0x9E3779B1u) >> (32 - HashBits)) & HashMask;
        }

        private static void FlushLiterals(BinaryWriter writer, MemoryStream literals)
        {
            if (literals.Length == 0) return;

            var data = literals.ToArray();

            if (data.Length == 1 && data[0] != 0xFF && data[0] != 0xFE)
            {
                // Single literal that is not a control byte
                writer.Write(data[0]);
            }
            else
            {
                // Literal run
                writer.Write((byte)0xFE);
                writer.Write((ushort)data.Length);
                writer.Write(data);
            }

            literals.SetLength(0);
        }

        private static byte[] CreateLiteralBlock(byte[] input)
        {
            using var output = new MemoryStream(input.Length + 256);
            var writer = new BinaryWriter(output);
            writer.Write(input.Length);
            if (input.Length > 0)
            {
                writer.Write((byte)0xFE);
                writer.Write((ushort)input.Length);
                writer.Write(input);
            }
            return output.ToArray();
        }
    }
}
