using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Policy
{
    /// <summary>
    /// Abstract base class implementing <see cref="IPolicyPersistence"/> with a template-method pattern.
    /// Centralizes input validation, key generation, and serialization so that concrete implementations
    /// only need to override the storage I/O methods (<c>*CoreAsync</c>).
    /// <para>
    /// All five persistence backends (InMemory, File, Database, TamperProof, Hybrid) extend this class.
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 69: Policy persistence base (PERS-01)")]
    public abstract class PolicyPersistenceBase : IPolicyPersistence
    {
        /// <summary>
        /// Loads all persisted policies from the storage backend.
        /// Delegates to <see cref="LoadAllCoreAsync"/> for backend-specific retrieval.
        /// </summary>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>A read-only list of all persisted policies as (FeatureId, Level, Path, Policy) tuples.</returns>
        public Task<IReadOnlyList<(string FeatureId, PolicyLevel Level, string Path, FeaturePolicy Policy)>> LoadAllAsync(CancellationToken ct = default)
        {
            return LoadAllCoreAsync(ct);
        }

        /// <summary>
        /// Saves a single policy to the persistence backend. Validates inputs, serializes the policy,
        /// generates a composite key, and delegates to <see cref="SaveCoreAsync"/>.
        /// </summary>
        /// <param name="featureId">Unique identifier of the feature this policy governs.</param>
        /// <param name="level">The hierarchy level at which this policy applies.</param>
        /// <param name="path">The VDE path at the specified level.</param>
        /// <param name="policy">The feature policy to persist.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <exception cref="ArgumentException">Thrown when <paramref name="featureId"/> is null or empty.</exception>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="policy"/> is null.</exception>
        public Task SaveAsync(string featureId, PolicyLevel level, string path, FeaturePolicy policy, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(featureId))
                throw new ArgumentException("Feature ID must not be null or empty.", nameof(featureId));
            if (policy == null)
                throw new ArgumentNullException(nameof(policy));

            var key = GenerateKey(featureId, level, path);
            var serialized = PolicySerializationHelper.SerializePolicy(policy);
            return SaveCoreAsync(key, featureId, level, path, serialized, ct);
        }

        /// <summary>
        /// Deletes a policy from the persistence backend. Validates inputs, generates the composite key,
        /// and delegates to <see cref="DeleteCoreAsync"/>.
        /// </summary>
        /// <param name="featureId">Unique identifier of the feature to delete the policy for.</param>
        /// <param name="level">The hierarchy level from which to delete the policy.</param>
        /// <param name="path">The VDE path at the specified level.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <exception cref="ArgumentException">Thrown when <paramref name="featureId"/> is null or empty.</exception>
        public Task DeleteAsync(string featureId, PolicyLevel level, string path, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(featureId))
                throw new ArgumentException("Feature ID must not be null or empty.", nameof(featureId));

            var key = GenerateKey(featureId, level, path);
            return DeleteCoreAsync(key, ct);
        }

        /// <summary>
        /// Saves the active <see cref="OperationalProfile"/> to the persistence backend.
        /// Validates the profile, serializes it, and delegates to <see cref="SaveProfileCoreAsync"/>.
        /// </summary>
        /// <param name="profile">The operational profile to persist.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="profile"/> is null.</exception>
        public Task SaveProfileAsync(OperationalProfile profile, CancellationToken ct = default)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));

            var serialized = PolicySerializationHelper.SerializeProfile(profile);
            return SaveProfileCoreAsync(serialized, ct);
        }

        /// <summary>
        /// Loads the previously saved active <see cref="OperationalProfile"/> from the persistence backend.
        /// Delegates to <see cref="LoadProfileCoreAsync"/> and deserializes the result if non-null.
        /// </summary>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>The persisted operational profile, or null if none exists.</returns>
        public async Task<OperationalProfile?> LoadProfileAsync(CancellationToken ct = default)
        {
            var data = await LoadProfileCoreAsync(ct).ConfigureAwait(false);
            if (data == null)
                return null;

            return PolicySerializationHelper.DeserializeProfile(data);
        }

        /// <summary>
        /// Flushes any pending writes to durable storage. Delegates to <see cref="FlushCoreAsync"/>
        /// which defaults to a no-op. Override in batched or buffered backends.
        /// </summary>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        public Task FlushAsync(CancellationToken ct = default)
        {
            return FlushCoreAsync(ct);
        }

        /// <summary>
        /// Generates a deterministic, sortable composite key from the policy identity components.
        /// Format: <c>{featureId}:{(int)level}:{path}</c>.
        /// </summary>
        /// <param name="featureId">Unique identifier of the feature.</param>
        /// <param name="level">The hierarchy level.</param>
        /// <param name="path">The VDE path.</param>
        /// <returns>A composite key string suitable for use as a storage key.</returns>
        protected static string GenerateKey(string featureId, PolicyLevel level, string path)
        {
            return $"{featureId}:{(int)level}:{path}";
        }

        /// <summary>
        /// When overridden in a derived class, loads all persisted policies from the storage backend.
        /// </summary>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>A read-only list of all persisted policies as (FeatureId, Level, Path, Policy) tuples.</returns>
        protected abstract Task<IReadOnlyList<(string FeatureId, PolicyLevel Level, string Path, FeaturePolicy Policy)>> LoadAllCoreAsync(CancellationToken ct);

        /// <summary>
        /// When overridden in a derived class, saves a serialized policy to the storage backend.
        /// </summary>
        /// <param name="key">The composite key generated by <see cref="GenerateKey"/>.</param>
        /// <param name="featureId">Unique identifier of the feature.</param>
        /// <param name="level">The hierarchy level at which this policy applies.</param>
        /// <param name="path">The VDE path at the specified level.</param>
        /// <param name="serializedPolicy">The JSON-serialized policy as a byte array.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        protected abstract Task SaveCoreAsync(string key, string featureId, PolicyLevel level, string path, byte[] serializedPolicy, CancellationToken ct);

        /// <summary>
        /// When overridden in a derived class, deletes a policy from the storage backend.
        /// </summary>
        /// <param name="key">The composite key identifying the policy to delete.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        protected abstract Task DeleteCoreAsync(string key, CancellationToken ct);

        /// <summary>
        /// When overridden in a derived class, saves a serialized operational profile to the storage backend.
        /// </summary>
        /// <param name="serializedProfile">The JSON-serialized profile as a byte array.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        protected abstract Task SaveProfileCoreAsync(byte[] serializedProfile, CancellationToken ct);

        /// <summary>
        /// When overridden in a derived class, loads the serialized operational profile from the storage backend.
        /// Returns null if no profile has been persisted.
        /// </summary>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>The serialized profile bytes, or null if none exists.</returns>
        protected abstract Task<byte[]?> LoadProfileCoreAsync(CancellationToken ct);

        /// <summary>
        /// When overridden in a derived class, flushes any pending writes to durable storage.
        /// The default implementation is a no-op suitable for synchronous backends.
        /// </summary>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        protected virtual Task FlushCoreAsync(CancellationToken ct)
        {
            return Task.CompletedTask;
        }
    }
}
