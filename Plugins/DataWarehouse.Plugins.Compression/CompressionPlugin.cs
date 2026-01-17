using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.IO.Compression;
using SysCompressionLevel = System.IO.Compression.CompressionLevel;

namespace DataWarehouse.Plugins.Compression
{
    /// <summary>
    /// GZip compression plugin for DataWarehouse pipeline.
    /// Extends PipelinePluginBase for bidirectional stream transformation.
    ///
    /// Message Commands:
    /// - compression.gzip.configure: Configure compression level
    /// - compression.gzip.stats: Get compression statistics
    /// - compression.gzip.test: Test compression on sample data
    /// </summary>
    public sealed class GZipCompressionPlugin : PipelinePluginBase
    {
        private readonly GZipConfig _config;
        private readonly object _statsLock = new();
        private long _totalBytesIn;
        private long _totalBytesOut;
        private long _operationCount;

        public override string Id => "datawarehouse.plugins.compression.gzip";
        public override string Name => "GZip Compression";
        public override string Version => "1.0.0";
        public override string SubCategory => "Compression";
        public override int QualityLevel => _config.Level switch
        {
            System.IO.Compression.CompressionLevel.Fastest => 20,
            System.IO.Compression.CompressionLevel.Optimal => 60,
            System.IO.Compression.CompressionLevel.SmallestSize => 90,
            _ => 50
        };

        public override int DefaultOrder => 50;
        public override bool AllowBypass => true;

        public GZipCompressionPlugin(GZipConfig? config = null)
        {
            _config = config ?? new GZipConfig();
        }

        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return
            [
                new() { Name = "compression.gzip.configure", DisplayName = "Configure", Description = "Configure GZip compression settings" },
                new() { Name = "compression.gzip.stats", DisplayName = "Statistics", Description = "Get compression statistics" },
                new() { Name = "compression.gzip.test", DisplayName = "Test", Description = "Test compression on sample data" },
                new() { Name = "compression.gzip.compress", DisplayName = "Compress", Description = "Compress data using GZip" },
                new() { Name = "compression.gzip.decompress", DisplayName = "Decompress", Description = "Decompress GZip data" }
            ];
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["Algorithm"] = "GZip";
            metadata["CompressionLevel"] = _config.Level.ToString();
            metadata["BufferSize"] = _config.BufferSize;
            metadata["SupportsStreaming"] = true;
            metadata["MagicBytes"] = "1F 8B";
            return metadata;
        }

        public override Task OnMessageAsync(PluginMessage message)
        {
            return message.Type switch
            {
                "compression.gzip.configure" => HandleConfigureAsync(message),
                "compression.gzip.stats" => HandleStatsAsync(message),
                "compression.gzip.test" => HandleTestAsync(message),
                _ => base.OnMessageAsync(message)
            };
        }

        public override Stream OnWrite(Stream input, IKernelContext context, Dictionary<string, object> args)
        {
            var level = GetCompressionLevel(args);
            var bufferSize = GetBufferSize(args);

            var output = new MemoryStream();

            using (var gzip = new GZipStream(output, level, leaveOpen: true))
            {
                input.CopyTo(gzip, bufferSize);
            }

            output.Position = 0;

            lock (_statsLock)
            {
                _totalBytesIn += input.Length;
                _totalBytesOut += output.Length;
                _operationCount++;
            }

            context.LogDebug($"GZip compressed {input.Length} bytes to {output.Length} bytes ({GetCompressionRatio(input.Length, output.Length):P1} ratio)");

            return output;
        }

        public override Stream OnRead(Stream stored, IKernelContext context, Dictionary<string, object> args)
        {
            var bufferSize = GetBufferSize(args);
            var output = new MemoryStream();

            using (var gzip = new GZipStream(stored, CompressionMode.Decompress, leaveOpen: true))
            {
                gzip.CopyTo(output, bufferSize);
            }

            output.Position = 0;

            lock (_statsLock)
            {
                _totalBytesIn += stored.Length;
                _totalBytesOut += output.Length;
                _operationCount++;
            }

            context.LogDebug($"GZip decompressed {stored.Length} bytes to {output.Length} bytes");

            return output;
        }

        private SysCompressionLevel GetCompressionLevel(Dictionary<string, object> args)
        {
            if (args.TryGetValue("level", out var levelObj))
            {
                return levelObj switch
                {
                    SysCompressionLevel cl => cl,
                    string s when Enum.TryParse<SysCompressionLevel>(s, true, out var parsed) => parsed,
                    int i => (SysCompressionLevel)Math.Clamp(i, 0, 2),
                    _ => _config.Level
                };
            }
            return _config.Level;
        }

        private int GetBufferSize(Dictionary<string, object> args)
        {
            if (args.TryGetValue("bufferSize", out var bufferObj) && bufferObj is int size)
            {
                return Math.Clamp(size, 4096, 1048576);
            }
            return _config.BufferSize;
        }

        private static double GetCompressionRatio(long original, long compressed)
        {
            return original > 0 ? (double)compressed / original : 0;
        }

        private Task HandleConfigureAsync(PluginMessage message)
        {
            if (message.Payload.TryGetValue("level", out var levelObj) && levelObj is string levelStr)
            {
                if (Enum.TryParse<System.IO.Compression.CompressionLevel>(levelStr, true, out var level))
                {
                    _config.Level = level;
                }
            }
            return Task.CompletedTask;
        }

        private Task HandleStatsAsync(PluginMessage message)
        {
            lock (_statsLock)
            {
                var stats = new Dictionary<string, object>
                {
                    ["TotalBytesIn"] = _totalBytesIn,
                    ["TotalBytesOut"] = _totalBytesOut,
                    ["OperationCount"] = _operationCount,
                    ["CompressionRatio"] = GetCompressionRatio(_totalBytesIn, _totalBytesOut)
                };
            }
            return Task.CompletedTask;
        }

        private async Task HandleTestAsync(PluginMessage message)
        {
            if (message.Payload.TryGetValue("data", out var dataObj) && dataObj is byte[] data)
            {
                using var input = new MemoryStream(data);
                var compressed = OnWrite(input, new TestKernelContext(), new Dictionary<string, object>());
                var ratio = GetCompressionRatio(data.Length, compressed.Length);
            }
            await Task.CompletedTask;
        }
    }

    /// <summary>
    /// LZ4 compression plugin for DataWarehouse pipeline.
    /// High-speed compression with configurable block size.
    ///
    /// Message Commands:
    /// - compression.lz4.configure: Configure compression settings
    /// - compression.lz4.stats: Get compression statistics
    /// </summary>
    public sealed class LZ4CompressionPlugin : PipelinePluginBase
    {
        private readonly LZ4Config _config;
        private readonly object _statsLock = new();
        private long _totalBytesIn;
        private long _totalBytesOut;
        private long _operationCount;

        public override string Id => "datawarehouse.plugins.compression.lz4";
        public override string Name => "LZ4 Compression";
        public override string Version => "1.0.0";
        public override string SubCategory => "Compression";
        public override int QualityLevel => _config.HighCompression ? 70 : 30;
        public override int DefaultOrder => 50;
        public override bool AllowBypass => true;

        public LZ4CompressionPlugin(LZ4Config? config = null)
        {
            _config = config ?? new LZ4Config();
        }

        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return
            [
                new() { Name = "compression.lz4.configure", DisplayName = "Configure", Description = "Configure LZ4 compression settings" },
                new() { Name = "compression.lz4.stats", DisplayName = "Statistics", Description = "Get compression statistics" },
                new() { Name = "compression.lz4.compress", DisplayName = "Compress", Description = "Compress data using LZ4" },
                new() { Name = "compression.lz4.decompress", DisplayName = "Decompress", Description = "Decompress LZ4 data" }
            ];
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["Algorithm"] = "LZ4";
            metadata["HighCompression"] = _config.HighCompression;
            metadata["BlockSize"] = _config.BlockSize;
            metadata["SupportsStreaming"] = true;
            metadata["MagicBytes"] = "04 22 4D 18";
            return metadata;
        }

        public override Task OnMessageAsync(PluginMessage message)
        {
            return message.Type switch
            {
                "compression.lz4.configure" => HandleConfigureAsync(message),
                "compression.lz4.stats" => HandleStatsAsync(message),
                _ => base.OnMessageAsync(message)
            };
        }

        public override Stream OnWrite(Stream input, IKernelContext context, Dictionary<string, object> args)
        {
            var highCompression = GetHighCompression(args);
            var blockSize = GetBlockSize(args);

            using var inputMs = new MemoryStream();
            input.CopyTo(inputMs);
            var inputData = inputMs.ToArray();

            var compressedData = LZ4Compress(inputData, highCompression, blockSize);
            var output = new MemoryStream(compressedData);

            lock (_statsLock)
            {
                _totalBytesIn += inputData.Length;
                _totalBytesOut += compressedData.Length;
                _operationCount++;
            }

            context.LogDebug($"LZ4 compressed {inputData.Length} bytes to {compressedData.Length} bytes");
            return output;
        }

        public override Stream OnRead(Stream stored, IKernelContext context, Dictionary<string, object> args)
        {
            using var inputMs = new MemoryStream();
            stored.CopyTo(inputMs);
            var compressedData = inputMs.ToArray();

            var decompressedData = LZ4Decompress(compressedData);
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

        private static byte[] LZ4Compress(byte[] input, bool highCompression, int blockSize)
        {
            var maxOutputSize = GetMaxCompressedSize(input.Length);
            var output = new byte[maxOutputSize + 8];

            BitConverter.GetBytes(input.Length).CopyTo(output, 0);
            BitConverter.GetBytes((int)(highCompression ? 1 : 0)).CopyTo(output, 4);

            var compressedLength = CompressLZ4Block(input, output.AsSpan(8), highCompression);

            var result = new byte[compressedLength + 8];
            Array.Copy(output, result, result.Length);
            return result;
        }

        private static byte[] LZ4Decompress(byte[] compressed)
        {
            if (compressed.Length < 8)
                throw new InvalidDataException("Invalid LZ4 data: too short");

            var originalSize = BitConverter.ToInt32(compressed, 0);
            if (originalSize <= 0 || originalSize > 1024 * 1024 * 1024)
                throw new InvalidDataException($"Invalid LZ4 original size: {originalSize}");

            var output = new byte[originalSize];
            DecompressLZ4Block(compressed.AsSpan(8), output);
            return output;
        }

        private static int GetMaxCompressedSize(int inputLength)
        {
            return inputLength + (inputLength / 255) + 16;
        }

        private static int CompressLZ4Block(byte[] input, Span<byte> output, bool highCompression)
        {
            var hashTable = new int[65536];
            Array.Fill(hashTable, -1);

            int inputPos = 0;
            int outputPos = 0;
            int anchor = 0;
            int inputEnd = input.Length;

            while (inputPos < inputEnd - 12)
            {
                int hash = GetHash(input, inputPos);
                int matchPos = hashTable[hash];
                hashTable[hash] = inputPos;

                if (matchPos >= 0 && inputPos - matchPos < 65535 &&
                    MatchesAt(input, matchPos, inputPos, Math.Min(inputEnd - inputPos, 255)))
                {
                    int literalLength = inputPos - anchor;
                    int matchLength = GetMatchLength(input, matchPos, inputPos, inputEnd);

                    outputPos = WriteLZ4Sequence(output, outputPos, input.AsSpan(anchor, literalLength),
                        inputPos - matchPos, matchLength);

                    inputPos += matchLength;
                    anchor = inputPos;
                }
                else
                {
                    inputPos++;
                }
            }

            if (anchor < inputEnd)
            {
                outputPos = WriteLastLiterals(output, outputPos, input.AsSpan(anchor, inputEnd - anchor));
            }

            return outputPos;
        }

        private static void DecompressLZ4Block(ReadOnlySpan<byte> input, Span<byte> output)
        {
            int inputPos = 0;
            int outputPos = 0;

            while (inputPos < input.Length)
            {
                byte token = input[inputPos++];
                int literalLength = token >> 4;

                if (literalLength == 15)
                {
                    byte b;
                    do
                    {
                        b = input[inputPos++];
                        literalLength += b;
                    } while (b == 255 && inputPos < input.Length);
                }

                if (literalLength > 0)
                {
                    input.Slice(inputPos, literalLength).CopyTo(output.Slice(outputPos));
                    inputPos += literalLength;
                    outputPos += literalLength;
                }

                if (inputPos >= input.Length)
                    break;

                int offset = input[inputPos] | (input[inputPos + 1] << 8);
                inputPos += 2;

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

                int matchPos = outputPos - offset;
                for (int i = 0; i < matchLength; i++)
                {
                    output[outputPos++] = output[matchPos + i];
                }
            }
        }

        private static int GetHash(byte[] data, int pos)
        {
            uint v = (uint)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24));
            return (int)((v * 2654435761u) >> 16) & 0xFFFF;
        }

        private static bool MatchesAt(byte[] data, int pos1, int pos2, int maxLen)
        {
            for (int i = 0; i < Math.Min(4, maxLen); i++)
            {
                if (data[pos1 + i] != data[pos2 + i]) return false;
            }
            return true;
        }

        private static int GetMatchLength(byte[] data, int pos1, int pos2, int end)
        {
            int len = 4;
            while (pos2 + len < end && data[pos1 + len] == data[pos2 + len] && len < 255)
                len++;
            return len;
        }

        private static int WriteLZ4Sequence(Span<byte> output, int pos, ReadOnlySpan<byte> literals, int offset, int matchLen)
        {
            int litLen = literals.Length;
            int ml = matchLen - 4;

            byte token = (byte)(((litLen >= 15 ? 15 : litLen) << 4) | (ml >= 15 ? 15 : ml));
            output[pos++] = token;

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

            literals.CopyTo(output.Slice(pos));
            pos += litLen;

            output[pos++] = (byte)(offset & 0xFF);
            output[pos++] = (byte)((offset >> 8) & 0xFF);

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

        private static int WriteLastLiterals(Span<byte> output, int pos, ReadOnlySpan<byte> literals)
        {
            int litLen = literals.Length;
            byte token = (byte)((litLen >= 15 ? 15 : litLen) << 4);
            output[pos++] = token;

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

            literals.CopyTo(output.Slice(pos));
            return pos + litLen;
        }

        private bool GetHighCompression(Dictionary<string, object> args)
        {
            if (args.TryGetValue("highCompression", out var hcObj) && hcObj is bool hc)
                return hc;
            return _config.HighCompression;
        }

        private int GetBlockSize(Dictionary<string, object> args)
        {
            if (args.TryGetValue("blockSize", out var bsObj) && bsObj is int bs)
                return Math.Clamp(bs, 64 * 1024, 4 * 1024 * 1024);
            return _config.BlockSize;
        }

        private Task HandleConfigureAsync(PluginMessage message)
        {
            if (message.Payload.TryGetValue("highCompression", out var hcObj) && hcObj is bool hc)
                _config.HighCompression = hc;
            if (message.Payload.TryGetValue("blockSize", out var bsObj) && bsObj is int bs)
                _config.BlockSize = Math.Clamp(bs, 64 * 1024, 4 * 1024 * 1024);
            return Task.CompletedTask;
        }

        private Task HandleStatsAsync(PluginMessage message)
        {
            lock (_statsLock)
            {
                var stats = new Dictionary<string, object>
                {
                    ["TotalBytesIn"] = _totalBytesIn,
                    ["TotalBytesOut"] = _totalBytesOut,
                    ["OperationCount"] = _operationCount
                };
            }
            return Task.CompletedTask;
        }
    }

    public class GZipConfig
    {
        public SysCompressionLevel Level { get; set; } = SysCompressionLevel.Optimal;
        public int BufferSize { get; set; } = 81920;
    }

    public class LZ4Config
    {
        public bool HighCompression { get; set; } = false;
        public int BlockSize { get; set; } = 64 * 1024;
    }

    internal class TestKernelContext : IKernelContext
    {
        public OperatingMode Mode => OperatingMode.Laptop;
        public string RootPath => Environment.CurrentDirectory;
        public void LogInfo(string message) { }
        public void LogError(string message, Exception? ex = null) { }
        public void LogWarning(string message) { }
        public void LogDebug(string message) { }
        public T? GetPlugin<T>() where T : class, IPlugin => null;
        public IEnumerable<T> GetPlugins<T>() where T : class, IPlugin => [];
    }
}
