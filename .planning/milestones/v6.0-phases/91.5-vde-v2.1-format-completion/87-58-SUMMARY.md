---
phase: 91.5-vde-v2.1-format-completion
plan: 58
subsystem: vde-io
tags: [kqueue, posix-aio, macos, freebsd, block-device, direct-io, p-invoke]

requires:
  - phase: 91.5-vde-v2.1-format-completion
    provides: "IBlockDevice hierarchy (87-56), IBatchBlockDevice interface"
provides:
  - "KqueueBlockDevice: kqueue + POSIX AIO async block device for macOS/FreeBSD"
  - "KqueueNativeMethods: P/Invoke declarations for kqueue, kevent, POSIX AIO, fcntl"
  - "F_NOCACHE page cache bypass on macOS"
affects: [87-59-block-device-factory, 87-60-io-uring]

tech-stack:
  added: [kqueue, posix-aio, f-nocache]
  patterns: [native-heap-aiocb-allocation, spin-then-delay-polling, intptr-async-bridge]

key-files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/IO/Unix/KqueueNativeMethods.cs
    - DataWarehouse.SDK/VirtualDiskEngine/IO/Unix/KqueueBlockDevice.cs

key-decisions:
  - "Native heap aiocb allocation via NativeMemory to ensure stable pointers across async/await"
  - "IntPtr bridge pattern for passing native pointers to async methods (C# disallows pointer params in async)"
  - "SIGEV_NONE polling instead of SIGEV_KEVENT for macOS compatibility"
  - "Struct named KqueueEvent to avoid collision with kevent P/Invoke method"

patterns-established:
  - "Native-heap aiocb: allocate via NativeMemory.AllocZeroed, pass as IntPtr, free in finally"
  - "TryCheckCompletion + PollForCompletionAsync: sync unsafe check separated from async poll loop"
  - "Spin-then-delay: yield for SpinCountBeforeDelay iterations, then Task.Delay(1ms)"

duration: 8min
completed: 2026-03-02
---

# Phase 91.5 Plan 58: KqueueBlockDevice Summary

**kqueue + POSIX AIO async block device for macOS/FreeBSD with F_NOCACHE page cache bypass and native-heap aiocb management**

## Performance

- **Duration:** 8 min
- **Started:** 2026-03-02T14:28:48Z
- **Completed:** 2026-03-02T14:37:00Z
- **Tasks:** 1
- **Files created:** 2

## Accomplishments
- KqueueNativeMethods with P/Invoke for kqueue, kevent, aio_read, aio_write, aio_error, aio_return, aio_cancel, fcntl, fsync, open, close, ftruncate
- KqueueBlockDevice implementing IBatchBlockDevice with F_NOCACHE direct I/O on macOS
- Native-heap aiocb allocation pattern to bridge unsafe pointers with async/await
- Batch read/write with sequential AIO submit and partial success semantics

## Task Commits

Each task was committed atomically:

1. **Task 1: KqueueBlockDevice with kqueue + POSIX AIO** - `3be3a5c6` (feat)

## Files Created/Modified
- `DataWarehouse.SDK/VirtualDiskEngine/IO/Unix/KqueueNativeMethods.cs` - P/Invoke for libc kqueue/AIO/fcntl with macOS/FreeBSD structs (Kevent, Aiocb, Timespec, Sigevent)
- `DataWarehouse.SDK/VirtualDiskEngine/IO/Unix/KqueueBlockDevice.cs` - IBatchBlockDevice using kqueue + POSIX AIO with F_NOCACHE, native-heap aiocb, spin-then-delay polling

## Decisions Made
- Used NativeMemory.AllocZeroed for aiocb allocation to get stable pointers across async/await (C# prohibits pointer locals in async methods and unsafe+await)
- Named the kevent struct KqueueEvent to avoid name collision with the KeventCall P/Invoke method
- Used SIGEV_NONE on all platforms (macOS does not reliably support SIGEV_KEVENT for AIO)
- Targeted macOS arm64/x86_64 struct layouts with TODO comments for FreeBSD validation
- Separated sync SubmitAioRead/Write and TryCheckCompletion from async PollForCompletionAsync to keep unsafe code out of async methods

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed async/unsafe incompatibility in C#**
- **Found during:** Task 1 (KqueueBlockDevice implementation)
- **Issue:** C# does not allow pointer parameters in async methods or await inside unsafe blocks. Initial approach of stack-allocated aiocb with pointer params failed with CS4004/CS9123 errors.
- **Fix:** Allocated aiocb structs on the native heap via NativeMemory.AllocZeroed, passed as IntPtr to async methods, cast back to pointer in sync unsafe helper methods (SubmitAioRead, TryCheckCompletion).
- **Files modified:** KqueueBlockDevice.cs
- **Verification:** Build compiles with zero errors from Kqueue files
- **Committed in:** 3be3a5c6 (Task 1 commit)

**2. [Rule 1 - Bug] Fixed Kevent struct/method name collision**
- **Found during:** Task 1 (KqueueNativeMethods)
- **Issue:** Both the P/Invoke method and the struct were named "Kevent", causing CS0102 duplicate definition error.
- **Fix:** Renamed struct to KqueueEvent, P/Invoke method to KeventCall.
- **Files modified:** KqueueNativeMethods.cs
- **Verification:** Build compiles clean
- **Committed in:** 3be3a5c6 (Task 1 commit)

---

**Total deviations:** 2 auto-fixed (2 bugs)
**Impact on plan:** Both fixes required for correctness -- C# compiler constraints on async+unsafe. No scope creep.

## Issues Encountered
- Pre-existing build errors in SpdkBlockDevice.cs (CS4004 unsafe+await, CS0213 fixed statement) -- not introduced by this plan, not blocking.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Unix/ directory established with KqueueBlockDevice ready for BlockDeviceFactory integration
- io_uring Linux block device (87-60) can follow same pattern with native-heap allocation

---
*Phase: 91.5-vde-v2.1-format-completion*
*Completed: 2026-03-02*
