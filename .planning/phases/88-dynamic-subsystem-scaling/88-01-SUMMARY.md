---
phase: 88-dynamic-subsystem-scaling
plan: 01
subsystem: sdk
tags: [scaling, bounded-cache, backpressure, arc-eviction, lru, ttl]

# Dependency graph
requires: []
provides:
  - "IScalableSubsystem unified scaling contract"
  - "IBackpressureAware with 5 strategies (DropOldest/BlockProducer/ShedLoad/DegradeQuality/Adaptive)"
  - "BoundedCache<TKey,TValue> with LRU/ARC/TTL eviction and IPersistentBackingStore integration"
  - "ScalingLimits, SubsystemScalingPolicy, ScalingHierarchyLevel models"
affects: [88-02 through 88-14, all plugin scaling migrations]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "ARC adaptive replacement cache with ghost list B1/B2 split-point tuning"
    - "BoundedCache auto-sizing from GC.GetGCMemoryInfo().TotalAvailableMemoryBytes"
    - "IPersistentBackingStore lazy-load on cache miss, write-through on eviction"

key-files:
  created:
    - DataWarehouse.SDK/Contracts/Scaling/IScalableSubsystem.cs
    - DataWarehouse.SDK/Contracts/Scaling/IBackpressureAware.cs
    - DataWarehouse.SDK/Contracts/Scaling/BoundedCache.cs
    - DataWarehouse.SDK/Contracts/Scaling/ScalingModels.cs

key-decisions:
  - "BoundedCache uses ReaderWriterLockSlim (same pattern as BoundedDictionary) for thread safety"
  - "ARC ghost lists capped at MaxEntries to bound memory; ghost entries are key-only (no values)"
  - "Auto-size heuristic uses 256 bytes/entry estimate; RamPercentage clamped to [0.001, 0.5]"
  - "TTL cleanup timer interval = max(TTL/4, 1s) for responsive but not excessive cleanup"
  - "CacheStatistics reused from ICacheableStorage.cs for consistency"

patterns-established:
  - "Contracts.Scaling namespace for all scaling-related SDK types"
  - "record types for immutable event args and configuration models"
  - "BoundedCacheOptions<TKey,TValue> options pattern for cache configuration"

# Metrics
duration: 4min
completed: 2026-02-23
---

# Phase 88 Plan 01: SDK Scaling Contract Foundation Summary

**IScalableSubsystem + IBackpressureAware contracts with BoundedCache LRU/ARC/TTL eviction and IPersistentBackingStore write-through**

## Performance

- **Duration:** 4 min
- **Started:** 2026-02-23T22:10:31Z
- **Completed:** 2026-02-23T22:14:39Z
- **Tasks:** 2
- **Files created:** 4

## Accomplishments
- Created IScalableSubsystem as the unified scaling contract for all 60+ plugins
- Created IBackpressureAware with 5 strategies and 4 state levels for coordinated load management
- Built BoundedCache<TKey,TValue> with 3 eviction modes (LRU, ARC with ghost-list adaptation, TTL with background cleanup)
- Integrated IPersistentBackingStore for lazy-load on cache miss and write-through on eviction
- All types have [SdkCompatibility("6.0.0")] and full XML documentation

## Task Commits

Each task was committed atomically:

1. **Task 1: Create IScalableSubsystem, IBackpressureAware, and ScalingModels** - `cd7ee590` (feat)
2. **Task 2: Create BoundedCache with LRU/ARC/TTL eviction** - `4c806e51` (feat)

## Files Created/Modified
- `DataWarehouse.SDK/Contracts/Scaling/IScalableSubsystem.cs` - Unified scaling interface with metrics, limit reconfiguration, backpressure state
- `DataWarehouse.SDK/Contracts/Scaling/IBackpressureAware.cs` - Backpressure contract with strategy enum, state enum, event args, and context record
- `DataWarehouse.SDK/Contracts/Scaling/ScalingModels.cs` - ScalingLimits, SubsystemScalingPolicy, ScalingHierarchyLevel, ScalingReconfiguredEventArgs
- `DataWarehouse.SDK/Contracts/Scaling/BoundedCache.cs` - Generic bounded cache with LRU/ARC/TTL, backing store integration, auto-sizing

## Decisions Made
- BoundedCache uses ReaderWriterLockSlim (same proven pattern as BoundedDictionary) rather than ConcurrentDictionary for fine-grained read/write control
- ARC ghost lists (B1/B2) store keys only (no values) and are capped at MaxEntries to bound ghost list memory
- Auto-sizing from RAM uses a conservative 256 bytes/entry heuristic; RamPercentage is clamped to [0.001, 0.5] to prevent misconfiguration
- TTL cleanup timer fires at max(TTL/4, 1 second) for responsive expiry without excessive timer overhead
- Reused existing CacheStatistics from ICacheableStorage.cs rather than creating a duplicate type

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
None

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- All 4 SDK scaling contract files are in place and compile cleanly
- Plans 88-02 through 88-14 can now consume IScalableSubsystem, IBackpressureAware, BoundedCache, and ScalingModels
- Existing BoundedDictionary untouched for backward compatibility

## Self-Check: PASSED
- FOUND: DataWarehouse.SDK/Contracts/Scaling/IScalableSubsystem.cs
- FOUND: DataWarehouse.SDK/Contracts/Scaling/IBackpressureAware.cs
- FOUND: DataWarehouse.SDK/Contracts/Scaling/BoundedCache.cs
- FOUND: DataWarehouse.SDK/Contracts/Scaling/ScalingModels.cs
- FOUND: cd7ee590 (Task 1 commit)
- FOUND: 4c806e51 (Task 2 commit)

---
*Phase: 88-dynamic-subsystem-scaling*
*Completed: 2026-02-23*
