---
phase: 91.5-vde-v2.1-format-completion
plan: 87-43
subsystem: vde-integrity
tags: [tpm, pcr, custody-proof, merkle, attestation, integrity-anchor, vopt-56]

# Dependency graph
requires:
  - phase: 91.5-vde-v2.1-format-completion
    provides: IntegrityAnchor block with MerkleRootHash at offset 0 (32 bytes)
  - phase: 91.5-vde-v2.1-format-completion
    provides: IBlockDevice abstraction for block-level reads

provides:
  - ITpmProvider: abstraction for TPM 2.0 PCR reads, quote generation, and quote verification
  - TpmQuote: signed TPM attestation record (QuoteData, Signature, PcrValues, PcrIndices)
  - CustodyProof: full proof record (MerkleRootHash, VolumeUuid, Timestamp, Nonce, TpmQuote, ProofSignature)
  - CustodyVerificationResult: per-step verification result (MerkleRootMatches, TpmQuoteValid, NonceValid, TimestampValid)
  - CustodyProofConfig: user-configurable PCR indices, hash algorithm, expiry window
  - ProofOfPhysicalCustody: engine that binds IntegrityAnchor MerkleRootHash to TPM PCR state with serialize/deserialize
affects:
  - Phase 92 VDE Decorator Chain (may surface custody proof at VdeMountPipeline checkpoint)
  - Phase 99 E2E certification (VOPT-56 compliance check)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - ITpmProvider injection pattern for hardware-independent TPM abstraction
    - SHA-256(MerkleRootHash || VolumeUuid || UTC-ticks) nonce construction for replay prevention
    - HMAC-SHA256 over ordered proof fields for tamper-evident proof-in-storage
    - Binary serialization with magic (0x43505043) + version header for portable proof transport

key-files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/Integrity/CustodyProofConfig.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Integrity/ProofOfPhysicalCustody.cs
  modified: []

key-decisions:
  - "SHA-256 used in place of BLAKE3 for nonce/signature (BCL substitute; documented as TODO for BCL BLAKE3 arrival)"
  - "ITpmProvider interface injected rather than static TPM calls — enables unit testing without hardware TPM"
  - "CustodyVerificationResult is readonly struct with per-step booleans — callers can distinguish tamper from expiry from platform change"
  - "ProofSignature HMAC key derived from stable domain label — proof signatures portable across ProofOfPhysicalCustody instances"

patterns-established:
  - "Domain label HMAC key derivation: SHA-256(domain-label-ascii) as HMAC key for deterministic proof signatures"
  - "Per-step verification result: separate boolean per verification step to enable granular compliance reporting"

# Metrics
duration: 7min
completed: 2026-03-02
---

# Phase 91.5 Plan 87-43: Proof of Physical Custody Summary

**TPM PCR + MerkleRootHash custody proof engine (VOPT-56) binding VDE volume to physical platform via ITpmProvider abstraction, freshness nonce, HMAC-signed proof record, and portable binary serialization**

## Performance

- **Duration:** 7 min
- **Started:** 2026-03-02T13:54:37Z
- **Completed:** 2026-03-02T13:58:50Z
- **Tasks:** 1
- **Files modified:** 2

## Accomplishments
- `CustodyProofConfig` with PcrIndices, HashAlgorithm, IncludeTimestamp, IncludeVolumeUuid, ProofExpiryHours
- `ITpmProvider` interface (ReadPcrAsync, GenerateQuoteAsync, VerifyQuoteAsync) enabling hardware-independent testing
- `TpmQuote` record (QuoteData, Signature, PcrValues, PcrIndices)
- `CustodyProof` record combining MerkleRootHash, VolumeUuid, Timestamp, Nonce, TpmQuote, ProofSignature
- `CustodyVerificationResult` with five per-step booleans and ProofAge for compliance reporting
- `ProofOfPhysicalCustody` engine: GenerateProofAsync reads IntegrityAnchor block, computes nonce, requests TPM quote, signs proof
- `VerifyProofAsync`: reads current MerkleRootHash, validates all components with detailed result
- `SerializeProof`/`DeserializeProof`: portable binary format with magic + version header

## Task Commits

Each task was committed atomically:

1. **Task 1: CustodyProofConfig and ProofOfPhysicalCustody** - `4eb477af` (feat)

**Plan metadata:** (included in state commit)

## Files Created/Modified
- `DataWarehouse.SDK/VirtualDiskEngine/Integrity/CustodyProofConfig.cs` - User-configurable custody proof parameters
- `DataWarehouse.SDK/VirtualDiskEngine/Integrity/ProofOfPhysicalCustody.cs` - Full custody proof engine with ITpmProvider, TpmQuote, CustodyProof, CustodyVerificationResult, and serialization

## Decisions Made
- SHA-256 used in place of BLAKE3 for nonce computation and HMAC key derivation. BLAKE3 is not yet available in the BCL; SHA-256 provides equivalent security. Both usages are documented with TODO comments indicating the migration path.
- ITpmProvider abstraction chosen over static TPM calls so the engine can be unit-tested without hardware TPM (test implementations inject deterministic PCR values).
- CustodyVerificationResult carries five separate booleans (MerkleRootMatches, TpmQuoteValid, NonceValid, TimestampValid, IsValid) rather than a single pass/fail to enable compliance systems to distinguish between data change, platform change, nonce tamper, and expiry.
- ProofSignature HMAC key derived from a stable ASCII domain label via SHA-256 so proof signatures are consistent across ProofOfPhysicalCustody instances on the same volume without requiring external key management.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] CA1512 linter auto-fix: ArgumentOutOfRangeException.ThrowIfNegative**
- **Found during:** Task 1 (first build attempt)
- **Issue:** The build analyzer (CA1512) rejected `throw new ArgumentOutOfRangeException(...)` when `ArgumentOutOfRangeException.ThrowIfNegative(integrityAnchorBlock)` was available
- **Fix:** Linter auto-applied the fix; incorporated into commit 4eb477af
- **Files modified:** DataWarehouse.SDK/VirtualDiskEngine/Integrity/ProofOfPhysicalCustody.cs
- **Verification:** Build succeeded: 0 errors, 0 warnings
- **Committed in:** 4eb477af (Task 1 commit)

---

**Total deviations:** 1 auto-fixed (Rule 1 - analyzer enforcement)
**Impact on plan:** Trivial style fix; no logic changes. No scope creep.

## Issues Encountered
- ProofOfPhysicalCustody.cs was partially committed by the previous plan's agent (87-44) as a build-unblocking stub. This plan replaced the stub with the full production implementation; the linter auto-applied CA1512 during the first build attempt.

## User Setup Required
None - no external service configuration required. ITpmProvider must be implemented by callers; a hardware TSS.NET implementation and a test double are expected upstream.

## Next Phase Readiness
- VOPT-56 satisfied: ProofOfPhysicalCustody provides TPM PCR + MerkleRootHash custody proof
- ITpmProvider interface ready for TSS.NET production implementation in a future plugin
- VdeMountPipeline (Phase 92) can surface custody proof generation as an optional mount-time checkpoint
- Phase 99 E2E certification can validate VOPT-56 using ITpmProvider test double

---
*Phase: 91.5-vde-v2.1-format-completion*
*Completed: 2026-03-02*
