using System.Globalization;
using System.Xml;

namespace DataWarehouse.SDK.Moonshots;

/// <summary>
/// Result of validating a <see cref="MoonshotConfiguration"/>.
/// </summary>
/// <param name="IsValid">True if no validation errors were found.</param>
/// <param name="Errors">List of validation errors, empty if valid.</param>
public sealed record MoonshotConfigValidationResult(
    bool IsValid,
    IReadOnlyList<MoonshotConfigValidationError> Errors)
{
    /// <summary>A valid result with no errors.</summary>
    public static MoonshotConfigValidationResult Valid { get; } =
        new(true, Array.Empty<MoonshotConfigValidationError>());
}

/// <summary>
/// Describes a single validation error in a moonshot configuration.
/// </summary>
/// <param name="AffectedMoonshot">The moonshot that caused the error, or null for global errors.</param>
/// <param name="Code">Machine-readable error code (e.g., "DEP_MISSING", "OVERRIDE_VIOLATION").</param>
/// <param name="Message">Human-readable description of the error.</param>
/// <param name="Suggestion">Optional suggested fix for the error.</param>
public sealed record MoonshotConfigValidationError(
    MoonshotId? AffectedMoonshot,
    string Code,
    string Message,
    string? Suggestion = null);

/// <summary>
/// Validates a <see cref="MoonshotConfiguration"/> for correctness, checking
/// dependency chains, override policy violations, strategy presence, and setting constraints.
/// </summary>
public interface IMoonshotConfigValidator
{
    /// <summary>
    /// Validates the given moonshot configuration and returns any errors found.
    /// </summary>
    /// <param name="config">The configuration to validate.</param>
    /// <returns>Validation result with any errors.</returns>
    MoonshotConfigValidationResult Validate(MoonshotConfiguration config);
}

/// <summary>
/// Production implementation of <see cref="IMoonshotConfigValidator"/>.
/// Checks dependency chains, override policy violations, missing strategies,
/// required settings, and setting value constraints.
/// </summary>
public sealed class MoonshotConfigurationValidator : IMoonshotConfigValidator
{
    /// <summary>Error code: A required dependency moonshot is not enabled.</summary>
    public const string CodeDepMissing = "DEP_MISSING";

    /// <summary>Error code: A child level attempted to override a locked configuration.</summary>
    public const string CodeOverrideViolation = "OVERRIDE_VIOLATION";

    /// <summary>Error code: An enabled moonshot is missing a required strategy selection.</summary>
    public const string CodeMissingStrategy = "MISSING_STRATEGY";

    /// <summary>Error code: An enabled moonshot is missing a required setting.</summary>
    public const string CodeMissingSetting = "MISSING_SETTING";

    /// <summary>Error code: A setting contains an invalid ISO 8601 duration.</summary>
    public const string CodeInvalidDuration = "INVALID_DURATION";

    /// <summary>Error code: A numeric setting is outside the valid range.</summary>
    public const string CodeInvalidRange = "INVALID_RANGE";

    /// <summary>Error code: A budget setting has an invalid (non-positive) value.</summary>
    public const string CodeInvalidBudget = "INVALID_BUDGET";

    /// <summary>
    /// Optional parent configuration to validate override policies against.
    /// When set, the validator checks that the target config does not override
    /// values that the parent has locked.
    /// </summary>
    public MoonshotConfiguration? ParentConfig { get; init; }

    /// <inheritdoc />
    public MoonshotConfigValidationResult Validate(MoonshotConfiguration config)
    {
        var errors = new List<MoonshotConfigValidationError>();

        foreach (var kvp in config.Moonshots)
        {
            var feature = kvp.Value;

            if (!feature.Enabled)
                continue;

            // Check dependency chains: if moonshot A depends on B, B must be enabled
            ValidateDependencies(config, feature, errors);

            // Check strategy names are not null/empty for enabled moonshots
            ValidateStrategies(feature, errors);

            // Check required settings per moonshot
            ValidateRequiredSettings(feature, errors);

            // Check setting value constraints
            ValidateSettingConstraints(feature, errors);
        }

        // Check override policy violations against parent
        if (ParentConfig != null)
        {
            ValidateOverridePolicies(ParentConfig, config, errors);
        }

        return errors.Count == 0
            ? MoonshotConfigValidationResult.Valid
            : new MoonshotConfigValidationResult(false, errors);
    }

    private static void ValidateDependencies(
        MoonshotConfiguration config,
        MoonshotFeatureConfig feature,
        List<MoonshotConfigValidationError> errors)
    {
        foreach (var dep in feature.RequiredDependencies)
        {
            if (!config.Moonshots.TryGetValue(dep, out var depConfig) || !depConfig.Enabled)
            {
                errors.Add(new MoonshotConfigValidationError(
                    AffectedMoonshot: feature.Id,
                    Code: CodeDepMissing,
                    Message: $"Moonshot '{feature.Id}' requires '{dep}' to be enabled, but it is not.",
                    Suggestion: $"Enable '{dep}' or disable '{feature.Id}'."));
            }
        }
    }

    private static void ValidateStrategies(
        MoonshotFeatureConfig feature,
        List<MoonshotConfigValidationError> errors)
    {
        foreach (var kvp in feature.StrategySelections)
        {
            if (string.IsNullOrWhiteSpace(kvp.Value.StrategyName))
            {
                errors.Add(new MoonshotConfigValidationError(
                    AffectedMoonshot: feature.Id,
                    Code: CodeMissingStrategy,
                    Message: $"Moonshot '{feature.Id}' has an empty strategy name for capability '{kvp.Key}'.",
                    Suggestion: $"Set a valid strategy name for capability '{kvp.Key}'."));
            }
        }
    }

    private static void ValidateRequiredSettings(
        MoonshotFeatureConfig feature,
        List<MoonshotConfigValidationError> errors)
    {
        // Per-moonshot required settings
        var required = GetRequiredSettings(feature.Id);
        foreach (var setting in required)
        {
            if (!feature.Settings.ContainsKey(setting))
            {
                errors.Add(new MoonshotConfigValidationError(
                    AffectedMoonshot: feature.Id,
                    Code: CodeMissingSetting,
                    Message: $"Moonshot '{feature.Id}' is missing required setting '{setting}'.",
                    Suggestion: $"Add setting '{setting}' to the '{feature.Id}' configuration."));
            }
        }
    }

    private static void ValidateSettingConstraints(
        MoonshotFeatureConfig feature,
        List<MoonshotConfigValidationError> errors)
    {
        switch (feature.Id)
        {
            case MoonshotId.CryptoTimeLocks:
                ValidateIsoDuration(feature, "DefaultLockDuration", errors);
                break;

            case MoonshotId.ChaosVaccination:
                ValidateIntRange(feature, "MaxBlastRadius", 1, 100, errors);
                break;

            case MoonshotId.CarbonAwareLifecycle:
                ValidatePositiveNumber(feature, "CarbonBudgetKgPerTB", errors);
                break;
        }
    }

    private static void ValidateOverridePolicies(
        MoonshotConfiguration parent,
        MoonshotConfiguration child,
        List<MoonshotConfigValidationError> errors)
    {
        foreach (var kvp in child.Moonshots)
        {
            if (!parent.Moonshots.TryGetValue(kvp.Key, out var parentFeature))
                continue;

            var childFeature = kvp.Value;
            var canOverride = parentFeature.OverridePolicy switch
            {
                MoonshotOverridePolicy.Locked => false,
                MoonshotOverridePolicy.TenantOverridable => child.Level == ConfigHierarchyLevel.Tenant,
                MoonshotOverridePolicy.UserOverridable => true,
                _ => false
            };

            // Check if child changed enabled state when not allowed
            if (!canOverride && childFeature.Enabled != parentFeature.Enabled)
            {
                errors.Add(new MoonshotConfigValidationError(
                    AffectedMoonshot: kvp.Key,
                    Code: CodeOverrideViolation,
                    Message: $"Moonshot '{kvp.Key}' has override policy '{parentFeature.OverridePolicy}' " +
                             $"at {parent.Level} level, but {child.Level} level attempted to change Enabled " +
                             $"from {parentFeature.Enabled} to {childFeature.Enabled}.",
                    Suggestion: $"Remove the override or change the parent override policy."));
            }
        }
    }

    private static IReadOnlyList<string> GetRequiredSettings(MoonshotId id)
    {
        return id switch
        {
            MoonshotId.DataConsciousness => new[] { "ConsciousnessThreshold" },
            MoonshotId.SovereigntyMesh => new[] { "DefaultZone" },
            MoonshotId.CryptoTimeLocks => new[] { "DefaultLockDuration" },
            MoonshotId.ChaosVaccination => new[] { "MaxBlastRadius" },
            MoonshotId.CarbonAwareLifecycle => new[] { "CarbonBudgetKgPerTB" },
            _ => Array.Empty<string>()
        };
    }

    private static void ValidateIsoDuration(
        MoonshotFeatureConfig feature,
        string settingName,
        List<MoonshotConfigValidationError> errors)
    {
        if (!feature.Settings.TryGetValue(settingName, out var value))
            return;

        try
        {
            XmlConvert.ToTimeSpan(value);
        }
        catch (FormatException)
        {
            errors.Add(new MoonshotConfigValidationError(
                AffectedMoonshot: feature.Id,
                Code: CodeInvalidDuration,
                Message: $"Setting '{settingName}' value '{value}' is not a valid ISO 8601 duration.",
                Suggestion: "Use ISO 8601 duration format (e.g., 'P30D' for 30 days, 'PT1H' for 1 hour)."));
        }
    }

    private static void ValidateIntRange(
        MoonshotFeatureConfig feature,
        string settingName,
        int min,
        int max,
        List<MoonshotConfigValidationError> errors)
    {
        if (!feature.Settings.TryGetValue(settingName, out var value))
            return;

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue)
            || intValue < min || intValue > max)
        {
            errors.Add(new MoonshotConfigValidationError(
                AffectedMoonshot: feature.Id,
                Code: CodeInvalidRange,
                Message: $"Setting '{settingName}' value '{value}' must be an integer between {min} and {max}.",
                Suggestion: $"Set '{settingName}' to a value between {min} and {max}."));
        }
    }

    private static void ValidatePositiveNumber(
        MoonshotFeatureConfig feature,
        string settingName,
        List<MoonshotConfigValidationError> errors)
    {
        if (!feature.Settings.TryGetValue(settingName, out var value))
            return;

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var numValue)
            || numValue <= 0)
        {
            errors.Add(new MoonshotConfigValidationError(
                AffectedMoonshot: feature.Id,
                Code: CodeInvalidBudget,
                Message: $"Setting '{settingName}' value '{value}' must be a positive number.",
                Suggestion: $"Set '{settingName}' to a positive value (e.g., '100')."));
        }
    }
}
