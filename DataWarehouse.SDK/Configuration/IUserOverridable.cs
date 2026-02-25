using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DataWarehouse.SDK.Configuration;

// ============================================================================
// USER-OVERRIDABLE CONFIGURATION INTERFACE
// Applied to features/strategies that can be configured by users.
// Supports Instance -> Tenant -> User hierarchy with override control.
// ============================================================================

#region Parameter Types

/// <summary>
/// Supported parameter types for configurable parameters.
/// </summary>
public enum ConfigurableParameterType
{
    /// <summary>Free-form string value.</summary>
    String = 0,

    /// <summary>32-bit integer value.</summary>
    Int = 1,

    /// <summary>64-bit floating point value.</summary>
    Double = 2,

    /// <summary>Boolean true/false value.</summary>
    Bool = 3,

    /// <summary>Enumeration value (must use AllowedValues).</summary>
    Enum = 4,

    /// <summary>List of string values.</summary>
    StringList = 5,

    /// <summary>64-bit integer value.</summary>
    Long = 6,

    /// <summary>TimeSpan duration value.</summary>
    Duration = 7
}

/// <summary>
/// Describes a configurable parameter on a feature or strategy.
/// Used for UI rendering, validation, and configuration hierarchy enforcement.
/// </summary>
public sealed record ConfigurableParameter
{
    /// <summary>
    /// Machine-readable parameter name (e.g., "compressionLevel", "maxRetries").
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Human-readable display name for UI (e.g., "Compression Level").
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Description of what this parameter controls.
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// The data type of this parameter.
    /// </summary>
    public ConfigurableParameterType ParameterType { get; init; } = ConfigurableParameterType.String;

    /// <summary>
    /// Default value when no configuration is applied.
    /// </summary>
    public object? DefaultValue { get; init; }

    /// <summary>
    /// Current effective value (resolved through hierarchy).
    /// </summary>
    public object? CurrentValue { get; init; }

    /// <summary>
    /// Whether users can override this parameter.
    /// When false, only Instance-level configuration applies.
    /// </summary>
    public bool AllowUserToOverride { get; init; } = true;

    /// <summary>
    /// Whether tenants can override this parameter.
    /// When false, only Instance-level configuration applies.
    /// </summary>
    public bool AllowTenantToOverride { get; init; } = true;

    /// <summary>
    /// Minimum value for numeric parameters (Int, Double, Long).
    /// Null means no minimum constraint.
    /// </summary>
    public object? MinValue { get; init; }

    /// <summary>
    /// Maximum value for numeric parameters (Int, Double, Long).
    /// Null means no maximum constraint.
    /// </summary>
    public object? MaxValue { get; init; }

    /// <summary>
    /// Allowed values for Enum or restricted String parameters.
    /// Null or empty means no restriction.
    /// </summary>
    public IReadOnlyList<string>? AllowedValues { get; init; }

    /// <summary>
    /// Regex pattern for string validation.
    /// Null means no pattern constraint.
    /// </summary>
    public string? RegexPattern { get; init; }

    /// <summary>
    /// Category for UI grouping (e.g., "Performance", "Security", "Storage").
    /// </summary>
    public string Category { get; init; } = "General";

    /// <summary>
    /// Whether this parameter is required (must have a value).
    /// </summary>
    public bool IsRequired { get; init; }

    /// <summary>
    /// Display order within category (lower = first).
    /// </summary>
    public int DisplayOrder { get; init; }

    /// <summary>
    /// Whether changing this parameter requires a restart.
    /// </summary>
    public bool RequiresRestart { get; init; }

    /// <summary>
    /// Unit label for display (e.g., "MB", "seconds", "%").
    /// </summary>
    public string? Unit { get; init; }
}

#endregion

#region Configurable Attribute

/// <summary>
/// Marks a property on a strategy class as user-configurable.
/// The configuration system discovers these via reflection.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class ConfigurableAttribute : Attribute
{
    /// <summary>
    /// Human-readable display name. Defaults to the property name.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Description of what this parameter controls.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Whether users can override this parameter.
    /// </summary>
    public bool AllowUserToOverride { get; set; } = true;

    /// <summary>
    /// Whether tenants can override this parameter.
    /// </summary>
    public bool AllowTenantToOverride { get; set; } = true;

    /// <summary>
    /// Category for UI grouping.
    /// </summary>
    public string Category { get; set; } = "General";

    /// <summary>
    /// Minimum value for numeric parameters.
    /// </summary>
    public double Min { get; set; } = double.NaN;

    /// <summary>
    /// Maximum value for numeric parameters.
    /// </summary>
    public double Max { get; set; } = double.NaN;

    /// <summary>
    /// Comma-separated allowed values for enum/string parameters.
    /// </summary>
    public string? AllowedValues { get; set; }

    /// <summary>
    /// Regex pattern for string validation.
    /// </summary>
    public string? RegexPattern { get; set; }

    /// <summary>
    /// Whether changing this parameter requires a restart.
    /// </summary>
    public bool RequiresRestart { get; set; }

    /// <summary>
    /// Display order within category.
    /// </summary>
    public int DisplayOrder { get; set; }

    /// <summary>
    /// Unit label for display.
    /// </summary>
    public string? Unit { get; set; }
}

#endregion

#region IUserOverridable Interface

/// <summary>
/// Interface for features and strategies that support user-configurable parameters.
/// Implementations declare their configurable parameters and accept configuration changes.
/// </summary>
public interface IUserOverridable
{
    /// <summary>
    /// Gets the list of configurable parameters this feature supports.
    /// </summary>
    /// <returns>All configurable parameters with their current values and constraints.</returns>
    IReadOnlyList<ConfigurableParameter> GetConfigurableParameters();

    /// <summary>
    /// Applies the given configuration dictionary to this feature.
    /// Values are pre-validated by the configuration system.
    /// </summary>
    /// <param name="config">Key-value configuration to apply.</param>
    /// <exception cref="ConfigurationValidationException">If a value is invalid.</exception>
    void ApplyConfiguration(IReadOnlyDictionary<string, object> config);

    /// <summary>
    /// Gets the current effective configuration as key-value pairs.
    /// </summary>
    /// <returns>Current configuration values.</returns>
    IReadOnlyDictionary<string, object> GetCurrentConfiguration();
}

#endregion

#region Validation Exception

/// <summary>
/// Exception thrown when configuration validation fails.
/// </summary>
public sealed class ConfigurationValidationException : Exception
{
    /// <summary>
    /// The parameter name that failed validation.
    /// </summary>
    public string ParameterName { get; }

    /// <summary>
    /// The rejected value.
    /// </summary>
    public object? RejectedValue { get; }

    /// <summary>
    /// Validation error details.
    /// </summary>
    public IReadOnlyList<string> ValidationErrors { get; }

    public ConfigurationValidationException(
        string parameterName,
        object? rejectedValue,
        IReadOnlyList<string> validationErrors)
        : base($"Configuration validation failed for '{parameterName}': {string.Join("; ", validationErrors)}")
    {
        ParameterName = parameterName;
        RejectedValue = rejectedValue;
        ValidationErrors = validationErrors;
    }

    public ConfigurationValidationException(
        string parameterName,
        object? rejectedValue,
        string validationError)
        : this(parameterName, rejectedValue, new[] { validationError })
    {
    }
}

#endregion

#region Parameter Validator

/// <summary>
/// Validates parameter values against their constraints.
/// </summary>
public static class ParameterValidator
{
    /// <summary>
    /// Validates a value against its parameter definition.
    /// </summary>
    /// <param name="parameter">The parameter definition with constraints.</param>
    /// <param name="value">The value to validate.</param>
    /// <returns>List of validation errors (empty if valid).</returns>
    public static IReadOnlyList<string> Validate(ConfigurableParameter parameter, object? value)
    {
        var errors = new List<string>();

        if (value is null)
        {
            if (parameter.IsRequired)
                errors.Add($"Parameter '{parameter.Name}' is required.");
            return errors;
        }

        switch (parameter.ParameterType)
        {
            case ConfigurableParameterType.Int:
                ValidateNumeric(parameter, value, errors);
                break;

            case ConfigurableParameterType.Long:
                ValidateNumeric(parameter, value, errors);
                break;

            case ConfigurableParameterType.Double:
                ValidateNumeric(parameter, value, errors);
                break;

            case ConfigurableParameterType.Bool:
                if (value is not bool && !bool.TryParse(value.ToString(), out _))
                    errors.Add($"Parameter '{parameter.Name}' must be a boolean value.");
                break;

            case ConfigurableParameterType.String:
                ValidateString(parameter, value, errors);
                break;

            case ConfigurableParameterType.Enum:
                if (parameter.AllowedValues is { Count: > 0 })
                {
                    var strVal = value.ToString() ?? string.Empty;
                    if (!parameter.AllowedValues.Contains(strVal))
                        errors.Add($"Parameter '{parameter.Name}' must be one of: {string.Join(", ", parameter.AllowedValues)}. Got: '{strVal}'.");
                }
                break;

            case ConfigurableParameterType.StringList:
                if (value is not IEnumerable<string>)
                    errors.Add($"Parameter '{parameter.Name}' must be a list of strings.");
                break;

            case ConfigurableParameterType.Duration:
                if (value is not TimeSpan && !TimeSpan.TryParse(value.ToString(), out _))
                    errors.Add($"Parameter '{parameter.Name}' must be a valid duration/TimeSpan.");
                break;
        }

        return errors;
    }

    private static void ValidateNumeric(ConfigurableParameter parameter, object value, List<string> errors)
    {
        if (!TryConvertToDouble(value, out var numericValue))
        {
            errors.Add($"Parameter '{parameter.Name}' must be a numeric value. Got: '{value}'.");
            return;
        }

        if (parameter.MinValue is not null && TryConvertToDouble(parameter.MinValue, out var min) && numericValue < min)
            errors.Add($"Parameter '{parameter.Name}' must be >= {min}. Got: {numericValue}.");

        if (parameter.MaxValue is not null && TryConvertToDouble(parameter.MaxValue, out var max) && numericValue > max)
            errors.Add($"Parameter '{parameter.Name}' must be <= {max}. Got: {numericValue}.");
    }

    private static void ValidateString(ConfigurableParameter parameter, object value, List<string> errors)
    {
        var strVal = value.ToString() ?? string.Empty;

        if (parameter.AllowedValues is { Count: > 0 } && !parameter.AllowedValues.Contains(strVal))
            errors.Add($"Parameter '{parameter.Name}' must be one of: {string.Join(", ", parameter.AllowedValues)}. Got: '{strVal}'.");

        if (!string.IsNullOrEmpty(parameter.RegexPattern))
        {
            try
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(strVal, parameter.RegexPattern))
                    errors.Add($"Parameter '{parameter.Name}' must match pattern: {parameter.RegexPattern}.");
            }
            catch (System.Text.RegularExpressions.RegexParseException)
            {
                errors.Add($"Parameter '{parameter.Name}' has an invalid regex pattern: {parameter.RegexPattern}.");
            }
        }
    }

    private static bool TryConvertToDouble(object value, out double result)
    {
        result = 0;
        if (value is double d) { result = d; return true; }
        if (value is int i) { result = i; return true; }
        if (value is long l) { result = l; return true; }
        if (value is float f) { result = f; return true; }
        if (value is decimal dec) { result = (double)dec; return true; }
        if (value is short s) { result = s; return true; }
        if (value is byte b) { result = b; return true; }
        return double.TryParse(value.ToString(), out result);
    }
}

#endregion
