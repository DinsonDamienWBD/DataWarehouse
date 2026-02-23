using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Policy
{
    /// <summary>
    /// Thread-safe, bounded in-memory implementation of <see cref="IPolicyStore"/>.
    /// Uses <see cref="ConcurrentDictionary{TKey,TValue}"/> with composite key format
    /// "featureId:level:path" (matching the persistence layer convention from Phase 69).
    /// <para>
    /// A secondary <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by "level:path"
    /// provides O(1) bloom-filter-style existence checks via <see cref="HasOverrideAsync"/>.
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 70: Cascade engine (CASC-01)")]
    public sealed class InMemoryPolicyStore : IPolicyStore
    {
        private readonly ConcurrentDictionary<string, FeaturePolicy> _policies = new();
        private readonly ConcurrentDictionary<string, int> _locationCounts = new();
        private readonly int _maxCapacity;

        /// <summary>
        /// Initializes a new instance of <see cref="InMemoryPolicyStore"/> with the specified capacity limit.
        /// </summary>
        /// <param name="maxCapacity">
        /// Maximum number of policy entries that can be stored simultaneously.
        /// Defaults to 100,000. Must be greater than zero.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxCapacity"/> is less than or equal to zero.</exception>
        public InMemoryPolicyStore(int maxCapacity = 100_000)
        {
            if (maxCapacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxCapacity), maxCapacity, "Maximum capacity must be greater than zero.");

            _maxCapacity = maxCapacity;
        }

        /// <summary>
        /// Gets the current number of policy entries stored.
        /// </summary>
        public int Count => _policies.Count;

        /// <inheritdoc />
        public Task<FeaturePolicy?> GetAsync(string featureId, PolicyLevel level, string path, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            var key = BuildKey(featureId, level, path);
            _policies.TryGetValue(key, out var policy);
            return Task.FromResult(policy);
        }

        /// <inheritdoc />
        public Task SetAsync(string featureId, PolicyLevel level, string path, FeaturePolicy policy, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            if (policy is null)
                throw new ArgumentNullException(nameof(policy));

            var key = BuildKey(featureId, level, path);
            var locationKey = BuildLocationKey(level, path);

            var isNew = !_policies.ContainsKey(key);
            if (isNew && _policies.Count >= _maxCapacity)
                throw new InvalidOperationException($"InMemoryPolicyStore capacity exceeded ({_maxCapacity}).");

            _policies.AddOrUpdate(key, _ => policy, (_, _) => policy);

            if (isNew)
            {
                _locationCounts.AddOrUpdate(locationKey, _ => 1, (_, count) => count + 1);
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task RemoveAsync(string featureId, PolicyLevel level, string path, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            var key = BuildKey(featureId, level, path);
            if (_policies.TryRemove(key, out _))
            {
                var locationKey = BuildLocationKey(level, path);
                _locationCounts.AddOrUpdate(locationKey, _ => 0, (_, count) => Math.Max(0, count - 1));

                // Clean up zero-count entries to avoid unbounded growth of the location index
                if (_locationCounts.TryGetValue(locationKey, out var remaining) && remaining <= 0)
                {
                    _locationCounts.TryRemove(locationKey, out _);
                }
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<(PolicyLevel Level, string Path, FeaturePolicy Policy)>> ListOverridesAsync(string featureId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            var prefix = featureId + ":";
            var result = new List<(PolicyLevel Level, string Path, FeaturePolicy Policy)>();

            foreach (var kvp in _policies)
            {
                if (kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var parts = ParseKey(kvp.Key);
                    result.Add((parts.Level, parts.Path, kvp.Value));
                }
            }

            return Task.FromResult<IReadOnlyList<(PolicyLevel Level, string Path, FeaturePolicy Policy)>>(result);
        }

        /// <inheritdoc />
        public Task<bool> HasOverrideAsync(PolicyLevel level, string path, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            var locationKey = BuildLocationKey(level, path);
            var exists = _locationCounts.TryGetValue(locationKey, out var count) && count > 0;
            return Task.FromResult(exists);
        }

        /// <summary>
        /// Clears all stored policies and location indexes.
        /// Intended for test reset scenarios.
        /// </summary>
        public void Clear()
        {
            _policies.Clear();
            _locationCounts.Clear();
        }

        private static string BuildKey(string featureId, PolicyLevel level, string path)
            => $"{featureId}:{level}:{path}";

        private static string BuildLocationKey(PolicyLevel level, string path)
            => $"{level}:{path}";

        private static (PolicyLevel Level, string Path) ParseKey(string compositeKey)
        {
            // Key format: "featureId:level:path"
            // Find the first colon (end of featureId), then parse level and path
            var firstColon = compositeKey.IndexOf(':');
            var remaining = compositeKey.Substring(firstColon + 1);
            var secondColon = remaining.IndexOf(':');
            var levelStr = remaining.Substring(0, secondColon);
            var path = remaining.Substring(secondColon + 1);

            var level = (PolicyLevel)Enum.Parse(typeof(PolicyLevel), levelStr);
            return (level, path);
        }
    }
}
