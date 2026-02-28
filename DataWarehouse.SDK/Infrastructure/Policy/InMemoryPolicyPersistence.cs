using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Policy
{
    /// <summary>
    /// Thread-safe, bounded in-memory implementation of <see cref="PolicyPersistenceBase"/>.
    /// Stores policies in a <see cref="ConcurrentDictionary{TKey,TValue}"/> with a configurable
    /// maximum capacity to prevent unbounded memory growth.
    /// <para>
    /// Primary use cases: unit/integration testing, ephemeral deployments, and development scenarios
    /// where persistence across process restarts is not required.
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 69: In-memory policy persistence (PERS-02)")]
    public sealed class InMemoryPolicyPersistence : PolicyPersistenceBase
    {
        private readonly ConcurrentDictionary<string, (string FeatureId, PolicyLevel Level, string Path, byte[] Data)> _policies = new();
        private readonly object _capacityLock = new();
        private byte[]? _profileData;
        private readonly int _maxCapacity;

        /// <summary>
        /// Initializes a new instance of <see cref="InMemoryPolicyPersistence"/> with the specified capacity limit.
        /// </summary>
        /// <param name="maxCapacity">
        /// Maximum number of policies that can be stored simultaneously.
        /// Defaults to 100,000. Must be greater than zero.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxCapacity"/> is less than or equal to zero.</exception>
        public InMemoryPolicyPersistence(int maxCapacity = 100_000)
        {
            if (maxCapacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxCapacity), maxCapacity, "Maximum capacity must be greater than zero.");

            _maxCapacity = maxCapacity;
        }

        /// <summary>
        /// Gets the current number of policies stored in memory.
        /// </summary>
        public int Count => _policies.Count;

        /// <summary>
        /// Clears all stored policies and the profile from memory.
        /// Intended for test reset scenarios.
        /// </summary>
        public void Clear()
        {
            _policies.Clear();
            _profileData = null;
        }

        /// <inheritdoc />
        protected override Task<IReadOnlyList<(string FeatureId, PolicyLevel Level, string Path, FeaturePolicy Policy)>> LoadAllCoreAsync(CancellationToken ct)
        {
            var result = new List<(string FeatureId, PolicyLevel Level, string Path, FeaturePolicy Policy)>();

            foreach (var entry in _policies.Values)
            {
                var policy = PolicySerializationHelper.DeserializePolicy(entry.Data);
                result.Add((entry.FeatureId, entry.Level, entry.Path, policy));
            }

            return Task.FromResult<IReadOnlyList<(string FeatureId, PolicyLevel Level, string Path, FeaturePolicy Policy)>>(result);
        }

        /// <inheritdoc />
        protected override Task SaveCoreAsync(string key, string featureId, PolicyLevel level, string path, byte[] serializedPolicy, CancellationToken ct)
        {
            // Atomic capacity check + insert under lock to prevent exceeding capacity
            lock (_capacityLock)
            {
                if (!_policies.ContainsKey(key) && _policies.Count >= _maxCapacity)
                    throw new InvalidOperationException($"InMemory persistence capacity exceeded ({_maxCapacity}).");

                _policies.AddOrUpdate(
                    key,
                    _ => (featureId, level, path, serializedPolicy),
                    (_, _) => (featureId, level, path, serializedPolicy));
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        protected override Task DeleteCoreAsync(string key, CancellationToken ct)
        {
            _policies.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        protected override Task SaveProfileCoreAsync(byte[] serializedProfile, CancellationToken ct)
        {
            Interlocked.Exchange(ref _profileData, serializedProfile);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        protected override Task<byte[]?> LoadProfileCoreAsync(CancellationToken ct)
        {
            return Task.FromResult(_profileData);
        }
    }
}
