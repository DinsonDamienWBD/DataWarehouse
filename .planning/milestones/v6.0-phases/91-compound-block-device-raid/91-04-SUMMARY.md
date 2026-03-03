---
phase: "91"
plan: "04"
subsystem: "VDE PhysicalDevice / UltimateRAID DeviceLevel"
tags: [hot-spare, raid, device-management, free-extents, rebuild]
dependency-graph:
  requires: [91-01, 91-02, 91-03]
  provides: [HotSpareManager, DeviceAwareFreeExtent, DeviceLevelHotSpareIntegration]
  affects: [CompoundBlockDevice, IDeviceRaidStrategy]
tech-stack:
  added: []
  patterns: [PeriodicTimer health monitoring, Progress<T> rebuild tracking, sealed records for policy/status]
key-files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/PhysicalDevice/HotSpareManager.cs
    - DataWarehouse.SDK/VirtualDiskEngine/PhysicalDevice/DeviceAwareFreeExtent.cs
    - Plugins/DataWarehouse.Plugins.UltimateRAID/Strategies/DeviceLevel/DeviceLevelHotSpareIntegration.cs
  modified: []
decisions:
  - "RebuildPriority reuses existing SDK enum from RaidConstants (Low/Medium/High/Critical) rather than defining a new enum"
  - "HotSpareManager uses single-rebuild-at-a-time constraint with InvalidOperationException for concurrent attempts"
  - "DeviceLevelHotSpareIntegration is internal to the plugin, not exposed via SDK"
metrics:
  duration: "3m 20s"
  completed: "2026-03-02"
  tasks: 3
  files-created: 3
  files-modified: 0
---

# Phase 91 Plan 04: Hot Spare Management & Device-Aware Free Extents Summary

Hot spare failover with HotSpareManager (policy-driven allocation, background rebuild with progress/ETA tracking) plus DeviceAwareFreeExtent structs for device-group-aware extent allocation in RAID arrays.

## What Was Built

### HotSpareManager (SDK)
- **HotSparePolicy** sealed record: MaxSpares, AutoRebuild, RebuildPriority, MaxRebuildBandwidthMBps with validation
- **RebuildState** enum: Idle/Rebuilding/Completed/Failed
- **RebuildStatus** sealed record: State, FailedDeviceIndex, Progress (0.0-1.0), EstimatedRemaining with static Idle sentinel
- **HotSpareManager** sealed class:
  - Thread-safe spare pool with lock-protected allocation (AllocateSpare returns first online spare)
  - RebuildAsync: allocates spare, delegates to IDeviceRaidStrategy.RebuildDeviceAsync, tracks progress with ETA calculation, fires OnDeviceReplaced event on success
  - Returns spare to pool on failure/cancellation
  - Single-rebuild constraint (throws InvalidOperationException for concurrent rebuilds)
  - Background priority support via Task.Yield() for low-priority rebuilds

### DeviceAwareFreeExtent (SDK)
- **DeviceGroupId** readonly struct: wraps int device index, IEquatable/IComparable, implicit int conversions
- **DeviceAwareFreeExtent** readonly struct: GroupId + StartBlock + BlockCount, sorted by (GroupId, StartBlock), ToString returns "DevGroup{N}:[start..end)"

### DeviceLevelHotSpareIntegration (Plugin)
- PeriodicTimer-based health monitor (configurable interval, default 5s)
- Detects online-to-offline transitions per device
- Auto-triggers RebuildAsync when policy allows and spares available
- Events: OnHealthChanged, OnDeviceFailureDetected, OnAutoRebuildStarted
- Graceful cancellation handling

## Verification

- `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj --no-restore` -- 0 errors, 0 warnings
- `dotnet build Plugins/DataWarehouse.Plugins.UltimateRAID/DataWarehouse.Plugins.UltimateRAID.csproj --no-restore` -- 0 errors, 0 warnings

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed XML cref to cross-project type**
- **Found during:** Task 1 (HotSpareManager)
- **Issue:** XML doc referenced `DeviceLevelHotSpareIntegration` which lives in the plugin, not the SDK, causing CS1574
- **Fix:** Changed cref to plain text description "a device health monitor"
- **Commit:** 0a4a5efe

## Commits

| Hash | Message |
|------|---------|
| 0a4a5efe | feat(91-04): add hot spare management and device-aware free extents |

## Self-Check: PASSED

- [x] HotSpareManager.cs exists
- [x] DeviceAwareFreeExtent.cs exists
- [x] DeviceLevelHotSpareIntegration.cs exists
- [x] Commit 0a4a5efe found in git log
- [x] SDK build: 0 errors, 0 warnings
- [x] UltimateRAID build: 0 errors, 0 warnings
