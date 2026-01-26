namespace DataWarehouse.SDK.Contracts
{
    /// <summary>
    /// Proposal
    /// </summary>
    public class Proposal
    {
        /// <summary>
        /// ID
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// Command
        /// </summary>
        public string Command { get; set; } = string.Empty;

        /// <summary>
        /// Payload
        /// </summary>
        public byte[] Payload { get; set; } = [];
    }

    /// <summary>
    /// Consensus interface
    /// </summary>
    public interface IConsensusEngine : IPlugin
    {
        /// <summary>
        /// Is leader
        /// </summary>
        bool IsLeader { get; }

        /// <summary>
        /// Propose a state change to the cluster.
        /// Returns only when Quorum is reached.
        /// </summary>
        Task<bool> ProposeAsync(Proposal proposal);

        /// <summary>
        /// Subscribe to committed entries from other nodes.
        /// </summary>
        void OnCommit(Action<Proposal> handler);
    }

    #region Geo-Distributed Consensus Types

    public enum GeoRaftRole
    {
        Follower,
        Candidate,
        Leader,
        Learner,
        Witness
    }

    public class GeoRaftState
    {
        public GeoRaftRole Role { get; set; }
        public long CurrentTerm { get; set; }
        public string? VotedFor { get; set; }
        public long CommitIndex { get; set; }
        public long LastApplied { get; set; }
        public bool IsTransferringLeadership { get; set; }
    }

    public enum NodeRole
    {
        Voter,
        Learner,
        Observer
    }

    public enum NodeStatus
    {
        Active,
        Inactive,
        Failed
    }

    public class GeoRaftNode
    {
        public string NodeId { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public string DatacenterId { get; set; } = string.Empty;
        public long NextIndex { get; set; }
        public long MatchIndex { get; set; }
        public bool IsLearner { get; set; }
        public NodeRole Role { get; set; }
        public NodeStatus Status { get; set; }
        public DateTime LastHeartbeat { get; set; }
        public object? VotingStatus { get; set; }
    }

    public class LogReplicator
    {
        public LogReplicator(object plugin) { }

        public long GetLastLogIndex() => 0;
        public long GetLastLogTerm() => 0;
        public Task CompactLogAsync(int threshold) => Task.CompletedTask;
        public Task<bool> AppendAndReplicateAsync(object entry) => Task.FromResult(true);
        public Task<bool> ReplicateToNodeAsync(string nodeId, long targetIndex) => Task.FromResult(true);
    }

    public class SessionState
    {
        public string SessionId { get; set; } = string.Empty;
        public long LastSeenIndex { get; set; }
    }

    public class HierarchicalConsensusManager
    {
        public HierarchicalConsensusManager(object plugin) { }
    }

    public class JointConsensusState
    {
        public HashSet<string> OldMembers { get; set; } = new();
        public HashSet<string> NewMembers { get; set; } = new();
        public HashSet<string> OldConfiguration { get; set; } = new();
        public HashSet<string> NewConfiguration { get; set; } = new();
        public object? Phase { get; set; }
        public DateTime StartedAt { get; set; }
    }

    public class SnapshotManager
    {
        public SnapshotManager(object plugin) { }

        public long LastSnapshotIndex { get; set; }
        public Task CreateSnapshotAsync(long index) => Task.CompletedTask;
    }

    public class SpeculativeExecution
    {
        public string ExecutionId { get; set; } = string.Empty;
        public byte[] Result { get; set; } = Array.Empty<byte>();
    }

    public class MembershipChangeRequest
    {
        public string NodeId { get; set; } = string.Empty;
        public string ChangeType { get; set; } = string.Empty;
    }

    public class MembershipChangeResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public HashSet<string> OldConfiguration { get; set; } = new();
        public HashSet<string> NewConfiguration { get; set; } = new();
    }

    public class RequestVoteMessage
    {
        public long Term { get; set; }
        public string CandidateId { get; set; } = string.Empty;
        public string CandidateDatacenterId { get; set; } = string.Empty;
        public long LastLogIndex { get; set; }
        public long LastLogTerm { get; set; }
        public bool IsPreVote { get; set; }
    }

    public class RequestVoteResponse
    {
        public long Term { get; set; }
        public bool VoteGranted { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    public class PreVoteResult
    {
        public bool Success { get; set; }
        public long Term { get; set; }
        public int VotesReceived { get; set; }
        public int VotesRequired { get; set; }
    }

    #endregion
}