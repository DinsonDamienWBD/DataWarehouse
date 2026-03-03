---
phase: 89-ecosystem-compatibility
plan: 09
subsystem: Jepsen Distributed Correctness Test Harness
tags: [jepsen, distributed-testing, consistency, fault-injection, linearizability, docker]
dependency_graph:
  requires: ["89-05"]
  provides: ["JepsenTestHarness", "IFaultInjector", "IWorkloadGenerator", "ConsistencyModel"]
  affects: ["DataWarehouse.SDK"]
tech_stack:
  added: ["Docker CLI orchestration", "iptables fault injection", "Knossos-style linearizability checker"]
  patterns: ["IAsyncEnumerable workload streams", "5-phase test execution", "Elle-compatible JSON output"]
key_files:
  created:
    - DataWarehouse.SDK/Contracts/Ecosystem/JepsenTestHarness.cs
    - DataWarehouse.SDK/Contracts/Ecosystem/JepsenFaultInjection.cs
    - DataWarehouse.SDK/Contracts/Ecosystem/JepsenWorkloadGenerators.cs
decisions:
  - "Process.Start for Docker CLI invocation (not Docker API library) for zero-dependency SDK"
  - "Knossos-style linearizability check using real-time ordering constraints on per-key operation graphs"
  - "IAsyncEnumerable for workload generators to support streaming operation histories with backpressure"
  - "Disk corruption healing deferred to DW self-healing (Merkle tree + Raft replication)"
metrics:
  duration: 6min
  completed: 2026-02-24T00:07:00Z
  tasks_completed: 2
  tasks_total: 2
  files_created: 3
  files_modified: 0
---

# Phase 89 Plan 09: Jepsen Distributed Correctness Test Harness Summary

Jepsen-style test harness for proving distributed correctness via Docker multi-node deployment, concurrent workloads with fault injection, and Elle-style consistency checking across linearizability, serializable, snapshot isolation, and causal consistency models.

## Completed Tasks

### Task 1: Jepsen Test Harness with Docker Deployment
- **Commit:** 19bc884c
- **File:** `DataWarehouse.SDK/Contracts/Ecosystem/JepsenTestHarness.cs`
- `JepsenTestHarness` with `DeployClusterAsync` (Docker network + N containers + health checks + Raft membership) and `TeardownClusterAsync`
- `RunTestAsync` executes 5-phase test: Setup -> Nemesis (fault schedule) -> Workload (concurrent clients) -> Heal (reconvergence) -> Verify (consistency check)
- `JepsenCluster` record with `NodeCount`, `Nodes`, `NetworkName`, `DockerImage`, `StartupTimeout`, `State` enum
- `JepsenNode` record with `NodeId`, `ContainerName`, `IpAddress`, 3 ports, `State` enum (Running/Killed/Partitioned/ClockSkewed)
- `JepsenTestPlan` with `TestName`, `Duration`, `ConcurrentClients`, `Workloads`, `FaultSchedule`, `ExpectedConsistency`
- `JepsenTestResult` with `Passed`, `TotalOperations`, `Violations`, `FullHistory`, `ElleAnalysisJson`
- `OperationHistoryEntry` with `SequenceId`, `NodeId`, `ClientId`, `Type` (Read/Write/Cas/Append), `Key`, `Value`, `Result` (Ok/Fail/Timeout/Crashed)
- `CheckLinearizability` — Knossos-style: groups by key, tracks write log, verifies reads return values from concurrent or preceding writes using real-time ordering
- `CheckSnapshotIsolation` — detects write skew by finding concurrent reads/writes across clients on overlapping keys
- `CheckSerializability` — verifies reads return previously written values via per-key version chains
- `CheckCausalConsistency` — tracks per-client causal dependencies (simplified without vector clocks)

### Task 2: Fault Injectors and Workload Generators
- **Commit:** 220cee69
- **Files:** `DataWarehouse.SDK/Contracts/Ecosystem/JepsenFaultInjection.cs`, `DataWarehouse.SDK/Contracts/Ecosystem/JepsenWorkloadGenerators.cs`
- `IFaultInjector` interface with `FaultType`, `InjectAsync`, `HealAsync`
- `NetworkPartitionFault` — iptables INPUT/OUTPUT DROP rules; 3 modes: `MajorityMinority`, `HalfAndHalf`, `Isolate`
- `ProcessKillFault` — `pkill -9` injection, `/start.sh` restart healing
- `ClockSkewFault` — `date -s` with configurable 500ms-30s range, NTP/hwclock healing
- `DiskCorruptionFault` — `dd if=/dev/urandom` at random offset past headers, self-healing via DW mechanisms
- `IWorkloadGenerator` with `IAsyncEnumerable<OperationHistoryEntry> RunAsync`
- `RegisterWorkload` — read/write/CAS with configurable probabilities, per-key value tracking for CAS
- `SetWorkload` — add/read operations for serializable isolation testing
- `ListAppendWorkload` — append/read operations for snapshot isolation and lost-update detection
- `BankWorkload` — transfer/balance operations with conservation invariant verification
- `WorkloadMixer` — weighted distribution combining multiple workloads across clients

## Deviations from Plan

None - plan executed exactly as written.

## Verification

- `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj --no-restore` — 0 errors, 0 warnings
- All 4 fault injectors implement `IFaultInjector` with inject/heal pairs
- All 4 workload generators implement `IWorkloadGenerator` with `IAsyncEnumerable` streams
- `CheckLinearizability` verifies real-time ordering constraints per key
- `CheckSnapshotIsolation` detects write skew anomalies across concurrent clients
- Elle JSON output generated for all consistency checks
