---
phase: 91.5-vde-v2.1-format-completion
plan: 47
subsystem: vde-preamble
tags: [linux-kernel, build-script, kconfig, vfio-pci, docker, bare-metal]

requires:
  - phase: 91.5-vde-v2.1-format-completion/87-45
    provides: PreambleHeader with TargetArchitecture enum and KernelSize uint32 field
provides:
  - StrippedKernelBuildSpec with three factory profiles (ServerFull, EmbeddedMinimal, AirgapReadonly)
  - KernelBuildScriptGenerator producing deterministic bash and Docker build scripts
  - Kconfig generation for USB/NIC/display/vfio-pci with architecture-aware targeting
affects: [87-48, 87-49, preamble-assembly, devops-pipeline]

tech-stack:
  added: []
  patterns: [factory-method-profiles, kconfig-generation, multi-stage-docker-builds]

key-files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/Preamble/StrippedKernelBuildSpec.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Preamble/KernelBuildScriptGenerator.cs
  modified: []

key-decisions:
  - "Monolithic kernel (CONFIG_MODULES=n) for simplicity and reduced attack surface"
  - "vfio-pci enforced as mandatory via validation — SPDK handoff is non-negotiable"
  - "ValidateSpec returns warnings list for non-fatal issues, throws for fatal"

patterns-established:
  - "Factory profiles for deployment variants: ServerFull/EmbeddedMinimal/AirgapReadonly"
  - "Kconfig generation as ordered list of CONFIG_xxx=y/n entries"

duration: 5min
completed: 2026-03-02
---

# Phase 91.5 Plan 47: Stripped Kernel Build Spec Summary

**C# helper classes generating deterministic Linux kernel build scripts with USB/NIC/display/vfio-pci kconfig profiles for DWVD bootable preamble**

## Performance

- **Duration:** 5 min
- **Started:** 2026-03-02T14:47:52Z
- **Completed:** 2026-03-02T14:52:52Z
- **Tasks:** 1
- **Files modified:** 2

## Accomplishments
- StrippedKernelBuildSpec with three factory profiles covering server, embedded, and air-gapped deployments
- Deterministic kconfig generation with architecture-aware entries (x86_64, aarch64, riscv64)
- KernelBuildScriptGenerator producing both bare-metal bash scripts and multi-stage Dockerfiles
- Validation enforcing vfio-pci requirement and uint32 PreambleHeader.KernelSize limit

## Task Commits

Each task was committed atomically:

1. **Task 1: StrippedKernelBuildSpec and KernelBuildScriptGenerator** - `c1db0782` (feat)

## Files Created/Modified
- `DataWarehouse.SDK/VirtualDiskEngine/Preamble/StrippedKernelBuildSpec.cs` - Kernel build configuration with factory profiles and kconfig generation
- `DataWarehouse.SDK/VirtualDiskEngine/Preamble/KernelBuildScriptGenerator.cs` - Bash and Docker build script generator with spec validation

## Decisions Made
- Monolithic kernel (CONFIG_MODULES=n) eliminates module loading complexity and reduces attack surface
- vfio-pci enforced as mandatory via ArgumentException — preamble boot without SPDK handoff is invalid
- ValidateSpec returns IReadOnlyList<string> warnings for non-fatal issues (e.g., no I/O path) while throwing for fatal errors
- RiscV64 architecture support included alongside x86_64 and aarch64 for future-proofing

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
- Pre-existing build errors in SpdkBlockDevice.cs (4 errors: unsafe context + fixed statement issues) — unrelated to this plan, no action taken

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Kernel build spec and script generator complete, ready for preamble assembly pipeline
- DevOps tooling can consume generated scripts to produce bzImage for embedding

---
*Phase: 91.5-vde-v2.1-format-completion*
*Completed: 2026-03-02*
