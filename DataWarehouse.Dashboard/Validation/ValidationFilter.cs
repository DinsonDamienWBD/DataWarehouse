using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace DataWarehouse.Dashboard.Validation;

/// <summary>
/// Model validation action filter that provides consistent validation responses.
/// </summary>
public sealed class ValidationFilter : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ModelState.IsValid)
        {
            var errors = context.ModelState
                .Where(e => e.Value?.Errors.Count > 0)
                .ToDictionary(
                    e => ToCamelCase(e.Key),
                    e => e.Value!.Errors.Select(er => er.ErrorMessage).ToArray()
                );

            var response = new ValidationProblemDetails(context.ModelState)
            {
                Type = "https://tools.ietf.org/html/rfc7231#section-6.5.1",
                Title = "One or more validation errors occurred.",
                Status = StatusCodes.Status400BadRequest,
                Instance = context.HttpContext.Request.Path
            };

            context.Result = new BadRequestObjectResult(new
            {
                type = response.Type,
                title = response.Title,
                status = response.Status,
                errors = errors,
                traceId = context.HttpContext.TraceIdentifier
            });
        }
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
        // No post-action processing needed
    }

    private static string ToCamelCase(string str)
    {
        if (string.IsNullOrEmpty(str) || char.IsLower(str[0]))
            return str;

        return char.ToLowerInvariant(str[0]) + str[1..];
    }
}

/// <summary>
/// Validates that a string does not contain any potentially dangerous content.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class SafeStringAttribute : ValidationAttribute
{
    private static readonly Regex DangerousPatterns = new(
        @"<script|javascript:|data:|vbscript:|on\w+\s*=",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public SafeStringAttribute() : base("The field {0} contains potentially dangerous content.")
    {
    }

    public override bool IsValid(object? value)
    {
        if (value is not string str)
            return true;

        return !DangerousPatterns.IsMatch(str);
    }
}

/// <summary>
/// Validates that a string is a valid identifier (alphanumeric with underscores/hyphens).
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class ValidIdentifierAttribute : ValidationAttribute
{
    private static readonly Regex IdentifierPattern = new(
        @"^[a-zA-Z][a-zA-Z0-9_-]*$",
        RegexOptions.Compiled);

    public int MinLength { get; set; } = 1;
    public int MaxLength { get; set; } = 100;

    public ValidIdentifierAttribute() : base("The field {0} must be a valid identifier (letters, numbers, underscores, hyphens).")
    {
    }

    public override bool IsValid(object? value)
    {
        if (value is not string str)
            return true;

        if (str.Length < MinLength || str.Length > MaxLength)
            return false;

        return IdentifierPattern.IsMatch(str);
    }
}

/// <summary>
/// Validates that a byte size is within acceptable limits.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class ValidByteSizeAttribute : ValidationAttribute
{
    public long MinBytes { get; set; } = 0;
    public long MaxBytes { get; set; } = long.MaxValue;

    public ValidByteSizeAttribute() : base("The field {0} must be a valid byte size.")
    {
    }

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value == null)
            return ValidationResult.Success;

        long bytes = value switch
        {
            long l => l,
            int i => i,
            _ => -1
        };

        if (bytes < MinBytes)
            return new ValidationResult($"The field {validationContext.DisplayName} must be at least {FormatBytes(MinBytes)}.");

        if (bytes > MaxBytes)
            return new ValidationResult($"The field {validationContext.DisplayName} must be at most {FormatBytes(MaxBytes)}.");

        return ValidationResult.Success;
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB", "PB" };
        int order = 0;
        double len = bytes;

        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }

        return $"{len:0.##} {sizes[order]}";
    }
}

/// <summary>
/// Validates that a URI is well-formed and optionally restricted to certain schemes.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class ValidUriAttribute : ValidationAttribute
{
    public string[] AllowedSchemes { get; set; } = { "http", "https" };
    public bool RequireAbsolute { get; set; } = true;

    public ValidUriAttribute() : base("The field {0} must be a valid URI.")
    {
    }

    public override bool IsValid(object? value)
    {
        if (value is not string str || string.IsNullOrWhiteSpace(str))
            return true;

        if (!Uri.TryCreate(str, UriKind.Absolute, out var uri))
        {
            if (RequireAbsolute)
                return false;

            return Uri.TryCreate(str, UriKind.Relative, out _);
        }

        return AllowedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Validates that a dictionary contains only safe keys and values.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class SafeDictionaryAttribute : ValidationAttribute
{
    public int MaxEntries { get; set; } = 100;
    public int MaxKeyLength { get; set; } = 100;
    public int MaxValueLength { get; set; } = 10000;

    public SafeDictionaryAttribute() : base("The field {0} contains invalid configuration.")
    {
    }

    public override bool IsValid(object? value)
    {
        if (value == null)
            return true;

        if (value is IDictionary<string, object> dict)
        {
            if (dict.Count > MaxEntries)
                return false;

            foreach (var (key, val) in dict)
            {
                if (key.Length > MaxKeyLength)
                    return false;

                if (val is string strVal && strVal.Length > MaxValueLength)
                    return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Validates that a value is one of the allowed enum values.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class ValidEnumValueAttribute<TEnum> : ValidationAttribute where TEnum : struct, Enum
{
    public ValidEnumValueAttribute() : base($"The field {{0}} must be a valid {typeof(TEnum).Name} value.")
    {
    }

    public override bool IsValid(object? value)
    {
        if (value == null)
            return true;

        if (value is string str)
            return Enum.TryParse<TEnum>(str, ignoreCase: true, out _);

        if (value is TEnum)
            return Enum.IsDefined(typeof(TEnum), value);

        return false;
    }
}

/// <summary>
/// Validates that a collection has an acceptable number of items.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class ValidCollectionSizeAttribute : ValidationAttribute
{
    public int MinCount { get; set; } = 0;
    public int MaxCount { get; set; } = 1000;

    public ValidCollectionSizeAttribute() : base("The field {0} must contain between {1} and {2} items.")
    {
    }

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value == null)
            return ValidationResult.Success;

        int count = value switch
        {
            System.Collections.ICollection c => c.Count,
            System.Collections.IEnumerable e => e.Cast<object>().Count(),
            _ => -1
        };

        if (count < 0)
            return ValidationResult.Success;

        if (count < MinCount)
            return new ValidationResult($"The field {validationContext.DisplayName} must contain at least {MinCount} items.");

        if (count > MaxCount)
            return new ValidationResult($"The field {validationContext.DisplayName} must contain at most {MaxCount} items.");

        return ValidationResult.Success;
    }
}

/// <summary>
/// Extension methods for validation services.
/// </summary>
public static class ValidationExtensions
{
    /// <summary>
    /// Adds validation services to the MVC builder.
    /// </summary>
    public static IMvcBuilder AddValidation(this IMvcBuilder builder)
    {
        builder.AddMvcOptions(options =>
        {
            options.Filters.Add<ValidationFilter>();
        });

        return builder;
    }

    /// <summary>
    /// Validates an object and returns validation errors.
    /// </summary>
    public static IReadOnlyList<ValidationResult> Validate(object obj)
    {
        var results = new List<ValidationResult>();
        var context = new ValidationContext(obj);
        Validator.TryValidateObject(obj, context, results, validateAllProperties: true);
        return results;
    }

    /// <summary>
    /// Checks if an object is valid according to its validation attributes.
    /// </summary>
    public static bool IsValid(object obj, out IReadOnlyList<ValidationResult> errors)
    {
        var results = new List<ValidationResult>();
        var context = new ValidationContext(obj);
        var isValid = Validator.TryValidateObject(obj, context, results, validateAllProperties: true);
        errors = results;
        return isValid;
    }
}
