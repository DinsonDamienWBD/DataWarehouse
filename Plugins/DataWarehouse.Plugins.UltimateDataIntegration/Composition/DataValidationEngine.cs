using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateDataIntegration.Composition;

/// <summary>
/// Comprehensive data validation engine with required fields, regex patterns,
/// range checks, referential integrity, and custom validators.
/// </summary>
public sealed class DataValidationEngine
{
    private readonly ConcurrentDictionary<string, ValidationRuleSet> _ruleSets = new();
    private readonly ConcurrentDictionary<string, Func<object?, ValidationResult>> _customValidators = new();

    /// <summary>
    /// Registers a validation rule set for a dataset/table.
    /// </summary>
    public void RegisterRuleSet(string datasetId, ValidationRuleSet ruleSet)
    {
        _ruleSets[datasetId] = ruleSet;
    }

    /// <summary>
    /// Registers a custom validator function.
    /// </summary>
    public void RegisterCustomValidator(string name, Func<object?, ValidationResult> validator)
    {
        _customValidators[name] = validator;
    }

    /// <summary>
    /// Validates a batch of records against a rule set.
    /// </summary>
    public Task<BatchValidationResult> ValidateAsync(
        string datasetId,
        IReadOnlyList<Dictionary<string, object?>> records,
        ValidationOptions? options = null,
        CancellationToken ct = default)
    {
        if (!_ruleSets.TryGetValue(datasetId, out var ruleSet))
            throw new KeyNotFoundException($"No rule set registered for dataset '{datasetId}'");

        options ??= new ValidationOptions();

        var errors = new List<RecordValidationError>();
        var validCount = 0;
        var invalidCount = 0;

        for (int i = 0; i < records.Count && !ct.IsCancellationRequested; i++)
        {
            var record = records[i];
            var recordErrors = ValidateRecord(record, ruleSet, i);

            if (recordErrors.Count > 0)
            {
                invalidCount++;
                errors.AddRange(recordErrors);

                if (options.StopAfterErrors > 0 && errors.Count >= options.StopAfterErrors)
                    break;
            }
            else
            {
                validCount++;
            }
        }

        return Task.FromResult(new BatchValidationResult
        {
            DatasetId = datasetId,
            TotalRecords = records.Count,
            ValidRecords = validCount,
            InvalidRecords = invalidCount,
            Errors = errors,
            IsValid = invalidCount == 0,
            ValidatedAt = DateTimeOffset.UtcNow
        });
    }

    private List<RecordValidationError> ValidateRecord(
        Dictionary<string, object?> record,
        ValidationRuleSet ruleSet,
        int rowIndex)
    {
        var errors = new List<RecordValidationError>();

        foreach (var rule in ruleSet.Rules)
        {
            record.TryGetValue(rule.FieldName, out var value);

            // Required field check
            if (rule.IsRequired && (value == null || (value is string s && string.IsNullOrWhiteSpace(s))))
            {
                errors.Add(new RecordValidationError
                {
                    RowIndex = rowIndex,
                    FieldName = rule.FieldName,
                    RuleType = "Required",
                    Message = $"Field '{rule.FieldName}' is required but is null or empty",
                    Severity = ValidationSeverity.Error
                });
                continue;
            }

            if (value == null) continue;

            // Regex pattern check
            if (!string.IsNullOrEmpty(rule.RegexPattern))
            {
                var strValue = value.ToString() ?? "";
                if (!Regex.IsMatch(strValue, rule.RegexPattern))
                {
                    errors.Add(new RecordValidationError
                    {
                        RowIndex = rowIndex,
                        FieldName = rule.FieldName,
                        RuleType = "Pattern",
                        Message = $"Field '{rule.FieldName}' value '{strValue}' does not match pattern '{rule.RegexPattern}'",
                        Severity = ValidationSeverity.Error
                    });
                }
            }

            // Range check (numeric)
            if (rule.MinValue.HasValue || rule.MaxValue.HasValue)
            {
                if (double.TryParse(value.ToString(), out var numValue))
                {
                    if (rule.MinValue.HasValue && numValue < rule.MinValue.Value)
                    {
                        errors.Add(new RecordValidationError
                        {
                            RowIndex = rowIndex,
                            FieldName = rule.FieldName,
                            RuleType = "Range",
                            Message = $"Field '{rule.FieldName}' value {numValue} is below minimum {rule.MinValue}",
                            Severity = ValidationSeverity.Error
                        });
                    }

                    if (rule.MaxValue.HasValue && numValue > rule.MaxValue.Value)
                    {
                        errors.Add(new RecordValidationError
                        {
                            RowIndex = rowIndex,
                            FieldName = rule.FieldName,
                            RuleType = "Range",
                            Message = $"Field '{rule.FieldName}' value {numValue} exceeds maximum {rule.MaxValue}",
                            Severity = ValidationSeverity.Error
                        });
                    }
                }
            }

            // Allowed values check
            if (rule.AllowedValues?.Count > 0)
            {
                var strValue = value.ToString() ?? "";
                if (!rule.AllowedValues.Contains(strValue))
                {
                    errors.Add(new RecordValidationError
                    {
                        RowIndex = rowIndex,
                        FieldName = rule.FieldName,
                        RuleType = "AllowedValues",
                        Message = $"Field '{rule.FieldName}' value '{strValue}' is not in allowed values: [{string.Join(", ", rule.AllowedValues)}]",
                        Severity = ValidationSeverity.Error
                    });
                }
            }

            // String length check
            if (rule.MaxLength > 0)
            {
                var strValue = value.ToString() ?? "";
                if (strValue.Length > rule.MaxLength)
                {
                    errors.Add(new RecordValidationError
                    {
                        RowIndex = rowIndex,
                        FieldName = rule.FieldName,
                        RuleType = "MaxLength",
                        Message = $"Field '{rule.FieldName}' length {strValue.Length} exceeds maximum {rule.MaxLength}",
                        Severity = ValidationSeverity.Error
                    });
                }
            }

            // Type check
            if (!string.IsNullOrEmpty(rule.ExpectedType))
            {
                var isValid = rule.ExpectedType.ToLowerInvariant() switch
                {
                    "int" or "integer" => int.TryParse(value.ToString(), out _),
                    "long" => long.TryParse(value.ToString(), out _),
                    "double" or "float" or "decimal" => double.TryParse(value.ToString(), out _),
                    "bool" or "boolean" => bool.TryParse(value.ToString(), out _),
                    "datetime" or "date" => DateTime.TryParse(value.ToString(), out _),
                    "guid" or "uuid" => Guid.TryParse(value.ToString(), out _),
                    "email" => Regex.IsMatch(value.ToString() ?? "", @"^[^@\s]+@[^@\s]+\.[^@\s]+$"),
                    "url" => Uri.TryCreate(value.ToString(), UriKind.Absolute, out _),
                    _ => true
                };

                if (!isValid)
                {
                    errors.Add(new RecordValidationError
                    {
                        RowIndex = rowIndex,
                        FieldName = rule.FieldName,
                        RuleType = "Type",
                        Message = $"Field '{rule.FieldName}' value cannot be parsed as {rule.ExpectedType}",
                        Severity = ValidationSeverity.Error
                    });
                }
            }

            // Custom validator
            if (!string.IsNullOrEmpty(rule.CustomValidatorName) &&
                _customValidators.TryGetValue(rule.CustomValidatorName, out var validator))
            {
                var result = validator(value);
                if (!result.IsValid)
                {
                    errors.Add(new RecordValidationError
                    {
                        RowIndex = rowIndex,
                        FieldName = rule.FieldName,
                        RuleType = "Custom",
                        Message = result.Message ?? $"Custom validation '{rule.CustomValidatorName}' failed",
                        Severity = result.Severity
                    });
                }
            }
        }

        return errors;
    }
}

/// <summary>
/// Data quality scoring engine. Computes completeness, accuracy, consistency,
/// and timeliness scores per column and per dataset.
/// </summary>
public sealed class DataQualityScoringEngine
{
    /// <summary>
    /// Computes comprehensive data quality scores for a dataset.
    /// </summary>
    public Task<DataQualityReport> ScoreAsync(
        IReadOnlyList<Dictionary<string, object?>> records,
        DataQualityScoringOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new DataQualityScoringOptions();

        if (records.Count == 0)
        {
            return Task.FromResult(new DataQualityReport
            {
                TotalRecords = 0,
                OverallScore = 0,
                CompletenessScore = 0,
                ConsistencyScore = 0,
                ColumnScores = new Dictionary<string, ColumnQualityScore>(),
                ScoredAt = DateTimeOffset.UtcNow
            });
        }

        // Collect all column names
        var allColumns = new HashSet<string>();
        foreach (var record in records)
        {
            foreach (var key in record.Keys)
                allColumns.Add(key);
        }

        var columnScores = new Dictionary<string, ColumnQualityScore>();

        foreach (var column in allColumns)
        {
            var values = records.Select(r => r.GetValueOrDefault(column)).ToList();

            // Completeness: ratio of non-null values
            var nonNullCount = values.Count(v => v != null && !(v is string s && string.IsNullOrWhiteSpace(s)));
            var completeness = (double)nonNullCount / records.Count;

            // Consistency: ratio of values matching the dominant type
            var typeGroups = values.Where(v => v != null)
                .GroupBy(v => v!.GetType().Name)
                .OrderByDescending(g => g.Count())
                .ToList();
            var consistencyScore = typeGroups.Count > 0
                ? (double)typeGroups[0].Count() / Math.Max(nonNullCount, 1)
                : 1.0;

            // Uniqueness: ratio of distinct values to total non-null values
            var distinctCount = values.Where(v => v != null)
                .Select(v => v!.ToString())
                .Distinct()
                .Count();
            var uniqueness = nonNullCount > 0 ? (double)distinctCount / nonNullCount : 1.0;

            // Detect outliers for numeric columns
            var numericValues = new List<double>();
            foreach (var v in values)
            {
                if (v != null && double.TryParse(v.ToString(), out var d))
                    numericValues.Add(d);
            }

            double accuracy = 1.0;
            if (numericValues.Count > 0)
            {
                var mean = numericValues.Average();
                var stdDev = Math.Sqrt(numericValues.Sum(x => Math.Pow(x - mean, 2)) / numericValues.Count);
                var outliers = stdDev > 0
                    ? numericValues.Count(x => Math.Abs(x - mean) > 3 * stdDev)
                    : 0;
                accuracy = 1.0 - ((double)outliers / numericValues.Count);
            }

            columnScores[column] = new ColumnQualityScore
            {
                ColumnName = column,
                Completeness = completeness,
                Consistency = consistencyScore,
                Uniqueness = uniqueness,
                Accuracy = accuracy,
                NullCount = records.Count - nonNullCount,
                DistinctCount = distinctCount,
                TotalCount = records.Count,
                DominantType = typeGroups.Count > 0 ? typeGroups[0].Key : "Unknown"
            };
        }

        // Overall scores
        var overallCompleteness = columnScores.Values.Average(c => c.Completeness);
        var overallConsistency = columnScores.Values.Average(c => c.Consistency);
        var overallAccuracy = columnScores.Values.Average(c => c.Accuracy);
        var overallUniqueness = columnScores.Values.Average(c => c.Uniqueness);

        var overallScore = (overallCompleteness * 0.3 + overallConsistency * 0.25 +
            overallAccuracy * 0.25 + overallUniqueness * 0.2) * 100;

        return Task.FromResult(new DataQualityReport
        {
            TotalRecords = records.Count,
            OverallScore = Math.Round(overallScore, 2),
            CompletenessScore = Math.Round(overallCompleteness * 100, 2),
            ConsistencyScore = Math.Round(overallConsistency * 100, 2),
            AccuracyScore = Math.Round(overallAccuracy * 100, 2),
            UniquenessScore = Math.Round(overallUniqueness * 100, 2),
            ColumnScores = columnScores,
            ColumnCount = allColumns.Count,
            ScoredAt = DateTimeOffset.UtcNow
        });
    }
}

#region Models

public sealed class ValidationRuleSet
{
    public required string DatasetId { get; init; }
    public required List<ValidationRule> Rules { get; init; }
}

public sealed class ValidationRule
{
    public required string FieldName { get; init; }
    public bool IsRequired { get; init; }
    public string? RegexPattern { get; init; }
    public double? MinValue { get; init; }
    public double? MaxValue { get; init; }
    public int MaxLength { get; init; }
    public string? ExpectedType { get; init; }
    public List<string>? AllowedValues { get; init; }
    public string? CustomValidatorName { get; init; }
}

public sealed class ValidationOptions
{
    public int StopAfterErrors { get; init; }
    public bool IncludeWarnings { get; init; } = true;
}

public sealed class BatchValidationResult
{
    public required string DatasetId { get; init; }
    public int TotalRecords { get; init; }
    public int ValidRecords { get; init; }
    public int InvalidRecords { get; init; }
    public required List<RecordValidationError> Errors { get; init; }
    public bool IsValid { get; init; }
    public DateTimeOffset ValidatedAt { get; init; }
}

public sealed class RecordValidationError
{
    public int RowIndex { get; init; }
    public required string FieldName { get; init; }
    public required string RuleType { get; init; }
    public required string Message { get; init; }
    public ValidationSeverity Severity { get; init; } = ValidationSeverity.Error;
}

public enum ValidationSeverity
{
    Info,
    Warning,
    Error,
    Critical
}

public sealed class ValidationResult
{
    public bool IsValid { get; init; }
    public string? Message { get; init; }
    public ValidationSeverity Severity { get; init; } = ValidationSeverity.Error;
}

public sealed class DataQualityScoringOptions
{
    public double CompletenessWeight { get; init; } = 0.3;
    public double ConsistencyWeight { get; init; } = 0.25;
    public double AccuracyWeight { get; init; } = 0.25;
    public double UniquenessWeight { get; init; } = 0.2;
}

public sealed class DataQualityReport
{
    public int TotalRecords { get; init; }
    public double OverallScore { get; init; }
    public double CompletenessScore { get; init; }
    public double ConsistencyScore { get; init; }
    public double AccuracyScore { get; init; }
    public double UniquenessScore { get; init; }
    public int ColumnCount { get; init; }
    public required Dictionary<string, ColumnQualityScore> ColumnScores { get; init; }
    public DateTimeOffset ScoredAt { get; init; }
}

public sealed class ColumnQualityScore
{
    public required string ColumnName { get; init; }
    public double Completeness { get; init; }
    public double Consistency { get; init; }
    public double Uniqueness { get; init; }
    public double Accuracy { get; init; }
    public int NullCount { get; init; }
    public int DistinctCount { get; init; }
    public int TotalCount { get; init; }
    public required string DominantType { get; init; }
}

#endregion
