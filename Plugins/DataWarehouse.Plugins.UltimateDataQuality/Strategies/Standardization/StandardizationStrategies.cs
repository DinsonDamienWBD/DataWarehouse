using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.UltimateDataQuality.Strategies.Standardization;

#region Standardization Types

/// <summary>
/// Result of standardization operation.
/// </summary>
public sealed class StandardizationResult
{
    /// <summary>
    /// Record ID that was standardized.
    /// </summary>
    public required string RecordId { get; init; }

    /// <summary>
    /// Whether any changes were made.
    /// </summary>
    public bool WasModified => StandardizedFields.Count > 0;

    /// <summary>
    /// Original record.
    /// </summary>
    public required DataRecord OriginalRecord { get; init; }

    /// <summary>
    /// Standardized record.
    /// </summary>
    public required DataRecord StandardizedRecord { get; init; }

    /// <summary>
    /// Fields that were standardized.
    /// </summary>
    public Dictionary<string, StandardizedValue> StandardizedFields { get; init; } = new();

    /// <summary>
    /// Processing time in milliseconds.
    /// </summary>
    public double ProcessingTimeMs { get; init; }
}

/// <summary>
/// A standardized value with before/after.
/// </summary>
public sealed class StandardizedValue
{
    public object? OriginalValue { get; init; }
    public object? StandardizedValue_ { get; init; }
    public required string StandardType { get; init; }
}

/// <summary>
/// Standard format specification.
/// </summary>
public sealed class StandardFormat
{
    /// <summary>
    /// Format identifier.
    /// </summary>
    public required string FormatId { get; init; }

    /// <summary>
    /// Target field pattern.
    /// </summary>
    public required string FieldPattern { get; init; }

    /// <summary>
    /// Type of standardization.
    /// </summary>
    public required StandardizationType Type { get; init; }

    /// <summary>
    /// Format parameters.
    /// </summary>
    public Dictionary<string, object>? Parameters { get; init; }

    /// <summary>
    /// Priority (higher = applied first).
    /// </summary>
    public int Priority { get; init; } = 100;
}

/// <summary>
/// Types of standardization.
/// </summary>
public enum StandardizationType
{
    /// <summary>Date format standardization.</summary>
    Date,
    /// <summary>DateTime format standardization.</summary>
    DateTime,
    /// <summary>Phone number format.</summary>
    Phone,
    /// <summary>Address format.</summary>
    Address,
    /// <summary>Name format.</summary>
    Name,
    /// <summary>Email format.</summary>
    Email,
    /// <summary>Currency format.</summary>
    Currency,
    /// <summary>Numeric format.</summary>
    Numeric,
    /// <summary>Boolean format.</summary>
    Boolean,
    /// <summary>UUID format.</summary>
    Uuid,
    /// <summary>Country code format.</summary>
    CountryCode,
    /// <summary>State/Province format.</summary>
    StateProvince,
    /// <summary>Postal code format.</summary>
    PostalCode,
    /// <summary>URL format.</summary>
    Url,
    /// <summary>Custom format.</summary>
    Custom
}

#endregion

#region Format Standardization Strategy

/// <summary>
/// Format standardization strategy for consistent data formats.
/// </summary>
public sealed class FormatStandardizationStrategy : DataQualityStrategyBase
{
    private readonly List<StandardFormat> _formats = new();
    private readonly Dictionary<string, Func<object?, Dictionary<string, object>?, object?>> _customFormatters = new();

    /// <inheritdoc/>
    public override string StrategyId => "format-standardization";

    /// <inheritdoc/>
    public override string DisplayName => "Format Standardization";

    /// <inheritdoc/>
    public override DataQualityCategory Category => DataQualityCategory.Standardization;

    /// <inheritdoc/>
    public override DataQualityCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsDistributed = true,
        SupportsIncremental = true,
        MaxThroughput = 80000,
        TypicalLatencyMs = 0.5
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Standardizes data formats including dates, phones, addresses, and more. " +
        "Ensures consistent formatting across all records.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "standardization", "format", "normalization", "consistency"
    };

    /// <summary>
    /// Adds a format specification.
    /// </summary>
    public void AddFormat(StandardFormat format) => _formats.Add(format);

    /// <summary>
    /// Registers a custom formatter.
    /// </summary>
    public void RegisterCustomFormatter(
        string formatterId,
        Func<object?, Dictionary<string, object>?, object?> formatter)
    {
        _customFormatters[formatterId] = formatter;
    }

    /// <summary>
    /// Standardizes a record.
    /// </summary>
    public Task<StandardizationResult> StandardizeAsync(
        DataRecord record,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        var startTime = DateTime.UtcNow;

        var standardizedFields = new Dictionary<string, StandardizedValue>();
        var newFields = new Dictionary<string, object?>(record.Fields);

        foreach (var (fieldName, value) in record.Fields)
        {
            ct.ThrowIfCancellationRequested();

            var applicableFormats = _formats
                .Where(f => MatchesPattern(fieldName, f.FieldPattern))
                .OrderByDescending(f => f.Priority)
                .ToList();

            foreach (var format in applicableFormats)
            {
                var standardized = ApplyFormat(value, format);
                if (!Equals(value?.ToString(), standardized?.ToString()))
                {
                    standardizedFields[fieldName] = new StandardizedValue
                    {
                        OriginalValue = value,
                        StandardizedValue_ = standardized,
                        StandardType = format.Type.ToString()
                    };
                    newFields[fieldName] = standardized;
                    break; // Only apply first matching format
                }
            }
        }

        var standardizedRecord = new DataRecord
        {
            RecordId = record.RecordId,
            SourceId = record.SourceId,
            Fields = newFields,
            Metadata = record.Metadata,
            CreatedAt = record.CreatedAt,
            ModifiedAt = DateTime.UtcNow
        };

        var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
        RecordProcessed(true, elapsed);

        return Task.FromResult(new StandardizationResult
        {
            RecordId = record.RecordId,
            OriginalRecord = record,
            StandardizedRecord = standardizedRecord,
            StandardizedFields = standardizedFields,
            ProcessingTimeMs = elapsed
        });
    }

    private bool MatchesPattern(string fieldName, string pattern)
    {
        if (pattern == "*") return true;
        if (pattern.EndsWith("*"))
            return fieldName.StartsWith(pattern.TrimEnd('*'), StringComparison.OrdinalIgnoreCase);
        if (pattern.StartsWith("*"))
            return fieldName.EndsWith(pattern.TrimStart('*'), StringComparison.OrdinalIgnoreCase);
        return string.Equals(fieldName, pattern, StringComparison.OrdinalIgnoreCase);
    }

    private object? ApplyFormat(object? value, StandardFormat format)
    {
        if (value == null) return null;
        var strValue = value.ToString() ?? "";

        return format.Type switch
        {
            StandardizationType.Date => StandardizeDate(strValue, format.Parameters),
            StandardizationType.DateTime => StandardizeDateTime(strValue, format.Parameters),
            StandardizationType.Phone => StandardizePhone(strValue, format.Parameters),
            StandardizationType.Email => StandardizeEmail(strValue),
            StandardizationType.Currency => StandardizeCurrency(strValue, format.Parameters),
            StandardizationType.Numeric => StandardizeNumeric(strValue, format.Parameters),
            StandardizationType.Boolean => StandardizeBoolean(strValue),
            StandardizationType.Uuid => StandardizeUuid(strValue),
            StandardizationType.Name => StandardizeName(strValue, format.Parameters),
            StandardizationType.Address => StandardizeAddress(strValue, format.Parameters),
            StandardizationType.CountryCode => StandardizeCountryCode(strValue),
            StandardizationType.StateProvince => StandardizeStateProvince(strValue, format.Parameters),
            StandardizationType.PostalCode => StandardizePostalCode(strValue, format.Parameters),
            StandardizationType.Url => StandardizeUrl(strValue),
            StandardizationType.Custom => ApplyCustomFormat(value, format.Parameters),
            _ => value
        };
    }

    private object? StandardizeDate(string value, Dictionary<string, object>? parameters)
    {
        var outputFormat = parameters?.GetValueOrDefault("format")?.ToString() ?? "yyyy-MM-dd";
        if (DateTime.TryParse(value, out var date))
        {
            return date.ToString(outputFormat);
        }
        // Try common formats
        string[] formats = { "MM/dd/yyyy", "dd/MM/yyyy", "yyyy-MM-dd", "MM-dd-yyyy", "dd.MM.yyyy" };
        foreach (var fmt in formats)
        {
            if (DateTime.TryParseExact(value, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            {
                return date.ToString(outputFormat);
            }
        }
        return value;
    }

    private object? StandardizeDateTime(string value, Dictionary<string, object>? parameters)
    {
        var outputFormat = parameters?.GetValueOrDefault("format")?.ToString() ?? "yyyy-MM-ddTHH:mm:ssZ";
        if (DateTime.TryParse(value, out var dt))
        {
            return dt.ToUniversalTime().ToString(outputFormat);
        }
        return value;
    }

    private static object? StandardizePhone(string value, Dictionary<string, object>? parameters)
    {
        var digits = Regex.Replace(value, @"[^\d+]", "");
        var countryCode = parameters?.GetValueOrDefault("countryCode")?.ToString() ?? "1";
        var format = parameters?.GetValueOrDefault("format")?.ToString() ?? "E164";

        // Remove leading + if present
        if (digits.StartsWith("+"))
            digits = digits.Substring(1);

        // Add country code if not present
        if (!digits.StartsWith(countryCode) && digits.Length == 10)
            digits = countryCode + digits;

        return format switch
        {
            "E164" => "+" + digits,
            "National" when digits.Length >= 10 =>
                $"({digits.Substring(digits.Length - 10, 3)}) {digits.Substring(digits.Length - 7, 3)}-{digits.Substring(digits.Length - 4)}",
            "Dashes" when digits.Length >= 10 =>
                $"{digits.Substring(digits.Length - 10, 3)}-{digits.Substring(digits.Length - 7, 3)}-{digits.Substring(digits.Length - 4)}",
            _ => digits
        };
    }

    private static object? StandardizeEmail(string value)
    {
        return value.Trim().ToLowerInvariant();
    }

    private static object? StandardizeCurrency(string value, Dictionary<string, object>? parameters)
    {
        var culture = parameters?.GetValueOrDefault("culture")?.ToString() ?? "en-US";
        var clean = Regex.Replace(value, @"[^\d.\-,]", "");
        if (decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount))
        {
            return amount.ToString("C", new CultureInfo(culture));
        }
        return value;
    }

    private static object? StandardizeNumeric(string value, Dictionary<string, object>? parameters)
    {
        var decimals = parameters?.GetValueOrDefault("decimals") is int d ? d : 2;
        var clean = Regex.Replace(value, @"[^\d.\-]", "");
        if (decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out var num))
        {
            return Math.Round(num, decimals).ToString($"F{decimals}", CultureInfo.InvariantCulture);
        }
        return value;
    }

    private static object? StandardizeBoolean(string value)
    {
        var lower = value.Trim().ToLowerInvariant();
        if (lower is "true" or "yes" or "1" or "y" or "on")
            return true;
        if (lower is "false" or "no" or "0" or "n" or "off")
            return false;
        return value;
    }

    private static object? StandardizeUuid(string value)
    {
        if (Guid.TryParse(value, out var guid))
        {
            return guid.ToString("D").ToLowerInvariant();
        }
        return value;
    }

    private static object? StandardizeName(string value, Dictionary<string, object>? parameters)
    {
        var format = parameters?.GetValueOrDefault("format")?.ToString() ?? "Title";
        var trimmed = value.Trim();

        return format switch
        {
            "Title" => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(trimmed.ToLower()),
            "Upper" => trimmed.ToUpperInvariant(),
            "Lower" => trimmed.ToLowerInvariant(),
            "LastFirst" => ConvertToLastFirst(trimmed),
            "FirstLast" => ConvertToFirstLast(trimmed),
            _ => trimmed
        };
    }

    private static string ConvertToLastFirst(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            var last = parts[^1];
            var first = string.Join(" ", parts[..^1]);
            return $"{last}, {first}";
        }
        return name;
    }

    private static string ConvertToFirstLast(string name)
    {
        if (name.Contains(','))
        {
            var parts = name.Split(',', 2);
            return $"{parts[1].Trim()} {parts[0].Trim()}";
        }
        return name;
    }

    private static object? StandardizeAddress(string value, Dictionary<string, object>? parameters)
    {
        var upper = parameters?.GetValueOrDefault("uppercase") is bool u && u;
        var result = value.Trim();

        // Standardize common abbreviations
        result = Regex.Replace(result, @"\bST\.?\b", "Street", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\bAVE\.?\b", "Avenue", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\bBLVD\.?\b", "Boulevard", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\bRD\.?\b", "Road", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\bDR\.?\b", "Drive", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\bLN\.?\b", "Lane", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\bAPT\.?\b", "Apartment", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\bSTE\.?\b", "Suite", RegexOptions.IgnoreCase);

        return upper ? result.ToUpperInvariant() : CultureInfo.CurrentCulture.TextInfo.ToTitleCase(result.ToLower());
    }

    private static readonly Dictionary<string, string> CountryCodeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["United States"] = "US", ["USA"] = "US", ["U.S.A."] = "US", ["America"] = "US",
        ["United Kingdom"] = "GB", ["UK"] = "GB", ["Britain"] = "GB", ["England"] = "GB",
        ["Canada"] = "CA", ["Germany"] = "DE", ["France"] = "FR", ["Japan"] = "JP",
        ["Australia"] = "AU", ["China"] = "CN", ["India"] = "IN", ["Brazil"] = "BR"
    };

    private static object? StandardizeCountryCode(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 2)
            return trimmed.ToUpperInvariant();
        if (CountryCodeMap.TryGetValue(trimmed, out var code))
            return code;
        return trimmed.ToUpperInvariant();
    }

    private static readonly Dictionary<string, string> UsStateMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Alabama"] = "AL", ["Alaska"] = "AK", ["Arizona"] = "AZ", ["Arkansas"] = "AR",
        ["California"] = "CA", ["Colorado"] = "CO", ["Connecticut"] = "CT", ["Delaware"] = "DE",
        ["Florida"] = "FL", ["Georgia"] = "GA", ["Hawaii"] = "HI", ["Idaho"] = "ID",
        ["Illinois"] = "IL", ["Indiana"] = "IN", ["Iowa"] = "IA", ["Kansas"] = "KS",
        ["Kentucky"] = "KY", ["Louisiana"] = "LA", ["Maine"] = "ME", ["Maryland"] = "MD",
        ["Massachusetts"] = "MA", ["Michigan"] = "MI", ["Minnesota"] = "MN", ["Mississippi"] = "MS",
        ["Missouri"] = "MO", ["Montana"] = "MT", ["Nebraska"] = "NE", ["Nevada"] = "NV",
        ["New Hampshire"] = "NH", ["New Jersey"] = "NJ", ["New Mexico"] = "NM", ["New York"] = "NY",
        ["North Carolina"] = "NC", ["North Dakota"] = "ND", ["Ohio"] = "OH", ["Oklahoma"] = "OK",
        ["Oregon"] = "OR", ["Pennsylvania"] = "PA", ["Rhode Island"] = "RI", ["South Carolina"] = "SC",
        ["South Dakota"] = "SD", ["Tennessee"] = "TN", ["Texas"] = "TX", ["Utah"] = "UT",
        ["Vermont"] = "VT", ["Virginia"] = "VA", ["Washington"] = "WA", ["West Virginia"] = "WV",
        ["Wisconsin"] = "WI", ["Wyoming"] = "WY"
    };

    private static object? StandardizeStateProvince(string value, Dictionary<string, object>? parameters)
    {
        var country = parameters?.GetValueOrDefault("country")?.ToString() ?? "US";
        var trimmed = value.Trim();

        if (country == "US" && UsStateMap.TryGetValue(trimmed, out var stateCode))
            return stateCode;

        if (trimmed.Length == 2)
            return trimmed.ToUpperInvariant();

        return trimmed;
    }

    private static object? StandardizePostalCode(string value, Dictionary<string, object>? parameters)
    {
        var country = parameters?.GetValueOrDefault("country")?.ToString() ?? "US";
        var digits = Regex.Replace(value, @"[^\dA-Za-z]", "");

        if (country == "US")
        {
            // US ZIP codes: 5 digits or 5+4
            if (digits.Length == 5)
                return digits;
            if (digits.Length == 9)
                return $"{digits.Substring(0, 5)}-{digits.Substring(5)}";
        }
        else if (country == "CA")
        {
            // Canadian postal codes: A1A 1A1
            if (digits.Length == 6)
                return $"{digits.Substring(0, 3)} {digits.Substring(3)}".ToUpperInvariant();
        }

        return value.Trim().ToUpperInvariant();
    }

    private static object? StandardizeUrl(string value)
    {
        var trimmed = value.Trim().ToLowerInvariant();
        if (!trimmed.StartsWith("http://") && !trimmed.StartsWith("https://"))
        {
            trimmed = "https://" + trimmed;
        }
        // Remove trailing slash
        return trimmed.TrimEnd('/');
    }

    private object? ApplyCustomFormat(object? value, Dictionary<string, object>? parameters)
    {
        var formatterId = parameters?.GetValueOrDefault("formatterId")?.ToString();
        if (formatterId != null && _customFormatters.TryGetValue(formatterId, out var formatter))
        {
            return formatter(value, parameters);
        }
        return value;
    }
}

#endregion

#region Code Standardization Strategy

/// <summary>
/// Standardizes codes and identifiers (country codes, currency codes, etc.).
/// </summary>
public sealed class CodeStandardizationStrategy : DataQualityStrategyBase
{
    private readonly ConcurrentDictionary<string, Dictionary<string, string>> _codeMappings = new();

    /// <inheritdoc/>
    public override string StrategyId => "code-standardization";

    /// <inheritdoc/>
    public override string DisplayName => "Code Standardization";

    /// <inheritdoc/>
    public override DataQualityCategory Category => DataQualityCategory.Standardization;

    /// <inheritdoc/>
    public override DataQualityCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsDistributed = true,
        SupportsIncremental = true,
        MaxThroughput = 150000,
        TypicalLatencyMs = 0.2
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Standardizes codes and identifiers including ISO country codes, " +
        "currency codes, language codes, and custom code mappings.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "standardization", "codes", "iso", "identifiers", "mapping"
    };

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        // Initialize ISO currency codes
        RegisterCodeMapping("currency", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["$"] = "USD", ["Dollar"] = "USD", ["US Dollar"] = "USD",
            ["Euro"] = "EUR", ["Pound"] = "GBP", ["Yen"] = "JPY",
            ["Yuan"] = "CNY", ["Rupee"] = "INR", ["Franc"] = "CHF"
        });

        // Initialize language codes
        RegisterCodeMapping("language", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["English"] = "en", ["Spanish"] = "es", ["French"] = "fr",
            ["German"] = "de", ["Chinese"] = "zh", ["Japanese"] = "ja",
            ["Portuguese"] = "pt", ["Italian"] = "it", ["Russian"] = "ru"
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Registers a code mapping.
    /// </summary>
    public void RegisterCodeMapping(string codeType, Dictionary<string, string> mapping)
    {
        _codeMappings[codeType] = new Dictionary<string, string>(mapping, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Standardizes a code value.
    /// </summary>
    public string StandardizeCode(string codeType, string value)
    {
        if (_codeMappings.TryGetValue(codeType, out var mapping))
        {
            if (mapping.TryGetValue(value.Trim(), out var standardized))
            {
                return standardized;
            }
        }
        return value.Trim().ToUpperInvariant();
    }

    /// <summary>
    /// Standardizes all code fields in a record.
    /// </summary>
    public Task<StandardizationResult> StandardizeAsync(
        DataRecord record,
        Dictionary<string, string> fieldCodeTypes,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        var startTime = DateTime.UtcNow;

        var standardizedFields = new Dictionary<string, StandardizedValue>();
        var newFields = new Dictionary<string, object?>(record.Fields);

        foreach (var (fieldName, codeType) in fieldCodeTypes)
        {
            ct.ThrowIfCancellationRequested();

            if (record.Fields.TryGetValue(fieldName, out var value) && value != null)
            {
                var original = value.ToString() ?? "";
                var standardized = StandardizeCode(codeType, original);

                if (original != standardized)
                {
                    standardizedFields[fieldName] = new StandardizedValue
                    {
                        OriginalValue = original,
                        StandardizedValue_ = standardized,
                        StandardType = $"Code:{codeType}"
                    };
                    newFields[fieldName] = standardized;
                }
            }
        }

        var standardizedRecord = new DataRecord
        {
            RecordId = record.RecordId,
            SourceId = record.SourceId,
            Fields = newFields,
            Metadata = record.Metadata,
            CreatedAt = record.CreatedAt,
            ModifiedAt = DateTime.UtcNow
        };

        var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
        RecordProcessed(true, elapsed);

        return Task.FromResult(new StandardizationResult
        {
            RecordId = record.RecordId,
            OriginalRecord = record,
            StandardizedRecord = standardizedRecord,
            StandardizedFields = standardizedFields,
            ProcessingTimeMs = elapsed
        });
    }
}

#endregion
