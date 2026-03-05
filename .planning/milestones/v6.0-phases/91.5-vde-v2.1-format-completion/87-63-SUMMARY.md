---
phase: 91.5-vde-v2.1-format-completion
plan: 63
subsystem: vde-mount
tags: [fuse3, linux, p-invoke, low-level-api, inode, mount, libfuse3]

# Dependency graph
requires:
  - phase: 91.5-87-61
    provides: IVdeMountProvider, IMountHandle, MountOptions, MountInfo, VdeFilesystemAdapter
provides:
  - Linux FUSE3 low-level mount provider (Fuse3LowLevelMountProvider)
  - FUSE3 mount handle with async disposal (Fuse3MountHandle)
  - Complete P/Invoke bindings for libfuse3.so.3 (Fuse3Native)
affects: [87-64-winfsp-provider, vde-mount-cli, mount-registry]

# Tech tracking
tech-stack:
  added: [libfuse3, p-invoke, fuse_lowlevel_ops]
  patterns: [callback-delegate-pinning, inode-to-stat-mapping, exception-to-errno-mapping]

key-files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/Mount/Fuse3Native.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Mount/Fuse3LowLevelMountProvider.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Mount/Fuse3MountHandle.cs

key-decisions:
  - "Used UnmanagedFunctionPointer delegates with GCHandle pinning for FUSE callback lifecycle"
  - "Synchronous adapter calls via GetAwaiter().GetResult() in FUSE callbacks (fuse_session_loop_mt provides concurrency)"
  - "Readdirplus via fuse_add_direntry_plus for combined lookup+readdir performance"

patterns-established:
  - "P/Invoke callback pattern: pin delegates with GCHandle, retrieve adapter from fuse_req_userdata"
  - "Exception-to-errno mapping for FUSE providers"
  - "InodeAttributes-to-Stat struct conversion with S_IF* mode bits"

# Metrics
duration: 8min
completed: 2026-03-02
---

# Phase 91.5 Plan 63: FUSE3 Low-Level Mount Provider Summary

**Linux FUSE3 low-level mount provider with P/Invoke to libfuse3, inode-based callbacks, multi-threaded dispatch, and full VdeFilesystemAdapter delegation**

## Performance

- **Duration:** 8 min
- **Started:** 2026-03-02T15:13:42Z
- **Completed:** 2026-03-02T15:21:55Z
- **Tasks:** 2
- **Files created:** 3

## Accomplishments
- Complete P/Invoke bindings for libfuse3.so.3 low-level API (structs, delegates, DllImport declarations)
- Fuse3LowLevelMountProvider with 23 callback handlers all delegating to VdeFilesystemAdapter
- Multi-threaded FUSE event loop with configurable concurrency and proper async teardown
- Runtime library detection via NativeLibrary.TryLoad

## Task Commits

Each task was committed atomically:

1. **Task 1: Fuse3Native P/Invoke bindings** - `0d609a62` (feat)
2. **Task 2: Fuse3LowLevelMountProvider and Fuse3MountHandle** - `b9497ae6` (feat)

## Files Created/Modified
- `DataWarehouse.SDK/VirtualDiskEngine/Mount/Fuse3Native.cs` - P/Invoke declarations for libfuse3 low-level API: struct layouts (Stat, FuseEntryParam, FuseFileInfo, Statvfs, FuseLowlevelOps, FuseArgs, FuseLoopConfig), 23 callback delegate types, session lifecycle functions, reply functions, errno constants, mode bits
- `DataWarehouse.SDK/VirtualDiskEngine/Mount/Fuse3LowLevelMountProvider.cs` - IVdeMountProvider implementation for Linux: MountAsync (arg building, session creation, mt loop), UnmountAsync, ListMountsAsync, 23 callback handlers (lookup, getattr, setattr, readlink, mkdir, unlink, rmdir, symlink, rename, open, read, write, flush, release, opendir, readdir, releasedir, statfs, create, setxattr, getxattr, listxattr), stat conversion, error mapping
- `DataWarehouse.SDK/VirtualDiskEngine/Mount/Fuse3MountHandle.cs` - IMountHandle implementation: session lifecycle, background thread join, GCHandle cleanup, adapter sync on disposal

## Decisions Made
- Used `UnmanagedFunctionPointer` delegates pinned with `GCHandle.Alloc` rather than `UnmanagedCallersOnly` static methods, because the delegates need to capture the adapter reference via the FUSE userdata mechanism (fuse_req_userdata)
- FUSE callbacks call adapter async methods synchronously via `GetAwaiter().GetResult()` -- this is safe because `fuse_session_loop_mt` runs callbacks on its own thread pool, so blocking does not deadlock
- Used `fuse_add_direntry_plus` (readdirplus) for readdir to combine lookup+readdir into a single kernel round-trip

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
None

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- FUSE3 provider ready for WinFsp counterpart (87-64) and mount CLI integration
- All three files follow the same IVdeMountProvider/VdeFilesystemAdapter delegation pattern established in 87-61

## Self-Check: PASSED

All 3 created files verified on disk. Both task commits (0d609a62, b9497ae6) verified in git log.

---
*Phase: 91.5-vde-v2.1-format-completion*
*Completed: 2026-03-02*
