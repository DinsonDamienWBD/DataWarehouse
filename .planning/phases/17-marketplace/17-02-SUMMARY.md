---
phase: 17-marketplace
plan: 02
subsystem: Plugin Marketplace - Certification, Reviews, Revenue
tags: [marketplace, certification, reviews, ratings, revenue, security-validation, commission]
dependency-graph:
  requires: [SDK FeaturePluginBase, IMessageBus, PluginMessage, System.Reflection.AssemblyName]
  provides: [marketplace.certify (5-stage pipeline), marketplace.review (multi-dimensional), revenue tracking]
  affects: [Marketplace.razor GUI (topReview, reviews, ratingCount), Kernel PluginLoader via kernel.plugin.validate]
tech-stack:
  added: [System.Reflection for assembly inspection]
  patterns: [5-stage certification pipeline, multi-dimensional review ratings, decimal revenue arithmetic, ConcurrentDictionary with locks for thread safety]
key-files:
  modified:
    - Plugins/DataWarehouse.Plugins.PluginMarketplace/PluginMarketplacePlugin.cs
    - Metadata/TODO.md
decisions:
  - "Auto-approve reviews (ReviewModerationStatus.Approved) - moderation is future enhancement"
  - "Catalog-only entries pass certification with warnings (assembly file not always present)"
  - "Revenue tracking uses decimal arithmetic for financial precision (not double/float)"
  - "Combined Task 1 and Task 2 into single commit since they modify the same file"
metrics:
  duration: 6 min
  completed: 2026-02-11
  tasks: 2
  files: 1
---

# Phase 17 Plan 02: Certification Pipeline, Reviews, and Revenue Tracking Summary

5-stage certification pipeline with kernel message bus integration, multi-dimensional review system (reliability/performance/documentation), and decimal-precision revenue tracking with commission calculation.

## Tasks Completed

### Task 1: Certification Pipeline with Real Security Validation

Added a 5-stage certification pipeline to PluginMarketplacePlugin.cs:

**Stage 1 - Security Scan (30% weight):** Sends `kernel.plugin.validate` message to kernel via message bus. On timeout/unavailability, falls back to local checks: file existence + size limits, SHA-256 hash computation, valid .NET assembly verification via `AssemblyName.GetAssemblyName`, and signed assembly check.

**Stage 2 - SDK Compatibility (25% weight):** Verifies assembly follows `DataWarehouse.Plugins.*` naming convention, checks for forbidden references to `DataWarehouse.Kernel`, validates SDK-only dependency pattern.

**Stage 3 - Dependency Validation (20% weight):** Checks all declared dependencies exist in catalog with compatible versions. Uses existing `TopologicalSort` to detect circular dependency chains. Optional dependencies don't penalize score.

**Stage 4 - Static Analysis (25% weight):** Checks for no cross-plugin references, naming convention compliance, and XML documentation file presence alongside the DLL.

**Stage 5 - Certification Scoring:** Computes weighted composite score. FullyCertified (>=90, all stages pass), BasicCertified (>=60, security+compat pass), Uncertified (<60 or security fail), Rejected (critical errors).

New record types: `CertificationRequest`, `CertificationResult`, `CertificationStageResult`, `CertificationPolicy`.

### Task 2: Rating/Review System and Revenue Tracking

**Review System:**
- `PluginReviewEntry` with multi-dimensional ratings: overall (required, 1-5), reliability, performance, documentation (optional, 1-5)
- `PluginReviewSubmission` for input validation
- `PluginReviewsResponse` with pagination, average rating, and star distribution
- `ReviewModerationStatus` enum: Pending, Approved, Rejected, Flagged
- Verified install check uses actual catalog `IsInstalled` status
- Duplicate review detection (same reviewer + plugin -> update existing)
- Auto-approve with moderation status support for future enhancement

**Revenue Tracking:**
- `DeveloperRevenueRecord` with decimal arithmetic (TotalEarnings, CommissionRate, CommissionAmount, NetEarnings)
- `PayoutStatus` enum: Pending, Processing, Paid, Failed
- `RevenueConfig` with 30% default commission, $50 minimum payout, USD currency
- Period-based records (yyyy-MM format) with aggregation on repeat installs
- Wired into install handler: checks catalog price, records revenue if > 0

**marketplace.list Updates:**
- Top review (highest rated, most recent approved) included as `topReview`
- Top 5 reviews by date (approved only) included as `reviews` array
- Each review includes dimension ratings and verified install status

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Backward-compatible review record migration**
- **Found during:** Task 2
- **Issue:** Existing `PluginReviewEntry` was a positional record with different fields (ReviewId, Author, Text, Rating, Date)
- **Fix:** Replaced with init-property record using new field names while maintaining serialization compatibility
- **Files modified:** PluginMarketplacePlugin.cs

**2. [Rule 3 - Blocking] Combined tasks into single commit**
- **Found during:** Task 1 and Task 2
- **Issue:** Both tasks modify the same file; review handler replacement in Task 2 depends on types added in Task 1
- **Fix:** Implemented both tasks sequentially and committed together
- **Files modified:** PluginMarketplacePlugin.cs

## Verification Results

1. `dotnet build` succeeds with 0 errors (44 pre-existing SDK warnings)
2. `CertifyPluginAsync` confirmed with 5 stages (SecurityScan, SdkCompatibility, DependencyValidation, StaticAnalysis, CertificationScoring)
3. `SubmitReviewAsync` confirmed with validation, duplicate detection, verified install check
4. `RecordInstallRevenueAsync` confirmed with decimal commission calculation
5. `marketplace.certify` and `marketplace.review` handlers confirmed in OnMessageAsync switch
6. Zero `NotImplementedException`, `TODO`, `HACK`, `FIXME`, or `Task.Delay` patterns

## Self-Check: PASSED

- [x] PluginMarketplacePlugin.cs exists and compiles
- [x] Commit fab0724 exists in git log
- [x] TODO.md updated with T57 marked [x] COMPLETE
