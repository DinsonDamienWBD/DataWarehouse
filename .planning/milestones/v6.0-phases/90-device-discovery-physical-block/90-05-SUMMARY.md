---
phase: 90-device-discovery-physical-block
plan: 05
subsystem: storage
tags: [hot-swap, bare-metal, device-journal, crash-recovery, WAL, CRC32]

requires:
  - phase: 90-02
    provides: PhysicalDeviceManager with discovery/health events
  - phase: 90-03
    provides: DevicePoolManager with ScanForPoolsAsync and pool CRUD
provides:
  - DeviceJournal with intent/commit/rollback pattern for crash-consistent device lifecycle
  - HotSwapManager for graceful device add/remove with automatic rebuild triggers
  - BaremetalBootstrap for OS-free pool initialization from raw devices
affects: [90-06, device-raid, hardware-to-storage-flow]

tech-stack:
  added: []
  patterns:
    - "Write-ahead journal with fixed 256-byte entries and CRC32 integrity"
    - "Circular buffer journal (128 entries, 32KB) on reserved device sectors"
    - "Intent/commit/rollback lifecycle for crash recovery"
    - "Action callbacks for device events (not C# events)"

key-files:
  created:
    - Plugins/DataWarehouse.Plugins.UltimateFilesystem/DeviceManagement/DeviceJournal.cs
    - Plugins/DataWarehouse.Plugins.UltimateFilesystem/DeviceManagement/HotSwapManager.cs
    - Plugins/DataWarehouse.Plugins.UltimateFilesystem/DeviceManagement/BaremetalBootstrap.cs
  modified: []

key-decisions:
  - "Fixed 256-byte journal entries with CRC32 integrity for predictable on-disk layout"
  - "Circular buffer overwrites oldest committed/rolled-back entries when full"
  - "StorageTier using alias to disambiguate SDK.Contracts vs SDK.VirtualDiskEngine.PhysicalDevice"
  - "BoundedDictionary(100) for active rebuild state tracking in HotSwapManager"

patterns-established:
  - "Journal area: blocks 1-8 (32KB on 4K devices) after pool metadata block 0"
  - "Proactive rebuild for Failing devices before complete failure"

duration: 5min
completed: 2026-02-24
---

# Phase 90 Plan 05: Hot-Swap, Bare-Metal Bootstrap & Device Journal Summary

**Write-ahead device journal with CRC32 integrity, hot-swap manager with automatic rebuild triggers, and bare-metal bootstrap for OS-free pool initialization from raw devices**

## Performance

- **Duration:** 5 min
- **Started:** 2026-02-24T00:29:50Z
- **Completed:** 2026-02-24T00:34:53Z
- **Tasks:** 2
- **Files created:** 3

## Accomplishments
- DeviceJournal with fixed 256-byte entries, CRC32 integrity, and intent/commit/rollback lifecycle for crash-consistent pool mutations
- HotSwapManager subscribes to PhysicalDeviceManager events for graceful device add/remove with journal-backed consistency and automatic rebuild triggers
- BaremetalBootstrap discovers devices, scans pool metadata from reserved sectors, recovers interrupted journal operations, and initializes new systems on raw hardware

## Task Commits

Each task was committed atomically:

1. **Task 1: DeviceJournal for crash-consistent lifecycle tracking** - `7295513d` (feat)
2. **Task 2: HotSwapManager and BaremetalBootstrap** - `b4e3f68b` (feat)

## Files Created/Modified
- `Plugins/DataWarehouse.Plugins.UltimateFilesystem/DeviceManagement/DeviceJournal.cs` - Write-ahead journal with 256-byte entries, CRC32 integrity, circular buffer, intent/commit/rollback pattern
- `Plugins/DataWarehouse.Plugins.UltimateFilesystem/DeviceManagement/HotSwapManager.cs` - Hot-swap device management with auto-add, auto-rebuild, degradation detection, BoundedDictionary rebuild state
- `Plugins/DataWarehouse.Plugins.UltimateFilesystem/DeviceManagement/BaremetalBootstrap.cs` - OS-free bootstrap from raw devices with pool scan, journal recovery, and new system initialization

## Decisions Made
- Fixed 256-byte journal entries with CRC32 integrity for predictable on-disk layout and fast sequential scanning
- Circular buffer journal (128 entries) overwrites oldest committed/rolled-back entries when full; intent entries are never overwritten
- StorageTier using alias required to disambiguate between SDK.Contracts.StorageTier and SDK.VirtualDiskEngine.PhysicalDevice.StorageTier (same pattern as DevicePoolManager)
- BoundedDictionary(100) for active rebuild state tracking to prevent unbounded memory growth
- Proactive rebuild trigger for devices in Failing status before complete failure, respecting MaxConcurrentRebuilds limit

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] StorageTier ambiguous reference**
- **Found during:** Task 2 (BaremetalBootstrap)
- **Issue:** StorageTier exists in both DataWarehouse.SDK.Contracts and DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice namespaces
- **Fix:** Added `using StorageTier = DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice.StorageTier;` alias (same pattern used by DevicePoolManager)
- **Files modified:** BaremetalBootstrap.cs
- **Verification:** Build succeeded with zero errors
- **Committed in:** b4e3f68b (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (1 blocking)
**Impact on plan:** Standard namespace disambiguation. No scope creep.

## Issues Encountered
None

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- DeviceJournal, HotSwapManager, and BaremetalBootstrap complete the device lifecycle management stack
- Ready for Phase 90-06 (remaining device management tasks)
- All 3 files integrate with PhysicalDeviceManager (90-02) and DevicePoolManager (90-03)

---
*Phase: 90-device-discovery-physical-block*
*Completed: 2026-02-24*
