using System;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;
using DataWarehouse.SDK.Infrastructure.Policy.Performance;

namespace DataWarehouse.SDK.Infrastructure.Intelligence;

/// <summary>
/// Provides factory methods and verification for v6.0 AI autonomy defaults (MIGR-06).
/// <para>
/// All 94 features at all 5 <see cref="PolicyLevel"/> values (470 config points total)
/// default to <see cref="AiAutonomyLevel.ManualOnly"/>. No AI actions occur without
/// explicit administrator configuration.
/// </para>
/// <para>
/// This class is pure: no I/O, no mutable state. It provides the single source of truth
/// for the v6.0 default AI posture and a verification method (<see cref="IsFullyManualOnly"/>)
/// to confirm all 470 config points are correctly configured.
/// </para>
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 81: AI ManualOnly defaults (MIGR-06)")]
public static class AiAutonomyDefaults
{
    /// <summary>
    /// Human-readable justification for audit logs explaining why AI autonomy defaults to ManualOnly.
    /// </summary>
    public const string DefaultJustification =
        "AI autonomy defaults to ManualOnly per MIGR-06. No AI actions occur without explicit administrator configuration.";

    /// <summary>
    /// Cached array of all <see cref="PolicyLevel"/> enum values for iteration.
    /// </summary>
    private static readonly PolicyLevel[] AllPolicyLevels = Enum.GetValues<PolicyLevel>();

    /// <summary>
    /// Applies <see cref="AiAutonomyLevel.ManualOnly"/> to all 94 features at all 5 policy levels
    /// on the specified <see cref="AiAutonomyConfiguration"/> instance.
    /// </summary>
    /// <param name="config">The configuration to apply ManualOnly defaults to.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="config"/> is null.</exception>
    public static void ApplyManualOnlyDefaults(AiAutonomyConfiguration config)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));

        for (int i = 0; i < AllPolicyLevels.Length; i++)
        {
            config.SetAutonomyForLevel(AllPolicyLevels[i], AiAutonomyLevel.ManualOnly);
        }
    }

    /// <summary>
    /// Creates a new <see cref="AiAutonomyConfiguration"/> with all 470 config points
    /// set to <see cref="AiAutonomyLevel.ManualOnly"/>.
    /// </summary>
    /// <returns>A fully configured <see cref="AiAutonomyConfiguration"/> with ManualOnly defaults.</returns>
    public static AiAutonomyConfiguration CreateManualOnlyConfiguration()
    {
        var config = new AiAutonomyConfiguration(AiAutonomyLevel.ManualOnly);
        ApplyManualOnlyDefaults(config);
        return config;
    }

    /// <summary>
    /// Verifies that every one of the 470 config points (94 features x 5 policy levels)
    /// in the specified configuration is set to <see cref="AiAutonomyLevel.ManualOnly"/>.
    /// </summary>
    /// <param name="config">The configuration to verify.</param>
    /// <returns>
    /// True if all config points return <see cref="AiAutonomyLevel.ManualOnly"/>;
    /// false if any point has a different autonomy level.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="config"/> is null.</exception>
    public static bool IsFullyManualOnly(AiAutonomyConfiguration config)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));

        foreach (CheckTiming timing in Enum.GetValues<CheckTiming>())
        {
            var features = CheckClassificationTable.GetFeaturesByTiming(timing);
            for (int fi = 0; fi < features.Count; fi++)
            {
                for (int li = 0; li < AllPolicyLevels.Length; li++)
                {
                    if (config.GetAutonomy(features[fi], AllPolicyLevels[li]) != AiAutonomyLevel.ManualOnly)
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Returns the v6.0 default AI autonomy level. Single source of truth for
    /// the default value used throughout the Policy Engine.
    /// </summary>
    /// <returns><see cref="AiAutonomyLevel.ManualOnly"/>.</returns>
    public static AiAutonomyLevel GetDefaultAutonomyLevel() => AiAutonomyLevel.ManualOnly;
}
