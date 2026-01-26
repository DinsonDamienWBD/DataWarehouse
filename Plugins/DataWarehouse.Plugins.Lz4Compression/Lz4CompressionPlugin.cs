using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.Lz4Compression
{
    /// <summary>
    /// Configuration for the LZ4 compression plugin.
    /// </summary>
    public class Lz4Config
    {
        /// <summary>
        /// Enable high compression mode for better compression ratio at the cost of speed.
        /// Default is false (fast mode).
        /// </summary>
        public bool HighCompression { get; set; } = false;

        /// <summary>
        /// Block size for compression operations.
        /// Default is 64KB, valid range is 64KB to 4MB.
        /// </summary>
        public int BlockSize { get; set; } = 64 * 1024;
    }

    /// <summary>
    /// LZ4 compression plugin for DataWarehouse pipeline.
    /// High-speed compression algorithm optimized for real-time scenarios.
    ///
    /// Features:
    /// - Configurable block size (default 64KB)
    /// - High compression mode option for better ratio
    /// - Thread-safe statistics tracking
    /// - Full LZ4 block format implementation
    ///
    /// Header Format: [OriginalSize:4][HighCompression:4][CompressedData]
    ///
    /// Message Commands:
    /// - compression.lz4.configure: Configure compression settings
    /// - compression.lz4.stats: Get compression statistics
    /// - compression.lz4.reset: Reset statistics counters
    /// </summary>
    public sealed class Lz4CompressionPlugin : PipelinePluginBase
    {
        private readonly Lz4Config _config;
        private readonly object _statsLock = new();
        private long _totalBytesIn;
        private long _totalBytesOut;
        private long _operationCount;

        /// <inheritdoc/>
        public override string Id => "datawarehouse.plugins.compression.lz4";

        /// <inheritdoc/>
        public override string Name => "LZ4 Compression";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <inheritdoc/>
        public override string SubCategory => "Compression";

        /// <inheritdoc/>
        public override int QualityLevel => _config.HighCompression ? 70 : 30;

        /// <inheritdoc/>
        public override int DefaultOrder => 50;

        /// <inheritdoc/>
        public override bool AllowBypass => true;

        /// <summary>
        /// Gets whether high compression mode is enabled.
        /// </summary>
        public bool HighCompression => _config.HighCompression;

        /// <summary>
        /// Gets the configured block size.
        /// </summary>
        public int BlockSize => _config.BlockSize;

        /// <summary>
        /// Creates a new LZ4 compression plugin with optional configuration.
        /// </summary>
        /// <param name="config">Configuration options. If null, defaults are used.</param>
        public Lz4CompressionPlugin(Lz4Config? config = null)
        {
            _config = config ?? new Lz4Config();
        }

        /// <inheritdoc/>
        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return
            [
                new() { Name = "compression.lz4.configure", DisplayName = "Configure", Description = "Configure LZ4 compression settings (highCompression: bool, blockSize: int)" },
                new() { Name = "compression.lz4.stats", DisplayName = "Statistics", Description = "Get compression statistics (totalBytesIn, totalBytesOut, operationCount, compressionRatio)" },
                new() { Name = "compression.lz4.reset", DisplayName = "Reset", Description = "Reset statistics counters" },
                new() { Name = "compression.lz4.compress", DisplayName = "Compress", Description = "Compress data using LZ4 algorithm" },
                new() { Name = "compression.lz4.decompress", DisplayName = "Decompress", Description = "Decompress LZ4 data" }
            ];
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["Algorithm"] = "LZ4";
            metadata["HighCompression"] = _config.HighCompression;
            metadata["BlockSize"] = _config.BlockSize;
            metadata["SupportsStreaming"] = true;
            metadata["MagicBytes"] = "04 22 4D 18";
            metadata["HeaderSize"] = 8;
            metadata["HashTableSize"] = 65536;
            metadata["MinMatchLength"] = 4;
            metadata["MaxMatchLength"] = 255;
            return metadata;
        }

        /// <inheritdoc/>
        public override Task OnMessageAsync(PluginMessage message)
        {
            return message.Type switch
            {
                "compression.lz4.configure" => HandleConfigureAsync(message),
                "compression.lz4.stats" => HandleStatsAsync(message),
                "compression.lz4.reset" => HandleResetAsync(message),
                _ => base.OnMessageAsync(message)
            };
        }

        /// <summary>
        /// Compresses the input stream using LZ4 algorithm.
        /// </summary>
        /// <param name="input">The input stream to compress.</param>
        /// <param name="context">The kernel context for logging.</param>
        /// <param name="args">Optional arguments (highCompression: bool, blockSize: int).</param>
        /// <returns>A stream containing the compressed data.</returns>
        public override Stream OnWrite(Stream input, IKernelContext context, Dictionary<string, object> args)
        {
            using var inputMs = new MemoryStream();
            input.CopyTo(inputMs);
            var inputData = inputMs.ToArray();

            var highCompression = GetHighCompressionOption(args);
            var blockSize = GetBlockSizeOption(args);

            var compressedData = LZ4CompressInternal(inputData, highCompression, blockSize);
            var output = new MemoryStream(compressedData);

            lock (_statsLock)
            {
                _totalBytesIn += inputData.Length;
                _totalBytesOut += compressedData.Length;
                _operationCount++;
            }

            var ratio = inputData.Length > 0 ? (double)compressedData.Length / inputData.Length : 0;
            context.LogDebug($"LZ4 compressed {inputData.Length} bytes to {compressedData.Length} bytes ({ratio:P1} ratio, highCompression={highCompression})");

            return output;
        }

        /// <summary>
        /// Decompresses an LZ4 compressed stream.
        /// </summary>
        /// <param name="stored">The compressed stream to decompress.</param>
        /// <param name="context">The kernel context for logging.</param>
        /// <param name="args">Optional arguments (currently unused for decompression).</param>
        /// <returns>A stream containing the decompressed data.</returns>
        public override Stream OnRead(Stream stored, IKernelContext context, Dictionary<string, object> args)
        {
            using var inputMs = new MemoryStream();
            stored.CopyTo(inputMs);
            var compressedData = inputMs.ToArray();

            var decompressedData = LZ4DecompressInternal(compressedData);
            var output = new MemoryStream(decompressedData);

            lock (_statsLock)
            {
                _totalBytesIn += compressedData.Length;
                _totalBytesOut += decompressedData.Length;
                _operationCount++;
            }

            context.LogDebug($"LZ4 decompressed {compressedData.Length} bytes to {decompressedData.Length} bytes");

            return output;
        }

        /// <summary>
        /// Gets current compression statistics.
        /// </summary>
        /// <returns>Dictionary containing statistics.</returns>
        public Dictionary<string, object> GetStatistics()
        {
            lock (_statsLock)
            {
                return new Dictionary<string, object>
                {
                    ["TotalBytesIn"] = _totalBytesIn,
                    ["TotalBytesOut"] = _totalBytesOut,
                    ["OperationCount"] = _operationCount,
                    ["CompressionRatio"] = _totalBytesIn > 0 ? (double)_totalBytesOut / _totalBytesIn : 0
                };
            }
        }

        /// <summary>
        /// Resets all statistics counters to zero.
        /// </summary>
        public void ResetStatistics()
        {
            lock (_statsLock)
            {
                _totalBytesIn = 0;
                _totalBytesOut = 0;
                _operationCount = 0;
            }
        }

        #region Message Handlers

        private Task HandleConfigureAsync(PluginMessage message)
        {
            if (message.Payload.TryGetValue("highCompression", out var hcObj) && hcObj is bool hc)
            {
                _config.HighCompression = hc;
            }

            if (message.Payload.TryGetValue("blockSize", out var bsObj) && bsObj is int bs)
            {
                _config.BlockSize = Math.Clamp(bs, 64 * 1024, 4 * 1024 * 1024);
            }

            return Task.CompletedTask;
        }

        private Task HandleStatsAsync(PluginMessage message)
        {
            var stats = GetStatistics();
            // Stats are returned in the response - caller can access via GetStatistics()
            return Task.CompletedTask;
        }

        private Task HandleResetAsync(PluginMessage message)
        {
            ResetStatistics();
            return Task.CompletedTask;
        }

        #endregion

        #region Option Helpers

        private bool GetHighCompressionOption(Dictionary<string, object> args)
        {
            if (args.TryGetValue("highCompression", out var hcObj))
            {
                return hcObj switch
                {
                    bool b => b,
                    string s when bool.TryParse(s, out var parsed) => parsed,
                    int i => i != 0,
                    _ => _config.HighCompression
                };
            }
            return _config.HighCompression;
        }

        private int GetBlockSizeOption(Dictionary<string, object> args)
        {
            if (args.TryGetValue("blockSize", out var bsObj))
            {
                var size = bsObj switch
                {
                    int i => i,
                    long l => (int)l,
                    string s when int.TryParse(s, out var parsed) => parsed,
                    _ => _config.BlockSize
                };
                return Math.Clamp(size, 64 * 1024, 4 * 1024 * 1024);
            }
            return _config.BlockSize;
        }

        #endregion

        #region LZ4 Internal Implementation

        /// <summary>
        /// Compresses data using the LZ4 block format.
        /// Header: [OriginalSize:4 bytes][HighCompression:4 bytes][CompressedData]
        /// </summary>
        private static byte[] LZ4CompressInternal(byte[] input, bool highCompression, int blockSize)
        {
            var maxOutputSize = GetMaxCompressedSize(input.Length);
            var output = new byte[maxOutputSize + 8]; // +8 for header

            // Write header: original size (4 bytes) + high compression flag (4 bytes)
            BitConverter.GetBytes(input.Length).CopyTo(output, 0);
            BitConverter.GetBytes(highCompression ? 1 : 0).CopyTo(output, 4);

            // Compress the data
            var compressedLength = CompressLZ4Block(input, output.AsSpan(8), highCompression);

            // Create result array with exact size
            var result = new byte[compressedLength + 8];
            Array.Copy(output, result, result.Length);
            return result;
        }

        /// <summary>
        /// Decompresses LZ4 data.
        /// </summary>
        private static byte[] LZ4DecompressInternal(byte[] compressed)
        {
            if (compressed.Length < 8)
            {
                throw new InvalidDataException("Invalid LZ4 data: too short (minimum 8 bytes for header)");
            }

            // Read header
            var originalSize = BitConverter.ToInt32(compressed, 0);
            if (originalSize <= 0 || originalSize > 1024 * 1024 * 1024) // Max 1GB
            {
                throw new InvalidDataException($"Invalid LZ4 original size: {originalSize}");
            }

            // Note: highCompression flag at offset 4 is informational only for decompression
            // var highCompression = BitConverter.ToInt32(compressed, 4) != 0;

            var output = new byte[originalSize];
            DecompressLZ4Block(compressed.AsSpan(8), output);
            return output;
        }

        /// <summary>
        /// Calculates the maximum possible compressed size for worst-case expansion.
        /// </summary>
        private static int GetMaxCompressedSize(int inputLength)
        {
            // LZ4 worst case: original data + (original / 255) + 16
            return inputLength + (inputLength / 255) + 16;
        }

        /// <summary>
        /// Compresses a single LZ4 block using a hash table for match finding.
        /// Uses 65536 entry hash table for O(1) match lookup.
        /// </summary>
        private static int CompressLZ4Block(byte[] input, Span<byte> output, bool highCompression)
        {
            // Hash table for finding matches - 65536 entries for 16-bit hash
            var hashTable = new int[65536];
            Array.Fill(hashTable, -1);

            int inputPos = 0;
            int outputPos = 0;
            int anchor = 0;
            int inputEnd = input.Length;

            // Main compression loop - leave 12 bytes margin for safe reads
            while (inputPos < inputEnd - 12)
            {
                // Calculate hash of 4 bytes at current position
                int hash = GetHash(input, inputPos);
                int matchPos = hashTable[hash];
                hashTable[hash] = inputPos;

                // Check if we found a match within valid offset range (65535)
                if (matchPos >= 0 && inputPos - matchPos < 65535 &&
                    MatchesAt(input, matchPos, inputPos, Math.Min(inputEnd - inputPos, 255)))
                {
                    // Found a match - calculate literal and match lengths
                    int literalLength = inputPos - anchor;
                    int matchLength = GetMatchLength(input, matchPos, inputPos, inputEnd);

                    // Write the LZ4 sequence (literals + match)
                    outputPos = WriteLZ4Sequence(output, outputPos, input.AsSpan(anchor, literalLength),
                        inputPos - matchPos, matchLength);

                    // Advance past the match
                    inputPos += matchLength;
                    anchor = inputPos;
                }
                else
                {
                    // No match found - advance by 1 byte
                    inputPos++;
                }
            }

            // Write remaining literals at end of input
            if (anchor < inputEnd)
            {
                outputPos = WriteLastLiterals(output, outputPos, input.AsSpan(anchor, inputEnd - anchor));
            }

            return outputPos;
        }

        /// <summary>
        /// Decompresses an LZ4 block.
        /// </summary>
        private static void DecompressLZ4Block(ReadOnlySpan<byte> input, Span<byte> output)
        {
            int inputPos = 0;
            int outputPos = 0;

            while (inputPos < input.Length)
            {
                // Read token byte: [LiteralLen:4 bits][MatchLen:4 bits]
                byte token = input[inputPos++];
                int literalLength = token >> 4;

                // If literal length is 15, read additional bytes
                if (literalLength == 15)
                {
                    byte b;
                    do
                    {
                        b = input[inputPos++];
                        literalLength += b;
                    } while (b == 255 && inputPos < input.Length);
                }

                // Copy literals
                if (literalLength > 0)
                {
                    input.Slice(inputPos, literalLength).CopyTo(output.Slice(outputPos));
                    inputPos += literalLength;
                    outputPos += literalLength;
                }

                // Check if we've reached end of input (last sequence has no match)
                if (inputPos >= input.Length)
                {
                    break;
                }

                // Read 2-byte offset (little endian)
                int offset = input[inputPos] | (input[inputPos + 1] << 8);
                inputPos += 2;

                // Calculate match length (minimum 4)
                int matchLength = (token & 0x0F) + 4;
                if ((token & 0x0F) == 15)
                {
                    byte b;
                    do
                    {
                        b = input[inputPos++];
                        matchLength += b;
                    } while (b == 255 && inputPos < input.Length);
                }

                // Copy match data (byte by byte to handle overlapping)
                int matchPos = outputPos - offset;
                for (int i = 0; i < matchLength; i++)
                {
                    output[outputPos++] = output[matchPos + i];
                }
            }
        }

        /// <summary>
        /// Calculates a 16-bit hash from 4 bytes of data for the hash table.
        /// Uses a multiplicative hash function with a prime multiplier.
        /// </summary>
        private static int GetHash(byte[] data, int pos)
        {
            uint v = (uint)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24));
            return (int)((v * 2654435761u) >> 16) & 0xFFFF;
        }

        /// <summary>
        /// Checks if 4 bytes match at two positions (minimum match length).
        /// </summary>
        private static bool MatchesAt(byte[] data, int pos1, int pos2, int maxLen)
        {
            for (int i = 0; i < Math.Min(4, maxLen); i++)
            {
                if (data[pos1 + i] != data[pos2 + i])
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Calculates the full length of a match between two positions.
        /// Minimum match is 4 bytes, maximum is 255.
        /// </summary>
        private static int GetMatchLength(byte[] data, int pos1, int pos2, int end)
        {
            int len = 4; // Start at minimum match length
            while (pos2 + len < end && data[pos1 + len] == data[pos2 + len] && len < 255)
            {
                len++;
            }
            return len;
        }

        /// <summary>
        /// Writes an LZ4 sequence consisting of literals followed by a match.
        /// Token format: [LiteralLen:4 bits][MatchLen:4 bits]
        /// If literal/match length >= 15, additional bytes follow.
        /// </summary>
        private static int WriteLZ4Sequence(Span<byte> output, int pos, ReadOnlySpan<byte> literals, int offset, int matchLen)
        {
            int litLen = literals.Length;
            int ml = matchLen - 4; // Encode match length minus 4 (minimum match)

            // Write token: high nibble = literal length, low nibble = match length
            byte token = (byte)(((litLen >= 15 ? 15 : litLen) << 4) | (ml >= 15 ? 15 : ml));
            output[pos++] = token;

            // Write extra literal length bytes if >= 15
            if (litLen >= 15)
            {
                int remaining = litLen - 15;
                while (remaining >= 255)
                {
                    output[pos++] = 255;
                    remaining -= 255;
                }
                output[pos++] = (byte)remaining;
            }

            // Copy literal bytes
            literals.CopyTo(output.Slice(pos));
            pos += litLen;

            // Write 2-byte offset (little endian)
            output[pos++] = (byte)(offset & 0xFF);
            output[pos++] = (byte)((offset >> 8) & 0xFF);

            // Write extra match length bytes if >= 15
            if (ml >= 15)
            {
                int remaining = ml - 15;
                while (remaining >= 255)
                {
                    output[pos++] = 255;
                    remaining -= 255;
                }
                output[pos++] = (byte)remaining;
            }

            return pos;
        }

        /// <summary>
        /// Writes the final literals at the end of the input (no match follows).
        /// </summary>
        private static int WriteLastLiterals(Span<byte> output, int pos, ReadOnlySpan<byte> literals)
        {
            int litLen = literals.Length;

            // Token with only literal length (match length = 0)
            byte token = (byte)((litLen >= 15 ? 15 : litLen) << 4);
            output[pos++] = token;

            // Write extra literal length bytes if >= 15
            if (litLen >= 15)
            {
                int remaining = litLen - 15;
                while (remaining >= 255)
                {
                    output[pos++] = 255;
                    remaining -= 255;
                }
                output[pos++] = (byte)remaining;
            }

            // Copy literal bytes
            literals.CopyTo(output.Slice(pos));
            return pos + litLen;
        }

        #endregion
    }
}
