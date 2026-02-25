using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Policy
{
    /// <summary>
    /// Abstracts the storage backend for policy persistence, decoupling the <see cref="IPolicyStore"/>
    /// from any specific storage technology (file system, database, distributed store).
    /// <para>
    /// Implementations handle durable storage of policy definitions and operational profiles.
    /// The persistence layer is loaded at startup to populate the <see cref="IPolicyStore"/> and
    /// written to when policies are modified. Implementations may batch or buffer writes; callers
    /// should invoke <see cref="FlushAsync"/> to guarantee durability.
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 68: Policy Engine interfaces (SDKF-01)")]
    public interface IPolicyPersistence
    {
        /// <summary>
        /// Loads all persisted policies from the storage backend.
        /// Called at startup to hydrate the <see cref="IPolicyStore"/> with previously saved policies.
        /// </summary>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>A read-only list of all persisted policies as (FeatureId, Level, Path, Policy) tuples.</returns>
        Task<IReadOnlyList<(string FeatureId, PolicyLevel Level, string Path, FeaturePolicy Policy)>> LoadAllAsync(CancellationToken ct = default);

        /// <summary>
        /// Saves a single policy to the persistence backend.
        /// Creates the entry if it does not exist, or updates it if it does.
        /// </summary>
        /// <param name="featureId">Unique identifier of the feature this policy governs.</param>
        /// <param name="level">The hierarchy level at which this policy applies.</param>
        /// <param name="path">The VDE path at the specified level.</param>
        /// <param name="policy">The feature policy to persist.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task SaveAsync(string featureId, PolicyLevel level, string path, FeaturePolicy policy, CancellationToken ct = default);

        /// <summary>
        /// Deletes a policy from the persistence backend.
        /// Removes the durable record; the <see cref="IPolicyStore"/> should also be updated to reflect removal.
        /// </summary>
        /// <param name="featureId">Unique identifier of the feature to delete the policy for.</param>
        /// <param name="level">The hierarchy level from which to delete the policy.</param>
        /// <param name="path">The VDE path at the specified level.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task DeleteAsync(string featureId, PolicyLevel level, string path, CancellationToken ct = default);

        /// <summary>
        /// Saves the active <see cref="OperationalProfile"/> to the persistence backend.
        /// Ensures the profile survives process restarts.
        /// </summary>
        /// <param name="profile">The operational profile to persist.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task SaveProfileAsync(OperationalProfile profile, CancellationToken ct = default);

        /// <summary>
        /// Loads the previously saved active <see cref="OperationalProfile"/> from the persistence backend.
        /// Returns null if no profile has been persisted (first run or after reset).
        /// </summary>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>The persisted operational profile, or null if none exists.</returns>
        Task<OperationalProfile?> LoadProfileAsync(CancellationToken ct = default);

        /// <summary>
        /// Flushes any pending writes to durable storage. For lazy or batched implementations,
        /// this ensures all buffered changes are committed. For synchronous implementations, this is a no-op.
        /// </summary>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>A task representing the asynchronous flush operation.</returns>
        Task FlushAsync(CancellationToken ct = default);
    }
}
