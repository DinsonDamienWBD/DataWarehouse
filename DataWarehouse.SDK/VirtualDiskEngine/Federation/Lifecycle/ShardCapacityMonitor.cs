using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Federation;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation.Lifecycle;

/// <summary>
/// Periodic capacity scanner that identifies shards exceeding the split threshold
/// and shards below the merge threshold. Stateless per call -- no locking required.
/// </summary>
/// <remarks>
/// <para>Split candidates are returned sorted by utilization descending (fullest first)
/// so the caller can prioritize splitting the most overloaded shards.</para>
/// <para>Merge candidates are returned sorted by StartSlot ascending so the merge engine
/// can efficiently detect adjacent pairs.</para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 93: VDE 2.0B Shard Lifecycle (VSHL-02)")]
public sealed class ShardCapacityMonitor
{
    private readonly Func<CancellationToken, ValueTask<IReadOnlyList<DataShardDescriptor>>> _shardEnumerator;
    private readonly double _splitThresholdPercent;

    /// <summary>
    /// Creates a new capacity monitor.
    /// </summary>
    /// <param name="shardEnumerator">
    /// Callback that returns all current data shard descriptors. Called on every scan.
    /// </param>
    /// <param name="splitThresholdPercent">
    /// Utilization percentage at or above which a shard becomes a split candidate.
    /// Defaults to <see cref="LifecycleConstants.DefaultSplitThresholdPercent"/> (80%).
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="shardEnumerator"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="splitThresholdPercent"/> is not in the range (0, 100).
    /// </exception>
    public ShardCapacityMonitor(
        Func<CancellationToken, ValueTask<IReadOnlyList<DataShardDescriptor>>> shardEnumerator,
        double splitThresholdPercent = LifecycleConstants.DefaultSplitThresholdPercent)
    {
        ArgumentNullException.ThrowIfNull(shardEnumerator);

        if (splitThresholdPercent <= 0.0 || splitThresholdPercent >= 100.0)
            throw new ArgumentOutOfRangeException(
                nameof(splitThresholdPercent),
                splitThresholdPercent,
                "Split threshold must be in the range (0, 100).");

        _shardEnumerator = shardEnumerator;
        _splitThresholdPercent = splitThresholdPercent;
    }

    /// <summary>
    /// Gets the configured split threshold percentage.
    /// </summary>
    public double SplitThresholdPercent => _splitThresholdPercent;

    /// <summary>
    /// Scans all data shards and returns those whose utilization is at or above
    /// the split threshold and that are available (Healthy or Degraded).
    /// Results are sorted by <see cref="DataShardDescriptor.UtilizationPercent"/> descending
    /// (fullest shard first -- highest split priority).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of split-candidate shard descriptors, sorted fullest-first.</returns>
    public async ValueTask<IReadOnlyList<DataShardDescriptor>> FindSplitCandidatesAsync(
        CancellationToken ct = default)
    {
        var allShards = await _shardEnumerator(ct).ConfigureAwait(false);

        var candidates = new List<DataShardDescriptor>();
        for (int i = 0; i < allShards.Count; i++)
        {
            var shard = allShards[i];
            if (shard.UtilizationPercent >= _splitThresholdPercent && shard.IsAvailable)
            {
                candidates.Add(shard);
            }
        }

        // Sort descending by utilization (fullest first)
        candidates.Sort((a, b) => b.UtilizationPercent.CompareTo(a.UtilizationPercent));

        return candidates;
    }

    /// <summary>
    /// Scans all data shards and returns those whose utilization is at or below
    /// the specified merge threshold and that are available (Healthy or Degraded).
    /// Results are sorted by <see cref="DataShardDescriptor.StartSlot"/> ascending
    /// for adjacency detection by the merge engine.
    /// </summary>
    /// <param name="mergeThresholdPercent">
    /// Utilization percentage at or below which a shard becomes a merge candidate.
    /// Must be in the range (0, 100).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of merge-candidate shard descriptors, sorted by StartSlot ascending.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="mergeThresholdPercent"/> is not in the range (0, 100).
    /// </exception>
    public async ValueTask<IReadOnlyList<DataShardDescriptor>> FindMergeCandidatesAsync(
        double mergeThresholdPercent,
        CancellationToken ct = default)
    {
        if (mergeThresholdPercent <= 0.0 || mergeThresholdPercent >= 100.0)
            throw new ArgumentOutOfRangeException(
                nameof(mergeThresholdPercent),
                mergeThresholdPercent,
                "Merge threshold must be in the range (0, 100).");

        var allShards = await _shardEnumerator(ct).ConfigureAwait(false);

        var candidates = new List<DataShardDescriptor>();
        for (int i = 0; i < allShards.Count; i++)
        {
            var shard = allShards[i];
            if (shard.UtilizationPercent <= mergeThresholdPercent && shard.IsAvailable)
            {
                candidates.Add(shard);
            }
        }

        // Sort ascending by StartSlot for adjacency detection
        candidates.Sort((a, b) => a.StartSlot.CompareTo(b.StartSlot));

        return candidates;
    }
}
