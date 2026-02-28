using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Primitives;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts
{
    /// <summary>
    /// Abstract base class for consensus engine plugins (Raft, Paxos, etc.).
    /// Provides default implementations for distributed consensus operations.
    /// Supports intelligent leader election and quorum decisions.
    /// </summary>
    public abstract class ConsensusPluginBase : InfrastructurePluginBase, IConsensusEngine
    {
        /// <inheritdoc/>
        public override string InfrastructureDomain => "Consensus";

        /// <summary>
        /// Category is always OrchestrationProvider for consensus plugins.
        /// </summary>
        public override PluginCategory Category => PluginCategory.OrchestrationProvider;

        /// <summary>
        /// Whether this node is currently the leader.
        /// </summary>
        public abstract bool IsLeader { get; }

        /// <summary>
        /// The node ID of the current Raft leader as known to this node.
        /// Returns <c>null</c> when the cluster has no established leader (e.g. during election).
        /// Override in derived classes that track leader state explicitly.
        /// </summary>
        public virtual string? LeaderId => null;

        /// <summary>
        /// Propose a state change to the cluster. Returns when quorum is reached.
        /// Must be implemented by derived classes.
        /// </summary>
        public abstract Task<bool> ProposeAsync(Proposal proposal, CancellationToken cancellationToken = default);

        /// <summary>
        /// Subscribe to committed entries from other nodes.
        /// Returns a registration token that can be disposed to unsubscribe.
        /// Must be implemented by derived classes.
        /// </summary>
        public abstract IDisposable OnCommit(Action<Proposal> handler);

        /// <summary>
        /// Get current cluster state. Override for custom state reporting.
        /// </summary>
        public virtual Task<ClusterState> GetClusterStateAsync()
        {
            return Task.FromResult(new ClusterState { IsHealthy = true });
        }

        #region Multi-Raft Extensions (Phase 41.1-06)

        /// <summary>
        /// Result of a consensus proposal operation.
        /// </summary>
        /// <param name="Success">Whether the proposal was committed by quorum.</param>
        /// <param name="LeaderId">Current leader node ID (null if no leader).</param>
        /// <param name="LogIndex">Log index of the committed entry.</param>
        /// <param name="Error">Error message if proposal failed.</param>
        public record ConsensusResult(bool Success, string? LeaderId, long LogIndex, string? Error);

        /// <summary>
        /// Current state of the consensus engine.
        /// </summary>
        /// <param name="State">State description (e.g., "follower", "leader", "multi-raft").</param>
        /// <param name="LeaderId">Current leader node ID.</param>
        /// <param name="CommitIndex">Highest committed log index.</param>
        /// <param name="LastApplied">Highest log index applied to state machine.</param>
        public record ConsensusState(string State, string? LeaderId, long CommitIndex, long LastApplied);

        /// <summary>
        /// Cluster health summary across all consensus groups.
        /// </summary>
        /// <param name="TotalNodes">Total nodes across all groups.</param>
        /// <param name="HealthyNodes">Number of healthy (reachable) nodes.</param>
        /// <param name="NodeStates">Per-node state descriptions.</param>
        public record ClusterHealthInfo(int TotalNodes, int HealthyNodes, Dictionary<string, string> NodeStates);

        /// <summary>
        /// Propose raw data to the consensus cluster. Routes to appropriate group in Multi-Raft.
        /// Default implementation wraps data in a <see cref="Proposal"/> and delegates to
        /// <see cref="ProposeAsync(Proposal, CancellationToken)"/>.
        /// </summary>
        /// <param name="data">Binary data to propose.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Result including success, leader, log index, and error info.</returns>
        public virtual async Task<ConsensusResult> ProposeAsync(byte[] data, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var proposal = new Proposal { Payload = data };
            var success = await ProposeAsync(proposal, ct).ConfigureAwait(false);
            return new ConsensusResult(success, null, 0, success ? null : "Proposal not committed by quorum");
        }

        /// <summary>
        /// Checks if the current node is a leader (async version with cancellation support).
        /// Default implementation returns <see cref="IsLeader"/>.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if this node is leader in at least one consensus group.</returns>
        public virtual Task<bool> IsLeaderAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(IsLeader);
        }

        /// <summary>
        /// Gets the current consensus state. Override for detailed state reporting.
        /// Default implementation builds state from <see cref="GetClusterStateAsync"/>.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Current consensus state including leader, commit index, and last applied.</returns>
        public virtual async Task<ConsensusState> GetStateAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var cluster = await GetClusterStateAsync().ConfigureAwait(false);
            return new ConsensusState(
                IsLeader ? "leader" : "follower",
                cluster.LeaderId,
                cluster.Term,
                cluster.Term);
        }

        /// <summary>
        /// Gets cluster health information aggregated across all consensus groups.
        /// Override for detailed per-group health reporting.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Aggregated health information.</returns>
        public virtual Task<ClusterHealthInfo> GetClusterHealthAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ClusterHealthInfo(0, 0, new Dictionary<string, string>()));
        }

        #endregion

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "Consensus";
            metadata["ConsensusAlgorithm"] = "Generic";
            metadata["SupportsLeaderElection"] = true;
            return metadata;
        }
    }

    /// <summary>
    /// Cluster state information.
    /// </summary>
    public class ClusterState
    {
        /// <summary>Whether the cluster is healthy.</summary>
        public bool IsHealthy { get; init; }
        /// <summary>Current leader node ID.</summary>
        public string? LeaderId { get; init; }
        /// <summary>Number of nodes in the cluster.</summary>
        public int NodeCount { get; init; }
        /// <summary>Current term/epoch.</summary>
        public long Term { get; init; }
    }
}
