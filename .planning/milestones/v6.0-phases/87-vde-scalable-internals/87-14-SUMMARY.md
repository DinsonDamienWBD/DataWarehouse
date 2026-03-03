---
phase: 87-vde-scalable-internals
plan: 14
subsystem: VDE CoW & Replication
tags: [cow, snapshots, replication, extent-granularity, mvcc]
dependency_graph:
  requires: ["87-04 (MVCC)", "87-06 (Extent format)"]
  provides: ["VOPT-26 (extent-aware CoW)", "VOPT-27 (extent-aware replication)"]
  affects: ["CopyOnWrite", "Replication"]
tech_stack:
  added: []
  patterns: ["ConcurrentDictionary ref counting", "extent-level CoW with partial split", "RDLT binary delta format", "XxHash64 checksum verification"]
key_files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/CopyOnWrite/ExtentAwareCowManager.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Replication/ExtentDeltaReplicator.cs
  modified: []
decisions:
  - "ConcurrentDictionary for extent ref counts (only tracks non-default ref counts > 1)"
  - "Partial-extent CoW splits into up to 3 sub-extents to minimize data copying"
  - "RDLT binary format: Magic(4) + Version(2) + SinceTxId(8) + UntilTxId(8) + ExtentCount(4) + entries"
  - "XxHash64 for post-write checksum verification on delta application"
metrics:
  duration: 7min
  completed: 2026-02-23T21:56:00Z
  tasks: 2
  files: 2
---

# Phase 87 Plan 14: Extent-Aware CoW & Replication Summary

Extent-granularity CoW snapshots with ref counting and extent-based replication delta computation with RDLT binary serialization format.

## Task 1: ExtentAwareCowManager

Created `ExtentAwareCowManager` implementing extent-level copy-on-write:

- **Ref counting**: `ConcurrentDictionary<long, int>` keyed by extent start block; only tracks non-default counts (>1) for space efficiency
- **IncrementRef/DecrementRef**: Thread-safe via `AddOrUpdate`; entries removed when back to default (1) or freed (0)
- **CreateSnapshotAsync**: Allocates snapshot inode, marks all source extents with `ExtentFlags.SharedCow`, increments ref counts, WAL-journaled
- **CopyOnWriteAsync**: If ref count > 1, allocates new blocks and copies; if partial write on multi-block extent, splits into up to 3 sub-extents (before/modified/after); if ref count == 1, writes directly
- **DeleteSnapshotAsync**: Decrements refs on all extents; frees extent blocks where ref drops to 0; frees snapshot inode
- **Persistence**: `SerializeRefCounts`/`DeserializeRefCounts` for SNAP region (4-byte count + 12-byte entries)

Commit: `ad114465`

## Task 2: ExtentDeltaReplicator

Created `ExtentDeltaReplicator` implementing extent-based replication:

- **ComputeDeltaAsync**: Two overloads -- auto-discovery via MVCC transaction IDs, and explicit changed extent list; reads full extent data from device; skips empty/sparse extents
- **ReplicationDelta**: `SinceTransactionId`, `UntilTransactionId`, `ChangedExtents` list, `TotalBytes`, `ExtentCount`
- **ExtentDelta**: readonly struct with `InodeExtent`, `byte[] Data`, `SourceInodeNumber`
- **ApplyDeltaAsync**: Writes each extent to target device at correct block positions; verifies XxHash64 checksum after write
- **SerializeDelta**: Compact binary format with RDLT magic header, version, and extent entries (36-byte header + variable data per extent)
- **DeserializeDelta**: Validates magic/version, reconstructs delta with full error detection for truncated data
- **ReplicationStats**: `ExtentsShipped`, `BytesShipped`, `BlocksSkipped`, `CompressionRatio`, `Efficiency` (ratio of delta to full VDE)

Commit: `735d55c1`

## Deviations from Plan

None -- plan executed exactly as written.

## Verification

- `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj --no-restore` -- **Build succeeded, 0 errors, 0 warnings**
- ExtentAwareCowManager marks extents with SharedCow flag on snapshot creation
- ExtentDeltaReplicator ships only changed extents as delta units

## Self-Check: PASSED
