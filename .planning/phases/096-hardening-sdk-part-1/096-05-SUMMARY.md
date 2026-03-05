---
phase: 096-hardening-sdk-part-1
plan: 05
subsystem: sdk
tags: [naming-conventions, PascalCase, enum-members, PII, IoRing, IoUring, interface-protocols, TDD, hardening]

# Dependency graph
requires:
  - phase: 096-04
    provides: "SDK hardening findings 711-954 complete"
provides:
  - "All 1249 SDK Part 1 findings hardened with tests"
  - "Phase 96 complete -- SDK Part 1 fully hardened"
affects: [097-hardening-sdk-part-2, 102-coyote-dotcover]

# Tech tracking
tech-stack:
  added: []
  patterns: [PascalCase-enum-members, PascalCase-static-fields, camelCase-local-constants, PII-acronym-casing]

key-files:
  created:
    - DataWarehouse.Hardening.Tests/SDK/CarbonAndInterfaceHardeningTests.cs
    - DataWarehouse.Hardening.Tests/SDK/InfrastructureHardeningTests.cs
    - DataWarehouse.Hardening.Tests/SDK/InterfaceAndIoRingHardeningTests.cs
  modified:
    - DataWarehouse.SDK/Contracts/Interface/InterfaceTypes.cs
    - DataWarehouse.SDK/Contracts/IntelligenceAware/IntelligenceAwarePluginBase.cs
    - DataWarehouse.SDK/VirtualDiskEngine/AdaptiveIndex/IoUringBindings.cs
    - DataWarehouse.SDK/VirtualDiskEngine/IO/Windows/IoRingNativeMethods.cs
    - DataWarehouse.SDK/Sustainability/ICarbonAwareStorage.cs
    - DataWarehouse.SDK/Contracts/InfrastructureContracts.cs

key-decisions:
  - "PIIDetectionResult->PiiDetectionResult: consistent PII->Pii acronym casing across IntelligenceAware contracts"
  - "InterfaceProtocol REST->Rest, gRPC->GRpc: PascalCase for all enum members including protocol names"
  - "IoRingNativeMethods: S-prefix for static Lazy fields (SIsSupported) to avoid property collision"
  - "External library enums (Elastic.Transport.HttpMethod) preserved -- only rename SDK-owned types"

patterns-established:
  - "PII acronym: always Pii in PascalCase (PiiDetectionResult, ContainsPii, RequestPiiDetection)"
  - "Protocol acronym enums: Rest, GRpc, GraphQl, Mqtt, Amqp, Nats (not REST, gRPC, GraphQL)"
  - "HTTP method enums: Get, Post, Put, Delete (not GET, POST, PUT, DELETE)"
  - "IoRing/IoUring constants: PascalCase (IoringOpRead, IoringSetupSqpoll, not IORING_OP_READ)"
  - "Static Lazy<T> fields: S-prefix (SIsSupported) when property with same name exists"

requirements-completed: [HARD-01, HARD-02, HARD-03, HARD-04, HARD-05]

# Metrics
duration: 35min
completed: 2026-03-05
---

# Phase 96 Plan 05: SDK Hardening Findings 955-1249 Summary

**295 SDK findings hardened: PII naming (PIIDetection->PiiDetection), InterfaceProtocol/HttpMethod enum PascalCase, IoRing/IoUring constant renames, plus 100 tests across 3 test files**

## Performance

- **Duration:** ~35 min
- **Started:** 2026-03-05
- **Completed:** 2026-03-05
- **Tasks:** 2
- **Files modified:** 120+ (92 in Task 2 alone, ~30 in Task 1)

## Accomplishments
- Hardened all 295 SDK findings (955-1249) with failing-then-passing tests
- PII naming standardized across IntelligenceAware contracts (PIIDetectionResult->PiiDetectionResult, 6 types/properties)
- InterfaceProtocol enum fully PascalCased (REST->Rest, gRPC->GRpc, GraphQL->GraphQl, etc.)
- HttpMethod enum fully PascalCased (GET->Get through CONNECT->Connect, 9 members)
- IoRing/IoUring 30+ OS-level constants renamed from SCREAMING_CASE to PascalCase
- 42 RAID enum members renamed (RAID_0->Raid0 through RAID_Linear->RaidLinear)
- Cascading renames propagated across 60+ plugin files (UltimateInterface 53 strategies)
- 100 new hardening tests (64 Task 1 + 37 Task 2 = 101 total, minus 1 overlap)
- Phase 96 complete: all 1249 SDK Part 1 findings addressed

## Task Commits

Each task was committed atomically:

1. **Task 1: Findings 955-1100** - `9c0bdc1d` (test+fix)
2. **Task 2: Findings 1101-1249** - `646c76bd` (test+fix)

## Files Created/Modified

### Test Files Created
- `DataWarehouse.Hardening.Tests/SDK/CarbonAndInterfaceHardeningTests.cs` - 35+ tests for carbon, compliance, connector, edge, hardware, hypervisor, I2C, kernel, security, message bus, replication findings
- `DataWarehouse.Hardening.Tests/SDK/InfrastructureHardeningTests.cs` - 29+ tests for incident response, index, inference, infrastructure, RAID, memory findings
- `DataWarehouse.Hardening.Tests/SDK/InterfaceAndIoRingHardeningTests.cs` - 37 tests for PII naming, interface capabilities, protocol enums, HTTP methods, IoRing, IoUring findings

### Key SDK Files Modified (Task 1)
- `DataWarehouse.SDK/Sustainability/ICarbonAwareStorage.cs` - CO2 property/param renames, OffsetStandard enum
- `DataWarehouse.SDK/Contracts/InfrastructureContracts.cs` - 42 RAID enum members to PascalCase
- `DataWarehouse.SDK/Contracts/ICompressionProvider.cs` - LZ4->Lz4 enum rename
- `DataWarehouse.SDK/Edge/Inference/InferenceSettings.cs` - CPU->Cpu, CUDA->Cuda, DirectML->DirectMl, TensorRT->TensorRt

### Key SDK Files Modified (Task 2)
- `DataWarehouse.SDK/Contracts/Interface/InterfaceTypes.cs` - InterfaceProtocol and HttpMethod enums fully PascalCased
- `DataWarehouse.SDK/Contracts/IntelligenceAware/IntelligenceAwarePluginBase.cs` - PII->Pii naming (6 renames)
- `DataWarehouse.SDK/VirtualDiskEngine/AdaptiveIndex/IoUringBindings.cs` - 30+ IORING_*/IOSQE_* constants
- `DataWarehouse.SDK/VirtualDiskEngine/IO/Windows/IoRingNativeMethods.cs` - Struct/constant/field renames

### Cascading Plugin Files (60+)
- 53 files in UltimateInterface (all strategy files referencing InterfaceProtocol/HttpMethod)
- UltimateCompliance, UltimateDataManagement, UltimateIntelligence plugin files
- Kernel test files (SdkInterfaceStrategyTests, UltimateInterfaceTests)

## Decisions Made
- PII->Pii acronym standardized across all IntelligenceAware contracts
- InterfaceProtocol uses strict PascalCase (GRpc not Grpc, GraphQl not Graphql)
- IoRingNativeMethods static Lazy fields use S-prefix (SIsSupported) to avoid collision with IsSupported property
- External library enums (Elastic.Transport.HttpMethod) preserved -- only SDK-owned types renamed
- Multiple projects with own same-named enums (CompressionAlgorithm, ExecutionProvider) each renamed independently

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] IoRingNativeMethods field/property naming collision**
- **Found during:** Task 2
- **Issue:** Renaming `_isSupported` to `IsSupported` collided with existing property `IsSupported`
- **Fix:** Used S-prefix convention: `SIsSupported` and `SIsWriteSupported` for the Lazy<bool> fields
- **Files modified:** IoRingNativeMethods.cs, IoRingBlockDevice.cs
- **Committed in:** 646c76bd

**2. [Rule 1 - Bug] ElasticsearchConnectionStrategy external library enum preserved**
- **Found during:** Task 2
- **Issue:** Sed renamed `ElasticHttpMethod.POST` to `ElasticHttpMethod.Post` but Elastic.Transport.HttpMethod uses UPPER_CASE
- **Fix:** Reverted changes in ElasticsearchConnectionStrategy.cs
- **Files modified:** ElasticsearchConnectionStrategy.cs
- **Committed in:** 646c76bd

**3. [Rule 1 - Bug] Test file type name corrections**
- **Found during:** Task 1 and Task 2
- **Issue:** Multiple test assertions used wrong type names (InputValidation vs InputValidator, SdkCapabilities vs IntelligenceCapabilities)
- **Fix:** Corrected all type references using actual SDK type names
- **Committed in:** 9c0bdc1d, 646c76bd

---

**Total deviations:** 3 auto-fixed (2 bugs, 1 blocking)
**Impact on plan:** All fixes necessary for correctness. No scope creep.

## Issues Encountered
- Cascading PII renames required touching files across 4 plugins (UltimateIntelligence, UltimateCompliance, UltimateDataManagement, UltimateInterface)
- InterfaceProtocol/HttpMethod renames cascaded to 53 strategy files in UltimateInterface alone
- 8 pre-existing Coyote test failures (VdeConcurrencyTests) unrelated to our changes -- documented as known flaky

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Phase 96 COMPLETE: all 1249 SDK Part 1 findings hardened with 730+ tests passing
- Ready for Phase 97: SDK Part 2 (findings 1250-2499)
- All naming conventions established in Phase 96 carry forward as patterns

---
*Phase: 096-hardening-sdk-part-1*
*Plan: 05*
*Completed: 2026-03-05*

## Self-Check: PASSED
- FOUND: InterfaceAndIoRingHardeningTests.cs
- FOUND: CarbonAndInterfaceHardeningTests.cs
- FOUND: InfrastructureHardeningTests.cs
- FOUND: 096-05-SUMMARY.md
- FOUND: 9c0bdc1d (Task 1 commit)
- FOUND: 646c76bd (Task 2 commit)
