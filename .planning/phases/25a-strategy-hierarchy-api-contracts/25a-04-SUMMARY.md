---
phase: 25a-strategy-hierarchy-api-contracts
plan: 04
subsystem: sdk
tags: [SdkCompatibility, null-object, NullMessageBus, version-tracking, API-03, API-04]

requires:
  - phase: 25a-01
    provides: "IStrategy and StrategyBase root types"
provides:
  - "SdkCompatibilityAttribute for version tracking on public types"
  - "NullMessageBus singleton (IMessageBus null-object)"
  - "NullLoggerProvider delegating to framework NullLogger"
affects: [25a-03, 25a-05, 25b-strategy-migration, 27-api-hardening]

tech-stack:
  added: [Microsoft.Extensions.Logging.Abstractions]
  patterns: [null-object-pattern, singleton-pattern, version-attribute]

key-files:
  created:
    - DataWarehouse.SDK/Contracts/SdkCompatibilityAttribute.cs
    - DataWarehouse.SDK/Contracts/NullObjects.cs
  modified: []

key-decisions:
  - "SdkCompatibilityAttribute self-applies [SdkCompatibility] to itself"
  - "NullLoggerProvider delegates to framework NullLogger.Instance instead of custom ILogger impl"
  - "NullMessageBus.Instance is sealed singleton, Send returns error MessageResponse"

patterns-established:
  - "Version tracking: [SdkCompatibility('2.0.0')] on all new public types"
  - "Null-object: NullMessageBus.Instance for default message bus"
  - "Framework delegation: NullLoggerProvider uses Microsoft.Extensions.Logging.Abstractions"

duration: 10min
completed: 2026-02-14
---

# Phase 25a Plan 04: SdkCompatibility and Null-Objects Summary

**SdkCompatibilityAttribute for version tracking and NullMessageBus/NullLoggerProvider null-objects for safe defaults**

## Performance

- **Duration:** ~10 min
- **Tasks:** 2
- **Files created:** 2

## Accomplishments
- Created SdkCompatibilityAttribute with init-only DeprecatedVersion, ReplacementType, Notes properties
- Created NullMessageBus sealed singleton implementing IMessageBus (publish=no-op, send=error)
- Created NullLoggerProvider static class delegating to framework NullLogger.Instance
- All new types self-apply [SdkCompatibility("2.0.0")]

## Task Commits

1. **Task 1: Create SdkCompatibility and NullObjects** - `c897908` (feat)

## Files Created/Modified
- `DataWarehouse.SDK/Contracts/SdkCompatibilityAttribute.cs` - Version tracking attribute with AttributeUsage validation
- `DataWarehouse.SDK/Contracts/NullObjects.cs` - NullMessageBus singleton and NullLoggerProvider delegation

## Decisions Made
- Used framework NullLogger instead of custom ILogger implementation (avoids .NET 10 preview ILogger interface changes)
- NullMessageBus.Send returns error response rather than throwing (fail-safe pattern)
- AttributeUsage: Class/Interface/Struct/Enum/Delegate, Inherited=false, AllowMultiple=false

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed NullLogger .NET 10 compatibility**
- **Found during:** Task 1
- **Issue:** Custom NullLogger<T> class failed CS0535 because .NET 10 preview changed ILogger interface
- **Fix:** Replaced custom NullLogger/NullLogger<T> with NullLoggerProvider static class delegating to Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance
- **Files modified:** DataWarehouse.SDK/Contracts/NullObjects.cs
- **Verification:** SDK builds with zero errors

---

**Total deviations:** 1 auto-fixed (1 bug)
**Impact on plan:** Essential fix for .NET 10 compatibility. No scope creep.

## Issues Encountered
None

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- SdkCompatibilityAttribute available for all Phase 25a types
- NullMessageBus available for backward-compat layer (Plan 25a-03)

---
*Phase: 25a-strategy-hierarchy-api-contracts*
*Completed: 2026-02-14*
