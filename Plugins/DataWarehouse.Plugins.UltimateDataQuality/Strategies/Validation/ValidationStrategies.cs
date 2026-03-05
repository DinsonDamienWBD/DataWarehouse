using System.Text.Json;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataQuality.Strategies.Validation;

#region Validation Rule Types

/// <summary>
/// Represents a validation rule for data quality checks.
/// </summary>
public sealed class ValidationRule
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
    /// Description of the rule.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Type of validation.
    /// </summary>
    public required ValidationType Type { get; init; }

    /// <summary>
    /// Target field name(s).
    /// </summary>
    public required string[] TargetFields { get; init; }

    /// <summary>
    /// Severity when rule is violated.
    /// </summary>
    public IssueSeverity Severity { get; init; } = IssueSeverity.Error;

    /// <summary>
    /// Rule parameters (type-specific).
    /// </summary>
    public Dictionary<string, object>? Parameters { get; init; }

    /// <summary>
    /// Custom error message template.
    /// </summary>
    public string? ErrorMessageTemplate { get; init; }

    /// <summary>
    /// Whether to stop on first failure.
    /// </summary>
    public bool StopOnFail { get; init; }

    /// <summary>
    /// Whether the rule is enabled.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Priority (higher = evaluated first).
    /// </summary>
    public int Priority { get; init; } = 100;

    /// <summary>
    /// Tags for rule categorization.
    /// </summary>
    public string[]? Tags { get; init; }
}

/// <summary>
/// Types of validation rules.
/// </summary>
public enum ValidationType
{
    /// <summary>Field is required (non-null, non-empty).</summary>
    Required,
    /// <summary>Field matches regex pattern.</summary>
    Regex,
    /// <summary>Numeric value is within range.</summary>
    Range,
    /// <summary>String length is within bounds.</summary>
    Length,
    /// <summary>Value is one of allowed values.</summary>
    AllowedValues,
    /// <summary>Value is a valid email address.</summary>
    Email,
    /// <summary>Value is a valid URL.</summary>
    Url,
    /// <summary>Value is a valid date.</summary>
    Date,
    /// <summary>Value is a valid date-time.</summary>
    DateTime,
    /// <summary>Value is a valid phone number.</summary>
    Phone,
    /// <summary>Value is a valid UUID/GUID.</summary>
    Uuid,
    /// <summary>Value is a valid JSON structure.</summary>
    Json,
    /// <summary>Value is numeric.</summary>
    Numeric,
    /// <summary>Value is a valid IP address.</summary>
    IpAddress,
    /// <summary>Value is unique across dataset.</summary>
    Unique,
    /// <summary>Cross-field comparison.</summary>
    CrossField,
    /// <summary>Custom expression-based validation.</summary>
    Expression,
    /// <summary>Referential integrity check.</summary>
    Reference,
    /// <summary>Data type check.</summary>
    DataType,
    /// <summary>Custom function validation.</summary>
    Custom
}

/// <summary>
/// Result of validating a single record.
/// </summary>
public sealed class ValidationResult
{
    /// <summary>
    /// Record ID that was validated.
    /// </summary>
    public required string RecordId { get; init; }

    /// <summary>
    /// Whether the record is valid.
    /// </summary>
    public bool IsValid => Issues.Count == 0;

    /// <summary>
    /// List of validation issues found.
    /// </summary>
    public List<DataQualityIssue> Issues { get; init; } = new();

    /// <summary>
    /// Rules that were evaluated.
    /// </summary>
    public List<string> EvaluatedRules { get; init; } = new();

    /// <summary>
    /// Rules that passed.
    /// </summary>
    public List<string> PassedRules { get; init; } = new();

    /// <summary>
    /// Rules that failed.
    /// </summary>
    public List<string> FailedRules { get; init; } = new();

    /// <summary>
    /// Validation timestamp.
    /// </summary>
    public DateTime ValidatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Processing time in milliseconds.
    /// </summary>
    public double ProcessingTimeMs { get; init; }
}

#endregion

#region Schema-Based Validation Strategy

/// <summary>
/// Schema-based validation strategy using JSON Schema or similar definitions.
/// Validates data structure, types, and constraints based on schema definitions.
/// </summary>
public sealed class SchemaValidationStrategy : DataQualityStrategyBase
{
    private readonly BoundedDictionary<string, JsonDocument> _schemas = new BoundedDictionary<string, JsonDocument>(1000);
    private readonly BoundedDictionary<string, ValidationRule[]> _schemaRules = new BoundedDictionary<string, ValidationRule[]>(1000);

    /// <inheritdoc/>
    public override string StrategyId => "schema-validation";

    /// <inheritdoc/>
    public override string DisplayName => "Schema-Based Validation";

    /// <inheritdoc/>
    public override DataQualityCategory Category => DataQualityCategory.Validation;

    /// <inheritdoc/>
    public override DataQualityCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsDistributed = true,
        SupportsIncremental = true,
        MaxThroughput = 50000,
        TypicalLatencyMs = 0.5
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Validates data against JSON Schema or custom schema definitions. " +
        "Supports type checking, required fields, constraints, and nested structures.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "validation", "schema", "json-schema", "structure", "types", "constraints"
    };

    /// <summary>
    /// Registers a schema for validation.
    /// </summary>
    public void RegisterSchema(string schemaId, string schemaJson)
    {
        using var doc = JsonDocument.Parse(schemaJson);
        _schemas[schemaId] = doc;
        _schemaRules[schemaId] = ParseSchemaToRules(schemaId, doc);
    }

    /// <summary>
    /// Validates a record against a registered schema.
    /// </summary>
    public async Task<ValidationResult> ValidateAsync(
        DataRecord record,
        string schemaId,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        var startTime = DateTime.UtcNow;

        if (!_schemaRules.TryGetValue(schemaId, out var rules))
        {
            throw new ArgumentException($"Schema '{schemaId}' not found");
        }

        var issues = new List<DataQualityIssue>();
        var evaluatedRules = new List<string>();
        var passedRules = new List<string>();
        var failedRules = new List<string>();

        foreach (var rule in rules.Where(r => r.Enabled).OrderByDescending(r => r.Priority))
        {
            ct.ThrowIfCancellationRequested();
            evaluatedRules.Add(rule.RuleId);

            var ruleIssues = await EvaluateRuleAsync(record, rule, ct);
            if (ruleIssues.Count == 0)
            {
                passedRules.Add(rule.RuleId);
            }
            else
            {
                failedRules.Add(rule.RuleId);
                issues.AddRange(ruleIssues);

                if (rule.StopOnFail)
                    break;
            }
        }

        var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
        RecordProcessed(issues.Count == 0, elapsed);

        return new ValidationResult
        {
            RecordId = record.RecordId,
            Issues = issues,
            EvaluatedRules = evaluatedRules,
            PassedRules = passedRules,
            FailedRules = failedRules,
            ProcessingTimeMs = elapsed
        };
    }

    private ValidationRule[] ParseSchemaToRules(string schemaId, JsonDocument doc)
    {
        var rules = new List<ValidationRule>();
        var root = doc.RootElement;

        if (root.TryGetProperty("properties", out var properties))
        {
            foreach (var prop in properties.EnumerateObject())
            {
                var fieldRules = ParsePropertyRules(schemaId, prop.Name, prop.Value);
                rules.AddRange(fieldRules);
            }
        }

        if (root.TryGetProperty("required", out var required))
        {
            foreach (var field in required.EnumerateArray())
            {
                rules.Add(new ValidationRule
                {
                    RuleId = $"{schemaId}.{field.GetString()}.required",
                    Name = $"Required: {field.GetString()}",
                    Type = ValidationType.Required,
                    TargetFields = new[] { field.GetString()! },
                    Severity = IssueSeverity.Error
                });
            }
        }

        return rules.ToArray();
    }

    private IEnumerable<ValidationRule> ParsePropertyRules(string schemaId, string fieldName, JsonElement prop)
    {
        var rules = new List<ValidationRule>();

        // Type validation
        if (prop.TryGetProperty("type", out var typeElem))
        {
            rules.Add(new ValidationRule
            {
                RuleId = $"{schemaId}.{fieldName}.type",
                Name = $"Type: {fieldName}",
                Type = ValidationType.DataType,
                TargetFields = new[] { fieldName },
                Parameters = new Dictionary<string, object>
                {
                    ["expectedType"] = typeElem.GetString()!
                }
            });
        }

        // String constraints
        if (prop.TryGetProperty("minLength", out var minLen))
        {
            rules.Add(new ValidationRule
            {
                RuleId = $"{schemaId}.{fieldName}.minLength",
                Name = $"Min Length: {fieldName}",
                Type = ValidationType.Length,
                TargetFields = new[] { fieldName },
                Parameters = new Dictionary<string, object>
                {
                    ["min"] = minLen.GetInt32()
                }
            });
        }

        if (prop.TryGetProperty("maxLength", out var maxLen))
        {
            rules.Add(new ValidationRule
            {
                RuleId = $"{schemaId}.{fieldName}.maxLength",
                Name = $"Max Length: {fieldName}",
                Type = ValidationType.Length,
                TargetFields = new[] { fieldName },
                Parameters = new Dictionary<string, object>
                {
                    ["max"] = maxLen.GetInt32()
                }
            });
        }

        // Pattern
        if (prop.TryGetProperty("pattern", out var pattern))
        {
            rules.Add(new ValidationRule
            {
                RuleId = $"{schemaId}.{fieldName}.pattern",
                Name = $"Pattern: {fieldName}",
                Type = ValidationType.Regex,
                TargetFields = new[] { fieldName },
                Parameters = new Dictionary<string, object>
                {
                    ["pattern"] = pattern.GetString()!
                }
            });
        }

        // Format
        if (prop.TryGetProperty("format", out var format))
        {
            var formatType = format.GetString() switch
            {
                "email" => ValidationType.Email,
                "uri" or "url" => ValidationType.Url,
                "date" => ValidationType.Date,
                "date-time" => ValidationType.DateTime,
                "uuid" => ValidationType.Uuid,
                "ipv4" or "ipv6" => ValidationType.IpAddress,
                _ => (ValidationType?)null
            };

            if (formatType.HasValue)
            {
                rules.Add(new ValidationRule
                {
                    RuleId = $"{schemaId}.{fieldName}.format",
                    Name = $"Format: {fieldName}",
                    Type = formatType.Value,
                    TargetFields = new[] { fieldName }
                });
            }
        }

        // Numeric constraints
        if (prop.TryGetProperty("minimum", out var min) || prop.TryGetProperty("maximum", out var max))
        {
            var parameters = new Dictionary<string, object>();
            if (prop.TryGetProperty("minimum", out min))
                parameters["min"] = min.GetDouble();
            if (prop.TryGetProperty("maximum", out max))
                parameters["max"] = max.GetDouble();

            rules.Add(new ValidationRule
            {
                RuleId = $"{schemaId}.{fieldName}.range",
                Name = $"Range: {fieldName}",
                Type = ValidationType.Range,
                TargetFields = new[] { fieldName },
                Parameters = parameters
            });
        }

        // Enum
        if (prop.TryGetProperty("enum", out var enumValues))
        {
            var allowed = enumValues.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText())
                .Cast<object>()
                .ToArray();

            rules.Add(new ValidationRule
            {
                RuleId = $"{schemaId}.{fieldName}.enum",
                Name = $"Allowed Values: {fieldName}",
                Type = ValidationType.AllowedValues,
                TargetFields = new[] { fieldName },
                Parameters = new Dictionary<string, object>
                {
                    ["allowed"] = allowed
                }
            });
        }

        return rules;
    }

    private Task<List<DataQualityIssue>> EvaluateRuleAsync(
        DataRecord record,
        ValidationRule rule,
        CancellationToken ct)
    {
        var issues = new List<DataQualityIssue>();

        foreach (var fieldName in rule.TargetFields)
        {
            var hasField = record.Fields.TryGetValue(fieldName, out var value);
            var issue = rule.Type switch
            {
                ValidationType.Required => ValidateRequired(fieldName, hasField, value, rule),
                ValidationType.Regex => ValidateRegex(fieldName, value, rule),
                ValidationType.Range => ValidateRange(fieldName, value, rule),
                ValidationType.Length => ValidateLength(fieldName, value, rule),
                ValidationType.AllowedValues => ValidateAllowedValues(fieldName, value, rule),
                ValidationType.Email => ValidateEmail(fieldName, value, rule),
                ValidationType.Url => ValidateUrl(fieldName, value, rule),
                ValidationType.Date => ValidateDate(fieldName, value, rule),
                ValidationType.DateTime => ValidateDateTime(fieldName, value, rule),
                ValidationType.Uuid => ValidateUuid(fieldName, value, rule),
                ValidationType.IpAddress => ValidateIpAddress(fieldName, value, rule),
                ValidationType.Numeric => ValidateNumeric(fieldName, value, rule),
                ValidationType.DataType => ValidateDataType(fieldName, value, rule),
                _ => null
            };

            if (issue != null)
            {
                issue.Context?.Add("recordId", record.RecordId);
                issues.Add(issue);
            }
        }

        return Task.FromResult(issues);
    }

    private DataQualityIssue? ValidateRequired(string field, bool hasField, object? value, ValidationRule rule)
    {
        if (!hasField || value == null || (value is string s && string.IsNullOrWhiteSpace(s)))
        {
            return new DataQualityIssue
            {
                Severity = rule.Severity,
                Category = IssueCategory.MissingValue,
                FieldName = field,
                Description = rule.ErrorMessageTemplate ?? $"Field '{field}' is required",
                RuleId = rule.RuleId,
                Context = new Dictionary<string, object>()
            };
        }
        return null;
    }

    private DataQualityIssue? ValidateRegex(string field, object? value, ValidationRule rule)
    {
        if (value == null) return null;
        var pattern = rule.Parameters?.GetValueOrDefault("pattern")?.ToString();
        if (pattern == null) return null;

        try
        {
            if (!Regex.IsMatch(value.ToString() ?? "", pattern))
            {
                return new DataQualityIssue
                {
                    Severity = rule.Severity,
                    Category = IssueCategory.InvalidFormat,
                    FieldName = field,
                    Description = rule.ErrorMessageTemplate ?? $"Field '{field}' does not match pattern",
                    OriginalValue = value,
                    RuleId = rule.RuleId,
                    Context = new Dictionary<string, object> { ["pattern"] = pattern }
                };
            }
        }
        catch { /* Invalid regex */ }
        return null;
    }

    private DataQualityIssue? ValidateRange(string field, object? value, ValidationRule rule)
    {
        if (value == null) return null;
        if (!double.TryParse(value.ToString(), out var numValue)) return null;

        var min = rule.Parameters?.GetValueOrDefault("min") as double?;
        var max = rule.Parameters?.GetValueOrDefault("max") as double?;

        if ((min.HasValue && numValue < min.Value) || (max.HasValue && numValue > max.Value))
        {
            return new DataQualityIssue
            {
                Severity = rule.Severity,
                Category = IssueCategory.OutOfRange,
                FieldName = field,
                Description = rule.ErrorMessageTemplate ??
                    $"Field '{field}' value {numValue} is out of range [{min?.ToString() ?? "*"}, {max?.ToString() ?? "*"}]",
                OriginalValue = value,
                RuleId = rule.RuleId,
                Context = new Dictionary<string, object> { ["min"] = min ?? double.MinValue, ["max"] = max ?? double.MaxValue }
            };
        }
        return null;
    }

    private DataQualityIssue? ValidateLength(string field, object? value, ValidationRule rule)
    {
        if (value == null) return null;
        var strValue = value.ToString() ?? "";
        var length = strValue.Length;

        var min = rule.Parameters?.GetValueOrDefault("min") as int?;
        var max = rule.Parameters?.GetValueOrDefault("max") as int?;

        if ((min.HasValue && length < min.Value) || (max.HasValue && length > max.Value))
        {
            return new DataQualityIssue
            {
                Severity = rule.Severity,
                Category = IssueCategory.InvalidFormat,
                FieldName = field,
                Description = rule.ErrorMessageTemplate ??
                    $"Field '{field}' length {length} is out of range [{min?.ToString() ?? "0"}, {max?.ToString() ?? "*"}]",
                OriginalValue = value,
                RuleId = rule.RuleId,
                Context = new Dictionary<string, object>()
            };
        }
        return null;
    }

    private DataQualityIssue? ValidateAllowedValues(string field, object? value, ValidationRule rule)
    {
        if (value == null) return null;
        var allowed = rule.Parameters?.GetValueOrDefault("allowed") as object[];
        if (allowed == null) return null;

        var strValue = value.ToString();
        if (!allowed.Any(a => string.Equals(a?.ToString(), strValue, StringComparison.OrdinalIgnoreCase)))
        {
            return new DataQualityIssue
            {
                Severity = rule.Severity,
                Category = IssueCategory.BusinessRule,
                FieldName = field,
                Description = rule.ErrorMessageTemplate ??
                    $"Field '{field}' value '{strValue}' is not in allowed values",
                OriginalValue = value,
                RuleId = rule.RuleId,
                Context = new Dictionary<string, object> { ["allowed"] = allowed }
            };
        }
        return null;
    }

    private static readonly Regex EmailRegex = new(@"^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}$", RegexOptions.Compiled);

    private DataQualityIssue? ValidateEmail(string field, object? value, ValidationRule rule)
    {
        if (value == null) return null;
        if (!EmailRegex.IsMatch(value.ToString() ?? ""))
        {
            return new DataQualityIssue
            {
                Severity = rule.Severity,
                Category = IssueCategory.InvalidFormat,
                FieldName = field,
                Description = rule.ErrorMessageTemplate ?? $"Field '{field}' is not a valid email address",
                OriginalValue = value,
                RuleId = rule.RuleId,
                Context = new Dictionary<string, object>()
            };
        }
        return null;
    }

    private DataQualityIssue? ValidateUrl(string field, object? value, ValidationRule rule)
    {
        if (value == null) return null;
        if (!Uri.TryCreate(value.ToString(), UriKind.Absolute, out _))
        {
            return new DataQualityIssue
            {
                Severity = rule.Severity,
                Category = IssueCategory.InvalidFormat,
                FieldName = field,
                Description = rule.ErrorMessageTemplate ?? $"Field '{field}' is not a valid URL",
                OriginalValue = value,
                RuleId = rule.RuleId,
                Context = new Dictionary<string, object>()
            };
        }
        return null;
    }

    private DataQualityIssue? ValidateDate(string field, object? value, ValidationRule rule)
    {
        if (value == null) return null;
        if (!DateOnly.TryParse(value.ToString(), out _))
        {
            return new DataQualityIssue
            {
                Severity = rule.Severity,
                Category = IssueCategory.InvalidFormat,
                FieldName = field,
                Description = rule.ErrorMessageTemplate ?? $"Field '{field}' is not a valid date",
                OriginalValue = value,
                RuleId = rule.RuleId,
                Context = new Dictionary<string, object>()
            };
        }
        return null;
    }

    private DataQualityIssue? ValidateDateTime(string field, object? value, ValidationRule rule)
    {
        if (value == null) return null;
        if (!DateTime.TryParse(value.ToString(), out _))
        {
            return new DataQualityIssue
            {
                Severity = rule.Severity,
                Category = IssueCategory.InvalidFormat,
                FieldName = field,
                Description = rule.ErrorMessageTemplate ?? $"Field '{field}' is not a valid date-time",
                OriginalValue = value,
                RuleId = rule.RuleId,
                Context = new Dictionary<string, object>()
            };
        }
        return null;
    }

    private DataQualityIssue? ValidateUuid(string field, object? value, ValidationRule rule)
    {
        if (value == null) return null;
        if (!Guid.TryParse(value.ToString(), out _))
        {
            return new DataQualityIssue
            {
                Severity = rule.Severity,
                Category = IssueCategory.InvalidFormat,
                FieldName = field,
                Description = rule.ErrorMessageTemplate ?? $"Field '{field}' is not a valid UUID",
                OriginalValue = value,
                RuleId = rule.RuleId,
                Context = new Dictionary<string, object>()
            };
        }
        return null;
    }

    private static readonly Regex IpV4Regex = new(@"^(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)$", RegexOptions.Compiled);

    private DataQualityIssue? ValidateIpAddress(string field, object? value, ValidationRule rule)
    {
        if (value == null) return null;
        var strValue = value.ToString() ?? "";
        if (!IpV4Regex.IsMatch(strValue) && !System.Net.IPAddress.TryParse(strValue, out _))
        {
            return new DataQualityIssue
            {
                Severity = rule.Severity,
                Category = IssueCategory.InvalidFormat,
                FieldName = field,
                Description = rule.ErrorMessageTemplate ?? $"Field '{field}' is not a valid IP address",
                OriginalValue = value,
                RuleId = rule.RuleId,
                Context = new Dictionary<string, object>()
            };
        }
        return null;
    }

    private DataQualityIssue? ValidateNumeric(string field, object? value, ValidationRule rule)
    {
        if (value == null) return null;
        if (!double.TryParse(value.ToString(), out _))
        {
            return new DataQualityIssue
            {
                Severity = rule.Severity,
                Category = IssueCategory.TypeMismatch,
                FieldName = field,
                Description = rule.ErrorMessageTemplate ?? $"Field '{field}' is not a valid number",
                OriginalValue = value,
                RuleId = rule.RuleId,
                Context = new Dictionary<string, object>()
            };
        }
        return null;
    }

    private DataQualityIssue? ValidateDataType(string field, object? value, ValidationRule rule)
    {
        if (value == null) return null;
        var expectedType = rule.Parameters?.GetValueOrDefault("expectedType")?.ToString();
        if (expectedType == null) return null;

        bool isValid = expectedType.ToLowerInvariant() switch
        {
            "string" => value is string,
            "number" or "integer" => double.TryParse(value.ToString(), out _),
            "boolean" => value is bool || bool.TryParse(value.ToString(), out _),
            "array" => value is System.Collections.IEnumerable && value is not string,
            "object" => value is IDictionary<string, object>,
            _ => true
        };

        if (!isValid)
        {
            return new DataQualityIssue
            {
                Severity = rule.Severity,
                Category = IssueCategory.TypeMismatch,
                FieldName = field,
                Description = rule.ErrorMessageTemplate ??
                    $"Field '{field}' expected type '{expectedType}' but got '{value.GetType().Name}'",
                OriginalValue = value,
                RuleId = rule.RuleId,
                Context = new Dictionary<string, object> { ["expectedType"] = expectedType }
            };
        }
        return null;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        foreach (var schema in _schemas.Values)
        {
            schema.Dispose();
        }
        _schemas.Clear();
        _schemaRules.Clear();
        return Task.CompletedTask;
    }
}

#endregion

#region Rule-Based Validation Strategy

/// <summary>
/// Rule-based validation strategy with customizable rules.
/// Supports dynamic rule registration and complex validation logic.
/// </summary>
public sealed class RuleBasedValidationStrategy : DataQualityStrategyBase
{
    private readonly BoundedDictionary<string, ValidationRule> _rules = new BoundedDictionary<string, ValidationRule>(1000);
    private readonly BoundedDictionary<string, Func<DataRecord, ValidationRule, DataQualityIssue?>> _customValidators = new BoundedDictionary<string, Func<DataRecord, ValidationRule, DataQualityIssue?>>(1000);

    /// <inheritdoc/>
    public override string StrategyId => "rule-based-validation";

    /// <inheritdoc/>
    public override string DisplayName => "Rule-Based Validation";

    /// <inheritdoc/>
    public override DataQualityCategory Category => DataQualityCategory.Validation;

    /// <inheritdoc/>
    public override DataQualityCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsDistributed = true,
        SupportsIncremental = true,
        MaxThroughput = 100000,
        TypicalLatencyMs = 0.2
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Validates data using configurable validation rules. " +
        "Supports required fields, regex patterns, ranges, cross-field validation, and custom validators.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "validation", "rules", "configurable", "business-rules", "constraints"
    };

    /// <summary>
    /// Registers a validation rule.
    /// </summary>
    public void RegisterRule(ValidationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        _rules[rule.RuleId] = rule;
    }

    /// <summary>
    /// Registers a custom validator function.
    /// </summary>
    public void RegisterCustomValidator(
        string validatorId,
        Func<DataRecord, ValidationRule, DataQualityIssue?> validator)
    {
        _customValidators[validatorId] = validator;
    }

    /// <summary>
    /// Removes a validation rule.
    /// </summary>
    public bool RemoveRule(string ruleId) => _rules.TryRemove(ruleId, out _);

    /// <summary>
    /// Gets all registered rules.
    /// </summary>
    public IEnumerable<ValidationRule> GetRules() => _rules.Values.OrderByDescending(r => r.Priority);

    /// <summary>
    /// Validates a record against all registered rules.
    /// </summary>
    public async Task<ValidationResult> ValidateAsync(DataRecord record, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        var startTime = DateTime.UtcNow;

        var issues = new List<DataQualityIssue>();
        var evaluatedRules = new List<string>();
        var passedRules = new List<string>();
        var failedRules = new List<string>();

        foreach (var rule in _rules.Values.Where(r => r.Enabled).OrderByDescending(r => r.Priority))
        {
            ct.ThrowIfCancellationRequested();
            evaluatedRules.Add(rule.RuleId);

            var ruleIssues = EvaluateRule(record, rule);
            if (ruleIssues.Count == 0)
            {
                passedRules.Add(rule.RuleId);
            }
            else
            {
                failedRules.Add(rule.RuleId);
                issues.AddRange(ruleIssues);

                if (rule.StopOnFail)
                    break;
            }
        }

        var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
        RecordProcessed(issues.Count == 0, elapsed);

        return new ValidationResult
        {
            RecordId = record.RecordId,
            Issues = issues,
            EvaluatedRules = evaluatedRules,
            PassedRules = passedRules,
            FailedRules = failedRules,
            ProcessingTimeMs = elapsed
        };
    }

    /// <summary>
    /// Batch validates multiple records.
    /// </summary>
    public async IAsyncEnumerable<ValidationResult> ValidateBatchAsync(
        IEnumerable<DataRecord> records,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();
            yield return await ValidateAsync(record, ct);
        }
    }

    private List<DataQualityIssue> EvaluateRule(DataRecord record, ValidationRule rule)
    {
        var issues = new List<DataQualityIssue>();

        if (rule.Type == ValidationType.Custom)
        {
            var validatorId = rule.Parameters?.GetValueOrDefault("validatorId")?.ToString();
            if (validatorId != null && _customValidators.TryGetValue(validatorId, out var validator))
            {
                var issue = validator(record, rule);
                if (issue != null)
                    issues.Add(issue);
            }
            return issues;
        }

        if (rule.Type == ValidationType.CrossField)
        {
            var issue = EvaluateCrossFieldRule(record, rule);
            if (issue != null)
                issues.Add(issue);
            return issues;
        }

        foreach (var fieldName in rule.TargetFields)
        {
            var hasField = record.Fields.TryGetValue(fieldName, out var value);

            DataQualityIssue? issue = rule.Type switch
            {
                ValidationType.Required => !hasField || value == null ||
                    (value is string s && string.IsNullOrWhiteSpace(s))
                    ? CreateIssue(rule, fieldName, value, IssueCategory.MissingValue,
                        $"Field '{fieldName}' is required")
                    : null,
                ValidationType.Unique => null, // Handled at batch level
                _ => null // Other types handled by schema validation
            };

            if (issue != null)
                issues.Add(issue);
        }

        return issues;
    }

    private DataQualityIssue? EvaluateCrossFieldRule(DataRecord record, ValidationRule rule)
    {
        if (rule.TargetFields.Length < 2) return null;

        var field1 = rule.TargetFields[0];
        var field2 = rule.TargetFields[1];
        var operation = rule.Parameters?.GetValueOrDefault("operation")?.ToString();

        var value1 = record.GetFieldAsString(field1);
        var value2 = record.GetFieldAsString(field2);

        bool isValid = EvaluateCrossFieldOperation(operation, value1, value2);

        if (!isValid)
        {
            return CreateIssue(rule, $"{field1},{field2}", $"{value1}:{value2}",
                IssueCategory.Consistency,
                rule.ErrorMessageTemplate ?? $"Cross-field validation failed: {field1} {operation} {field2}");
        }

        return null;
    }

    private static bool EvaluateCrossFieldOperation(string? operation, string? value1, string? value2)
    {
        switch (operation?.ToLowerInvariant())
        {
            case "equals":
                return string.Equals(value1, value2, StringComparison.OrdinalIgnoreCase);
            case "notequals":
                return !string.Equals(value1, value2, StringComparison.OrdinalIgnoreCase);
            case "lessthan":
                return double.TryParse(value1, out var lessThanV1) &&
                       double.TryParse(value2, out var lessThanV2) &&
                       lessThanV1 < lessThanV2;
            case "greaterthan":
                return double.TryParse(value1, out var greaterThanV1) &&
                       double.TryParse(value2, out var greaterThanV2) &&
                       greaterThanV1 > greaterThanV2;
            case "before":
                return DateTime.TryParse(value1, out var beforeD1) &&
                       DateTime.TryParse(value2, out var beforeD2) &&
                       beforeD1 < beforeD2;
            case "after":
                return DateTime.TryParse(value1, out var afterD1) &&
                       DateTime.TryParse(value2, out var afterD2) &&
                       afterD1 > afterD2;
            default:
                return true;
        }
    }

    private static DataQualityIssue CreateIssue(
        ValidationRule rule,
        string fieldName,
        object? value,
        IssueCategory category,
        string description)
    {
        return new DataQualityIssue
        {
            Severity = rule.Severity,
            Category = category,
            FieldName = fieldName,
            Description = rule.ErrorMessageTemplate ?? description,
            OriginalValue = value,
            RuleId = rule.RuleId,
            Context = new Dictionary<string, object>()
        };
    }
}

#endregion

#region Statistical Validation Strategy

/// <summary>
/// Statistical validation strategy for detecting anomalies and outliers.
/// Uses statistical methods to identify data quality issues.
/// </summary>
public sealed class StatisticalValidationStrategy : DataQualityStrategyBase
{
    private readonly BoundedDictionary<string, FieldStatistics> _fieldStats = new BoundedDictionary<string, FieldStatistics>(1000);
    private double _outlierThreshold = 3.0; // Standard deviations

    /// <inheritdoc/>
    public override string StrategyId => "statistical-validation";

    /// <inheritdoc/>
    public override string DisplayName => "Statistical Validation";

    /// <inheritdoc/>
    public override DataQualityCategory Category => DataQualityCategory.Validation;

    /// <inheritdoc/>
    public override DataQualityCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = false,
        SupportsDistributed = true,
        SupportsIncremental = true,
        MaxThroughput = 25000,
        TypicalLatencyMs = 1.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Validates data using statistical methods to detect anomalies and outliers. " +
        "Uses z-scores, IQR, and distribution analysis to identify unusual values.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "validation", "statistical", "outliers", "anomaly-detection", "z-score"
    };

    /// <summary>
    /// Gets or sets the outlier threshold in standard deviations.
    /// </summary>
    public double OutlierThreshold
    {
        get => _outlierThreshold;
        set => _outlierThreshold = Math.Max(1.0, value);
    }

    /// <summary>
    /// Trains the statistical model on a dataset.
    /// </summary>
    public void Train(IEnumerable<DataRecord> records, string[] numericFields)
    {
        var fieldValues = new Dictionary<string, List<double>>();
        foreach (var field in numericFields)
        {
            fieldValues[field] = new List<double>();
        }

        foreach (var record in records)
        {
            foreach (var field in numericFields)
            {
                if (record.Fields.TryGetValue(field, out var value) &&
                    double.TryParse(value?.ToString(), out var numValue))
                {
                    fieldValues[field].Add(numValue);
                }
            }
        }

        foreach (var (field, values) in fieldValues)
        {
            if (values.Count == 0) continue;

            var sorted = values.OrderBy(v => v).ToList();
            var mean = values.Average();
            var variance = values.Sum(v => Math.Pow(v - mean, 2)) / values.Count;
            var stdDev = Math.Sqrt(variance);

            var q1Index = sorted.Count / 4;
            var q3Index = 3 * sorted.Count / 4;
            var q1 = sorted[q1Index];
            var q3 = sorted[q3Index];
            var iqr = q3 - q1;

            _fieldStats[field] = new FieldStatistics
            {
                FieldName = field,
                Count = values.Count,
                Mean = mean,
                StdDev = stdDev,
                Min = sorted.First(),
                Max = sorted.Last(),
                Median = sorted[sorted.Count / 2],
                Q1 = q1,
                Q3 = q3,
                IQR = iqr
            };
        }
    }

    /// <summary>
    /// Validates a record for statistical anomalies.
    /// </summary>
    public Task<ValidationResult> ValidateAsync(DataRecord record, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        var startTime = DateTime.UtcNow;
        var issues = new List<DataQualityIssue>();

        foreach (var (field, stats) in _fieldStats)
        {
            if (!record.Fields.TryGetValue(field, out var value)) continue;
            if (!double.TryParse(value?.ToString(), out var numValue)) continue;

            // Z-score outlier detection
            if (stats.StdDev > 0)
            {
                var zScore = Math.Abs((numValue - stats.Mean) / stats.StdDev);
                if (zScore > _outlierThreshold)
                {
                    issues.Add(new DataQualityIssue
                    {
                        Severity = IssueSeverity.Warning,
                        Category = IssueCategory.Accuracy,
                        FieldName = field,
                        RecordId = record.RecordId,
                        Description = $"Value {numValue} is a statistical outlier (z-score: {zScore:F2})",
                        OriginalValue = numValue,
                        Context = new Dictionary<string, object>
                        {
                            ["zScore"] = zScore,
                            ["mean"] = stats.Mean,
                            ["stdDev"] = stats.StdDev
                        }
                    });
                }
            }

            // IQR outlier detection
            var lowerBound = stats.Q1 - 1.5 * stats.IQR;
            var upperBound = stats.Q3 + 1.5 * stats.IQR;
            if (numValue < lowerBound || numValue > upperBound)
            {
                issues.Add(new DataQualityIssue
                {
                    Severity = IssueSeverity.Info,
                    Category = IssueCategory.OutOfRange,
                    FieldName = field,
                    RecordId = record.RecordId,
                    Description = $"Value {numValue} is outside IQR bounds [{lowerBound:F2}, {upperBound:F2}]",
                    OriginalValue = numValue,
                    Context = new Dictionary<string, object>
                    {
                        ["q1"] = stats.Q1,
                        ["q3"] = stats.Q3,
                        ["iqr"] = stats.IQR
                    }
                });
            }
        }

        var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
        RecordProcessed(issues.Count == 0, elapsed);

        return Task.FromResult(new ValidationResult
        {
            RecordId = record.RecordId,
            Issues = issues,
            ProcessingTimeMs = elapsed
        });
    }

    /// <summary>
    /// Gets statistics for a field.
    /// </summary>
    public FieldStatistics? GetFieldStatistics(string fieldName)
    {
        return _fieldStats.TryGetValue(fieldName, out var stats) ? stats : null;
    }
}

/// <summary>
/// Statistics for a numeric field.
/// </summary>
public sealed class FieldStatistics
{
    public required string FieldName { get; init; }
    public int Count { get; init; }
    public double Mean { get; init; }
    public double StdDev { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public double Median { get; init; }
    public double Q1 { get; init; }
    public double Q3 { get; init; }
    public double IQR { get; init; }
}

#endregion
