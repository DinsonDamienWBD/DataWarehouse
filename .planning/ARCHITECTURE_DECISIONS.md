# Architecture Decisions: v2.0 SDK Hardening

**Decided:** 2026-02-12
**Context:** Discussion between user and architect during v2.0 milestone planning

---

## AD-01: Plugin Hierarchy — Two Branches Under IntelligenceAwarePluginBase

**Decision:** All plugins (except UltimateIntelligencePlugin) inherit from IntelligenceAwarePluginBase. Below that, two sibling branches:
- **DataPipelinePluginBase** — plugins that data flows THROUGH (encryption, storage, replication, transit, integrity)
- **FeaturePluginBase** — plugins that provide SERVICES (security, interfaces, governance, compute, observability)

**Rationale:** Not everything is a pipeline stage. MetricsPluginBase observes the pipeline, GovernancePluginBase enforces policy on it, InterfacePluginBase serves data from it. These don't need pipeline semantics (stage ordering, back-pressure, throughput tracking). But storage, encryption, compression, replication — data literally flows through them.

**Target hierarchy:**
```
IPlugin (interface)
└── PluginBase (lifecycle, capability registry, knowledge registry, dispose)
    ├── UltimateIntelligencePlugin (IS the intelligence — inherits PluginBase directly)
    └── IntelligenceAwarePluginBase (AI socket, graceful degradation)
        ├── DataPipelinePluginBase (data flows THROUGH these)
        │   ├── DataTransformationPluginBase (mutates data)
        │   │   ├── EncryptionPluginBase → UltimateEncryptionPlugin
        │   │   └── CompressionPluginBase → UltimateCompressionPlugin
        │   ├── StoragePluginBase (data persistence endpoint)
        │   │   └── UltimateStoragePlugin (composes all storage capabilities via strategies)
        │   ├── ReplicationPluginBase (distributes data)
        │   │   ├── UltimateReplicationPlugin
        │   │   └── UltimateRAIDPlugin
        │   ├── DataTransitPluginBase (moves data between nodes)
        │   │   └── UltimateDataTransitPlugin
        │   └── IntegrityPluginBase (verifies data at boundaries)
        │       Strategies: TamperProof, Blockchain, WORM, HashChain
        │
        └── FeaturePluginBase (provides services/capabilities)
            ├── SecurityPluginBase
            │   ├── AccessControlPluginBase → UltimateAccessControlPlugin
            │   ├── KeyManagementPluginBase → UltimateKeyManagementPlugin
            │   ├── CompliancePluginBase → UltimateCompliancePlugin
            │   └── ThreatDetectionPluginBase
            ├── InterfacePluginBase → UltimateInterfacePlugin
            ├── DataManagementPluginBase
            │   → UltimateDataGovernancePlugin, UltimateDataCatalogPlugin,
            │     UltimateDataQualityPlugin, UltimateDataLineagePlugin, etc.
            ├── ComputePluginBase → UltimateComputePlugin
            ├── ObservabilityPluginBase → UniversalObservabilityPlugin
            ├── StreamingPluginBase → UltimateStreamingDataPlugin
            ├── MediaPluginBase
            ├── FormatPluginBase → UltimateDataFormatPlugin
            ├── InfrastructurePluginBase
            │   → UltimateDeploymentPlugin, UltimateResiliencePlugin,
            │     UltimateSustainabilityPlugin, UltimateMultiCloudPlugin
            ├── OrchestrationPluginBase
            │   → UltimateWorkflowPlugin, UltimateEdgeComputingPlugin
            └── PlatformPluginBase
                → UltimateSDKPortsPlugin, UltimateMicroservicesPlugin
```

---

## AD-02: Single Encryption/Compression Base — No AtRest vs Transit Split

**Decision:** One `EncryptionPluginBase` and one `CompressionPluginBase`. No separate AtRest/Transit base classes.

**Rationale:** The difference between at-rest and in-transit is about key management and lifecycle (session keys vs stored keys, forward secrecy vs key wrapping), not about the algorithm. This is exactly what the strategy pattern handles — the base provides the common interface, strategies handle context-specific behavior. UltimateEncryptionPlugin already works this way with 30+ strategies.

---

## AD-03: Specialized Bases Become Composable Services/Strategies

**Decision:** Specialized base classes (TieredStoragePluginBase, CacheableStoragePluginBase, IndexableStoragePluginBase, MandatoryAccessControlPluginBase, etc.) should NOT be in the inheritance chain. Their logic should be extracted into composable services that the domain PluginBase orchestrates.

**Rationale:** C# has single inheritance. UltimateStoragePlugin needs ALL storage capabilities (tiered + cached + indexed + low-latency + database), but can only inherit ONE base. If these capabilities are locked in an inheritance chain, Ultimate plugins can't use them all.

**Pattern:**
```csharp
// BEFORE: Logic locked in inheritance chain
class TieredStoragePluginBase : ListableStoragePluginBase { /* tiering logic */ }
class CacheableStoragePluginBase : ListableStoragePluginBase { /* caching logic */ }
// UltimateStoragePlugin can't inherit both

// AFTER: Logic extracted into composable services
class StoragePluginBase : DataPipelinePluginBase
{
    protected ITierManager TierManager { get; }       // from TieredStoragePluginBase
    protected ICacheManager CacheManager { get; }     // from CacheableStoragePluginBase
    protected IStorageIndex StorageIndex { get; }     // from IndexableStoragePluginBase
    protected IConnectionRegistry Connections { get; } // from HybridStoragePluginBase
    protected IHealthMonitor HealthMonitor { get; }   // from HybridStoragePluginBase
}
// UltimateStoragePlugin inherits StoragePluginBase, composes ALL capabilities
```

**Rule:** If there are SEPARATE Ultimate plugins for sub-domains (AccessControl, KeyManagement, Compliance are separate plugins), sub-domain bases stay. If ONE Ultimate plugin handles all variations (UltimateStoragePlugin handles all 130+ backends), the specialized bases become strategies/services.

---

## AD-04: Object Storage as THE Core Model — Translation Layer for Paths

**Decision:** Object/key-based storage is the default and only internal model. A translation layer maps file-path (URI) operations to object operations for users who want path-based access.

**Rationale:** The SDK currently has two competing storage abstractions:
- `IStorageProvider` (Uri-based, file-path model) — used by base classes, kernel
- `IStorageStrategy` (key-based, object model) — used by UltimateStoragePlugin, 130+ strategies

Advanced features (replication, tiering, RAID, dedup, AEDS, governance) all work on objects with rich metadata (ETag, tier, version, custom metadata). If some plugins use file paths and others use objects, the foundation doesn't support the features uniformly.

**Architecture:**
```
User-facing: Path API (URI)  ←→  Translation Layer  ←→  Object Storage Core (key-based)
                                                           ↓
                                                    All advanced features
                                                    (tiering, replication, RAID, etc.)
                                                           ↓
                                                    130+ backend strategies
```

Even FileSystem strategy stores objects — it maps `key="data/file.csv"` to a file at `{root}/data/file.csv` with metadata in a sidecar. The user sees files; the system sees objects.

---

## AD-05: Strategy Hierarchy — Flat, Simple, No Intelligence Layer

**Decision:** Strategy hierarchy is two levels deep: StrategyBase → DomainStrategyBase → ConcreteStrategy. No IntelligenceAwareStrategyBase. No DataPipelineStrategyBase.

### Key Principles:

**Plugin = Container/Orchestrator.** A collection of ways to do a similar thing.
**Strategy = Worker.** One specific way to do it.

**Strategies do NOT have:**
- ✗ Intelligence/AI integration (plugin handles this via IntelligenceAwarePluginBase)
- ✗ Capability registry (strategy IS a capability of its parent plugin)
- ✗ Knowledge bank access (plugin registers on behalf of strategy)
- ✗ Message bus access (plugin orchestrates messaging)
- ✗ Pipeline awareness (plugin handles pipeline context)

**Strategies DO have:**
- ✓ Lifecycle (InitializeAsync / ShutdownAsync)
- ✓ Dispose (IDisposable / IAsyncDisposable)
- ✓ Identity (Name, Description)
- ✓ Characteristics/metadata (key sizes, performance profile, hardware requirements)
- ✓ Structured logging hooks

### Capability Flow:
- Plugin registers strategy characteristics as plugin capabilities
- Strategy declares characteristics via metadata properties
- Plugin queries AI for strategy selection, passes decisions as options to strategy
- Strategy executes, returns results including operational metadata
- Plugin registers strategy's operational metadata as runtime knowledge

### Knowledge Flow:
- **Plugin knowledge (static):** "AES, Serpent available; AES disabled, Serpent enabled"
- **Strategy knowledge (runtime):** "Block X encrypted with Serpent-256, key ID abc"
- Strategy produces runtime knowledge as return values
- Plugin registers it in the knowledge bank

### Intelligence Flow:
- Plugin has AI socket via IntelligenceAwarePluginBase
- Plugin asks AI: "which strategy for this data?" → gets recommendation
- Plugin passes recommendation as options to strategy method
- Strategy executes with the given options (doesn't know AI was involved)
- Plugin collects strategy metrics, feeds back to AI for learning

### Target hierarchy:
```
IStrategy (interface — Name, Description, Characteristics)
└── StrategyBase (lifecycle, dispose, metadata, logging hooks)
    ├── EncryptionStrategyBase (encrypt/decrypt contract)
    ├── CompressionStrategyBase (compress/decompress contract)
    ├── StorageStrategyBase (store/retrieve/delete/list contract)
    ├── SecurityStrategyBase (authenticate/authorize contract)
    ├── KeyManagementStrategyBase (generate/rotate/store key contract)
    ├── ComplianceStrategyBase (audit/verify/report contract)
    ├── InterfaceStrategyBase (start/handle/stop contract)
    ├── ConnectorStrategyBase (connect/execute/disconnect + retry/pooling)
    ├── ComputeStrategyBase (execute/sandbox contract)
    ├── ObservabilityStrategyBase (metrics/trace/log/health contract)
    ├── ReplicationStrategyBase (replicate/sync/resolve contract)
    ├── MediaStrategyBase (transcode/extract contract)
    ├── StreamingStrategyBase (publish/subscribe contract)
    ├── FormatStrategyBase (serialize/deserialize contract)
    ├── TransitStrategyBase (transfer/receive contract)
    └── DataManagementStrategyBase (catalog/lineage/quality contract)
```

---

## AD-06: Dead Code Cleanup Policy

**Decision:** Remove truly dead code (classes, files, interfaces referenced by nothing). Keep future-ready interfaces for unreleased hardware/technology.

**Keep (future-ready):**
- Interfaces/bases for hardware not yet available (brain-reading encryption, quantum crypto, neuromorphic computing, DNA storage)
- Abstract contracts where the domain is defined but implementation awaits SDK releases
- Hardware-specific bases (RDMA, io_uring, NUMA, TPM2, HSM) — hardware exists but may not be present on every machine

**Remove (truly dead):**
- Classes/files with zero references (no inheritance, no instantiation, no import)
- Superseded implementations (logic reimplemented elsewhere)
- Duplicate code from copy-paste that was never cleaned up
- Obsolete specialized base classes whose logic moves to composable services (per AD-03)
- Test stubs, placeholder files, commented-out code blocks

**Verification:** Before removing any file, confirm:
1. Zero references in codebase (grep for class name, file name)
2. Not referenced in tests
3. Not a future-ready interface for unreleased technology
4. Logic is available elsewhere (if it was useful, confirm it's been extracted)

---

## AD-07: Base Class Consolidation Numbers

**Current state:**
- ~111+ plugin base classes (many unused by any Ultimate plugin)
- ~7 fragmented strategy bases (each independently implementing same boilerplate)
- ~1,500+ strategy classes across all plugins

**Target state:**
- ~15-20 domain plugin base classes (from AD-01 hierarchy)
- ~15-16 domain strategy bases (from AD-05 hierarchy)
- ~1,500 strategy classes (same count, but inheriting correct bases with less boilerplate)
- Specialized bases → composable services (per AD-03)

**Boilerplate eliminated:**
- ~1,000 lines of duplicated intelligence code removed from strategy bases
- ~50+ lines of capability/knowledge code removed per strategy (×1,500 strategies)
- Common lifecycle/dispose consolidated into StrategyBase once

---

## Summary Table

| ID | Decision | Impact |
|----|----------|--------|
| AD-01 | Two branches: DataPipeline + Feature | Clarifies plugin identity |
| AD-02 | Single encryption/compression base | Eliminates unnecessary split |
| AD-03 | Specialized bases → composable services | Unlocks composition for Ultimate plugins |
| AD-04 | Object storage core + translation layer | Uniform foundation for all features |
| AD-05 | Flat strategy hierarchy, no intelligence | Massive simplification (~1,000 lines removed) |
| AD-06 | Dead code cleanup (keep future-ready) | Cleaner codebase, less confusion |
| AD-07 | 111+ bases → ~15-20 domain bases | Maintainable hierarchy |

---

## AD-08: Zero Regression Policy — DO NOT LOSE IMPLEMENTED LOGIC

**Decision:** The v2.0 refactor MUST NOT lose ANY already-implemented logic, functionality, or behavior. Every line of production code that was painstakingly implemented across v1.0's 21 phases, 116 plans, 863 commits, and 1,110,244 lines of C# MUST be preserved or correctly migrated.

**Rationale:** v1.0 took 30 days of intensive implementation across 60 plugins with ~1,500 strategies covering encryption, compression, storage, RAID, security, compliance, interfaces, compute, formats, media, governance, AEDS, marketplace, app platform, WASM ecosystem, data transit, and more. Losing any of this through careless refactoring would be catastrophic.

**Mandatory Rules for ALL v2.0 Work:**

1. **VERIFY BEFORE MODIFY**: Before changing any file, read and understand what it does. Never assume code is unused without grep verification.
2. **EXTRACT, DON'T DELETE**: When moving logic from specialized bases to composable services (AD-03), the logic MUST be extracted into the new location FIRST, verified to compile and work, and ONLY THEN can the old location be deprecated.
3. **BEHAVIORAL EQUIVALENCE**: After any strategy migration (Phase 25b), the strategy MUST produce identical results for identical inputs. This is not optional — it is verified by tests.
4. **BASE CLASS MIGRATION ≠ LOGIC REMOVAL**: Changing a plugin's base class (Phase 27) means updating the inheritance chain. It does NOT mean removing the plugin's unique functionality. All 60 plugins must retain their full feature set.
5. **STRATEGY BOILERPLATE vs STRATEGY LOGIC**: When removing "boilerplate" from strategies (intelligence/capability/dispose code that moves to the plugin level), only remove code that is TRULY redundant. If a strategy has custom dispose logic (e.g., releasing hardware resources), that STAYS.
6. **COMPILE + TEST GATE**: Every plan execution MUST end with a successful `dotnet build` and `dotnet test`. If the build breaks or tests fail, the plan is NOT complete.
7. **NO SILENT REMOVALS**: Any file/class removal must be logged with justification. Dead code cleanup (Phase 28) happens AFTER migration is verified, not during.

**What "No Regression" Means Specifically:**
- All 1,039+ tests continue to pass at every phase boundary
- All 60 plugins compile and retain their full strategy catalogs
- All ~1,500 strategies produce identical results after migration
- All message bus subscriptions, capability registrations, and knowledge registrations still function
- All distributed features (AEDS, replication, RAID, transit) still operate correctly
- All interface protocols (REST, gRPC, GraphQL, SQL wire, WebSocket, etc.) still work
- All security features (encryption, access control, key management, compliance, zero trust) remain functional
- All data formats (columnar, graph, scientific, lakehouse, streaming, media) still parse and serialize correctly

**Verification Approach:**
- Phase 24/25: Build compiles, existing tests pass, adapter wrappers maintain backward compat
- Phase 25b: Each strategy domain migration verified before proceeding to next
- Phase 27: Each plugin batch verified individually
- Phase 28: Dead code removal only AFTER Phase 27 verifies everything works
- Phase 30: Final comprehensive regression test suite

---

## Summary Table

| ID | Decision | Impact |
|----|----------|--------|
| AD-01 | Two branches: DataPipeline + Feature | Clarifies plugin identity |
| AD-02 | Single encryption/compression base | Eliminates unnecessary split |
| AD-03 | Specialized bases → composable services | Unlocks composition for Ultimate plugins |
| AD-04 | Object storage core + translation layer | Uniform foundation for all features |
| AD-05 | Flat strategy hierarchy, no intelligence | Massive simplification (~1,000 lines removed) |
| AD-06 | Dead code cleanup (keep future-ready) | Cleaner codebase, less confusion |
| AD-07 | 111+ bases → ~15-20 domain bases | Maintainable hierarchy |
| AD-08 | ZERO REGRESSION — preserve all v1.0 logic | Protects 30 days of implementation work |

---

## AD-09: Unified User Interface — Shared ↔ UltimateInterface Connection

**Decision:** CLI and GUI are thin UI wrappers over `DataWarehouse.Shared`. Shared connects to `UltimateInterfacePlugin` (server-side) via the message bus through the kernel. No new plugin or project needed — connect the existing pieces.

**Rationale:** The architecture already has all the components. Shared has CommandExecutor, InstanceManager, MessageBridge, CapabilityManager. UltimateInterfacePlugin already inherits IntelligenceAwarePluginBase (AI socket, graceful degradation) and has 68+ protocol strategies. The kernel already publishes `capability.changed`, `plugin.loaded`, `plugin.unloaded` events. The Knowledge Bank already supports dynamic queries. These just aren't wired together for dynamic CLI/GUI reflection.

**Architecture:**
```
CLI executable ──┐
                 ├── DataWarehouse.Shared (command routing, MessageBridge, CapabilityManager)
GUI executable ──┘           ↕ (message bus via kernel)
                         Kernel (routes messages, manages plugins)
                           ↕ (message bus)
                   UltimateInterfacePlugin (IntelligenceAware, 68+ strategies)
                           ↕
                   Capability Registry + Knowledge Bank
```

**What needs wiring:**

1. **Dynamic capability reflection**: Shared subscribes to `capability.changed`, `plugin.loaded`, `plugin.unloaded` events via MessageBridge. Maintains a `DynamicCommandRegistry` that auto-updates available commands/features. CLI/GUI read from this registry — no hardcoded command lists.

2. **NLP query routing**: When user asks "What encryption algorithms are available?", the flow is:
   - CLI/GUI → Shared → MessageBridge → Kernel → UltimateInterfacePlugin
   - UltimateInterfacePlugin uses its IntelligenceAwarePluginBase AI socket to parse the NLP query
   - Queries Knowledge Bank for capability answers
   - Returns structured response
   - If Intelligence unavailable: falls back to direct Capability Registry lookup (keyword matching). Manual mode always works.

3. **Dynamic command generation**: When UltimateEncryption loads at runtime:
   - Kernel loads plugin → plugin registers 30+ capabilities
   - Publishes `capability.changed` on message bus
   - Shared receives event → DynamicCommandRegistry updates
   - CLI: `dw encrypt --algorithm aes-256-gcm` now available
   - GUI: Encryption panel appears dynamically
   - No restart of kernel, CLI, or GUI needed

4. **Feature parity**: CLI and GUI both read from the same Shared `DynamicCommandRegistry`. Whatever one can do, the other can do. Guaranteed by design — single source of truth.

**What already works:**
- PluginCapabilityRegistry has `OnCapabilityRegistered`, `OnCapabilityUnregistered`, `OnAvailabilityChanged`
- Message bus topics: `capability.register`, `capability.unregister`, `capability.changed`
- Knowledge Bank: `QueryAsync(KnowledgeQuery)` with topic patterns, types, text search
- Shared's InstanceManager already handles Local/Remote/InProcess connections
- CLI already delegates all business logic to Shared via CommandExecutor

**What's missing:**
- Shared doesn't subscribe to capability change events (no dynamic reflection)
- NLP queries aren't connected to UltimateInterface's AI socket
- CommandExecutor uses hardcoded command lists instead of dynamic generation
- No mechanism enforcing CLI/GUI parity (both should read same registry)

---

## AD-10: Deployment Modes — Three Operating Modes for CLI/GUI

**Decision:** DW CLI/GUI supports three deployment modes: Standard Client, Live Mode, and Install Mode. No new projects needed — use existing DataWarehouseHost, Launcher, and Shared infrastructure.

**Rationale:** DataWarehouseHost already defines Install, Connect, Embedded, and Service modes. The infrastructure exists but isn't exposed as user-facing CLI/GUI commands, and several operational methods are stubs. Complete the implementation, wire up the CLI commands, and add USB/portable awareness.

### Mode 1: Standard Client (Connect to any DW instance)

**What it does:** CLI/GUI connects to any running DW instance (local, remote, or cluster) to manage and configure it. The user must have appropriate access and permissions.

**Architecture:**
```
CLI/GUI (client-side)                    DW Instance (server-side)
┌────────────────────┐                  ┌──────────────────────────┐
│ DataWarehouse.Shared│                  │ DataWarehouse.Launcher   │
│ ├── InstanceManager │◄────────────────►│ ├── ServiceHost          │
│ ├── MessageBridge   │   HTTP/gRPC/TCP  │ ├── DataWarehouseHost    │
│ ├── CapabilityMgr   │                  │ └── Kernel + Plugins     │
│ └── CommandExecutor │                  │                          │
└────────────────────┘                  └──────────────────────────┘
```

**What exists:**
- `dw connect --host <host> --port <port>` CLI command
- `InstanceManager` with Local/Remote/InProcess connections
- `MessageBridge` with TCP client (length-prefixed JSON)
- `DataWarehouseHost.ConnectAsync()` with Local/Remote/Cluster support
- `RemoteInstanceConnection` using HTTP API (`/api/v1/info`, `/api/v1/capabilities`, etc.)
- `LocalInstanceConnection` using named pipes/IPC
- `ClusterInstanceConnection` for multi-node clusters
- Launcher as service/daemon with gRPC/HTTP endpoints
- Dashboard as web API with REST, SignalR, JWT auth

**What needs completing:**
- `ServerStartCommand` must actually start the kernel (currently `Task.Delay(100)` placeholder)
- `MessageBridge.SendInProcessAsync()` must use real in-memory message queue (currently returns mock)
- `InstanceManager.ExecuteAsync()` must not return mock data when disconnected (should error)
- Protocol alignment: MessageBridge uses raw TCP, Launcher/Dashboard use HTTP/gRPC. Either unify or support both.
- Launcher must expose a listener endpoint that MessageBridge can connect to

**No new projects needed.** Server-side: Launcher already exists. Client-side: Shared/InstanceManager already exists. AEDS already has ServerDispatcher (server) and ClientCourier (client) for content distribution. For management/control, Launcher + Dashboard cover the server-side.

### Mode 2: Live Mode (In-memory, no persistence — "Linux Live CD" style)

**What it does:** A portable DW instance runs entirely in memory with no persistence. Perfect for USB drives, demo environments, or evaluation. CLI/GUI auto-connect to the local live instance.

**Architecture:**
```
USB Drive / Portable Media
├── dw.exe (CLI)
├── dw-gui.exe (GUI)
├── dw-live.exe (Live kernel — embedded mode, no persistence)
├── live-config.json (preconfigured embedded settings)
└── plugins/ (minimal plugin set)

On launch:
1. dw-live.exe starts DataWarehouseHost.RunEmbeddedAsync(PersistData=false, ExposeHttp=true)
2. dw.exe / dw-gui.exe auto-detect local live instance on localhost:8080
3. User works in DW — all data in memory
4. On shutdown — everything vanishes (no trace on host machine)
```

**What exists:**
- `DataWarehouseHost.RunEmbeddedAsync()` with `EmbeddedConfiguration { PersistData = false, MaxMemoryMb = 512 }`
- `OperatingMode.Embedded` enum value
- `AdapterRunner` for running the kernel in embedded mode

**What needs implementing:**
- CLI command: `dw live [--port 8080] [--memory 512]` — starts embedded DW instance
- Live profile: pre-configured `EmbeddedConfiguration` bundled with installer/USB
- Auto-discovery: CLI/GUI detect local live instance (check localhost ports, named pipe, or marker file)
- USB/portable detection: detect if running from removable media, adapt paths accordingly (no writes to host filesystem)
- Minimal plugin set for live mode: Storage (in-memory), Interface (REST for CLI/GUI), Intelligence (optional)

### Mode 3: Install Mode (Deploy new or copy "DW-on-a-stick" to local machine)

**What it does:** CLI/GUI installs DW to the local machine. Two sub-modes:
- **Clean install**: Fresh DW instance with user-chosen configuration
- **Copy from USB**: Clone an entire portable DW instance (with data, config, plugins) from USB to local disk, remap paths, register services, set up autostart

**Architecture (Clean Install):**
```
dw install --path "C:\DataWarehouse" --admin-password "..." --autostart --service
    ↓
DataWarehouseHost.InstallAsync()
    ├── Step 1: Validate configuration
    ├── Step 2: Create directories (data, config, plugins, logs)
    ├── Step 3: Copy binaries and plugin DLLs
    ├── Step 4: Initialize configuration (datawarehouse.json)
    ├── Step 5: Initialize plugins (copy, register)
    ├── Step 6: Create admin user (in security store)
    ├── Step 7: Register service (sc create / systemd / launchd)
    └── Step 8: Configure autostart (Task Scheduler / systemd enable / launchctl)
```

**Architecture (Copy from USB):**
```
dw install --from-usb "E:\" --path "C:\DataWarehouse" --autostart --service
    ↓
    ├── Step 1: Detect USB source layout (validate it's a DW instance)
    ├── Step 2: Copy entire DW tree from USB to target path
    ├── Step 3: Remap all paths in configuration (USB drive letter → local path)
    ├── Step 4: Copy data (if present and user opts in)
    ├── Step 5: Register service with remapped paths
    ├── Step 6: Configure autostart
    └── Step 7: Verify installation (start kernel, run health check)
```

**What exists:**
- `DataWarehouseHost.InstallAsync()` with full 7-step pipeline
- `InstallConfiguration` with InstallPath, DataPath, IncludedPlugins, CreateService, AutoStart, AdminPassword
- `InstallStep` enum for progress tracking
- Platform-aware service registration scaffold (Windows/Linux detected)
- UltimateDeployment plugin with 65+ deployment strategies

**What needs completing:**
- `CopyFilesAsync()` — currently stub, needs real binary/DLL copy logic
- `RegisterServiceAsync()` — currently just logs, needs real `sc create` (Windows), systemd unit file (Linux), `launchctl` (macOS)
- `CreateAdminUserAsync()` — currently just logs, needs real security store integration
- `InitializePluginsAsync()` — currently just creates dirs, needs actual plugin DLL copy
- CLI command: `dw install [--path <path>] [--from-usb <source>] [--autostart] [--service] [--admin-password <pwd>]`
- USB source detection and validation
- Path remapping logic for copy-from-USB mode
- Post-install verification (start kernel, run health check)
- Autostart configuration beyond service registration (Task Scheduler, systemd enable, launchd plist)

**Platform-specific service operations:**
| Platform | Service Registration | Autostart | Service Name |
|----------|---------------------|-----------|-------------|
| Windows | `sc create DataWarehouse binPath=...` | Task Scheduler or `sc config start=auto` | DataWarehouse |
| Linux | Write `/etc/systemd/system/datawarehouse.service` unit file | `systemctl enable datawarehouse` | datawarehouse |
| macOS | Write `~/Library/LaunchAgents/com.datawarehouse.plist` | `launchctl load` | com.datawarehouse |

---

## AD-11: Capability Delegation — No Inline Duplication of Ultimate Plugin Features

**Decided:** 2026-02-17
**Context:** Audit of all 60+ plugins revealed widespread inline implementations of capabilities that should be delegated to their owning Ultimate plugins via message bus.

**Decision:** Every cross-cutting capability has exactly ONE owning plugin. All other plugins MUST delegate to that owner via message bus. No plugin may implement inline versions of capabilities owned by another plugin.

**Rationale:** When plugins implement their own crypto, hashing, transport, or other cross-cutting capabilities inline:
1. **Security inconsistency** — different plugins may use different key sizes, algorithms, or modes
2. **Audit difficulty** — crypto/hashing code scattered across 60+ plugins cannot be centrally audited
3. **Algorithm upgrades** — upgrading SHA-256 to SHA-3 requires touching every plugin instead of one
4. **Violation of microkernel** — the entire point of the plugin architecture is that each capability is owned by ONE plugin

**Capability Ownership Registry:**

| Capability | Owner Plugin | Bus Topics | Notes |
|------------|-------------|------------|-------|
| Encryption/Decryption | UltimateEncryption | `encryption.encrypt`, `encryption.decrypt` | AES-GCM, ChaCha20, RSA, ECDSA, 30+ algorithms |
| Hashing/Integrity | UltimateDataIntegrity | `integrity.hash.compute`, `integrity.hash.verify` | SHA-2, SHA-3, Keccak, HMAC, BLAKE, 15+ providers |
| Blockchain/Anchoring | UltimateBlockchain | `blockchain.anchor`, `blockchain.verify`, `blockchain.chain` | Merkle trees, chain validation |
| Tamper-Proof Orchestration | TamperProof | `tamperproof.*` | Orchestrates integrity + blockchain + WORM |
| Data Transport | UltimateDataTransit | `transit.transfer.request` | HTTP/2, HTTP/3, gRPC, FTP, SFTP, SCP, P2P |
| AI/ML Inference | UltimateIntelligence | `intelligence.*` | 12 AI providers |
| Key Management | UltimateKeyManagement | `keymanagement.*` | 30+ key strategies |
| Storage I/O | UltimateStorage | `storage.*` | 130+ backends |
| Access Control | UltimateAccessControl | `accesscontrol.*` | 9 models, MFA, Zero Trust |
| Compression | UltimateCompression | `compression.*` | 40+ algorithms |
| Observability | UniversalObservability | `observability.*` | Metrics, traces, logs |

**Delegation Pattern:**
```csharp
// WRONG — inline crypto in a non-crypto plugin
using var aes = new AesGcm(key, 16);
aes.Encrypt(nonce, plaintext, ciphertext, tag);

// RIGHT — delegate to UltimateEncryption via bus
var msg = new PluginMessage("encryption.encrypt");
msg.Payload["data"] = plaintext;
msg.Payload["key"] = key;
var response = await MessageBus.PublishAndWaitAsync("encryption.encrypt", msg, ct);
var encrypted = (byte[])response.Payload["result"];
```

**Exceptions (inline allowed):**
1. **Protocol-specific signatures** — AWS Signature V4 (HMACSHA256) in observability strategies is protocol-mandated and cannot be delegated
2. **Boot-time operations** — if the message bus is not yet available during plugin initialization, limited inline crypto for self-verification is acceptable (must be documented)
3. **Performance-critical tight loops** — if profiling proves bus delegation adds unacceptable latency (>1ms per call in a loop of >10,000 iterations), inline with documented justification and matching algorithm to the owner plugin

**Enforcement:**
- All executing agents MUST search for existing capabilities before implementing inline
- Code review must flag any new `Aes.Create()`, `SHA256.Create()`, `new HttpClient()`, `new TcpClient()` outside the owning plugin
- Build-time: Roslyn analyzer rule planned for v3.0+ to enforce at compile time

**Audit Results (Phase 31.2 baseline):**
- ~73 inline crypto sites found outside UltimateEncryption
- ~196+ inline hashing sites found outside TamperProof/UltimateDataIntegrity
- ~166+ transport duplications found outside UltimateDataTransit
- Phase 31.2 will remediate ALL of these

---

## Summary Table (Updated)

| ID | Decision | Impact |
|----|----------|--------|
| AD-01 | Two branches: DataPipeline + Feature | Clarifies plugin identity |
| AD-02 | Single encryption/compression base | Eliminates unnecessary split |
| AD-03 | Specialized bases → composable services | Unlocks composition for Ultimate plugins |
| AD-04 | Object storage core + translation layer | Uniform foundation for all features |
| AD-05 | Flat strategy hierarchy, no intelligence | Massive simplification (~1,000 lines removed) |
| AD-06 | Dead code cleanup (keep future-ready) | Cleaner codebase, less confusion |
| AD-07 | 111+ bases → ~15-20 domain bases | Maintainable hierarchy |
| AD-08 | ZERO REGRESSION — preserve all v1.0 logic | Protects 30 days of implementation work |
| AD-09 | Unified UI — Shared ↔ UltimateInterface | 100% CLI/GUI feature parity, dynamic capability reflection |
| AD-10 | Three deployment modes (Client/Live/Install) | Portable, installable, and client-server DW |
| AD-11 | Capability delegation — no inline duplication | Centralized crypto, hashing, transport; auditable and upgradeable |

---
*Decided: 2026-02-12 (AD-01 through AD-10), 2026-02-17 (AD-11)*
*Participants: User (architect), Claude (analysis)*
