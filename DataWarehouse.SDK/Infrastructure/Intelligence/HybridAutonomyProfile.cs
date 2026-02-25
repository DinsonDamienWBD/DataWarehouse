using System;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;
using DataWarehouse.SDK.Infrastructure.Policy.Performance;

namespace DataWarehouse.SDK.Infrastructure.Intelligence;

/// <summary>
/// Allows setting different AI autonomy levels per <see cref="CheckTiming"/> category,
/// enabling hybrid configurations where security-critical features use ManualOnly while
/// performance features use AutoNotify.
/// </summary>
/// <remarks>
/// Implements AIPI-09. Provides 3 pre-built profiles (Paranoid, Balanced, Performance) as
/// static factory methods. Each profile applies autonomy levels by timing category, affecting
/// all features classified under that timing in <see cref="CheckClassificationTable"/>.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 77: AI Policy Intelligence (AIPI-09)")]
public sealed class HybridAutonomyProfile
{
    private readonly AiAutonomyConfiguration _config;
    private readonly Dictionary<CheckTiming, AiAutonomyLevel> _categoryLevels = new();

    /// <summary>
    /// Currently applied profile name, or null if no profile has been applied.
    /// </summary>
    public string ActiveProfileName { get; private set; } = string.Empty;

    /// <summary>
    /// Creates a new HybridAutonomyProfile wrapping the specified autonomy configuration.
    /// </summary>
    /// <param name="config">The autonomy configuration to apply category-level overrides to.</param>
    public HybridAutonomyProfile(AiAutonomyConfiguration config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Applies a named profile by setting autonomy for all features in each timing category
    /// to the specified levels.
    /// </summary>
    /// <param name="profileName">Human-readable name for this profile.</param>
    /// <param name="categoryLevels">Mapping from <see cref="CheckTiming"/> to <see cref="AiAutonomyLevel"/>.</param>
    public void ApplyProfile(string profileName, Dictionary<CheckTiming, AiAutonomyLevel> categoryLevels)
    {
        if (profileName is null) throw new ArgumentNullException(nameof(profileName));
        if (categoryLevels is null) throw new ArgumentNullException(nameof(categoryLevels));

        _categoryLevels.Clear();

        foreach (var kvp in categoryLevels)
        {
            _categoryLevels[kvp.Key] = kvp.Value;
            _config.SetAutonomyForCategory(kvp.Key, kvp.Value);
        }

        ActiveProfileName = profileName;
    }

    /// <summary>
    /// Returns the current per-category autonomy mapping.
    /// </summary>
    /// <returns>A read-only snapshot of category-to-autonomy-level assignments.</returns>
    public IReadOnlyDictionary<CheckTiming, AiAutonomyLevel> GetCategoryLevels()
    {
        return _categoryLevels;
    }

    /// <summary>
    /// Creates a Paranoid profile: all critical timing categories set to ManualOnly,
    /// deferred/per-operation to Suggest. Suitable for highest-security environments.
    /// </summary>
    /// <param name="config">The autonomy configuration to apply the profile to.</param>
    /// <returns>A <see cref="HybridAutonomyProfile"/> with the Paranoid preset applied.</returns>
    public static HybridAutonomyProfile Paranoid(AiAutonomyConfiguration config)
    {
        var profile = new HybridAutonomyProfile(config);
        profile.ApplyProfile("Paranoid", new Dictionary<CheckTiming, AiAutonomyLevel>
        {
            [CheckTiming.ConnectTime] = AiAutonomyLevel.ManualOnly,
            [CheckTiming.SessionCached] = AiAutonomyLevel.ManualOnly,
            [CheckTiming.PerOperation] = AiAutonomyLevel.Suggest,
            [CheckTiming.Deferred] = AiAutonomyLevel.Suggest,
            [CheckTiming.Periodic] = AiAutonomyLevel.ManualOnly
        });
        return profile;
    }

    /// <summary>
    /// Creates a Balanced profile: moderate autonomy across categories, with connect-time
    /// features requiring human review and deferred/periodic allowing notification-based auto-apply.
    /// </summary>
    /// <param name="config">The autonomy configuration to apply the profile to.</param>
    /// <returns>A <see cref="HybridAutonomyProfile"/> with the Balanced preset applied.</returns>
    public static HybridAutonomyProfile Balanced(AiAutonomyConfiguration config)
    {
        var profile = new HybridAutonomyProfile(config);
        profile.ApplyProfile("Balanced", new Dictionary<CheckTiming, AiAutonomyLevel>
        {
            [CheckTiming.ConnectTime] = AiAutonomyLevel.Suggest,
            [CheckTiming.SessionCached] = AiAutonomyLevel.SuggestExplain,
            [CheckTiming.PerOperation] = AiAutonomyLevel.AutoNotify,
            [CheckTiming.Deferred] = AiAutonomyLevel.AutoNotify,
            [CheckTiming.Periodic] = AiAutonomyLevel.SuggestExplain
        });
        return profile;
    }

    /// <summary>
    /// Creates a Performance profile: maximum automation for throughput-sensitive features,
    /// with silent auto-apply for per-operation and deferred checks.
    /// </summary>
    /// <param name="config">The autonomy configuration to apply the profile to.</param>
    /// <returns>A <see cref="HybridAutonomyProfile"/> with the Performance preset applied.</returns>
    public static HybridAutonomyProfile Performance(AiAutonomyConfiguration config)
    {
        var profile = new HybridAutonomyProfile(config);
        profile.ApplyProfile("Performance", new Dictionary<CheckTiming, AiAutonomyLevel>
        {
            [CheckTiming.ConnectTime] = AiAutonomyLevel.SuggestExplain,
            [CheckTiming.SessionCached] = AiAutonomyLevel.AutoNotify,
            [CheckTiming.PerOperation] = AiAutonomyLevel.AutoSilent,
            [CheckTiming.Deferred] = AiAutonomyLevel.AutoSilent,
            [CheckTiming.Periodic] = AiAutonomyLevel.AutoNotify
        });
        return profile;
    }
}
