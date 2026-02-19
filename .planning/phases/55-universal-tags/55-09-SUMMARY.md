---
phase: 55-universal-tags
plan: 09
subsystem: tag-query
tags: [tags, query, expression-tree, aggregation, sdk]
dependency_graph:
  requires: ["55-05 (tag store)", "55-08 (inverted index)"]
  provides: ["ITagQueryApi", "TagQueryExpression", "DefaultTagQueryApi"]
  affects: ["55-10 (CRDT tags)", "55-11 (tag integration)"]
tech_stack:
  added: []
  patterns: ["expression tree compilation", "cardinality-based query optimization", "tag projection"]
key_files:
  created:
    - DataWarehouse.SDK/Tags/TagQueryExpression.cs
    - DataWarehouse.SDK/Tags/ITagQueryApi.cs
    - DataWarehouse.SDK/Tags/DefaultTagQueryApi.cs
  modified: []
decisions:
  - "Used simple two-element arrays for And/Or combinators instead of nested flattening to satisfy SonarSource S3060 (no self-type-testing in base class)"
  - "Sort by non-ObjectKey fields falls back to ObjectKey ordering since tag data is not loaded at sort stage; future optimization can pass sort hints to index layer"
  - "NOT queries use full-scan subtraction via GetTagDistributionAsync + per-tag queries with warning logging"
metrics:
  duration: "4m 22s"
  completed: "2026-02-19T16:49:29Z"
---

# Phase 55 Plan 09: Tag Query API Summary

Composable expression tree query API with AND/OR/NOT compilation, cardinality-based optimization, pagination, tag projection, and value aggregation over the inverted index.

## What Was Built

### TagQueryExpression.cs (242 lines)
- **TagQueryExpression** abstract base with static builders: `Where()`, `HasTag()`, `TagEquals()`
- Instance combinators: `And()`, `Or()`, `Negate()` for fluent composition
- **TagFilterExpression** leaf node wrapping `TagFilter` for direct index queries
- **AndExpression**, **OrExpression**, **NotExpression** composite nodes
- **TagQuerySortField** enum: ObjectKey, TagKey, TagValue, ModifiedUtc, CreatedUtc
- **TagQueryRequest** with Skip/Take pagination, SortBy, Descending, IncludeTagValues, ProjectTags
- **TagQueryResult** and **TagQueryResultItem** with tag collection and projected tag support

### ITagQueryApi.cs (59 lines)
- `QueryAsync(TagQueryRequest)` -- full expression tree query with pagination and projection
- `CountAsync(TagQueryExpression)` -- efficient count-only path
- `AggregateAsync(TagKey, topN)` -- value distribution for analytics
- `GetTagDistributionAsync(topN)` -- tag key usage distribution

### DefaultTagQueryApi.cs (300 lines)
- Expression tree compiler: recursive descent through AND/OR/NOT/Leaf nodes
- **AND optimization**: estimates child cardinality via `CountByTagAsync`, evaluates smallest first, short-circuits on empty intersection
- **OR optimization**: estimates child cardinality, evaluates largest first for early coverage
- **NOT handling**: full-scan subtraction with `LogWarning` about cost
- Tag loading from `ITagStore` for matched objects
- Tag projection: filters loaded tags to only requested `TagKey` set
- Aggregation: groups tag values across all objects, returns top N by count

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] SonarSource S3060 type-test prohibition**
- **Found during:** Task 1
- **Issue:** `And()` and `Or()` methods used `this is AndExpression` self-type-tests to flatten nested trees. S3060 treats this as an error.
- **Fix:** Simplified to always create two-element arrays without flattening. Tree flattening can be done in the compiler instead.
- **Files modified:** DataWarehouse.SDK/Tags/TagQueryExpression.cs
- **Commit:** 7a5ee95f

## Verification

- `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj` -- 0 errors, 0 warnings
- `dotnet build DataWarehouse.Kernel/DataWarehouse.Kernel.csproj` -- 0 errors
- Expression tree supports arbitrary nesting of AND/OR/NOT
- Query optimizer evaluates smallest filter first for AND
- Aggregation groups by tag value with top-N support
- Fluent API works: `HasTag("compliance", "gdpr").And(TagEquals(key, val))`

## Self-Check: PASSED

All 3 created files verified on disk. Both commit hashes (7a5ee95f, 53fff45c) verified in git log.
