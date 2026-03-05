---
phase: 91.5-vde-v2.1-format-completion
plan: 87-37
subsystem: vde-integrity
tags: [probabilistic, corruption-detection, trlr, reservoir-sampling, wilson-score, xxhash64]

# Dependency graph
requires:
  - phase: 91.5-vde-v2.1-format-completion
    provides: UniversalBlockTrailer, BlockTypeTags, IBlockDevice, CorruptionDetector

provides:
  - CorruptionRadarConfig: configurable scan parameters (SampleSize, ConfidenceLevel, AlertThreshold, ScanInterval, MaxRetries, RandomSeed)
  - ProbabilisticCorruptionRadar: statistical TRLR sampling, Algorithm R reservoir sampling, Wilson score CI, continuous monitoring
  - RadarResult: complete scan result with estimated corruption rate, confidence bounds, corrupt block list

affects: [87-38, vde-integrity, corruption-detection]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Reservoir sampling (Algorithm R) for uniform TRLR block selection"
    - "Wilson score binomial confidence interval for rare-event estimation"
    - "Separated trailer architecture: TRLR blocks at every 256th block starting offset 255"

key-files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/Integrity/CorruptionRadarConfig.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Integrity/ProbabilisticCorruptionRadar.cs
  modified:
    - DataWarehouse.SDK/VirtualDiskEngine/Integrity/IntegrityChainConfig.cs

key-decisions:
  - "Wilson score interval chosen over normal approximation for rare-event accuracy (corruption is typically << 1%)"
  - "Algorithm R reservoir sampling provides O(n) uniform selection without materializing all positions"
  - "TRLR block stride=256, offset=255 matches separated trailer architecture (255 data blocks per TRLR block)"
  - "IntegrityChainConfig.cs forward cref XML comments fixed with <c> tags to unblock build before 87-38 creates CrossExtentIntegrityChain"

patterns-established:
  - "RadarResult as readonly struct: immutable scan result with all statistical fields"
  - "Continuous monitoring via RunContinuousAsync with configurable ScanInterval and OnAlertTriggered event"
  - "Retry-on-read: TryReadBlockWithRetryAsync up to MaxRetries before confirming unreadable block as corrupt"

# Metrics
duration: 3min
completed: 2026-03-02
---

# Phase 91.5 Plan 87-37: Probabilistic Corruption Radar Summary

**Statistical TRLR sampling corruption detector using Algorithm R reservoir sampling and Wilson score confidence intervals for lightweight VDE integrity checking without full scrubs (VOPT-48)**

## Performance

- **Duration:** 3 min
- **Started:** 2026-03-02T13:34:25Z
- **Completed:** 2026-03-02T13:37:22Z
- **Tasks:** 1
- **Files modified:** 3

## Accomplishments

- Probabilistic corruption radar using reservoir sampling of TRLR blocks (every 256th, starting at offset 255)
- Wilson score binomial confidence interval for accurate rare-event (corruption) probability estimation
- Continuous monitoring loop with configurable scan interval and alert callback via OnAlertTriggered event
- Retry-aware block reading with configurable MaxRetries before confirming corruption

## Task Commits

Each task was committed atomically:

1. **Task 1: CorruptionRadarConfig and ProbabilisticCorruptionRadar** - `6502dc5d` (feat)

## Files Created/Modified

- `DataWarehouse.SDK/VirtualDiskEngine/Integrity/CorruptionRadarConfig.cs` - Scan configuration: SampleSize, ConfidenceLevel, AlertThreshold, ScanInterval, MaxRetries, RandomSeed
- `DataWarehouse.SDK/VirtualDiskEngine/Integrity/ProbabilisticCorruptionRadar.cs` - Main radar class with GenerateTrlrBlockPositions, SelectSample (Algorithm R), EstimateCorruptionRate (Wilson score), ScanAsync, RunContinuousAsync
- `DataWarehouse.SDK/VirtualDiskEngine/Integrity/IntegrityChainConfig.cs` - Fixed forward cref XML doc comments (Rule 3 fix)

## Decisions Made

- Wilson score interval chosen over normal approximation: more accurate for small proportions (corruption rates << 1% where normal approximation breaks down)
- Algorithm R reservoir sampling is O(n) and avoids materializing all candidate positions into a secondary list
- TRLR block addressing: `(n * 256) + 255` for n=0,1,2... matches the separated trailer architecture where 255 data blocks precede each TRLR block
- Z-score computed via piecewise lookup table (not Gaussian CDF) — sufficient precision for 90/95/99% confidence levels, no external math dependency

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Fixed dangling XML cref in IntegrityChainConfig.cs**
- **Found during:** Task 1 (build verification)
- **Issue:** IntegrityChainConfig.cs (created by a prior execution) referenced `CrossExtentIntegrityChain`, `ChainVerificationResult`, and `BlockLevelResult` via XML `<see cref>` — types that don't exist until plan 87-38 runs. This caused 3 CS1574 build errors.
- **Fix:** Replaced `<see cref="CrossExtentIntegrityChain"/>`, `<see cref="ChainVerificationResult"/>`, `<see cref="BlockLevelResult"/>` with `<c>TypeName</c>` in the XML doc comments.
- **Files modified:** `DataWarehouse.SDK/VirtualDiskEngine/Integrity/IntegrityChainConfig.cs`
- **Verification:** Build succeeds with 0 errors, 0 warnings.
- **Committed in:** `6502dc5d` (Task 1 commit)

---

**Total deviations:** 1 auto-fixed (Rule 3 - blocking build error)
**Impact on plan:** Essential fix to restore build green. IntegrityChainConfig.cs content is correct — only XML doc comment forward references were removed. Plan 87-38 will add CrossExtentIntegrityChain and can restore the cref links at that time.

## Issues Encountered

None beyond the Rule 3 fix above.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- VOPT-48 satisfied: ProbabilisticCorruptionRadar is production-ready
- Plan 87-38 (CrossExtentIntegrityChain) can now proceed with IntegrityChainConfig.cs as its foundation
- SDK build is green (0 errors, 0 warnings)

---
*Phase: 91.5-vde-v2.1-format-completion*
*Completed: 2026-03-02*
