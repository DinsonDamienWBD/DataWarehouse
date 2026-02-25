# Feature Research

**Domain:** Hyperscale-grade C#/.NET 10 SDK Framework
**Researched:** 2026-02-11
**Confidence:** HIGH

## Feature Landscape

### Table Stakes (Must Have for Enterprise Code Review)

Features that Google/Microsoft/Amazon/military code review expects. Missing these = review fails.

| Feature | Why Expected | Complexity | Notes |
|---------|--------------|------------|-------|
| **IDisposable on all resource-holding types** | Unmanaged resources must be cleaned up deterministically | LOW | Already implemented. Verify protected virtual Dispose(bool) pattern, GC.SuppressFinalize(this), idempotent |
| **CancellationToken on all async methods** | Enables cooperative cancellation in distributed systems | LOW | Already implemented. Verify propagation through call chains |
| **Nullable reference types enabled** | Prevents null reference exceptions at compile time | LOW | Already implemented. Verify no #nullable disable directives |
| **TreatWarningsAsErrors=true** | Enforces zero-tolerance for compiler warnings | LOW | Requires Roslyn analyzer configuration |
| **Roslyn analyzers (Microsoft.CodeAnalysis.NetAnalyzers)** | Static analysis for security, performance, maintainability | MEDIUM | Microsoft.CodeAnalysis.NetAnalyzers 10.0+ required for .NET 10 |
| **Input validation at all boundaries** | SQL injection, XSS, command injection prevention | MEDIUM | All plugin entry points, strategy inputs, registry queries |
| **ReDoS protection on regex patterns** | Prevents regex denial-of-service attacks | MEDIUM | Regex constructor with timeout since .NET 4.5; verify all Regex instances |
| **Constant-time cryptographic comparisons** | Prevents timing attacks on authentication/authorization | MEDIUM | Use CryptographicOperations.FixedTimeEquals for secrets/tokens |
| **Immutable DTOs for API contracts** | Prevents accidental mutation, safe threading | LOW | Use C# records with init-only properties |
| **Strong typing everywhere** | No object, dynamic, or weak typing in public APIs | LOW | Verify all public methods, strategy contracts |
| **ActivitySource for distributed tracing** | Required for distributed debugging in hyperscale | MEDIUM | One ActivitySource per major component (plugins, strategies, kernel) |
| **Health checks endpoints** | Kubernetes liveness/readiness probes | MEDIUM | ASP.NET Core health checks pattern for SDK-level health |
| **Structured logging (ILogger)** | Machine-readable logs for aggregation | LOW | Already exists; verify no Console.WriteLine in production code |
| **Metrics/telemetry (IMeterFactory)** | Prometheus/OpenTelemetry metrics | MEDIUM | .NET 10 meters for plugin execution, strategy calls, registry hits |
| **Defensive copies for mutable inputs** | Prevents time-of-check-time-of-use bugs | LOW | Clone collections/arrays at boundaries |
| **Bounded collections** | Prevents unbounded memory growth | MEDIUM | Max sizes on ConcurrentDictionary, List, cache structures |
| **Exception sanitization** | No sensitive data in exception messages | LOW | Already implemented. Verify all throw statements |
| **API versioning strategy** | Breaking changes must be opt-in | HIGH | Semantic versioning, deprecation timelines (6-18 months) |
| **SBOM generation** | Software Bill of Materials for supply chain security | LOW | Microsoft.Sbom.Targets NuGet package, auto-generate on build |
| **FIPS 140-3 compliant cryptography** | Required for government/military contracts | MEDIUM | Use .NET BCL crypto (certified), avoid 3rd-party crypto libs |

### Differentiators (Competitive Advantage)

Features that exceed hyperscale standards. Not required, but valued for security-critical environments.

| Feature | Value Proposition | Complexity | Notes |
|---------|-------------------|------------|-------|
| **Memory wiping for sensitive data** | Securely clears credentials/keys from memory | HIGH | Marshal.ZeroFreeGlobalAllocUnicode, GC.Collect(2, GCCollectionMode.Forced). SecureString deprecated but concept valid |
| **Algorithm agility for cryptography** | Supports crypto upgrades without code changes | HIGH | Abstraction layer allowing AES-256 -> post-quantum transition |
| **Key rotation automation** | Reduces blast radius of key compromise | HIGH | Automatic detection and re-encryption with new keys |
| **Circuit breaker per external dependency** | Prevents cascading failures in distributed systems | MEDIUM | Polly v8 with circuit breaker on each external service call |
| **Bulkhead isolation per tenant** | Multi-tenant resource isolation | HIGH | Polly bulkhead strategy, prevents noisy neighbor |
| **Timeouts with fallback strategies** | Graceful degradation under load | MEDIUM | Polly timeout + fallback, max 30 seconds for plugin operations |
| **Retry with exponential backoff + jitter** | Prevents thundering herd on transient failures | MEDIUM | Polly retry strategy, jitter prevents synchronized retries |
| **Adaptive rate limiting** | Protects SDK from abuse | MEDIUM | .NET 10 rate limiting middleware with concurrency/token bucket |
| **Compile-time code generation** | Type-safe plugin/strategy registration without reflection | HIGH | Source generators for capability registry, reduces runtime overhead |
| **P2P synchronization for distributed caches** | Multi-node consistency without central coordinator | HIGH | Gossip protocol for KnowledgeLake sync (Phase 3 requirement) |
| **Automatic telemetry for all operations** | Zero-config observability | MEDIUM | ActivitySource + meters auto-wired via dependency injection |
| **Chaos engineering hooks** | Testable failure injection | MEDIUM | IFailureSimulator interface for controlled fault injection |
| **Auto-scaling plugin instances** | Dynamic resource allocation based on load | HIGH | Phase 3 requirement: elastic plugin pools |
| **Self-healing via automatic rollback** | Detects degradation and reverts to last-known-good config | HIGH | Health check failure -> automatic plugin version downgrade |
| **Compliance audit logging** | Immutable audit trail for SOC2/HIPAA/NIST | MEDIUM | Append-only event stream with cryptographic signatures |

### Anti-Features (Deliberately NOT Implement in SDK)

Features that seem valuable but create problems in SDK context.

| Anti-Feature | Why Avoid | What to Do Instead |
|--------------|-----------|-------------------|
| **Global configuration singletons** | Breaks multi-tenancy, untestable | Pass configuration via dependency injection, support scoped config |
| **Synchronous blocking APIs (no CancellationToken)** | Deadlocks in async contexts, poor resource utilization | All public methods async with CancellationToken |
| **SecureString for new code** | Microsoft deprecated, misleading security claims | Use Azure Key Vault with managed identity, or char[] with manual zeroing |
| **reflection-based plugin discovery** | Slow startup, breaks AOT compilation | Source generators for compile-time registration |
| **Mutable DTOs** | Threading bugs, unpredictable state | C# records with init-only properties |
| **dynamic or object in public APIs** | No compile-time safety, breaks tooling | Strongly-typed generics |
| **Implicit async/await ConfigureAwait** | Context capture overhead in SDK code | ConfigureAwait(false) on all SDK internal awaits |
| **Unbounded caches** | Memory exhaustion under load | LRU/TTL eviction, max size limits |
| **Console.WriteLine for logging** | Not structured, not filterable | ILogger with structured logging |
| **Real-time sync everywhere** | Complexity without value, network overhead | Eventual consistency with configurable sync intervals |
| **Supporting EOL .NET versions** | Security risk, maintenance burden | Support only .NET 10 and future LTS (drop <.NET 8) |
| **Custom cryptography implementations** | Easy to get wrong, unaudited | Use .NET BCL crypto APIs (FIPS 140-3 certified) |
| **String concatenation for SQL/commands** | Injection vulnerabilities | Parameterized queries, command builders |
| **Exception-driven control flow** | Performance penalty, unclear intent | Result<T> or Option<T> for expected failures |
| **Generic Object pools without bounds** | Memory leaks under high load | Bounded ArrayPool<T> or custom pool with max size |

## Feature Dependencies

```
TreatWarningsAsErrors
    └──requires──> Roslyn analyzers configured
                       └──requires──> .editorconfig + .globalconfig

ActivitySource (distributed tracing)
    └──requires──> OpenTelemetry SDK
                       └──requires──> OTLP exporter configuration

Circuit breaker
    └──requires──> Polly v8
                       └──requires──> Health checks for state detection

SBOM generation
    └──requires──> Microsoft.Sbom.Targets
                       └──requires──> CycloneDX or SPDX format selection

Algorithm agility
    └──requires──> Crypto abstraction layer
                       └──requires──> Configuration-driven algorithm selection

P2P synchronization
    └──requires──> Distributed cache layer
                       └──requires──> Conflict resolution strategy

Auto-scaling
    └──requires──> Load metrics collection
                       └──requires──> Plugin lifecycle management

Memory wiping
    └──conflicts──> Garbage collected memory (best-effort only)

API versioning
    ├──requires──> Deprecation warning system
    └──requires──> Multi-version support infrastructure
```

### Dependency Notes

- **TreatWarningsAsErrors requires Roslyn analyzers**: Setting TreatWarningsAsErrors without configuring analyzers creates build noise from style violations. Configure Microsoft.CodeAnalysis.NetAnalyzers first.
- **ActivitySource requires OpenTelemetry**: ActivitySource emits traces, but without OTLP exporter nothing collects them. Configure exporter or traces disappear.
- **Circuit breaker requires health checks**: Circuit breaker decisions need health state. Without health checks, circuit remains closed or stuck open.
- **Algorithm agility conflicts with direct BCL calls**: If code calls AesCryptoServiceProvider directly, can't swap algorithms. Requires abstraction layer.
- **Memory wiping conflicts with GC**: .NET GC can move objects, leaving copies in memory. Best-effort via Marshal.ZeroFree* for unmanaged memory only.

## MVP Definition

### Already Implemented (Verify)
- [x] IDisposable pattern — Verify protected virtual Dispose(bool)
- [x] CancellationToken on async methods — Verify propagation
- [x] Nullable reference types — Verify no #nullable disable
- [x] Exception sanitization — Verify no sensitive data leaks
- [x] Structured logging (ILogger) — Verify no Console.WriteLine

### Launch With (v2.0 Hardening Phase)

Minimum for passing Google/Microsoft/Amazon code review.

- [ ] TreatWarningsAsErrors=true — Project-wide enforcement
- [ ] Roslyn analyzers (Microsoft.CodeAnalysis.NetAnalyzers 10.0+) — Security + performance rules
- [ ] Input validation at all boundaries — Plugin entry points, strategy inputs
- [ ] ReDoS protection (Regex with timeout) — Audit all Regex instances
- [ ] Constant-time crypto comparisons — Use CryptographicOperations.FixedTimeEquals
- [ ] Immutable DTOs (C# records) — All API contracts, event payloads
- [ ] Strong typing (no object/dynamic in public APIs) — Audit and fix
- [ ] ActivitySource per component — Plugin, strategy, kernel, registry
- [ ] Health checks — SDK-level liveness/readiness
- [ ] Metrics (IMeterFactory) — Plugin execution, strategy calls, cache hits
- [ ] Defensive copies — Clone collections at boundaries
- [ ] Bounded collections — Max sizes on caches, registries
- [ ] API versioning strategy — Semantic versioning + deprecation policy
- [ ] SBOM generation — Microsoft.Sbom.Targets on build
- [ ] FIPS 140-3 crypto — Audit all crypto calls, ensure BCL only

### Add After Hardening Validation (v2.1+)

Features to add once core hardening is verified working.

- [ ] Circuit breaker per dependency — Polly v8 on external calls
- [ ] Bulkhead isolation — Per-tenant resource limits
- [ ] Timeouts with fallback — Max 30s per operation
- [ ] Retry with exponential backoff — Polly retry strategy
- [ ] Adaptive rate limiting — .NET 10 rate limiting middleware
- [ ] Compile-time code generation — Source generators for registration
- [ ] Automatic telemetry — Zero-config ActivitySource + meters
- [ ] Chaos engineering hooks — IFailureSimulator interface

### Future Consideration (v3.0+ Distributed Infrastructure)

Features for Phase 3 distributed requirements.

- [ ] P2P synchronization — Gossip protocol for KnowledgeLake
- [ ] Auto-scaling plugin instances — Elastic plugin pools
- [ ] Self-healing rollback — Health check -> version downgrade
- [ ] Algorithm agility — Crypto abstraction for post-quantum transition
- [ ] Key rotation automation — Automatic re-encryption
- [ ] Memory wiping for sensitive data — Unmanaged memory zeroing
- [ ] Compliance audit logging — Immutable audit trail with signatures

## Feature Prioritization Matrix

| Feature | User Value | Implementation Cost | Priority |
|---------|------------|---------------------|----------|
| TreatWarningsAsErrors + Roslyn analyzers | HIGH (catches bugs early) | LOW (config only) | P1 |
| Input validation at boundaries | HIGH (security critical) | MEDIUM (systematic audit) | P1 |
| ReDoS protection | HIGH (DoS prevention) | MEDIUM (regex audit + timeout) | P1 |
| Constant-time crypto comparisons | HIGH (timing attack prevention) | LOW (replace comparisons) | P1 |
| ActivitySource per component | HIGH (debugging distributed) | MEDIUM (instrumentation) | P1 |
| Immutable DTOs | MEDIUM (bug prevention) | LOW (records conversion) | P1 |
| Health checks | HIGH (k8s readiness) | MEDIUM (SDK-level health) | P1 |
| Metrics/telemetry | HIGH (observability) | MEDIUM (meters + counters) | P1 |
| Bounded collections | HIGH (memory safety) | MEDIUM (max size enforcement) | P1 |
| API versioning strategy | HIGH (backward compat) | MEDIUM (policy + tooling) | P1 |
| SBOM generation | HIGH (supply chain security) | LOW (NuGet package) | P1 |
| FIPS 140-3 crypto audit | HIGH (gov/military contracts) | LOW (verify BCL usage) | P1 |
| Circuit breaker | MEDIUM (resilience) | MEDIUM (Polly integration) | P2 |
| Bulkhead isolation | MEDIUM (multi-tenant) | HIGH (per-tenant limits) | P2 |
| Timeouts with fallback | MEDIUM (graceful degradation) | MEDIUM (Polly + fallback logic) | P2 |
| Retry with backoff | MEDIUM (transient failure handling) | MEDIUM (Polly + jitter) | P2 |
| Rate limiting | MEDIUM (abuse prevention) | MEDIUM (.NET 10 middleware) | P2 |
| Compile-time code generation | LOW (perf optimization) | HIGH (source generators) | P2 |
| Chaos engineering | LOW (testing only) | MEDIUM (injection framework) | P2 |
| P2P synchronization | MEDIUM (Phase 3 req) | HIGH (gossip protocol) | P3 |
| Auto-scaling | MEDIUM (Phase 3 req) | HIGH (elastic pools) | P3 |
| Self-healing rollback | LOW (advanced resilience) | HIGH (automated rollback) | P3 |
| Algorithm agility | LOW (future-proofing) | HIGH (crypto abstraction) | P3 |
| Key rotation | LOW (advanced security) | HIGH (automated re-encryption) | P3 |
| Memory wiping | LOW (marginal security) | HIGH (GC conflicts) | P3 |
| Compliance audit logging | MEDIUM (SOC2/HIPAA) | MEDIUM (append-only log) | P3 |

**Priority key:**
- **P1: Must have for v2.0 launch** — Blocks hyperscale code review
- **P2: Should have for v2.1** — Competitive advantage, improves resilience
- **P3: Nice to have for v3.0+** — Advanced features, defer until distributed phase

## Competitor Feature Analysis

| Feature | AWS SDK (.NET) | Azure SDK (.NET) | Google Cloud SDK (.NET) | Our Approach |
|---------|----------------|------------------|-------------------------|--------------|
| Resilience patterns | Manual retry logic | Built-in retry + exponential backoff | Manual retry logic | Polly v8 (circuit breaker, bulkhead, timeout, retry) |
| Distributed tracing | X-Ray integration | Azure Monitor (Application Insights) | Cloud Trace integration | ActivitySource + OpenTelemetry (vendor-agnostic) |
| Health checks | SDK-level health endpoints | SDK health checks via ASP.NET Core | SDK health endpoints | ASP.NET Core health checks pattern |
| API versioning | Major version in namespace | Date-based versioning (2023-11-01) | Major version in package | Semantic versioning + deprecation policy |
| Cryptography | FIPS 140-2 validated modules | FIPS 140-3 validated (Azure Key Vault) | FIPS 140-2 validated | FIPS 140-3 via .NET BCL crypto |
| Metrics/telemetry | CloudWatch metrics | Azure Monitor metrics | Cloud Monitoring metrics | OpenTelemetry meters (vendor-agnostic) |
| SBOM | No automatic SBOM | SBOM in build pipeline | No automatic SBOM | Microsoft.Sbom.Targets (automatic) |
| Memory safety | IDisposable pattern | IDisposable + IAsyncDisposable | IDisposable pattern | IDisposable + bounded collections + defensive copies |
| Input validation | Manual validation | Azure.Core.Pipeline validators | Manual validation | Systematic boundary validation + ReDoS protection |
| Rate limiting | Service-side only | Service-side only | Service-side only | Client-side adaptive rate limiting |

## Sources

### Official Microsoft Documentation (HIGH confidence)
- [Best practices for using the Azure SDK with ASP.NET Core](https://learn.microsoft.com/en-us/dotnet/azure/sdk/aspnetcore-guidance)
- [Authentication best practices with Azure Identity library for .NET](https://learn.microsoft.com/en-us/dotnet/azure/sdk/authentication/best-practices)
- [Observability patterns - .NET](https://learn.microsoft.com/en-us/dotnet/architecture/cloud-native/observability-patterns)
- [CA1063: Implement IDisposable correctly](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/quality-rules/ca1063)
- [Implement a Dispose method - .NET](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/implementing-dispose)
- [Add distributed tracing instrumentation - .NET](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/distributed-tracing-instrumentation-walkthroughs)
- [Code analysis using .NET compiler platform (Roslyn) analyzers](https://learn.microsoft.com/en-us/visualstudio/code-quality/roslyn-analyzers-overview)
- [System.Security.SecureString class - .NET](https://learn.microsoft.com/en-us/dotnet/fundamentals/runtime-libraries/system-security-securestring)

### Official Third-Party Documentation (HIGH confidence)
- [Polly - Resilience and transient-fault-handling library](https://github.com/App-vNext/Polly)
- [Circuit breaker resilience strategy | Polly](https://www.pollydocs.org/strategies/circuit-breaker.html)
- [Bulkhead isolation | Polly](https://github.com/App-vNext/Polly/wiki/Bulkhead)
- [CycloneDX .NET - SBOM generation](https://github.com/CycloneDX/cyclonedx-dotnet)
- [Regular expression Denial of Service - ReDoS | OWASP](https://owasp.org/www-community/attacks/Regular_expression_Denial_of_Service_-_ReDoS)

### Industry Standards (HIGH confidence)
- [NIST Cryptographic Standards and Guidelines](https://www.nist.gov/programs-projects/cryptographic-standards-and-guidelines)
- [CNSA 2.0 Algorithms - NSA](https://media.defense.gov/2025/May/30/2003728741/-1/-1/0/CSA_CNSA_2.0_ALGORITHMS.PDF)
- [Software Bill of Materials (SBOM) | CISA](https://www.cisa.gov/sbom)

### Technical Analysis (MEDIUM confidence)
- [Performance Tuning in ASP.NET Core: Best Practices for 2026](https://www.syncfusion.com/blogs/post/performance-tuning-in-aspnetcore-2026)
- [How to Instrument Polly Resilience Policies with OpenTelemetry in .NET](https://oneuptime.com/blog/post/2026-02-06-instrument-polly-resilience-policies-opentelemetry-dotnet/view)
- [.NET Aspire Tutorial - Build Cloud-Ready Apps with .NET 10](https://codewithmukesh.com/blog/aspire-for-dotnet-developers-deep-dive/)
- [API Versioning Best Practices: How to Manage Changes Effectively](https://www.gravitee.io/blog/api-versioning-best-practices)
- [Building a Web API with C# Records for DTOs](https://www.c-sharpcorner.com/article/building-a-web-api-with-c-sharp-records-for-dtos/)
- [Cryptography .NET, Avoiding Timing Attack](https://bryanavery.co.uk/cryptography-net-avoiding-timing-attack/)
- [Creating a software bill of materials (SBOM) for an open-source NuGet package](https://andrewlock.net/creating-a-software-bill-of-materials-sbom-for-an-open-source-nuget-package/)
- [Memory Management in .NET – GC, IDisposable, Best Practices](https://medium.com/turbo-net/memory-management-dotnet-gc-idisposable-best-practices-061ee99d326f)

### Security Research (MEDIUM confidence)
- [The need for constant-time cryptography | Red Hat Research](https://research.redhat.com/blog/article/the-need-for-constant-time-cryptography/)
- [FixedTimeEquals in .NET Core](https://vcsjones.dev/fixed-time-equals-dotnet-core/)
- [When Would I Need SecureString in .NET? The Complete 2026 Guide](https://copyprogramming.com/howto/secure-string-is-it-still-worth-it)
- [Military-Grade Encryption Explained | NordPass](https://nordpass.com/blog/military-grade-encryption-explained/)
- [Quantum-Proof with CNSA 2.0 | Encryption Consulting](https://www.encryptionconsulting.com/quantum-proof-with-cnsa-2-0/)

---
*Feature research for: DataWarehouse v2.0 SDK Hardening*
*Researched: 2026-02-11*
