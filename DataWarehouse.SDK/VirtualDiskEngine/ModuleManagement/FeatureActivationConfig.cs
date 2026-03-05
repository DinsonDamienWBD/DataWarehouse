using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.ModuleManagement;

/// <summary>
/// Defines a rollout policy for a single VDE module feature. Controls what fraction of
/// newly-created inodes get the module's bit activated in their per-inode ActiveModules
/// bitmap, enabling progressive A/B testing of new VDE capabilities.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: Per-inode progressive feature rollout policy (VOPT-55)")]
public sealed record FeatureRolloutPolicy
{
    /// <summary>
    /// The ActiveModules bitmap bit position (0-63) that this policy controls.
    /// Corresponds to the <see cref="Format.ModuleId"/> cast to int.
    /// </summary>
    public required int ModuleBitPosition { get; init; }

    /// <summary>
    /// Fraction of new inodes that should have this module activated.
    /// Range: 0.0 (none) to 1.0 (all). Values outside this range are clamped.
    /// </summary>
    public double RolloutPercentage { get; set; } = 0.0;

    /// <summary>Human-readable name for this A/B experiment.</summary>
    public required string ExperimentName { get; init; }

    /// <summary>Whether this policy is currently active. False pauses rollout without deleting the policy.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>UTC timestamp when the rollout began. Null if not yet started.</summary>
    public DateTimeOffset? StartTime { get; set; }

    /// <summary>
    /// UTC timestamp when this rollout should automatically stop. Null means indefinite.
    /// After EndTime the policy is treated as disabled regardless of <see cref="Enabled"/>.
    /// </summary>
    public DateTimeOffset? EndTime { get; set; }

    /// <summary>
    /// Returns true if the policy is currently within its active time window.
    /// </summary>
    public bool IsWithinTimeWindow()
    {
        var now = DateTimeOffset.UtcNow;
        if (StartTime.HasValue && now < StartTime.Value) return false;
        if (EndTime.HasValue && now > EndTime.Value) return false;
        return true;
    }
}

/// <summary>
/// Configuration for the progressive feature activation engine. Holds all rollout policies
/// and the seed used for deterministic inode-to-experiment-group assignment.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: Progressive feature activation configuration (VOPT-55)")]
public sealed class FeatureActivationConfig
{
    /// <summary>All rollout policies managed by this configuration.</summary>
    public List<FeatureRolloutPolicy> Policies { get; set; } = new();

    /// <summary>
    /// Seed value mixed into the hash when assigning inodes to experiment groups.
    /// Change this to shuffle group assignments without altering inode numbers.
    /// Default: 42.
    /// </summary>
    public int HashSeed { get; set; } = 42;
}
