# Phase 26: Distributed Contracts & Resilience - Research

**Researched:** 2026-02-14
**Domain:** Distributed infrastructure contracts, resilience patterns, observability for SDK-based microkernel architecture
**Confidence:** HIGH

## Summary

Phase 26 defines all distributed infrastructure contracts in the SDK with security primitives baked in, builds in-memory single-node implementations for backward compatibility, and adds resilience/observability contracts available to all plugins. This is a **contract-first** phase -- define interfaces before any real distributed implementation (Phase 29).

The SDK already has substantial infrastructure that Phase 26 builds upon: IMessageBus with IAuthenticatedMessageBus (HMAC-SHA256 + replay protection), PluginBase with full lifecycle methods (InitializeAsync, ExecuteAsync, ShutdownAsync), IHealthCheck + HealthCheckAggregator, IResiliencePolicy with circuit breaker states, IDistributedTracing, IMetricsCollector, IRateLimiter, and comprehensive load balancing/fault tolerance configuration. The SDK also has pre-existing types for IFederationNode, IConsensusEngine, IMultiMasterReplication, VectorClock, and various scale/sharding types.

**Key finding:** Much of the supporting infrastructure already exists. The primary gap is the 7 specific distributed contracts (IClusterMembership, ILoadBalancerStrategy, IP2PNetwork, IAutoScaler, IReplicationSync, IAutoTier, IAutoGovernance), the FederatedMessageBus wrapper, the multi-phase plugin initialization (construction/initialization/activation), and in-memory single-node implementations. The resilience and observability contracts are partially present but need refinement to match the specific RESIL-01 through RESIL-05 and OBS-01 through OBS-05 requirements.

**Primary recommendation:** Organize work into 5 plans as defined in the ROADMAP. Leverage existing types heavily -- do not recreate IHealthCheck, IResiliencePolicy, IDistributedTracing, IMetricsCollector, or IMultiMasterReplication. Instead, extend and wrap them with the specific contracts Phase 26 requires.

## Current State per Requirement Area

### DIST-01 through DIST-07: The 7 Distributed Contracts

**Status: NOT YET DEFINED in SDK**

None of these specific interfaces exist yet:
- `IClusterMembership` -- does not exist. Closest: `IFederationNode` (gRPC-oriented, node-level), `NodeHandshake` primitive, `GeoRaftNode` types
- `ILoadBalancerStrategy` -- does not exist as a contract interface. Closest: `LoadBalancingConfig` + `LoadBalancingManager` (implementation-level config, not a pluggable strategy contract)
- `IP2PNetwork` / `IGossipProtocol` -- do not exist. No gossip or P2P abstractions in SDK
- `IAutoScaler` / `IScalingPolicy` -- do not exist. `DeploymentTier` enum and `OperatingMode` exist but no scaling contract
- `IReplicationSync` -- does not exist as specified. `IReplicationService` exists (simple restore-from-replica), `IMultiMasterReplication` exists (full multi-master with CRDT/VectorClock), but no unified online/offline sync contract
- `IAutoTier` -- does not exist. `ITieredStorage` exists (simple move-to-tier), `ITierManager` composable service exists (default in-memory), but no auto-placement contract
- `IAutoGovernance` -- does not exist. `INeuralSentinel` and governance contracts exist but focus on AI-driven governance, not policy-level SDK enforcement

**Gap: All 7 contracts need creation as new SDK interfaces.**

### DIST-08: FederatedMessageBus

**Status: NOT YET DEFINED**

- `IMessageBus` exists with full publish/subscribe, request-response, pattern matching
- `IAuthenticatedMessageBus` extends IMessageBus with HMAC-SHA256 signing + replay protection
- `IAdvancedMessageBus` extends IMessageBus with reliable delivery, confirmation, groups
- `MessageBusBase` abstract class exists with default implementations
- `NullMessageBus` null-object exists
- **No FederatedMessageBus** -- no local/remote routing, no consistent hashing for message distribution

**Gap: FederatedMessageBus needs to wrap IMessageBus with transparent local/remote routing. Should extend IMessageBus so existing code works unchanged.**

### DIST-09: Multi-Phase Plugin Initialization

**Status: PARTIALLY PRESENT**

- `PluginBase` has `InitializeAsync(ct)`, `ExecuteAsync(ct)`, `ShutdownAsync(ct)` (from Phase 24)
- `InitializeAsync` currently sets MessageBus, registers capabilities/knowledge, and calls `OnHandshakeAsync`
- **Missing: Explicit 3-phase model:** construction (zero deps) -> initialization (MessageBus) -> activation (distributed coordination)
- Current model is 2-phase at best: construction + InitializeAsync. No separate "activation" phase for distributed coordination
- No `ActivateAsync` method exists

**Gap: Need to add `ActivateAsync(ct)` to PluginBase for the third phase (distributed coordination). Must be backward-compatible -- plugins that don't override it work unchanged.**

### DIST-10: In-Memory Single-Node Implementations

**Status: PARTIALLY PRESENT**

Existing in-memory implementations:
- `NullMessageBus` -- no-op message bus (exists)
- `DefaultTierManager` -- in-memory tier manager (exists)
- `DefaultCacheManager` -- in-memory cache manager (exists)
- `DefaultStorageIndex` -- in-memory storage index (exists)
- `DefaultConnectionRegistry` -- in-memory connection registry (exists)
- `HealthCheckAggregator` -- in-memory health aggregation (exists)
- `MemoryPressureMonitor` -- in-memory memory monitoring (exists)
- `TokenBucketRateLimiter` -- in-memory rate limiting (exists)
- `AIProviderRegistry` -- in-memory AI provider registry (exists)
- `ConfigurationHotReloader` -- in-memory config (exists)
- `FaultToleranceManager` -- in-memory fault tolerance (exists)
- `LoadBalancingManager` -- in-memory load balancing (exists)

**Missing in-memory implementations for the 7 new contracts + FederatedMessageBus:**
- `InMemoryClusterMembership` -- single-node, always reports self as only member
- `InMemoryLoadBalancerStrategy` -- always routes to self
- `InMemoryP2PNetwork` -- no-op peers
- `InMemoryAutoScaler` -- no-op scaling
- `InMemoryReplicationSync` -- no-op sync (local-only)
- `InMemoryAutoTier` -- delegates to DefaultTierManager
- `InMemoryAutoGovernance` -- no-op policy enforcement
- `InMemoryFederatedMessageBus` -- wraps existing local message bus, no remote routing

**Gap: 8 new in-memory implementations needed.**

### DIST-11: Auto-Scaling User Prompts

**Status: NOT PRESENT**

- No user interaction contract for scaling prompts
- No concept of "accepts new node information, deploys and integrates new nodes"
- This is primarily a UI/interaction contract, not a deep distributed systems contract

**Gap: Need `IScalingPrompt` or integrate into IAutoScaler with callback hooks.**

### RESIL-01: ICircuitBreaker Contract

**Status: PARTIALLY PRESENT**

- `IResiliencePolicy` exists with `CircuitState` (Open/Closed/HalfOpen), `ExecuteAsync<T>`, `Reset()`, `GetStatistics()`
- `ResiliencePolicyConfig` exists with full circuit breaker config (FailureThreshold, FailureWindow, BreakDuration, MaxRetries, etc.)
- `IResiliencePolicyManager` exists (GetPolicy, RegisterPolicy, ResetAll)
- **But**: RESIL-01 specifically asks for `ICircuitBreaker` -- a simpler, focused contract separate from the combo resilience policy

**Assessment: Existing IResiliencePolicy already covers circuit breaker functionality. The planner should decide whether to add a separate `ICircuitBreaker` or alias/wrap existing IResiliencePolicy. Recommendation: Add `ICircuitBreaker` as a focused interface that IResiliencePolicy can implement, for clarity.**

### RESIL-02: IBulkheadIsolation

**Status: NOT PRESENT**

- No bulkhead isolation contract exists
- `IMemoryPressureMonitor` exists (related but different -- monitors memory, not isolates resources)
- `IRateLimiter` exists (related -- limits throughput, not resource partitioning)

**Gap: Need `IBulkheadIsolation` for per-plugin resource limits (memory, CPU, connections).**

### RESIL-03: Configurable Timeout Policies

**Status: PARTIALLY PRESENT**

- `ResiliencePolicyConfig.Timeout` exists (default 30s)
- `IMessageBus.SendAsync` has timeout overload
- `HealthCheckOptions.Timeout` exists
- **But**: No unified `ITimeoutPolicy` or SDK-wide timeout configuration

**Assessment: Timeout support is scattered across individual contracts. Phase 26 should ensure all async operations honor CancellationToken + configurable timeout. This is more of a pattern enforcement than a new contract.**

### RESIL-04: Graceful Shutdown with CancellationToken

**Status: MOSTLY PRESENT**

- `PluginBase.ShutdownAsync(ct)` exists and propagates CancellationToken
- `StrategyBase` has `ShutdownAsync(ct)` (from Phase 25a)
- All async methods throughout SDK accept CancellationToken
- **Gap**: No explicit contract ensuring kernel propagates cancellation through all plugins to strategy level. This is more architectural verification than a new interface.

### RESIL-05: Dead Letter Queue

**Status: NOT PRESENT**

- No dead letter queue contract
- `IAdvancedMessageBus.PublishReliableAsync` hints at delivery guarantees but no DLQ
- `PublishResult` tracks delivery status

**Gap: Need `IDeadLetterQueue` contract for failed messages with retry policies.**

### OBS-01: ActivitySource for Distributed Tracing

**Status: PARTIALLY PRESENT but needs enhancement**

- `IDistributedTracing` exists with StartTrace/StartSpan/ContinueTrace, TraceContext, ITraceScope
- `IObservabilityStrategy` exists with TracingAsync, MetricsAsync, LoggingAsync
- `SpanContext`, `SpanKind`, `SpanStatus`, `SpanEvent`, `TraceContext` types exist (W3C-compatible)
- **But**: No `System.Diagnostics.ActivitySource` integration. The SDK uses custom tracing, not the .NET built-in ActivitySource
- No ActivitySource found in any SDK file (confirmed by grep)

**Gap: Need to either wrap or bridge to `System.Diagnostics.ActivitySource` for standard .NET observability integration. The requirement specifically says "ActivitySource" not custom tracing.**

### OBS-02: Structured Logging with Correlation IDs

**Status: PARTIALLY PRESENT**

- `IDistributedTracing` has CorrelationId in TraceContext
- SDK uses `Microsoft.Extensions.Logging.ILogger` (via NullLoggerProvider)
- `LogEntry` record exists in Observability namespace
- **But**: No mandatory correlation ID enforcement at SDK level

**Gap: Need SDK-level structured logging extension that automatically attaches correlation IDs.**

### OBS-03: IHealthCheck Required by All Plugins

**Status: PRESENT but not enforced**

- `IHealthCheck` interface exists in both `Contracts.IKernelInfrastructure` (via InfrastructurePluginBases) and `Infrastructure.KernelInfrastructure`
- `HealthCheckAggregator` exists with registration/aggregation
- `KernelHealthCheck` built-in check exists
- `HealthProviderPluginBase` exists
- **But**: IHealthCheck is not required by PluginBase -- it's opt-in via HealthProviderPluginBase

**Gap: Need to make IHealthCheck part of the PluginBase contract so kernel can aggregate health from ALL plugins. Default implementation should return Healthy.**

### OBS-04: Resource Usage Metering Per Plugin

**Status: PARTIALLY PRESENT**

- `IMetricsCollector` exists with counters, gauges, histograms, timers
- `MemoryPressureMonitor` exists with per-process stats
- `NodeMetrics` exists with CPU/memory/disk/connections
- **But**: No per-plugin resource metering contract

**Gap: Need `IResourceMeter` or similar contract for per-plugin metering (memory, CPU, I/O).**

### OBS-05: Audit Trail Interface

**Status: NOT PRESENT**

- No immutable, append-only audit trail interface
- `SecurityAudit` topic exists in MessageTopics
- GovernanceJudgment has audit-like properties
- **But**: No formal `IAuditTrail` contract

**Gap: Need `IAuditTrail` interface for security-sensitive operations (immutable, append-only).**

## Gap Analysis Summary

| Requirement | Status | Gap | Effort |
|-------------|--------|-----|--------|
| DIST-01 (IClusterMembership) | Missing | New contract | Medium |
| DIST-02 (ILoadBalancerStrategy) | Missing | New contract | Medium |
| DIST-03 (IP2PNetwork/IGossipProtocol) | Missing | New contracts | Medium |
| DIST-04 (IAutoScaler/IScalingPolicy) | Missing | New contracts | Medium |
| DIST-05 (IReplicationSync) | Missing | New contract (builds on existing types) | Medium |
| DIST-06 (IAutoTier) | Missing | New contract (builds on ITierManager) | Small |
| DIST-07 (IAutoGovernance) | Missing | New contract | Medium |
| DIST-08 (FederatedMessageBus) | Missing | New class wrapping IMessageBus | Large |
| DIST-09 (Multi-phase init) | Partial | Add ActivateAsync to PluginBase | Medium |
| DIST-10 (In-memory impls) | Partial | 8 new in-memory classes | Large |
| DIST-11 (Auto-scaling prompts) | Missing | New interaction contract | Small |
| RESIL-01 (ICircuitBreaker) | Partial | Focused interface (IResiliencePolicy exists) | Small |
| RESIL-02 (IBulkheadIsolation) | Missing | New contract | Medium |
| RESIL-03 (Timeout policies) | Partial | Pattern enforcement, possible ITimeoutPolicy | Small |
| RESIL-04 (Graceful shutdown) | Mostly present | Verification/documentation | Small |
| RESIL-05 (Dead letter queue) | Missing | New contract | Medium |
| OBS-01 (ActivitySource) | Partial | Bridge to System.Diagnostics.ActivitySource | Medium |
| OBS-02 (Structured logging) | Partial | SDK correlation ID enforcement | Small |
| OBS-03 (IHealthCheck all plugins) | Present but opt-in | Make required on PluginBase | Medium |
| OBS-04 (Resource metering) | Partial | New per-plugin metering contract | Medium |
| OBS-05 (Audit trail) | Missing | New IAuditTrail contract | Medium |

## File Inventory

### Existing Files to Modify

| File | What Changes | Why |
|------|-------------|-----|
| `SDK/Contracts/PluginBase.cs` | Add `ActivateAsync(ct)`, default `CheckHealthAsync()` | DIST-09, OBS-03 |
| `SDK/Contracts/IMessageBus.cs` | Add distributed message topics | DIST-08 |

### New Files to Create

**Plan 26-01: Distributed Contracts (DIST-01 through DIST-07)**

| File | Purpose |
|------|---------|
| `SDK/Contracts/Distributed/IClusterMembership.cs` | Node join/leave/discovery with health monitoring |
| `SDK/Contracts/Distributed/ILoadBalancerStrategy.cs` | Pluggable load balancing algorithm contract |
| `SDK/Contracts/Distributed/IP2PNetwork.cs` | P2P network + IGossipProtocol contracts |
| `SDK/Contracts/Distributed/IAutoScaler.cs` | Auto-scaling + IScalingPolicy contracts |
| `SDK/Contracts/Distributed/IReplicationSync.cs` | Online/offline sync with conflict resolution |
| `SDK/Contracts/Distributed/IAutoTier.cs` | Automatic data placement by access patterns |
| `SDK/Contracts/Distributed/IAutoGovernance.cs` | SDK-level policy enforcement contract |

**Plan 26-02: FederatedMessageBus & Multi-Phase Init (DIST-08, DIST-09)**

| File | Purpose |
|------|---------|
| `SDK/Contracts/Distributed/IFederatedMessageBus.cs` | Interface extending IMessageBus with routing |
| `SDK/Contracts/Distributed/FederatedMessageBusBase.cs` | Abstract base with local/remote routing |
| (Modify) `SDK/Contracts/PluginBase.cs` | Add ActivateAsync lifecycle method |

**Plan 26-03: Resilience Contracts (RESIL-01 through RESIL-05)**

| File | Purpose |
|------|---------|
| `SDK/Contracts/Resilience/ICircuitBreaker.cs` | Focused circuit breaker contract |
| `SDK/Contracts/Resilience/IBulkheadIsolation.cs` | Per-plugin resource isolation |
| `SDK/Contracts/Resilience/IDeadLetterQueue.cs` | Failed message handling with retry |
| `SDK/Contracts/Resilience/ITimeoutPolicy.cs` | Unified timeout configuration |
| `SDK/Contracts/Resilience/IGracefulShutdown.cs` | Shutdown coordination contract |

**Plan 26-04: Observability Contracts (OBS-01 through OBS-05)**

| File | Purpose |
|------|---------|
| `SDK/Contracts/Observability/ISdkActivitySource.cs` | ActivitySource wrapper for SDK tracing |
| `SDK/Contracts/Observability/ICorrelatedLogger.cs` | Structured logging with mandatory correlation |
| `SDK/Contracts/Observability/IResourceMeter.cs` | Per-plugin resource usage metering |
| `SDK/Contracts/Observability/IAuditTrail.cs` | Immutable append-only audit trail |
| (Modify) `SDK/Contracts/PluginBase.cs` | Add default IHealthCheck implementation |

**Plan 26-05: In-Memory Implementations & Security Primitives (DIST-10, DIST-11)**

| File | Purpose |
|------|---------|
| `SDK/Infrastructure/InMemory/InMemoryClusterMembership.cs` | Single-node cluster membership |
| `SDK/Infrastructure/InMemory/InMemoryLoadBalancerStrategy.cs` | Always-self load balancer |
| `SDK/Infrastructure/InMemory/InMemoryP2PNetwork.cs` | No-op P2P/gossip |
| `SDK/Infrastructure/InMemory/InMemoryAutoScaler.cs` | No-op scaling |
| `SDK/Infrastructure/InMemory/InMemoryReplicationSync.cs` | Local-only sync |
| `SDK/Infrastructure/InMemory/InMemoryAutoTier.cs` | Delegates to DefaultTierManager |
| `SDK/Infrastructure/InMemory/InMemoryAutoGovernance.cs` | No-op governance |
| `SDK/Infrastructure/InMemory/InMemoryFederatedMessageBus.cs` | Local-only message bus wrapper |
| `SDK/Infrastructure/InMemory/InMemoryCircuitBreaker.cs` | In-memory circuit breaker |
| `SDK/Infrastructure/InMemory/InMemoryBulkheadIsolation.cs` | In-memory bulkhead |
| `SDK/Infrastructure/InMemory/InMemoryDeadLetterQueue.cs` | In-memory DLQ |
| `SDK/Infrastructure/InMemory/InMemoryAuditTrail.cs` | In-memory audit trail |
| `SDK/Infrastructure/InMemory/InMemoryResourceMeter.cs` | In-memory resource metering |

## Recommended Plan Structure

### Plan 26-01: Distributed SDK Contracts
**Requirements:** DIST-01 through DIST-07
**Scope:** Define all 7 distributed interfaces with XML documentation
**Files:** 7 new files in `SDK/Contracts/Distributed/`
**Dependencies:** None (pure contract definitions)
**Risk:** Low -- interfaces only, no implementation logic
**Estimated effort:** Medium

### Plan 26-02: FederatedMessageBus & Multi-Phase Init
**Requirements:** DIST-08, DIST-09
**Scope:** FederatedMessageBus wrapping IMessageBus, add ActivateAsync to PluginBase
**Files:** 2-3 new files + 1 modification
**Dependencies:** Plan 26-01 (uses IClusterMembership for routing)
**Risk:** Medium -- modifying PluginBase requires careful backward compatibility
**Estimated effort:** Medium-Large

### Plan 26-03: Resilience Contracts
**Requirements:** RESIL-01 through RESIL-05
**Scope:** ICircuitBreaker, IBulkheadIsolation, IDeadLetterQueue, timeout policy, graceful shutdown
**Files:** 3-5 new files
**Dependencies:** None (pure contracts, builds on existing IResiliencePolicy)
**Risk:** Low -- contracts only
**Estimated effort:** Medium

### Plan 26-04: Observability Contracts
**Requirements:** OBS-01 through OBS-05
**Scope:** ActivitySource integration, correlated logging, IHealthCheck on PluginBase, resource metering, audit trail
**Files:** 4 new files + 1 modification (PluginBase)
**Dependencies:** None
**Risk:** Medium -- ActivitySource integration and PluginBase modification
**Estimated effort:** Medium

### Plan 26-05: In-Memory Implementations & Distributed Security
**Requirements:** DIST-10, DIST-11, plus CRYPTO-06 integration
**Scope:** In-memory single-node implementations for all contracts, security primitives integration
**Files:** ~13 new files
**Dependencies:** Plans 26-01 through 26-04 (implements the contracts)
**Risk:** Medium -- many files but each is simple (single-node no-op/local implementations)
**Estimated effort:** Large (volume of files)

## Architecture Patterns

### Contract-First Design Pattern

All Phase 26 contracts follow this pattern:
```csharp
// 1. Interface in SDK/Contracts/Distributed/
public interface IClusterMembership
{
    // Events for state changes
    event Action<ClusterMembershipEvent>? OnMembershipChanged;

    // Read state
    IReadOnlyList<ClusterNode> GetMembers();
    ClusterNode? GetLeader();

    // Mutating operations
    Task JoinAsync(ClusterJoinRequest request, CancellationToken ct = default);
    Task LeaveAsync(string reason, CancellationToken ct = default);
}

// 2. In-memory impl in SDK/Infrastructure/InMemory/
public sealed class InMemoryClusterMembership : IClusterMembership
{
    private readonly ClusterNode _self;
    // Single-node: always self, no cluster operations
    public IReadOnlyList<ClusterNode> GetMembers() => new[] { _self };
}
```

### FederatedMessageBus Architecture

```
FederatedMessageBus : IMessageBus
  |
  +-- IMessageBus _localBus        (existing in-process bus)
  +-- IClusterMembership _cluster   (for node discovery)
  +-- IConsistentHashRing _hashRing (for message routing)
  |
  +-- PublishAsync(topic, message)
       |-- Determine target: local or remote?
       |-- If local: _localBus.PublishAsync(topic, message)
       |-- If remote: serialize + route to remote node
       |-- If broadcast: both local + all remotes
```

### Multi-Phase Plugin Initialization

```
Phase 1: Construction (new Plugin())
  - Zero dependencies
  - No MessageBus, no services
  - Only set Id, Name, Version

Phase 2: InitializeAsync(ct)  [EXISTING]
  - MessageBus injected
  - Register capabilities and knowledge
  - Set up subscriptions
  - Local-only initialization

Phase 3: ActivateAsync(ct)    [NEW]
  - Distributed coordination available
  - Cluster membership resolved
  - Cross-node capabilities discovered
  - Plugin fully operational in cluster
  - Default: no-op (backward compatible)
```

### In-Memory Implementation Pattern

Every distributed contract gets an in-memory implementation that:
1. Works on a single node with zero configuration
2. Returns sensible defaults (self as only cluster member, etc.)
3. Implements all interface methods without throwing NotImplementedException
4. Is the default when no cluster is configured

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Circuit breaker state machine | Custom state tracking | Build on existing IResiliencePolicy | Already has Open/Closed/HalfOpen + stats |
| Health check aggregation | Custom aggregator | Build on existing HealthCheckAggregator | Already supports parallel, caching, tags |
| Distributed tracing | Custom span tracking | Bridge to System.Diagnostics.ActivitySource | .NET built-in, integrates with OTEL |
| Vector clocks | Custom implementation | Use existing VectorClock record | Already in SDK.Replication namespace |
| Consistent hashing | Custom hash ring | Build on existing LoadBalancingManager | Already has ConsistentHashing algorithm |
| Rate limiting | Custom token bucket | Use existing TokenBucketRateLimiter | Full implementation with queuing |
| Message authentication | Custom HMAC | Use existing IAuthenticatedMessageBus | HMAC-SHA256 + replay protection done |

## Common Pitfalls

### Pitfall 1: Breaking PluginBase Backward Compatibility
**What goes wrong:** Adding abstract methods to PluginBase breaks all 60+ plugins
**Why it happens:** Forgetting that PluginBase is inherited by every plugin
**How to avoid:** All new methods MUST be virtual with sensible defaults (no-op for ActivateAsync, Healthy for CheckHealthAsync). NEVER add abstract methods.
**Warning signs:** Build errors in plugin projects after PluginBase changes

### Pitfall 2: Duplicate Type Definitions
**What goes wrong:** Creating types that already exist (IHealthCheck, CircuitState, HealthStatus, etc.)
**Why it happens:** Not checking existing SDK types across all namespaces
**How to avoid:** Search SDK before creating any new type. Key duplicates to watch: HealthStatus exists in BOTH `Contracts.IKernelInfrastructure` and `Infrastructure.KernelInfrastructure`. IHealthCheck exists in both locations too.
**Warning signs:** CS0104 ambiguous reference errors

### Pitfall 3: Over-Engineering In-Memory Implementations
**What goes wrong:** Building complex distributed logic in what should be simple single-node stubs
**Why it happens:** Trying to make in-memory impls "realistic"
**How to avoid:** In-memory implementations should be TRIVIAL. Single-node only. No threading, no complex state machines, no simulated failures. Just enough to make the interface contract work on a laptop.
**Warning signs:** In-memory implementation exceeds 100 lines

### Pitfall 4: Ignoring Existing Namespace Conflicts
**What goes wrong:** Types collide across SDK namespaces
**Why it happens:** SDK has types in both `Contracts.*` and `Infrastructure.*` with same names
**How to avoid:** Use distinct namespaces for new types. Put contracts in `SDK.Contracts.Distributed`, `SDK.Contracts.Resilience`, `SDK.Contracts.Observability`. Put implementations in `SDK.Infrastructure.InMemory`.
**Warning signs:** Ambiguous type references during build

### Pitfall 5: Not Following SDK Patterns
**What goes wrong:** New contracts don't match existing SDK coding conventions
**Why it happens:** Not studying existing contract patterns
**How to avoid:** Follow established patterns: interfaces use `I` prefix, have XML docs on every member, use CancellationToken on all async methods, use `init` properties, records for immutable DTOs, factory methods (`.Ok()`, `.Error()`).
**Warning signs:** Code review finds inconsistent naming or patterns

## Risk Assessment

### Low Risk
- Defining interface contracts (Plans 26-01, 26-03, 26-04) -- pure additive, no breaking changes
- In-memory implementations for simple contracts -- straightforward

### Medium Risk
- **PluginBase modification** (Plans 26-02, 26-04) -- must be backward-compatible
  - Mitigation: All new methods virtual with no-op defaults
  - Verification: Build all 69 projects after every PluginBase change
- **FederatedMessageBus** (Plan 26-02) -- complex wrapper around IMessageBus
  - Mitigation: Contract-first, keep in-memory impl simple
  - Verification: Existing message bus tests still pass
- **Type conflicts** across SDK namespaces
  - Mitigation: Use `SDK.Contracts.Distributed` namespace, unique names

### Dependencies on Prior Phases
- Phase 24 (PluginBase lifecycle): **COMPLETE** -- InitializeAsync/ExecuteAsync/ShutdownAsync exist
- Phase 25 (Strategy hierarchy): **25a COMPLETE**, 25b pending -- Strategy hierarchy exists, migration ongoing
  - Phase 26 does NOT depend on 25b completion (contract-first, no strategy migration)
- Phase 23 (IAuthenticatedMessageBus): **COMPLETE** -- HMAC + replay protection contracts exist
- IDisposable/IAsyncDisposable on PluginBase: **COMPLETE** -- full dispose chain

### Pre-existing Build Issues
- CS1729/CS0234 errors in UltimateCompression and AedsCore -- pre-existing, not from our changes
- These should be noted in plan verification steps but NOT fixed in Phase 26

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| IFederationNode (v1.0) | New IClusterMembership (Phase 26) | Phase 26 | IFederationNode preserved, new contract for cluster-level ops |
| ITieredStorage (v1.0) | IAutoTier (Phase 26) + ITierManager (Phase 24) | Phase 26 | ITieredStorage preserved for backward compat |
| IReplicationService (v1.0) | IReplicationSync (Phase 26) + IMultiMasterReplication (v1.0) | Phase 26 | IReplicationService preserved, new contract adds online/offline sync |
| Custom tracing (IDistributedTracing) | System.Diagnostics.ActivitySource bridge | Phase 26 | Existing IDistributedTracing preserved, new bridge to standard .NET |
| IResiliencePolicy (v1.0) | ICircuitBreaker + IResiliencePolicy | Phase 26 | IResiliencePolicy preserved, focused ICircuitBreaker extracted |

## Open Questions

1. **ActivitySource vs IDistributedTracing**
   - What we know: SDK has custom IDistributedTracing with W3C-compatible TraceContext. .NET has built-in System.Diagnostics.ActivitySource.
   - What's unclear: Should we replace IDistributedTracing or bridge to ActivitySource?
   - Recommendation: Bridge -- create `ISdkActivitySource` that wraps System.Diagnostics.ActivitySource and can interop with existing IDistributedTracing. Do not replace.

2. **IHealthCheck Duplication**
   - What we know: IHealthCheck exists in TWO places: `Contracts.IKernelInfrastructure` (InfrastructurePluginBases.cs) and `Infrastructure.KernelInfrastructure` (KernelInfrastructure.cs)
   - What's unclear: Which is canonical? They have slightly different shapes.
   - Recommendation: Use the `Infrastructure.KernelInfrastructure` version (more complete, used by HealthCheckAggregator). Add IHealthCheck to PluginBase as virtual method returning Healthy.

3. **Relationship between LoadBalancingManager and ILoadBalancerStrategy**
   - What we know: LoadBalancingManager has full algorithm selection logic. DIST-02 requires ILoadBalancerStrategy.
   - What's unclear: Should ILoadBalancerStrategy wrap LoadBalancingManager or replace it?
   - Recommendation: ILoadBalancerStrategy is the contract (interface). LoadBalancingManager can implement it internally. They serve different purposes: contract vs implementation.

## Sources

### Primary (HIGH confidence)
- SDK source code: `DataWarehouse.SDK/Contracts/IMessageBus.cs` -- full IMessageBus, IAuthenticatedMessageBus, MessageBusBase
- SDK source code: `DataWarehouse.SDK/Contracts/PluginBase.cs` -- lifecycle methods, MessageBus injection
- SDK source code: `DataWarehouse.SDK/Infrastructure/KernelInfrastructure.cs` -- IHealthCheck, HealthCheckAggregator, IMemoryPressureMonitor, IRateLimiter
- SDK source code: `DataWarehouse.SDK/Contracts/InfrastructurePluginBases.cs` -- IResiliencePolicy, IDistributedTracing, IMetricsCollector
- SDK source code: `DataWarehouse.SDK/Replication/IMultiMasterReplication.cs` -- VectorClock, consistency levels
- SDK source code: `DataWarehouse.SDK/Configuration/LoadBalancingConfig.cs` -- load balancing algorithms and config
- SDK source code: `DataWarehouse.SDK/Configuration/FaultToleranceConfig.cs` -- fault tolerance modes and config
- SDK source code: `DataWarehouse.SDK/Contracts/IFederationNode.cs` -- existing federation primitives
- SDK source code: `DataWarehouse.SDK/Contracts/IConsensusEngine.cs` -- Raft/consensus types
- `.planning/ROADMAP.md` lines 251-269 -- Phase 26 definition
- `.planning/REQUIREMENTS.md` -- DIST-01 through DIST-11, RESIL-01 through RESIL-05, OBS-01 through OBS-05

### Secondary (MEDIUM confidence)
- `.planning/STATE.md` -- Phase 25a completion status, accumulated decisions

## Metadata

**Confidence breakdown:**
- Current state assessment: HIGH -- direct source code inspection of all relevant SDK files
- Gap analysis: HIGH -- requirements compared line-by-line against existing code
- Architecture patterns: HIGH -- consistent with existing SDK patterns observed in 218+ .cs files
- Risk assessment: HIGH -- based on actual Phase 24/25a experience with PluginBase/StrategyBase modifications
- Plan structure: HIGH -- follows ROADMAP-defined 5-plan structure

**Research date:** 2026-02-14
**Valid until:** 2026-03-14 (stable -- contracts-only phase, no external dependency changes)
