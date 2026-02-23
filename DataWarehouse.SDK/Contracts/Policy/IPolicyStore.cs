using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Policy
{
    /// <summary>
    /// Provides CRUD operations for policy definitions within the VDE hierarchy.
    /// <para>
    /// The policy store is the in-memory (or cached) layer that <see cref="IPolicyEngine"/> queries
    /// during cascade resolution. It holds per-feature, per-level, per-path policy overrides.
    /// Persistence is handled separately by <see cref="IPolicyPersistence"/>; the store is the
    /// working set that the engine resolves against.
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 68: Policy Engine interfaces (SDKF-01)")]
    public interface IPolicyStore
    {
        /// <summary>
        /// Gets the policy for a specific feature at a given hierarchy level and path.
        /// Returns null if no override exists at this level and path.
        /// </summary>
        /// <param name="featureId">Unique identifier of the feature (e.g., "compression", "encryption").</param>
        /// <param name="level">The hierarchy level to query (Block, Chunk, Object, Container, VDE).</param>
        /// <param name="path">The VDE path at the specified level (e.g., "/container1/object2").</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>The feature policy at the specified location, or null if no override exists.</returns>
        Task<FeaturePolicy?> GetAsync(string featureId, PolicyLevel level, string path, CancellationToken ct = default);

        /// <summary>
        /// Sets (creates or updates) a policy for a specific feature at a given hierarchy level and path.
        /// This creates an override at the specified location in the VDE hierarchy.
        /// </summary>
        /// <param name="featureId">Unique identifier of the feature to set the policy for.</param>
        /// <param name="level">The hierarchy level at which to set the policy.</param>
        /// <param name="path">The VDE path at the specified level.</param>
        /// <param name="policy">The feature policy to store.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task SetAsync(string featureId, PolicyLevel level, string path, FeaturePolicy policy, CancellationToken ct = default);

        /// <summary>
        /// Removes a policy override at a specific hierarchy level and path.
        /// After removal, cascade resolution will skip this level for the specified feature at this path.
        /// </summary>
        /// <param name="featureId">Unique identifier of the feature to remove the override for.</param>
        /// <param name="level">The hierarchy level from which to remove the override.</param>
        /// <param name="path">The VDE path at the specified level.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task RemoveAsync(string featureId, PolicyLevel level, string path, CancellationToken ct = default);

        /// <summary>
        /// Lists all policy overrides for a specific feature across all hierarchy levels.
        /// Returns tuples of (Level, Path, Policy) for every location where an override exists.
        /// </summary>
        /// <param name="featureId">Unique identifier of the feature to list overrides for.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>A read-only list of all overrides for the specified feature.</returns>
        Task<IReadOnlyList<(PolicyLevel Level, string Path, FeaturePolicy Policy)>> ListOverridesAsync(string featureId, CancellationToken ct = default);

        /// <summary>
        /// Checks if any policy override exists at a given hierarchy level and path.
        /// Used by bloom filter fast-path optimization to skip unnecessary store lookups.
        /// </summary>
        /// <param name="level">The hierarchy level to check.</param>
        /// <param name="path">The VDE path at the specified level.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>True if at least one feature has an override at the specified level and path.</returns>
        Task<bool> HasOverrideAsync(PolicyLevel level, string path, CancellationToken ct = default);
    }
}
