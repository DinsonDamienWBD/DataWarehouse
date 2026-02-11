# Pitfalls Research: DataWarehouse v2.0 SDK Hardening

**Domain:** C#/.NET 10 Enterprise SDK (1.1M LOC) - Distributed Infrastructure & Security Hardening
**Researched:** 2026-02-11
**Confidence:** HIGH

## Critical Pitfalls

### Pitfall 1: Retroactive IDisposable Breaking 60 Existing Plugins

**What goes wrong:**
Adding IDisposable to PluginBase (3,777 lines) retroactively breaks every one of 60 existing plugins that don't properly implement the Dispose pattern. The break is silent at compile-time but catastrophic at runtime when disposal chains are invoked. Plugins that hold unmanaged resources won't clean up correctly, and plugins that override virtual methods incorrectly will break the disposal chain.

**Why it happens:**
Adding IDisposable to an existing base class is a breaking change according to Microsoft's guidelines. Pre-existing consumers cannot call Dispose, and you cannot be certain that unmanaged resources held by derived types will be released. Developers assume base class changes are "just infrastructure" and don't realize every derived class needs attention.

**How to avoid:**
- **Phase 1 - Base Class Addition:** Add IDisposable to PluginBase with a sealed `Dispose()` method and a protected virtual `Dispose(bool disposing)` method following the standard dispose pattern
- **Phase 2 - Audit Pass:** Use Roslyn analyzers (CA1063, CA2215) to identify every plugin that needs disposal logic
- **Phase 3 - Incremental Migration:** Create an `IDisposablePlugin` marker interface first, migrate plugins in batches of 10-15, verify each batch before continuing
- **Phase 4 - Enforcement:** Enable CA1063 as an error only after all plugins are migrated

**Warning signs:**
- Unit tests pass but integration tests show resource leaks
- Plugins work in isolation but fail when composed
- Finalization queue grows unexpectedly
- Memory profiler shows objects retained after plugin unload
- Event subscription leaks (check _knowledgeSubscriptions, _intelligenceSubscriptions in base classes)

**Phase to address:**
Phase 1 (Foundation Hardening) - but needs a dedicated 3-week sub-phase just for IDisposable migration with gating criteria before proceeding.

---

### Pitfall 2: Circular Dependencies in Message Bus + New Distributed Contracts

**What goes wrong:**
Adding distributed infrastructure (P2P, auto-sync) to an existing message bus creates circular dependencies between PluginBase, MessageBus, and the new distributed coordination layer. The classic trap: PluginBase needs MessageBus for communication, MessageBus needs distributed coordinator for P2P routing, distributed coordinator needs to discover plugins through PluginBase. The application won't start, or worse, starts but deadlocks during plugin initialization.

**Why it happens:**
The message bus currently lives as a protected property in PluginBase. Adding P2P and auto-sync means the message bus needs to coordinate with a distributed layer, which needs to know about plugins. This creates a dependency cycle. The temptation is to "just inject one more dependency" without redesigning the initialization flow.

**How to avoid:**
- **Dependency Inversion:** Create `IDistributedCoordinator` abstraction that doesn't know about plugins
- **Multi-Phase Initialization:** Separate plugin construction from initialization from activation (3-phase startup)
  1. Construction: Zero dependencies
  2. Initialization (`InitializeAsync`): Inject MessageBus and basic services
  3. Activation (`StartAsync`): Distributed coordination kicks in
- **Reverse Discovery:** Have the distributed coordinator pull plugin metadata from MessageBus instead of pushing to plugins
- **Message Bus as Facade:** MessageBus should only depend on abstractions, never concrete distributed implementations

**Warning signs:**
- Initialization timeout exceptions during `StartAsync`
- Deadlocks when 3+ plugins start simultaneously
- Plugins work when started individually but fail when bulk-loaded
- Distributed features work in tests but not in production (because test mocks avoid the cycle)
- Stack overflow during recursive initialization calls

**Phase to address:**
Phase 2 (Distributed Foundation) - requires architecture review BEFORE implementation. This is a blocker-level risk.

---

### Pitfall 3: Unifying 7 Strategy Bases Without Breaking 25+ Feature-Specific Plugins

**What goes wrong:**
Creating a single `StrategyBase` to replace 7 fragmented strategy bases (ConnectionStrategyBase, InterfaceStrategyBase, MediaStrategyBase, ObservabilityStrategyBase, + 3 others) breaks 25+ plugins that depend on feature-specific methods and properties. The compile errors are obvious, but the semantic breaks are worse: plugins that compile but have subtly different behavior because the unified base class changed method signatures, execution order, or virtual method dispatch.

**Why it happens:**
The temptation to "fix" fragmentation by bottom-up refactoring. You look at 7 bases, see duplication, extract commonality, then realize each base has domain-specific quirks that consumers rely on. Pattern over-unification is a known pitfall: implementing patterns "to the letter" without adapting them to the context of the project, which haunts novices who have just familiarized themselves with patterns.

**How to avoid:**
- **Top-Down Design:** Define the ideal `IStrategy` contract FIRST, before touching existing code
- **Adapter Layer:** Keep existing strategy bases as thin adapters over the new unified base (temporary, 1-2 releases)
- **Incremental Migration Matrix:**
  ```
  Phase 1: Add new StrategyBase (parallel to old bases)
  Phase 2: Migrate ConnectionStrategy (smallest, 3 plugins)
  Phase 3: Migrate InterfaceStrategy (medium, 5 plugins)
  Phase 4-7: One domain at a time
  Phase 8: Deprecate old bases, remove in v3.0
  ```
- **Behavioral Verification:** Automated tests comparing old vs new strategy behavior for each plugin
- **Feature Flags:** New strategy base behind feature flag for progressive rollout

**Warning signs:**
- Compile breaks in more than 10 plugins simultaneously (too aggressive)
- Tests pass but production behavior changes (semantic break)
- Strategy selection logic breaks (wrong strategy activated)
- Performance regression (unified base is slower due to generalization overhead)
- Plugin authors report "the new version doesn't work like the old one"

**Phase to address:**
Phase 3 (Strategy Unification) - needs a 4-6 week execution window with checkpoint gates every 2 weeks.

---

### Pitfall 4: TreatWarningsAsErrors on 1.1M LOC Without Incremental Enforcement

**What goes wrong:**
Enabling `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` globally on a 1.1M LOC codebase instantly breaks the build with 500+ errors. The current state (16 NuGet-only warnings) explodes to hundreds when applied to all code. Development halts. The team spends weeks fixing warnings instead of building features. Morale collapses.

**Why it happens:**
The assumption that "we only have 16 warnings" because those are the only ones currently visible. .NET projects have layered warning configuration - some warnings are suppressed at project/directory level, some only appear with specific analyzers enabled, some are hidden by SDK defaults. The true warning count is unknown until enforcement is attempted.

**How to avoid:**
- **Phase 1 - Discovery:** Run full build with `/warnaserror+` on CI (non-blocking) to get real warning count
- **Phase 2 - Categorization:** Group warnings by type (nullability, obsolete APIs, CA rules, IDE suggestions)
- **Phase 3 - Incremental Enforcement:** Enable by category with project-level `<NoWarn>` suppressions
  ```xml
  <!-- Phase 1: Enable only null-related warnings -->
  <WarningsAsErrors>CS8600,CS8601,CS8602,CS8603,CS8604</WarningsAsErrors>

  <!-- Phase 2: Add obsolete API warnings -->
  <WarningsAsErrors>CS0618,CS8600-CS8604</WarningsAsErrors>

  <!-- Phase 3: Add CA rules incrementally -->
  ```
- **Phase 4 - Bottom-Up Migration:** Start with leaf projects (plugins), then SDK layers, finally core
- **Parallel Fixing:** Assign warning categories to different developers (nullability squad, API squad, etc.)
- **Warning Budget:** Set max 50 warnings per phase, gate next phase on hitting 0

**Warning signs:**
- Build time increases 3x+ after enabling warnings-as-errors
- PRs blocked for weeks on "warning fixes"
- Developers disable warnings locally to keep working (technical debt explosion)
- Warning fixes introduce behavioral changes (null checks that break existing logic)
- Team velocity drops by 50%+ due to warning cleanup

**Phase to address:**
Phase 4 (Build Hardening) - but needs a parallel track starting in Phase 1 with progressive enforcement. Estimate 8-12 weeks of incremental work.

---

### Pitfall 5: Distributed State Management Violating Message Bus Isolation

**What goes wrong:**
Adding P2P auto-sync and distributed coordination introduces shared state (distributed locks, consensus, cluster membership) that bypasses the message bus abstraction. Plugins start directly accessing distributed state instead of communicating through messages. The clean microkernel architecture degrades into a distributed monolith where plugins have hidden dependencies on cluster state. Failures cascade - one node's state corruption affects all nodes.

**Why it happens:**
Distributed systems introduce the temptation to "just share this one piece of state" for performance or simplicity. A plugin needs to know cluster membership, so it accesses the distributed coordinator directly instead of subscribing to membership change messages. This violates data isolation principles: a common mistake is to share a common source of truth among several microservices, which violates both loose coupling and high cohesion.

**How to avoid:**
- **Message Bus as Single API:** All plugin-to-plugin and plugin-to-system communication MUST go through message bus
- **Distributed Services as Plugins:** Implement P2P coordinator, auto-sync manager, consensus engine as first-class plugins
  - They send messages, they don't expose state
  - Other plugins subscribe to messages, never call coordinator APIs directly
- **State Encapsulation:** Each plugin owns its state, distributed layer only handles message routing and sync
- **Contract-First IPC:** Protocol-first IPC with schemas checked in CI, generated stubs, version negotiation (2026 best practice for microkernels)
- **Clear Boundaries:**
  ```
  ✓ Plugin → MessageBus → DistributedPlugin → Network
  ✗ Plugin → DistributedCoordinator → Network (bypass)
  ```

**Warning signs:**
- Plugins that "sometimes work" depending on cluster state
- Test failures that only occur when multiple nodes are running
- Race conditions between local message handlers and distributed state updates
- Plugins that work on single-node but fail in cluster mode
- Circular dependencies between plugins and distributed services
- Performance degradation as cluster size increases (N² communication patterns)

**Phase to address:**
Phase 2 (Distributed Foundation) - architectural constraint, must be enforced through code reviews and static analysis.

---

### Pitfall 6: Breaking Changes Hidden Behind Semantic Versioning

**What goes wrong:**
Refactoring PluginBase from 3,777 lines to a cleaner hierarchy changes virtual method signatures, execution order, and base class behavior. The SDK version bumps from 1.0 to 2.0, signaling breaking changes, but the breaks are subtle. A plugin compiled against SDK 1.0 loads into SDK 2.0 (binary compatibility), but has different runtime behavior (semantic compatibility broken). Virtual method dispatch resolves differently. Initialization order changes. Plugins fail in production but work in development.

**Why it happens:**
.NET's strong-named assembly loading and semantic versioning don't protect against semantic breaks, only binary breaks. You can change the implementation of a virtual method without changing its signature - this is binary compatible but semantically breaking. In 2026, MAJOR versions indicate breaking changes or deprecations that may require code changes, but tooling doesn't enforce semantic compatibility - only developers do.

**How to avoid:**
- **Binary + Semantic Versioning:** Version not just the SDK but the contract surface separately
  ```csharp
  [assembly: PluginApiVersion("2.0.0")]  // Contract version
  [assembly: AssemblyVersion("2.0.0")]   // Binary version
  ```
- **Runtime Contract Validation:** Plugins declare minimum/maximum SDK version they're compatible with
  ```csharp
  [SdkCompatibility(MinVersion = "2.0.0", MaxVersion = "2.999.0")]
  public class MyPlugin : PluginBase { }
  ```
- **Behavioral Test Suite:** For each SDK release, run ALL existing plugin tests (60 plugins × avg 17 tests = 1,000+ test runs)
- **Progressive Rollout with Canary Plugins:** Test SDK 2.0 with 5 representative plugins before releasing to all 60
- **Clear Migration Path:** Azure's commitment to backward compatibility: "Customers can adopt a new API version without requiring code changes"
  - If this isn't possible, document exactly what breaks and why

**Warning signs:**
- Plugins report "random" failures that can't be reproduced in tests
- Behavior differs between plugin versions compiled against different SDK versions
- Virtual method overrides in plugins stop being called (base class changed dispatching)
- Initialization lifecycle hooks fire in different order
- Plugin authors say "it used to work" with no code changes

**Phase to address:**
Continuous across ALL phases - this is a quality gate, not a phase. Requires tooling investment in Phase 1.

---

### Pitfall 7: Hyperscale Security Review Showing Missing Distributed Security Primitives

**What goes wrong:**
The codebase passes initial security review, but when submitted for military/hyperscale review, auditors flag missing distributed security primitives: no distributed authentication, no message signing, no replay protection, no secure cluster membership. The message bus uses plain pub/sub with no verification of sender identity. A compromised plugin can impersonate any other plugin. The entire distributed system is built on trusted-network assumptions that don't hold at hyperscale.

**Why it happens:**
Distributed security is added as an afterthought after basic distributed functionality works. The message bus was designed for single-process or trusted-network scenarios. Adding P2P and auto-sync extends the trust boundary across the network without adding security controls. The 2026 security hardening checklist requires identity controls for admins and critical systems - but this SDK doesn't have per-plugin identity at all.

**How to avoid:**
- **Security First Architecture:** Distributed security must be in Phase 2, not Phase 5
- **Message Authentication:** Every message includes cryptographic signature of sender
  ```csharp
  interface IAuthenticatedMessage {
      string SenderId { get; }
      byte[] Signature { get; }  // HMAC-SHA256 of message body
      DateTimeOffset Timestamp { get; }
  }
  ```
- **Plugin Identity:** Each plugin has a unique identity (public/private key pair) registered at startup
- **Trust Boundaries:** Clear trust zones with explicit boundary enforcement
  - Zone 1: Same process (trusted)
  - Zone 2: Same machine (semi-trusted, needs auth)
  - Zone 3: Network (untrusted, needs auth + encryption)
- **Replay Protection:** Message bus rejects messages older than 5 minutes or with duplicate message IDs
- **Capability-Based Security:** Plugins declare capabilities they need, kernel enforces at message dispatch
  ```csharp
  [RequiredCapability("system.management.restart")]
  public async Task RestartAsync() { }
  ```
- **2026 Best Practices:** Memory-safe languages for security-critical components (consider C# 10's improved memory safety), protocol-first IPC with schemas, fuzzing for message parsers

**Warning signs:**
- Security review requests "threat model for distributed plugins" and you can't provide one
- No documentation on plugin trust boundaries
- Message bus has no concept of sender authentication
- Plugins can send messages "from" any other plugin ID
- No audit logging of inter-plugin communication
- Cluster membership changes have no authentication

**Phase to address:**
Phase 2 (Distributed Foundation) - must be architectural, not bolted on later. Security review should happen at end of Phase 2, NOT at end of Phase 5.

---

### Pitfall 8: IntelligenceAwarePluginBase Coupling Blocking Independent Plugin Development

**What goes wrong:**
The 1,530-line IntelligenceAwarePluginBase tightly couples Universal Intelligence (T90) discovery to the base class startup lifecycle. Every plugin that inherits from it must wait for Intelligence discovery during `StartAsync`, even if the plugin doesn't use AI features. When T90 is unavailable or slow, ALL Intelligence-aware plugins are blocked. The coupling makes it impossible to develop plugins independently - you need a working T90 instance or complex mocks in every test.

**Why it happens:**
The base class tries to be helpful by doing Intelligence discovery automatically. The abstraction leaks - the base class makes assumptions about when and how Intelligence should be discovered. This violates the microkernel principle: the core should provide mechanisms, not policies.

**How to avoid:**
- **Lazy Discovery:** Intelligence discovery should be on-demand, not automatic during startup
  ```csharp
  // BAD: Automatic in StartAsync
  public override async Task StartAsync(CancellationToken ct)
  {
      await DiscoverIntelligenceAsync(ct);  // Blocks startup
  }

  // GOOD: Lazy when first needed
  protected async Task<bool> EnsureIntelligenceAsync(CancellationToken ct)
  {
      if (!_discoveryAttempted) await DiscoverIntelligenceAsync(ct);
      return IsIntelligenceAvailable;
  }
  ```
- **Optional Intelligence:** Make IntelligenceAwarePluginBase an interface, not a base class
  ```csharp
  public interface IIntelligenceAware { }  // Marker interface

  public class IntelligenceHelper {  // Helper, not base class
      public static Task<bool> DiscoverAsync(...) { }
  }
  ```
- **Composition Over Inheritance:** Plugins that need Intelligence use an injected service, not base class magic
- **Graceful Degradation:** Plugins work WITHOUT Intelligence, enhanced WITH Intelligence
- **Test Isolation:** Every plugin's test suite runs without T90 dependency (use nullability or test doubles)

**Warning signs:**
- Every plugin test requires T90 mock setup
- Startup time increases linearly with Intelligence-aware plugin count
- Plugins fail to start when T90 is slow or unavailable
- Developers avoid inheriting from IntelligenceAwarePluginBase because it's "too complicated"
- Plugin startup times are non-deterministic (depends on T90 response time)

**Phase to address:**
Phase 3 (Strategy Unification) - can be done in parallel with strategy base refactoring, similar concerns.

---

## Technical Debt Patterns

| Shortcut | Immediate Benefit | Long-term Cost | When Acceptable |
|----------|-------------------|----------------|-----------------|
| Skip IDisposable on new plugins | Faster plugin development, no disposal ceremony | Resource leaks, failures under load, memory profiler shows retained objects | Never - IDisposable is foundational once added to base |
| Use `#pragma warning disable` instead of fixing warnings | Unblocks development immediately | Hides real issues, builds warning blindness, fails hyperscale review | Only for false positives after reviewer confirms |
| Direct distributed state access instead of message bus | Performance boost (skip message overhead), simpler code | Violates architecture, creates hidden dependencies, breaks in cluster mode | Never in plugins; acceptable in distributed layer internals only |
| Shared strategy base for "just 2 similar strategies" | Reduces duplication by 50 lines | Creates coupling, makes both strategies harder to evolve, breaks SRP | Only if strategies are genuinely identical in lifecycle AND data model |
| Delay security hardening until "after it works" | Faster initial implementation, see results sooner | Security review fails, expensive retrofit, potential rewrite of distributed layer | Never for distributed systems - security is foundational |
| Keep old strategy bases "for compatibility" | Existing plugins don't break | Fragmentation continues, maintenance burden doubles, new developers confused about which base to use | Acceptable for 1-2 releases with aggressive deprecation schedule |
| Mock Intelligence in plugin tests with simple stubs | Tests run fast, no T90 dependency | Tests don't catch Intelligence integration bugs, false confidence | Acceptable for unit tests; integration tests MUST use real T90 |

## Integration Gotchas

| Integration | Common Mistake | Correct Approach |
|-------------|----------------|------------------|
| Message Bus + Distributed Coordinator | Distributed coordinator subscribes to plugin messages during initialization, creating circular dependency | Coordinator registers after all plugins initialize; use multi-phase startup with explicit ordering |
| Plugin Base + IDisposable | Adding Dispose() method without virtual Dispose(bool disposing) pattern | Follow CA1063 exactly: sealed Dispose(), protected virtual Dispose(bool), suppress finalization |
| Strategy Base + Plugin Feature Bases | Creating deep inheritance chain: PluginBase → FeaturePluginBase → StrategyBase → ConnectionStrategyBase (4 levels) | Max 2 levels: PluginBase → Plugin OR PluginBase → FeaturePluginBase → Plugin, strategies use composition |
| Intelligence Discovery + Plugin Startup | Blocking StartAsync on Intelligence availability with DiscoveryTimeout = 500ms | Discover asynchronously in background after StartAsync completes, use events to signal availability |
| Distributed Locks + Message Bus | Using distributed locks around message publishing to ensure ordering | Message bus handles ordering, distributed locks only for cross-node resource coordination |
| P2P Sync + Plugin State | Syncing entire plugin state object on every change | Sync granular change events through message bus, reconstruct state from event log |
| Security Review + Breaking Changes | Treating security hardening as "additive only" to avoid breaking changes | Accept that security primitives ARE breaking changes, version SDK correctly (1.x → 2.x) |

## Performance Traps

| Trap | Symptoms | Prevention | When It Breaks |
|------|----------|------------|----------------|
| Intelligence discovery on every StartAsync | Startup time increases linearly with Intelligence-aware plugin count; cluster startup takes minutes | Cache discovery results at kernel level, inject into plugins | 10+ Intelligence-aware plugins, or when T90 latency > 100ms |
| Synchronous message bus dispatch | Single slow plugin blocks all message processing; cascading timeouts | Async dispatch with per-plugin task queues, circuit breaker per plugin | Any plugin handler > 50ms, or under load |
| Deep base class hierarchy virtual dispatch | Method calls 10-20% slower than direct calls; call stack traces are 6+ levels deep | Composition over inheritance, max 2-level hierarchy | Hot paths with 100k+ calls/sec |
| Strategy lookup via reflection on every invocation | CPU spikes during strategy-heavy operations | Cache strategy instances by key, use concurrent dictionary | Strategies invoked > 1000/sec per node |
| Unmanaged resource retention without IDisposable | Memory grows unbounded, GC pauses increase, OutOfMemoryException after hours/days | Proper IDisposable pattern on all base classes, enforce with analyzers | Cluster running > 4 hours, or high plugin churn |
| Distributed state synchronization on every property change | Network traffic explodes (N² problem), cluster becomes unresponsive | Batch state changes, sync every 100ms or 1000 changes (whichever first) | 5+ nodes with high-frequency state changes (> 100/sec) |
| Message bus without backpressure | Publisher floods subscribers, memory exhaustion, message loss | Bounded channels with overflow policy (drop or block), monitor queue depths | Message rate > 10k/sec system-wide |

## Security Mistakes

| Mistake | Risk | Prevention |
|---------|------|------------|
| No plugin identity verification | Malicious plugin impersonates critical plugin, executes unauthorized operations | Cryptographic plugin identity at load time, sign plugin assemblies, verify signatures before load |
| Message bus without sender authentication | Any plugin can send messages "from" any other plugin | HMAC signing of messages with per-plugin keys, verify signature before dispatch |
| Distributed state without encryption | Network eavesdropping reveals sensitive plugin state, configuration secrets | TLS 1.3 for all inter-node communication, encrypt state at rest with per-node keys |
| No capability-based security | Plugin escalates privileges by calling APIs it shouldn't have access to | Capability registry with kernel enforcement, plugins declare required capabilities at startup |
| Trusting plugin assembly metadata (Name, Version, Id) | Plugin lies about identity to bypass security checks | Verify cryptographic identity (strong name + signature), not metadata strings |
| No audit logging of privileged operations | Security incident occurs, no forensics trail | Structured audit log of all inter-plugin messages, state changes, capability grants |
| Shared message bus for trusted and untrusted plugins | Compromised plugin monitors all traffic, exfiltrates data | Trust zones with separate message buses, explicit bridge for cross-zone communication |

## UX Pitfalls

| Pitfall | User Impact | Better Approach |
|---------|-------------|-----------------|
| Plugin startup failures silent (logged but not surfaced) | Application appears to work but features randomly missing | Surface plugin health in system status API, fail-fast if critical plugins don't start |
| Breaking changes without migration guide | Plugin authors upgrade SDK, everything breaks, no guidance | Auto-generated migration guide from source diffs, annotate breaking changes with [Obsolete("Use X instead")] |
| Strategy selection via string matching ("compression.gzip") | Typos cause silent fallbacks to default strategy, confusing behavior | Type-safe strategy keys (enum or const string class), compile-time verification |
| Intelligence availability hidden from plugin developers | Plugins have fallback behavior but devs don't know if Intelligence is working | System health dashboard showing T90 status, capability flags, plugin discovery state |
| TreatWarningsAsErrors enabled without warning | Developer opens solution, 500 errors, doesn't know why | Phased rollout with CHANGELOG entry, migrate warnings incrementally, communicate warning budget |
| Distributed cluster status opaque | Application runs on 5 nodes but user doesn't know which plugins are on which nodes | Cluster dashboard showing node health, plugin distribution, message routing topology |
| No plugin isolation in error messages | Exception: "Object reference not set to an instance of an object" (which plugin?) | Structured logging with plugin ID context, exception wrappers that include plugin source |

## "Looks Done But Isn't" Checklist

- [ ] **IDisposable Implementation:** Often missing Dispose(bool) virtual pattern — verify CA1063 passes on ALL plugins, check event subscription cleanup
- [ ] **Message Bus Security:** Often missing sender authentication — verify every message has cryptographic signature, test with malicious plugin
- [ ] **Strategy Unification:** Often missing behavioral equivalence tests — verify old and new strategy bases produce identical results for same inputs
- [ ] **Distributed Coordination:** Often missing failure mode handling — test with network partitions, node crashes, message loss
- [ ] **Plugin Versioning:** Often missing runtime compatibility checks — verify plugins declare min/max SDK version, kernel rejects incompatible plugins
- [ ] **Intelligence Discovery:** Often missing graceful degradation — verify plugins work when T90 unavailable, not just when it's available
- [ ] **Warning Enforcement:** Often missing incremental rollout — verify TreatWarningsAsErrors applied by category with gating criteria, not globally
- [ ] **Security Primitives:** Often missing replay protection — verify message bus rejects duplicate/old messages, test with message replay attacks
- [ ] **Base Class Refactoring:** Often missing semantic versioning — verify binary AND semantic compatibility with behavior test suite
- [ ] **Distributed State:** Often missing data isolation — verify plugins can't access each other's state directly, only through message bus
- [ ] **Plugin Identity:** Often missing cryptographic verification — verify plugin signatures checked at load time, not just assembly metadata
- [ ] **Capability Security:** Often missing runtime enforcement — verify plugins can't call APIs without declared capabilities

## Recovery Strategies

| Pitfall | Recovery Cost | Recovery Steps |
|---------|---------------|----------------|
| IDisposable breaking plugins | HIGH (3-6 weeks) | 1. Revert IDisposable from PluginBase 2. Create IDisposablePlugin marker interface 3. Migrate plugins incrementally 4. Re-add to base class when 100% migrated |
| Circular dependencies in message bus | MEDIUM (2-3 weeks) | 1. Add IDistributedCoordinator abstraction 2. Extract distributed logic to separate plugin 3. Implement multi-phase initialization 4. Update MessageBus to use abstraction |
| Strategy unification breaks plugins | MEDIUM (2-4 weeks) | 1. Keep old strategy bases as adapters 2. Migrate plugins one at a time 3. Run parallel test suites (old vs new) 4. Remove adapters in next major version |
| TreatWarningsAsErrors breaks build | LOW (1 week) | 1. Revert to warnings-as-warnings 2. Enable by category incrementally 3. Use NoWarn suppressions for false positives 4. Set warning budget and enforce in CI |
| Missing distributed security primitives | VERY HIGH (8-12 weeks) | 1. Design security architecture (threat model, boundaries) 2. Implement plugin identity and message signing 3. Add capability-based security 4. Security review before release (non-negotiable) |
| IntelligenceAwarePluginBase coupling | MEDIUM (2-3 weeks) | 1. Extract Intelligence discovery to helper service 2. Change base class to use helper lazily 3. Make StartAsync non-blocking 4. Update plugin tests to mock helper |
| Breaking changes without versioning | LOW (1 week) | 1. Add SdkCompatibility attribute to plugin contract 2. Kernel validates compatibility at load 3. Document breaking changes in CHANGELOG 4. Provide migration guide |

## Pitfall-to-Phase Mapping

| Pitfall | Prevention Phase | Verification |
|---------|------------------|--------------|
| Retroactive IDisposable | Phase 1 (Foundation Hardening) | CA1063/CA2215 analyzers pass on all plugins, resource leak tests pass |
| Circular Dependencies in Message Bus | Phase 2 (Distributed Foundation) | Dependency graph analysis shows no cycles, integration tests pass with all plugins |
| Strategy Unification Breaking Plugins | Phase 3 (Strategy Unification) | Behavioral equivalence tests pass for all strategies, zero semantic breaks |
| TreatWarningsAsErrors | Phase 4 (Build Hardening) | Build passes with zero warnings in all configurations, CI enforces warning budget |
| Distributed State Management | Phase 2 (Distributed Foundation) | Static analysis shows no direct state access, message bus is only communication path |
| Breaking Changes Without Versioning | Continuous (All Phases) | SdkCompatibility tests pass, plugin test suite runs against new SDK version |
| Missing Distributed Security | Phase 2 (Distributed Foundation) | Security review sign-off, penetration testing passes, audit logging covers all operations |
| IntelligenceAwarePluginBase Coupling | Phase 3 (Strategy Unification) | Plugin tests run without T90 dependency, startup time independent of Intelligence availability |

## Sources

- [Refactoring: This class is too large - Martin Fowler](https://martinfowler.com/articles/class-too-large.html)
- [How do you refactor your code to optimize inheritance hierarchies? - LinkedIn](https://www.linkedin.com/advice/1/how-do-you-refactor-your-code-optimize-inheritance)
- [CA1063: Implement IDisposable correctly - Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/quality-rules/ca1063)
- [Implement a Dispose method - .NET - Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/implementing-dispose)
- [Pitfalls of Disposing Blindly When Using IDisposable Resources - Hamid Mosalla](https://hamidmosalla.com/2025/02/20/pitfalls-of-disposing-blindly-when-using-idisposable-resources/)
- [CA2215: Dispose methods should call base class dispose - Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/quality-rules/ca2215)
- [MicroKernel Architecture: The Power of Pluggability - BuildSimple](https://buildsimple.substack.com/p/microkernel-architecture-the-power)
- [Microkernels in Operating Systems: A Practical, Modern Guide (2026) - TheLinuxCode](https://thelinuxcode.com/microkernels-in-operating-systems-a-practical-modern-guide-2026/)
- [Distributed System: Microkernel Architecture Glimpse - Medium](https://medium.com/@bindubc/distributed-system-microkernel-architecture-glimpse-e72abbeaedcf)
- [Events and the Message Bus - Cosmic Python](https://www.cosmicpython.com/book/chapter_08_events_and_message_bus.html)
- [.NET Framework to .NET Core Migration Challenges - Robin Waite](https://www.robinwaite.com/blog/net-framework-to-net-core-migration-challenges)
- [Tales from the .NET Migration Trenches - Jimmy Bogard](https://www.jimmybogard.com/tales-from-the-net-migration-trenches/)
- [How the .NET Runtime and SDK are versioned - Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/versions/)
- [Azure's approach to versioning and avoiding breaking changes - Azure SDK Blog](https://devblogs.microsoft.com/azure-sdk/azure-approach-to-versioning-and-avoiding-breaking-changes/)
- [API Versioning: Strategies & Best Practices - xMatters](https://www.xmatters.com/blog/api-versioning-strategies)
- [Strategy Pattern - Refactoring Guru](https://refactoring.guru/design-patterns/strategy)
- [Criticism of patterns - Refactoring Guru](https://refactoring.guru/design-patterns/criticism)
- [Latest Cybersecurity Best Practices 2026: A Practical Checklist - NMS Consulting](https://nmsconsulting.com/latest-cybersecurity-best-practices-2026/)
- [Cloud Security Assessment Checklist for 2026 - SentinelOne](https://www.sentinelone.com/cybersecurity-101/cloud-security/cloud-security-assessment-checklist/)
- [Scalability Patterns for Modern Distributed Systems - ByteByteGo](https://blog.bytebytego.com/p/scalability-patterns-for-modern-distributed)
- [A Guide to Large-Scale Distributed Systems (2026) - System Design Handbook](https://www.systemdesignhandbook.com/blog/large-scale-distributed-systems/)
- [Design Pattern Proposal for Autoscaling Stateful Systems - InfoQ](https://www.infoq.com/articles/design-proposal-autoscaling-stateful-systems/)

---
*Pitfalls research for: DataWarehouse v2.0 SDK - Distributed Infrastructure & Security Hardening*
*Researched: 2026-02-11*
*Confidence: HIGH (based on official Microsoft documentation, 2026 best practices, and codebase analysis)*
