---
phase: 91.5-vde-v2.1-format-completion
plan: 57
subsystem: vde-io
tags: [windows, iocp, ioring, overlapped, p-invoke, block-device, batch-io]

requires:
  - phase: 91.5-vde-v2.1-format-completion
    provides: "IBatchBlockDevice interface, BlockRange, WriteRequest structs (plan 56)"
provides:
  - "WindowsOverlappedBlockDevice: IOCP-based IBatchBlockDevice for Windows 10+"
  - "IoRingBlockDevice: IoRing SQE/CQE IBatchBlockDevice for Windows 11+"
  - "OverlappedNativeMethods: P/Invoke for IOCP and file I/O"
  - "IoRingNativeMethods: P/Invoke for Windows 11 IoRing API with availability detection"
affects: [vde-mount-pipeline, platform-factory, device-selection]

tech-stack:
  added: [kernel32-ioring, kernel32-iocp]
  patterns: [pinned-buffer-pool, lazy-availability-detection, sqe-cqe-batching, dangerous-addref-lifecycle]

key-files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/IO/Windows/OverlappedNativeMethods.cs
    - DataWarehouse.SDK/VirtualDiskEngine/IO/Windows/WindowsOverlappedBlockDevice.cs
    - DataWarehouse.SDK/VirtualDiskEngine/IO/Windows/IoRingNativeMethods.cs
    - DataWarehouse.SDK/VirtualDiskEngine/IO/Windows/IoRingBlockDevice.cs
  modified: []

key-decisions:
  - "Used RandomAccess API internally for WindowsOverlappedBlockDevice instead of raw P/Invoke ReadFile/WriteFile -- provides IOCP performance with managed safety"
  - "IoRingBlockDevice stores raw handle via DangerousAddRef lifecycle to satisfy S3869 analyzer rule"
  - "IoRing write fallback to RandomAccess.WriteAsync for early Windows 11 builds missing BuildIoRingWriteFile"
  - "GC.AllocateArray with pinned:true for IoRing buffer pool instead of NativeMemory.AlignedAlloc"

patterns-established:
  - "DangerousAddRef/DangerousRelease lifecycle: Cache raw OS handle once in constructor, release in Dispose"
  - "Lazy<bool> availability detection: Create+Close probe for IoRing API presence"
  - "Concurrency limiter pattern: SemaphoreSlim throttling for batch I/O parallelism"

duration: 6min
completed: 2026-03-02
---

# Phase 91.5 Plan 57: Windows Async Block Devices Summary

**IOCP-based WindowsOverlappedBlockDevice (Win10+) and IoRing-based IoRingBlockDevice (Win11+) implementing IBatchBlockDevice with scatter-gather batching and runtime fallback**

## Performance

- **Duration:** 6 min
- **Started:** 2026-03-02T14:19:35Z
- **Completed:** 2026-03-02T14:25:49Z
- **Tasks:** 1
- **Files modified:** 4

## Accomplishments
- WindowsOverlappedBlockDevice opens files with NO_BUFFERING + WRITE_THROUGH + Async for IOCP integration, with concurrent batch coalescing via Task.WhenAll
- IoRingBlockDevice provides SQE/CQE model with pinned buffer pool, submitting multiple I/O ops in a single SubmitIoRing syscall
- IoRingNativeMethods detects API availability via Lazy<bool> probe (CreateIoRing + CloseIoRing), with separate IsWriteSupported check for BuildIoRingWriteFile
- Both devices implement IBatchBlockDevice with ReadBatchAsync/WriteBatchAsync supporting scatter-gather BlockRange/WriteRequest

## Task Commits

Each task was committed atomically:

1. **Task 1: WindowsOverlappedBlockDevice with IOCP and IoRingBlockDevice with IoRing API** - `71c23e7f` (feat)

## Files Created/Modified
- `DataWarehouse.SDK/VirtualDiskEngine/IO/Windows/OverlappedNativeMethods.cs` - P/Invoke declarations for OVERLAPPED I/O, IOCP completion ports, FlushFileBuffers, flag constants
- `DataWarehouse.SDK/VirtualDiskEngine/IO/Windows/WindowsOverlappedBlockDevice.cs` - IOCP-based IBatchBlockDevice with FILE_FLAG_NO_BUFFERING, concurrent batch coalescing, concurrency limiter
- `DataWarehouse.SDK/VirtualDiskEngine/IO/Windows/IoRingNativeMethods.cs` - P/Invoke for IoRing API (CreateIoRing, BuildIoRingReadFile/WriteFile, SubmitIoRing, PopIoRingCompletion) with Lazy availability detection
- `DataWarehouse.SDK/VirtualDiskEngine/IO/Windows/IoRingBlockDevice.cs` - IoRing SQE/CQE IBatchBlockDevice with pinned buffer pool, write fallback, ring serialization

## Decisions Made
- Used RandomAccess API internally for WindowsOverlappedBlockDevice rather than raw OVERLAPPED P/Invoke. This provides IOCP performance (FileOptions.Asynchronous enables IOCP) with managed safety and simpler code. The batch coalescing via Task.WhenAll is the key differentiator from FileBlockDevice.
- Used GC.AllocateArray with `pinned: true` for IoRing buffer pool (safer than NativeMemory.AlignedAlloc, automatically collected on dispose)
- Cached raw file handle via DangerousAddRef/DangerousGetHandle/DangerousRelease lifecycle to satisfy SonarSource S3869 rule

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed SonarSource S3869 DangerousGetHandle violations**
- **Found during:** Task 1 (build verification)
- **Issue:** SonarSource analyzer flagged 4 uses of SafeHandle.DangerousGetHandle as errors
- **Fix:** Cached raw handle once in constructor via DangerousAddRef lifecycle pattern, replaced all call sites with cached field, added pragma suppression for the single constructor call
- **Files modified:** IoRingBlockDevice.cs
- **Verification:** Build succeeds with 0 errors 0 warnings
- **Committed in:** 71c23e7f (Task 1 commit)

---

**Total deviations:** 1 auto-fixed (1 bug)
**Impact on plan:** Minor refactor required by static analyzer. No scope creep.

## Issues Encountered
None

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Windows async I/O backends complete, ready for platform-aware device factory or VdeMountPipeline integration
- IoRing availability detection enables transparent fallback at runtime

---
*Phase: 91.5-vde-v2.1-format-completion*
*Completed: 2026-03-02*
