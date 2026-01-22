using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.ThreatDetection
{
    /// <summary>
    /// Enterprise-grade threat detection plugin for DataWarehouse.
    /// Provides comprehensive protection against ransomware, malware, and suspicious activity.
    ///
    /// Features:
    /// - Shannon entropy analysis for ransomware/encryption detection
    /// - Magic bytes verification for file type validation
    /// - Known malware signature scanning
    /// - Behavioral anomaly detection for access patterns
    /// - Content quarantine capabilities
    /// - Real-time threat alerting via callbacks
    ///
    /// Detection Capabilities:
    /// - Ransomware: High entropy detection, ransomware extensions, ransom notes
    /// - Malware: Signature-based detection, known malicious patterns
    /// - Anomalies: Unusual access times, mass modifications, suspicious users
    /// - File Tampering: Extension/content mismatch detection
    /// </summary>
    public sealed class ThreatDetectionPlugin : ThreatDetectionPluginBase
    {
        #region Fields and Configuration

        private readonly ThreatDetectionConfig _config;
        private readonly ConcurrentDictionary<string, BaselineData> _baselines = new();
        private readonly ConcurrentDictionary<string, QuarantinedItem> _quarantine = new();
        private readonly ConcurrentDictionary<string, AccessPattern> _accessPatterns = new();
        private readonly ConcurrentDictionary<string, long> _threatsByType = new();
        private readonly List<Action<ThreatAlert>> _threatCallbacks = new();
        private readonly object _callbackLock = new();
        private readonly object _statsLock = new();

        private long _totalScansInternal;
        private long _threatsDetectedInternal;
        private long _anomaliesDetectedInternal;
        private DateTime _lastScanTimeInternal = DateTime.MinValue;

        // Entropy thresholds (based on research and empirical data)
        private const double HighEntropyThreshold = 7.9;      // Very likely encrypted
        private const double SuspiciousEntropyThreshold = 7.5; // Possibly encrypted/compressed
        private const double NormalEntropyBaseline = 5.0;      // Typical for text/documents

        // Sliding window configuration for partial file analysis
        private const int EntropyWindowSize = 4096;
        private const int EntropyWindowStep = 1024;

        #endregion

        #region Plugin Identity

        public override string Id => "datawarehouse.plugins.threatdetection";
        public override string Name => "Enterprise Threat Detection";
        public override string Version => "1.0.0";

        public override ThreatDetectionCapabilities DetectionCapabilities =>
            ThreatDetectionCapabilities.Ransomware |
            ThreatDetectionCapabilities.Malware |
            ThreatDetectionCapabilities.Anomaly |
            ThreatDetectionCapabilities.Entropy |
            ThreatDetectionCapabilities.Signature |
            ThreatDetectionCapabilities.Behavioral;

        #endregion

        #region Constructors

        public ThreatDetectionPlugin() : this(new ThreatDetectionConfig())
        {
        }

        public ThreatDetectionPlugin(ThreatDetectionConfig config)
        {
            _config = config ?? new ThreatDetectionConfig();
            InitializeMalwareSignatures();
            InitializeMagicBytes();
            InitializeRansomwarePatterns();
        }

        #endregion

        #region Core Scanning Methods

        /// <summary>
        /// Analyzes content for all types of threats.
        /// This is the main entry point for comprehensive threat analysis.
        /// Combines entropy analysis, malware signatures, ransomware detection, and file verification.
        /// </summary>
        public Task<ThreatScanResult> AnalyzeContentAsync(
            byte[] content,
            string? filename = null,
            ScanOptions? options = null,
            CancellationToken ct = default)
        {
            using var stream = new MemoryStream(content);
            return ScanAsync(stream, filename, options ?? new ScanOptions
            {
                DeepScan = true,
                CheckEntropy = true,
                CheckSignatures = true,
                CheckBehavior = true
            }, ct);
        }

        /// <summary>
        /// Analyzes content for all types of threats from a stream.
        /// </summary>
        public Task<ThreatScanResult> AnalyzeContentAsync(
            Stream data,
            string? filename = null,
            ScanOptions? options = null,
            CancellationToken ct = default)
        {
            return ScanAsync(data, filename, options ?? new ScanOptions
            {
                DeepScan = true,
                CheckEntropy = true,
                CheckSignatures = true,
                CheckBehavior = true
            }, ct);
        }

        /// <summary>
        /// Performs comprehensive threat scanning on the provided data.
        /// Combines entropy analysis, signature scanning, and file type verification.
        /// </summary>
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
                // Read data into memory for multi-pass analysis
                byte[] content;
                using (var ms = new MemoryStream())
                {
                    await data.CopyToAsync(ms, ct);
                    content = ms.ToArray();
                }

                // 1. Entropy Analysis (ransomware detection)
                if (options.CheckEntropy)
                {
                    var entropyResult = await AnalyzeEntropyInternalAsync(content, ct);
                    if (entropyResult.IsHighEntropy)
                    {
                        var severity = entropyResult.NormalizedEntropy > 0.98
                            ? ThreatSeverity.Critical
                            : ThreatSeverity.High;

                        threats.Add(new DetectedThreat
                        {
                            ThreatType = "Ransomware",
                            Name = "High Entropy Content",
                            Severity = severity,
                            Confidence = entropyResult.NormalizedEntropy,
                            Description = $"Content exhibits high entropy ({entropyResult.Entropy:F2} bits/byte), " +
                                        $"indicating possible encryption. This is a common ransomware indicator."
                        });

                        if (severity > highestSeverity) highestSeverity = severity;
                    }
                }

                // 2. File Type Verification (magic bytes vs extension)
                if (filename != null)
                {
                    var fileTypeResult = VerifyFileTypeInternal(content, filename);
                    if (fileTypeResult.IsMismatch)
                    {
                        threats.Add(new DetectedThreat
                        {
                            ThreatType = "FileTypeMismatch",
                            Name = "Extension Spoofing Detected",
                            Severity = ThreatSeverity.Medium,
                            Confidence = 0.95,
                            Description = $"File extension '{fileTypeResult.Extension}' does not match " +
                                        $"actual content type '{fileTypeResult.DetectedType}'. " +
                                        $"This may indicate an attempt to bypass security controls."
                        });

                        if (ThreatSeverity.Medium > highestSeverity)
                            highestSeverity = ThreatSeverity.Medium;
                    }

                    // Check for known ransomware extensions
                    var ransomwareExtResult = CheckRansomwareExtension(filename);
                    if (ransomwareExtResult != null)
                    {
                        threats.Add(ransomwareExtResult);
                        if (ransomwareExtResult.Severity > highestSeverity)
                            highestSeverity = ransomwareExtResult.Severity;
                    }
                }

                // 3. Signature-based Malware Scanning
                if (options.CheckSignatures)
                {
                    var signatureThreats = ScanForMalwareSignatures(content);
                    foreach (var threat in signatureThreats)
                    {
                        threats.Add(threat);
                        if (threat.Severity > highestSeverity)
                            highestSeverity = threat.Severity;
                    }
                }

                // 4. Ransom Note Detection
                var ransomNoteResult = DetectRansomNote(content, filename);
                if (ransomNoteResult != null)
                {
                    threats.Add(ransomNoteResult);
                    if (ransomNoteResult.Severity > highestSeverity)
                        highestSeverity = ransomNoteResult.Severity;
                }

                // 5. Deep Scan (additional heuristic analysis)
                if (options.DeepScan)
                {
                    var heuristicThreats = PerformHeuristicAnalysis(content, filename);
                    foreach (var threat in heuristicThreats)
                    {
                        threats.Add(threat);
                        if (threat.Severity > highestSeverity)
                            highestSeverity = threat.Severity;
                    }
                }

                // Record statistics
                var isThreat = threats.Count > 0;
                RecordScanInternal(isThreat, threats);

                // Trigger callbacks for detected threats
                if (isThreat)
                {
                    NotifyThreatCallbacks(threats, filename);
                }

                var duration = DateTime.UtcNow - startTime;
                return new ThreatScanResult
                {
                    IsThreatDetected = isThreat,
                    Severity = highestSeverity,
                    Threats = threats,
                    ScanDuration = duration,
                    Recommendation = GenerateRecommendation(threats, highestSeverity)
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
                    Recommendation = $"Scan failed with error: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Scans content for threats (convenience method for byte arrays).
        /// </summary>
        public async Task<ThreatScanResult> ScanContentAsync(
            byte[] content,
            string? filename = null,
            ScanOptions? options = null,
            CancellationToken ct = default)
        {
            using var stream = new MemoryStream(content);
            return await ScanAsync(stream, filename, options, ct);
        }

        /// <summary>
        /// Scans content specifically for malware signatures using pattern-based detection.
        /// Returns only malware-related threats (web shells, exploits, cryptominers, etc.).
        /// </summary>
        public Task<MalwareScanResult> ScanForMalwareAsync(
            byte[] content,
            string? filename = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var startTime = DateTime.UtcNow;

            var signatureThreats = ScanForMalwareSignatures(content);
            var heuristicThreats = PerformHeuristicAnalysis(content, filename);

            var allMalwareThreats = signatureThreats
                .Concat(heuristicThreats.Where(t => t.ThreatType == "Suspicious" || t.ThreatType == "Webshell" || t.ThreatType == "Exploit"))
                .ToList();

            var highestSeverity = allMalwareThreats.Count > 0
                ? allMalwareThreats.Max(t => t.Severity)
                : ThreatSeverity.None;

            return Task.FromResult(new MalwareScanResult
            {
                IsMalwareDetected = allMalwareThreats.Count > 0,
                Severity = highestSeverity,
                DetectedThreats = allMalwareThreats,
                SignaturesChecked = _malwareSignatures.Count,
                ScanDuration = DateTime.UtcNow - startTime,
                Recommendation = highestSeverity switch
                {
                    ThreatSeverity.Critical => "CRITICAL: Known malware detected. Quarantine immediately and initiate incident response.",
                    ThreatSeverity.High => "HIGH: Suspicious malware patterns detected. Quarantine for further analysis.",
                    ThreatSeverity.Medium => "MEDIUM: Potentially malicious content. Review before allowing access.",
                    ThreatSeverity.Low => "LOW: Minor suspicious indicators. Monitor for additional signs.",
                    _ => "No malware signatures detected."
                }
            });
        }

        /// <summary>
        /// Scans content specifically for malware signatures from a stream.
        /// </summary>
        public async Task<MalwareScanResult> ScanForMalwareAsync(
            Stream data,
            string? filename = null,
            CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            await data.CopyToAsync(ms, ct);
            return await ScanForMalwareAsync(ms.ToArray(), filename, ct);
        }

        /// <summary>
        /// Detects ransomware-specific threats including:
        /// - High entropy content (encrypted data)
        /// - Known ransomware file extensions
        /// - Ransom note patterns and content
        /// - Mass modification patterns
        /// </summary>
        public async Task<RansomwareDetectionResult> DetectRansomwareAsync(
            byte[] content,
            string? filename = null,
            CancellationToken ct = default)
        {
            var startTime = DateTime.UtcNow;
            var indicators = new List<RansomwareIndicator>();
            var threats = new List<DetectedThreat>();

            // 1. Entropy Analysis
            var entropyResult = await AnalyzeEntropyInternalAsync(content, ct);
            if (entropyResult.IsHighEntropy)
            {
                indicators.Add(new RansomwareIndicator
                {
                    Type = RansomwareIndicatorType.HighEntropy,
                    Description = $"High entropy detected: {entropyResult.Entropy:F2} bits/byte (normalized: {entropyResult.NormalizedEntropy:F4})",
                    Confidence = entropyResult.NormalizedEntropy,
                    Severity = entropyResult.NormalizedEntropy > 0.98 ? ThreatSeverity.Critical : ThreatSeverity.High
                });

                threats.Add(new DetectedThreat
                {
                    ThreatType = "Ransomware",
                    Name = "Encrypted Content Detected",
                    Severity = entropyResult.NormalizedEntropy > 0.98 ? ThreatSeverity.Critical : ThreatSeverity.High,
                    Confidence = entropyResult.NormalizedEntropy,
                    Description = entropyResult.Assessment ?? "High entropy indicates possible encryption"
                });
            }

            // 2. Ransomware Extension Check
            if (filename != null)
            {
                var extResult = CheckRansomwareExtension(filename);
                if (extResult != null)
                {
                    indicators.Add(new RansomwareIndicator
                    {
                        Type = RansomwareIndicatorType.RansomwareExtension,
                        Description = $"Known ransomware extension: {Path.GetExtension(filename)}",
                        Confidence = 0.95,
                        Severity = ThreatSeverity.Critical
                    });
                    threats.Add(extResult);
                }
            }

            // 3. Ransom Note Detection
            var ransomNoteResult = DetectRansomNote(content, filename);
            if (ransomNoteResult != null)
            {
                indicators.Add(new RansomwareIndicator
                {
                    Type = RansomwareIndicatorType.RansomNote,
                    Description = "Ransom note content or filename pattern detected",
                    Confidence = ransomNoteResult.Confidence,
                    Severity = ThreatSeverity.Critical
                });
                threats.Add(ransomNoteResult);
            }

            // 4. Check for encrypted section patterns (partial encryption)
            if (entropyResult.HighEntropyWindowPercentage > 0.5 && entropyResult.HighEntropyWindowPercentage < 1.0)
            {
                indicators.Add(new RansomwareIndicator
                {
                    Type = RansomwareIndicatorType.PartialEncryption,
                    Description = $"{entropyResult.HighEntropyWindowPercentage * 100:F1}% of content shows high entropy (possible partial encryption)",
                    Confidence = entropyResult.HighEntropyWindowPercentage,
                    Severity = ThreatSeverity.High
                });
            }

            var isRansomware = indicators.Count > 0;
            var highestSeverity = threats.Count > 0 ? threats.Max(t => t.Severity) : ThreatSeverity.None;
            var riskScore = indicators.Count > 0
                ? Math.Min(1.0, indicators.Sum(i => i.Confidence * (int)i.Severity / 4.0) / indicators.Count)
                : 0;

            return new RansomwareDetectionResult
            {
                IsRansomwareDetected = isRansomware,
                RiskScore = riskScore,
                Severity = highestSeverity,
                Indicators = indicators,
                Threats = threats,
                EntropyAnalysis = entropyResult,
                ScanDuration = DateTime.UtcNow - startTime,
                Recommendation = isRansomware
                    ? "CRITICAL: Ransomware indicators detected. Isolate affected systems, preserve evidence, and restore from verified clean backups."
                    : "No ransomware indicators detected."
            };
        }

        /// <summary>
        /// Detects ransomware from a stream.
        /// </summary>
        public async Task<RansomwareDetectionResult> DetectRansomwareAsync(
            Stream data,
            string? filename = null,
            CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            await data.CopyToAsync(ms, ct);
            return await DetectRansomwareAsync(ms.ToArray(), filename, ct);
        }

        /// <summary>
        /// Verifies file signature (magic bytes) matches the declared extension.
        /// Detects extension spoofing and polyglot files.
        /// </summary>
        public Task<FileSignatureResult> CheckFileSignatureAsync(
            byte[] content,
            string filename,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var startTime = DateTime.UtcNow;

            var verification = VerifyFileTypeInternal(content, filename);
            var detectedType = DetectFileType(content);

            // Check for polyglot file (valid as multiple formats)
            var polyglotTypes = DetectPolyglotFormats(content);
            var isPolyglot = polyglotTypes.Count > 1;

            ThreatSeverity severity = ThreatSeverity.None;
            string assessment;

            if (verification.IsMismatch)
            {
                severity = ThreatSeverity.Medium;
                assessment = $"Extension spoofing detected: File claims to be '{verification.Extension}' " +
                           $"but content indicates '{verification.DetectedType}'. This may be an attempt to bypass security controls.";
            }
            else if (isPolyglot)
            {
                severity = ThreatSeverity.Low;
                assessment = $"Polyglot file detected: Content is valid as multiple formats ({string.Join(", ", polyglotTypes)}). " +
                           "While not necessarily malicious, polyglot files can be used for evasion.";
            }
            else if (!verification.MagicBytesFound)
            {
                severity = ThreatSeverity.None;
                assessment = "No recognized magic bytes found. File type could not be verified from content.";
            }
            else
            {
                assessment = $"File signature verified: Content matches declared extension '{verification.Extension}'.";
            }

            return Task.FromResult(new FileSignatureResult
            {
                IsValid = !verification.IsMismatch,
                DeclaredExtension = verification.Extension,
                DetectedType = verification.DetectedType,
                IsMismatch = verification.IsMismatch,
                IsPolyglot = isPolyglot,
                PolyglotTypes = polyglotTypes,
                MagicBytesFound = verification.MagicBytesFound,
                Confidence = verification.Confidence,
                Severity = severity,
                Assessment = assessment,
                CheckDuration = DateTime.UtcNow - startTime
            });
        }

        /// <summary>
        /// Checks file signature from a stream.
        /// </summary>
        public async Task<FileSignatureResult> CheckFileSignatureAsync(
            Stream data,
            string filename,
            CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            await data.CopyToAsync(ms, ct);
            return await CheckFileSignatureAsync(ms.ToArray(), filename, ct);
        }

        /// <summary>
        /// Detects if content is valid as multiple file formats (polyglot detection).
        /// </summary>
        private List<string> DetectPolyglotFormats(byte[] content)
        {
            var validFormats = new List<string>();

            foreach (var (extension, signatures) in _magicBytes)
            {
                foreach (var (signature, offset, _) in signatures)
                {
                    if (content.Length >= offset + signature.Length)
                    {
                        var match = true;
                        for (int i = 0; i < signature.Length && match; i++)
                        {
                            if (content[offset + i] != signature[i])
                                match = false;
                        }
                        if (match)
                        {
                            validFormats.Add(extension);
                            break; // Found match for this extension, move to next
                        }
                    }
                }
            }

            return validFormats;
        }

        #endregion

        #region Entropy Analysis

        /// <summary>
        /// Analyzes Shannon entropy of data to detect encryption/ransomware.
        /// Uses sliding window analysis for more accurate partial-file detection.
        /// </summary>
        public async Task<EntropyAnalysisResult> AnalyzeEntropyAsync(
            Stream data,
            CancellationToken ct = default)
        {
            byte[] content;
            using (var ms = new MemoryStream())
            {
                await data.CopyToAsync(ms, ct);
                content = ms.ToArray();
            }

            return await AnalyzeEntropyInternalAsync(content, ct);
        }

        private Task<EntropyAnalysisResult> AnalyzeEntropyInternalAsync(byte[] content, CancellationToken ct)
        {
            if (content.Length == 0)
            {
                return Task.FromResult(new EntropyAnalysisResult
                {
                    Entropy = 0,
                    NormalizedEntropy = 0,
                    IsHighEntropy = false,
                    Assessment = "Empty data",
                    WindowAnalysis = Array.Empty<WindowEntropyResult>()
                });
            }

            // Calculate overall entropy
            var overallEntropy = CalculateShannonEntropy(content);
            var normalizedEntropy = overallEntropy / 8.0;

            // Perform sliding window analysis for partial encryption detection
            var windowResults = new List<WindowEntropyResult>();
            if (content.Length >= EntropyWindowSize)
            {
                for (int offset = 0; offset + EntropyWindowSize <= content.Length; offset += EntropyWindowStep)
                {
                    ct.ThrowIfCancellationRequested();

                    var windowData = new byte[EntropyWindowSize];
                    Array.Copy(content, offset, windowData, 0, EntropyWindowSize);
                    var windowEntropy = CalculateShannonEntropy(windowData);

                    windowResults.Add(new WindowEntropyResult
                    {
                        Offset = offset,
                        Size = EntropyWindowSize,
                        Entropy = windowEntropy,
                        IsHighEntropy = windowEntropy >= SuspiciousEntropyThreshold
                    });
                }
            }

            // Determine if content is suspicious
            var isHigh = normalizedEntropy >= (HighEntropyThreshold / 8.0);
            var highEntropyWindowPercentage = windowResults.Count > 0
                ? (double)windowResults.Count(w => w.IsHighEntropy) / windowResults.Count
                : 0;

            // Assessment logic with file-type awareness
            string assessment;
            if (isHigh)
            {
                assessment = "CRITICAL: High entropy detected across entire content. " +
                           "Strong indicator of encryption or ransomware activity.";
            }
            else if (highEntropyWindowPercentage > 0.7)
            {
                assessment = "WARNING: Majority of content shows high entropy. " +
                           "Possible partial encryption or embedded encrypted data.";
            }
            else if (highEntropyWindowPercentage > 0.3)
            {
                assessment = "SUSPICIOUS: Significant portions show elevated entropy. " +
                           "May contain encrypted sections or compressed data.";
            }
            else if (normalizedEntropy > 0.85)
            {
                assessment = "ELEVATED: Higher than normal entropy. Could be compressed data, " +
                           "media files, or binary content.";
            }
            else
            {
                assessment = "NORMAL: Entropy levels are within expected range for typical content.";
            }

            return Task.FromResult(new EntropyAnalysisResult
            {
                Entropy = overallEntropy,
                NormalizedEntropy = normalizedEntropy,
                IsHighEntropy = isHigh || highEntropyWindowPercentage > 0.7,
                Assessment = assessment,
                WindowAnalysis = windowResults,
                HighEntropyWindowPercentage = highEntropyWindowPercentage
            });
        }

        /// <summary>
        /// Calculates Shannon entropy for a byte array.
        /// Returns value between 0 (no randomness) and 8 (maximum randomness for bytes).
        /// </summary>
        private static double CalculateShannonEntropy(byte[] data)
        {
            if (data.Length == 0) return 0;

            var frequencies = new long[256];
            foreach (var b in data)
            {
                frequencies[b]++;
            }

            double entropy = 0;
            var totalBytes = (double)data.Length;

            for (int i = 0; i < 256; i++)
            {
                if (frequencies[i] > 0)
                {
                    var probability = frequencies[i] / totalBytes;
                    entropy -= probability * Math.Log2(probability);
                }
            }

            return entropy;
        }

        /// <summary>
        /// Gets expected entropy baseline for a given file type.
        /// Used to reduce false positives for naturally high-entropy file types.
        /// </summary>
        private static double GetEntropyBaselineForType(string? fileType)
        {
            return fileType?.ToLowerInvariant() switch
            {
                // Compressed formats (naturally high entropy)
                "zip" or "gz" or "rar" or "7z" or "xz" or "bz2" => 7.8,

                // Media formats (high entropy due to compression)
                "jpg" or "jpeg" or "png" or "mp3" or "mp4" or "avi" or "mkv" => 7.6,
                "pdf" => 6.5,

                // Binary executables
                "exe" or "dll" or "so" or "dylib" => 6.0,

                // Text-based formats
                "txt" or "log" or "csv" => 4.5,
                "html" or "xml" or "json" => 5.0,
                "cs" or "java" or "py" or "js" or "ts" => 4.8,

                // Office documents (compressed XML internally)
                "docx" or "xlsx" or "pptx" => 7.5,
                "doc" or "xls" or "ppt" => 5.5,

                // Default baseline
                _ => NormalEntropyBaseline
            };
        }

        #endregion

        #region File Type Verification

        /// <summary>
        /// Magic bytes database for file type detection.
        /// </summary>
        private readonly Dictionary<string, (byte[] Signature, int Offset, string Description)[]> _magicBytes = new();

        private void InitializeMagicBytes()
        {
            // Common file format magic bytes
            _magicBytes["pdf"] = new[] { (new byte[] { 0x25, 0x50, 0x44, 0x46 }, 0, "PDF document") }; // %PDF
            _magicBytes["zip"] = new[] { (new byte[] { 0x50, 0x4B, 0x03, 0x04 }, 0, "ZIP archive") }; // PK..
            _magicBytes["docx"] = new[] { (new byte[] { 0x50, 0x4B, 0x03, 0x04 }, 0, "Office Open XML") };
            _magicBytes["xlsx"] = new[] { (new byte[] { 0x50, 0x4B, 0x03, 0x04 }, 0, "Office Open XML") };
            _magicBytes["pptx"] = new[] { (new byte[] { 0x50, 0x4B, 0x03, 0x04 }, 0, "Office Open XML") };
            _magicBytes["rar"] = new[] { (new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07 }, 0, "RAR archive") };
            _magicBytes["7z"] = new[] { (new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }, 0, "7-Zip archive") };
            _magicBytes["gz"] = new[] { (new byte[] { 0x1F, 0x8B }, 0, "GZIP archive") };
            _magicBytes["bz2"] = new[] { (new byte[] { 0x42, 0x5A, 0x68 }, 0, "BZIP2 archive") };
            _magicBytes["xz"] = new[] { (new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 }, 0, "XZ archive") };
            _magicBytes["tar"] = new[] { (new byte[] { 0x75, 0x73, 0x74, 0x61, 0x72 }, 257, "TAR archive") };

            // Images
            _magicBytes["png"] = new[] { (new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, "PNG image") };
            _magicBytes["jpg"] = new[] { (new byte[] { 0xFF, 0xD8, 0xFF }, 0, "JPEG image") };
            _magicBytes["jpeg"] = new[] { (new byte[] { 0xFF, 0xD8, 0xFF }, 0, "JPEG image") };
            _magicBytes["gif"] = new[] { (new byte[] { 0x47, 0x49, 0x46, 0x38 }, 0, "GIF image") };
            _magicBytes["bmp"] = new[] { (new byte[] { 0x42, 0x4D }, 0, "BMP image") };
            _magicBytes["webp"] = new[] { (new byte[] { 0x52, 0x49, 0x46, 0x46 }, 0, "WebP image") };
            _magicBytes["ico"] = new[] { (new byte[] { 0x00, 0x00, 0x01, 0x00 }, 0, "ICO icon") };
            _magicBytes["tiff"] = new[]
            {
                (new byte[] { 0x49, 0x49, 0x2A, 0x00 }, 0, "TIFF image (little-endian)"),
                (new byte[] { 0x4D, 0x4D, 0x00, 0x2A }, 0, "TIFF image (big-endian)")
            };

            // Audio/Video
            _magicBytes["mp3"] = new[]
            {
                (new byte[] { 0xFF, 0xFB }, 0, "MP3 audio"),
                (new byte[] { 0xFF, 0xFA }, 0, "MP3 audio"),
                (new byte[] { 0x49, 0x44, 0x33 }, 0, "MP3 audio (ID3)")
            };
            _magicBytes["mp4"] = new[]
            {
                (new byte[] { 0x66, 0x74, 0x79, 0x70 }, 4, "MP4 video"),
                (new byte[] { 0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70 }, 0, "MP4 video")
            };
            _magicBytes["avi"] = new[] { (new byte[] { 0x52, 0x49, 0x46, 0x46 }, 0, "AVI video") };
            _magicBytes["mkv"] = new[] { (new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }, 0, "MKV video") };
            _magicBytes["wav"] = new[] { (new byte[] { 0x52, 0x49, 0x46, 0x46 }, 0, "WAV audio") };
            _magicBytes["flac"] = new[] { (new byte[] { 0x66, 0x4C, 0x61, 0x43 }, 0, "FLAC audio") };

            // Executables
            _magicBytes["exe"] = new[] { (new byte[] { 0x4D, 0x5A }, 0, "Windows executable") }; // MZ
            _magicBytes["dll"] = new[] { (new byte[] { 0x4D, 0x5A }, 0, "Windows DLL") };
            _magicBytes["elf"] = new[] { (new byte[] { 0x7F, 0x45, 0x4C, 0x46 }, 0, "ELF executable") };
            _magicBytes["macho"] = new[]
            {
                (new byte[] { 0xFE, 0xED, 0xFA, 0xCE }, 0, "Mach-O 32-bit"),
                (new byte[] { 0xFE, 0xED, 0xFA, 0xCF }, 0, "Mach-O 64-bit"),
                (new byte[] { 0xCF, 0xFA, 0xED, 0xFE }, 0, "Mach-O 64-bit (reverse)")
            };

            // Documents
            _magicBytes["doc"] = new[] { (new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }, 0, "MS Office document") };
            _magicBytes["xls"] = new[] { (new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }, 0, "MS Office document") };
            _magicBytes["ppt"] = new[] { (new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }, 0, "MS Office document") };

            // Database
            _magicBytes["sqlite"] = new[] { (new byte[] { 0x53, 0x51, 0x4C, 0x69, 0x74, 0x65, 0x20, 0x66, 0x6F, 0x72, 0x6D, 0x61, 0x74, 0x20, 0x33 }, 0, "SQLite database") };

            // Java
            _magicBytes["class"] = new[] { (new byte[] { 0xCA, 0xFE, 0xBA, 0xBE }, 0, "Java class file") };
            _magicBytes["jar"] = new[] { (new byte[] { 0x50, 0x4B, 0x03, 0x04 }, 0, "Java JAR archive") };

            // Scripts (check for text patterns)
            _magicBytes["ps1"] = new[] { (Encoding.UTF8.GetBytes("#!"), 0, "PowerShell script") };
            _magicBytes["sh"] = new[] { (Encoding.UTF8.GetBytes("#!"), 0, "Shell script") };
            _magicBytes["bat"] = new[] { (Encoding.UTF8.GetBytes("@echo"), 0, "Batch file") };
        }

        /// <summary>
        /// Verifies that a file's content matches its extension using magic bytes.
        /// </summary>
        public FileTypeVerificationResult VerifyFileType(byte[] content, string filename)
        {
            return VerifyFileTypeInternal(content, filename);
        }

        private FileTypeVerificationResult VerifyFileTypeInternal(byte[] content, string filename)
        {
            var extension = Path.GetExtension(filename)?.TrimStart('.').ToLowerInvariant() ?? "";
            var detectedType = DetectFileType(content);

            // Check if there's a mismatch
            var isMismatch = false;
            if (!string.IsNullOrEmpty(extension) && !string.IsNullOrEmpty(detectedType))
            {
                // Special handling for Office formats (all use ZIP structure)
                var officeFormats = new HashSet<string> { "docx", "xlsx", "pptx", "odt", "ods", "odp" };
                if (officeFormats.Contains(extension) && detectedType == "zip")
                {
                    // Office documents are ZIP files - this is expected
                    isMismatch = false;
                }
                else if (extension != detectedType)
                {
                    // Check for known equivalent formats
                    var equivalents = new Dictionary<string, HashSet<string>>
                    {
                        { "jpg", new HashSet<string> { "jpeg" } },
                        { "jpeg", new HashSet<string> { "jpg" } },
                        { "tif", new HashSet<string> { "tiff" } },
                        { "tiff", new HashSet<string> { "tif" } },
                        { "htm", new HashSet<string> { "html" } },
                        { "html", new HashSet<string> { "htm" } }
                    };

                    if (equivalents.TryGetValue(extension, out var equiv) && equiv.Contains(detectedType))
                    {
                        isMismatch = false;
                    }
                    else
                    {
                        isMismatch = true;
                    }
                }
            }

            return new FileTypeVerificationResult
            {
                Extension = extension,
                DetectedType = detectedType ?? "unknown",
                IsMismatch = isMismatch,
                Confidence = string.IsNullOrEmpty(detectedType) ? 0.5 : 0.95,
                MagicBytesFound = !string.IsNullOrEmpty(detectedType)
            };
        }

        /// <summary>
        /// Detects file type from content using magic bytes.
        /// </summary>
        private string? DetectFileType(byte[] content)
        {
            if (content.Length == 0) return null;

            foreach (var (extension, signatures) in _magicBytes)
            {
                foreach (var (signature, offset, _) in signatures)
                {
                    if (content.Length >= offset + signature.Length)
                    {
                        var match = true;
                        for (int i = 0; i < signature.Length && match; i++)
                        {
                            if (content[offset + i] != signature[i])
                                match = false;
                        }
                        if (match) return extension;
                    }
                }
            }

            // Text file detection (check for printable ASCII/UTF-8)
            if (IsLikelyTextFile(content))
            {
                return "txt";
            }

            return null;
        }

        private static bool IsLikelyTextFile(byte[] content)
        {
            if (content.Length == 0) return false;

            var sampleSize = Math.Min(content.Length, 8192);
            var printableCount = 0;
            var nullCount = 0;

            for (int i = 0; i < sampleSize; i++)
            {
                var b = content[i];
                if (b == 0) nullCount++;
                else if (b >= 32 && b < 127 || b == 9 || b == 10 || b == 13) printableCount++;
            }

            // If more than 2 nulls or less than 80% printable, probably not text
            return nullCount < 3 && (double)printableCount / sampleSize > 0.8;
        }

        #endregion

        #region Malware Signature Scanning

        /// <summary>
        /// Known malware signatures database.
        /// Each signature includes pattern, offset, threat name, and severity.
        /// </summary>
        private readonly List<MalwareSignature> _malwareSignatures = new();

        private void InitializeMalwareSignatures()
        {
            // EICAR test string (standard malware test signature)
            _malwareSignatures.Add(new MalwareSignature
            {
                Name = "EICAR-Test-File",
                Pattern = Encoding.ASCII.GetBytes("X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*"),
                ThreatType = "Test",
                Severity = ThreatSeverity.Low,
                Description = "EICAR antivirus test file - not a real threat but indicates AV should have detected this"
            });

            // Common web shell patterns
            _malwareSignatures.Add(new MalwareSignature
            {
                Name = "PHP-WebShell-Eval",
                Pattern = Encoding.ASCII.GetBytes("eval($_"),
                ThreatType = "Webshell",
                Severity = ThreatSeverity.Critical,
                Description = "PHP web shell using eval with user input - allows remote code execution"
            });

            _malwareSignatures.Add(new MalwareSignature
            {
                Name = "PHP-WebShell-System",
                Pattern = Encoding.ASCII.GetBytes("system($_"),
                ThreatType = "Webshell",
                Severity = ThreatSeverity.Critical,
                Description = "PHP web shell using system() with user input - allows command execution"
            });

            _malwareSignatures.Add(new MalwareSignature
            {
                Name = "PHP-WebShell-Base64",
                Pattern = Encoding.ASCII.GetBytes("eval(base64_decode("),
                ThreatType = "Webshell",
                Severity = ThreatSeverity.Critical,
                Description = "Obfuscated PHP web shell using base64 encoding"
            });

            _malwareSignatures.Add(new MalwareSignature
            {
                Name = "ASP-WebShell",
                Pattern = Encoding.ASCII.GetBytes("eval request("),
                ThreatType = "Webshell",
                Severity = ThreatSeverity.Critical,
                Description = "ASP/ASPX web shell pattern"
            });

            // PowerShell-based threats
            _malwareSignatures.Add(new MalwareSignature
            {
                Name = "PowerShell-EncodedCommand",
                Pattern = Encoding.ASCII.GetBytes("-EncodedCommand"),
                ThreatType = "Suspicious",
                Severity = ThreatSeverity.Medium,
                Description = "PowerShell encoded command - often used for obfuscation"
            });

            _malwareSignatures.Add(new MalwareSignature
            {
                Name = "PowerShell-DownloadString",
                Pattern = Encoding.ASCII.GetBytes("DownloadString("),
                ThreatType = "Downloader",
                Severity = ThreatSeverity.High,
                Description = "PowerShell download pattern - may fetch and execute remote code"
            });

            _malwareSignatures.Add(new MalwareSignature
            {
                Name = "PowerShell-IEX",
                Pattern = Encoding.ASCII.GetBytes("IEX("),
                ThreatType = "Suspicious",
                Severity = ThreatSeverity.High,
                Description = "PowerShell Invoke-Expression - commonly used in malware"
            });

            // Windows Scripting threats
            _malwareSignatures.Add(new MalwareSignature
            {
                Name = "VBS-WScript-Shell",
                Pattern = Encoding.ASCII.GetBytes("WScript.Shell"),
                ThreatType = "Suspicious",
                Severity = ThreatSeverity.Medium,
                Description = "VBScript shell access - may execute system commands"
            });

            _malwareSignatures.Add(new MalwareSignature
            {
                Name = "VBS-CreateObject-Shell",
                Pattern = Encoding.ASCII.GetBytes("CreateObject(\"Shell.Application\")"),
                ThreatType = "Suspicious",
                Severity = ThreatSeverity.Medium,
                Description = "VBScript shell object creation"
            });

            // Common exploit patterns
            _malwareSignatures.Add(new MalwareSignature
            {
                Name = "ShellCode-NOP-Sled",
                Pattern = new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 },
                ThreatType = "Exploit",
                Severity = ThreatSeverity.High,
                Description = "NOP sled pattern - common in buffer overflow exploits"
            });

            // Cryptominer patterns
            _malwareSignatures.Add(new MalwareSignature
            {
                Name = "CryptoMiner-Stratum",
                Pattern = Encoding.ASCII.GetBytes("stratum+tcp://"),
                ThreatType = "Cryptominer",
                Severity = ThreatSeverity.Medium,
                Description = "Cryptocurrency mining pool connection string"
            });

            _malwareSignatures.Add(new MalwareSignature
            {
                Name = "CryptoMiner-XMRig",
                Pattern = Encoding.ASCII.GetBytes("xmrig"),
                ThreatType = "Cryptominer",
                Severity = ThreatSeverity.Medium,
                Description = "XMRig cryptominer reference"
            });
        }

        private List<DetectedThreat> ScanForMalwareSignatures(byte[] content)
        {
            var threats = new List<DetectedThreat>();

            foreach (var signature in _malwareSignatures)
            {
                var offset = FindPattern(content, signature.Pattern);
                if (offset >= 0)
                {
                    threats.Add(new DetectedThreat
                    {
                        ThreatType = signature.ThreatType,
                        Name = signature.Name,
                        Severity = signature.Severity,
                        Confidence = 0.99,
                        Description = signature.Description,
                        Offset = offset,
                        Signature = Convert.ToHexString(signature.Pattern.Take(16).ToArray())
                    });
                }
            }

            return threats;
        }

        /// <summary>
        /// Boyer-Moore-Horspool pattern matching for efficient signature scanning.
        /// </summary>
        private static int FindPattern(byte[] data, byte[] pattern)
        {
            if (pattern.Length == 0 || data.Length < pattern.Length)
                return -1;

            // Build bad character table
            var badChar = new int[256];
            for (int i = 0; i < 256; i++)
                badChar[i] = pattern.Length;
            for (int i = 0; i < pattern.Length - 1; i++)
                badChar[pattern[i]] = pattern.Length - 1 - i;

            // Search
            int offset = 0;
            while (offset <= data.Length - pattern.Length)
            {
                int j = pattern.Length - 1;
                while (j >= 0 && pattern[j] == data[offset + j])
                    j--;

                if (j < 0)
                    return offset;

                offset += badChar[data[offset + pattern.Length - 1]];
            }

            return -1;
        }

        #endregion

        #region Ransomware Detection

        /// <summary>
        /// Known ransomware file extensions.
        /// </summary>
        private readonly HashSet<string> _ransomwareExtensions = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Ransom note filename patterns.
        /// </summary>
        private readonly List<Regex> _ransomNotePatterns = new();

        /// <summary>
        /// Content patterns found in ransom notes.
        /// </summary>
        private readonly List<(string Pattern, string RansomwareName)> _ransomNoteContentPatterns = new();

        private void InitializeRansomwarePatterns()
        {
            // Known ransomware file extensions (extensive list)
            var extensions = new[]
            {
                // Common ransomware extensions
                ".encrypted", ".enc", ".locked", ".crypto", ".crypt",
                ".locky", ".zepto", ".odin", ".thor", ".aesir",

                // WannaCry variants
                ".wncry", ".wcry", ".wncryt", ".wncrypt",

                // CryptoLocker family
                ".cryptolocker", ".cryptowall", ".cryptodefense",

                // Cerber variants
                ".cerber", ".cerber2", ".cerber3",

                // Other known ransomware
                ".petya", ".notpetya", ".goldeneye",
                ".ryuk", ".hermes", ".hermes2",
                ".maze", ".revil", ".sodinokibi", ".sodin",
                ".conti", ".lockbit", ".lockbit2", ".lockbit3",
                ".darkside", ".blackmatter", ".blackcat", ".alphv",
                ".hive", ".ransomexx", ".ragnar", ".ragnarok",
                ".avaddon", ".babuk", ".quantum", ".royal",
                ".play", ".clop", ".cl0p", ".egregor",

                // GandCrab variants
                ".gdcb", ".crab", ".krab",

                // Generic ransomware patterns
                ".pay", ".payme", ".paynow", ".ransom", ".rnsm",
                ".crypted", ".crypttt", ".cry", ".crysis",
                ".dharma", ".phobos", ".wallet", ".arena",
                ".btc", ".btcware", ".onion", ".tor",
                ".aaa", ".abc", ".xyz", ".zzz",
                ".r5a", ".r4a", ".helpme", ".helppme",
                ".micro", ".xxx", ".ttt", ".ecc",
                ".vvv", ".ccc", ".exx", ".ezz",
                ".good", ".locked", ".lol!", ".OMG!"
            };

            foreach (var ext in extensions)
            {
                _ransomwareExtensions.Add(ext.TrimStart('.'));
            }

            // Ransom note filename patterns
            _ransomNotePatterns.Add(new Regex(@"(?i)readme.*\.(txt|html|hta)$", RegexOptions.Compiled));
            _ransomNotePatterns.Add(new Regex(@"(?i)decrypt.*\.(txt|html|hta)$", RegexOptions.Compiled));
            _ransomNotePatterns.Add(new Regex(@"(?i)restore.*\.(txt|html|hta)$", RegexOptions.Compiled));
            _ransomNotePatterns.Add(new Regex(@"(?i)recover.*\.(txt|html|hta)$", RegexOptions.Compiled));
            _ransomNotePatterns.Add(new Regex(@"(?i)how.*to.*decrypt.*\.(txt|html|hta)$", RegexOptions.Compiled));
            _ransomNotePatterns.Add(new Regex(@"(?i)how.*to.*restore.*\.(txt|html|hta)$", RegexOptions.Compiled));
            _ransomNotePatterns.Add(new Regex(@"(?i)instruction.*\.(txt|html|hta)$", RegexOptions.Compiled));
            _ransomNotePatterns.Add(new Regex(@"(?i)help.*restore.*\.(txt|html|hta)$", RegexOptions.Compiled));
            _ransomNotePatterns.Add(new Regex(@"(?i)!+.*\.(txt|html|hta)$", RegexOptions.Compiled));
            _ransomNotePatterns.Add(new Regex(@"(?i)_readme_\.txt$", RegexOptions.Compiled));
            _ransomNotePatterns.Add(new Regex(@"(?i)ransom.*note.*\.(txt|html)$", RegexOptions.Compiled));

            // Content patterns in ransom notes
            _ransomNoteContentPatterns.Add(("Your files have been encrypted", "Generic"));
            _ransomNoteContentPatterns.Add(("All your files are encrypted", "Generic"));
            _ransomNoteContentPatterns.Add(("bitcoin", "Cryptocurrency"));
            _ransomNoteContentPatterns.Add(("BTC", "Cryptocurrency"));
            _ransomNoteContentPatterns.Add(("monero", "Cryptocurrency"));
            _ransomNoteContentPatterns.Add(("XMR", "Cryptocurrency"));
            _ransomNoteContentPatterns.Add(("decrypt your files", "Generic"));
            _ransomNoteContentPatterns.Add(("restore your files", "Generic"));
            _ransomNoteContentPatterns.Add(("send payment", "Generic"));
            _ransomNoteContentPatterns.Add(("pay ransom", "Generic"));
            _ransomNoteContentPatterns.Add(("decryption key", "Generic"));
            _ransomNoteContentPatterns.Add(("decryptor tool", "Generic"));
            _ransomNoteContentPatterns.Add(("personal ID", "Generic"));
            _ransomNoteContentPatterns.Add(("unique key", "Generic"));
            _ransomNoteContentPatterns.Add((".onion", "TorNetwork"));
            _ransomNoteContentPatterns.Add(("tor browser", "TorNetwork"));
            _ransomNoteContentPatterns.Add(("dark web", "TorNetwork"));
            _ransomNoteContentPatterns.Add(("deadline", "Urgency"));
            _ransomNoteContentPatterns.Add(("time limit", "Urgency"));
            _ransomNoteContentPatterns.Add(("double price", "Urgency"));
            _ransomNoteContentPatterns.Add(("files will be deleted", "Urgency"));
            _ransomNoteContentPatterns.Add(("WANNACRY", "WannaCry"));
            _ransomNoteContentPatterns.Add(("LOCKBIT", "LockBit"));
            _ransomNoteContentPatterns.Add(("RYUK", "Ryuk"));
            _ransomNoteContentPatterns.Add(("CONTI", "Conti"));
            _ransomNoteContentPatterns.Add(("MAZE", "Maze"));
        }

        private DetectedThreat? CheckRansomwareExtension(string filename)
        {
            var extension = Path.GetExtension(filename)?.TrimStart('.').ToLowerInvariant();
            if (string.IsNullOrEmpty(extension)) return null;

            if (_ransomwareExtensions.Contains(extension))
            {
                return new DetectedThreat
                {
                    ThreatType = "Ransomware",
                    Name = "Ransomware File Extension",
                    Severity = ThreatSeverity.Critical,
                    Confidence = 0.95,
                    Description = $"File has known ransomware extension '.{extension}'. " +
                                "This strongly indicates ransomware activity."
                };
            }

            return null;
        }

        private DetectedThreat? DetectRansomNote(byte[] content, string? filename)
        {
            // Check filename patterns
            if (filename != null)
            {
                var name = Path.GetFileName(filename);
                foreach (var pattern in _ransomNotePatterns)
                {
                    if (pattern.IsMatch(name))
                    {
                        return new DetectedThreat
                        {
                            ThreatType = "Ransomware",
                            Name = "Ransom Note Detected",
                            Severity = ThreatSeverity.Critical,
                            Confidence = 0.9,
                            Description = $"Filename '{name}' matches known ransom note pattern."
                        };
                    }
                }
            }

            // Check content for ransom note indicators
            if (IsLikelyTextFile(content))
            {
                var text = Encoding.UTF8.GetString(content);
                var matchCount = 0;
                var matchedPatterns = new List<string>();

                foreach (var (pattern, category) in _ransomNoteContentPatterns)
                {
                    if (text.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        matchCount++;
                        matchedPatterns.Add(category);
                    }
                }

                // Multiple matches indicate high probability of ransom note
                if (matchCount >= 3)
                {
                    return new DetectedThreat
                    {
                        ThreatType = "Ransomware",
                        Name = "Ransom Note Content",
                        Severity = ThreatSeverity.Critical,
                        Confidence = Math.Min(0.95, 0.5 + (matchCount * 0.1)),
                        Description = $"Content contains {matchCount} ransom note indicators " +
                                    $"(categories: {string.Join(", ", matchedPatterns.Distinct())})"
                    };
                }
            }

            return null;
        }

        #endregion

        #region Behavioral Analysis

        /// <summary>
        /// Analyzes access patterns for anomalous behavior.
        /// </summary>
        public override async Task<AnomalyAnalysisResult> AnalyzeBehaviorAsync(
            BehaviorData behavior,
            CancellationToken ct = default)
        {
            var anomalies = new List<DetectedAnomaly>();
            double totalScore = 0;

            // 1. Analyze access time patterns
            var timeAnomalies = AnalyzeAccessTimePatterns(behavior.RecentAccesses);
            anomalies.AddRange(timeAnomalies);
            totalScore += timeAnomalies.Sum(a => a.Score);

            // 2. Analyze modification patterns (mass modification detection)
            var modificationAnomalies = AnalyzeModificationPatterns(behavior.RecentModifications);
            anomalies.AddRange(modificationAnomalies);
            totalScore += modificationAnomalies.Sum(a => a.Score);

            // 3. Analyze user patterns
            var userAnomalies = AnalyzeUserPatterns(behavior.RecentAccesses);
            anomalies.AddRange(userAnomalies);
            totalScore += userAnomalies.Sum(a => a.Score);

            // 4. Analyze entropy changes (ransomware indicator)
            var entropyAnomalies = AnalyzeEntropyChanges(behavior.RecentModifications);
            anomalies.AddRange(entropyAnomalies);
            totalScore += entropyAnomalies.Sum(a => a.Score);

            // Update statistics
            if (anomalies.Count > 0)
            {
                Interlocked.Increment(ref _anomaliesDetectedInternal);
            }

            var normalizedScore = Math.Min(1.0, totalScore / Math.Max(1, anomalies.Count));

            return new AnomalyAnalysisResult
            {
                IsAnomalous = anomalies.Count > 0 && normalizedScore > 0.5,
                AnomalyScore = normalizedScore,
                Anomalies = anomalies
            };
        }

        private List<DetectedAnomaly> AnalyzeAccessTimePatterns(IReadOnlyList<AccessRecord> accesses)
        {
            var anomalies = new List<DetectedAnomaly>();
            if (accesses.Count < 2) return anomalies;

            // Check for unusual hours (outside normal business hours)
            var offHoursAccesses = accesses.Where(a =>
                a.Timestamp.Hour < 6 || a.Timestamp.Hour > 22 ||
                a.Timestamp.DayOfWeek == DayOfWeek.Saturday ||
                a.Timestamp.DayOfWeek == DayOfWeek.Sunday).ToList();

            if (offHoursAccesses.Count > accesses.Count * 0.3)
            {
                anomalies.Add(new DetectedAnomaly
                {
                    Type = "UnusualAccessTime",
                    Description = $"{offHoursAccesses.Count} of {accesses.Count} accesses occurred outside business hours",
                    Score = 0.6,
                    DetectedAt = DateTime.UtcNow
                });
            }

            // Check for rapid successive accesses (possible automated attack)
            var rapidAccesses = 0;
            for (int i = 1; i < accesses.Count; i++)
            {
                var diff = (accesses[i].Timestamp - accesses[i - 1].Timestamp).TotalSeconds;
                if (diff < 1) rapidAccesses++;
            }

            if (rapidAccesses > accesses.Count * 0.5)
            {
                anomalies.Add(new DetectedAnomaly
                {
                    Type = "RapidAccess",
                    Description = $"{rapidAccesses} rapid successive accesses detected (< 1 second apart)",
                    Score = 0.8,
                    DetectedAt = DateTime.UtcNow
                });
            }

            return anomalies;
        }

        private List<DetectedAnomaly> AnalyzeModificationPatterns(IReadOnlyList<ModificationRecord> modifications)
        {
            var anomalies = new List<DetectedAnomaly>();
            if (modifications.Count < 2) return anomalies;

            // Mass modification detection (ransomware behavior)
            var recentWindow = TimeSpan.FromMinutes(5);
            var now = modifications.Max(m => m.Timestamp);
            var recentMods = modifications.Where(m => (now - m.Timestamp) <= recentWindow).ToList();

            if (recentMods.Count > 50)
            {
                anomalies.Add(new DetectedAnomaly
                {
                    Type = "MassModification",
                    Description = $"{recentMods.Count} files modified within {recentWindow.TotalMinutes} minutes - " +
                                "possible ransomware encryption in progress",
                    Score = 0.95,
                    DetectedAt = DateTime.UtcNow
                });
            }

            // Large total byte changes
            var totalBytesChanged = recentMods.Sum(m => m.BytesChanged);
            if (totalBytesChanged > 100_000_000) // 100MB
            {
                anomalies.Add(new DetectedAnomaly
                {
                    Type = "LargeDataModification",
                    Description = $"{totalBytesChanged / 1_000_000}MB modified in short period",
                    Score = 0.7,
                    DetectedAt = DateTime.UtcNow
                });
            }

            return anomalies;
        }

        private List<DetectedAnomaly> AnalyzeUserPatterns(IReadOnlyList<AccessRecord> accesses)
        {
            var anomalies = new List<DetectedAnomaly>();
            if (accesses.Count < 5) return anomalies;

            // Check for unusual IP patterns
            var uniqueIps = accesses.Where(a => !string.IsNullOrEmpty(a.SourceIP))
                                   .Select(a => a.SourceIP!)
                                   .Distinct()
                                   .ToList();

            if (uniqueIps.Count > 5)
            {
                anomalies.Add(new DetectedAnomaly
                {
                    Type = "MultipleSourceIPs",
                    Description = $"Access from {uniqueIps.Count} different IP addresses - possible credential compromise",
                    Score = 0.65,
                    DetectedAt = DateTime.UtcNow
                });
            }

            // Check for unusual user diversity
            var uniqueUsers = accesses.Select(a => a.UserId).Distinct().ToList();
            if (uniqueUsers.Count > 10)
            {
                anomalies.Add(new DetectedAnomaly
                {
                    Type = "UnusualUserDiversity",
                    Description = $"{uniqueUsers.Count} different users accessed this resource",
                    Score = 0.5,
                    DetectedAt = DateTime.UtcNow
                });
            }

            return anomalies;
        }

        private List<DetectedAnomaly> AnalyzeEntropyChanges(IReadOnlyList<ModificationRecord> modifications)
        {
            var anomalies = new List<DetectedAnomaly>();
            if (modifications.Count < 2) return anomalies;

            // Check for significant entropy increases (encryption indicator)
            var entropyIncreases = modifications.Where(m => m.EntropyDelta > 0.5).ToList();

            if (entropyIncreases.Count > modifications.Count * 0.5)
            {
                anomalies.Add(new DetectedAnomaly
                {
                    Type = "EntropyIncrease",
                    Description = $"{entropyIncreases.Count} modifications showed significant entropy increase - " +
                                "possible ransomware encryption",
                    Score = 0.9,
                    DetectedAt = DateTime.UtcNow
                });
            }

            return anomalies;
        }

        /// <summary>
        /// Detects anomalies in access patterns for a specific object.
        /// </summary>
        public async Task<List<DetectedAnomaly>> DetectAnomaliesAsync(
            string objectId,
            IReadOnlyList<AccessRecord> recentAccesses,
            CancellationToken ct = default)
        {
            var behavior = new BehaviorData
            {
                ObjectId = objectId,
                RecentAccesses = recentAccesses,
                RecentModifications = Array.Empty<ModificationRecord>()
            };

            var result = await AnalyzeBehaviorAsync(behavior, ct);
            return result.Anomalies.ToList();
        }

        #endregion

        #region Baseline Management

        /// <summary>
        /// Registers a content baseline for future comparison.
        /// </summary>
        public override Task RegisterBaselineAsync(
            string objectId,
            BaselineData baseline,
            CancellationToken ct = default)
        {
            _baselines[objectId] = baseline;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Compares current content against registered baseline.
        /// </summary>
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

            byte[] content;
            using (var ms = new MemoryStream())
            {
                await currentData.CopyToAsync(ms, ct);
                content = ms.ToArray();
            }

            var changedFeatures = new List<string>();
            double deviationScore = 0;

            // Check size change
            var sizeDiff = Math.Abs(content.Length - baseline.Size) / (double)Math.Max(baseline.Size, 1);
            if (sizeDiff > 0.1)
            {
                changedFeatures.Add($"Size changed by {sizeDiff * 100:F1}%");
                deviationScore += sizeDiff * 0.3;
            }

            // Check entropy change
            var currentEntropy = CalculateShannonEntropy(content);
            var entropyDiff = Math.Abs(currentEntropy - baseline.Entropy);
            if (entropyDiff > 1.0)
            {
                changedFeatures.Add($"Entropy changed from {baseline.Entropy:F2} to {currentEntropy:F2}");
                deviationScore += entropyDiff * 0.2;
            }

            // Check content hash
            using var sha256 = SHA256.Create();
            var currentHash = Convert.ToHexString(sha256.ComputeHash(content));
            if (!string.Equals(currentHash, baseline.ContentHash, StringComparison.OrdinalIgnoreCase))
            {
                changedFeatures.Add("Content hash changed");
                deviationScore += 0.5;
            }

            // Normalize deviation score
            deviationScore = Math.Min(1.0, deviationScore);

            string assessment;
            if (deviationScore > 0.8)
            {
                assessment = "CRITICAL: Major deviation from baseline detected. Content may have been compromised.";
            }
            else if (deviationScore > 0.5)
            {
                assessment = "WARNING: Significant deviation from baseline. Review changes carefully.";
            }
            else if (deviationScore > 0.2)
            {
                assessment = "NOTICE: Minor deviations detected. May be normal modifications.";
            }
            else
            {
                assessment = "OK: Content is within expected parameters of baseline.";
            }

            return new BaselineComparisonResult
            {
                HasDeviation = deviationScore > 0.2,
                DeviationScore = deviationScore,
                ChangedFeatures = changedFeatures,
                Assessment = assessment
            };
        }

        #endregion

        #region Quarantine Operations

        /// <summary>
        /// Quarantines suspicious content, preventing access until reviewed.
        /// </summary>
        public Task<QuarantineResult> QuarantineAsync(
            string objectId,
            byte[] content,
            IReadOnlyList<DetectedThreat> threats,
            string? reason = null,
            CancellationToken ct = default)
        {
            var quarantineId = Guid.NewGuid().ToString("N");

            var item = new QuarantinedItem
            {
                QuarantineId = quarantineId,
                ObjectId = objectId,
                Content = content,
                ContentHash = Convert.ToHexString(SHA256.HashData(content)),
                Threats = threats.ToList(),
                Reason = reason ?? string.Join("; ", threats.Select(t => t.Description)),
                QuarantinedAt = DateTime.UtcNow,
                Status = QuarantineStatus.Quarantined
            };

            _quarantine[quarantineId] = item;

            // Notify via callbacks
            var alert = new ThreatAlert
            {
                AlertId = Guid.NewGuid().ToString(),
                Severity = threats.Max(t => t.Severity),
                ObjectId = objectId,
                Threats = threats.ToList(),
                Timestamp = DateTime.UtcNow,
                Action = "Quarantined",
                Details = $"Content quarantined with ID {quarantineId}"
            };
            NotifyThreatCallbacksDirect(alert);

            return Task.FromResult(new QuarantineResult
            {
                Success = true,
                QuarantineId = quarantineId,
                Message = $"Content successfully quarantined. ID: {quarantineId}"
            });
        }

        /// <summary>
        /// Retrieves a quarantined item for review.
        /// </summary>
        public Task<QuarantinedItem?> GetQuarantinedItemAsync(
            string quarantineId,
            CancellationToken ct = default)
        {
            _quarantine.TryGetValue(quarantineId, out var item);
            return Task.FromResult(item);
        }

        /// <summary>
        /// Lists all quarantined items.
        /// </summary>
        public Task<IReadOnlyList<QuarantinedItem>> ListQuarantinedItemsAsync(
            QuarantineStatus? status = null,
            CancellationToken ct = default)
        {
            IEnumerable<QuarantinedItem> items = _quarantine.Values;

            if (status.HasValue)
            {
                items = items.Where(i => i.Status == status.Value);
            }

            return Task.FromResult<IReadOnlyList<QuarantinedItem>>(items.OrderByDescending(i => i.QuarantinedAt).ToList());
        }

        /// <summary>
        /// Releases an item from quarantine (after review determines it's safe).
        /// </summary>
        public Task<QuarantineResult> ReleaseFromQuarantineAsync(
            string quarantineId,
            string approvedBy,
            string? notes = null,
            CancellationToken ct = default)
        {
            if (!_quarantine.TryGetValue(quarantineId, out var item))
            {
                return Task.FromResult(new QuarantineResult
                {
                    Success = false,
                    QuarantineId = quarantineId,
                    Message = "Quarantine item not found"
                });
            }

            item.Status = QuarantineStatus.Released;
            item.ReviewedBy = approvedBy;
            item.ReviewedAt = DateTime.UtcNow;
            item.ReviewNotes = notes;

            return Task.FromResult(new QuarantineResult
            {
                Success = true,
                QuarantineId = quarantineId,
                Message = $"Item released from quarantine by {approvedBy}"
            });
        }

        /// <summary>
        /// Permanently deletes a quarantined item (confirmed threat).
        /// </summary>
        public Task<QuarantineResult> DeleteQuarantinedItemAsync(
            string quarantineId,
            string deletedBy,
            CancellationToken ct = default)
        {
            if (!_quarantine.TryRemove(quarantineId, out var item))
            {
                return Task.FromResult(new QuarantineResult
                {
                    Success = false,
                    QuarantineId = quarantineId,
                    Message = "Quarantine item not found"
                });
            }

            return Task.FromResult(new QuarantineResult
            {
                Success = true,
                QuarantineId = quarantineId,
                Message = $"Quarantined item permanently deleted by {deletedBy}"
            });
        }

        #endregion

        #region Threat Callbacks

        /// <summary>
        /// Registers a callback to be invoked when threats are detected.
        /// </summary>
        public void RegisterThreatCallback(Action<ThreatAlert> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));

            lock (_callbackLock)
            {
                _threatCallbacks.Add(callback);
            }
        }

        /// <summary>
        /// Unregisters a previously registered callback.
        /// </summary>
        public void UnregisterThreatCallback(Action<ThreatAlert> callback)
        {
            lock (_callbackLock)
            {
                _threatCallbacks.Remove(callback);
            }
        }

        private void NotifyThreatCallbacks(IReadOnlyList<DetectedThreat> threats, string? filename)
        {
            var alert = new ThreatAlert
            {
                AlertId = Guid.NewGuid().ToString(),
                Severity = threats.Max(t => t.Severity),
                ObjectId = filename ?? "unknown",
                Threats = threats.ToList(),
                Timestamp = DateTime.UtcNow,
                Action = "Detected"
            };

            NotifyThreatCallbacksDirect(alert);
        }

        private void NotifyThreatCallbacksDirect(ThreatAlert alert)
        {
            List<Action<ThreatAlert>> callbacks;
            lock (_callbackLock)
            {
                callbacks = new List<Action<ThreatAlert>>(_threatCallbacks);
            }

            foreach (var callback in callbacks)
            {
                try
                {
                    callback(alert);
                }
                catch
                {
                    // Don't let callback failures affect threat detection
                }
            }
        }

        #endregion

        #region Heuristic Analysis

        private List<DetectedThreat> PerformHeuristicAnalysis(byte[] content, string? filename)
        {
            var threats = new List<DetectedThreat>();

            // Check for suspicious strings
            if (IsLikelyTextFile(content))
            {
                var text = Encoding.UTF8.GetString(content);

                // Check for credential harvesting patterns
                if (ContainsCredentialPattern(text))
                {
                    threats.Add(new DetectedThreat
                    {
                        ThreatType = "Suspicious",
                        Name = "Credential Harvesting Pattern",
                        Severity = ThreatSeverity.Medium,
                        Confidence = 0.7,
                        Description = "Content contains patterns commonly used for credential theft"
                    });
                }

                // Check for suspicious URLs
                var suspiciousUrls = FindSuspiciousUrls(text);
                if (suspiciousUrls.Any())
                {
                    threats.Add(new DetectedThreat
                    {
                        ThreatType = "Suspicious",
                        Name = "Suspicious URLs Detected",
                        Severity = ThreatSeverity.Medium,
                        Confidence = 0.75,
                        Description = $"Found {suspiciousUrls.Count} suspicious URLs: {string.Join(", ", suspiciousUrls.Take(3))}"
                    });
                }
            }

            // Check for packed/obfuscated content
            if (IsPotentiallyPacked(content))
            {
                threats.Add(new DetectedThreat
                {
                    ThreatType = "Suspicious",
                    Name = "Potentially Packed/Obfuscated",
                    Severity = ThreatSeverity.Low,
                    Confidence = 0.6,
                    Description = "Content shows characteristics of packing or obfuscation"
                });
            }

            return threats;
        }

        private static bool ContainsCredentialPattern(string text)
        {
            var patterns = new[]
            {
                @"(?i)password\s*[=:]\s*['""]",
                @"(?i)passwd\s*[=:]\s*['""]",
                @"(?i)api[_-]?key\s*[=:]\s*['""]",
                @"(?i)secret[_-]?key\s*[=:]\s*['""]",
                @"(?i)access[_-]?token\s*[=:]\s*['""]",
                @"(?i)private[_-]?key\s*[=:]\s*['""]"
            };

            return patterns.Any(p => Regex.IsMatch(text, p));
        }

        private static List<string> FindSuspiciousUrls(string text)
        {
            var urls = new List<string>();
            var urlPattern = @"https?://[^\s""'>]+";
            var matches = Regex.Matches(text, urlPattern);

            foreach (Match match in matches)
            {
                var url = match.Value;
                // Check for suspicious characteristics
                if (url.Contains(".tk") || url.Contains(".ml") || url.Contains(".ga") ||
                    url.Contains(".cf") || url.Contains(".gq") ||  // Free domains
                    url.Contains("bit.ly") || url.Contains("tinyurl") || // URL shorteners
                    url.Contains(".onion") || // Tor
                    Regex.IsMatch(url, @"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}")) // IP address
                {
                    urls.Add(url);
                }
            }

            return urls;
        }

        private static bool IsPotentiallyPacked(byte[] content)
        {
            if (content.Length < 1000) return false;

            // Check for high ratio of non-printable characters (excluding common binary formats)
            var nonPrintable = 0;
            var sampleSize = Math.Min(content.Length, 4096);

            for (int i = 0; i < sampleSize; i++)
            {
                var b = content[i];
                if (b < 32 && b != 9 && b != 10 && b != 13) nonPrintable++;
            }

            var ratio = (double)nonPrintable / sampleSize;

            // High non-printable ratio with no recognizable magic bytes suggests packing
            return ratio > 0.3 && ratio < 0.95;
        }

        #endregion

        #region Threat Report Generation

        /// <summary>
        /// Generates a detailed threat analysis report.
        /// </summary>
        public async Task<ThreatReport> GetThreatReportAsync(
            byte[] content,
            string? filename = null,
            CancellationToken ct = default)
        {
            var startTime = DateTime.UtcNow;

            // Run comprehensive scan
            using var stream = new MemoryStream(content);
            var scanResult = await ScanAsync(stream, filename, new ScanOptions
            {
                DeepScan = true,
                CheckSignatures = true,
                CheckEntropy = true,
                CheckBehavior = true
            }, ct);

            // Get entropy analysis
            var entropyResult = await AnalyzeEntropyInternalAsync(content, ct);

            // Get file type verification
            var fileTypeResult = filename != null
                ? VerifyFileTypeInternal(content, filename)
                : null;

            // Generate report
            return new ThreatReport
            {
                ReportId = Guid.NewGuid().ToString(),
                GeneratedAt = DateTime.UtcNow,
                FileName = filename,
                FileSize = content.Length,
                ContentHash = Convert.ToHexString(SHA256.HashData(content)),
                OverallRiskLevel = scanResult.Severity,
                ThreatCount = scanResult.Threats.Count,
                Threats = scanResult.Threats.ToList(),
                EntropyAnalysis = new EntropyReportSection
                {
                    Entropy = entropyResult.Entropy,
                    NormalizedEntropy = entropyResult.NormalizedEntropy,
                    IsHighEntropy = entropyResult.IsHighEntropy,
                    Assessment = entropyResult.Assessment,
                    HighEntropyWindowPercentage = entropyResult.HighEntropyWindowPercentage
                },
                FileTypeAnalysis = fileTypeResult != null ? new FileTypeReportSection
                {
                    DeclaredExtension = fileTypeResult.Extension,
                    DetectedType = fileTypeResult.DetectedType,
                    IsMismatch = fileTypeResult.IsMismatch,
                    Confidence = fileTypeResult.Confidence
                } : null,
                Recommendations = GenerateDetailedRecommendations(scanResult.Threats, entropyResult, fileTypeResult),
                ScanDuration = DateTime.UtcNow - startTime
            };
        }

        private List<string> GenerateDetailedRecommendations(
            IReadOnlyList<DetectedThreat> threats,
            EntropyAnalysisResult entropy,
            FileTypeVerificationResult? fileType)
        {
            var recommendations = new List<string>();

            // Threat-based recommendations
            if (threats.Any(t => t.ThreatType == "Ransomware"))
            {
                recommendations.Add("CRITICAL: Ransomware indicators detected. Isolate affected systems immediately.");
                recommendations.Add("Do not pay ransom - restore from verified clean backups.");
                recommendations.Add("Preserve evidence for forensic analysis.");
                recommendations.Add("Report incident to security team and law enforcement if applicable.");
            }

            if (threats.Any(t => t.ThreatType == "Malware" || t.ThreatType == "Webshell"))
            {
                recommendations.Add("CRITICAL: Malware detected. Quarantine file immediately.");
                recommendations.Add("Scan all systems that may have accessed this file.");
                recommendations.Add("Review access logs for unauthorized activity.");
            }

            if (threats.Any(t => t.ThreatType == "Suspicious"))
            {
                recommendations.Add("Review file contents manually for confirmation.");
                recommendations.Add("Verify the source and intended purpose of this file.");
            }

            // Entropy-based recommendations
            if (entropy.IsHighEntropy)
            {
                recommendations.Add("High entropy indicates possible encryption. Verify if this is expected.");
                recommendations.Add("Compare with baseline if available to detect unauthorized changes.");
            }

            // File type recommendations
            if (fileType?.IsMismatch == true)
            {
                recommendations.Add($"File extension does not match content type. Expected {fileType.DetectedType}, got {fileType.Extension}.");
                recommendations.Add("This may indicate an attempt to bypass security controls.");
            }

            if (recommendations.Count == 0)
            {
                recommendations.Add("No immediate action required based on scan results.");
                recommendations.Add("Continue regular monitoring and backup procedures.");
            }

            return recommendations;
        }

        private string GenerateRecommendation(IReadOnlyList<DetectedThreat> threats, ThreatSeverity severity)
        {
            return severity switch
            {
                ThreatSeverity.Critical => "CRITICAL: Immediate action required. Quarantine content and initiate incident response.",
                ThreatSeverity.High => "HIGH RISK: Content should be quarantined pending security review.",
                ThreatSeverity.Medium => "MEDIUM RISK: Review content before allowing access. Consider additional scanning.",
                ThreatSeverity.Low => "LOW RISK: Monitor for additional indicators. Safe for review by security team.",
                _ => "No threats detected. Content appears safe based on current scan."
            };
        }

        #endregion

        #region Statistics

        /// <summary>
        /// Gets comprehensive threat detection statistics.
        /// </summary>
        public override Task<ThreatStatistics> GetStatisticsAsync(CancellationToken ct = default)
        {
            return Task.FromResult(new ThreatStatistics
            {
                TotalScans = Interlocked.Read(ref _totalScansInternal),
                ThreatsDetected = Interlocked.Read(ref _threatsDetectedInternal),
                AnomaliesDetected = Interlocked.Read(ref _anomaliesDetectedInternal),
                ThreatsByType = new Dictionary<string, long>(_threatsByType),
                LastScanTime = _lastScanTimeInternal
            });
        }

        private void RecordScanInternal(bool threatDetected, IReadOnlyList<DetectedThreat> threats)
        {
            Interlocked.Increment(ref _totalScansInternal);
            if (threatDetected)
            {
                Interlocked.Increment(ref _threatsDetectedInternal);

                foreach (var threat in threats)
                {
                    _threatsByType.AddOrUpdate(threat.ThreatType, 1, (_, count) => count + 1);
                }
            }
            _lastScanTimeInternal = DateTime.UtcNow;

            // Also call base class tracking
            RecordScan(threatDetected);
        }

        #endregion

        #region Plugin Lifecycle

        public override Task StartAsync(CancellationToken ct)
        {
            // Initialize any resources
            return Task.CompletedTask;
        }

        public override Task StopAsync()
        {
            // Cleanup resources
            lock (_callbackLock)
            {
                _threatCallbacks.Clear();
            }
            return Task.CompletedTask;
        }

        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>
            {
                new() { Name = "threat.analyze", DisplayName = "Analyze Content", Description = "Comprehensive threat analysis of content" },
                new() { Name = "threat.scan", DisplayName = "Scan for Threats", Description = "Scan content for malware, ransomware, and suspicious patterns" },
                new() { Name = "threat.malware", DisplayName = "Malware Detection", Description = "Pattern-based malware signature scanning" },
                new() { Name = "threat.ransomware", DisplayName = "Ransomware Detection", Description = "Detect ransomware indicators and encrypted content" },
                new() { Name = "threat.entropy", DisplayName = "Entropy Analysis", Description = "Calculate Shannon entropy to detect encrypted/ransomware content" },
                new() { Name = "threat.signature", DisplayName = "File Signature Check", Description = "Verify magic bytes and detect polyglot files" },
                new() { Name = "threat.anomaly", DisplayName = "Anomaly Detection", Description = "Detect anomalous access patterns and behaviors" },
                new() { Name = "threat.quarantine", DisplayName = "Quarantine", Description = "Isolate suspicious content for review" },
                new() { Name = "threat.report", DisplayName = "Threat Report", Description = "Generate comprehensive threat analysis report" },
                new() { Name = "threat.baseline", DisplayName = "Baseline Management", Description = "Register and compare content baselines" },
                new() { Name = "threat.callback", DisplayName = "Alert Callbacks", Description = "Register callbacks for real-time threat alerts" }
            };
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["EntropyThreshold"] = HighEntropyThreshold;
            metadata["MalwareSignatureCount"] = _malwareSignatures.Count;
            metadata["RansomwareExtensionCount"] = _ransomwareExtensions.Count;
            metadata["MagicBytesCount"] = _magicBytes.Count;
            metadata["QuarantineEnabled"] = true;
            metadata["RealTimeAlerts"] = true;
            return metadata;
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// Configuration options for ThreatDetectionPlugin.
    /// </summary>
    public class ThreatDetectionConfig
    {
        /// <summary>
        /// Custom entropy threshold for high entropy detection.
        /// Default is 7.9 (out of 8.0 maximum).
        /// </summary>
        public double HighEntropyThreshold { get; set; } = 7.9;

        /// <summary>
        /// Enable deep scanning with additional heuristics.
        /// </summary>
        public bool EnableDeepScan { get; set; } = true;

        /// <summary>
        /// Enable behavioral analysis.
        /// </summary>
        public bool EnableBehavioralAnalysis { get; set; } = true;

        /// <summary>
        /// Path for quarantine storage.
        /// </summary>
        public string? QuarantinePath { get; set; }
    }

    /// <summary>
    /// Extended entropy analysis result with window details.
    /// </summary>
    public class EntropyAnalysisResult
    {
        public double Entropy { get; init; }
        public double NormalizedEntropy { get; init; }
        public bool IsHighEntropy { get; init; }
        public string? Assessment { get; init; }
        public IReadOnlyList<WindowEntropyResult> WindowAnalysis { get; init; } = Array.Empty<WindowEntropyResult>();
        public double HighEntropyWindowPercentage { get; init; }
    }

    /// <summary>
    /// Entropy result for a sliding window section.
    /// </summary>
    public class WindowEntropyResult
    {
        public long Offset { get; init; }
        public int Size { get; init; }
        public double Entropy { get; init; }
        public bool IsHighEntropy { get; init; }
    }

    /// <summary>
    /// Result of file type verification.
    /// </summary>
    public class FileTypeVerificationResult
    {
        public string Extension { get; init; } = string.Empty;
        public string DetectedType { get; init; } = string.Empty;
        public bool IsMismatch { get; init; }
        public double Confidence { get; init; }
        public bool MagicBytesFound { get; init; }
    }

    /// <summary>
    /// Malware signature definition.
    /// </summary>
    internal class MalwareSignature
    {
        public string Name { get; init; } = string.Empty;
        public byte[] Pattern { get; init; } = Array.Empty<byte>();
        public string ThreatType { get; init; } = string.Empty;
        public ThreatSeverity Severity { get; init; }
        public string Description { get; init; } = string.Empty;
    }

    /// <summary>
    /// Quarantined item information.
    /// </summary>
    public class QuarantinedItem
    {
        public string QuarantineId { get; init; } = string.Empty;
        public string ObjectId { get; init; } = string.Empty;
        public byte[] Content { get; init; } = Array.Empty<byte>();
        public string ContentHash { get; init; } = string.Empty;
        public IReadOnlyList<DetectedThreat> Threats { get; init; } = Array.Empty<DetectedThreat>();
        public string? Reason { get; init; }
        public DateTime QuarantinedAt { get; init; }
        public QuarantineStatus Status { get; set; }
        public string? ReviewedBy { get; set; }
        public DateTime? ReviewedAt { get; set; }
        public string? ReviewNotes { get; set; }
    }

    /// <summary>
    /// Quarantine status.
    /// </summary>
    public enum QuarantineStatus
    {
        Quarantined,
        UnderReview,
        Released,
        ConfirmedThreat,
        Deleted
    }

    /// <summary>
    /// Result of quarantine operation.
    /// </summary>
    public class QuarantineResult
    {
        public bool Success { get; init; }
        public string QuarantineId { get; init; } = string.Empty;
        public string? Message { get; init; }
    }

    /// <summary>
    /// Alert sent to registered callbacks on threat detection.
    /// </summary>
    public class ThreatAlert
    {
        public string AlertId { get; init; } = string.Empty;
        public ThreatSeverity Severity { get; init; }
        public string ObjectId { get; init; } = string.Empty;
        public IReadOnlyList<DetectedThreat> Threats { get; init; } = Array.Empty<DetectedThreat>();
        public DateTime Timestamp { get; init; }
        public string? Action { get; init; }
        public string? Details { get; init; }
    }

    /// <summary>
    /// Comprehensive threat report.
    /// </summary>
    public class ThreatReport
    {
        public string ReportId { get; init; } = string.Empty;
        public DateTime GeneratedAt { get; init; }
        public string? FileName { get; init; }
        public long FileSize { get; init; }
        public string ContentHash { get; init; } = string.Empty;
        public ThreatSeverity OverallRiskLevel { get; init; }
        public int ThreatCount { get; init; }
        public IReadOnlyList<DetectedThreat> Threats { get; init; } = Array.Empty<DetectedThreat>();
        public EntropyReportSection? EntropyAnalysis { get; init; }
        public FileTypeReportSection? FileTypeAnalysis { get; init; }
        public IReadOnlyList<string> Recommendations { get; init; } = Array.Empty<string>();
        public TimeSpan ScanDuration { get; init; }
    }

    /// <summary>
    /// Entropy section of threat report.
    /// </summary>
    public class EntropyReportSection
    {
        public double Entropy { get; init; }
        public double NormalizedEntropy { get; init; }
        public bool IsHighEntropy { get; init; }
        public string? Assessment { get; init; }
        public double HighEntropyWindowPercentage { get; init; }
    }

    /// <summary>
    /// File type section of threat report.
    /// </summary>
    public class FileTypeReportSection
    {
        public string DeclaredExtension { get; init; } = string.Empty;
        public string DetectedType { get; init; } = string.Empty;
        public bool IsMismatch { get; init; }
        public double Confidence { get; init; }
    }

    /// <summary>
    /// Access pattern tracking for behavioral analysis.
    /// </summary>
    internal class AccessPattern
    {
        public string ObjectId { get; init; } = string.Empty;
        public List<DateTime> AccessTimes { get; } = new();
        public Dictionary<string, int> UserAccessCounts { get; } = new();
        public Dictionary<string, int> IpAccessCounts { get; } = new();
        public double BaselineEntropy { get; set; }
        public long BaselineSize { get; set; }
    }

    /// <summary>
    /// Result of dedicated malware signature scanning.
    /// </summary>
    public class MalwareScanResult
    {
        /// <summary>Whether any malware was detected.</summary>
        public bool IsMalwareDetected { get; init; }

        /// <summary>Highest severity of detected malware.</summary>
        public ThreatSeverity Severity { get; init; }

        /// <summary>List of detected malware threats.</summary>
        public IReadOnlyList<DetectedThreat> DetectedThreats { get; init; } = Array.Empty<DetectedThreat>();

        /// <summary>Number of signatures checked.</summary>
        public int SignaturesChecked { get; init; }

        /// <summary>Time taken for the scan.</summary>
        public TimeSpan ScanDuration { get; init; }

        /// <summary>Recommended action based on findings.</summary>
        public string? Recommendation { get; init; }
    }

    /// <summary>
    /// Result of dedicated ransomware detection.
    /// </summary>
    public class RansomwareDetectionResult
    {
        /// <summary>Whether ransomware indicators were detected.</summary>
        public bool IsRansomwareDetected { get; init; }

        /// <summary>Overall risk score (0.0 - 1.0).</summary>
        public double RiskScore { get; init; }

        /// <summary>Highest severity of detected indicators.</summary>
        public ThreatSeverity Severity { get; init; }

        /// <summary>List of ransomware indicators found.</summary>
        public IReadOnlyList<RansomwareIndicator> Indicators { get; init; } = Array.Empty<RansomwareIndicator>();

        /// <summary>List of detected threats.</summary>
        public IReadOnlyList<DetectedThreat> Threats { get; init; } = Array.Empty<DetectedThreat>();

        /// <summary>Entropy analysis results.</summary>
        public EntropyAnalysisResult? EntropyAnalysis { get; init; }

        /// <summary>Time taken for detection.</summary>
        public TimeSpan ScanDuration { get; init; }

        /// <summary>Recommended action based on findings.</summary>
        public string? Recommendation { get; init; }
    }

    /// <summary>
    /// Individual ransomware indicator.
    /// </summary>
    public class RansomwareIndicator
    {
        /// <summary>Type of ransomware indicator.</summary>
        public RansomwareIndicatorType Type { get; init; }

        /// <summary>Description of the indicator.</summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>Confidence level (0.0 - 1.0).</summary>
        public double Confidence { get; init; }

        /// <summary>Severity of this indicator.</summary>
        public ThreatSeverity Severity { get; init; }
    }

    /// <summary>
    /// Types of ransomware indicators.
    /// </summary>
    public enum RansomwareIndicatorType
    {
        /// <summary>High entropy indicating encryption.</summary>
        HighEntropy,

        /// <summary>Known ransomware file extension.</summary>
        RansomwareExtension,

        /// <summary>Ransom note file or content detected.</summary>
        RansomNote,

        /// <summary>Partial encryption pattern detected.</summary>
        PartialEncryption,

        /// <summary>Mass file modification pattern.</summary>
        MassModification,

        /// <summary>File system traversal pattern.</summary>
        FileSystemTraversal,

        /// <summary>Shadow copy deletion attempt.</summary>
        ShadowCopyDeletion,

        /// <summary>Suspicious process behavior.</summary>
        SuspiciousProcess
    }

    /// <summary>
    /// Result of file signature (magic bytes) verification.
    /// </summary>
    public class FileSignatureResult
    {
        /// <summary>Whether the file signature is valid (matches extension).</summary>
        public bool IsValid { get; init; }

        /// <summary>Declared file extension.</summary>
        public string DeclaredExtension { get; init; } = string.Empty;

        /// <summary>File type detected from content.</summary>
        public string DetectedType { get; init; } = string.Empty;

        /// <summary>Whether there is a mismatch between extension and content.</summary>
        public bool IsMismatch { get; init; }

        /// <summary>Whether the file is a polyglot (valid as multiple formats).</summary>
        public bool IsPolyglot { get; init; }

        /// <summary>List of formats the file is valid as (for polyglots).</summary>
        public IReadOnlyList<string> PolyglotTypes { get; init; } = Array.Empty<string>();

        /// <summary>Whether magic bytes were found.</summary>
        public bool MagicBytesFound { get; init; }

        /// <summary>Confidence level of detection.</summary>
        public double Confidence { get; init; }

        /// <summary>Threat severity based on findings.</summary>
        public ThreatSeverity Severity { get; init; }

        /// <summary>Human-readable assessment.</summary>
        public string? Assessment { get; init; }

        /// <summary>Time taken for the check.</summary>
        public TimeSpan CheckDuration { get; init; }
    }

    #endregion
}
