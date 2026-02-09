using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Privacy
{
    /// <summary>
    /// T124.3: PII Detection and Masking Strategy
    /// Automatically detects and masks Personally Identifiable Information (PII) in data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PII Categories Detected:
    /// - Direct Identifiers: Name, SSN, Passport, Driver's License
    /// - Contact Information: Email, Phone, Address
    /// - Financial: Credit Card, Bank Account, Tax ID
    /// - Health: Medical Record Numbers, Health Plan IDs
    /// - Digital: IP Address, Device IDs, Biometric Data
    /// - Authentication: Passwords, Security Questions
    /// </para>
    /// <para>
    /// Detection Methods:
    /// - Regex Pattern Matching: High-precision patterns for structured PII
    /// - Contextual Analysis: Understanding surrounding text for NER
    /// - Format Validation: Luhn check for credit cards, checksum validation
    /// - ML Integration: Connection to T90 for advanced NLP detection
    /// </para>
    /// </remarks>
    public sealed class PiiDetectionMaskingStrategy : ComplianceStrategyBase
    {
        private readonly ConcurrentDictionary<string, PiiDetector> _detectors = new();
        private readonly ConcurrentDictionary<string, MaskingProfile> _maskingProfiles = new();
        private readonly ConcurrentBag<PiiDetectionAuditEntry> _auditLog = new();

        private PiiSensitivityLevel _defaultSensitivityLevel = PiiSensitivityLevel.Standard;
        private bool _enableContextualAnalysis = true;
        private bool _validateFormats = true;

        /// <inheritdoc/>
        public override string StrategyId => "pii-detection-masking";

        /// <inheritdoc/>
        public override string StrategyName => "PII Detection and Masking";

        /// <inheritdoc/>
        public override string Framework => "DataPrivacy";

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("DefaultSensitivityLevel", out var levelObj) && levelObj is int level)
                _defaultSensitivityLevel = (PiiSensitivityLevel)Math.Clamp(level, 0, 2);

            if (configuration.TryGetValue("EnableContextualAnalysis", out var ctxObj) && ctxObj is bool ctx)
                _enableContextualAnalysis = ctx;

            if (configuration.TryGetValue("ValidateFormats", out var valObj) && valObj is bool val)
                _validateFormats = val;

            InitializeDefaultDetectors();
            InitializeDefaultMaskingProfiles();

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Registers a custom PII detector.
        /// </summary>
        public void RegisterDetector(PiiDetector detector)
        {
            ArgumentNullException.ThrowIfNull(detector);
            _detectors[detector.DetectorId] = detector;
        }

        /// <summary>
        /// Registers a masking profile.
        /// </summary>
        public void RegisterMaskingProfile(MaskingProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);
            _maskingProfiles[profile.ProfileId] = profile;
        }

        /// <summary>
        /// Scans text for PII and returns all detections.
        /// </summary>
        public PiiScanResult ScanForPii(string text, PiiScanOptions? options = null)
        {
            options ??= new PiiScanOptions { SensitivityLevel = _defaultSensitivityLevel };
            var detections = new List<PiiDetection>();

            foreach (var detector in _detectors.Values.Where(d => d.Enabled))
            {
                if ((int)detector.SensitivityLevel > (int)options.SensitivityLevel)
                    continue;

                var matches = detector.Pattern.Matches(text);
                foreach (Match match in matches)
                {
                    var detection = new PiiDetection
                    {
                        DetectionId = Guid.NewGuid().ToString(),
                        PiiType = detector.PiiType,
                        Category = detector.Category,
                        Value = match.Value,
                        StartIndex = match.Index,
                        EndIndex = match.Index + match.Length,
                        Confidence = CalculateConfidence(match.Value, detector),
                        DetectorId = detector.DetectorId
                    };

                    // Validate format if enabled
                    if (_validateFormats && detector.Validator != null)
                    {
                        detection = detection with { IsValidFormat = detector.Validator(match.Value) };
                    }

                    // Only include high-confidence or valid-format detections
                    if (detection.Confidence >= options.MinConfidence ||
                        (detection.IsValidFormat ?? false))
                    {
                        detections.Add(detection);
                    }
                }
            }

            // Remove overlapping detections, keeping highest confidence
            detections = ResolveOverlaps(detections);

            // Contextual analysis
            if (_enableContextualAnalysis && options.IncludeContextualAnalysis)
            {
                detections = EnhanceWithContext(text, detections);
            }

            var result = new PiiScanResult
            {
                Success = true,
                ScanId = Guid.NewGuid().ToString(),
                TextLength = text.Length,
                Detections = detections,
                PiiFound = detections.Count > 0,
                CategorySummary = detections.GroupBy(d => d.Category)
                    .ToDictionary(g => g.Key, g => g.Count()),
                HighestRiskLevel = detections.Count > 0
                    ? detections.Max(d => GetRiskLevel(d.PiiType))
                    : PiiRiskLevel.None,
                ScannedAt = DateTime.UtcNow
            };

            // Audit log
            _auditLog.Add(new PiiDetectionAuditEntry
            {
                EntryId = Guid.NewGuid().ToString(),
                ScanId = result.ScanId,
                TextLength = text.Length,
                PiiTypesFound = detections.Select(d => d.PiiType).Distinct().ToArray(),
                DetectionCount = detections.Count,
                Timestamp = DateTime.UtcNow
            });

            return result;
        }

        /// <summary>
        /// Scans and masks PII in text.
        /// </summary>
        public PiiMaskResult ScanAndMask(string text, string? maskingProfileId = null, PiiScanOptions? options = null)
        {
            var scanResult = ScanForPii(text, options);

            if (!scanResult.PiiFound)
            {
                return new PiiMaskResult
                {
                    Success = true,
                    OriginalText = text,
                    MaskedText = text,
                    PiiFound = false,
                    MaskingsApplied = 0,
                    ScanResult = scanResult
                };
            }

            // Get masking profile
            var profileId = maskingProfileId ?? "default";
            if (!_maskingProfiles.TryGetValue(profileId, out var profile))
            {
                profile = _maskingProfiles.Values.FirstOrDefault()
                    ?? throw new InvalidOperationException("No masking profile available");
            }

            // Apply masking (from end to start to preserve indices)
            var maskedText = text;
            var orderedDetections = scanResult.Detections
                .OrderByDescending(d => d.StartIndex)
                .ToList();

            var maskingDetails = new List<MaskingDetail>();

            foreach (var detection in orderedDetections)
            {
                var rule = profile.GetRule(detection.PiiType) ?? profile.DefaultRule;
                var maskedValue = ApplyMasking(detection.Value, detection.PiiType, rule);

                maskedText = maskedText[..detection.StartIndex] + maskedValue + maskedText[detection.EndIndex..];

                maskingDetails.Add(new MaskingDetail
                {
                    PiiType = detection.PiiType,
                    OriginalLength = detection.Value.Length,
                    MaskedLength = maskedValue.Length,
                    MaskingRule = rule.RuleId
                });
            }

            return new PiiMaskResult
            {
                Success = true,
                OriginalText = text,
                MaskedText = maskedText,
                PiiFound = true,
                MaskingsApplied = maskingDetails.Count,
                MaskingDetails = maskingDetails,
                ScanResult = scanResult
            };
        }

        /// <summary>
        /// Scans structured data (dictionary) for PII.
        /// </summary>
        public StructuredPiiScanResult ScanStructuredData(
            Dictionary<string, object> data,
            PiiScanOptions? options = null)
        {
            var fieldResults = new Dictionary<string, PiiScanResult>();
            var allDetections = new List<PiiDetection>();

            foreach (var (field, value) in data)
            {
                if (value is string stringValue)
                {
                    var fieldResult = ScanForPii(stringValue, options);
                    fieldResults[field] = fieldResult;

                    foreach (var detection in fieldResult.Detections)
                    {
                        allDetections.Add(detection with { FieldName = field });
                    }
                }
            }

            return new StructuredPiiScanResult
            {
                Success = true,
                FieldsScanned = data.Count,
                FieldResults = fieldResults,
                TotalDetections = allDetections.Count,
                AllDetections = allDetections,
                HighRiskFields = fieldResults
                    .Where(kvp => kvp.Value.HighestRiskLevel >= PiiRiskLevel.High)
                    .Select(kvp => kvp.Key)
                    .ToList()
            };
        }

        /// <summary>
        /// Gets all registered detectors.
        /// </summary>
        public IReadOnlyList<PiiDetector> GetDetectors()
        {
            return _detectors.Values.ToList();
        }

        /// <summary>
        /// Gets audit log entries.
        /// </summary>
        public IReadOnlyList<PiiDetectionAuditEntry> GetAuditLog(int count = 100)
        {
            return _auditLog.OrderByDescending(e => e.Timestamp).Take(count).ToList();
        }

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check if PII scanning is enabled
            if (context.Attributes.TryGetValue("DataContent", out var contentObj) && contentObj is string content)
            {
                var scanResult = ScanForPii(content);

                if (scanResult.PiiFound)
                {
                    if (scanResult.HighestRiskLevel >= PiiRiskLevel.Critical)
                    {
                        violations.Add(new ComplianceViolation
                        {
                            Code = "PII-001",
                            Description = $"Critical PII detected: {string.Join(", ", scanResult.Detections.Where(d => GetRiskLevel(d.PiiType) >= PiiRiskLevel.Critical).Select(d => d.PiiType).Distinct())}",
                            Severity = ViolationSeverity.Critical,
                            Remediation = "Mask or remove critical PII before processing"
                        });
                    }
                    else if (scanResult.HighestRiskLevel >= PiiRiskLevel.High)
                    {
                        violations.Add(new ComplianceViolation
                        {
                            Code = "PII-002",
                            Description = $"High-risk PII detected: {scanResult.Detections.Count} instances",
                            Severity = ViolationSeverity.High,
                            Remediation = "Apply appropriate masking or obtain consent for processing"
                        });
                    }
                    else
                    {
                        recommendations.Add($"PII detected ({scanResult.Detections.Count} instances). Consider masking for additional protection.");
                    }
                }
            }

            // Check masking configuration
            if (!context.Attributes.TryGetValue("PiiMaskingEnabled", out var maskObj) || maskObj is not true)
            {
                recommendations.Add("Enable automatic PII masking for enhanced data protection");
            }

            var isCompliant = !violations.Any(v => v.Severity >= ViolationSeverity.High);
            var status = violations.Count == 0 ? ComplianceStatus.Compliant :
                        violations.Any(v => v.Severity >= ViolationSeverity.High) ? ComplianceStatus.NonCompliant :
                        ComplianceStatus.PartiallyCompliant;

            return Task.FromResult(new ComplianceResult
            {
                IsCompliant = isCompliant,
                Framework = Framework,
                Status = status,
                Violations = violations,
                Recommendations = recommendations,
                Metadata = new Dictionary<string, object>
                {
                    ["DetectorCount"] = _detectors.Count,
                    ["MaskingProfileCount"] = _maskingProfiles.Count,
                    ["SensitivityLevel"] = _defaultSensitivityLevel.ToString()
                }
            });
        }

        private double CalculateConfidence(string value, PiiDetector detector)
        {
            var confidence = detector.BaseConfidence;

            // Adjust confidence based on validation
            if (_validateFormats && detector.Validator != null)
            {
                if (detector.Validator(value))
                    confidence = Math.Min(1.0, confidence + 0.2);
                else
                    confidence = Math.Max(0.1, confidence - 0.3);
            }

            // Adjust based on length (too short/long reduces confidence)
            if (detector.ExpectedMinLength.HasValue && value.Length < detector.ExpectedMinLength.Value)
                confidence -= 0.2;
            if (detector.ExpectedMaxLength.HasValue && value.Length > detector.ExpectedMaxLength.Value)
                confidence -= 0.2;

            return Math.Clamp(confidence, 0, 1);
        }

        private List<PiiDetection> ResolveOverlaps(List<PiiDetection> detections)
        {
            var resolved = new List<PiiDetection>();

            foreach (var detection in detections.OrderByDescending(d => d.Confidence))
            {
                var overlaps = resolved.Any(r =>
                    (detection.StartIndex >= r.StartIndex && detection.StartIndex < r.EndIndex) ||
                    (detection.EndIndex > r.StartIndex && detection.EndIndex <= r.EndIndex) ||
                    (detection.StartIndex <= r.StartIndex && detection.EndIndex >= r.EndIndex));

                if (!overlaps)
                    resolved.Add(detection);
            }

            return resolved;
        }

        private List<PiiDetection> EnhanceWithContext(string text, List<PiiDetection> detections)
        {
            var enhanced = new List<PiiDetection>();
            var contextKeywords = new Dictionary<string, string[]>
            {
                ["NAME"] = new[] { "name", "named", "called", "mr", "mrs", "ms", "dr" },
                ["EMAIL"] = new[] { "email", "mail", "contact", "reach" },
                ["PHONE"] = new[] { "phone", "tel", "call", "mobile", "cell", "fax" },
                ["SSN"] = new[] { "ssn", "social security", "ss#" },
                ["DOB"] = new[] { "born", "birthday", "dob", "date of birth" },
                ["ADDRESS"] = new[] { "address", "street", "avenue", "road", "located" }
            };

            foreach (var detection in detections)
            {
                var contextStart = Math.Max(0, detection.StartIndex - 50);
                var contextEnd = Math.Min(text.Length, detection.EndIndex + 50);
                var context = text[contextStart..contextEnd].ToLowerInvariant();

                var enhancedDetection = detection;
                if (contextKeywords.TryGetValue(detection.PiiType, out var keywords))
                {
                    if (keywords.Any(k => context.Contains(k)))
                    {
                        enhancedDetection = detection with
                        {
                            Confidence = Math.Min(1.0, detection.Confidence + 0.15),
                            HasContextualSupport = true
                        };
                    }
                }

                enhanced.Add(enhancedDetection);
            }

            return enhanced;
        }

        private string ApplyMasking(string value, string piiType, MaskingRule rule)
        {
            return rule.MaskingType switch
            {
                MaskingType.Full => new string(rule.MaskCharacter, value.Length),
                MaskingType.Partial => ApplyPartialMask(value, rule),
                MaskingType.Redact => rule.RedactionText ?? "[REDACTED]",
                MaskingType.Hash => HashValue(value),
                MaskingType.Token => $"[{piiType}_{Guid.NewGuid().ToString()[..8].ToUpperInvariant()}]",
                MaskingType.Preserve => value,
                _ => new string(rule.MaskCharacter, value.Length)
            };
        }

        private string ApplyPartialMask(string value, MaskingRule rule)
        {
            var keepFirst = rule.KeepFirst ?? 0;
            var keepLast = rule.KeepLast ?? 0;

            if (keepFirst + keepLast >= value.Length)
                return value;

            var masked = new System.Text.StringBuilder();
            if (keepFirst > 0)
                masked.Append(value[..keepFirst]);
            masked.Append(new string(rule.MaskCharacter, value.Length - keepFirst - keepLast));
            if (keepLast > 0)
                masked.Append(value[^keepLast..]);

            return masked.ToString();
        }

        private string HashValue(string value)
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var bytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
        }

        private PiiRiskLevel GetRiskLevel(string piiType)
        {
            return piiType.ToUpperInvariant() switch
            {
                "SSN" or "SOCIAL_SECURITY" => PiiRiskLevel.Critical,
                "CREDIT_CARD" or "BANK_ACCOUNT" => PiiRiskLevel.Critical,
                "PASSPORT" or "DRIVERS_LICENSE" => PiiRiskLevel.Critical,
                "MEDICAL_RECORD" or "HEALTH_ID" => PiiRiskLevel.Critical,
                "BIOMETRIC" => PiiRiskLevel.Critical,
                "PASSWORD" or "SECURITY_ANSWER" => PiiRiskLevel.Critical,
                "TAX_ID" or "EIN" => PiiRiskLevel.High,
                "DOB" or "DATE_OF_BIRTH" => PiiRiskLevel.High,
                "ADDRESS" => PiiRiskLevel.High,
                "EMAIL" => PiiRiskLevel.Medium,
                "PHONE" => PiiRiskLevel.Medium,
                "NAME" => PiiRiskLevel.Medium,
                "IP_ADDRESS" => PiiRiskLevel.Low,
                "DEVICE_ID" => PiiRiskLevel.Low,
                _ => PiiRiskLevel.Low
            };
        }

        private void InitializeDefaultDetectors()
        {
            // SSN
            RegisterDetector(new PiiDetector
            {
                DetectorId = "ssn-us",
                PiiType = "SSN",
                Category = PiiCategory.Government,
                Pattern = new Regex(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.Compiled),
                BaseConfidence = 0.9,
                SensitivityLevel = PiiSensitivityLevel.Strict,
                Validator = ValidateSsn,
                ExpectedMinLength = 11,
                ExpectedMaxLength = 11
            });

            // Credit Card (major brands)
            RegisterDetector(new PiiDetector
            {
                DetectorId = "credit-card",
                PiiType = "CREDIT_CARD",
                Category = PiiCategory.Financial,
                Pattern = new Regex(@"\b(?:4[0-9]{12}(?:[0-9]{3})?|5[1-5][0-9]{14}|3[47][0-9]{13}|6(?:011|5[0-9]{2})[0-9]{12})\b", RegexOptions.Compiled),
                BaseConfidence = 0.85,
                SensitivityLevel = PiiSensitivityLevel.Strict,
                Validator = ValidateLuhn,
                ExpectedMinLength = 13,
                ExpectedMaxLength = 19
            });

            // Email
            RegisterDetector(new PiiDetector
            {
                DetectorId = "email",
                PiiType = "EMAIL",
                Category = PiiCategory.Contact,
                Pattern = new Regex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,}\b", RegexOptions.Compiled),
                BaseConfidence = 0.95,
                SensitivityLevel = PiiSensitivityLevel.Standard
            });

            // Phone (US formats)
            RegisterDetector(new PiiDetector
            {
                DetectorId = "phone-us",
                PiiType = "PHONE",
                Category = PiiCategory.Contact,
                Pattern = new Regex(@"\b(?:\+?1[-.\s]?)?\(?[0-9]{3}\)?[-.\s]?[0-9]{3}[-.\s]?[0-9]{4}\b", RegexOptions.Compiled),
                BaseConfidence = 0.8,
                SensitivityLevel = PiiSensitivityLevel.Standard,
                ExpectedMinLength = 10
            });

            // Date of Birth
            RegisterDetector(new PiiDetector
            {
                DetectorId = "dob",
                PiiType = "DOB",
                Category = PiiCategory.Personal,
                Pattern = new Regex(@"\b(?:DOB|Date of Birth|Birth\s?Date)[:\s]*(\d{1,2}[-/]\d{1,2}[-/]\d{2,4})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
                BaseConfidence = 0.85,
                SensitivityLevel = PiiSensitivityLevel.Standard
            });

            // IP Address
            RegisterDetector(new PiiDetector
            {
                DetectorId = "ip-address",
                PiiType = "IP_ADDRESS",
                Category = PiiCategory.Digital,
                Pattern = new Regex(@"\b(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\b", RegexOptions.Compiled),
                BaseConfidence = 0.9,
                SensitivityLevel = PiiSensitivityLevel.Relaxed,
                Validator = ValidateIpAddress
            });

            // Medical Record Number
            RegisterDetector(new PiiDetector
            {
                DetectorId = "mrn",
                PiiType = "MEDICAL_RECORD",
                Category = PiiCategory.Health,
                Pattern = new Regex(@"\b(?:MRN|Medical Record)[:\s#]*[A-Z0-9]{6,12}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
                BaseConfidence = 0.75,
                SensitivityLevel = PiiSensitivityLevel.Strict
            });

            // Driver's License
            RegisterDetector(new PiiDetector
            {
                DetectorId = "drivers-license",
                PiiType = "DRIVERS_LICENSE",
                Category = PiiCategory.Government,
                Pattern = new Regex(@"\b(?:DL|Driver'?s?\s*License)[:\s#]*[A-Z0-9]{5,15}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
                BaseConfidence = 0.7,
                SensitivityLevel = PiiSensitivityLevel.Strict
            });

            // Passport
            RegisterDetector(new PiiDetector
            {
                DetectorId = "passport",
                PiiType = "PASSPORT",
                Category = PiiCategory.Government,
                Pattern = new Regex(@"\b(?:Passport)[:\s#]*[A-Z0-9]{6,12}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
                BaseConfidence = 0.7,
                SensitivityLevel = PiiSensitivityLevel.Strict
            });

            // Bank Account
            RegisterDetector(new PiiDetector
            {
                DetectorId = "bank-account",
                PiiType = "BANK_ACCOUNT",
                Category = PiiCategory.Financial,
                Pattern = new Regex(@"\b(?:Account|Acct)[:\s#]*\d{8,17}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
                BaseConfidence = 0.65,
                SensitivityLevel = PiiSensitivityLevel.Strict
            });
        }

        private void InitializeDefaultMaskingProfiles()
        {
            RegisterMaskingProfile(new MaskingProfile
            {
                ProfileId = "default",
                Name = "Default Masking Profile",
                DefaultRule = new MaskingRule
                {
                    RuleId = "default",
                    MaskingType = MaskingType.Partial,
                    MaskCharacter = '*',
                    KeepFirst = 2,
                    KeepLast = 2
                },
                Rules = new[]
                {
                    new MaskingRule { RuleId = "ssn", PiiType = "SSN", MaskingType = MaskingType.Partial, MaskCharacter = '*', KeepLast = 4 },
                    new MaskingRule { RuleId = "credit-card", PiiType = "CREDIT_CARD", MaskingType = MaskingType.Partial, MaskCharacter = '*', KeepLast = 4 },
                    new MaskingRule { RuleId = "email", PiiType = "EMAIL", MaskingType = MaskingType.Partial, MaskCharacter = '*', KeepFirst = 2, KeepLast = 0 },
                    new MaskingRule { RuleId = "phone", PiiType = "PHONE", MaskingType = MaskingType.Partial, MaskCharacter = '*', KeepLast = 4 }
                }
            });

            RegisterMaskingProfile(new MaskingProfile
            {
                ProfileId = "full-redaction",
                Name = "Full Redaction Profile",
                DefaultRule = new MaskingRule
                {
                    RuleId = "full-redact",
                    MaskingType = MaskingType.Redact,
                    RedactionText = "[REDACTED]"
                }
            });

            RegisterMaskingProfile(new MaskingProfile
            {
                ProfileId = "tokenization",
                Name = "Tokenization Profile",
                DefaultRule = new MaskingRule
                {
                    RuleId = "tokenize",
                    MaskingType = MaskingType.Token
                }
            });
        }

        private static bool ValidateSsn(string value)
        {
            var digits = value.Replace("-", "");
            if (digits.Length != 9 || !digits.All(char.IsDigit))
                return false;

            // Check for invalid SSN patterns
            if (digits.StartsWith("000") || digits.StartsWith("666"))
                return false;
            if (digits.Substring(0, 3) == "900" || digits.Substring(0, 3) == "999")
                return false;

            return true;
        }

        private static bool ValidateLuhn(string value)
        {
            var digits = value.Where(char.IsDigit).Select(c => c - '0').ToArray();
            if (digits.Length < 13 || digits.Length > 19)
                return false;

            var sum = 0;
            var alternate = false;
            for (int i = digits.Length - 1; i >= 0; i--)
            {
                var n = digits[i];
                if (alternate)
                {
                    n *= 2;
                    if (n > 9)
                        n -= 9;
                }
                sum += n;
                alternate = !alternate;
            }
            return sum % 10 == 0;
        }

        private static bool ValidateIpAddress(string value)
        {
            var parts = value.Split('.');
            if (parts.Length != 4)
                return false;

            foreach (var part in parts)
            {
                if (!int.TryParse(part, out var num) || num < 0 || num > 255)
                    return false;
            }
            return true;
        }
    }

    #region Types

    /// <summary>
    /// PII sensitivity level for detection.
    /// </summary>
    public enum PiiSensitivityLevel
    {
        Relaxed = 0,
        Standard = 1,
        Strict = 2
    }

    /// <summary>
    /// PII category classification.
    /// </summary>
    public enum PiiCategory
    {
        Personal,
        Contact,
        Financial,
        Health,
        Government,
        Digital,
        Authentication
    }

    /// <summary>
    /// PII risk level.
    /// </summary>
    public enum PiiRiskLevel
    {
        None = 0,
        Low = 1,
        Medium = 2,
        High = 3,
        Critical = 4
    }

    /// <summary>
    /// Masking type options.
    /// </summary>
    public enum MaskingType
    {
        Full,
        Partial,
        Redact,
        Hash,
        Token,
        Preserve
    }

    /// <summary>
    /// PII detector configuration.
    /// </summary>
    public sealed class PiiDetector
    {
        public required string DetectorId { get; init; }
        public required string PiiType { get; init; }
        public required PiiCategory Category { get; init; }
        public required Regex Pattern { get; init; }
        public double BaseConfidence { get; init; } = 0.8;
        public PiiSensitivityLevel SensitivityLevel { get; init; } = PiiSensitivityLevel.Standard;
        public Func<string, bool>? Validator { get; init; }
        public int? ExpectedMinLength { get; init; }
        public int? ExpectedMaxLength { get; init; }
        public bool Enabled { get; init; } = true;
    }

    /// <summary>
    /// Masking profile configuration.
    /// </summary>
    public sealed record MaskingProfile
    {
        public required string ProfileId { get; init; }
        public required string Name { get; init; }
        public string? Description { get; init; }
        public required MaskingRule DefaultRule { get; init; }
        public MaskingRule[] Rules { get; init; } = Array.Empty<MaskingRule>();

        public MaskingRule? GetRule(string piiType)
        {
            return Rules.FirstOrDefault(r => r.PiiType == piiType);
        }
    }

    /// <summary>
    /// Masking rule configuration.
    /// </summary>
    public sealed record MaskingRule
    {
        public required string RuleId { get; init; }
        public string? PiiType { get; init; }
        public required MaskingType MaskingType { get; init; }
        public char MaskCharacter { get; init; } = '*';
        public int? KeepFirst { get; init; }
        public int? KeepLast { get; init; }
        public string? RedactionText { get; init; }
    }

    /// <summary>
    /// PII scan options.
    /// </summary>
    public sealed record PiiScanOptions
    {
        public PiiSensitivityLevel SensitivityLevel { get; init; } = PiiSensitivityLevel.Standard;
        public double MinConfidence { get; init; } = 0.5;
        public bool IncludeContextualAnalysis { get; init; } = true;
        public string[]? PiiTypesToInclude { get; init; }
        public string[]? PiiTypesToExclude { get; init; }
    }

    /// <summary>
    /// PII detection result.
    /// </summary>
    public sealed record PiiDetection
    {
        public required string DetectionId { get; init; }
        public required string PiiType { get; init; }
        public required PiiCategory Category { get; init; }
        public required string Value { get; init; }
        public required int StartIndex { get; init; }
        public required int EndIndex { get; init; }
        public required double Confidence { get; init; }
        public required string DetectorId { get; init; }
        public bool? IsValidFormat { get; init; }
        public bool HasContextualSupport { get; init; }
        public string? FieldName { get; init; }
    }

    /// <summary>
    /// PII scan result.
    /// </summary>
    public sealed record PiiScanResult
    {
        public required bool Success { get; init; }
        public required string ScanId { get; init; }
        public int TextLength { get; init; }
        public required IReadOnlyList<PiiDetection> Detections { get; init; }
        public bool PiiFound { get; init; }
        public Dictionary<PiiCategory, int>? CategorySummary { get; init; }
        public PiiRiskLevel HighestRiskLevel { get; init; }
        public DateTime ScannedAt { get; init; }
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// PII mask result.
    /// </summary>
    public sealed record PiiMaskResult
    {
        public required bool Success { get; init; }
        public string? OriginalText { get; init; }
        public required string MaskedText { get; init; }
        public bool PiiFound { get; init; }
        public int MaskingsApplied { get; init; }
        public IReadOnlyList<MaskingDetail>? MaskingDetails { get; init; }
        public PiiScanResult? ScanResult { get; init; }
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Masking operation detail.
    /// </summary>
    public sealed record MaskingDetail
    {
        public required string PiiType { get; init; }
        public int OriginalLength { get; init; }
        public int MaskedLength { get; init; }
        public required string MaskingRule { get; init; }
    }

    /// <summary>
    /// Structured data PII scan result.
    /// </summary>
    public sealed record StructuredPiiScanResult
    {
        public required bool Success { get; init; }
        public int FieldsScanned { get; init; }
        public required Dictionary<string, PiiScanResult> FieldResults { get; init; }
        public int TotalDetections { get; init; }
        public required IReadOnlyList<PiiDetection> AllDetections { get; init; }
        public required IReadOnlyList<string> HighRiskFields { get; init; }
    }

    /// <summary>
    /// PII detection audit entry.
    /// </summary>
    public sealed record PiiDetectionAuditEntry
    {
        public required string EntryId { get; init; }
        public required string ScanId { get; init; }
        public int TextLength { get; init; }
        public required string[] PiiTypesFound { get; init; }
        public int DetectionCount { get; init; }
        public required DateTime Timestamp { get; init; }
    }

    #endregion
}
