using DataWarehouse.Plugins.SemanticUnderstanding.Models;
using DataWarehouse.SDK.AI;
using System.Text;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.SemanticUnderstanding.Engine
{
    /// <summary>
    /// Engine for detecting Personally Identifiable Information (PII).
    /// Uses patterns, checksums, and AI for comprehensive PII detection.
    /// </summary>
    public sealed class PIIDetectionEngine
    {
        private readonly IAIProvider? _aiProvider;
        private readonly PIIPatterns _patterns;
        private readonly List<CustomPIIPattern> _customPatterns;
        private readonly ComplianceRules _complianceRules;

        public PIIDetectionEngine(IAIProvider? aiProvider = null)
        {
            _aiProvider = aiProvider;
            _patterns = new PIIPatterns();
            _customPatterns = new List<CustomPIIPattern>();
            _complianceRules = new ComplianceRules();
        }

        /// <summary>
        /// Detects PII in the text.
        /// </summary>
        public async Task<PIIDetectionResult> DetectAsync(
            string text,
            PIIDetectionConfig? config = null,
            CancellationToken ct = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            config ??= new PIIDetectionConfig();

            var piiItems = new List<DetectedPII>();
            var usedAI = false;

            // Pattern-based detection
            var patternPII = DetectWithPatterns(text, config);
            piiItems.AddRange(patternPII);

            // AI-enhanced detection
            if (config.EnableAI && _aiProvider != null && _aiProvider.IsAvailable)
            {
                var aiPII = await DetectWithAIAsync(text, config, ct);
                if (aiPII.Count > 0)
                {
                    usedAI = true;
                    piiItems = MergePIIResults(piiItems, aiPII);
                }
            }

            // Custom pattern detection
            foreach (var customPattern in _customPatterns)
            {
                var customPII = DetectCustomPattern(text, customPattern, config);
                piiItems.AddRange(customPII);
            }

            // Deduplicate
            piiItems = DeduplicatePII(piiItems);

            // Add context if requested
            if (config.IncludeContext)
            {
                piiItems = AddContext(text, piiItems, config.ContextWindowSize);
            }

            // Calculate risk
            var (riskLevel, riskScore) = CalculateRisk(piiItems);

            // Check compliance
            var complianceImplications = new List<ComplianceImplication>();
            if (config.CheckCompliance)
            {
                complianceImplications = CheckCompliance(piiItems, config.ComplianceRegulations);
            }

            // Generate redaction recommendations
            var redactionRecommendations = new List<RedactionRecommendation>();
            if (config.GenerateRedactions)
            {
                redactionRecommendations = GenerateRedactionRecommendations(piiItems);
            }

            sw.Stop();

            return new PIIDetectionResult
            {
                PIIItems = piiItems.OrderBy(p => p.StartPosition).ToList(),
                RiskLevel = riskLevel,
                RiskScore = riskScore,
                UsedAI = usedAI,
                Duration = sw.Elapsed,
                ComplianceImplications = complianceImplications,
                RedactionRecommendations = redactionRecommendations
            };
        }

        /// <summary>
        /// Detects PII using regex patterns.
        /// </summary>
        private List<DetectedPII> DetectWithPatterns(string text, PIIDetectionConfig config)
        {
            var results = new List<DetectedPII>();

            // SSN
            if (ShouldDetect(PIIType.SSN, config))
            {
                results.AddRange(DetectPattern(text, PIIType.SSN, _patterns.SSNPattern,
                    PIIRiskLevel.Critical, ValidateSSN));
            }

            // Credit Card
            if (ShouldDetect(PIIType.CreditCard, config))
            {
                results.AddRange(DetectPattern(text, PIIType.CreditCard, _patterns.CreditCardPattern,
                    PIIRiskLevel.Critical, ValidateLuhn));
            }

            // Email
            if (ShouldDetect(PIIType.Email, config))
            {
                results.AddRange(DetectPattern(text, PIIType.Email, _patterns.EmailPattern,
                    PIIRiskLevel.Medium, null));
            }

            // Phone
            if (ShouldDetect(PIIType.PhoneNumber, config))
            {
                results.AddRange(DetectPattern(text, PIIType.PhoneNumber, _patterns.PhonePattern,
                    PIIRiskLevel.Low, null));
            }

            // IP Address
            if (ShouldDetect(PIIType.IPAddress, config))
            {
                results.AddRange(DetectPattern(text, PIIType.IPAddress, _patterns.IPAddressPattern,
                    PIIRiskLevel.Low, null));
            }

            // Date of Birth
            if (ShouldDetect(PIIType.DateOfBirth, config))
            {
                results.AddRange(DetectPattern(text, PIIType.DateOfBirth, _patterns.DateOfBirthPattern,
                    PIIRiskLevel.Medium, null));
            }

            // Bank Account / IBAN
            if (ShouldDetect(PIIType.IBAN, config))
            {
                results.AddRange(DetectPattern(text, PIIType.IBAN, _patterns.IBANPattern,
                    PIIRiskLevel.High, ValidateIBAN));
            }

            // Passport
            if (ShouldDetect(PIIType.PassportNumber, config))
            {
                results.AddRange(DetectPattern(text, PIIType.PassportNumber, _patterns.PassportPattern,
                    PIIRiskLevel.High, null));
            }

            // Driver's License
            if (ShouldDetect(PIIType.DriversLicense, config))
            {
                results.AddRange(DetectPattern(text, PIIType.DriversLicense, _patterns.DriversLicensePattern,
                    PIIRiskLevel.High, null));
            }

            // Medical Record Number
            if (ShouldDetect(PIIType.MedicalRecordNumber, config))
            {
                results.AddRange(DetectPattern(text, PIIType.MedicalRecordNumber, _patterns.MedicalRecordPattern,
                    PIIRiskLevel.High, null));
            }

            // API Keys/Tokens
            if (ShouldDetect(PIIType.APIKey, config))
            {
                results.AddRange(DetectPattern(text, PIIType.APIKey, _patterns.APIKeyPattern,
                    PIIRiskLevel.High, null));
            }

            // Password patterns
            if (ShouldDetect(PIIType.Password, config))
            {
                results.AddRange(DetectPattern(text, PIIType.Password, _patterns.PasswordPattern,
                    PIIRiskLevel.Critical, null));
            }

            return results;
        }

        private List<DetectedPII> DetectPattern(
            string text,
            PIIType type,
            Regex pattern,
            PIIRiskLevel riskLevel,
            Func<string, bool>? validator)
        {
            var results = new List<DetectedPII>();

            try
            {
                var matches = pattern.Matches(text);
                foreach (Match match in matches)
                {
                    var value = match.Value;

                    // Validate if validator provided
                    var confidence = 0.9f;
                    if (validator != null)
                    {
                        if (!validator(value))
                        {
                            confidence = 0.5f; // Lower confidence if validation fails
                        }
                    }

                    results.Add(new DetectedPII
                    {
                        Text = MaskPII(value, type),
                        OriginalText = value,
                        Type = type,
                        Confidence = confidence,
                        StartPosition = match.Index,
                        Length = match.Length,
                        RiskLevel = riskLevel,
                        DetectionMethod = validator != null
                            ? PIIDetectionMethod.ChecksumValidation
                            : PIIDetectionMethod.Pattern,
                        Regulations = _complianceRules.GetRegulationsForType(type)
                    });
                }
            }
            catch { /* Invalid regex */ }

            return results;
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

            var request = new AIRequest
            {
                Prompt = $@"Identify all Personally Identifiable Information (PII) in this text.

For each PII found, provide:
- The exact text
- PII type (Name, Email, Phone, Address, SSN, CreditCard, etc.)
- Risk level (Low, Medium, High, Critical)

Text:
{truncatedText}

Format response as:
PII: [text] | TYPE: [type] | RISK: [level]

Focus on: names, addresses, phone numbers, email addresses, financial data, health information, government IDs.",
                SystemMessage = "You are a PII detection system for data privacy compliance. Identify all personally identifiable information accurately.",
                MaxTokens = 500
            };

            var response = await _aiProvider.CompleteAsync(request, ct);

            if (!response.Success)
                return new List<DetectedPII>();

            return ParseAIPIIResponse(response.Content, text);
        }

        private List<DetectedPII> ParseAIPIIResponse(string response, string originalText)
        {
            var results = new List<DetectedPII>();
            var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var match = Regex.Match(line, @"PII:\s*(.+?)\s*\|\s*TYPE:\s*(\w+)\s*\|\s*RISK:\s*(\w+)");
                if (match.Success)
                {
                    var piiText = match.Groups[1].Value.Trim();
                    var typeStr = match.Groups[2].Value.Trim();
                    var riskStr = match.Groups[3].Value.Trim();

                    var position = originalText.IndexOf(piiText, StringComparison.OrdinalIgnoreCase);
                    var type = ParsePIIType(typeStr);
                    var risk = ParseRiskLevel(riskStr);

                    results.Add(new DetectedPII
                    {
                        Text = MaskPII(piiText, type),
                        OriginalText = piiText,
                        Type = type,
                        Confidence = 0.85f,
                        StartPosition = position >= 0 ? position : 0,
                        Length = piiText.Length,
                        RiskLevel = risk,
                        DetectionMethod = PIIDetectionMethod.AI,
                        Regulations = _complianceRules.GetRegulationsForType(type)
                    });
                }
            }

            return results;
        }

        private PIIType ParsePIIType(string typeStr)
        {
            return typeStr.ToLowerInvariant() switch
            {
                "name" or "fullname" or "person" => PIIType.FullName,
                "firstname" => PIIType.FirstName,
                "lastname" => PIIType.LastName,
                "email" => PIIType.Email,
                "phone" or "phonenumber" => PIIType.PhoneNumber,
                "address" => PIIType.Address,
                "ssn" or "socialsecurity" => PIIType.SSN,
                "creditcard" or "credit" or "card" => PIIType.CreditCard,
                "iban" or "bankaccount" => PIIType.IBAN,
                "passport" => PIIType.PassportNumber,
                "driverslicense" or "license" => PIIType.DriversLicense,
                "dob" or "dateofbirth" or "birthday" => PIIType.DateOfBirth,
                "ip" or "ipaddress" => PIIType.IPAddress,
                "medical" or "medicalrecord" => PIIType.MedicalRecordNumber,
                "password" => PIIType.Password,
                "apikey" or "token" => PIIType.APIKey,
                _ => PIIType.Unknown
            };
        }

        private PIIRiskLevel ParseRiskLevel(string riskStr)
        {
            return riskStr.ToLowerInvariant() switch
            {
                "critical" => PIIRiskLevel.Critical,
                "high" => PIIRiskLevel.High,
                "medium" => PIIRiskLevel.Medium,
                "low" => PIIRiskLevel.Low,
                _ => PIIRiskLevel.Medium
            };
        }

        private List<DetectedPII> DetectCustomPattern(
            string text,
            CustomPIIPattern pattern,
            PIIDetectionConfig config)
        {
            var results = new List<DetectedPII>();

            try
            {
                var regex = new Regex(pattern.Pattern, RegexOptions.IgnoreCase);
                var matches = regex.Matches(text);

                foreach (Match match in matches)
                {
                    var value = match.Value;
                    var isValid = pattern.Validator?.Invoke(value) ?? true;

                    if (isValid)
                    {
                        results.Add(new DetectedPII
                        {
                            Text = MaskPII(value, pattern.Type),
                            OriginalText = value,
                            Type = pattern.Type,
                            Confidence = 0.85f,
                            StartPosition = match.Index,
                            Length = match.Length,
                            RiskLevel = pattern.RiskLevel,
                            DetectionMethod = PIIDetectionMethod.Pattern,
                            Regulations = pattern.Regulations,
                            Metadata = new Dictionary<string, object>
                            {
                                ["customPatternId"] = pattern.Id,
                                ["customPatternName"] = pattern.Name
                            }
                        });
                    }
                }
            }
            catch { /* Invalid regex */ }

            return results;
        }

        private string MaskPII(string value, PIIType type)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            return type switch
            {
                PIIType.SSN => value.Length >= 4 ? $"***-**-{value[^4..]}" : "***-**-****",
                PIIType.CreditCard => value.Length >= 4 ? $"****-****-****-{value[^4..]}" : "****-****-****-****",
                PIIType.Email => MaskEmail(value),
                PIIType.PhoneNumber => value.Length >= 4 ? $"***-***-{value[^4..]}" : "***-***-****",
                PIIType.IBAN => value.Length >= 4 ? $"{value[..4]}****{value[^4..]}" : "****",
                PIIType.FullName or PIIType.FirstName or PIIType.LastName =>
                    value.Length > 2 ? $"{value[0]}***{value[^1]}" : "***",
                _ => new string('*', Math.Min(value.Length, 10))
            };
        }

        private string MaskEmail(string email)
        {
            var atIndex = email.IndexOf('@');
            if (atIndex <= 1)
                return "***@***.***";

            var localPart = email[..atIndex];
            var domain = email[(atIndex + 1)..];
            var maskedLocal = localPart.Length > 2
                ? $"{localPart[0]}***{localPart[^1]}"
                : "***";

            return $"{maskedLocal}@{domain}";
        }

        private bool ShouldDetect(PIIType type, PIIDetectionConfig config)
        {
            return config.EnabledTypes.Count == 0 || config.EnabledTypes.Contains(type);
        }

        private List<DetectedPII> MergePIIResults(List<DetectedPII> pattern, List<DetectedPII> ai)
        {
            var merged = new List<DetectedPII>(ai);

            foreach (var p in pattern)
            {
                var hasOverlap = ai.Any(a =>
                    (p.StartPosition >= a.StartPosition && p.StartPosition < a.StartPosition + a.Length) ||
                    (a.StartPosition >= p.StartPosition && a.StartPosition < p.StartPosition + p.Length));

                if (!hasOverlap)
                {
                    merged.Add(p);
                }
            }

            return merged;
        }

        private List<DetectedPII> DeduplicatePII(List<DetectedPII> items)
        {
            return items
                .GroupBy(p => (p.StartPosition, p.Length))
                .Select(g => g.OrderByDescending(p => p.Confidence).First())
                .ToList();
        }

        private List<DetectedPII> AddContext(string text, List<DetectedPII> items, int windowSize)
        {
            return items.Select(p =>
            {
                var start = Math.Max(0, p.StartPosition - windowSize);
                var end = Math.Min(text.Length, p.StartPosition + p.Length + windowSize);
                var context = text[start..end];

                return new DetectedPII
                {
                    Text = p.Text,
                    OriginalText = p.OriginalText,
                    Type = p.Type,
                    Confidence = p.Confidence,
                    StartPosition = p.StartPosition,
                    Length = p.Length,
                    Context = context,
                    RiskLevel = p.RiskLevel,
                    DetectionMethod = p.DetectionMethod,
                    Regulations = p.Regulations,
                    Metadata = p.Metadata
                };
            }).ToList();
        }

        private (PIIRiskLevel Level, int Score) CalculateRisk(List<DetectedPII> items)
        {
            if (items.Count == 0)
                return (PIIRiskLevel.None, 0);

            var score = 0;

            foreach (var item in items)
            {
                score += item.RiskLevel switch
                {
                    PIIRiskLevel.Critical => 40,
                    PIIRiskLevel.High => 25,
                    PIIRiskLevel.Medium => 15,
                    PIIRiskLevel.Low => 5,
                    _ => 0
                };
            }

            score = Math.Min(score, 100);

            var level = score switch
            {
                >= 80 => PIIRiskLevel.Critical,
                >= 50 => PIIRiskLevel.High,
                >= 25 => PIIRiskLevel.Medium,
                >= 1 => PIIRiskLevel.Low,
                _ => PIIRiskLevel.None
            };

            return (level, score);
        }

        private List<ComplianceImplication> CheckCompliance(
            List<DetectedPII> items,
            List<string> regulations)
        {
            var implications = new List<ComplianceImplication>();

            foreach (var regulation in regulations)
            {
                var triggeringTypes = items
                    .Where(p => p.Regulations.Contains(regulation))
                    .Select(p => p.Type)
                    .Distinct()
                    .ToList();

                if (triggeringTypes.Count > 0)
                {
                    var severity = triggeringTypes.Any(t =>
                        t == PIIType.SSN || t == PIIType.CreditCard ||
                        t == PIIType.MedicalRecordNumber || t == PIIType.Password)
                        ? ComplianceSeverity.Violation
                        : ComplianceSeverity.Warning;

                    implications.Add(new ComplianceImplication
                    {
                        Regulation = regulation,
                        Requirement = _complianceRules.GetRequirementDescription(regulation),
                        Severity = severity,
                        TriggeringTypes = triggeringTypes,
                        RecommendedAction = GetRecommendedAction(regulation, triggeringTypes)
                    });
                }
            }

            return implications;
        }

        private string GetRecommendedAction(string regulation, List<PIIType> types)
        {
            return regulation switch
            {
                "GDPR" => "Ensure lawful basis for processing, implement data minimization, and provide data subject rights.",
                "CCPA" => "Provide notice at collection, honor opt-out requests, and maintain records of data processing.",
                "HIPAA" => "Implement safeguards, ensure minimum necessary access, and maintain audit logs.",
                "PCI-DSS" => "Do not store sensitive authentication data, encrypt cardholder data, and implement access controls.",
                "SOC2" => "Implement access controls, encryption, and monitoring for sensitive data.",
                _ => "Review data handling practices and implement appropriate safeguards."
            };
        }

        private List<RedactionRecommendation> GenerateRedactionRecommendations(List<DetectedPII> items)
        {
            return items
                .OrderByDescending(p => (int)p.RiskLevel)
                .Select(p => new RedactionRecommendation
                {
                    StartPosition = p.StartPosition,
                    Length = p.Length,
                    Replacement = GetRedactionReplacement(p.Type),
                    PIIType = p.Type,
                    Style = GetRedactionStyle(p.Type),
                    Priority = (int)p.RiskLevel
                })
                .ToList();
        }

        private string GetRedactionReplacement(PIIType type)
        {
            return type switch
            {
                PIIType.SSN => "[SSN REDACTED]",
                PIIType.CreditCard => "[CREDIT CARD REDACTED]",
                PIIType.Email => "[EMAIL REDACTED]",
                PIIType.PhoneNumber => "[PHONE REDACTED]",
                PIIType.FullName or PIIType.FirstName or PIIType.LastName => "[NAME REDACTED]",
                PIIType.Address => "[ADDRESS REDACTED]",
                PIIType.DateOfBirth => "[DOB REDACTED]",
                PIIType.Password => "[PASSWORD REDACTED]",
                PIIType.APIKey => "[API KEY REDACTED]",
                _ => "[REDACTED]"
            };
        }

        private RedactionStyle GetRedactionStyle(PIIType type)
        {
            return type switch
            {
                PIIType.SSN or PIIType.CreditCard or PIIType.Password => RedactionStyle.TypeLabel,
                PIIType.Email or PIIType.PhoneNumber => RedactionStyle.PartialMask,
                _ => RedactionStyle.Marker
            };
        }

        /// <summary>
        /// Applies redactions to text.
        /// </summary>
        public string ApplyRedactions(string text, List<RedactionRecommendation> redactions)
        {
            if (redactions.Count == 0)
                return text;

            // Sort by position descending to avoid offset issues
            var sorted = redactions.OrderByDescending(r => r.StartPosition).ToList();
            var result = new StringBuilder(text);

            foreach (var redaction in sorted)
            {
                if (redaction.StartPosition >= 0 && redaction.StartPosition + redaction.Length <= result.Length)
                {
                    result.Remove(redaction.StartPosition, redaction.Length);
                    result.Insert(redaction.StartPosition, redaction.Replacement);
                }
            }

            return result.ToString();
        }

        /// <summary>
        /// Registers a custom PII pattern.
        /// </summary>
        public void RegisterCustomPattern(CustomPIIPattern pattern)
        {
            _customPatterns.Add(pattern);
        }

        #region Validators

        private bool ValidateSSN(string ssn)
        {
            var cleaned = Regex.Replace(ssn, @"[\s-]", "");
            if (cleaned.Length != 9 || !cleaned.All(char.IsDigit))
                return false;

            // Invalid area numbers
            var area = int.Parse(cleaned[..3]);
            if (area == 0 || area == 666 || area >= 900)
                return false;

            // All zeros invalid
            if (cleaned[3..5] == "00" || cleaned[5..] == "0000")
                return false;

            return true;
        }

        private bool ValidateLuhn(string cardNumber)
        {
            var cleaned = Regex.Replace(cardNumber, @"\D", "");
            if (cleaned.Length < 13 || cleaned.Length > 19)
                return false;

            var sum = 0;
            var alternate = false;

            for (int i = cleaned.Length - 1; i >= 0; i--)
            {
                var digit = cleaned[i] - '0';

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

        private bool ValidateIBAN(string iban)
        {
            var cleaned = Regex.Replace(iban.ToUpperInvariant(), @"\s", "");
            if (cleaned.Length < 15 || cleaned.Length > 34)
                return false;

            // Move first 4 chars to end
            var rearranged = cleaned[4..] + cleaned[..4];

            // Convert letters to numbers (A=10, B=11, etc.)
            var numeric = new StringBuilder();
            foreach (var c in rearranged)
            {
                if (char.IsDigit(c))
                    numeric.Append(c);
                else if (char.IsLetter(c))
                    numeric.Append(c - 'A' + 10);
                else
                    return false;
            }

            // Mod 97 check
            var remainder = 0;
            foreach (var c in numeric.ToString())
            {
                remainder = (remainder * 10 + (c - '0')) % 97;
            }

            return remainder == 1;
        }

        #endregion
    }

    /// <summary>
    /// PII detection patterns.
    /// </summary>
    internal sealed class PIIPatterns
    {
        public Regex SSNPattern { get; } = new(
            @"\b\d{3}[-\s]?\d{2}[-\s]?\d{4}\b",
            RegexOptions.Compiled);

        public Regex CreditCardPattern { get; } = new(
            @"\b(?:\d{4}[-\s]?){3}\d{4}\b|\b\d{13,19}\b",
            RegexOptions.Compiled);

        public Regex EmailPattern { get; } = new(
            @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,}\b",
            RegexOptions.Compiled);

        public Regex PhonePattern { get; } = new(
            @"(?:\+?1[-.\s]?)?\(?[0-9]{3}\)?[-.\s]?[0-9]{3}[-.\s]?[0-9]{4}",
            RegexOptions.Compiled);

        public Regex IPAddressPattern { get; } = new(
            @"\b(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\b",
            RegexOptions.Compiled);

        public Regex DateOfBirthPattern { get; } = new(
            @"\b(?:dob|date\s+of\s+birth|birthday|born)[\s:]+\d{1,2}[-/]\d{1,2}[-/]\d{2,4}\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public Regex IBANPattern { get; } = new(
            @"\b[A-Z]{2}\d{2}[A-Z0-9]{4,30}\b",
            RegexOptions.Compiled);

        public Regex PassportPattern { get; } = new(
            @"\b(?:passport|travel\s+document)[\s:#]+[A-Z0-9]{6,12}\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public Regex DriversLicensePattern { get; } = new(
            @"\b(?:driver'?s?\s+license|dl|license\s+number?)[\s:#]+[A-Z0-9]{5,15}\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public Regex MedicalRecordPattern { get; } = new(
            @"\b(?:mrn|medical\s+record|patient\s+id)[\s:#]+[A-Z0-9]{4,15}\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public Regex APIKeyPattern { get; } = new(
            @"\b(?:api[_-]?key|api[_-]?secret|access[_-]?token|auth[_-]?token)[\s:=]+[A-Za-z0-9_-]{20,64}\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public Regex PasswordPattern { get; } = new(
            @"\b(?:password|passwd|pwd)[\s:=]+\S{6,}\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Compliance rules for PII types.
    /// </summary>
    internal sealed class ComplianceRules
    {
        private readonly Dictionary<PIIType, List<string>> _typeRegulations = new()
        {
            [PIIType.FullName] = new() { "GDPR", "CCPA" },
            [PIIType.FirstName] = new() { "GDPR", "CCPA" },
            [PIIType.LastName] = new() { "GDPR", "CCPA" },
            [PIIType.Email] = new() { "GDPR", "CCPA", "CAN-SPAM" },
            [PIIType.PhoneNumber] = new() { "GDPR", "CCPA", "TCPA" },
            [PIIType.Address] = new() { "GDPR", "CCPA" },
            [PIIType.SSN] = new() { "GDPR", "CCPA", "GLBA" },
            [PIIType.CreditCard] = new() { "PCI-DSS", "GDPR", "CCPA" },
            [PIIType.IBAN] = new() { "PCI-DSS", "GDPR" },
            [PIIType.DateOfBirth] = new() { "GDPR", "CCPA", "HIPAA" },
            [PIIType.MedicalRecordNumber] = new() { "HIPAA", "GDPR" },
            [PIIType.MedicalCondition] = new() { "HIPAA", "GDPR" },
            [PIIType.IPAddress] = new() { "GDPR" },
            [PIIType.BiometricData] = new() { "GDPR", "BIPA", "CCPA" }
        };

        public List<string> GetRegulationsForType(PIIType type)
        {
            return _typeRegulations.TryGetValue(type, out var regs) ? regs : new List<string>();
        }

        public string GetRequirementDescription(string regulation)
        {
            return regulation switch
            {
                "GDPR" => "Article 5, 6 - Lawful processing and data minimization",
                "CCPA" => "Section 1798.100 - Consumer rights and disclosures",
                "HIPAA" => "45 CFR 164 - Protected Health Information safeguards",
                "PCI-DSS" => "Requirement 3 - Protect stored cardholder data",
                "SOC2" => "Trust Services Criteria - Security and confidentiality",
                _ => $"{regulation} compliance requirements"
            };
        }
    }
}
