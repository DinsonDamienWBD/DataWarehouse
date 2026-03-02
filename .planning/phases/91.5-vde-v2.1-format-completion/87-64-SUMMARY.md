---
phase: 91.5-vde-v2.1-format-completion
plan: 64
subsystem: vde-mount
tags: [macfuse, fuse3, macos, spotlight, pinvoke, native-interop]

requires:
  - phase: 87-61
    provides: IVdeMountProvider, IMountHandle, VdeFilesystemAdapter, MountOptions
provides:
  - MacFuseMountProvider for macOS VDE mount via macFUSE 4.x
  - MacFuseMountHandle for mount session lifecycle
  - MacFuseNative P/Invoke bindings for libfuse-t and kext
  - SpotlightMetadataHelper for macOS search integration
affects: [92-vde-decorator-chain, mount-orchestrator]

tech-stack:
  added: [macFUSE 4.x libfuse3-compatible API, binary plist encoding]
  patterns: [NativeLibrary dynamic loading with dual-path fallback, delegate-based P/Invoke for runtime library resolution, Spotlight xattr metadata provider]

key-files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/Mount/MacFuseNative.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Mount/MacFuseMountHandle.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Mount/MacFuseMountProvider.cs

key-decisions:
  - "Used NativeLibrary + delegate-based P/Invoke instead of DllImport to support dual library paths (libfuse-t.dylib and /usr/local/lib/libfuse.dylib)"
  - "Spotlight metadata encoded as binary plist (bplist00) for native macOS consumption"
  - "Core Data epoch (2001-01-01) used for NSDate encoding in Spotlight xattr"

patterns-established:
  - "Dynamic native library resolution: NativeLibrary.TryLoad with multiple paths, Lazy<IntPtr> caching, delegate-based function dispatch"
  - "Spotlight xattr provider: intercept com.apple.metadata:* keys, return binary plist encoded metadata"
  - "macOS stat struct: separate struct type with BSD-specific fields (st_birthtimespec, st_flags, st_gen)"

duration: 7min
completed: 2026-03-03
---

# Phase 91.5 Plan 64: macOS macFUSE Mount Provider Summary

**macFUSE 4.x mount provider with Spotlight metadata integration, dual library loading (libfuse-t + kext), and full low-level FUSE ops delegation to VdeFilesystemAdapter**

## Performance

- **Duration:** 7 min
- **Started:** 2026-03-02T15:24:23Z
- **Completed:** 2026-03-02T15:31:05Z
- **Tasks:** 1
- **Files modified:** 3

## Accomplishments
- MacFuseNative with dual-path library loading (libfuse-t preferred, kext fallback) and macOS-specific stat struct
- MacFuseMountProvider with full FUSE3 low-level callback suite delegating to VdeFilesystemAdapter
- SpotlightMetadataHelper encoding kMDItemFSName/FSSize/ContentCreationDate/ContentModificationDate/ContentType/ContentTypeTree as binary plist
- macOS Finder integration via volname, local, noappledouble, noapplexattr mount options
- macOS errno values (ENOTEMPTY=66, ENOATTR=93) replacing Linux equivalents

## Task Commits

Each task was committed atomically:

1. **Task 1: MacFuseNative, MacFuseMountProvider, and MacFuseMountHandle** - `01163a2a` (feat)

## Files Created/Modified
- `DataWarehouse.SDK/VirtualDiskEngine/Mount/MacFuseNative.cs` - P/Invoke bindings for macFUSE libfuse3-compatible API with dual library loading, macOS stat struct, macOS errno constants, delegate-based function dispatch
- `DataWarehouse.SDK/VirtualDiskEngine/Mount/MacFuseMountHandle.cs` - Active mount session handle with clean teardown (exit loop, unmount, destroy, sync adapter, free GCHandles)
- `DataWarehouse.SDK/VirtualDiskEngine/Mount/MacFuseMountProvider.cs` - Full macFUSE mount provider with 23 FUSE callbacks, Spotlight metadata provider, Finder integration options, macOS error mapping

## Decisions Made
- Used NativeLibrary + delegate-based P/Invoke instead of DllImport to handle the two possible macFUSE library paths at runtime
- Encoded Spotlight metadata as binary plist (bplist00 format) for direct macOS consumption without requiring an mdimporter plugin
- Used Core Data absolute reference date (2001-01-01) for NSDate plist encoding in Spotlight date fields
- Set clone_fd=0 for macOS (not supported on macOS unlike Linux)
- Validated mount point emptiness (macOS requirement) as additional check beyond Linux provider

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
- Pre-existing build errors in SpdkBlockDevice.cs (4 errors) unrelated to new files. New macFUSE files compile cleanly.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness
- All three platform mount providers now complete (WinFsp, FUSE3, macFUSE)
- Ready for VDE mount orchestrator or decorator chain integration in Phase 92

---
*Phase: 91.5-vde-v2.1-format-completion*
*Completed: 2026-03-03*
