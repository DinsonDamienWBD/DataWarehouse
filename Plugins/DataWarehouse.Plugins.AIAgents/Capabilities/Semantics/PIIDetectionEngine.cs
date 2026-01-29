// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.SDK.AI;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using IExtendedAIProvider = DataWarehouse.Plugins.AIAgents.IExtendedAIProvider;

namespace DataWarehouse.Plugins.AIAgents.Capabilities.Semantics;

/// <summary>
/// Engine for detecting Personally Identifiable Information (PII) in text.
/// Supports pattern-based detection with validation (Luhn, checksums),
/// AI-enhanced detection, and compliance assessment.
/// </summary>
public sealed class PIIDetectionEngine
{
    private IExtendedAIProvider? _aiProvider;
    private readonly ConcurrentDictionary<string, CustomPIIPattern> _customPatterns;
    private readonly Dictionary<PIIType, PIIPatternSet> _builtInPatterns;

    /// <summary>
    /// Initializes a new instance of the <see cref="PIIDetectionEngine"/> class.
    /// </summary>
    /// <param name="aiProvider">Optional AI provider for enhanced detection.</param>
    public PIIDetectionEngine(IExtendedAIProvider? aiProvider = null)
    {
        _aiProvider = aiProvider;
        _customPatterns = new ConcurrentDictionary<string, CustomPIIPattern>();
        _builtInPatterns = InitializeBuiltInPatterns();
    }

    /// <summary>
    /// Sets or updates the AI provider.
    /// </summary>
    /// <param name="provider">The AI provider to use.</param>
    public void SetProvider(IExtendedAIProvider? provider)
    {
        _aiProvider = provider;
    }

    /// <summary>
    /// Gets whether an AI provider is available.
    /// </summary>
    public bool IsAIAvailable => _aiProvider?.IsAvailable ?? false;

    /// <summary>
    /// Detects PII in the given text.
    /// </summary>
    /// <param name="text">Text to analyze.</param>
    /// <param name="config">Detection configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>PII detection result.</returns>
    public async Task<PIIDetectionResult> DetectAsync(
        string text,
        PIIDetectionConfig? config = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        config ??= new PIIDetectionConfig();

        if (string.IsNullOrWhiteSpace(text))
        {
            return new PIIDetectionResult
            {
                Duration = sw.Elapsed,
                RiskLevel = PIIRiskLevel.None,
                RiskScore = 0
            };
        }

        var piiItems = new List<DetectedPII>();

        try
        {
            // Pattern-based detection (always run for high-precision patterns)
            var patternPII = DetectWithPatterns(text, config);
            piiItems.AddRange(patternPII);

            // AI-enhanced detection if enabled and available
            if (config.EnableAI && IsAIAvailable)
            {
                var aiPII = await DetectWithAIAsync(text, config, ct);

                // Merge AI results that don't overlap with pattern results
                foreach (var aiItem in aiPII)
                {
                    var overlaps = piiItems.Any(p =>
                        (aiItem.StartPosition >= p.StartPosition &&
                         aiItem.StartPosition < p.StartPosition + p.Length) ||
                        (p.StartPosition >= aiItem.StartPosition &&
                         p.StartPosition < aiItem.StartPosition + aiItem.Length));

                    if (!overlaps)
                    {
                        piiItems.Add(aiItem);
                    }
                }
            }

            // Apply confidence filtering
            piiItems = piiItems
                .Where(p => p.Confidence >= config.MinConfidenceThreshold)
                .OrderBy(p => p.StartPosition)
                .ToList();

            // Add context if requested
            if (config.IncludeContext)
            {
                piiItems = piiItems
                    .Select(p => p with { Context = GetContext(text, p.StartPosition, p.Length, config.ContextWindowSize) })
                    .ToList();
            }

            // Calculate risk
            var (riskLevel, riskScore) = CalculateRisk(piiItems);

            // Generate compliance implications
            var complianceImplications = config.CheckCompliance
                ? GenerateComplianceImplications(piiItems, config.ComplianceRegulations)
                : new List<ComplianceImplication>();

            // Generate redaction recommendations
            var redactionRecommendations = config.GenerateRedactions
                ? GenerateRedactionRecommendations(piiItems)
                : new List<RedactionRecommendation>();

            sw.Stop();

            return new PIIDetectionResult
            {
                PIIItems = piiItems,
                RiskLevel = riskLevel,
                RiskScore = riskScore,
                UsedAI = config.EnableAI && IsAIAvailable,
                Duration = sw.Elapsed,
                ComplianceImplications = complianceImplications,
                RedactionRecommendations = redactionRecommendations
            };
        }
        catch (Exception)
        {
            sw.Stop();
            // Fall back to pattern-only detection
            var fallbackItems = DetectWithPatterns(text, config);
            var (riskLevel, riskScore) = CalculateRisk(fallbackItems);

            return new PIIDetectionResult
            {
                PIIItems = fallbackItems,
                RiskLevel = riskLevel,
                RiskScore = riskScore,
                UsedAI = false,
                Duration = sw.Elapsed
            };
        }
    }

    /// <summary>
    /// Registers a custom PII pattern.
    /// </summary>
    /// <param name="pattern">Custom pattern definition.</param>
    public void RegisterCustomPattern(CustomPIIPattern pattern)
    {
        _customPatterns[pattern.Id] = pattern;
    }

    /// <summary>
    /// Unregisters a custom PII pattern.
    /// </summary>
    /// <param name="patternId">ID of the pattern to remove.</param>
    public void UnregisterCustomPattern(string patternId)
    {
        _customPatterns.TryRemove(patternId, out _);
    }

    /// <summary>
    /// Detects and redacts PII from text in a single operation.
    /// </summary>
    /// <param name="text">Text to redact.</param>
    /// <param name="config">Detection configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Redaction result with original and redacted text.</returns>
    public async Task<PIIRedactionResult> RedactAsync(
        string text,
        PIIDetectionConfig? config = null,
        CancellationToken ct = default)
    {
        config ??= new PIIDetectionConfig { GenerateRedactions = true };

        var detectionResult = await DetectAsync(text, config, ct);

        if (detectionResult.RedactionRecommendations.Count == 0)
        {
            return new PIIRedactionResult
            {
                OriginalText = text,
                RedactedText = text,
                RedactionCount = 0,
                DetectionResult = detectionResult
            };
        }

        var redactedText = ApplyRedactions(text, detectionResult.RedactionRecommendations);

        return new PIIRedactionResult
        {
            OriginalText = text,
            RedactedText = redactedText,
            RedactionCount = detectionResult.RedactionRecommendations.Count,
            DetectionResult = detectionResult
        };
    }

    /// <summary>
    /// Redacts PII from text using recommendations.
    /// </summary>
    /// <param name="text">Original text.</param>
    /// <param name="recommendations">Redaction recommendations.</param>
    /// <returns>Redacted text.</returns>
    public string ApplyRedactions(string text, IEnumerable<RedactionRecommendation> recommendations)
    {
        var sorted = recommendations.OrderByDescending(r => r.StartPosition).ToList();
        var result = text;

        foreach (var rec in sorted)
        {
            if (rec.StartPosition >= 0 && rec.StartPosition + rec.Length <= result.Length)
            {
                result = result[..rec.StartPosition] + rec.Replacement + result[(rec.StartPosition + rec.Length)..];
            }
        }

        return result;
    }

    /// <summary>
    /// Detects PII using pattern matching and validation.
    /// </summary>
    private List<DetectedPII> DetectWithPatterns(string text, PIIDetectionConfig config)
    {
        var results = new List<DetectedPII>();

        // Built-in patterns
        foreach (var (piiType, patternSet) in _builtInPatterns)
        {
            if (config.EnabledTypes.Count > 0 && !config.EnabledTypes.Contains(piiType))
                continue;

            foreach (var pattern in patternSet.Patterns)
            {
                try
                {
                    var matches = pattern.Matches(text);
                    foreach (Match match in matches)
                    {
                        var confidence = GetPatternConfidence(piiType, match.Value, patternSet.Validator);

                        if (confidence >= config.MinConfidenceThreshold)
                        {
                            results.Add(new DetectedPII
                            {
                                Text = MaskSensitiveText(match.Value, piiType),
                                OriginalText = match.Value,
                                Type = piiType,
                                Confidence = confidence,
                                StartPosition = match.Index,
                                Length = match.Length,
                                RiskLevel = patternSet.RiskLevel,
                                DetectionMethod = patternSet.Validator != null
                                    ? PIIDetectionMethod.ChecksumValidation
                                    : PIIDetectionMethod.Pattern,
                                Regulations = patternSet.Regulations.ToList()
                            });
                        }
                    }
                }
                catch { /* Invalid regex or timeout */ }
            }
        }

        // Custom patterns
        foreach (var customPattern in _customPatterns.Values)
        {
            if (config.EnabledTypes.Count > 0 && !config.EnabledTypes.Contains(customPattern.Type))
                continue;

            try
            {
                var pattern = new Regex(customPattern.Pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
                var matches = pattern.Matches(text);

                foreach (Match match in matches)
                {
                    var isValid = customPattern.Validator?.Invoke(match.Value) ?? true;

                    if (isValid)
                    {
                        results.Add(new DetectedPII
                        {
                            Text = MaskSensitiveText(match.Value, customPattern.Type),
                            OriginalText = match.Value,
                            Type = customPattern.Type,
                            Confidence = 0.85f,
                            StartPosition = match.Index,
                            Length = match.Length,
                            RiskLevel = customPattern.RiskLevel,
                            DetectionMethod = customPattern.Validator != null
                                ? PIIDetectionMethod.ChecksumValidation
                                : PIIDetectionMethod.Pattern,
                            Regulations = customPattern.Regulations.ToList()
                        });
                    }
                }
            }
            catch { /* Invalid regex or timeout */ }
        }

        // Remove overlapping detections (keep highest confidence)
        return RemoveOverlapping(results);
    }

    /// <summary>
    /// Detects PII using AI.
    /// </summary>
    private async Task<List<DetectedPII>> DetectWithAIAsync(
        string text,
        PIIDetectionConfig config,
        CancellationToken ct)
    {
        if (_aiProvider == null || !_aiProvider.IsAvailable)
            return new List<DetectedPII>();

        var truncatedText = text.Length > 3000 ? text[..3000] : text;

        var prompt = $@"Identify all Personally Identifiable Information (PII) in this text.

Text:
{truncatedText}

For each PII found, respond in this exact format (one per line):
PII: [exact text] | TYPE: [type] | CONFIDENCE: [0.0-1.0] | POSITION: [start char position]

PII types to detect:
- FullName, FirstName, LastName, Username
- SSN, NationalId, PassportNumber, DriversLicense, TaxId
- Email, PhoneNumber, Address, PostalCode
- CreditCard, BankAccount, RoutingNumber, IBAN
- MedicalRecordNumber, HealthInsuranceId, MedicalCondition
- DateOfBirth, Age, Gender, RaceEthnicity
- IPAddress, MACAddress, DeviceId, Password, APIKey
- EmployeeId, Salary

Only include clear PII instances with high confidence. Do not include generic names or ambiguous data.";

        var response = await _aiProvider.CompleteAsync(new AIRequest
        {
            Prompt = prompt,
            SystemMessage = "You are a PII detection system. Identify personally identifiable information accurately and precisely.",
            MaxTokens = 800,
            Temperature = 0.1f
        }, ct);

        if (!response.Success)
            return new List<DetectedPII>();

        return ParseAIPIIResponse(response.Content, text);
    }

    /// <summary>
    /// Parses AI response into PII items.
    /// </summary>
    private List<DetectedPII> ParseAIPIIResponse(string response, string originalText)
    {
        var results = new List<DetectedPII>();
        var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            if (!line.Contains("PII:"))
                continue;

            try
            {
                var parts = line.Split('|');
                if (parts.Length < 2)
                    continue;

                var piiText = parts[0].Replace("PII:", "").Trim();
                var typeStr = parts.Length > 1 ? parts[1].Replace("TYPE:", "").Trim() : "Unknown";
                var confidenceStr = parts.Length > 2 ? parts[2].Replace("CONFIDENCE:", "").Trim() : "0.8";

                if (!Enum.TryParse<PIIType>(typeStr, true, out var piiType))
                    piiType = PIIType.Unknown;

                if (!float.TryParse(confidenceStr, out var confidence))
                    confidence = 0.8f;

                // Find position in original text
                var position = originalText.IndexOf(piiText, StringComparison.Ordinal);
                if (position < 0)
                    position = originalText.IndexOf(piiText, StringComparison.OrdinalIgnoreCase);

                if (position >= 0)
                {
                    results.Add(new DetectedPII
                    {
                        Text = MaskSensitiveText(piiText, piiType),
                        OriginalText = piiText,
                        Type = piiType,
                        Confidence = Math.Clamp(confidence, 0, 1),
                        StartPosition = position,
                        Length = piiText.Length,
                        RiskLevel = GetRiskLevelForType(piiType),
                        DetectionMethod = PIIDetectionMethod.AI
                    });
                }
            }
            catch { /* Skip malformed lines */ }
        }

        return results;
    }

    /// <summary>
    /// Initializes built-in PII patterns with validators.
    /// </summary>
    private Dictionary<PIIType, PIIPatternSet> InitializeBuiltInPatterns()
    {
        return new Dictionary<PIIType, PIIPatternSet>
        {
            // Social Security Number (US)
            [PIIType.SSN] = new PIIPatternSet
            {
                Patterns = new[]
                {
                    CreateRegex(@"\b\d{3}[-\s]?\d{2}[-\s]?\d{4}\b")
                },
                Validator = ValidateSSN,
                RiskLevel = PIIRiskLevel.Critical,
                Regulations = new[] { "CCPA", "SOC2" }
            },

            // Credit Card Numbers
            [PIIType.CreditCard] = new PIIPatternSet
            {
                Patterns = new[]
                {
                    CreateRegex(@"\b(?:4[0-9]{12}(?:[0-9]{3})?|5[1-5][0-9]{14}|3[47][0-9]{13}|6(?:011|5[0-9]{2})[0-9]{12})\b"),
                    CreateRegex(@"\b\d{4}[-\s]?\d{4}[-\s]?\d{4}[-\s]?\d{4}\b")
                },
                Validator = ValidateLuhn,
                RiskLevel = PIIRiskLevel.Critical,
                Regulations = new[] { "PCI-DSS", "GDPR" }
            },

            // Email Addresses
            [PIIType.Email] = new PIIPatternSet
            {
                Patterns = new[]
                {
                    CreateRegex(@"\b[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}\b")
                },
                RiskLevel = PIIRiskLevel.Medium,
                Regulations = new[] { "GDPR", "CCPA" }
            },

            // Phone Numbers
            [PIIType.PhoneNumber] = new PIIPatternSet
            {
                Patterns = new[]
                {
                    CreateRegex(@"\+?1?[-.\s]?\(?[0-9]{3}\)?[-.\s]?[0-9]{3}[-.\s]?[0-9]{4}"),
                    CreateRegex(@"\+[0-9]{1,3}[-.\s]?[0-9]{6,14}")
                },
                RiskLevel = PIIRiskLevel.Medium,
                Regulations = new[] { "GDPR", "TCPA" }
            },

            // Physical Addresses
            [PIIType.Address] = new PIIPatternSet
            {
                Patterns = new[]
                {
                    CreateRegex(@"\d{1,5}\s+[\w\s]+(?:Street|St|Avenue|Ave|Road|Rd|Boulevard|Blvd|Drive|Dr|Lane|Ln|Court|Ct|Way|Circle|Cir)\b", RegexOptions.IgnoreCase)
                },
                RiskLevel = PIIRiskLevel.High,
                Regulations = new[] { "GDPR", "CCPA" }
            },

            // Postal Codes
            [PIIType.PostalCode] = new PIIPatternSet
            {
                Patterns = new[]
                {
                    CreateRegex(@"\b\d{5}(?:-\d{4})?\b"), // US ZIP
                    CreateRegex(@"\b[A-Z]{1,2}\d{1,2}[A-Z]?\s?\d[A-Z]{2}\b", RegexOptions.IgnoreCase) // UK Postal
                },
                RiskLevel = PIIRiskLevel.Low,
                Regulations = new[] { "GDPR" }
            },

            // Date of Birth
            [PIIType.DateOfBirth] = new PIIPatternSet
            {
                Patterns = new[]
                {
                    CreateRegex(@"\b(?:DOB|Date\s*of\s*Birth|Born|Birthday)\s*:?\s*\d{1,2}[/-]\d{1,2}[/-]\d{2,4}\b", RegexOptions.IgnoreCase),
                    CreateRegex(@"\b(?:DOB|Date\s*of\s*Birth|Born)\s*:?\s*(?:January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{1,2},?\s+\d{4}\b", RegexOptions.IgnoreCase)
                },
                RiskLevel = PIIRiskLevel.High,
                Regulations = new[] { "GDPR", "HIPAA" }
            },

            // IP Addresses
            [PIIType.IPAddress] = new PIIPatternSet
            {
                Patterns = new[]
                {
                    CreateRegex(@"\b(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\b"),
                    CreateRegex(@"\b(?:[0-9a-fA-F]{1,4}:){7}[0-9a-fA-F]{1,4}\b") // IPv6
                },
                RiskLevel = PIIRiskLevel.Medium,
                Regulations = new[] { "GDPR" }
            },

            // Passport Numbers
            [PIIType.PassportNumber] = new PIIPatternSet
            {
                Patterns = new[]
                {
                    CreateRegex(@"\b[A-Z]{1,2}\d{6,9}\b") // Generic passport format
                },
                RiskLevel = PIIRiskLevel.Critical,
                Regulations = new[] { "GDPR" }
            },

            // Driver's License
            [PIIType.DriversLicense] = new PIIPatternSet
            {
                Patterns = new[]
                {
                    CreateRegex(@"\b[A-Z]\d{7,8}\b"), // Common US format
                    CreateRegex(@"\b\d{3}-\d{3}-\d{3}\b") // Some state formats
                },
                RiskLevel = PIIRiskLevel.High,
                Regulations = new[] { "GDPR", "CCPA" }
            },

            // Bank Account Numbers
            [PIIType.BankAccount] = new PIIPatternSet
            {
                Patterns = new[]
                {
                    CreateRegex(@"\b\d{8,17}\b") // Generic bank account
                },
                RiskLevel = PIIRiskLevel.Critical,
                Regulations = new[] { "GDPR", "PCI-DSS" }
            },

            // IBAN
            [PIIType.IBAN] = new PIIPatternSet
            {
                Patterns = new[]
                {
                    CreateRegex(@"\b[A-Z]{2}\d{2}[\sA-Z0-9]{11,30}\b")
                },
                Validator = ValidateIBAN,
                RiskLevel = PIIRiskLevel.Critical,
                Regulations = new[] { "GDPR", "PCI-DSS" }
            },

            // Medical Record Number
            [PIIType.MedicalRecordNumber] = new PIIPatternSet
            {
                Patterns = new[]
                {
                    CreateRegex(@"\b(?:MRN|Medical\s*Record)\s*:?\s*\d{6,12}\b", RegexOptions.IgnoreCase)
                },
                RiskLevel = PIIRiskLevel.Critical,
                Regulations = new[] { "HIPAA" }
            },

            // API Keys / Secrets
            [PIIType.APIKey] = new PIIPatternSet
            {
                Patterns = new[]
                {
                    CreateRegex(@"\b(?:sk|pk|api)[-_][a-zA-Z0-9]{20,50}\b", RegexOptions.IgnoreCase),
                    CreateRegex(@"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b"), // AWS Access Key
                    CreateRegex(@"ghp_[a-zA-Z0-9]{36}"), // GitHub Personal Access Token
                    CreateRegex(@"xox[baprs]-[a-zA-Z0-9-]+") // Slack Token
                },
                RiskLevel = PIIRiskLevel.Critical,
                Regulations = new[] { "SOC2" }
            },

            // Passwords (in common formats)
            [PIIType.Password] = new PIIPatternSet
            {
                Patterns = new[]
                {
                    CreateRegex(@"(?:password|passwd|pwd)\s*[:=]\s*\S+", RegexOptions.IgnoreCase)
                },
                RiskLevel = PIIRiskLevel.Critical,
                Regulations = new[] { "SOC2", "PCI-DSS" }
            }
        };
    }

    private Regex CreateRegex(string pattern, RegexOptions additionalOptions = RegexOptions.None)
    {
        return new Regex(pattern, RegexOptions.Compiled | additionalOptions, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Validates US Social Security Number format and range.
    /// </summary>
    private bool ValidateSSN(string ssn)
    {
        var digits = new string(ssn.Where(char.IsDigit).ToArray());
        if (digits.Length != 9)
            return false;

        // Area number cannot be 000, 666, or 900-999
        var area = int.Parse(digits[..3]);
        if (area == 0 || area == 666 || area >= 900)
            return false;

        // Group number cannot be 00
        var group = int.Parse(digits[3..5]);
        if (group == 0)
            return false;

        // Serial number cannot be 0000
        var serial = int.Parse(digits[5..]);
        if (serial == 0)
            return false;

        return true;
    }

    /// <summary>
    /// Validates credit card number using Luhn algorithm.
    /// </summary>
    private bool ValidateLuhn(string number)
    {
        var digits = new string(number.Where(char.IsDigit).ToArray());
        if (digits.Length < 13 || digits.Length > 19)
            return false;

        var sum = 0;
        var alternate = false;

        for (int i = digits.Length - 1; i >= 0; i--)
        {
            var digit = digits[i] - '0';

            if (alternate)
            {
                digit *= 2;
                if (digit > 9)
                    digit -= 9;
            }

            sum += digit;
            alternate = !alternate;
        }

        return sum % 10 == 0;
    }

    /// <summary>
    /// Basic IBAN validation (structure and checksum).
    /// </summary>
    private bool ValidateIBAN(string iban)
    {
        var cleaned = iban.Replace(" ", "").ToUpperInvariant();
        if (cleaned.Length < 15 || cleaned.Length > 34)
            return false;

        // Move first 4 chars to end
        var rearranged = cleaned[4..] + cleaned[..4];

        // Convert letters to numbers (A=10, B=11, etc.)
        var numeric = string.Concat(rearranged.Select(c =>
            char.IsLetter(c) ? (c - 'A' + 10).ToString() : c.ToString()));

        // Mod 97 check
        try
        {
            var remainder = 0;
            foreach (var c in numeric)
            {
                remainder = (remainder * 10 + (c - '0')) % 97;
            }
            return remainder == 1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets pattern confidence, higher if validator passes.
    /// </summary>
    private float GetPatternConfidence(PIIType type, string value, Func<string, bool>? validator)
    {
        var baseConfidence = type switch
        {
            PIIType.Email => 0.95f,
            PIIType.CreditCard => 0.80f, // Before validation
            PIIType.SSN => 0.80f,
            PIIType.PhoneNumber => 0.85f,
            PIIType.IPAddress => 0.95f,
            PIIType.APIKey => 0.90f,
            _ => 0.75f
        };

        if (validator != null)
        {
            return validator(value) ? Math.Min(0.98f, baseConfidence + 0.15f) : baseConfidence * 0.5f;
        }

        return baseConfidence;
    }

    /// <summary>
    /// Gets risk level for a PII type.
    /// </summary>
    private PIIRiskLevel GetRiskLevelForType(PIIType type)
    {
        return type switch
        {
            PIIType.SSN or PIIType.CreditCard or PIIType.BankAccount or
            PIIType.PassportNumber or PIIType.APIKey or PIIType.Password or
            PIIType.MedicalRecordNumber => PIIRiskLevel.Critical,

            PIIType.DriversLicense or PIIType.DateOfBirth or PIIType.Address or
            PIIType.HealthInsuranceId => PIIRiskLevel.High,

            PIIType.Email or PIIType.PhoneNumber or PIIType.IPAddress => PIIRiskLevel.Medium,

            PIIType.PostalCode or PIIType.Age => PIIRiskLevel.Low,

            _ => PIIRiskLevel.Medium
        };
    }

    /// <summary>
    /// Masks sensitive text for display.
    /// </summary>
    private string MaskSensitiveText(string text, PIIType type)
    {
        if (string.IsNullOrEmpty(text) || text.Length < 4)
            return new string('*', text?.Length ?? 0);

        return type switch
        {
            PIIType.SSN => $"***-**-{text[^4..]}",
            PIIType.CreditCard => $"****-****-****-{text[^4..]}",
            PIIType.Email => MaskEmail(text),
            PIIType.PhoneNumber => $"***-***-{text[^4..]}",
            PIIType.BankAccount or PIIType.IBAN => $"{text[..2]}***{text[^2..]}",
            PIIType.APIKey or PIIType.Password => $"{text[..4]}...{text[^4..]}",
            _ => text.Length > 6 ? $"{text[..2]}***{text[^2..]}" : new string('*', text.Length)
        };
    }

    private string MaskEmail(string email)
    {
        var parts = email.Split('@');
        if (parts.Length != 2)
            return "***@***.***";

        var local = parts[0];
        var domain = parts[1];

        var maskedLocal = local.Length <= 2 ? "***" : $"{local[0]}***{local[^1]}";
        return $"{maskedLocal}@{domain}";
    }

    /// <summary>
    /// Calculates overall risk level and score.
    /// </summary>
    private (PIIRiskLevel Level, int Score) CalculateRisk(List<DetectedPII> items)
    {
        if (items.Count == 0)
            return (PIIRiskLevel.None, 0);

        var maxRisk = items.Max(p => p.RiskLevel);

        // Calculate score based on type and count
        var score = items.Sum(p => p.RiskLevel switch
        {
            PIIRiskLevel.Critical => 25,
            PIIRiskLevel.High => 15,
            PIIRiskLevel.Medium => 8,
            PIIRiskLevel.Low => 3,
            _ => 1
        });

        score = Math.Min(100, score);

        return (maxRisk, score);
    }

    /// <summary>
    /// Generates compliance implications based on detected PII.
    /// </summary>
    private List<ComplianceImplication> GenerateComplianceImplications(
        List<DetectedPII> items,
        List<string> regulations)
    {
        var implications = new List<ComplianceImplication>();
        var typesByRegulation = new Dictionary<string, HashSet<PIIType>>();

        foreach (var item in items)
        {
            foreach (var reg in item.Regulations)
            {
                if (!typesByRegulation.ContainsKey(reg))
                    typesByRegulation[reg] = new HashSet<PIIType>();
                typesByRegulation[reg].Add(item.Type);
            }
        }

        foreach (var reg in regulations.Where(r => typesByRegulation.ContainsKey(r)))
        {
            var types = typesByRegulation[reg];
            var severity = types.Any(t => GetRiskLevelForType(t) == PIIRiskLevel.Critical)
                ? ComplianceSeverity.Critical
                : types.Any(t => GetRiskLevelForType(t) == PIIRiskLevel.High)
                    ? ComplianceSeverity.Violation
                    : ComplianceSeverity.Warning;

            implications.Add(new ComplianceImplication
            {
                Regulation = reg,
                Requirement = GetRegulationRequirement(reg),
                Severity = severity,
                TriggeringTypes = types.ToList(),
                RecommendedAction = GetRecommendedAction(reg, severity)
            });
        }

        return implications;
    }

    private string GetRegulationRequirement(string regulation)
    {
        return regulation switch
        {
            "GDPR" => "Article 4 - Personal Data Processing",
            "HIPAA" => "45 CFR 164.514 - Protected Health Information",
            "PCI-DSS" => "Requirement 3 - Protect Stored Cardholder Data",
            "CCPA" => "Section 1798.100 - Personal Information Rights",
            "SOC2" => "Trust Services Criteria - Security",
            _ => "Data Protection Requirement"
        };
    }

    private string GetRecommendedAction(string regulation, ComplianceSeverity severity)
    {
        return severity switch
        {
            ComplianceSeverity.Critical => $"Immediate action required: Remove or encrypt {regulation}-regulated data",
            ComplianceSeverity.Violation => $"Review and remediate {regulation} compliance violation",
            ComplianceSeverity.Warning => $"Consider data minimization for {regulation} compliance",
            _ => "Review data handling practices"
        };
    }

    /// <summary>
    /// Generates redaction recommendations for detected PII.
    /// </summary>
    private List<RedactionRecommendation> GenerateRedactionRecommendations(List<DetectedPII> items)
    {
        return items
            .OrderByDescending(p => p.RiskLevel)
            .Select((p, i) => new RedactionRecommendation
            {
                StartPosition = p.StartPosition,
                Length = p.Length,
                Replacement = GetRedactionReplacement(p),
                PIIType = p.Type,
                Style = GetRedactionStyle(p.Type),
                Priority = (int)p.RiskLevel * 10 + (items.Count - i)
            })
            .ToList();
    }

    private string GetRedactionReplacement(DetectedPII pii)
    {
        return pii.Type switch
        {
            PIIType.SSN => "[SSN REDACTED]",
            PIIType.CreditCard => "[CREDIT CARD REDACTED]",
            PIIType.Email => "[EMAIL REDACTED]",
            PIIType.PhoneNumber => "[PHONE REDACTED]",
            PIIType.Address => "[ADDRESS REDACTED]",
            PIIType.DateOfBirth => "[DOB REDACTED]",
            PIIType.APIKey => "[API KEY REDACTED]",
            PIIType.Password => "[PASSWORD REDACTED]",
            _ => "[PII REDACTED]"
        };
    }

    private RedactionStyle GetRedactionStyle(PIIType type)
    {
        return type switch
        {
            PIIType.SSN or PIIType.CreditCard => RedactionStyle.PartialMask,
            PIIType.APIKey or PIIType.Password => RedactionStyle.Remove,
            _ => RedactionStyle.TypeLabel
        };
    }

    private string GetContext(string text, int position, int length, int windowSize)
    {
        var start = Math.Max(0, position - windowSize);
        var end = Math.Min(text.Length, position + length + windowSize);
        return text[start..end];
    }

    private List<DetectedPII> RemoveOverlapping(List<DetectedPII> items)
    {
        var sorted = items.OrderBy(p => p.StartPosition).ThenByDescending(p => p.Confidence).ToList();
        var result = new List<DetectedPII>();

        foreach (var item in sorted)
        {
            var overlaps = result.Any(p =>
                (item.StartPosition >= p.StartPosition && item.StartPosition < p.StartPosition + p.Length) ||
                (p.StartPosition >= item.StartPosition && p.StartPosition < item.StartPosition + item.Length));

            if (!overlaps)
            {
                result.Add(item);
            }
        }

        return result;
    }
}

#region Supporting Types

/// <summary>
/// Pattern set for a PII type including validation.
/// </summary>
internal sealed class PIIPatternSet
{
    public required Regex[] Patterns { get; init; }
    public Func<string, bool>? Validator { get; init; }
    public PIIRiskLevel RiskLevel { get; init; } = PIIRiskLevel.Medium;
    public string[] Regulations { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Result of PII detection.
/// </summary>
public sealed class PIIDetectionResult
{
    /// <summary>All detected PII items.</summary>
    public List<DetectedPII> PIIItems { get; init; } = new();

    /// <summary>Total PII count.</summary>
    public int TotalCount => PIIItems.Count;

    /// <summary>Count by PII type.</summary>
    public Dictionary<PIIType, int> CountByType =>
        PIIItems.GroupBy(p => p.Type).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>Overall risk level.</summary>
    public PIIRiskLevel RiskLevel { get; init; }

    /// <summary>Risk score (0-100).</summary>
    public int RiskScore { get; init; }

    /// <summary>Whether AI was used for detection.</summary>
    public bool UsedAI { get; init; }

    /// <summary>Processing duration.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Compliance implications.</summary>
    public List<ComplianceImplication> ComplianceImplications { get; init; } = new();

    /// <summary>Recommended redactions.</summary>
    public List<RedactionRecommendation> RedactionRecommendations { get; init; } = new();
}

/// <summary>
/// A detected PII item.
/// </summary>
public sealed record DetectedPII
{
    /// <summary>The PII text (possibly masked for sensitive types).</summary>
    public required string Text { get; init; }

    /// <summary>Original unmasked text (only available before redaction).</summary>
    public string? OriginalText { get; init; }

    /// <summary>PII type classification.</summary>
    public PIIType Type { get; init; }

    /// <summary>Confidence score (0-1).</summary>
    public float Confidence { get; init; }

    /// <summary>Start position in source text.</summary>
    public int StartPosition { get; init; }

    /// <summary>Length in source text.</summary>
    public int Length { get; init; }

    /// <summary>Surrounding context.</summary>
    public string? Context { get; init; }

    /// <summary>Risk level for this specific PII.</summary>
    public PIIRiskLevel RiskLevel { get; init; }

    /// <summary>Detection method used.</summary>
    public PIIDetectionMethod DetectionMethod { get; init; }

    /// <summary>Associated regulations.</summary>
    public List<string> Regulations { get; init; } = new();

    /// <summary>Additional metadata.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Types of PII.
/// </summary>
public enum PIIType
{
    /// <summary>Unknown PII type.</summary>
    Unknown = 0,

    // Identity
    /// <summary>Full name.</summary>
    FullName,
    /// <summary>First name only.</summary>
    FirstName,
    /// <summary>Last name only.</summary>
    LastName,
    /// <summary>Username or alias.</summary>
    Username,

    // Government IDs
    /// <summary>Social Security Number (US).</summary>
    SSN,
    /// <summary>National ID number.</summary>
    NationalId,
    /// <summary>Passport number.</summary>
    PassportNumber,
    /// <summary>Driver's license number.</summary>
    DriversLicense,
    /// <summary>Tax ID / EIN.</summary>
    TaxId,

    // Contact
    /// <summary>Email address.</summary>
    Email,
    /// <summary>Phone number.</summary>
    PhoneNumber,
    /// <summary>Physical address.</summary>
    Address,
    /// <summary>ZIP/postal code.</summary>
    PostalCode,

    // Financial
    /// <summary>Credit card number.</summary>
    CreditCard,
    /// <summary>Bank account number.</summary>
    BankAccount,
    /// <summary>Bank routing number.</summary>
    RoutingNumber,
    /// <summary>IBAN number.</summary>
    IBAN,
    /// <summary>SWIFT/BIC code.</summary>
    SWIFT,

    // Health
    /// <summary>Medical record number.</summary>
    MedicalRecordNumber,
    /// <summary>Health insurance ID.</summary>
    HealthInsuranceId,
    /// <summary>Medical condition.</summary>
    MedicalCondition,
    /// <summary>Prescription/medication.</summary>
    Medication,

    // Biometric
    /// <summary>Biometric data reference.</summary>
    BiometricData,
    /// <summary>Fingerprint reference.</summary>
    Fingerprint,
    /// <summary>Facial recognition data.</summary>
    FacialData,

    // Demographic
    /// <summary>Date of birth.</summary>
    DateOfBirth,
    /// <summary>Age.</summary>
    Age,
    /// <summary>Gender.</summary>
    Gender,
    /// <summary>Race or ethnicity.</summary>
    RaceEthnicity,
    /// <summary>Religion.</summary>
    Religion,
    /// <summary>Sexual orientation.</summary>
    SexualOrientation,
    /// <summary>Political affiliation.</summary>
    PoliticalAffiliation,

    // Digital
    /// <summary>IP address.</summary>
    IPAddress,
    /// <summary>MAC address.</summary>
    MACAddress,
    /// <summary>Device identifier.</summary>
    DeviceId,
    /// <summary>Cookie/tracking ID.</summary>
    TrackingId,
    /// <summary>Password or credential.</summary>
    Password,
    /// <summary>API key or token.</summary>
    APIKey,

    // Employment
    /// <summary>Employee ID.</summary>
    EmployeeId,
    /// <summary>Salary information.</summary>
    Salary,
    /// <summary>Employment history.</summary>
    EmploymentHistory,

    // Legal
    /// <summary>Criminal record reference.</summary>
    CriminalRecord,
    /// <summary>Court case number.</summary>
    CourtCaseNumber,

    // Location
    /// <summary>GPS coordinates.</summary>
    GPSCoordinates,
    /// <summary>Location history.</summary>
    LocationHistory,

    // Custom
    /// <summary>Custom PII type.</summary>
    Custom
}

/// <summary>
/// PII risk levels.
/// </summary>
public enum PIIRiskLevel
{
    /// <summary>No significant risk.</summary>
    None = 0,
    /// <summary>Low risk PII.</summary>
    Low = 1,
    /// <summary>Medium risk PII.</summary>
    Medium = 2,
    /// <summary>High risk PII.</summary>
    High = 3,
    /// <summary>Critical risk PII (requires immediate attention).</summary>
    Critical = 4
}

/// <summary>
/// PII detection method.
/// </summary>
public enum PIIDetectionMethod
{
    /// <summary>Pattern/regex matching.</summary>
    Pattern,
    /// <summary>Dictionary/list lookup.</summary>
    Dictionary,
    /// <summary>Machine learning model.</summary>
    MachineLearning,
    /// <summary>AI/LLM detection.</summary>
    AI,
    /// <summary>Context-based inference.</summary>
    ContextInference,
    /// <summary>Checksum validation.</summary>
    ChecksumValidation
}

/// <summary>
/// Compliance implication for detected PII.
/// </summary>
public sealed class ComplianceImplication
{
    /// <summary>Regulation/standard name.</summary>
    public required string Regulation { get; init; }

    /// <summary>Specific requirement violated or applicable.</summary>
    public string? Requirement { get; init; }

    /// <summary>Severity level.</summary>
    public ComplianceSeverity Severity { get; init; }

    /// <summary>PII types triggering this implication.</summary>
    public List<PIIType> TriggeringTypes { get; init; } = new();

    /// <summary>Recommended action.</summary>
    public string? RecommendedAction { get; init; }
}

/// <summary>
/// Compliance severity levels.
/// </summary>
public enum ComplianceSeverity
{
    /// <summary>Informational.</summary>
    Info,
    /// <summary>Advisory warning.</summary>
    Warning,
    /// <summary>Compliance violation.</summary>
    Violation,
    /// <summary>Critical compliance breach.</summary>
    Critical
}

/// <summary>
/// Redaction recommendation.
/// </summary>
public sealed class RedactionRecommendation
{
    /// <summary>Start position in text.</summary>
    public int StartPosition { get; init; }

    /// <summary>Length of text to redact.</summary>
    public int Length { get; init; }

    /// <summary>Suggested replacement text.</summary>
    public required string Replacement { get; init; }

    /// <summary>PII type being redacted.</summary>
    public PIIType PIIType { get; init; }

    /// <summary>Redaction style.</summary>
    public RedactionStyle Style { get; init; }

    /// <summary>Priority (higher = more important to redact).</summary>
    public int Priority { get; init; }
}

/// <summary>
/// Redaction styles.
/// </summary>
public enum RedactionStyle
{
    /// <summary>Replace with [REDACTED].</summary>
    Marker,
    /// <summary>Replace with asterisks.</summary>
    Asterisks,
    /// <summary>Replace with type label (e.g., [SSN]).</summary>
    TypeLabel,
    /// <summary>Replace with masked version (e.g., ***-**-1234).</summary>
    PartialMask,
    /// <summary>Remove entirely.</summary>
    Remove,
    /// <summary>Replace with placeholder (e.g., John Doe).</summary>
    Placeholder
}

/// <summary>
/// Result of PII redaction operation.
/// </summary>
public sealed class PIIRedactionResult
{
    /// <summary>Original text before redaction.</summary>
    public required string OriginalText { get; init; }

    /// <summary>Text after redaction.</summary>
    public required string RedactedText { get; init; }

    /// <summary>Number of redactions applied.</summary>
    public int RedactionCount { get; init; }

    /// <summary>Detection result with details.</summary>
    public PIIDetectionResult? DetectionResult { get; init; }
}

/// <summary>
/// Custom PII pattern definition.
/// </summary>
public sealed class CustomPIIPattern
{
    /// <summary>Unique identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable name.</summary>
    public required string Name { get; init; }

    /// <summary>Regex pattern for matching.</summary>
    public required string Pattern { get; init; }

    /// <summary>Validation function (optional).</summary>
    public Func<string, bool>? Validator { get; init; }

    /// <summary>Associated PII type.</summary>
    public PIIType Type { get; init; } = PIIType.Custom;

    /// <summary>Risk level for matches.</summary>
    public PIIRiskLevel RiskLevel { get; init; } = PIIRiskLevel.Medium;

    /// <summary>Associated regulations.</summary>
    public List<string> Regulations { get; init; } = new();

    /// <summary>Preferred redaction style.</summary>
    public RedactionStyle RedactionStyle { get; init; } = RedactionStyle.Marker;
}

/// <summary>
/// Configuration for PII detection.
/// </summary>
public sealed record PIIDetectionConfig
{
    /// <summary>PII types to detect (empty = all).</summary>
    public List<PIIType> EnabledTypes { get; init; } = new();

    /// <summary>Minimum confidence threshold.</summary>
    public float MinConfidenceThreshold { get; init; } = 0.7f;

    /// <summary>Enable AI-enhanced detection.</summary>
    public bool EnableAI { get; init; } = true;

    /// <summary>Include context in results.</summary>
    public bool IncludeContext { get; init; } = true;

    /// <summary>Context window size (characters).</summary>
    public int ContextWindowSize { get; init; } = 50;

    /// <summary>Generate redaction recommendations.</summary>
    public bool GenerateRedactions { get; init; } = true;

    /// <summary>Check compliance implications.</summary>
    public bool CheckCompliance { get; init; } = true;

    /// <summary>Regulations to check against.</summary>
    public List<string> ComplianceRegulations { get; init; } = new()
    {
        "GDPR", "CCPA", "HIPAA", "PCI-DSS", "SOC2"
    };

    /// <summary>Custom PII patterns.</summary>
    public List<CustomPIIPattern> CustomPatterns { get; init; } = new();
}

#endregion
