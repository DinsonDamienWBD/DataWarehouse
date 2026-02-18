using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.UltimateDataPrivacy.Strategies.Anonymization;

public sealed class KAnonymityStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "k-anonymity";
    public override string DisplayName => "K-Anonymity";
    public override PrivacyCategory Category => PrivacyCategory.Anonymization;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "K-anonymity anonymization ensuring each record is indistinguishable from k-1 others";
    public override string[] Tags => ["anonymization", "k-anonymity", "privacy-protection"];

    public AnonymizationResult Anonymize(List<Dictionary<string, object>> records, string[] quasiIdentifiers, int k)
    {
        if (k < 2) throw new ArgumentException("k must be at least 2", nameof(k));

        // Group records by quasi-identifier combinations
        var groups = records.GroupBy(r => string.Join("|", quasiIdentifiers.Select(qi => r.GetValueOrDefault(qi, "NULL")?.ToString() ?? "NULL"))).ToList();

        var suppressedCount = 0;
        var anonymizedRecords = new List<Dictionary<string, object>>();

        foreach (var group in groups)
        {
            if (group.Count() < k)
            {
                // Suppress groups smaller than k
                suppressedCount += group.Count();
            }
            else
            {
                // Keep groups with at least k members
                anonymizedRecords.AddRange(group);
            }
        }

        return new AnonymizationResult
        {
            OriginalCount = records.Count,
            AnonymizedCount = anonymizedRecords.Count,
            SuppressedCount = suppressedCount,
            AnonymizedRecords = anonymizedRecords
        };
    }
}

public sealed record AnonymizationResult
{
    public int OriginalCount { get; init; }
    public int AnonymizedCount { get; init; }
    public int SuppressedCount { get; init; }
    public List<Dictionary<string, object>> AnonymizedRecords { get; init; } = new();
}

public sealed class LDiversityStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "l-diversity";
    public override string DisplayName => "L-Diversity";
    public override PrivacyCategory Category => PrivacyCategory.Anonymization;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "L-diversity anonymization ensuring sensitive attributes have diverse values";
    public override string[] Tags => ["anonymization", "l-diversity", "attribute-diversity"];

    public AnonymizationResult Anonymize(List<Dictionary<string, object>> records, string[] quasiIdentifiers, string sensitiveAttribute, int l)
    {
        if (l < 2) throw new ArgumentException("l must be at least 2", nameof(l));

        // Group by quasi-identifiers
        var groups = records.GroupBy(r => string.Join("|", quasiIdentifiers.Select(qi => r.GetValueOrDefault(qi, "NULL")?.ToString() ?? "NULL"))).ToList();

        var suppressedCount = 0;
        var anonymizedRecords = new List<Dictionary<string, object>>();

        foreach (var group in groups)
        {
            // Count distinct sensitive values in this equivalence class
            var distinctSensitiveValues = group.Select(r => r.GetValueOrDefault(sensitiveAttribute, "NULL")?.ToString() ?? "NULL").Distinct().Count();

            if (distinctSensitiveValues < l)
            {
                // Suppress groups with insufficient diversity
                suppressedCount += group.Count();
            }
            else
            {
                // Keep groups with at least l distinct sensitive values
                anonymizedRecords.AddRange(group);
            }
        }

        return new AnonymizationResult
        {
            OriginalCount = records.Count,
            AnonymizedCount = anonymizedRecords.Count,
            SuppressedCount = suppressedCount,
            AnonymizedRecords = anonymizedRecords
        };
    }
}

public sealed class TClosenessStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "t-closeness";
    public override string DisplayName => "T-Closeness";
    public override PrivacyCategory Category => PrivacyCategory.Anonymization;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "T-closeness anonymization ensuring attribute distribution is close to global distribution";
    public override string[] Tags => ["anonymization", "t-closeness", "distribution"];

    public AnonymizationResult Anonymize(List<Dictionary<string, object>> records, string[] quasiIdentifiers, string sensitiveAttribute, double t)
    {
        if (t <= 0 || t > 1) throw new ArgumentException("t must be between 0 and 1", nameof(t));

        // Calculate global distribution of sensitive attribute
        var globalDistribution = records
            .GroupBy(r => r.GetValueOrDefault(sensitiveAttribute, "NULL")?.ToString() ?? "NULL")
            .ToDictionary(g => g.Key, g => (double)g.Count() / records.Count);

        // Group by quasi-identifiers
        var groups = records.GroupBy(r => string.Join("|", quasiIdentifiers.Select(qi => r.GetValueOrDefault(qi, "NULL")?.ToString() ?? "NULL"))).ToList();

        var suppressedCount = 0;
        var anonymizedRecords = new List<Dictionary<string, object>>();

        foreach (var group in groups)
        {
            var groupList = group.ToList();

            // Calculate local distribution in this equivalence class
            var localDistribution = groupList
                .GroupBy(r => r.GetValueOrDefault(sensitiveAttribute, "NULL")?.ToString() ?? "NULL")
                .ToDictionary(g => g.Key, g => (double)g.Count() / groupList.Count);

            // Calculate Earth Mover's Distance (simplified as sum of absolute differences)
            var distance = globalDistribution.Keys.Union(localDistribution.Keys)
                .Sum(key =>
                {
                    var globalProb = globalDistribution.GetValueOrDefault(key, 0);
                    var localProb = localDistribution.GetValueOrDefault(key, 0);
                    return Math.Abs(globalProb - localProb);
                }) / 2.0; // Divide by 2 because we count differences twice

            if (distance > t)
            {
                // Suppress groups with distribution too different from global
                suppressedCount += groupList.Count;
            }
            else
            {
                // Keep groups with distribution within threshold
                anonymizedRecords.AddRange(groupList);
            }
        }

        return new AnonymizationResult
        {
            OriginalCount = records.Count,
            AnonymizedCount = anonymizedRecords.Count,
            SuppressedCount = suppressedCount,
            AnonymizedRecords = anonymizedRecords
        };
    }
}

public sealed class DataSuppressionStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "data-suppression";
    public override string DisplayName => "Data Suppression";
    public override PrivacyCategory Category => PrivacyCategory.Anonymization;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Suppress sensitive data by removing or redacting fields";
    public override string[] Tags => ["anonymization", "suppression", "redaction"];
}

public sealed class GeneralizationStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "generalization";
    public override string DisplayName => "Data Generalization";
    public override PrivacyCategory Category => PrivacyCategory.Anonymization;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Generalize data by replacing specific values with broader categories";
    public override string[] Tags => ["anonymization", "generalization", "abstraction"];
}

public sealed class DataSwappingStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "data-swapping";
    public override string DisplayName => "Data Swapping";
    public override PrivacyCategory Category => PrivacyCategory.Anonymization;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = true };
    public override string SemanticDescription => "Swap values between records to preserve statistics while anonymizing";
    public override string[] Tags => ["anonymization", "swapping", "permutation"];
}

public sealed class DataPerturbationStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "data-perturbation";
    public override string DisplayName => "Data Perturbation";
    public override PrivacyCategory Category => PrivacyCategory.Anonymization;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Add controlled noise to data values for anonymization";
    public override string[] Tags => ["anonymization", "perturbation", "noise"];
}

public sealed class TopBottomCodingStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "top-bottom-coding";
    public override string DisplayName => "Top/Bottom Coding";
    public override PrivacyCategory Category => PrivacyCategory.Anonymization;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Replace extreme values with threshold values to prevent identification";
    public override string[] Tags => ["anonymization", "coding", "outliers"];
}

public sealed class SyntheticDataGenerationStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "synthetic-data-generation";
    public override string DisplayName => "Synthetic Data Generation";
    public override PrivacyCategory Category => PrivacyCategory.Anonymization;
    public override DataPrivacyCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsReversible = false, SupportsFormatPreserving = false };
    public override string SemanticDescription => "Generate synthetic data with similar statistical properties";
    public override string[] Tags => ["anonymization", "synthetic", "generation"];
}
