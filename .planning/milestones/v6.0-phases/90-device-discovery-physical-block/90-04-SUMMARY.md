---
phase: 90-device-discovery-physical-block
plan: 04
subsystem: storage
tags: [topology, numa, nvme, device-management, scheduling, physical-device]

# Dependency graph
requires:
  - phase: 90-01
    provides: "PhysicalDeviceInfo with ControllerPath, NumaNode, NvmeNamespaceId, BusType"
provides:
  - "TopologyNode, DeviceTopologyTree, NumaAffinityInfo SDK types"
  - "DeviceTopologyMapper: controller->bus->device tree builder with NVMe namespace grouping"
  - "NumaAwareIoScheduler: cross-NUMA detection and best-effort affinitized I/O"
affects: [90-05, 90-06, 91-compound-block-device]

# Tech tracking
tech-stack:
  added: []
  patterns: [topology-tree, numa-affinity, round-robin-core-selection, platform-guarded-pInvoke]

key-files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/PhysicalDevice/DeviceTopology.cs
    - Plugins/DataWarehouse.Plugins.UltimateFilesystem/DeviceManagement/DeviceTopologyMapper.cs
    - Plugins/DataWarehouse.Plugins.UltimateFilesystem/DeviceManagement/NumaAwareIoScheduler.cs
  modified: []

key-decisions:
  - "SupportedOSPlatform attribute for Windows-only ProcessorAffinity P/Invoke; Linux affinity deferred"
  - "NVMe namespace grouping: multiple namespaces on same controller create NvmeSubsystem intermediate node"
  - "BoundedDictionary(500) for device->NUMA cache in scheduler; Linux /sys/devices/system/node for CPU core discovery"

patterns-established:
  - "Platform-guarded thread affinity: RuntimeInformation.IsOSPlatform check + [SupportedOSPlatform] annotation for CA1416 compliance"
  - "Topology tree DFS traversal pattern for device lookup, sibling discovery, and NUMA grouping"

# Metrics
duration: 5min
completed: 2026-02-24
---

# Phase 90 Plan 04: Device Topology and NUMA-Aware I/O Summary

**Controller->bus->device topology tree with NVMe namespace grouping and NUMA-aware I/O scheduling hints**

## Performance

- **Duration:** 5 min
- **Started:** 2026-02-24T00:22:32Z
- **Completed:** 2026-02-24T00:27:31Z
- **Tasks:** 2
- **Files created:** 3

## Accomplishments
- SDK topology types: TopologyNodeType enum (6 values), TopologyNode record, DeviceTopologyTree, NumaAffinityInfo
- DeviceTopologyMapper builds full tree from PhysicalDeviceInfo list with NVMe namespace grouping under NvmeSubsystem nodes
- Topology queries: GetDevicesByController, FindDeviceNode, GetSiblingDevices, GetNumaAffinity
- NumaAwareIoScheduler detects cross-NUMA I/O, provides scheduling hints, and best-effort affinitized execution
- Linux cpulist parser for /sys/devices/system/node NUMA core discovery

## Task Commits

Each task was committed atomically:

1. **Task 1: DeviceTopology SDK types** - `ade0e32b` (feat)
2. **Task 2: DeviceTopologyMapper and NumaAwareIoScheduler** - `44ad3e0f` (feat)

## Files Created/Modified
- `DataWarehouse.SDK/VirtualDiskEngine/PhysicalDevice/DeviceTopology.cs` - TopologyNodeType, TopologyNode, DeviceTopologyTree, NumaAffinityInfo SDK types
- `Plugins/DataWarehouse.Plugins.UltimateFilesystem/DeviceManagement/DeviceTopologyMapper.cs` - Builds and queries controller->bus->device topology tree with NVMe namespace grouping
- `Plugins/DataWarehouse.Plugins.UltimateFilesystem/DeviceManagement/NumaAwareIoScheduler.cs` - NUMA-aware I/O scheduling with cross-NUMA detection and best-effort thread affinity

## Decisions Made
- Used `[SupportedOSPlatform("windows")]` annotation to resolve CA1416 for `ProcessThread.ProcessorAffinity`; Linux sched_setaffinity deferred to future enhancement
- NVMe namespace grouping triggers when multiple devices share same ControllerPath but have different NvmeNamespaceId values
- BoundedDictionary(500) LRU cache for device-to-NUMA mapping in scheduler to avoid repeated topology tree traversals
- Linux CPU core discovery reads /sys/devices/system/node/node{N}/cpulist; Windows returns empty cores list (P/Invoke complexity deferred)
- Round-robin core selection within target NUMA node for load distribution

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] CA1416 platform compatibility for ProcessThread.ProcessorAffinity**
- **Found during:** Task 2 (NumaAwareIoScheduler)
- **Issue:** `ProcessThread.ProcessorAffinity` is Windows-only but called unconditionally, causing CA1416 build error
- **Fix:** Extracted Windows-specific code into `TrySetWindowsThreadAffinity` method with `[SupportedOSPlatform("windows")]` attribute, guarded by `RuntimeInformation.IsOSPlatform(OSPlatform.Windows)` check
- **Files modified:** NumaAwareIoScheduler.cs
- **Verification:** Build succeeds with zero errors
- **Committed in:** 44ad3e0f (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (1 bug)
**Impact on plan:** Essential for cross-platform build correctness. No scope creep.

## Issues Encountered
None

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Topology tree and NUMA scheduling ready for CompoundBlockDevice (Phase 91) to use for failure domain isolation and cross-NUMA traffic minimization
- DeviceTopologyMapper.GetNumaAffinity provides the input data NumaAwareIoScheduler needs

---
*Phase: 90-device-discovery-physical-block*
*Completed: 2026-02-24*
