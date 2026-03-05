using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;

namespace DataWarehouse.SDK.VirtualDiskEngine.Retention;

/// <summary>
/// Describes a time-based retention policy that maps data age to epoch boundaries.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87-32: Epoch-gated lazy deletion (VOPT-49)")]
public sealed record RetentionPolicy(string Name, TimeSpan Duration, bool AutoDelete);

/// <summary>
/// Represents a retention policy that has exceeded its configured duration and is ready
/// for epoch-based bulk deletion.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87-32: Epoch-gated lazy deletion (VOPT-49)")]
public readonly struct RetentionPolicyExpiration
{
    /// <summary>Name of the retention policy that has expired.</summary>
    public string PolicyName { get; init; }

    /// <summary>
    /// The epoch at or before which data covered by this policy is considered expired.
    /// Advancing <c>OldestActiveEpoch</c> to this value logically deletes all pre-epoch data.
    /// </summary>
    public long ExpirationEpoch { get; init; }

    /// <summary>
    /// How long the policy has been overdue (current epoch > expiration epoch, expressed as epoch time).
    /// </summary>
    public TimeSpan OverdueBy { get; init; }
}

/// <summary>
/// Maps time-based retention policies to epoch boundaries, enabling automatic
/// bulk expiration of time-series data via epoch advancement.
/// </summary>
/// <remarks>
/// <para>
/// Time-series workloads (IoT telemetry, metrics, logs) generate millions of records
/// that need periodic expiration. Instead of issuing one delete per record,
/// <see cref="RetentionEpochMapper"/> computes the epoch boundary that corresponds
/// to the cutoff time, allowing a lazy deletion pass to perform a
/// single <c>OldestActiveEpoch</c> advancement — an O(1) logical bulk delete.
/// </para>
/// <para>
/// Epoch semantics: epochs are monotonically increasing counters advanced at a
/// configurable interval (epochDuration, supplied to query methods). Data
/// written in epoch N is logically deleted when <c>OldestActiveEpoch</c> is advanced
/// past N.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87-32: Epoch-gated lazy deletion (VOPT-49)")]
public sealed class RetentionEpochMapper
{
    private readonly Dictionary<string, RetentionPolicy> _policies = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the number of registered retention policies.
    /// </summary>
    public int PolicyCount => _policies.Count;

    /// <summary>
    /// Registers a named retention policy. If a policy with the same name exists,
    /// it is replaced.
    /// </summary>
    /// <param name="policy">The retention policy to register.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="policy"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown if the policy duration is non-positive.</exception>
    public void RegisterPolicy(RetentionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.Duration <= TimeSpan.Zero)
            throw new ArgumentException("Retention policy duration must be positive.", nameof(policy));

        _policies[policy.Name] = policy;
    }

    /// <summary>
    /// Removes a retention policy by name.
    /// </summary>
    /// <param name="policyName">Name of the policy to remove.</param>
    /// <returns><c>true</c> if the policy was found and removed; <c>false</c> otherwise.</returns>
    public bool RemovePolicy(string policyName) => _policies.Remove(policyName);

    /// <summary>
    /// Returns whether a policy with the given name is registered.
    /// </summary>
    /// <param name="policyName">The policy name to look up.</param>
    public bool HasPolicy(string policyName) => _policies.ContainsKey(policyName);

    /// <summary>
    /// Computes the epoch number at which data under <paramref name="policyName"/> expires.
    /// </summary>
    /// <param name="policyName">Name of the retention policy.</param>
    /// <param name="currentEpoch">The current global epoch counter.</param>
    /// <param name="epochDuration">Wall-clock duration of one epoch (e.g. 1 minute).</param>
    /// <returns>
    /// The epoch boundary at which data is considered expired: all blocks written in an
    /// epoch &lt;= this value can be logically deleted by advancing <c>OldestActiveEpoch</c>.
    /// </returns>
    /// <exception cref="KeyNotFoundException">Thrown if <paramref name="policyName"/> is not registered.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="epochDuration"/> is non-positive.</exception>
    public long ComputeExpirationEpoch(string policyName, long currentEpoch, TimeSpan epochDuration)
    {
        if (epochDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(epochDuration), "Epoch duration must be positive.");

        if (!_policies.TryGetValue(policyName, out var policy))
            throw new KeyNotFoundException($"Retention policy '{policyName}' is not registered.");

        // Number of epochs that correspond to the retention duration.
        // Data written more than this many epochs ago has exceeded its retention window.
        long epochsBack = (long)Math.Ceiling(policy.Duration.TotalSeconds / epochDuration.TotalSeconds);
        long expirationEpoch = currentEpoch - epochsBack;

        // Epoch numbers are non-negative; clamp to zero.
        return Math.Max(0L, expirationEpoch);
    }

    /// <summary>
    /// Returns all registered policies whose expiration epoch is strictly less than
    /// <paramref name="currentEpoch"/> — i.e. policies that are overdue for enforcement.
    /// </summary>
    /// <param name="currentEpoch">The current global epoch counter.</param>
    /// <param name="epochDuration">Wall-clock duration of one epoch.</param>
    /// <returns>
    /// A list of <see cref="RetentionPolicyExpiration"/> values for policies that are
    /// ready for bulk deletion. Only policies where <see cref="RetentionPolicy.AutoDelete"/>
    /// is <c>true</c> are included.
    /// </returns>
    public IReadOnlyList<RetentionPolicyExpiration> GetExpiredPolicies(long currentEpoch, TimeSpan epochDuration)
    {
        if (epochDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(epochDuration), "Epoch duration must be positive.");

        var expired = new List<RetentionPolicyExpiration>();

        foreach (var (name, policy) in _policies)
        {
            if (!policy.AutoDelete)
                continue;

            long expEpoch = ComputeExpirationEpoch(name, currentEpoch, epochDuration);
            if (expEpoch < currentEpoch)
            {
                // How many epoch durations past the cutoff are we? This gives the overdue
                // interval expressed as wall-clock time.
                long epochsOverdue = currentEpoch - expEpoch;
                var overdueBy = epochDuration * epochsOverdue;

                expired.Add(new RetentionPolicyExpiration
                {
                    PolicyName = name,
                    ExpirationEpoch = expEpoch,
                    OverdueBy = overdueBy,
                });
            }
        }

        return expired;
    }

    /// <summary>
    /// Returns the minimum expiration epoch across all registered policies that have
    /// <see cref="RetentionPolicy.AutoDelete"/> set to <c>true</c>.
    /// This is the safe epoch to advance <c>OldestActiveEpoch</c> to for automatic
    /// bulk deletion of time-series data.
    /// </summary>
    /// <param name="currentEpoch">The current global epoch counter.</param>
    /// <param name="epochDuration">Wall-clock duration of one epoch.</param>
    /// <returns>
    /// The minimum expiration epoch, or <c>-1</c> if no auto-delete policies are registered.
    /// </returns>
    public long GetOldestRetentionEpoch(long currentEpoch, TimeSpan epochDuration)
    {
        if (epochDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(epochDuration), "Epoch duration must be positive.");

        long oldest = long.MaxValue;
        bool found = false;

        foreach (var (name, policy) in _policies)
        {
            if (!policy.AutoDelete)
                continue;

            long expEpoch = ComputeExpirationEpoch(name, currentEpoch, epochDuration);
            if (expEpoch < oldest)
            {
                oldest = expEpoch;
                found = true;
            }
        }

        return found ? oldest : -1L;
    }
}
