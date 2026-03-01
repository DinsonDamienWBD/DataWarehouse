using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace DataWarehouse.Plugins.UltimateDataQuality.Strategies.DuplicateDetection;

#region Duplicate Detection Types

/// <summary>
/// Represents a group of duplicate records.
/// </summary>
public sealed class DuplicateGroup
{
    /// <summary>
    /// Group identifier.
    /// </summary>
    public string GroupId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Record IDs in this duplicate group.
    /// </summary>
    public List<string> RecordIds { get; init; } = new();

    /// <summary>
    /// The master record ID (canonical record).
    /// </summary>
    public string? MasterRecordId { get; set; }

    /// <summary>
    /// Similarity score between records (0-1).
    /// </summary>
    public double SimilarityScore { get; init; }

    /// <summary>
    /// Fields that matched for duplication.
    /// </summary>
    public List<string> MatchingFields { get; init; } = new();

    /// <summary>
    /// Detection method used.
    /// </summary>
    public required string DetectionMethod { get; init; }
}

/// <summary>
/// Result of duplicate detection.
/// </summary>
public sealed class DuplicateDetectionResult
{
    /// <summary>
    /// Total records analyzed.
    /// </summary>
    public int TotalRecords { get; init; }

    /// <summary>
    /// Number of duplicate groups found.
    /// </summary>
    public int DuplicateGroupCount => DuplicateGroups.Count;

    /// <summary>
    /// Total duplicate records found.
    /// </summary>
    public int TotalDuplicates { get; init; }

    /// <summary>
    /// Duplicate groups.
    /// </summary>
    public List<DuplicateGroup> DuplicateGroups { get; init; } = new();

    /// <summary>
    /// Unique record IDs (not duplicates).
    /// </summary>
    public List<string> UniqueRecordIds { get; init; } = new();

    /// <summary>
    /// Processing time in milliseconds.
    /// </summary>
    public double ProcessingTimeMs { get; init; }

    /// <summary>
    /// Duplicate percentage.
    /// </summary>
    public double DuplicatePercentage => TotalRecords > 0
        ? (double)TotalDuplicates / TotalRecords * 100
        : 0;
}

/// <summary>
/// Configuration for duplicate detection.
/// </summary>
public sealed class DuplicateDetectionConfig
{
    /// <summary>
    /// Fields to use for matching.
    /// </summary>
    public required string[] MatchFields { get; init; }

    /// <summary>
    /// Minimum similarity score to consider as duplicate (0-1).
    /// </summary>
    public double SimilarityThreshold { get; init; } = 0.9;

    /// <summary>
    /// Whether to use exact matching only.
    /// </summary>
    public bool ExactMatchOnly { get; init; }

    /// <summary>
    /// Whether to ignore case in comparisons.
    /// </summary>
    public bool IgnoreCase { get; init; } = true;

    /// <summary>
    /// Whether to trim whitespace before comparison.
    /// </summary>
    public bool TrimWhitespace { get; init; } = true;

    /// <summary>
    /// Fields to use for determining master record.
    /// </summary>
    public string[]? MasterSelectionFields { get; init; }

    /// <summary>
    /// Selection strategy for master record.
    /// </summary>
    public MasterSelectionStrategy MasterStrategy { get; init; } = MasterSelectionStrategy.MostComplete;
}

/// <summary>
/// Strategy for selecting master record from duplicates.
/// </summary>
public enum MasterSelectionStrategy
{
    /// <summary>Select the first record found.</summary>
    First,
    /// <summary>Select the most recently modified.</summary>
    MostRecent,
    /// <summary>Select the oldest record.</summary>
    Oldest,
    /// <summary>Select the record with most non-null fields.</summary>
    MostComplete,
    /// <summary>Select based on priority field values.</summary>
    Priority
}

/// <summary>
/// Result of merging duplicate records.
/// </summary>
public sealed class DuplicateMergeResult
{
    /// <summary>
    /// The merged master record.
    /// </summary>
    public required DataRecord MergedRecord { get; init; }

    /// <summary>
    /// Record IDs that were merged.
    /// </summary>
    public required List<string> MergedRecordIds { get; init; }

    /// <summary>
    /// Fields that were consolidated.
    /// </summary>
    public List<string> ConsolidatedFields { get; init; } = new();
}

#endregion

#region Exact Match Strategy

/// <summary>
/// Exact match duplicate detection strategy.
/// Finds records with identical values in specified fields.
/// </summary>
public sealed class ExactMatchDuplicateStrategy : DataQualityStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "exact-match-duplicate";

    /// <inheritdoc/>
    public override string DisplayName => "Exact Match Duplicate Detection";

    /// <inheritdoc/>
    public override DataQualityCategory Category => DataQualityCategory.DuplicateDetection;

    /// <inheritdoc/>
    public override DataQualityCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = false,
        SupportsDistributed = true,
        SupportsIncremental = false,
        MaxThroughput = 100000,
        TypicalLatencyMs = 0.5
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Detects exact duplicates by comparing hash values of specified fields. " +
        "Fast and efficient for large datasets with exact matching requirements.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "duplicate", "exact-match", "hash", "deduplication"
    };

    /// <summary>
    /// Detects duplicates using exact matching.
    /// </summary>
    public Task<DuplicateDetectionResult> DetectAsync(
        IEnumerable<DataRecord> records,
        DuplicateDetectionConfig config,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        var startTime = DateTime.UtcNow;

        var recordList = records.ToList();
        var hashGroups = new Dictionary<string, List<string>>();

        foreach (var record in recordList)
        {
            ct.ThrowIfCancellationRequested();

            var hash = ComputeHash(record, config);
            if (!hashGroups.TryGetValue(hash, out var group))
            {
                group = new List<string>();
                hashGroups[hash] = group;
            }
            group.Add(record.RecordId);
        }

        var duplicateGroups = new List<DuplicateGroup>();
        var uniqueRecords = new List<string>();
        var totalDuplicates = 0;

        foreach (var (hash, recordIds) in hashGroups)
        {
            if (recordIds.Count > 1)
            {
                var group = new DuplicateGroup
                {
                    RecordIds = recordIds,
                    SimilarityScore = 1.0,
                    MatchingFields = config.MatchFields.ToList(),
                    DetectionMethod = "ExactMatch"
                };

                // Select master record
                group.MasterRecordId = SelectMaster(
                    recordIds,
                    recordList.Where(r => recordIds.Contains(r.RecordId)),
                    config);

                duplicateGroups.Add(group);
                totalDuplicates += recordIds.Count - 1; // All but master are duplicates
                RecordDuplicateFound();
            }
            else
            {
                uniqueRecords.Add(recordIds[0]);
            }
        }

        var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;

        return Task.FromResult(new DuplicateDetectionResult
        {
            TotalRecords = recordList.Count,
            TotalDuplicates = totalDuplicates,
            DuplicateGroups = duplicateGroups,
            UniqueRecordIds = uniqueRecords,
            ProcessingTimeMs = elapsed
        });
    }

    private string ComputeHash(DataRecord record, DuplicateDetectionConfig config)
    {
        var sb = new StringBuilder();

        foreach (var field in config.MatchFields)
        {
            var value = record.GetFieldAsString(field) ?? "";

            if (config.TrimWhitespace)
                value = value.Trim();

            if (config.IgnoreCase)
                value = value.ToLowerInvariant();

            sb.Append(value);
            sb.Append('\0'); // Separator
        }

        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }

    private string SelectMaster(
        List<string> recordIds,
        IEnumerable<DataRecord> records,
        DuplicateDetectionConfig config)
    {
        var recordList = records.ToList();

        return config.MasterStrategy switch
        {
            MasterSelectionStrategy.First => recordIds[0],
            MasterSelectionStrategy.MostRecent => recordList
                .OrderByDescending(r => r.ModifiedAt ?? r.CreatedAt ?? DateTime.MinValue)
                .First().RecordId,
            MasterSelectionStrategy.Oldest => recordList
                .OrderBy(r => r.CreatedAt ?? DateTime.MaxValue)
                .First().RecordId,
            MasterSelectionStrategy.MostComplete => recordList
                .OrderByDescending(r => r.Fields.Count(f => f.Value != null))
                .First().RecordId,
            _ => recordIds[0]
        };
    }
}

#endregion

#region Fuzzy Match Strategy

/// <summary>
/// Fuzzy match duplicate detection strategy.
/// Finds records with similar (not exact) values in specified fields.
/// </summary>
public sealed class FuzzyMatchDuplicateStrategy : DataQualityStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "fuzzy-match-duplicate";

    /// <inheritdoc/>
    public override string DisplayName => "Fuzzy Match Duplicate Detection";

    /// <inheritdoc/>
    public override DataQualityCategory Category => DataQualityCategory.DuplicateDetection;

    /// <inheritdoc/>
    public override DataQualityCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = false,
        SupportsDistributed = true,
        SupportsIncremental = false,
        MaxThroughput = 5000,
        TypicalLatencyMs = 5.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Detects near-duplicates using fuzzy string matching algorithms. " +
        "Handles typos, variations, and minor differences in data.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "duplicate", "fuzzy-match", "similarity", "levenshtein", "deduplication"
    };

    /// <summary>
    /// Detects duplicates using fuzzy matching.
    /// </summary>
    public Task<DuplicateDetectionResult> DetectAsync(
        IEnumerable<DataRecord> records,
        DuplicateDetectionConfig config,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        var startTime = DateTime.UtcNow;

        var recordList = records.ToList();
        var processed = new HashSet<string>();
        var duplicateGroups = new List<DuplicateGroup>();
        var uniqueRecords = new List<string>();
        var totalDuplicates = 0;

        // Use blocking to reduce comparisons
        var blocks = CreateBlocks(recordList, config);

        foreach (var block in blocks.Values)
        {
            ct.ThrowIfCancellationRequested();

            for (int i = 0; i < block.Count; i++)
            {
                var record1 = block[i];
                if (processed.Contains(record1.RecordId)) continue;

                var group = new List<string> { record1.RecordId };

                for (int j = i + 1; j < block.Count; j++)
                {
                    var record2 = block[j];
                    if (processed.Contains(record2.RecordId)) continue;

                    var similarity = CalculateSimilarity(record1, record2, config);
                    if (similarity >= config.SimilarityThreshold)
                    {
                        group.Add(record2.RecordId);
                        processed.Add(record2.RecordId);
                    }
                }

                if (group.Count > 1)
                {
                    var dupGroup = new DuplicateGroup
                    {
                        RecordIds = group,
                        SimilarityScore = CalculateGroupSimilarity(
                            recordList.Where(r => group.Contains(r.RecordId)), config),
                        MatchingFields = config.MatchFields.ToList(),
                        DetectionMethod = "FuzzyMatch",
                        MasterRecordId = group[0]
                    };
                    duplicateGroups.Add(dupGroup);
                    totalDuplicates += group.Count - 1;
                    RecordDuplicateFound();
                }
                else
                {
                    uniqueRecords.Add(record1.RecordId);
                }

                processed.Add(record1.RecordId);
            }
        }

        var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;

        return Task.FromResult(new DuplicateDetectionResult
        {
            TotalRecords = recordList.Count,
            TotalDuplicates = totalDuplicates,
            DuplicateGroups = duplicateGroups,
            UniqueRecordIds = uniqueRecords,
            ProcessingTimeMs = elapsed
        });
    }

    private Dictionary<string, List<DataRecord>> CreateBlocks(
        List<DataRecord> records,
        DuplicateDetectionConfig config)
    {
        var blocks = new Dictionary<string, List<DataRecord>>();

        // Use first few characters of first match field as blocking key
        var blockField = config.MatchFields[0];

        foreach (var record in records)
        {
            var value = record.GetFieldAsString(blockField) ?? "";
            if (config.IgnoreCase) value = value.ToLowerInvariant();

            // Use first 3 chars as block key
            var blockKey = value.Length >= 3 ? value.Substring(0, 3) : value;

            if (!blocks.TryGetValue(blockKey, out var block))
            {
                block = new List<DataRecord>();
                blocks[blockKey] = block;
            }
            block.Add(record);
        }

        return blocks;
    }

    private double CalculateSimilarity(
        DataRecord record1,
        DataRecord record2,
        DuplicateDetectionConfig config)
    {
        double totalSimilarity = 0;

        foreach (var field in config.MatchFields)
        {
            var value1 = record1.GetFieldAsString(field) ?? "";
            var value2 = record2.GetFieldAsString(field) ?? "";

            if (config.TrimWhitespace)
            {
                value1 = value1.Trim();
                value2 = value2.Trim();
            }

            if (config.IgnoreCase)
            {
                value1 = value1.ToLowerInvariant();
                value2 = value2.ToLowerInvariant();
            }

            totalSimilarity += JaroWinklerSimilarity(value1, value2);
        }

        return totalSimilarity / config.MatchFields.Length;
    }

    private double CalculateGroupSimilarity(
        IEnumerable<DataRecord> records,
        DuplicateDetectionConfig config)
    {
        var recordList = records.ToList();
        if (recordList.Count < 2) return 1.0;

        // Cap pairwise comparisons to avoid O(n²) on large groups.
        const int MaxComparisons = 500;
        double totalSimilarity = 0;
        int comparisons = 0;

        for (int i = 0; i < recordList.Count - 1 && comparisons < MaxComparisons; i++)
        {
            for (int j = i + 1; j < recordList.Count && comparisons < MaxComparisons; j++)
            {
                totalSimilarity += CalculateSimilarity(recordList[i], recordList[j], config);
                comparisons++;
            }
        }

        return comparisons > 0 ? totalSimilarity / comparisons : 1.0;
    }

    /// <summary>
    /// Calculates Jaro-Winkler similarity between two strings.
    /// </summary>
    private static double JaroWinklerSimilarity(string s1, string s2)
    {
        if (s1 == s2) return 1.0;
        if (s1.Length == 0 || s2.Length == 0) return 0.0;

        var matchDistance = Math.Max(s1.Length, s2.Length) / 2 - 1;
        if (matchDistance < 0) matchDistance = 0;

        var s1Matches = new bool[s1.Length];
        var s2Matches = new bool[s2.Length];

        int matches = 0;
        int transpositions = 0;

        // Find matches
        for (int i = 0; i < s1.Length; i++)
        {
            var start = Math.Max(0, i - matchDistance);
            var end = Math.Min(i + matchDistance + 1, s2.Length);

            for (int j = start; j < end; j++)
            {
                if (s2Matches[j] || s1[i] != s2[j]) continue;
                s1Matches[i] = true;
                s2Matches[j] = true;
                matches++;
                break;
            }
        }

        if (matches == 0) return 0.0;

        // Count transpositions
        int k = 0;
        for (int i = 0; i < s1.Length; i++)
        {
            if (!s1Matches[i]) continue;
            while (!s2Matches[k]) k++;
            if (s1[i] != s2[k]) transpositions++;
            k++;
        }

        double jaro = ((double)matches / s1.Length +
                       (double)matches / s2.Length +
                       (double)(matches - transpositions / 2.0) / matches) / 3.0;

        // Winkler modification
        int prefix = 0;
        for (int i = 0; i < Math.Min(Math.Min(s1.Length, s2.Length), 4); i++)
        {
            if (s1[i] == s2[i])
                prefix++;
            else
                break;
        }

        return jaro + prefix * 0.1 * (1 - jaro);
    }
}

#endregion

#region Phonetic Match Strategy

/// <summary>
/// Phonetic matching duplicate detection using Soundex and Metaphone.
/// </summary>
public sealed class PhoneticMatchDuplicateStrategy : DataQualityStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "phonetic-match-duplicate";

    /// <inheritdoc/>
    public override string DisplayName => "Phonetic Match Duplicate Detection";

    /// <inheritdoc/>
    public override DataQualityCategory Category => DataQualityCategory.DuplicateDetection;

    /// <inheritdoc/>
    public override DataQualityCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = false,
        SupportsDistributed = true,
        SupportsIncremental = false,
        MaxThroughput = 50000,
        TypicalLatencyMs = 1.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Detects duplicates using phonetic algorithms (Soundex, Metaphone). " +
        "Ideal for finding name variations and spelling differences.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "duplicate", "phonetic", "soundex", "metaphone", "names"
    };

    /// <summary>
    /// Detects duplicates using phonetic matching.
    /// </summary>
    public Task<DuplicateDetectionResult> DetectAsync(
        IEnumerable<DataRecord> records,
        DuplicateDetectionConfig config,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        var startTime = DateTime.UtcNow;

        var recordList = records.ToList();
        var phoneticGroups = new Dictionary<string, List<string>>();

        foreach (var record in recordList)
        {
            ct.ThrowIfCancellationRequested();

            var phoneticKey = ComputePhoneticKey(record, config);
            if (!phoneticGroups.TryGetValue(phoneticKey, out var group))
            {
                group = new List<string>();
                phoneticGroups[phoneticKey] = group;
            }
            group.Add(record.RecordId);
        }

        var duplicateGroups = new List<DuplicateGroup>();
        var uniqueRecords = new List<string>();
        var totalDuplicates = 0;

        foreach (var (key, recordIds) in phoneticGroups)
        {
            if (recordIds.Count > 1)
            {
                var group = new DuplicateGroup
                {
                    RecordIds = recordIds,
                    SimilarityScore = 0.9, // Phonetic match implies high similarity
                    MatchingFields = config.MatchFields.ToList(),
                    DetectionMethod = "PhoneticMatch",
                    MasterRecordId = recordIds[0]
                };
                duplicateGroups.Add(group);
                totalDuplicates += recordIds.Count - 1;
                RecordDuplicateFound();
            }
            else
            {
                uniqueRecords.Add(recordIds[0]);
            }
        }

        var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;

        return Task.FromResult(new DuplicateDetectionResult
        {
            TotalRecords = recordList.Count,
            TotalDuplicates = totalDuplicates,
            DuplicateGroups = duplicateGroups,
            UniqueRecordIds = uniqueRecords,
            ProcessingTimeMs = elapsed
        });
    }

    private string ComputePhoneticKey(DataRecord record, DuplicateDetectionConfig config)
    {
        var sb = new StringBuilder();

        foreach (var field in config.MatchFields)
        {
            var value = record.GetFieldAsString(field) ?? "";
            sb.Append(ComputeSoundex(value));
            sb.Append('|');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Computes Soundex code for a string.
    /// </summary>
    private static string ComputeSoundex(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "0000";

        var sb = new StringBuilder();
        var upper = input.ToUpperInvariant();

        // First letter
        if (char.IsLetter(upper[0]))
            sb.Append(upper[0]);
        else
            sb.Append('0');

        var lastCode = GetSoundexCode(upper[0]);

        for (int i = 1; i < upper.Length && sb.Length < 4; i++)
        {
            var code = GetSoundexCode(upper[i]);
            if (code != '0' && code != lastCode)
            {
                sb.Append(code);
            }
            lastCode = code;
        }

        while (sb.Length < 4)
            sb.Append('0');

        return sb.ToString();
    }

    private static char GetSoundexCode(char c)
    {
        return c switch
        {
            'B' or 'F' or 'P' or 'V' => '1',
            'C' or 'G' or 'J' or 'K' or 'Q' or 'S' or 'X' or 'Z' => '2',
            'D' or 'T' => '3',
            'L' => '4',
            'M' or 'N' => '5',
            'R' => '6',
            _ => '0'
        };
    }
}

#endregion

#region Duplicate Merger

/// <summary>
/// Merges duplicate records into a single master record.
/// </summary>
public sealed class DuplicateMerger
{
    /// <summary>
    /// Merges a group of duplicates into a single record.
    /// </summary>
    public DuplicateMergeResult Merge(
        IEnumerable<DataRecord> duplicateRecords,
        MergeStrategy strategy = MergeStrategy.MostComplete)
    {
        var records = duplicateRecords.ToList();
        if (records.Count == 0)
            throw new ArgumentException("No records to merge");

        if (records.Count == 1)
        {
            return new DuplicateMergeResult
            {
                MergedRecord = records[0],
                MergedRecordIds = new List<string> { records[0].RecordId }
            };
        }

        var mergedFields = new Dictionary<string, object?>();
        var consolidatedFields = new List<string>();
        var allFields = records.SelectMany(r => r.Fields.Keys).Distinct().ToList();

        foreach (var field in allFields)
        {
            var values = records
                .Where(r => r.Fields.ContainsKey(field) && r.Fields[field] != null)
                .Select(r => r.Fields[field])
                .ToList();

            if (values.Count == 0)
            {
                mergedFields[field] = null;
            }
            else if (values.Distinct().Count() == 1)
            {
                mergedFields[field] = values.First();
            }
            else
            {
                // Conflict - use strategy to resolve
                mergedFields[field] = strategy switch
                {
                    MergeStrategy.First => values.First(),
                    MergeStrategy.Last => values.Last(),
                    MergeStrategy.MostComplete => values.OrderByDescending(v => v?.ToString()?.Length ?? 0).First(),
                    MergeStrategy.Longest => values.OrderByDescending(v => v?.ToString()?.Length ?? 0).First(),
                    MergeStrategy.Shortest => values.OrderBy(v => v?.ToString()?.Length ?? int.MaxValue).First(),
                    _ => values.First()
                };
                consolidatedFields.Add(field);
            }
        }

        var masterId = records.First().RecordId;
        var mergedRecord = new DataRecord
        {
            RecordId = masterId,
            SourceId = records.First().SourceId,
            Fields = mergedFields,
            Metadata = new Dictionary<string, object>
            {
                ["mergedFrom"] = records.Select(r => r.RecordId).ToList(),
                ["mergedAt"] = DateTime.UtcNow
            },
            CreatedAt = records.Min(r => r.CreatedAt),
            ModifiedAt = DateTime.UtcNow
        };

        return new DuplicateMergeResult
        {
            MergedRecord = mergedRecord,
            MergedRecordIds = records.Select(r => r.RecordId).ToList(),
            ConsolidatedFields = consolidatedFields
        };
    }
}

/// <summary>
/// Strategy for merging conflicting field values.
/// </summary>
public enum MergeStrategy
{
    /// <summary>Use the first non-null value.</summary>
    First,
    /// <summary>Use the last non-null value.</summary>
    Last,
    /// <summary>Use value from most complete record.</summary>
    MostComplete,
    /// <summary>Use the longest value.</summary>
    Longest,
    /// <summary>Use the shortest value.</summary>
    Shortest
}

#endregion
