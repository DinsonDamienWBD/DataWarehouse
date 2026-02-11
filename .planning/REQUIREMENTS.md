# Requirements: DataWarehouse SDK v2.0

**Defined:** 2026-02-11
**Core Value:** SDK must pass hyperscale/military-level code review — clean hierarchy, secure by default, distributed-ready, zero warnings.

## v2.0 Requirements

### Plugin Hierarchy

- [ ] **HIER-01**: PluginBase implements IDisposable and IAsyncDisposable with proper Dispose(bool) pattern and GC.SuppressFinalize
- [ ] **HIER-02**: PluginBase has complete lifecycle methods: Initialize(), Execute(), Shutdown() with CancellationToken on all async overloads
- [ ] **HIER-03**: PluginBase capability registry allows plugins to register, query, and deregister capabilities at runtime
- [ ] **HIER-04**: PluginBase knowledge registry enables plugins to register and query knowledge objects via ConcurrentDictionary cache
- [ ] **HIER-05**: IntelligenceAwarePluginBase (IntelligentPluginBase) extends FeaturePluginBase with UltimateIntelligence socket, graceful degradation when unavailable
- [ ] **HIER-06**: All feature-specific plugin base classes (Encryption, Compression, Storage, Security, Observability, Interface, Format, Streaming, Media, Processing) inherit from IntelligenceAwarePluginBase
- [ ] **HIER-07**: UltimateIntelligence plugin inherits from PluginBase directly (not IntelligenceAwarePluginBase — it IS the intelligence provider)
- [ ] **HIER-08**: Each feature-specific base class implements common functionality for its domain without code duplication across Ultimate plugins
- [ ] **HIER-09**: All base classes compile and all 60 existing plugins still build without errors after hierarchy changes

### Strategy Hierarchy

- [ ] **STRAT-01**: Unified StrategyBase root class exists with common strategy lifecycle (Initialize, Execute, Cleanup), capability declaration, and CancellationToken support
- [ ] **STRAT-02**: Multi-tier strategy hierarchy: StrategyBase → IntelligenceAwareStrategyBase → feature-specific strategy bases (InterfaceStrategyBase, MediaStrategyBase, etc.)
- [ ] **STRAT-03**: IntelligenceAwareStrategyBase provides UltimateIntelligence integration with graceful degradation (mirroring IntelligenceAwarePluginBase pattern)
- [ ] **STRAT-04**: All 7 existing fragmented strategy bases are consolidated under the unified hierarchy via adapter wrappers for backward compatibility
- [ ] **STRAT-05**: Feature-specific strategy base classes implement domain-common functionality (e.g., MediaStrategyBase has format detection, InterfaceStrategyBase has protocol negotiation)
- [ ] **STRAT-06**: All existing plugin strategies still compile and function identically after hierarchy unification

### Distributed Infrastructure

- [ ] **DIST-01**: SDK defines IClusterMembership contract for node join/leave/discovery with health monitoring
- [ ] **DIST-02**: SDK defines ILoadBalancerStrategy contract with pluggable algorithms (round-robin, consistent hashing, weighted, resource-aware)
- [ ] **DIST-03**: SDK defines IP2PNetwork and IGossipProtocol contracts for peer-to-peer data distribution
- [ ] **DIST-04**: SDK defines IAutoScaler and IScalingPolicy contracts for elastic scaling based on storage/performance metrics
- [ ] **DIST-05**: SDK defines IReplicationSync contract supporting online (real-time) and offline (air-gap) synchronization with conflict resolution
- [ ] **DIST-06**: SDK defines IAutoTier contract for automatic data placement based on access patterns and cost optimization
- [ ] **DIST-07**: SDK defines IAutoGovernance contract for policy enforcement at SDK level (retention, classification, compliance)
- [ ] **DIST-08**: FederatedMessageBus wraps IMessageBus with transparent local/remote routing using consistent hashing
- [ ] **DIST-09**: Multi-phase plugin initialization: construction (zero deps) → initialization (MessageBus) → activation (distributed coordination)
- [ ] **DIST-10**: In-memory single-node implementations exist for all distributed contracts (backward compatible — single laptop works without cluster)
- [ ] **DIST-11**: Auto-scaling prompts user when nodes reach capacity limits, accepts new node information, deploys and integrates new nodes automatically
- [ ] **DIST-12**: SWIM gossip protocol implementation for decentralized cluster membership and failure detection
- [ ] **DIST-13**: Raft consensus implementation for leader election in multi-node clusters
- [ ] **DIST-14**: Multi-master replication with CRDT conflict resolution for distributed writes
- [ ] **DIST-15**: P2P gossip-based data replication across cluster nodes
- [ ] **DIST-16**: Consistent hashing load balancer with virtual nodes for cache-friendly request distribution
- [ ] **DIST-17**: Resource-aware load balancer monitoring CPU/memory for adaptive routing decisions

### Decoupling Verification

- [ ] **DECPL-01**: Zero plugins or kernel depend on any other plugin directly — all depend only on SDK
- [ ] **DECPL-02**: All inter-plugin and plugin-kernel communication uses Commands/Messages via message bus only
- [ ] **DECPL-03**: Kernel leverages capability registry and knowledge bank for informed routing decisions
- [ ] **DECPL-04**: All plugins can register capabilities and knowledge into system knowledge bank
- [ ] **DECPL-05**: All plugins leverage auto-scaling, load balancing, P2P, auto-sync, auto-tier, auto-governance from SDK base classes

### Plugin Updates

- [ ] **UPLT-01**: All Ultimate plugins inherit from their respective feature-specific plugin base classes
- [ ] **UPLT-02**: All Ultimate plugins leverage new distributed infrastructure features from SDK base classes
- [ ] **UPLT-03**: All Ultimate plugin strategies inherit from appropriate strategy base in the unified hierarchy
- [ ] **UPST-01**: All standalone plugins inherit from IntelligenceAwarePluginBase at minimum
- [ ] **UPST-02**: All standalone plugin strategies leverage the unified strategy base class hierarchy
- [ ] **UPST-03**: All standalone plugins leverage distributed infrastructure features from SDK

### CLI

- [ ] **CLI-01**: DataWarehouse.CLI uses current System.CommandLine API (not deprecated NamingConventionBinder) for all command parsing and binding

### Memory Safety

- [ ] **MEM-01**: All key material, tokens, and passwords are wiped from memory using CryptographicOperations.ZeroMemory after use
- [ ] **MEM-02**: Hot-path buffer allocations use ArrayPool<byte> or MemoryPool<byte> instead of raw new byte[]
- [ ] **MEM-03**: All collections exposed in public APIs are bounded with configurable maximum sizes
- [ ] **MEM-04**: PluginBase.Dispose() properly cleans up knowledge cache, capability subscriptions, and message bus subscriptions
- [ ] **MEM-05**: All IAsyncDisposable implementations follow the async dispose pattern with DisposeAsyncCore()

### Cryptographic Hygiene

- [ ] **CRYPTO-01**: All secret/hash comparisons use CryptographicOperations.FixedTimeEquals (constant-time) to prevent timing attacks
- [ ] **CRYPTO-02**: All cryptographic random generation uses RandomNumberGenerator, never System.Random
- [ ] **CRYPTO-03**: Key rotation contracts defined in SDK (IKeyRotationPolicy) usable by any plugin
- [ ] **CRYPTO-04**: Algorithm agility — no hardcoded algorithm choices in SDK; all configurable via strategy
- [ ] **CRYPTO-05**: FIPS 140-3 compliance verified — all crypto uses .NET BCL implementations (no custom crypto)
- [ ] **CRYPTO-06**: Distributed message authentication uses HMAC-SHA256 signatures with replay protection

### Input Validation

- [ ] **VALID-01**: Every public SDK method validates inputs before processing (null checks, range checks, format validation)
- [ ] **VALID-02**: All file/URI operations include path traversal protection
- [ ] **VALID-03**: All incoming data has configurable size limits (messages, knowledge objects, capability payloads)
- [ ] **VALID-04**: All regex operations use bounded timeouts (Regex.MatchTimeout) to prevent ReDoS attacks
- [ ] **VALID-05**: Plugin identity verification via cryptographic keys for distributed message authentication

### Resilience Contracts

- [ ] **RESIL-01**: SDK defines ICircuitBreaker contract with Open/Closed/HalfOpen states for cross-service calls
- [ ] **RESIL-02**: SDK defines IBulkheadIsolation contract for per-plugin resource limits (memory, CPU, connections)
- [ ] **RESIL-03**: All async operations have configurable timeout policies with sensible defaults
- [ ] **RESIL-04**: Graceful shutdown propagates CancellationToken from kernel through all plugins to strategy level
- [ ] **RESIL-05**: Dead letter queue contract for failed message bus messages with retry policies

### Observability Contracts

- [ ] **OBS-01**: SDK provides ActivitySource for distributed tracing at plugin, strategy, kernel, and registry boundaries
- [ ] **OBS-02**: All SDK operations include structured logging with mandatory correlation IDs
- [ ] **OBS-03**: IHealthCheck interface required by all plugins — kernel aggregates health status
- [ ] **OBS-04**: Resource usage metering per plugin (memory, CPU, I/O) available via SDK contracts
- [ ] **OBS-05**: Audit trail interface for security-sensitive operations (immutable, append-only)

### API Contract Safety

- [ ] **API-01**: All public SDK data transfer types use C# records or init-only setters (immutable by default)
- [ ] **API-02**: Public APIs use strongly-typed contracts instead of Dictionary<string, object> where possible
- [ ] **API-03**: SdkCompatibility attributes on all public types for versioning and backward compatibility tracking
- [ ] **API-04**: Null-object pattern for optional dependencies (no scattered null checks)

### Build Safety

- [ ] **BUILD-01**: TreatWarningsAsErrors enabled incrementally across all projects (SDK + 60 plugins) via category-based rollout
- [ ] **BUILD-02**: Roslyn analyzers added: Microsoft.CodeAnalysis.NetAnalyzers, SecurityCodeScan, SonarAnalyzer, Roslynator, BannedApiAnalyzers
- [ ] **BUILD-03**: EnforceCodeStyleInBuild enabled in Directory.Build.props
- [ ] **BUILD-04**: XML documentation completeness enforced on all public APIs in SDK
- [ ] **BUILD-05**: Zero compiler warnings in final build (excluding NuGet-sourced warnings)

### Supply Chain Security

- [ ] **SUPPLY-01**: NuGet vulnerability audit passes with zero known vulnerabilities (dotnet list package --vulnerable)
- [ ] **SUPPLY-02**: All package versions pinned to exact versions (no floating ranges)
- [ ] **SUPPLY-03**: SBOM generated for SDK and all plugins (CycloneDX or SPDX format)
- [ ] **SUPPLY-04**: Minimal dependency surface — SDK maintains ≤6 direct PackageReferences

### Testing

- [ ] **TEST-01**: Full solution builds with zero errors after all refactoring
- [ ] **TEST-02**: New unit tests cover all new/modified base classes (plugin hierarchy, strategy hierarchy)
- [ ] **TEST-03**: Behavioral verification tests confirm existing strategies produce identical results after hierarchy migration
- [ ] **TEST-04**: All existing 1,039+ tests continue to pass
- [ ] **TEST-05**: Distributed infrastructure contracts have integration tests with in-memory implementations
- [ ] **TEST-06**: Security hardening verified via Roslyn analyzer clean pass (zero suppressed warnings without justification)

## v3.0 Requirements (Deferred)

- **PERF-01**: Source generator-based plugin discovery (replace reflection for AOT compatibility)
- **PERF-02**: Span<T>/Memory<T> zero-allocation hot paths throughout SDK
- **CLOUD-01**: Azure Key Vault integration for algorithm agility crypto migration
- **CLOUD-02**: Kubernetes-native auto-scaling with HPA integration

## Out of Scope

| Feature | Reason |
|---------|--------|
| Orleans/Aspire as SDK dependencies | Vendor lock-in, violates dependency-lean principle — use SDK contracts instead |
| SecureString usage | Deprecated in .NET — use CryptographicOperations.ZeroMemory |
| Global TreatWarningsAsErrors day-one | Would break 1.1M LOC build — use incremental category rollout |
| Real-time sync everywhere | Complexity without value — use eventual consistency with configurable intervals |
| UI/Dashboard changes | SDK-only milestone |
| Synchronous blocking APIs | Causes deadlocks — all APIs must be async with CancellationToken |

## Traceability

Updated during roadmap creation.

| Requirement | Phase | Status |
|-------------|-------|--------|
| (populated by roadmapper) | | |

**Coverage:**
- v2.0 requirements: 76 total
- Mapped to phases: 0
- Unmapped: 76

---
*Requirements defined: 2026-02-11*
*Last updated: 2026-02-11 after research synthesis*
