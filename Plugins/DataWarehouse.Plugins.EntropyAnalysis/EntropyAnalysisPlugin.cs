using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace DataWarehouse.Plugins.EntropyAnalysis
{
    /// <summary>
    /// Advanced entropy-based threat detection plugin for DataWarehouse.
    /// Provides comprehensive entropy analysis capabilities for detecting encrypted,
    /// compressed, or potentially ransomware-affected data.
    ///
    /// Key Features:
    /// - Shannon entropy calculation with configurable precision
    /// - Rolling/sliding window entropy analysis for partial encryption detection
    /// - Block-level entropy mapping for visualization
    /// - File type classification based on entropy signatures
    /// - Entropy baseline management and deviation detection
    /// - Anomaly detection based on entropy changes over time
    /// - Support for entropy histogram generation
    /// - Configurable thresholds for different security policies
    ///
    /// Use Cases:
    /// - Ransomware detection (high entropy indicates encryption)
    /// - Data exfiltration prevention (encrypted data leaving network)
    /// - File type verification (entropy-based file fingerprinting)
    /// - Compliance monitoring (detecting unauthorized encryption)
    /// - Storage optimization (identifying already-compressed data)
    /// </summary>
    public sealed class EntropyAnalysisPlugin : ThreatDetectionPluginBase
    {
        #region Fields and Configuration

        private readonly EntropyAnalysisConfig _config;
        private readonly ConcurrentDictionary<string, EntropyBaseline> _baselines = new();
        private readonly ConcurrentDictionary<string, EntropyHistory> _entropyHistory = new();
        private readonly List<FileTypeSignature> _fileTypeSignatures = new();
        private readonly object _statsLock = new();

        private long _totalAnalyses;
        private long _highEntropyDetections;
        private long _anomaliesDetected;
        private DateTime _lastAnalysisTime = DateTime.MinValue;

        // Default entropy thresholds (Shannon entropy for byte data, max = 8.0)
        private const double DefaultHighEntropyThreshold = 7.9;
        private const double DefaultSuspiciousEntropyThreshold = 7.5;
        private const double DefaultEncryptedMinThreshold = 7.8;
        private const double DefaultCompressedMinThreshold = 7.0;
        private const double DefaultTextMaxThreshold = 5.5;

        // Default sliding window configuration
        private const int DefaultWindowSize = 4096;
        private const int DefaultWindowStep = 1024;
        private const int DefaultBlockSize = 256;

        #endregion

        #region Plugin Identity

        /// <inheritdoc />
        public override string Id => "com.datawarehouse.threatdetection.entropy";

        /// <inheritdoc />
        public override string Name => "Entropy Analysis Plugin";

        /// <inheritdoc />
        public override string Version => "1.0.0";

        /// <inheritdoc />
        public override ThreatDetectionCapabilities DetectionCapabilities =>
            ThreatDetectionCapabilities.Ransomware |
            ThreatDetectionCapabilities.Entropy |
            ThreatDetectionCapabilities.Anomaly;

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="EntropyAnalysisPlugin"/> class
        /// with default configuration.
        /// </summary>
        public EntropyAnalysisPlugin() : this(new EntropyAnalysisConfig())
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="EntropyAnalysisPlugin"/> class
        /// with custom configuration.
        /// </summary>
        /// <param name="config">The configuration for entropy analysis.</param>
        public EntropyAnalysisPlugin(EntropyAnalysisConfig config)
        {
            _config = config ?? new EntropyAnalysisConfig();
            InitializeFileTypeSignatures();
        }

        #endregion

        #region Core Entropy Calculation

        /// <summary>
        /// Calculates Shannon entropy for a byte array.
        /// Shannon entropy measures the average information content per byte.
        /// </summary>
        /// <param name="data">The data to analyze.</param>
        /// <returns>Entropy value between 0.0 (uniform) and 8.0 (maximum randomness).</returns>
        /// <remarks>
        /// The formula used is: H = -sum(p(x) * log2(p(x))) for all byte values x
        /// where p(x) is the probability of byte value x occurring.
        /// </remarks>
        public static double CalculateShannonEntropy(ReadOnlySpan<byte> data)
        {
            if (data.Length == 0) return 0.0;

            // Count byte frequencies
            Span<long> frequencies = stackalloc long[256];
            foreach (var b in data)
            {
                frequencies[b]++;
            }

            // Calculate entropy
            double entropy = 0.0;
            double totalBytes = data.Length;

            for (int i = 0; i < 256; i++)
            {
                if (frequencies[i] > 0)
                {
                    double probability = frequencies[i] / totalBytes;
                    entropy -= probability * Math.Log2(probability);
                }
            }

            return entropy;
        }

        /// <summary>
        /// Calculates normalized entropy (0.0 to 1.0 scale).
        /// </summary>
        /// <param name="data">The data to analyze.</param>
        /// <returns>Normalized entropy value between 0.0 and 1.0.</returns>
        public static double CalculateNormalizedEntropy(ReadOnlySpan<byte> data)
        {
            return CalculateShannonEntropy(data) / 8.0;
        }

        /// <summary>
        /// Calculates entropy asynchronously for a stream.
        /// </summary>
        /// <param name="data">The stream to analyze.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The entropy result.</returns>
        public override async Task<EntropyResult> CalculateEntropyAsync(Stream data, CancellationToken ct = default)
        {
            var frequencies = new long[256];
            var buffer = new byte[8192];
            long totalBytes = 0;
            int bytesRead;

            while ((bytesRead = await data.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                for (int i = 0; i < bytesRead; i++)
                {
                    frequencies[buffer[i]]++;
                }
                totalBytes += bytesRead;
            }

            if (totalBytes == 0)
            {
                return new EntropyResult
                {
                    Entropy = 0,
                    NormalizedEntropy = 0,
                    IsHighEntropy = false,
                    Assessment = "Empty data - no entropy calculation possible"
                };
            }

            double entropy = 0.0;
            for (int i = 0; i < 256; i++)
            {
                if (frequencies[i] > 0)
                {
                    double probability = (double)frequencies[i] / totalBytes;
                    entropy -= probability * Math.Log2(probability);
                }
            }

            double normalized = entropy / 8.0;
            bool isHigh = normalized >= (_config.HighEntropyThreshold / 8.0);

            return new EntropyResult
            {
                Entropy = entropy,
                NormalizedEntropy = normalized,
                IsHighEntropy = isHigh,
                Assessment = GenerateEntropyAssessment(entropy, normalized, isHigh)
            };
        }

        #endregion

        #region Advanced Entropy Analysis

        /// <summary>
        /// Performs comprehensive entropy analysis including rolling window analysis,
        /// block-level mapping, and file type classification.
        /// </summary>
        /// <param name="data">The data to analyze.</param>
        /// <param name="options">Analysis options.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Comprehensive entropy profile.</returns>
        public async Task<EntropyProfile> AnalyzeEntropyAsync(
            Stream data,
            EntropyAnalysisOptions? options = null,
            CancellationToken ct = default)
        {
            options ??= new EntropyAnalysisOptions();
            var startTime = DateTime.UtcNow;

            // Read data into memory for multi-pass analysis
            byte[] content;
            using (var ms = new MemoryStream())
            {
                await data.CopyToAsync(ms, ct);
                content = ms.ToArray();
            }

            if (content.Length == 0)
            {
                return CreateEmptyProfile(startTime);
            }

            // Calculate overall entropy
            var overallEntropy = CalculateShannonEntropy(content);
            var normalizedEntropy = overallEntropy / 8.0;

            // Perform rolling window analysis
            var windowResults = options.EnableRollingAnalysis
                ? PerformRollingWindowAnalysis(content, options.WindowSize, options.WindowStep, ct)
                : Array.Empty<WindowEntropyPoint>();

            // Perform block-level analysis for visualization
            var blockResults = options.EnableBlockAnalysis
                ? PerformBlockAnalysis(content, options.BlockSize, ct)
                : Array.Empty<BlockEntropyData>();

            // Generate histogram
            var histogram = options.GenerateHistogram
                ? GenerateEntropyHistogram(content)
                : null;

            // Classify file type based on entropy
            var classification = ClassifyByEntropy(content, overallEntropy, windowResults);

            // Detect anomalies
            var anomalies = DetectEntropyAnomalies(content, overallEntropy, windowResults);

            // Update statistics
            RecordAnalysis(normalizedEntropy >= (_config.HighEntropyThreshold / 8.0), anomalies.Count > 0);

            var duration = DateTime.UtcNow - startTime;

            return new EntropyProfile
            {
                OverallEntropy = overallEntropy,
                NormalizedEntropy = normalizedEntropy,
                DataSize = content.Length,
                IsHighEntropy = normalizedEntropy >= (_config.HighEntropyThreshold / 8.0),
                IsSuspicious = normalizedEntropy >= (_config.SuspiciousEntropyThreshold / 8.0),
                Classification = classification,
                WindowAnalysis = windowResults,
                BlockAnalysis = blockResults,
                Histogram = histogram,
                Anomalies = anomalies,
                HighEntropyRegions = IdentifyHighEntropyRegions(windowResults),
                LowEntropyRegions = IdentifyLowEntropyRegions(windowResults),
                EntropyVariance = CalculateEntropyVariance(windowResults),
                Assessment = GenerateDetailedAssessment(overallEntropy, normalizedEntropy, classification, anomalies),
                AnalysisDuration = duration
            };
        }

        /// <summary>
        /// Performs rolling window entropy analysis to detect partial encryption
        /// or entropy variations across the data.
        /// </summary>
        /// <param name="content">The content to analyze.</param>
        /// <param name="windowSize">Size of each analysis window in bytes.</param>
        /// <param name="windowStep">Step size between windows.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Array of entropy points for each window position.</returns>
        public WindowEntropyPoint[] PerformRollingWindowAnalysis(
            byte[] content,
            int windowSize = DefaultWindowSize,
            int windowStep = DefaultWindowStep,
            CancellationToken ct = default)
        {
            if (content.Length < windowSize)
            {
                return new[]
                {
                    new WindowEntropyPoint
                    {
                        Offset = 0,
                        Size = content.Length,
                        Entropy = CalculateShannonEntropy(content),
                        NormalizedEntropy = CalculateNormalizedEntropy(content)
                    }
                };
            }

            var results = new List<WindowEntropyPoint>();

            for (int offset = 0; offset + windowSize <= content.Length; offset += windowStep)
            {
                ct.ThrowIfCancellationRequested();

                var windowData = content.AsSpan(offset, windowSize);
                var entropy = CalculateShannonEntropy(windowData);
                var normalized = entropy / 8.0;

                results.Add(new WindowEntropyPoint
                {
                    Offset = offset,
                    Size = windowSize,
                    Entropy = entropy,
                    NormalizedEntropy = normalized,
                    IsHighEntropy = normalized >= (_config.SuspiciousEntropyThreshold / 8.0)
                });
            }

            return results.ToArray();
        }

        /// <summary>
        /// Performs block-level entropy analysis for visualization purposes.
        /// </summary>
        /// <param name="content">The content to analyze.</param>
        /// <param name="blockSize">Size of each block in bytes.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Array of block entropy data.</returns>
        public BlockEntropyData[] PerformBlockAnalysis(
            byte[] content,
            int blockSize = DefaultBlockSize,
            CancellationToken ct = default)
        {
            if (content.Length == 0) return Array.Empty<BlockEntropyData>();

            var blockCount = (content.Length + blockSize - 1) / blockSize;
            var results = new BlockEntropyData[blockCount];

            for (int i = 0; i < blockCount; i++)
            {
                ct.ThrowIfCancellationRequested();

                int offset = i * blockSize;
                int size = Math.Min(blockSize, content.Length - offset);
                var blockData = content.AsSpan(offset, size);
                var entropy = CalculateShannonEntropy(blockData);

                results[i] = new BlockEntropyData
                {
                    BlockIndex = i,
                    Offset = offset,
                    Size = size,
                    Entropy = entropy,
                    NormalizedEntropy = entropy / 8.0,
                    EntropyCategory = CategorizeEntropy(entropy)
                };
            }

            return results;
        }

        /// <summary>
        /// Generates an entropy histogram showing the distribution of byte values.
        /// </summary>
        /// <param name="content">The content to analyze.</param>
        /// <returns>Entropy histogram data.</returns>
        public EntropyHistogram GenerateEntropyHistogram(byte[] content)
        {
            if (content.Length == 0)
            {
                return new EntropyHistogram
                {
                    ByteFrequencies = new long[256],
                    ByteProbabilities = new double[256],
                    TotalBytes = 0,
                    UniqueByteCount = 0,
                    MostFrequentByte = 0,
                    LeastFrequentByte = 0,
                    Uniformity = 0
                };
            }

            var frequencies = new long[256];
            foreach (var b in content)
            {
                frequencies[b]++;
            }

            var probabilities = new double[256];
            var total = (double)content.Length;
            int uniqueCount = 0;
            int mostFrequentByte = 0;
            int leastFrequentByte = -1;
            long maxFreq = 0;
            long minFreq = long.MaxValue;

            for (int i = 0; i < 256; i++)
            {
                probabilities[i] = frequencies[i] / total;
                if (frequencies[i] > 0)
                {
                    uniqueCount++;
                    if (frequencies[i] > maxFreq)
                    {
                        maxFreq = frequencies[i];
                        mostFrequentByte = i;
                    }
                    if (frequencies[i] < minFreq)
                    {
                        minFreq = frequencies[i];
                        leastFrequentByte = i;
                    }
                }
            }

            // Calculate uniformity (how close to uniform distribution)
            double expectedFreq = content.Length / 256.0;
            double variance = 0;
            for (int i = 0; i < 256; i++)
            {
                double diff = frequencies[i] - expectedFreq;
                variance += diff * diff;
            }
            double stdDev = Math.Sqrt(variance / 256.0);
            double uniformity = 1.0 - Math.Min(1.0, stdDev / expectedFreq);

            return new EntropyHistogram
            {
                ByteFrequencies = frequencies,
                ByteProbabilities = probabilities,
                TotalBytes = content.Length,
                UniqueByteCount = uniqueCount,
                MostFrequentByte = (byte)mostFrequentByte,
                LeastFrequentByte = leastFrequentByte >= 0 ? (byte)leastFrequentByte : (byte)0,
                MaxFrequency = maxFreq,
                MinFrequency = minFreq,
                Uniformity = uniformity
            };
        }

        #endregion

        #region File Type Classification

        /// <summary>
        /// Classifies the content based on entropy characteristics.
        /// Different file types have characteristic entropy signatures.
        /// </summary>
        /// <param name="content">The content to classify.</param>
        /// <param name="overallEntropy">Pre-calculated overall entropy.</param>
        /// <param name="windowResults">Window analysis results.</param>
        /// <returns>File type classification based on entropy.</returns>
        public EntropyClassification ClassifyByEntropy(
            byte[] content,
            double overallEntropy,
            IReadOnlyList<WindowEntropyPoint> windowResults)
        {
            var normalized = overallEntropy / 8.0;

            // Check against known file type signatures
            foreach (var signature in _fileTypeSignatures)
            {
                if (MatchesEntropySignature(content, overallEntropy, windowResults, signature))
                {
                    return new EntropyClassification
                    {
                        Category = signature.Category,
                        FileType = signature.FileType,
                        Confidence = signature.ConfidenceWhenMatched,
                        Description = signature.Description,
                        ExpectedEntropyRange = (signature.MinEntropy, signature.MaxEntropy),
                        IsWithinExpectedRange = overallEntropy >= signature.MinEntropy && overallEntropy <= signature.MaxEntropy
                    };
                }
            }

            // Default classification based on entropy ranges
            return ClassifyByEntropyRange(overallEntropy, normalized, windowResults);
        }

        /// <summary>
        /// Gets the expected entropy baseline for a given file type.
        /// </summary>
        /// <param name="fileType">The file type (extension without dot).</param>
        /// <returns>Expected entropy range for the file type.</returns>
        public static (double Min, double Max, double Typical) GetExpectedEntropyForFileType(string? fileType)
        {
            return fileType?.ToLowerInvariant() switch
            {
                // Encrypted data (maximum entropy)
                "enc" or "aes" or "gpg" => (7.8, 8.0, 7.99),

                // Compressed archives (very high entropy)
                "zip" or "gz" or "xz" or "zst" or "bz2" or "lz4" => (7.5, 8.0, 7.9),
                "rar" or "7z" => (7.6, 8.0, 7.85),

                // Media files (high entropy due to compression)
                "jpg" or "jpeg" or "webp" => (7.4, 8.0, 7.8),
                "png" => (6.5, 8.0, 7.5),
                "gif" => (5.0, 7.5, 6.5),
                "mp3" or "aac" or "ogg" or "opus" => (7.5, 8.0, 7.9),
                "mp4" or "mkv" or "webm" or "avi" => (7.3, 8.0, 7.8),

                // Binary executables
                "exe" or "dll" => (5.5, 7.5, 6.2),
                "so" or "dylib" => (5.0, 7.0, 6.0),
                "elf" => (5.0, 7.0, 5.8),

                // Office documents (compressed XML internally)
                "docx" or "xlsx" or "pptx" or "odt" or "ods" => (7.0, 8.0, 7.6),
                "doc" or "xls" or "ppt" => (4.5, 6.5, 5.5),
                "pdf" => (5.0, 7.5, 6.5),

                // Text and code files
                "txt" or "log" or "csv" => (3.5, 5.5, 4.5),
                "html" or "xml" or "json" or "yaml" => (4.0, 5.5, 4.8),
                "cs" or "java" or "py" or "js" or "ts" or "go" or "rs" => (4.0, 5.5, 4.7),
                "c" or "cpp" or "h" or "hpp" => (4.0, 5.5, 4.6),
                "sql" => (3.5, 5.0, 4.2),
                "md" or "rst" => (3.5, 5.0, 4.3),

                // Structured data
                "sqlite" or "db" => (4.0, 7.0, 5.5),

                // Default - unknown type
                _ => (0.0, 8.0, 5.0)
            };
        }

        private bool MatchesEntropySignature(
            byte[] content,
            double entropy,
            IReadOnlyList<WindowEntropyPoint> windows,
            FileTypeSignature signature)
        {
            // Check entropy range
            if (entropy < signature.MinEntropy || entropy > signature.MaxEntropy)
                return false;

            // Check magic bytes if present
            if (signature.MagicBytes != null && signature.MagicBytes.Length > 0)
            {
                if (content.Length < signature.MagicBytesOffset + signature.MagicBytes.Length)
                    return false;

                for (int i = 0; i < signature.MagicBytes.Length; i++)
                {
                    if (content[signature.MagicBytesOffset + i] != signature.MagicBytes[i])
                        return false;
                }
            }

            // Check entropy uniformity if specified
            if (signature.RequiresUniformEntropy && windows.Count > 1)
            {
                var entropyValues = windows.Select(w => w.Entropy).ToList();
                var variance = CalculateVariance(entropyValues);
                if (variance > signature.MaxEntropyVariance)
                    return false;
            }

            return true;
        }

        private EntropyClassification ClassifyByEntropyRange(
            double entropy,
            double normalized,
            IReadOnlyList<WindowEntropyPoint> windows)
        {
            // Calculate entropy variance for better classification
            double variance = windows.Count > 1 ? CalculateEntropyVariance(windows) : 0;
            double highEntropyRatio = windows.Count > 0
                ? (double)windows.Count(w => w.IsHighEntropy) / windows.Count
                : normalized >= (_config.SuspiciousEntropyThreshold / 8.0) ? 1.0 : 0.0;

            if (normalized >= 0.975)
            {
                // Near-maximum entropy - likely encrypted
                return new EntropyClassification
                {
                    Category = FileEntropyCategory.Encrypted,
                    FileType = variance < 0.01 ? "encrypted" : "encrypted_partial",
                    Confidence = normalized >= 0.99 ? 0.95 : 0.85,
                    Description = "Near-maximum entropy strongly indicates encryption",
                    ExpectedEntropyRange = (7.8, 8.0),
                    IsWithinExpectedRange = true
                };
            }
            else if (normalized >= 0.9375)
            {
                // Very high entropy - encrypted or compressed
                if (variance < 0.05)
                {
                    return new EntropyClassification
                    {
                        Category = FileEntropyCategory.Encrypted,
                        FileType = "possibly_encrypted",
                        Confidence = 0.75,
                        Description = "Very high uniform entropy suggests encryption",
                        ExpectedEntropyRange = (7.5, 8.0),
                        IsWithinExpectedRange = true
                    };
                }
                return new EntropyClassification
                {
                    Category = FileEntropyCategory.Compressed,
                    FileType = "compressed_archive",
                    Confidence = 0.7,
                    Description = "Very high entropy typical of compressed data",
                    ExpectedEntropyRange = (7.5, 8.0),
                    IsWithinExpectedRange = true
                };
            }
            else if (normalized >= 0.8)
            {
                // High entropy - likely compressed media or partial encryption
                if (highEntropyRatio > 0.5 && highEntropyRatio < 1.0)
                {
                    return new EntropyClassification
                    {
                        Category = FileEntropyCategory.PartiallyEncrypted,
                        FileType = "partial_encryption",
                        Confidence = 0.6,
                        Description = $"Mixed entropy pattern - {highEntropyRatio * 100:F0}% high entropy regions",
                        ExpectedEntropyRange = (6.4, 8.0),
                        IsWithinExpectedRange = true
                    };
                }
                return new EntropyClassification
                {
                    Category = FileEntropyCategory.Compressed,
                    FileType = "compressed_media",
                    Confidence = 0.65,
                    Description = "High entropy typical of compressed media files",
                    ExpectedEntropyRange = (6.4, 8.0),
                    IsWithinExpectedRange = true
                };
            }
            else if (normalized >= 0.6)
            {
                // Medium-high entropy - binary executable, PDF, or packed
                return new EntropyClassification
                {
                    Category = FileEntropyCategory.Binary,
                    FileType = "binary_executable",
                    Confidence = 0.5,
                    Description = "Medium-high entropy typical of binary executables or structured binary data",
                    ExpectedEntropyRange = (4.8, 6.4),
                    IsWithinExpectedRange = true
                };
            }
            else if (normalized >= 0.4)
            {
                // Medium entropy - structured text or simple binary
                return new EntropyClassification
                {
                    Category = FileEntropyCategory.StructuredData,
                    FileType = "structured_data",
                    Confidence = 0.5,
                    Description = "Medium entropy typical of structured text (code, markup, config)",
                    ExpectedEntropyRange = (3.2, 4.8),
                    IsWithinExpectedRange = true
                };
            }
            else
            {
                // Low entropy - plain text or repetitive data
                return new EntropyClassification
                {
                    Category = FileEntropyCategory.PlainText,
                    FileType = "plain_text",
                    Confidence = 0.6,
                    Description = "Low entropy typical of plain text or repetitive data",
                    ExpectedEntropyRange = (0.0, 3.2),
                    IsWithinExpectedRange = true
                };
            }
        }

        #endregion

        #region Baseline Management

        /// <inheritdoc />
        public override Task RegisterBaselineAsync(
            string objectId,
            BaselineData baseline,
            CancellationToken ct = default)
        {
            var entropyBaseline = new EntropyBaseline
            {
                ObjectId = objectId,
                Entropy = baseline.Entropy,
                Size = baseline.Size,
                ContentHash = baseline.ContentHash,
                ContentType = baseline.ContentType,
                CreatedAt = DateTime.UtcNow,
                Features = baseline.Features
            };

            _baselines[objectId] = entropyBaseline;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Registers an entropy baseline with detailed entropy analysis.
        /// </summary>
        /// <param name="objectId">Object identifier.</param>
        /// <param name="profile">Full entropy profile to use as baseline.</param>
        /// <param name="ct">Cancellation token.</param>
        public Task RegisterEntropyBaselineAsync(
            string objectId,
            EntropyProfile profile,
            CancellationToken ct = default)
        {
            var baseline = new EntropyBaseline
            {
                ObjectId = objectId,
                Entropy = profile.OverallEntropy,
                NormalizedEntropy = profile.NormalizedEntropy,
                Size = profile.DataSize,
                ContentHash = string.Empty,
                Classification = profile.Classification,
                EntropyVariance = profile.EntropyVariance,
                HighEntropyRegionCount = profile.HighEntropyRegions.Count,
                CreatedAt = DateTime.UtcNow
            };

            _baselines[objectId] = baseline;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Gets the registered baseline for an object.
        /// </summary>
        /// <param name="objectId">Object identifier.</param>
        /// <returns>The baseline if registered, null otherwise.</returns>
        public EntropyBaseline? GetBaseline(string objectId)
        {
            _baselines.TryGetValue(objectId, out var baseline);
            return baseline;
        }

        /// <inheritdoc />
        public override async Task<BaselineComparisonResult> CompareToBaselineAsync(
            string objectId,
            Stream currentData,
            CancellationToken ct = default)
        {
            if (!_baselines.TryGetValue(objectId, out var baseline))
            {
                return new BaselineComparisonResult
                {
                    HasDeviation = false,
                    DeviationScore = 0,
                    ChangedFeatures = Array.Empty<string>(),
                    Assessment = "No baseline registered for this object"
                };
            }

            // Read current data
            byte[] content;
            using (var ms = new MemoryStream())
            {
                await currentData.CopyToAsync(ms, ct);
                content = ms.ToArray();
            }

            // Calculate current entropy
            var currentEntropy = CalculateShannonEntropy(content);
            var currentNormalized = currentEntropy / 8.0;

            return CompareToBaseline(objectId, content, currentEntropy, baseline);
        }

        /// <summary>
        /// Compares current content entropy against baseline with detailed analysis.
        /// </summary>
        /// <param name="objectId">Object identifier.</param>
        /// <param name="currentData">Current content.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Detailed baseline comparison result.</returns>
        public async Task<EntropyBaselineComparison> CompareEntropyToBaselineAsync(
            string objectId,
            Stream currentData,
            CancellationToken ct = default)
        {
            if (!_baselines.TryGetValue(objectId, out var baseline))
            {
                return new EntropyBaselineComparison
                {
                    ObjectId = objectId,
                    HasBaseline = false,
                    Assessment = "No baseline registered for this object"
                };
            }

            // Perform full analysis on current data
            var profile = await AnalyzeEntropyAsync(currentData, new EntropyAnalysisOptions
            {
                EnableRollingAnalysis = true,
                EnableBlockAnalysis = false
            }, ct);

            var entropyDelta = profile.OverallEntropy - baseline.Entropy;
            var normalizedDelta = profile.NormalizedEntropy - baseline.NormalizedEntropy;
            var sizeDelta = profile.DataSize - baseline.Size;
            var sizeChangePercent = baseline.Size > 0
                ? (double)sizeDelta / baseline.Size * 100
                : (profile.DataSize > 0 ? 100 : 0);

            // Determine severity of changes
            var changes = new List<EntropyChange>();
            double deviationScore = 0;

            // Check entropy change
            if (Math.Abs(entropyDelta) > 0.5)
            {
                changes.Add(new EntropyChange
                {
                    ChangeType = EntropyChangeType.EntropyIncrease,
                    Description = $"Entropy changed from {baseline.Entropy:F3} to {profile.OverallEntropy:F3}",
                    Severity = Math.Abs(entropyDelta) > 2.0 ? ChangeSeverity.Critical :
                               Math.Abs(entropyDelta) > 1.0 ? ChangeSeverity.High : ChangeSeverity.Medium,
                    Impact = Math.Abs(normalizedDelta)
                });
                deviationScore += Math.Abs(normalizedDelta) * 0.5;
            }

            // Check size change
            if (Math.Abs(sizeChangePercent) > 10)
            {
                changes.Add(new EntropyChange
                {
                    ChangeType = sizeDelta > 0 ? EntropyChangeType.SizeIncrease : EntropyChangeType.SizeDecrease,
                    Description = $"Size changed by {sizeChangePercent:F1}% ({baseline.Size} -> {profile.DataSize})",
                    Severity = Math.Abs(sizeChangePercent) > 50 ? ChangeSeverity.High : ChangeSeverity.Medium,
                    Impact = Math.Abs(sizeChangePercent) / 100
                });
                deviationScore += Math.Min(1.0, Math.Abs(sizeChangePercent) / 100) * 0.3;
            }

            // Check classification change
            if (baseline.Classification != null && profile.Classification.Category != baseline.Classification.Category)
            {
                changes.Add(new EntropyChange
                {
                    ChangeType = EntropyChangeType.ClassificationChange,
                    Description = $"Classification changed from {baseline.Classification.Category} to {profile.Classification.Category}",
                    Severity = profile.Classification.Category == FileEntropyCategory.Encrypted ?
                               ChangeSeverity.Critical : ChangeSeverity.High,
                    Impact = 1.0
                });
                deviationScore += 0.4;
            }

            // Normalize deviation score
            deviationScore = Math.Min(1.0, deviationScore);

            // Generate assessment
            var assessment = GenerateBaselineAssessment(baseline, profile, changes, deviationScore);

            // Record in history
            RecordEntropyHistory(objectId, profile.OverallEntropy, profile.NormalizedEntropy);

            return new EntropyBaselineComparison
            {
                ObjectId = objectId,
                HasBaseline = true,
                BaselineEntropy = baseline.Entropy,
                CurrentEntropy = profile.OverallEntropy,
                EntropyDelta = entropyDelta,
                NormalizedEntropyDelta = normalizedDelta,
                SizeDelta = sizeDelta,
                SizeChangePercent = sizeChangePercent,
                Changes = changes,
                DeviationScore = deviationScore,
                HasSignificantDeviation = deviationScore > 0.3,
                CurrentProfile = profile,
                Assessment = assessment
            };
        }

        private BaselineComparisonResult CompareToBaseline(
            string objectId,
            byte[] content,
            double currentEntropy,
            EntropyBaseline baseline)
        {
            var changedFeatures = new List<string>();
            double deviationScore = 0;

            // Check entropy change
            var entropyDiff = Math.Abs(currentEntropy - baseline.Entropy);
            if (entropyDiff > 0.5)
            {
                changedFeatures.Add($"Entropy changed from {baseline.Entropy:F2} to {currentEntropy:F2}");
                deviationScore += entropyDiff * 0.15;
            }

            // Check size change
            var sizeDiff = Math.Abs(content.Length - baseline.Size) / (double)Math.Max(baseline.Size, 1);
            if (sizeDiff > 0.1)
            {
                changedFeatures.Add($"Size changed by {sizeDiff * 100:F1}%");
                deviationScore += sizeDiff * 0.3;
            }

            // Check content hash if available
            if (!string.IsNullOrEmpty(baseline.ContentHash))
            {
                var currentHash = Convert.ToHexString(SHA256.HashData(content));
                if (!string.Equals(currentHash, baseline.ContentHash, StringComparison.OrdinalIgnoreCase))
                {
                    changedFeatures.Add("Content hash changed");
                    deviationScore += 0.5;
                }
            }

            deviationScore = Math.Min(1.0, deviationScore);

            string assessment;
            if (deviationScore > 0.7)
            {
                assessment = "CRITICAL: Major entropy deviation detected. Content may have been encrypted or significantly altered.";
            }
            else if (deviationScore > 0.4)
            {
                assessment = "WARNING: Significant entropy change detected. Review content changes.";
            }
            else if (deviationScore > 0.2)
            {
                assessment = "NOTICE: Minor entropy deviations detected. Normal modifications may have occurred.";
            }
            else
            {
                assessment = "OK: Entropy is within expected parameters of baseline.";
            }

            return new BaselineComparisonResult
            {
                HasDeviation = deviationScore > 0.2,
                DeviationScore = deviationScore,
                ChangedFeatures = changedFeatures,
                Assessment = assessment
            };
        }

        private void RecordEntropyHistory(string objectId, double entropy, double normalizedEntropy)
        {
            var history = _entropyHistory.GetOrAdd(objectId, _ => new EntropyHistory { ObjectId = objectId });
            history.AddEntry(entropy, normalizedEntropy);
        }

        #endregion

        #region Anomaly Detection

        /// <summary>
        /// Detects entropy-based anomalies in the content.
        /// </summary>
        /// <param name="content">Content to analyze.</param>
        /// <param name="overallEntropy">Pre-calculated overall entropy.</param>
        /// <param name="windows">Window analysis results.</param>
        /// <returns>List of detected anomalies.</returns>
        public List<EntropyAnomaly> DetectEntropyAnomalies(
            byte[] content,
            double overallEntropy,
            IReadOnlyList<WindowEntropyPoint> windows)
        {
            var anomalies = new List<EntropyAnomaly>();

            // Check for sudden entropy transitions
            if (windows.Count > 2)
            {
                for (int i = 1; i < windows.Count; i++)
                {
                    var entropyDrop = windows[i - 1].Entropy - windows[i].Entropy;
                    var entropyJump = windows[i].Entropy - windows[i - 1].Entropy;

                    if (Math.Abs(entropyDrop) > 2.0)
                    {
                        anomalies.Add(new EntropyAnomaly
                        {
                            Type = entropyDrop > 0 ? EntropyAnomalyType.SuddenEntropyDrop : EntropyAnomalyType.SuddenEntropySpike,
                            Offset = windows[i].Offset,
                            Size = windows[i].Size,
                            Description = $"Sudden entropy {(entropyDrop > 0 ? "drop" : "spike")} of {Math.Abs(entropyDrop):F2} bits/byte at offset {windows[i].Offset}",
                            Severity = Math.Abs(entropyDrop) > 3.0 ? AnomalySeverity.High : AnomalySeverity.Medium,
                            Confidence = 0.8,
                            EntropyBefore = windows[i - 1].Entropy,
                            EntropyAfter = windows[i].Entropy
                        });
                    }
                }
            }

            // Check for encrypted headers/sections
            if (windows.Count > 0)
            {
                var firstWindowEntropy = windows[0].NormalizedEntropy;
                var avgEntropy = windows.Average(w => w.NormalizedEntropy);

                // Low entropy header followed by high entropy content (common in partially encrypted files)
                if (firstWindowEntropy < 0.6 && avgEntropy > 0.9)
                {
                    anomalies.Add(new EntropyAnomaly
                    {
                        Type = EntropyAnomalyType.EncryptedPayload,
                        Offset = 0,
                        Size = content.Length,
                        Description = "Low-entropy header followed by high-entropy payload - possible encrypted content with header",
                        Severity = AnomalySeverity.High,
                        Confidence = 0.75,
                        EntropyBefore = firstWindowEntropy * 8,
                        EntropyAfter = avgEntropy * 8
                    });
                }

                // High entropy header with lower content (encrypted header, plain body)
                if (firstWindowEntropy > 0.95 && avgEntropy < 0.8)
                {
                    anomalies.Add(new EntropyAnomaly
                    {
                        Type = EntropyAnomalyType.EncryptedHeader,
                        Offset = 0,
                        Size = windows[0].Size,
                        Description = "High-entropy header with lower-entropy content - possible encrypted metadata",
                        Severity = AnomalySeverity.Medium,
                        Confidence = 0.65,
                        EntropyBefore = firstWindowEntropy * 8,
                        EntropyAfter = avgEntropy * 8
                    });
                }
            }

            // Check for unusual byte distribution
            var histogram = GenerateEntropyHistogram(content);
            if (histogram.UniqueByteCount < 10 && overallEntropy > 3.0)
            {
                anomalies.Add(new EntropyAnomaly
                {
                    Type = EntropyAnomalyType.UnusualByteDistribution,
                    Offset = 0,
                    Size = content.Length,
                    Description = $"Unusual entropy pattern: only {histogram.UniqueByteCount} unique byte values but entropy is {overallEntropy:F2}",
                    Severity = AnomalySeverity.Low,
                    Confidence = 0.6
                });
            }

            // Check for periodic patterns (might indicate padding or specific encryption modes)
            if (content.Length > 1024)
            {
                var periodicAnomaly = DetectPeriodicPatterns(content);
                if (periodicAnomaly != null)
                {
                    anomalies.Add(periodicAnomaly);
                }
            }

            return anomalies;
        }

        private EntropyAnomaly? DetectPeriodicPatterns(byte[] content)
        {
            // Check for periodic patterns by analyzing autocorrelation at various offsets
            var sampleSize = Math.Min(content.Length, 4096);
            var testOffsets = new[] { 16, 32, 64, 128, 256 };

            foreach (var offset in testOffsets)
            {
                if (sampleSize < offset * 4) continue;

                int matches = 0;
                int comparisons = Math.Min(256, sampleSize / offset - 1);

                for (int i = 0; i < comparisons; i++)
                {
                    if (content[i * offset] == content[(i + 1) * offset])
                        matches++;
                }

                var matchRatio = (double)matches / comparisons;
                if (matchRatio > 0.8)
                {
                    return new EntropyAnomaly
                    {
                        Type = EntropyAnomalyType.PeriodicPattern,
                        Offset = 0,
                        Size = content.Length,
                        Description = $"Periodic pattern detected with period {offset} bytes ({matchRatio * 100:F0}% correlation)",
                        Severity = AnomalySeverity.Low,
                        Confidence = matchRatio
                    };
                }
            }

            return null;
        }

        #endregion

        #region IThreatDetectionProvider Implementation

        /// <inheritdoc />
        public override async Task<ThreatScanResult> ScanAsync(
            Stream data,
            string? filename = null,
            ScanOptions? options = null,
            CancellationToken ct = default)
        {
            var startTime = DateTime.UtcNow;
            options ??= new ScanOptions();
            var threats = new List<DetectedThreat>();
            var highestSeverity = ThreatSeverity.None;

            try
            {
                // Perform entropy analysis
                var profile = await AnalyzeEntropyAsync(data, new EntropyAnalysisOptions
                {
                    EnableRollingAnalysis = true,
                    EnableBlockAnalysis = options.DeepScan
                }, ct);

                // Check for high entropy (potential encryption/ransomware)
                if (profile.IsHighEntropy)
                {
                    var severity = profile.NormalizedEntropy >= 0.99
                        ? ThreatSeverity.Critical
                        : profile.NormalizedEntropy >= 0.975
                            ? ThreatSeverity.High
                            : ThreatSeverity.Medium;

                    threats.Add(new DetectedThreat
                    {
                        ThreatType = "Ransomware",
                        Name = "High Entropy Content",
                        Severity = severity,
                        Confidence = profile.NormalizedEntropy,
                        Description = $"Content exhibits high entropy ({profile.OverallEntropy:F2} bits/byte, " +
                                    $"normalized: {profile.NormalizedEntropy:F4}). This strongly indicates " +
                                    "encryption and is a common ransomware indicator."
                    });

                    if (severity > highestSeverity) highestSeverity = severity;
                }

                // Check for partial encryption pattern
                if (profile.Classification.Category == FileEntropyCategory.PartiallyEncrypted)
                {
                    threats.Add(new DetectedThreat
                    {
                        ThreatType = "Ransomware",
                        Name = "Partial Encryption Detected",
                        Severity = ThreatSeverity.High,
                        Confidence = 0.8,
                        Description = $"Content shows mixed entropy pattern with {profile.HighEntropyRegions.Count} " +
                                    "high-entropy regions. This may indicate partial encryption by ransomware."
                    });

                    if (ThreatSeverity.High > highestSeverity) highestSeverity = ThreatSeverity.High;
                }

                // Check for entropy anomalies
                foreach (var anomaly in profile.Anomalies.Where(a => a.Severity >= AnomalySeverity.Medium))
                {
                    var threatSeverity = anomaly.Severity == AnomalySeverity.High
                        ? ThreatSeverity.High
                        : ThreatSeverity.Medium;

                    threats.Add(new DetectedThreat
                    {
                        ThreatType = "Anomaly",
                        Name = anomaly.Type.ToString(),
                        Severity = threatSeverity,
                        Confidence = anomaly.Confidence,
                        Description = anomaly.Description,
                        Offset = anomaly.Offset
                    });

                    if (threatSeverity > highestSeverity) highestSeverity = threatSeverity;
                }

                // Check against baseline if available and filename provided
                if (filename != null && _baselines.ContainsKey(filename))
                {
                    var baselineResult = CompareToBaseline(filename,
                        await ReadStreamToArray(data),
                        profile.OverallEntropy,
                        _baselines[filename]);

                    if (baselineResult.HasDeviation && baselineResult.DeviationScore > 0.5)
                    {
                        threats.Add(new DetectedThreat
                        {
                            ThreatType = "Baseline Deviation",
                            Name = "Significant Entropy Change",
                            Severity = baselineResult.DeviationScore > 0.7 ? ThreatSeverity.High : ThreatSeverity.Medium,
                            Confidence = baselineResult.DeviationScore,
                            Description = baselineResult.Assessment ?? "Entropy significantly deviates from baseline"
                        });

                        if (baselineResult.DeviationScore > 0.7 && ThreatSeverity.High > highestSeverity)
                            highestSeverity = ThreatSeverity.High;
                    }
                }

                // Record scan
                RecordScan(threats.Count > 0);

                var duration = DateTime.UtcNow - startTime;
                return new ThreatScanResult
                {
                    IsThreatDetected = threats.Count > 0,
                    Severity = highestSeverity,
                    Threats = threats,
                    ScanDuration = duration,
                    Recommendation = GenerateScanRecommendation(threats, highestSeverity, profile)
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new ThreatScanResult
                {
                    IsThreatDetected = false,
                    Severity = ThreatSeverity.None,
                    Threats = Array.Empty<DetectedThreat>(),
                    ScanDuration = DateTime.UtcNow - startTime,
                    Recommendation = $"Entropy analysis failed: {ex.Message}"
                };
            }
        }

        /// <inheritdoc />
        public override async Task<AnomalyAnalysisResult> AnalyzeBehaviorAsync(
            BehaviorData behavior,
            CancellationToken ct = default)
        {
            var anomalies = new List<DetectedAnomaly>();
            double anomalyScore = 0;

            // Check for rapid entropy changes in modification history
            if (behavior.RecentModifications.Count > 1)
            {
                var entropyChanges = behavior.RecentModifications
                    .Select(m => m.EntropyDelta)
                    .ToList();

                var avgEntropyChange = entropyChanges.Average(Math.Abs);
                var maxEntropyChange = entropyChanges.Max(Math.Abs);

                if (maxEntropyChange > 2.0)
                {
                    anomalies.Add(new DetectedAnomaly
                    {
                        Type = "RapidEntropyChange",
                        Description = $"Large entropy change of {maxEntropyChange:F2} bits/byte detected in recent modifications",
                        Score = Math.Min(1.0, maxEntropyChange / 4.0),
                        DetectedAt = DateTime.UtcNow
                    });
                    anomalyScore += maxEntropyChange / 4.0;
                }

                if (avgEntropyChange > 1.0)
                {
                    anomalies.Add(new DetectedAnomaly
                    {
                        Type = "ConsistentEntropyIncrease",
                        Description = $"Consistent entropy increases averaging {avgEntropyChange:F2} bits/byte across modifications",
                        Score = Math.Min(1.0, avgEntropyChange / 3.0),
                        DetectedAt = DateTime.UtcNow
                    });
                    anomalyScore += avgEntropyChange / 3.0;
                }
            }

            // Check entropy history if available
            if (_entropyHistory.TryGetValue(behavior.ObjectId, out var history) && history.Entries.Count > 2)
            {
                var trend = history.CalculateTrend();
                if (trend > 0.5)
                {
                    anomalies.Add(new DetectedAnomaly
                    {
                        Type = "EntropyTrendIncrease",
                        Description = $"Consistent upward trend in entropy over time (trend score: {trend:F2})",
                        Score = trend,
                        DetectedAt = DateTime.UtcNow
                    });
                    anomalyScore += trend * 0.5;
                }
            }

            anomalyScore = Math.Min(1.0, anomalyScore);

            return new AnomalyAnalysisResult
            {
                IsAnomalous = anomalyScore > 0.3,
                AnomalyScore = anomalyScore,
                Anomalies = anomalies
            };
        }

        #endregion

        #region Visualization Support

        /// <summary>
        /// Generates entropy visualization data suitable for rendering as a heatmap or chart.
        /// </summary>
        /// <param name="data">The data to visualize.</param>
        /// <param name="resolution">Number of data points to generate.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Visualization data for entropy display.</returns>
        public async Task<EntropyVisualizationData> GenerateVisualizationDataAsync(
            Stream data,
            int resolution = 256,
            CancellationToken ct = default)
        {
            byte[] content;
            using (var ms = new MemoryStream())
            {
                await data.CopyToAsync(ms, ct);
                content = ms.ToArray();
            }

            if (content.Length == 0)
            {
                return new EntropyVisualizationData
                {
                    Points = Array.Empty<EntropyVisualizationPoint>(),
                    OverallEntropy = 0,
                    DataSize = 0
                };
            }

            // Calculate block size based on resolution
            var blockSize = Math.Max(1, content.Length / resolution);
            var actualResolution = (content.Length + blockSize - 1) / blockSize;

            var points = new EntropyVisualizationPoint[actualResolution];

            for (int i = 0; i < actualResolution; i++)
            {
                ct.ThrowIfCancellationRequested();

                int offset = i * blockSize;
                int size = Math.Min(blockSize, content.Length - offset);
                var blockData = content.AsSpan(offset, size);
                var entropy = CalculateShannonEntropy(blockData);
                var normalized = entropy / 8.0;

                points[i] = new EntropyVisualizationPoint
                {
                    Index = i,
                    Offset = offset,
                    OffsetPercent = (double)offset / content.Length * 100,
                    Entropy = entropy,
                    NormalizedEntropy = normalized,
                    Category = CategorizeEntropy(entropy),
                    Color = GetEntropyColor(normalized)
                };
            }

            var overallEntropy = CalculateShannonEntropy(content);

            return new EntropyVisualizationData
            {
                Points = points,
                OverallEntropy = overallEntropy,
                NormalizedOverallEntropy = overallEntropy / 8.0,
                DataSize = content.Length,
                Resolution = actualResolution,
                BlockSize = blockSize,
                MinEntropy = points.Min(p => p.Entropy),
                MaxEntropy = points.Max(p => p.Entropy),
                MeanEntropy = points.Average(p => p.Entropy),
                EntropyStdDev = CalculateStdDev(points.Select(p => p.Entropy))
            };
        }

        private static string GetEntropyColor(double normalizedEntropy)
        {
            // Return color code for visualization (green->yellow->orange->red scale)
            return normalizedEntropy switch
            {
                >= 0.95 => "#FF0000", // Red - maximum entropy (encrypted)
                >= 0.85 => "#FF4500", // OrangeRed - very high
                >= 0.75 => "#FFA500", // Orange - high (compressed)
                >= 0.60 => "#FFD700", // Gold - medium-high
                >= 0.45 => "#FFFF00", // Yellow - medium
                >= 0.30 => "#ADFF2F", // GreenYellow - medium-low
                >= 0.15 => "#7FFF00", // Chartreuse - low
                _ => "#00FF00"        // Green - very low (text)
            };
        }

        #endregion

        #region Statistics and Monitoring

        /// <inheritdoc />
        public override Task<ThreatStatistics> GetStatisticsAsync(CancellationToken ct = default)
        {
            lock (_statsLock)
            {
                return Task.FromResult(new ThreatStatistics
                {
                    TotalScans = _totalAnalyses,
                    ThreatsDetected = _highEntropyDetections,
                    AnomaliesDetected = _anomaliesDetected,
                    ThreatsByType = new Dictionary<string, long>
                    {
                        ["HighEntropy"] = _highEntropyDetections,
                        ["Anomaly"] = _anomaliesDetected
                    },
                    LastScanTime = _lastAnalysisTime
                });
            }
        }

        /// <summary>
        /// Gets detailed entropy analysis statistics.
        /// </summary>
        /// <returns>Entropy-specific statistics.</returns>
        public EntropyStatistics GetEntropyStatistics()
        {
            lock (_statsLock)
            {
                return new EntropyStatistics
                {
                    TotalAnalyses = _totalAnalyses,
                    HighEntropyDetections = _highEntropyDetections,
                    AnomaliesDetected = _anomaliesDetected,
                    BaselinesRegistered = _baselines.Count,
                    ObjectsTracked = _entropyHistory.Count,
                    LastAnalysisTime = _lastAnalysisTime,
                    Configuration = new EntropyConfigurationSnapshot
                    {
                        HighEntropyThreshold = _config.HighEntropyThreshold,
                        SuspiciousEntropyThreshold = _config.SuspiciousEntropyThreshold,
                        DefaultWindowSize = _config.DefaultWindowSize,
                        DefaultBlockSize = _config.DefaultBlockSize
                    }
                };
            }
        }

        private void RecordAnalysis(bool isHighEntropy, bool hasAnomalies)
        {
            lock (_statsLock)
            {
                _totalAnalyses++;
                if (isHighEntropy) _highEntropyDetections++;
                if (hasAnomalies) _anomaliesDetected++;
                _lastAnalysisTime = DateTime.UtcNow;
            }
        }

        #endregion

        #region Plugin Lifecycle

        /// <inheritdoc />
        public override Task StartAsync(CancellationToken ct)
        {
            // Plugin is ready to use
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public override Task StopAsync()
        {
            // Clear any resources
            _baselines.Clear();
            _entropyHistory.Clear();
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>
            {
                new() { Name = "entropy.calculate", DisplayName = "Calculate Entropy", Description = "Calculate Shannon entropy for data" },
                new() { Name = "entropy.analyze", DisplayName = "Analyze Entropy", Description = "Perform comprehensive entropy analysis" },
                new() { Name = "entropy.rolling", DisplayName = "Rolling Entropy", Description = "Calculate rolling window entropy" },
                new() { Name = "entropy.blocks", DisplayName = "Block Entropy", Description = "Calculate block-level entropy" },
                new() { Name = "entropy.classify", DisplayName = "Classify by Entropy", Description = "Classify file type based on entropy signature" },
                new() { Name = "entropy.baseline", DisplayName = "Baseline Management", Description = "Register and compare entropy baselines" },
                new() { Name = "entropy.anomaly", DisplayName = "Anomaly Detection", Description = "Detect entropy-based anomalies" },
                new() { Name = "entropy.visualize", DisplayName = "Visualization Data", Description = "Generate entropy visualization data" },
                new() { Name = "entropy.histogram", DisplayName = "Entropy Histogram", Description = "Generate byte frequency histogram" }
            };
        }

        /// <inheritdoc />
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["HighEntropyThreshold"] = _config.HighEntropyThreshold;
            metadata["SuspiciousEntropyThreshold"] = _config.SuspiciousEntropyThreshold;
            metadata["DefaultWindowSize"] = _config.DefaultWindowSize;
            metadata["DefaultBlockSize"] = _config.DefaultBlockSize;
            metadata["FileTypeSignatures"] = _fileTypeSignatures.Count;
            metadata["SupportsVisualization"] = true;
            metadata["SupportsBaselines"] = true;
            metadata["SupportsHistograms"] = true;
            return metadata;
        }

        #endregion

        #region Helper Methods

        private void InitializeFileTypeSignatures()
        {
            // Encrypted files - maximum entropy
            _fileTypeSignatures.Add(new FileTypeSignature
            {
                FileType = "encrypted",
                Category = FileEntropyCategory.Encrypted,
                MinEntropy = 7.9,
                MaxEntropy = 8.0,
                RequiresUniformEntropy = true,
                MaxEntropyVariance = 0.02,
                ConfidenceWhenMatched = 0.95,
                Description = "Encrypted data with maximum entropy"
            });

            // Compressed archives
            _fileTypeSignatures.Add(new FileTypeSignature
            {
                FileType = "zip",
                Category = FileEntropyCategory.Compressed,
                MinEntropy = 7.5,
                MaxEntropy = 8.0,
                MagicBytes = new byte[] { 0x50, 0x4B, 0x03, 0x04 },
                ConfidenceWhenMatched = 0.9,
                Description = "ZIP archive"
            });

            _fileTypeSignatures.Add(new FileTypeSignature
            {
                FileType = "gzip",
                Category = FileEntropyCategory.Compressed,
                MinEntropy = 7.5,
                MaxEntropy = 8.0,
                MagicBytes = new byte[] { 0x1F, 0x8B },
                ConfidenceWhenMatched = 0.9,
                Description = "GZIP compressed data"
            });

            // JPEG - compressed media
            _fileTypeSignatures.Add(new FileTypeSignature
            {
                FileType = "jpeg",
                Category = FileEntropyCategory.Compressed,
                MinEntropy = 7.4,
                MaxEntropy = 8.0,
                MagicBytes = new byte[] { 0xFF, 0xD8, 0xFF },
                ConfidenceWhenMatched = 0.85,
                Description = "JPEG image"
            });

            // PNG - compressed but with header
            _fileTypeSignatures.Add(new FileTypeSignature
            {
                FileType = "png",
                Category = FileEntropyCategory.Compressed,
                MinEntropy = 6.0,
                MaxEntropy = 8.0,
                MagicBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },
                ConfidenceWhenMatched = 0.85,
                Description = "PNG image"
            });

            // PDF - mixed content
            _fileTypeSignatures.Add(new FileTypeSignature
            {
                FileType = "pdf",
                Category = FileEntropyCategory.Binary,
                MinEntropy = 5.0,
                MaxEntropy = 7.5,
                MagicBytes = new byte[] { 0x25, 0x50, 0x44, 0x46 },
                ConfidenceWhenMatched = 0.8,
                Description = "PDF document"
            });

            // Windows executable
            _fileTypeSignatures.Add(new FileTypeSignature
            {
                FileType = "exe",
                Category = FileEntropyCategory.Binary,
                MinEntropy = 5.0,
                MaxEntropy = 7.0,
                MagicBytes = new byte[] { 0x4D, 0x5A },
                ConfidenceWhenMatched = 0.75,
                Description = "Windows PE executable"
            });
        }

        private static EntropyCategory CategorizeEntropy(double entropy)
        {
            var normalized = entropy / 8.0;
            return normalized switch
            {
                >= 0.95 => EntropyCategory.Maximum,
                >= 0.85 => EntropyCategory.VeryHigh,
                >= 0.70 => EntropyCategory.High,
                >= 0.50 => EntropyCategory.Medium,
                >= 0.30 => EntropyCategory.Low,
                _ => EntropyCategory.VeryLow
            };
        }

        private EntropyProfile CreateEmptyProfile(DateTime startTime)
        {
            return new EntropyProfile
            {
                OverallEntropy = 0,
                NormalizedEntropy = 0,
                DataSize = 0,
                IsHighEntropy = false,
                IsSuspicious = false,
                Classification = new EntropyClassification
                {
                    Category = FileEntropyCategory.Unknown,
                    FileType = "empty",
                    Confidence = 1.0,
                    Description = "Empty data"
                },
                WindowAnalysis = Array.Empty<WindowEntropyPoint>(),
                BlockAnalysis = Array.Empty<BlockEntropyData>(),
                Anomalies = new List<EntropyAnomaly>(),
                HighEntropyRegions = new List<EntropyRegion>(),
                LowEntropyRegions = new List<EntropyRegion>(),
                EntropyVariance = 0,
                Assessment = "Empty data - no entropy analysis possible",
                AnalysisDuration = DateTime.UtcNow - startTime
            };
        }

        private List<EntropyRegion> IdentifyHighEntropyRegions(IReadOnlyList<WindowEntropyPoint> windows)
        {
            var regions = new List<EntropyRegion>();
            if (windows.Count == 0) return regions;

            long? regionStart = null;
            long regionSize = 0;
            double entropySum = 0;
            int windowCount = 0;

            foreach (var window in windows)
            {
                if (window.IsHighEntropy)
                {
                    if (regionStart == null)
                    {
                        regionStart = window.Offset;
                        regionSize = 0;
                        entropySum = 0;
                        windowCount = 0;
                    }
                    regionSize = window.Offset + window.Size - regionStart.Value;
                    entropySum += window.Entropy;
                    windowCount++;
                }
                else if (regionStart != null)
                {
                    regions.Add(new EntropyRegion
                    {
                        Offset = regionStart.Value,
                        Size = regionSize,
                        AverageEntropy = entropySum / windowCount,
                        Category = EntropyCategory.VeryHigh
                    });
                    regionStart = null;
                }
            }

            // Handle final region
            if (regionStart != null)
            {
                regions.Add(new EntropyRegion
                {
                    Offset = regionStart.Value,
                    Size = regionSize,
                    AverageEntropy = entropySum / windowCount,
                    Category = EntropyCategory.VeryHigh
                });
            }

            return regions;
        }

        private List<EntropyRegion> IdentifyLowEntropyRegions(IReadOnlyList<WindowEntropyPoint> windows)
        {
            var regions = new List<EntropyRegion>();
            if (windows.Count == 0) return regions;

            long? regionStart = null;
            long regionSize = 0;
            double entropySum = 0;
            int windowCount = 0;
            var lowThreshold = 0.5; // Normalized

            foreach (var window in windows)
            {
                if (window.NormalizedEntropy < lowThreshold)
                {
                    if (regionStart == null)
                    {
                        regionStart = window.Offset;
                        regionSize = 0;
                        entropySum = 0;
                        windowCount = 0;
                    }
                    regionSize = window.Offset + window.Size - regionStart.Value;
                    entropySum += window.Entropy;
                    windowCount++;
                }
                else if (regionStart != null)
                {
                    regions.Add(new EntropyRegion
                    {
                        Offset = regionStart.Value,
                        Size = regionSize,
                        AverageEntropy = entropySum / windowCount,
                        Category = EntropyCategory.Low
                    });
                    regionStart = null;
                }
            }

            if (regionStart != null)
            {
                regions.Add(new EntropyRegion
                {
                    Offset = regionStart.Value,
                    Size = regionSize,
                    AverageEntropy = entropySum / windowCount,
                    Category = EntropyCategory.Low
                });
            }

            return regions;
        }

        private static double CalculateEntropyVariance(IReadOnlyList<WindowEntropyPoint> windows)
        {
            if (windows.Count < 2) return 0;
            return CalculateVariance(windows.Select(w => w.Entropy));
        }

        private static double CalculateVariance(IEnumerable<double> values)
        {
            var list = values.ToList();
            if (list.Count < 2) return 0;

            var mean = list.Average();
            return list.Sum(v => (v - mean) * (v - mean)) / (list.Count - 1);
        }

        private static double CalculateStdDev(IEnumerable<double> values)
        {
            return Math.Sqrt(CalculateVariance(values));
        }

        private string GenerateEntropyAssessment(double entropy, double normalized, bool isHigh)
        {
            if (isHigh)
            {
                return normalized >= 0.99
                    ? "CRITICAL: Maximum entropy detected - content is almost certainly encrypted. This is a strong ransomware indicator."
                    : "WARNING: Very high entropy detected - content appears to be encrypted or heavily compressed.";
            }

            if (normalized >= (_config.SuspiciousEntropyThreshold / 8.0))
            {
                return "ELEVATED: Entropy is above normal thresholds. Content may be compressed or partially encrypted.";
            }

            if (normalized >= 0.6)
            {
                return "NORMAL: Entropy is within expected range for binary or compressed content.";
            }

            if (normalized >= 0.4)
            {
                return "NORMAL: Entropy is typical for structured text or code files.";
            }

            return "LOW: Entropy indicates plain text or repetitive data content.";
        }

        private string GenerateDetailedAssessment(
            double entropy,
            double normalized,
            EntropyClassification classification,
            IReadOnlyList<EntropyAnomaly> anomalies)
        {
            var sb = new StringBuilder();

            sb.AppendLine($"Entropy: {entropy:F3} bits/byte (normalized: {normalized:F4})");
            sb.AppendLine($"Classification: {classification.Category} ({classification.FileType})");
            sb.AppendLine($"Confidence: {classification.Confidence:P0}");

            if (anomalies.Count > 0)
            {
                sb.AppendLine($"Anomalies detected: {anomalies.Count}");
                foreach (var anomaly in anomalies.Take(3))
                {
                    sb.AppendLine($"  - {anomaly.Type}: {anomaly.Description}");
                }
            }

            if (normalized >= 0.95)
            {
                sb.AppendLine();
                sb.AppendLine("ALERT: Extremely high entropy strongly suggests encryption. " +
                            "This is a primary indicator of ransomware activity.");
            }

            return sb.ToString();
        }

        private string GenerateBaselineAssessment(
            EntropyBaseline baseline,
            EntropyProfile current,
            IReadOnlyList<EntropyChange> changes,
            double deviationScore)
        {
            if (changes.Count == 0)
            {
                return "OK: Content entropy is within expected parameters of the baseline.";
            }

            var sb = new StringBuilder();

            if (deviationScore > 0.7)
            {
                sb.AppendLine("CRITICAL: Major entropy deviation from baseline detected.");
                sb.AppendLine("This may indicate encryption or significant content modification.");
            }
            else if (deviationScore > 0.4)
            {
                sb.AppendLine("WARNING: Significant entropy changes detected from baseline.");
            }
            else
            {
                sb.AppendLine("NOTICE: Minor entropy deviations from baseline.");
            }

            foreach (var change in changes)
            {
                sb.AppendLine($"  - [{change.Severity}] {change.Description}");
            }

            return sb.ToString();
        }

        private string? GenerateScanRecommendation(
            IReadOnlyList<DetectedThreat> threats,
            ThreatSeverity severity,
            EntropyProfile profile)
        {
            if (threats.Count == 0)
            {
                return "No high-entropy threats detected. Content appears normal.";
            }

            return severity switch
            {
                ThreatSeverity.Critical =>
                    "CRITICAL: Maximum entropy detected. Immediately isolate this content and investigate for ransomware. " +
                    "Check for associated ransom notes and verify backup integrity.",
                ThreatSeverity.High =>
                    "HIGH: Very high entropy content detected. This may be encrypted or ransomware-affected data. " +
                    "Review the content source and check for unauthorized encryption.",
                ThreatSeverity.Medium =>
                    "MEDIUM: Elevated entropy patterns detected. While this may be normal for compressed files, " +
                    "verify that encryption is authorized if present.",
                _ => "LOW: Minor entropy anomalies detected. Monitor for changes over time."
            };
        }

        private static async Task<byte[]> ReadStreamToArray(Stream stream)
        {
            if (stream.CanSeek)
            {
                stream.Position = 0;
            }
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            return ms.ToArray();
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// Configuration for entropy analysis.
    /// </summary>
    public class EntropyAnalysisConfig
    {
        /// <summary>
        /// Threshold for high entropy detection (0-8 scale).
        /// Default is 7.9.
        /// </summary>
        public double HighEntropyThreshold { get; set; } = 7.9;

        /// <summary>
        /// Threshold for suspicious entropy detection.
        /// Default is 7.5.
        /// </summary>
        public double SuspiciousEntropyThreshold { get; set; } = 7.5;

        /// <summary>
        /// Default window size for rolling analysis in bytes.
        /// </summary>
        public int DefaultWindowSize { get; set; } = 4096;

        /// <summary>
        /// Default window step for rolling analysis in bytes.
        /// </summary>
        public int DefaultWindowStep { get; set; } = 1024;

        /// <summary>
        /// Default block size for block-level analysis in bytes.
        /// </summary>
        public int DefaultBlockSize { get; set; } = 256;

        /// <summary>
        /// Whether to track entropy history for trend analysis.
        /// </summary>
        public bool EnableHistoryTracking { get; set; } = true;

        /// <summary>
        /// Maximum number of history entries per object.
        /// </summary>
        public int MaxHistoryEntries { get; set; } = 100;
    }

    /// <summary>
    /// Options for entropy analysis operations.
    /// </summary>
    public class EntropyAnalysisOptions
    {
        /// <summary>Enable rolling window analysis.</summary>
        public bool EnableRollingAnalysis { get; set; } = true;

        /// <summary>Enable block-level analysis for visualization.</summary>
        public bool EnableBlockAnalysis { get; set; } = false;

        /// <summary>Generate byte frequency histogram.</summary>
        public bool GenerateHistogram { get; set; } = false;

        /// <summary>Window size for rolling analysis.</summary>
        public int WindowSize { get; set; } = 4096;

        /// <summary>Window step size.</summary>
        public int WindowStep { get; set; } = 1024;

        /// <summary>Block size for block analysis.</summary>
        public int BlockSize { get; set; } = 256;
    }

    /// <summary>
    /// Comprehensive entropy profile for analyzed content.
    /// </summary>
    public class EntropyProfile
    {
        /// <summary>Overall Shannon entropy (0-8 scale).</summary>
        public double OverallEntropy { get; init; }

        /// <summary>Normalized entropy (0-1 scale).</summary>
        public double NormalizedEntropy { get; init; }

        /// <summary>Size of analyzed data in bytes.</summary>
        public long DataSize { get; init; }

        /// <summary>Whether entropy exceeds high threshold.</summary>
        public bool IsHighEntropy { get; init; }

        /// <summary>Whether entropy exceeds suspicious threshold.</summary>
        public bool IsSuspicious { get; init; }

        /// <summary>File type classification based on entropy.</summary>
        public EntropyClassification Classification { get; init; } = new();

        /// <summary>Rolling window analysis results.</summary>
        public IReadOnlyList<WindowEntropyPoint> WindowAnalysis { get; init; } = Array.Empty<WindowEntropyPoint>();

        /// <summary>Block-level analysis results.</summary>
        public IReadOnlyList<BlockEntropyData> BlockAnalysis { get; init; } = Array.Empty<BlockEntropyData>();

        /// <summary>Byte frequency histogram.</summary>
        public EntropyHistogram? Histogram { get; init; }

        /// <summary>Detected entropy anomalies.</summary>
        public IReadOnlyList<EntropyAnomaly> Anomalies { get; init; } = Array.Empty<EntropyAnomaly>();

        /// <summary>Regions with high entropy.</summary>
        public IReadOnlyList<EntropyRegion> HighEntropyRegions { get; init; } = Array.Empty<EntropyRegion>();

        /// <summary>Regions with low entropy.</summary>
        public IReadOnlyList<EntropyRegion> LowEntropyRegions { get; init; } = Array.Empty<EntropyRegion>();

        /// <summary>Variance of entropy across windows.</summary>
        public double EntropyVariance { get; init; }

        /// <summary>Human-readable assessment.</summary>
        public string? Assessment { get; init; }

        /// <summary>Time taken for analysis.</summary>
        public TimeSpan AnalysisDuration { get; init; }
    }

    /// <summary>
    /// Entropy data point from rolling window analysis.
    /// </summary>
    public class WindowEntropyPoint
    {
        /// <summary>Byte offset in the data.</summary>
        public long Offset { get; init; }

        /// <summary>Size of the window in bytes.</summary>
        public int Size { get; init; }

        /// <summary>Entropy of this window (0-8).</summary>
        public double Entropy { get; init; }

        /// <summary>Normalized entropy (0-1).</summary>
        public double NormalizedEntropy { get; init; }

        /// <summary>Whether this window has high entropy.</summary>
        public bool IsHighEntropy { get; init; }
    }

    /// <summary>
    /// Entropy data for a single block.
    /// </summary>
    public class BlockEntropyData
    {
        /// <summary>Block index.</summary>
        public int BlockIndex { get; init; }

        /// <summary>Byte offset.</summary>
        public long Offset { get; init; }

        /// <summary>Block size.</summary>
        public int Size { get; init; }

        /// <summary>Block entropy (0-8).</summary>
        public double Entropy { get; init; }

        /// <summary>Normalized entropy (0-1).</summary>
        public double NormalizedEntropy { get; init; }

        /// <summary>Entropy category.</summary>
        public EntropyCategory EntropyCategory { get; init; }
    }

    /// <summary>
    /// Entropy histogram showing byte distribution.
    /// </summary>
    public class EntropyHistogram
    {
        /// <summary>Frequency count for each byte value (0-255).</summary>
        public long[] ByteFrequencies { get; init; } = new long[256];

        /// <summary>Probability for each byte value.</summary>
        public double[] ByteProbabilities { get; init; } = new double[256];

        /// <summary>Total bytes analyzed.</summary>
        public long TotalBytes { get; init; }

        /// <summary>Count of unique byte values present.</summary>
        public int UniqueByteCount { get; init; }

        /// <summary>Most frequently occurring byte.</summary>
        public byte MostFrequentByte { get; init; }

        /// <summary>Least frequently occurring byte (among those present).</summary>
        public byte LeastFrequentByte { get; init; }

        /// <summary>Maximum frequency.</summary>
        public long MaxFrequency { get; init; }

        /// <summary>Minimum frequency (among present bytes).</summary>
        public long MinFrequency { get; init; }

        /// <summary>Uniformity score (0-1, 1 = perfectly uniform).</summary>
        public double Uniformity { get; init; }
    }

    /// <summary>
    /// Classification of content based on entropy characteristics.
    /// </summary>
    public class EntropyClassification
    {
        /// <summary>General category.</summary>
        public FileEntropyCategory Category { get; init; }

        /// <summary>Specific file type guess.</summary>
        public string FileType { get; init; } = "unknown";

        /// <summary>Confidence in the classification.</summary>
        public double Confidence { get; init; }

        /// <summary>Description of the classification.</summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>Expected entropy range for this classification.</summary>
        public (double Min, double Max) ExpectedEntropyRange { get; init; }

        /// <summary>Whether actual entropy is within expected range.</summary>
        public bool IsWithinExpectedRange { get; init; }
    }

    /// <summary>
    /// File type signature based on entropy characteristics.
    /// </summary>
    public class FileTypeSignature
    {
        /// <summary>File type identifier.</summary>
        public string FileType { get; init; } = string.Empty;

        /// <summary>Entropy category.</summary>
        public FileEntropyCategory Category { get; init; }

        /// <summary>Minimum expected entropy.</summary>
        public double MinEntropy { get; init; }

        /// <summary>Maximum expected entropy.</summary>
        public double MaxEntropy { get; init; }

        /// <summary>Magic bytes for additional verification.</summary>
        public byte[]? MagicBytes { get; init; }

        /// <summary>Offset of magic bytes.</summary>
        public int MagicBytesOffset { get; init; }

        /// <summary>Whether uniform entropy is required.</summary>
        public bool RequiresUniformEntropy { get; init; }

        /// <summary>Maximum allowed entropy variance.</summary>
        public double MaxEntropyVariance { get; init; } = 1.0;

        /// <summary>Confidence when signature matches.</summary>
        public double ConfidenceWhenMatched { get; init; } = 0.8;

        /// <summary>Description of the file type.</summary>
        public string Description { get; init; } = string.Empty;
    }

    /// <summary>
    /// Detected entropy-based anomaly.
    /// </summary>
    public class EntropyAnomaly
    {
        /// <summary>Type of anomaly.</summary>
        public EntropyAnomalyType Type { get; init; }

        /// <summary>Byte offset where anomaly starts.</summary>
        public long Offset { get; init; }

        /// <summary>Size of anomalous region.</summary>
        public long Size { get; init; }

        /// <summary>Description of the anomaly.</summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>Severity of the anomaly.</summary>
        public AnomalySeverity Severity { get; init; }

        /// <summary>Confidence in detection.</summary>
        public double Confidence { get; init; }

        /// <summary>Entropy before the anomaly point.</summary>
        public double EntropyBefore { get; init; }

        /// <summary>Entropy after the anomaly point.</summary>
        public double EntropyAfter { get; init; }
    }

    /// <summary>
    /// A region with consistent entropy characteristics.
    /// </summary>
    public class EntropyRegion
    {
        /// <summary>Start offset.</summary>
        public long Offset { get; init; }

        /// <summary>Region size.</summary>
        public long Size { get; init; }

        /// <summary>Average entropy in the region.</summary>
        public double AverageEntropy { get; init; }

        /// <summary>Entropy category.</summary>
        public EntropyCategory Category { get; init; }
    }

    /// <summary>
    /// Entropy baseline for comparison.
    /// </summary>
    public class EntropyBaseline
    {
        /// <summary>Object identifier.</summary>
        public string ObjectId { get; init; } = string.Empty;

        /// <summary>Baseline entropy value.</summary>
        public double Entropy { get; init; }

        /// <summary>Normalized entropy.</summary>
        public double NormalizedEntropy { get; init; }

        /// <summary>Content size.</summary>
        public long Size { get; init; }

        /// <summary>Content hash.</summary>
        public string ContentHash { get; init; } = string.Empty;

        /// <summary>Content type if known.</summary>
        public string? ContentType { get; init; }

        /// <summary>Classification at baseline time.</summary>
        public EntropyClassification? Classification { get; init; }

        /// <summary>Entropy variance at baseline.</summary>
        public double EntropyVariance { get; init; }

        /// <summary>Number of high entropy regions.</summary>
        public int HighEntropyRegionCount { get; init; }

        /// <summary>When baseline was created.</summary>
        public DateTime CreatedAt { get; init; }

        /// <summary>Additional features.</summary>
        public Dictionary<string, double> Features { get; init; } = new();
    }

    /// <summary>
    /// Result of baseline comparison.
    /// </summary>
    public class EntropyBaselineComparison
    {
        /// <summary>Object identifier.</summary>
        public string ObjectId { get; init; } = string.Empty;

        /// <summary>Whether a baseline exists.</summary>
        public bool HasBaseline { get; init; }

        /// <summary>Baseline entropy value.</summary>
        public double BaselineEntropy { get; init; }

        /// <summary>Current entropy value.</summary>
        public double CurrentEntropy { get; init; }

        /// <summary>Absolute entropy change.</summary>
        public double EntropyDelta { get; init; }

        /// <summary>Normalized entropy change.</summary>
        public double NormalizedEntropyDelta { get; init; }

        /// <summary>Size change in bytes.</summary>
        public long SizeDelta { get; init; }

        /// <summary>Size change percentage.</summary>
        public double SizeChangePercent { get; init; }

        /// <summary>List of detected changes.</summary>
        public IReadOnlyList<EntropyChange> Changes { get; init; } = Array.Empty<EntropyChange>();

        /// <summary>Overall deviation score (0-1).</summary>
        public double DeviationScore { get; init; }

        /// <summary>Whether deviation is significant.</summary>
        public bool HasSignificantDeviation { get; init; }

        /// <summary>Current entropy profile.</summary>
        public EntropyProfile? CurrentProfile { get; init; }

        /// <summary>Assessment text.</summary>
        public string? Assessment { get; init; }
    }

    /// <summary>
    /// A specific change detected from baseline.
    /// </summary>
    public class EntropyChange
    {
        /// <summary>Type of change.</summary>
        public EntropyChangeType ChangeType { get; init; }

        /// <summary>Description of the change.</summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>Severity of the change.</summary>
        public ChangeSeverity Severity { get; init; }

        /// <summary>Impact score (0-1).</summary>
        public double Impact { get; init; }
    }

    /// <summary>
    /// Entropy history tracking for an object.
    /// </summary>
    internal class EntropyHistory
    {
        public string ObjectId { get; init; } = string.Empty;
        public List<EntropyHistoryEntry> Entries { get; } = new();
        private readonly object _lock = new();
        private const int MaxEntries = 100;

        public void AddEntry(double entropy, double normalizedEntropy)
        {
            lock (_lock)
            {
                Entries.Add(new EntropyHistoryEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Entropy = entropy,
                    NormalizedEntropy = normalizedEntropy
                });

                while (Entries.Count > MaxEntries)
                {
                    Entries.RemoveAt(0);
                }
            }
        }

        public double CalculateTrend()
        {
            lock (_lock)
            {
                if (Entries.Count < 3) return 0;

                // Simple linear regression on normalized entropy
                var n = Entries.Count;
                var sumX = 0.0;
                var sumY = 0.0;
                var sumXY = 0.0;
                var sumX2 = 0.0;

                for (int i = 0; i < n; i++)
                {
                    sumX += i;
                    sumY += Entries[i].NormalizedEntropy;
                    sumXY += i * Entries[i].NormalizedEntropy;
                    sumX2 += i * i;
                }

                var slope = (n * sumXY - sumX * sumY) / (n * sumX2 - sumX * sumX);
                return Math.Min(1.0, Math.Max(-1.0, slope * 10)); // Scale slope to -1 to 1
            }
        }
    }

    /// <summary>
    /// Single entry in entropy history.
    /// </summary>
    internal class EntropyHistoryEntry
    {
        public DateTime Timestamp { get; init; }
        public double Entropy { get; init; }
        public double NormalizedEntropy { get; init; }
    }

    /// <summary>
    /// Data for entropy visualization.
    /// </summary>
    public class EntropyVisualizationData
    {
        /// <summary>Entropy data points for visualization.</summary>
        public IReadOnlyList<EntropyVisualizationPoint> Points { get; init; } = Array.Empty<EntropyVisualizationPoint>();

        /// <summary>Overall entropy.</summary>
        public double OverallEntropy { get; init; }

        /// <summary>Normalized overall entropy.</summary>
        public double NormalizedOverallEntropy { get; init; }

        /// <summary>Data size in bytes.</summary>
        public long DataSize { get; init; }

        /// <summary>Number of points generated.</summary>
        public int Resolution { get; init; }

        /// <summary>Size of each block.</summary>
        public int BlockSize { get; init; }

        /// <summary>Minimum entropy in data.</summary>
        public double MinEntropy { get; init; }

        /// <summary>Maximum entropy in data.</summary>
        public double MaxEntropy { get; init; }

        /// <summary>Mean entropy.</summary>
        public double MeanEntropy { get; init; }

        /// <summary>Standard deviation of entropy.</summary>
        public double EntropyStdDev { get; init; }
    }

    /// <summary>
    /// Single point for entropy visualization.
    /// </summary>
    public class EntropyVisualizationPoint
    {
        /// <summary>Point index.</summary>
        public int Index { get; init; }

        /// <summary>Byte offset.</summary>
        public long Offset { get; init; }

        /// <summary>Offset as percentage of file.</summary>
        public double OffsetPercent { get; init; }

        /// <summary>Entropy value (0-8).</summary>
        public double Entropy { get; init; }

        /// <summary>Normalized entropy (0-1).</summary>
        public double NormalizedEntropy { get; init; }

        /// <summary>Entropy category.</summary>
        public EntropyCategory Category { get; init; }

        /// <summary>Suggested color for visualization.</summary>
        public string Color { get; init; } = "#000000";
    }

    /// <summary>
    /// Entropy analysis statistics.
    /// </summary>
    public class EntropyStatistics
    {
        /// <summary>Total analyses performed.</summary>
        public long TotalAnalyses { get; init; }

        /// <summary>High entropy detections.</summary>
        public long HighEntropyDetections { get; init; }

        /// <summary>Anomalies detected.</summary>
        public long AnomaliesDetected { get; init; }

        /// <summary>Number of registered baselines.</summary>
        public int BaselinesRegistered { get; init; }

        /// <summary>Number of objects being tracked.</summary>
        public int ObjectsTracked { get; init; }

        /// <summary>Last analysis time.</summary>
        public DateTime LastAnalysisTime { get; init; }

        /// <summary>Current configuration snapshot.</summary>
        public EntropyConfigurationSnapshot? Configuration { get; init; }
    }

    /// <summary>
    /// Snapshot of entropy configuration.
    /// </summary>
    public class EntropyConfigurationSnapshot
    {
        /// <summary>High entropy threshold.</summary>
        public double HighEntropyThreshold { get; init; }

        /// <summary>Suspicious entropy threshold.</summary>
        public double SuspiciousEntropyThreshold { get; init; }

        /// <summary>Default window size.</summary>
        public int DefaultWindowSize { get; init; }

        /// <summary>Default block size.</summary>
        public int DefaultBlockSize { get; init; }
    }

    #endregion

    #region Enumerations

    /// <summary>
    /// File entropy category based on content type.
    /// </summary>
    public enum FileEntropyCategory
    {
        /// <summary>Unknown content type.</summary>
        Unknown,

        /// <summary>Plain text or low-entropy content.</summary>
        PlainText,

        /// <summary>Structured data (code, markup, config).</summary>
        StructuredData,

        /// <summary>Binary executable or library.</summary>
        Binary,

        /// <summary>Compressed data (archives, media).</summary>
        Compressed,

        /// <summary>Encrypted data.</summary>
        Encrypted,

        /// <summary>Partially encrypted content.</summary>
        PartiallyEncrypted
    }

    /// <summary>
    /// Entropy level category.
    /// </summary>
    public enum EntropyCategory
    {
        /// <summary>Very low entropy (0-2.4).</summary>
        VeryLow,

        /// <summary>Low entropy (2.4-4.0).</summary>
        Low,

        /// <summary>Medium entropy (4.0-5.6).</summary>
        Medium,

        /// <summary>High entropy (5.6-6.8).</summary>
        High,

        /// <summary>Very high entropy (6.8-7.6).</summary>
        VeryHigh,

        /// <summary>Maximum entropy (7.6-8.0).</summary>
        Maximum
    }

    /// <summary>
    /// Types of entropy anomalies.
    /// </summary>
    public enum EntropyAnomalyType
    {
        /// <summary>Sudden increase in entropy.</summary>
        SuddenEntropySpike,

        /// <summary>Sudden decrease in entropy.</summary>
        SuddenEntropyDrop,

        /// <summary>High-entropy payload with low-entropy header.</summary>
        EncryptedPayload,

        /// <summary>Low-entropy payload with high-entropy header.</summary>
        EncryptedHeader,

        /// <summary>Unusual byte value distribution.</summary>
        UnusualByteDistribution,

        /// <summary>Periodic pattern in entropy.</summary>
        PeriodicPattern,

        /// <summary>Entropy inconsistent with file type.</summary>
        TypeMismatch
    }

    /// <summary>
    /// Severity of entropy anomalies.
    /// </summary>
    public enum AnomalySeverity
    {
        /// <summary>Low severity.</summary>
        Low,

        /// <summary>Medium severity.</summary>
        Medium,

        /// <summary>High severity.</summary>
        High,

        /// <summary>Critical severity.</summary>
        Critical
    }

    /// <summary>
    /// Types of entropy changes from baseline.
    /// </summary>
    public enum EntropyChangeType
    {
        /// <summary>Entropy increased.</summary>
        EntropyIncrease,

        /// <summary>Entropy decreased.</summary>
        EntropyDecrease,

        /// <summary>Size increased.</summary>
        SizeIncrease,

        /// <summary>Size decreased.</summary>
        SizeDecrease,

        /// <summary>Classification changed.</summary>
        ClassificationChange,

        /// <summary>Variance changed.</summary>
        VarianceChange
    }

    /// <summary>
    /// Severity of baseline changes.
    /// </summary>
    public enum ChangeSeverity
    {
        /// <summary>Low severity.</summary>
        Low,

        /// <summary>Medium severity.</summary>
        Medium,

        /// <summary>High severity.</summary>
        High,

        /// <summary>Critical severity.</summary>
        Critical
    }

    #endregion
}
