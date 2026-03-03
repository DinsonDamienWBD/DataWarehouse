---
phase: 91.5-vde-v2.1-format-completion
plan: 87-17
subsystem: vde-integrity
tags: [merkle, epoch-batched, write-hot-path, wal-recovery, concurrent-queue, sha256]

requires:
  - phase: 91.5-87-65
    provides: WalShard and ShardCommitBarrier for sharded WAL infrastructure

provides:
  - EpochBatchedMerkleUpdater: lock-free hot-path dirty queuing + background epoch batch processing
  - MerkleBatchConfig: configurable interval (5s), max batch (8192), WAL recovery, queue capacity (65536)
  - DirtyBlockEntry readonly struct: block number + 32-byte hash
  - MerkleBatchStats readonly struct: batch metrics snapshot

affects:
  - VdeMountPipeline (calls RecoverFromWalAsync at mount, RunAsync in background, FlushAsync at unmount)
  - MerkleIntegrityVerifier (receives UpdateBlockAsync calls from batch processor)
  - Write path (calls MarkDirty post-write; no tree traversal on hot path)

tech-stack:
  added: []
  patterns:
    - "ConcurrentQueue<T> for lock-free O(1) hot-path enqueue"
    - "Interlocked.Read/Exchange for volatile metric fields"
    - "IAsyncDisposable with best-effort flush on dispose"
    - "SHA-256 BCL fallback with BLAKE3 TODO comment for future upgrade"
    - "Last-write-wins deduplication within batch via Dictionary<long, byte[]>"

key-files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/Integrity/MerkleBatchConfig.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Integrity/EpochBatchedMerkleUpdater.cs
  modified: []

key-decisions:
  - "MarkDirty is the ONLY method on write hot path — O(1) ConcurrentQueue.Enqueue, no locks, no tree traversal"
  - "SHA-256 used as BLAKE3 BCL fallback; TODO comment marks substitution site for future upgrade"
  - "Last-write-wins deduplication per block within batch epoch via Dictionary before UpdateBlockAsync"
  - "DirtyQueueCapacity is a soft cap (back-pressure warning only); queue itself is unbounded to preserve write progress"
  - "DisposeAsync flushes with 30s timeout so in-flight dirty blocks are not silently lost on shutdown"
  - "RecoverFromWalAsync prefers AfterImage from WAL entry over device read to minimize I/O during recovery"

patterns-established:
  - "Epoch-batch pattern: hot path enqueues, background thread drains at interval — applies to any latency-sensitive tree update"
  - "Interlocked.Exchange for TimeSpan ticks (volatile long workaround for DateTimeOffset/TimeSpan fields)"

duration: 4min
completed: 2026-03-02
---

# Phase 91.5 Plan 87-17: Epoch-Batched Merkle Updater Summary

**Lock-free epoch-batched Merkle tree background updater (VOPT-30): write hot path enqueues O(1) dirty entries; background thread batch-processes at configurable 5s interval with WAL crash recovery and unmount flush**

## Performance

- **Duration:** 4 min
- **Started:** 2026-03-02T12:35:23Z
- **Completed:** 2026-03-02T12:39:00Z
- **Tasks:** 1
- **Files modified:** 2

## Accomplishments

- `MerkleBatchConfig` provides four tunable knobs: epoch interval (5 s default), max dirty blocks per batch (8 192), WAL recovery toggle, and soft queue capacity (65 536)
- `EpochBatchedMerkleUpdater` fully decouples the write hot path from Merkle tree traversal: `MarkDirty` only enqueues a `DirtyBlockEntry` (SHA-256 hash + block number), with zero locks and zero tree I/O
- `RunAsync` background loop wakes every `BatchInterval`, deduplicates dirty entries within the epoch (last-write-wins), and delegates each leaf update to `MerkleIntegrityVerifier.UpdateBlockAsync`
- `RecoverFromWalAsync` reconstructs the dirty set from WAL `BlockWrite` entries at mount time, preferring embedded AfterImages to avoid extra device reads, then processes one full batch to make the tree consistent before writes resume
- `FlushAsync` drains all pending dirty blocks at unmount; `DisposeAsync` wraps it with a 30 s timeout so in-flight entries are not silently lost

## Task Commits

1. **Task 1: MerkleBatchConfig and EpochBatchedMerkleUpdater** - `f74d8784` (feat)

**Plan metadata:** (docs commit — see below)

## Files Created/Modified

- `DataWarehouse.SDK/VirtualDiskEngine/Integrity/MerkleBatchConfig.cs` - Batch configuration (interval, max blocks, WAL recovery, queue capacity)
- `DataWarehouse.SDK/VirtualDiskEngine/Integrity/EpochBatchedMerkleUpdater.cs` - Background epoch updater + DirtyBlockEntry + MerkleBatchStats

## Decisions Made

- **SHA-256 as BLAKE3 BCL fallback:** BLAKE3 is not in System.Security.Cryptography as of .NET 9; SHA-256 used with a `// TODO: replace with Blake3.HashData` comment at the hash site for easy future substitution.
- **Soft queue cap:** `DirtyQueueCapacity` triggers a `Debug.WriteLine` back-pressure warning but never blocks the hot path — the ConcurrentQueue is unbounded to preserve write progress under burst.
- **Last-write-wins deduplication:** A `Dictionary<long, byte[]>` per batch epoch ensures that if block N is dirtied 100 times in one epoch, `UpdateBlockAsync` is called once with the most recent hash, saving O(n) I/O.
- **30 s dispose timeout:** `DisposeAsync` uses a scoped `CancellationTokenSource` so a stuck or very large flush does not hang the host process indefinitely.

## Deviations from Plan

None — plan executed exactly as written.

## Issues Encountered

None. The first build run showed a pre-existing XML doc error in `WalShard.cs` that resolved on the second build invocation (incremental build cache); subsequent builds were clean with zero errors.

## User Setup Required

None — no external service configuration required.

## Next Phase Readiness

- VOPT-30 satisfied: epoch-batched Merkle updates are ready for integration into `VdeMountPipeline`
- `VdeMountPipeline` should call `RecoverFromWalAsync(wal.ReplayAsync())` before starting `RunAsync` on a background task, and `FlushAsync` before unmounting
- Ready for Phase 91.5 plan 87-18 or next plan in sequence

---
*Phase: 91.5-vde-v2.1-format-completion*
*Completed: 2026-03-02*

## Self-Check: PASSED

- FOUND: DataWarehouse.SDK/VirtualDiskEngine/Integrity/MerkleBatchConfig.cs
- FOUND: DataWarehouse.SDK/VirtualDiskEngine/Integrity/EpochBatchedMerkleUpdater.cs
- FOUND: .planning/phases/91.5-vde-v2.1-format-completion/87-17-SUMMARY.md
- FOUND commit: f74d8784
- Build: 0 errors, 0 warnings
