// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Plugins.AIInterface.Channels;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.AIInterface.GUI;

/// <summary>
/// Builds structured filter queries from natural language input.
/// Enables users to describe filters in plain English instead of using complex query syntax.
/// </summary>
/// <remarks>
/// <para>
/// The NaturalLanguageFilterBuilder supports conversions like:
/// <list type="bullet">
/// <item>"files larger than 10MB" becomes <c>size > 10485760</c></item>
/// <item>"documents modified last week" becomes <c>type = document AND modifiedDate > [date]</c></item>
/// <item>"images shared with marketing team" becomes <c>type = image AND sharedWith CONTAINS "marketing"</c></item>
/// <item>"encrypted PDFs from 2023" becomes <c>extension = pdf AND encrypted = true AND year = 2023</c></item>
/// </list>
/// </para>
/// <para>
/// When Intelligence is unavailable, pattern-based parsing is used to extract
/// filter criteria from common natural language patterns.
/// </para>
/// </remarks>
public sealed class NaturalLanguageFilterBuilder : IDisposable
{
    private readonly IMessageBus? _messageBus;
    private readonly FilterBuilderConfig _config;
    private bool _intelligenceAvailable;
    private IntelligenceCapabilities _capabilities = IntelligenceCapabilities.None;
    private bool _disposed;

    /// <summary>
    /// Gets whether Intelligence is available for natural language parsing.
    /// </summary>
    public bool IsIntelligenceAvailable => _intelligenceAvailable;

    /// <summary>
    /// Initializes a new instance of the <see cref="NaturalLanguageFilterBuilder"/> class.
    /// </summary>
    /// <param name="messageBus">Message bus for Intelligence communication.</param>
    /// <param name="config">Optional configuration.</param>
    public NaturalLanguageFilterBuilder(IMessageBus? messageBus, FilterBuilderConfig? config = null)
    {
        _messageBus = messageBus;
        _config = config ?? new FilterBuilderConfig();
    }

    /// <summary>
    /// Initializes the filter builder and discovers Intelligence availability.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_messageBus == null)
        {
            _intelligenceAvailable = false;
            return;
        }

        try
        {
            var response = await _messageBus.RequestAsync(
                IntelligenceTopics.Discover,
                new Dictionary<string, object>
                {
                    ["requestorId"] = "gui.filterbuilder",
                    ["requestorName"] = "NaturalLanguageFilterBuilder",
                    ["timestamp"] = DateTimeOffset.UtcNow
                },
                ct);

            if (response != null &&
                response.TryGetValue("available", out var available) && available is true)
            {
                _intelligenceAvailable = true;

                if (response.TryGetValue("capabilities", out var caps))
                {
                    if (caps is IntelligenceCapabilities ic)
                        _capabilities = ic;
                    else if (caps is long longVal)
                        _capabilities = (IntelligenceCapabilities)longVal;
                }
            }
        }
        catch
        {
            _intelligenceAvailable = false;
        }
    }

    /// <summary>
    /// Builds a filter expression from natural language input.
    /// </summary>
    /// <param name="input">The natural language filter description.</param>
    /// <param name="availableFields">Optional list of available filter fields.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The parsed filter result.</returns>
    public async Task<FilterBuildResult> BuildFilterAsync(
        string input,
        IEnumerable<FilterField>? availableFields = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new FilterBuildResult
            {
                Success = false,
                ErrorMessage = "No filter input provided."
            };
        }

        var result = new FilterBuildResult
        {
            OriginalInput = input
        };

        // Try pattern-based parsing first (fast path)
        var patternFilters = ParseWithPatterns(input);
        result.Filters.AddRange(patternFilters);

        // Enhance with AI if available
        if (_intelligenceAvailable && _messageBus != null)
        {
            try
            {
                var aiFilters = await ParseWithAIAsync(input, availableFields, ct);
                MergeFilters(result.Filters, aiFilters);
            }
            catch
            {
                // Use pattern-based results
            }
        }

        // Build the query expression
        if (result.Filters.Count > 0)
        {
            result.Success = true;
            result.QueryExpression = BuildQueryExpression(result.Filters);
            result.DisplaySummary = BuildDisplaySummary(result.Filters);
        }
        else
        {
            result.Success = false;
            result.ErrorMessage = "Could not extract any filters from the input.";
            result.Suggestions = new[]
            {
                "Try: files larger than 10MB",
                "Try: documents from last month",
                "Try: images with keyword 'project'"
            };
        }

        return result;
    }

    /// <summary>
    /// Validates and normalizes a filter expression.
    /// </summary>
    /// <param name="expression">The filter expression to validate.</param>
    /// <param name="availableFields">Available filter fields.</param>
    /// <returns>Validation result.</returns>
    public FilterValidationResult ValidateExpression(
        string expression,
        IEnumerable<FilterField>? availableFields = null)
    {
        var result = new FilterValidationResult
        {
            OriginalExpression = expression,
            IsValid = true
        };

        if (string.IsNullOrWhiteSpace(expression))
        {
            result.IsValid = false;
            result.Errors.Add("Empty expression");
            return result;
        }

        // Check for balanced parentheses
        var parenCount = 0;
        foreach (var c in expression)
        {
            if (c == '(') parenCount++;
            if (c == ')') parenCount--;
            if (parenCount < 0)
            {
                result.IsValid = false;
                result.Errors.Add("Unbalanced parentheses: extra closing parenthesis");
                break;
            }
        }
        if (parenCount > 0)
        {
            result.IsValid = false;
            result.Errors.Add("Unbalanced parentheses: missing closing parenthesis");
        }

        // Validate field names if available
        if (availableFields != null)
        {
            var fieldNames = availableFields.Select(f => f.Name.ToLowerInvariant()).ToHashSet();
            var fieldPattern = new Regex(@"\b([a-zA-Z_][a-zA-Z0-9_]*)\s*(?:=|!=|>|<|>=|<=|CONTAINS|LIKE)", RegexOptions.IgnoreCase);

            foreach (Match match in fieldPattern.Matches(expression))
            {
                var field = match.Groups[1].Value.ToLowerInvariant();
                if (!fieldNames.Contains(field))
                {
                    result.Warnings.Add($"Unknown field: {field}");
                }
            }
        }

        // Normalize the expression
        result.NormalizedExpression = NormalizeExpression(expression);

        return result;
    }

    /// <summary>
    /// Gets natural language suggestions for refining a filter.
    /// </summary>
    /// <param name="currentFilters">Current filters applied.</param>
    /// <param name="availableFields">Available filter fields.</param>
    /// <returns>List of refinement suggestions.</returns>
    public IReadOnlyList<FilterSuggestion> GetRefinementSuggestions(
        IEnumerable<FilterCriteria> currentFilters,
        IEnumerable<FilterField>? availableFields = null)
    {
        var suggestions = new List<FilterSuggestion>();
        var currentFields = currentFilters.Select(f => f.Field.ToLowerInvariant()).ToHashSet();

        // Suggest common refinements
        if (!currentFields.Contains("size"))
        {
            suggestions.Add(new FilterSuggestion
            {
                Text = "Filter by file size",
                Examples = new[] { "larger than 10MB", "smaller than 1GB" },
                Field = "size"
            });
        }

        if (!currentFields.Contains("type") && !currentFields.Contains("extension"))
        {
            suggestions.Add(new FilterSuggestion
            {
                Text = "Filter by file type",
                Examples = new[] { "only documents", "images only", "PDF files" },
                Field = "type"
            });
        }

        if (!currentFields.Contains("modified") && !currentFields.Contains("created"))
        {
            suggestions.Add(new FilterSuggestion
            {
                Text = "Filter by date",
                Examples = new[] { "modified this week", "created last year", "from 2023" },
                Field = "modified"
            });
        }

        if (!currentFields.Contains("owner") && !currentFields.Contains("sharedwith"))
        {
            suggestions.Add(new FilterSuggestion
            {
                Text = "Filter by owner/sharing",
                Examples = new[] { "owned by me", "shared with team", "from marketing" },
                Field = "owner"
            });
        }

        if (!currentFields.Contains("encrypted"))
        {
            suggestions.Add(new FilterSuggestion
            {
                Text = "Filter by security",
                Examples = new[] { "encrypted files", "unencrypted", "classified as confidential" },
                Field = "encrypted"
            });
        }

        // Add available field-based suggestions
        if (availableFields != null)
        {
            foreach (var field in availableFields.Where(f => !currentFields.Contains(f.Name.ToLowerInvariant())))
            {
                if (!suggestions.Any(s => s.Field == field.Name))
                {
                    suggestions.Add(new FilterSuggestion
                    {
                        Text = $"Filter by {field.DisplayName}",
                        Examples = GenerateFieldExamples(field),
                        Field = field.Name
                    });
                }
            }
        }

        return suggestions.Take(_config.MaxSuggestions).ToList().AsReadOnly();
    }

    /// <summary>
    /// Converts a structured filter to natural language description.
    /// </summary>
    /// <param name="filters">The filters to describe.</param>
    /// <returns>Natural language description.</returns>
    public string ToNaturalLanguage(IEnumerable<FilterCriteria> filters)
    {
        var parts = new List<string>();

        foreach (var filter in filters)
        {
            var description = DescribeFilter(filter);
            if (!string.IsNullOrEmpty(description))
            {
                parts.Add(description);
            }
        }

        if (parts.Count == 0)
            return "All files";

        if (parts.Count == 1)
            return parts[0];

        return string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts.Last();
    }

    private List<FilterCriteria> ParseWithPatterns(string input)
    {
        var filters = new List<FilterCriteria>();
        var lower = input.ToLowerInvariant();

        // Size patterns
        var sizePatterns = new[]
        {
            (@"(?:larger|bigger|greater)\s+than\s+(\d+(?:\.\d+)?)\s*(kb|mb|gb|tb)?", FilterOperator.GreaterThan),
            (@"(?:smaller|less)\s+than\s+(\d+(?:\.\d+)?)\s*(kb|mb|gb|tb)?", FilterOperator.LessThan),
            (@"(?:at least|minimum)\s+(\d+(?:\.\d+)?)\s*(kb|mb|gb|tb)?", FilterOperator.GreaterThanOrEqual),
            (@"(?:at most|maximum|up to)\s+(\d+(?:\.\d+)?)\s*(kb|mb|gb|tb)?", FilterOperator.LessThanOrEqual),
            (@"size\s*[>:]\s*(\d+(?:\.\d+)?)\s*(kb|mb|gb|tb)?", FilterOperator.GreaterThan),
            (@"size\s*[<]\s*(\d+(?:\.\d+)?)\s*(kb|mb|gb|tb)?", FilterOperator.LessThan)
        };

        foreach (var (pattern, op) in sizePatterns)
        {
            var match = Regex.Match(lower, pattern);
            if (match.Success)
            {
                var size = ParseSize(match.Groups[1].Value, match.Groups[2].Value);
                filters.Add(new FilterCriteria
                {
                    Field = "size",
                    Operator = op,
                    Value = size,
                    DisplayValue = $"{match.Groups[1].Value} {match.Groups[2].Value}".Trim().ToUpperInvariant()
                });
                break;
            }
        }

        // Date patterns
        var datePatterns = new Dictionary<string, (DateTime Start, DateTime End)>
        {
            ["today"] = (DateTime.Today, DateTime.Today.AddDays(1)),
            ["yesterday"] = (DateTime.Today.AddDays(-1), DateTime.Today),
            ["this week"] = (StartOfWeek(DateTime.Today), DateTime.Today.AddDays(1)),
            ["last week"] = (StartOfWeek(DateTime.Today).AddDays(-7), StartOfWeek(DateTime.Today)),
            ["this month"] = (new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1), DateTime.Today.AddDays(1)),
            ["last month"] = (new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-1), new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1)),
            ["this year"] = (new DateTime(DateTime.Today.Year, 1, 1), DateTime.Today.AddDays(1)),
            ["last year"] = (new DateTime(DateTime.Today.Year - 1, 1, 1), new DateTime(DateTime.Today.Year, 1, 1))
        };

        foreach (var (datePhrase, (start, end)) in datePatterns)
        {
            if (lower.Contains(datePhrase))
            {
                var field = lower.Contains("created") ? "createdDate" : "modifiedDate";
                filters.Add(new FilterCriteria
                {
                    Field = field,
                    Operator = FilterOperator.Between,
                    Value = new DateRange { Start = start, End = end },
                    DisplayValue = datePhrase
                });
                break;
            }
        }

        // Specific year
        var yearMatch = Regex.Match(lower, @"\bfrom\s+(20\d{2})\b|\b(20\d{2})\b");
        if (yearMatch.Success)
        {
            var year = int.Parse(yearMatch.Groups[1].Success ? yearMatch.Groups[1].Value : yearMatch.Groups[2].Value);
            filters.Add(new FilterCriteria
            {
                Field = "year",
                Operator = FilterOperator.Equals,
                Value = year,
                DisplayValue = year.ToString()
            });
        }

        // File type patterns
        var typePatterns = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["documents"] = new[] { "doc", "docx", "pdf", "txt", "rtf", "odt" },
            ["images"] = new[] { "jpg", "jpeg", "png", "gif", "bmp", "webp", "svg" },
            ["videos"] = new[] { "mp4", "avi", "mov", "wmv", "mkv", "webm" },
            ["audio"] = new[] { "mp3", "wav", "flac", "aac", "ogg", "m4a" },
            ["spreadsheets"] = new[] { "xls", "xlsx", "csv", "ods" },
            ["presentations"] = new[] { "ppt", "pptx", "odp" },
            ["archives"] = new[] { "zip", "rar", "7z", "tar", "gz" },
            ["code"] = new[] { "cs", "js", "ts", "py", "java", "cpp", "go", "rs" }
        };

        foreach (var (typeName, extensions) in typePatterns)
        {
            if (lower.Contains(typeName) || lower.Contains(typeName.TrimEnd('s')))
            {
                filters.Add(new FilterCriteria
                {
                    Field = "fileType",
                    Operator = FilterOperator.In,
                    Value = extensions,
                    DisplayValue = typeName
                });
                break;
            }
        }

        // Specific extension
        var extMatch = Regex.Match(lower, @"\b(pdf|doc|docx|xls|xlsx|jpg|png|mp4|zip)\s+(?:files?)?");
        if (extMatch.Success)
        {
            filters.Add(new FilterCriteria
            {
                Field = "extension",
                Operator = FilterOperator.Equals,
                Value = extMatch.Groups[1].Value,
                DisplayValue = extMatch.Groups[1].Value.ToUpperInvariant()
            });
        }

        // Encryption status
        if (lower.Contains("encrypted"))
        {
            var isEncrypted = !lower.Contains("not encrypted") && !lower.Contains("unencrypted");
            filters.Add(new FilterCriteria
            {
                Field = "encrypted",
                Operator = FilterOperator.Equals,
                Value = isEncrypted,
                DisplayValue = isEncrypted ? "encrypted" : "not encrypted"
            });
        }

        // Owner patterns
        var ownerMatch = Regex.Match(lower, @"(?:owned by|from|by)\s+(?:user\s+)?[""']?([^""']+)[""']?");
        if (ownerMatch.Success)
        {
            var owner = ownerMatch.Groups[1].Value.Trim();
            if (owner != "me")
            {
                filters.Add(new FilterCriteria
                {
                    Field = "owner",
                    Operator = FilterOperator.Equals,
                    Value = owner,
                    DisplayValue = owner
                });
            }
        }

        if (lower.Contains("owned by me") || lower.Contains("my files"))
        {
            filters.Add(new FilterCriteria
            {
                Field = "owner",
                Operator = FilterOperator.Equals,
                Value = "@currentUser",
                DisplayValue = "me"
            });
        }

        // Shared patterns
        var sharedMatch = Regex.Match(lower, @"shared\s+with\s+[""']?([^""']+)[""']?");
        if (sharedMatch.Success)
        {
            filters.Add(new FilterCriteria
            {
                Field = "sharedWith",
                Operator = FilterOperator.Contains,
                Value = sharedMatch.Groups[1].Value.Trim(),
                DisplayValue = sharedMatch.Groups[1].Value.Trim()
            });
        }

        // Tag/keyword patterns
        var tagMatch = Regex.Match(lower, @"(?:with|tagged|keyword)\s+[""']?([^""']+)[""']?");
        if (tagMatch.Success)
        {
            filters.Add(new FilterCriteria
            {
                Field = "tags",
                Operator = FilterOperator.Contains,
                Value = tagMatch.Groups[1].Value.Trim(),
                DisplayValue = tagMatch.Groups[1].Value.Trim()
            });
        }

        return filters;
    }

    private async Task<List<FilterCriteria>> ParseWithAIAsync(
        string input,
        IEnumerable<FilterField>? availableFields,
        CancellationToken ct)
    {
        var filters = new List<FilterCriteria>();

        var payload = new Dictionary<string, object>
        {
            ["input"] = input,
            ["domain"] = "file_filter"
        };

        if (availableFields != null)
        {
            payload["availableFields"] = availableFields.Select(f => new Dictionary<string, object>
            {
                ["name"] = f.Name,
                ["type"] = f.Type.ToString(),
                ["displayName"] = f.DisplayName
            }).ToList();
        }

        var response = await _messageBus!.RequestAsync(
            "intelligence.request.filter-parse",
            payload,
            ct);

        if (response != null && response.TryGetValue("filters", out var filtersObj) &&
            filtersObj is IEnumerable<object> filterList)
        {
            foreach (var filter in filterList.OfType<Dictionary<string, object>>())
            {
                var field = filter.TryGetValue("field", out var f) ? f?.ToString() : null;
                var op = filter.TryGetValue("operator", out var o) ? o?.ToString() : null;
                var value = filter.TryGetValue("value", out var v) ? v : null;

                if (!string.IsNullOrEmpty(field) && value != null)
                {
                    filters.Add(new FilterCriteria
                    {
                        Field = field,
                        Operator = ParseOperator(op),
                        Value = value,
                        DisplayValue = filter.TryGetValue("displayValue", out var dv) ? dv?.ToString() : value.ToString()
                    });
                }
            }
        }

        return filters;
    }

    private void MergeFilters(List<FilterCriteria> target, List<FilterCriteria> source)
    {
        foreach (var filter in source)
        {
            // Only add if not already present for the same field
            if (!target.Any(f => f.Field.Equals(filter.Field, StringComparison.OrdinalIgnoreCase)))
            {
                target.Add(filter);
            }
        }
    }

    private string BuildQueryExpression(List<FilterCriteria> filters)
    {
        var parts = new List<string>();

        foreach (var filter in filters)
        {
            var expr = BuildFilterExpression(filter);
            if (!string.IsNullOrEmpty(expr))
            {
                parts.Add(expr);
            }
        }

        return string.Join(" AND ", parts);
    }

    private string BuildFilterExpression(FilterCriteria filter)
    {
        var field = filter.Field;
        var value = FormatValue(filter.Value ?? "null");

        return filter.Operator switch
        {
            FilterOperator.Equals => $"{field} = {value}",
            FilterOperator.NotEquals => $"{field} != {value}",
            FilterOperator.GreaterThan => $"{field} > {value}",
            FilterOperator.GreaterThanOrEqual => $"{field} >= {value}",
            FilterOperator.LessThan => $"{field} < {value}",
            FilterOperator.LessThanOrEqual => $"{field} <= {value}",
            FilterOperator.Contains => $"{field} CONTAINS {value}",
            FilterOperator.StartsWith => $"{field} STARTS WITH {value}",
            FilterOperator.EndsWith => $"{field} ENDS WITH {value}",
            FilterOperator.In => $"{field} IN ({FormatArray(filter.Value!)})",
            FilterOperator.Between when filter.Value is DateRange range =>
                $"({field} >= '{range.Start:yyyy-MM-dd}' AND {field} < '{range.End:yyyy-MM-dd}')",
            _ => $"{field} = {value}"
        };
    }

    private string BuildDisplaySummary(List<FilterCriteria> filters)
    {
        var parts = filters
            .Select(f => $"{f.Field}: {f.DisplayValue}")
            .ToList();

        return string.Join(", ", parts);
    }

    private long ParseSize(string sizeValue, string unit)
    {
        var size = double.Parse(sizeValue);
        var multiplier = unit?.ToLowerInvariant() switch
        {
            "kb" => 1024L,
            "mb" => 1024L * 1024,
            "gb" => 1024L * 1024 * 1024,
            "tb" => 1024L * 1024 * 1024 * 1024,
            _ => 1L
        };

        return (long)(size * multiplier);
    }

    private DateTime StartOfWeek(DateTime date)
    {
        var diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
        return date.AddDays(-diff).Date;
    }

    private FilterOperator ParseOperator(string? op)
    {
        return op?.ToLowerInvariant() switch
        {
            "eq" or "equals" or "=" => FilterOperator.Equals,
            "neq" or "notequals" or "!=" => FilterOperator.NotEquals,
            "gt" or "greaterthan" or ">" => FilterOperator.GreaterThan,
            "gte" or "greaterthanorequal" or ">=" => FilterOperator.GreaterThanOrEqual,
            "lt" or "lessthan" or "<" => FilterOperator.LessThan,
            "lte" or "lessthanorequal" or "<=" => FilterOperator.LessThanOrEqual,
            "contains" or "like" => FilterOperator.Contains,
            "startswith" => FilterOperator.StartsWith,
            "endswith" => FilterOperator.EndsWith,
            "in" => FilterOperator.In,
            "between" => FilterOperator.Between,
            _ => FilterOperator.Equals
        };
    }

    private string FormatValue(object value)
    {
        return value switch
        {
            string s => $"'{s}'",
            bool b => b.ToString().ToLowerInvariant(),
            DateTime dt => $"'{dt:yyyy-MM-dd}'",
            _ => value.ToString() ?? "null"
        };
    }

    private string FormatArray(object value)
    {
        if (value is string[] arr)
        {
            return string.Join(", ", arr.Select(s => $"'{s}'"));
        }
        if (value is IEnumerable<string> list)
        {
            return string.Join(", ", list.Select(s => $"'{s}'"));
        }
        return FormatValue(value);
    }

    private string NormalizeExpression(string expression)
    {
        // Normalize whitespace
        expression = Regex.Replace(expression, @"\s+", " ").Trim();

        // Normalize operators
        expression = expression.Replace(" = ", " = ");
        expression = Regex.Replace(expression, @"\s*AND\s*", " AND ", RegexOptions.IgnoreCase);
        expression = Regex.Replace(expression, @"\s*OR\s*", " OR ", RegexOptions.IgnoreCase);

        return expression;
    }

    private string[] GenerateFieldExamples(FilterField field)
    {
        return field.Type switch
        {
            FilterFieldType.String => new[] { $"{field.Name} contains 'value'", $"{field.Name} = 'exact'" },
            FilterFieldType.Number => new[] { $"{field.Name} > 100", $"{field.Name} between 50 and 200" },
            FilterFieldType.Date => new[] { $"{field.Name} this week", $"{field.Name} after 2023-01-01" },
            FilterFieldType.Boolean => new[] { $"{field.Name} is true", $"{field.Name} is false" },
            FilterFieldType.Enum => new[] { $"{field.Name} = option1", $"{field.Name} in (option1, option2)" },
            _ => new[] { $"{field.Name} = value" }
        };
    }

    private string DescribeFilter(FilterCriteria filter)
    {
        var field = filter.Field.ToLowerInvariant();
        var value = filter.DisplayValue ?? filter.Value?.ToString() ?? "";

        return field switch
        {
            "size" => filter.Operator switch
            {
                FilterOperator.GreaterThan => $"larger than {value}",
                FilterOperator.LessThan => $"smaller than {value}",
                _ => $"size {value}"
            },
            "modifieddate" or "modified" => $"modified {value}",
            "createddate" or "created" => $"created {value}",
            "filetype" or "type" => $"{value} files",
            "extension" => $".{value} files",
            "encrypted" => filter.Value is true ? "encrypted files" : "unencrypted files",
            "owner" => value == "@currentUser" ? "owned by me" : $"owned by {value}",
            "sharedwith" => $"shared with {value}",
            "tags" => $"tagged with {value}",
            _ => $"{field} is {value}"
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}

/// <summary>
/// Result of building a filter from natural language.
/// </summary>
public sealed class FilterBuildResult
{
    /// <summary>Gets or sets whether building was successful.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets the original input.</summary>
    public string OriginalInput { get; init; } = string.Empty;

    /// <summary>Gets or sets the extracted filters.</summary>
    public List<FilterCriteria> Filters { get; init; } = new();

    /// <summary>Gets or sets the query expression.</summary>
    public string? QueryExpression { get; set; }

    /// <summary>Gets or sets a display summary.</summary>
    public string? DisplaySummary { get; set; }

    /// <summary>Gets or sets the error message if failed.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Gets or sets suggestions if parsing failed.</summary>
    public string[]? Suggestions { get; set; }
}

/// <summary>
/// A single filter criteria.
/// </summary>
public sealed class FilterCriteria
{
    /// <summary>Gets or sets the field name.</summary>
    public string Field { get; init; } = string.Empty;

    /// <summary>Gets or sets the operator.</summary>
    public FilterOperator Operator { get; init; }

    /// <summary>Gets or sets the value.</summary>
    public object? Value { get; init; }

    /// <summary>Gets or sets the display value.</summary>
    public string? DisplayValue { get; init; }
}

/// <summary>
/// Filter operators.
/// </summary>
public enum FilterOperator
{
    /// <summary>Equals.</summary>
    Equals,

    /// <summary>Not equals.</summary>
    NotEquals,

    /// <summary>Greater than.</summary>
    GreaterThan,

    /// <summary>Greater than or equal.</summary>
    GreaterThanOrEqual,

    /// <summary>Less than.</summary>
    LessThan,

    /// <summary>Less than or equal.</summary>
    LessThanOrEqual,

    /// <summary>Contains.</summary>
    Contains,

    /// <summary>Starts with.</summary>
    StartsWith,

    /// <summary>Ends with.</summary>
    EndsWith,

    /// <summary>In list.</summary>
    In,

    /// <summary>Between range.</summary>
    Between
}

/// <summary>
/// Date range for between filters.
/// </summary>
public sealed class DateRange
{
    /// <summary>Gets or sets the start date.</summary>
    public DateTime Start { get; init; }

    /// <summary>Gets or sets the end date.</summary>
    public DateTime End { get; init; }
}

/// <summary>
/// Available filter field definition.
/// </summary>
public sealed class FilterField
{
    /// <summary>Gets or sets the field name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets or sets the display name.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Gets or sets the field type.</summary>
    public FilterFieldType Type { get; init; }

    /// <summary>Gets or sets allowed values for enum fields.</summary>
    public string[]? AllowedValues { get; init; }
}

/// <summary>
/// Filter field types.
/// </summary>
public enum FilterFieldType
{
    /// <summary>String field.</summary>
    String,

    /// <summary>Number field.</summary>
    Number,

    /// <summary>Date field.</summary>
    Date,

    /// <summary>Boolean field.</summary>
    Boolean,

    /// <summary>Enum field.</summary>
    Enum
}

/// <summary>
/// Filter validation result.
/// </summary>
public sealed class FilterValidationResult
{
    /// <summary>Gets or sets the original expression.</summary>
    public string OriginalExpression { get; init; } = string.Empty;

    /// <summary>Gets or sets whether the expression is valid.</summary>
    public bool IsValid { get; set; }

    /// <summary>Gets or sets the normalized expression.</summary>
    public string? NormalizedExpression { get; set; }

    /// <summary>Gets or sets validation errors.</summary>
    public List<string> Errors { get; init; } = new();

    /// <summary>Gets or sets validation warnings.</summary>
    public List<string> Warnings { get; init; } = new();
}

/// <summary>
/// Filter refinement suggestion.
/// </summary>
public sealed class FilterSuggestion
{
    /// <summary>Gets or sets the suggestion text.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Gets or sets example usage.</summary>
    public string[] Examples { get; init; } = Array.Empty<string>();

    /// <summary>Gets or sets the related field.</summary>
    public string Field { get; init; } = string.Empty;
}

/// <summary>
/// Filter builder configuration.
/// </summary>
public sealed class FilterBuilderConfig
{
    /// <summary>Maximum suggestions to return.</summary>
    public int MaxSuggestions { get; init; } = 5;

    /// <summary>Enable AI-enhanced parsing.</summary>
    public bool EnableAIParsing { get; init; } = true;
}
