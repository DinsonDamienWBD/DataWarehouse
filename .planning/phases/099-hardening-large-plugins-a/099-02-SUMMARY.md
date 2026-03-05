---
phase: 099-hardening-large-plugins-a
plan: 02
subsystem: storage
tags: [hardening, tdd, naming, async-safety, enum-rename, null-check, collection-query, using-var]

requires:
  - phase: 099-hardening-large-plugins-a
    plan: 01
    provides: UltimateStorage findings 1-250 patterns and test infrastructure
provides:
  - UltimateStorage findings 251-500 hardened with TDD tests and production fixes
  - 85 tests across 6 test files in DataWarehouse.Hardening.Tests/UltimateStorage/
  - Enum renames, async Timer safety, unused field exposure, collection query patterns
affects: [099-03 through 099-11, 102-audit, 104-mutation]

tech-stack:
  added: []
  patterns: [enum-PascalCase-rename, async-timer-try-catch, using-var-initializer-safety, null-check-removal-per-NRT, collection-materialization, struct-equality-replacement]

key-files:
  created:
    - DataWarehouse.Hardening.Tests/UltimateStorage/DistributedInfraTests2.cs
    - DataWarehouse.Hardening.Tests/UltimateStorage/NetworkStrategyTests.cs
    - DataWarehouse.Hardening.Tests/UltimateStorage/DecentralizedStrategyTests.cs
    - DataWarehouse.Hardening.Tests/UltimateStorage/SoftwareDefinedStrategyTests.cs
    - DataWarehouse.Hardening.Tests/UltimateStorage/CloudAndConnectorTests2.cs
    - DataWarehouse.Hardening.Tests/UltimateStorage/InnovationAndFeatureTests2.cs
  modified:
    - 33 production files across UltimateStorage plugin

key-decisions:
  - "ConsistencyLevel enum renamed from ALL_CAPS to PascalCase (One, Two, Three, Quorum, LocalQuorum, All)"
  - "Unused fields exposed as internal properties rather than converted to active usage (config pattern)"
  - "Redundant null checks removed per NRT annotations (metadata parameter in 4 SoftwareDefined strategies)"
  - "KubernetesCsi struct equality replaced with explicit key/value comparison to avoid boxing"
  - "InfiniteDedup TenantRefs.Count added to chunk deletion guard (correctness improvement)"
  - "IpfsClient.IdAsync cancellation token not passed (API does not accept CancellationToken)"

patterns-established:
  - "collection-query-fix: Expose never-queried collections via internal property or add meaningful query"
  - "assignment-not-used: Remove = null initializer when out parameter guarantees assignment"
  - "using-var-safety: Object initializers separated from using variable construction"
  - "null-check-removal: Remove null checks on non-nullable NRT parameters"

requirements-completed: [HARD-01, HARD-02, HARD-03, HARD-04, HARD-05]

duration: 36min
completed: 2026-03-06
---

# Phase 099 Plan 02: UltimateStorage Findings 251-500 Summary

**TDD hardening of 250 UltimateStorage findings: ConsistencyLevel enum PascalCase, 30+ unused field exposures, async Timer safety, using-var initializer separation, NRT null check removal, struct equality fix across 33 production files**

## What Was Done

### DistributedStorageInfrastructure (Findings 251-261)
- **Findings 251-256**: ConsistencyLevel enum: ONE/TWO/THREE/QUORUM/LOCAL_QUORUM/ALL -> One/Two/Three/Quorum/LocalQuorum/All (+ 7 cascading references)
- **Finding 257**: Removed unused `batch` List<ReplicationEvent> (only updated, never queried)
- **Finding 258**: Local constant `MaxLogEntries` -> `maxLogEntries` (camelCase)
- **Finding 259**: `_healthCheckInterval` exposed as `internal TimeSpan HealthCheckInterval`
- **Findings 260-261**: PossibleMultipleEnumeration fixed with `.ToList()` materialization

### Network Strategies (Findings 262-267, 277-279, 407-409)
- **Findings 263-264**: EdgeCascade `_edgeCacheMaxBytes` and `_enableCacheWarming` exposed
- **Finding 265**: FcStrategy `_multipathStates` exposed as `IReadOnlyDictionary`
- **Finding 266**: FcStrategy useless subtraction of 0 removed (usedCapacity always 0)
- **Finding 267**: FcStrategy `object? data = null` -> `object? data;`
- **Finding 277**: FtpStrategy `_useCompression` exposed
- **Finding 279**: FtpStrategy `memoryStream.Dispose()` -> `await memoryStream.DisposeAsync()`
- **Finding 407**: IscsiStrategy `_dataPDUInOrder` -> `_dataPduInOrder`
- **Finding 408**: IscsiStrategy `_stream.Dispose()` -> `await _stream.DisposeAsync()`
- **Finding 409**: IscsiStrategy `StartLBA` -> `StartLba`

### Decentralized Strategies (Findings 268-272, 382-406)
- **Findings 269-272**: FilecoinStrategy 4 `= null` redundant assignments removed
- **Finding 386**: IpfsStrategy `Cid? cid = null` -> `Cid? cid;`
- **Findings 384-405**: IpfsStrategy cancellation token awareness verified (15+ findings)

### SoftwareDefined Strategies (Findings 288-321, 410-495)
- **Finding 288**: GlusterFs `_arbiterCount` exposed (30 fields already fixed in prior phases)
- **Finding 318**: GlusterFs `_fileLocks` collection exposed via `FileLockCount`
- **Finding 319**: GlusterFs `bytesWritten = 0` -> `bytesWritten;`
- **Finding 320**: GlusterFs `metadata == null ||` removed (NRT non-nullable)
- **Finding 321**: GlusterFs using-var object initializer separated
- **Findings 322-359**: GpfsStrategy: 2 fields exposed, bytesWritten fixed, null check removed, using-var separated, _fileLocks exposed
- **Findings 410-424**: JuiceFs: bytesWritten fixed, 2 response/xml/json null assignments removed
- **Findings 446-466**: LizardFs: bytesWritten fixed, null check removed
- **Findings 477-495**: Lustre: `s_shellInjectionChars` -> `ShellInjectionChars`, collections exposed, bytesWritten fixed, null check removed, using-var separated

### Cloud/Connector/Other (Findings 280-287, 360-377, 425-500)
- **Finding 280**: GcsArchive `_timeoutSeconds` exposed
- **Finding 282**: GcsStrategy `_enableRequesterPays` exposed
- **Finding 360**: GraphQlConnector `stream.Dispose()` -> `await stream.DisposeAsync()`
- **Finding 361**: GraphQlConnector `ParseGraphQLKey` -> `ParseGraphQlKey`
- **Finding 362**: GrpcConnector `_authToken` exposed
- **Finding 364**: GrpcStorage `_loadBalancingPolicy` exposed
- **Findings 372-375**: IbmCos: `_enableAsperaSupport` exposed, `uploadedSize = 0` fixed, `key_meta` -> `keyMeta`
- **Finding 376**: InfiniteDedup `_enableCrosstenantDedup` exposed
- **Finding 377**: InfiniteDedup `TenantRefs.Count == 0` added to chunk deletion guard
- **Findings 378-381**: InfiniteStorage: `_enableAutoRebalancing`/`_healthCheckTimer` exposed, async Timer try/catch
- **Findings 425-436**: KubernetesCsi: `_socketPath` exposed, null checks removed, struct equality fixed, AES256GCM/CBC -> Aes256Gcm/Cbc
- **Finding 437**: LatencyBased Timer callback wrapped with try/catch
- **Findings 438-439**: LegacyBridge `ConvertToEBCDIC`/`ConvertFromEBCDIC` -> `ConvertToEbcdic`/`ConvertFromEbcdic`
- **Findings 441-445**: Linode: `_enableCors` exposed, `s3ex` -> `s3Ex`, CORS rules materialized
- **Findings 467-471**: LocalFile: MediaType USB/SDCard -> Usb/SdCard
- **Findings 472-476**: LsmTree: `LoadExistingSSTables` -> `LoadExistingSsTables`, dead sstable assignment removed
- **Finding 497**: Memcached `_indexKeyPrefix` exposed
- **Findings 498-499**: Memcached `meta = null` removed, always-true condition simplified
- **Finding 500**: Minio `_useSSL` -> `_useSsl`

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] IpfsClient.IdAsync does not accept CancellationToken**
- **Found during:** Task 1
- **Issue:** Added ct parameter to IdAsync() but API signature only accepts MultiHash
- **Fix:** Reverted to parameterless call; IPFS client handles cancellation internally
- **Commit:** 1c3cee32

**2. [Rule 1 - Bug] FilecoinStrategy dealInfo variable removed but still referenced**
- **Found during:** Task 1
- **Issue:** Replaced dealInfo with discard `_` in DeleteAsyncCore, but dealInfo was used for size tracking below
- **Fix:** Restored dealInfo variable with TryGetValue, restructured to avoid unused initial assignment
- **Commit:** 1c3cee32

## Verification

- `dotnet build DataWarehouse.slnx --no-restore`: 0 errors, 0 warnings
- `dotnet test --filter "FullyQualifiedName~UltimateStorage"`: 152 passed, 0 failed (67 from P01 + 85 new)
- Commit: 1c3cee32

## Files Changed

- 6 test files created (85 new tests)
- 33 production files modified
- 39 files total
