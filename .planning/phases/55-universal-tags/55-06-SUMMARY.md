---
phase: 55-universal-tags
plan: 06
subsystem: Tags
tags: [propagation, pipeline, lifecycle, rules-engine]
dependency_graph:
  requires: ["55-04 (TagAttachmentService)", "55-05 (TagSchemaRegistry)"]
  provides: ["ITagPropagationEngine", "DefaultTagPropagationEngine", "TagPropagationRule", "PipelineStage"]
  affects: ["55-07 (TagQueryEngine)", "55-08 (TagPolicy)"]
tech_stack:
  added: []
  patterns: ["rules-engine with priority ordering", "stop-on-match evaluation", "LWW merge semantics"]
key_files:
  created:
    - DataWarehouse.SDK/Tags/TagPropagationRule.cs
    - DataWarehouse.SDK/Tags/ITagPropagationEngine.cs
    - DataWarehouse.SDK/Tags/DefaultTagPropagationEngine.cs
  modified: []
decisions:
  - "Default propagation action is Copy -- tags propagate unless explicitly dropped (safe default)"
  - "Built-in rules use priority 5-10 to allow user rules at 100+ to override"
  - "LWW merge uses ModifiedUtc timestamp for conflict resolution"
  - "Attachment failure on bulk-attach moves tags from propagated to failed (does not throw)"
metrics:
  duration: "189s"
  completed: "2026-02-19T16:38:06Z"
  tasks: 2
  files_created: 3
  total_lines: 654
---

# Phase 55 Plan 06: Tag Propagation Engine Summary

Tag propagation engine with priority-ordered rules that carry tags through 9 pipeline stages (Ingest->Delete), defaulting to Copy with built-in rules for system/compliance/temp namespaces.

## What Was Built

### Task 1: Propagation Rules and Contracts (70aaa1b2)

Created the type system for tag propagation:

- **PipelineStage enum**: 9 stages from Ingest through Delete covering the complete data lifecycle
- **PropagationAction enum**: Copy, Drop, Transform, Merge, InheritFromParent
- **TagPropagationRule record**: Supports key/namespace filters, priority ordering, stop-on-match, and custom transform functions
- **TagPropagationContext record**: Carries source/target object keys, current stage, source tags, and pipeline metadata
- **TagPropagationResult record**: Reports propagated, dropped, and failed tags separately
- **ITagPropagationEngine interface**: PropagateAsync, AddRule, RemoveRule, GetRules with stage filter

### Task 2: Default Propagation Engine (c5eabcb0)

Implemented `DefaultTagPropagationEngine` with full rule evaluation:

- Rules evaluated in priority order (lower = first), with RuleId as tiebreaker
- **Copy**: tag forwarded unchanged
- **Drop**: tag removed with reason tracking
- **Transform**: custom function applied, result validated against schema registry
- **Merge**: LWW semantics using ModifiedUtc when target already has the tag
- **InheritFromParent**: looks up parent tag via attachment service
- Default behavior when no rules match: **Copy** (no silent tag loss)
- Built-in rules registered at construction:
  - System namespace: always Copy (priority 10) across all stage transitions
  - Compliance namespace: always Copy (priority 10) across all stage transitions
  - Temp namespace: Drop at Store->Replicate transition (priority 5)
- Thread-safe rule management with lock-based synchronization
- Bulk-attach propagated tags to target object when attachment service available

## Verification

- `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj` -- 0 errors, 0 warnings
- `dotnet build DataWarehouse.Kernel/DataWarehouse.Kernel.csproj` -- 0 errors, 0 warnings
- PipelineStage covers Ingest through Delete (9 stages)
- Rules support filter, transform, merge, drop, inherit actions
- Priority ordering confirmed with stop-on-match support
- Default Copy behavior ensures no silent tag loss

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed TagSchemaValidator API call**
- **Found during:** Task 2
- **Issue:** Initial code called `TagSchemaValidator.Validate(transformed.Value, schema.CurrentVersion)` but the actual API signature is `Validate(Tag tag, TagSchema schema)`
- **Fix:** Changed to `TagSchemaValidator.Validate(transformed, schema)`
- **Files modified:** DefaultTagPropagationEngine.cs
- **Commit:** c5eabcb0

## Decisions Made

1. **Safe default is Copy** -- When no propagation rule matches a tag, it is copied forward. This prevents accidental data loss. Users must explicitly add Drop rules.
2. **Built-in rules at low priority numbers** -- System (10) and compliance (10) copy rules, temp drop (5) use low priority values so user-defined rules at the default 100 can override them.
3. **LWW for Merge** -- Last-Writer-Wins based on `ModifiedUtc` is the default merge strategy. Custom merge logic can be implemented via Transform action with a custom function.
4. **Attachment failure is non-fatal** -- If bulk-attach to the target fails, tags move to the FailedTags list rather than throwing an exception, preserving the evaluation result.

## Self-Check: PASSED

- All 3 created files exist on disk
- All 1 modified file verified
- Commit 70aaa1b2 found in git log
- Commit c5eabcb0 found in git log
- SDK build: 0 errors, 0 warnings
- Kernel build: 0 errors, 0 warnings
