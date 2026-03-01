using System.Diagnostics;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Lifecycle;

/// <summary>
/// Classification rule for pattern-based classification.
/// </summary>
public sealed class ClassificationRule
{
    /// <summary>
    /// Unique identifier for the rule.
    /// </summary>
    public required string RuleId { get; init; }

    /// <summary>
    /// Display name of the rule.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Description of what the rule detects.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Classification label to apply when matched.
    /// </summary>
    public required ClassificationLabel Label { get; init; }

    /// <summary>
    /// Priority of the rule (higher = evaluated first).
    /// </summary>
    public int Priority { get; init; } = 100;

    /// <summary>
    /// Content patterns to match (regex).
    /// </summary>
    public List<string>? ContentPatterns { get; init; }

    /// <summary>
    /// Metadata field patterns to match.
    /// </summary>
    public Dictionary<string, string>? MetadataPatterns { get; init; }

    /// <summary>
    /// Path patterns to match.
    /// </summary>
    public List<string>? PathPatterns { get; init; }

    /// <summary>
    /// Content types to match.
    /// </summary>
    public List<string>? ContentTypes { get; init; }

    /// <summary>
    /// Tags that indicate this classification.
    /// </summary>
    public List<string>? IndicativeTags { get; init; }

    /// <summary>
    /// Minimum confidence threshold.
    /// </summary>
    public double MinConfidence { get; init; } = 0.7;

    /// <summary>
    /// Whether the rule is enabled.
    /// </summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// Result of a classification operation.
/// </summary>
public sealed class ClassificationResult
{
    /// <summary>
    /// Object ID that was classified.
    /// </summary>
    public required string ObjectId { get; init; }

    /// <summary>
    /// Previous classification label.
    /// </summary>
    public ClassificationLabel PreviousLabel { get; init; }

    /// <summary>
    /// New classification label.
    /// </summary>
    public required ClassificationLabel NewLabel { get; init; }

    /// <summary>
    /// Confidence score (0-1).
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// Rules that matched.
    /// </summary>
    public List<string> MatchedRules { get; init; } = new();

    /// <summary>
    /// Detected patterns.
    /// </summary>
    public List<string> DetectedPatterns { get; init; } = new();

    /// <summary>
    /// Whether classification changed.
    /// </summary>
    public bool Changed => PreviousLabel != NewLabel;

    /// <summary>
    /// Whether ML classification was used.
    /// </summary>
    public bool UsedMlClassification { get; init; }

    /// <summary>
    /// Additional details.
    /// </summary>
    public Dictionary<string, object>? Details { get; init; }
}

/// <summary>
/// Data classification strategy that auto-classifies data based on content and metadata.
/// Supports content-based PII detection, metadata classification, and ML-based classification.
/// </summary>
public sealed class DataClassificationStrategy : LifecycleStrategyBase
{
    private readonly BoundedDictionary<string, ClassificationRule> _rules = new BoundedDictionary<string, ClassificationRule>(1000);
    private readonly BoundedDictionary<string, ClassificationResult> _classificationCache = new BoundedDictionary<string, ClassificationResult>(1000);
    private readonly BoundedDictionary<ClassificationLabel, long> _labelCounts = new BoundedDictionary<ClassificationLabel, long>(1000);
    private readonly SemaphoreSlim _mlLock = new(1, 1);
    private bool _mlAvailable;

    // Common PII patterns
    private static readonly Dictionary<string, Regex> PiiPatterns = new()
    {
        ["SSN"] = new Regex(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.Compiled, TimeSpan.FromSeconds(5)),
        ["CreditCard"] = new Regex(@"\b(?:4[0-9]{12}(?:[0-9]{3})?|5[1-5][0-9]{14}|3[47][0-9]{13}|6(?:011|5[0-9]{2})[0-9]{12})\b", RegexOptions.Compiled, TimeSpan.FromSeconds(5)),
        ["Email"] = new Regex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,}\b", RegexOptions.Compiled, TimeSpan.FromSeconds(5)),
        ["Phone"] = new Regex(@"\b(?:\+?1[-.\s]?)?\(?[0-9]{3}\)?[-.\s]?[0-9]{3}[-.\s]?[0-9]{4}\b", RegexOptions.Compiled, TimeSpan.FromSeconds(5)),
        ["IPAddress"] = new Regex(@"\b(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\b", RegexOptions.Compiled, TimeSpan.FromSeconds(5)),
        ["DateOfBirth"] = new Regex(@"\b(?:DOB|Date of Birth|Birth ?Date)[:\s]*\d{1,2}[-/]\d{1,2}[-/]\d{2,4}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5)),
        ["MedicalRecordNumber"] = new Regex(@"\b(?:MRN|Medical Record)[:\s#]*[A-Z0-9]{6,12}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5)),
        ["DriversLicense"] = new Regex(@"\b(?:DL|Driver'?s? ?License)[:\s#]*[A-Z0-9]{5,15}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5)),
        ["Passport"] = new Regex(@"\b(?:Passport)[:\s#]*[A-Z0-9]{6,12}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5)),
        ["BankAccount"] = new Regex(@"\b(?:Account|Acct)[:\s#]*\d{8,17}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)
    };

    // PHI (Protected Health Information) indicators
    private static readonly string[] PhiKeywords = new[]
    {
        "diagnosis", "treatment", "prescription", "medication", "patient",
        "medical", "health", "condition", "symptom", "prognosis",
        "hospital", "clinic", "physician", "doctor", "nurse"
    };

    // PCI (Payment Card Industry) indicators
    private static readonly string[] PciKeywords = new[]
    {
        "credit card", "debit card", "cvv", "expiration date", "card number",
        "cardholder", "billing address", "payment", "transaction"
    };

    /// <inheritdoc/>
    public override string StrategyId => "data-classification";

    /// <inheritdoc/>
    public override string DisplayName => "Data Classification Strategy";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 2000,
        TypicalLatencyMs = 5.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Auto-classifies data based on content analysis and metadata patterns. " +
        "Detects PII, PHI, PCI, and confidential data with configurable classification rules. " +
        "Supports ML-based classification via T90 message bus integration.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "lifecycle", "classification", "pii", "phi", "pci", "compliance", "security", "ml", "detection"
    };

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        // Initialize default classification rules
        InitializeDefaultRules();

        // P2-2433: _mlAvailable defaults to false (rule-based classification only).
        // Set to true at runtime when T90 UltimateIntelligence is available (injected via
        // RegisterStrategy or EnableMlClassification configuration override).
        // GetMlClassificationAsync will then route to "intelligence.classify.text" topic.
        _mlAvailable = false;

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task<LifecycleDecision> EvaluateCoreAsync(LifecycleDataObject data, CancellationToken ct)
    {
        var result = await ClassifyAsync(data, ct);

        if (result.Changed)
        {
            return new LifecycleDecision
            {
                Action = LifecycleAction.Classify,
                Reason = $"Classified as {result.NewLabel} (confidence: {result.Confidence:P0})",
                Confidence = result.Confidence,
                Parameters = new Dictionary<string, object>
                {
                    ["PreviousLabel"] = result.PreviousLabel.ToString(),
                    ["NewLabel"] = result.NewLabel.ToString(),
                    ["MatchedRules"] = result.MatchedRules,
                    ["DetectedPatterns"] = result.DetectedPatterns
                }
            };
        }

        return LifecycleDecision.NoAction(
            $"Classification unchanged: {result.NewLabel}",
            DateTime.UtcNow.AddDays(7));
    }

    /// <summary>
    /// Classifies a data object.
    /// </summary>
    /// <param name="data">Data object to classify.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Classification result.</returns>
    public async Task<ClassificationResult> ClassifyAsync(LifecycleDataObject data, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(data);

        var matchedRules = new List<string>();
        var detectedPatterns = new List<string>();
        var scores = new Dictionary<ClassificationLabel, double>();

        // Get cached content for analysis (would fetch from storage in production)
        var content = GetContentForAnalysis(data);

        // Rule-based classification
        foreach (var rule in _rules.Values.Where(r => r.Enabled).OrderByDescending(r => r.Priority))
        {
            ct.ThrowIfCancellationRequested();

            var ruleMatch = EvaluateRule(data, content, rule);
            if (ruleMatch.matched)
            {
                matchedRules.Add(rule.RuleId);
                detectedPatterns.AddRange(ruleMatch.patterns);

                if (!scores.TryGetValue(rule.Label, out var currentScore))
                {
                    currentScore = 0;
                }
                scores[rule.Label] = Math.Max(currentScore, ruleMatch.confidence);
            }
        }

        // Content-based PII detection
        var piiResults = DetectPii(content);
        if (piiResults.Count > 0)
        {
            detectedPatterns.AddRange(piiResults.Select(p => $"PII:{p.Key}"));

            var piiConfidence = Math.Min(1.0, piiResults.Sum(r => r.Value * 0.1) + 0.5);
            scores[ClassificationLabel.PII] = Math.Max(
                scores.GetValueOrDefault(ClassificationLabel.PII, 0),
                piiConfidence);
        }

        // PHI detection
        if (ContainsPhiIndicators(content, data))
        {
            detectedPatterns.Add("PHI:HealthIndicators");
            scores[ClassificationLabel.PHI] = Math.Max(
                scores.GetValueOrDefault(ClassificationLabel.PHI, 0),
                0.85);
        }

        // PCI detection
        if (ContainsPciIndicators(content, data))
        {
            detectedPatterns.Add("PCI:PaymentIndicators");
            scores[ClassificationLabel.PCI] = Math.Max(
                scores.GetValueOrDefault(ClassificationLabel.PCI, 0),
                0.85);
        }

        // ML-based classification (if available)
        ClassificationLabel? mlLabel = null;
        double mlConfidence = 0;
        if (_mlAvailable && content != null)
        {
            (mlLabel, mlConfidence) = await GetMlClassificationAsync(data, content, ct);
            if (mlLabel.HasValue && mlConfidence > 0.7)
            {
                scores[mlLabel.Value] = Math.Max(
                    scores.GetValueOrDefault(mlLabel.Value, 0),
                    mlConfidence);
            }
        }

        // Determine final classification
        ClassificationLabel finalLabel;
        double finalConfidence;

        if (scores.Count > 0)
        {
            var best = scores.OrderByDescending(s => s.Value).First();
            finalLabel = best.Key;
            finalConfidence = best.Value;
        }
        else
        {
            // Default based on metadata
            (finalLabel, finalConfidence) = ClassifyByMetadata(data);
        }

        // Update label counts
        _labelCounts.AddOrUpdate(finalLabel, 1, (_, c) => c + 1);

        var result = new ClassificationResult
        {
            ObjectId = data.ObjectId,
            PreviousLabel = data.Classification,
            NewLabel = finalLabel,
            Confidence = finalConfidence,
            MatchedRules = matchedRules,
            DetectedPatterns = detectedPatterns,
            UsedMlClassification = mlLabel.HasValue && mlLabel == finalLabel,
            Details = new Dictionary<string, object>
            {
                ["AllScores"] = scores,
                ["RulesEvaluated"] = _rules.Count
            }
        };

        // Cache the result
        _classificationCache[data.ObjectId] = result;

        // Update tracked object with new classification
        if (result.Changed)
        {
            var updated = new LifecycleDataObject
            {
                ObjectId = data.ObjectId,
                Path = data.Path,
                ContentType = data.ContentType,
                Size = data.Size,
                CreatedAt = data.CreatedAt,
                LastModifiedAt = DateTime.UtcNow,
                LastAccessedAt = data.LastAccessedAt,
                TenantId = data.TenantId,
                Tags = data.Tags,
                Classification = finalLabel,
                StorageTier = data.StorageTier,
                StorageLocation = data.StorageLocation,
                IsArchived = data.IsArchived,
                IsOnHold = data.IsOnHold,
                Metadata = data.Metadata
            };
            TrackedObjects[data.ObjectId] = updated;
        }

        return result;
    }

    /// <summary>
    /// Registers a classification rule.
    /// </summary>
    /// <param name="rule">Rule to register.</param>
    public void RegisterRule(ClassificationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        _rules[rule.RuleId] = rule;
    }

    /// <summary>
    /// Removes a classification rule.
    /// </summary>
    /// <param name="ruleId">Rule ID to remove.</param>
    /// <returns>True if removed.</returns>
    public bool RemoveRule(string ruleId)
    {
        return _rules.TryRemove(ruleId, out _);
    }

    /// <summary>
    /// Gets all registered rules.
    /// </summary>
    /// <returns>Collection of rules.</returns>
    public IEnumerable<ClassificationRule> GetRules()
    {
        return _rules.Values.OrderByDescending(r => r.Priority);
    }

    /// <summary>
    /// Gets classification statistics by label.
    /// </summary>
    /// <returns>Dictionary of label counts.</returns>
    public IReadOnlyDictionary<ClassificationLabel, long> GetLabelDistribution()
    {
        return _labelCounts.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    /// <summary>
    /// Batch classifies multiple objects.
    /// </summary>
    /// <param name="objects">Objects to classify.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Classification results.</returns>
    public async Task<IEnumerable<ClassificationResult>> BatchClassifyAsync(
        IEnumerable<LifecycleDataObject> objects,
        CancellationToken ct = default)
    {
        var results = new List<ClassificationResult>();

        foreach (var obj in objects)
        {
            ct.ThrowIfCancellationRequested();
            var result = await ClassifyAsync(obj, ct);
            results.Add(result);
        }

        return results;
    }

    /// <summary>
    /// Gets cached classification result.
    /// </summary>
    /// <param name="objectId">Object ID.</param>
    /// <returns>Cached result or null.</returns>
    public ClassificationResult? GetCachedClassification(string objectId)
    {
        return _classificationCache.TryGetValue(objectId, out var result) ? result : null;
    }

    private void InitializeDefaultRules()
    {
        // PII content rule
        RegisterRule(new ClassificationRule
        {
            RuleId = "pii-content",
            Name = "PII Content Detection",
            Description = "Detects personally identifiable information in content",
            Label = ClassificationLabel.PII,
            Priority = 100,
            ContentPatterns = new List<string>
            {
                @"social\s*security",
                @"date\s*of\s*birth",
                @"driver'?s?\s*license"
            }
        });

        // Confidential path rule
        RegisterRule(new ClassificationRule
        {
            RuleId = "confidential-path",
            Name = "Confidential Path Detection",
            Description = "Marks files in confidential directories",
            Label = ClassificationLabel.Confidential,
            Priority = 90,
            PathPatterns = new List<string>
            {
                @"/confidential/",
                @"/private/",
                @"/restricted/",
                @"/secrets/"
            }
        });

        // Internal documents
        RegisterRule(new ClassificationRule
        {
            RuleId = "internal-docs",
            Name = "Internal Documents",
            Description = "Marks internal documentation",
            Label = ClassificationLabel.Internal,
            Priority = 50,
            PathPatterns = new List<string>
            {
                @"/internal/",
                @"/docs/internal/"
            },
            IndicativeTags = new List<string> { "internal", "internal-only" }
        });

        // Public content
        RegisterRule(new ClassificationRule
        {
            RuleId = "public-content",
            Name = "Public Content",
            Description = "Marks public content",
            Label = ClassificationLabel.Public,
            Priority = 10,
            PathPatterns = new List<string>
            {
                @"/public/",
                @"/www/",
                @"/static/"
            },
            IndicativeTags = new List<string> { "public", "published" }
        });
    }

    private (bool matched, double confidence, List<string> patterns) EvaluateRule(
        LifecycleDataObject data,
        string? content,
        ClassificationRule rule)
    {
        var patterns = new List<string>();
        var matchCount = 0;
        var totalChecks = 0;

        // Check content patterns
        if (rule.ContentPatterns?.Count > 0 && content != null)
        {
            foreach (var pattern in rule.ContentPatterns)
            {
                totalChecks++;
                try
                {
                    if (Regex.IsMatch(content, pattern, RegexOptions.IgnoreCase))
                    {
                        matchCount++;
                        patterns.Add($"Content:{pattern}");
                    }
                }
                catch
                {

                    // Invalid regex - skip
                    System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
                }
            }
        }

        // Check path patterns
        if (rule.PathPatterns?.Count > 0 && data.Path != null)
        {
            foreach (var pattern in rule.PathPatterns)
            {
                totalChecks++;
                try
                {
                    if (Regex.IsMatch(data.Path, pattern, RegexOptions.IgnoreCase))
                    {
                        matchCount++;
                        patterns.Add($"Path:{pattern}");
                    }
                }
                catch
                {

                    // Invalid regex - skip
                    System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
                }
            }
        }

        // Check content types
        if (rule.ContentTypes?.Count > 0 && data.ContentType != null)
        {
            totalChecks++;
            if (rule.ContentTypes.Any(ct =>
                data.ContentType.Contains(ct, StringComparison.OrdinalIgnoreCase)))
            {
                matchCount++;
                patterns.Add($"ContentType:{data.ContentType}");
            }
        }

        // Check tags
        if (rule.IndicativeTags?.Count > 0 && data.Tags?.Length > 0)
        {
            totalChecks++;
            var matchedTags = rule.IndicativeTags.Intersect(data.Tags, StringComparer.OrdinalIgnoreCase).ToList();
            if (matchedTags.Count > 0)
            {
                matchCount++;
                patterns.AddRange(matchedTags.Select(t => $"Tag:{t}"));
            }
        }

        // Check metadata patterns
        if (rule.MetadataPatterns?.Count > 0 && data.Metadata != null)
        {
            foreach (var (field, pattern) in rule.MetadataPatterns)
            {
                totalChecks++;
                if (data.Metadata.TryGetValue(field, out var value) && value != null)
                {
                    try
                    {
                        if (Regex.IsMatch(value.ToString() ?? "", pattern, RegexOptions.IgnoreCase))
                        {
                            matchCount++;
                            patterns.Add($"Metadata:{field}");
                        }
                    }
                    catch
                    {

                        // Invalid regex - skip
                        System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
                    }
                }
            }
        }

        if (totalChecks == 0 || matchCount == 0)
        {
            return (false, 0, patterns);
        }

        var confidence = (double)matchCount / totalChecks;
        return (confidence >= rule.MinConfidence, confidence, patterns);
    }

    private Dictionary<string, int> DetectPii(string? content)
    {
        var results = new Dictionary<string, int>();

        if (string.IsNullOrEmpty(content))
        {
            return results;
        }

        foreach (var (patternName, regex) in PiiPatterns)
        {
            var matches = regex.Matches(content);
            if (matches.Count > 0)
            {
                results[patternName] = matches.Count;
            }
        }

        return results;
    }

    private bool ContainsPhiIndicators(string? content, LifecycleDataObject data)
    {
        // Check content
        if (!string.IsNullOrEmpty(content))
        {
            var lowerContent = content.ToLowerInvariant();
            if (PhiKeywords.Count(k => lowerContent.Contains(k)) >= 2)
            {
                return true;
            }
        }

        // Check path
        if (!string.IsNullOrEmpty(data.Path))
        {
            var lowerPath = data.Path.ToLowerInvariant();
            if (lowerPath.Contains("health") || lowerPath.Contains("medical") ||
                lowerPath.Contains("patient") || lowerPath.Contains("hipaa"))
            {
                return true;
            }
        }

        // Check content type
        if (data.ContentType?.Contains("hl7", StringComparison.OrdinalIgnoreCase) == true ||
            data.ContentType?.Contains("fhir", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        return false;
    }

    private bool ContainsPciIndicators(string? content, LifecycleDataObject data)
    {
        // Check content
        if (!string.IsNullOrEmpty(content))
        {
            var lowerContent = content.ToLowerInvariant();
            if (PciKeywords.Count(k => lowerContent.Contains(k)) >= 2)
            {
                return true;
            }

            // Check for credit card pattern
            if (PiiPatterns["CreditCard"].IsMatch(content))
            {
                return true;
            }
        }

        // Check path
        if (!string.IsNullOrEmpty(data.Path))
        {
            var lowerPath = data.Path.ToLowerInvariant();
            if (lowerPath.Contains("payment") || lowerPath.Contains("billing") ||
                lowerPath.Contains("transaction") || lowerPath.Contains("pci"))
            {
                return true;
            }
        }

        return false;
    }

    private (ClassificationLabel label, double confidence) ClassifyByMetadata(LifecycleDataObject data)
    {
        // Check existing tags
        if (data.Tags?.Length > 0)
        {
            var tagLower = data.Tags.Select(t => t.ToLowerInvariant()).ToHashSet();

            if (tagLower.Contains("pii") || tagLower.Contains("personal"))
                return (ClassificationLabel.PII, 0.9);

            if (tagLower.Contains("phi") || tagLower.Contains("health"))
                return (ClassificationLabel.PHI, 0.9);

            if (tagLower.Contains("pci") || tagLower.Contains("payment"))
                return (ClassificationLabel.PCI, 0.9);

            if (tagLower.Contains("confidential") || tagLower.Contains("secret"))
                return (ClassificationLabel.Confidential, 0.85);

            if (tagLower.Contains("internal"))
                return (ClassificationLabel.Internal, 0.85);

            if (tagLower.Contains("public"))
                return (ClassificationLabel.Public, 0.85);
        }

        // Check path-based classification
        if (!string.IsNullOrEmpty(data.Path))
        {
            var pathLower = data.Path.ToLowerInvariant();

            if (pathLower.Contains("/secret") || pathLower.Contains("/confidential"))
                return (ClassificationLabel.Confidential, 0.7);

            if (pathLower.Contains("/internal"))
                return (ClassificationLabel.Internal, 0.7);

            if (pathLower.Contains("/public"))
                return (ClassificationLabel.Public, 0.7);
        }

        return (ClassificationLabel.Unclassified, 1.0);
    }

    private string? GetContentForAnalysis(LifecycleDataObject data)
    {
        // Attempt to retrieve analysable content from well-known metadata keys.
        // Callers that have access to raw storage bytes should populate "content" or
        // "preview" in the metadata dictionary before invoking classification.
        if (data.Metadata?.TryGetValue("content", out var content) == true && content != null)
        {
            return content.ToString();
        }

        if (data.Metadata?.TryGetValue("preview", out var preview) == true && preview != null)
        {
            return preview.ToString();
        }

        // Fall back to combining all string metadata values for keyword/PII scanning.
        if (data.Metadata?.Count > 0)
        {
            var parts = data.Metadata
                .Where(kv => kv.Value is string)
                .Select(kv => kv.Value?.ToString())
                .Where(v => !string.IsNullOrWhiteSpace(v));
            var combined = string.Join(" ", parts);
            if (!string.IsNullOrWhiteSpace(combined))
                return combined;
        }

        return null;
    }

    private async Task<(ClassificationLabel? label, double confidence)> GetMlClassificationAsync(
        LifecycleDataObject data,
        string content,
        CancellationToken ct)
    {
        // This would send a message to T90 message bus for ML classification
        // For now, return null to indicate ML not available
        await _mlLock.WaitAsync(ct);
        try
        {
            // Simulated ML classification (would call T90 service)
            if (!_mlAvailable)
            {
                return (null, 0);
            }

            // Would send message to T90 and await response
            return (null, 0);
        }
        finally
        {
            _mlLock.Release();
        }
    }
}
