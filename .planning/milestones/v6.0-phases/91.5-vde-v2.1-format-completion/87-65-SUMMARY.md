---
phase: 91.5-vde-v2.1-format-completion
plan: 87-65
subsystem: vde-format
tags: [vde, module-registry, uint64, manifest, block-type-tags, format-constants]

# Dependency graph
requires:
  - phase: 91-compound-block-device-raid
    provides: CompoundBlockDevice, RAID device-level infrastructure
provides:
  - ModuleManifestField with 64-bit ulong backing (was 32-bit uint)
  - ModuleId enum with 39 entries covering bits 0-38
  - ModuleRegistry with all 39 VdeModule definitions including v2.1 modules
  - FormatConstants.DefinedModules=39, MaxModules=64
  - BlockTypeTags.WALS, ZNSM, OPJR, TRLR new tags in KnownTags set
affects: [92-vde-decorator-chain, 92.5-workload-format, WalSubscribers, ZnsZoneMap, OnlineOps]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "64-bit module manifest: ulong Value with 1UL << shift for bits 0-63"
    - "Registry method overloads accept ulong (uint implicitly widens for backward compat)"
    - "v2.1 modules are opt-in: MaxSecurity manifest stays at 0x0007_FFFF (bits 0-18)"

key-files:
  created: []
  modified:
    - DataWarehouse.SDK/VirtualDiskEngine/Format/ModuleManifest.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Format/FormatConstants.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Format/BlockTypeTags.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Format/ModuleDefinitions.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Format/VdeCreationProfile.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Verification/Tier1ModuleVerifier.cs
    - DataWarehouse.Tests/VdeFormat/VdeFormatModuleTests.cs
    - DataWarehouse.Tests/Integration/VdeComposerTests.cs
    - DataWarehouse.CLI/Commands/VdeCommands.cs

key-decisions:
  - "ModuleManifestField.Value changed from uint to ulong; Serialize/Deserialize now writes 8 bytes (was 4)"
  - "SuperblockV2.ModuleManifest stays uint for on-disk backward compat; VdeCreationProfile.ModuleManifest stays uint"
  - "Registry methods (GetActiveModules, CalculateTotalInodeFieldBytes, etc.) upgraded to ulong param; uint callers implicitly widen"
  - "MaxSecurity profile keeps manifest 0x0007_FFFF (bits 0-18 only); v2.1 modules opt-in via custom profiles"
  - "BuildAllModulesMaxConfig loops only 0-18 to match MaxSecurity manifest active bits"
  - "Pre-existing DataBlocks>0 test failures fixed: 1024-block volume too small (inode table min=1024)"

patterns-established:
  - "1UL << (int)moduleId for module bit shifts in 64-bit manifest context"
  - "Explicit (uint) cast when passing ulong manifest.Value to uint fields (on-disk compat)"

# Metrics
duration: 23min
completed: 2026-03-02
---

# Phase 91.5 Plan 87-65: Expand Module Registration (VOPT-87) Summary

**ModuleManifestField upgraded from uint32 to uint64 with 39-entry ModuleId enum and full ModuleRegistry covering all v2.1 modules (CPSH through WLCK)**

## Performance

- **Duration:** 23 min
- **Started:** 2026-03-02T11:57:12Z
- **Completed:** 2026-03-02T12:20:22Z
- **Tasks:** 2
- **Files modified:** 9

## Accomplishments
- ModuleManifestField now uses ulong backing (64-bit), serializes to 8 bytes, covers bits 0-63
- FormatConstants: MaxModules=64, DefinedModules=39 (was 32/19)
- BlockTypeTags: added WALS (0x57414C53), ZNSM (0x5A4E534D), OPJR (0x4F504A52), TRLR (0x54524C52)
- ModuleId enum expanded with 20 new entries: ComputePushdown through WormTimeLock (bits 19-38)
- ModuleRegistry.BuildModules() now registers all 39 VdeModule entries with correct spec metadata

## Task Commits

Each task was committed atomically:

1. **Task 1: Expand ModuleManifestField to uint64 and update FormatConstants** - `44baff1f` (feat)
2. **Task 2: Register modules 19-38 in ModuleId enum and ModuleRegistry** - `e5ece4f2` (feat)

## Files Created/Modified
- `DataWarehouse.SDK/VirtualDiskEngine/Format/ModuleManifest.cs` - ulong Value, 8-byte serialize, AllModules mask 0x0000_007F_FFFF_FFFFuL
- `DataWarehouse.SDK/VirtualDiskEngine/Format/FormatConstants.cs` - MaxModules=64, DefinedModules=39
- `DataWarehouse.SDK/VirtualDiskEngine/Format/BlockTypeTags.cs` - WALS, ZNSM, OPJR, TRLR tags + FrozenSet
- `DataWarehouse.SDK/VirtualDiskEngine/Format/ModuleDefinitions.cs` - 39-entry enum, 39 registry entries, ulong manifest methods
- `DataWarehouse.SDK/VirtualDiskEngine/Format/VdeCreationProfile.cs` - BuildAllModulesMaxConfig loops 0-18 only
- `DataWarehouse.SDK/VirtualDiskEngine/Verification/Tier1ModuleVerifier.cs` - fixed 1UL shift for module IDs >= 32
- `DataWarehouse.Tests/VdeFormat/VdeFormatModuleTests.cs` - updated assertions for 39 modules
- `DataWarehouse.Tests/Integration/VdeComposerTests.cs` - cast ulong->uint, 39 modules, larger volumes
- `DataWarehouse.CLI/Commands/VdeCommands.cs` - cast ulong->uint for VdeCreationProfile

## Decisions Made
- ModuleManifestField.Value changed from uint to ulong; Serialize/Deserialize now writes 8 bytes (was 4)
- SuperblockV2.ModuleManifest stays uint for on-disk backward compat; VdeCreationProfile.ModuleManifest stays uint
- Registry methods (GetActiveModules, CalculateTotalInodeFieldBytes, etc.) upgraded to ulong param; uint callers implicitly widen
- MaxSecurity profile keeps manifest 0x0007_FFFF (bits 0-18 only); v2.1 modules opt-in via custom profiles

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed uint->ulong cascade in calling code**
- **Found during:** Task 1 (ModuleManifest upgrade)
- **Issue:** After upgrading ModuleManifestField.Value to ulong, multiple callers assigning manifest.Value to uint fields (VdeCreationProfile.ModuleManifest, VdeCreationProfile.Custom param) failed to compile
- **Fix:** Added explicit `(uint)manifest.Value` casts in CLI/VdeCommands.cs and test files
- **Files modified:** DataWarehouse.CLI/Commands/VdeCommands.cs, DataWarehouse.Tests/Integration/VdeComposerTests.cs
- **Verification:** Build succeeds 0 errors
- **Committed in:** 44baff1f (Task 1 commit)

**2. [Rule 1 - Bug] Fixed Tier1ModuleVerifier uint shift overflow**
- **Found during:** Task 1 (reviewing callers after ulong upgrade)
- **Issue:** `uint testManifest = 1u << (int)module.Id` would overflow silently for module IDs >= 32 (new v2.1 modules bits 32-38)
- **Fix:** Changed to `ulong testManifest = 1UL << (int)module.Id`
- **Files modified:** DataWarehouse.SDK/VirtualDiskEngine/Verification/Tier1ModuleVerifier.cs
- **Verification:** Build succeeds, no shift overflow
- **Committed in:** 44baff1f (Task 1 commit)

**3. [Rule 1 - Bug] Fixed pre-existing DataBlocks>0 test failures**
- **Found during:** Task 2 (running tests)
- **Issue:** Tests `VdeCreator_CalculateLayout_MinimalProfile_HasCoreRegions` and `CalculateLayout_StandardProfile_HasExpectedRegions` using Minimal(1024)/Standard(1024) had DataBlocks=0 because inode table minimum is 1024 blocks (eating all available space)
- **Fix:** Updated tests to use Minimal(4096) and Standard(8192) for sufficient space
- **Files modified:** DataWarehouse.Tests/VdeFormat/VdeFormatModuleTests.cs, DataWarehouse.Tests/Integration/VdeComposerTests.cs
- **Verification:** Both tests now pass; confirmed failures were pre-existing (not introduced by plan)
- **Committed in:** e5ece4f2 (Task 2 commit)

---

**Total deviations:** 3 auto-fixed (2 Rule 1 bugs, 1 Rule 1 pre-existing)
**Impact on plan:** All auto-fixes necessary for correctness and compilation. No scope creep.

## Issues Encountered
- Pre-existing LegacyMigrationTests failures (3 tests) - confirmed unrelated to this plan's changes

## Next Phase Readiness
- ModuleRegistry infrastructure ready for v2.1 module implementations
- WALS module infrastructure ready for Phase 92 WAL decorator implementation
- ZNSM module infrastructure ready for ZNS zone map region implementation
- FormatConstants.DefinedModules=39 drives all module loops automatically

---
*Phase: 91.5-vde-v2.1-format-completion*
*Completed: 2026-03-02*
