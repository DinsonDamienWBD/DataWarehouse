---
phase: 62-carbon-aware
plan: "04"
subsystem: UltimateSustainability
tags: [carbon-awareness, green-placement, grid-api, watttime, electricity-maps, renewable-routing]
dependency-graph:
  requires: ["62-01 (SDK carbon types)", "62-02 (energy measurement)"]
  provides: ["IGreenPlacementService implementation", "grid carbon API integration", "backend green scoring"]
  affects: ["62-05 (green tiering)", "62-06 (carbon reporting)"]
tech-stack:
  added: ["WattTime API v3", "Electricity Maps API v3"]
  patterns: ["strategy cascade (WattTime -> ElectricityMaps -> estimation)", "IHttpClientFactory", "ConcurrentDictionary caching with TTL", "message bus pub/sub for audit"]
key-files:
  created:
    - Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/GreenPlacement/WattTimeGridApiStrategy.cs
    - Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/GreenPlacement/ElectricityMapsApiStrategy.cs
    - Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/GreenPlacement/BackendGreenScoreRegistry.cs
    - Plugins/DataWarehouse.Plugins.UltimateSustainability/Strategies/GreenPlacement/GreenPlacementService.cs
  modified: []
decisions:
  - "WattTime primary, ElectricityMaps fallback, estimation as final fallback -- cascade pattern"
  - "5-minute cache TTL default balances freshness vs API rate limits"
  - "Pre-registered 13 cloud backends with real sustainability data from public reports"
  - "Weighted scoring: renewable 40%, carbon intensity 30%, PUE 20%, WUE 10%"
metrics:
  duration: "5m 14s"
  completed: "2026-02-19T21:19:36Z"
  tasks: 2
  files-created: 4
  total-lines: 1787
---

# Phase 62 Plan 04: Green Placement API Integration Summary

Renewable-aware data placement with live grid carbon API integration and composite backend scoring using WattTime v3 and ElectricityMaps APIs.

## What Was Built

### Task 1: Grid API Strategies (e4d07e32)

**WattTimeGridApiStrategy.cs** (484 lines):
- WattTime API v3 integration: `/login` (Basic auth -> bearer token), `/signal-index` (MOER data), `/forecast` (24h forecast)
- MOER-to-gCO2e/kWh conversion: `moer * 453.592 / 1000`
- Token caching with 25-minute refresh, SemaphoreSlim for thread-safe re-auth
- Cloud region mapping (12 regions across AWS/Azure/GCP -> balancing authorities)
- ConcurrentDictionary response cache with configurable TTL (default 5 min)
- Exponential backoff retry (3 retries) for HTTP 429 rate limits
- Auto re-auth on 401 Unauthorized

**ElectricityMapsApiStrategy.cs** (482 lines):
- Electricity Maps API v3: `/carbon-intensity/latest`, `/power-breakdown/latest`, `/carbon-intensity/forecast`
- Auth via `auth-token` header
- Parallel fetch of carbon intensity + power breakdown for renewable % data
- Same caching and retry patterns as WattTime
- Zone mapping (12 cloud regions -> EM zone identifiers)

Both strategies: extend SustainabilityStrategyBase, never throw to callers (return cached/estimated on failure), configurable via ApiKey/ApiEndpoint/CacheTtlSeconds properties, `IsAvailable()` gated on credentials.

### Task 2: Registry and Placement Service (e8672543)

**BackendGreenScoreRegistry.cs** (332 lines):
- Composite green score formula: `(renewable% * 0.4) + ((1 - carbon/1000) * 100 * 0.3) + ((2 - PUE) * 100 * 0.2) + (WUE component * 0.1)`
- Pre-registered 13 backends: 5 AWS, 4 Azure, 3 GCP, 1 on-prem default
- `UpdateCarbonIntensity(region, intensity)` re-scores all backends in a region
- `GetBestBackend(candidates)` for quick selection
- JSON persistence to disk for reload

**GreenPlacementService.cs** (489 lines):
- Implements `IGreenPlacementService` from SDK
- `SelectGreenestBackendAsync`: fetches live grid data per candidate region, updates scores, selects best, estimates carbon
- Grid data cascade: WattTime -> ElectricityMaps -> cached -> estimation
- `RefreshGridDataAsync`: SemaphoreSlim(3) rate-limited parallel refresh of all known regions
- Subscribes to `storage.backend.registered` for auto-registration
- Publishes `sustainability.placement.decision` for audit trail
- Carbon savings calculation: difference between best and worst candidate

## Deviations from Plan

None -- plan executed exactly as written.

## Verification

- `dotnet build Plugins/DataWarehouse.Plugins.UltimateSustainability/DataWarehouse.Plugins.UltimateSustainability.csproj` -- 0 errors, 0 warnings
- `dotnet build DataWarehouse.Kernel/DataWarehouse.Kernel.csproj` -- 0 errors, 0 warnings
- GreenPlacementService implements IGreenPlacementService (confirmed)
- WattTime uses real API endpoints: `/v3/login`, `/v3/signal-index`, `/v3/forecast`
- ElectricityMaps uses real API endpoints: `/v3/carbon-intensity/latest`, `/v3/power-breakdown/latest`
- Registry pre-populated with 13 known backends from AWS/Azure/GCP sustainability reports
- All artifacts exceed min_lines: 484/180, 482/150, 489/180, 332/120

## Self-Check: PASSED

All 4 files exist and both commits verified:
- FOUND: WattTimeGridApiStrategy.cs (484 lines)
- FOUND: ElectricityMapsApiStrategy.cs (482 lines)
- FOUND: BackendGreenScoreRegistry.cs (332 lines)
- FOUND: GreenPlacementService.cs (489 lines)
- FOUND: e4d07e32 (Task 1 commit)
- FOUND: e8672543 (Task 2 commit)
