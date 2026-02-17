using System.Threading;
using System.Threading.Tasks;
using static DataWarehouse.SDK.Contracts.ConsensusPluginBase;

namespace DataWarehouse.Plugins.UltimateConsensus;

/// <summary>
/// Strategy interface for consensus algorithms within the Multi-Raft framework.
/// Allows plugging in different consensus algorithms (Raft, Paxos, PBFT, ZAB)
/// while maintaining the same group routing and lifecycle.
/// </summary>
public interface IRaftStrategy
{
    /// <summary>
    /// Name of the consensus algorithm.
    /// </summary>
    string AlgorithmName { get; }

    /// <summary>
    /// Proposes data to the consensus group.
    /// </summary>
    /// <param name="data">Binary data to propose.</param>
    /// <param name="groupId">Raft group to propose to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Consensus result.</returns>
    Task<ConsensusResult> ProposeAsync(byte[] data, int groupId, CancellationToken ct);
}

/// <summary>
/// Full Raft consensus strategy implementation.
/// Uses leader election, log replication, and majority quorum for consensus.
/// </summary>
public sealed class RaftStrategy : IRaftStrategy
{
    private readonly UltimateConsensusPlugin _plugin;

    /// <inheritdoc/>
    public string AlgorithmName => "Raft";

    /// <summary>
    /// Creates a Raft strategy bound to the specified plugin.
    /// </summary>
    /// <param name="plugin">The parent consensus plugin.</param>
    public RaftStrategy(UltimateConsensusPlugin plugin)
    {
        _plugin = plugin ?? throw new System.ArgumentNullException(nameof(plugin));
    }

    /// <inheritdoc/>
    public async Task<ConsensusResult> ProposeAsync(byte[] data, int groupId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return await _plugin.ProposeToGroupAsync(data, groupId, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Paxos consensus strategy stub. Interface-compliant placeholder for future implementation.
/// Paxos provides consensus with flexible leader roles (proposer/acceptor/learner).
/// </summary>
public sealed class PaxosStrategy : IRaftStrategy
{
    /// <inheritdoc/>
    public string AlgorithmName => "Paxos";

    /// <inheritdoc/>
    public Task<ConsensusResult> ProposeAsync(byte[] data, int groupId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new ConsensusResult(
            false, null, 0, "Paxos strategy not yet implemented. Use Raft strategy for production consensus."));
    }
}

/// <summary>
/// PBFT (Practical Byzantine Fault Tolerance) strategy stub.
/// PBFT tolerates up to f Byzantine (malicious) nodes in a 3f+1 cluster.
/// </summary>
public sealed class PbftStrategy : IRaftStrategy
{
    /// <inheritdoc/>
    public string AlgorithmName => "PBFT";

    /// <inheritdoc/>
    public Task<ConsensusResult> ProposeAsync(byte[] data, int groupId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new ConsensusResult(
            false, null, 0, "PBFT strategy not yet implemented. Use Raft strategy for production consensus."));
    }
}

/// <summary>
/// ZAB (Zookeeper Atomic Broadcast) strategy stub.
/// ZAB provides total order broadcast with crash recovery, similar to ZooKeeper.
/// </summary>
public sealed class ZabStrategy : IRaftStrategy
{
    /// <inheritdoc/>
    public string AlgorithmName => "ZAB";

    /// <inheritdoc/>
    public Task<ConsensusResult> ProposeAsync(byte[] data, int groupId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new ConsensusResult(
            false, null, 0, "ZAB strategy not yet implemented. Use Raft strategy for production consensus."));
    }
}
