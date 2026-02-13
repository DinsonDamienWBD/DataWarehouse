# Roadmap: DataWarehouse

## Milestone: v1.0 Production Readiness (Shipped: 2026-02-11)

> 21 phases, 116 plans | 863 commits | 1,110,244 LOC C# | 60 plugins | 30 days

<details>
<summary>v1.0 Phases (all complete)</summary>

- [x] **Phase 1: SDK Foundation & Base Classes** — 5 plans, verified all SDK infrastructure, 140+ base classes, strategy interfaces
- [x] **Phase 2: Core Infrastructure (Intelligence, RAID, Compression)** — 12 plans, UniversalIntelligence (12 AI providers), UltimateRAID (50+ strategies), UltimateCompression (40+ algorithms)
- [x] **Phase 3: Security Infrastructure (Encryption, Keys, Access Control)** — 10 plans, UltimateEncryption (30+ algorithms), UltimateKeyManagement (30+ strategies), UltimateAccessControl (9 models + 10 identity + 8 MFA + Zero Trust + threat detection)
- [x] **Phase 4: Compliance, Storage & Replication** — 5 plans, UltimateCompliance (160 files), UltimateStorage (130 backends), UltimateReplication (60 strategies), UniversalObservability (55 strategies)
- [x] **Phase 5: TamperProof Pipeline** — 5 plans, blockchain-verified WORM storage, hash chains, tamper recovery, integrity verification
- [x] **Phase 6: Interface Layer** — 12 plans, 80+ strategies (REST, gRPC, GraphQL, SQL wire, WebSocket, Conversational AI, Innovation)
- [x] **Phase 7: Format & Media Processing** — 8 plans, columnar/scientific/graph/lakehouse/streaming formats, video codecs (H.264-VVC), image/RAW/GPU/3D
- [x] **Phase 8: Compute & Processing** — 5 plans, 55+ runtime strategies (WASM, Container, Sandbox, Enclave, GPU), 43 storage processing strategies
- [x] **Phase 9: Advanced Security Features** — 6 plans, canary/honeypot, steganography, secure MPC, dead drops, sovereignty, forensic watermarking
- [x] **Phase 10: Advanced Storage Features** — 7 plans, air-gap bridge, CDP, block-tiering, branching, generative compression, probabilistic structures, self-emulating
- [x] **Phase 11: Spatial & Psychometric** — 2 plans, AR spatial anchors (GPS + SLAM), psychometric indexing (sentiment, emotion, deception detection)
- [x] **Phase 12: AEDS System** — 4 plans, core engine, control plane (WebSocket/MQTT/gRPC), data plane (HTTP/3/QUIC/WebTransport), 9 extensions
- [x] **Phase 13: Data Governance Intelligence** — 5 plans, active lineage, living catalog, predictive quality, semantic intelligence, intelligent governance
- [x] **Phase 14: Other Ultimate Plugins** — 5 plans, dashboards, resilience, deployment, sustainability, deprecation scoping
- [x] **Phase 15: Bug Fixes & Build Health** — 4 plans, warnings 1,201 to 16, 121 null! fixed, 37 TODOs resolved, critical bugs verified
- [x] **Phase 16: Testing & Quality Assurance** — 2 plans, 1,039 tests passing (0 failures), STRIDE/OWASP penetration test plan
- [x] **Phase 17: Plugin Marketplace** — 3 plans, discovery, install, versioning, certification, ratings, analytics
- [x] **Phase 18: Plugin Deprecation & File Cleanup** — 3 plans, 88 deprecated dirs removed (~200K lines dead code), 60 active plugins remain
- [x] **Phase 19: Application Platform Services** — 4 plans, app registration, per-app ACL/AI/observability, service consumption API
- [x] **Phase 20: WASM/WASI Language Ecosystem** — 4 plans, 31 language verifications, SDK bindings, performance benchmarks
- [x] **Phase 21: UltimateDataTransit** — 5 plans, 6 protocols + chunked/delta/P2P/multi-path/store-and-forward + QoS + cost-aware routing

</details>

---

## Milestone: v2.0 SDK Hardening & Distributed Infrastructure

> 12 phases (21.5, 22-31) | 118 requirements across 22 categories | Approach: verify first, implement only where code deviates
> Architecture decisions: .planning/ARCHITECTURE_DECISIONS.md (AD-01 through AD-10)

**Milestone Goal:** Harden the SDK to pass hyperscale/military-level code review -- refactor base class hierarchies, enforce security best practices, add distributed infrastructure contracts, achieve zero compiler warnings.

> **CRITICAL DIRECTIVE — ZERO REGRESSION (AD-08):**
> DO NOT LOSE ANY ALREADY-IMPLEMENTED LOGIC. v1.0 represents 30 days, 21 phases, 116 plans, 863 commits, and 1,110,244 LOC of production-ready C# code. Every plugin, every strategy, every feature MUST be preserved or correctly migrated. All ~1,500 strategies must produce identical results. All 1,039+ tests must pass at every phase boundary. EXTRACT first, VERIFY, then deprecate. No exceptions. All sub-agents executing plans MUST understand and follow this policy.

**Ordering Rationale:** Security-first. Build tooling and static analysis go first (catches issues in all subsequent work). Memory/crypto hardening before hierarchy refactoring (IDisposable must exist before plugin hierarchy changes). Hierarchies before distributed contracts (clean base classes before layering distributed features). Strategy hierarchy is split: design first (25a), then mechanical migration (25b) after plugin migration. Dead code cleanup after hierarchies stabilize. Testing last as the final gate.

### Cross-Reference: SDK_REFACTOR_PLAN.md Phases → v2.0 Roadmap

All 6 main phases and subphases from the original SDK_REFACTOR_PLAN.md are fully covered:

| SDK_REFACTOR_PLAN Phase | v2.0 Phase(s) | Requirements |
|---|---|---|
| Phase 1: Refactor PluginBase + IntelligentPluginBase | Phase 24 | HIER-01 to HIER-09 |
| Phase 2: Refactor StrategyBase | Phase 25a + 25b | STRAT-01 to STRAT-06 |
| Phase 3: Auto-scaling, LB, P2P, Sync, Tier, Governance | Phase 26 | DIST-01 to DIST-11 |
| Phase 3.5: Decouple Plugins and Kernel | Phase 27 | DECPL-01 to DECPL-05 |
| Phase 4A: Update Ultimate Plugins | Phase 27 | UPLT-01 to UPLT-03 |
| Phase 4B: Update Ultimate Plugin Strategies | Phase 25b + 27 | STRAT-06, UPLT-03 |
| Phase 5A: Update Standalone Plugins | Phase 27 | UPST-01 to UPST-03 |
| Phase 5B: Update Standalone Plugin Strategies | Phase 27 | UPST-02 |
| Phase 5C: Fix DataWarehouse.CLI | Phase 22 + 31 | CLI-01 to CLI-05 |
| Phase 6: Testing & Documentation | Phase 30 | TEST-01 to TEST-06 |
| NEW: Unified Interface & Deployment | Phase 31 | CLI-02 to CLI-05, DEPLOY-01 to DEPLOY-09 |

### Phases

- [x] **Phase 21.5: Pre-Execution Cleanup** - Consolidate duplicate types to SDK, standardize JSON serializer, add missing projects to solution, remove dead code
- [x] **Phase 22: Build Safety & Supply Chain** - Roslyn analyzers, TreatWarningsAsErrors rollout, SBOM, vulnerability audit, CLI fix
- [ ] **Phase 23: Memory Safety & Cryptographic Hygiene** - IDisposable patterns, secure memory wiping, bounded collections, constant-time comparisons, FIPS compliance
- [ ] **Phase 24: Plugin Hierarchy, Storage Core & Input Validation** - Two-branch plugin hierarchy (DataPipeline + Feature), object storage core with translation layer, specialized bases to composable services, input validation (AD-01, AD-02, AD-03, AD-04)
- [ ] **Phase 25a: Strategy Hierarchy Design & API Contracts** - Flat StrategyBase → domain bases (no intelligence layer), API contract safety (AD-05)
- [ ] **Phase 25b: Strategy Migration** - Migrate ~1,500 strategies to new bases, remove duplicated boilerplate, domain by domain
- [ ] **Phase 26: Distributed Contracts & Resilience** - All distributed SDK contracts, security primitives, multi-phase init, in-memory implementations, circuit breakers, observability
- [ ] **Phase 27: Plugin Migration & Decoupling Verification** - All Ultimate and standalone plugins updated to new hierarchies, decoupling enforced
- [ ] **Phase 28: Dead Code Cleanup** - Remove truly unused classes/files, keep future-ready interfaces for unreleased hardware/technology (AD-06)
- [ ] **Phase 29: Advanced Distributed Coordination** - SWIM gossip, Raft consensus, CRDT replication, FederatedMessageBus, load balancing implementations
- [ ] **Phase 30: Testing & Final Verification** - Full build validation, behavioral verification, distributed integration tests, analyzer clean pass
- [ ] **Phase 31: Unified Interface & Deployment Modes** - Dynamic CLI/GUI capability reflection, NLP query routing, three deployment modes (Client/Live/Install), platform-specific service management

### Phase Details

#### Phase 21.5: Pre-Execution Cleanup
**Goal**: Clean baseline before v2.0 execution begins — eliminate duplicate type definitions, standardize serialization, fix solution completeness, remove dead code.
**Depends on**: Nothing (first action)
**Requirements**: PRECLEAN-01, PRECLEAN-02, PRECLEAN-03, PRECLEAN-04, PRECLEAN-05, PRECLEAN-06
**Approach**: Mechanical cleanup — no architectural changes. Move shared types to SDK, update all references, standardize serializer, add missing projects. Low risk, high hygiene value.
**Success Criteria** (what must be TRUE):
  1. `ConnectionType`, `ConnectionTarget`, `OperatingMode`, `InstallConfiguration`, `EmbeddedConfiguration` exist once in SDK (namespace `DataWarehouse.SDK.Hosting`), used by all projects
  2. `DataWarehouse.CLI/Integration/OperatingMode.cs` deleted (unused dead code)
  3. CLI Commands import SDK types instead of referencing example code namespace
  4. All 7 Newtonsoft.Json usages in DataWarehouse.Shared migrated to System.Text.Json; Newtonsoft.Json PackageReference removed
  5. All 69 .csproj files listed in DataWarehouse.slnx (AirGapBridge and DataMarketplace added)
  6. Metadata/Adapter/ example code updated to reference SDK types (maintains value as integration example)
  7. Full solution builds with zero errors after all changes
**Plans**: 3 plans

**Canonical source:** Use Launcher's versions of all types (most complete: has Service mode, Cluster connection, TLS, auth token, timeouts).

**Verified impact map (files that need updating):**
```
CREATE:
  DataWarehouse.SDK/Hosting/OperatingMode.cs      <- new file, Launcher values (Install/Connect/Embedded/Service)
  DataWarehouse.SDK/Hosting/ConnectionTarget.cs    <- new file, Launcher values (Host/Port/LocalPath/AuthToken/UseTls/Timeout + factory methods)
  DataWarehouse.SDK/Hosting/ConnectionType.cs      <- new file, Launcher values (Local/Remote/Cluster)
  DataWarehouse.SDK/Hosting/InstallConfiguration.cs
  DataWarehouse.SDK/Hosting/EmbeddedConfiguration.cs

UPDATE (change namespace import to DataWarehouse.SDK.Hosting):
  DataWarehouse.Launcher/Integration/DataWarehouseHost.cs  <- uses all types
  DataWarehouse.Launcher/Integration/InstanceConnection.cs <- uses ConnectionTarget, ConnectionType
  DataWarehouse.Launcher/Program.cs
  DataWarehouse.Launcher/Adapters/DataWarehouseAdapter.cs
  DataWarehouse.CLI/Commands/ConnectCommand.cs     <- currently imports DataWarehouse.Integration (example code!)
  DataWarehouse.CLI/Commands/InstallCommand.cs     <- currently imports DataWarehouse.Integration
  DataWarehouse.CLI/Commands/EmbeddedCommand.cs    <- currently imports DataWarehouse.Integration
  DataWarehouse.CLI/Commands/StorageCommands.cs
  DataWarehouse.Shared/InstanceManager.cs          <- has its own ConnectionType/ConnectionTarget (replace entirely)
  DataWarehouse.Shared/MessageBridge.cs            <- uses Shared's ConnectionType enum
  DataWarehouse.GUI/Components/Pages/Connections.razor

DELETE (dead code -- completely unused):
  DataWarehouse.CLI/Integration/OperatingMode.cs   <- never imported by any CLI command
  DataWarehouse.CLI/Integration/IKernelAdapter.cs  <- dead code
  DataWarehouse.CLI/Integration/AdapterFactory.cs  <- dead code
  DataWarehouse.CLI/Integration/AdapterRunner.cs   <- dead code
  DataWarehouse.Launcher/Integration/OperatingMode.cs  <- replaced by SDK version

UPDATE (example code -- keep but change namespace):
  Metadata/Adapter/OperatingMode.cs                <- update to use DataWarehouse.SDK.Hosting
  Metadata/Adapter/DataWarehouseHost.cs            <- update to use DataWarehouse.SDK.Hosting
  Metadata/Adapter/InstanceConnection.cs           <- update to use DataWarehouse.SDK.Hosting

SPECIAL: DataWarehouse.Shared/InstanceManager.cs
  - Remove inline ConnectionType enum (Local/Remote/InProcess -> use SDK's Local/Remote/Cluster)
  - Remove inline ConnectionTarget class (Address/Port -> use SDK's Host/Port/LocalPath/etc.)
  - Keep ConnectionProfile class (unique to Shared, not duplicated)
  - Adapt ConnectRemoteAsync/ConnectLocalAsync/ConnectInProcessAsync to use SDK ConnectionTarget
```

Plans:
- [ ] 21.5-01: Consolidate ConnectionType, ConnectionTarget, OperatingMode, InstallConfiguration, EmbeddedConfiguration into SDK (`DataWarehouse.SDK/Hosting/`); update all references in Launcher, CLI, Shared, GUI; delete CLI dead code (4 files); update Metadata/Adapter examples to use SDK types
- [ ] 21.5-02: Migrate DataWarehouse.Shared from Newtonsoft.Json to System.Text.Json (7 files: MessageBridge, InstanceManager, CapabilityManager, DeveloperToolsService, ComplianceReportService, plus UniversalDashboards plugin)
- [ ] 21.5-03: Add DataWarehouse.Plugins.AirGapBridge and DataWarehouse.Plugins.DataMarketplace to DataWarehouse.slnx; verify full solution builds

#### Phase 22: Build Safety & Supply Chain
**Goal**: The SDK build pipeline enforces code quality and supply chain security at compile time -- zero warnings, static analysis on every build, known dependency posture.
**Depends on**: Nothing (first v2.0 phase)
**Requirements**: BUILD-01, BUILD-02, BUILD-03, BUILD-04, BUILD-05, SUPPLY-01, SUPPLY-02, SUPPLY-03, SUPPLY-04, CLI-01
**Approach**: Verify existing build configuration first. Incrementally enable TreatWarningsAsErrors by category to avoid halting 1.1M LOC build. CLI fix is isolated and can be done in parallel.
**Success Criteria** (what must be TRUE):
  1. `dotnet build` runs Roslyn analyzers (NetAnalyzers, SecurityCodeScan, SonarAnalyzer, Roslynator, BannedApiAnalyzers) and reports findings on every compilation
  2. TreatWarningsAsErrors is enabled across all projects with zero remaining compiler warnings (excluding NuGet-sourced)
  3. EnforceCodeStyleInBuild produces consistent code style enforcement at build time
  4. `dotnet list package --vulnerable` returns zero known vulnerabilities and all package versions are pinned (no floating ranges)
  5. SBOM generation produces a valid CycloneDX or SPDX artifact for the full solution
**Plans**: 4 plans

Plans:
- [ ] 22-01-PLAN.md — Roslyn analyzer suite integration and build configuration
- [ ] 22-02-PLAN.md — TreatWarningsAsErrors incremental rollout
- [ ] 22-03-PLAN.md — Supply chain security (vulnerability audit, version pinning, SBOM generation)
- [ ] 22-04-PLAN.md — CLI System.CommandLine migration

#### Phase 23: Memory Safety & Cryptographic Hygiene
**Goal**: All SDK code handles memory deterministically and uses cryptographic primitives correctly -- no leaked secrets in memory, no timing attacks, no unsafe random sources.
**Depends on**: Phase 22 (analyzers catch memory/crypto issues during implementation)
**Requirements**: MEM-01, MEM-02, MEM-03, MEM-04, MEM-05, CRYPTO-01, CRYPTO-02, CRYPTO-03, CRYPTO-04, CRYPTO-05, CRYPTO-06
**Approach**: Verify existing patterns first. Many crypto implementations from v1.0 may already use correct APIs. Focus on gaps -- especially IDisposable on PluginBase (affects all 60 plugins) and secure memory wiping of key material.
**Success Criteria** (what must be TRUE):
  1. All key material, tokens, and passwords are zeroed from memory after use via CryptographicOperations.ZeroMemory -- no secret persists in managed memory after its scope ends
  2. Hot-path buffer allocations use ArrayPool/MemoryPool instead of raw `new byte[]` -- verified by BannedApiAnalyzers or code review
  3. All public collections have configurable maximum sizes preventing unbounded memory growth
  4. All secret/hash comparisons use CryptographicOperations.FixedTimeEquals -- no timing side channels in authentication or verification paths
  5. All cryptographic random generation uses RandomNumberGenerator, never System.Random -- verified by BannedApiAnalyzers rule
**Plans**: 4 plans

Plans:
- [ ] 23-01-PLAN.md — IDisposable/IAsyncDisposable on PluginBase with proper dispose pattern (MEM-04, MEM-05)
- [ ] 23-02-PLAN.md — Secure memory wiping, buffer pooling, bounded collections (MEM-01, MEM-02, MEM-03)
- [ ] 23-03-PLAN.md — Cryptographic hygiene audit: constant-time comparisons, secure RNG, FIPS compliance (CRYPTO-01, CRYPTO-02, CRYPTO-05)
- [ ] 23-04-PLAN.md — Key rotation contracts, algorithm agility, message authentication (CRYPTO-03, CRYPTO-04, CRYPTO-06)

#### Phase 24: Plugin Hierarchy, Storage Core & Input Validation
**Goal**: The plugin base class hierarchy follows the two-branch design (DataPipeline + Feature), object-based storage is the universal core with a path translation layer, specialized bases are extracted into composable services, and all boundaries validate inputs. See AD-01, AD-02, AD-03, AD-04.
**Depends on**: Phase 23 (IDisposable pattern must be on PluginBase before hierarchy work)
**Requirements**: HIER-01, HIER-02, HIER-03, HIER-04, HIER-05, HIER-06, HIER-07, HIER-08, HIER-09, VALID-01, VALID-02, VALID-03, VALID-04, VALID-05
**Architecture Decisions**: AD-01 (two branches), AD-02 (single encryption/compression base), AD-03 (composable services), AD-04 (object storage core)
**Approach**: Verify existing hierarchy first. Restructure into two branches under IntelligenceAwarePluginBase: DataPipelinePluginBase (encryption, storage, replication, transit, integrity) and FeaturePluginBase (security, interface, compute, observability, etc.). Extract specialized base class logic (TieredStorage, CacheableStorage, etc.) into composable services. Unify storage model around object/key-based core with URI translation layer.
**Success Criteria** (what must be TRUE):
  1. PluginBase has complete lifecycle (Initialize/Execute/Shutdown with CancellationToken), IDisposable/IAsyncDisposable, capability registry, and knowledge registry
  2. IntelligenceAwarePluginBase extends PluginBase with graceful degradation -- every plugin except UltimateIntelligencePlugin inherits this
  3. DataPipelinePluginBase and FeaturePluginBase are sibling branches under IntelligenceAwarePluginBase with ~15-20 domain bases total
  4. Object/key-based storage is the universal core model with StorageObjectMetadata; PathStorageAdapter provides URI translation layer
  5. Specialized base class logic (tiering, caching, indexing, hybrid) extracted into composable services accessible by all storage plugins
  6. Every public SDK method validates its inputs (null checks, range checks, format validation, path traversal protection, ReDoS protection)
  7. All 60 existing plugins still compile and pass tests after hierarchy changes
**Plans**: 7 plans

Plans:
- [ ] 24-01-PLAN.md — PluginBase lifecycle verification and HIER-02 lifecycle methods
- [ ] 24-02-PLAN.md — Reparent IntelligenceAwarePluginBase, create two-branch design, fix UltimateIntelligencePlugin
- [ ] 24-03-PLAN.md — Create 18 domain plugin bases (7 DataPipeline + 11 Feature)
- [ ] 24-04-PLAN.md — IObjectStorageCore interface and PathStorageAdapter URI translation layer
- [ ] 24-05-PLAN.md — Extract composable services: ITierManager, ICacheManager, IStorageIndex, IConnectionRegistry
- [ ] 24-06-PLAN.md — Input validation: Guards class, SizeLimitOptions, Regex timeout audit, PluginIdentity
- [ ] 24-07-PLAN.md — Full solution build verification across all 60 plugins

#### Phase 25a: Strategy Hierarchy Design & API Contracts
**Goal**: Design and implement the unified strategy base hierarchy (flat, two-level, NO intelligence layer) and enforce immutable API contracts. See AD-05.
**Depends on**: Phase 24 (plugin hierarchy must be stable -- strategies belong to plugins)
**Requirements**: STRAT-01, STRAT-02, STRAT-04, STRAT-05, API-01, API-02, API-03, API-04
**Architecture Decisions**: AD-05 (flat strategy hierarchy, no intelligence layer)
**Approach**: Strategies are workers, not orchestrators. They do NOT have intelligence, capability registry, knowledge bank access, or pipeline awareness. The plugin handles all of that. Strategy hierarchy is flat: StrategyBase → ~15 domain strategy bases → concrete strategies. Remove STRAT-03 (IntelligenceAwareStrategyBase) per AD-05 -- intelligence belongs at the plugin level only.
**Success Criteria** (what must be TRUE):
  1. StrategyBase exists with lifecycle (Initialize/Shutdown), IDisposable/IAsyncDisposable, metadata (Name, Description, Characteristics), CancellationToken support -- NO intelligence, NO capability registry, NO knowledge bank
  2. ~15 domain strategy bases exist (Encryption, Compression, Storage, Security, KeyManagement, Compliance, Interface, Connector, Compute, Observability, Replication, Media, Streaming, Format, Transit, DataManagement) each defining their domain contract
  3. Common boilerplate from 7 fragmented bases consolidated into StrategyBase (~1,000 lines of duplication eliminated)
  4. All public SDK data transfer types use C# records or init-only setters (immutable by default) and strongly-typed contracts replace Dictionary<string, object>
  5. Adapter wrappers allow existing strategies to compile against new hierarchy without changes (temporary -- removed during 25b migration)
**Plans**: 5 plans

Plans:
- [ ] 25a-01-PLAN.md — StrategyBase root class + IStrategy interface (lifecycle, dispose, metadata, no intelligence)
- [ ] 25a-02-PLAN.md — Refactor 17+ domain bases to inherit StrategyBase, remove intelligence boilerplate
- [ ] 25a-03-PLAN.md — Backward-compatibility shim for ~1,500 plugin strategies
- [ ] 25a-04-PLAN.md — SdkCompatibilityAttribute + NullMessageBus/NullLogger (API contract safety)
- [ ] 25a-05-PLAN.md — Full build verification, test suite, hierarchy grep validation

#### Phase 25b: Strategy Migration
**Goal**: Migrate all ~1,727 strategy classes to the new hierarchy, remove duplicated boilerplate (intelligence, capability, dispose code), verify behavioral equivalence.
**Depends on**: Phase 25a (new strategy bases must exist)
**Requirements**: STRAT-04, STRAT-06
**Approach**: Research-informed complexity-based grouping (not just count-based). Three waves: Wave 1 verifies SDK-base domains need no changes (Type A -- ~964 strategies across 14 domains, can run in parallel). Wave 2 migrates plugin-local bases (Type B/C -- 6 bases controlling ~763 strategies, requires code changes). Wave 3 removes backward-compat shims and performs final verification. Key findings: >95% of strategies have ZERO intelligence boilerplate; only ~55 files use MessageBus directly; real work is concentrated on 6 plugin-local bases and the ~55 MessageBus-using strategies.
**Success Criteria** (what must be TRUE):
  1. All ~1,727 strategies inherit from their correct domain strategy base (no more direct inheritance from fragmented bases)
  2. Intelligence/capability/knowledge boilerplate removed from plugin-local strategy bases (Compute ~60 lines, DataProtection ~30 lines)
  3. Backward-compat shim intelligence methods removed from StrategyBase (ConfigureIntelligence, GetStrategyKnowledge, GetStrategyCapability)
  4. All existing plugin strategies produce identical results after migration (behavioral verification via AD-08)
  5. Build compiles with zero new errors across all 60 plugins
**Plans**: 6 plans

Plans:
- [ ] 25b-01-PLAN.md — Verify smallest SDK-base domains (Transit 11, Media 20, DataFormat 28, StorageProcessing 43 = 102 strategies)
- [ ] 25b-02-PLAN.md — Verify medium SDK-base domains (Observability 55, DataLake 56, DataMesh 56, Compression 59, Replication 61 = 287 strategies)
- [ ] 25b-03-PLAN.md — Verify large SDK-base domains with intermediates (KeyManagement 69, RAID 47, Storage 130, DatabaseStorage 49, Connector 280 = 575 strategies)
- [ ] 25b-04-PLAN.md — Migrate plugin-local bases WITHOUT intelligence (AccessControl 146, Compliance 149, DataManagement 101, Streaming 58 = 454 strategies)
- [ ] 25b-05-PLAN.md — Migrate plugin-local bases WITH intelligence + verify complex domains (Compute 85, DataProtection 82, Interface 73, Encryption 69 = 309 strategies)
- [ ] 25b-06-PLAN.md — Remove backward-compat shims, final build verification, comprehensive audit (~1,727 strategies)

#### Phase 26: Distributed Contracts & Resilience
**Goal**: The SDK defines all distributed infrastructure contracts with security primitives baked in, in-memory single-node implementations for backward compatibility, and resilience/observability contracts that any plugin can use.
**Depends on**: Phase 24 (plugin hierarchy for multi-phase init), Phase 25 (strategy hierarchy for distributed strategies)
**Requirements**: DIST-01, DIST-02, DIST-03, DIST-04, DIST-05, DIST-06, DIST-07, DIST-08, DIST-09, DIST-10, DIST-11, RESIL-01, RESIL-02, RESIL-03, RESIL-04, RESIL-05, OBS-01, OBS-02, OBS-03, OBS-04, OBS-05
**Approach**: Contract-first design. Define all interfaces (IClusterMembership, ILoadBalancerStrategy, IP2PNetwork, etc.) before any implementation. Build in-memory implementations that make the system work on a single laptop with no cluster. Security primitives (message signing, replay protection) are architectural -- included here, not bolted on later.
**Success Criteria** (what must be TRUE):
  1. All 7 distributed contracts (IClusterMembership, ILoadBalancerStrategy, IP2PNetwork, IAutoScaler, IReplicationSync, IAutoTier, IAutoGovernance) are defined in the SDK with XML documentation
  2. FederatedMessageBus wraps IMessageBus with transparent local/remote routing -- existing single-node code works unchanged
  3. Multi-phase plugin initialization (construction with zero deps, initialization with MessageBus, activation with distributed coordination) prevents circular dependency deadlocks
  4. In-memory single-node implementations exist for every distributed contract -- the SDK runs on a single laptop without any cluster configuration
  5. ICircuitBreaker, IBulkheadIsolation, IHealthCheck, and ActivitySource contracts are available to all plugins with sensible defaults
**Plans**: 5 plans

Plans:
- [ ] 26-01-PLAN.md — Distributed SDK contracts (cluster membership, load balancing, P2P, auto-scaling, sync, tiering, governance)
- [ ] 26-02-PLAN.md — FederatedMessageBus architecture and multi-phase plugin initialization
- [ ] 26-03-PLAN.md — Resilience contracts (circuit breaker, bulkhead, timeouts, dead letter queue, graceful shutdown)
- [ ] 26-04-PLAN.md — Observability contracts (ActivitySource, structured logging, health checks, resource metering, audit trail)
- [ ] 26-05-PLAN.md — In-memory single-node implementations and distributed security primitives (HMAC, replay protection)

#### Phase 27: Plugin Migration & Decoupling Verification
**Goal**: All 60 plugins use the new base class hierarchies and distributed infrastructure features -- no plugin references another plugin directly, all communication flows through the message bus.
**Depends on**: Phase 25b (strategy migration), Phase 26 (distributed contracts)
**Requirements**: UPLT-01, UPLT-02, UPLT-03, UPST-01, UPST-02, UPST-03, DECPL-01, DECPL-02, DECPL-03, DECPL-04, DECPL-05
**Approach**: Highest-leverage first: re-parent ~60 SDK intermediate bases from LegacyFeaturePluginBase to Hierarchy domain bases (automatically migrates all concrete plugins through intermediate bases). Then migrate remaining plugins by domain in batches. Static analysis to verify zero cross-plugin references. Decoupling verification is a gate: phase does not complete until every plugin is verified SDK-only.
**Success Criteria** (what must be TRUE):
  1. All Ultimate plugins inherit from their respective feature-specific plugin base classes and use unified strategy hierarchy
  2. All standalone plugins inherit from IntelligenceAwarePluginBase at minimum and use unified strategy base classes
  3. Zero plugins or kernel depend on any other plugin directly -- static analysis confirms SDK-only dependencies
  4. All inter-plugin and plugin-kernel communication uses Commands/Messages via message bus only -- no direct method calls between plugins
  5. All plugins register capabilities and knowledge into the system knowledge bank and leverage distributed infrastructure from SDK base classes
**Plans**: 5 plans

Plans:
- [ ] 27-01-PLAN.md — SDK intermediate base re-parenting (~60 bases from LegacyFeaturePluginBase/PluginBase to Hierarchy domain bases)
- [ ] 27-02-PLAN.md — Ultimate DataPipeline plugin migration (Encryption, Compression, Storage, Replication, Transit, Integrity -- 10 plugins)
- [ ] 27-03-PLAN.md — Ultimate Feature plugin migration (Security, Interface, DataManagement, Compute, Observability, Streaming, Format, Infrastructure, Orchestration, Platform -- 32 plugins)
- [ ] 27-04-PLAN.md — Standalone plugin migration + special cases (AirGapBridge, AEDS verification, LegacyFeaturePluginBase direct users -- 16+ plugins)
- [ ] 27-05-PLAN.md — Decoupling verification (static analysis, message bus audit, capability/knowledge registration, full build)

#### Phase 28: Dead Code Cleanup
**Goal**: Remove truly unused classes, files, and interfaces that are referenced by nothing. Keep future-ready interfaces for unreleased hardware/technology (quantum crypto, brain-reading encryption, DNA storage, neuromorphic computing). See AD-06.
**Depends on**: Phase 27 (plugin migration must be complete -- know what's used before deleting)
**Requirements**: (new — CLEAN-01 through CLEAN-03)
**Architecture Decisions**: AD-06 (dead code cleanup policy)
**Approach**: Systematic scan for zero-reference classes/files. For each candidate: verify zero references (grep class name), confirm not future-ready, confirm logic exists elsewhere if useful. Remove in batches by category.
**Success Criteria** (what must be TRUE):
  1. All classes/files with zero references (no inheritance, no instantiation, no import) that are NOT future-ready interfaces are removed
  2. Superseded implementations (logic reimplemented elsewhere) are removed
  3. Future-ready interfaces for unreleased technology are preserved and documented with clear "FUTURE:" comments
  4. Build compiles with zero errors after cleanup
  5. No functionality is lost -- all removed code verified as either dead or extracted into composable services (AD-03)
**Plans**: TBD (estimated 3-4 plans)

Plans:
- [ ] 28-01: Dead code scan -- identify all zero-reference classes/files across SDK and plugins
- [ ] 28-02: Categorize candidates (truly dead vs future-ready vs superseded)
- [ ] 28-03: Remove dead code in batches, preserve future-ready interfaces with documentation
- [ ] 28-04: Build verification and LOC impact report

#### Phase 29: Advanced Distributed Coordination
**Goal**: The SDK has production-ready implementations of cluster membership, consensus, replication, and load balancing -- a multi-node DataWarehouse cluster can form, elect leaders, replicate data, and balance load.
**Depends on**: Phase 26 (distributed contracts), Phase 27 (plugins updated to use distributed features)
**Requirements**: DIST-12, DIST-13, DIST-14, DIST-15, DIST-16, DIST-17
**Approach**: Implement against the contracts defined in Phase 26. SWIM gossip for membership, Raft for consensus, CRDT for conflict resolution, consistent hashing and resource-aware load balancing. All implementations are plugins using the SDK contracts -- no special kernel coupling.
**Success Criteria** (what must be TRUE):
  1. SWIM gossip protocol handles decentralized cluster membership with automatic failure detection -- nodes join and leave without manual intervention
  2. Raft consensus provides leader election in multi-node clusters -- exactly one leader at any time, automatic re-election on leader failure
  3. Multi-master replication with CRDT conflict resolution allows concurrent writes from multiple nodes with deterministic merge
  4. Consistent hashing load balancer with virtual nodes distributes requests cache-friendly across cluster nodes
  5. Resource-aware load balancer monitors CPU/memory and adapts routing to avoid overloaded nodes
**Plans**: TBD (estimated 3-4 plans)

Plans:
- [ ] 29-01: SWIM gossip cluster membership and P2P gossip replication
- [ ] 29-02: Raft consensus for leader election
- [ ] 29-03: Multi-master replication with CRDT conflict resolution
- [ ] 29-04: Consistent hashing and resource-aware load balancing

#### Phase 30: Testing & Final Verification
**Goal**: The entire v2.0 milestone passes comprehensive verification -- all tests pass, behavioral equivalence confirmed, distributed contracts integration-tested, analyzers produce zero warnings.
**Depends on**: All prior v2.0 phases (22-29)
**Requirements**: TEST-01, TEST-02, TEST-03, TEST-04, TEST-05, TEST-06
**Approach**: This phase runs verification, not implementation. If failures are found, they are fixed here. The phase is the final gate before the milestone ships.
**Success Criteria** (what must be TRUE):
  1. Full solution builds with zero errors and zero compiler warnings after all refactoring
  2. All 1,039+ existing tests continue to pass -- no regressions from v2.0 changes
  3. New unit tests cover all new/modified base classes (plugin hierarchy, strategy hierarchy, distributed contracts)
  4. Behavioral verification tests confirm existing strategies produce identical results after hierarchy migration
  5. Roslyn analyzer clean pass with zero suppressed warnings without documented justification
**Plans**: TBD (estimated 2-3 plans)

Plans:
- [ ] 30-01: Build validation and existing test suite regression check
- [ ] 30-02: New unit tests for base classes and behavioral verification suite
- [ ] 30-03: Distributed infrastructure integration tests with in-memory implementations

#### Phase 31: Unified Interface & Deployment Modes
**Goal**: CLI and GUI dynamically reflect plugin capabilities at runtime, NLP queries route through the intelligence stack, and DW supports three deployment modes: Standard Client (connect to any instance), Live Mode (in-memory portable), and Install Mode (deploy to local machine from clean or USB source). See AD-09, AD-10.
**Depends on**: Phase 27 (UltimateInterface must be migrated to new hierarchy), Phase 22 (CLI System.CommandLine fix)
**Requirements**: CLI-02, CLI-03, CLI-04, CLI-05, DEPLOY-01, DEPLOY-02, DEPLOY-03, DEPLOY-04, DEPLOY-05, DEPLOY-06, DEPLOY-07, DEPLOY-08, DEPLOY-09
**Architecture Decisions**: AD-09 (unified user interface), AD-10 (deployment modes)
**Approach**: Three work streams that can largely run in parallel:

**Stream A — Dynamic Capability Reflection (CLI-02, CLI-04, CLI-05):**
Wire Shared to subscribe to capability change events via MessageBridge. Build DynamicCommandRegistry that auto-generates available commands from live capabilities. Replace hardcoded command lists in CommandExecutor. Both CLI and GUI read from this single registry.

**Stream B — Intelligence-Backed Queries (CLI-03, DEPLOY-09):**
Connect NLP query path from Shared → MessageBridge → UltimateInterface → AI socket → Knowledge Bank. Implement graceful degradation (AI unavailable → keyword-based capability lookup). Replace mock/placeholder server commands with real kernel startup/shutdown. Replace mock MessageBridge.SendInProcessAsync with real in-memory message queue.

**Stream C — Deployment Modes (DEPLOY-01 through DEPLOY-08):**
Complete the stub implementations in DataWarehouseHost (CopyFiles, RegisterService, CreateAdmin, InitializePlugins). Add CLI commands: `dw live`, `dw install`. Implement platform-specific service registration (sc create, systemd, launchd). Add USB/portable detection and path adaptation. Add copy-from-USB installation mode with path remapping.

**Success Criteria** (what must be TRUE):
  1. When a plugin loads/unloads at runtime, CLI and GUI commands appear/disappear dynamically within seconds — no restart needed
  2. User NLP queries ("What encryption algorithms are available?") return accurate, live answers from the Knowledge Bank; if intelligence is unavailable, keyword-based fallback provides capability listings
  3. CLI and GUI have 100% feature parity — verified by ensuring both read from the same DynamicCommandRegistry
  4. `dw connect --host <ip> --port <port>` connects to a real running DW instance, discovers capabilities, and enables remote management
  5. `dw live` starts an in-memory DW instance that CLI/GUI can connect to; all data vanishes on shutdown
  6. `dw install --path <path> --service --autostart` creates a working DW installation with registered service and autostart on Windows, Linux, and macOS
  7. `dw install --from-usb <source> --path <target>` clones a portable DW instance to local disk with remapped paths and registered service
**Plans**: TBD (estimated 6-8 plans)

Plans:
- [ ] 31-01: DynamicCommandRegistry — subscribe to capability events, auto-generate commands from live capabilities
- [ ] 31-02: NLP query routing — Shared → MessageBridge → UltimateInterface → Knowledge Bank with graceful degradation
- [ ] 31-03: Remove mocks/stubs — real kernel startup in ServerCommands, real in-memory message queue in MessageBridge.SendInProcessAsync, real InstanceManager.ExecuteAsync error handling
- [ ] 31-04: Launcher endpoint alignment — ensure Launcher exposes `/api/v1/*` endpoints compatible with RemoteInstanceConnection protocol
- [ ] 31-05: Live mode — `dw live` command, EmbeddedConfiguration with PersistData=false, auto-discovery, USB/portable detection
- [ ] 31-06: Install mode (clean) — `dw install` command, real CopyFilesAsync, real RegisterServiceAsync, real CreateAdminUserAsync, real InitializePluginsAsync
- [ ] 31-07: Install mode (from USB) — `dw install --from-usb`, USB source validation, tree copy, path remapping, post-install verification
- [ ] 31-08: Platform-specific service management — Windows (sc create, Task Scheduler), Linux (systemd unit, systemctl enable), macOS (launchd plist, launchctl load)

### Progress

**Execution Order:** 21.5 → 22 → 23 → 24 → 25a → 25b → 26 → 27 → 28 → 29 → 30 → 31

| Phase | Milestone | Plans | Status | Completed |
|-------|-----------|-------|--------|-----------|
| 21.5. Pre-Execution Cleanup | v2.0 | 0/3 | Not started | - |
| 22. Build Safety & Supply Chain | v2.0 | 0/4 | Not started | - |
| 23. Memory Safety & Crypto Hygiene | v2.0 | 0/4 | Not started | - |
| 24. Plugin Hierarchy, Storage Core & Validation | v2.0 | 0/7 | Planned | - |
| 25a. Strategy Hierarchy Design & API Contracts | v2.0 | 0/5 | Planned | - |
| 25b. Strategy Migration (~1,727 strategies) | v2.0 | 0/6 | Planned | - |
| 26. Distributed Contracts & Resilience | v2.0 | 0/5 | Planned | - |
| 27. Plugin Migration & Decoupling | v2.0 | 0/5 | Planned | - |
| 28. Dead Code Cleanup | v2.0 | 0/4 | Not started | - |
| 29. Advanced Distributed Coordination | v2.0 | 0/4 | Not started | - |
| 30. Testing & Final Verification | v2.0 | 0/3 | Not started | - |
| 31. Unified Interface & Deployment Modes | v2.0 | 0/8 | Not started | - |
