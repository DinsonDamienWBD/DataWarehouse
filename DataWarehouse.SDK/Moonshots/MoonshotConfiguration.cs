using System.Collections.ObjectModel;

namespace DataWarehouse.SDK.Moonshots;

/// <summary>
/// Identifies which level in the configuration hierarchy a setting was defined at.
/// Instance is the broadest scope; User is the narrowest.
/// </summary>
public enum ConfigHierarchyLevel
{
    /// <summary>Instance-wide defaults set by platform administrators.</summary>
    Instance = 0,

    /// <summary>Tenant-scoped overrides set by tenant administrators.</summary>
    Tenant = 1,

    /// <summary>User-scoped overrides set by individual users.</summary>
    User = 2
}

/// <summary>
/// Controls which hierarchy levels are allowed to override a moonshot's configuration.
/// </summary>
public enum MoonshotOverridePolicy
{
    /// <summary>Only Instance administrators can change this setting. Tenant and User levels are locked out.</summary>
    Locked = 0,

    /// <summary>Tenant administrators can override Instance defaults, but Users cannot.</summary>
    TenantOverridable = 1,

    /// <summary>Any user can override the setting at their scope.</summary>
    UserOverridable = 2
}

/// <summary>
/// Specifies which strategy implementation to use for a particular moonshot capability,
/// along with any strategy-specific parameters.
/// </summary>
/// <param name="StrategyName">The name of the strategy implementation (e.g., "InvertedIndex", "AiValueScoring").</param>
/// <param name="StrategyParameters">Strategy-specific configuration parameters as key-value pairs.</param>
public sealed record MoonshotStrategySelection(
    string StrategyName,
    IReadOnlyDictionary<string, string> StrategyParameters)
{
    /// <summary>
    /// Creates a strategy selection with no additional parameters.
    /// </summary>
    public MoonshotStrategySelection(string strategyName)
        : this(strategyName, ReadOnlyDictionary<string, string>.Empty)
    {
    }
}

/// <summary>
/// Configuration for a single moonshot feature, including enable/disable,
/// override policy, strategy selections, custom settings, and dependency declarations.
/// </summary>
/// <param name="Id">The moonshot this configuration applies to.</param>
/// <param name="Enabled">Master on/off switch for the moonshot.</param>
/// <param name="OverridePolicy">Controls which hierarchy levels can change this configuration.</param>
/// <param name="StrategySelections">
/// Per-capability strategy selections. Key is capability name (e.g., "PlacementAlgorithm"),
/// value is the selected strategy and its parameters.
/// </param>
/// <param name="Settings">
/// Moonshot-specific key-value configuration (e.g., "ConsciousnessThreshold" = "50").
/// </param>
/// <param name="RequiredDependencies">Other moonshots that must be enabled for this one to function.</param>
/// <param name="DefinedAt">Which hierarchy level defined this configuration entry.</param>
public sealed record MoonshotFeatureConfig(
    MoonshotId Id,
    bool Enabled,
    MoonshotOverridePolicy OverridePolicy,
    IReadOnlyDictionary<string, MoonshotStrategySelection> StrategySelections,
    IReadOnlyDictionary<string, string> Settings,
    IReadOnlyList<MoonshotId> RequiredDependencies,
    ConfigHierarchyLevel DefinedAt)
{
    /// <summary>
    /// Creates a disabled moonshot configuration with no strategies, settings, or dependencies.
    /// </summary>
    public static MoonshotFeatureConfig CreateDisabled(MoonshotId id, ConfigHierarchyLevel level = ConfigHierarchyLevel.Instance)
        => new(
            id,
            Enabled: false,
            OverridePolicy: MoonshotOverridePolicy.UserOverridable,
            StrategySelections: ReadOnlyDictionary<string, MoonshotStrategySelection>.Empty,
            Settings: ReadOnlyDictionary<string, string>.Empty,
            RequiredDependencies: Array.Empty<MoonshotId>(),
            DefinedAt: level);
}

/// <summary>
/// Complete moonshot configuration for a particular hierarchy level.
/// Supports Instance -> Tenant -> User inheritance with override policy enforcement.
/// </summary>
public sealed class MoonshotConfiguration
{
    /// <summary>
    /// Per-moonshot feature configurations keyed by <see cref="MoonshotId"/>.
    /// </summary>
    public IReadOnlyDictionary<MoonshotId, MoonshotFeatureConfig> Moonshots { get; init; }
        = ReadOnlyDictionary<MoonshotId, MoonshotFeatureConfig>.Empty;

    /// <summary>
    /// The hierarchy level this configuration was defined at.
    /// </summary>
    public ConfigHierarchyLevel Level { get; init; } = ConfigHierarchyLevel.Instance;

    /// <summary>
    /// The tenant identifier when <see cref="Level"/> is <see cref="ConfigHierarchyLevel.Tenant"/> or lower.
    /// Null for Instance-level configurations.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// The user identifier when <see cref="Level"/> is <see cref="ConfigHierarchyLevel.User"/>.
    /// Null for Instance and Tenant-level configurations.
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>
    /// Returns the effective configuration for the specified moonshot at this level.
    /// If no configuration exists for the moonshot, returns a disabled default.
    /// </summary>
    /// <param name="id">The moonshot to retrieve configuration for.</param>
    /// <returns>The effective <see cref="MoonshotFeatureConfig"/> for this level.</returns>
    public MoonshotFeatureConfig GetEffectiveConfig(MoonshotId id)
    {
        return Moonshots.TryGetValue(id, out var config)
            ? config
            : MoonshotFeatureConfig.CreateDisabled(id, Level);
    }

    /// <summary>
    /// Quick check whether a moonshot is enabled in this configuration.
    /// </summary>
    /// <param name="id">The moonshot to check.</param>
    /// <returns>True if the moonshot exists in the configuration and is enabled.</returns>
    public bool IsEnabled(MoonshotId id)
    {
        return Moonshots.TryGetValue(id, out var config) && config.Enabled;
    }

    /// <summary>
    /// Gets the strategy selection for a specific capability of a moonshot.
    /// </summary>
    /// <param name="id">The moonshot to query.</param>
    /// <param name="capabilityName">The capability name (e.g., "PlacementAlgorithm").</param>
    /// <returns>The strategy selection, or null if the moonshot or capability is not configured.</returns>
    public MoonshotStrategySelection? GetStrategy(MoonshotId id, string capabilityName)
    {
        if (!Moonshots.TryGetValue(id, out var config))
            return null;

        return config.StrategySelections.TryGetValue(capabilityName, out var strategy)
            ? strategy
            : null;
    }

    /// <summary>
    /// Merges this (parent) configuration with a child configuration, producing a new
    /// effective configuration that respects override policies.
    /// <para>
    /// Override policy rules:
    /// <list type="bullet">
    ///   <item><see cref="MoonshotOverridePolicy.Locked"/>: Parent always wins; child changes are ignored.</item>
    ///   <item><see cref="MoonshotOverridePolicy.TenantOverridable"/>: Tenant-level children can override,
    ///         but User-level children cannot.</item>
    ///   <item><see cref="MoonshotOverridePolicy.UserOverridable"/>: Any child level can override.</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="child">The child configuration to merge (must be at a lower/more-specific hierarchy level).</param>
    /// <returns>A new <see cref="MoonshotConfiguration"/> with the effective merged values.</returns>
    public MoonshotConfiguration MergeWith(MoonshotConfiguration child)
    {
        if (child.Level <= Level)
            throw new ArgumentException(
                $"Child level ({child.Level}) must be more specific than parent level ({Level}).",
                nameof(child));

        var merged = new Dictionary<MoonshotId, MoonshotFeatureConfig>();

        // Start with all parent configurations
        foreach (var kvp in Moonshots)
        {
            merged[kvp.Key] = kvp.Value;
        }

        // Apply child overrides where policy allows
        foreach (var kvp in child.Moonshots)
        {
            var childConfig = kvp.Value;

            if (merged.TryGetValue(kvp.Key, out var parentConfig))
            {
                if (CanOverride(parentConfig.OverridePolicy, child.Level))
                {
                    // Child is allowed to override: merge strategies and settings
                    var mergedStrategies = MergeDictionaries(
                        parentConfig.StrategySelections, childConfig.StrategySelections);
                    var mergedSettings = MergeDictionaries(
                        parentConfig.Settings, childConfig.Settings);

                    merged[kvp.Key] = childConfig with
                    {
                        StrategySelections = new ReadOnlyDictionary<string, MoonshotStrategySelection>(
                            new Dictionary<string, MoonshotStrategySelection>(mergedStrategies)),
                        Settings = new ReadOnlyDictionary<string, string>(
                            new Dictionary<string, string>(mergedSettings)),
                        DefinedAt = child.Level
                    };
                }
                // else: parent config wins (Locked or policy doesn't allow this level)
            }
            else
            {
                // New moonshot not in parent -- child can always add
                merged[kvp.Key] = childConfig with { DefinedAt = child.Level };
            }
        }

        return new MoonshotConfiguration
        {
            Moonshots = new ReadOnlyDictionary<MoonshotId, MoonshotFeatureConfig>(merged),
            Level = child.Level,
            TenantId = child.TenantId ?? TenantId,
            UserId = child.UserId ?? UserId
        };
    }

    /// <summary>
    /// Determines whether a child at the given hierarchy level can override a parent config
    /// with the specified override policy.
    /// </summary>
    private static bool CanOverride(MoonshotOverridePolicy policy, ConfigHierarchyLevel childLevel)
    {
        return policy switch
        {
            MoonshotOverridePolicy.Locked => false,
            MoonshotOverridePolicy.TenantOverridable => childLevel == ConfigHierarchyLevel.Tenant,
            MoonshotOverridePolicy.UserOverridable => true,
            _ => false
        };
    }

    /// <summary>
    /// Merges two read-only dictionaries, with child values overriding parent values for matching keys.
    /// </summary>
    private static IReadOnlyDictionary<string, T> MergeDictionaries<T>(
        IReadOnlyDictionary<string, T> parent,
        IReadOnlyDictionary<string, T> child)
    {
        var result = new Dictionary<string, T>(parent.Count + child.Count);
        foreach (var kvp in parent)
            result[kvp.Key] = kvp.Value;
        foreach (var kvp in child)
            result[kvp.Key] = kvp.Value;
        return result;
    }
}
