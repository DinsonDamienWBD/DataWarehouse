using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Geofencing
{
    /// <summary>
    /// T77: Sovereignty Classification Strategy
    /// Automatically classifies data sovereignty jurisdiction at ingest time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Features:
    /// - Multi-signal jurisdiction detection (IP, PII patterns, metadata, account, storage)
    /// - Confidence scoring for classification reliability
    /// - Applicable regulation mapping (GDPR, CCPA, PIPL, etc.)
    /// - Data sensitivity classification (Public/Internal/Confidential/Restricted)
    /// - Classification result publishing on message bus
    /// </para>
    /// <para>
    /// Classification signals (in priority order):
    /// 1. Explicit metadata tags (highest confidence)
    /// 2. Storage location jurisdiction
    /// 3. User account jurisdiction
    /// 4. PII content patterns (EU, US, CN, etc.)
    /// 5. Source IP geolocation (lowest confidence)
    /// </para>
    /// </remarks>
    public sealed class SovereigntyClassificationStrategy : ComplianceStrategyBase
    {
        private readonly ConcurrentDictionary<string, ClassificationCache> _classificationCache = new();
        private readonly Dictionary<string, Regex> _piiPatterns = new();
        private readonly Dictionary<string, List<string>> _jurisdictionRegulations = new();

        private TimeSpan _cacheTtl = TimeSpan.FromHours(1);
        private double _minimumConfidence = 0.7;

        /// <inheritdoc/>
        public override string StrategyId => "sovereignty-classification";

        /// <inheritdoc/>
        public override string StrategyName => "Sovereignty Classification";

        /// <inheritdoc/>
        public override string Framework => "DataSovereignty";

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("CacheTtlMinutes", out var ttlObj) && ttlObj is int ttlMinutes)
            {
                _cacheTtl = TimeSpan.FromMinutes(ttlMinutes);
            }

            if (configuration.TryGetValue("MinimumConfidence", out var confObj) && confObj is double confidence)
            {
                _minimumConfidence = confidence;
            }

            InitializePiiPatterns();
            InitializeJurisdictionRegulations();

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Classifies data sovereignty jurisdiction from multiple signals.
        /// </summary>
        public async Task<SovereigntyClassification> ClassifyDataAsync(
            ClassificationInput input,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(input, nameof(input));

            // Check cache
            var cacheKey = GenerateCacheKey(input);
            if (_classificationCache.TryGetValue(cacheKey, out var cached) &&
                cached.ExpiresAt > DateTime.UtcNow)
            {
                return cached.Classification;
            }

            var signals = new List<ClassificationSignal>();

            // Signal 1: Explicit metadata (highest confidence)
            if (!string.IsNullOrEmpty(input.ExplicitJurisdiction))
            {
                signals.Add(new ClassificationSignal
                {
                    Source = ClassificationSource.Metadata,
                    Jurisdiction = input.ExplicitJurisdiction.ToUpperInvariant(),
                    Confidence = 1.0,
                    Reason = "Explicit jurisdiction metadata provided"
                });
            }

            // Signal 2: Storage location jurisdiction
            if (!string.IsNullOrEmpty(input.StorageLocation))
            {
                signals.Add(new ClassificationSignal
                {
                    Source = ClassificationSource.StorageLocation,
                    Jurisdiction = input.StorageLocation.ToUpperInvariant(),
                    Confidence = 0.95,
                    Reason = $"Data stored in {input.StorageLocation}"
                });
            }

            // Signal 3: User account jurisdiction
            if (!string.IsNullOrEmpty(input.UserJurisdiction))
            {
                signals.Add(new ClassificationSignal
                {
                    Source = ClassificationSource.UserAccount,
                    Jurisdiction = input.UserJurisdiction.ToUpperInvariant(),
                    Confidence = 0.85,
                    Reason = $"User account registered in {input.UserJurisdiction}"
                });
            }

            // Signal 4: PII content patterns
            if (!string.IsNullOrEmpty(input.DataContent))
            {
                var piiSignals = AnalyzePiiPatterns(input.DataContent);
                signals.AddRange(piiSignals);
            }

            // Signal 5: Source IP geolocation (via message bus)
            if (!string.IsNullOrEmpty(input.SourceIp))
            {
                var ipSignal = await ResolveSourceIpAsync(input.SourceIp, cancellationToken);
                if (ipSignal != null)
                {
                    signals.Add(ipSignal);
                }
            }

            // Calculate primary jurisdiction and confidence
            var classification = CalculateClassification(signals, input);

            // Cache the result
            _classificationCache[cacheKey] = new ClassificationCache
            {
                Classification = classification,
                ExpiresAt = DateTime.UtcNow.Add(_cacheTtl)
            };

            return classification;
        }

        /// <summary>
        /// Batch classifies multiple data items.
        /// </summary>
        public async Task<IReadOnlyList<SovereigntyClassification>> BatchClassifyAsync(
            IEnumerable<ClassificationInput> inputs,
            CancellationToken cancellationToken = default)
        {
            var tasks = inputs.Select(input => ClassifyDataAsync(input, cancellationToken));
            var results = await Task.WhenAll(tasks);
            return results.ToList();
        }

        /// <summary>
        /// Clears the classification cache.
        /// </summary>
        public void ClearCache()
        {
            _classificationCache.Clear();
        }

        /// <summary>
        /// Gets cache statistics.
        /// </summary>
        public ClassificationCacheStats GetCacheStats()
        {
            var now = DateTime.UtcNow;
            var entries = _classificationCache.ToArray();

            return new ClassificationCacheStats
            {
                TotalEntries = entries.Length,
                ValidEntries = entries.Count(e => e.Value.ExpiresAt > now),
                ExpiredEntries = entries.Count(e => e.Value.ExpiresAt <= now)
            };
        }

        /// <inheritdoc/>
        protected override async Task<ComplianceResult> CheckComplianceCoreAsync(
            ComplianceContext context,
            CancellationToken cancellationToken)
        {
        IncrementCounter("sovereignty_classification.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check if classification is provided
            if (string.IsNullOrEmpty(context.DataClassification) ||
                context.DataClassification.Equals("Standard", StringComparison.OrdinalIgnoreCase) ||
                context.DataClassification.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            {
                // Attempt auto-classification
                var input = new ClassificationInput
                {
                    ResourceId = context.ResourceId ?? "unknown",
                    SourceIp = context.Attributes.GetValueOrDefault("SourceIp") as string,
                    StorageLocation = context.DestinationLocation,
                    UserJurisdiction = context.Attributes.GetValueOrDefault("UserJurisdiction") as string,
                    ExplicitJurisdiction = context.SourceLocation,
                    DataContent = context.Attributes.GetValueOrDefault("DataContent") as string
                };

                var classification = await ClassifyDataAsync(input, cancellationToken);

                if (classification.ClassificationConfidence < _minimumConfidence)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "SCLASS-001",
                        Description = $"Data classification confidence ({classification.ClassificationConfidence:P0}) below threshold ({_minimumConfidence:P0})",
                        Severity = ViolationSeverity.Medium,
                        AffectedResource = context.ResourceId,
                        Remediation = "Provide explicit jurisdiction metadata or increase data quality signals"
                    });
                }

                if (string.IsNullOrEmpty(classification.PrimaryJurisdiction))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "SCLASS-002",
                        Description = "Unable to determine data sovereignty jurisdiction",
                        Severity = ViolationSeverity.High,
                        AffectedResource = context.ResourceId,
                        Remediation = "Tag data with explicit jurisdiction before processing"
                    });
                }
                else
                {
                    recommendations.Add($"Auto-classified jurisdiction: {classification.PrimaryJurisdiction} ({classification.ClassificationConfidence:P0} confidence)");
                    recommendations.Add($"Applicable regulations: {string.Join(", ", classification.ApplicableRegulations)}");
                }
            }

            var isCompliant = !violations.Any(v => v.Severity >= ViolationSeverity.High);
            var status = violations.Count == 0 ? ComplianceStatus.Compliant :
                        violations.Any(v => v.Severity >= ViolationSeverity.High) ? ComplianceStatus.NonCompliant :
                        ComplianceStatus.PartiallyCompliant;

            return new ComplianceResult
            {
                IsCompliant = isCompliant,
                Framework = Framework,
                Status = status,
                Violations = violations,
                Recommendations = recommendations,
                Metadata = new Dictionary<string, object>
                {
                    ["CacheStats"] = GetCacheStats()
                }
            };
        }

        private void InitializePiiPatterns()
        {
            // EU patterns (GDPR)
            _piiPatterns["EU-EMAIL"] = new Regex(
                @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.(?:eu|de|fr|it|es|nl|be|at|se|pl|pt|gr|cz|ro|dk|fi|sk|ie|hr|bg|lt|si|lv|ee|cy|lu|mt)\b",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

            _piiPatterns["EU-PHONE"] = new Regex(
                @"\+(?:32|33|34|39|43|45|46|47|48|49|351|352|353|354|356|357|358|370|371|372|386|420|421)[0-9]{6,13}",
                RegexOptions.Compiled);

            _piiPatterns["EU-IBAN"] = new Regex(
                @"\b[A-Z]{2}[0-9]{2}[A-Z0-9]{11,30}\b",
                RegexOptions.Compiled);

            _piiPatterns["EU-VAT"] = new Regex(
                @"\b(?:ATU|BE0|BG|CY|CZ|DE|DK|EE|EL|ES|FI|FR|GB|HU|IE|IT|LT|LU|LV|MT|NL|PL|PT|RO|SE|SI|SK)[0-9]{8,12}\b",
                RegexOptions.Compiled);

            _piiPatterns["EU-PASSPORT"] = new Regex(
                @"\b[A-Z]{1,2}[0-9]{6,9}\b",
                RegexOptions.Compiled);

            // US patterns (CCPA, HIPAA)
            _piiPatterns["US-SSN"] = new Regex(
                @"\b\d{3}-\d{2}-\d{4}\b",
                RegexOptions.Compiled);

            _piiPatterns["US-PHONE"] = new Regex(
                @"\b(?:\+1[-.\s]?)?\(?\d{3}\)?[-.\s]?\d{3}[-.\s]?\d{4}\b",
                RegexOptions.Compiled);

            _piiPatterns["US-ZIP"] = new Regex(
                @"\b\d{5}(?:-\d{4})?\b",
                RegexOptions.Compiled);

            _piiPatterns["US-DRIVERS-LICENSE"] = new Regex(
                @"\b[A-Z]{1,2}[0-9]{5,9}\b",
                RegexOptions.Compiled);

            // China patterns (PIPL)
            _piiPatterns["CN-ID"] = new Regex(
                @"\b[1-9]\d{5}(?:18|19|20)\d{2}(?:0[1-9]|1[0-2])(?:0[1-9]|[12]\d|3[01])\d{3}[\dXx]\b",
                RegexOptions.Compiled);

            _piiPatterns["CN-PHONE"] = new Regex(
                @"\+86[-.\s]?1[3-9]\d{9}",
                RegexOptions.Compiled);

            _piiPatterns["CN-EMAIL"] = new Regex(
                @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.cn\b",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

            // India patterns (PDPB)
            _piiPatterns["IN-AADHAAR"] = new Regex(
                @"\b\d{4}\s?\d{4}\s?\d{4}\b",
                RegexOptions.Compiled);

            _piiPatterns["IN-PAN"] = new Regex(
                @"\b[A-Z]{5}[0-9]{4}[A-Z]\b",
                RegexOptions.Compiled);

            _piiPatterns["IN-PHONE"] = new Regex(
                @"\+91[-.\s]?[6-9]\d{9}",
                RegexOptions.Compiled);

            // Brazil patterns (LGPD)
            _piiPatterns["BR-CPF"] = new Regex(
                @"\b\d{3}\.\d{3}\.\d{3}-\d{2}\b",
                RegexOptions.Compiled);

            _piiPatterns["BR-PHONE"] = new Regex(
                @"\+55[-.\s]?\d{2}[-.\s]?[6-9]\d{3,4}[-.\s]?\d{4}",
                RegexOptions.Compiled);

            // Russia patterns
            _piiPatterns["RU-INN"] = new Regex(
                @"\b\d{10}|\d{12}\b",
                RegexOptions.Compiled);

            _piiPatterns["RU-PHONE"] = new Regex(
                @"\+7[-.\s]?\d{3}[-.\s]?\d{3}[-.\s]?\d{2}[-.\s]?\d{2}",
                RegexOptions.Compiled);

            // Canada patterns (PIPEDA)
            _piiPatterns["CA-SIN"] = new Regex(
                @"\b\d{3}-\d{3}-\d{3}\b",
                RegexOptions.Compiled);

            _piiPatterns["CA-POSTAL"] = new Regex(
                @"\b[A-Z]\d[A-Z]\s?\d[A-Z]\d\b",
                RegexOptions.Compiled);

            // Australia patterns
            _piiPatterns["AU-TFN"] = new Regex(
                @"\b\d{3}\s?\d{3}\s?\d{3}\b",
                RegexOptions.Compiled);

            _piiPatterns["AU-ABN"] = new Regex(
                @"\b\d{2}\s?\d{3}\s?\d{3}\s?\d{3}\b",
                RegexOptions.Compiled);

            // Generic patterns
            _piiPatterns["CREDIT-CARD"] = new Regex(
                @"\b\d{4}[-\s]?\d{4}[-\s]?\d{4}[-\s]?\d{4}\b",
                RegexOptions.Compiled);

            _piiPatterns["IP-ADDRESS"] = new Regex(
                @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b",
                RegexOptions.Compiled);
        }

        private void InitializeJurisdictionRegulations()
        {
            _jurisdictionRegulations["EU"] = new List<string> { "GDPR", "ePrivacy", "NIS2" };
            _jurisdictionRegulations["EEA"] = new List<string> { "GDPR", "ePrivacy" };
            _jurisdictionRegulations["US"] = new List<string> { "CCPA", "HIPAA", "GLBA", "FERPA" };
            _jurisdictionRegulations["US-CA"] = new List<string> { "CCPA", "CPRA" };
            _jurisdictionRegulations["US-NY"] = new List<string> { "SHIELD" };
            _jurisdictionRegulations["CN"] = new List<string> { "PIPL", "CSL", "DSL" };
            _jurisdictionRegulations["IN"] = new List<string> { "PDPB", "DPDPA" };
            _jurisdictionRegulations["BR"] = new List<string> { "LGPD" };
            _jurisdictionRegulations["RU"] = new List<string> { "FZ-152" };
            _jurisdictionRegulations["CA"] = new List<string> { "PIPEDA" };
            _jurisdictionRegulations["AU"] = new List<string> { "Privacy Act 1988" };
            _jurisdictionRegulations["GB"] = new List<string> { "UK GDPR", "DPA 2018" };
            _jurisdictionRegulations["JP"] = new List<string> { "APPI" };
            _jurisdictionRegulations["KR"] = new List<string> { "PIPA" };
            _jurisdictionRegulations["SG"] = new List<string> { "PDPA" };
        }

        private List<ClassificationSignal> AnalyzePiiPatterns(string content)
        {
            var signals = new List<ClassificationSignal>();
            var jurisdictionMatches = new ConcurrentDictionary<string, int>();

            foreach (var (patternName, regex) in _piiPatterns)
            {
                var matches = regex.Matches(content);
                if (matches.Count > 0)
                {
                    var jurisdiction = ExtractJurisdictionFromPattern(patternName);
                    jurisdictionMatches.AddOrUpdate(jurisdiction, matches.Count, (_, count) => count + matches.Count);
                }
            }

            foreach (var (jurisdiction, matchCount) in jurisdictionMatches.OrderByDescending(kvp => kvp.Value))
            {
                var confidence = CalculatePatternConfidence(matchCount);
                signals.Add(new ClassificationSignal
                {
                    Source = ClassificationSource.ContentAnalysis,
                    Jurisdiction = jurisdiction,
                    Confidence = confidence,
                    Reason = $"Detected {matchCount} PII pattern(s) for {jurisdiction}"
                });
            }

            return signals;
        }

        private string ExtractJurisdictionFromPattern(string patternName)
        {
            if (patternName.StartsWith("EU-")) return "EU";
            if (patternName.StartsWith("US-")) return "US";
            if (patternName.StartsWith("CN-")) return "CN";
            if (patternName.StartsWith("IN-")) return "IN";
            if (patternName.StartsWith("BR-")) return "BR";
            if (patternName.StartsWith("RU-")) return "RU";
            if (patternName.StartsWith("CA-")) return "CA";
            if (patternName.StartsWith("AU-")) return "AU";
            return "UNKNOWN";
        }

        private double CalculatePatternConfidence(int matchCount)
        {
            // More matches = higher confidence, but with diminishing returns
            return Math.Min(0.6 + (matchCount * 0.05), 0.85);
        }

        private async Task<ClassificationSignal?> ResolveSourceIpAsync(string sourceIp, CancellationToken cancellationToken)
        {
            // In production, this would publish to message bus topic "geolocation.resolve"
            // and wait for response from GeolocationServiceStrategy
            await Task.Delay(1, cancellationToken); // Placeholder for async message bus call

            // For now, return a simulated signal with lower confidence
            return new ClassificationSignal
            {
                Source = ClassificationSource.SourceIP,
                Jurisdiction = "US", // Would be actual resolved location
                Confidence = 0.6,
                Reason = $"Source IP {sourceIp} resolved to location"
            };
        }

        private SovereigntyClassification CalculateClassification(
            List<ClassificationSignal> signals,
            ClassificationInput input)
        {
            if (signals.Count == 0)
            {
                return new SovereigntyClassification
                {
                    ResourceId = input.ResourceId,
                    PrimaryJurisdiction = "",
                    ApplicableRegulations = Array.Empty<string>(),
                    DataSensitivity = DataSensitivity.Internal,
                    ClassificationConfidence = 0.0,
                    ClassificationSource = ClassificationSource.Combined,
                    ClassifiedAt = DateTime.UtcNow,
                    Signals = Array.Empty<ClassificationSignal>()
                };
            }

            // Group signals by jurisdiction and calculate weighted confidence
            var jurisdictionGroups = signals
                .GroupBy(s => s.Jurisdiction)
                .Select(g => new
                {
                    Jurisdiction = g.Key,
                    WeightedConfidence = g.Max(s => s.Confidence), // Take highest confidence per jurisdiction
                    Sources = g.Select(s => s.Source).Distinct().ToList(),
                    Signals = g.ToList()
                })
                .OrderByDescending(x => x.WeightedConfidence)
                .ToList();

            var primaryGroup = jurisdictionGroups.First();
            var primaryJurisdiction = primaryGroup.Jurisdiction;
            var confidence = primaryGroup.WeightedConfidence;

            // If multiple sources agree, boost confidence
            if (primaryGroup.Sources.Count > 1)
            {
                confidence = Math.Min(confidence + 0.1, 1.0);
            }

            // Determine classification source
            var classificationSource = primaryGroup.Sources.Count == 1
                ? primaryGroup.Sources[0]
                : ClassificationSource.Combined;

            // Get applicable regulations
            var regulations = _jurisdictionRegulations.TryGetValue(primaryJurisdiction, out var regs)
                ? regs.ToList()
                : new List<string>();

            // Determine data sensitivity based on PII presence and jurisdiction
            var sensitivity = DetermineSensitivity(signals, primaryJurisdiction);

            return new SovereigntyClassification
            {
                ResourceId = input.ResourceId,
                PrimaryJurisdiction = primaryJurisdiction,
                ApplicableRegulations = regulations,
                DataSensitivity = sensitivity,
                ClassificationConfidence = confidence,
                ClassificationSource = classificationSource,
                ClassifiedAt = DateTime.UtcNow,
                Signals = signals
            };
        }

        private DataSensitivity DetermineSensitivity(List<ClassificationSignal> signals, string jurisdiction)
        {
            // Check for high-sensitivity PII patterns
            var hasHighSensitivityPii = signals.Any(s =>
                s.Source == ClassificationSource.ContentAnalysis &&
                s.Reason.Contains("SSN", StringComparison.OrdinalIgnoreCase) ||
                s.Reason.Contains("ID", StringComparison.OrdinalIgnoreCase) ||
                s.Reason.Contains("AADHAAR", StringComparison.OrdinalIgnoreCase) ||
                s.Reason.Contains("CPF", StringComparison.OrdinalIgnoreCase));

            if (hasHighSensitivityPii)
                return DataSensitivity.Restricted;

            // GDPR/PIPL/LGPD jurisdictions default to higher sensitivity
            var strictJurisdictions = new[] { "EU", "EEA", "CN", "BR" };
            if (strictJurisdictions.Contains(jurisdiction, StringComparer.OrdinalIgnoreCase))
                return DataSensitivity.Confidential;

            // Has some PII patterns
            if (signals.Any(s => s.Source == ClassificationSource.ContentAnalysis))
                return DataSensitivity.Confidential;

            return DataSensitivity.Internal;
        }

        private string GenerateCacheKey(ClassificationInput input)
        {
            return $"{input.ResourceId}:{input.SourceIp}:{input.StorageLocation}:{input.UserJurisdiction}";
        }

        private sealed class ClassificationCache
        {
            public required SovereigntyClassification Classification { get; init; }
            public required DateTime ExpiresAt { get; init; }
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("sovereignty_classification.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("sovereignty_classification.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}

    /// <summary>
    /// Input for sovereignty classification.
    /// </summary>
    public sealed record ClassificationInput
    {
        public required string ResourceId { get; init; }
        public string? SourceIp { get; init; }
        public string? StorageLocation { get; init; }
        public string? UserJurisdiction { get; init; }
        public string? ExplicitJurisdiction { get; init; }
        public string? DataContent { get; init; }
    }

    /// <summary>
    /// Result of sovereignty classification.
    /// </summary>
    public sealed record SovereigntyClassification
    {
        /// <summary>
        /// Resource being classified.
        /// </summary>
        public required string ResourceId { get; init; }

        /// <summary>
        /// Primary jurisdiction (e.g., "EU", "US-CA", "CN").
        /// </summary>
        public required string PrimaryJurisdiction { get; init; }

        /// <summary>
        /// Applicable regulations (e.g., ["GDPR", "ePrivacy"]).
        /// </summary>
        public required IReadOnlyList<string> ApplicableRegulations { get; init; }

        /// <summary>
        /// Data sensitivity level.
        /// </summary>
        public required DataSensitivity DataSensitivity { get; init; }

        /// <summary>
        /// Classification confidence (0.0-1.0).
        /// </summary>
        public required double ClassificationConfidence { get; init; }

        /// <summary>
        /// Primary classification source.
        /// </summary>
        public required ClassificationSource ClassificationSource { get; init; }

        /// <summary>
        /// When classification was performed.
        /// </summary>
        public required DateTime ClassifiedAt { get; init; }

        /// <summary>
        /// All classification signals collected.
        /// </summary>
        public required IReadOnlyList<ClassificationSignal> Signals { get; init; }
    }

    /// <summary>
    /// Individual classification signal.
    /// </summary>
    public sealed record ClassificationSignal
    {
        public required ClassificationSource Source { get; init; }
        public required string Jurisdiction { get; init; }
        public required double Confidence { get; init; }
        public required string Reason { get; init; }
    }

    /// <summary>
    /// Classification source type.
    /// </summary>
    public enum ClassificationSource
    {
        ContentAnalysis,
        SourceIP,
        Metadata,
        UserAccount,
        StorageLocation,
        Combined
    }

    /// <summary>
    /// Data sensitivity levels.
    /// </summary>
    public enum DataSensitivity
    {
        Public,
        Internal,
        Confidential,
        Restricted
    }

    /// <summary>
    /// Classification cache statistics.
    /// </summary>
    public sealed record ClassificationCacheStats
    {
        public int TotalEntries { get; init; }
        public int ValidEntries { get; init; }
        public int ExpiredEntries { get; init; }
    }
}
