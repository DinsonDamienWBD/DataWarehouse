# Phase 38: Feature Composition & Orchestration - Research

**Researched:** 2026-02-16
**Domain:** Cross-strategy composition, orchestration layers, message bus wiring
**Confidence:** HIGH

## Summary

Phase 39 wires existing, fully-implemented strategy classes into higher-level composite features via orchestration layers. All five COMP requirements compose pieces that already exist and work individually -- the work is building the glue code (orchestrators, feedback loops, rules engines) and the new aggregate types (ProvenanceCertificate, DataRoom, AttestationDocument).

The codebase has rich, production-ready building blocks: SchemaEvolution strategies with real compatibility checking (forward/backward/full), SelfTrackingDataStrategy with per-object hash chains and BFS graph traversal, BlockchainVerificationService with anchor/verify/audit-chain operations, EphemeralSharingStrategy with cryptographic token generation, GeofencingStrategy with 11 sub-strategies, DataMarketplace with full commerce lifecycle, SelfHealingStorageStrategy with auto-repair and health monitoring, AutoTieringFeature with policy-based data movement, and 9 build strategies for supply chain.

**Primary recommendation:** Each COMP plan creates a thin orchestrator class that subscribes to message bus topics, calls existing strategies, and exposes a unified API. No new base classes needed. All orchestrators live in SDK or appropriate plugin.

## Existing Code Assessment

### COMP-01: Self-Evolving Schema Engine

| Component | File | Status | Assessment |
|-----------|------|--------|------------|
| ForwardCompatibleSchemaStrategy | `Plugins/.../SchemaEvolution/SchemaEvolutionStrategies.cs` | REAL | Full compatibility checking, type widening rules, version history |
| BackwardCompatibleSchemaStrategy | Same file | REAL | Backward compat checking with defaults requirement |
| FullCompatibleSchemaStrategy | Same file | REAL | Strict compat: no removal, no type changes, optional-only additions |
| SchemaMigrationStrategy | Same file | REAL | Up/down migrations, step execution, rollback |
| SchemaRegistryStrategy | Same file | REAL | Centralized registry with subjects, versions, fingerprinting |
| SelfLearningCatalogStrategy | `Plugins/.../LivingCatalog/LivingCatalogStrategies.cs` | REAL | Feedback loop, confidence scoring, auto-apply corrections |
| AutoTaggingStrategy | Same file | REAL | Pattern-based domain tag inference from column names |
| SchemaEvolutionTrackerStrategy | Same file | REAL | Version history, diff computation (added/removed/changed columns) |
| UsagePatternLearnerStrategy | Same file | REAL | Access tracking, co-access recommendations |
| UltimateIntelligence pattern detection | `Plugins/.../UltimateIntelligence/` | REAL | AnomalyDetectionStrategy, ContentClassificationStrategy, SemanticSearchStrategy all implemented |
| IntelligenceTopics | `Plugins/.../IntelligenceTopics.cs` | REAL | Message bus topics for: `intelligence.connector.detect-anomaly`, `intelligence.evolution.learn`, `intelligence.evolution.adapt` |

**What's Missing:** An orchestrator class (SchemaEvolutionEngine) that:
1. Subscribes to data ingestion events on message bus
2. Calls UltimateIntelligence pattern detection (via `intelligence.connector.detect-anomaly` topic)
3. When pattern change >10% detected, calls ForwardCompatibleSchemaStrategy.CheckCompatibilityAsync
4. Proposes schema evolution via SchemaMigrationStrategy.CreateMigrationAsync
5. Updates LivingCatalog via SchemaEvolutionTrackerStrategy.RecordSchema
6. Auto-approve or queue for admin approval

**Message Bus Topics Available:**
- `intelligence.connector.detect-anomaly` / `.response` -- trigger pattern detection
- `intelligence.evolution.learn` / `.response` -- record learning interactions
- `intelligence.evolution.adapt` / `.response` -- adaptive behavior adjustment
- Custom topics needed: `schema.evolution.proposed`, `schema.evolution.approved`, `schema.evolution.applied`

### COMP-02: Data DNA Provenance Certificates

| Component | File | Status | Assessment |
|-----------|------|--------|------------|
| SelfTrackingDataStrategy | `Plugins/.../UltimateDataLineage/Strategies/ActiveLineageStrategies.cs` | REAL | Per-object TransformationRecord with Operation, BeforeHash, AfterHash. BFS graph traversal for upstream/downstream. Impact analysis with weighted scoring. |
| RealTimeLineageCaptureStrategy | Same file | REAL | Event-driven timestamps, upstream/downstream link maps |
| ImpactAnalysisEngineStrategy | Same file | REAL | Criticality-weighted BFS with depth decay |
| BlockchainVerificationService | `Plugins/.../TamperProof/Services/BlockchainVerificationService.cs` | REAL | VerifyAnchorAsync (hash verification), GetAuditChainAsync (full chain), CreateExternalAnchorAsync (Ethereum/Bitcoin), ValidateChainIntegrityAsync |
| IBlockchainProvider (SDK contract) | `DataWarehouse.SDK/Contracts/TamperProof/` | REAL | AnchorAsync, VerifyAnchorAsync, GetAuditChainAsync |
| SealService | `Plugins/.../TamperProof/Services/SealService.cs` | REAL | Object sealing |
| AuditTrailService | `Plugins/.../TamperProof/Services/AuditTrailService.cs` | REAL | Tamper-evident audit logs |

**Key Data Structures:**
- `TransformationRecord(TransformId, Operation, SourceObjectId, Timestamp, BeforeHash, AfterHash)` -- per-object lineage
- `ProvenanceRecord` -- used by LineageStrategyBase with DataObjectId, SourceObjects, BeforeHash, AfterHash, Transformation
- `BlockchainVerificationResult` -- Success, ObjectId, BlockNumber, AnchoredAt, Confirmations, Anchor
- `ExternalAnchorRecord` -- AnchorId, ObjectId, MerkleRoot, TargetChain, Status

**What's Missing:** A `ProvenanceCertificate` record type and a `ProvenanceCertificateService` that:
1. Takes an object ID
2. Walks the SelfTrackingDataStrategy hash chain (upstream traversal)
3. Gets the blockchain anchor from BlockchainVerificationService
4. Composes a certificate with: complete chain of TransformationRecords, root anchor hash, blockchain verification result
5. Provides `Verify()` method that re-walks the chain and verifies each hash link

### COMP-03: Cross-Organization Data Rooms

| Component | File | Status | Assessment |
|-----------|------|--------|------------|
| EphemeralLinkGenerator | `Plugins/.../EphemeralSharing/EphemeralSharingStrategy.cs` | REAL | Cryptographic token generation (32-byte URL-safe), time-limited URLs, short tokens |
| GeofencingStrategy + 10 sub-strategies | `Plugins/.../Geofencing/` (11 files) | REAL | RegionRegistryStrategy, DataTaggingStrategy, WriteInterceptionStrategy, ReplicationFenceStrategy, AdminOverridePreventionStrategy, ComplianceAuditStrategy, CrossBorderExceptionsStrategy, AttestationStrategy, DynamicReconfigurationStrategy |
| ZeroTrustStrategy | `Plugins/.../UltimateAccessControl/Strategies/Core/ZeroTrustStrategy.cs` | REAL | Zero-trust access control |
| DataMarketplacePlugin | `Plugins/.../DataMarketplace/DataMarketplacePlugin.cs` | REAL | Full lifecycle: listings, subscriptions, licenses, usage metering, billing, reviews, access grants, smart contracts. Message commands: `marketplace.list`, `marketplace.subscribe`, `marketplace.access`, `marketplace.revoke` |

**What's Missing:** A `DataRoom` class and `DataRoomOrchestrator` that:
1. Create(name, orgs, datasets, expiry) -- provisions EphemeralSharing links, Geofencing boundaries, ZeroTrust policies
2. Invite(org, permissions) -- issues time-limited access via EphemeralLinkGenerator
3. SetExpiry(duration) -- configures auto-destruct timer
4. GetAuditTrail() -- aggregates from ComplianceAuditStrategy
5. Destroy() -- revokes all access, deletes shared data references
6. Integration with DataMarketplace for data commerce within rooms

### COMP-04: Autonomous Operations Engine

| Component | File | Status | Assessment |
|-----------|------|--------|------------|
| SelfHealingStorageStrategy | `Plugins/.../UltimateStorage/Strategies/Innovation/SelfHealingStorageStrategy.cs` | REAL | Continuous health monitoring, corruption detection, auto-repair via erasure coding, proactive replication, scrubbing, node failure detection, repair queue |
| AutoTieringFeature | `Plugins/.../UltimateStorage/Features/AutoTieringFeature.cs` | REAL | Access frequency tracking, temperature scoring (Hot/Warm/Cold/Archive), policy-based migration, background tiering worker |
| DataGravitySchedulerStrategy | `Plugins/.../UltimateCompute/Strategies/IndustryFirst/DataGravitySchedulerStrategy.cs` | REAL | Locality scoring (same-node=1.0, same-rack=0.7, same-dc=0.4, remote=0.1), weighted cost model |
| AlertManagerStrategy | `Plugins/.../UniversalObservability/Strategies/Alerting/AlertManagerStrategy.cs` | REAL | Prometheus AlertManager integration, PostAlertsAsync, GetAlertsAsync with severity/labels |

**Events/Messages for Auto-Remediation:**
- SelfHealingStorageStrategy has internal health check timer, scrubbing timer, repair worker timer
- AutoTieringFeature has background tiering timer
- AlertManagerStrategy posts to Prometheus `/api/v2/alerts`
- Message bus topics from observability: alerting events can be subscribed to
- DataGravityScheduler publishes placement records

**What's Missing:** An `AutonomousOperationsEngine` class that:
1. Subscribes to observability alert topics on message bus
2. Maintains a rules engine (condition -> action mappings)
3. When alert matches rule: triggers appropriate strategy (SelfHealing for corruption, AutoTiering for capacity, DataGravity for performance)
4. Logs all auto-remediation actions with audit trail
5. Provides manual override and rule management API

### COMP-05: Supply Chain Attestation

| Component | File | Status | Assessment |
|-----------|------|--------|------------|
| TamperProofPlugin | `Plugins/.../TamperProof/TamperProofPlugin.cs` | REAL | Full WORM storage pipeline, hash chains, integrity verification |
| BlockchainVerificationService | (see COMP-02 above) | REAL | Anchor/verify/external anchor |
| DotNetBuildStrategy | `Plugins/.../UltimateStorageProcessing/Strategies/Build/DotNetBuildStrategy.cs` | REAL | `dotnet build` invocation, error/warning parsing |
| TypeScriptBuildStrategy | Same directory | REAL | TypeScript builds |
| RustBuildStrategy | Same directory | REAL | Rust/Cargo builds |
| GoBuildStrategy | Same directory | REAL | Go builds |
| DockerBuildStrategy | Same directory | REAL | Docker builds |
| BazelBuildStrategy | Same directory | REAL | Bazel builds |
| GradleBuildStrategy | Same directory | REAL | Gradle builds |
| MavenBuildStrategy | Same directory | REAL | Maven builds |
| NpmBuildStrategy | Same directory | REAL | npm builds |

**What's Missing:** An `AttestationDocument` type and `SupplyChainAttestationService` that:
1. Takes build output from any BuildStrategy
2. Records: source commit hash, build command, build environment, output hashes
3. Formats as SLSA Level 2 compatible attestation (in-toto format)
4. Anchors attestation hash via TamperProof blockchain
5. Provides `Verify(binaryPath)` that checks binary hash against attestation

## Composition Strategy

### How Pieces Wire Together

All composition uses the **message bus** for inter-plugin communication. Each orchestrator:
1. Lives in the appropriate plugin or a new composition namespace
2. Subscribes to relevant message bus topics
3. Calls strategy methods directly when in-plugin, or via message bus when cross-plugin
4. Exposes a unified API through the orchestrator class
5. Registers capabilities in the knowledge bank

### Message Bus Architecture

```
[IntelligencePlugin] --detect-anomaly.response--> [SchemaEvolutionEngine]
[SchemaEvolutionEngine] --schema.evolution.proposed--> [Admin/AutoApprove]
[LineagePlugin] --provenance.request--> [ProvenanceCertificateService]
[ObservabilityPlugin] --alert.fired--> [AutonomousOperationsEngine]
[AutonomousOperationsEngine] --remediation.trigger--> [SelfHealingStorage/AutoTiering]
[BuildStrategy] --build.completed--> [SupplyChainAttestationService]
```

## Recommended Plan Structure

### Wave Ordering

**Wave 1 (Independent - can run in parallel):**
- 38-02: Data DNA Provenance Certificates (COMP-02) -- self-contained, lineage + blockchain
- 38-05: Supply Chain Attestation (COMP-05) -- self-contained, tamperproof + build

**Wave 2 (Depends on intelligence integration patterns from Wave 1):**
- 38-01: Self-Evolving Schema (COMP-01) -- needs intelligence message bus pattern
- 38-04: Autonomous Operations (COMP-04) -- needs observability event subscription pattern

**Wave 3 (Most complex, multi-plugin orchestration):**
- 38-03: Cross-Org Data Rooms (COMP-03) -- orchestrates 4+ strategies across plugins

### Dependencies Between Plans

| Plan | Depends On | Reason |
|------|-----------|--------|
| 38-01 | None (but benefits from 38-02 pattern) | Can establish message bus subscription patterns |
| 38-02 | None | Self-contained: lineage + tamperproof |
| 38-03 | None (but most complex) | Multi-plugin orchestration |
| 38-04 | None | Observability subscription + rules engine |
| 38-05 | None (but benefits from 38-02 pattern) | SLSA format + tamperproof |

**Recommended order:** 38-02, 38-05, 38-01, 38-04, 38-03

## Risk Assessment

| Risk | Severity | Mitigation |
|------|----------|------------|
| Cross-plugin message bus topic naming conflicts | LOW | Use hierarchical namespace: `composition.schema.*`, `composition.provenance.*` |
| Circular dependencies between orchestrators | MEDIUM | Orchestrators are consumers, not producers -- they subscribe and call, they don't depend on each other |
| Intelligence plugin availability | LOW | All orchestrators must implement graceful degradation (intelligence unavailable = reduced functionality, not failure) |
| Performance overhead from message bus composition | LOW | All existing strategies are async with CancellationToken; composition adds minimal overhead |
| Data room lifecycle complexity | MEDIUM | DataRoom needs careful state machine design: Created -> Active -> Expiring -> Destroyed |

## Common Pitfalls

### Pitfall 1: Direct Plugin References
**What goes wrong:** Orchestrator imports another plugin's namespace directly
**How to avoid:** ALL cross-plugin communication via message bus. Use SDK types only.

### Pitfall 2: Missing Graceful Degradation
**What goes wrong:** Schema evolution engine crashes when UltimateIntelligence plugin isn't loaded
**How to avoid:** Every orchestrator checks for strategy availability and operates in reduced mode

### Pitfall 3: Unbounded Event Queues
**What goes wrong:** Autonomous operations engine subscribes to all alerts, queue grows unbounded
**How to avoid:** Use bounded channels, configurable max queue size, drop oldest when full

## Sources

### Primary (HIGH confidence)
- Direct code reading of all strategy files listed above
- SDK contract interfaces for message bus, lineage, tamperproof
- IntelligenceTopics.cs for available message bus topics
- All 9 build strategies for supply chain inventory

### Confidence Assessment
- Existing code assessment: HIGH -- direct file reading, all strategies verified
- Composition strategy: HIGH -- follows established message bus patterns
- Risk assessment: MEDIUM -- complexity of multi-plugin orchestration is inherent
