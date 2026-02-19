using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Utilities;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Compression
{
    /// <summary>
    /// Defines the compression level for compression operations.
    /// Higher levels provide better compression ratios but slower performance.
    /// </summary>
    public enum CompressionLevel
    {
        /// <summary>
        /// Fastest compression with minimal CPU usage.
        /// Optimized for speed over compression ratio.
        /// </summary>
        Fastest = 0,

        /// <summary>
        /// Fast compression with good performance.
        /// Balanced for quick operations with acceptable compression.
        /// </summary>
        Fast = 1,

        /// <summary>
        /// Default compression level providing balanced performance.
        /// Suitable for most use cases with good compression ratio and reasonable speed.
        /// </summary>
        Default = 2,

        /// <summary>
        /// Better compression with moderate CPU usage.
        /// Optimized for better compression ratio with acceptable performance impact.
        /// </summary>
        Better = 3,

        /// <summary>
        /// Best compression ratio with highest CPU usage.
        /// Optimized for maximum compression, suitable for archival or storage-constrained scenarios.
        /// </summary>
        Best = 4
    }

    /// <summary>
    /// Defines when compression should be applied.
    /// </summary>
    [Flags]
    public enum CompressionMode
    {
        /// <summary>
        /// No compression applied.
        /// </summary>
        None = 0,

        /// <summary>
        /// Compress data at rest (storage).
        /// Applied when data is written to disk or persistent storage.
        /// </summary>
        AtRest = 1,

        /// <summary>
        /// Compress data in transit (network).
        /// Applied when data is transmitted over network connections.
        /// </summary>
        InTransit = 2,

        /// <summary>
        /// Compress data both at rest and in transit.
        /// Maximum compression for storage and bandwidth efficiency.
        /// </summary>
        Both = AtRest | InTransit
    }

    /// <summary>
    /// Describes the characteristics and capabilities of a compression strategy.
    /// This record provides metadata about compression algorithm performance and features.
    /// </summary>
    public sealed record CompressionCharacteristics
    {
        /// <summary>
        /// Gets the name of the compression algorithm.
        /// Example: "LZ4", "Zstd", "Brotli", "GZip"
        /// </summary>
        public required string AlgorithmName { get; init; }

        /// <summary>
        /// Gets the typical compression ratio (compressed size / original size).
        /// Lower values indicate better compression.
        /// Range: 0.0 (perfect compression) to 1.0 (no compression).
        /// Example: 0.3 means compressed size is 30% of original.
        /// </summary>
        public required double TypicalCompressionRatio { get; init; }

        /// <summary>
        /// Gets the relative compression speed (1-10 scale).
        /// Higher values indicate faster compression.
        /// 10 = Extremely fast (LZ4), 1 = Very slow (Brotli level 11).
        /// </summary>
        public required int CompressionSpeed { get; init; }

        /// <summary>
        /// Gets the relative decompression speed (1-10 scale).
        /// Higher values indicate faster decompression.
        /// 10 = Extremely fast, 1 = Very slow.
        /// </summary>
        public required int DecompressionSpeed { get; init; }

        /// <summary>
        /// Gets the memory usage in bytes for compression operations.
        /// Approximate memory footprint during compression.
        /// </summary>
        public required long CompressionMemoryUsage { get; init; }

        /// <summary>
        /// Gets the memory usage in bytes for decompression operations.
        /// Approximate memory footprint during decompression.
        /// </summary>
        public required long DecompressionMemoryUsage { get; init; }

        /// <summary>
        /// Gets a value indicating whether the algorithm supports streaming compression.
        /// True if data can be compressed in chunks without loading entire dataset into memory.
        /// </summary>
        public required bool SupportsStreaming { get; init; }

        /// <summary>
        /// Gets a value indicating whether the algorithm supports parallel compression.
        /// True if compression can be parallelized across multiple threads/cores.
        /// </summary>
        public required bool SupportsParallelCompression { get; init; }

        /// <summary>
        /// Gets a value indicating whether the algorithm supports parallel decompression.
        /// True if decompression can be parallelized across multiple threads/cores.
        /// </summary>
        public required bool SupportsParallelDecompression { get; init; }

        /// <summary>
        /// Gets a value indicating whether the compressed format supports seeking.
        /// True if random access to compressed data is possible without full decompression.
        /// </summary>
        public required bool SupportsRandomAccess { get; init; }

        /// <summary>
        /// Gets the recommended minimum data size in bytes for effective compression.
        /// Data smaller than this may not compress well or may expand.
        /// </summary>
        public long MinimumRecommendedSize { get; init; } = 1024; // 1 KB default

        /// <summary>
        /// Gets the optimal block size in bytes for streaming operations.
        /// Used for chunked compression/decompression operations.
        /// </summary>
        public int OptimalBlockSize { get; init; } = 65536; // 64 KB default
    }

    /// <summary>
    /// Records benchmark metrics for compression operations.
    /// Used for performance profiling and algorithm selection.
    /// </summary>
    public sealed record CompressionBenchmark
    {
        /// <summary>
        /// Gets the name of the compression algorithm benchmarked.
        /// </summary>
        public required string AlgorithmName { get; init; }

        /// <summary>
        /// Gets the compression level used in the benchmark.
        /// </summary>
        public required CompressionLevel Level { get; init; }

        /// <summary>
        /// Gets the original (uncompressed) data size in bytes.
        /// </summary>
        public required long OriginalSize { get; init; }

        /// <summary>
        /// Gets the compressed data size in bytes.
        /// </summary>
        public required long CompressedSize { get; init; }

        /// <summary>
        /// Gets the actual compression ratio achieved (compressed / original).
        /// </summary>
        public double CompressionRatio => OriginalSize > 0 ? (double)CompressedSize / OriginalSize : 1.0;

        /// <summary>
        /// Gets the compression throughput in bytes per second.
        /// </summary>
        public required long CompressionThroughput { get; init; }

        /// <summary>
        /// Gets the decompression throughput in bytes per second.
        /// </summary>
        public required long DecompressionThroughput { get; init; }

        /// <summary>
        /// Gets the time taken to compress in milliseconds.
        /// </summary>
        public required double CompressionTimeMs { get; init; }

        /// <summary>
        /// Gets the time taken to decompress in milliseconds.
        /// </summary>
        public required double DecompressionTimeMs { get; init; }

        /// <summary>
        /// Gets the peak memory used during compression in bytes.
        /// </summary>
        public required long PeakCompressionMemory { get; init; }

        /// <summary>
        /// Gets the peak memory used during decompression in bytes.
        /// </summary>
        public required long PeakDecompressionMemory { get; init; }

        /// <summary>
        /// Gets the timestamp when the benchmark was performed.
        /// </summary>
        public DateTime Timestamp { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Gets additional metadata about the benchmark environment.
        /// Can include CPU model, OS version, .NET version, etc.
        /// </summary>
        public string? Metadata { get; init; }
    }

    /// <summary>
    /// Detected content type for adaptive compression selection.
    /// Different content types benefit from different compression strategies.
    /// </summary>
    public enum ContentType
    {
        /// <summary>
        /// Unknown or mixed content.
        /// </summary>
        Unknown = 0,

        /// <summary>
        /// Plain text (highly compressible).
        /// Examples: .txt, .csv, .log files.
        /// </summary>
        Text = 1,

        /// <summary>
        /// Structured data formats (highly compressible).
        /// Examples: JSON, XML, YAML.
        /// </summary>
        Structured = 2,

        /// <summary>
        /// Binary data (moderate compressibility).
        /// Examples: executables, databases.
        /// </summary>
        Binary = 3,

        /// <summary>
        /// Already compressed data (not compressible).
        /// Examples: .zip, .gz, .zst files.
        /// </summary>
        Compressed = 4,

        /// <summary>
        /// Media files (typically not compressible).
        /// Examples: .jpg, .mp3, .mp4, .png.
        /// </summary>
        Media = 5,

        /// <summary>
        /// Encrypted data (not compressible).
        /// High entropy makes compression ineffective.
        /// </summary>
        Encrypted = 6
    }

    /// <summary>
    /// Common interface for compression strategy implementations.
    /// Provides methods for compressing and decompressing data with various options.
    /// </summary>
    public interface ICompressionStrategy
    {
        /// <summary>
        /// Gets the characteristics of this compression strategy.
        /// </summary>
        CompressionCharacteristics Characteristics { get; }

        /// <summary>
        /// Gets the compression level for this strategy instance.
        /// </summary>
        CompressionLevel Level { get; }

        /// <summary>
        /// Compresses data synchronously.
        /// </summary>
        /// <param name="input">The uncompressed input data.</param>
        /// <returns>The compressed output data.</returns>
        /// <exception cref="ArgumentNullException">Thrown when input is null.</exception>
        /// <exception cref="CompressionException">Thrown when compression fails.</exception>
        byte[] Compress(byte[] input);

        /// <summary>
        /// Decompresses data synchronously.
        /// </summary>
        /// <param name="input">The compressed input data.</param>
        /// <returns>The decompressed output data.</returns>
        /// <exception cref="ArgumentNullException">Thrown when input is null.</exception>
        /// <exception cref="CompressionException">Thrown when decompression fails.</exception>
        byte[] Decompress(byte[] input);

        /// <summary>
        /// Compresses data asynchronously.
        /// </summary>
        /// <param name="input">The uncompressed input data.</param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        /// <returns>The compressed output data.</returns>
        /// <exception cref="ArgumentNullException">Thrown when input is null.</exception>
        /// <exception cref="OperationCanceledException">Thrown when operation is cancelled.</exception>
        /// <exception cref="CompressionException">Thrown when compression fails.</exception>
        Task<byte[]> CompressAsync(byte[] input, CancellationToken cancellationToken = default);

        /// <summary>
        /// Decompresses data asynchronously.
        /// </summary>
        /// <param name="input">The compressed input data.</param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        /// <returns>The decompressed output data.</returns>
        /// <exception cref="ArgumentNullException">Thrown when input is null.</exception>
        /// <exception cref="OperationCanceledException">Thrown when operation is cancelled.</exception>
        /// <exception cref="CompressionException">Thrown when decompression fails.</exception>
        Task<byte[]> DecompressAsync(byte[] input, CancellationToken cancellationToken = default);

        /// <summary>
        /// Creates a compression stream that wraps an output stream.
        /// Data written to the returned stream will be compressed and written to the output stream.
        /// </summary>
        /// <param name="output">The stream to write compressed data to.</param>
        /// <param name="leaveOpen">True to leave the output stream open after disposal.</param>
        /// <returns>A stream that compresses data written to it.</returns>
        /// <exception cref="ArgumentNullException">Thrown when output is null.</exception>
        /// <exception cref="ArgumentException">Thrown when output stream is not writable.</exception>
        Stream CreateCompressionStream(Stream output, bool leaveOpen = false);

        /// <summary>
        /// Creates a decompression stream that wraps an input stream.
        /// Data read from the returned stream will be decompressed from the input stream.
        /// </summary>
        /// <param name="input">The stream to read compressed data from.</param>
        /// <param name="leaveOpen">True to leave the input stream open after disposal.</param>
        /// <returns>A stream that decompresses data read from it.</returns>
        /// <exception cref="ArgumentNullException">Thrown when input is null.</exception>
        /// <exception cref="ArgumentException">Thrown when input stream is not readable.</exception>
        Stream CreateDecompressionStream(Stream input, bool leaveOpen = false);

        /// <summary>
        /// Estimates the maximum compressed size for the given input size.
        /// Used for buffer allocation.
        /// </summary>
        /// <param name="inputSize">The size of uncompressed data in bytes.</param>
        /// <returns>The estimated maximum compressed size in bytes.</returns>
        long EstimateCompressedSize(long inputSize);

        /// <summary>
        /// Detects if the input data is already compressed or unlikely to compress well.
        /// </summary>
        /// <param name="input">The data to analyze (typically first 512-4096 bytes).</param>
        /// <returns>True if compression is recommended, false otherwise.</returns>
        bool ShouldCompress(ReadOnlySpan<byte> input);

        /// <summary>
        /// Gets statistics about compression operations performed by this instance.
        /// </summary>
        /// <returns>Compression statistics including bytes processed, ratio, throughput.</returns>
        CompressionStatistics GetStatistics();

        /// <summary>
        /// Resets the statistics counters.
        /// </summary>
        void ResetStatistics();
    }

    /// <summary>
    /// Statistics tracking for compression operations.
    /// </summary>
    public sealed class CompressionStatistics
    {
        /// <summary>
        /// Gets or sets the total number of compression operations.
        /// </summary>
        public long CompressionOperations { get; set; }

        /// <summary>
        /// Gets or sets the total number of decompression operations.
        /// </summary>
        public long DecompressionOperations { get; set; }

        /// <summary>
        /// Gets or sets the total bytes processed (uncompressed) during compression.
        /// </summary>
        public long TotalBytesIn { get; set; }

        /// <summary>
        /// Gets or sets the total bytes produced (compressed) during compression.
        /// </summary>
        public long TotalBytesOut { get; set; }

        /// <summary>
        /// Gets the average compression ratio (compressed / original).
        /// </summary>
        public double AverageCompressionRatio => TotalBytesIn > 0 ? (double)TotalBytesOut / TotalBytesIn : 1.0;

        /// <summary>
        /// Gets or sets the total time spent compressing in milliseconds.
        /// </summary>
        public double TotalCompressionTimeMs { get; set; }

        /// <summary>
        /// Gets or sets the total time spent decompressing in milliseconds.
        /// </summary>
        public double TotalDecompressionTimeMs { get; set; }

        /// <summary>
        /// Gets the average compression throughput in bytes per second.
        /// </summary>
        public double AverageCompressionThroughput =>
            TotalCompressionTimeMs > 0 ? (TotalBytesIn / TotalCompressionTimeMs) * 1000.0 : 0;

        /// <summary>
        /// Gets the average decompression throughput in bytes per second.
        /// </summary>
        public double AverageDecompressionThroughput =>
            TotalDecompressionTimeMs > 0 ? (TotalBytesOut / TotalDecompressionTimeMs) * 1000.0 : 0;

        /// <summary>
        /// Gets or sets the number of compression failures.
        /// </summary>
        public long CompressionFailures { get; set; }

        /// <summary>
        /// Gets or sets the number of decompression failures.
        /// </summary>
        public long DecompressionFailures { get; set; }
    }

    /// <summary>
    /// Exception thrown when compression or decompression operations fail.
    /// </summary>
    public sealed class CompressionException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="CompressionException"/> class.
        /// </summary>
        /// <param name="message">The error message.</param>
        public CompressionException(string message) : base(message) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="CompressionException"/> class.
        /// </summary>
        /// <param name="message">The error message.</param>
        /// <param name="innerException">The inner exception.</param>
        public CompressionException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// Abstract base class for compression strategy implementations.
    /// Provides common functionality including validation, statistics tracking, and content detection.
    /// Thread-safe for concurrent operations.
    /// </summary>
    public abstract class CompressionStrategyBase : StrategyBase, ICompressionStrategy
    {
        // ISO-03 (CVSS 5.9): Shared mutable state -- thread-safety analysis:
        // - _statistics: Instance-level, guarded by _statsLock for all reads/writes
        // - _contentTypeCache: Static ConcurrentDictionary -- inherently thread-safe for concurrent access
        // Both fields are safe for concurrent multi-plugin access.
        private readonly CompressionStatistics _statistics = new();
        private readonly object _statsLock = new();
        private static readonly ConcurrentDictionary<string, ContentType> _contentTypeCache = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="CompressionStrategyBase"/> class.
        /// </summary>
        /// <param name="level">The compression level to use.</param>
        protected CompressionStrategyBase(CompressionLevel level)
        {
            Level = level;
        }

        /// <summary>
        /// Gets the unique identifier for this compression strategy.
        /// Default implementation derives from the algorithm name.
        /// Override in concrete strategies to provide a custom identifier.
        /// </summary>
        public override string StrategyId => Characteristics.AlgorithmName.ToLowerInvariant().Replace(" ", "-");

        /// <summary>
        /// Gets the display name for this compression strategy.
        /// Default implementation uses the algorithm name from Characteristics.
        /// Override in concrete strategies to provide a custom name.
        /// </summary>
        public virtual string StrategyName => Characteristics.AlgorithmName;

        /// <summary>
        /// Bridges StrategyName to the StrategyBase.Name contract.
        /// </summary>
        public override string Name => StrategyName;

        /// <inheritdoc/>
        public new abstract CompressionCharacteristics Characteristics { get; }

        /// <inheritdoc/>
        public CompressionLevel Level { get; }

        /// <inheritdoc/>
        public byte[] Compress(byte[] input)
        {
            ValidateInput(input, nameof(input));

            var sw = Stopwatch.StartNew();
            try
            {
                var result = CompressCore(input);
                sw.Stop();

                UpdateCompressionStats(input.Length, result.Length, sw.Elapsed.TotalMilliseconds);
                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                IncrementCompressionFailures();
                throw new CompressionException($"Compression failed using {Characteristics.AlgorithmName}", ex);
            }
        }

        /// <inheritdoc/>
        public byte[] Decompress(byte[] input)
        {
            ValidateInput(input, nameof(input));

            var sw = Stopwatch.StartNew();
            try
            {
                var result = DecompressCore(input);
                sw.Stop();

                UpdateDecompressionStats(sw.Elapsed.TotalMilliseconds);
                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                IncrementDecompressionFailures();
                throw new CompressionException($"Decompression failed using {Characteristics.AlgorithmName}", ex);
            }
        }

        /// <inheritdoc/>
        public async Task<byte[]> CompressAsync(byte[] input, CancellationToken cancellationToken = default)
        {
            ValidateInput(input, nameof(input));
            cancellationToken.ThrowIfCancellationRequested();

            var sw = Stopwatch.StartNew();
            try
            {
                var result = await CompressAsyncCore(input, cancellationToken).ConfigureAwait(false);
                sw.Stop();

                UpdateCompressionStats(input.Length, result.Length, sw.Elapsed.TotalMilliseconds);
                return result;
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                IncrementCompressionFailures();
                throw new CompressionException($"Async compression failed using {Characteristics.AlgorithmName}", ex);
            }
        }

        /// <inheritdoc/>
        public async Task<byte[]> DecompressAsync(byte[] input, CancellationToken cancellationToken = default)
        {
            ValidateInput(input, nameof(input));
            cancellationToken.ThrowIfCancellationRequested();

            var sw = Stopwatch.StartNew();
            try
            {
                var result = await DecompressAsyncCore(input, cancellationToken).ConfigureAwait(false);
                sw.Stop();

                UpdateDecompressionStats(sw.Elapsed.TotalMilliseconds);
                return result;
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                IncrementDecompressionFailures();
                throw new CompressionException($"Async decompression failed using {Characteristics.AlgorithmName}", ex);
            }
        }

        /// <inheritdoc/>
        public Stream CreateCompressionStream(Stream output, bool leaveOpen = false)
        {
            if (output == null)
                throw new ArgumentNullException(nameof(output));
            if (!output.CanWrite)
                throw new ArgumentException("Output stream must be writable", nameof(output));

            return CreateCompressionStreamCore(output, leaveOpen);
        }

        /// <inheritdoc/>
        public Stream CreateDecompressionStream(Stream input, bool leaveOpen = false)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));
            if (!input.CanRead)
                throw new ArgumentException("Input stream must be readable", nameof(input));

            return CreateDecompressionStreamCore(input, leaveOpen);
        }

        /// <inheritdoc/>
        public virtual long EstimateCompressedSize(long inputSize)
        {
            // Conservative estimate: original size + 5% overhead + 1KB header
            // Derived classes can override with algorithm-specific estimates
            return (long)(inputSize * 1.05) + 1024;
        }

        /// <inheritdoc/>
        public virtual bool ShouldCompress(ReadOnlySpan<byte> input)
        {
            // Don't compress very small data (overhead not worth it)
            if (input.Length < Characteristics.MinimumRecommendedSize)
                return false;

            // Detect content type and decide
            var contentType = DetectContentType(input);

            return contentType switch
            {
                ContentType.Text => true,
                ContentType.Structured => true,
                ContentType.Binary => true,
                ContentType.Compressed => false,
                ContentType.Media => false,
                ContentType.Encrypted => false,
                _ => CalculateEntropy(input) < 7.5 // High entropy = already compressed/encrypted
            };
        }

        /// <inheritdoc/>
        public CompressionStatistics GetStatistics()
        {
            lock (_statsLock)
            {
                return new CompressionStatistics
                {
                    CompressionOperations = _statistics.CompressionOperations,
                    DecompressionOperations = _statistics.DecompressionOperations,
                    TotalBytesIn = _statistics.TotalBytesIn,
                    TotalBytesOut = _statistics.TotalBytesOut,
                    TotalCompressionTimeMs = _statistics.TotalCompressionTimeMs,
                    TotalDecompressionTimeMs = _statistics.TotalDecompressionTimeMs,
                    CompressionFailures = _statistics.CompressionFailures,
                    DecompressionFailures = _statistics.DecompressionFailures
                };
            }
        }

        /// <inheritdoc/>
        public void ResetStatistics()
        {
            lock (_statsLock)
            {
                _statistics.CompressionOperations = 0;
                _statistics.DecompressionOperations = 0;
                _statistics.TotalBytesIn = 0;
                _statistics.TotalBytesOut = 0;
                _statistics.TotalCompressionTimeMs = 0;
                _statistics.TotalDecompressionTimeMs = 0;
                _statistics.CompressionFailures = 0;
                _statistics.DecompressionFailures = 0;
            }
        }

        /// <summary>
        /// Core compression implementation to be provided by derived classes.
        /// </summary>
        /// <param name="input">The uncompressed input data.</param>
        /// <returns>The compressed output data.</returns>
        protected abstract byte[] CompressCore(byte[] input);

        /// <summary>
        /// Core decompression implementation to be provided by derived classes.
        /// </summary>
        /// <param name="input">The compressed input data.</param>
        /// <returns>The decompressed output data.</returns>
        protected abstract byte[] DecompressCore(byte[] input);

        /// <summary>
        /// Core async compression implementation to be provided by derived classes.
        /// Default implementation calls synchronous version on thread pool.
        /// </summary>
        /// <param name="input">The uncompressed input data.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The compressed output data.</returns>
        protected virtual Task<byte[]> CompressAsyncCore(byte[] input, CancellationToken cancellationToken)
        {
            return Task.Run(() => CompressCore(input), cancellationToken);
        }

        /// <summary>
        /// Core async decompression implementation to be provided by derived classes.
        /// Default implementation calls synchronous version on thread pool.
        /// </summary>
        /// <param name="input">The compressed input data.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The decompressed output data.</returns>
        protected virtual Task<byte[]> DecompressAsyncCore(byte[] input, CancellationToken cancellationToken)
        {
            return Task.Run(() => DecompressCore(input), cancellationToken);
        }

        /// <summary>
        /// Creates a compression stream. Must be implemented by derived classes.
        /// </summary>
        /// <param name="output">The output stream.</param>
        /// <param name="leaveOpen">Whether to leave the stream open.</param>
        /// <returns>A compression stream.</returns>
        protected abstract Stream CreateCompressionStreamCore(Stream output, bool leaveOpen);

        /// <summary>
        /// Creates a decompression stream. Must be implemented by derived classes.
        /// </summary>
        /// <param name="input">The input stream.</param>
        /// <param name="leaveOpen">Whether to leave the stream open.</param>
        /// <returns>A decompression stream.</returns>
        protected abstract Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen);

        /// <summary>
        /// Validates input data is not null or empty.
        /// </summary>
        /// <param name="input">The input data to validate.</param>
        /// <param name="paramName">The parameter name for exception messages.</param>
        /// <exception cref="ArgumentNullException">Thrown when input is null.</exception>
        /// <exception cref="ArgumentException">Thrown when input is empty.</exception>
        protected static void ValidateInput(byte[] input, string paramName)
        {
            if (input == null)
                throw new ArgumentNullException(paramName);
            if (input.Length == 0)
                throw new ArgumentException("Input cannot be empty", paramName);
        }

        /// <summary>
        /// Detects the content type of data for adaptive compression decisions.
        /// </summary>
        /// <param name="data">Sample data to analyze (typically first 512-4096 bytes).</param>
        /// <returns>The detected content type.</returns>
        protected virtual ContentType DetectContentType(ReadOnlySpan<byte> data)
        {
            // Check for common compressed file signatures
            if (IsCompressedFormat(data))
                return ContentType.Compressed;

            // Check for common media file signatures
            if (IsMediaFormat(data))
                return ContentType.Media;

            // Check for encrypted data (high entropy, no patterns)
            var entropy = CalculateEntropy(data);
            if (entropy > 7.8)
                return ContentType.Encrypted;

            // Check for text (printable ASCII/UTF-8 characters)
            if (IsTextFormat(data))
                return ContentType.Text;

            // Check for structured formats (JSON, XML, YAML)
            if (IsStructuredFormat(data))
                return ContentType.Structured;

            // Default to binary
            return ContentType.Binary;
        }

        /// <summary>
        /// Calculates the Shannon entropy of data (0-8 bits per byte).
        /// Higher entropy indicates more randomness (compressed/encrypted data).
        /// </summary>
        /// <param name="data">The data to analyze.</param>
        /// <returns>Entropy value (0-8).</returns>
        protected static double CalculateEntropy(ReadOnlySpan<byte> data)
        {
            if (data.Length == 0)
                return 0;

            Span<int> frequency = stackalloc int[256];
            frequency.Clear();

            foreach (var b in data)
                frequency[b]++;

            double entropy = 0.0;
            double length = data.Length;

            for (int i = 0; i < 256; i++)
            {
                if (frequency[i] == 0)
                    continue;

                double probability = frequency[i] / length;
                entropy -= probability * Math.Log2(probability);
            }

            return entropy;
        }

        /// <summary>
        /// Checks if data starts with known compressed file signatures.
        /// </summary>
        private static bool IsCompressedFormat(ReadOnlySpan<byte> data)
        {
            if (data.Length < 4)
                return false;

            // GZip: 1F 8B
            if (data[0] == 0x1F && data[1] == 0x8B)
                return true;

            // ZIP: 50 4B 03 04 or 50 4B 05 06
            if (data[0] == 0x50 && data[1] == 0x4B && (data[2] == 0x03 || data[2] == 0x05))
                return true;

            // Zstd: 28 B5 2F FD
            if (data[0] == 0x28 && data[1] == 0xB5 && data[2] == 0x2F && data[3] == 0xFD)
                return true;

            // Brotli has no magic number, check via other means
            return false;
        }

        /// <summary>
        /// Checks if data starts with known media file signatures.
        /// </summary>
        private static bool IsMediaFormat(ReadOnlySpan<byte> data)
        {
            if (data.Length < 4)
                return false;

            // JPEG: FF D8 FF
            if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
                return true;

            // PNG: 89 50 4E 47
            if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
                return true;

            // MP3: ID3 or FF FB/FF F3
            if ((data[0] == 0x49 && data[1] == 0x44 && data[2] == 0x33) ||
                (data[0] == 0xFF && (data[1] == 0xFB || data[1] == 0xF3)))
                return true;

            // MP4/MOV: ftyp
            if (data.Length >= 8 && data[4] == 0x66 && data[5] == 0x74 && data[6] == 0x79 && data[7] == 0x70)
                return true;

            return false;
        }

        /// <summary>
        /// Checks if data appears to be text (high proportion of printable characters).
        /// </summary>
        private static bool IsTextFormat(ReadOnlySpan<byte> data)
        {
            if (data.Length == 0)
                return false;

            int printableCount = 0;
            int sampleSize = Math.Min(data.Length, 512);

            for (int i = 0; i < sampleSize; i++)
            {
                byte b = data[i];
                // Printable ASCII + common whitespace
                if ((b >= 32 && b <= 126) || b == 9 || b == 10 || b == 13)
                    printableCount++;
            }

            // If > 90% printable, likely text
            return (double)printableCount / sampleSize > 0.90;
        }

        /// <summary>
        /// Checks if data appears to be structured format (JSON, XML, YAML).
        /// </summary>
        private static bool IsStructuredFormat(ReadOnlySpan<byte> data)
        {
            if (data.Length < 2)
                return false;

            // Skip whitespace
            int start = 0;
            while (start < data.Length && (data[start] == ' ' || data[start] == '\t' ||
                   data[start] == '\r' || data[start] == '\n'))
                start++;

            if (start >= data.Length)
                return false;

            // JSON: starts with { or [
            if (data[start] == '{' || data[start] == '[')
                return true;

            // XML: starts with <
            if (data[start] == '<')
                return true;

            // YAML: check for common patterns
            if (data.Length >= start + 3)
            {
                var slice = data.Slice(start, Math.Min(20, data.Length - start));
                var text = System.Text.Encoding.UTF8.GetString(slice);
                if (text.Contains("---") || text.Contains(": "))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Updates compression statistics atomically.
        /// </summary>
        private void UpdateCompressionStats(long bytesIn, long bytesOut, double timeMs)
        {
            lock (_statsLock)
            {
                _statistics.CompressionOperations++;
                _statistics.TotalBytesIn += bytesIn;
                _statistics.TotalBytesOut += bytesOut;
                _statistics.TotalCompressionTimeMs += timeMs;
            }
        }

        /// <summary>
        /// Updates decompression statistics atomically.
        /// </summary>
        private void UpdateDecompressionStats(double timeMs)
        {
            lock (_statsLock)
            {
                _statistics.DecompressionOperations++;
                _statistics.TotalDecompressionTimeMs += timeMs;
            }
        }

        /// <summary>
        /// Increments compression failure counter atomically.
        /// </summary>
        private void IncrementCompressionFailures()
        {
            lock (_statsLock)
            {
                _statistics.CompressionFailures++;
            }
        }

        /// <summary>
        /// Increments decompression failure counter atomically.
        /// </summary>
        private void IncrementDecompressionFailures()
        {
            lock (_statsLock)
            {
                _statistics.DecompressionFailures++;
            }
        }

        #region Intelligence Helper Methods

        /// <summary>
        /// Gets a description for this strategy. Used by plugins for intelligence registration.
        /// </summary>
        protected virtual string GetStrategyDescription() =>
            $"{StrategyName} compression strategy with {Characteristics.TypicalCompressionRatio:P0} typical ratio";

        /// <summary>
        /// Gets the knowledge payload for this strategy. Used by plugins for intelligence registration.
        /// </summary>
        protected virtual Dictionary<string, object> GetKnowledgePayload() => new()
        {
            ["algorithm"] = Characteristics.AlgorithmName,
            ["level"] = Level.ToString(),
            ["typicalRatio"] = Characteristics.TypicalCompressionRatio,
            ["compressionSpeed"] = Characteristics.CompressionSpeed,
            ["decompressionSpeed"] = Characteristics.DecompressionSpeed,
            ["supportsStreaming"] = Characteristics.SupportsStreaming,
            ["supportsParallel"] = Characteristics.SupportsParallelCompression
        };

        /// <summary>
        /// Gets tags for this strategy. Used by plugins for intelligence registration.
        /// </summary>
        protected virtual string[] GetKnowledgeTags() => new[]
        {
            "strategy",
            "compression",
            Characteristics.AlgorithmName.ToLowerInvariant(),
            Level.ToString().ToLowerInvariant()
        };

        /// <summary>
        /// Gets capability metadata for this strategy. Used by plugins for intelligence registration.
        /// </summary>
        protected virtual Dictionary<string, object> GetCapabilityMetadata() => new()
        {
            ["algorithm"] = Characteristics.AlgorithmName,
            ["level"] = Level.ToString(),
            ["typicalRatio"] = Characteristics.TypicalCompressionRatio
        };

        /// <summary>
        /// Gets the semantic description for AI-driven discovery. Used by plugins for intelligence registration.
        /// </summary>
        protected virtual string GetSemanticDescription() =>
            $"Use {StrategyName} for {Level} compression with {Characteristics.TypicalCompressionRatio:P0} typical ratio";

        #endregion
    }
}
