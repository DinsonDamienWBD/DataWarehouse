using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace DataWarehouse.SDK.Validation;

#region Validation Interfaces

/// <summary>
/// Centralized input validation service for all DataWarehouse operations.
/// Provides defense-in-depth validation at system boundaries.
/// </summary>
public interface IInputValidator
{
    /// <summary>
    /// Validates an object and returns validation results.
    /// </summary>
    ValidationResult Validate<T>(T input, ValidationContext? context = null) where T : class;

    /// <summary>
    /// Validates an object and throws if invalid.
    /// </summary>
    void ValidateAndThrow<T>(T input, ValidationContext? context = null) where T : class;

    /// <summary>
    /// Validates a specific value against rules.
    /// </summary>
    ValidationResult ValidateValue(object? value, string propertyName, params IValidationRule[] rules);

    /// <summary>
    /// Registers a custom validator for a type.
    /// </summary>
    void RegisterValidator<T>(ITypeValidator<T> validator) where T : class;
}

/// <summary>
/// Type-specific validator interface.
/// </summary>
public interface ITypeValidator<T> where T : class
{
    ValidationResult Validate(T input, ValidationContext? context);
}

/// <summary>
/// Individual validation rule.
/// </summary>
public interface IValidationRule
{
    string RuleName { get; }
    ValidationRuleResult Validate(object? value, string propertyName);
}

#endregion

#region Validation Results

/// <summary>
/// Result of a validation operation.
/// </summary>
public sealed class ValidationResult
{
    public bool IsValid => Errors.Count == 0;
    public List<ValidationError> Errors { get; } = new();
    public List<ValidationWarning> Warnings { get; } = new();

    public static ValidationResult Success() => new();

    public static ValidationResult Failure(string propertyName, string errorMessage, string? errorCode = null)
    {
        var result = new ValidationResult();
        result.Errors.Add(new ValidationError
        {
            PropertyName = propertyName,
            ErrorMessage = errorMessage,
            ErrorCode = errorCode ?? "VALIDATION_ERROR"
        });
        return result;
    }

    public ValidationResult AddError(string propertyName, string errorMessage, string? errorCode = null)
    {
        Errors.Add(new ValidationError
        {
            PropertyName = propertyName,
            ErrorMessage = errorMessage,
            ErrorCode = errorCode ?? "VALIDATION_ERROR"
        });
        return this;
    }

    public ValidationResult AddWarning(string propertyName, string message)
    {
        Warnings.Add(new ValidationWarning
        {
            PropertyName = propertyName,
            Message = message
        });
        return this;
    }

    public ValidationResult Merge(ValidationResult other)
    {
        Errors.AddRange(other.Errors);
        Warnings.AddRange(other.Warnings);
        return this;
    }
}

public sealed class ValidationError
{
    public string PropertyName { get; init; } = string.Empty;
    public string ErrorMessage { get; init; } = string.Empty;
    public string ErrorCode { get; init; } = string.Empty;
    public object? AttemptedValue { get; init; }
}

public sealed class ValidationWarning
{
    public string PropertyName { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public sealed class ValidationRuleResult
{
    public bool IsValid { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorCode { get; init; }

    public static ValidationRuleResult Success() => new() { IsValid = true };
    public static ValidationRuleResult Failure(string message, string? code = null) => new()
    {
        IsValid = false,
        ErrorMessage = message,
        ErrorCode = code
    };
}

/// <summary>
/// Context for validation operations.
/// </summary>
public sealed class ValidationContext
{
    public string? TenantId { get; init; }
    public string? UserId { get; init; }
    public bool StrictMode { get; init; } = true;
    public Dictionary<string, object> Items { get; } = new();
}

#endregion

#region Validation Exception

/// <summary>
/// Exception thrown when validation fails.
/// </summary>
public sealed class ValidationException : Exception
{
    public ValidationResult ValidationResult { get; }
    public string[] ErrorMessages => ValidationResult.Errors.Select(e => e.ErrorMessage).ToArray();

    public ValidationException(ValidationResult result)
        : base($"Validation failed with {result.Errors.Count} error(s): {string.Join("; ", result.Errors.Select(e => e.ErrorMessage))}")
    {
        ValidationResult = result;
    }

    public ValidationException(string propertyName, string errorMessage)
        : this(ValidationResult.Failure(propertyName, errorMessage))
    {
    }
}

#endregion

#region Input Validator Implementation

/// <summary>
/// Production-ready input validator with comprehensive rule support.
/// Thread-safe and extensible.
/// </summary>
public sealed class InputValidator : IInputValidator
{
    private readonly ConcurrentDictionary<Type, object> _validators = new();
    private readonly InputValidatorConfig _config;

    public InputValidator(InputValidatorConfig? config = null)
    {
        _config = config ?? new InputValidatorConfig();
    }

    public ValidationResult Validate<T>(T input, ValidationContext? context = null) where T : class
    {
        ArgumentNullException.ThrowIfNull(input);

        var result = new ValidationResult();

        // Check for registered type validator
        if (_validators.TryGetValue(typeof(T), out var validator))
        {
            var typeValidator = (ITypeValidator<T>)validator;
            result.Merge(typeValidator.Validate(input, context));
        }

        // Validate using data annotations
        if (_config.UseDataAnnotations)
        {
            var annotationResults = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
            var annotationContext = new System.ComponentModel.DataAnnotations.ValidationContext(input);

            if (!Validator.TryValidateObject(input, annotationContext, annotationResults, validateAllProperties: true))
            {
                foreach (var annotationResult in annotationResults)
                {
                    result.AddError(
                        annotationResult.MemberNames.FirstOrDefault() ?? "Unknown",
                        annotationResult.ErrorMessage ?? "Validation failed",
                        "DATA_ANNOTATION_ERROR"
                    );
                }
            }
        }

        // Apply built-in security validations
        ValidateSecurityRules(input, result, context);

        return result;
    }

    public void ValidateAndThrow<T>(T input, ValidationContext? context = null) where T : class
    {
        var result = Validate(input, context);
        if (!result.IsValid)
        {
            throw new ValidationException(result);
        }
    }

    public ValidationResult ValidateValue(object? value, string propertyName, params IValidationRule[] rules)
    {
        var result = new ValidationResult();

        foreach (var rule in rules)
        {
            var ruleResult = rule.Validate(value, propertyName);
            if (!ruleResult.IsValid)
            {
                result.AddError(propertyName, ruleResult.ErrorMessage ?? "Validation failed", ruleResult.ErrorCode);
            }
        }

        return result;
    }

    public void RegisterValidator<T>(ITypeValidator<T> validator) where T : class
    {
        _validators[typeof(T)] = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    private void ValidateSecurityRules<T>(T input, ValidationResult result, ValidationContext? context) where T : class
    {
        var properties = typeof(T).GetProperties();

        foreach (var property in properties)
        {
            var value = property.GetValue(input);
            if (value == null) continue;

            var stringValue = value as string;
            if (stringValue == null) continue;

            // Check for SQL injection patterns
            if (_config.DetectSqlInjection && SecurityRules.ContainsSqlInjection(stringValue))
            {
                result.AddError(property.Name, "Potential SQL injection detected", "SQL_INJECTION");
            }

            // Check for XSS patterns
            if (_config.DetectXss && SecurityRules.ContainsXss(stringValue))
            {
                result.AddError(property.Name, "Potential XSS attack detected", "XSS_ATTACK");
            }

            // Check for path traversal
            if (_config.DetectPathTraversal && SecurityRules.ContainsPathTraversal(stringValue))
            {
                result.AddError(property.Name, "Path traversal attempt detected", "PATH_TRAVERSAL");
            }

            // Check for command injection
            if (_config.DetectCommandInjection && SecurityRules.ContainsCommandInjection(stringValue))
            {
                result.AddError(property.Name, "Potential command injection detected", "COMMAND_INJECTION");
            }

            // Check string length limits
            if (_config.MaxStringLength > 0 && stringValue.Length > _config.MaxStringLength)
            {
                result.AddError(property.Name, $"String exceeds maximum length of {_config.MaxStringLength}", "STRING_TOO_LONG");
            }
        }
    }
}

/// <summary>
/// Configuration for input validation.
/// </summary>
public sealed class InputValidatorConfig
{
    public bool UseDataAnnotations { get; set; } = true;
    public bool DetectSqlInjection { get; set; } = true;
    public bool DetectXss { get; set; } = true;
    public bool DetectPathTraversal { get; set; } = true;
    public bool DetectCommandInjection { get; set; } = true;
    public int MaxStringLength { get; set; } = 10000;
    public bool StrictMode { get; set; } = true;
}

#endregion

#region Security Rules

/// <summary>
/// Security validation rules for detecting common attack patterns.
/// </summary>
public static class SecurityRules
{
    // SQL Injection patterns
    private static readonly Regex SqlInjectionPattern = new(
        @"(\b(SELECT|INSERT|UPDATE|DELETE|DROP|CREATE|ALTER|EXEC|EXECUTE|UNION|TRUNCATE|DECLARE|CAST|CONVERT)\b.*\b(FROM|INTO|TABLE|DATABASE|WHERE|SET|VALUES)\b)|" +
        @"(--|#|/\*|\*/|;|\bOR\b\s+\d+\s*=\s*\d+|\bAND\b\s+\d+\s*=\s*\d+|'\s*OR\s*'|'\s*AND\s*')",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    // XSS patterns
    private static readonly Regex XssPattern = new(
        @"(<script[^>]*>|</script>|javascript:|on\w+\s*=|<iframe|<object|<embed|<link[^>]*href|<img[^>]*onerror|<svg[^>]*onload)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    // Path traversal patterns
    private static readonly Regex PathTraversalPattern = new(
        @"(\.\.[\\/]|\.\.%2[fF]|\.\.%5[cC]|%2e%2e[\\/]|%252e%252e[\\/])",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    // Command injection patterns
    private static readonly Regex CommandInjectionPattern = new(
        @"([;&|`$]|\$\(|`.*`|\|{1,2}|>{1,2}|<{1,2}|\beval\b|\bexec\b|\bsystem\b|\bshell_exec\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    // LDAP injection patterns
    private static readonly Regex LdapInjectionPattern = new(
        @"[()\\*\x00]|%00|%28|%29|%5c|%2a",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    // XML injection patterns
    private static readonly Regex XmlInjectionPattern = new(
        @"<!ENTITY|<!DOCTYPE|SYSTEM\s*[""']|PUBLIC\s*[""']|\]\]>|<!\[CDATA\[",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    public static bool ContainsSqlInjection(string input)
    {
        if (string.IsNullOrEmpty(input)) return false;
        try
        {
            return SqlInjectionPattern.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return true; // Treat timeout as suspicious
        }
    }

    public static bool ContainsXss(string input)
    {
        if (string.IsNullOrEmpty(input)) return false;
        try
        {
            return XssPattern.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    public static bool ContainsPathTraversal(string input)
    {
        if (string.IsNullOrEmpty(input)) return false;
        try
        {
            return PathTraversalPattern.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    public static bool ContainsCommandInjection(string input)
    {
        if (string.IsNullOrEmpty(input)) return false;
        try
        {
            return CommandInjectionPattern.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    public static bool ContainsLdapInjection(string input)
    {
        if (string.IsNullOrEmpty(input)) return false;
        try
        {
            return LdapInjectionPattern.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    public static bool ContainsXmlInjection(string input)
    {
        if (string.IsNullOrEmpty(input)) return false;
        try
        {
            return XmlInjectionPattern.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    /// <summary>
    /// Sanitizes input by removing dangerous patterns.
    /// </summary>
    public static string Sanitize(string input, SanitizationOptions options)
    {
        if (string.IsNullOrEmpty(input)) return input;

        var result = input;

        if (options.HasFlag(SanitizationOptions.HtmlEncode))
        {
            result = System.Net.WebUtility.HtmlEncode(result);
        }

        if (options.HasFlag(SanitizationOptions.RemoveScriptTags))
        {
            result = Regex.Replace(result, @"<script[^>]*>.*?</script>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromMilliseconds(100));
        }

        if (options.HasFlag(SanitizationOptions.RemoveNullBytes))
        {
            result = result.Replace("\0", "");
        }

        if (options.HasFlag(SanitizationOptions.TrimWhitespace))
        {
            result = result.Trim();
        }

        if (options.HasFlag(SanitizationOptions.NormalizeNewlines))
        {
            result = result.Replace("\r\n", "\n").Replace("\r", "\n");
        }

        return result;
    }
}

[Flags]
public enum SanitizationOptions
{
    None = 0,
    HtmlEncode = 1,
    RemoveScriptTags = 2,
    RemoveNullBytes = 4,
    TrimWhitespace = 8,
    NormalizeNewlines = 16,
    All = HtmlEncode | RemoveScriptTags | RemoveNullBytes | TrimWhitespace | NormalizeNewlines
}

#endregion

#region Common Validation Rules

/// <summary>
/// Common validation rules for reuse.
/// </summary>
public static class ValidationRules
{
    /// <summary>
    /// Validates that a value is not null or empty.
    /// </summary>
    public static IValidationRule Required() => new RequiredRule();

    /// <summary>
    /// Validates string length.
    /// </summary>
    public static IValidationRule StringLength(int min, int max) => new StringLengthRule(min, max);

    /// <summary>
    /// Validates against a regex pattern.
    /// </summary>
    public static IValidationRule Pattern(string regex, string? errorMessage = null) => new PatternRule(regex, errorMessage);

    /// <summary>
    /// Validates email format.
    /// </summary>
    public static IValidationRule Email() => new EmailRule();

    /// <summary>
    /// Validates URL format.
    /// </summary>
    public static IValidationRule Url() => new UrlRule();

    /// <summary>
    /// Validates numeric range.
    /// </summary>
    public static IValidationRule Range<T>(T min, T max) where T : IComparable<T> => new RangeRule<T>(min, max);

    /// <summary>
    /// Validates that string contains no dangerous patterns.
    /// </summary>
    public static IValidationRule SafeString() => new SafeStringRule();

    /// <summary>
    /// Validates that a path is safe (no traversal).
    /// </summary>
    public static IValidationRule SafePath() => new SafePathRule();

    /// <summary>
    /// Custom validation rule.
    /// </summary>
    public static IValidationRule Custom(Func<object?, bool> predicate, string errorMessage) => new CustomRule(predicate, errorMessage);
}

internal class RequiredRule : IValidationRule
{
    public string RuleName => "Required";

    public ValidationRuleResult Validate(object? value, string propertyName)
    {
        if (value == null)
            return ValidationRuleResult.Failure($"{propertyName} is required", "REQUIRED");

        if (value is string str && string.IsNullOrWhiteSpace(str))
            return ValidationRuleResult.Failure($"{propertyName} cannot be empty", "REQUIRED");

        return ValidationRuleResult.Success();
    }
}

internal class StringLengthRule : IValidationRule
{
    private readonly int _min;
    private readonly int _max;

    public StringLengthRule(int min, int max)
    {
        _min = min;
        _max = max;
    }

    public string RuleName => "StringLength";

    public ValidationRuleResult Validate(object? value, string propertyName)
    {
        if (value is not string str) return ValidationRuleResult.Success();

        if (str.Length < _min)
            return ValidationRuleResult.Failure($"{propertyName} must be at least {_min} characters", "STRING_TOO_SHORT");

        if (str.Length > _max)
            return ValidationRuleResult.Failure($"{propertyName} must not exceed {_max} characters", "STRING_TOO_LONG");

        return ValidationRuleResult.Success();
    }
}

internal class PatternRule : IValidationRule
{
    private readonly Regex _regex;
    private readonly string? _errorMessage;

    public PatternRule(string pattern, string? errorMessage)
    {
        _regex = new Regex(pattern, RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
        _errorMessage = errorMessage;
    }

    public string RuleName => "Pattern";

    public ValidationRuleResult Validate(object? value, string propertyName)
    {
        if (value is not string str) return ValidationRuleResult.Success();
        if (string.IsNullOrEmpty(str)) return ValidationRuleResult.Success();

        try
        {
            if (!_regex.IsMatch(str))
                return ValidationRuleResult.Failure(_errorMessage ?? $"{propertyName} does not match required pattern", "PATTERN_MISMATCH");
        }
        catch (RegexMatchTimeoutException)
        {
            return ValidationRuleResult.Failure($"{propertyName} validation timed out", "VALIDATION_TIMEOUT");
        }

        return ValidationRuleResult.Success();
    }
}

internal class EmailRule : IValidationRule
{
    private static readonly Regex EmailRegex = new(
        @"^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}$",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    public string RuleName => "Email";

    public ValidationRuleResult Validate(object? value, string propertyName)
    {
        if (value is not string str) return ValidationRuleResult.Success();
        if (string.IsNullOrEmpty(str)) return ValidationRuleResult.Success();

        if (!EmailRegex.IsMatch(str))
            return ValidationRuleResult.Failure($"{propertyName} is not a valid email address", "INVALID_EMAIL");

        return ValidationRuleResult.Success();
    }
}

internal class UrlRule : IValidationRule
{
    public string RuleName => "Url";

    public ValidationRuleResult Validate(object? value, string propertyName)
    {
        if (value is not string str) return ValidationRuleResult.Success();
        if (string.IsNullOrEmpty(str)) return ValidationRuleResult.Success();

        if (!Uri.TryCreate(str, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return ValidationRuleResult.Failure($"{propertyName} is not a valid URL", "INVALID_URL");
        }

        return ValidationRuleResult.Success();
    }
}

internal class RangeRule<T> : IValidationRule where T : IComparable<T>
{
    private readonly T _min;
    private readonly T _max;

    public RangeRule(T min, T max)
    {
        _min = min;
        _max = max;
    }

    public string RuleName => "Range";

    public ValidationRuleResult Validate(object? value, string propertyName)
    {
        if (value == null) return ValidationRuleResult.Success();

        if (value is T typedValue)
        {
            if (typedValue.CompareTo(_min) < 0)
                return ValidationRuleResult.Failure($"{propertyName} must be at least {_min}", "BELOW_MINIMUM");

            if (typedValue.CompareTo(_max) > 0)
                return ValidationRuleResult.Failure($"{propertyName} must not exceed {_max}", "ABOVE_MAXIMUM");
        }

        return ValidationRuleResult.Success();
    }
}

internal class SafeStringRule : IValidationRule
{
    public string RuleName => "SafeString";

    public ValidationRuleResult Validate(object? value, string propertyName)
    {
        if (value is not string str) return ValidationRuleResult.Success();
        if (string.IsNullOrEmpty(str)) return ValidationRuleResult.Success();

        if (SecurityRules.ContainsSqlInjection(str))
            return ValidationRuleResult.Failure($"{propertyName} contains potentially dangerous SQL content", "SQL_INJECTION");

        if (SecurityRules.ContainsXss(str))
            return ValidationRuleResult.Failure($"{propertyName} contains potentially dangerous script content", "XSS_ATTACK");

        if (SecurityRules.ContainsCommandInjection(str))
            return ValidationRuleResult.Failure($"{propertyName} contains potentially dangerous command content", "COMMAND_INJECTION");

        return ValidationRuleResult.Success();
    }
}

internal class SafePathRule : IValidationRule
{
    public string RuleName => "SafePath";

    public ValidationRuleResult Validate(object? value, string propertyName)
    {
        if (value is not string str) return ValidationRuleResult.Success();
        if (string.IsNullOrEmpty(str)) return ValidationRuleResult.Success();

        if (SecurityRules.ContainsPathTraversal(str))
            return ValidationRuleResult.Failure($"{propertyName} contains path traversal attempt", "PATH_TRAVERSAL");

        // Check for absolute paths when not allowed
        if (Path.IsPathRooted(str) && str.Contains(".."))
            return ValidationRuleResult.Failure($"{propertyName} contains suspicious path", "SUSPICIOUS_PATH");

        return ValidationRuleResult.Success();
    }
}

internal class CustomRule : IValidationRule
{
    private readonly Func<object?, bool> _predicate;
    private readonly string _errorMessage;

    public CustomRule(Func<object?, bool> predicate, string errorMessage)
    {
        _predicate = predicate;
        _errorMessage = errorMessage;
    }

    public string RuleName => "Custom";

    public ValidationRuleResult Validate(object? value, string propertyName)
    {
        if (!_predicate(value))
            return ValidationRuleResult.Failure(_errorMessage, "CUSTOM_VALIDATION");

        return ValidationRuleResult.Success();
    }
}

#endregion

#region Fluent Validation Builder

/// <summary>
/// Fluent builder for creating validation rules.
/// </summary>
public sealed class ValidationBuilder<T> where T : class
{
    private readonly List<(string PropertyName, IValidationRule[] Rules)> _propertyRules = new();

    public ValidationBuilder<T> RuleFor<TProperty>(
        System.Linq.Expressions.Expression<Func<T, TProperty>> propertyExpression,
        params IValidationRule[] rules)
    {
        var propertyName = GetPropertyName(propertyExpression);
        _propertyRules.Add((propertyName, rules));
        return this;
    }

    public ITypeValidator<T> Build()
    {
        return new BuiltValidator<T>(_propertyRules);
    }

    private static string GetPropertyName<TProperty>(System.Linq.Expressions.Expression<Func<T, TProperty>> expression)
    {
        if (expression.Body is System.Linq.Expressions.MemberExpression member)
            return member.Member.Name;

        throw new ArgumentException("Expression must be a property access expression");
    }
}

internal class BuiltValidator<T> : ITypeValidator<T> where T : class
{
    private readonly List<(string PropertyName, IValidationRule[] Rules)> _propertyRules;

    public BuiltValidator(List<(string PropertyName, IValidationRule[] Rules)> propertyRules)
    {
        _propertyRules = propertyRules;
    }

    public ValidationResult Validate(T input, ValidationContext? context)
    {
        var result = new ValidationResult();

        foreach (var (propertyName, rules) in _propertyRules)
        {
            var property = typeof(T).GetProperty(propertyName);
            if (property == null) continue;

            var value = property.GetValue(input);

            foreach (var rule in rules)
            {
                var ruleResult = rule.Validate(value, propertyName);
                if (!ruleResult.IsValid)
                {
                    result.AddError(propertyName, ruleResult.ErrorMessage ?? "Validation failed", ruleResult.ErrorCode);
                }
            }
        }

        return result;
    }
}

#endregion
