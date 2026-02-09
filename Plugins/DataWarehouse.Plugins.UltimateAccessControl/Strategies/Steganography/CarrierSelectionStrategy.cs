using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Steganography
{
    /// <summary>
    /// Carrier selection strategy for optimal steganographic carrier file selection.
    /// Implements T74.5 - Production-ready automatic carrier selection for data hiding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Selection criteria:
    /// - Capacity requirements based on payload size
    /// - Entropy and noise characteristics
    /// - Format suitability for embedding method
    /// - Detection resistance profile
    /// - Cover traffic analysis (natural media appearance)
    /// </para>
    /// <para>
    /// Carrier types evaluated:
    /// - Images: PNG, JPEG, BMP, TIFF, GIF
    /// - Audio: WAV, AIFF, MP3, FLAC
    /// - Video: AVI, MP4, MKV
    /// - Documents: PDF, DOCX
    /// - Archives: ZIP (comment fields)
    /// </para>
    /// </remarks>
    public sealed class CarrierSelectionStrategy : AccessControlStrategyBase
    {
        private byte[]? _encryptionKey;
        private CarrierSelectionMode _selectionMode = CarrierSelectionMode.Optimal;
        private double _minimumSuitabilityScore = 50.0;
        private double _capacityMarginRatio = 1.5; // Need 1.5x payload size capacity
        private bool _preferCommonFormats = true;
        private int _maxCandidates = 100;

        /// <inheritdoc/>
        public override string StrategyId => "carrier-selection";

        /// <inheritdoc/>
        public override string StrategyName => "Carrier Selection";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = false,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = false,
            SupportsTemporalAccess = false,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 50
        };

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("EncryptionKey", out var keyObj) && keyObj is byte[] key)
            {
                _encryptionKey = key;
            }
            else
            {
                _encryptionKey = new byte[32];
                RandomNumberGenerator.Fill(_encryptionKey);
            }

            if (configuration.TryGetValue("SelectionMode", out var modeObj) && modeObj is string mode)
            {
                _selectionMode = Enum.TryParse<CarrierSelectionMode>(mode, true, out var m)
                    ? m : CarrierSelectionMode.Optimal;
            }

            if (configuration.TryGetValue("MinimumSuitabilityScore", out var scoreObj) && scoreObj is double score)
            {
                _minimumSuitabilityScore = Math.Clamp(score, 0, 100);
            }

            if (configuration.TryGetValue("CapacityMarginRatio", out var marginObj) && marginObj is double margin)
            {
                _capacityMarginRatio = Math.Clamp(margin, 1.0, 5.0);
            }

            if (configuration.TryGetValue("PreferCommonFormats", out var prefObj) && prefObj is bool pref)
            {
                _preferCommonFormats = pref;
            }

            if (configuration.TryGetValue("MaxCandidates", out var maxObj) && maxObj is int max)
            {
                _maxCandidates = Math.Clamp(max, 10, 1000);
            }

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Selects the optimal carrier from a list of candidate files.
        /// </summary>
        /// <param name="candidates">List of candidate carrier files.</param>
        /// <param name="payloadSize">Size of the payload to hide.</param>
        /// <returns>The selected carrier with analysis.</returns>
        public CarrierSelectionResult SelectCarrier(IEnumerable<CarrierCandidate> candidates, long payloadSize)
        {
            var candidateList = candidates.Take(_maxCandidates).ToList();

            if (!candidateList.Any())
            {
                return new CarrierSelectionResult
                {
                    Success = false,
                    Reason = "No candidate carriers provided"
                };
            }

            var requiredCapacity = (long)(payloadSize * _capacityMarginRatio);
            var analyses = new List<CarrierEvaluation>();

            foreach (var candidate in candidateList)
            {
                var evaluation = EvaluateCarrier(candidate, payloadSize, requiredCapacity);
                if (evaluation.MeetsRequirements)
                {
                    analyses.Add(evaluation);
                }
            }

            if (!analyses.Any())
            {
                return new CarrierSelectionResult
                {
                    Success = false,
                    Reason = "No suitable carriers found meeting requirements",
                    RequiredCapacity = requiredCapacity,
                    CandidatesEvaluated = candidateList.Count
                };
            }

            // Select based on mode
            CarrierEvaluation selected = _selectionMode switch
            {
                CarrierSelectionMode.Optimal => SelectOptimal(analyses),
                CarrierSelectionMode.MaximumCapacity => SelectMaxCapacity(analyses),
                CarrierSelectionMode.MinimumDetection => SelectMinDetection(analyses),
                CarrierSelectionMode.Random => SelectRandom(analyses),
                CarrierSelectionMode.Balanced => SelectBalanced(analyses),
                _ => SelectOptimal(analyses)
            };

            return new CarrierSelectionResult
            {
                Success = true,
                SelectedCarrier = selected.Candidate,
                Evaluation = selected,
                RequiredCapacity = requiredCapacity,
                CandidatesEvaluated = candidateList.Count,
                SuitableCandidates = analyses.Count,
                SelectionMode = _selectionMode.ToString()
            };
        }

        /// <summary>
        /// Analyzes a single carrier file for steganographic suitability.
        /// </summary>
        public CarrierEvaluation AnalyzeCarrier(byte[] data, string fileName)
        {
            var candidate = new CarrierCandidate
            {
                FileName = fileName,
                Data = data,
                FileSize = data.Length
            };

            return EvaluateCarrier(candidate, 0, 0);
        }

        /// <summary>
        /// Recommends carriers for a given payload from a directory.
        /// </summary>
        public CarrierRecommendation RecommendCarriers(IEnumerable<CarrierCandidate> candidates, long payloadSize, int count = 5)
        {
            var candidateList = candidates.Take(_maxCandidates).ToList();
            var requiredCapacity = (long)(payloadSize * _capacityMarginRatio);
            var evaluations = new List<CarrierEvaluation>();

            foreach (var candidate in candidateList)
            {
                var evaluation = EvaluateCarrier(candidate, payloadSize, requiredCapacity);
                evaluations.Add(evaluation);
            }

            var suitable = evaluations
                .Where(e => e.MeetsRequirements)
                .OrderByDescending(e => e.OverallScore)
                .Take(count)
                .ToList();

            var unsuitable = evaluations
                .Where(e => !e.MeetsRequirements)
                .OrderByDescending(e => e.OverallScore)
                .Take(count)
                .ToList();

            return new CarrierRecommendation
            {
                PayloadSize = payloadSize,
                RequiredCapacity = requiredCapacity,
                TotalCandidates = candidateList.Count,
                SuitableCandidates = suitable.Count,
                TopRecommendations = suitable,
                NearMissCandidates = unsuitable,
                RecommendedFormat = DetermineRecommendedFormat(suitable),
                RecommendedMethod = DetermineRecommendedMethod(suitable)
            };
        }

        /// <summary>
        /// Calculates capacity requirements for a payload with specified redundancy.
        /// </summary>
        public CapacityRequirements CalculateRequirements(long payloadSize, EmbeddingRedundancy redundancy = EmbeddingRedundancy.Standard)
        {
            double redundancyFactor = redundancy switch
            {
                EmbeddingRedundancy.Minimal => 1.2,
                EmbeddingRedundancy.Standard => 1.5,
                EmbeddingRedundancy.High => 2.0,
                EmbeddingRedundancy.Maximum => 3.0,
                _ => 1.5
            };

            long headerOverhead = 64; // Standard header
            long encryptionOverhead = 32; // IV + padding
            long checksumOverhead = 32; // HMAC

            long baseRequired = payloadSize + headerOverhead + encryptionOverhead + checksumOverhead;
            long withRedundancy = (long)(baseRequired * redundancyFactor);

            return new CapacityRequirements
            {
                PayloadSize = payloadSize,
                HeaderOverhead = headerOverhead,
                EncryptionOverhead = encryptionOverhead,
                ChecksumOverhead = checksumOverhead,
                BaseCapacityNeeded = baseRequired,
                RedundancyFactor = redundancyFactor,
                TotalCapacityNeeded = withRedundancy,
                MinimumCarrierSize = EstimateMinimumCarrierSize(withRedundancy),
                RecommendedCarrierTypes = GetRecommendedCarrierTypes(withRedundancy)
            };
        }

        /// <inheritdoc/>
        protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            return Task.FromResult(new AccessDecision
            {
                IsGranted = true,
                Reason = "Carrier selection strategy does not perform access control - use for carrier selection",
                ApplicablePolicies = new[] { "Steganography.CarrierSelection" }
            });
        }

        private CarrierEvaluation EvaluateCarrier(CarrierCandidate candidate, long payloadSize, long requiredCapacity)
        {
            var format = DetectFormat(candidate.Data, candidate.FileName);
            var capacity = EstimateCapacity(candidate.Data, format);
            var entropy = CalculateEntropy(candidate.Data);
            var variance = CalculateVariance(candidate.Data);
            var formatPopularity = GetFormatPopularity(format);

            // Score components
            double capacityScore = requiredCapacity > 0
                ? Math.Min((double)capacity / requiredCapacity, 1.0) * 25
                : Math.Min((double)capacity / 10000, 1.0) * 25;

            double entropyScore = Math.Min(entropy / 8.0, 1.0) * 25;
            double varianceScore = Math.Min(variance / 50.0, 1.0) * 20;
            double popularityScore = _preferCommonFormats ? formatPopularity * 15 : 10;
            double sizeScore = Math.Min((double)candidate.FileSize / 1_000_000, 1.0) * 15;

            double overallScore = capacityScore + entropyScore + varianceScore + popularityScore + sizeScore;

            bool meetsRequirements = capacity >= requiredCapacity &&
                                    overallScore >= _minimumSuitabilityScore;

            string detectionRisk = overallScore >= 80 ? "Low" :
                                  overallScore >= 60 ? "Medium" : "High";

            return new CarrierEvaluation
            {
                Candidate = candidate,
                DetectedFormat = format,
                Capacity = capacity,
                RequiredCapacity = requiredCapacity,
                Entropy = entropy,
                Variance = variance,
                FormatPopularity = formatPopularity,
                CapacityScore = capacityScore,
                EntropyScore = entropyScore,
                VarianceScore = varianceScore,
                PopularityScore = popularityScore,
                SizeScore = sizeScore,
                OverallScore = overallScore,
                MeetsRequirements = meetsRequirements,
                DetectionRisk = detectionRisk,
                RecommendedMethod = RecommendEmbeddingMethod(format, entropy, variance),
                CapacityUtilization = requiredCapacity > 0 ? (double)requiredCapacity / capacity : 0
            };
        }

        private CarrierFormat DetectFormat(byte[] data, string fileName)
        {
            if (data.Length < 8)
                return CarrierFormat.Unknown;

            // Magic byte detection
            if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
                return CarrierFormat.Png;

            if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
                return CarrierFormat.Jpeg;

            if (data[0] == 0x42 && data[1] == 0x4D)
                return CarrierFormat.Bmp;

            if (data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F')
            {
                if (data.Length > 11)
                {
                    var format = System.Text.Encoding.ASCII.GetString(data, 8, 4);
                    if (format == "WAVE")
                        return CarrierFormat.Wav;
                    if (format == "AVI ")
                        return CarrierFormat.Avi;
                }
            }

            if (data[0] == 'F' && data[1] == 'O' && data[2] == 'R' && data[3] == 'M')
                return CarrierFormat.Aiff;

            if (data[0] == 'I' && data[1] == 'D' && data[2] == '3')
                return CarrierFormat.Mp3;

            if (data[0] == 0x66 && data[1] == 0x4C && data[2] == 0x61 && data[3] == 0x43)
                return CarrierFormat.Flac;

            if (data[0] == 0x1A && data[1] == 0x45 && data[2] == 0xDF && data[3] == 0xA3)
                return CarrierFormat.Mkv;

            if (data[0] == 0x25 && data[1] == 0x50 && data[2] == 0x44 && data[3] == 0x46)
                return CarrierFormat.Pdf;

            if (data[0] == 0x50 && data[1] == 0x4B && data[2] == 0x03 && data[3] == 0x04)
            {
                // Check for DOCX
                if (fileName?.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) == true)
                    return CarrierFormat.Docx;
                return CarrierFormat.Zip;
            }

            if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46)
                return CarrierFormat.Gif;

            if ((data[0] == 0x49 && data[1] == 0x49 && data[2] == 0x2A && data[3] == 0x00) ||
                (data[0] == 0x4D && data[1] == 0x4D && data[2] == 0x00 && data[3] == 0x2A))
                return CarrierFormat.Tiff;

            // Fallback to extension
            if (!string.IsNullOrEmpty(fileName))
            {
                var ext = Path.GetExtension(fileName).ToLowerInvariant();
                return ext switch
                {
                    ".png" => CarrierFormat.Png,
                    ".jpg" or ".jpeg" => CarrierFormat.Jpeg,
                    ".bmp" => CarrierFormat.Bmp,
                    ".wav" => CarrierFormat.Wav,
                    ".mp3" => CarrierFormat.Mp3,
                    ".avi" => CarrierFormat.Avi,
                    ".mp4" => CarrierFormat.Mp4,
                    ".pdf" => CarrierFormat.Pdf,
                    _ => CarrierFormat.Unknown
                };
            }

            return CarrierFormat.Unknown;
        }

        private long EstimateCapacity(byte[] data, CarrierFormat format)
        {
            return format switch
            {
                CarrierFormat.Png => (data.Length - 100) / 8,
                CarrierFormat.Bmp => (data.Length - 54) / 8,
                CarrierFormat.Jpeg => data.Length / 20, // Lower capacity due to DCT
                CarrierFormat.Tiff => (data.Length - 100) / 8,
                CarrierFormat.Gif => data.Length / 100, // Very limited
                CarrierFormat.Wav => (data.Length - 44) / 8,
                CarrierFormat.Aiff => (data.Length - 54) / 8,
                CarrierFormat.Mp3 => data.Length / 50,
                CarrierFormat.Flac => data.Length / 16,
                CarrierFormat.Avi => data.Length / 64,
                CarrierFormat.Mp4 => data.Length / 100,
                CarrierFormat.Mkv => data.Length / 100,
                CarrierFormat.Pdf => data.Length / 200,
                CarrierFormat.Docx => data.Length / 100,
                CarrierFormat.Zip => data.Length / 500, // Comment field only
                _ => data.Length / 100
            };
        }

        private double CalculateEntropy(byte[] data)
        {
            if (data.Length == 0)
                return 0;

            var histogram = new int[256];
            foreach (var b in data)
                histogram[b]++;

            double entropy = 0;
            double total = data.Length;

            foreach (var count in histogram)
            {
                if (count > 0)
                {
                    double p = count / total;
                    entropy -= p * Math.Log2(p);
                }
            }

            return entropy;
        }

        private double CalculateVariance(byte[] data)
        {
            if (data.Length < 2)
                return 0;

            long sum = 0;
            foreach (var b in data)
                sum += b;

            double mean = (double)sum / data.Length;

            double variance = 0;
            foreach (var b in data)
                variance += Math.Pow(b - mean, 2);

            return Math.Sqrt(variance / data.Length);
        }

        private double GetFormatPopularity(CarrierFormat format)
        {
            // Popularity score 0-1 based on how common the format is
            return format switch
            {
                CarrierFormat.Jpeg => 1.0,
                CarrierFormat.Png => 0.95,
                CarrierFormat.Mp3 => 0.9,
                CarrierFormat.Mp4 => 0.85,
                CarrierFormat.Pdf => 0.8,
                CarrierFormat.Docx => 0.75,
                CarrierFormat.Wav => 0.5,
                CarrierFormat.Bmp => 0.4,
                CarrierFormat.Avi => 0.4,
                CarrierFormat.Gif => 0.6,
                CarrierFormat.Zip => 0.7,
                CarrierFormat.Flac => 0.3,
                CarrierFormat.Aiff => 0.2,
                CarrierFormat.Tiff => 0.3,
                CarrierFormat.Mkv => 0.4,
                _ => 0.1
            };
        }

        private string RecommendEmbeddingMethod(CarrierFormat format, double entropy, double variance)
        {
            return format switch
            {
                CarrierFormat.Png or CarrierFormat.Bmp or CarrierFormat.Tiff => "LSB",
                CarrierFormat.Jpeg => entropy > 7 ? "DCT-F5" : "DCT-Direct",
                CarrierFormat.Wav or CarrierFormat.Aiff => variance > 30 ? "EchoHiding" : "LSB",
                CarrierFormat.Mp3 or CarrierFormat.Flac => "SpreadSpectrum",
                CarrierFormat.Avi or CarrierFormat.Mp4 or CarrierFormat.Mkv => "InterFrame",
                CarrierFormat.Pdf or CarrierFormat.Docx => "MetadataEmbedding",
                CarrierFormat.Zip => "CommentField",
                CarrierFormat.Gif => "PaletteModification",
                _ => "LSB"
            };
        }

        private CarrierEvaluation SelectOptimal(List<CarrierEvaluation> candidates)
        {
            // Weight: 40% overall score, 30% detection resistance, 30% capacity margin
            return candidates
                .OrderByDescending(c => c.OverallScore * 0.4 +
                                       (c.DetectionRisk == "Low" ? 30 : c.DetectionRisk == "Medium" ? 15 : 0) +
                                       (1 - c.CapacityUtilization) * 30)
                .First();
        }

        private CarrierEvaluation SelectMaxCapacity(List<CarrierEvaluation> candidates)
        {
            return candidates.OrderByDescending(c => c.Capacity).First();
        }

        private CarrierEvaluation SelectMinDetection(List<CarrierEvaluation> candidates)
        {
            return candidates
                .OrderByDescending(c => c.EntropyScore + c.VarianceScore)
                .ThenBy(c => c.CapacityUtilization)
                .First();
        }

        private CarrierEvaluation SelectRandom(List<CarrierEvaluation> candidates)
        {
            var rng = RandomNumberGenerator.Create();
            var bytes = new byte[4];
            rng.GetBytes(bytes);
            var index = Math.Abs(BitConverter.ToInt32(bytes, 0)) % candidates.Count;
            return candidates[index];
        }

        private CarrierEvaluation SelectBalanced(List<CarrierEvaluation> candidates)
        {
            // Equal weight to all factors
            return candidates
                .OrderByDescending(c => c.CapacityScore + c.EntropyScore + c.VarianceScore +
                                       c.PopularityScore + c.SizeScore)
                .First();
        }

        private string DetermineRecommendedFormat(List<CarrierEvaluation> suitable)
        {
            if (!suitable.Any())
                return "PNG";

            var formatCounts = suitable
                .GroupBy(s => s.DetectedFormat)
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Average(e => e.OverallScore))
                .FirstOrDefault();

            return formatCounts?.Key.ToString() ?? "PNG";
        }

        private string DetermineRecommendedMethod(List<CarrierEvaluation> suitable)
        {
            if (!suitable.Any())
                return "LSB";

            var methodCounts = suitable
                .GroupBy(s => s.RecommendedMethod)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault();

            return methodCounts?.Key ?? "LSB";
        }

        private long EstimateMinimumCarrierSize(long capacityNeeded)
        {
            // Assume worst case (JPEG) embedding ratio
            return capacityNeeded * 20;
        }

        private List<string> GetRecommendedCarrierTypes(long capacityNeeded)
        {
            var types = new List<string>();

            if (capacityNeeded < 1000)
            {
                types.AddRange(new[] { "PNG (small)", "BMP (small)", "JPEG (high quality)" });
            }
            else if (capacityNeeded < 50000)
            {
                types.AddRange(new[] { "PNG (1MP+)", "WAV (30s+)", "JPEG (5MP+)" });
            }
            else if (capacityNeeded < 500000)
            {
                types.AddRange(new[] { "PNG (10MP+)", "WAV (5min+)", "AVI (30s+)" });
            }
            else
            {
                types.AddRange(new[] { "Video (1min+)", "WAV (30min+)", "Large images (20MP+)" });
            }

            return types;
        }
    }

    /// <summary>
    /// Carrier selection modes.
    /// </summary>
    public enum CarrierSelectionMode
    {
        Optimal,
        MaximumCapacity,
        MinimumDetection,
        Random,
        Balanced
    }

    /// <summary>
    /// Embedding redundancy levels.
    /// </summary>
    public enum EmbeddingRedundancy
    {
        Minimal,
        Standard,
        High,
        Maximum
    }

    /// <summary>
    /// Carrier file formats.
    /// </summary>
    public enum CarrierFormat
    {
        Unknown,
        Png,
        Jpeg,
        Bmp,
        Tiff,
        Gif,
        Wav,
        Aiff,
        Mp3,
        Flac,
        Avi,
        Mp4,
        Mkv,
        Pdf,
        Docx,
        Zip
    }

    /// <summary>
    /// Candidate carrier file.
    /// </summary>
    public record CarrierCandidate
    {
        public string FileName { get; init; } = "";
        public string FilePath { get; init; } = "";
        public byte[] Data { get; init; } = Array.Empty<byte>();
        public long FileSize { get; init; }
        public DateTime ModifiedTime { get; init; }
    }

    /// <summary>
    /// Carrier evaluation results.
    /// </summary>
    public record CarrierEvaluation
    {
        public CarrierCandidate Candidate { get; init; } = new();
        public CarrierFormat DetectedFormat { get; init; }
        public long Capacity { get; init; }
        public long RequiredCapacity { get; init; }
        public double Entropy { get; init; }
        public double Variance { get; init; }
        public double FormatPopularity { get; init; }
        public double CapacityScore { get; init; }
        public double EntropyScore { get; init; }
        public double VarianceScore { get; init; }
        public double PopularityScore { get; init; }
        public double SizeScore { get; init; }
        public double OverallScore { get; init; }
        public bool MeetsRequirements { get; init; }
        public string DetectionRisk { get; init; } = "Medium";
        public string RecommendedMethod { get; init; } = "";
        public double CapacityUtilization { get; init; }
    }

    /// <summary>
    /// Carrier selection result.
    /// </summary>
    public record CarrierSelectionResult
    {
        public bool Success { get; init; }
        public string Reason { get; init; } = "";
        public CarrierCandidate? SelectedCarrier { get; init; }
        public CarrierEvaluation? Evaluation { get; init; }
        public long RequiredCapacity { get; init; }
        public int CandidatesEvaluated { get; init; }
        public int SuitableCandidates { get; init; }
        public string SelectionMode { get; init; } = "";
    }

    /// <summary>
    /// Carrier recommendations.
    /// </summary>
    public record CarrierRecommendation
    {
        public long PayloadSize { get; init; }
        public long RequiredCapacity { get; init; }
        public int TotalCandidates { get; init; }
        public int SuitableCandidates { get; init; }
        public List<CarrierEvaluation> TopRecommendations { get; init; } = new();
        public List<CarrierEvaluation> NearMissCandidates { get; init; } = new();
        public string RecommendedFormat { get; init; } = "";
        public string RecommendedMethod { get; init; } = "";
    }

    /// <summary>
    /// Capacity requirements calculation.
    /// </summary>
    public record CapacityRequirements
    {
        public long PayloadSize { get; init; }
        public long HeaderOverhead { get; init; }
        public long EncryptionOverhead { get; init; }
        public long ChecksumOverhead { get; init; }
        public long BaseCapacityNeeded { get; init; }
        public double RedundancyFactor { get; init; }
        public long TotalCapacityNeeded { get; init; }
        public long MinimumCarrierSize { get; init; }
        public List<string> RecommendedCarrierTypes { get; init; } = new();
    }
}
