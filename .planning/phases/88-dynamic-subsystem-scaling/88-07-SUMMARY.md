---
phase: 88-dynamic-subsystem-scaling
plan: 07
subsystem: replication-aeds-scaling
tags: [scaling, replication, aeds, wal, bounded-cache, partitioned-queues]
dependency_graph:
  requires: ["88-01"]
  provides: ["ReplicationScalingManager", "AedsScalingManager"]
  affects: ["UltimateReplication", "AedsCore"]
tech_stack:
  added: []
  patterns: ["per-namespace-strategy-routing", "wal-backed-queue", "streaming-conflict-comparison", "partitioned-job-queues", "per-collection-locking", "sliding-window-throughput"]
key_files:
  created:
    - Plugins/DataWarehouse.Plugins.UltimateReplication/Scaling/ReplicationScalingManager.cs
    - Plugins/DataWarehouse.Plugins.AedsCore/Scaling/AedsScalingManager.cs
  modified: []
decisions:
  - "Glob-to-regex conversion for namespace pattern matching with compiled Regex for performance"
  - "WAL entry serialization uses simple binary format (sequence+timestamp+length-prefixed strings)"
  - "SHA-256 per-chunk hash comparison before byte-by-byte for large objects (>= 2 chunks)"
  - "ConcurrentDictionary<string,SemaphoreSlim> for per-collection locks with lazy creation"
  - "Hash-based job partition routing via string.GetHashCode for consistent distribution"
  - "Sliding window throughput tracker (100 samples) for chunk size auto-adjustment"
metrics:
  duration: 6min
  completed: 2026-02-23T22:33:36Z
  tasks_completed: 2
  tasks_total: 2
  files_created: 2
  files_modified: 0
---

# Phase 88 Plan 07: Replication and AEDS Scaling Managers Summary

Per-namespace replication strategy routing with WAL-backed durable queues, streaming conflict comparison, bounded AEDS caches with LRU eviction, partitioned job queues, and per-collection locking.

## Task 1: ReplicationScalingManager

Created `ReplicationScalingManager` implementing `IScalableSubsystem` with four core capabilities:

**Per-namespace strategy routing:** `BoundedCache<string, string>` maps namespace patterns (glob) to strategy names via compiled `Regex`. First matching pattern wins; unmatched namespaces use configurable default. Routes configurable via `RegisterRoute()` or `dw.replication.strategy-routing` message bus topic.

**WAL-backed replication queue:** Append-only WAL using `IPersistentBackingStore` at `dw://replication/wal/{sequence}`. Per-replica consumer offsets track progress at `dw://replication/offsets/{replicaId}`. On restart, `RestoreOffsetsAsync` replays from last offset. Configurable retention (default 48h) with purge only after all replicas acknowledge.

**Streaming conflict comparison:** Replaces full-blob `SequenceEqual` with chunk-based comparison:
- Quick length check (short-circuit on size mismatch)
- For objects >= 2 chunks (128KB+): SHA-256 hash per 64KB chunk, compare hashes first
- Only byte-by-byte comparison when hashes differ (handles hash collisions)
- Short-circuits on first differing chunk

**Dynamic replica discovery:** Subscribes to `dw.cluster.membership.changed`. Auto-registers replicas on `joined`, removes on `left`/`removed`. Health check timer marks replicas inactive after 3 consecutive failures.

**Commit:** `3ac3af8b`

## Task 2: AedsScalingManager

Created `AedsScalingManager` implementing `IScalableSubsystem` with four core capabilities:

**Bounded LRU caches:** All five AEDS collections use `BoundedCache<string, byte[]>` with LRU eviction:
- Manifests: 10K entries
- Validations: 50K entries
- Jobs: 100K entries
- Clients: 10K entries
- Channels: 1K entries

All sizes configurable via `ScalingLimits` and runtime-reconfigurable with cache migration.

**Per-collection locks:** `ConcurrentDictionary<string, SemaphoreSlim>` keyed by collection name. Operations on different collections (manifests vs jobs vs clients) proceed concurrently. Lazy-create locks on first access.

**Partitioned job queues:** N partitions (default: `Environment.ProcessorCount`). Jobs routed by hash of job ID. Each partition processed by its own worker task. Results aggregated via metrics.

**Auto-adjusting chunk parameters:**
- `MaxConcurrentChunks`: computed from `GC.GetGCMemoryInfo()` available memory (10% budget / chunk size) capped by CPU count * 4
- `ChunkSizeBytes`: computed from sliding window throughput estimation (100 samples) targeting ~100ms per chunk

**Commit:** `ad901a6d`

## Deviations from Plan

None - plan executed exactly as written.

## Verification

- Replication plugin builds with 0 errors
- AEDS plugin builds with 0 errors
- SDK builds with 0 errors
- `SequenceEqual` in ReplicationScalingManager appears only in streaming chunk-based context (64KB `ReadOnlySpan<byte>` chunks and SHA-256 hash spans)
- No raw `ConcurrentDictionary` for entity state in AedsScalingManager (only for per-collection locks as specified)

## Self-Check: PASSED

- [x] `Plugins/DataWarehouse.Plugins.UltimateReplication/Scaling/ReplicationScalingManager.cs` exists
- [x] `Plugins/DataWarehouse.Plugins.AedsCore/Scaling/AedsScalingManager.cs` exists
- [x] Commit `3ac3af8b` exists
- [x] Commit `ad901a6d` exists
