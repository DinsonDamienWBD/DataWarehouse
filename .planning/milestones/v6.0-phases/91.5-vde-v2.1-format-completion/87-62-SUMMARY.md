---
phase: 91.5-vde-v2.1-format-completion
plan: 62
subsystem: vde
tags: [winfsp, mount, windows, pinvoke, filesystem, ntstatus]

requires:
  - phase: 91.5-vde-v2.1-format-completion/87-61
    provides: "IVdeMountProvider, IMountHandle, MountOptions, MountInfo, VdeFilesystemAdapter"
provides:
  - "WinFspMountProvider: Windows drive letter mounting via WinFsp P/Invoke"
  - "WinFspMountHandle: Active mount session with clean unmount lifecycle"
  - "WinFspNative: Internal P/Invoke bindings for winfsp-x64.dll"
  - "NtStatus error mapping from .NET exceptions to NTSTATUS codes"
affects: [87-63-fuse3-provider, 87-64-macfuse-provider, vde-mount-integration]

tech-stack:
  added: [WinFsp P/Invoke, NativeLibrary]
  patterns: [dynamic-dll-loading, callback-delegation, mount-registry]

key-files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/Mount/WinFspMountProvider.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Mount/WinFspMountHandle.cs

key-decisions:
  - "P/Invoke over WinFsp.Net NuGet to keep SDK dependency-free"
  - "Runtime WinFsp detection via registry + common paths + DLL probe (triple fallback)"
  - "Adapter factory injection to decouple provider from VDE internals"
  - "GC.KeepAlive for all callback delegates to prevent premature collection"

patterns-established:
  - "Windows-only mount provider pattern: SupportedOSPlatform + Lazy detection + ConcurrentDictionary registry"
  - "WinFsp callback delegation: all storage logic in VdeFilesystemAdapter, provider handles only OS binding"
  - "NTSTATUS error mapping via pattern-matched switch expression on exception types"

duration: 7min
completed: 2026-03-02
---

# Phase 91.5 Plan 62: WinFsp Mount Provider Summary

**Windows WinFsp mount provider with P/Invoke bindings delegating all filesystem callbacks to VdeFilesystemAdapter for native drive letter mounting**

## Performance

- **Duration:** 7 min
- **Started:** 2026-03-02T15:13:54Z
- **Completed:** 2026-03-02T15:21:14Z
- **Tasks:** 1
- **Files modified:** 2

## Accomplishments
- WinFspMountProvider implementing IVdeMountProvider with full WinFsp P/Invoke bindings (no NuGet dependency)
- 15 filesystem callbacks wired to VdeFilesystemAdapter: GetVolumeInfo, Open, Close, Read, Write, Flush, GetFileInfo, Create, Cleanup, Rename, SetBasicInfo, SetFileSize, ReadDirectory, Overwrite, GetSecurityByName
- Runtime WinFsp detection via triple fallback: registry key, common install path, dynamic DLL load
- Complete NTSTATUS error mapping for 13 exception types including disk full, directory not empty, sharing violation
- ConcurrentDictionary-based mount registry with ListMountsAsync for active mount enumeration
- Clean unmount lifecycle: stop dispatcher, flush adapter, delete filesystem object, deregister

## Task Commits

Each task was committed atomically:

1. **Task 1: WinFspMountProvider and WinFspMountHandle** - `ca37a7c3` (feat)

## Files Created/Modified
- `DataWarehouse.SDK/VirtualDiskEngine/Mount/WinFspMountProvider.cs` - Windows WinFsp mount provider with P/Invoke bindings, callback delegation, error mapping, path resolution, and mount lifecycle management
- `DataWarehouse.SDK/VirtualDiskEngine/Mount/WinFspMountHandle.cs` - Active mount handle implementing IMountHandle with clean unmount via DisposeAsync

## Decisions Made
- Used P/Invoke to winfsp-x64.dll instead of WinFsp.Net NuGet package to keep the SDK dependency-free
- Runtime WinFsp detection uses triple fallback: registry (HKLM\SOFTWARE\WinFsp), common Program Files path, dynamic NativeLibrary.Load
- Adapter factory delegate injected into constructor to decouple provider from VDE file opening internals
- GC.KeepAlive used on all callback delegates to prevent garbage collection while native code holds function pointers

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed InodeType enum member names**
- **Found during:** Task 1
- **Issue:** Plan referenced InodeType.RegularFile and InodeType.Symlink but actual enum uses InodeType.File and InodeType.SymLink
- **Fix:** Updated to correct enum member names
- **Files modified:** WinFspMountProvider.cs
- **Committed in:** ca37a7c3

**2. [Rule 3 - Blocking] Fixed CA1832 analyzer errors for range indexers**
- **Found during:** Task 1 (build verification)
- **Issue:** `parts[..^1]` triggers CA1832 (use AsSpan instead of Range-based indexer) which is treated as error
- **Fix:** Changed all occurrences to `parts.AsSpan()[..^1]`
- **Files modified:** WinFspMountProvider.cs
- **Committed in:** ca37a7c3

---

**Total deviations:** 2 auto-fixed (1 bug, 1 blocking)
**Impact on plan:** Both fixes necessary for correctness and compilation. No scope creep.

## Issues Encountered
None - pre-existing SpdkBlockDevice errors (4) are unrelated to this plan.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- WinFsp mount provider complete, ready for FUSE3 (87-63) and macFUSE (87-64) providers
- All three providers will share VdeFilesystemAdapter for storage logic

---
*Phase: 91.5-vde-v2.1-format-completion*
*Completed: 2026-03-02*
