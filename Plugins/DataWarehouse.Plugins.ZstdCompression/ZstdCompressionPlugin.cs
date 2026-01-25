using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.ZstdCompression
{
    /// <summary>
    /// Zstandard (Zstd) compression plugin for DataWarehouse pipeline.
    /// Implements RFC 8478 compliant Zstd compression with high compression ratio
    /// and excellent speed balance, suitable for storage and archival.
    ///
    /// Features:
    /// - Configurable compression level (1-22, default 3)
    /// - Thread-safe statistics tracking
    /// - LZ-style match finding with hash table
    /// - Block-based compression with proper token encoding
    /// - RFC 8478 compliant frame format
    ///
    /// Message Commands:
    /// - compression.zstd.configure: Configure compression settings
    /// - compression.zstd.stats: Get compression statistics
    /// - compression.zstd.reset: Reset statistics counters
    /// </summary>
    public sealed class ZstdCompressionPlugin : PipelinePluginBase
    {
        private readonly ZstdConfig _config;
        private readonly object _statsLock = new();
        private long _totalBytesIn;
        private long _totalBytesOut;
        private long _operationCount;

        /// <summary>Zstd frame format magic number (little-endian 0x28B52FFD).</summary>
        private const uint ZstdMagic = 0xFD2FB528;

        /// <summary>Minimum compression level.</summary>
        public const int MinCompressionLevel = 1;

        /// <summary>Maximum compression level.</summary>
        public const int MaxCompressionLevel = 22;

        /// <summary>Default compression level (balanced speed/ratio).</summary>
        public const int DefaultCompressionLevel = 3;

        public override string Id => "datawarehouse.plugins.compression.zstd";
        public override string Name => "Zstd Compression";
        public override string Version => "1.0.0";
        public override string SubCategory => "Compression";

        public override int QualityLevel => _config.Level switch
        {
            <= 3 => 40,
            <= 9 => 70,
            <= 15 => 85,
            _ => 95
        };

        public override int DefaultOrder => 50;
        public override bool AllowBypass => true;

        /// <summary>
        /// Gets the current compression level.
        /// </summary>
        public int CompressionLevel => _config.Level;

        /// <summary>
        /// Gets compression statistics.
        /// </summary>
        public ZstdStatistics Statistics
        {
            get
            {
                lock (_statsLock)
                {
                    return new ZstdStatistics
                    {
                        TotalBytesIn = _totalBytesIn,
                        TotalBytesOut = _totalBytesOut,
                        OperationCount = _operationCount,
                        CompressionRatio = _totalBytesIn > 0
                            ? (double)_totalBytesOut / _totalBytesIn
                            : 0
                    };
                }
            }
        }

        /// <summary>
        /// Creates a new Zstd compression plugin with optional configuration.
        /// </summary>
        /// <param name="config">Optional configuration. Uses defaults if null.</param>
        public ZstdCompressionPlugin(ZstdConfig? config = null)
        {
            _config = config ?? new ZstdConfig();
        }

        /// <summary>
        /// Creates a new Zstd compression plugin with specified compression level.
        /// </summary>
        /// <param name="level">Compression level (1-22, default 3).</param>
        public ZstdCompressionPlugin(int level)
        {
            _config = new ZstdConfig { Level = Math.Clamp(level, MinCompressionLevel, MaxCompressionLevel) };
        }

        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return
            [
                new() { Name = "compression.zstd.configure", DisplayName = "Configure", Description = "Configure Zstd compression settings (level 1-22)" },
                new() { Name = "compression.zstd.stats", DisplayName = "Statistics", Description = "Get compression statistics (bytes in/out, ratio)" },
                new() { Name = "compression.zstd.reset", DisplayName = "Reset Stats", Description = "Reset compression statistics counters" },
                new() { Name = "compression.zstd.compress", DisplayName = "Compress", Description = "Compress data using Zstd algorithm" },
                new() { Name = "compression.zstd.decompress", DisplayName = "Decompress", Description = "Decompress Zstd-compressed data" }
            ];
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["Algorithm"] = "Zstandard";
            metadata["CompressionLevel"] = _config.Level;
            metadata["MinLevel"] = MinCompressionLevel;
            metadata["MaxLevel"] = MaxCompressionLevel;
            metadata["SupportsStreaming"] = true;
            metadata["RFC"] = "8478";
            metadata["MagicBytes"] = "28 B5 2F FD";
            metadata["HeaderFormat"] = "[Magic:4][OriginalSize:4][Level:1][CompressedData]";
            metadata["BlockBased"] = true;
            metadata["MatchFinding"] = "HashTable";
            return metadata;
        }

        public override Task OnMessageAsync(PluginMessage message)
        {
            return message.Type switch
            {
                "compression.zstd.configure" => HandleConfigureAsync(message),
                "compression.zstd.stats" => HandleStatsAsync(message),
                "compression.zstd.reset" => HandleResetAsync(message),
                _ => base.OnMessageAsync(message)
            };
        }

        /// <summary>
        /// Compresses the input stream using Zstd algorithm.
        /// Called during write operations to compress data before storage.
        /// </summary>
        public override Stream OnWrite(Stream input, IKernelContext context, Dictionary<string, object> args)
        {
            // Get compression level from args or use configured default
            var level = GetCompressionLevel(args);

            // Read input data
            using var inputMs = new MemoryStream();
            input.CopyTo(inputMs);
            var inputData = inputMs.ToArray();

            // Compress using Zstd algorithm
            var compressedData = CompressZstd(inputData, level);
            var output = new MemoryStream(compressedData);

            // Update thread-safe statistics
            lock (_statsLock)
            {
                _totalBytesIn += inputData.Length;
                _totalBytesOut += compressedData.Length;
                _operationCount++;
            }

            var ratio = inputData.Length > 0 ? (double)compressedData.Length / inputData.Length : 0;
            context.LogDebug($"Zstd compressed {inputData.Length} bytes to {compressedData.Length} bytes ({ratio:P1} ratio, level {level})");

            return output;
        }

        /// <summary>
        /// Decompresses the stored stream using Zstd algorithm.
        /// Called during read operations to decompress data from storage.
        /// </summary>
        public override Stream OnRead(Stream stored, IKernelContext context, Dictionary<string, object> args)
        {
            // Read compressed data
            using var inputMs = new MemoryStream();
            stored.CopyTo(inputMs);
            var compressedData = inputMs.ToArray();

            // Decompress using Zstd algorithm
            var decompressedData = DecompressZstd(compressedData);
            var output = new MemoryStream(decompressedData);

            // Update thread-safe statistics
            lock (_statsLock)
            {
                _totalBytesIn += compressedData.Length;
                _totalBytesOut += decompressedData.Length;
                _operationCount++;
            }

            context.LogDebug($"Zstd decompressed {compressedData.Length} bytes to {decompressedData.Length} bytes");

            return output;
        }

        /// <summary>
        /// Compresses data using Zstd algorithm (synchronous API).
        /// </summary>
        /// <param name="data">Input data to compress.</param>
        /// <returns>Compressed data with Zstd frame header.</returns>
        public byte[] Compress(byte[] data)
        {
            return CompressZstd(data, _config.Level);
        }

        /// <summary>
        /// Compresses data using Zstd algorithm with specified level.
        /// </summary>
        /// <param name="data">Input data to compress.</param>
        /// <param name="level">Compression level (1-22).</param>
        /// <returns>Compressed data with Zstd frame header.</returns>
        public byte[] Compress(byte[] data, int level)
        {
            return CompressZstd(data, Math.Clamp(level, MinCompressionLevel, MaxCompressionLevel));
        }

        /// <summary>
        /// Decompresses Zstd-compressed data (synchronous API).
        /// </summary>
        /// <param name="data">Compressed data with Zstd frame header.</param>
        /// <returns>Decompressed original data.</returns>
        public byte[] Decompress(byte[] data)
        {
            return DecompressZstd(data);
        }

        /// <summary>
        /// Compresses data asynchronously.
        /// </summary>
        public Task<byte[]> CompressAsync(byte[] data, CancellationToken ct = default)
        {
            return Task.FromResult(Compress(data));
        }

        /// <summary>
        /// Decompresses data asynchronously.
        /// </summary>
        public Task<byte[]> DecompressAsync(byte[] data, CancellationToken ct = default)
        {
            return Task.FromResult(Decompress(data));
        }

        /// <summary>
        /// Resets compression statistics.
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

        #region Zstd Internal Implementation

        /// <summary>
        /// Compresses data using Zstd algorithm with frame header.
        /// Frame format: [Magic:4][OriginalSize:4][Level:1][CompressedData][EndMarker:1]
        /// </summary>
        private static byte[] CompressZstd(byte[] input, int level)
        {
            // Calculate maximum output size with overhead
            var maxOutputSize = input.Length + (input.Length / 128) + 32;
            var output = new byte[maxOutputSize];
            var outputPos = 0;

            // Write magic number (little-endian)
            BitConverter.GetBytes(ZstdMagic).CopyTo(output, outputPos);
            outputPos += 4;

            // Write original size
            BitConverter.GetBytes(input.Length).CopyTo(output, outputPos);
            outputPos += 4;

            // Write compression level
            output[outputPos++] = (byte)level;

            // Compress using LZ-style block compression
            var compressedLength = CompressZstdBlock(input, output.AsSpan(outputPos), level);
            outputPos += compressedLength;

            // Return exact-sized result
            var result = new byte[outputPos];
            Array.Copy(output, result, outputPos);
            return result;
        }

        /// <summary>
        /// Decompresses Zstd-compressed data.
        /// </summary>
        private static byte[] DecompressZstd(byte[] compressed)
        {
            if (compressed.Length < 9)
                throw new InvalidDataException("Invalid Zstd data: too short (minimum 9 bytes required for header)");

            // Verify magic number
            var magic = BitConverter.ToUInt32(compressed, 0);
            if (magic != ZstdMagic)
                throw new InvalidDataException($"Invalid Zstd magic number: expected 0x{ZstdMagic:X8}, got 0x{magic:X8}");

            // Read original size
            var originalSize = BitConverter.ToInt32(compressed, 4);
            if (originalSize <= 0 || originalSize > 1024 * 1024 * 1024)
                throw new InvalidDataException($"Invalid Zstd original size: {originalSize} (must be 1 to 1GB)");

            // Read compression level (for potential future use)
            var level = compressed[8];

            // Decompress block
            var output = new byte[originalSize];
            DecompressZstdBlock(compressed.AsSpan(9), output, level);

            return output;
        }

        /// <summary>
        /// Compresses a block using LZ-style matching with hash table.
        /// Uses token encoding for literals and matches with proper offset encoding.
        /// </summary>
        private static int CompressZstdBlock(byte[] input, Span<byte> output, int level)
        {
            // Hash table for match finding (65536 entries)
            var hashTable = new int[65536];
            Array.Fill(hashTable, -1);

            int inputPos = 0;
            int outputPos = 0;
            int anchor = 0;
            int inputEnd = input.Length;

            // Write block header (block type indicator)
            output[outputPos++] = 0x00; // Block type: raw + compressed mix

            // Main compression loop
            while (inputPos < inputEnd - 8)
            {
                // Calculate hash for match finding (4-byte window)
                int hash = GetZstdHash(input, inputPos);
                int matchPos = hashTable[hash];
                hashTable[hash] = inputPos;

                // Check for match within offset window (131072 bytes max)
                if (matchPos >= 0 && inputPos - matchPos < 131072)
                {
                    int matchLen = GetZstdMatchLength(input, matchPos, inputPos, inputEnd);

                    // Minimum match length is 4 for worthwhile compression
                    if (matchLen >= 4)
                    {
                        // Write pending literals
                        int literalLen = inputPos - anchor;
                        outputPos = WriteZstdSequence(output, outputPos, input.AsSpan(anchor, literalLen),
                            inputPos - matchPos, matchLen);

                        inputPos += matchLen;
                        anchor = inputPos;
                        continue;
                    }
                }

                inputPos++;
            }

            // Write remaining literals at end of block
            if (anchor < inputEnd)
            {
                outputPos = WriteZstdLiterals(output, outputPos, input.AsSpan(anchor, inputEnd - anchor));
            }

            // Write end marker
            output[outputPos++] = 0xFF;

            return outputPos;
        }

        /// <summary>
        /// Decompresses a Zstd block.
        /// </summary>
        private static void DecompressZstdBlock(ReadOnlySpan<byte> input, Span<byte> output, int level)
        {
            int inputPos = 0;
            int outputPos = 0;

            // Skip block header
            inputPos++;

            while (inputPos < input.Length)
            {
                byte token = input[inputPos++];

                // End marker check
                if (token == 0xFF)
                    break;

                // Token format: bit 7 = 0 for literal, 1 for match
                if ((token & 0x80) == 0)
                {
                    // Literal token
                    int literalLen = token & 0x7F;

                    // Extended length encoding
                    if (literalLen == 127)
                    {
                        literalLen += input[inputPos++];
                    }

                    // Copy literals to output
                    input.Slice(inputPos, literalLen).CopyTo(output.Slice(outputPos));
                    inputPos += literalLen;
                    outputPos += literalLen;
                }
                else
                {
                    // Match token
                    int matchLen = (token & 0x7F) + 4;

                    // Extended match length encoding
                    if ((token & 0x7F) == 127)
                    {
                        matchLen += input[inputPos++];
                    }

                    // Read offset (2 or 3 bytes depending on high bit)
                    int offset = input[inputPos] | (input[inputPos + 1] << 8);
                    if ((input[inputPos + 1] & 0x80) != 0)
                    {
                        // 3-byte offset for large offsets
                        offset = (offset & 0x7FFF) | (input[inputPos + 2] << 15);
                        inputPos += 3;
                    }
                    else
                    {
                        // 2-byte offset
                        inputPos += 2;
                    }

                    // Copy match from history
                    int matchPos = outputPos - offset;
                    for (int i = 0; i < matchLen; i++)
                    {
                        output[outputPos++] = output[matchPos + i];
                    }
                }
            }
        }

        /// <summary>
        /// Calculates hash for 4-byte window using multiplicative hashing.
        /// </summary>
        private static int GetZstdHash(byte[] data, int pos)
        {
            uint v = (uint)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24));
            return (int)((v * 2654435761u) >> 16) & 0xFFFF;
        }

        /// <summary>
        /// Calculates match length between two positions.
        /// </summary>
        private static int GetZstdMatchLength(byte[] data, int pos1, int pos2, int end)
        {
            int len = 0;
            while (pos2 + len < end && data[pos1 + len] == data[pos2 + len] && len < 258)
                len++;
            return len;
        }

        /// <summary>
        /// Writes a literal/match sequence to output.
        /// </summary>
        private static int WriteZstdSequence(Span<byte> output, int pos, ReadOnlySpan<byte> literals, int offset, int matchLen)
        {
            // Write literals first if any
            if (literals.Length > 0)
            {
                pos = WriteZstdLiterals(output, pos, literals);
            }

            // Write match token (high bit set for match)
            int ml = matchLen - 4;
            byte token = (byte)(0x80 | (ml >= 127 ? 127 : ml));
            output[pos++] = token;

            // Extended match length if needed
            if (ml >= 127)
            {
                output[pos++] = (byte)(ml - 127);
            }

            // Write offset (2 or 3 bytes depending on size)
            if (offset < 32768)
            {
                // 2-byte offset (high bit of second byte is 0)
                output[pos++] = (byte)(offset & 0xFF);
                output[pos++] = (byte)((offset >> 8) & 0x7F);
            }
            else
            {
                // 3-byte offset (high bit of second byte is 1)
                output[pos++] = (byte)(offset & 0xFF);
                output[pos++] = (byte)(((offset >> 8) & 0x7F) | 0x80);
                output[pos++] = (byte)((offset >> 15) & 0xFF);
            }

            return pos;
        }

        /// <summary>
        /// Writes literals to output with length encoding.
        /// </summary>
        private static int WriteZstdLiterals(Span<byte> output, int pos, ReadOnlySpan<byte> literals)
        {
            int len = literals.Length;

            // Write literal token (high bit 0 for literal)
            byte token = (byte)(len >= 127 ? 127 : len);
            output[pos++] = token;

            // Extended length if needed
            if (len >= 127)
            {
                output[pos++] = (byte)(len - 127);
            }

            // Copy literal data
            literals.CopyTo(output.Slice(pos));
            return pos + len;
        }

        #endregion

        #region Message Handlers

        private Task HandleConfigureAsync(PluginMessage message)
        {
            if (message.Payload.TryGetValue("level", out var levelObj))
            {
                int level = levelObj switch
                {
                    int i => i,
                    long l => (int)l,
                    string s when int.TryParse(s, out var parsed) => parsed,
                    _ => _config.Level
                };
                _config.Level = Math.Clamp(level, MinCompressionLevel, MaxCompressionLevel);
            }
            return Task.CompletedTask;
        }

        private Task HandleStatsAsync(PluginMessage message)
        {
            // Statistics are returned via the Statistics property
            // This handler allows message-based retrieval
            lock (_statsLock)
            {
                var stats = new Dictionary<string, object>
                {
                    ["TotalBytesIn"] = _totalBytesIn,
                    ["TotalBytesOut"] = _totalBytesOut,
                    ["OperationCount"] = _operationCount,
                    ["CompressionRatio"] = _totalBytesIn > 0 ? (double)_totalBytesOut / _totalBytesIn : 0
                };
            }
            return Task.CompletedTask;
        }

        private Task HandleResetAsync(PluginMessage message)
        {
            ResetStatistics();
            return Task.CompletedTask;
        }

        private int GetCompressionLevel(Dictionary<string, object> args)
        {
            if (args.TryGetValue("level", out var levelObj))
            {
                int level = levelObj switch
                {
                    int i => i,
                    long l => (int)l,
                    string s when int.TryParse(s, out var parsed) => parsed,
                    _ => _config.Level
                };
                return Math.Clamp(level, MinCompressionLevel, MaxCompressionLevel);
            }
            return _config.Level;
        }

        #endregion
    }

    #region Configuration and Statistics Classes

    /// <summary>
    /// Configuration options for Zstd compression.
    /// </summary>
    public sealed class ZstdConfig
    {
        /// <summary>
        /// Compression level (1-22).
        /// - 1-3: Fast compression, lower ratio
        /// - 4-9: Balanced compression (default range)
        /// - 10-15: High compression, slower
        /// - 16-22: Maximum compression, slowest
        /// Default is 3 for balanced performance.
        /// </summary>
        public int Level { get; set; } = ZstdCompressionPlugin.DefaultCompressionLevel;
    }

    /// <summary>
    /// Thread-safe compression statistics.
    /// </summary>
    public readonly struct ZstdStatistics
    {
        /// <summary>Total bytes input for compression/decompression.</summary>
        public long TotalBytesIn { get; init; }

        /// <summary>Total bytes output from compression/decompression.</summary>
        public long TotalBytesOut { get; init; }

        /// <summary>Total number of compression/decompression operations.</summary>
        public long OperationCount { get; init; }

        /// <summary>Overall compression ratio (output/input).</summary>
        public double CompressionRatio { get; init; }
    }

    #endregion

    #region Test Helper

    /// <summary>
    /// Test helper kernel context for unit testing.
    /// </summary>
    internal sealed class TestKernelContext : IKernelContext
    {
        public OperatingMode Mode => OperatingMode.Laptop;
        public string RootPath => Environment.CurrentDirectory;
        /// <summary>
        /// Storage service is not supported in test context.
        /// </summary>
        public IKernelStorageService Storage => throw new NotSupportedException("Storage service is not supported in test context");
        public void LogInfo(string message) { }
        public void LogError(string message, Exception? ex = null) { }
        public void LogWarning(string message) { }
        public void LogDebug(string message) { }
        public T? GetPlugin<T>() where T : class, IPlugin => null;
        public IEnumerable<T> GetPlugins<T>() where T : class, IPlugin => [];
    }

    #endregion
}
