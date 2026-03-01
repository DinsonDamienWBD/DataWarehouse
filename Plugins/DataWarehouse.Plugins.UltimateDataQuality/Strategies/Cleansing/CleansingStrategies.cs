using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.UltimateDataQuality.Strategies.Cleansing;

#region Cleansing Types

/// <summary>
/// Result of a cleansing operation.
/// </summary>
public sealed class CleansingResult
{
    /// <summary>
    /// Record ID that was cleansed.
    /// </summary>
    public required string RecordId { get; init; }

    /// <summary>
    /// Whether any changes were made.
    /// </summary>
    public bool WasModified => Modifications.Count > 0;

    /// <summary>
    /// Original record.
    /// </summary>
    public required DataRecord OriginalRecord { get; init; }

    /// <summary>
    /// Cleansed record.
    /// </summary>
    public required DataRecord CleansedRecord { get; init; }

    /// <summary>
    /// List of modifications made.
    /// </summary>
    public List<FieldModification> Modifications { get; init; } = new();

    /// <summary>
    /// Processing time in milliseconds.
    /// </summary>
    public double ProcessingTimeMs { get; init; }
}

/// <summary>
/// Represents a modification to a field.
/// </summary>
public sealed class FieldModification
{
    /// <summary>
    /// Field name that was modified.
    /// </summary>
    public required string FieldName { get; init; }

    /// <summary>
    /// Type of modification.
    /// </summary>
    public required ModificationType Type { get; init; }

    /// <summary>
    /// Original value.
    /// </summary>
    public object? OriginalValue { get; init; }

    /// <summary>
    /// New value after modification.
    /// </summary>
    public object? NewValue { get; init; }

    /// <summary>
    /// Description of the modification.
    /// </summary>
    public string? Description { get; init; }
}

/// <summary>
/// Type of modification performed.
/// </summary>
public enum ModificationType
{
    /// <summary>Trimmed whitespace.</summary>
    Trimmed,
    /// <summary>Normalized case.</summary>
    CaseNormalized,
    /// <summary>Removed special characters.</summary>
    SpecialCharsRemoved,
    /// <summary>Formatted value.</summary>
    Formatted,
    /// <summary>Replaced value.</summary>
    Replaced,
    /// <summary>Set default value.</summary>
    DefaultSet,
    /// <summary>Normalized encoding.</summary>
    EncodingNormalized,
    /// <summary>Fixed typo.</summary>
    TypoCorrected,
    /// <summary>Standardized abbreviation.</summary>
    AbbreviationExpanded,
    /// <summary>Removed duplicates in list.</summary>
    DeduplicatedList,
    /// <summary>Null replaced with default.</summary>
    NullReplaced,
    /// <summary>Invalid value removed.</summary>
    InvalidRemoved
}

/// <summary>
/// Cleansing rule configuration.
/// </summary>
public sealed class CleansingRule
{
    /// <summary>
    /// Rule identifier.
    /// </summary>
    public required string RuleId { get; init; }

    /// <summary>
    /// Target field name pattern (supports wildcards).
    /// </summary>
    public required string FieldPattern { get; init; }

    /// <summary>
    /// Cleansing operation to perform.
    /// </summary>
    public required CleansingOperation Operation { get; init; }

    /// <summary>
    /// Operation parameters.
    /// </summary>
    public Dictionary<string, object>? Parameters { get; init; }

    /// <summary>
    /// Whether the rule is enabled.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Priority (higher = applied first).
    /// </summary>
    public int Priority { get; init; } = 100;
}

/// <summary>
/// Cleansing operations.
/// </summary>
public enum CleansingOperation
{
    /// <summary>Trim whitespace.</summary>
    Trim,
    /// <summary>Convert to uppercase.</summary>
    ToUpper,
    /// <summary>Convert to lowercase.</summary>
    ToLower,
    /// <summary>Convert to title case.</summary>
    ToTitleCase,
    /// <summary>Remove special characters.</summary>
    RemoveSpecialChars,
    /// <summary>Remove digits.</summary>
    RemoveDigits,
    /// <summary>Remove non-digits.</summary>
    KeepDigitsOnly,
    /// <summary>Normalize whitespace.</summary>
    NormalizeWhitespace,
    /// <summary>Replace value.</summary>
    Replace,
    /// <summary>Apply regex replacement.</summary>
    RegexReplace,
    /// <summary>Set default if null/empty.</summary>
    SetDefault,
    /// <summary>Truncate to max length.</summary>
    Truncate,
    /// <summary>Pad to min length.</summary>
    Pad,
    /// <summary>Format phone number.</summary>
    FormatPhone,
    /// <summary>Format email.</summary>
    FormatEmail,
    /// <summary>Format date.</summary>
    FormatDate,
    /// <summary>Format currency.</summary>
    FormatCurrency,
    /// <summary>Remove HTML tags.</summary>
    RemoveHtml,
    /// <summary>Decode HTML entities.</summary>
    DecodeHtml,
    /// <summary>Normalize unicode.</summary>
    NormalizeUnicode,
    /// <summary>Remove accents/diacritics.</summary>
    RemoveAccents,
    /// <summary>Custom function.</summary>
    Custom
}

#endregion

#region Basic Cleansing Strategy

/// <summary>
/// Basic cleansing strategy for common data cleanup operations.
/// </summary>
public sealed class BasicCleansingStrategy : DataQualityStrategyBase
{
    private readonly List<CleansingRule> _rules = new();
    private readonly Dictionary<string, Func<object?, Dictionary<string, object>?, object?>> _customOperations = new();

    /// <inheritdoc/>
    public override string StrategyId => "basic-cleansing";

    /// <inheritdoc/>
    public override string DisplayName => "Basic Data Cleansing";

    /// <inheritdoc/>
    public override DataQualityCategory Category => DataQualityCategory.Cleansing;

    /// <inheritdoc/>
    public override DataQualityCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsDistributed = true,
        SupportsIncremental = true,
        MaxThroughput = 100000,
        TypicalLatencyMs = 0.3
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Performs basic data cleansing including trimming, case normalization, " +
        "special character removal, and format standardization.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "cleansing", "cleaning", "trim", "normalize", "format", "sanitize"
    };

    /// <summary>
    /// Adds a cleansing rule.
    /// </summary>
    public void AddRule(CleansingRule rule) => _rules.Add(rule);

    /// <summary>
    /// Removes a cleansing rule.
    /// </summary>
    public bool RemoveRule(string ruleId) => _rules.RemoveAll(r => r.RuleId == ruleId) > 0;

    /// <summary>
    /// Registers a custom cleansing operation.
    /// </summary>
    public void RegisterCustomOperation(
        string operationId,
        Func<object?, Dictionary<string, object>?, object?> operation)
    {
        _customOperations[operationId] = operation;
    }

    /// <summary>
    /// Cleanses a record according to configured rules.
    /// </summary>
    public Task<CleansingResult> CleanseAsync(DataRecord record, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        var startTime = DateTime.UtcNow;

        var modifications = new List<FieldModification>();
        var cleansedFields = new Dictionary<string, object?>(record.Fields);

        foreach (var (fieldName, value) in record.Fields)
        {
            ct.ThrowIfCancellationRequested();

            var applicableRules = _rules
                .Where(r => r.Enabled && MatchesPattern(fieldName, r.FieldPattern))
                .OrderByDescending(r => r.Priority)
                .ToList();

            var currentValue = value;
            foreach (var rule in applicableRules)
            {
                var (newValue, modified) = ApplyOperation(currentValue, rule);
                if (modified)
                {
                    modifications.Add(new FieldModification
                    {
                        FieldName = fieldName,
                        Type = GetModificationType(rule.Operation),
                        OriginalValue = currentValue,
                        NewValue = newValue,
                        Description = $"Applied {rule.Operation} from rule {rule.RuleId}"
                    });
                    currentValue = newValue;
                }
            }

            cleansedFields[fieldName] = currentValue;
        }

        var cleansedRecord = new DataRecord
        {
            RecordId = record.RecordId,
            SourceId = record.SourceId,
            Fields = cleansedFields,
            Metadata = record.Metadata,
            CreatedAt = record.CreatedAt,
            ModifiedAt = DateTime.UtcNow
        };

        var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
        RecordProcessed(true, elapsed);
        if (modifications.Count > 0)
        {
            RecordCorrected();
        }

        return Task.FromResult(new CleansingResult
        {
            RecordId = record.RecordId,
            OriginalRecord = record,
            CleansedRecord = cleansedRecord,
            Modifications = modifications,
            ProcessingTimeMs = elapsed
        });
    }

    /// <summary>
    /// Batch cleanses multiple records.
    /// </summary>
    public async IAsyncEnumerable<CleansingResult> CleanseBatchAsync(
        IEnumerable<DataRecord> records,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();
            yield return await CleanseAsync(record, ct);
        }
    }

    private bool MatchesPattern(string fieldName, string pattern)
    {
        if (pattern == "*") return true;
        if (pattern.EndsWith("*"))
        {
            return fieldName.StartsWith(pattern.TrimEnd('*'), StringComparison.OrdinalIgnoreCase);
        }
        if (pattern.StartsWith("*"))
        {
            return fieldName.EndsWith(pattern.TrimStart('*'), StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(fieldName, pattern, StringComparison.OrdinalIgnoreCase);
    }

    private (object? value, bool modified) ApplyOperation(object? value, CleansingRule rule)
    {
        if (value == null && rule.Operation != CleansingOperation.SetDefault)
        {
            return (null, false);
        }

        var strValue = value?.ToString() ?? "";
        object? newValue = rule.Operation switch
        {
            CleansingOperation.Trim => strValue.Trim(),
            CleansingOperation.ToUpper => strValue.ToUpperInvariant(),
            CleansingOperation.ToLower => strValue.ToLowerInvariant(),
            CleansingOperation.ToTitleCase => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(strValue.ToLower()),
            CleansingOperation.RemoveSpecialChars => Regex.Replace(strValue, @"[^a-zA-Z0-9\s]", ""),
            CleansingOperation.RemoveDigits => Regex.Replace(strValue, @"\d", ""),
            CleansingOperation.KeepDigitsOnly => Regex.Replace(strValue, @"[^\d]", ""),
            CleansingOperation.NormalizeWhitespace => Regex.Replace(strValue.Trim(), @"\s+", " "),
            CleansingOperation.Replace => ApplyReplace(strValue, rule.Parameters),
            CleansingOperation.RegexReplace => ApplyRegexReplace(strValue, rule.Parameters),
            CleansingOperation.SetDefault => string.IsNullOrWhiteSpace(strValue)
                ? rule.Parameters?.GetValueOrDefault("default")
                : value,
            CleansingOperation.Truncate => ApplyTruncate(strValue, rule.Parameters),
            CleansingOperation.Pad => ApplyPad(strValue, rule.Parameters),
            CleansingOperation.FormatPhone => FormatPhoneNumber(strValue),
            CleansingOperation.FormatEmail => strValue.Trim().ToLowerInvariant(),
            CleansingOperation.FormatDate => FormatDate(strValue, rule.Parameters),
            CleansingOperation.FormatCurrency => FormatCurrency(strValue, rule.Parameters),
            CleansingOperation.RemoveHtml => Regex.Replace(strValue, @"<[^>]+>", ""),
            CleansingOperation.DecodeHtml => System.Net.WebUtility.HtmlDecode(strValue),
            CleansingOperation.NormalizeUnicode => strValue.Normalize(NormalizationForm.FormC),
            CleansingOperation.RemoveAccents => RemoveAccents(strValue),
            CleansingOperation.Custom => ApplyCustomOperation(value, rule.Parameters),
            _ => value
        };

        var modified = !Equals(value?.ToString(), newValue?.ToString());
        return (newValue, modified);
    }

    private object? ApplyReplace(string value, Dictionary<string, object>? parameters)
    {
        var find = parameters?.GetValueOrDefault("find")?.ToString() ?? "";
        var replace = parameters?.GetValueOrDefault("replace")?.ToString() ?? "";
        return value.Replace(find, replace);
    }

    private object? ApplyRegexReplace(string value, Dictionary<string, object>? parameters)
    {
        var pattern = parameters?.GetValueOrDefault("pattern")?.ToString() ?? "";
        var replace = parameters?.GetValueOrDefault("replace")?.ToString() ?? "";
        try
        {
            return Regex.Replace(value, pattern, replace);
        }
        catch
        {
            return value;
        }
    }

    private object? ApplyTruncate(string value, Dictionary<string, object>? parameters)
    {
        var maxLength = parameters?.GetValueOrDefault("maxLength") is int len ? len : 255;
        return value.Length > maxLength ? value.Substring(0, maxLength) : value;
    }

    private object? ApplyPad(string value, Dictionary<string, object>? parameters)
    {
        var minLength = parameters?.GetValueOrDefault("minLength") is int len ? len : 0;
        var padChar = parameters?.GetValueOrDefault("padChar")?.ToString()?[0] ?? ' ';
        var padLeft = parameters?.GetValueOrDefault("padLeft") is bool left && left;

        if (value.Length >= minLength) return value;
        return padLeft
            ? value.PadLeft(minLength, padChar)
            : value.PadRight(minLength, padChar);
    }

    private static string FormatPhoneNumber(string value)
    {
        var digits = Regex.Replace(value, @"[^\d]", "");
        if (digits.Length == 10)
        {
            return $"({digits.Substring(0, 3)}) {digits.Substring(3, 3)}-{digits.Substring(6)}";
        }
        if (digits.Length == 11 && digits.StartsWith("1"))
        {
            return $"+1 ({digits.Substring(1, 3)}) {digits.Substring(4, 3)}-{digits.Substring(7)}";
        }
        return value;
    }

    private static object? FormatDate(string value, Dictionary<string, object>? parameters)
    {
        var format = parameters?.GetValueOrDefault("format")?.ToString() ?? "yyyy-MM-dd";
        if (DateTime.TryParse(value, out var date))
        {
            return date.ToString(format);
        }
        return value;
    }

    private static object? FormatCurrency(string value, Dictionary<string, object>? parameters)
    {
        var culture = parameters?.GetValueOrDefault("culture")?.ToString() ?? "en-US";
        if (decimal.TryParse(Regex.Replace(value, @"[^\d.\-]", ""), out var amount))
        {
            return amount.ToString("C", new CultureInfo(culture));
        }
        return value;
    }

    private static string RemoveAccents(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private object? ApplyCustomOperation(object? value, Dictionary<string, object>? parameters)
    {
        var operationId = parameters?.GetValueOrDefault("operationId")?.ToString();
        if (operationId != null && _customOperations.TryGetValue(operationId, out var operation))
        {
            return operation(value, parameters);
        }
        return value;
    }

    private static ModificationType GetModificationType(CleansingOperation operation)
    {
        return operation switch
        {
            CleansingOperation.Trim or CleansingOperation.NormalizeWhitespace => ModificationType.Trimmed,
            CleansingOperation.ToUpper or CleansingOperation.ToLower or CleansingOperation.ToTitleCase => ModificationType.CaseNormalized,
            CleansingOperation.RemoveSpecialChars or CleansingOperation.RemoveDigits or CleansingOperation.RemoveHtml => ModificationType.SpecialCharsRemoved,
            CleansingOperation.FormatPhone or CleansingOperation.FormatEmail or CleansingOperation.FormatDate or CleansingOperation.FormatCurrency => ModificationType.Formatted,
            CleansingOperation.Replace or CleansingOperation.RegexReplace => ModificationType.Replaced,
            CleansingOperation.SetDefault => ModificationType.DefaultSet,
            CleansingOperation.NormalizeUnicode or CleansingOperation.RemoveAccents or CleansingOperation.DecodeHtml => ModificationType.EncodingNormalized,
            _ => ModificationType.Replaced
        };
    }
}

#endregion

#region Semantic Cleansing Strategy

/// <summary>
/// Semantic cleansing strategy for intelligent data correction.
/// Uses patterns and dictionaries for typo correction and standardization.
/// </summary>
public sealed class SemanticCleansingStrategy : DataQualityStrategyBase
{
    private readonly Dictionary<string, Dictionary<string, string>> _fieldDictionaries = new();
    private readonly Dictionary<string, List<string>> _validValues = new();
    private readonly Dictionary<string, string> _abbreviations = new();

    /// <inheritdoc/>
    public override string StrategyId => "semantic-cleansing";

    /// <inheritdoc/>
    public override string DisplayName => "Semantic Data Cleansing";

    /// <inheritdoc/>
    public override DataQualityCategory Category => DataQualityCategory.Cleansing;

    /// <inheritdoc/>
    public override DataQualityCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = false,
        SupportsDistributed = true,
        SupportsIncremental = true,
        MaxThroughput = 20000,
        TypicalLatencyMs = 2.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Performs intelligent semantic cleansing including typo correction, " +
        "abbreviation expansion, and value standardization using dictionaries.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "cleansing", "semantic", "typo", "correction", "standardization", "dictionary"
    };

    /// <summary>
    /// Registers a correction dictionary for a field.
    /// </summary>
    public void RegisterDictionary(string fieldName, Dictionary<string, string> corrections)
    {
        _fieldDictionaries[fieldName] = corrections;
    }

    /// <summary>
    /// Registers valid values for a field (for fuzzy matching).
    /// </summary>
    public void RegisterValidValues(string fieldName, IEnumerable<string> validValues)
    {
        _validValues[fieldName] = validValues.ToList();
    }

    /// <summary>
    /// Registers common abbreviations for expansion.
    /// </summary>
    public void RegisterAbbreviations(Dictionary<string, string> abbreviations)
    {
        foreach (var (abbr, full) in abbreviations)
        {
            _abbreviations[abbr.ToUpperInvariant()] = full;
        }
    }

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        // Register common abbreviations
        RegisterAbbreviations(new Dictionary<string, string>
        {
            ["ST"] = "Street",
            ["AVE"] = "Avenue",
            ["BLVD"] = "Boulevard",
            ["RD"] = "Road",
            ["DR"] = "Drive",
            ["LN"] = "Lane",
            ["CT"] = "Court",
            ["PL"] = "Place",
            ["APT"] = "Apartment",
            ["STE"] = "Suite",
            ["N"] = "North",
            ["S"] = "South",
            ["E"] = "East",
            ["W"] = "West",
            ["NE"] = "Northeast",
            ["NW"] = "Northwest",
            ["SE"] = "Southeast",
            ["SW"] = "Southwest"
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Cleanses a record with semantic corrections.
    /// </summary>
    public Task<CleansingResult> CleanseAsync(DataRecord record, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        var startTime = DateTime.UtcNow;

        var modifications = new List<FieldModification>();
        var cleansedFields = new Dictionary<string, object?>(record.Fields);

        foreach (var (fieldName, value) in record.Fields)
        {
            ct.ThrowIfCancellationRequested();

            if (value == null) continue;
            var strValue = value.ToString() ?? "";

            // Apply dictionary corrections
            if (_fieldDictionaries.TryGetValue(fieldName, out var dictionary))
            {
                if (dictionary.TryGetValue(strValue, out var corrected))
                {
                    modifications.Add(new FieldModification
                    {
                        FieldName = fieldName,
                        Type = ModificationType.TypoCorrected,
                        OriginalValue = strValue,
                        NewValue = corrected,
                        Description = "Dictionary correction"
                    });
                    cleansedFields[fieldName] = corrected;
                    continue;
                }
            }

            // Fuzzy match against valid values
            if (_validValues.TryGetValue(fieldName, out var validList))
            {
                var match = FindClosestMatch(strValue, validList);
                if (match != null && match != strValue)
                {
                    modifications.Add(new FieldModification
                    {
                        FieldName = fieldName,
                        Type = ModificationType.TypoCorrected,
                        OriginalValue = strValue,
                        NewValue = match,
                        Description = "Fuzzy match correction"
                    });
                    cleansedFields[fieldName] = match;
                    continue;
                }
            }

            // Expand abbreviations
            var expanded = ExpandAbbreviations(strValue);
            if (expanded != strValue)
            {
                modifications.Add(new FieldModification
                {
                    FieldName = fieldName,
                    Type = ModificationType.AbbreviationExpanded,
                    OriginalValue = strValue,
                    NewValue = expanded,
                    Description = "Abbreviation expansion"
                });
                cleansedFields[fieldName] = expanded;
            }
        }

        var cleansedRecord = new DataRecord
        {
            RecordId = record.RecordId,
            SourceId = record.SourceId,
            Fields = cleansedFields,
            Metadata = record.Metadata,
            CreatedAt = record.CreatedAt,
            ModifiedAt = DateTime.UtcNow
        };

        var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
        RecordProcessed(true, elapsed);
        if (modifications.Count > 0)
        {
            RecordCorrected();
        }

        return Task.FromResult(new CleansingResult
        {
            RecordId = record.RecordId,
            OriginalRecord = record,
            CleansedRecord = cleansedRecord,
            Modifications = modifications,
            ProcessingTimeMs = elapsed
        });
    }

    private string? FindClosestMatch(string value, List<string> validValues)
    {
        if (string.IsNullOrWhiteSpace(value) || validValues.Count == 0)
            return null;

        var valueLower = value.ToLowerInvariant();
        var threshold = Math.Max(1, value.Length / 4); // Allow ~25% difference

        string? bestMatch = null;
        var bestDistance = int.MaxValue;

        foreach (var valid in validValues)
        {
            var validLower = valid.ToLowerInvariant();
            if (valueLower == validLower)
                return valid; // Exact match

            // Early length filter: skip values that differ in length by more than the threshold.
            if (Math.Abs(valueLower.Length - validLower.Length) > threshold)
                continue;

            var distance = LevenshteinDistance(valueLower, validLower);
            if (distance <= threshold && distance < bestDistance)
            {
                bestDistance = distance;
                bestMatch = valid;
                if (bestDistance == 1) break; // Can't do better than 1 edit
            }
        }

        return bestMatch;
    }

    private static int LevenshteinDistance(string s1, string s2)
    {
        var m = s1.Length;
        var n = s2.Length;
        var d = new int[m + 1, n + 1];

        for (var i = 0; i <= m; i++) d[i, 0] = i;
        for (var j = 0; j <= n; j++) d[0, j] = j;

        for (var i = 1; i <= m; i++)
        {
            for (var j = 1; j <= n; j++)
            {
                var cost = s1[i - 1] == s2[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[m, n];
    }

    private string ExpandAbbreviations(string value)
    {
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var expanded = new List<string>();

        foreach (var word in words)
        {
            var wordUpper = word.ToUpperInvariant().TrimEnd('.', ',');
            expanded.Add(_abbreviations.TryGetValue(wordUpper, out var full) ? full : word);
        }

        return string.Join(" ", expanded);
    }
}

#endregion

#region Null Handling Strategy

/// <summary>
/// Strategy for handling null and missing values.
/// </summary>
public sealed class NullHandlingStrategy : DataQualityStrategyBase
{
    private readonly Dictionary<string, object> _defaultValues = new();
    private readonly Dictionary<string, NullHandlingMode> _fieldModes = new();
    private NullHandlingMode _defaultMode = NullHandlingMode.SetDefault;

    /// <inheritdoc/>
    public override string StrategyId => "null-handling";

    /// <inheritdoc/>
    public override string DisplayName => "Null Value Handling";

    /// <inheritdoc/>
    public override DataQualityCategory Category => DataQualityCategory.Cleansing;

    /// <inheritdoc/>
    public override DataQualityCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsDistributed = true,
        SupportsIncremental = true,
        MaxThroughput = 200000,
        TypicalLatencyMs = 0.1
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Handles null and missing values with configurable strategies " +
        "including default values, imputation, and removal.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "cleansing", "null", "missing", "imputation", "default"
    };

    /// <summary>
    /// Sets the default value for a field.
    /// </summary>
    public void SetDefault(string fieldName, object defaultValue)
    {
        _defaultValues[fieldName] = defaultValue;
    }

    /// <summary>
    /// Sets the null handling mode for a field.
    /// </summary>
    public void SetMode(string fieldName, NullHandlingMode mode)
    {
        _fieldModes[fieldName] = mode;
    }

    /// <summary>
    /// Sets the global default mode.
    /// </summary>
    public void SetDefaultMode(NullHandlingMode mode)
    {
        _defaultMode = mode;
    }

    /// <summary>
    /// Handles null values in a record.
    /// </summary>
    public Task<CleansingResult> HandleNullsAsync(DataRecord record, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        var startTime = DateTime.UtcNow;

        var modifications = new List<FieldModification>();
        var cleansedFields = new Dictionary<string, object?>(record.Fields);

        foreach (var (fieldName, value) in record.Fields)
        {
            ct.ThrowIfCancellationRequested();

            if (!IsNullOrEmpty(value)) continue;

            var mode = _fieldModes.TryGetValue(fieldName, out var m) ? m : _defaultMode;

            switch (mode)
            {
                case NullHandlingMode.SetDefault:
                    if (_defaultValues.TryGetValue(fieldName, out var defaultValue))
                    {
                        modifications.Add(new FieldModification
                        {
                            FieldName = fieldName,
                            Type = ModificationType.NullReplaced,
                            OriginalValue = value,
                            NewValue = defaultValue,
                            Description = "Set default value"
                        });
                        cleansedFields[fieldName] = defaultValue;
                    }
                    break;

                case NullHandlingMode.SetEmptyString:
                    modifications.Add(new FieldModification
                    {
                        FieldName = fieldName,
                        Type = ModificationType.NullReplaced,
                        OriginalValue = value,
                        NewValue = "",
                        Description = "Set empty string"
                    });
                    cleansedFields[fieldName] = "";
                    break;

                case NullHandlingMode.SetZero:
                    modifications.Add(new FieldModification
                    {
                        FieldName = fieldName,
                        Type = ModificationType.NullReplaced,
                        OriginalValue = value,
                        NewValue = 0,
                        Description = "Set zero"
                    });
                    cleansedFields[fieldName] = 0;
                    break;

                case NullHandlingMode.Remove:
                    cleansedFields.Remove(fieldName);
                    modifications.Add(new FieldModification
                    {
                        FieldName = fieldName,
                        Type = ModificationType.InvalidRemoved,
                        OriginalValue = value,
                        NewValue = null,
                        Description = "Removed null field"
                    });
                    break;

                case NullHandlingMode.Skip:
                    // Leave as is
                    break;
            }
        }

        var cleansedRecord = new DataRecord
        {
            RecordId = record.RecordId,
            SourceId = record.SourceId,
            Fields = cleansedFields,
            Metadata = record.Metadata,
            CreatedAt = record.CreatedAt,
            ModifiedAt = DateTime.UtcNow
        };

        var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
        RecordProcessed(true, elapsed);
        if (modifications.Count > 0)
        {
            RecordCorrected();
        }

        return Task.FromResult(new CleansingResult
        {
            RecordId = record.RecordId,
            OriginalRecord = record,
            CleansedRecord = cleansedRecord,
            Modifications = modifications,
            ProcessingTimeMs = elapsed
        });
    }

    private static bool IsNullOrEmpty(object? value)
    {
        return value == null || (value is string s && string.IsNullOrWhiteSpace(s));
    }
}

/// <summary>
/// Mode for handling null values.
/// </summary>
public enum NullHandlingMode
{
    /// <summary>Skip null handling.</summary>
    Skip,
    /// <summary>Set a default value.</summary>
    SetDefault,
    /// <summary>Set empty string.</summary>
    SetEmptyString,
    /// <summary>Set zero for numeric fields.</summary>
    SetZero,
    /// <summary>Remove the field.</summary>
    Remove
}

#endregion
