---
phase: 21-data-transit
plan: 04
subsystem: transit
tags: [store-and-forward, offline-transfer, sneakernet, qos, token-bucket, bandwidth-throttling, cost-aware-routing, sha256, air-gap]

# Dependency graph
requires:
  - phase: 21-01
    provides: SDK transit contracts (IDataTransitStrategy, DataTransitStrategyBase, TransitTypes)
provides:
  - StoreAndForwardStrategy for offline/air-gap transfers with SHA-256 manifest integrity
  - QoSThrottlingManager with token bucket rate limiting and per-priority minimum bandwidth guarantees
  - CostAwareRouter with 4 routing policies (Cheapest, Fastest, Balanced, CostCapped)
  - ThrottledStream wrapper for transparent bandwidth enforcement
affects: [21-05, transit-orchestration, qos-integration]

# Tech tracking
tech-stack:
  added: []
  patterns: [token-bucket-rate-limiting, weighted-fair-queueing, min-max-normalization, incremental-hash-streaming, manifest-integrity-verification]

key-files:
  created:
    - Plugins/DataWarehouse.Plugins.UltimateDataTransit/Strategies/Offline/StoreAndForwardStrategy.cs
    - Plugins/DataWarehouse.Plugins.UltimateDataTransit/QoS/QoSThrottlingManager.cs
    - Plugins/DataWarehouse.Plugins.UltimateDataTransit/QoS/CostAwareRouter.cs
  modified: []

key-decisions:
  - "IncrementalHash for streaming SHA-256 computation on large files (no full-file buffering)"
  - "Two-phase bandwidth allocation: minimum guarantees reserved first, remainder distributed by weight"
  - "Token bucket capacity set to 2x per-second rate for burst handling"
  - "Min-max normalization for balanced route scoring across heterogeneous metrics"

patterns-established:
  - "Manifest-based package integrity: serialize without hash field, compute SHA-256, then set hash"
  - "Token bucket with SemaphoreSlim for thread-safe refill and consumption"
  - "ThrottledStream pattern: read-then-throttle for reads, throttle-then-write for writes"
  - "Cost computation: FixedCostPerTransfer + CostPerGB * sizeBytes / 1_073_741_824"

# Metrics
duration: 7min
completed: 2026-02-11
---

# Phase 21 Plan 04: Offline Transfer, QoS Throttling, and Cost-Aware Routing Summary

**Store-and-forward offline strategy with SHA-256 manifest integrity, token bucket QoS with starvation-prevention minimum guarantees, and 4-policy cost-aware route selection**

## Performance

- **Duration:** 7 min
- **Started:** 2026-02-11T10:40:04Z
- **Completed:** 2026-02-11T10:47:28Z
- **Tasks:** 2
- **Files created:** 3

## Accomplishments
- StoreAndForwardStrategy packages data for air-gap transfer with directory structure, manifest.json, and per-entry SHA-256 integrity hashes (661 lines)
- QoSThrottlingManager enforces bandwidth with token bucket algorithm, weighted fair queueing, and guaranteed minimum bandwidth per priority tier preventing starvation (530 lines)
- CostAwareRouter selects optimal routes across 4 policies using min-max normalization for balanced scoring (338 lines)
- ThrottledStream wraps any Stream with transparent bandwidth enforcement on read/write operations

## Task Commits

Each task was committed atomically:

1. **Task 1: StoreAndForwardStrategy** - `95db34c` (feat)
2. **Task 2: QoSThrottlingManager + CostAwareRouter** - `08e639d` (feat)

## Files Created/Modified
- `Plugins/DataWarehouse.Plugins.UltimateDataTransit/Strategies/Offline/StoreAndForwardStrategy.cs` - Offline/sneakernet transfer with package creation, manifest integrity hash, per-entry SHA-256, and ingest verification
- `Plugins/DataWarehouse.Plugins.UltimateDataTransit/QoS/QoSThrottlingManager.cs` - Token bucket rate limiter, per-priority buckets, minimum bandwidth guarantees, ThrottledStream wrapper
- `Plugins/DataWarehouse.Plugins.UltimateDataTransit/QoS/CostAwareRouter.cs` - Cost-based route selection with Cheapest/Fastest/Balanced/CostCapped policies, route ranking

## Decisions Made
- Used IncrementalHash.CreateHash(HashAlgorithmName.SHA256) for streaming hash computation on large files instead of loading entire files into memory
- Two-phase bandwidth allocation: Phase 1 reserves minimum guarantees per priority tier unconditionally (starvation prevention per research pitfall 5), Phase 2 distributes remaining bandwidth by weight percentage
- Token bucket capacity = 2x per-second rate to allow burst handling while still enforcing average rate
- ThrottledStream applies throttle after read (to avoid holding stream locks) and before write (to enforce upstream backpressure)
- Min-max normalization for balanced scoring handles heterogeneous metrics (bytes/sec vs milliseconds vs currency) by normalizing to [0,1] range

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness
- Store-and-forward strategy auto-discovered by TransitStrategyRegistry via reflection
- QoSThrottlingManager ready for integration with plugin orchestrator TransferAsync pipeline
- CostAwareRouter ready for strategy selection enhancement
- Phase 21 Plan 05 can proceed

## Self-Check: PASSED

- [x] StoreAndForwardStrategy.cs exists (661 lines, min 180)
- [x] QoSThrottlingManager.cs exists (530 lines, min 200)
- [x] CostAwareRouter.cs exists (338 lines, min 150)
- [x] Commit 95db34c verified in git log
- [x] Commit 08e639d verified in git log
- [x] Build passes with 0 errors
- [x] Zero forbidden patterns (TODO, NotImplementedException, simulate, placeholder)
- [x] Key link patterns matched: token bucket/refill/SemaphoreSlim, CostPerGB/cheapest/balanced, throttl/bandwidth

---
*Phase: 21-data-transit*
*Completed: 2026-02-11*
