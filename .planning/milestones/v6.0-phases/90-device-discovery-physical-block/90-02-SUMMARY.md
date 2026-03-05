---
phase: 90-device-discovery-physical-block
plan: 02
subsystem: storage
tags: [smart, ewma, health-monitoring, failure-prediction, device-management, sysfs, wmi]

requires:
  - phase: 90-01
    provides: "PhysicalDeviceInfo, PhysicalDeviceHealth records, DeviceDiscoveryService, BusType/MediaType enums"
provides:
  - "SmartMonitor: cross-platform SMART attribute reader (Linux sysfs + Windows WMI)"
  - "FailurePredictionEngine: EWMA-based failure prediction with risk levels"
  - "PhysicalDeviceManager: device lifecycle orchestration with health polling and discovery"
  - "ManagedDevice record with status tracking (Online/Degraded/Failing/Offline/Removed)"
  - "DeviceManagerConfig and FailurePredictionConfig for user configurability"
affects: [90-03-device-pool, 90-04-topology, 90-05-hot-swap]

tech-stack:
  added: []
  patterns: [ewma-smoothing, periodic-timer-polling, action-callbacks, bounded-dictionary-state]

key-files:
  created:
    - Plugins/DataWarehouse.Plugins.UltimateFilesystem/DeviceManagement/SmartMonitor.cs
    - Plugins/DataWarehouse.Plugins.UltimateFilesystem/DeviceManagement/FailurePredictionEngine.cs
    - Plugins/DataWarehouse.Plugins.UltimateFilesystem/DeviceManagement/PhysicalDeviceManager.cs
  modified: []

key-decisions:
  - "Action callbacks (not C# events) for health/prediction/status/discovery notifications"
  - "EWMA alpha=0.3 default with configurable thresholds for temperature, wear, error rate"
  - "ATA SMART 12-byte attribute parsing for standard IDs (5,9,187,194,197,198,241,242)"
  - "PeriodicTimer for both health (5min) and discovery (30min) loops"
  - "BoundedDictionary<string, ManagedDevice>(500) for managed device state with LRU eviction"

patterns-established:
  - "EWMA smoothing: alpha * current + (1-alpha) * previous for time-series prediction"
  - "Device status machine: Online/Degraded/Failing/Offline/Removed with callback notifications"
  - "Best-effort sysfs reads: all file reads wrapped in try-catch returning safe defaults"

duration: 5min
completed: 2026-02-24
---

# Phase 90 Plan 02: Physical Device Health Monitoring & Failure Prediction Summary

**EWMA-based device health monitoring with SmartMonitor (Linux sysfs + Windows WMI), FailurePredictionEngine (configurable risk thresholds), and PhysicalDeviceManager lifecycle orchestrator**

## Performance

- **Duration:** 5 min
- **Started:** 2026-02-24T00:22:28Z
- **Completed:** 2026-02-24T00:27:24Z
- **Tasks:** 2
- **Files modified:** 3

## Accomplishments
- SmartMonitor reads SMART attributes from Linux (NVMe via sysfs/hwmon, SATA via hwmon/drivetemp) and Windows (WMI MSStorageDriver_FailurePredictStatus/Data with ATA attribute parsing)
- FailurePredictionEngine maintains per-device EWMA state for temperature, error rate, and wear rate with configurable thresholds producing None/Low/Medium/High/Critical risk levels
- PhysicalDeviceManager orchestrates discovery + health polling via PeriodicTimer, with status transitions (Online/Degraded/Failing/Offline/Removed) and Action callbacks for downstream consumers

## Task Commits

Each task was committed atomically:

1. **Task 1: SmartMonitor and FailurePredictionEngine** - `abc63d08` (feat)
2. **Task 2: PhysicalDeviceManager** - `257aa65a` (feat)

## Files Created/Modified
- `Plugins/DataWarehouse.Plugins.UltimateFilesystem/DeviceManagement/SmartMonitor.cs` - Platform-specific SMART reader with Linux sysfs/hwmon and Windows WMI/ATA parsing
- `Plugins/DataWarehouse.Plugins.UltimateFilesystem/DeviceManagement/FailurePredictionEngine.cs` - EWMA engine with FailurePrediction/FailurePredictionConfig/DeviceEwmaState records
- `Plugins/DataWarehouse.Plugins.UltimateFilesystem/DeviceManagement/PhysicalDeviceManager.cs` - Device lifecycle orchestrator with ManagedDevice/DeviceManagerConfig/DeviceStatus

## Decisions Made
- Action callbacks instead of C# events to avoid subscription leak complexity in device management lifecycle
- ATA SMART attribute parsing follows standard 12-byte format (ID + flags + current + worst + 6-byte raw) for attributes 1,5,9,12,187,188,194,197,198,199,241,242
- Risk level evaluation: Critical at 1.5x threshold, High at threshold, Medium at 0.8x, Low for upward trends above 0.5x
- Estimated time-to-failure extrapolated from wear rate to 100% or from error rate trend to 10x current count
- PeriodicTimer chosen over System.Threading.Timer for clean async/await integration with CancellationToken support

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed nullable reference type mismatch in GetDeviceAsync**
- **Found during:** Task 2 (PhysicalDeviceManager)
- **Issue:** CS8619: `Task<ManagedDevice>` doesn't match `Task<ManagedDevice?>` return type
- **Fix:** Used explicit `Task.FromResult<ManagedDevice?>(device)` cast
- **Files modified:** PhysicalDeviceManager.cs
- **Verification:** Build passes with zero errors
- **Committed in:** 257aa65a (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (1 bug)
**Impact on plan:** Trivial nullability fix required for correct compilation. No scope creep.

## Issues Encountered
None

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- PhysicalDeviceManager ready for DevicePoolManager (90-03) to consume via GetDevicesAsync and status callbacks
- OnStatusChange/OnFailurePrediction callbacks ready for hot-swap logic (90-05)
- SmartMonitor and FailurePredictionEngine injectable into any device management consumer

---
*Phase: 90-device-discovery-physical-block*
*Completed: 2026-02-24*
