---
phase: 55-universal-tags
plan: 01
subsystem: sdk
tags: [tags, type-system, discriminated-union, acl, provenance, versioning]

requires: []
provides:
  - "TagValue discriminated union (10 concrete types: String, Color, Object, Pointer, Link, Paragraph, Number, List, Tree, Bool)"
  - "TagKey qualified namespace:name key with Parse/TryParse"
  - "Tag record with Value, Source, Acl, Version, timestamps, SchemaId"
  - "TagSourceInfo provenance tracking (User, Plugin, AI, System)"
  - "TagAcl per-tag access control with entries and default fallback"
  - "TagCollection immutable indexed collection with namespace/source queries"
  - "TagCollectionBuilder fluent mutable builder"
affects: [55-02, 55-03, 55-04, 55-05, 55-06, 55-07, 55-08, 55-09, 55-10, 55-11]

tech-stack:
  added: []
  patterns:
    - "Discriminated union via sealed record hierarchy with Kind enum discriminator"
    - "Static factory methods on abstract base (TagValue.String(), TagValue.Number())"
    - "Structural equality for collection-based records (ObjectTagValue, ListTagValue, TreeTagValue)"
    - "Flags enum for combinable permissions and source filtering"
    - "Builder pattern for immutable collection construction"

key-files:
  created:
    - DataWarehouse.SDK/Tags/TagValueTypes.cs
    - DataWarehouse.SDK/Tags/TagTypes.cs
    - DataWarehouse.SDK/Tags/TagSource.cs
    - DataWarehouse.SDK/Tags/TagAcl.cs
  modified: []

key-decisions:
  - "TagSource as Flags enum (not plain enum) to support multi-source filtering"
  - "TagCollection as class (not record) to avoid record equality issues with dictionary wrapping"
  - "TagKey.TryParse added alongside Parse for safe parsing in user input scenarios"

patterns-established:
  - "Discriminated union pattern: abstract record base + sealed record subtypes + Kind enum"
  - "Per-tag ACL with principal-based entries and configurable default fallback"
  - "Monotonic version counter on Tag for optimistic concurrency"

duration: 6min
completed: 2026-02-20
---

# Phase 55 Plan 01: Core Tag Type System Summary

**Strongly-typed tag value discriminated union (10 types), qualified TagKey, versioned Tag record with source provenance and per-tag ACL in DataWarehouse.SDK/Tags/**

## Performance

- **Duration:** 6 min
- **Started:** 2026-02-19T16:13:57Z
- **Completed:** 2026-02-19T16:20:08Z
- **Tasks:** 2
- **Files modified:** 4

## Accomplishments
- Defined complete TagValue discriminated union with 10 concrete sealed record types, each with Kind discriminator, ToString, and structural equality
- Implemented TagKey with Parse/TryParse, Tag record with monotonic versioning, TagSourceInfo provenance, and TagAcl access control
- TagCollection with indexed lookup, namespace queries, source filtering, and fluent TagCollectionBuilder
- All types marked with SdkCompatibility("5.0.0"), full XML documentation, 0 build errors

## Task Commits

Each task was committed atomically:

1. **Task 1: Create tag value type system** - `b4066873` (feat)
2. **Task 2: Create core tag types, source, and ACL** - already committed in `b4066873` + `db4a5b1e` (feat)

## Files Created/Modified
- `DataWarehouse.SDK/Tags/TagValueTypes.cs` - TagValue discriminated union base + 10 sealed record subtypes + TagValueKind enum + static factories (327 lines)
- `DataWarehouse.SDK/Tags/TagTypes.cs` - TagKey, Tag, TagCollection, TagCollectionBuilder (236 lines)
- `DataWarehouse.SDK/Tags/TagSource.cs` - TagSource flags enum, TagSourceInfo provenance record (53 lines)
- `DataWarehouse.SDK/Tags/TagAcl.cs` - TagPermission flags, TagAclEntry, TagAcl with GetEffectivePermission (102 lines)

## Decisions Made
- TagSource defined as [Flags] enum rather than plain enum -- enables multi-source filtering (e.g., `TagSource.User | TagSource.AI`)
- TagCollection implemented as class rather than record to avoid record equality issues with dictionary field wrapping
- Added TagKey.TryParse alongside Parse for defensive parsing in user-input scenarios (Rule 2 deviation)

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Removed `virtual` from Equals on sealed records**
- **Found during:** Task 1 (TagValueTypes.cs)
- **Issue:** CS0549 compiler error -- `virtual` keyword on Equals in sealed types is invalid in .NET 10
- **Fix:** Removed `virtual` from Equals overrides on ObjectTagValue, ListTagValue, TreeTagValue
- **Files modified:** DataWarehouse.SDK/Tags/TagValueTypes.cs
- **Verification:** dotnet build passes with 0 errors
- **Committed in:** b4066873

**2. [Rule 2 - Missing Critical] Added TagKey.TryParse method**
- **Found during:** Task 2 (TagTypes.cs)
- **Issue:** Plan only specified Parse but user-input scenarios need non-throwing parse
- **Fix:** Added TryParse(string?, out TagKey?) method
- **Files modified:** DataWarehouse.SDK/Tags/TagTypes.cs
- **Verification:** Build passes, method follows standard .NET TryParse pattern

---

**Total deviations:** 2 auto-fixed (1 bug, 1 missing critical)
**Impact on plan:** Both fixes necessary for correctness and defensive coding. No scope creep.

## Issues Encountered
- Prior agent had already created skeleton files that needed production hardening (XML docs, equality, ToString) -- upgraded in place rather than starting from scratch

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- All foundational tag types are in place for Phase 55 Plan 02 (Tag Schema Registry)
- TagValue discriminated union ready for schema validation constraints
- TagCollection/TagKey ready for storage integration
- No blockers

## Self-Check: PASSED

- [x] DataWarehouse.SDK/Tags/TagValueTypes.cs exists (327 lines)
- [x] DataWarehouse.SDK/Tags/TagTypes.cs exists (236 lines)
- [x] DataWarehouse.SDK/Tags/TagSource.cs exists (53 lines)
- [x] DataWarehouse.SDK/Tags/TagAcl.cs exists (102 lines)
- [x] Commit b4066873 exists
- [x] dotnet build DataWarehouse.SDK -- 0 errors, 0 warnings
- [x] dotnet build DataWarehouse.Kernel -- 0 errors, 0 warnings

---
*Phase: 55-universal-tags*
*Completed: 2026-02-20*
