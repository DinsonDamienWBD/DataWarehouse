# DataWarehouse Production Readiness - Implementation Plan

## Executive Summary

This document outlines the implementation plan for achieving full production readiness across all deployment tiers (Individual, SMB, Enterprise, High-Stakes, Hyperscale). Tasks are ordered by priority and organized by severity.

---

## IMPLEMENTATION STRATEGY

Before implementing any task:
1. Read this TODO.md
2. Read Metadata/CLAUDE.md
3. Read Metadata/RULES.md
4. Plan implementation according to the rules and style guidelines (minimize code duplication, maximize reuse)
5. Implement according to the implementation plan
6. Update Documentation (XML docs for all public entities - functions, variables, enums, classes, interfaces etc.)
7. At each step, ensure full production readiness, no simulations, placeholders, mocks, simplifications or shortcuts
8. Add Test Cases for each feature

---

## COMMIT STRATEGY

After completing each task:
1. Verify the actual implemented code to see that the implementation is fully production ready without any simulations, placeholders, mocks, simplifications or shortcuts
2. If the verification fails, continue with the implementation of this task, until the code reaches a level where it passes the verification.
3. Only after it passes verification, update this TODO.md with completion status
4. Commit changes and the updated TODO.md document with descriptive message
5. Move to next task

Do NOT wait for an entire phase to complete before committing.

---

## NOTES

- Follow the philosophy of code reuse: Leverage existing abstractions before creating new ones
- Commit frequently to avoid losing work
- Test each feature thoroughly before moving to the next
- Document all security-related changes

---

## Plugin Implementation Checklist

For each plugin:
1. [ ] Create plugin project in `Plugins/DataWarehouse.Plugins.{Name}/`
2. [ ] Implement plugin class extending appropriate base class
3. [ ] Add XML documentation for all public members
4. [ ] Register plugin in solution file DataWarehouse.slnx
5. [ ] Add unit tests
6. [ ] Update this TODO.md with completion status

---

## TO DO - Code Review Fixes (2026-01-23)

Based on comprehensive code review (see `Metadata/CODE_REVIEW_REPORT.md`), the following issues must be fixed before production deployment.

---

### CRITICAL Severity (Must Fix Before ANY Deployment)

#### 1. Delete Obsolete DatabaseInfrastructure.cs
**File:** `DataWarehouse.SDK/Database/DatabaseInfrastructure.cs`
**Issue:** This entire file (~1,000 lines) is obsolete and superseded by existing infrastructure

**Analysis:** All functionality is already provided by:
- `StorageConnectionRegistry.cs` - Connection pooling and instance management
- `HybridDatabasePluginBase.cs` - Message-based command handling for all database operations

All three database plugins (Relational, NoSQL, Embedded) already extend `HybridDatabasePluginBase<TConfig>`.

**Redundant classes to be removed:**
| Class | Replacement |
|-------|-------------|
| `ConnectionRegistry` | `StorageConnectionRegistry<TConfig>` |
| `ConnectionInstance` | `StorageConnectionInstance<TConfig>` |
| `PooledConnection` | `PooledStorageConnection<TConfig>` |
| `DatabaseFunctionAdapter` | Message handlers in HybridDatabasePluginBase |
| `StorageFunctionAdapter` | `SaveAsync`, `LoadAsync`, `DeleteAsync` in base |
| `IndexFunctionAdapter` | Index message handlers in base |
| `CacheFunctionAdapter` | Cache message handlers in base |
| `MetadataFunctionAdapter` | Metadata message handlers in base |
| `ConnectionRole` enum | `StorageRole` enum in IStorageOrchestration.cs |
| `InstanceHealth` enum | `InstanceHealthStatus` enum |

| Task | Status |
|------|--------|
| Verify no code references DatabaseInfrastructure.cs | [x] |
| Delete `DataWarehouse.SDK/Database/DatabaseInfrastructure.cs` | [x] |
| Run full solution build to confirm no breakage | [x] |
| Update any imports/usings if needed | [x] |
| Add unit tests verifying deletion | [x] |

---

#### 2. Fix EmbeddedDatabasePlugin - Replace In-Memory Simulation
**File:** `Plugins/DataWarehouse.Plugins.EmbeddedDatabaseStorage/EmbeddedDatabasePlugin.cs:48-82`
**Issue:** Uses `ConcurrentDictionary` instead of actual SQLite/LiteDB/RocksDB

| Task | Status |
|------|--------|
| Implement real SQLite connection via `Microsoft.Data.Sqlite` | [x] |
| Implement real LiteDB connection via `LiteDB` package | [x] |
| Implement real RocksDB connection via `RocksDb` package | [x] |
| Replace `_inMemoryTables` with actual database operations | [x] |
| Fix `CreateConnectionAsync` to return real connections | [x] |
| Add connection pooling and proper disposal | [x] |
| Add unit tests with actual database files | [x] |

---

#### 3. Fix RelationalDatabasePlugin - Replace In-Memory Simulation
**File:** `Plugins/DataWarehouse.Plugins.RelationalDatabaseStorage/RelationalDatabasePlugin.cs:54-81`
**Issue:** Uses `ConcurrentDictionary` instead of actual PostgreSQL/MySQL/SQL Server

| Task | Status |
|------|--------|
| Implement real PostgreSQL connection via `Npgsql` | [x] |
| Implement real MySQL connection via `MySqlConnector` | [x] |
| Implement real SQL Server connection via `Microsoft.Data.SqlClient` | [x] |
| Replace `_inMemoryStore` with actual ADO.NET operations | [x] |
| Fix `CreateConnectionAsync` to return real `DbConnection` | [x] |
| Add connection string validation and pooling | [x] |
| Add unit tests with actual database connections | [x] |

---

#### 4. Fix VFS Base Class NotImplementedException
**File:** `DataWarehouse.SDK/Contracts/PluginBase.cs:843`
**Issue:** Base `LoadAsync` throws instead of being abstract

| Task | Status |
|------|--------|
| Change `LoadAsync` to abstract method OR provide default implementation | [x] |
| Review all storage plugins to ensure they override `LoadAsync` | [x] |
| Fix fire-and-forget `TouchAsync` bug (line 841) | [x] |
| Add compile-time enforcement for required overrides | [x] |

---

#### 5. Fix GeoReplicationPlugin - Compilation Errors
**File:** `Plugins/DataWarehouse.Plugins.GeoReplication/GeoReplicationPlugin.cs`
**Issues:**
1. References non-existent `GeoReplicationManager` class (lines 36, 55)
2. Missing required `StartAsync`/`StopAsync` abstract method implementations

**Architecture Context:**
- `GeoReplicationPlugin` extends `ReplicationPluginBase` → `FeaturePluginBase`
- `FeaturePluginBase` requires `StartAsync(CancellationToken ct)` and `StopAsync()` implementations
- Reference implementations: `CrdtReplicationPlugin.cs:195-208`, `FederationPlugin.cs:124-176`

| Task | Status |
|------|--------|
| Add `StartAsync(CancellationToken ct)` override (follow CrdtReplication pattern) | [x] |
| Add `StopAsync()` override with proper cleanup | [x] |
| Add `CancellationTokenSource` field for lifecycle management | [x] |
| Create `GeoReplicationManager` class with full implementation | [x] |
| Implement replication lag tracking with real metrics (not zeros) | [x] |
| Implement cross-region data sync | [x] |
| Add conflict resolution logic | [x] |
| Add unit tests for geo-replication scenarios | [x] |

---

### HIGH Severity (Must Fix Before Enterprise Deployment)

#### 6. Fix Silent Exception Swallowing in Compliance/Backup Plugins
**Files:**
- `Plugins/DataWarehouse.Plugins.Compliance/GdprCompliancePlugin.cs:819`
- `Plugins/DataWarehouse.Plugins.Backup/BackupPlugin.cs:493-496`

**Issue:** Empty `catch { }` blocks silently swallow exceptions

| Task | Status |
|------|--------|
| Replace empty catch in `GdprCompliancePlugin.cs` with proper logging | [x] |
| Replace empty catch in `BackupPlugin.cs` with proper logging | [x] |
| Audit all plugins for similar empty catch blocks | [x] |
| Add structured logging for all exception scenarios | [x] |
| Add alerting for critical compliance/backup failures | [x] |

---

#### 7. Fix Email Alerting Placeholder
**File:** `Plugins/DataWarehouse.Plugins.Alerting/AlertingPlugin.cs:882-884`
**Issue:** `SendEmailAsync` just returns `Task.CompletedTask` without sending

| Task | Status |
|------|--------|
| Implement real SMTP email sending via `System.Net.Mail` or `MailKit` | [x] |
| Add SMTP configuration (server, port, credentials, TLS) | [x] |
| Add email template support | [x] |
| Add retry logic for transient failures | [x] |
| Add unit tests with mock SMTP server | [x] |

---

#### 8. Fix Auto-RAID Rebuild Simulation
**File:** `Plugins/DataWarehouse.Plugins.AutoRaid/AutoRaidPlugin.cs:1373,1432`
**Issue:** Rebuild is simulated with fake progress, no actual data recovery

| Task | Status |
|------|--------|
| Implement real RAID rebuild logic with actual data copying | [ ] |
| Replace simulation loop with actual disk I/O operations | [ ] |
| Add checksum verification during rebuild | [ ] |
| Add progress tracking based on actual bytes processed | [ ] |
| Add unit tests with mock disk arrays | [ ] |

---

#### 9. Fix Missing IDisposable on RaidPlugin
**File:** `Plugins/DataWarehouse.Plugins.Raid/RaidPlugin.cs`
**Issue:** Has `ReaderWriterLockSlim` and `SemaphoreSlim` but no `IDisposable`

| Task | Status |
|------|--------|
| Implement `IDisposable` interface on `RaidPlugin` | [x] |
| Implement proper `Dispose(bool disposing)` pattern | [x] |
| Add finalizer for safety | [x] |
| Move disposal logic from `StopAsync` to `Dispose` | [x] |
| Audit all plugins for similar resource leak issues | [x] |

---

### MEDIUM Severity (Architectural Improvements)

#### 10. Implement Missing Kernel Features
**Per Code Review - ~70% architecture match**

| Task | Status |
|------|--------|
| Implement Smart Folders feature | [x] |
| Implement Service Manager | [x] |
| Implement full Permission Cascade mechanism | [x] |
| Implement VFS Placeholders/Ghost Files | [x] |
| Improve Job Scheduler beyond fire-and-forget | [x] |
| Implement Instance Pooling (unified) | [x] |

---

#### 11. Clean Up RAID Plugin Code Duplications
**Issue:** 10 RAID plugins contain ~1,450 lines of duplicated code

**Affected Plugins:**
| Plugin | Location |
|--------|----------|
| AutoRaid | `Plugins/DataWarehouse.Plugins.AutoRaid/` |
| Raid | `Plugins/DataWarehouse.Plugins.Raid/` |
| SelfHealingRaid | `Plugins/DataWarehouse.Plugins.SelfHealingRaid/` |
| StandardRaid | `Plugins/DataWarehouse.Plugins.StandardRaid/` |
| AdvancedRaid | `Plugins/DataWarehouse.Plugins.AdvancedRaid/` |
| EnhancedRaid | `Plugins/DataWarehouse.Plugins.EnhancedRaid/` |
| NestedRaid | `Plugins/DataWarehouse.Plugins.NestedRaid/` |
| ExtendedRaid | `Plugins/DataWarehouse.Plugins.ExtendedRaid/` |
| VendorSpecificRaid | `Plugins/DataWarehouse.Plugins.VendorSpecificRaid/` |
| ZfsRaid | `Plugins/DataWarehouse.Plugins.ZfsRaid/` |

**Duplications Identified:**

1. **GaloisField Implementations (7 independent copies, ~600 lines)**
   - `ZfsRaid/GaloisField.cs` - Standalone 640 lines (most comprehensive)
   - `Raid/RaidPlugin.cs:2416` - Embedded
   - `StandardRaid/StandardRaidPlugin.cs:1825` - Embedded
   - `AdvancedRaid/AdvancedRaidPlugin.cs:1788` - Embedded
   - `EnhancedRaid/EnhancedRaidPlugin.cs:2090` - Embedded (identical to AdvancedRaid)
   - `NestedRaid/NestedRaidPlugin.cs:2052` - Embedded (identical to AdvancedRaid)
   - `VendorSpecificRaid/VendorSpecificRaidPlugin.cs:2269` - Embedded as `VendorGaloisField`

2. **Reed-Solomon Z1/Z2/Z3 Parity Calculations (~300 lines)**
   - `Raid/RaidPlugin.cs:1942-2120` - `CalculateReedSolomonZ1/Z2/Z3Parity()`, `ReconstructRaidZ3Failures()`
   - `ZfsRaid/ZfsRaidPlugin.cs:766-791, 1023-1043` - `CalculateZ3Parity()`, `ReconstructZ3()`
   - Similar implementations in AutoRaid, StandardRaid

3. **Z3 References in 7 Files** (RAID-Z3 with 3 parity devices)
   - `AutoRaidPlugin.cs:202, 510-511, 705, 723`
   - `RaidPlugin.cs:102-104, 290-291, 368-376, 420, 1564, 1995-2015, 2101-2120`
   - `SelfHealingRaidPlugin.cs:200-202`
   - `ZfsRaidPlugin.cs:24, 107-113, 216-221, 766-791, 1023-1043`

4. **Duplicated RAID Constants**
   - Minimum device requirements (RAID_Z1=3, RAID_Z2=4, RAID_Z3=5) in 3+ plugins
   - Capacity calculation formulas duplicated

**Solution: Create SharedRaidUtilities Project**

Create `Plugins/DataWarehouse.Plugins.SharedRaidUtilities/` with:

| Task | Status |
|------|--------|
| Create `SharedRaidUtilities` project | [ ] |
| Implement shared `GaloisField.cs` (consolidate from ZfsRaid) | [ ] |
| Implement shared `ReedSolomonHelper.cs` with P/Q/R parity methods | [ ] |
| Implement shared `RaidConstants.cs` (MinimumDevices, CapacityFactors) | [ ] |
| Update `AutoRaidPlugin.cs` to use shared utilities | [ ] |
| Update `RaidPlugin.cs` to use shared utilities | [ ] |
| Update `SelfHealingRaidPlugin.cs` to use shared utilities | [ ] |
| Update `StandardRaidPlugin.cs` to use shared utilities | [ ] |
| Update `AdvancedRaidPlugin.cs` to use shared utilities | [ ] |
| Update `EnhancedRaidPlugin.cs` to use shared utilities | [ ] |
| Update `NestedRaidPlugin.cs` to use shared utilities | [ ] |
| Update `ExtendedRaidPlugin.cs` to use shared utilities | [ ] |
| Update `VendorSpecificRaidPlugin.cs` to use shared utilities | [ ] |
| Update `ZfsRaidPlugin.cs` to use shared utilities | [ ] |
| Delete embedded GaloisField classes from all plugins | [ ] |
| Run all RAID tests to verify correctness | [ ] |

**Expected Reduction:** ~1,450 lines of duplicated code

---

#### 12. HSM/VaultKeyStore Architecture (Verified Correct)
**Status:** ✓ No changes needed

**Analysis:** HSM is correctly implemented as a capability within VaultKeyStorePlugin, not a separate plugin.

**Current Architecture (Correct):**
```
SecurityProviderPluginBase
├── FileKeyStorePlugin (local/edge deployments)
│   └── 4-tier protection: DPAPI, Credential Manager, Database, Password
├── VaultKeyStorePlugin (enterprise/cloud with HSM)
│   └── Backends: HashiCorp Vault, Azure Key Vault, AWS KMS, Google Cloud KMS
│   └── Features: SupportsHSM=true, SupportsEnvelopeEncryption=true
└── KeyRotationPlugin (wrapper for any IKeyStore)
```

**Rationale for keeping separate:**
- Different deployment scenarios (edge vs enterprise)
- Different dependencies (zero external deps vs cloud SDKs)
- Different failure modes and SLAs
- Different compliance/audit requirements
- Different cost models

| Task | Status |
|------|--------|
| Document HSM/VaultKeyStore architecture decision | [x] |
| No implementation changes required | [x] |

---

## Verification Checklist

After implementing fixes:

| Check | Status |
|-------|--------|
| Solution compiles without errors | [ ] |
| All unit tests pass | [ ] |
| Database plugins tested against real databases | [ ] |
| RAID rebuild tested with actual data | [ ] |
| Email alerting sends real emails | [x] |
| Geo-replication syncs data across regions | [ ] |
| No empty catch blocks remain | [x] |
| All IDisposable resources properly disposed | [x] |
| DatabaseInfrastructure.cs deleted with no breakage | [x] |
| GeoReplicationPlugin compiles with StartAsync/StopAsync | [x] |
| All 10 RAID plugins use SharedRaidUtilities | [ ] |
| RAID parity calculations verified with test vectors | [ ] |

---

## TO DO - Microkernel Architecture Compliance (2026-01-24)

### Architecture Principle

**Microkernel Philosophy:** SDK and Kernel should ONLY provide:
- Core features (plugin loading, hot-reload)
- In-memory volatile storage (no persistence)
- Task management & background job scheduling
- Message routing (IMessageBus)
- Pipeline orchestration (WITHOUT implementation details)

**Features that MUST be plugins (NOT in SDK/Kernel):**
- Persistence (durability, backups, journaling)
- Safety/Integrity (checksums, replication, recovery)
- Encryption/Security (key management, credential storage)
- Compression (all algorithms)
- Deduplication
- Consensus
- Governance (AI-based or otherwise)
- Federation/Multi-region
- Any advanced features beyond basic orchestration

---

### CRITICAL - Files to Delete or Refactor

#### 13. Delete DurableState.cs - Persistence in Kernel Violation
**File:** `DataWarehouse.SDK/Utilities/DurableState.cs` (266 lines)
**Issue:** Full persistence implementation with journaling, file I/O, and log compaction in the kernel

**Violation Details:**
- Line 27: `_filePath` - File persistence in kernel
- Line 44-76: `Load()` - Replay journal log from disk
- Line 81-83: `InitializeJournal()` - Opens FileStream for persistent journaling
- Line 147-169: `AppendLog()` - Writes to persistent journal
- Line 185-226: `CompactInternal()` - Log compaction (persistence feature)

**Remediation:**
| Task | Status |
|------|--------|
| Create `Plugins/DataWarehouse.Plugins.PersistentState/` project | [ ] |
| Move `DurableState<T>` to PersistentStatePlugin | [ ] |
| Create `IStateStore` interface in SDK (for plugin contracts) | [ ] |
| Replace SDK usage with in-memory `ConcurrentDictionary<string, T>` | [ ] |
| Delete `DataWarehouse.SDK/Utilities/DurableState.cs` | [ ] |
| Update any code that imports DurableState to use plugin | [ ] |

---

#### 14. Refactor SecretManager.cs - Security Implementation in Kernel
**File:** `DataWarehouse.SDK/Security/SecretManager.cs` (669 lines)
**Issue:** Full secret management implementation should be a plugin, not kernel

**Violation Details:**
- Lines 230-584: `SecretManager` class - Full implementation with caching, rotation, validation
- Lines 241-250: `PlainTextSecretPatterns` - Regex-based secret detection
- Lines 252-275: Timer-based cache cleanup and rotation (background tasks)
- Lines 338-392: `RotateSecretAsync()` - Secret rotation with versioning
- Lines 618-666: `EnvironmentSecretProvider` - Concrete provider in kernel

**What to KEEP in SDK:**
- `ISecretManager` interface (lines 16-63)
- `SecretReference` class (lines 69-173) - Data transfer object
- `SecretProvider` enum (lines 178-196)
- `SecretMetadata` class (lines 201-209)
- `ISecretProvider` interface (lines 604-613)

**What to MOVE to Plugin:**
- `SecretManager` class implementation
- `SecretManagerConfig` class
- `EnvironmentSecretProvider` class
- All concrete provider implementations

**Remediation:**
| Task | Status |
|------|--------|
| Keep only interfaces in `DataWarehouse.SDK/Security/SecretManager.cs` | [ ] |
| Create `Plugins/DataWarehouse.Plugins.SecretManagement/` project | [ ] |
| Move `SecretManager` implementation to SecretManagementPlugin | [ ] |
| Move `EnvironmentSecretProvider` to plugin | [ ] |
| Update SDK to only define contracts (interfaces) | [ ] |
| Remove implementation code from SDK | [ ] |

---

#### 15. Delete/Move Federation Module - Advanced Feature in Kernel
**Directory:** `DataWarehouse.SDK/Federation/` (13 files, ~6,000+ lines)
**Issue:** Entire federation subsystem is an advanced feature that should be a plugin

**Files to Move:**
| File | Lines | Description |
|------|-------|-------------|
| `MultiRegion.cs` | 517 | Multi-region federation with failover |
| `Protocol.cs` | 1,600+ | Replication protocol with sync |
| `Transport.cs` | 400+ | Network transport layer |
| `DormantNode.cs` | 1,600+ | Node hibernation with encryption |
| `ObjectStore.cs` | 800+ | Distributed object storage |
| `StoragePool.cs` | 1,500+ | Pool management with alerts |
| `NatTraversal.cs` | 1,100+ | NAT traversal/hole punching |
| `VFS.cs` | 500+ | Virtual filesystem |
| `Resolution.cs` | 500+ | Path resolution |
| `Routing.cs` | 400+ | Routing tables |
| `Groups.cs` | 300+ | Replica groups |
| `CloudShare.cs` | 200+ | Cloud sharing |
| `NodeIdentity.cs` | 200+ | Node identification |
| `FederationHealth.cs` | 200+ | Health monitoring |
| `Capabilities.cs` | 100+ | Capability negotiation |

**Remediation:**
| Task | Status |
|------|--------|
| Create `Plugins/DataWarehouse.Plugins.Federation/` project | [ ] |
| Move all 13 files from `DataWarehouse.SDK/Federation/` to plugin | [ ] |
| Keep only `IFederationNode` interface in SDK | [ ] |
| Update imports in any SDK code that references Federation | [ ] |
| Delete `DataWarehouse.SDK/Federation/` directory | [ ] |

---

#### 16. Refactor GovernanceContracts.cs - AI Governance in Kernel
**File:** `DataWarehouse.SDK/Governance/GovernanceContracts.cs` (200+ lines)
**Issue:** AI-based governance (INeuralSentinel) is an advanced feature

**What to KEEP in SDK (Contracts Only):**
- `INeuralSentinel` interface
- `ISentinelModule` interface
- `SentinelContext` class (input DTO)
- `GovernanceJudgment` class (output DTO)
- `GovernanceResult` class
- `GovernanceAlert` class
- `IntegrityResult` class
- Attributes (`SentinelSkillAttribute`, etc.)

**What to MOVE to Plugin:**
- Any concrete implementations
- Background tasks for governance checks

**Remediation:**
| Task | Status |
|------|--------|
| Verify no concrete implementations exist in GovernanceContracts.cs | [ ] |
| If any exist, move to `Plugins/DataWarehouse.Plugins.AIGovernance/` | [ ] |
| Ensure file contains only interfaces and DTOs | [ ] |

---

#### 17. Refactor StorageOrchestratorBase.cs - Advanced Features in Base Class
**File:** `DataWarehouse.SDK/Contracts/StorageOrchestratorBase.cs` (1,278+ lines)
**Issue:** Base class contains implementations of advanced features

**Violations Found:**
- `RealTimeStorageOrchestratorBase`: Audit trails, locks, hash computation
- `IndexingStorageOrchestratorBase`: OCR, vector embeddings, AI summaries
- Replication logic embedded in base classes
- Integrity verification in base classes

**Remediation:**
| Task | Status |
|------|--------|
| Remove audit trail implementation from base class | [ ] |
| Remove hash computation from base class | [ ] |
| Remove lock management from base class | [ ] |
| Keep only abstract method declarations | [ ] |
| Move implementations to appropriate plugins | [ ] |

---

#### 18. Refactor BackupPluginAdapter.cs - Backup Logic in Kernel
**File:** `DataWarehouse.SDK/Infrastructure/BackupPluginAdapter.cs` (~200 lines)
**Issue:** Backup adapter contains backup-specific logic that should be in plugin

**Remediation:**
| Task | Status |
|------|--------|
| Keep only adapter interface in SDK | [ ] |
| Move backup scheduling logic to BackupPlugin | [ ] |
| Move backup destination management to BackupPlugin | [ ] |
| Remove concrete backup types from SDK | [ ] |

---

### MEDIUM - Interface-Only Violations (Keep Interface, Remove Implementation)

#### 19. Clean Up ICompressionProvider.cs
**File:** `DataWarehouse.SDK/Contracts/ICompressionProvider.cs`
**Issue:** Should be interface-only, no implementation

| Task | Status |
|------|--------|
| Verify file contains only interface definition | [ ] |
| Remove any concrete compression implementations | [ ] |
| Remove compression algorithm registry if present | [ ] |

---

#### 20. Clean Up IConsensusEngine.cs
**File:** `DataWarehouse.SDK/Contracts/IConsensusEngine.cs`
**Issue:** Consensus is an advanced feature - SDK should only define interface

| Task | Status |
|------|--------|
| Verify file contains only interface definition | [ ] |
| Remove any Raft/Paxos implementations | [ ] |
| Remove any quorum logic from SDK | [ ] |

---

#### 21. Clean Up IReplicationService.cs
**File:** `DataWarehouse.SDK/Contracts/IReplicationService.cs`
**Issue:** Replication is an advanced feature - SDK should only define interface

| Task | Status |
|------|--------|
| Verify file contains only interface definition | [ ] |
| Remove corrupted blob restoration logic | [ ] |
| Remove any concrete replication implementations | [ ] |

---

#### 22. Clean Up IMultiRegionReplication.cs
**File:** `DataWarehouse.SDK/Contracts/IMultiRegionReplication.cs`
**Issue:** Multi-region is an advanced feature - SDK should only define interface

| Task | Status |
|------|--------|
| Verify file contains only interface definition | [ ] |
| Remove conflict resolution implementations | [ ] |
| Remove consistency level implementations | [ ] |

---

### Microkernel Compliance Verification Checklist

After refactoring:

| Check | Status |
|-------|--------|
| `DurableState.cs` deleted from SDK | [ ] |
| `SecretManager.cs` contains only interfaces | [ ] |
| `Federation/` directory deleted from SDK | [ ] |
| `GovernanceContracts.cs` contains only interfaces/DTOs | [ ] |
| `StorageOrchestratorBase.cs` contains no concrete implementations | [ ] |
| `BackupPluginAdapter.cs` contains only adapter interface | [ ] |
| All compression implementations in plugins only | [ ] |
| All consensus implementations in plugins only | [ ] |
| All replication implementations in plugins only | [ ] |
| SDK provides only: interfaces, DTOs, base classes (abstract) | [ ] |
| SDK has NO file I/O for persistence | [ ] |
| SDK has NO encryption/decryption implementations | [ ] |
| SDK has NO network I/O for federation | [ ] |
| Solution compiles after refactoring | [ ] |
| All existing plugins still work | [ ] |
