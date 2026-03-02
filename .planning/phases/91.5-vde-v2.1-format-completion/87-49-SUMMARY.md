---
phase: 91.5-vde-v2.1-format-completion
plan: 49
subsystem: VDE Preamble
tags: [preamble, composition, profiles, layout, vopt-66]
dependency_graph:
  requires: [87-45]
  provides: [PreambleCompositionProfile, PreambleCompositionEngine, PreambleLayoutResult, PreambleProfileType]
  affects: [Preamble creation pipeline, VDE image assembly]
tech_stack:
  added: []
  patterns: [Factory method profiles, Readonly struct layout result, Flag composition with bit shifting]
key_files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/Preamble/PreambleCompositionProfile.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Preamble/PreambleCompositionEngine.cs
  modified: []
decisions:
  - Three named profiles (server-full, embedded-minimal, airgap-readonly) map directly to StrippedKernelBuildSpec and NativeAotPublishSpec factory methods
  - VDE offset uses 4KiB alignment (4096 bytes) via ceiling division
  - Header checksum left as zero in ToHeader; callers compute BLAKE3 after serialization
  - ComputeFlags is static to allow use from PreambleLayoutResult.ToHeader without engine instance
metrics:
  duration: 229s
  completed: 2026-03-02T14:58:30Z
  tasks: 1
  files_created: 2
  files_modified: 0
---

# Phase 91.5 Plan 49: Preamble Composition Profiles & Engine Summary

Three named composition profiles (server-full, embedded-minimal, airgap-readonly) configure preamble content bundling, with a validation engine that computes 4KiB-aligned layout offsets and produces PreambleHeader instances.

## What Was Built

### PreambleCompositionProfile (+ PreambleProfileType enum)
- `PreambleProfileType` enum: ServerFull(0), EmbeddedMinimal(1), AirgapReadonly(2), Custom(3)
- Sealed class with 12 properties: ProfileType, Name, Description, KernelSpec, RuntimeSpec, IncludeSpdk, SpdkTransport, PreambleFlags, Architecture, SignWithEd25519, CompressPreamble, EncryptRuntime
- Three static factory methods wiring to StrippedKernelBuildSpec and NativeAotPublishSpec profiles
- AirgapReadonly profile sets SignWithEd25519=true and PreambleFlags.SignaturePresent

### PreambleCompositionEngine (+ PreambleLayoutResult struct)
- `ComputeLayout(profile, kernelSize, spdkSize, runtimeSize)`: Calculates byte offsets for kernel (starts at byte 64), optional SPDK, runtime, 32-byte BLAKE3 hash, optional 64-byte Ed25519 signature, then 4KiB-aligns VDE offset
- `ValidateProfile(profile)`: Returns validation errors for architecture mismatches, SPDK/vfio-pci dependency, flag-boolean consistency, unknown transports
- `ComputeFlags(profile)`: Static method building combined ushort (bits 0-2 features, bits 3-5 architecture)
- `PreambleLayoutResult` readonly struct with all offsets, ToHeader() method, IEquatable, EstimatedTotalMB

## Deviations from Plan

None - plan executed exactly as written.

## Verification

- Build verified: zero errors in new files (pre-existing errors in SpdkBlockDevice.cs and NativeAotScriptGenerator.cs are unrelated)
- Three factory profiles produce valid configurations with correct KernelSpec/RuntimeSpec wiring
- VdeOffset is always 4KiB-aligned via AlignUp ceiling division
- ValidateProfile catches: architecture mismatches, SPDK without vfio-pci, flag inconsistencies, unknown transports

## Commits

| Commit | Description | Files |
|--------|------------|-------|
| `72641ac9` | feat(91.5-87-49): add preamble composition profiles and engine (VOPT-66) | PreambleCompositionProfile.cs, PreambleCompositionEngine.cs |
