---
phase: 28-dead-code-cleanup
plan: 01
subsystem: sdk
tags: [dead-code, refactoring, type-extraction, future-ready, AD-06]

requires:
  - phase: 27-plugin-migration-decoupling
    provides: all plugins migrated to Hierarchy bases
provides:
  - live types extracted from mixed files into InfrastructureContracts.cs and OrchestrationContracts.cs
  - 9 future-ready interface files documented with FUTURE: comments per AD-06
affects: [28-02, 28-03, 28-04]

tech-stack:
  added: []
  patterns: [same-namespace extraction to avoid using changes]

key-files:
  created:
    - DataWarehouse.SDK/Contracts/InfrastructureContracts.cs
    - DataWarehouse.SDK/Contracts/OrchestrationContracts.cs
  modified:
    - DataWarehouse.SDK/Contracts/InfrastructurePluginBases.cs
    - DataWarehouse.SDK/Contracts/OrchestrationInterfaces.cs

key-decisions:
  - "Extract live types to new same-namespace files rather than moving to avoid using directive changes"
  - "Keep ProviderInterfaces.cs as-is (IFeaturePlugin: 5 refs, IStorageProvider: 29 refs)"
  - "9 future-ready interfaces preserved per AD-06 with FUTURE: comment headers"

patterns-established:
  - "Same-namespace extraction: live types moved to new files in same namespace to avoid cascading using changes"

duration: 15min
completed: 2026-02-14
---

# Phase 28 Plan 01: Extract Live Types and Document Future-Ready Interfaces Summary

**Extracted 18 live types from mixed dead/live files into safe locations, documented 9 future-ready interfaces per AD-06**

## Performance

- **Duration:** 15 min
- **Tasks:** 2
- **Files modified:** 13

## Accomplishments
- Extracted 18 live types from InfrastructurePluginBases.cs and OrchestrationInterfaces.cs into new safe files
- InfrastructureContracts.cs: 18 types (CircuitBreakerOpenException, RaidLevel, ComplianceReport, AuthenticationResult, etc.)
- OrchestrationContracts.cs: 16 types (OperationContext, IPluginRegistry, IndexableContent, IWriteFanOutOrchestrator, etc.)
- Documented 9 future-ready interface files with FUTURE: headers (quantum, DNA, brain-reading, neuromorphic, RDMA, etc.)

## Task Commits

1. **Task 1: Extract live types from mixed files** - `ee2b207` (feat)
2. **Task 2: Document future-ready interfaces** - `b3dac97` (docs)

## Files Created/Modified
- `DataWarehouse.SDK/Contracts/InfrastructureContracts.cs` - 18 live types extracted from InfrastructurePluginBases.cs
- `DataWarehouse.SDK/Contracts/OrchestrationContracts.cs` - 16 live types extracted from OrchestrationInterfaces.cs
- `DataWarehouse.SDK/Contracts/InfrastructurePluginBases.cs` - Rewritten with dead types only
- `DataWarehouse.SDK/Contracts/OrchestrationInterfaces.cs` - Rewritten with dead types only
- 9 future-ready interface files - Added FUTURE: comment headers

## Decisions Made
- ProviderInterfaces.cs kept as-is (IFeaturePlugin: 5 refs, IStorageProvider: 29 refs are all live)
- Same-namespace extraction pattern chosen to avoid cascading using directive changes

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
None

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Mixed files now contain only dead types, ready for deletion in Plan 02
- Future-ready interfaces preserved and documented for AD-06 compliance

---
*Phase: 28-dead-code-cleanup*
*Completed: 2026-02-14*
