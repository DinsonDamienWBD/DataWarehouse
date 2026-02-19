---
phase: 58-zero-gravity-storage
plan: "07"
subsystem: Storage.Migration
tags: [migration, zero-downtime, read-forwarding, checkpointing, throttling]
dependency-graph:
  requires: ["58-01"]
  provides: ["BackgroundMigrationEngine", "ReadForwardingTable", "MigrationCheckpointStore"]
  affects: ["58-08"]
tech-stack:
  added: []
  patterns: ["read-forwarding-table", "file-based-checkpointing", "delegate-injection", "throttled-batch-processing"]
key-files:
  created:
    - DataWarehouse.SDK/Storage/Migration/BackgroundMigrationEngine.cs
    - DataWarehouse.SDK/Storage/Migration/ReadForwardingTable.cs
    - DataWarehouse.SDK/Storage/Migration/MigrationCheckpointStore.cs
  modified: []
decisions:
  - "Used positional record constructors to match existing MigrationTypes.cs patterns"
  - "Delegate injection for storage operations (ReadObjectAsync, WriteObjectAsync, etc.) instead of interface dependency"
metrics:
  duration: "~4 minutes"
  completed: "2026-02-19"
  tasks: 2
  files-created: 3
  files-modified: 0
---

# Phase 58 Plan 07: Background Migration Engine Summary

Zero-downtime background migration engine with read forwarding, checkpointing, and throttling for moving data between storage nodes without service interruption.

## What Was Built

### ReadForwardingTable
Thread-safe in-memory forwarding table using `ConcurrentDictionary` that maps object keys to their new node locations during migration. Features:
- `RegisterForwarding`: creates forwarding entry with configurable TTL and max hops
- `Lookup`: returns active forwarding entry or null (auto-removes expired)
- `RemoveForwarding` / `RemoveByOriginalNode`: explicit cleanup after migration
- Background timer cleans expired entries every 5 minutes
- Default 24-hour TTL, configurable via constructor

### MigrationCheckpointStore
File-based checkpoint persistence with in-memory cache for fast lookups:
- `SaveCheckpointAsync`: writes checkpoint to both memory and JSON file on disk
- `LoadCheckpointAsync`: checks memory cache first, falls back to disk
- `DeleteCheckpointAsync`: removes from both memory and disk after successful migration
- Checkpoints stored as `migration-{jobId}.checkpoint.json`

### BackgroundMigrationEngine
Full `IMigrationEngine` implementation with 8 interface methods:
- **StartMigrationAsync**: creates job, checks for resume checkpoint, starts background execution
- **PauseMigrationAsync / ResumeMigrationAsync**: graceful pause with spin-wait loop
- **CancelMigrationAsync**: clean abort via linked CancellationTokenSource
- **GetStatusAsync / ListJobsAsync**: job status queries with optional status filtering
- **MonitorAsync**: IAsyncEnumerable streaming of job state updates at 1-second intervals
- **GetForwardingEntryAsync**: lookup forwarding for specific object key

Migration lifecycle per object: COPY -> FORWARD -> VERIFY -> CLEANUP. Throttling enforces configurable bytes/sec limit. Checkpoints saved every N objects (configurable batch size, default 100).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed record constructor syntax**
- **Found during:** Task 1 and Task 2
- **Issue:** Plan used object initializer syntax (`new ReadForwardingEntry { ... }`) but MigrationTypes.cs defines positional records requiring constructor syntax
- **Fix:** Used positional constructor calls (`new ReadForwardingEntry(ObjectKey: ..., ...)`) throughout
- **Files modified:** All 3 created files
- **Commit:** aa31868b, c075438e

## Commits

| Task | Commit | Description |
|------|--------|-------------|
| 1 | aa31868b | ReadForwardingTable and MigrationCheckpointStore |
| 2 | c075438e | BackgroundMigrationEngine with zero-downtime migration |

## Self-Check: PASSED

- [x] BackgroundMigrationEngine.cs exists
- [x] ReadForwardingTable.cs exists
- [x] MigrationCheckpointStore.cs exists
- [x] Commit aa31868b verified
- [x] Commit c075438e verified
- [x] SDK build succeeds with 0 errors, 0 warnings
