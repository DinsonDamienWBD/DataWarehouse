---
phase: "60-semantic-sync"
plan: "04"
subsystem: "SemanticSync"
tags: ["routing", "bandwidth", "summary", "fidelity", "downsampling"]
dependency_graph:
  requires: ["60-01", "60-02"]
  provides: ["ISummaryRouter implementation", "SummaryGenerator", "FidelityDownsampler"]
  affects: ["SemanticSync plugin", "bandwidth-aware sync decisions"]
tech_stack:
  added: []
  patterns: ["decision matrix lookup", "progressive fidelity reduction", "AI fallback extraction"]
key_files:
  created:
    - Plugins/DataWarehouse.Plugins.SemanticSync/Strategies/Routing/BandwidthAwareSummaryRouter.cs
    - Plugins/DataWarehouse.Plugins.SemanticSync/Strategies/Routing/SummaryGenerator.cs
    - Plugins/DataWarehouse.Plugins.SemanticSync/Strategies/Routing/FidelityDownsampler.cs
  modified: []
decisions:
  - "Made constructor internal to resolve accessibility mismatch between public router and internal helpers"
  - "Used SearchValues<char> for IndexOfAny to satisfy CA1870 performance rule"
  - "Bandwidth tiers use bytes-per-second thresholds: High >12.5MB/s, Medium >1.25MB/s, Low >125KB/s"
metrics:
  duration: "4m 39s"
  completed: "2026-02-19T19:50:36Z"
  tasks_completed: 2
  tasks_total: 2
---

# Phase 60 Plan 04: Bandwidth-Aware Summary Routing Summary

Bandwidth-aware routing engine with 5x4 decision matrix (importance x bandwidth tier), AI-powered summary generation with extraction fallback, and progressive JSON/binary fidelity downsampling.

## What Was Built

### BandwidthAwareSummaryRouter
- Implements `ISummaryRouter` interface from SDK
- Decision matrix maps all 20 combinations of `SemanticImportance` (5 levels) x `BandwidthTier` (4 tiers) to `SyncFidelity`
- Critical data syncs at Full fidelity even on low bandwidth; negligible data defers on very-low links
- Strategy ID: `summary-router-bandwidth-aware`
- Supports all 5 fidelity levels, 10 MB max summary, reconstruction enabled

### SummaryGenerator
- Internal helper used by the router for summary generation and reconstruction
- AI path: Uses `IAIProvider.CompleteAsync` with fidelity-specific prompts (Summarized = 3-sentence summary, Metadata = schema extraction)
- Fallback path: Extraction-based -- takes proportional bytes (10% for Summarized, 256 bytes for Metadata)
- Embedding generation via `IAIProvider.GetEmbeddingsAsync` when available
- Reconstruction: AI-driven expansion when embedding available, otherwise returns summary text as bytes

### FidelityDownsampler
- Progressive field stripping through fidelity transitions
- JSON path: Parses with `System.Text.Json`, removes fields by category (audit/history at Detailed, previews/cache at Standard), re-serializes
- Binary path: Header-preserving truncation at target size ratios
- Target ratios: Detailed 80%, Standard 50%, Summarized 15%, Metadata 2%
- Graceful degradation: malformed JSON returned unmodified, empty data passed through

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Constructor accessibility mismatch**
- **Found during:** Task 2
- **Issue:** BandwidthAwareSummaryRouter is public but constructor accepted internal types (SummaryGenerator, FidelityDownsampler)
- **Fix:** Made constructor `internal` since the router is composed inside the plugin assembly
- **Files modified:** BandwidthAwareSummaryRouter.cs
- **Commit:** 0277bee8

**2. [Rule 1 - Bug] CA1870 SearchValues performance warning treated as error**
- **Found during:** Task 1 build verification
- **Issue:** `IndexOfAny(new[] { ':', '=' })` triggers CA1870 requiring cached SearchValues
- **Fix:** Added `SearchValues<char>` static field and used `AsSpan().IndexOfAny(SearchValues)` pattern
- **Files modified:** SummaryGenerator.cs
- **Commit:** bacdbfcb

## Commits

| Task | Commit | Description |
|------|--------|-------------|
| 1 | bacdbfcb | feat(60-04): add SummaryGenerator and FidelityDownsampler helpers |
| 2 | 0277bee8 | feat(60-04): add BandwidthAwareSummaryRouter with 5x4 decision matrix |

## Verification

- All 3 files compile in plugin project: PASSED
- Kernel build: PASSED
- BandwidthAwareSummaryRouter implements ISummaryRouter: PASSED
- Decision matrix covers all 20 importance-bandwidth combinations: PASSED (5 importance x 4 tiers)
- SummaryGenerator handles AI and fallback paths: PASSED
- FidelityDownsampler parses JSON and strips fields (not a stub): PASSED
- No Task.Delay or placeholder implementations: PASSED

## Self-Check: PASSED

All 3 created files verified on disk. Both commit hashes (bacdbfcb, 0277bee8) verified in git log.
