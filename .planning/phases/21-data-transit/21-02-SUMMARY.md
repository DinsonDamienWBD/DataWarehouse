---
phase: 21-data-transit
plan: 02
subsystem: transport
tags: [chunked-transfer, resumable, delta-sync, rolling-hash, adler-32, sha-256, rsync, manifest, exponential-backoff]

# Dependency graph
requires:
  - phase: 21-data-transit-01
    provides: DataTransitStrategyBase, TransitCapabilities, TransitRequest, TransitResult, TransitProgress, IDataTransitStrategy
provides:
  - ChunkedResumableStrategy with manifest-based chunk tracking and resume from last completed chunk
  - DeltaDifferentialStrategy with Adler-32 rolling hash and SHA-256 strong hash for rsync-style delta sync
  - RollingHashComputer with O(1) per-byte rolling window updates
affects: [21-03, 21-04, 21-05]

# Tech tracking
tech-stack:
  added: []
  patterns: [chunk-manifest-pattern, rolling-hash-delta-sync, exponential-backoff-retry, binary-delta-instruction-set]

key-files:
  created:
    - Plugins/DataWarehouse.Plugins.UltimateDataTransit/Strategies/Chunked/ChunkedResumableStrategy.cs
    - Plugins/DataWarehouse.Plugins.UltimateDataTransit/Strategies/Chunked/DeltaDifferentialStrategy.cs
  modified: []

key-decisions:
  - "ChunkManifest stores TransitRequest reference for resume capability (Request property set during TransferAsync)"
  - "Adler-32 mod 65521 for weak hash (rsync-compatible), SHA-256 for strong hash collision resolution"
  - "Binary delta payload format: instruction count, then per-instruction (type byte, MATCH=destBlockIndex+sourceOffset, LITERAL=offset+length+data)"
  - "RollingHashComputer is stateless and thread-safe (no instance fields), shared across all transfers"

patterns-established:
  - "Chunk manifest pattern: ConcurrentDictionary<string, ChunkManifest> for in-memory transfer state enabling resume"
  - "Exponential backoff retry: 1s, 2s, 4s per chunk (3 retries) before failing entire transfer"
  - "3-phase delta transfer: signature retrieval -> block matching -> delta instruction send"
  - "Full transfer fallback when destination returns 404 for signatures (no existing file)"

# Metrics
duration: 7min
completed: 2026-02-11
---

# Phase 21 Plan 02: Chunked/Resumable and Delta/Differential Transfer Strategies Summary

**Chunked resumable transfer with SHA-256 per-chunk integrity and manifest-based resume; rsync-style delta differential with Adler-32 rolling hash and SHA-256 strong hash transferring only changed blocks**

## Performance

- **Duration:** 7 min
- **Started:** 2026-02-11T10:18:13Z
- **Completed:** 2026-02-11T10:25:15Z
- **Tasks:** 2/2
- **Files created:** 2

## Accomplishments

- Implemented ChunkedResumableStrategy (754 lines) splitting files into configurable chunks (default 4MB) with SHA-256 integrity hash per chunk, Content-Range headers for server-side assembly, and ConcurrentDictionary-based manifest storage enabling ResumeTransferAsync to skip completed chunks
- Implemented DeltaDifferentialStrategy (793 lines) with 3-phase rsync-style algorithm: (1) request destination block signatures, (2) rolling Adler-32 scan with SHA-256 collision resolution, (3) binary delta instruction transfer (MATCH references + LITERAL data)
- RollingHashComputer provides O(1) per-byte ComputeAdler32 and RollHash (sliding window update subtracting outgoing byte, adding incoming byte, all mod 65521)
- Exponential backoff retry per chunk (1s, 2s, 4s, 3 attempts) before failing the transfer; failed chunks stay unmarked in manifest for resume
- Full transfer fallback when destination has no existing file (404 response to signatures request)
- Delta savings tracked in TransitResult.Metadata["deltaSavingsPercent"] for bandwidth optimization visibility

## Task Commits

Each task was committed atomically:

1. **Task 1: Implement ChunkedResumableStrategy** - `e867b91` (feat)
2. **Task 2: Implement DeltaDifferentialStrategy** - `d0d849c` (feat)

## Files Created/Modified

- `Plugins/DataWarehouse.Plugins.UltimateDataTransit/Strategies/Chunked/ChunkedResumableStrategy.cs` - Chunked transfer with manifest tracking, SHA-256 per-chunk, resume from last completed chunk, exponential backoff retry
- `Plugins/DataWarehouse.Plugins.UltimateDataTransit/Strategies/Chunked/DeltaDifferentialStrategy.cs` - Delta differential with Adler-32 rolling hash, SHA-256 strong hash, binary delta instructions, full transfer fallback

## Decisions Made

- ChunkManifest stores a reference to the original TransitRequest so ResumeTransferAsync can access source stream and destination endpoint without requiring them as parameters
- Used Adler-32 (mod 65521, the largest prime < 2^16) for weak hash to match rsync protocol standard; SHA-256 for strong hash per research recommendation (no MD5)
- Binary delta payload format chosen over JSON for efficiency: instruction count header, then per-instruction type byte (0=MATCH, 1=LITERAL) with compact binary encoding
- RollingHashComputer is a stateless class (no instance mutable state) making it inherently thread-safe and shareable across concurrent transfers

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

- Transient MSBuild workload manifest resolver error (MSB4242) during Task 2 build verification. Resolved by shutting down stale build servers (`dotnet build-server shutdown`). Not a code issue.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Two advanced chunked strategies ready for Plan 21-03+ (message queue, cloud, P2P, features)
- ChunkManifest pattern can be extended for persistent storage (currently in-memory via ConcurrentDictionary)
- RollingHashComputer is reusable for any strategy needing block-level comparison
- Both strategies integrate with base class transfer ID generation, statistics, cancellation tracking

## Self-Check: PASSED

- All 2 created files verified present on disk
- Commit e867b91 verified (Task 1: ChunkedResumableStrategy)
- Commit d0d849c verified (Task 2: DeltaDifferentialStrategy)
- Plugin build: 0 errors
- Forbidden patterns: 0 matches
- ChunkedResumableStrategy: 754 lines (min 200 required)
- DeltaDifferentialStrategy: 793 lines (min 180 required)

---
*Phase: 21-data-transit*
*Completed: 2026-02-11*
