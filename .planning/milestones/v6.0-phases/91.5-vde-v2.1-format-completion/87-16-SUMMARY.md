---
phase: 91.5-vde-v2.1-format-completion
plan: 87-16
subsystem: VirtualDiskEngine/Journal
tags: [wal, sharding, lock-free, thread-affinity, crash-recovery, vopt-29]
dependency_graph:
  requires: ["87-65"]
  provides: ["ShardedWriteAheadLog", "WalShard", "ShardCommitBarrier"]
  affects: ["IWriteAheadLog consumers", "VdeMountPipeline WAL selection"]
tech_stack:
  added:
    - "WalShard.cs — lock-free CAS ring buffer, per-shard monotonic LSN, flush to independent disk region"
    - "ShardCommitBarrier.cs — Shard 0 barrier with packed [ShardId:2][Lsn:6] AfterImage encoding"
    - "ShardedWriteAheadLog.cs — IWriteAheadLog + IAsyncDisposable, N-shard thread-affinity routing"
  patterns:
    - "ConcurrentDictionary thread-ring indexing (avoids S2696 ThreadStatic write in instance method)"
    - "Interlocked.CompareExchange CAS loop for lock-free ring-buffer head advancement"
    - "Task.WhenAll parallel shard flush with serial Shard 0 barrier commit"
key_files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/Journal/WalShard.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Journal/ShardCommitBarrier.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Journal/ShardedWriteAheadLog.cs
  modified:
    - DataWarehouse.SDK/VirtualDiskEngine/Integrity/MerkleBatchConfig.cs
decisions:
  - "ConcurrentDictionary<int,int> for thread->shard mapping instead of [ThreadStatic] to comply with Sonar S2696 (consistent with Phase 86-12 precedent)"
  - "Ring capacity 4096 entries as default (power-of-2); mask = capacity-1 for zero-division slot indexing"
  - "Shard 0 receives extra WAL blocks (walTotalBlocks % shardCount remainder) to hold more barrier entries"
  - "Barrier AfterImage packs N entries of [ShardId:2][Lsn:6] for 8 bytes per shard in 48-bit LSN range"
  - "WalSizeBlocks = walTotalBlocks field (total region); WalUtilization = sum(pendingEntries)/(shards*4096)"
metrics:
  duration: "6min"
  completed: "2026-03-02T12:41:26Z"
  tasks: 2
  files_created: 3
  files_modified: 1
---

# Phase 91.5 Plan 87-16: Sharded Write-Ahead Log (VOPT-29) Summary

Thread-affinity sharded WAL with N lock-free per-core ring buffers, parallel shard flush, and Shard 0 commit barrier referencing all shard LSNs for atomic multi-shard crash recovery.

## Objective

Eliminate single-threaded WAL contention by sharding the write-ahead log across N CPU cores (up to 32). Each thread is pinned to exactly one shard on first access, removing all cross-thread lock contention. Shard 0 acts as the commit barrier, recording a packed LSN snapshot from all shards at each flush point.

## Tasks Completed

| Task | Name | Commit | Files |
|------|------|--------|-------|
| 1 | WalShard and ShardCommitBarrier | `bbabb442` | WalShard.cs, ShardCommitBarrier.cs, MerkleBatchConfig.cs |
| 2 | ShardedWriteAheadLog | `d8deccac` | ShardedWriteAheadLog.cs |

## Implementation Details

### WalShard (bbabb442)

- Lock-free ring buffer with power-of-2 capacity (default 4096), index via `head & mask`
- `Append(JournalEntry)`: CAS loop on `_head` to claim slot atomically; assigns per-shard LSN via `Interlocked.Increment`
- `FlushAsync`: iterates tail..head, serialises each entry to shard's disk region (wraps on circular overflow), flushes device
- `ReplayAsync`: scans all shard blocks, calls `JournalEntry.Deserialize` for each valid entry
- `TruncateToLsn(long)`: internal method used by `ShardCommitBarrier.ReconcileShardsAsync` during recovery

### ShardCommitBarrier (bbabb442)

- `CommitBarrierAsync(txId)`: collects `CurrentLsn` from all shards, packs as `[ShardId:2][Lsn:6]` AfterImage, appends `JournalEntryType.Checkpoint` to Shard 0, flushes Shard 0
- `FindLastBarrierAsync()`: replays Shard 0, finds last Checkpoint entry with valid AfterImage, returns `BarrierRecord`
- `ReconcileShardsAsync(barrier, shards)`: calls `TruncateToLsn` on each shard to discard entries past barrier
- `BarrierRecord` readonly struct: `BarrierLsn`, `ShardLsns[]`, `TransactionId`

### ShardedWriteAheadLog (d8deccac)

- Constructor auto-detects `shardCount = Math.Min(ProcessorCount, 32)` when 0 is passed
- Divides `walTotalBlocks` evenly; Shard 0 gets remainder blocks for barrier overhead
- Thread affinity via `ConcurrentDictionary<int, int>` keyed by `ManagedThreadId` (avoids Sonar S2696)
- `AppendEntryAsync`: routes to `_shards[GetShardForCurrentThread()].Append` — truly lock-free
- `FlushAsync`: `Task.WhenAll` all shards then `CommitBarrierAsync` — barrier is the linearisation point
- `ReplayAsync`: find barrier → reconcile → parallel replay → merge by LSN → filter to barrier max
- `CheckpointAsync`: flush all + write barrier
- `GetShardStats()`: `WalShardStats[]` with ShardId/CurrentLsn/PendingEntries for monitoring

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Pre-existing XML cref error in MerkleBatchConfig.cs**
- **Found during:** Task 1 build
- **Issue:** `<see cref="EpochBatchedMerkleUpdater.RecoverFromWalAsync"/>` triggered CS1574 because the method is in a different class — the cross-class cref was unresolved
- **Fix:** Changed to `<c>EpochBatchedMerkleUpdater.RecoverFromWalAsync</c>` (plain code span)
- **Files modified:** DataWarehouse.SDK/VirtualDiskEngine/Integrity/MerkleBatchConfig.cs
- **Commit:** bbabb442

**2. [Rule 1 - Bug] Sonar S2696 on [ThreadStatic] write in instance method**
- **Found during:** Task 2 build
- **Issue:** `_assignedShard = assigned` in instance method `GetShardForCurrentThread()` triggered error S2696
- **Fix:** Replaced `[ThreadStatic] private static int? _assignedShard` with `ConcurrentDictionary<int, int> _threadShardMap` keyed by `ManagedThreadId` — consistent with Phase 86-12 pattern
- **Files modified:** ShardedWriteAheadLog.cs
- **Commit:** d8deccac

**3. [Rule 1 - Bug] WalSizeBlocks always returned 0**
- **Found during:** Task 2 review
- **Issue:** The `WalSizeBlocks` property had a dead loop over shards but always returned `total = 0`
- **Fix:** Added `_walTotalBlocks` field stored in constructor; property returns it directly
- **Files modified:** ShardedWriteAheadLog.cs
- **Commit:** d8deccac

## Verification

```
dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj --no-restore
Build succeeded. 0 Warning(s). 0 Error(s).
```

- ShardedWriteAheadLog implements IWriteAheadLog (drop-in replacement verified by interface compliance)
- Thread affinity: each thread gets exactly one shard via ConcurrentDictionary.GetOrAdd (idempotent)
- Barrier on Shard 0 references LSNs from all shards
- Multi-shard recovery: FindLastBarrier → ReconcileShards → parallel replay → merge by LSN

## Self-Check: PASSED
