using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DataWarehouse.SDK.Validation;

// ============================================================================
// VALIDATION MIDDLEWARE
// Centralized input validation for all external API endpoints
// Integrates with REST, gRPC, and SQL interfaces
// ============================================================================

#region Validation Pipeline

/// <summary>
/// Centralized validation middleware that validates all external inputs.
/// Provides defense-in-depth validation at system boundaries.
/// </summary>
public sealed class ValidationMiddleware
{
    private readonly List<IValidationFilter> _filters = new();
    private readonly ConcurrentDictionary<string, ValidationMetrics> _metrics = new();
    private readonly ValidationMiddlewareConfig _config;
    private long _totalValidations;
    private long _failedValidations;

    public ValidationMiddleware(ValidationMiddlewareConfig? config = null)
    {
        _config = config ?? ValidationMiddlewareConfig.Default;
        InitializeDefaultFilters();
    }

    private void InitializeDefaultFilters()
    {
        // Add default security filters
        _filters.Add(new NullByteFilter());
        _filters.Add(new ControlCharacterFilter());
        _filters.Add(new PathTraversalFilter());
        _filters.Add(new XssFilter());
        _filters.Add(new CommandInjectionFilter());
        _filters.Add(new LdapInjectionFilter());
        _filters.Add(new XmlInjectionFilter());

        if (_config.EnableSqlValidation)
        {
            _filters.Add(new SqlInjectionFilter());
        }
    }

    /// <summary>
    /// Adds a custom validation filter.
    /// </summary>
    public ValidationMiddleware AddFilter(IValidationFilter filter)
    {
        _filters.Add(filter ?? throw new ArgumentNullException(nameof(filter)));
        return this;
    }

    /// <summary>
    /// Validates input and returns a validation result.
    /// </summary>
    public async Task<MiddlewareValidationResult> ValidateAsync(
        object? input,
        MiddlewareValidationContext context,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        Interlocked.Increment(ref _totalValidations);

        var result = new MiddlewareValidationResult
        {
            IsValid = true,
            ValidatedAt = DateTime.UtcNow,
            Context = context
        };

        if (input == null)
        {
            if (_config.AllowNull)
            {
                return result;
            }
            result.IsValid = false;
            result.Errors.Add(new MiddlewareValidationError
            {
                Code = "NULL_INPUT",
                Message = "Input cannot be null",
                Severity = ValidationSeverity.Error
            });
            RecordMetrics(context.Source, sw.Elapsed, false);
            return result;
        }

        try
        {
            // Convert input to string for filter processing
            var inputStr = input switch
            {
                string s => s,
                _ => JsonSerializer.Serialize(input)
            };

            // Apply all filters
            foreach (var filter in _filters)
            {
                if (ct.IsCancellationRequested) break;

                var filterResult = await filter.ValidateAsync(inputStr, context, ct);
                if (!filterResult.IsValid)
                {
                    result.IsValid = false;
                    result.Errors.AddRange(filterResult.Errors);

                    if (_config.FailFast)
                    {
                        break;
                    }
                }

                result.Warnings.AddRange(filterResult.Warnings);
            }

            // Type-specific validation
            if (result.IsValid && input is not string)
            {
                var typeResult = await ValidateTypeAsync(input, context, ct);
                if (!typeResult.IsValid)
                {
                    result.IsValid = false;
                    result.Errors.AddRange(typeResult.Errors);
                }
                result.Warnings.AddRange(typeResult.Warnings);
            }
        }
        catch (Exception ex)
        {
            result.IsValid = false;
            result.Errors.Add(new MiddlewareValidationError
            {
                Code = "VALIDATION_EXCEPTION",
                Message = $"Validation failed: {ex.Message}",
                Severity = ValidationSeverity.Error
            });
        }

        sw.Stop();
        result.Duration = sw.Elapsed;

        if (!result.IsValid)
        {
            Interlocked.Increment(ref _failedValidations);
        }

        RecordMetrics(context.Source, sw.Elapsed, result.IsValid);

        return result;
    }

    private async Task<MiddlewareValidationResult> ValidateTypeAsync(
        object input,
        MiddlewareValidationContext context,
        CancellationToken ct)
    {
        var result = new MiddlewareValidationResult { IsValid = true };

        // Get type-specific validators
        var inputType = input.GetType();
        var validators = GetValidatorsForType(inputType);

        foreach (var validator in validators)
        {
            var validatorResult = await validator.ValidateAsync(input, context, ct);
            if (!validatorResult.IsValid)
            {
                result.IsValid = false;
                result.Errors.AddRange(validatorResult.Errors);
            }
            result.Warnings.AddRange(validatorResult.Warnings);
        }

        return result;
    }

    private readonly ConcurrentDictionary<Type, List<ITypeValidator>> _typeValidators = new();

    /// <summary>
    /// Registers a type-specific validator.
    /// </summary>
    public void RegisterTypeValidator<T>(ITypeValidator validator)
    {
        var validators = _typeValidators.GetOrAdd(typeof(T), _ => new List<ITypeValidator>());
        lock (validators)
        {
            validators.Add(validator);
        }
    }

    private List<ITypeValidator> GetValidatorsForType(Type type)
    {
        if (_typeValidators.TryGetValue(type, out var validators))
        {
            return validators;
        }
        return new List<ITypeValidator>();
    }

    private void RecordMetrics(string source, TimeSpan duration, bool success)
    {
        _metrics.AddOrUpdate(
            source,
            _ => new ValidationMetrics
            {
                Source = source,
                TotalValidations = 1,
                SuccessfulValidations = success ? 1 : 0,
                FailedValidations = success ? 0 : 1,
                TotalDuration = duration,
                LastValidation = DateTime.UtcNow
            },
            (_, existing) =>
            {
                existing.TotalValidations++;
                if (success) existing.SuccessfulValidations++;
                else existing.FailedValidations++;
                existing.TotalDuration += duration;
                existing.LastValidation = DateTime.UtcNow;
                return existing;
            });
    }

    /// <summary>
    /// Gets validation statistics.
    /// </summary>
    public ValidationStats GetStats()
    {
        return new ValidationStats
        {
            TotalValidations = _totalValidations,
            FailedValidations = _failedValidations,
            SuccessRate = _totalValidations > 0
                ? (double)(_totalValidations - _failedValidations) / _totalValidations * 100
                : 100,
            MetricsBySource = _metrics.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
        };
    }
}

#endregion

#region Validation Filters

/// <summary>
/// Interface for validation filters.
/// </summary>
public interface IValidationFilter
{
    string Name { get; }
    Task<MiddlewareValidationResult> ValidateAsync(string input, MiddlewareValidationContext context, CancellationToken ct);
}

/// <summary>
/// Interface for type-specific validators.
/// </summary>
public interface ITypeValidator
{
    Task<MiddlewareValidationResult> ValidateAsync(object input, MiddlewareValidationContext context, CancellationToken ct);
}

/// <summary>
/// Detects and blocks null byte injection.
/// </summary>
public sealed class NullByteFilter : IValidationFilter
{
    public string Name => "NullByte";

    public Task<MiddlewareValidationResult> ValidateAsync(string input, MiddlewareValidationContext context, CancellationToken ct)
    {
        var result = new MiddlewareValidationResult { IsValid = true };

        if (input.Contains('\0'))
        {
            result.IsValid = false;
            result.Errors.Add(new MiddlewareValidationError
            {
                Code = "NULL_BYTE",
                Message = "Input contains null byte (potential injection attack)",
                Severity = ValidationSeverity.Critical
            });
        }

        return Task.FromResult(result);
    }
}

/// <summary>
/// Detects and blocks control characters.
/// </summary>
public sealed class ControlCharacterFilter : IValidationFilter
{
    public string Name => "ControlCharacter";

    private static readonly HashSet<char> AllowedControlChars = new() { '\t', '\n', '\r' };

    public Task<MiddlewareValidationResult> ValidateAsync(string input, MiddlewareValidationContext context, CancellationToken ct)
    {
        var result = new MiddlewareValidationResult { IsValid = true };

        foreach (var c in input)
        {
            if (char.IsControl(c) && !AllowedControlChars.Contains(c))
            {
                result.Warnings.Add($"Input contains control character (U+{(int)c:X4})");
                if (context.StrictMode)
                {
                    result.IsValid = false;
                    result.Errors.Add(new MiddlewareValidationError
                    {
                        Code = "CONTROL_CHAR",
                        Message = $"Input contains disallowed control character (U+{(int)c:X4})",
                        Severity = ValidationSeverity.Warning
                    });
                }
            }
        }

        return Task.FromResult(result);
    }
}

/// <summary>
/// Detects path traversal attacks.
/// </summary>
public sealed class PathTraversalFilter : IValidationFilter
{
    public string Name => "PathTraversal";

    private static readonly Regex PathTraversalPattern = new(
        @"(\.\.[\\/])|" +                      // ../
        @"(\.\.%2[fF])|" +                     // URL encoded
        @"(\.\.%5[cC])|" +                     // URL encoded backslash
        @"(%2[eE]%2[eE][\\/])|" +              // Double URL encoded
        @"(\.%2[eE][\\/])|" +                  // Mixed encoding
        @"(%c0%ae)|" +                         // Overlong UTF-8
        @"(%c0%2f)",                           // Overlong UTF-8 slash
        RegexOptions.Compiled);

    public Task<MiddlewareValidationResult> ValidateAsync(string input, MiddlewareValidationContext context, CancellationToken ct)
    {
        var result = new MiddlewareValidationResult { IsValid = true };

        if (PathTraversalPattern.IsMatch(input))
        {
            result.IsValid = false;
            result.Errors.Add(new MiddlewareValidationError
            {
                Code = "PATH_TRAVERSAL",
                Message = "Input contains path traversal sequence",
                Severity = ValidationSeverity.Critical
            });
        }

        return Task.FromResult(result);
    }
}

/// <summary>
/// Detects XSS (Cross-Site Scripting) attacks.
/// </summary>
public sealed class XssFilter : IValidationFilter
{
    public string Name => "XSS";

    private static readonly Regex XssPattern = new(
        @"(?i)" +
        @"(<script[^>]*>)|" +                  // Script tags
        @"(</script>)|" +
        @"(javascript:)|" +                     // JavaScript protocol
        @"(vbscript:)|" +                       // VBScript protocol
        @"(on\w+\s*=)|" +                       // Event handlers
        @"(<iframe[^>]*>)|" +                   // iframes
        @"(<object[^>]*>)|" +                   // Object tags
        @"(<embed[^>]*>)|" +                    // Embed tags
        @"(<applet[^>]*>)|" +                   // Applet tags
        @"(<form[^>]*>)|" +                     // Form tags
        @"(<meta[^>]*>)|" +                     // Meta tags
        @"(<link[^>]*>)|" +                     // Link tags
        @"(<style[^>]*>)|" +                    // Style tags
        @"(expression\s*\()|" +                 // CSS expression
        @"(url\s*\([^)]*javascript:)",          // CSS url with JS
        RegexOptions.Compiled);

    public Task<MiddlewareValidationResult> ValidateAsync(string input, MiddlewareValidationContext context, CancellationToken ct)
    {
        var result = new MiddlewareValidationResult { IsValid = true };

        if (XssPattern.IsMatch(input))
        {
            result.IsValid = false;
            result.Errors.Add(new MiddlewareValidationError
            {
                Code = "XSS",
                Message = "Input contains potential XSS payload",
                Severity = ValidationSeverity.Critical
            });
        }

        return Task.FromResult(result);
    }
}

/// <summary>
/// Detects SQL injection attacks.
/// </summary>
public sealed class SqlInjectionFilter : IValidationFilter
{
    public string Name => "SQLInjection";

    private readonly SqlSecurityAnalyzer _analyzer = new();

    public Task<MiddlewareValidationResult> ValidateAsync(string input, MiddlewareValidationContext context, CancellationToken ct)
    {
        var result = new MiddlewareValidationResult { IsValid = true };

        var analysisResult = _analyzer.Analyze(input);
        if (!analysisResult.IsValid)
        {
            result.IsValid = false;
            foreach (var threat in analysisResult.Threats)
            {
                result.Errors.Add(new MiddlewareValidationError
                {
                    Code = "SQL_INJECTION",
                    Message = threat.Description,
                    Severity = threat.Severity == ThreatSeverity.Critical
                        ? ValidationSeverity.Critical
                        : ValidationSeverity.Error
                });
            }
        }

        return Task.FromResult(result);
    }
}

/// <summary>
/// Detects command injection attacks.
/// </summary>
public sealed class CommandInjectionFilter : IValidationFilter
{
    public string Name => "CommandInjection";

    private static readonly Regex CommandInjectionPattern = new(
        @"[;&|`$]|" +                          // Shell metacharacters
        @"(\|\|)|" +                           // OR operator
        @"(&&)|" +                             // AND operator
        @"(\$\()|" +                           // Command substitution
        @"(`)|" +                              // Backtick substitution
        @"(\$\{)|" +                           // Variable expansion
        @"(>)|" +                              // Output redirect
        @"(<)|" +                              // Input redirect
        @"(\r\n)|" +                           // CRLF
        @"(%0[aAdD])",                         // Encoded newline
        RegexOptions.Compiled);

    public Task<MiddlewareValidationResult> ValidateAsync(string input, MiddlewareValidationContext context, CancellationToken ct)
    {
        var result = new MiddlewareValidationResult { IsValid = true };

        if (CommandInjectionPattern.IsMatch(input))
        {
            result.IsValid = false;
            result.Errors.Add(new MiddlewareValidationError
            {
                Code = "COMMAND_INJECTION",
                Message = "Input contains potential command injection characters",
                Severity = ValidationSeverity.Critical
            });
        }

        return Task.FromResult(result);
    }
}

/// <summary>
/// Detects LDAP injection attacks.
/// </summary>
public sealed class LdapInjectionFilter : IValidationFilter
{
    public string Name => "LDAPInjection";

    private static readonly Regex LdapInjectionPattern = new(
        @"[()\\*\x00]|" +                      // LDAP special chars
        @"(\|\()|" +                           // OR filter
        @"(&\()|" +                            // AND filter
        @"(!\()",                              // NOT filter
        RegexOptions.Compiled);

    public Task<MiddlewareValidationResult> ValidateAsync(string input, MiddlewareValidationContext context, CancellationToken ct)
    {
        var result = new MiddlewareValidationResult { IsValid = true };

        if (LdapInjectionPattern.IsMatch(input))
        {
            result.Warnings.Add("Input contains LDAP special characters");
            if (context.StrictMode)
            {
                result.IsValid = false;
                result.Errors.Add(new MiddlewareValidationError
                {
                    Code = "LDAP_INJECTION",
                    Message = "Input contains potential LDAP injection characters",
                    Severity = ValidationSeverity.Warning
                });
            }
        }

        return Task.FromResult(result);
    }
}

/// <summary>
/// Detects XML injection and XXE attacks.
/// </summary>
public sealed class XmlInjectionFilter : IValidationFilter
{
    public string Name => "XMLInjection";

    private static readonly Regex XmlInjectionPattern = new(
        @"(?i)" +
        @"(<!DOCTYPE)|" +                      // DOCTYPE declaration
        @"(<!ENTITY)|" +                       // Entity declaration
        @"(<!\[CDATA\[)|" +                    // CDATA section
        @"(SYSTEM\s+[""'])|" +                 // External entity
        @"(PUBLIC\s+[""'])|" +                 // Public entity
        @"(&[a-zA-Z]+;)|" +                    // Named entity
        @"(&#\d+;)|" +                         // Numeric entity
        @"(&#x[0-9a-fA-F]+;)",                 // Hex entity
        RegexOptions.Compiled);

    public Task<MiddlewareValidationResult> ValidateAsync(string input, MiddlewareValidationContext context, CancellationToken ct)
    {
        var result = new MiddlewareValidationResult { IsValid = true };

        if (XmlInjectionPattern.IsMatch(input))
        {
            result.IsValid = false;
            result.Errors.Add(new MiddlewareValidationError
            {
                Code = "XML_INJECTION",
                Message = "Input contains potential XML injection or XXE payload",
                Severity = ValidationSeverity.Critical
            });
        }

        return Task.FromResult(result);
    }
}

#endregion

#region Configuration and Types

/// <summary>
/// Configuration for validation middleware.
/// </summary>
public sealed class ValidationMiddlewareConfig
{
    public bool EnableSqlValidation { get; init; } = true;
    public bool AllowNull { get; init; }
    public bool FailFast { get; init; } = true;
    public int MaxInputLength { get; init; } = 1_000_000;
    public bool LogValidationFailures { get; init; } = true;

    public static ValidationMiddlewareConfig Default => new();

    public static ValidationMiddlewareConfig Strict => new()
    {
        EnableSqlValidation = true,
        AllowNull = false,
        FailFast = true,
        MaxInputLength = 100_000,
        LogValidationFailures = true
    };
}

/// <summary>
/// Context for middleware validation operations.
/// </summary>
public sealed class MiddlewareValidationContext
{
    public required string Source { get; init; }
    public string? UserId { get; init; }
    public string? TenantId { get; init; }
    public string? Operation { get; init; }
    public bool StrictMode { get; init; }
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Result of middleware validation.
/// </summary>
public sealed class MiddlewareValidationResult
{
    public bool IsValid { get; set; }
    public List<MiddlewareValidationError> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public DateTime ValidatedAt { get; init; }
    public TimeSpan Duration { get; set; }
    public MiddlewareValidationContext? Context { get; init; }
}

/// <summary>
/// Middleware validation error.
/// </summary>
public sealed class MiddlewareValidationError
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public ValidationSeverity Severity { get; init; }
    public string? Field { get; init; }
}

/// <summary>
/// Validation severity levels.
/// </summary>
public enum ValidationSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
    Critical = 3
}

/// <summary>
/// Validation metrics for a source.
/// </summary>
public sealed class ValidationMetrics
{
    public required string Source { get; init; }
    public long TotalValidations { get; set; }
    public long SuccessfulValidations { get; set; }
    public long FailedValidations { get; set; }
    public TimeSpan TotalDuration { get; set; }
    public DateTime LastValidation { get; set; }
    public double AverageDurationMs => TotalValidations > 0
        ? TotalDuration.TotalMilliseconds / TotalValidations
        : 0;
}

/// <summary>
/// Validation statistics.
/// </summary>
public sealed class ValidationStats
{
    public long TotalValidations { get; init; }
    public long FailedValidations { get; init; }
    public double SuccessRate { get; init; }
    public Dictionary<string, ValidationMetrics> MetricsBySource { get; init; } = new();
}

#endregion
