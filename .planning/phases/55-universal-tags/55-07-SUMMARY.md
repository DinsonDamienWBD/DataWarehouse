---
phase: 55-universal-tags
plan: 07
subsystem: SDK/Tags
tags: [tag-policy, compliance, governance, policy-engine]
dependency_graph:
  requires: ["55-04 (TagSchemaValidator)", "55-05 (TagEvents, TagTopics)"]
  provides: ["ITagPolicyEngine", "DefaultTagPolicyEngine", "TagPolicy", "PolicyViolation", "PolicyEvaluationResult"]
  affects: ["compliance-passports", "sovereignty-mesh", "tag-attachment-service"]
tech_stack:
  added: []
  patterns: ["ConcurrentDictionary for thread-safe policy store", "message bus violation publishing", "Stopwatch-based evaluation metrics"]
key_files:
  created:
    - DataWarehouse.SDK/Tags/TagPolicy.cs
    - DataWarehouse.SDK/Tags/ITagPolicyEngine.cs
    - DataWarehouse.SDK/Tags/DefaultTagPolicyEngine.cs
  modified: []
decisions:
  - "Used ConcurrentDictionary for in-memory policy storage (thread-safe, no external dependency)"
  - "Built-in classification policy disabled by default to avoid breaking existing objects"
  - "Violation publish is best-effort (caught exceptions) to not block evaluation results"
metrics:
  duration: "174s"
  completed: "2026-02-19T16:38:06Z"
  tasks: 2
  files_created: 3
  files_modified: 0
---

# Phase 55 Plan 07: Tag Policy Engine Summary

Tag policy engine with 7 condition types, scope-based applicability, severity-driven compliance, and message bus violation publishing.

## What Was Built

### Task 1: Tag Policy Types (TagPolicy.cs)

Defined the complete policy type system:

- **PolicySeverity** enum: Info, Warning, Error, Critical -- determines operational impact
- **PolicyScope** enum: Global, Namespace, ObjectPrefix, Custom -- determines which objects a policy applies to
- **PolicyConditionType** enum: RequireTag, ForbidTag, RequireValue, RequireSource, RequireAcl, TagCountLimit, CustomPredicate
- **PolicyCondition** record: carries condition-specific parameters (TagKey, ExpectedValue, RequiredSource, MaxTagCount, Predicate)
- **TagPolicy** record: full policy definition with ID, scope, conditions (AND semantics), severity, enabled flag
- **PolicyViolation** record: captures which policy/condition failed on which object
- **PolicyEvaluationResult** record: IsCompliant flag, all violations, metrics

### Task 2: Policy Engine Interface and Implementation

**ITagPolicyEngine** interface provides:
- `EvaluateAsync` -- full evaluation returning all violations
- `AddPolicyAsync` / `RemovePolicyAsync` -- policy CRUD
- `GetPolicyAsync` / `ListPoliciesAsync` -- retrieval with optional scope filter
- `IsCompliantAsync` -- fast-path boolean check for pre-write validation

**DefaultTagPolicyEngine** implements all 7 condition types:
- **RequireTag**: checks `tags.ContainsKey(tagKey)`
- **ForbidTag**: checks tag does NOT exist
- **RequireValue**: checks tag exists AND value equals expected
- **RequireSource**: checks tag exists AND source matches
- **RequireAcl**: checks tag exists AND has ACL entries
- **TagCountLimit**: checks `tags.Count <= maxCount`
- **CustomPredicate**: invokes `Func<TagCollection, bool>`

Built-in policies registered at construction:
- `system.require-classification` (disabled): requires `compliance:data-classification` tag
- `system.tag-count-limit` (enabled): max 1000 tags per object

Violations with severity >= Warning are published to `TagTopics.TagPolicyViolation` via message bus.

## Deviations from Plan

None -- plan executed exactly as written.

## Verification

- `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj` -- 0 errors, 0 warnings
- `dotnet build DataWarehouse.Kernel/DataWarehouse.Kernel.csproj` -- 0 errors
- All 7 condition types implemented in `EvaluateCondition` switch expression
- Severity-based compliance: only Error/Critical mark as non-compliant
- Built-in policies registered: classification disabled, tag-count-limit enabled

## Commits

| Task | Commit     | Description                                      |
| ---- | ---------- | ------------------------------------------------ |
| 1    | `6dfd0653` | Tag policy types (enums, conditions, violations) |
| 2    | `e102dc3d` | Policy engine interface and default implementation |

## Self-Check: PASSED

All 3 created files exist. Both commit hashes verified in git log.
