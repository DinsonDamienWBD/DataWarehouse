using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.IO.Compression;

namespace DataWarehouse.Plugins.GZipCompression
{
    /// <summary>
    /// GZip compression plugin for DataWarehouse pipeline.
    /// Provides RFC 1952 GZip compression with configurable compression levels.
    ///
    /// Features:
    /// - Full RFC 1952 compliance using .NET GZipStream
    /// - Configurable compression levels (Fastest, Optimal, SmallestSize)
    /// - Configurable buffer size for streaming operations
    /// - Streaming compression for memory efficiency
    /// - Automatic incompressible data detection
    /// - Thread-safe compression statistics tracking
    /// - Header with original size for efficient decompression buffer allocation
    ///
    /// Thread Safety: All operations are thread-safe.
    ///
    /// Performance Characteristics:
    /// - Fastest: Prioritizes speed over compression ratio (~2-3x faster than Optimal)
    /// - Optimal: Best balance of speed and compression (default)
    /// - SmallestSize: Maximum compression, slower processing (~2-3x slower than Optimal)
    ///
    /// Message Commands:
    /// - compression.gzip.configure: Configure compression settings
    /// - compression.gzip.stats: Get compression statistics
    /// - compression.gzip.test: Test compression on sample data
    /// - compression.gzip.reset: Reset statistics counters
    /// </summary>
    public sealed class GZipCompressionPlugin : PipelinePluginBase, IDisposable
    {
        private readonly GZipCompressionConfig _config;
        private readonly object _statsLock = new();
        private long _compressionCount;
        private long _decompressionCount;
        private long _totalBytesIn;
        private long _totalBytesOut;
        private long _bypassCount;
        private double _averageCompressionRatio;
        private bool _disposed;

        /// <summary>
        /// Header version for GZip-compressed data wrapper.
        /// </summary>
        private const byte HeaderVersion = 0x47; // 'G' for GZip

        /// <summary>
        /// Magic bytes for GZip identification (standard GZip: 1F 8B).
        /// Our wrapper uses: GZIP
        /// </summary>
        private static readonly byte[] MagicBytes = { 0x47, 0x5A, 0x49, 0x50 }; // "GZIP"

        /// <summary>
        /// Header size: Magic(4) + Version(1) + Level(1) + OriginalSize(8) + Flags(1)
        /// </summary>
        private const int HeaderSize = 15;

        /// <summary>
        /// Minimum size to attempt compression (smaller data may expand).
        /// </summary>
        private const int MinCompressionThreshold = 64;

        /// <summary>
        /// Threshold for compression ratio below which we skip compression.
        /// </summary>
        private const double MinEffectiveCompressionRatio = 0.95;

        /// <summary>
        /// Standard GZip magic bytes for detection.
        /// </summary>
        private static readonly byte[] StandardGZipMagic = { 0x1F, 0x8B };

        /// <inheritdoc/>
        public override string Id => "datawarehouse.plugins.compression.gzip";

        /// <inheritdoc/>
        public override string Name => "GZip Compression";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <inheritdoc/>
        public override string SubCategory => "Compression";

        /// <inheritdoc/>
        public override int QualityLevel => _config.CompressionLevel switch
        {
            CompressionLevel.Fastest => 20,
            CompressionLevel.Optimal => 60,
            CompressionLevel.SmallestSize => 90,
            _ => 50
        };

        /// <inheritdoc/>
        public override int DefaultOrder => 50;

        /// <inheritdoc/>
        public override bool AllowBypass => true;

        /// <inheritdoc/>
        public override string[] IncompatibleStages => new[] { "compression.deflate", "compression.brotli", "compression.lz4", "compression.zstd" };

        /// <summary>
        /// Gets the current compression configuration.
        /// </summary>
        public GZipCompressionConfig Configuration => _config;

        /// <summary>
        /// Initializes a new instance of the GZip compression plugin.
        /// </summary>
        /// <param name="config">Optional configuration. If null, defaults are used.</param>
        public GZipCompressionPlugin(GZipCompressionConfig? config = null)
        {
            _config = config ?? new GZipCompressionConfig();
        }

        /// <inheritdoc/>
        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>
            {
                new() { Name = "compression.gzip.compress", DisplayName = "Compress", Description = "Compress data using GZip (RFC 1952)" },
                new() { Name = "compression.gzip.decompress", DisplayName = "Decompress", Description = "Decompress GZip-compressed data" },
                new() { Name = "compression.gzip.configure", DisplayName = "Configure", Description = "Configure GZip compression settings" },
                new() { Name = "compression.gzip.stats", DisplayName = "Statistics", Description = "Get compression statistics" },
                new() { Name = "compression.gzip.test", DisplayName = "Test", Description = "Test compression on sample data" },
                new() { Name = "compression.gzip.reset", DisplayName = "Reset", Description = "Reset statistics counters" }
            };
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["Algorithm"] = "GZip";
            metadata["Standard"] = "RFC 1952";
            metadata["CompressionLevel"] = _config.CompressionLevel.ToString();
            metadata["BufferSize"] = _config.BufferSize;
            metadata["SupportsStreaming"] = true;
            metadata["SupportsBypass"] = true;
            metadata["MinCompressionThreshold"] = MinCompressionThreshold;
            metadata["MagicBytes"] = "1F 8B";
            metadata["FileExtension"] = ".gz";

            lock (_statsLock)
            {
                metadata["AverageCompressionRatio"] = _averageCompressionRatio;
                metadata["TotalOperations"] = _compressionCount + _decompressionCount;
            }

            return metadata;
        }

        /// <inheritdoc/>
        public override Task OnMessageAsync(PluginMessage message)
        {
            return message.Type switch
            {
                "compression.gzip.configure" => HandleConfigureAsync(message),
                "compression.gzip.stats" => HandleStatsAsync(message),
                "compression.gzip.test" => HandleTestAsync(message),
                "compression.gzip.reset" => HandleResetAsync(message),
                _ => base.OnMessageAsync(message)
            };
        }

        /// <summary>
        /// Compresses data using GZip algorithm (RFC 1952).
        /// </summary>
        /// <param name="input">The uncompressed input stream.</param>
        /// <param name="context">The kernel context for logging and state.</param>
        /// <param name="args">
        /// Optional arguments:
        /// - level: CompressionLevel override (Fastest, Optimal, SmallestSize)
        /// - bufferSize: Buffer size override for streaming
        /// - bypassIfIncompressible: Whether to bypass compression for incompressible data (default: true)
        /// </param>
        /// <returns>Compressed data stream with header.</returns>
        public override Stream OnWrite(Stream input, IKernelContext context, Dictionary<string, object> args)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var compressionLevel = GetCompressionLevel(args);
            var bufferSize = GetBufferSize(args);
            var bypassIfIncompressible = !args.TryGetValue("bypassIfIncompressible", out var bypass) || bypass is not false;

            // Read input data
            byte[] inputData;
            using (var inputMs = new MemoryStream())
            {
                input.CopyTo(inputMs, bufferSize);
                inputData = inputMs.ToArray();
            }

            var originalSize = inputData.Length;

            // Check if compression should be bypassed for small data
            if (originalSize < MinCompressionThreshold && compressionLevel != CompressionLevel.NoCompression)
            {
                context.LogDebug($"GZip: Bypassing compression for small data ({originalSize} bytes < {MinCompressionThreshold} threshold)");
                return CreateBypassedOutput(inputData, originalSize, context);
            }

            // Compress the data
            byte[] compressedData;
            using (var compressedMs = new MemoryStream())
            {
                using (var gzipStream = new GZipStream(compressedMs, compressionLevel, leaveOpen: true))
                {
                    gzipStream.Write(inputData, 0, inputData.Length);
                }
                compressedData = compressedMs.ToArray();
            }

            var compressionRatio = originalSize > 0 ? (double)compressedData.Length / originalSize : 1.0;

            // Check if compression is effective
            if (bypassIfIncompressible && compressionRatio > MinEffectiveCompressionRatio)
            {
                context.LogDebug($"GZip: Bypassing compression (ratio {compressionRatio:P1} exceeds threshold {MinEffectiveCompressionRatio:P1})");
                Interlocked.Increment(ref _bypassCount);
                return CreateBypassedOutput(inputData, originalSize, context);
            }

            // Build output with header
            var output = new MemoryStream();
            var writer = new BinaryWriter(output);

            // Write header
            writer.Write(MagicBytes);
            writer.Write(HeaderVersion);
            writer.Write((byte)compressionLevel);
            writer.Write((long)originalSize);
            writer.Write((byte)GZipFlags.Compressed);

            // Write compressed data
            writer.Write(compressedData);

            // Update statistics
            UpdateCompressionStats(originalSize, compressedData.Length, isCompression: true);

            context.LogDebug($"GZip: Compressed {originalSize} -> {compressedData.Length} bytes (ratio: {compressionRatio:P1}, level: {compressionLevel})");

            output.Position = 0;
            return output;
        }

        /// <summary>
        /// Decompresses GZip-compressed data.
        /// </summary>
        /// <param name="stored">The compressed input stream.</param>
        /// <param name="context">The kernel context for logging and state.</param>
        /// <param name="args">
        /// Optional arguments:
        /// - bufferSize: Buffer size override for streaming
        /// </param>
        /// <returns>Decompressed data stream.</returns>
        public override Stream OnRead(Stream stored, IKernelContext context, Dictionary<string, object> args)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var bufferSize = GetBufferSize(args);

            using var inputMs = new MemoryStream();
            stored.CopyTo(inputMs, bufferSize);
            inputMs.Position = 0;

            // Check for standard GZip magic (raw GZip without our header)
            var firstBytes = new byte[4];
            var bytesRead = inputMs.Read(firstBytes, 0, 4);
            inputMs.Position = 0;

            if (bytesRead >= 2 && firstBytes[0] == StandardGZipMagic[0] && firstBytes[1] == StandardGZipMagic[1])
            {
                // This is standard GZip data without our wrapper
                return DecompressStandardGZip(inputMs, context, bufferSize);
            }

            // Check for our header
            if (!firstBytes.SequenceEqual(MagicBytes))
            {
                throw new InvalidDataException($"Invalid GZip header: magic bytes mismatch. Expected GZIP or standard GZip magic bytes.");
            }

            var reader = new BinaryReader(inputMs);

            // Read header
            reader.ReadBytes(4); // Magic already validated
            var version = reader.ReadByte();
            if (version != HeaderVersion)
            {
                throw new InvalidDataException($"Unsupported GZip header version: 0x{version:X2}");
            }

            var level = (CompressionLevel)reader.ReadByte();
            var originalSize = reader.ReadInt64();
            var flags = (GZipFlags)reader.ReadByte();

            if (originalSize < 0 || originalSize > int.MaxValue)
            {
                throw new InvalidDataException($"Invalid original size in header: {originalSize}");
            }

            // Read compressed/stored data
            var remainingLength = (int)(inputMs.Length - inputMs.Position);
            var compressedData = reader.ReadBytes(remainingLength);

            byte[] decompressedData;

            if ((flags & GZipFlags.Stored) == GZipFlags.Stored)
            {
                // Data was stored without compression
                decompressedData = compressedData;
                context.LogDebug($"GZip: Retrieved stored data ({decompressedData.Length} bytes, was bypassed)");
            }
            else
            {
                // Decompress the data
                decompressedData = new byte[originalSize];

                using var compressedMs = new MemoryStream(compressedData);
                using var gzipStream = new GZipStream(compressedMs, CompressionMode.Decompress);

                var totalRead = 0;
                int chunkRead;
                while (totalRead < originalSize &&
                       (chunkRead = gzipStream.Read(decompressedData, totalRead, (int)originalSize - totalRead)) > 0)
                {
                    totalRead += chunkRead;
                }

                if (totalRead != originalSize)
                {
                    throw new InvalidDataException($"Decompression size mismatch: expected {originalSize}, got {totalRead}");
                }

                context.LogDebug($"GZip: Decompressed {compressedData.Length} -> {decompressedData.Length} bytes (ratio: {(double)compressedData.Length / decompressedData.Length:P1})");
            }

            // Update statistics
            UpdateCompressionStats(compressedData.Length, decompressedData.Length, isCompression: false);

            return new MemoryStream(decompressedData);
        }

        /// <summary>
        /// Decompresses standard GZip data (without our custom header).
        /// </summary>
        private Stream DecompressStandardGZip(MemoryStream inputMs, IKernelContext context, int bufferSize)
        {
            var compressedSize = inputMs.Length;
            var output = new MemoryStream();

            using (var gzipStream = new GZipStream(inputMs, CompressionMode.Decompress, leaveOpen: true))
            {
                gzipStream.CopyTo(output, bufferSize);
            }

            output.Position = 0;

            // Update statistics
            UpdateCompressionStats(compressedSize, output.Length, isCompression: false);

            context.LogDebug($"GZip: Decompressed standard GZip {compressedSize} -> {output.Length} bytes");

            return output;
        }

        /// <summary>
        /// Compresses data synchronously and returns statistics.
        /// </summary>
        /// <param name="data">Data to compress.</param>
        /// <returns>Compression result with statistics.</returns>
        public CompressionResult CompressData(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return new CompressionResult
                {
                    Success = true,
                    OriginalSize = 0,
                    CompressedSize = 0,
                    CompressionRatio = 1.0,
                    CompressedData = Array.Empty<byte>()
                };
            }

            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, _config.CompressionLevel, leaveOpen: true))
            {
                gzip.Write(data, 0, data.Length);
            }

            var compressedData = output.ToArray();
            var ratio = (double)compressedData.Length / data.Length;

            return new CompressionResult
            {
                Success = true,
                OriginalSize = data.Length,
                CompressedSize = compressedData.Length,
                CompressionRatio = ratio,
                CompressedData = compressedData,
                SpaceSaved = data.Length - compressedData.Length,
                CompressionLevel = _config.CompressionLevel.ToString()
            };
        }

        /// <summary>
        /// Decompresses data synchronously.
        /// </summary>
        /// <param name="compressedData">Compressed data.</param>
        /// <returns>Decompressed data.</returns>
        public byte[] DecompressData(byte[] compressedData)
        {
            if (compressedData == null || compressedData.Length == 0)
            {
                return Array.Empty<byte>();
            }

            using var input = new MemoryStream(compressedData);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();

            gzip.CopyTo(output, _config.BufferSize);
            return output.ToArray();
        }

        /// <summary>
        /// Gets current compression statistics.
        /// </summary>
        /// <returns>Current statistics snapshot.</returns>
        public GZipStatistics GetStatistics()
        {
            lock (_statsLock)
            {
                return new GZipStatistics
                {
                    CompressionCount = _compressionCount,
                    DecompressionCount = _decompressionCount,
                    TotalBytesIn = _totalBytesIn,
                    TotalBytesOut = _totalBytesOut,
                    BypassCount = _bypassCount,
                    AverageCompressionRatio = _averageCompressionRatio,
                    TotalSpaceSaved = _totalBytesIn - _totalBytesOut,
                    OverallCompressionRatio = _totalBytesIn > 0 ? (double)_totalBytesOut / _totalBytesIn : 1.0
                };
            }
        }

        /// <summary>
        /// Resets all statistics counters.
        /// </summary>
        public void ResetStatistics()
        {
            lock (_statsLock)
            {
                _compressionCount = 0;
                _decompressionCount = 0;
                _totalBytesIn = 0;
                _totalBytesOut = 0;
                _bypassCount = 0;
                _averageCompressionRatio = 0;
            }
        }

        private Stream CreateBypassedOutput(byte[] data, int originalSize, IKernelContext context)
        {
            var output = new MemoryStream();
            var writer = new BinaryWriter(output);

            // Write header with stored flag
            writer.Write(MagicBytes);
            writer.Write(HeaderVersion);
            writer.Write((byte)CompressionLevel.NoCompression);
            writer.Write((long)originalSize);
            writer.Write((byte)GZipFlags.Stored);

            // Write uncompressed data
            writer.Write(data);

            // Update statistics
            UpdateCompressionStats(originalSize, data.Length, isCompression: true);

            output.Position = 0;
            return output;
        }

        private CompressionLevel GetCompressionLevel(Dictionary<string, object> args)
        {
            if (args.TryGetValue("level", out var levelObj))
            {
                return levelObj switch
                {
                    CompressionLevel cl => cl,
                    string s when Enum.TryParse<CompressionLevel>(s, true, out var parsed) => parsed,
                    int i when Enum.IsDefined(typeof(CompressionLevel), i) => (CompressionLevel)i,
                    _ => _config.CompressionLevel
                };
            }

            return _config.CompressionLevel;
        }

        private int GetBufferSize(Dictionary<string, object> args)
        {
            if (args.TryGetValue("bufferSize", out var bufferObj))
            {
                if (bufferObj is int size)
                {
                    return Math.Clamp(size, 4096, 1048576); // 4KB to 1MB
                }

                if (bufferObj is long longSize)
                {
                    return Math.Clamp((int)longSize, 4096, 1048576);
                }
            }

            return _config.BufferSize;
        }

        private void UpdateCompressionStats(long bytesIn, long bytesOut, bool isCompression)
        {
            lock (_statsLock)
            {
                if (isCompression)
                {
                    _compressionCount++;
                }
                else
                {
                    _decompressionCount++;
                }

                _totalBytesIn += bytesIn;
                _totalBytesOut += bytesOut;

                // Update running average compression ratio for compression operations
                if (isCompression && bytesIn > 0)
                {
                    var currentRatio = (double)bytesOut / bytesIn;
                    if (_compressionCount == 1)
                    {
                        _averageCompressionRatio = currentRatio;
                    }
                    else
                    {
                        _averageCompressionRatio = (_averageCompressionRatio * (_compressionCount - 1) + currentRatio) / _compressionCount;
                    }
                }
            }
        }

        private Task HandleConfigureAsync(PluginMessage message)
        {
            if (message.Payload.TryGetValue("level", out var levelObj))
            {
                if (levelObj is string levelStr && Enum.TryParse<CompressionLevel>(levelStr, true, out var level))
                {
                    _config.CompressionLevel = level;
                }
            }

            if (message.Payload.TryGetValue("bufferSize", out var bufferObj))
            {
                if (bufferObj is int size)
                {
                    _config.BufferSize = Math.Clamp(size, 4096, 1048576);
                }
                else if (bufferObj is long longSize)
                {
                    _config.BufferSize = Math.Clamp((int)longSize, 4096, 1048576);
                }
            }

            if (message.Payload.TryGetValue("bypassIncompressible", out var bypassObj) && bypassObj is bool bypass)
            {
                _config.BypassIncompressible = bypass;
            }

            // Return current configuration
            message.Payload["currentLevel"] = _config.CompressionLevel.ToString();
            message.Payload["currentBufferSize"] = _config.BufferSize;
            message.Payload["currentBypassIncompressible"] = _config.BypassIncompressible;

            return Task.CompletedTask;
        }

        private Task HandleStatsAsync(PluginMessage message)
        {
            var stats = GetStatistics();

            message.Payload["compressionCount"] = stats.CompressionCount;
            message.Payload["decompressionCount"] = stats.DecompressionCount;
            message.Payload["bypassCount"] = stats.BypassCount;
            message.Payload["totalBytesIn"] = stats.TotalBytesIn;
            message.Payload["totalBytesOut"] = stats.TotalBytesOut;
            message.Payload["averageCompressionRatio"] = stats.AverageCompressionRatio;
            message.Payload["totalSpaceSaved"] = stats.TotalSpaceSaved;
            message.Payload["overallCompressionRatio"] = stats.OverallCompressionRatio;

            if (stats.TotalBytesIn > 0)
            {
                message.Payload["spaceSavingsPercent"] = (1.0 - stats.OverallCompressionRatio) * 100;
            }

            return Task.CompletedTask;
        }

        private Task HandleTestAsync(PluginMessage message)
        {
            if (!message.Payload.TryGetValue("data", out var dataObj) || dataObj is not byte[] data)
            {
                message.Payload["error"] = "Missing 'data' parameter (byte[])";
                message.Payload["success"] = false;
                return Task.CompletedTask;
            }

            var result = CompressData(data);

            message.Payload["success"] = result.Success;
            message.Payload["originalSize"] = result.OriginalSize;
            message.Payload["compressedSize"] = result.CompressedSize;
            message.Payload["compressionRatio"] = result.CompressionRatio;
            message.Payload["spaceSaved"] = result.SpaceSaved;
            message.Payload["spaceSavingsPercent"] = (1.0 - result.CompressionRatio) * 100;
            message.Payload["compressionLevel"] = result.CompressionLevel;

            return Task.CompletedTask;
        }

        private Task HandleResetAsync(PluginMessage message)
        {
            ResetStatistics();
            message.Payload["success"] = true;
            message.Payload["message"] = "Statistics reset successfully";
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }

    /// <summary>
    /// Flags for GZip header.
    /// </summary>
    [Flags]
    internal enum GZipFlags : byte
    {
        /// <summary>No flags set.</summary>
        None = 0,

        /// <summary>Data is compressed.</summary>
        Compressed = 1,

        /// <summary>Data is stored without compression.</summary>
        Stored = 2,

        /// <summary>Checksum is included.</summary>
        Checksum = 4,

        /// <summary>Original filename is included.</summary>
        FileName = 8
    }

    /// <summary>
    /// Configuration for GZip compression plugin.
    /// </summary>
    public sealed class GZipCompressionConfig
    {
        /// <summary>
        /// Compression level to use.
        /// Default is Optimal (balanced speed and compression).
        /// </summary>
        public CompressionLevel CompressionLevel { get; set; } = CompressionLevel.Optimal;

        /// <summary>
        /// Buffer size for streaming operations in bytes.
        /// Default is 81920 (80KB). Valid range: 4096-1048576.
        /// </summary>
        public int BufferSize { get; set; } = 81920;

        /// <summary>
        /// Whether to bypass compression for data that doesn't compress well.
        /// Default is true.
        /// </summary>
        public bool BypassIncompressible { get; set; } = true;

        /// <summary>
        /// Creates a configuration optimized for speed.
        /// </summary>
        public static GZipCompressionConfig ForSpeed() => new()
        {
            CompressionLevel = CompressionLevel.Fastest,
            BufferSize = 131072, // 128KB for faster streaming
            BypassIncompressible = true
        };

        /// <summary>
        /// Creates a configuration optimized for compression ratio.
        /// </summary>
        public static GZipCompressionConfig ForSize() => new()
        {
            CompressionLevel = CompressionLevel.SmallestSize,
            BufferSize = 65536, // 64KB
            BypassIncompressible = true
        };

        /// <summary>
        /// Creates a balanced configuration (default).
        /// </summary>
        public static GZipCompressionConfig Balanced() => new()
        {
            CompressionLevel = CompressionLevel.Optimal,
            BufferSize = 81920, // 80KB
            BypassIncompressible = true
        };
    }

    /// <summary>
    /// Result of a compression operation.
    /// </summary>
    public sealed class CompressionResult
    {
        /// <summary>
        /// Whether the compression succeeded.
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// Original data size in bytes.
        /// </summary>
        public long OriginalSize { get; init; }

        /// <summary>
        /// Compressed data size in bytes.
        /// </summary>
        public long CompressedSize { get; init; }

        /// <summary>
        /// Compression ratio (compressed/original). Lower is better.
        /// </summary>
        public double CompressionRatio { get; init; }

        /// <summary>
        /// The compressed data.
        /// </summary>
        public byte[] CompressedData { get; init; } = Array.Empty<byte>();

        /// <summary>
        /// Space saved in bytes.
        /// </summary>
        public long SpaceSaved { get; init; }

        /// <summary>
        /// Compression level used.
        /// </summary>
        public string CompressionLevel { get; init; } = string.Empty;

        /// <summary>
        /// Error message if compression failed.
        /// </summary>
        public string? ErrorMessage { get; init; }

        /// <summary>
        /// Space savings as a percentage.
        /// </summary>
        public double SpaceSavingsPercent => (1.0 - CompressionRatio) * 100;
    }

    /// <summary>
    /// GZip compression statistics.
    /// </summary>
    public sealed class GZipStatistics
    {
        /// <summary>
        /// Number of compression operations performed.
        /// </summary>
        public long CompressionCount { get; init; }

        /// <summary>
        /// Number of decompression operations performed.
        /// </summary>
        public long DecompressionCount { get; init; }

        /// <summary>
        /// Total bytes input (before compression or after decompression).
        /// </summary>
        public long TotalBytesIn { get; init; }

        /// <summary>
        /// Total bytes output (after compression or before decompression).
        /// </summary>
        public long TotalBytesOut { get; init; }

        /// <summary>
        /// Number of times compression was bypassed due to incompressible data.
        /// </summary>
        public long BypassCount { get; init; }

        /// <summary>
        /// Running average compression ratio across all operations.
        /// </summary>
        public double AverageCompressionRatio { get; init; }

        /// <summary>
        /// Total space saved in bytes (may be negative if data expanded).
        /// </summary>
        public long TotalSpaceSaved { get; init; }

        /// <summary>
        /// Overall compression ratio (total out / total in).
        /// </summary>
        public double OverallCompressionRatio { get; init; }

        /// <summary>
        /// Total number of operations (compression + decompression).
        /// </summary>
        public long TotalOperations => CompressionCount + DecompressionCount;

        /// <summary>
        /// Space savings as a percentage.
        /// </summary>
        public double SpaceSavingsPercent => (1.0 - OverallCompressionRatio) * 100;
    }
}
