// Phase 89: Ecosystem Compatibility - Jepsen Test Harness (ECOS-16)
// Docker-based multi-node deployment and test orchestration with consistency checking

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Ecosystem;

#region Enums

/// <summary>
/// Cluster lifecycle state.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Jepsen test harness (ECOS-16)")]
public enum JepsenClusterState
{
    /// <summary>Cluster has not been started.</summary>
    NotStarted = 0,
    /// <summary>Cluster is starting up containers.</summary>
    Starting = 1,
    /// <summary>Cluster is running and healthy.</summary>
    Running = 2,
    /// <summary>Cluster encountered a fault during startup.</summary>
    Faulted = 3,
    /// <summary>Cluster has been stopped.</summary>
    Stopped = 4
}

/// <summary>
/// Individual node state within a Jepsen cluster.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Jepsen test harness (ECOS-16)")]
public enum JepsenNodeState
{
    /// <summary>Node is running normally.</summary>
    Running = 0,
    /// <summary>Node process has been killed.</summary>
    Killed = 1,
    /// <summary>Node is network-partitioned from some or all peers.</summary>
    Partitioned = 2,
    /// <summary>Node has clock skew applied.</summary>
    ClockSkewed = 3
}

/// <summary>
/// Type of operation recorded in the history.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Jepsen test harness (ECOS-16)")]
public enum OperationType
{
    /// <summary>Read a value.</summary>
    Read = 0,
    /// <summary>Write a value.</summary>
    Write = 1,
    /// <summary>Compare-and-swap.</summary>
    Cas = 2,
    /// <summary>Append to a list or set.</summary>
    Append = 3
}

/// <summary>
/// Result of a single operation.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Jepsen test harness (ECOS-16)")]
public enum OperationResult
{
    /// <summary>Operation completed successfully.</summary>
    Ok = 0,
    /// <summary>Operation failed (definite).</summary>
    Fail = 1,
    /// <summary>Operation timed out (indeterminate).</summary>
    Timeout = 2,
    /// <summary>Client or node crashed during operation.</summary>
    Crashed = 3
}

/// <summary>
/// Consistency model to verify against.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Jepsen test harness (ECOS-16)")]
public enum ConsistencyModel
{
    /// <summary>Linearizable — strongest single-object consistency.</summary>
    Linearizable = 0,
    /// <summary>Serializable — all transactions appear in some serial order.</summary>
    Serializable = 1,
    /// <summary>Snapshot isolation — no write skew within a transaction.</summary>
    SnapshotIsolation = 2,
    /// <summary>Causal consistency — causally related operations ordered.</summary>
    CausalConsistency = 3
}

#endregion

#region Records

/// <summary>
/// Represents a single node in the Jepsen cluster.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Jepsen test harness (ECOS-16)")]
public sealed record JepsenNode
{
    /// <summary>Unique node identifier (e.g., "n1", "n2").</summary>
    public required string NodeId { get; init; }

    /// <summary>Docker container name.</summary>
    public required string ContainerName { get; init; }

    /// <summary>IP address within the Docker network.</summary>
    public required string IpAddress { get; init; }

    /// <summary>PostgreSQL wire protocol port.</summary>
    public required int PostgresPort { get; init; }

    /// <summary>Raft consensus port.</summary>
    public required int RaftPort { get; init; }

    /// <summary>gRPC service port.</summary>
    public required int GrpcPort { get; init; }

    /// <summary>Current node state.</summary>
    public JepsenNodeState State { get; init; } = JepsenNodeState.Running;
}

/// <summary>
/// Represents a deployed Jepsen cluster of DataWarehouse nodes.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Jepsen test harness (ECOS-16)")]
public sealed record JepsenCluster
{
    /// <summary>Number of nodes in the cluster.</summary>
    public int NodeCount { get; init; } = 5;

    /// <summary>All nodes in the cluster.</summary>
    public required IReadOnlyList<JepsenNode> Nodes { get; init; }

    /// <summary>Docker network name for inter-node communication.</summary>
    public required string NetworkName { get; init; }

    /// <summary>Docker image used for DW nodes.</summary>
    public required string DockerImage { get; init; }

    /// <summary>Maximum time to wait for each node to become healthy.</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Current cluster state.</summary>
    public JepsenClusterState State { get; init; } = JepsenClusterState.NotStarted;
}

/// <summary>
/// Configuration for deploying a Jepsen cluster.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Jepsen test harness (ECOS-16)")]
public sealed record JepsenClusterConfig
{
    /// <summary>Number of nodes to deploy (default 5).</summary>
    public int NodeCount { get; init; } = 5;

    /// <summary>Docker image tag for DW nodes.</summary>
    public string DockerImage { get; init; } = "datawarehouse:latest";

    /// <summary>Docker network name (auto-generated if null).</summary>
    public string? NetworkName { get; init; }

    /// <summary>Base port for PostgreSQL (each node increments by 1).</summary>
    public int BasePostgresPort { get; init; } = 15432;

    /// <summary>Base port for Raft consensus.</summary>
    public int BaseRaftPort { get; init; } = 18000;

    /// <summary>Base port for gRPC services.</summary>
    public int BaseGrpcPort { get; init; } = 19000;

    /// <summary>Per-node startup timeout.</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Additional Docker environment variables per node.</summary>
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }
}

/// <summary>
/// A single operation recorded in the test history.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Jepsen test harness (ECOS-16)")]
public sealed record OperationHistoryEntry
{
    /// <summary>Monotonically increasing sequence ID.</summary>
    public required long SequenceId { get; init; }

    /// <summary>Node the operation was sent to.</summary>
    public required string NodeId { get; init; }

    /// <summary>Client that issued the operation.</summary>
    public required string ClientId { get; init; }

    /// <summary>Type of operation (read, write, CAS, append).</summary>
    public required OperationType Type { get; init; }

    /// <summary>Key operated on.</summary>
    public required string Key { get; init; }

    /// <summary>Value written or read (null for failed reads).</summary>
    public string? Value { get; init; }

    /// <summary>Expected value for CAS operations.</summary>
    public string? ExpectedValue { get; init; }

    /// <summary>When the client sent the request.</summary>
    public required DateTimeOffset StartTime { get; init; }

    /// <summary>When the client received the response.</summary>
    public required DateTimeOffset EndTime { get; init; }

    /// <summary>Outcome of the operation.</summary>
    public required OperationResult Result { get; init; }
}

/// <summary>
/// Describes a violation of a consistency model.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Jepsen test harness (ECOS-16)")]
public sealed record ConsistencyViolation
{
    /// <summary>Human-readable description of the violation.</summary>
    public required string Description { get; init; }

    /// <summary>The operation that exhibited the violation.</summary>
    public required OperationHistoryEntry Operation { get; init; }

    /// <summary>What behavior was expected under the consistency model.</summary>
    public required string ExpectedBehavior { get; init; }

    /// <summary>What behavior was actually observed.</summary>
    public required string ActualBehavior { get; init; }
}

/// <summary>
/// Result of a Jepsen test run.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Jepsen test harness (ECOS-16)")]
public sealed record JepsenTestResult
{
    /// <summary>Name of the test.</summary>
    public required string TestName { get; init; }

    /// <summary>Whether the test passed (no consistency violations).</summary>
    public required bool Passed { get; init; }

    /// <summary>Consistency model that was tested.</summary>
    public required ConsistencyModel TestedModel { get; init; }

    /// <summary>Total number of operations executed.</summary>
    public required long TotalOperations { get; init; }

    /// <summary>Operations that completed successfully.</summary>
    public required long SuccessfulOperations { get; init; }

    /// <summary>Operations that failed or timed out.</summary>
    public required long FailedOperations { get; init; }

    /// <summary>Any consistency violations found.</summary>
    public required IReadOnlyList<ConsistencyViolation> Violations { get; init; }

    /// <summary>Complete operation history for analysis.</summary>
    public required IReadOnlyList<OperationHistoryEntry> FullHistory { get; init; }

    /// <summary>Total test duration.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>When the test started.</summary>
    public required DateTimeOffset StartTime { get; init; }

    /// <summary>Raw Elle checker output JSON (if available).</summary>
    public string? ElleAnalysisJson { get; init; }
}

/// <summary>
/// Defines a complete Jepsen test plan.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Jepsen test harness (ECOS-16)")]
public sealed record JepsenTestPlan
{
    /// <summary>Name of the test.</summary>
    public required string TestName { get; init; }

    /// <summary>How long to run the workload phase.</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Number of concurrent client connections.</summary>
    public int ConcurrentClients { get; init; } = 10;

    /// <summary>Workload generators to run concurrently.</summary>
    public required IReadOnlyList<IWorkloadGenerator> Workloads { get; init; }

    /// <summary>Fault injection schedule.</summary>
    public required IReadOnlyList<FaultScheduleEntry> FaultSchedule { get; init; }

    /// <summary>Expected consistency model to verify.</summary>
    public ConsistencyModel ExpectedConsistency { get; init; } = ConsistencyModel.Linearizable;

    /// <summary>Cluster configuration for this test plan.</summary>
    public JepsenClusterConfig? ClusterConfig { get; init; }

    /// <summary>Tags for categorization (e.g., "raft", "partition", "recovery", "isolation").</summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
}

#endregion

#region Test Harness

/// <summary>
/// Jepsen-style distributed correctness test harness.
/// Deploys multi-node DW clusters in Docker, runs concurrent workloads with fault injection,
/// and verifies consistency properties using Elle-style checkers.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Jepsen test harness (ECOS-16)")]
public sealed class JepsenTestHarness
{
    private static long _sequenceCounter;

    /// <summary>
    /// Generates the next sequence ID for operation history entries.
    /// </summary>
    internal static long NextSequenceId() => Interlocked.Increment(ref _sequenceCounter);

    #region Cluster Deployment

    /// <summary>
    /// Deploys a Jepsen cluster of N DataWarehouse nodes in Docker containers.
    /// Creates a Docker network, launches containers, waits for health checks,
    /// and configures Raft cluster membership.
    /// </summary>
    /// <param name="config">Cluster deployment configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The deployed cluster.</returns>
    public async Task<JepsenCluster> DeployClusterAsync(JepsenClusterConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.NodeCount < 1)
            throw new ArgumentOutOfRangeException(nameof(config), "NodeCount must be at least 1.");

        var networkName = config.NetworkName ?? $"jepsen-{Guid.NewGuid():N}";

        // Create Docker network
        await RunDockerCommandAsync($"network create {networkName}", ct).ConfigureAwait(false);

        var nodes = new List<JepsenNode>(config.NodeCount);

        try
        {
            // Launch N containers
            for (int i = 0; i < config.NodeCount; i++)
            {
                ct.ThrowIfCancellationRequested();

                var nodeId = $"n{i + 1}";
                var containerName = $"jepsen-{networkName}-{nodeId}";
                var pgPort = config.BasePostgresPort + i;
                var raftPort = config.BaseRaftPort + i;
                var grpcPort = config.BaseGrpcPort + i;

                var envArgs = BuildEnvironmentArgs(config.EnvironmentVariables, nodeId, config.NodeCount);

                var dockerRunArgs = $"run -d --name {containerName} --network {networkName} " +
                    $"-p {pgPort}:5432 -p {raftPort}:8000 -p {grpcPort}:9000 " +
                    $"{envArgs} {config.DockerImage}";

                await RunDockerCommandAsync(dockerRunArgs, ct).ConfigureAwait(false);

                // Get container IP
                var ipAddress = await GetContainerIpAsync(containerName, networkName, ct).ConfigureAwait(false);

                nodes.Add(new JepsenNode
                {
                    NodeId = nodeId,
                    ContainerName = containerName,
                    IpAddress = ipAddress,
                    PostgresPort = pgPort,
                    RaftPort = raftPort,
                    GrpcPort = grpcPort,
                    State = JepsenNodeState.Running
                });
            }

            // Wait for health checks on all nodes
            await WaitForClusterHealthAsync(nodes, config.StartupTimeout, ct).ConfigureAwait(false);

            // Configure Raft cluster membership
            await ConfigureRaftMembershipAsync(nodes, ct).ConfigureAwait(false);

            return new JepsenCluster
            {
                NodeCount = config.NodeCount,
                Nodes = nodes.AsReadOnly(),
                NetworkName = networkName,
                DockerImage = config.DockerImage,
                StartupTimeout = config.StartupTimeout,
                State = JepsenClusterState.Running
            };
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Cleanup on failure
            foreach (var node in nodes)
            {
                try
                {
                    await RunDockerCommandAsync($"rm -f {node.ContainerName}", CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Best effort cleanup
                }
            }

            try
            {
                await RunDockerCommandAsync($"network rm {networkName}", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Best effort cleanup
            }

            throw;
        }
    }

    /// <summary>
    /// Tears down a Jepsen cluster: stops and removes all containers, removes Docker network.
    /// </summary>
    /// <param name="cluster">The cluster to tear down.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task TeardownClusterAsync(JepsenCluster cluster, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cluster);

        // Stop and remove all containers
        foreach (var node in cluster.Nodes)
        {
            try
            {
                await RunDockerCommandAsync($"rm -f {node.ContainerName}", ct).ConfigureAwait(false);
            }
            catch
            {
                // Best effort — container may already be removed
            }
        }

        // Remove the Docker network
        try
        {
            await RunDockerCommandAsync($"network rm {cluster.NetworkName}", ct).ConfigureAwait(false);
        }
        catch
        {
            // Best effort
        }
    }

    #endregion

    #region Test Execution

    /// <summary>
    /// Runs a complete Jepsen test against the cluster:
    /// Phase 1 (Setup) — Ensure cluster is healthy, create test tables/data.
    /// Phase 2 (Nemesis) — Start fault injection schedule.
    /// Phase 3 (Workload) — Run workload generators concurrently against random nodes.
    /// Phase 4 (Heal) — Remove all faults, wait for cluster reconvergence.
    /// Phase 5 (Verify) — Run consistency checker against operation history.
    /// </summary>
    /// <param name="cluster">The deployed cluster.</param>
    /// <param name="plan">Test plan defining workloads and faults.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Test results including consistency check.</returns>
    public async Task<JepsenTestResult> RunTestAsync(
        JepsenCluster cluster, JepsenTestPlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(plan);

        var startTime = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var allHistory = new ConcurrentBag<OperationHistoryEntry>();

        // Phase 1: Setup — verify cluster health and create test tables
        await VerifyClusterHealthAsync(cluster, ct).ConfigureAwait(false);
        await SetupTestDataAsync(cluster, plan, ct).ConfigureAwait(false);

        // Phase 2: Nemesis — start fault injection on schedule
        using var nemesisCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var nemesisTask = RunNemesisAsync(cluster, plan.FaultSchedule, nemesisCts.Token);

        // Phase 3: Workload — run concurrent workloads
        using var workloadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        workloadCts.CancelAfter(plan.Duration);

        var workloadTasks = new List<Task>();
        for (int clientId = 0; clientId < plan.ConcurrentClients; clientId++)
        {
            var workload = plan.Workloads[clientId % plan.Workloads.Count];
            var cid = clientId;
            workloadTasks.Add(Task.Run(async () =>
            {
                try
                {
                    await foreach (var entry in workload
                        .RunAsync(cluster, cid, workloadCts.Token)
                        .ConfigureAwait(false))
                    {
                        allHistory.Add(entry);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when workload duration expires
                }
            }, ct));
        }

        await Task.WhenAll(workloadTasks).ConfigureAwait(false);

        // Phase 4: Heal — stop nemesis, remove all faults, wait for reconvergence
        nemesisCts.Cancel();
        try { await nemesisTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* Expected */ }

        await HealAllFaultsAsync(cluster, plan.FaultSchedule, ct).ConfigureAwait(false);
        await WaitForReconvergenceAsync(cluster, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

        // Phase 5: Verify — check consistency
        sw.Stop();
        var sortedHistory = allHistory
            .OrderBy(e => e.SequenceId)
            .ToList()
            .AsReadOnly();

        var violations = plan.ExpectedConsistency switch
        {
            ConsistencyModel.Linearizable => CheckLinearizability(sortedHistory),
            ConsistencyModel.SnapshotIsolation => CheckSnapshotIsolation(sortedHistory),
            ConsistencyModel.Serializable => CheckSerializability(sortedHistory),
            ConsistencyModel.CausalConsistency => CheckCausalConsistency(sortedHistory),
            _ => Array.Empty<ConsistencyViolation>()
        };

        var successCount = sortedHistory.Count(e => e.Result == OperationResult.Ok);

        return new JepsenTestResult
        {
            TestName = plan.TestName,
            Passed = violations.Count == 0,
            TestedModel = plan.ExpectedConsistency,
            TotalOperations = sortedHistory.Count,
            SuccessfulOperations = successCount,
            FailedOperations = sortedHistory.Count - successCount,
            Violations = violations,
            FullHistory = sortedHistory,
            Duration = sw.Elapsed,
            StartTime = startTime,
            ElleAnalysisJson = GenerateElleJson(sortedHistory, violations)
        };
    }

    #endregion

    #region Consistency Checkers

    /// <summary>
    /// Verifies single-register linearizability using a Knossos-style algorithm.
    /// Builds a graph of operations and checks that a total order exists consistent
    /// with real-time ordering and read-write relationships.
    /// </summary>
    /// <param name="history">Ordered operation history.</param>
    /// <returns>List of violations (empty if linearizable).</returns>
    public static IReadOnlyList<ConsistencyViolation> CheckLinearizability(
        IReadOnlyList<OperationHistoryEntry> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var violations = new List<ConsistencyViolation>();

        // Group operations by key for single-register checks
        var byKey = history
            .Where(e => e.Result == OperationResult.Ok)
            .GroupBy(e => e.Key);

        foreach (var keyGroup in byKey)
        {
            var ops = keyGroup.OrderBy(e => e.StartTime).ToList();
            string? currentValue = null;

            // Track writes and verify reads see the most recent write
            // considering real-time ordering (non-overlapping operations)
            var writeLog = new List<(OperationHistoryEntry Op, string? Value)>();

            foreach (var op in ops)
            {
                if (op.Type == OperationType.Write)
                {
                    currentValue = op.Value;
                    writeLog.Add((op, op.Value));
                }
                else if (op.Type == OperationType.Read)
                {
                    // For linearizability, a read must return a value from
                    // a write that could have been the "latest" at read time.
                    // Find all writes that completed before this read started (must-precede).
                    var mustPrecedeWrites = writeLog
                        .Where(w => w.Op.EndTime <= op.StartTime)
                        .ToList();

                    // Find all writes that started before this read ended (could-overlap).
                    var possibleWrites = writeLog
                        .Where(w => w.Op.StartTime <= op.EndTime)
                        .ToList();

                    if (possibleWrites.Count > 0)
                    {
                        // Read must return a value from one of the possible writes
                        var possibleValues = possibleWrites
                            .Select(w => w.Value)
                            .ToHashSet();

                        // Null is valid if no writes preceded
                        if (writeLog.Count == 0 || mustPrecedeWrites.Count == 0)
                            possibleValues.Add(null);

                        if (!possibleValues.Contains(op.Value))
                        {
                            violations.Add(new ConsistencyViolation
                            {
                                Description = $"Linearizability violation on key '{keyGroup.Key}': " +
                                    $"read returned '{op.Value}' but no concurrent or preceding write produced that value.",
                                Operation = op,
                                ExpectedBehavior = $"Read should return one of: [{string.Join(", ", possibleValues.Select(v => v ?? "null"))}]",
                                ActualBehavior = $"Read returned '{op.Value}'"
                            });
                        }
                    }
                }
                else if (op.Type == OperationType.Cas)
                {
                    // CAS succeeds only if current value matches expected
                    if (op.ExpectedValue != currentValue)
                    {
                        // CAS should have failed — if it succeeded, that's a violation
                        violations.Add(new ConsistencyViolation
                        {
                            Description = $"Linearizability violation on key '{keyGroup.Key}': " +
                                $"CAS expected '{op.ExpectedValue}' but current was '{currentValue}', yet operation succeeded.",
                            Operation = op,
                            ExpectedBehavior = $"CAS should fail when expected '{op.ExpectedValue}' != current '{currentValue}'",
                            ActualBehavior = "CAS succeeded"
                        });
                    }
                    else
                    {
                        currentValue = op.Value;
                        writeLog.Add((op, op.Value));
                    }
                }
            }
        }

        return violations.AsReadOnly();
    }

    /// <summary>
    /// Verifies snapshot isolation: no write skew anomalies.
    /// Detects cases where two concurrent transactions read overlapping data
    /// and make disjoint writes that violate an invariant.
    /// </summary>
    /// <param name="history">Ordered operation history.</param>
    /// <returns>List of violations (empty if snapshot isolation holds).</returns>
    public static IReadOnlyList<ConsistencyViolation> CheckSnapshotIsolation(
        IReadOnlyList<OperationHistoryEntry> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var violations = new List<ConsistencyViolation>();

        // Group by client to reconstruct transaction-like sessions
        var byClient = history
            .Where(e => e.Result == OperationResult.Ok)
            .GroupBy(e => e.ClientId);

        // Build a global write timeline per key
        var writeTimeline = history
            .Where(e => e.Result == OperationResult.Ok &&
                        (e.Type == OperationType.Write || e.Type == OperationType.Append))
            .GroupBy(e => e.Key)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.StartTime).ToList());

        foreach (var clientOps in byClient)
        {
            var ops = clientOps.OrderBy(e => e.StartTime).ToList();

            for (int i = 0; i < ops.Count; i++)
            {
                var readOp = ops[i];
                if (readOp.Type != OperationType.Read) continue;

                // Check if a later write by another client happened between
                // this read's snapshot and the next operation by this client
                if (!writeTimeline.TryGetValue(readOp.Key, out var writes)) continue;

                var nextOpTime = i + 1 < ops.Count ? ops[i + 1].StartTime : DateTimeOffset.MaxValue;

                // Find writes by OTHER clients that committed between read and next op
                var interveningWrites = writes
                    .Where(w => w.ClientId != clientOps.Key &&
                               w.EndTime > readOp.EndTime &&
                               w.EndTime < nextOpTime)
                    .ToList();

                // Check if this client also wrote to a different key concurrently (write skew)
                var clientWrites = ops
                    .Where(o => o.StartTime >= readOp.StartTime &&
                               o.StartTime <= nextOpTime &&
                               (o.Type == OperationType.Write || o.Type == OperationType.Append) &&
                               o.Key != readOp.Key)
                    .ToList();

                if (interveningWrites.Count > 0 && clientWrites.Count > 0)
                {
                    violations.Add(new ConsistencyViolation
                    {
                        Description = $"Possible write skew on key '{readOp.Key}': " +
                            $"client '{clientOps.Key}' read stale data while writing to '{clientWrites[0].Key}'.",
                        Operation = readOp,
                        ExpectedBehavior = "Snapshot isolation prevents concurrent transactions from creating write skew",
                        ActualBehavior = $"Client read '{readOp.Value}' from '{readOp.Key}' while " +
                            $"another client wrote to same key, and this client wrote to '{clientWrites[0].Key}'"
                    });
                }
            }
        }

        return violations.AsReadOnly();
    }

    /// <summary>
    /// Verifies serializability: all transactions appear to execute in some serial order.
    /// </summary>
    /// <param name="history">Ordered operation history.</param>
    /// <returns>List of violations.</returns>
    public static IReadOnlyList<ConsistencyViolation> CheckSerializability(
        IReadOnlyList<OperationHistoryEntry> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var violations = new List<ConsistencyViolation>();

        // Build per-key version chains and verify reads see consistent snapshots
        var writeChains = new Dictionary<string, List<(string? Value, DateTimeOffset Time)>>();

        foreach (var op in history.Where(e => e.Result == OperationResult.Ok).OrderBy(e => e.SequenceId))
        {
            if (op.Type == OperationType.Write || op.Type == OperationType.Append)
            {
                if (!writeChains.TryGetValue(op.Key, out var chain))
                {
                    chain = new List<(string? Value, DateTimeOffset Time)>();
                    writeChains[op.Key] = chain;
                }
                chain.Add((op.Value, op.EndTime));
            }
            else if (op.Type == OperationType.Read)
            {
                if (!writeChains.TryGetValue(op.Key, out var chain) || chain.Count == 0)
                {
                    // Reading a key with no writes — value should be null
                    if (op.Value != null)
                    {
                        violations.Add(new ConsistencyViolation
                        {
                            Description = $"Serializability violation: read non-null value from un-written key '{op.Key}'.",
                            Operation = op,
                            ExpectedBehavior = "Read of un-written key should return null",
                            ActualBehavior = $"Read returned '{op.Value}'"
                        });
                    }
                    continue;
                }

                // Value should match some version in the chain
                var validValues = chain.Select(c => c.Value).ToHashSet();
                if (!validValues.Contains(op.Value))
                {
                    violations.Add(new ConsistencyViolation
                    {
                        Description = $"Serializability violation on key '{op.Key}': " +
                            $"read value '{op.Value}' never written.",
                        Operation = op,
                        ExpectedBehavior = $"Read should return a previously written value",
                        ActualBehavior = $"Read returned '{op.Value}' which was never written"
                    });
                }
            }
        }

        return violations.AsReadOnly();
    }

    /// <summary>
    /// Verifies causal consistency: causally related operations are ordered.
    /// </summary>
    /// <param name="history">Ordered operation history.</param>
    /// <returns>List of violations.</returns>
    public static IReadOnlyList<ConsistencyViolation> CheckCausalConsistency(
        IReadOnlyList<OperationHistoryEntry> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var violations = new List<ConsistencyViolation>();

        // Track per-client causal dependencies: if client A reads a value written by client B,
        // then client A's subsequent operations must see all of B's prior writes
        var lastWriteByClient = new Dictionary<string, Dictionary<string, string?>>();

        foreach (var op in history.Where(e => e.Result == OperationResult.Ok).OrderBy(e => e.SequenceId))
        {
            if (op.Type == OperationType.Write || op.Type == OperationType.Append)
            {
                if (!lastWriteByClient.TryGetValue(op.ClientId, out var clientWrites))
                {
                    clientWrites = new Dictionary<string, string?>();
                    lastWriteByClient[op.ClientId] = clientWrites;
                }
                clientWrites[op.Key] = op.Value;
            }
            else if (op.Type == OperationType.Read && op.Value != null)
            {
                // Find the writer of this value
                var writer = lastWriteByClient
                    .Where(kv => kv.Value.TryGetValue(op.Key, out var v) && v == op.Value)
                    .Select(kv => kv.Key)
                    .FirstOrDefault();

                if (writer != null && writer != op.ClientId)
                {
                    // This client now has a causal dependency on the writer's operations.
                    // Track that this client has observed the writer's state at this point.
                    if (lastWriteByClient.TryGetValue(writer, out var writerWrites))
                    {
                        // Merge writer's known writes into causal dependencies for this client.
                        if (!lastWriteByClient.TryGetValue(op.ClientId, out var clientWrites))
                        {
                            clientWrites = new Dictionary<string, string?>();
                            lastWriteByClient[op.ClientId] = clientWrites;
                        }

                        // For each key the writer has written, check if this client
                        // subsequently reads a stale (older) value for that key.
                        foreach (var writerKv in writerWrites)
                        {
                            // Check subsequent reads by this client for the same key
                            var laterReads = history
                                .Where(e => e.ClientId == op.ClientId
                                    && e.SequenceId > op.SequenceId
                                    && e.Type == OperationType.Read
                                    && e.Key == writerKv.Key
                                    && e.Result == OperationResult.Ok
                                    && e.Value != null
                                    && e.Value != writerKv.Value)
                                .Take(1);

                            foreach (var staleRead in laterReads)
                            {
                                violations.Add(new ConsistencyViolation
                                {
                                    Description = $"Causal violation: Client '{op.ClientId}' read stale value for key '{writerKv.Key}' " +
                                        $"(got '{staleRead.Value}', expected at least '{writerKv.Value}' from causal dependency on '{writer}')",
                                    Operation = op,
                                    ExpectedBehavior = $"Read should return value >= '{writerKv.Value}' written by causally-dependent client '{writer}'",
                                    ActualBehavior = $"Read returned stale value '{staleRead.Value}'"
                                });
                            }
                        }
                    }
                }
            }
        }

        return violations.AsReadOnly();
    }

    #endregion

    #region Internal Docker Operations

    private static async Task RunDockerCommandAsync(string arguments, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Docker command 'docker {arguments}' failed with exit code {process.ExitCode}: {stderr}");
        }
    }

    private static async Task<string> GetContainerIpAsync(
        string containerName, string networkName, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"inspect -f \"{{{{.NetworkSettings.Networks.{networkName}.IPAddress}}}}\" {containerName}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var ip = (await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false)).Trim();
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        return string.IsNullOrEmpty(ip) ? "127.0.0.1" : ip;
    }

    private static string BuildEnvironmentArgs(
        IReadOnlyDictionary<string, string>? envVars, string nodeId, int totalNodes)
    {
        var sb = new StringBuilder();
        sb.Append($"-e DW_NODE_ID={EscapeDockerEnvValue(nodeId)} -e DW_CLUSTER_SIZE={totalNodes}");

        if (envVars != null)
        {
            foreach (var kv in envVars)
            {
                // Validate env var key: must be alphanumeric + underscore (POSIX env var naming)
                if (string.IsNullOrWhiteSpace(kv.Key) ||
                    kv.Key.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
                {
                    throw new ArgumentException(
                        $"Invalid environment variable name: '{kv.Key}'. Only alphanumeric and underscore allowed.");
                }

                sb.Append($" -e {kv.Key}={EscapeDockerEnvValue(kv.Value)}");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Escapes a value for safe use in docker -e arguments by wrapping in single quotes
    /// and escaping embedded single quotes.
    /// </summary>
    private static string EscapeDockerEnvValue(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "''";
        // Replace single quotes with '\'' (end quote, escaped quote, start quote)
        return "'" + value.Replace("'", "'\\''") + "'";
    }

    private static async Task WaitForClusterHealthAsync(
        IReadOnlyList<JepsenNode> nodes, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        foreach (var node in nodes)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException($"Cluster health check timed out waiting for node {node.NodeId}.");

            using var healthCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            healthCts.CancelAfter(remaining);

            while (DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    await RunDockerCommandAsync(
                        $"exec {node.ContainerName} /bin/sh -c \"echo ok\"",
                        healthCts.Token).ConfigureAwait(false);
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), healthCts.Token).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task ConfigureRaftMembershipAsync(
        IReadOnlyList<JepsenNode> nodes, CancellationToken ct)
    {
        // Configure each node with the full cluster membership list
        var memberList = string.Join(",", nodes.Select(n => $"{n.IpAddress}:{n.RaftPort}"));

        foreach (var node in nodes)
        {
            await RunDockerCommandAsync(
                $"exec {node.ContainerName} /bin/sh -c \"echo '{memberList}' > /etc/dw/raft-members.conf\"",
                ct).ConfigureAwait(false);
        }
    }

    private static Task VerifyClusterHealthAsync(JepsenCluster cluster, CancellationToken ct)
    {
        // Verify all nodes are still reachable
        return WaitForClusterHealthAsync(
            cluster.Nodes, TimeSpan.FromSeconds(10), ct);
    }

    private static Task SetupTestDataAsync(
        JepsenCluster cluster, JepsenTestPlan plan, CancellationToken ct)
    {
        // Create test tables on the first node (will replicate via Raft)
        var leader = cluster.Nodes[0];
        return RunDockerCommandAsync(
            $"exec {leader.ContainerName} /bin/sh -c \"echo 'CREATE TABLE IF NOT EXISTS jepsen_test (key TEXT PRIMARY KEY, value TEXT);' | dw-sql\"",
            ct);
    }

    private static async Task RunNemesisAsync(
        JepsenCluster cluster, IReadOnlyList<FaultScheduleEntry> schedule, CancellationToken ct)
    {
        var startTime = DateTimeOffset.UtcNow;
        var activeFaults = new List<FaultScheduleEntry>();

        while (!ct.IsCancellationRequested)
        {
            var elapsed = DateTimeOffset.UtcNow - startTime;

            // Start faults that are due
            foreach (var entry in schedule)
            {
                if (elapsed >= entry.StartAfter && !activeFaults.Contains(entry))
                {
                    try
                    {
                        await entry.Injector.InjectAsync(cluster, entry.Target, ct).ConfigureAwait(false);
                        activeFaults.Add(entry);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch
                    {
                        // Fault injection failure is non-fatal to the test
                    }
                }
            }

            // Heal faults that have expired
            for (int i = activeFaults.Count - 1; i >= 0; i--)
            {
                var fault = activeFaults[i];
                if (elapsed >= fault.StartAfter + fault.Target.Duration)
                {
                    try
                    {
                        await fault.Injector.HealAsync(cluster, fault.Target, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { /* Best effort */ }
                    activeFaults.RemoveAt(i);
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
        }
    }

    private static async Task HealAllFaultsAsync(
        JepsenCluster cluster, IReadOnlyList<FaultScheduleEntry> schedule, CancellationToken ct)
    {
        foreach (var entry in schedule)
        {
            try
            {
                await entry.Injector.HealAsync(cluster, entry.Target, ct).ConfigureAwait(false);
            }
            catch
            {
                // Best effort healing
            }
        }
    }

    private static async Task WaitForReconvergenceAsync(
        JepsenCluster cluster, TimeSpan timeout, CancellationToken ct)
    {
        // Wait for all nodes to become healthy again
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                await WaitForClusterHealthAsync(
                    cluster.Nodes, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                return; // All healthy
            }
            catch (TimeoutException)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
        }
    }

    private static string? GenerateElleJson(
        IReadOnlyList<OperationHistoryEntry> history,
        IReadOnlyList<ConsistencyViolation> violations)
    {
        // Generate Elle-compatible JSON output
        var sb = new StringBuilder();
        sb.Append("{\"valid\":");
        sb.Append(violations.Count == 0 ? "true" : "false");
        sb.Append(",\"op-count\":");
        sb.Append(history.Count);
        sb.Append(",\"violation-count\":");
        sb.Append(violations.Count);
        sb.Append(",\"anomalies\":[");

        for (int i = 0; i < violations.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"type\":\"");
            sb.Append(violations[i].Description.Replace("\"", "\\\""));
            sb.Append("\"}");
        }

        sb.Append("]}");
        return sb.ToString();
    }

    #endregion
}

#endregion
