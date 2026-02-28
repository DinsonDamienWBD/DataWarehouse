using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Consciousness;

namespace DataWarehouse.Plugins.UltimateDataGovernance.Strategies.IntelligentGovernance;

#region Shared Constants

/// <summary>
/// Shared constants for liability scoring strategies including compiled regex patterns
/// and the maximum data scan size (1 MB) to prevent OOM on large objects.
/// </summary>
internal static class LiabilityScanConstants
{
    /// <summary>Maximum bytes to scan for pattern detection (1 MB).</summary>
    internal const int MaxScanBytes = 1_048_576;

    // PII patterns (compiled for performance)
    internal static readonly Regex EmailPattern = new(
        @"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}",
        RegexOptions.Compiled, TimeSpan.FromSeconds(5));

    internal static readonly Regex SsnPattern = new(
        @"\b\d{3}-\d{2}-\d{4}\b",
        RegexOptions.Compiled, TimeSpan.FromSeconds(5));

    internal static readonly Regex PhonePattern = new(
        @"(?:\+\d{1,3}[\s\-]?)?\(?\d{2,4}\)?[\s\-]?\d{3,4}[\s\-]?\d{3,4}\b",
        RegexOptions.Compiled, TimeSpan.FromSeconds(5));

    internal static readonly Regex CreditCardPattern = new(
        @"\b(?:\d[\s\-]?){13,19}\b",
        RegexOptions.Compiled, TimeSpan.FromSeconds(5));

    internal static readonly Regex PassportPattern = new(
        @"\b[A-Z]{1,2}\d{6,9}\b",
        RegexOptions.Compiled, TimeSpan.FromSeconds(5));

    internal static readonly Regex DobPattern = new(
        @"\b(?:0[1-9]|1[0-2])[/\-](?:0[1-9]|[12]\d|3[01])[/\-](?:19|20)\d{2}\b",
        RegexOptions.Compiled, TimeSpan.FromSeconds(5));

    // PHI patterns
    internal static readonly Regex IcdCodePattern = new(
        @"\b[A-Z]\d{2}(?:\.\d{1,4})?\b",
        RegexOptions.Compiled, TimeSpan.FromSeconds(5));

    internal static readonly Regex MedicationPattern = new(
        @"\b\w+(?:mab|nib|ide|ine|pril|sartan|statin|olol|azole|mycin|cillin|floxacin)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));

    internal static readonly Regex PatientIdPattern = new(
        @"\bPAT[\-_]?\d{6,10}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));

    internal static readonly Regex DiagnosisKeywordPattern = new(
        @"\b(?:diagnosis|diagnosed|prognosis|symptom|condition|disorder|disease|syndrome|carcinoma|tumor|fracture|infection)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));

    // PCI patterns
    internal static readonly Regex CvvPattern = new(
        @"\b\d{3,4}\b",
        RegexOptions.Compiled, TimeSpan.FromSeconds(5));

    internal static readonly Regex ExpiryDatePattern = new(
        @"\b(?:0[1-9]|1[0-2])[/\-]\d{2}\b",
        RegexOptions.Compiled, TimeSpan.FromSeconds(5));

    /// <summary>
    /// Validates a digit sequence using the Luhn algorithm.
    /// </summary>
    internal static bool PassesLuhnCheck(string digits)
    {
        // Strip spaces and dashes
        var cleaned = new string(digits.Where(char.IsDigit).ToArray());
        if (cleaned.Length < 13 || cleaned.Length > 19)
            return false;

        int sum = 0;
        bool alternate = false;
        for (int i = cleaned.Length - 1; i >= 0; i--)
        {
            int n = cleaned[i] - '0';
            if (alternate)
            {
                n *= 2;
                if (n > 9) n -= 9;
            }
            sum += n;
            alternate = !alternate;
        }
        return sum % 10 == 0;
    }

    /// <summary>
    /// Extracts scannable text from raw data bytes, capped at <see cref="MaxScanBytes"/>.
    /// </summary>
    internal static string ExtractText(byte[] data)
    {
        if (data == null || data.Length == 0)
            return string.Empty;

        int length = Math.Min(data.Length, MaxScanBytes);
        return Encoding.UTF8.GetString(data, 0, length);
    }
}

#endregion

#region Strategy 1: PIILiabilityStrategy

/// <summary>
/// Scores liability based on the presence of Personally Identifiable Information (PII).
/// Scans both metadata and raw data bytes using compiled regex patterns for
/// email addresses, SSNs, phone numbers, credit card numbers (Luhn-validated),
/// passport numbers, and dates of birth.
/// </summary>
public sealed class PIILiabilityStrategy : ConsciousnessStrategyBase
{
    public override string StrategyId => "liability-pii";
    public override string DisplayName => "PII Liability Scorer";
    public override ConsciousnessCategory Category => ConsciousnessCategory.LiabilityScoring;
    public override ConsciousnessCapabilities Capabilities => new(
        SupportsAsync: true, SupportsBatch: true, SupportsRealTime: true);
    public override string SemanticDescription =>
        "Detects personally identifiable information (email, SSN, phone, credit card, passport, DOB) " +
        "in data content and metadata to produce a PII liability score from 0-100.";
    public override string[] Tags => ["liability", "pii", "privacy", "detection", "regex"];

    private static readonly Dictionary<string, int> PiiTypeScores = new()
    {
        ["email"] = 15,
        ["ssn"] = 40,
        ["phone"] = 10,
        ["credit_card"] = 50,
        ["passport"] = 30,
        ["name"] = 5,
        ["address"] = 10,
        ["dob"] = 20
    };

    /// <summary>
    /// Scores PII liability for a data object by scanning metadata and data content.
    /// </summary>
    public Task<(double Score, List<string> DetectedTypes, List<string> Factors)> ScoreAsync(
        byte[] data, IReadOnlyDictionary<string, object> metadata, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IncrementCounter("pii_scans");

        var detectedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var factors = new List<string>();

        // Check metadata for pre-detected PII types
        if (metadata.TryGetValue("detected_pii_types", out var piiTypesObj))
        {
            var types = ExtractStringArray(piiTypesObj);
            foreach (var t in types)
            {
                detectedTypes.Add(t.ToLowerInvariant());
                factors.Add($"Metadata declares PII type: {t}");
            }
        }

        // Scan data bytes for PII patterns
        var text = LiabilityScanConstants.ExtractText(data);
        if (!string.IsNullOrEmpty(text))
        {
            if (LiabilityScanConstants.EmailPattern.IsMatch(text))
            {
                detectedTypes.Add("email");
                factors.Add("Email address pattern detected in data content");
            }

            if (LiabilityScanConstants.SsnPattern.IsMatch(text))
            {
                detectedTypes.Add("ssn");
                factors.Add("SSN pattern (XXX-XX-XXXX) detected in data content");
            }

            if (LiabilityScanConstants.PhonePattern.IsMatch(text))
            {
                detectedTypes.Add("phone");
                factors.Add("Phone number pattern detected in data content");
            }

            // Credit card with Luhn validation
            var ccMatches = LiabilityScanConstants.CreditCardPattern.Matches(text);
            foreach (Match m in ccMatches)
            {
                if (LiabilityScanConstants.PassesLuhnCheck(m.Value))
                {
                    detectedTypes.Add("credit_card");
                    factors.Add("Luhn-valid credit card number detected in data content");
                    break;
                }
            }

            if (LiabilityScanConstants.PassportPattern.IsMatch(text))
            {
                detectedTypes.Add("passport");
                factors.Add("Passport number pattern detected in data content");
            }

            if (LiabilityScanConstants.DobPattern.IsMatch(text))
            {
                detectedTypes.Add("dob");
                factors.Add("Date of birth pattern detected in data content");
            }
        }

        // Calculate score from detected types
        double score = 0;
        foreach (var type in detectedTypes)
        {
            if (PiiTypeScores.TryGetValue(type, out int typeScore))
                score += typeScore;
        }

        score = Math.Min(score, 100.0);

        if (score > 0) IncrementCounter("pii_detected");

        return Task.FromResult((score, detectedTypes.ToList(), factors));
    }

    private static string[] ExtractStringArray(object value)
    {
        return value switch
        {
            string[] arr => arr,
            IEnumerable<string> enumerable => enumerable.ToArray(),
            string s => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            _ => Array.Empty<string>()
        };
    }
}

#endregion

#region Strategy 2: PHILiabilityStrategy

/// <summary>
/// Scores liability based on the presence of Protected Health Information (PHI).
/// Scans for ICD codes, medication names (common drug suffixes), diagnosis keywords,
/// and patient ID patterns. HIPAA-relevant data receives a minimum score of 80.
/// </summary>
public sealed class PHILiabilityStrategy : ConsciousnessStrategyBase
{
    public override string StrategyId => "liability-phi";
    public override string DisplayName => "PHI Liability Scorer";
    public override ConsciousnessCategory Category => ConsciousnessCategory.LiabilityScoring;
    public override ConsciousnessCapabilities Capabilities => new(
        SupportsAsync: true, SupportsBatch: true, SupportsRealTime: true);
    public override string SemanticDescription =>
        "Detects protected health information (ICD codes, medications, diagnoses, patient IDs) " +
        "in data content and metadata to produce a PHI liability score from 0-100.";
    public override string[] Tags => ["liability", "phi", "hipaa", "health", "detection"];

    private static readonly Dictionary<string, int> PhiTypeScores = new()
    {
        ["diagnosis"] = 30,
        ["medication"] = 25,
        ["lab_result"] = 20,
        ["procedure"] = 25,
        ["patient_id"] = 40
    };

    /// <summary>
    /// Scores PHI liability for a data object.
    /// </summary>
    public Task<(double Score, List<string> Factors)> ScoreAsync(
        byte[] data, IReadOnlyDictionary<string, object> metadata, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IncrementCounter("phi_scans");

        var detectedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var factors = new List<string>();
        bool hipaaRelevant = false;

        // Check metadata
        if (metadata.TryGetValue("hipaa_relevant", out var hipaaObj) && hipaaObj is bool hr && hr)
        {
            hipaaRelevant = true;
            factors.Add("Data flagged as HIPAA-relevant in metadata");
        }

        if (metadata.TryGetValue("detected_phi_types", out var phiTypesObj))
        {
            var types = ExtractStringArray(phiTypesObj);
            foreach (var t in types)
            {
                detectedTypes.Add(t.ToLowerInvariant());
                factors.Add($"Metadata declares PHI type: {t}");
            }
        }

        // Scan data bytes
        var text = LiabilityScanConstants.ExtractText(data);
        if (!string.IsNullOrEmpty(text))
        {
            if (LiabilityScanConstants.IcdCodePattern.IsMatch(text))
            {
                detectedTypes.Add("diagnosis");
                factors.Add("ICD code pattern detected in data content");
            }

            if (LiabilityScanConstants.MedicationPattern.IsMatch(text))
            {
                detectedTypes.Add("medication");
                factors.Add("Medication name pattern detected in data content");
            }

            if (LiabilityScanConstants.DiagnosisKeywordPattern.IsMatch(text))
            {
                if (!detectedTypes.Contains("diagnosis"))
                {
                    detectedTypes.Add("diagnosis");
                    factors.Add("Diagnosis keyword detected in data content");
                }
            }

            if (LiabilityScanConstants.PatientIdPattern.IsMatch(text))
            {
                detectedTypes.Add("patient_id");
                factors.Add("Patient ID pattern detected in data content");
            }
        }

        // Calculate score
        double score = 0;
        foreach (var type in detectedTypes)
        {
            if (PhiTypeScores.TryGetValue(type, out int typeScore))
                score += typeScore;
        }

        // HIPAA-relevant forces minimum 80
        if (hipaaRelevant)
            score = Math.Max(score, 80.0);

        score = Math.Min(score, 100.0);

        if (score > 0) IncrementCounter("phi_detected");

        return Task.FromResult((score, factors));
    }

    private static string[] ExtractStringArray(object value)
    {
        return value switch
        {
            string[] arr => arr,
            IEnumerable<string> enumerable => enumerable.ToArray(),
            string s => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            _ => Array.Empty<string>()
        };
    }
}

#endregion

#region Strategy 3: PCILiabilityStrategy

/// <summary>
/// Scores liability based on the presence of Payment Card Industry (PCI) data.
/// Detects credit card numbers (with Luhn validation), CVV patterns, and expiry dates.
/// Tokenized data receives a 60% reduction in liability score.
/// </summary>
public sealed class PCILiabilityStrategy : ConsciousnessStrategyBase
{
    public override string StrategyId => "liability-pci";
    public override string DisplayName => "PCI Liability Scorer";
    public override ConsciousnessCategory Category => ConsciousnessCategory.LiabilityScoring;
    public override ConsciousnessCapabilities Capabilities => new(
        SupportsAsync: true, SupportsBatch: true, SupportsRealTime: true);
    public override string SemanticDescription =>
        "Detects payment card data (card numbers, CVV, expiry) in content and metadata. " +
        "Tokenized data receives reduced liability. CVV storage is flagged as a PCI violation.";
    public override string[] Tags => ["liability", "pci", "payment", "credit-card", "detection"];

    /// <summary>
    /// Scores PCI liability for a data object.
    /// </summary>
    public Task<(double Score, List<string> Factors)> ScoreAsync(
        byte[] data, IReadOnlyDictionary<string, object> metadata, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IncrementCounter("pci_scans");

        var factors = new List<string>();
        double score = 0;
        bool tokenized = false;

        // Check metadata flags
        if (metadata.TryGetValue("pci_scope", out var pciObj) && pciObj is bool pci && pci)
        {
            factors.Add("Data marked as in PCI scope");
        }

        if (metadata.TryGetValue("tokenized", out var tokObj) && tokObj is bool tok && tok)
        {
            tokenized = true;
            factors.Add("Data is tokenized (60% liability reduction applied)");
        }

        if (metadata.TryGetValue("card_data_present", out var cardObj) && cardObj is bool card && card)
        {
            score += 60;
            factors.Add("Metadata indicates card data present");
        }

        // Scan data bytes
        var text = LiabilityScanConstants.ExtractText(data);
        if (!string.IsNullOrEmpty(text))
        {
            // Credit card numbers with Luhn validation
            var ccMatches = LiabilityScanConstants.CreditCardPattern.Matches(text);
            bool cardFound = false;
            foreach (Match m in ccMatches)
            {
                if (LiabilityScanConstants.PassesLuhnCheck(m.Value))
                {
                    cardFound = true;
                    break;
                }
            }

            if (cardFound)
            {
                score += 60;
                factors.Add("Luhn-valid credit card number detected in data content");

                // Check for CVV near card numbers (3-4 digit sequences)
                if (LiabilityScanConstants.CvvPattern.IsMatch(text))
                {
                    score += 80;
                    factors.Add("CVV pattern detected near card data (PCI violation: CVV storage prohibited)");
                }
            }

            // Expiry date patterns
            if (LiabilityScanConstants.ExpiryDatePattern.IsMatch(text))
            {
                score += 30;
                factors.Add("Card expiry date pattern (MM/YY) detected in data content");
            }
        }

        // Check for cardholder name in metadata
        if (metadata.ContainsKey("cardholder_name"))
        {
            score += 20;
            factors.Add("Cardholder name present in metadata");
        }

        // Tokenized data reduces liability by 60%
        if (tokenized)
            score *= 0.4;

        score = Math.Min(score, 100.0);

        if (score > 0) IncrementCounter("pci_detected");

        return Task.FromResult((score, factors));
    }
}

#endregion

#region Strategy 4: ClassificationLiabilityStrategy

/// <summary>
/// Scores liability based on data classification level. Higher classification
/// levels indicate more sensitive data requiring stricter controls.
/// Unclassified data receives a score of 40 because absence of classification
/// is itself a governance risk.
/// </summary>
public sealed class ClassificationLiabilityStrategy : ConsciousnessStrategyBase
{
    public override string StrategyId => "liability-classification";
    public override string DisplayName => "Classification Liability Scorer";
    public override ConsciousnessCategory Category => ConsciousnessCategory.LiabilityScoring;
    public override ConsciousnessCapabilities Capabilities => new(
        SupportsAsync: true, SupportsBatch: true, SupportsRealTime: true);
    public override string SemanticDescription =>
        "Maps data classification level (public, internal, confidential, restricted, top_secret) " +
        "to a liability score. Unclassified data scores 40 because missing classification is a governance gap.";
    public override string[] Tags => ["liability", "classification", "sensitivity"];

    private static readonly Dictionary<string, double> ClassificationScores = new(StringComparer.OrdinalIgnoreCase)
    {
        ["public"] = 0,
        ["internal"] = 20,
        ["confidential"] = 50,
        ["restricted"] = 80,
        ["top_secret"] = 100
    };

    /// <summary>
    /// Scores classification liability for a data object.
    /// </summary>
    public Task<(double Score, List<string> Factors)> ScoreAsync(
        IReadOnlyDictionary<string, object> metadata, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IncrementCounter("classification_scans");

        var factors = new List<string>();

        string level = "unclassified";
        if (metadata.TryGetValue("classification_level", out var clObj) && clObj is string cl && !string.IsNullOrWhiteSpace(cl))
        {
            level = cl.Trim();
        }

        double score;
        if (ClassificationScores.TryGetValue(level, out double knownScore))
        {
            score = knownScore;
            factors.Add($"Classification level '{level}' maps to liability score {score}");
        }
        else
        {
            // Unclassified or unknown classification
            score = 40;
            factors.Add($"Classification level '{level}' is unrecognized; scored as unclassified (40) — missing classification is a governance risk");
        }

        return Task.FromResult((score, factors));
    }
}

#endregion

#region Strategy 5: RetentionLiabilityStrategy

/// <summary>
/// Scores liability based on data retention obligations. Legal holds, overdue
/// disposal, missing retention policies, and expired retention windows all
/// contribute to different liability levels.
/// </summary>
public sealed class RetentionLiabilityStrategy : ConsciousnessStrategyBase
{
    public override string StrategyId => "liability-retention";
    public override string DisplayName => "Retention Liability Scorer";
    public override ConsciousnessCategory Category => ConsciousnessCategory.LiabilityScoring;
    public override ConsciousnessCapabilities Capabilities => new(
        SupportsAsync: true, SupportsBatch: true, SupportsRealTime: true);
    public override string SemanticDescription =>
        "Scores liability based on retention obligations: legal holds (90), overdue disposal (85), " +
        "active retention (50), no policy (60), and expired retention without hold (30).";
    public override string[] Tags => ["liability", "retention", "compliance", "legal-hold"];

    /// <summary>
    /// Scores retention liability for a data object.
    /// </summary>
    public Task<(double Score, List<string> Factors)> ScoreAsync(
        IReadOnlyDictionary<string, object> metadata, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IncrementCounter("retention_scans");

        var factors = new List<string>();

        bool hasRetentionPolicy = metadata.ContainsKey("retention_policy") &&
            metadata["retention_policy"] is string rp && !string.IsNullOrWhiteSpace(rp);
        bool legalHold = metadata.TryGetValue("legal_hold", out var lhObj) && lhObj is bool lh && lh;
        bool disposalOverdue = metadata.TryGetValue("disposal_overdue", out var doObj) && doObj is bool dov && dov;

        DateTime? retentionEndDate = null;
        if (metadata.TryGetValue("retention_end_date", out var redObj))
        {
            retentionEndDate = redObj switch
            {
                DateTime dt => dt,
                string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) => parsed,
                _ => null
            };
        }

        double score;

        if (legalHold)
        {
            score = 90;
            factors.Add("Data is under legal hold — cannot be disposed, high liability exposure");
        }
        else if (disposalOverdue)
        {
            score = 85;
            factors.Add("Data disposal is overdue — keeping data past retention window is a compliance violation");
        }
        else if (!hasRetentionPolicy)
        {
            score = 60;
            factors.Add("No retention policy defined — policy absence is itself a governance risk");
        }
        else if (retentionEndDate.HasValue && retentionEndDate.Value < DateTime.UtcNow)
        {
            // Expired retention, no legal hold
            score = 30;
            factors.Add("Retention window has expired with no legal hold — reduced but non-zero liability");
        }
        else
        {
            // Active retention policy
            score = 50;
            factors.Add("Data has active retention policy — standard liability for governed data");
        }

        return Task.FromResult((score, factors));
    }
}

#endregion

#region Strategy 6: RegulatoryExposureLiabilityStrategy

/// <summary>
/// Scores liability based on applicable regulations and jurisdictional exposure.
/// Each regulation contributes a weighted score, and cross-border data transfers
/// apply a 1.3x multiplier to the total.
/// </summary>
public sealed class RegulatoryExposureLiabilityStrategy : ConsciousnessStrategyBase
{
    public override string StrategyId => "liability-regulatory";
    public override string DisplayName => "Regulatory Exposure Liability Scorer";
    public override ConsciousnessCategory Category => ConsciousnessCategory.LiabilityScoring;
    public override ConsciousnessCapabilities Capabilities => new(
        SupportsAsync: true, SupportsBatch: true, SupportsRealTime: true);
    public override string SemanticDescription =>
        "Scores regulatory exposure based on applicable regulations (GDPR, CCPA, HIPAA, SOX, PCI-DSS, LGPD, PIPL, PDPA) " +
        "and cross-border data transfer multiplier (1.3x).";
    public override string[] Tags => ["liability", "regulatory", "compliance", "gdpr", "hipaa", "cross-border"];

    private static readonly Dictionary<string, int> RegulationScores = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GDPR"] = 25,
        ["CCPA"] = 20,
        ["HIPAA"] = 30,
        ["SOX"] = 25,
        ["PCI_DSS"] = 30,
        ["PCI-DSS"] = 30,
        ["LGPD"] = 20,
        ["PIPL"] = 20,
        ["PDPA"] = 15
    };

    /// <summary>
    /// Scores regulatory exposure liability for a data object.
    /// </summary>
    public Task<(double Score, List<string> ApplicableRegulations, List<string> Factors)> ScoreAsync(
        IReadOnlyDictionary<string, object> metadata, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IncrementCounter("regulatory_scans");

        var factors = new List<string>();
        var applicableRegulations = new List<string>();
        double score = 0;

        // Check applicable regulations
        if (metadata.TryGetValue("applicable_regulations", out var regObj))
        {
            var regulations = ExtractStringArray(regObj);
            foreach (var reg in regulations)
            {
                var normalized = reg.Trim().ToUpperInvariant();
                applicableRegulations.Add(normalized);

                if (RegulationScores.TryGetValue(normalized, out int regScore))
                {
                    score += regScore;
                    factors.Add($"Regulation {normalized} applies (score contribution: {regScore})");
                }
                else
                {
                    score += 15; // Unknown regulation still adds liability
                    factors.Add($"Regulation {normalized} applies (unknown regulation, default contribution: 15)");
                }
            }
        }

        // Cross-border multiplier
        bool crossBorder = metadata.TryGetValue("cross_border", out var cbObj) && cbObj is bool cb && cb;
        if (crossBorder)
        {
            score *= 1.3;
            factors.Add("Cross-border data transfer detected (1.3x multiplier applied)");
        }

        score = Math.Min(score, 100.0);

        return Task.FromResult((score, applicableRegulations, factors));
    }

    private static string[] ExtractStringArray(object value)
    {
        return value switch
        {
            string[] arr => arr,
            IEnumerable<string> enumerable => enumerable.ToArray(),
            string s => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            _ => Array.Empty<string>()
        };
    }
}

#endregion

#region Strategy 7: BreachRiskLiabilityStrategy

/// <summary>
/// Scores liability based on breach risk factors: encryption status, access control level,
/// sharing scope, and security scan recency. Unencrypted sensitive data with public access
/// represents maximum breach risk.
/// </summary>
public sealed class BreachRiskLiabilityStrategy : ConsciousnessStrategyBase
{
    public override string StrategyId => "liability-breach-risk";
    public override string DisplayName => "Breach Risk Liability Scorer";
    public override ConsciousnessCategory Category => ConsciousnessCategory.LiabilityScoring;
    public override ConsciousnessCapabilities Capabilities => new(
        SupportsAsync: true, SupportsBatch: true, SupportsRealTime: true);
    public override string SemanticDescription =>
        "Scores breach risk based on encryption status, access controls, sharing scope, " +
        "and security scan recency. Unencrypted + public access = highest risk.";
    public override string[] Tags => ["liability", "breach-risk", "encryption", "access-control", "security"];

    /// <summary>
    /// Scores breach risk liability for a data object.
    /// </summary>
    public Task<(double Score, List<string> Factors)> ScoreAsync(
        IReadOnlyDictionary<string, object> metadata, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IncrementCounter("breach_risk_scans");

        var factors = new List<string>();
        double score = 0;

        // Encryption status
        string encryptionStatus = "unencrypted";
        if (metadata.TryGetValue("encryption_status", out var esObj) && esObj is string es)
            encryptionStatus = es.Trim().ToLowerInvariant();

        switch (encryptionStatus)
        {
            case "unencrypted":
                score += 80;
                factors.Add("Data is unencrypted — maximum encryption risk");
                break;
            case "partial":
                score += 40;
                factors.Add("Data is partially encrypted — reduced but present risk");
                break;
            case "encrypted":
                score += 10;
                factors.Add("Data is fully encrypted — minimal encryption risk");
                break;
            default:
                score += 50;
                factors.Add($"Unknown encryption status '{encryptionStatus}' — treated as moderate risk");
                break;
        }

        // Access control level
        string accessLevel = "private";
        if (metadata.TryGetValue("access_control_level", out var acObj) && acObj is string ac)
            accessLevel = ac.Trim().ToLowerInvariant();

        switch (accessLevel)
        {
            case "public":
                score += 70;
                factors.Add("Public access control — maximum access risk");
                break;
            case "restricted":
                score += 30;
                factors.Add("Restricted access control — moderate access risk");
                break;
            case "private":
                score += 10;
                factors.Add("Private access control — minimal access risk");
                break;
        }

        // Sharing scope
        string sharingScope = "internal";
        if (metadata.TryGetValue("sharing_scope", out var ssObj) && ssObj is string ss)
            sharingScope = ss.Trim().ToLowerInvariant();

        switch (sharingScope)
        {
            case "public":
                score += 60;
                factors.Add("Public sharing scope — data accessible externally");
                break;
            case "external":
                score += 40;
                factors.Add("External sharing scope — data shared outside organization");
                break;
            case "internal":
                score += 10;
                factors.Add("Internal sharing scope — data contained within organization");
                break;
        }

        // Security scan recency
        if (metadata.TryGetValue("last_security_scan", out var scanObj))
        {
            DateTime? lastScan = scanObj switch
            {
                DateTime dt => dt,
                string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) => parsed,
                _ => null
            };

            if (lastScan.HasValue && (DateTime.UtcNow - lastScan.Value).TotalDays > 90)
            {
                score += 20;
                factors.Add("No security scan in over 90 days — increased breach risk");
            }
        }
        else
        {
            score += 20;
            factors.Add("No security scan date recorded — increased breach risk");
        }

        // Apply cap; best case: encrypted + private + internal = 10+10+10 = 30
        // but with scan check could be 30 or 50. We just cap at 100.
        score = Math.Min(score, 100.0);

        return Task.FromResult((score, factors));
    }
}

#endregion

#region Strategy 8: CompositeLiabilityScoringStrategy

/// <summary>
/// Composite liability scorer that aggregates all 7 dimension strategies with
/// configurable weights to produce an overall <see cref="LiabilityScore"/>.
/// Implements <see cref="ILiabilityScorer"/> and extends <see cref="ConsciousnessStrategyBase"/>.
/// </summary>
/// <remarks>
/// Default weights: PIIPresence=0.20, PHIPresence=0.15, PCIPresence=0.15,
/// ClassificationLevel=0.10, RetentionObligation=0.15, RegulatoryExposure=0.15, BreachRisk=0.10.
/// Merges DetectedPIITypes and ApplicableRegulations from dimension strategies.
/// </remarks>
public sealed class CompositeLiabilityScoringStrategy : ConsciousnessStrategyBase, ILiabilityScorer
{
    public override string StrategyId => "liability-composite";
    public override string DisplayName => "Composite Liability Scorer";
    public override ConsciousnessCategory Category => ConsciousnessCategory.LiabilityScoring;
    public override ConsciousnessCapabilities Capabilities => new(
        SupportsAsync: true, SupportsBatch: true, SupportsRealTime: true);
    public override string SemanticDescription =>
        "Aggregates all 7 liability dimension strategies (PII, PHI, PCI, classification, retention, " +
        "regulatory exposure, breach risk) with configurable weights to produce a composite liability score.";
    public override string[] Tags => ["liability", "composite", "aggregator", "consciousness"];

    private static readonly IReadOnlyDictionary<LiabilityDimension, double> DefaultWeights =
        new Dictionary<LiabilityDimension, double>
        {
            [LiabilityDimension.PIIPresence] = 0.20,
            [LiabilityDimension.PHIPresence] = 0.15,
            [LiabilityDimension.PCIPresence] = 0.15,
            [LiabilityDimension.ClassificationLevel] = 0.10,
            [LiabilityDimension.RetentionObligation] = 0.15,
            [LiabilityDimension.RegulatoryExposure] = 0.15,
            [LiabilityDimension.BreachRisk] = 0.10
        };

    private readonly PIILiabilityStrategy _piiStrategy = new();
    private readonly PHILiabilityStrategy _phiStrategy = new();
    private readonly PCILiabilityStrategy _pciStrategy = new();
    private readonly ClassificationLiabilityStrategy _classificationStrategy = new();
    private readonly RetentionLiabilityStrategy _retentionStrategy = new();
    private readonly RegulatoryExposureLiabilityStrategy _regulatoryStrategy = new();
    private readonly BreachRiskLiabilityStrategy _breachRiskStrategy = new();

    private ConsciousnessScoringConfig _config = new();

    /// <summary>
    /// Sets the scoring configuration. If null, uses defaults.
    /// </summary>
    public void Configure(ConsciousnessScoringConfig? config)
    {
        _config = config ?? new ConsciousnessScoringConfig();
    }

    /// <inheritdoc />
    public async Task<LiabilityScore> ScoreLiabilityAsync(
        string objectId, byte[] data, IReadOnlyDictionary<string, object> metadata, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IncrementCounter("composite_liability_scans");

        var weights = _config.LiabilityWeights ?? DefaultWeights;
        var dimensionScores = new Dictionary<LiabilityDimension, double>();
        var allFactors = new List<string>();
        var detectedPiiTypes = new List<string>();
        var applicableRegulations = new List<string>();

        // Score each dimension
        var piiResult = await _piiStrategy.ScoreAsync(data, metadata, ct).ConfigureAwait(false);
        dimensionScores[LiabilityDimension.PIIPresence] = piiResult.Score;
        allFactors.AddRange(piiResult.Factors);
        detectedPiiTypes.AddRange(piiResult.DetectedTypes);

        var phiResult = await _phiStrategy.ScoreAsync(data, metadata, ct).ConfigureAwait(false);
        dimensionScores[LiabilityDimension.PHIPresence] = phiResult.Score;
        allFactors.AddRange(phiResult.Factors);

        var pciResult = await _pciStrategy.ScoreAsync(data, metadata, ct).ConfigureAwait(false);
        dimensionScores[LiabilityDimension.PCIPresence] = pciResult.Score;
        allFactors.AddRange(pciResult.Factors);

        var classificationResult = await _classificationStrategy.ScoreAsync(metadata, ct).ConfigureAwait(false);
        dimensionScores[LiabilityDimension.ClassificationLevel] = classificationResult.Score;
        allFactors.AddRange(classificationResult.Factors);

        var retentionResult = await _retentionStrategy.ScoreAsync(metadata, ct).ConfigureAwait(false);
        dimensionScores[LiabilityDimension.RetentionObligation] = retentionResult.Score;
        allFactors.AddRange(retentionResult.Factors);

        var regulatoryResult = await _regulatoryStrategy.ScoreAsync(metadata, ct).ConfigureAwait(false);
        dimensionScores[LiabilityDimension.RegulatoryExposure] = regulatoryResult.Score;
        allFactors.AddRange(regulatoryResult.Factors);
        applicableRegulations.AddRange(regulatoryResult.ApplicableRegulations);

        var breachResult = await _breachRiskStrategy.ScoreAsync(metadata, ct).ConfigureAwait(false);
        dimensionScores[LiabilityDimension.BreachRisk] = breachResult.Score;
        allFactors.AddRange(breachResult.Factors);

        // Compute weighted overall score
        double overallScore = 0;
        foreach (var dimension in dimensionScores)
        {
            if (weights.TryGetValue(dimension.Key, out double weight))
                overallScore += dimension.Value * weight;
        }

        overallScore = Math.Min(Math.Max(overallScore, 0), 100.0);

        return new LiabilityScore(
            OverallScore: overallScore,
            DimensionScores: dimensionScores,
            LiabilityFactors: allFactors.AsReadOnly(),
            DetectedPIITypes: detectedPiiTypes.Distinct().ToList().AsReadOnly(),
            ApplicableRegulations: applicableRegulations.Distinct().ToList().AsReadOnly(),
            ScoredAt: DateTime.UtcNow);
    }
}

#endregion
