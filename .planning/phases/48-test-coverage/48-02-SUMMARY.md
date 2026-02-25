---
phase: "48"
plan: "02"
subsystem: "test-infrastructure"
tags: [integration-tests, cross-plugin-flows, gap-assessment]
dependency-graph:
  requires: [48-01-unit-test-gaps]
  provides: [integration-test-gap-map]
  affects: [all-plugins, sdk, kernel]
tech-stack:
  added: []
  patterns: [xunit-v3, fluent-assertions]
key-files:
  created: []
  modified: []
decisions:
  - "Analysis only - no code modifications"
metrics:
  completed: "2026-02-17"
---

# Phase 48 Plan 02: Integration Test Gap Analysis

**One-liner:** 29 integration-style tests exist across 2 files, covering only message bus pub/sub and V3 type instantiation, with zero tests for the 12 critical cross-plugin data flows.

## Current Integration Test Inventory

### Dedicated Integration Test Files

| File | Tests | What It Tests | True Integration? |
|------|-------|---------------|-------------------|
| `Integration/SdkIntegrationTests.cs` | 6 | MessageBus pub/sub, InMemoryStorage, unsubscribe | Partial - uses test doubles |
| `V3Integration/V3ComponentTests.cs` | 23 | StorageAddress, VDE, Federation, Hardware, Deployment types | No - type instantiation only |

### Tests Marked as Integration But Are Unit Tests

The V3ComponentTests file tests individual type construction (StorageAddress, VdeOptions, Superblock serialization, BitmapAllocator) without cross-component interaction. These are unit tests despite the namespace name.

The SdkIntegrationTests file uses TestMessageBus and InMemoryTestStorage (in-memory test doubles), testing component interaction patterns but not real plugin-to-plugin flows.

### Other Files with Cross-Component Aspects

| File | Tests | Cross-Component Aspect |
|------|-------|----------------------|
| `Infrastructure/EnvelopeEncryptionIntegrationTests.cs` | 11 | Encryption + Key Management interaction |
| `TamperProof/WritePipelineTests.cs` | 21 | Multi-provider tamper-proof write flow |
| `TamperProof/ReadPipelineTests.cs` | 18 | Multi-provider tamper-proof read flow |
| `Compliance/ComplianceTestSuites.cs` | 50 | HIPAA/PCI-DSS/FedRAMP/SOC2/FAANG compliance scenarios |
| `Performance/PerformanceBaselineTests.cs` | 6 | Cross-cutting performance measurement |

**Total integration-adjacent tests:** ~106 (but most use mocks/stubs, not real plugin instances)

## Critical Cross-Plugin Flows with NO Integration Tests

### Data Flow: Write Pipeline (Priority 1 - CRITICAL)

**Flow:** Client -> CLI/API -> Kernel -> Encryption -> Compression -> Storage -> RAID -> Replication
**Components involved:** 7+ plugins
**Current coverage:** TamperProof has its own write pipeline test, but the main SDK write pipeline has ZERO integration tests
**Risk:** Data corruption, silent write failures, encryption-before-compression ordering bugs

### Data Flow: Read Pipeline (Priority 2 - CRITICAL)

**Flow:** Client -> CLI/API -> Kernel -> Storage -> RAID Reconstruct -> Decompress -> Decrypt -> Return
**Components involved:** 7+ plugins
**Current coverage:** TamperProof has its own read pipeline test, but the main SDK read pipeline has ZERO integration tests
**Risk:** Data retrieval failures, decompression-decryption ordering bugs, RAID rebuild failures

### Data Flow: RAID Rebuild (Priority 3 - CRITICAL)

**Flow:** Failure Detection -> RAID -> Storage -> Replication -> Rebuild -> Verification
**Components involved:** UltimateRAID, UltimateStorage, UltimateReplication
**Current coverage:** UltimateRAIDTests has 10 unit tests, but no multi-plugin rebuild test
**Risk:** Data loss during disk failure, incomplete rebuild, rebuild corruption

### Data Flow: Cluster Formation (Priority 4 - HIGH)

**Flow:** Node Discovery -> Consensus -> Leader Election -> State Sync -> Replication Setup
**Components involved:** UltimateConsensus, UltimateReplication, UltimateConnector
**Current coverage:** UltimateConsensusTests has 15 unit tests (single-node), no multi-node cluster test
**Risk:** Split-brain, failed leader election, state divergence

### Data Flow: Key Rotation (Priority 5 - HIGH)

**Flow:** Key Management -> Generate New Key -> Re-encrypt Data -> Update References -> Verify
**Components involved:** UltimateKeyManagement, UltimateEncryption, UltimateStorage
**Current coverage:** UltimateKeyManagementTests has 18 tests, EnvelopeEncryptionIntegrationTests has 11, but no end-to-end rotation test
**Risk:** Data inaccessible after rotation, partial rotation leaving mixed-key data

### Data Flow: Backup & Restore (Priority 6 - HIGH)

**Flow:** Snapshot -> Compress -> Encrypt -> Transfer -> Store -> Verify Integrity
**Components involved:** UltimateDataProtection, UltimateCompression, UltimateEncryption, UltimateStorage
**Current coverage:** ZERO tests
**Risk:** Backup corruption, restore failure, data loss

### Data Flow: Message Bus Event Chain (Priority 7 - MEDIUM)

**Flow:** Event Published -> Subscribers Notified -> Handler Executed -> Response Returned
**Components involved:** SDK MessageBus, all plugins
**Current coverage:** 6 tests with TestMessageBus (mock), no real bus integration test
**Risk:** Message loss, handler ordering issues, dead letter queue overflow

### Data Flow: Plugin Lifecycle (Priority 8 - MEDIUM)

**Flow:** Discovery -> Load -> Initialize -> Configure -> Health Check -> Shutdown
**Components involved:** Kernel, all plugins
**Current coverage:** PluginSystemTests has 10 tests, KernelContractTests has 7
**Risk:** Plugin load order failures, initialization deadlocks, shutdown resource leaks

### Data Flow: Multi-Cloud Failover (Priority 9 - MEDIUM)

**Flow:** Primary Failure -> Health Check -> Failover Decision -> Data Redirect -> Consistency Check
**Components involved:** UltimateMultiCloud, UltimateConnector, UltimateStorage
**Current coverage:** ZERO tests
**Risk:** Failover delay, data inconsistency across clouds

### Data Flow: Data Sovereignty Enforcement (Priority 10 - MEDIUM)

**Flow:** Request -> Geo Check -> Policy Lookup -> Route to Allowed Region -> Audit Log
**Components involved:** UltimateCompliance, UltimateDataGovernance, UltimateStorage
**Current coverage:** DataSovereigntyEnforcerTests has 23 tests (unit-level only)
**Risk:** Data stored in wrong jurisdiction, compliance violation

### Data Flow: Streaming Ingestion (Priority 11 - MEDIUM)

**Flow:** Stream Source -> Buffer -> Transform -> Validate -> Store -> Acknowledge
**Components involved:** UltimateStreamingData, UltimateDataFormat, UltimateStorage
**Current coverage:** ZERO tests
**Risk:** Stream data loss, backpressure failures, OOM on unbounded streams

### Data Flow: Intelligence Pipeline (Priority 12 - LOW)

**Flow:** Data -> Feature Extract -> Model Inference -> Result Store -> Alert
**Components involved:** UltimateIntelligence, UltimateStorage, UniversalObservability
**Current coverage:** UniversalIntelligenceTests has 11 unit tests, no pipeline test
**Risk:** Inference failures, stale model data

## Integration Test Infrastructure Gaps

| Gap | Impact | Effort |
|-----|--------|--------|
| No real plugin instantiation in tests | Tests don't catch plugin loading/wiring bugs | Medium |
| No multi-node test harness | Cannot test cluster, replication, consensus | High |
| No database test containers | Cannot test real SQL/NoSQL backends | Medium |
| No network simulation | Cannot test failover, partition tolerance | High |
| No test data fixtures | Each test creates ad-hoc data | Low |
| Single test project | Slow build, no plugin isolation | Medium |

## Recommendations

1. **Immediate (P0):** Create integration test for the write pipeline: Encryption -> Compression -> Storage round-trip with real plugin instances
2. **Immediate (P0):** Create integration test for RAID rebuild with simulated disk failure
3. **Short-term (P1):** Add cluster formation test using in-process multi-node setup
4. **Short-term (P1):** Add key rotation integration test covering re-encryption flow
5. **Medium-term (P2):** Set up test containers (Docker) for database integration tests
6. **Medium-term (P2):** Create network fault injection test harness for failover scenarios
7. **Infrastructure:** Split test project into `DataWarehouse.Tests.Unit` and `DataWarehouse.Tests.Integration`

## Self-Check: PASSED
- V3ComponentTests.cs verified: 23 test methods
- SdkIntegrationTests.cs verified: 6 test methods
- All plugin flow gaps identified from project references and source analysis
