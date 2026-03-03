---
phase: 91.5-vde-v2.1-format-completion
plan: 87-22
subsystem: vde-journal
tags: [wal, pub-sub, streaming, cursor, zero-copy, journal, vde, wals]

# Dependency graph
requires:
  - phase: 71
    provides: BlockTypeTags (WALS = 0x57414C53), VDE v2.0 format foundation
  - phase: 91.5
    provides: IVolatileKeyRing (EKEY), ZnsZoneMapRegion (ZNSM) — peer VOPT modules

provides:
  - WalSubscriberCursor 32-byte readonly struct with LE serialization
  - WalSubscriberFlags [Flags] enum (None/Active/Paused/AclRestricted/ReadOnly/AutoAdvance)
  - WalSubscriberCursorTable class: WALS region (BlockTypeTag=0x57414C53, ModuleBitPosition=21)
  - IWalSubscriber interface: PollAsync/AdvanceAsync/GetCursorAsync/PauseAsync/ResumeAsync

affects:
  - Phase 92 VDE Decorator Chain (WAL subscriber decorators)
  - Phase 95 CRDT WAL (dual-ring WAL subscription topology)
  - Phase 96 Federation Router (multi-VDE WAL fan-out)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "32-byte cursor struct with Serialize/Deserialize static pair using BinaryPrimitives LE"
    - "WALS region header: [CursorCount:4][Reserved:12] + 32B cursor slots"
    - "GetOldestSubscriberEpoch drives WAL retention: keeps minimum LastEpoch across active subscribers"
    - "AutoAdvance flag eliminates explicit AdvanceAsync call in streaming-only subscribers"

key-files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/Journal/WalSubscriberCursorTable.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Journal/IWalSubscriber.cs
  modified: []

key-decisions:
  - "WalSubscriberCursor Flags field is ulong (not byte) so the full WalSubscriberFlags [Flags] enum packs cleanly into 8 bytes, preserving 32B struct size per spec"
  - "MaxCursorsPerBlock computed as (blockSize-16)/32; capped by MaxSubscribers=128; constructor validates blockSize >= 48"
  - "SubscriberId=0 is reserved (unregistered slot sentinel); RegisterSubscriber throws ArgumentException for zero"
  - "Serialize writes all registered cursors; Deserialize skips slots where SubscriberId=0 (sparse on-disk layout support)"
  - "GetOldestSubscriberEpoch returns ulong.MaxValue when no active subscribers (safe: WAL can truncate freely)"
  - "IWalSubscriber.AdvanceAsync throws InvalidOperationException for ReadOnly subscribers per spec ACL semantics"

patterns-established:
  - "WALS cursor table: 16-byte region header + packed 32B cursor slots, LE byte order throughout"
  - "Per-subscriber ACL via flags field: AclRestricted bit gates entry visibility; ReadOnly bit gates cursor advance"

# Metrics
duration: 3min
completed: 2026-03-02
---

# Phase 91.5 Plan 87-22: WAL Streaming Pub/Sub Summary

**WalSubscriberCursorTable WALS region (bit 21) with 32-byte cursor entries enabling filesystem-as-Kafka zero-copy WAL streaming via IWalSubscriber poll/advance interface with per-subscriber ACLs.**

## Performance

- **Duration:** 3 min
- **Started:** 2026-03-02T12:17:42Z
- **Completed:** 2026-03-02T12:20:11Z
- **Tasks:** 1
- **Files modified:** 2

## Accomplishments

- Implemented `WalSubscriberCursor` as a 32-byte readonly struct with exact layout per spec: [SubscriberId:8][LastEpoch:8][LastSequence:8][Flags:8] using BinaryPrimitives LE serialization
- Implemented `WalSubscriberCursorTable` with WALS block type tag (0x57414C53), module bit 21, MaxSubscribers=128, full register/unregister/advance/query/serialize/deserialize API
- Implemented `IWalSubscriber` providing zero-copy WAL streaming contract: PollAsync, AdvanceAsync, GetCursorAsync, PauseAsync, ResumeAsync with per-subscriber ACL enforcement semantics
- Build: 0 errors, 0 warnings

## Task Commits

1. **Task 1: WalSubscriberCursorTable and IWalSubscriber** - `a1a5093f` (feat)

## Files Created/Modified

- `DataWarehouse.SDK/VirtualDiskEngine/Journal/WalSubscriberCursorTable.cs` - WalSubscriberCursor struct, WalSubscriberFlags enum, WalSubscriberCursorTable class
- `DataWarehouse.SDK/VirtualDiskEngine/Journal/IWalSubscriber.cs` - IWalSubscriber interface for zero-copy WAL subscription

## Decisions Made

- WalSubscriberCursor Flags field is `ulong` (not `byte`) so the full `WalSubscriberFlags` `[Flags]` enum packs cleanly into 8 bytes, preserving the 32B struct size per spec
- MaxCursorsPerBlock computed as `(blockSize - 16) / 32`; constructor validates `blockSize >= 48` to ensure at least one cursor fits
- SubscriberId=0 is reserved as the unregistered slot sentinel; `RegisterSubscriber` throws `ArgumentException` for zero
- `GetOldestSubscriberEpoch` returns `ulong.MaxValue` when no active subscribers are present (WAL can truncate freely)
- `IWalSubscriber.AdvanceAsync` documented to throw `InvalidOperationException` for `ReadOnly` subscribers per ACL semantics

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- WALS module (bit 21) complete: cursor region, serialization, and subscription interface ready
- VDE Decorator Chain (Phase 92) can build WAL subscriber decorators against IWalSubscriber
- CRDT WAL (Phase 95) dual-ring topology can use WalSubscriberCursorTable for subscriber state persistence

## Self-Check

- [x] `DataWarehouse.SDK/VirtualDiskEngine/Journal/WalSubscriberCursorTable.cs` exists
- [x] `DataWarehouse.SDK/VirtualDiskEngine/Journal/IWalSubscriber.cs` exists
- [x] Commit `a1a5093f` exists
- [x] Build: 0 errors, 0 warnings

## Self-Check: PASSED

---
*Phase: 91.5-vde-v2.1-format-completion*
*Completed: 2026-03-02*
