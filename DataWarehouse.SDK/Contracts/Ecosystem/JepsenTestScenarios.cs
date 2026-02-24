// Phase 89: Ecosystem Compatibility - Jepsen Test Scenarios (ECOS-17)
// Defines 7 distributed correctness test scenarios covering linearizability, Raft,
// CRDT convergence, WAL crash recovery, MVCC snapshot isolation, DVV replication, and split-brain

using System;
using System.Collections.Generic;

namespace DataWarehouse.SDK.Contracts.Ecosystem;

/// <summary>
/// Factory class providing pre-built Jepsen test scenarios that prove distributed correctness
/// of DataWarehouse under failure conditions. Each scenario returns a <see cref="JepsenTestPlan"/>
/// that can be executed by <see cref="JepsenTestHarness"/>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Jepsen test scenarios (ECOS-17)")]
public static class JepsenTestScenarios
{
    /// <summary>
    /// Tests linearizability under a majority/minority network partition.
    ///
    /// Setup: 5-node cluster with RegisterWorkload and 10 concurrent clients.
    /// Fault: NetworkPartitionFault (MajorityMinority) injected at T+10s for 30s.
    /// Duration: 60s total.
    /// Expected: ConsistencyModel.Linearizable.
    ///
    /// Passing means: The majority partition (3 nodes) continues to accept reads and writes,
    /// while the minority partition (2 nodes) rejects write operations. After partition heals,
    /// the minority nodes rejoin and converge to the majority state without data loss or
    /// conflicting values.
    /// </summary>
    /// <returns>A test plan for linearizability under network partition.</returns>
    public static JepsenTestPlan LinearizabilityUnderPartition()
    {
        return new JepsenTestPlan
        {
            TestName = "linearizability-under-partition",
            Duration = TimeSpan.FromSeconds(60),
            ConcurrentClients = 10,
            Workloads = new IWorkloadGenerator[] { new RegisterWorkload() },
            FaultSchedule = new[]
            {
                new FaultScheduleEntry
                {
                    Injector = new NetworkPartitionFault { Mode = PartitionMode.MajorityMinority },
                    Target = new FaultTarget
                    {
                        NodeIds = new[] { "n4", "n5" },
                        Duration = TimeSpan.FromSeconds(30)
                    },
                    StartAfter = TimeSpan.FromSeconds(10),
                    Duration = TimeSpan.FromSeconds(30)
                }
            },
            ExpectedConsistency = ConsistencyModel.Linearizable,
            ClusterConfig = new JepsenClusterConfig { NodeCount = 5 },
            Tags = new[] { "linearizability", "partition", "majority-minority" }
        };
    }

    /// <summary>
    /// Tests Raft leader election correctness under leader process kill.
    ///
    /// Setup: 5-node cluster with RegisterWorkload and 5 concurrent clients.
    /// Fault: ProcessKillFault targeting the current leader node (n1) at T+15s,
    /// partition healing at T+30s (process restart).
    /// Duration: 60s total.
    /// Expected: ConsistencyModel.Linearizable.
    ///
    /// Passing means: A new leader is elected within the Raft election timeout after n1 is killed.
    /// No split-brain occurs (only one leader exists at any time). When n1 is restarted, it
    /// steps down to follower and replicates from the new leader. No committed writes are lost.
    /// </summary>
    /// <returns>A test plan for Raft leader election correctness.</returns>
    public static JepsenTestPlan RaftLeaderElectionUnderPartition()
    {
        return new JepsenTestPlan
        {
            TestName = "raft-leader-election-under-partition",
            Duration = TimeSpan.FromSeconds(60),
            ConcurrentClients = 5,
            Workloads = new IWorkloadGenerator[] { new RegisterWorkload() },
            FaultSchedule = new[]
            {
                new FaultScheduleEntry
                {
                    Injector = new ProcessKillFault(),
                    Target = new FaultTarget
                    {
                        NodeIds = new[] { "n1" },
                        Duration = TimeSpan.FromSeconds(15)
                    },
                    StartAfter = TimeSpan.FromSeconds(15),
                    Duration = TimeSpan.FromSeconds(15)
                }
            },
            ExpectedConsistency = ConsistencyModel.Linearizable,
            ClusterConfig = new JepsenClusterConfig { NodeCount = 5 },
            Tags = new[] { "raft", "leader-election", "process-kill", "partition" }
        };
    }

    /// <summary>
    /// Tests CRDT convergence under clock skew conditions.
    ///
    /// Setup: 5-node cluster with SetWorkload and 10 concurrent clients.
    /// Fault: ClockSkewFault (500ms-2s skew on nodes n3, n4) at T+5s for 40s.
    /// Duration: 60s total.
    /// Expected: ConsistencyModel.CausalConsistency.
    ///
    /// Passing means: Despite clock skew on 2 of 5 nodes, all CRDT sets converge to the
    /// identical state after clock skew is healed and nodes re-synchronize. No set elements
    /// are lost or duplicated due to clock differences.
    /// </summary>
    /// <returns>A test plan for CRDT convergence under clock skew.</returns>
    public static JepsenTestPlan CrdtConvergenceUnderDelay()
    {
        return new JepsenTestPlan
        {
            TestName = "crdt-convergence-under-delay",
            Duration = TimeSpan.FromSeconds(60),
            ConcurrentClients = 10,
            Workloads = new IWorkloadGenerator[] { new SetWorkload() },
            FaultSchedule = new[]
            {
                new FaultScheduleEntry
                {
                    Injector = new ClockSkewFault
                    {
                        MinSkew = TimeSpan.FromMilliseconds(500),
                        MaxSkew = TimeSpan.FromSeconds(2)
                    },
                    Target = new FaultTarget
                    {
                        NodeIds = new[] { "n3", "n4" },
                        Duration = TimeSpan.FromSeconds(40)
                    },
                    StartAfter = TimeSpan.FromSeconds(5),
                    Duration = TimeSpan.FromSeconds(40)
                }
            },
            ExpectedConsistency = ConsistencyModel.CausalConsistency,
            ClusterConfig = new JepsenClusterConfig { NodeCount = 5 },
            Tags = new[] { "crdt", "clock-skew", "convergence", "eventual-consistency" }
        };
    }

    /// <summary>
    /// Tests WAL crash recovery after a kill-9 during active writes.
    ///
    /// Setup: 3-node cluster with ListAppendWorkload and 5 concurrent clients.
    /// Fault: ProcessKillFault (kill-9 on node n2 during active writes) at T+10s,
    /// process restart at T+20s.
    /// Duration: 45s total.
    /// Expected: ConsistencyModel.Serializable.
    ///
    /// Passing means: After n2 is killed and restarted, WAL replay recovers all committed
    /// operations. No data loss occurs for operations that were acknowledged to clients.
    /// The append list on all nodes is consistent after recovery.
    /// </summary>
    /// <returns>A test plan for WAL crash recovery.</returns>
    public static JepsenTestPlan WalCrashRecovery()
    {
        return new JepsenTestPlan
        {
            TestName = "wal-crash-recovery",
            Duration = TimeSpan.FromSeconds(45),
            ConcurrentClients = 5,
            Workloads = new IWorkloadGenerator[] { new ListAppendWorkload() },
            FaultSchedule = new[]
            {
                new FaultScheduleEntry
                {
                    Injector = new ProcessKillFault(),
                    Target = new FaultTarget
                    {
                        NodeIds = new[] { "n2" },
                        Duration = TimeSpan.FromSeconds(10)
                    },
                    StartAfter = TimeSpan.FromSeconds(10),
                    Duration = TimeSpan.FromSeconds(10)
                }
            },
            ExpectedConsistency = ConsistencyModel.Serializable,
            ClusterConfig = new JepsenClusterConfig { NodeCount = 3 },
            Tags = new[] { "wal", "crash-recovery", "durability", "process-kill" }
        };
    }

    /// <summary>
    /// Tests MVCC snapshot isolation under high concurrency with no faults.
    ///
    /// Setup: 3-node cluster with BankWorkload (balance transfer) and 20 concurrent clients.
    /// Fault: None — this scenario tests isolation under pure concurrency pressure.
    /// Duration: 30s total.
    /// Expected: ConsistencyModel.SnapshotIsolation.
    ///
    /// Passing means: No write skew anomalies occur. The total balance across all accounts
    /// is conserved at every point (no money created or destroyed). No dirty reads are observed.
    /// All transactions see a consistent snapshot of the database.
    /// </summary>
    /// <returns>A test plan for MVCC snapshot isolation.</returns>
    public static JepsenTestPlan MvccSnapshotIsolation()
    {
        return new JepsenTestPlan
        {
            TestName = "mvcc-snapshot-isolation",
            Duration = TimeSpan.FromSeconds(30),
            ConcurrentClients = 20,
            Workloads = new IWorkloadGenerator[] { new BankWorkload() },
            FaultSchedule = Array.Empty<FaultScheduleEntry>(),
            ExpectedConsistency = ConsistencyModel.SnapshotIsolation,
            ClusterConfig = new JepsenClusterConfig { NodeCount = 3 },
            Tags = new[] { "mvcc", "snapshot-isolation", "concurrency", "bank-test" }
        };
    }

    /// <summary>
    /// Tests DVV (Dotted Version Vector) replication consistency under a half-and-half partition.
    ///
    /// Setup: 5-node cluster with RegisterWorkload using CAS operations and 10 concurrent clients.
    /// Fault: NetworkPartitionFault (HalfAndHalf) at T+10s for 20s.
    /// Duration: 60s total.
    /// Expected: ConsistencyModel.CausalConsistency.
    ///
    /// Passing means: DVV correctly resolves conflicts that arise from writes on both sides
    /// of the partition. After partition heals, all nodes converge to a consistent state
    /// with no lost updates. CAS operations that succeed on one partition are correctly
    /// arbitrated against concurrent CAS operations on the other partition.
    /// </summary>
    /// <returns>A test plan for DVV replication consistency.</returns>
    public static JepsenTestPlan DvvReplicationConsistency()
    {
        return new JepsenTestPlan
        {
            TestName = "dvv-replication-consistency",
            Duration = TimeSpan.FromSeconds(60),
            ConcurrentClients = 10,
            Workloads = new IWorkloadGenerator[] { new RegisterWorkload() },
            FaultSchedule = new[]
            {
                new FaultScheduleEntry
                {
                    Injector = new NetworkPartitionFault { Mode = PartitionMode.HalfAndHalf },
                    Target = new FaultTarget
                    {
                        NodeIds = new[] { "n1", "n2" },
                        Duration = TimeSpan.FromSeconds(20)
                    },
                    StartAfter = TimeSpan.FromSeconds(10),
                    Duration = TimeSpan.FromSeconds(20)
                }
            },
            ExpectedConsistency = ConsistencyModel.CausalConsistency,
            ClusterConfig = new JepsenClusterConfig { NodeCount = 5 },
            Tags = new[] { "dvv", "replication", "cas", "partition", "conflict-resolution" }
        };
    }

    /// <summary>
    /// Tests split-brain prevention by isolating a minority from the majority.
    ///
    /// Setup: 5-node cluster with RegisterWorkload and 10 concurrent clients targeting
    /// both partitions.
    /// Fault: NetworkPartitionFault isolating nodes n4, n5 from the 3-node majority (n1, n2, n3)
    /// at T+5s for 45s.
    /// Duration: 60s total.
    /// Expected: ConsistencyModel.Linearizable.
    ///
    /// Passing means: The minority partition (n4, n5) rejects all write operations (fences itself)
    /// because it cannot form a quorum. The majority partition (n1, n2, n3) continues serving
    /// reads and writes normally. After partition heals, minority nodes rejoin without
    /// introducing conflicting state.
    /// </summary>
    /// <returns>A test plan for split-brain prevention.</returns>
    public static JepsenTestPlan SplitBrainPrevention()
    {
        return new JepsenTestPlan
        {
            TestName = "split-brain-prevention",
            Duration = TimeSpan.FromSeconds(60),
            ConcurrentClients = 10,
            Workloads = new IWorkloadGenerator[] { new RegisterWorkload() },
            FaultSchedule = new[]
            {
                new FaultScheduleEntry
                {
                    Injector = new NetworkPartitionFault { Mode = PartitionMode.MajorityMinority },
                    Target = new FaultTarget
                    {
                        NodeIds = new[] { "n4", "n5" },
                        Duration = TimeSpan.FromSeconds(45)
                    },
                    StartAfter = TimeSpan.FromSeconds(5),
                    Duration = TimeSpan.FromSeconds(45)
                }
            },
            ExpectedConsistency = ConsistencyModel.Linearizable,
            ClusterConfig = new JepsenClusterConfig { NodeCount = 5 },
            Tags = new[] { "split-brain", "quorum", "fencing", "partition", "linearizability" }
        };
    }

    /// <summary>
    /// Returns all 7 Jepsen test scenarios as a single collection.
    /// Useful for running the complete correctness verification suite.
    /// </summary>
    /// <returns>All 7 predefined Jepsen test plans.</returns>
    public static IReadOnlyList<JepsenTestPlan> GetAllScenarios()
    {
        return new[]
        {
            LinearizabilityUnderPartition(),
            RaftLeaderElectionUnderPartition(),
            CrdtConvergenceUnderDelay(),
            WalCrashRecovery(),
            MvccSnapshotIsolation(),
            DvvReplicationConsistency(),
            SplitBrainPrevention()
        };
    }
}
