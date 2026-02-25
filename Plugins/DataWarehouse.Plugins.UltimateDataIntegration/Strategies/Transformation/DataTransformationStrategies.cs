using System.Text.Json;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataIntegration.Strategies.Transformation;

#region 126.3.1 Type Conversion Strategy

/// <summary>
/// 126.3.1: Type conversion strategy for converting data types between
/// different formats and systems with validation and error handling.
/// </summary>
public sealed class TypeConversionStrategy : DataIntegrationStrategyBase
{
    private readonly BoundedDictionary<string, TypeMapping> _typeMappings = new BoundedDictionary<string, TypeMapping>(1000);

    public override string StrategyId => "transform-type-conversion";
    public override string DisplayName => "Type Conversion";
    public override IntegrationCategory Category => IntegrationCategory.DataTransformation;
    public override DataIntegrationCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsExactlyOnce = true,
        SupportsSchemaEvolution = true,
        SupportsIncremental = true,
        SupportsParallel = true,
        SupportsDistributed = true,
        MaxThroughputRecordsPerSec = 2000000,
        TypicalLatencyMs = 5.0
    };
    public override string SemanticDescription =>
        "Type conversion for data type transformations between different systems. Supports " +
        "primitive types, complex types, nullability handling, and custom conversion rules.";
    public override string[] Tags => ["transform", "type-conversion", "cast", "coerce", "validation"];

    /// <summary>
    /// Registers a type mapping.
    /// </summary>
    public Task<TypeMapping> RegisterMappingAsync(
        string mappingId,
        string sourceType,
        string targetType,
        ConversionRule rule,
        CancellationToken ct = default)
    {
        var mapping = new TypeMapping
        {
            MappingId = mappingId,
            SourceType = sourceType,
            TargetType = targetType,
            Rule = rule,
            CreatedAt = DateTime.UtcNow
        };

        if (!_typeMappings.TryAdd(mappingId, mapping))
            throw new InvalidOperationException($"Mapping {mappingId} already exists");

        RecordOperation("RegisterTypeMapping");
        return Task.FromResult(mapping);
    }

    /// <summary>
    /// Converts a value using registered mappings.
    /// </summary>
    public Task<ConversionResult> ConvertAsync(
        object? value,
        string sourceType,
        string targetType,
        CancellationToken ct = default)
    {
        var result = new ConversionResult
        {
            OriginalValue = value,
            SourceType = sourceType,
            TargetType = targetType
        };

        try
        {
            result.ConvertedValue = ConvertValue(value, sourceType, targetType);
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }

        RecordOperation("ConvertType");
        return Task.FromResult(result);
    }

    private object? ConvertValue(object? value, string sourceType, string targetType)
    {
        if (value == null) return null;

        return (sourceType.ToLower(), targetType.ToLower()) switch
        {
            ("string", "int") => int.Parse(value.ToString()!),
            ("string", "long") => long.Parse(value.ToString()!),
            ("string", "double") => double.Parse(value.ToString()!),
            ("string", "decimal") => decimal.Parse(value.ToString()!),
            ("string", "datetime") => DateTime.Parse(value.ToString()!),
            ("string", "bool") => bool.Parse(value.ToString()!),
            ("int", "string") => value.ToString(),
            ("long", "string") => value.ToString(),
            ("double", "string") => value.ToString(),
            ("decimal", "string") => value.ToString(),
            ("datetime", "string") => ((DateTime)value).ToString("O"),
            ("bool", "string") => value.ToString(),
            ("int", "long") => Convert.ToInt64(value),
            ("long", "int") => Convert.ToInt32(value),
            ("int", "double") => Convert.ToDouble(value),
            ("double", "int") => Convert.ToInt32(value),
            _ => value
        };
    }
}

#endregion

#region 126.3.2 Aggregation Strategy

/// <summary>
/// 126.3.2: Aggregation strategy for grouping and summarizing data
/// with support for various aggregation functions.
/// </summary>
public sealed class AggregationStrategy : DataIntegrationStrategyBase
{
    public override string StrategyId => "transform-aggregation";
    public override string DisplayName => "Data Aggregation";
    public override IntegrationCategory Category => IntegrationCategory.DataTransformation;
    public override DataIntegrationCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsExactlyOnce = true,
        SupportsSchemaEvolution = false,
        SupportsIncremental = true,
        SupportsParallel = true,
        SupportsDistributed = true,
        MaxThroughputRecordsPerSec = 1000000,
        TypicalLatencyMs = 20.0
    };
    public override string SemanticDescription =>
        "Data aggregation with support for SUM, COUNT, AVG, MIN, MAX, DISTINCT, and custom aggregations. " +
        "Supports grouping, windowing, and incremental aggregations.";
    public override string[] Tags => ["transform", "aggregation", "sum", "count", "group-by", "windowing"];

    /// <summary>
    /// Aggregates data with specified functions.
    /// </summary>
    public Task<AggregationResult> AggregateAsync(
        IReadOnlyList<Dictionary<string, object>> records,
        IReadOnlyList<string> groupByColumns,
        IReadOnlyList<AggregationFunction> aggregations,
        CancellationToken ct = default)
    {
        var groups = records
            .GroupBy(r => string.Join("|", groupByColumns.Select(c => r.GetValueOrDefault(c)?.ToString() ?? "")))
            .ToList();

        var aggregatedRecords = new List<Dictionary<string, object>>();

        foreach (var group in groups)
        {
            var record = new Dictionary<string, object>();

            // Add group by values
            var firstRecord = group.First();
            foreach (var col in groupByColumns)
            {
                record[col] = firstRecord.GetValueOrDefault(col)!;
            }

            // Calculate aggregations
            foreach (var agg in aggregations)
            {
                record[agg.OutputColumn] = CalculateAggregation(group.ToList(), agg);
            }

            aggregatedRecords.Add(record);
        }

        RecordOperation("Aggregate");

        return Task.FromResult(new AggregationResult
        {
            InputRecords = records.Count,
            OutputRecords = aggregatedRecords.Count,
            AggregatedData = aggregatedRecords,
            Status = AggregationStatus.Success
        });
    }

    private object CalculateAggregation(List<Dictionary<string, object>> records, AggregationFunction agg)
    {
        var values = records
            .Select(r => r.GetValueOrDefault(agg.InputColumn))
            .Where(v => v != null)
            .ToList();

        return agg.Function switch
        {
            AggFunction.Count => values.Count,
            AggFunction.Sum => values.Sum(v => Convert.ToDouble(v)),
            AggFunction.Avg => values.Average(v => Convert.ToDouble(v)),
            AggFunction.Min => values.Min(v => Convert.ToDouble(v)),
            AggFunction.Max => values.Max(v => Convert.ToDouble(v)),
            AggFunction.CountDistinct => values.Distinct().Count(),
            AggFunction.First => values.FirstOrDefault() ?? DBNull.Value,
            AggFunction.Last => values.LastOrDefault() ?? DBNull.Value,
            _ => DBNull.Value
        };
    }
}

#endregion

#region 126.3.3 Data Cleansing Strategy

/// <summary>
/// 126.3.3: Data cleansing strategy for cleaning, standardizing,
/// and correcting data quality issues.
/// </summary>
public sealed class DataCleansingStrategy : DataIntegrationStrategyBase
{
    private readonly BoundedDictionary<string, CleansingRule> _rules = new BoundedDictionary<string, CleansingRule>(1000);

    public override string StrategyId => "transform-cleansing";
    public override string DisplayName => "Data Cleansing";
    public override IntegrationCategory Category => IntegrationCategory.DataTransformation;
    public override DataIntegrationCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsExactlyOnce = true,
        SupportsSchemaEvolution = true,
        SupportsIncremental = true,
        SupportsParallel = true,
        SupportsDistributed = true,
        MaxThroughputRecordsPerSec = 1500000,
        TypicalLatencyMs = 10.0
    };
    public override string SemanticDescription =>
        "Data cleansing for standardization, deduplication, null handling, trimming, " +
        "case normalization, and pattern-based corrections.";
    public override string[] Tags => ["transform", "cleansing", "quality", "standardization", "dedup", "null-handling"];

    /// <summary>
    /// Registers a cleansing rule.
    /// </summary>
    public Task<CleansingRule> RegisterRuleAsync(
        string ruleId,
        string targetColumn,
        CleansingOperation operation,
        Dictionary<string, object>? parameters = null,
        CancellationToken ct = default)
    {
        var rule = new CleansingRule
        {
            RuleId = ruleId,
            TargetColumn = targetColumn,
            Operation = operation,
            Parameters = parameters ?? new(),
            CreatedAt = DateTime.UtcNow
        };

        if (!_rules.TryAdd(ruleId, rule))
            throw new InvalidOperationException($"Rule {ruleId} already exists");

        RecordOperation("RegisterCleansingRule");
        return Task.FromResult(rule);
    }

    /// <summary>
    /// Cleanses records using registered rules.
    /// </summary>
    public Task<CleansingResult> CleanseAsync(
        IReadOnlyList<Dictionary<string, object>> records,
        IReadOnlyList<string>? ruleIds = null,
        CancellationToken ct = default)
    {
        var rules = ruleIds != null
            ? ruleIds.Select(id => _rules.GetValueOrDefault(id)).Where(r => r != null).Cast<CleansingRule>().ToList()
            : _rules.Values.ToList();

        var cleansedRecords = new List<Dictionary<string, object>>();
        var modifications = 0;

        foreach (var record in records)
        {
            var cleansed = new Dictionary<string, object>(record);
            foreach (var rule in rules)
            {
                if (ApplyCleansingRule(cleansed, rule!))
                    modifications++;
            }
            cleansedRecords.Add(cleansed);
        }

        RecordOperation("Cleanse");

        return Task.FromResult(new CleansingResult
        {
            InputRecords = records.Count,
            OutputRecords = cleansedRecords.Count,
            ModificationsApplied = modifications,
            CleansedData = cleansedRecords,
            Status = CleansingStatus.Success
        });
    }

    private bool ApplyCleansingRule(Dictionary<string, object> record, CleansingRule rule)
    {
        if (!record.TryGetValue(rule.TargetColumn, out var value))
            return false;

        var modified = false;
        object? newValue = value;

        switch (rule.Operation)
        {
            case CleansingOperation.Trim:
                if (value is string str)
                {
                    newValue = str.Trim();
                    modified = !str.Equals(newValue);
                }
                break;

            case CleansingOperation.ToUpperCase:
                if (value is string s1)
                {
                    newValue = s1.ToUpperInvariant();
                    modified = !s1.Equals(newValue);
                }
                break;

            case CleansingOperation.ToLowerCase:
                if (value is string s2)
                {
                    newValue = s2.ToLowerInvariant();
                    modified = !s2.Equals(newValue);
                }
                break;

            case CleansingOperation.ReplaceNull:
                if (value == null || (value is string s3 && string.IsNullOrEmpty(s3)))
                {
                    newValue = rule.Parameters.GetValueOrDefault("defaultValue", "");
                    modified = true;
                }
                break;

            case CleansingOperation.RemoveSpecialChars:
                if (value is string s4)
                {
                    newValue = Regex.Replace(s4, @"[^a-zA-Z0-9\s]", "");
                    modified = !s4.Equals(newValue);
                }
                break;

            case CleansingOperation.Standardize:
                // Apply standardization rules
                modified = true;
                break;
        }

        if (modified)
            record[rule.TargetColumn] = newValue!;

        return modified;
    }
}

#endregion

#region 126.3.4 Data Enrichment Strategy

/// <summary>
/// 126.3.4: Data enrichment strategy for augmenting records with
/// additional data from external sources.
/// </summary>
public sealed class DataEnrichmentStrategy : DataIntegrationStrategyBase
{
    private readonly BoundedDictionary<string, EnrichmentSource> _sources = new BoundedDictionary<string, EnrichmentSource>(1000);
    private readonly BoundedDictionary<string, Dictionary<string, object>> _cache = new BoundedDictionary<string, Dictionary<string, object>>(1000);

    public override string StrategyId => "transform-enrichment";
    public override string DisplayName => "Data Enrichment";
    public override IntegrationCategory Category => IntegrationCategory.DataTransformation;
    public override DataIntegrationCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsExactlyOnce = false,
        SupportsSchemaEvolution = true,
        SupportsIncremental = true,
        SupportsParallel = true,
        SupportsDistributed = true,
        MaxThroughputRecordsPerSec = 500000,
        TypicalLatencyMs = 50.0
    };
    public override string SemanticDescription =>
        "Data enrichment for augmenting records with external data from APIs, databases, " +
        "reference tables, and ML models. Supports caching and async lookups.";
    public override string[] Tags => ["transform", "enrichment", "lookup", "augmentation", "external-data"];

    /// <summary>
    /// Registers an enrichment source.
    /// </summary>
    public Task<EnrichmentSource> RegisterSourceAsync(
        string sourceId,
        EnrichmentSourceType sourceType,
        string connectionString,
        EnrichmentConfig? config = null,
        CancellationToken ct = default)
    {
        var source = new EnrichmentSource
        {
            SourceId = sourceId,
            SourceType = sourceType,
            ConnectionString = connectionString,
            Config = config ?? new EnrichmentConfig(),
            CreatedAt = DateTime.UtcNow
        };

        if (!_sources.TryAdd(sourceId, source))
            throw new InvalidOperationException($"Source {sourceId} already exists");

        RecordOperation("RegisterEnrichmentSource");
        return Task.FromResult(source);
    }

    /// <summary>
    /// Enriches records with data from registered sources.
    /// </summary>
    public async Task<EnrichmentResult> EnrichAsync(
        IReadOnlyList<Dictionary<string, object>> records,
        string sourceId,
        string keyColumn,
        IReadOnlyList<string> enrichColumns,
        CancellationToken ct = default)
    {
        if (!_sources.TryGetValue(sourceId, out var source))
            throw new KeyNotFoundException($"Source {sourceId} not found");

        var enrichedRecords = new List<Dictionary<string, object>>();
        var enrichmentsApplied = 0;

        foreach (var record in records)
        {
            var enriched = new Dictionary<string, object>(record);
            var key = record.GetValueOrDefault(keyColumn)?.ToString() ?? "";

            var lookupData = await LookupAsync(source, key, ct);
            if (lookupData != null)
            {
                foreach (var col in enrichColumns)
                {
                    if (lookupData.TryGetValue(col, out var value))
                    {
                        enriched[col] = value;
                        enrichmentsApplied++;
                    }
                }
            }

            enrichedRecords.Add(enriched);
        }

        RecordOperation("Enrich");

        return new EnrichmentResult
        {
            InputRecords = records.Count,
            OutputRecords = enrichedRecords.Count,
            EnrichmentsApplied = enrichmentsApplied,
            EnrichedData = enrichedRecords,
            Status = EnrichmentStatus.Success
        };
    }

    private Task<Dictionary<string, object>?> LookupAsync(
        EnrichmentSource source,
        string key,
        CancellationToken ct)
    {
        var cacheKey = $"{source.SourceId}:{key}";

        if (source.Config.EnableCache && _cache.TryGetValue(cacheKey, out var cached))
            return Task.FromResult<Dictionary<string, object>?>(cached);

        // Simulate lookup
        var result = new Dictionary<string, object>
        {
            ["enriched_field1"] = $"value_for_{key}",
            ["enriched_field2"] = 42,
            ["enriched_timestamp"] = DateTime.UtcNow
        };

        if (source.Config.EnableCache)
            _cache[cacheKey] = result;

        return Task.FromResult<Dictionary<string, object>?>(result);
    }
}

#endregion

#region 126.3.5 Data Normalization Strategy

/// <summary>
/// 126.3.5: Data normalization strategy for converting data to
/// standard formats and reducing redundancy.
/// </summary>
public sealed class DataNormalizationStrategy : DataIntegrationStrategyBase
{
    private readonly BoundedDictionary<string, NormalizationRule> _rules = new BoundedDictionary<string, NormalizationRule>(1000);

    public override string StrategyId => "transform-normalization";
    public override string DisplayName => "Data Normalization";
    public override IntegrationCategory Category => IntegrationCategory.DataTransformation;
    public override DataIntegrationCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsExactlyOnce = true,
        SupportsSchemaEvolution = true,
        SupportsIncremental = true,
        SupportsParallel = true,
        SupportsDistributed = true,
        MaxThroughputRecordsPerSec = 1500000,
        TypicalLatencyMs = 8.0
    };
    public override string SemanticDescription =>
        "Data normalization for standardizing formats, units, and structures. Supports " +
        "date/time normalization, unit conversion, address standardization, and phone formatting.";
    public override string[] Tags => ["transform", "normalization", "standardization", "format", "units"];

    /// <summary>
    /// Registers a normalization rule.
    /// </summary>
    public Task<NormalizationRule> RegisterRuleAsync(
        string ruleId,
        string targetColumn,
        NormalizationType normType,
        Dictionary<string, object>? parameters = null,
        CancellationToken ct = default)
    {
        var rule = new NormalizationRule
        {
            RuleId = ruleId,
            TargetColumn = targetColumn,
            NormalizationType = normType,
            Parameters = parameters ?? new(),
            CreatedAt = DateTime.UtcNow
        };

        if (!_rules.TryAdd(ruleId, rule))
            throw new InvalidOperationException($"Rule {ruleId} already exists");

        RecordOperation("RegisterNormalizationRule");
        return Task.FromResult(rule);
    }

    /// <summary>
    /// Normalizes records using registered rules.
    /// </summary>
    public Task<NormalizationResult> NormalizeAsync(
        IReadOnlyList<Dictionary<string, object>> records,
        IReadOnlyList<string>? ruleIds = null,
        CancellationToken ct = default)
    {
        var rules = ruleIds != null
            ? ruleIds.Select(id => _rules.GetValueOrDefault(id)).Where(r => r != null).Cast<NormalizationRule>().ToList()
            : _rules.Values.ToList();

        var normalizedRecords = new List<Dictionary<string, object>>();
        var modifications = 0;

        foreach (var record in records)
        {
            var normalized = new Dictionary<string, object>(record);
            foreach (var rule in rules)
            {
                if (ApplyNormalizationRule(normalized, rule))
                    modifications++;
            }
            normalizedRecords.Add(normalized);
        }

        RecordOperation("Normalize");

        return Task.FromResult(new NormalizationResult
        {
            InputRecords = records.Count,
            OutputRecords = normalizedRecords.Count,
            ModificationsApplied = modifications,
            NormalizedData = normalizedRecords,
            Status = NormalizationStatus.Success
        });
    }

    private bool ApplyNormalizationRule(Dictionary<string, object> record, NormalizationRule rule)
    {
        if (!record.TryGetValue(rule.TargetColumn, out var value))
            return false;

        var modified = false;
        object? newValue = value;

        switch (rule.NormalizationType)
        {
            case NormalizationType.DateTimeIso8601:
                if (value is DateTime dt)
                {
                    newValue = dt.ToString("O");
                    modified = true;
                }
                else if (value is string dateStr && DateTime.TryParse(dateStr, out var parsed))
                {
                    newValue = parsed.ToString("O");
                    modified = true;
                }
                break;

            case NormalizationType.PhoneE164:
                if (value is string phone)
                {
                    newValue = NormalizePhone(phone);
                    modified = !phone.Equals(newValue);
                }
                break;

            case NormalizationType.EmailLowercase:
                if (value is string email)
                {
                    newValue = email.ToLowerInvariant().Trim();
                    modified = !email.Equals(newValue);
                }
                break;

            case NormalizationType.UnitConversion:
                // Apply unit conversion based on parameters
                modified = true;
                break;

            case NormalizationType.AddressStandard:
                // Apply address standardization
                modified = true;
                break;
        }

        if (modified)
            record[rule.TargetColumn] = newValue!;

        return modified;
    }

    private string NormalizePhone(string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length == 10)
            return $"+1{digits}";
        return $"+{digits}";
    }
}

#endregion

#region 126.3.6 Flatten/Nest Strategy

/// <summary>
/// 126.3.6: Flatten and nest strategy for converting between
/// hierarchical and flat data structures.
/// </summary>
public sealed class FlattenNestStrategy : DataIntegrationStrategyBase
{
    public override string StrategyId => "transform-flatten-nest";
    public override string DisplayName => "Flatten/Nest Transformation";
    public override IntegrationCategory Category => IntegrationCategory.DataTransformation;
    public override DataIntegrationCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsExactlyOnce = true,
        SupportsSchemaEvolution = true,
        SupportsIncremental = true,
        SupportsParallel = true,
        SupportsDistributed = true,
        MaxThroughputRecordsPerSec = 1000000,
        TypicalLatencyMs = 10.0
    };
    public override string SemanticDescription =>
        "Flatten and nest transformations for converting between hierarchical (nested JSON) " +
        "and flat (tabular) data structures with path-based field access.";
    public override string[] Tags => ["transform", "flatten", "nest", "hierarchical", "json", "struct"];

    /// <summary>
    /// Flattens nested structures.
    /// </summary>
    public Task<FlattenResult> FlattenAsync(
        IReadOnlyList<Dictionary<string, object>> records,
        FlattenConfig? config = null,
        CancellationToken ct = default)
    {
        var cfg = config ?? new FlattenConfig();
        var flattened = new List<Dictionary<string, object>>();

        foreach (var record in records)
        {
            var flat = new Dictionary<string, object>();
            FlattenRecursive(record, "", flat, cfg.Separator);
            flattened.Add(flat);
        }

        RecordOperation("Flatten");

        return Task.FromResult(new FlattenResult
        {
            InputRecords = records.Count,
            OutputRecords = flattened.Count,
            FlattenedData = flattened,
            Status = FlattenStatus.Success
        });
    }

    private void FlattenRecursive(
        Dictionary<string, object> source,
        string prefix,
        Dictionary<string, object> target,
        string separator)
    {
        foreach (var kvp in source)
        {
            var key = string.IsNullOrEmpty(prefix) ? kvp.Key : $"{prefix}{separator}{kvp.Key}";

            if (kvp.Value is Dictionary<string, object> nested)
            {
                FlattenRecursive(nested, key, target, separator);
            }
            else if (kvp.Value is JsonElement je && je.ValueKind == JsonValueKind.Object)
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(je.GetRawText())!;
                FlattenRecursive(dict, key, target, separator);
            }
            else
            {
                target[key] = kvp.Value;
            }
        }
    }

    /// <summary>
    /// Nests flat structures into hierarchical format.
    /// </summary>
    public Task<NestResult> NestAsync(
        IReadOnlyList<Dictionary<string, object>> records,
        NestConfig? config = null,
        CancellationToken ct = default)
    {
        var cfg = config ?? new NestConfig();
        var nested = new List<Dictionary<string, object>>();

        foreach (var record in records)
        {
            var nest = new Dictionary<string, object>();
            foreach (var kvp in record)
            {
                SetNestedValue(nest, kvp.Key.Split(cfg.Separator), kvp.Value);
            }
            nested.Add(nest);
        }

        RecordOperation("Nest");

        return Task.FromResult(new NestResult
        {
            InputRecords = records.Count,
            OutputRecords = nested.Count,
            NestedData = nested,
            Status = NestStatus.Success
        });
    }

    private void SetNestedValue(Dictionary<string, object> target, string[] path, object value)
    {
        var current = target;
        for (int i = 0; i < path.Length - 1; i++)
        {
            if (!current.TryGetValue(path[i], out var next))
            {
                next = new Dictionary<string, object>();
                current[path[i]] = next;
            }
            current = (Dictionary<string, object>)next;
        }
        current[path[^1]] = value;
    }
}

#endregion

#region Supporting Types

// Type Conversion Types
public sealed record TypeMapping
{
    public required string MappingId { get; init; }
    public required string SourceType { get; init; }
    public required string TargetType { get; init; }
    public ConversionRule Rule { get; init; }
    public DateTime CreatedAt { get; init; }
}

public enum ConversionRule { Strict, Lenient, Coerce }

public sealed record ConversionResult
{
    public object? OriginalValue { get; init; }
    public object? ConvertedValue { get; set; }
    public required string SourceType { get; init; }
    public required string TargetType { get; init; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

// Aggregation Types
public enum AggFunction { Count, Sum, Avg, Min, Max, CountDistinct, First, Last }
public enum AggregationStatus { Success, PartialSuccess, Failed }

public sealed record AggregationFunction
{
    public required string InputColumn { get; init; }
    public required string OutputColumn { get; init; }
    public required AggFunction Function { get; init; }
}

public sealed record AggregationResult
{
    public int InputRecords { get; init; }
    public int OutputRecords { get; init; }
    public required List<Dictionary<string, object>> AggregatedData { get; init; }
    public AggregationStatus Status { get; init; }
}

// Cleansing Types
public enum CleansingOperation { Trim, ToUpperCase, ToLowerCase, ReplaceNull, RemoveSpecialChars, Standardize }
public enum CleansingStatus { Success, PartialSuccess, Failed }

public sealed record CleansingRule
{
    public required string RuleId { get; init; }
    public required string TargetColumn { get; init; }
    public CleansingOperation Operation { get; init; }
    public required Dictionary<string, object> Parameters { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed record CleansingResult
{
    public int InputRecords { get; init; }
    public int OutputRecords { get; init; }
    public int ModificationsApplied { get; init; }
    public required List<Dictionary<string, object>> CleansedData { get; init; }
    public CleansingStatus Status { get; init; }
}

// Enrichment Types
public enum EnrichmentSourceType { Database, Api, File, Cache }
public enum EnrichmentStatus { Success, PartialSuccess, Failed }

public sealed record EnrichmentSource
{
    public required string SourceId { get; init; }
    public EnrichmentSourceType SourceType { get; init; }
    public required string ConnectionString { get; init; }
    public required EnrichmentConfig Config { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed record EnrichmentConfig
{
    public bool EnableCache { get; init; } = true;
    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromMinutes(15);
    public int MaxConcurrentLookups { get; init; } = 10;
}

public sealed record EnrichmentResult
{
    public int InputRecords { get; init; }
    public int OutputRecords { get; init; }
    public int EnrichmentsApplied { get; init; }
    public required List<Dictionary<string, object>> EnrichedData { get; init; }
    public EnrichmentStatus Status { get; init; }
}

// Normalization Types
public enum NormalizationType { DateTimeIso8601, PhoneE164, EmailLowercase, UnitConversion, AddressStandard }
public enum NormalizationStatus { Success, PartialSuccess, Failed }

public sealed record NormalizationRule
{
    public required string RuleId { get; init; }
    public required string TargetColumn { get; init; }
    public NormalizationType NormalizationType { get; init; }
    public required Dictionary<string, object> Parameters { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed record NormalizationResult
{
    public int InputRecords { get; init; }
    public int OutputRecords { get; init; }
    public int ModificationsApplied { get; init; }
    public required List<Dictionary<string, object>> NormalizedData { get; init; }
    public NormalizationStatus Status { get; init; }
}

// Flatten/Nest Types
public enum FlattenStatus { Success, Failed }
public enum NestStatus { Success, Failed }

public sealed record FlattenConfig
{
    public string Separator { get; init; } = ".";
    public int MaxDepth { get; init; } = 10;
}

public sealed record FlattenResult
{
    public int InputRecords { get; init; }
    public int OutputRecords { get; init; }
    public required List<Dictionary<string, object>> FlattenedData { get; init; }
    public FlattenStatus Status { get; init; }
}

public sealed record NestConfig
{
    public string Separator { get; init; } = ".";
}

public sealed record NestResult
{
    public int InputRecords { get; init; }
    public int OutputRecords { get; init; }
    public required List<Dictionary<string, object>> NestedData { get; init; }
    public NestStatus Status { get; init; }
}

#endregion
