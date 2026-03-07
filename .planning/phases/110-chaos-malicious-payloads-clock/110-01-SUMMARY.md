---
phase: 110-chaos-malicious-payloads-clock
plan: 01
subsystem: testing
tags: [chaos, security, zip-bomb, path-traversal, integer-overflow, superblock, vde, gzip, aes]

requires:
  - phase: 109-chaos-message-bus-federation
    provides: "Federation partition chaos testing infrastructure"
provides:
  - "PayloadGenerator with 7 attack vector factories (zip bomb, malformed IV, oversized header, path traversal, malformed superblock, integer overflow)"
  - "27 malicious payload rejection tests proving bounded time/memory/security"
  - "CHAOS-07 requirement satisfied"
affects: [110-02, 111]

tech-stack:
  added: []
  patterns: [bounded-decompression-guard, checked-arithmetic-overflow, path-traversal-validation, iv-length-validation]

key-files:
  created:
    - DataWarehouse.Hardening.Tests/Chaos/MaliciousPayload/PayloadGenerator.cs
    - DataWarehouse.Hardening.Tests/Chaos/MaliciousPayload/MaliciousPayloadTests.cs
  modified: []

key-decisions:
  - "Zip bomb uses tampered GZip ISIZE field to claim 10GB expansion while actual compressed payload stays under 2MB"
  - "IV validation checks length, all-zero, and truncation for AES-256-GCM, ChaCha20, AES-128-CBC"
  - "Path traversal covers 17 variants: Unix, Windows, null byte, Unicode normalization, URL-encoding, UNC, absolute path"
  - "Integer overflow detection uses C# checked arithmetic for blockCount * blockSize"
  - "xUnit1031 fix: replaced Task.Wait with async Task.WhenAny for timeout-based hang detection"

patterns-established:
  - "Bounded decompression: cap decompressed bytes to prevent zip bomb OOM"
  - "Size-before-allocate: validate claimed sizes against limits before allocation"
  - "Checked arithmetic: use checked() for all block math to detect overflow"

requirements-completed: [CHAOS-07]

duration: 11min
completed: 2026-03-07
---

# Phase 110 Plan 01: Malicious Payload Chaos Testing Summary

**27 chaos tests proving VDE rejects zip bombs, malformed IVs, oversized headers, path traversal, integer overflows, and corrupt superblocks within bounded time and memory**

## Performance

- **Duration:** 11 min
- **Started:** 2026-03-07T08:23:43Z
- **Completed:** 2026-03-07T08:35:09Z
- **Tasks:** 2
- **Files modified:** 2

## Accomplishments
- PayloadGenerator with 7 attack vector factories producing realistic hostile payloads
- 27 tests covering zip bombs (1000:1 ratio), malformed encryption IVs (3 algorithms x 5 variants), oversized headers (4GB claims), path traversal (17 variants), integer overflow (6 cases), malformed superblocks, arithmetic overflow, hang detection, and compression ratio validation
- All payloads rejected within bounded time (<5s) and memory (<50MB) with no OOM, hangs, or security bypass

## Task Commits

Each task was committed atomically:

1. **Task 1: Create malicious payload generator** - `2d864440` (feat)
2. **Task 2: Implement malicious payload rejection tests** - `b61e7864` (test)

## Files Created/Modified
- `DataWarehouse.Hardening.Tests/Chaos/MaliciousPayload/PayloadGenerator.cs` - Static factory methods for zip bombs, malformed IVs, oversized headers, path traversal, malformed superblocks, integer overflow payloads
- `DataWarehouse.Hardening.Tests/Chaos/MaliciousPayload/MaliciousPayloadTests.cs` - 27 xUnit tests validating rejection of all malicious payload types

## Decisions Made
- Zip bomb uses tampered GZip ISIZE to claim 10GB while keeping compressed payload small
- Path traversal covers Unix/Windows/null byte/Unicode/URL-encoding/UNC/absolute paths
- Checked arithmetic for overflow detection matches C# best practice
- Superblock tests leverage real SuperblockV2 Serialize/Deserialize with corrupted fields

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Fixed xUnit1031 analyzer error**
- **Found during:** Task 2 (test build)
- **Issue:** `task.Wait(timeout)` triggers xUnit1031 "blocking task operations cause deadlocks"
- **Fix:** Replaced with async `Task.WhenAny(task, Task.Delay(timeout))` pattern
- **Files modified:** MaliciousPayloadTests.cs
- **Verification:** Build succeeded with 0 warnings, 0 errors
- **Committed in:** b61e7864 (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (1 blocking)
**Impact on plan:** Standard xUnit analyzer compliance fix. No scope creep.

## Issues Encountered
None

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Malicious payload chaos testing complete, ready for 110-02 (Clock Skew chaos testing)
- All 27 tests passing in under 2 seconds total

---
*Phase: 110-chaos-malicious-payloads-clock*
*Completed: 2026-03-07*
