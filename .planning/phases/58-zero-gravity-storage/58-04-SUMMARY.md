---
phase: 58-zero-gravity-storage
plan: 04
subsystem: Storage/Placement
tags: [crush, placement, deterministic, straw2, failure-domain]
dependency_graph:
  requires: ["58-01"]
  provides: ["CrushPlacementAlgorithm", "CrushBucket", "BucketType"]
  affects: ["58-05", "58-06", "58-07"]
tech_stack:
  added: []
  patterns: ["CRUSH placement", "Straw2 selection", "Jenkins hash", "FNV-1a hash", "hierarchical bucket tree"]
key_files:
  created:
    - DataWarehouse.SDK/Storage/Placement/CrushBucket.cs
    - DataWarehouse.SDK/Storage/Placement/CrushPlacementAlgorithm.cs
  modified: []
decisions:
  - "Used record type for CrushBucket with init setters for with-expression support and mutable Children list"
  - "Jenkins one-at-a-time hash for Straw2 bucket selection; FNV-1a for object key hashing"
  - "MaxRetries=50 for failure domain separation with progressive relaxation (zone first, then rack)"
metrics:
  duration: ~2m
  completed: 2026-02-19T18:26:32Z
  tasks: 2/2
  files_created: 2
  files_modified: 0
  build: 0 errors, 0 warnings
---

# Phase 58 Plan 04: CRUSH Deterministic Placement Algorithm Summary

CRUSH-equivalent placement with Straw2 weighted bucket selection, Jenkins hash mixing, and zone/rack/host failure domain separation.

## What Was Built

### CrushBucket (Hierarchical Bucket Tree)
- `BucketType` enum: Root, Zone, Rack, Host
- `CrushBucket` sealed record with Straw2 `SelectChild` method
- Jenkins one-at-a-time hash variant for deterministic child selection
- `BuildHierarchy` static factory: flat `NodeDescriptor[]` -> Root > Zone > Rack > Host tree
- Weight propagation from leaves up through hierarchy

### CrushPlacementAlgorithm (IPlacementAlgorithm)
- `ComputePlacement`: deterministic object-to-node mapping
  - FNV-1a hash of object key produces stable placement group seed
  - Traverses bucket hierarchy per replica using Straw2
  - Failure domain separation: zone > rack > host with progressive relaxation
  - Constraint filtering: storage class, zones, tags, explicit PlacementConstraints
- `RecomputeOnNodeChange`: recomputes with new cluster map (CRUSH minimizes movement naturally)
- `EstimateMovementOnResize`: estimates data movement fraction for capacity planning

## Key Design Decisions

1. **Straw2 over uniform/list**: Straw2 provides weight-proportional selection with minimal movement on weight changes. Each child gets a "straw" = hash * weight; longest straw wins.

2. **Progressive relaxation for failure domains**: First 25 retries enforce zone separation, first ~17 enforce rack separation, all 50 enforce host uniqueness. Falls back to any unused host if retries exhausted.

3. **Positional record constructor for PlacementDecision**: Used named arguments to match the positional record defined in PlacementTypes.cs.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed PlacementDecision construction syntax**
- **Found during:** Task 2
- **Issue:** Plan used object-initializer syntax (`new PlacementDecision { ... }`) but PlacementDecision is a positional record requiring constructor arguments
- **Fix:** Used positional constructor with named arguments
- **Files modified:** CrushPlacementAlgorithm.cs

**2. [Rule 1 - Bug] Replaced Count() with Any() for performance**
- **Found during:** Task 2
- **Issue:** Plan used `eligibleNodes.Count(n => ...)` for existence checks in hot loop
- **Fix:** Changed to `eligibleNodes.Any(n => ...)` which short-circuits
- **Files modified:** CrushPlacementAlgorithm.cs

**3. [Rule 1 - Bug] Division-by-zero guard in EstimateMovementOnResize**
- **Found during:** Task 2
- **Issue:** `newTotal` could be zero if new map is empty
- **Fix:** Used `Math.Max(newTotal, 1.0)` to prevent division by zero
- **Files modified:** CrushPlacementAlgorithm.cs

## Verification

- `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj` -- 0 errors, 0 warnings
- CrushPlacementAlgorithm implements all 3 IPlacementAlgorithm methods
- BuildHierarchy creates proper Root > Zone > Rack > Host tree
- SelectChild uses Straw2 with Jenkins hash
- Both files marked with `[SdkCompatibility("5.0.0", Notes = "Phase 58: CRUSH placement")]`

## Commits

| Task | Commit | Description |
|------|--------|-------------|
| 1 | 76e0fdb9 | CrushBucket hierarchy with Straw2 selection |
| 2 | 1de34fdc | CRUSH deterministic placement algorithm |
