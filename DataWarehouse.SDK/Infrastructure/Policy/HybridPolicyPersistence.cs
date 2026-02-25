using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Policy
{
    /// <summary>
    /// Composite policy persistence that delegates CRUD operations to a primary policy store and
    /// replicates all mutating operations to a separate audit store. This is the recommended
    /// production configuration: policies in a fast database, audit trail in tamper-proof storage.
    /// <para>
    /// <b>Dual-store pattern:</b> All read operations (LoadAll, LoadProfile) are served exclusively
    /// from the <c>policyStore</c>. All write operations (Save, Delete, SaveProfile) are applied to
    /// both stores sequentially. If the policy store succeeds but the audit store fails, the method
    /// throws an <see cref="AggregateException"/> containing the audit failure so that callers can
    /// detect partial-write conditions. Fire-and-forget is <b>not acceptable</b> for compliance --
    /// both stores must succeed for a write to be considered complete.
    /// </para>
    /// <para>
    /// <b>Typical production setup:</b><br/>
    /// <c>policyStore = DatabasePolicyPersistence</c> (fast CRUD)<br/>
    /// <c>auditStore = TamperProofPolicyPersistence(InMemoryPolicyPersistence)</c> (immutable chain)<br/>
    /// Or <c>auditStore = TamperProofPolicyPersistence(FilePolicyPersistence)</c> for durable audit.
    /// </para>
    /// <para>
    /// This class implements <see cref="IPolicyPersistence"/> directly (decorator/composite pattern)
    /// rather than extending <see cref="PolicyPersistenceBase"/> to avoid double-serialization and
    /// to maintain full control over delegation semantics.
    /// </para>
    /// </summary>
    /// <example>
    /// <code>
    /// var dbStore = new DatabasePolicyPersistence(config);
    /// var auditInner = new InMemoryPolicyPersistence(config);
    /// var tamperProof = new TamperProofPolicyPersistence(auditInner);
    /// var hybrid = new HybridPolicyPersistence(dbStore, tamperProof);
    ///
    /// // Reads come from dbStore only
    /// var policies = await hybrid.LoadAllAsync();
    ///
    /// // Writes go to both stores
    /// await hybrid.SaveAsync("compression", PolicyLevel.VDE, "/", policy);
    /// </code>
    /// </example>
    [SdkCompatibility("6.0.0", Notes = "Phase 69: Hybrid policy persistence (PERS-06)")]
    public sealed class HybridPolicyPersistence : IPolicyPersistence
    {
        private readonly IPolicyPersistence _policyStore;
        private readonly IPolicyPersistence _auditStore;

        /// <summary>
        /// Initializes a new instance of <see cref="HybridPolicyPersistence"/> with separate stores
        /// for policy CRUD and audit trail.
        /// </summary>
        /// <param name="policyStore">
        /// The primary store that handles all CRUD operations for policies and profiles.
        /// Must not be null.
        /// </param>
        /// <param name="auditStore">
        /// The audit store that receives a copy of every mutating operation for compliance purposes.
        /// Must not be null. Typically a <see cref="TamperProofPolicyPersistence"/> instance.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="policyStore"/> or <paramref name="auditStore"/> is null.
        /// </exception>
        public HybridPolicyPersistence(IPolicyPersistence policyStore, IPolicyPersistence auditStore)
        {
            _policyStore = policyStore ?? throw new ArgumentNullException(nameof(policyStore));
            _auditStore = auditStore ?? throw new ArgumentNullException(nameof(auditStore));
        }

        /// <summary>
        /// Loads all persisted policies from the primary policy store. The audit store is write-only
        /// for policies and is not consulted during reads.
        /// </summary>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>A read-only list of all persisted policies from the policy store.</returns>
        public Task<IReadOnlyList<(string FeatureId, PolicyLevel Level, string Path, FeaturePolicy Policy)>> LoadAllAsync(CancellationToken ct = default)
        {
            return _policyStore.LoadAllAsync(ct);
        }

        /// <summary>
        /// Saves a policy to both the policy store and audit store sequentially. The policy store
        /// is written first. If the policy store succeeds but the audit store fails, an
        /// <see cref="AggregateException"/> is thrown containing the audit failure. Both stores must
        /// succeed for the operation to be considered complete.
        /// </summary>
        /// <param name="featureId">Unique identifier of the feature this policy governs.</param>
        /// <param name="level">The hierarchy level at which this policy applies.</param>
        /// <param name="path">The VDE path at the specified level.</param>
        /// <param name="policy">The feature policy to persist.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <exception cref="AggregateException">
        /// Thrown when the policy store succeeds but the audit store fails. Contains the audit
        /// store exception as an inner exception.
        /// </exception>
        public async Task SaveAsync(string featureId, PolicyLevel level, string path, FeaturePolicy policy, CancellationToken ct = default)
        {
            await _policyStore.SaveAsync(featureId, level, path, policy, ct).ConfigureAwait(false);

            try
            {
                await _auditStore.SaveAsync(featureId, level, path, policy, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new AggregateException(
                    $"Policy saved to primary store but audit store write failed for '{featureId}:{(int)level}:{path}'. " +
                    "The policy store is in an inconsistent state with the audit trail.",
                    ex);
            }
        }

        /// <summary>
        /// Deletes a policy from both the policy store and audit store sequentially. The policy store
        /// is written first. If the policy store succeeds but the audit store fails, an
        /// <see cref="AggregateException"/> is thrown containing the audit failure.
        /// </summary>
        /// <param name="featureId">Unique identifier of the feature to delete the policy for.</param>
        /// <param name="level">The hierarchy level from which to delete the policy.</param>
        /// <param name="path">The VDE path at the specified level.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <exception cref="AggregateException">
        /// Thrown when the policy store succeeds but the audit store fails.
        /// </exception>
        public async Task DeleteAsync(string featureId, PolicyLevel level, string path, CancellationToken ct = default)
        {
            await _policyStore.DeleteAsync(featureId, level, path, ct).ConfigureAwait(false);

            try
            {
                await _auditStore.DeleteAsync(featureId, level, path, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new AggregateException(
                    $"Policy deleted from primary store but audit store write failed for '{featureId}:{(int)level}:{path}'. " +
                    "The policy store is in an inconsistent state with the audit trail.",
                    ex);
            }
        }

        /// <summary>
        /// Saves the operational profile to both the policy store and audit store sequentially.
        /// The policy store is written first. If the audit store fails, an
        /// <see cref="AggregateException"/> is thrown.
        /// </summary>
        /// <param name="profile">The operational profile to persist.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <exception cref="AggregateException">
        /// Thrown when the policy store succeeds but the audit store fails.
        /// </exception>
        public async Task SaveProfileAsync(OperationalProfile profile, CancellationToken ct = default)
        {
            await _policyStore.SaveProfileAsync(profile, ct).ConfigureAwait(false);

            try
            {
                await _auditStore.SaveProfileAsync(profile, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new AggregateException(
                    "Profile saved to primary store but audit store write failed. " +
                    "The policy store is in an inconsistent state with the audit trail.",
                    ex);
            }
        }

        /// <summary>
        /// Loads the operational profile from the primary policy store only. The audit store is
        /// write-only and not consulted during reads.
        /// </summary>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>The persisted operational profile, or null if none exists.</returns>
        public Task<OperationalProfile?> LoadProfileAsync(CancellationToken ct = default)
        {
            return _policyStore.LoadProfileAsync(ct);
        }

        /// <summary>
        /// Flushes pending writes on both the policy store and audit store concurrently.
        /// Both stores are flushed in parallel since flush operations are independent.
        /// </summary>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>A task representing the asynchronous flush operation on both stores.</returns>
        public Task FlushAsync(CancellationToken ct = default)
        {
            return Task.WhenAll(
                _policyStore.FlushAsync(ct),
                _auditStore.FlushAsync(ct));
        }
    }
}
