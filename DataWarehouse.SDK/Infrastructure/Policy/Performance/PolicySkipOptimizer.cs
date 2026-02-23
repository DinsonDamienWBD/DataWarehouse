using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Policy.Performance
{
    /// <summary>
    /// Wraps an <see cref="IPolicyStore"/> with a <see cref="BloomFilterSkipIndex"/> pre-check
    /// to short-circuit store access for paths with no policy overrides.
    /// <para>
    /// In typical deployments, 99%+ of VDE paths have no overrides. The skip optimizer uses
    /// the bloom filter to confirm "definitely no override" in O(1), avoiding the full store
    /// lookup. When the bloom filter says "maybe has override" (including ~1% false positives),
    /// the request falls through to the real store for authoritative resolution.
    /// </para>
    /// <para>
    /// This class does NOT implement <see cref="IPolicyStore"/> -- it is a companion that the
    /// fast-path engine uses alongside the store, avoiding changes to the store interface contract.
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 76: Performance Optimization (PERF-02)")]
    public sealed class PolicySkipOptimizer
    {
        private readonly IPolicyStore _innerStore;
        private readonly BloomFilterSkipIndex _bloomFilter;
        private long _skipCount;
        private long _fallThroughCount;

        /// <summary>
        /// Initializes a new skip optimizer wrapping the given store with bloom filter pre-checks.
        /// </summary>
        /// <param name="innerStore">The real policy store to delegate to on bloom filter hits.</param>
        /// <param name="bloomFilter">The pre-built bloom filter for O(1) override existence checks.</param>
        public PolicySkipOptimizer(IPolicyStore innerStore, BloomFilterSkipIndex bloomFilter)
        {
            _innerStore = innerStore ?? throw new ArgumentNullException(nameof(innerStore));
            _bloomFilter = bloomFilter ?? throw new ArgumentNullException(nameof(bloomFilter));
        }

        /// <summary>
        /// Checks whether a policy override exists at the given level and path, using the bloom
        /// filter to skip the store lookup when no override is present.
        /// </summary>
        /// <param name="level">The policy hierarchy level to check.</param>
        /// <param name="path">The VDE path to check.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>
        /// False if definitely no override (O(1) bloom filter skip).
        /// Delegates to the inner store if the bloom filter indicates a possible override.
        /// </returns>
        public Task<bool> HasOverrideOptimizedAsync(PolicyLevel level, string path, CancellationToken ct = default)
        {
            if (!_bloomFilter.MayContain(level, path))
            {
                Interlocked.Increment(ref _skipCount);
                return Task.FromResult(false);
            }

            Interlocked.Increment(ref _fallThroughCount);
            return _innerStore.HasOverrideAsync(level, path, ct);
        }

        /// <summary>
        /// Gets the policy for a specific feature at a given level and path, using the bloom
        /// filter to return null immediately when no override is present.
        /// </summary>
        /// <param name="featureId">The feature identifier to look up.</param>
        /// <param name="level">The policy hierarchy level to query.</param>
        /// <param name="path">The VDE path at the specified level.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>
        /// Null if definitely no override (O(1) bloom filter skip).
        /// Delegates to the inner store if the bloom filter indicates a possible override.
        /// </returns>
        public Task<FeaturePolicy?> GetOptimizedAsync(string featureId, PolicyLevel level, string path, CancellationToken ct = default)
        {
            if (!_bloomFilter.MayContain(level, path))
            {
                Interlocked.Increment(ref _skipCount);
                return Task.FromResult<FeaturePolicy?>(null);
            }

            Interlocked.Increment(ref _fallThroughCount);
            return _innerStore.GetAsync(featureId, level, path, ct);
        }

        /// <summary>
        /// Rebuilds the bloom filter from the current state of the inner policy store.
        /// Called when policies change (same trigger as PERF-06 cache invalidation).
        /// </summary>
        /// <param name="featureIds">All feature IDs to scan for overrides.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        public async Task RebuildFilterAsync(IReadOnlyList<string> featureIds, CancellationToken ct = default)
        {
            if (featureIds is null)
                throw new ArgumentNullException(nameof(featureIds));

            _bloomFilter.Clear();

            foreach (string featureId in featureIds)
            {
                ct.ThrowIfCancellationRequested();
                var overrides = await _innerStore.ListOverridesAsync(featureId, ct).ConfigureAwait(false);
                foreach (var (level, path, _) in overrides)
                {
                    _bloomFilter.Add(level, path);
                }
            }
        }

        /// <summary>
        /// Returns the current skip and fall-through counts for observability and diagnostics.
        /// </summary>
        /// <returns>A tuple of (Skips, FallThroughs) representing bloom filter decisions.</returns>
        public (long Skips, long FallThroughs) GetStatistics()
        {
            return (Interlocked.Read(ref _skipCount), Interlocked.Read(ref _fallThroughCount));
        }

        /// <summary>
        /// Gets the ratio of bloom filter skips to total checks.
        /// Should be >= 0.99 in normal deployments where 99%+ of paths have no overrides.
        /// Returns 1.0 if no checks have been made yet.
        /// </summary>
        public double SkipRatio
        {
            get
            {
                long skips = Interlocked.Read(ref _skipCount);
                long fallThroughs = Interlocked.Read(ref _fallThroughCount);
                long total = skips + fallThroughs;
                return total > 0 ? skips / (double)total : 1.0;
            }
        }
    }
}
