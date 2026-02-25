# Phase 4: Compliance, Storage & Replication - Research

**Researched:** 2026-02-10
**Domain:** Compliance automation, cloud storage, geo-replication, observability
**Confidence:** MEDIUM

## Summary

Phase 4 implements UltimateCompliance (T96), UltimateStorage (T97), UltimateReplication (T98), UniversalObservability (T100), and compliance reporting (T5.12-T5.16). The codebase already has substantial implementations using the strategy pattern, with plugins supporting 30+ compliance strategies, 50+ storage backends, 60+ replication strategies, and 50+ observability backends. Key gaps include FedRAMP-specific compliance checks, WORM wrapper implementations for cloud providers, compliance reporting dashboard integration, and evidence collection automation.

This phase builds on Phase 3's encryption and access control infrastructure, as secure storage and replication depend on data protection. The strategy pattern is consistently used: plugins register strategies via reflection, expose capabilities through IReadOnlyList<RegisteredCapability>, and communicate via message bus with Intelligence-aware fallbacks.

**Primary recommendation:** Verify existing strategy implementations meet all requirements, add missing FedRAMP/SOC2 controls, implement S3 Object Lock and Azure Immutable Blob WORM wrappers, build compliance reporting service with automated evidence collection, and ensure all AI-dependent features have rule-based fallbacks via IsIntelligenceAvailable checks.

## Standard Stack

### Core Libraries (Verified via WebSearch)

| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| FluentStorage | 5.x+ | Multi-cloud storage abstraction for C# | Strategy pattern abstraction over S3, Azure Blob, GCS, MinIO, SFTP, FTP with pure C# implementation |
| AWSSDK.S3 | 3.7+ | AWS S3 SDK for .NET | Official AWS SDK for S3 operations, S3 Object Lock for WORM |
| Azure.Storage.Blobs | 12.x+ | Azure Blob Storage SDK | Official Azure SDK with immutable blob support (SEC 17a-4(f) validated) |
| Google.Cloud.Storage.V1 | 4.x+ | Google Cloud Storage SDK | Official GCS SDK for object storage operations |
| Minio | 6.x+ | MinIO client for .NET | S3-compatible open-source object storage, supports on-premise deployments |

### Observability Libraries

| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| OpenTelemetry.Api | 1.8+ | Vendor-neutral telemetry API | Preferred for metrics, traces, and logs (OTEL standard) |
| Prometheus.Client | 8.x+ | Prometheus metrics exporter | Pull-based metrics, industry standard for monitoring |
| Jaeger.Client | 1.x+ | Distributed tracing client | OpenTracing-compatible distributed traces |
| Serilog | 3.x+ | Structured logging framework | Rich structured logging with multiple sinks |

### Compliance Frameworks (No Specific .NET Library - Custom Implementation Required)

No single .NET library provides GDPR/HIPAA/SOC2/FedRAMP compliance checks. Research shows compliance automation tools like Drata, Vanta, Scrut provide continuous monitoring and evidence collection but are SaaS platforms, not embeddable libraries. Our implementation uses custom strategy pattern with rule-based checks.

**Installation:**
```bash
dotnet add package AWSSDK.S3
dotnet add package Azure.Storage.Blobs
dotnet add package Google.Cloud.Storage.V1
dotnet add package Minio
dotnet add package OpenTelemetry.Api
dotnet add package OpenTelemetry.Exporter.Prometheus.AspNetCore
dotnet add package OpenTelemetry.Exporter.Jaeger
dotnet add package Serilog.AspNetCore
```

## Architecture Patterns

### Recommended Project Structure (Already Implemented)
```
Plugins/
├── DataWarehouse.Plugins.UltimateCompliance/
│   ├── UltimateCompliancePlugin.cs              # Plugin orchestrator with auto-discovery
│   ├── IComplianceStrategy.cs                    # Strategy interface
│   ├── Strategies/
│   │   ├── Regulations/                          # GDPR, HIPAA, SOX, PCI-DSS, SOC2 (5 strategies)
│   │   ├── Automation/                           # Automated checking, reporting, monitoring (6 strategies)
│   │   ├── Geofencing/                           # Data sovereignty, region restrictions (10 strategies)
│   │   ├── Privacy/                              # Anonymization, pseudonymization, consent (8 strategies)
│   │   └── SovereigntyMesh/                      # Advanced geofencing strategies
│   └── DataWarehouse.Plugins.UltimateCompliance.csproj
├── DataWarehouse.Plugins.UltimateStorage/
│   ├── UltimateStoragePlugin.cs                  # Plugin with 50+ storage backends
│   ├── StorageStrategyBase.cs                    # Base class for strategies
│   ├── StorageStrategyRegistry.cs                # Strategy registry with discovery
│   ├── Strategies/
│   │   ├── Cloud/                                # S3, Azure Blob, GCS, Oracle (5 strategies)
│   │   ├── S3Compatible/                         # Linode, Vultr, Scaleway, OVH (4 strategies)
│   │   ├── Enterprise/                           # Pure Storage, NetApp strategies
│   │   ├── Specialized/                          # gRPC, REST storage adapters
│   │   └── Innovation/                           # AI-tiered, self-healing, infinite storage
│   └── Features/
│       ├── StoragePoolAggregationFeature.cs      # Multi-backend pooling
│       └── ReplicationIntegrationFeature.cs      # Cross-plugin replication
├── DataWarehouse.Plugins.UltimateReplication/
│   ├── UltimateReplicationPlugin.cs              # Plugin with 60+ replication strategies
│   ├── ReplicationStrategyBase.cs                # Base class with vector clock support
│   ├── ReplicationStrategyRegistry.cs            # Strategy registry
│   ├── Strategies/
│   │   ├── Core/                                 # CRDT, multi-master, delta sync (4 strategies)
│   │   ├── Geo/                                  # Geo-replication, cross-region (2 strategies)
│   │   ├── Cloud/                                # AWS, Azure, GCP replication (6 strategies)
│   │   ├── ActiveActive/                         # Hot-hot, N-way active (3 strategies)
│   │   ├── CDC/                                  # Kafka, Debezium, Maxwell, Canal (4 strategies)
│   │   ├── AI/                                   # Predictive, semantic, adaptive (5 strategies)
│   │   ├── Conflict/                             # LWW, vector clock, merge resolution (7 strategies)
│   │   ├── Topology/                             # Star, mesh, chain, tree (6 strategies)
│   │   ├── DR/                                   # Disaster recovery strategies (5 strategies)
│   │   └── AirGap/                               # Bidirectional merge, zero data loss (7 strategies)
└── DataWarehouse.Plugins.UniversalObservability/
    ├── UniversalObservabilityPlugin.cs           # Plugin with 50+ observability backends
    ├── ObservabilityStrategyBase.cs              # Base class for strategies
    └── Strategies/                               # Metrics, logging, tracing, APM, alerting
```

### Pattern 1: Strategy Pattern with Auto-Discovery
**What:** Plugins use reflection to discover and register all strategy implementations
**When to use:** All Ultimate plugins (Compliance, Storage, Replication, Observability)
**Example:**
```csharp
// From UltimateCompliancePlugin.cs (verified in codebase)
private async Task DiscoverAndRegisterStrategiesAsync(CancellationToken ct)
{
    var strategyType = typeof(IComplianceStrategy);
    var assembly = Assembly.GetExecutingAssembly();

    var types = assembly.GetTypes()
        .Where(t => t.IsClass && !t.IsAbstract && strategyType.IsAssignableFrom(t));

    foreach (var type in types)
    {
        if (Activator.CreateInstance(type) is IComplianceStrategy strategy)
        {
            await strategy.InitializeAsync(new Dictionary<string, object>(), ct);
            _strategies[strategy.StrategyId] = strategy;
        }
    }
}
```

### Pattern 2: Intelligence-Aware Plugin Base
**What:** Plugins inherit IntelligenceAwarePluginBase, implement OnStartWithIntelligenceAsync and OnStartWithoutIntelligenceAsync
**When to use:** All plugins with AI-dependent features
**Example:**
```csharp
// From UltimateReplicationPlugin.cs (verified in codebase)
protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct)
{
    // Intelligence available - enable AI-enhanced features
    MessageBus.Subscribe(ReplicationTopics.PredictConflict, HandlePredictConflictMessageAsync);
    MessageBus.Subscribe(ReplicationTopics.OptimizeConsistency, HandleOptimizeConsistencyMessageAsync);
}

protected override async Task OnStartWithoutIntelligenceAsync(CancellationToken ct)
{
    // Intelligence unavailable - use fallback behavior
    MessageBus.Subscribe(ReplicationTopics.Replicate, HandleReplicateMessageAsync);
    MessageBus.Subscribe(ReplicationTopics.Sync, HandleSyncMessageAsync);
}
```

### Pattern 3: WORM Storage Implementation (Cloud Providers)
**What:** Wrap cloud provider immutability APIs to enforce write-once-read-many semantics
**When to use:** S3 Object Lock, Azure Immutable Blob, compliance-required storage
**Example (pattern, not yet implemented):**
```csharp
// S3 Object Lock WORM wrapper (to be implemented)
public class S3ObjectLockWormStrategy : IWormStorageProvider
{
    public async Task WriteAsync(string key, byte[] data, WormOptions options)
    {
        await _s3Client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = key,
            InputStream = new MemoryStream(data),
            ObjectLockMode = ObjectLockMode.COMPLIANCE,
            ObjectLockRetainUntilDate = DateTime.UtcNow.AddDays(options.RetentionDays)
        });
    }
}
```

### Pattern 4: Compliance Reporting with Evidence Collection
**What:** Compliance reporting service collects evidence from plugins via message bus, generates reports
**When to use:** SOC2, HIPAA, FedRAMP, GDPR audit reports
**Example (pattern from research, to be implemented):**
```csharp
// Compliance reporting service pattern (inspired by Drata/Vanta research)
public class ComplianceReportService
{
    public async Task<ComplianceReport> GenerateReportAsync(string framework, DateRange period)
    {
        var evidence = await CollectEvidenceAsync(framework, period);
        var gaps = await AnalyzeGapsAsync(evidence, framework);
        var controls = await MapControlsAsync(framework);

        return new ComplianceReport
        {
            Framework = framework,
            Period = period,
            Evidence = evidence,
            Gaps = gaps,
            Controls = controls,
            OverallStatus = DetermineStatus(gaps)
        };
    }

    private async Task<List<EvidenceItem>> CollectEvidenceAsync(string framework, DateRange period)
    {
        // Collect from audit logs, access logs, encryption logs, etc.
        var msg = new PluginMessage { Type = "compliance.collect-evidence", Payload = ... };
        await _messageBus.PublishAsync("compliance.evidence-request", msg);
        // Aggregate responses from all plugins
    }
}
```

### Anti-Patterns to Avoid
- **Direct plugin-to-plugin references:** All communication must go through message bus. Plugins reference SDK only.
- **Hardcoded provider selection:** Use strategy registry with auto-discovery, allow runtime provider switching.
- **Simulations/mocks in production code:** Rule 13 violation. Real implementations only, mocks only in tests.
- **AI-only features without fallbacks:** All AI-dependent features must have IsIntelligenceAvailable checks with rule-based fallbacks.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Multi-cloud storage abstraction | Custom S3/Azure/GCS wrappers | FluentStorage or BlobHelper | Handles provider differences, connection pooling, retries, error handling, multipart uploads |
| WORM immutability enforcement | Custom write-prevention logic | S3 Object Lock + Azure Immutable Blob native APIs | SEC 17a-4(f) validated, tamper-proof clock, kernel-level enforcement |
| Geo-replication conflict resolution | Custom merge logic | CRDT algorithms (Redis Enterprise, Riak) or vector clocks | Mathematically proven convergence, handles network partitions, no data loss |
| Distributed tracing correlation | Custom trace ID propagation | OpenTelemetry or W3C Trace Context | Industry standard, automatic context propagation, vendor-neutral |
| Compliance evidence collection | Manual log aggregation | Automated evidence mapping (Scrut pattern) | Cross-framework mapping, continuous monitoring, eliminates redundant tasks |

**Key insight:** Cloud provider APIs for WORM (S3 Object Lock, Azure Immutable Blob) are SEC/FINRA validated. Custom implementations risk compliance violations. OpenTelemetry is the industry standard for observability - vendor lock-in risks with proprietary solutions.

## Common Pitfalls

### Pitfall 1: WORM Mode Selection (Compliance vs Enterprise)
**What goes wrong:** Using enterprise mode for WORM when compliance mode is required, or vice versa
**Why it happens:** Misunderstanding regulatory requirements. Compliance mode is immutable (no deletion), enterprise mode allows privileged deletion with audit trail.
**How to avoid:**
- HIPAA/SEC/FINRA require compliance mode (true immutability)
- Internal archival can use enterprise mode (allows compliance officer override)
- Document retention policy requirements before selecting mode
**Warning signs:** Audit requirements mention "SEC 17a-4(f)" or "FINRA 4511" → use compliance mode. Internal policy only → enterprise mode acceptable.

### Pitfall 2: S3-Compatible Storage API Incompatibilities
**What goes wrong:** Assuming all S3-compatible providers support identical features (Object Lock, versioning, multipart upload)
**Why it happens:** MinIO/Ceph/Wasabi implement S3 API but may lag on advanced features
**How to avoid:**
- Feature detection before use: check if provider supports Object Lock via HeadBucket API
- Graceful degradation: fall back to application-level immutability if Object Lock unavailable
- Test against actual provider (MinIO behavior differs from S3)
**Warning signs:** "UnsupportedOperation" exceptions on MinIO, Object Lock not available on self-hosted Ceph

### Pitfall 3: Compliance Framework Overlaps and Conflicts
**What goes wrong:** Implementing GDPR's "right to be forgotten" breaks HIPAA's retention requirements
**Why it happens:** Frameworks have conflicting requirements. GDPR mandates data deletion on request, HIPAA requires minimum 6-year retention.
**How to avoid:**
- Implement framework priority rules (HIPAA > GDPR for healthcare data)
- Pseudonymization instead of deletion where both apply
- Document data classification → applicable frameworks mapping
**Warning signs:** User requests deletion of healthcare records. Check: is HIPAA-covered data? If yes, pseudonymize instead of delete.

### Pitfall 4: Geo-Replication Lag Exceeding Consistency SLA
**What goes wrong:** Cross-region replication lag causes stale reads, violating application consistency requirements
**Why it happens:** Network latency, large payloads, insufficient bandwidth between regions
**How to avoid:**
- Set realistic consistency SLA based on replication mode (eventual: >100ms lag acceptable, strong: <10ms required)
- Monitor replication lag via observability (Prometheus metric: replication_lag_milliseconds)
- Use synchronous replication for strong consistency (at cost of write latency)
- Delta sync or compression to reduce payload size
**Warning signs:** User reports seeing old data after write. Check replication lag metrics immediately.

### Pitfall 5: Evidence Collection Automation Gaps
**What goes wrong:** Manual evidence collection for SOC2/ISO27001 audits misses controls, causes audit delays
**Why it happens:** Evidence scattered across logs, access controls, encryption keys, backup systems
**How to avoid:**
- Map each control to evidence sources (SOC2 CC6.1 → encryption logs, key rotation logs)
- Automate evidence tagging via message bus (plugins publish evidence with control IDs)
- Continuous collection instead of point-in-time snapshots
- Cross-framework mapping (one evidence item satisfies multiple controls)
**Warning signs:** Auditor requests evidence and team scrambles to collect logs manually. Implement automated collection before next audit.

## Code Examples

Verified patterns from codebase:

### Strategy Registration and Selection
```csharp
// Source: UltimateStoragePlugin.cs (verified in codebase, lines 1079-1083)
private void DiscoverAndRegisterStrategies()
{
    // Auto-discover strategies in this assembly
    _registry.DiscoverStrategies(Assembly.GetExecutingAssembly());
}

// Strategy selection with fallback
private IObservabilityStrategy? SelectBestMetricsStrategy()
{
    return GetStrategy("opentelemetry") ??
           GetStrategy("prometheus") ??
           _strategies.Values.FirstOrDefault(s => s.Capabilities.SupportsMetrics);
}
```

### Intelligence-Aware Compliance
```csharp
// Source: UltimateCompliancePlugin.cs (verified in codebase, lines 163-202)
protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct)
{
    await base.OnStartWithIntelligenceAsync(ct);

    if (MessageBus != null)
    {
        var frameworks = _strategies.Values.Select(s => s.Framework).Distinct().ToList();

        await MessageBus.PublishAsync(IntelligenceTopics.QueryCapability, new PluginMessage
        {
            Type = "capability.register",
            Source = Id,
            Payload = new Dictionary<string, object>
            {
                ["supportsPIIDetection"] = true,
                ["supportsComplianceClassification"] = true,
                ["frameworks"] = frameworks
            }
        }, ct);

        SubscribeToPIIDetectionRequests();
    }
}
```

### Replication with Conflict Resolution
```csharp
// Source: UltimateReplicationPlugin.cs (verified in codebase, lines 540-584)
private async Task<Dictionary<string, object>> HandleResolveConflictAsync(Dictionary<string, object> payload)
{
    var conflict = new ReplicationConflict(
        DataId: Guid.NewGuid().ToString(),
        LocalVersion: localClock,
        RemoteVersion: remoteClock,
        LocalData: localData,
        RemoteData: remoteData,
        LocalNodeId: _nodeId,
        RemoteNodeId: "remote",
        DetectedAt: DateTimeOffset.UtcNow);

    var (resolvedData, resolvedVersion) = await _activeStrategy.ResolveConflictAsync(conflict);

    return new Dictionary<string, object>
    {
        ["success"] = true,
        ["resolvedData"] = Convert.ToBase64String(resolvedData.ToArray()),
        ["strategy"] = _activeStrategy.Characteristics.StrategyName
    };
}
```

### Observability Strategy Capability Declaration
```csharp
// Source: UniversalObservabilityPlugin.cs (verified in codebase, lines 255-306)
protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
{
    get
    {
        var capabilities = new List<RegisteredCapability>
        {
            new()
            {
                CapabilityId = $"{Id}.metrics",
                DisplayName = $"{Name} - Metrics",
                Description = "Record and export metrics to various backends",
                Category = CapabilityCategory.Observability,
                Tags = new[] { "observability", "metrics", "monitoring" }
            }
        };

        foreach (var kvp in _strategies)
        {
            if (kvp.Value is ObservabilityStrategyBase strategy)
            {
                capabilities.Add(strategy.GetStrategyCapability());
            }
        }

        return capabilities;
    }
}
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Per-provider SDKs (separate code for S3/Azure/GCS) | Unified abstraction (FluentStorage, strategy pattern) | 2020-2023 | Reduces code duplication, enables runtime provider switching |
| Manual compliance checks (spreadsheets, checklists) | Automated continuous monitoring (Drata/Vanta pattern) | 2021-2025 | Real-time compliance status, reduces audit prep time from weeks to hours |
| Point-in-time compliance snapshots | Continuous evidence collection with automatic control mapping | 2023-2026 | Eliminates manual evidence gathering, cross-framework evidence reuse |
| Master-slave replication (single write region) | Active-active multi-master with CRDT conflict resolution | 2018-2024 | Zero-downtime writes, improved write latency, automatic conflict resolution |
| Proprietary metrics/tracing (New Relic, Datadog only) | OpenTelemetry vendor-neutral standard | 2019-2025 | Prevents vendor lock-in, standardized APIs, multi-backend export |

**Deprecated/outdated:**
- Azure Storage SDK v11 (deprecated 2020) → Use Azure.Storage.Blobs v12+ (new API surface)
- AWS SDK v2 (deprecated 2022) → Use AWS SDK v3.7+ for .NET
- OpenTracing (archived 2022) → Use OpenTelemetry (merged standard)
- Custom replication without conflict resolution → Use CRDT-based strategies or vector clocks

## Open Questions

1. **FedRAMP-specific control implementation**
   - What we know: FedRAMP requires NIST 800-53 controls, more extensive than SOC2
   - What's unclear: Which controls are already covered by encryption/access control plugins vs. need new compliance strategies
   - Recommendation: Audit existing Phase 3 encryption/key/access implementations against FedRAMP Moderate baseline, add missing control checks to UltimateCompliance

2. **WORM replication across providers**
   - What we know: S3 Object Lock and Azure Immutable Blob provide WORM, requirement T5.5 mentions "WORM replication"
   - What's unclear: Does WORM replication mean (a) replicate to WORM storage, or (b) replication process itself is immutable (prevent tampering during transfer)?
   - Recommendation: Interpret as (a) - replicate TO WORM storage. UltimateReplication should support writing to IWormStorageProvider backends.

3. **Compliance reporting dashboard integration**
   - What we know: T5.12-T5.16 require SOC2/HIPAA/FedRAMP/GDPR reports with dashboards and alerts
   - What's unclear: Does "dashboard" mean web UI (ASP.NET/Blazor) or API-only with external visualization?
   - Recommendation: Implement ComplianceReportService as API (returns JSON reports), expose via UltimateInterface plugin (REST/GraphQL). Dashboard web UI deferred to Phase 14 (UltimateDashboards plugin).

4. **Geo-replication and data sovereignty conflicts**
   - What we know: GDPR restricts cross-border data transfer, geo-replication spans regions
   - What's unclear: How to enforce geofencing during replication (prevent EU data from replicating to US)?
   - Recommendation: UltimateCompliance GeofencingStrategy intercepts replication requests via message bus, blocks cross-border transfers unless exception exists (Standard Contractual Clauses). UltimateReplication checks compliance before replicating.

## Sources

### Primary (HIGH confidence)
- Codebase verified: UltimateCompliancePlugin.cs, UltimateStoragePlugin.cs, UltimateReplicationPlugin.cs, UniversalObservabilityPlugin.cs (2026-02-10)
- [FluentStorage GitHub](https://github.com/robinrodricks/FluentStorage) - Multi-cloud storage abstraction for C# (verified 2026-02-10)
- [Azure Immutable Storage Documentation](https://learn.microsoft.com/en-us/azure/storage/blobs/immutable-storage-overview) - SEC 17a-4(f) validated WORM (verified 2026-02-10)
- [CTERA WORM Storage Guide](https://www.ctera.com/blog/immutable-file-systems-ctera-worm-storage/) - WORM implementation best practices (verified 2026-02-10)

### Secondary (MEDIUM confidence)
- [Best S3-Compatible Storage Providers 2026](https://cloudian.com/guides/s3-storage/best-s3-compatible-storage-providers-top-5-options-in-2026/) - MinIO, Ceph, Wasabi comparison (verified 2026-02-10)
- [Compliance Automation Tools 2026](https://www.zluri.com/blog/compliance-automation-tools) - Drata, Vanta, Scrut automated evidence collection patterns (verified 2026-02-10)
- [Redis Active-Active Geo-Distribution](https://redis.io/active-active/) - CRDT-based conflict resolution (verified 2026-02-10)
- [Ceph Multi-Site Geo-Redundancy](https://oneuptime.com/blog/post/2026-01-07-ceph-multi-site-geo-redundancy/view) - Ceph replication conflict resolution (January 2026)
- [Compliance Evidence Collection Automation](https://www.trustcloud.ai/security-questionnaires/automating-evidence-collection-for-regulatory-compliance-tools-best-practices/) - Best practices (verified 2026-02-10)

### Tertiary (LOW confidence)
- [HIPAA, NIST, FedRAMP Standards Comparison](https://www.strongdm.com/blog/fisma-vs-fedramp-nist-vs-iso-soc2-vs-hipaa-iso27001-vs-soc2) - Framework overlap analysis (no date verification)
- [Kafka Multi-Region Geo-Replication](https://streamnative.io/blog/kafkas-approach-to-multi-region-streaming) - Async replication patterns (no 2026 content)

## Metadata

**Confidence breakdown:**
- Standard stack: MEDIUM - FluentStorage/Azure/AWS SDKs verified via docs, OpenTelemetry standard verified, but no specific .NET compliance libraries exist
- Architecture: HIGH - Codebase patterns verified (strategy pattern, Intelligence-aware plugins, message bus communication)
- Pitfalls: MEDIUM - WORM modes and S3 compatibility verified via official docs, geo-replication lag and evidence collection based on research and industry patterns

**Research date:** 2026-02-10
**Valid until:** 2026-03-31 (45 days - compliance/cloud storage standards evolve quarterly)
