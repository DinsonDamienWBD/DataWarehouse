using System;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Replication
{
    /// <summary>
    /// Lightweight cluster membership interface for DVV (Dotted Version Vector) pruning.
    /// Provides the active node set and membership change callbacks needed for
    /// membership-aware causality tracking. Dead node entries are pruned from DVVs
    /// when nodes leave the cluster, preventing unbounded vector growth.
    /// </summary>
    /// <remarks>
    /// This interface is separate from <see cref="Contracts.Distributed.IClusterMembership"/>
    /// which provides full cluster join/leave/health semantics. This is intentionally
    /// simpler: DVV only needs to know which nodes are active for pruning decisions.
    /// Implementations should bridge to the full cluster membership system.
    /// Thread-safety: Implementations must be thread-safe as DVV operations may
    /// call <see cref="GetActiveNodes"/> from multiple threads concurrently.
    /// </remarks>
    [SdkCompatibility("3.0.0", Notes = "Phase 41.1-06: DVV membership-aware causality tracking")]
    public interface IReplicationClusterMembership
    {
        /// <summary>
        /// Gets the set of currently active node identifiers in the cluster.
        /// Used by <see cref="DottedVersionVector.PruneDeadNodes"/> to remove
        /// entries for nodes no longer in the cluster.
        /// </summary>
        /// <returns>A read-only set of active node IDs. Never null; empty if single-node.</returns>
        IReadOnlySet<string> GetActiveNodes();

        /// <summary>
        /// Registers a callback to be invoked when a new node joins the cluster.
        /// </summary>
        /// <param name="callback">Action receiving the new node's identifier.</param>
        void RegisterNodeAdded(Action<string> callback);

        /// <summary>
        /// Registers a callback to be invoked when a node leaves or is declared dead.
        /// DVV uses this to trigger automatic pruning of dead node entries.
        /// </summary>
        /// <param name="callback">Action receiving the departed node's identifier.</param>
        void RegisterNodeRemoved(Action<string> callback);
    }
}
