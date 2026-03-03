using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation.Lifecycle;

/// <summary>
/// Information about an available compound block device for placement decisions.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 93: VDE 2.0B Shard Lifecycle (VSHL-01)")]
public readonly record struct CompoundBlockDeviceInfo
{
    /// <summary>Unique identifier of the compound block device.</summary>
    public Guid DeviceId { get; init; }

    /// <summary>The storage tier this device belongs to.</summary>
    public StorageTier Tier { get; init; }

    /// <summary>Rack identifier for rack-awareness placement constraints.</summary>
    public string RackId { get; init; }

    /// <summary>Total capacity in blocks.</summary>
    public long CapacityBlocks { get; init; }

    /// <summary>Blocks currently in use.</summary>
    public long UsedBlocks { get; init; }

    /// <summary>Whether this device is healthy and available for new placements.</summary>
    public bool IsHealthy { get; init; }
}

/// <summary>
/// Access pattern metrics for a shard, used by <see cref="PlacementPolicyEngine.RecommendTier"/>
/// to classify workload intensity and recommend an appropriate storage tier.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 93: VDE 2.0B Shard Lifecycle (VSHL-01)")]
public readonly record struct ShardAccessPattern
{
    /// <summary>Average reads per second over the measurement window.</summary>
    public double ReadsPerSecond { get; init; }

    /// <summary>Average writes per second over the measurement window.</summary>
    public double WritesPerSecond { get; init; }

    /// <summary>Average observed latency in milliseconds.</summary>
    public double AvgLatencyMs { get; init; }

    /// <summary>UTC ticks of the most recent access to this shard.</summary>
    public long LastAccessUtcTicks { get; init; }
}

/// <summary>
/// Core placement engine that evaluates shard metrics against policies to produce
/// tier-aware, rack-diverse shard placements with cost optimization.
/// </summary>
/// <remarks>
/// <para>The engine is stateless per call: all state comes from constructor parameters
/// and the <see cref="ComputePlacementAsync"/> arguments. No locking is required.</para>
/// <para>For single-VDE deployments, use <see cref="CreatePassthrough"/> to bypass
/// engine evaluation entirely with zero overhead.</para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 93: VDE 2.0B Shard Lifecycle (VSHL-01)")]
public sealed class PlacementPolicyEngine
{
    /// <summary>
    /// Threshold for Hot tier classification: reads per second.
    /// </summary>
    private const double HotReadsThreshold = 1000.0;

    /// <summary>
    /// Threshold for Hot tier classification: writes per second.
    /// </summary>
    private const double HotWritesThreshold = 100.0;

    /// <summary>
    /// Threshold for Warm tier classification: reads per second.
    /// </summary>
    private const double WarmReadsThreshold = 10.0;

    /// <summary>
    /// Number of days without access before a shard is classified as Frozen.
    /// </summary>
    private const int FrozenInactiveDays = 30;

    /// <summary>
    /// Default block size estimate in bytes, used for cost calculations.
    /// </summary>
    private const int DefaultBlockSizeEstimateBytes = 4096;

    /// <summary>
    /// One gigabyte in bytes.
    /// </summary>
    private const long OneGigabyte = 1L * 1024 * 1024 * 1024;

    private readonly Dictionary<StorageTier, PlacementPolicy> _policies;
    private readonly Func<CancellationToken, ValueTask<IReadOnlyList<CompoundBlockDeviceInfo>>> _deviceDiscovery;

    /// <summary>
    /// Creates a new placement policy engine.
    /// </summary>
    /// <param name="policies">Placement policies ordered by tier (hot first). The engine selects the policy matching the requested tier.</param>
    /// <param name="deviceDiscovery">Callback that discovers available storage nodes at evaluation time.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="policies"/> or <paramref name="deviceDiscovery"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="policies"/> is empty or contains duplicate tiers.</exception>
    public PlacementPolicyEngine(
        IReadOnlyList<PlacementPolicy> policies,
        Func<CancellationToken, ValueTask<IReadOnlyList<CompoundBlockDeviceInfo>>> deviceDiscovery)
    {
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(deviceDiscovery);

        if (policies.Count == 0)
            throw new ArgumentException("At least one placement policy must be provided.", nameof(policies));

        _policies = new Dictionary<StorageTier, PlacementPolicy>(policies.Count);
        foreach (var policy in policies)
        {
            if (!_policies.TryAdd(policy.TargetTier, policy))
                throw new ArgumentException(
                    $"Duplicate placement policy for tier {policy.TargetTier}.",
                    nameof(policies));
        }

        _deviceDiscovery = deviceDiscovery;
    }

    /// <summary>
    /// Computes an optimal shard placement for the given shard on the requested storage tier.
    /// </summary>
    /// <param name="shard">The data shard descriptor to place.</param>
    /// <param name="requestedTier">The target storage tier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="blockSizeEstimateBytes">Block size in bytes for cost calculations (default 4096).</param>
    /// <returns>A <see cref="ShardPlacement"/> describing where the shard should be placed.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no policy exists for the requested tier, insufficient devices are available,
    /// or the placement exceeds the cost ceiling.
    /// </exception>
    public async ValueTask<ShardPlacement> ComputePlacementAsync(
        DataShardDescriptor shard,
        StorageTier requestedTier,
        CancellationToken ct = default,
        int blockSizeEstimateBytes = DefaultBlockSizeEstimateBytes)
    {
        // 1. Look up policy
        if (!_policies.TryGetValue(requestedTier, out var policy))
            throw new InvalidOperationException(
                $"No placement policy configured for tier {requestedTier}.");

        // 2. Validate policy
        policy.Validate();

        // 3. Discover available devices
        var allDevices = await _deviceDiscovery(ct).ConfigureAwait(false);

        // 4. Filter to matching tier + healthy
        var candidates = new List<CompoundBlockDeviceInfo>();
        foreach (var device in allDevices)
        {
            if (device.Tier == requestedTier && device.IsHealthy)
                candidates.Add(device);
        }

        // Determine required device count
        bool useErasure = policy.ErasureDataShards > 0;
        int requiredCount = useErasure
            ? policy.ErasureDataShards + policy.ErasureParityShards
            : policy.ReplicaCount;

        if (candidates.Count < requiredCount)
            throw new InvalidOperationException(
                $"Insufficient healthy devices for tier {requestedTier}: need {requiredCount}, found {candidates.Count}.");

        // 5-6-7. Select devices with rack-awareness
        string? warning = null;
        var selected = policy.RackAware
            ? SelectRackAware(candidates, requiredCount, out warning)
            : SelectBestEffort(candidates, requiredCount);

        // 8. Assign roles
        var targets = new PlacementTarget[selected.Count];
        if (useErasure)
        {
            // First device = Primary (hosts data + serves reads)
            // ErasureData roles, then ErasureParity roles
            for (int i = 0; i < selected.Count; i++)
            {
                PlacementRole role;
                if (i < policy.ErasureDataShards)
                    role = PlacementRole.ErasureData;
                else
                    role = PlacementRole.ErasureParity;

                targets[i] = new PlacementTarget
                {
                    CompoundBlockDeviceId = selected[i].DeviceId,
                    RackId = selected[i].RackId,
                    Role = role
                };
            }
        }
        else
        {
            // Replication mode: first = Primary, rest = Replica
            for (int i = 0; i < selected.Count; i++)
            {
                targets[i] = new PlacementTarget
                {
                    CompoundBlockDeviceId = selected[i].DeviceId,
                    RackId = selected[i].RackId,
                    Role = i == 0 ? PlacementRole.Primary : PlacementRole.Replica
                };
            }
        }

        // 9. Calculate estimated monthly cost
        var tierProfile = TierProfile.GetDefault(requestedTier);
        double shardSizeGb = (double)shard.CapacityBlocks * blockSizeEstimateBytes / OneGigabyte;
        double estimatedCost = shardSizeGb * tierProfile.CostPerGbMonth * selected.Count;

        // 10. Enforce cost ceiling
        double costCeiling = policy.MaxCostPerGbMonth * shardSizeGb;
        if (estimatedCost > costCeiling)
            throw new InvalidOperationException(
                $"Placement exceeds cost ceiling: estimated ${estimatedCost:F4}/mo vs ceiling ${costCeiling:F4}/mo.");

        // 11. Return placement
        return new ShardPlacement
        {
            ShardVdeId = shard.DataVdeId,
            AssignedTier = requestedTier,
            AppliedPolicy = policy,
            Targets = targets,
            EstimatedMonthlyCostUsd = estimatedCost,
            PlacementTimestampUtcTicks = DateTime.UtcNow.Ticks,
            Warning = warning
        };
    }

    /// <summary>
    /// Recommends a storage tier based on the shard's access pattern.
    /// </summary>
    /// <param name="shard">The data shard descriptor.</param>
    /// <param name="accessPattern">Recent access pattern metrics.</param>
    /// <returns>The recommended <see cref="StorageTier"/>.</returns>
    public StorageTier RecommendTier(DataShardDescriptor shard, ShardAccessPattern accessPattern)
    {
        // Hot: high read or write throughput
        if (accessPattern.ReadsPerSecond > HotReadsThreshold ||
            accessPattern.WritesPerSecond > HotWritesThreshold)
        {
            return StorageTier.Hot;
        }

        // Warm: moderate read activity
        if (accessPattern.ReadsPerSecond > WarmReadsThreshold)
        {
            return StorageTier.Warm;
        }

        // Frozen: no access for 30+ days
        long frozenThresholdTicks = DateTime.UtcNow.Ticks - TimeSpan.FromDays(FrozenInactiveDays).Ticks;
        if (accessPattern.LastAccessUtcTicks < frozenThresholdTicks && accessPattern.LastAccessUtcTicks > 0)
        {
            return StorageTier.Frozen;
        }

        // Default: Cold
        return StorageTier.Cold;
    }

    /// <summary>
    /// Creates a minimal passthrough placement for single-VDE deployments.
    /// No policy evaluation, rack-awareness, or cost calculation is performed.
    /// </summary>
    /// <param name="shardVdeId">The shard VDE identifier.</param>
    /// <param name="deviceId">The single compound block device identifier.</param>
    /// <returns>A zero-overhead <see cref="ShardPlacement"/> with Hot tier and one Primary target.</returns>
    public static ShardPlacement CreatePassthrough(Guid shardVdeId, Guid deviceId)
    {
        return new ShardPlacement
        {
            ShardVdeId = shardVdeId,
            AssignedTier = StorageTier.Hot,
            AppliedPolicy = PlacementPolicy.HotDefault(),
            Targets = new[]
            {
                new PlacementTarget
                {
                    CompoundBlockDeviceId = deviceId,
                    RackId = string.Empty,
                    Role = PlacementRole.Primary
                }
            },
            EstimatedMonthlyCostUsd = 0.0,
            PlacementTimestampUtcTicks = DateTime.UtcNow.Ticks,
            Warning = null
        };
    }

    /// <summary>
    /// Selects devices from distinct racks using round-robin rack distribution.
    /// Falls back to best-effort (allowing same-rack) if insufficient distinct racks.
    /// </summary>
    private static List<CompoundBlockDeviceInfo> SelectRackAware(
        List<CompoundBlockDeviceInfo> candidates,
        int requiredCount,
        out string? warning)
    {
        warning = null;

        // Group by rack
        var rackGroups = new Dictionary<string, List<CompoundBlockDeviceInfo>>(StringComparer.Ordinal);
        foreach (var device in candidates)
        {
            string rack = device.RackId ?? string.Empty;
            if (!rackGroups.TryGetValue(rack, out var list))
            {
                list = new List<CompoundBlockDeviceInfo>();
                rackGroups[rack] = list;
            }
            list.Add(device);
        }

        // Sort racks by number of available devices (descending) for balanced selection
        var sortedRacks = rackGroups.OrderByDescending(kv => kv.Value.Count).ToList();

        // Round-robin across racks
        var selected = new List<CompoundBlockDeviceInfo>(requiredCount);
        var rackIndexes = new int[sortedRacks.Count];
        int rackPointer = 0;

        while (selected.Count < requiredCount)
        {
            bool addedAny = false;

            for (int i = 0; i < sortedRacks.Count && selected.Count < requiredCount; i++)
            {
                int rackIdx = (rackPointer + i) % sortedRacks.Count;
                var rackDevices = sortedRacks[rackIdx].Value;

                if (rackIndexes[rackIdx] < rackDevices.Count)
                {
                    // Only take one device per rack per round for rack diversity
                    selected.Add(rackDevices[rackIndexes[rackIdx]]);
                    rackIndexes[rackIdx]++;
                    addedAny = true;
                }
            }

            if (!addedAny)
            {
                // Not enough devices across all racks -- this shouldn't happen since
                // we validated candidate count, but guard against it
                break;
            }

            rackPointer = (rackPointer + 1) % sortedRacks.Count;
        }

        // Check if we achieved full rack diversity
        var usedRacks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var device in selected)
        {
            usedRacks.Add(device.RackId ?? string.Empty);
        }

        if (usedRacks.Count < selected.Count)
        {
            warning = $"Rack-awareness degraded: {selected.Count} targets placed across {usedRacks.Count} distinct racks.";
        }

        return selected;
    }

    /// <summary>
    /// Selects devices without rack-awareness constraints, preferring devices with more free capacity.
    /// </summary>
    private static List<CompoundBlockDeviceInfo> SelectBestEffort(
        List<CompoundBlockDeviceInfo> candidates,
        int requiredCount)
    {
        // Sort by free capacity descending to prefer less-utilized devices
        candidates.Sort((a, b) =>
        {
            long freeA = a.CapacityBlocks - a.UsedBlocks;
            long freeB = b.CapacityBlocks - b.UsedBlocks;
            return freeB.CompareTo(freeA);
        });

        return candidates.GetRange(0, Math.Min(requiredCount, candidates.Count));
    }
}
