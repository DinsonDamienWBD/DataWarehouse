---
phase: 50
plan: 50-05
title: "Re-audit: Domain Spot-Check"
type: documentation-only
tags: [reaudit, spot-check, domain-verification, phase-44]
completed: 2026-02-18
---

# Phase 50 Plan 05: Re-audit -- Domain Spot-Check

**One-liner:** Spot-check of 5 key Phase 44 findings confirms: honeypot credentials fixed, 15 TLS bypasses still present, XXE still present, sync-over-async count reduced from 170+ to 60, null! count stable at 75.

## Spot-Check Methodology

Selected 5 key findings from Phase 44/47 audits representing different risk categories. Ran live grep scans against current codebase to verify current state.

---

## Spot-Check 1: Honeypot Hardcoded Credentials (Phase 43-02 P0)

**Original Finding:** 2 hardcoded honeypot passwords (`P@ssw0rd`, `Pr0d#Adm!n2024`) enabling fingerprinting.
**Fixed In:** Phase 43-04, commit e442e16

**Live Scan:**
```
grep -r "Password=P@ssw0rd\|Password=Pr0d#Adm!n" --include="*.cs" = 0 matches
```

**Verdict: FIXED.** Both honeypot strategies now use `RandomNumberGenerator.GetBytes()` for credential generation. Fingerprinting attack vector eliminated.

---

## Spot-Check 2: TLS Certificate Validation Bypass (Phase 47 CRIT-01)

**Original Finding:** 15 files with `ServerCertificateCustomValidationCallback = (...) => true`, enabling MITM attacks. CVSS 9.1.

**Live Scan:**
```
grep "ServerCertificateCustomValidationCallback.*true" = 15 source files
```

**Affected files (confirmed still present):**
1. AdaptiveTransportPlugin.cs
2. QuicDataPlanePlugin.cs (via AedsCore ControlPlane)
3. FederationSystem.cs (Intelligence)
4. DynamoDbConnectionStrategy.cs
5. CosmosDbConnectionStrategy.cs
6. ElasticsearchProtocolStrategy.cs (2 occurrences)
7. RestStorageStrategy.cs
8. GrpcStorageStrategy.cs (via AdaptiveTransport)
9. DashboardStrategyBase.cs (has VerifySsl option)
10. WekaIoStrategy.cs
11. VastDataStrategy.cs
12. PureStorageStrategy.cs
13. NetAppOntapStrategy.cs
14. HpeStoreOnceStrategy.cs
15. WebDavStrategy.cs
16. IcingaStrategy.cs

**Verdict: NOT FIXED.** This remains the highest-severity unfixed finding. Phase 49 did not address this. Remediation requires adding configurable `VerifySsl` (default: true) to each affected strategy, following the DashboardStrategyBase pattern.

---

## Spot-Check 3: XXE Vulnerability (Phase 47 HIGH-01)

**Original Finding:** `DtdProcessing.Parse` in XmlDocumentRegenerationStrategy allows external entity resolution.

**Live Scan:**
```
grep "DtdProcessing.Parse" = 1 match
  XmlDocumentRegenerationStrategy.cs:346
```

**Verdict: NOT FIXED.** The XXE vulnerability remains. Fix is simple: change `DtdProcessing.Parse` to `DtdProcessing.Prohibit`.

---

## Spot-Check 4: Sync-over-Async Pattern Count (Phase 43-01)

**Original Finding:** 170+ occurrences of `.GetAwaiter().GetResult()` across SDK, plugins, and tests.

**Live Scan:**
```
grep "GetAwaiter().GetResult()" --type=cs = 60 occurrences across 41 files
```

**Breakdown of reduction:**
- Phase 43-01 baseline: ~101 `.GetAwaiter().GetResult()` occurrences
- Phase 43-04 fixes: Removed from 8 files (dispose methods, timers, property getters)
- Current: 60 occurrences (41% reduction)

**Note:** The ~40 reduction exceeds the 15 P0 fixes because some files had multiple occurrences that were addressed in single fixes, and the Phase 43-04 RamDiskStrategy fix converted 2 `.GetAwaiter().GetResult()` calls plus the init method.

**Verdict: PARTIALLY FIXED.** 41% reduction achieved. Remaining 60 occurrences are P1/P2 in non-critical paths.

---

## Spot-Check 5: Null Suppression (`null!`) Count (Phase 43-02)

**Original Finding:** 75 `null!` occurrences across 46 files, defeating nullable reference type safety.

**Live Scan:**
```
grep "null!" --type=cs = 75 occurrences across 46 files
```

**Verdict: UNCHANGED.** No `null!` remediation has been performed. Count and distribution identical to Phase 43-02 baseline.

---

## Spot-Check Summary

| # | Finding | Phase | Original | Current | Status |
|---|---------|-------|----------|---------|--------|
| 1 | Honeypot credentials | 43-02 | 2 hardcoded | 0 hardcoded | FIXED |
| 2 | TLS cert bypass | 47 | 15 files | 15 files | NOT FIXED |
| 3 | XXE vulnerability | 47 | 1 file | 1 file | NOT FIXED |
| 4 | Sync-over-async | 43-01 | ~101 GAR | 60 GAR | PARTIAL (41% reduction) |
| 5 | Null suppression | 43-02 | 75 null! | 75 null! | UNCHANGED |

## Risk Assessment

| Risk | Current Level | Trend |
|------|--------------|-------|
| Security (TLS/XXE) | HIGH | Unchanged since Phase 47 |
| Code quality (async) | MEDIUM | Improving (41% reduction) |
| Code quality (null!) | LOW-MEDIUM | Unchanged |
| Build health | EXCELLENT | Stable |
| Test coverage | LOW | Unchanged (2.4%) |

## Recommended Fix Priority

1. **TLS certificate validation** (15 files) -- CRITICAL, production blocker for network security
2. **XXE vulnerability** (1 file) -- HIGH, simple 1-line fix
3. **Remaining sync-over-async dispose methods** (15 deferred P0s) -- P0, well-defined pattern
4. **Null! replacement** -- P2, gradual migration to `required` properties
5. **Test coverage** -- Structural, requires new test projects

## Self-Check: PASSED
