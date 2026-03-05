using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.ModuleManagement;

/// <summary>
/// Per-inode progressive feature A/B testing engine (VOPT-55).
/// Deterministically assigns inodes to experiment groups and activates module bits in
/// new inode ActiveModules bitmaps based on configured rollout percentages.
/// </summary>
/// <remarks>
/// Design principles:
/// <list type="bullet">
/// <item>Deterministic: same inode + experiment + seed always produces the same group (0-99).</item>
/// <item>Non-destructive: rollout only affects NEW inodes; existing inodes are never modified.</item>
/// <item>Safe by default: modules are only activated if also present in the volume ModuleManifest.</item>
/// <item>Independently pausable and completable per experiment without affecting others.</item>
/// </list>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: Per-inode progressive feature A/B testing engine (VOPT-55)")]
public sealed class ProgressiveFeatureActivation
{
    private readonly FeatureActivationConfig _config;

    /// <summary>
    /// Initialises the engine with the given configuration.
    /// </summary>
    /// <param name="config">Rollout policies and hash seed to use.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="config"/> is null.</exception>
    public ProgressiveFeatureActivation(FeatureActivationConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    // ── Group assignment ─────────────────────────────────────────────────

    /// <summary>
    /// Deterministically assigns an inode to an experiment group in the range [0, 99].
    /// The same inputs always produce the same group, making assignments reproducible
    /// across restarts and nodes.
    /// </summary>
    /// <param name="inodeNumber">Inode number to assign.</param>
    /// <param name="experimentName">Name of the A/B experiment.</param>
    /// <returns>Group number in [0, 99].</returns>
    public int GetExperimentGroup(long inodeNumber, string experimentName)
    {
        // Build a short deterministic byte buffer: inodeNumber (8) + seed (4) + experiment name (UTF-8)
        var experimentBytes = Encoding.UTF8.GetBytes(experimentName);
        Span<byte> buffer = stackalloc byte[12 + experimentBytes.Length];
        BinaryPrimitives.WriteInt64LittleEndian(buffer[0..8], inodeNumber);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[8..12], _config.HashSeed);
        experimentBytes.CopyTo(buffer[12..]);

        ulong hash = XxHash64.HashToUInt64(buffer);
        return (int)(hash % 100);
    }

    // ── Module activation ────────────────────────────────────────────────

    /// <summary>
    /// Computes the ActiveModules bitmap to write into a newly created inode.
    /// Only bits that are also set in <paramref name="volumeModuleManifest"/> can be activated;
    /// modules not present at the volume level are never activated per-inode.
    /// </summary>
    /// <param name="inodeNumber">The inode number being created.</param>
    /// <param name="volumeModuleManifest">64-bit bitmap of modules available on this volume.</param>
    /// <returns>ActiveModules bitmap to store in the new inode.</returns>
    public ulong ComputeActiveModulesForNewInode(long inodeNumber, ulong volumeModuleManifest)
    {
        ulong activeModules = 0UL;
        var now = DateTimeOffset.UtcNow;

        foreach (var policy in _config.Policies)
        {
            if (!policy.Enabled) continue;
            if (!policy.IsWithinTimeWindow()) continue;

            // Guard: the module must be available at the volume level
            int bit = policy.ModuleBitPosition;
            if (bit < 0 || bit > 63) continue;
            ulong moduleMask = 1UL << bit;
            if ((volumeModuleManifest & moduleMask) == 0) continue;

            int group = GetExperimentGroup(inodeNumber, policy.ExperimentName);
            double threshold = Math.Clamp(policy.RolloutPercentage, 0.0, 1.0) * 100.0;

            if (group < threshold)
            {
                activeModules |= moduleMask;
            }
        }

        return activeModules;
    }

    /// <summary>
    /// Returns true if the given inode should have the policy's module activated.
    /// Shorthand over <see cref="ComputeActiveModulesForNewInode"/> for single-policy checks.
    /// Time-window and Enabled checks are honoured.
    /// </summary>
    /// <param name="inodeNumber">Inode number to test.</param>
    /// <param name="policy">The rollout policy to evaluate.</param>
    /// <returns>True if the inode falls within the rollout percentage.</returns>
    public bool ShouldActivateModule(long inodeNumber, FeatureRolloutPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.Enabled || !policy.IsWithinTimeWindow()) return false;

        int group = GetExperimentGroup(inodeNumber, policy.ExperimentName);
        double threshold = Math.Clamp(policy.RolloutPercentage, 0.0, 1.0) * 100.0;
        return group < threshold;
    }

    // ── Monitoring ───────────────────────────────────────────────────────

    /// <summary>
    /// Samples a set of inodes and calculates the actual module activation rate for the
    /// specified module bit. Useful for verifying that rollout matches the target percentage.
    /// </summary>
    /// <param name="moduleBitPosition">Bit position (0-63) of the module to measure.</param>
    /// <param name="inodeSample">Sample of (InodeNumber, ActiveModules) tuples to analyse.</param>
    /// <returns>A <see cref="RolloutStats"/> snapshot.</returns>
    public Task<RolloutStats> GetRolloutStatsAsync(
        int moduleBitPosition,
        IEnumerable<(long InodeNumber, ulong ActiveModules)> inodeSample)
    {
        ArgumentNullException.ThrowIfNull(inodeSample);

        // Find the matching policy (if any) for experiment name lookup
        var policy = _config.Policies.FirstOrDefault(p => p.ModuleBitPosition == moduleBitPosition);
        string experimentName = policy?.ExperimentName ?? $"bit{moduleBitPosition}";
        double targetPercentage = policy != null ? Math.Clamp(policy.RolloutPercentage, 0.0, 1.0) : 0.0;

        ulong moduleMask = moduleBitPosition is >= 0 and <= 63 ? 1UL << moduleBitPosition : 0UL;
        long activatedCount = 0;
        long totalCount = 0;

        foreach (var (_, activeModules) in inodeSample)
        {
            totalCount++;
            if ((activeModules & moduleMask) != 0)
                activatedCount++;
        }

        double actualPercentage = totalCount > 0 ? (double)activatedCount / totalCount : 0.0;

        return Task.FromResult(new RolloutStats
        {
            ExperimentName = experimentName,
            ModuleBitPosition = moduleBitPosition,
            TargetPercentage = targetPercentage,
            ActualPercentage = actualPercentage,
            ActivatedCount = activatedCount,
            TotalCount = totalCount,
        });
    }

    // ── Rollout control ──────────────────────────────────────────────────

    /// <summary>
    /// Updates the rollout percentage for the named experiment. Only affects inodes created
    /// after this call; existing inodes are never modified.
    /// </summary>
    /// <param name="experimentName">Name of the experiment to update.</param>
    /// <param name="newPercentage">New rollout percentage [0.0, 1.0].</param>
    /// <exception cref="KeyNotFoundException">No policy with the given experiment name exists.</exception>
    public void SetRolloutPercentage(string experimentName, double newPercentage)
    {
        ArgumentException.ThrowIfNullOrEmpty(experimentName);
        var policy = FindPolicy(experimentName);
        policy.RolloutPercentage = Math.Clamp(newPercentage, 0.0, 1.0);
    }

    /// <summary>
    /// Pauses the named experiment. New inodes will not get the module bit set until
    /// the experiment is re-enabled.
    /// </summary>
    /// <param name="experimentName">Name of the experiment to pause.</param>
    /// <exception cref="KeyNotFoundException">No policy with the given experiment name exists.</exception>
    public void PauseRollout(string experimentName)
    {
        ArgumentException.ThrowIfNullOrEmpty(experimentName);
        FindPolicy(experimentName).Enabled = false;
    }

    /// <summary>
    /// Completes the named experiment by setting rollout to 100% and recording the end time.
    /// After this call every new inode (that has the module in its volume manifest) will
    /// have the module bit set.
    /// </summary>
    /// <param name="experimentName">Name of the experiment to complete.</param>
    /// <exception cref="KeyNotFoundException">No policy with the given experiment name exists.</exception>
    public void CompleteRollout(string experimentName)
    {
        ArgumentException.ThrowIfNullOrEmpty(experimentName);
        var policy = FindPolicy(experimentName);
        policy.RolloutPercentage = 1.0;
        policy.Enabled = true;
        policy.EndTime = DateTimeOffset.UtcNow;
    }

    // ── Rollback helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Identifies inodes that currently have the experiment's module bit set in their
    /// ActiveModules bitmap. These are candidates for manual rollback if the feature
    /// needs to be disabled on existing data.
    /// </summary>
    /// <param name="experimentName">Name of the experiment.</param>
    /// <param name="inodes">Inode sample to search.</param>
    /// <returns>Inode numbers that have the module bit activated.</returns>
    /// <exception cref="KeyNotFoundException">No policy with the given experiment name exists.</exception>
    public IReadOnlyList<long> IdentifyRollbackCandidates(
        string experimentName,
        IEnumerable<(long InodeNumber, ulong ActiveModules)> inodes)
    {
        ArgumentException.ThrowIfNullOrEmpty(experimentName);
        ArgumentNullException.ThrowIfNull(inodes);

        var policy = FindPolicy(experimentName);
        int bit = policy.ModuleBitPosition;
        if (bit < 0 || bit > 63) return Array.Empty<long>();

        ulong moduleMask = 1UL << bit;
        var candidates = new List<long>();

        foreach (var (inodeNumber, activeModules) in inodes)
        {
            if ((activeModules & moduleMask) != 0)
                candidates.Add(inodeNumber);
        }

        return candidates.AsReadOnly();
    }

    // ── Internal helpers ─────────────────────────────────────────────────

    private FeatureRolloutPolicy FindPolicy(string experimentName)
    {
        var policy = _config.Policies.FirstOrDefault(
            p => string.Equals(p.ExperimentName, experimentName, StringComparison.Ordinal));

        if (policy is null)
            throw new KeyNotFoundException(
                $"No rollout policy found for experiment '{experimentName}'.");

        return policy;
    }
}

/// <summary>
/// A snapshot of activation statistics for a single module rollout experiment.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: Rollout monitoring statistics (VOPT-55)")]
public readonly struct RolloutStats
{
    /// <summary>Human-readable name of the A/B experiment.</summary>
    public required string ExperimentName { get; init; }

    /// <summary>The ActiveModules bit position this experiment controls.</summary>
    public required int ModuleBitPosition { get; init; }

    /// <summary>Configured target activation rate [0.0, 1.0].</summary>
    public required double TargetPercentage { get; init; }

    /// <summary>Observed activation rate in the sampled inodes [0.0, 1.0].</summary>
    public required double ActualPercentage { get; init; }

    /// <summary>Number of sampled inodes that have the module bit set.</summary>
    public required long ActivatedCount { get; init; }

    /// <summary>Total number of inodes in the sample.</summary>
    public required long TotalCount { get; init; }

    /// <inheritdoc />
    public override string ToString() =>
        $"RolloutStats({ExperimentName}, bit={ModuleBitPosition}, " +
        $"target={TargetPercentage:P1}, actual={ActualPercentage:P1}, " +
        $"{ActivatedCount}/{TotalCount})";
}
