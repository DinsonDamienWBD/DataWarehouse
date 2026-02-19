---
phase: 55-universal-tags
plan: 02
subsystem: SDK Tags
tags: [tags, schema, validation, sdk, governance]
dependency_graph:
  requires: []
  provides: [TagSchema, TagSchemaVersion, TagConstraint, TagSchemaEvolutionRule, ITagSchemaRegistry, TagSchemaValidator, TagValidationResult, TagValidationError]
  affects: [tag-storage, tag-propagation, compliance-passports, sovereignty-mesh, policy-engine]
tech_stack:
  added: []
  patterns: [discriminated-union, sealed-hierarchy, flags-enum, builder-pattern, async-streaming]
key_files:
  created:
    - DataWarehouse.SDK/Tags/TagSchema.cs
    - DataWarehouse.SDK/Tags/ITagSchemaRegistry.cs
    - DataWarehouse.SDK/Tags/TagSchemaValidator.cs
    - DataWarehouse.SDK/Tags/TagValueTypes.cs
    - DataWarehouse.SDK/Tags/TagSource.cs
    - DataWarehouse.SDK/Tags/TagTypes.cs
    - DataWarehouse.SDK/Tags/TagAcl.cs
  modified: []
decisions:
  - "Created Plan 01 prerequisite types inline (TagValueKind, TagValue, TagKey, Tag, TagCollection, TagSource, TagAcl) since Plan 01 was not yet executed"
  - "TagSource as flags enum supporting bitwise combination for AllowedSources filtering"
  - "TagCollection changed from record to class for proper equality semantics on dictionary wrapper"
metrics:
  duration: "5m 0s"
  completed: "2026-02-19T16:19:18Z"
  tasks_completed: 2
  tasks_total: 2
  files_created: 7
  files_modified: 0
---

# Phase 55 Plan 02: Tag Schema Registry in SDK Summary

Tag schema governance layer with versioned constraints, registry interface, and comprehensive validator covering type/range/length/pattern/source/depth/required checks.

## What Was Built

### TagSchema.cs
- **TagConstraint** record: MinValue/MaxValue, MinLength/MaxLength, Pattern, AllowedValues, AllowedItemKinds, MaxItems, MaxDepth, Required, DefaultValue
- **TagSchemaVersion** record: semver Version, CreatedUtc, ChangeDescription, Constraints, RequiredKind
- **TagSchema** record: SchemaId, TagKey, DisplayName, Description, Versions (ordered oldest-newest), CurrentVersion, AllowedSources (flags), Immutable, SystemOnly
- **TagSchemaEvolutionRule** enum: None, Additive, Compatible, Breaking

### ITagSchemaRegistry.cs
- Async interface: RegisterAsync, GetAsync, GetByTagKeyAsync, ListAsync (IAsyncEnumerable), AddVersionAsync (with evolution rule), DeleteAsync, CountAsync
- Full CancellationToken support on all methods

### TagSchemaValidator.cs
- **Validate(Tag, TagSchema)**: validates single tag against schema
- **ValidateCollection(TagCollection, IEnumerable\<TagSchema\>)**: validates all tags + checks Required constraint
- Constraint checks: type mismatch, numeric range, string length, regex pattern (with timeout), allowed values, list item count/kinds, object property count, tree depth, source authorization, system-only, immutability
- **TagValidationResult** / **TagValidationError**: structured error reporting with error codes
- **TagValidationErrorCodes**: static class with all error code constants

### Prerequisite Types (Plan 01 dependency)
- **TagValueTypes.cs**: 10 sealed record subtypes of TagValue with Kind discriminator and factory methods
- **TagSource.cs**: Flags enum (None, User, Plugin, AI, System, All) + TagSourceInfo record
- **TagTypes.cs**: TagKey (with Parse/TryParse), Tag, TagCollection, TagCollectionBuilder
- **TagAcl.cs**: TagPermission flags, TagAclEntry, TagAcl with GetEffectivePermission

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Created Plan 01 prerequisite types inline**
- **Found during:** Task 1
- **Issue:** Plan 02 depends on TagValueKind, TagValue, TagKey, TagSource, TagCollection from Plan 01, but Plan 01 had not been executed
- **Fix:** Created all 4 prerequisite files (TagValueTypes.cs, TagSource.cs, TagTypes.cs, TagAcl.cs) with full production implementations matching Plan 01 spec
- **Files created:** TagValueTypes.cs, TagSource.cs, TagTypes.cs, TagAcl.cs
- **Commits:** 298d1a95, b4066873

## Verification

- `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj` -- 0 errors, 0 warnings
- `dotnet build DataWarehouse.Kernel/DataWarehouse.Kernel.csproj` -- 0 errors, 0 warnings
- All 7 files in namespace `DataWarehouse.SDK.Tags`
- No plugin project references (SDK-only)
- Validator covers: type mismatch, range, length, pattern, required, source, immutable, system-only, tree depth, list items

## Commits

| Task | Commit | Description |
|------|--------|-------------|
| 1 | 298d1a95 | Tag schema types with prerequisite tag value system |
| 1 (linter) | b4066873 | Linter-enhanced tag value discriminated union |
| 2 | db4a5b1e | Schema registry interface and validator |

## Self-Check: PASSED

All 7 files verified present. All 3 commits verified in history.
