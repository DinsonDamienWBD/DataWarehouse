using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Security.Transit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts
{
    /// <summary>
    /// Abstract base class for transit compression plugins.
    /// Simplifies implementation of ITransitCompression by providing common infrastructure,
    /// algorithm negotiation logic, entropy calculation, and policy-based configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derived classes should:
    /// 1. Override CompressDataAsync and DecompressDataAsync for core compression operations
    /// 2. Implement GetSupportedAlgorithms() to advertise available compression algorithms
    /// 3. Optionally override CalculateEntropyAsync for custom entropy calculation
    /// 4. Optionally override EstimateCompressedSizeInternal for better size estimation
    /// </para>
    /// <para>
    /// This base class provides:
    /// - Algorithm negotiation with fallback handling
    /// - Entropy-based auto-skip of incompressible data
    /// - Stream processing wrapper around byte array operations
    /// - Pre-defined compression policies (Default, HighPerformance, MaximumCompression, etc.)
    /// - Capability advertisement
    /// - Statistics collection
    /// </para>
    /// </remarks>
    public abstract class TransitCompressionPluginBase : PluginBase, ITransitCompression
    {
        private long _totalCompressions;
        private long _totalDecompressions;
        private long _totalBytesCompressed;
        private long _totalBytesDecompressed;
        private long _skippedIncompressible;

        /// <summary>
        /// Gets the plugin category (always DataTransformationProvider).
        /// </summary>
        public override PluginCategory Category => PluginCategory.DataTransformationProvider;

        #region Pre-defined Transit Compression Policies

        /// <summary>
        /// Default policy: Zstd level 3, BeforeEncryption, Balanced priority.
        /// Good all-around performance for most scenarios.
        /// </summary>
        public static readonly TransitCompressionPolicy DefaultPolicy = new()
        {
            PolicyName = "Default",
            Description = "Balanced compression using Zstd level 3, suitable for most transit scenarios",
            Mode = TransitCompressionMode.BeforeEncryption,
            Priority = TransitCompressionPriority.Balanced,
            PreferredAlgorithm = "zstd",
            AllowedAlgorithms = new[] { "zstd", "lz4", "gzip", "brotli" },
            DefaultLevel = 3,
            MinimumSizeBytes = 256,
            AllowNegotiation = true,
            AllowFallbackToUncompressed = true
        };

        /// <summary>
        /// High performance policy: LZ4, Speed priority, minimum 1KB.
        /// Optimized for low-latency scenarios where speed is critical.
        /// </summary>
        public static readonly TransitCompressionPolicy HighPerformancePolicy = new()
        {
            PolicyName = "HighPerformance",
            Description = "Fastest compression using LZ4, optimized for low latency",
            Mode = TransitCompressionMode.BeforeEncryption,
            Priority = TransitCompressionPriority.Speed,
            PreferredAlgorithm = "lz4",
            AllowedAlgorithms = new[] { "lz4", "snappy", "zstd" },
            DefaultLevel = 1,
            MinimumSizeBytes = 1024,
            AllowNegotiation = true,
            AllowFallbackToUncompressed = true
        };

        /// <summary>
        /// Maximum compression policy: Zstd level 19 or Brotli 11, Ratio priority.
        /// Optimized for maximum space savings when compression time is not critical.
        /// </summary>
        public static readonly TransitCompressionPolicy MaximumCompressionPolicy = new()
        {
            PolicyName = "MaximumCompression",
            Description = "Maximum compression ratio using Zstd level 19 or Brotli level 11",
            Mode = TransitCompressionMode.BeforeEncryption,
            Priority = TransitCompressionPriority.Ratio,
            PreferredAlgorithm = "zstd",
            AllowedAlgorithms = new[] { "zstd", "brotli", "xz", "lzma" },
            DefaultLevel = 19,
            MinimumSizeBytes = 512,
            AllowNegotiation = true,
            AllowFallbackToUncompressed = false
        };

        /// <summary>
        /// Bandwidth saving policy: Brotli level 9, BandwidthSaving priority.
        /// Optimized for metered connections where bandwidth cost is a concern.
        /// </summary>
        public static readonly TransitCompressionPolicy BandwidthSavingPolicy = new()
        {
            PolicyName = "BandwidthSaving",
            Description = "Optimized for metered connections with Brotli level 9",
            Mode = TransitCompressionMode.BeforeEncryption,
            Priority = TransitCompressionPriority.BandwidthSaving,
            PreferredAlgorithm = "brotli",
            AllowedAlgorithms = new[] { "brotli", "zstd", "xz" },
            DefaultLevel = 9,
            MinimumSizeBytes = 128,
            AllowNegotiation = true,
            AllowFallbackToUncompressed = false
        };

        /// <summary>
        /// Mobile optimized policy: LZ4/Snappy, Speed priority, lower buffer sizes.
        /// Optimized for mobile devices with limited CPU and battery constraints.
        /// </summary>
        public static readonly TransitCompressionPolicy MobileOptimizedPolicy = new()
        {
            PolicyName = "MobileOptimized",
            Description = "Low CPU usage compression for mobile devices using LZ4/Snappy",
            Mode = TransitCompressionMode.BeforeEncryption,
            Priority = TransitCompressionPriority.Speed,
            PreferredAlgorithm = "lz4",
            AllowedAlgorithms = new[] { "lz4", "snappy" },
            DefaultLevel = 1,
            MinimumSizeBytes = 512,
            AllowNegotiation = true,
            AllowFallbackToUncompressed = true
        };

        #endregion

        /// <inheritdoc/>
        public virtual async Task<TransitCompressionResult> CompressForTransitAsync(
            byte[] data,
            TransitCompressionOptions options,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(data);
            ArgumentNullException.ThrowIfNull(options);

            var startTime = DateTime.UtcNow;

            // Check minimum size threshold
            if (data.Length < options.MinimumSizeBytes)
            {
                Interlocked.Increment(ref _skippedIncompressible);
                return CreateUncompressedResult(data, startTime);
            }

            // Calculate entropy if auto-mode or entropy check enabled
            if (options.Mode == TransitCompressionMode.Auto)
            {
                var entropy = await CalculateEntropyAsync(data, ct).ConfigureAwait(false);
                if (entropy > options.MaximumEntropy)
                {
                    // Data is too random/incompressible, skip compression
                    Interlocked.Increment(ref _skippedIncompressible);
                    return CreateUncompressedResult(data, startTime);
                }
            }

            // Select algorithm based on options and negotiation
            string algorithm;
            int level;

            if (options.RemoteCapabilities != null)
            {
                var negotiation = await NegotiateCompressionAsync(
                    GetLocalCapabilities(),
                    options.RemoteCapabilities,
                    ct).ConfigureAwait(false);

                if (!negotiation.Success)
                {
                    // Negotiation failed, return uncompressed if allowed
                    return CreateUncompressedResult(data, startTime);
                }

                algorithm = negotiation.AgreedAlgorithm!;
                level = negotiation.AgreedLevel;
            }
            else
            {
                algorithm = SelectBestAlgorithm(options);
                level = options.CompressionLevel ?? GetDefaultLevel(algorithm, options.Priority);
            }

            // Perform compression
            var compressedData = await CompressDataAsync(data, algorithm, level, options, ct).ConfigureAwait(false);

            var compressionTime = DateTime.UtcNow - startTime;
            Interlocked.Increment(ref _totalCompressions);
            Interlocked.Add(ref _totalBytesCompressed, data.Length);

            return new TransitCompressionResult
            {
                CompressedData = compressedData,
                Metadata = new TransitCompressionMetadata
                {
                    AlgorithmName = algorithm,
                    IsCompressed = true,
                    OriginalSize = data.Length,
                    DictionaryId = options.DictionaryId,
                    Parameters = new Dictionary<string, object>
                    {
                        ["CompressionLevel"] = level,
                        ["Timestamp"] = DateTime.UtcNow
                    }
                },
                OriginalSize = data.Length,
                CompressedSize = compressedData.Length,
                WasCompressed = true,
                CompressionTime = compressionTime
            };
        }

        /// <inheritdoc/>
        public virtual async Task<TransitDecompressionResult> DecompressFromTransitAsync(
            byte[] compressedData,
            TransitCompressionMetadata metadata,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(compressedData);
            ArgumentNullException.ThrowIfNull(metadata);

            var startTime = DateTime.UtcNow;

            // If data wasn't actually compressed, return as-is
            if (!metadata.IsCompressed)
            {
                return new TransitDecompressionResult
                {
                    Data = compressedData,
                    WasDecompressed = false,
                    DecompressionTime = TimeSpan.Zero
                };
            }

            // Decompress
            var decompressedData = await DecompressDataAsync(
                compressedData,
                metadata.AlgorithmName,
                metadata,
                ct).ConfigureAwait(false);

            var decompressionTime = DateTime.UtcNow - startTime;
            Interlocked.Increment(ref _totalDecompressions);
            Interlocked.Add(ref _totalBytesDecompressed, decompressedData.Length);

            return new TransitDecompressionResult
            {
                Data = decompressedData,
                WasDecompressed = true,
                DecompressionTime = decompressionTime
            };
        }

        /// <inheritdoc/>
        public virtual async Task CompressStreamForTransitAsync(
            Stream input,
            Stream output,
            TransitCompressionOptions options,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(options);

            // Default implementation: read to byte array and use byte array compression
            // Derived classes should override for true streaming compression
            using var ms = new MemoryStream();
            await input.CopyToAsync(ms, options.StreamBufferSize, ct).ConfigureAwait(false);
            var data = ms.ToArray();

            var result = await CompressForTransitAsync(data, options, ct).ConfigureAwait(false);

            await output.WriteAsync(result.CompressedData, ct).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public virtual async Task DecompressStreamFromTransitAsync(
            Stream input,
            Stream output,
            TransitCompressionMetadata metadata,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(metadata);

            // Default implementation: read to byte array and use byte array decompression
            // Derived classes should override for true streaming decompression
            using var ms = new MemoryStream();
            await input.CopyToAsync(ms, ct).ConfigureAwait(false);
            var compressedData = ms.ToArray();

            var result = await DecompressFromTransitAsync(compressedData, metadata, ct).ConfigureAwait(false);

            await output.WriteAsync(result.Data, ct).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public virtual Task<CompressionNegotiationResult> NegotiateCompressionAsync(
            TransitCompressionCapabilities localCapabilities,
            TransitCompressionCapabilities remoteCapabilities,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(localCapabilities);
            ArgumentNullException.ThrowIfNull(remoteCapabilities);

            // Find mutually supported algorithms
            var mutualAlgorithms = localCapabilities.SupportedAlgorithms
                .Intersect(remoteCapabilities.SupportedAlgorithms)
                .ToArray();

            if (mutualAlgorithms.Length == 0)
            {
                return Task.FromResult(new CompressionNegotiationResult
                {
                    Success = false,
                    FailureReason = "No mutually supported compression algorithms",
                    MutuallySupportedAlgorithms = Array.Empty<string>()
                });
            }

            // Select the first mutual algorithm (highest priority from local)
            var agreedAlgorithm = mutualAlgorithms[0];

            // Determine compression level (use minimum of max levels)
            var agreedLevel = Math.Min(
                localCapabilities.MaxCompressionLevel,
                remoteCapabilities.MaxCompressionLevel);

            // Dictionary negotiation
            var useDictionary = localCapabilities.SupportsDictionary &&
                               remoteCapabilities.SupportsDictionary;

            string? dictionaryId = null;
            if (useDictionary)
            {
                dictionaryId = localCapabilities.AvailableDictionaries
                    .Intersect(remoteCapabilities.AvailableDictionaries)
                    .FirstOrDefault();
            }

            return Task.FromResult(new CompressionNegotiationResult
            {
                Success = true,
                AgreedAlgorithm = agreedAlgorithm,
                AgreedLevel = agreedLevel,
                UseDictionary = useDictionary && dictionaryId != null,
                DictionaryId = dictionaryId,
                MutuallySupportedAlgorithms = mutualAlgorithms
            });
        }

        /// <inheritdoc/>
        public virtual TransitCompressionCapabilities GetLocalCapabilities()
        {
            var algorithms = GetSupportedAlgorithms();

            return new TransitCompressionCapabilities
            {
                SupportedAlgorithms = algorithms.ToArray(),
                SupportsStreaming = SupportsStreaming(),
                SupportsDictionary = SupportsDictionary(),
                AvailableDictionaries = GetAvailableDictionaries().ToArray(),
                EstimatedThroughputMBps = GetEstimatedThroughput(),
                MaxCompressionLevel = 22,
                HasHardwareAcceleration = HasHardwareAcceleration()
            };
        }

        /// <inheritdoc/>
        public virtual bool SupportsAlgorithm(string algorithmName)
        {
            if (string.IsNullOrWhiteSpace(algorithmName))
                return false;

            var supportedAlgorithms = GetSupportedAlgorithms();
            return supportedAlgorithms.Any(a => string.Equals(a, algorithmName, StringComparison.OrdinalIgnoreCase));
        }

        /// <inheritdoc/>
        public virtual long EstimateCompressedSize(long inputSize, string algorithmName)
        {
            if (inputSize <= 0)
                return 0;

            return EstimateCompressedSizeInternal(inputSize, algorithmName);
        }

        /// <summary>
        /// Compresses data with the specified algorithm and level.
        /// Derived classes must implement this for actual compression.
        /// </summary>
        /// <param name="data">The data to compress.</param>
        /// <param name="algorithm">The compression algorithm to use.</param>
        /// <param name="level">The compression level.</param>
        /// <param name="options">Additional compression options.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The compressed data.</returns>
        protected abstract Task<byte[]> CompressDataAsync(
            byte[] data,
            string algorithm,
            int level,
            TransitCompressionOptions options,
            CancellationToken ct);

        /// <summary>
        /// Decompresses data with the specified algorithm.
        /// Derived classes must implement this for actual decompression.
        /// </summary>
        /// <param name="compressedData">The compressed data.</param>
        /// <param name="algorithm">The compression algorithm that was used.</param>
        /// <param name="metadata">Compression metadata.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The decompressed data.</returns>
        protected abstract Task<byte[]> DecompressDataAsync(
            byte[] compressedData,
            string algorithm,
            TransitCompressionMetadata metadata,
            CancellationToken ct);

        /// <summary>
        /// Gets the list of compression algorithms supported by this plugin.
        /// </summary>
        /// <returns>Array of algorithm names (e.g., "zstd", "lz4", "gzip", "brotli").</returns>
        protected abstract IReadOnlyList<string> GetSupportedAlgorithms();

        /// <summary>
        /// Calculates Shannon entropy of data to determine compressibility.
        /// Returns value between 0 (perfectly compressible) and 8 (random/incompressible).
        /// </summary>
        /// <param name="data">The data to analyze.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Entropy value (0-8).</returns>
        protected virtual Task<double> CalculateEntropyAsync(byte[] data, CancellationToken ct)
        {
            if (data.Length == 0)
                return Task.FromResult(0.0);

            // Count byte frequencies
            var frequencies = new int[256];
            foreach (var b in data)
            {
                frequencies[b]++;
            }

            // Calculate Shannon entropy: -Σ(p * log2(p))
            double entropy = 0.0;
            var length = (double)data.Length;

            for (int i = 0; i < 256; i++)
            {
                if (frequencies[i] > 0)
                {
                    var probability = frequencies[i] / length;
                    entropy -= probability * Math.Log2(probability);
                }
            }

            return Task.FromResult(entropy);
        }

        /// <summary>
        /// Estimates compressed size for a given input size and algorithm.
        /// Override for algorithm-specific estimation logic.
        /// </summary>
        protected virtual long EstimateCompressedSizeInternal(long inputSize, string algorithmName)
        {
            // Default heuristic: assume 50% compression ratio for text/structured data
            // Adjust based on algorithm characteristics
            var compressionRatio = algorithmName.ToLowerInvariant() switch
            {
                "lz4" => 0.6,      // Fast but lower ratio
                "snappy" => 0.6,   // Fast but lower ratio
                "gzip" => 0.5,     // Balanced
                "zstd" => 0.45,    // Good ratio
                "brotli" => 0.4,   // High ratio
                "xz" => 0.35,      // Very high ratio
                "lzma" => 0.35,    // Very high ratio
                _ => 0.5           // Default
            };

            return (long)(inputSize * compressionRatio);
        }

        /// <summary>
        /// Gets the default compression level for an algorithm based on priority.
        /// </summary>
        protected virtual int GetDefaultLevel(string algorithm, TransitCompressionPriority priority)
        {
            return priority switch
            {
                TransitCompressionPriority.Speed => 1,
                TransitCompressionPriority.Balanced => algorithm.ToLowerInvariant() == "zstd" ? 3 : 6,
                TransitCompressionPriority.Ratio => algorithm.ToLowerInvariant() == "zstd" ? 19 : 9,
                TransitCompressionPriority.BandwidthSaving => algorithm.ToLowerInvariant() == "zstd" ? 19 : 11,
                _ => 6
            };
        }

        /// <summary>
        /// Selects the best algorithm based on options.
        /// </summary>
        protected virtual string SelectBestAlgorithm(TransitCompressionOptions options)
        {
            // Try preferred algorithm first
            if (!string.IsNullOrWhiteSpace(options.PreferredAlgorithm) &&
                SupportsAlgorithm(options.PreferredAlgorithm))
            {
                return options.PreferredAlgorithm;
            }

            // Try fallback algorithms
            foreach (var fallback in options.FallbackAlgorithms)
            {
                if (SupportsAlgorithm(fallback))
                    return fallback;
            }

            // Select based on priority
            var supportedAlgorithms = GetSupportedAlgorithms();
            return options.Priority switch
            {
                TransitCompressionPriority.Speed =>
                    supportedAlgorithms.FirstOrDefault(a => a.Equals("lz4", StringComparison.OrdinalIgnoreCase)) ??
                    supportedAlgorithms.FirstOrDefault(a => a.Equals("snappy", StringComparison.OrdinalIgnoreCase)) ??
                    supportedAlgorithms.First(),

                TransitCompressionPriority.Ratio or TransitCompressionPriority.BandwidthSaving =>
                    supportedAlgorithms.FirstOrDefault(a => a.Equals("zstd", StringComparison.OrdinalIgnoreCase)) ??
                    supportedAlgorithms.FirstOrDefault(a => a.Equals("brotli", StringComparison.OrdinalIgnoreCase)) ??
                    supportedAlgorithms.First(),

                _ => // Balanced
                    supportedAlgorithms.FirstOrDefault(a => a.Equals("zstd", StringComparison.OrdinalIgnoreCase)) ??
                    supportedAlgorithms.First()
            };
        }

        /// <summary>
        /// Creates a result for data that was not compressed.
        /// </summary>
        private TransitCompressionResult CreateUncompressedResult(byte[] data, DateTime startTime)
        {
            return new TransitCompressionResult
            {
                CompressedData = data,
                Metadata = new TransitCompressionMetadata
                {
                    AlgorithmName = "none",
                    IsCompressed = false,
                    OriginalSize = data.Length
                },
                OriginalSize = data.Length,
                CompressedSize = data.Length,
                WasCompressed = false,
                CompressionTime = DateTime.UtcNow - startTime
            };
        }

        /// <summary>
        /// Whether this plugin supports streaming compression.
        /// Override to return true if streaming is implemented.
        /// </summary>
        protected virtual bool SupportsStreaming() => false;

        /// <summary>
        /// Whether this plugin supports dictionary-based compression.
        /// Override to return true if dictionary compression is implemented.
        /// </summary>
        protected virtual bool SupportsDictionary() => false;

        /// <summary>
        /// Gets available dictionary IDs for dictionary-based compression.
        /// Override to return available dictionaries.
        /// </summary>
        protected virtual IReadOnlyList<string> GetAvailableDictionaries() => Array.Empty<string>();

        /// <summary>
        /// Gets estimated compression throughput in MB/s.
        /// Override with actual benchmarked values.
        /// </summary>
        protected virtual double GetEstimatedThroughput() => 100.0; // Default: 100 MB/s

        /// <summary>
        /// Whether hardware acceleration is available (e.g., Intel QAT, FPGA).
        /// Override to return true if hardware acceleration is detected.
        /// </summary>
        protected virtual bool HasHardwareAcceleration() => false;

        /// <summary>
        /// Gets compression statistics.
        /// </summary>
        public virtual (long TotalCompressions, long TotalDecompressions, long BytesCompressed, long BytesDecompressed, long SkippedIncompressible) GetStatistics()
        {
            return (
                _totalCompressions,
                _totalDecompressions,
                _totalBytesCompressed,
                _totalBytesDecompressed,
                _skippedIncompressible
            );
        }
    }
}
