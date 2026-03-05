---
phase: 097-hardening-sdk-part-2
plan: 01
subsystem: DataWarehouse.SDK
tags: [hardening, naming, security, tdd, enums, p-invoke, hmac, constructor-safety]
dependency_graph:
  requires: [096-05]
  provides: [097-01-hardened-sdk-part2]
  affects: [all-plugins, hardening-tests]
tech_stack:
  added: []
  patterns: [lazy-init-for-virtual-ctor, checked-cast, toctou-snapshot, hmac-key-derivation]
key_files:
  created:
    - DataWarehouse.Hardening.Tests/SDK/Part2NamingHardeningTests.cs
    - DataWarehouse.Hardening.Tests/SDK/Part2LogicHardeningTests.cs
  modified:
    - DataWarehouse.SDK/VirtualDiskEngine/Identity/NamespaceAuthority.cs
    - DataWarehouse.SDK/Contracts/TamperProof/ITamperProofProvider.cs
    - DataWarehouse.SDK/Contracts/LegacyStoragePluginBases.cs
    - DataWarehouse.SDK/Hardware/MacOsHardwareProbe.cs
    - DataWarehouse.SDK/Hardware/Interop/MessagePackSerialization.cs
    - DataWarehouse.SDK/Contracts/Ecosystem/JepsenFaultInjection.cs
    - DataWarehouse.SDK/Contracts/Ecosystem/JepsenTestHarness.cs
    - DataWarehouse.SDK/Contracts/IPluginCapabilityRegistry.cs
    - DataWarehouse.SDK/Contracts/IStorageOrchestration.cs
    - DataWarehouse.SDK/Contracts/Media/MediaTypes.cs
    - DataWarehouse.SDK/Security/SupplyChain/ISbomProvider.cs
    - DataWarehouse.SDK/Infrastructure/KernelInfrastructure.cs
    - DataWarehouse.SDK/VirtualDiskEngine/IO/Unix/KqueueNativeMethods.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Mount/MacFuseNative.cs
    - DataWarehouse.SDK/Hardware/Accelerators/MetalInterop.cs
    - DataWarehouse.SDK/Hardware/Accelerators/OpenClInterop.cs
    - DataWarehouse.SDK/Hardware/NVMe/NvmeInterop.cs
    - DataWarehouse.SDK/VirtualDiskEngine/BlockAllocation/MetaslabAllocator.cs
    - DataWarehouse.SDK/Edge/Mesh/MeshSettings.cs
    - DataWarehouse.SDK/Primitives/Metadata/MetadataTypes.cs
    - DataWarehouse.SDK/Contracts/TamperProof/TamperProofEnums.cs
decisions:
  - "Use lazy initialization pattern to avoid virtual member calls in constructors"
  - "Derive HMAC public key from private key via SHA-512 hash for sign/verify consistency"
  - "Use compile-time enum references instead of reflection Enum.IsDefined for tests with ambiguous types"
  - "Cascade ALL_CAPS-to-PascalCase renames across all 52+ plugins via sed"
metrics:
  duration: ~45min
  completed: 2026-03-06
  tasks_completed: 1
  tasks_total: 1
  tests_added: 103
  tests_total_passing: 824
  files_modified: 88
  findings_covered: 247
---

# Phase 097 Plan 01: SDK Part 2 Hardening (Findings 1250-1499) Summary

TDD hardening of 247 findings across SDK files from IoUringBindings through OpenClInterop, covering CRITICAL HMAC sign/verify mismatch, virtual-call-in-constructor defects, TOCTOU races, integer overflow, and 200+ ALL_CAPS naming violations cascaded across 88 files.

## Task Completion

| Task | Name | Commit | Status |
|------|------|--------|--------|
| 1 | TDD hardening findings 1250-1499 | c0c6b748 | COMPLETE |

## CRITICAL Fixes

### Finding #1463: HMAC Sign/Verify Key Derivation Mismatch (SECURITY)
- **File:** `NamespaceAuthority.cs` (HmacSignatureProvider)
- **Issue:** `Sign()` used raw private key for HMAC, `Verify()` derived a different key from public key. Signatures could never verify.
- **Fix:** `Sign()` now derives the same public key from private key via `SHA512.HashData(privateKey)[0..KeySize]` before HMAC computation, ensuring Sign/Verify consistency.
- **Tests:** 3 tests (round-trip, multi-iteration, tamper detection)

### Finding #1279: Virtual Member Call in Constructor (TamperProofProviderPluginBase)
- **Issue:** Constructor called abstract `Configuration` property, which crashes on derived types.
- **Fix:** Added lazy initialization with `_instanceStatesInitialized` flag and `EnsureInstanceStatesInitialized()` method.

### Finding #1333: Virtual Member Call in Constructor (CacheableStoragePluginBase)
- **Issue:** Constructor called virtual `DefaultCacheOptions` property.
- **Fix:** Constructor uses concrete `CacheOptions` defaults; virtual property used only after construction.

## HIGH Fixes

| Finding | File | Issue | Fix |
|---------|------|-------|-----|
| #1276-1278 | ITamperProofProvider.cs | Unused ctor fields | Exposed as protected properties |
| #1280 | TimeLockProviderPluginBase | Nullable condition always true | Simplified condition |
| #1285-1289 | JepsenFaultInjection.cs | Process object initializer in using, new Random() | Separate assignment, Random.Shared |
| #1290-1293 | JepsenTestHarness.cs | Disposed variable access, object initializer | Separate assignment pattern |
| #1334 | LegacyStoragePluginBases.cs | Async void lambda in timer | Changed to `_ = CleanupExpiredAsync()` |
| #1381 | MacOsHardwareProbe.cs | TOCTOU race on volatile field | Snapshot volatile once |
| #1429 | MessagePackSerialization.cs | Integer overflow UInt64->Int64 | `checked((long)...)` cast |
| #1438 | MetalInterop.cs | IsCpuFallback inversion | `!_isAvailable` |

## Naming Convention Fixes (200+ Members)

Renamed ALL_CAPS enum members and P/Invoke constants to PascalCase:

| Area | Examples | Count |
|------|----------|-------|
| CapabilityCategory | AI->Ai, RAID->Raid | 2 |
| ComplianceMode | HIPAA->Hipaa, GDPR->Gdpr | 2 |
| EncryptionLevel | AES128->Aes128, AES256WithHSM->Aes256WithHsm | 3 |
| HashAlgorithmType | SHA256->Sha256, BLAKE2->Blake2 | 5+ |
| SbomFormat | CycloneDX_1_5_Json->CycloneDx15Json | 4 |
| MediaFormat | FLV->Flv, HLS->Hls, CMAF->Cmaf, CR2->Cr2 | 23 |
| PlatformFlags | MacOS->MacOs, FreeBSD->FreeBsd | 2 |
| MeshProtocol | BLE->Ble | 1 |
| MorphLevel | Level0_FlatBitmap->Level0FlatBitmap | 4 |
| KqueueNativeMethods | O_EXCL->OExcl, EVFILT_AIO->EvfiltAio | 15+ |
| MacFuseNative | ENOENT->Enoent, S_IFREG->SIfreg | 35+ |
| NvmeInterop | IOCTL_*->IoctlStorageProtocolCommand | 12+ |
| OpenClInterop | CL_SUCCESS->ClSuccess | 10+ |
| MetalInterop | MTL_*->Mtl* | 3 |
| TamperProofEnums | SHA256->Sha256, HMAC_SHA256->HmacSha256 | 10+ |
| KernelInfrastructure | AI*->Ai* (types and enum) | 6 |

## Cascade Impact

88 files modified across:
- **SDK:** 43 files (source of all renames)
- **Plugins:** 20 files (UltimateIntelligence, UltimateRAID, UltimateStorage, UltimateInterface, UltimateDataIntegrity, Transcoding.Media)
- **Tests:** 25 files (existing hardening tests + 2 new test files)

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Ambiguous MediaFormat enum in tests**
- **Found during:** Test verification
- **Issue:** Two `MediaFormat` enums exist in SDK (Media.MediaFormat and ActiveStoragePluginBases.MediaFormat). Reflection `First()` found the wrong one.
- **Fix:** Switched from `Enum.IsDefined(type, string)` to direct compile-time `MediaMediaFormat.Xyz.ToString()` with type alias.

**2. [Rule 3 - Blocking] Local enum definitions not cascaded**
- **Found during:** Build verification
- **Issue:** `QuantumSafeIntegrity.cs` and `KubernetesCsiStorageStrategy.cs` had local enum definitions with ALL_CAPS members that referenced SDK enums.
- **Fix:** Renamed local enum members to match SDK PascalCase convention.

**3. [Rule 1 - Bug] Volatile.Read warning on already-volatile field**
- **Found during:** MacOsHardwareProbe fix
- **Issue:** CS0420 warning using `Volatile.Read` on field already marked `volatile`.
- **Fix:** Used direct field read `var cached = _lastDiscovery` which is safe for volatile fields.

## Verification

- Build: 0 errors, 0 warnings
- Tests: 824/824 SDK hardening tests passing
- New tests: 103 (84 naming + 19 logic)
- Key plugins verified: UltimateIntelligence, UltimateRAID, UltimateStorage
