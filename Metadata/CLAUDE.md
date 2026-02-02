# CLAUDE.md - AI Assistant Context for DataWarehouse

## Project Overview

DataWarehouse is a production-grade, AI-native data warehouse SDK built in C#. It uses a plugin-based architecture where all components communicate via messages rather than direct function calls.

## Repository Structure

```
DataWarehouse/
├── DataWarehouse.SDK/           # Core SDK library
│   ├── AI/                      # AI integration
│   │   ├── IAIProvider.cs       # AI-agnostic provider interface
│   │   ├── VectorOperations.cs  # Embeddings, similarity, IVectorStore
│   │   ├── GraphStructures.cs   # Knowledge graphs, nodes, edges
│   │   ├── MathUtilities.cs     # Statistics, normalization, activation
│   │   └── Runtime/             # Runtime types
│   ├── Attributes/              # Custom attributes
│   ├── Contracts/               # Interfaces and base classes
│   ├── Extensions/              # Extension methods
│   ├── Governance/              # Governance contracts
│   ├── IO/                      # I/O utilities
│   ├── Primitives/              # Core types
│   ├── Security/                # Security contracts
│   ├── Services/                # Core services
│   └── Utilities/               # Helper utilities
├── Metadata/                    # Project documentation
│   ├── CLAUDE.md                # This file
│   ├── README.md                # Project readme
│   ├── RULES.md                 # The 12 Absolute Rules
│   └── TODO.md                  # Task tracking
└── DataWarehouse.slnx           # Solution file
```

## Key Concepts

### Message-Based Architecture
- Plugins NEVER reference each other directly
- All communication via `IMessageBus` (pub/sub + request/response)
- Standard topics in `MessageTopics` class
- `PluginMessage` for all inter-plugin communication

### Plugin Hierarchy (Use Base Classes, Not Interfaces Directly)

```
PluginBase (IPlugin)
├── DataTransformationPluginBase (IDataTransformation)
│   └── PipelinePluginBase (runtime ordering)
├── StorageProviderPluginBase (IStorageProvider)
│   └── ListableStoragePluginBase (IListableStorage)
│       └── TieredStoragePluginBase (ITieredStorage)
├── MetadataIndexPluginBase (IMetadataIndex)
├── FeaturePluginBase (IFeaturePlugin)
│   ├── InterfacePluginBase (REST, gRPC, SQL)
│   ├── ConsensusPluginBase (IConsensusEngine)
│   └── RealTimePluginBase (IRealTimeProvider)
├── SecurityProviderPluginBase
├── OrchestrationProviderPluginBase
├── IntelligencePluginBase (AI providers)
└── CloudEnvironmentPluginBase (ICloudEnvironment)
```

### Which Base Class to Use?

| Plugin Type | Base Class |
|-------------|-----------|
| S3, Local, IPFS storage | `StorageProviderPluginBase` |
| Storage with file listing | `ListableStoragePluginBase` |
| Hot/Cold/Archive tiering | `TieredStoragePluginBase` |
| Compression, Encryption | `DataTransformationPluginBase` |
| Pipeline stage (orderable) | `PipelinePluginBase` |
| SQLite, Postgres metadata | `MetadataIndexPluginBase` |
| REST API, SQL listener | `InterfacePluginBase` |
| Raft, Paxos consensus | `ConsensusPluginBase` |
| Pub/Sub, Change feeds | `RealTimePluginBase` |
| AWS, Azure, GCP detection | `CloudEnvironmentPluginBase` |
| OpenAI, Claude, Ollama | `IntelligencePluginBase` |
| ACL, Authentication | `SecurityProviderPluginBase` |
| Workflow orchestration | `OrchestrationProviderPluginBase` |

## AI Infrastructure

### IAIProvider (AI-Agnostic)
```csharp
// Supports ANY AI provider without SDK changes
interface IAIProvider {
    string ProviderId { get; }           // "openai", "anthropic", "ollama"
    AICapabilities Capabilities { get; } // Flags: Streaming, Embeddings, etc.
    Task<AIResponse> CompleteAsync(AIRequest request);
    IAsyncEnumerable<AIStreamChunk> CompleteStreamingAsync(AIRequest request);
    Task<float[]> GetEmbeddingsAsync(string text);
}
```

### VectorOperations
```csharp
// Embeddings and similarity
IVectorOperations ops = new DefaultVectorOperations();
float similarity = ops.CosineSimilarity(vectorA, vectorB);
var matches = ops.FindTopK(query, candidates, k: 10);

// Vector storage
interface IVectorStore {
    Task StoreAsync(string id, float[] vector, Dictionary<string, object>? metadata);
    Task<IEnumerable<VectorMatch>> SearchAsync(float[] query, int topK);
}
```

### GraphStructures
```csharp
// Knowledge graphs for relationships
interface IKnowledgeGraph {
    Task<GraphNode> AddNodeAsync(string label, Dictionary<string, object>? properties);
    Task<GraphEdge> AddEdgeAsync(string fromId, string toId, string relationship);
    Task<GraphPath?> FindPathAsync(string fromId, string toId);
    Task<GraphTraversalResult> TraverseAsync(string startId, GraphTraversalOptions options);
}
```

### MathUtilities
```csharp
// Statistical and ML utilities
float mean = AIMath.Mean(values);
float[] normalized = AIMath.MinMaxNormalize(values);
float[] probs = AIMath.Softmax(logits);
float entropy = AIMath.Entropy(probabilities);
```

## Pipeline Orchestration

### Runtime Ordering
```csharp
// Default: Compress → Encrypt
var config = PipelineConfiguration.CreateDefault();

// User override: Encrypt → CustomCompress
config.WriteStages = new List<PipelineStageConfig>
{
    new() { StageType = "Encryption", Order = 100 },
    new() { StageType = "CustomCompression", Order = 200 }
};
```

### PipelinePluginBase Properties
- `DefaultOrder` - Execution order (lower = earlier)
- `AllowBypass` - Can skip based on content analysis
- `RequiredPrecedingStages` - Dependencies
- `IncompatibleStages` - Conflicts

## Critical Patterns

### Property Override (NOT Assignment)
```csharp
// WRONG - CS0200 error
public MyPlugin() { SemanticDescription = "..."; }

// CORRECT - property override
protected override string SemanticDescription => "...";
```

### Plugin ID Format
```csharp
public override string Id => "com.company.plugin.storage.s3";
```

## The 12 Absolute Rules (Summary)

1. **Production-Ready** - Full error handling, validation, thread safety
2. **Documentation** - XML docs on ALL public APIs
3. **Code Reuse** - Use base classes, no duplication
4. **Message-Based** - No direct plugin references
5. **Plugin Architecture** - Follow directory structure
6. **Base Classes** - ALWAYS extend, never implement interfaces directly
7. **AI-Native** - Semantic descriptions, tags, profiles
8. **Resilience** - Retry logic, circuit breakers
9. **Performance** - Async I/O, streaming, pooling
10. **Testing** - Dependency injection, testable code
11. **Security** - Input validation, encryption, audit
12. **Task Tracking** - Update TODO.md

## Forbidden Practices

- Direct plugin-to-plugin references
- Implementing interfaces directly (use base classes)
- TODO comments in code
- Console logging
- Empty catch blocks
- Magic numbers
- **Simulations, mock-ups, or stub implementations** (see Rule 13 below)
- **Direct project references between plugins** (see Plugin Isolation below)

---

## RULE 13: Production-Ready Only - NO Simulations, NO Mock-ups

> **ABSOLUTE RULE:** Every feature, algorithm, and strategy MUST be fully production-ready.
> There are NO exceptions to this rule.

### What This Means

| ❌ FORBIDDEN | ✅ REQUIRED |
|--------------|-------------|
| "QKD simulation" | Real QKD hardware integration OR don't implement |
| "Mock HSM" | Real PKCS#11/HSM driver integration |
| "Simulated quantum" | Real quantum computing API (IBM Quantum, AWS Braket) |
| "Placeholder logic" | Complete, working implementation |
| "TODO: implement later" | Full implementation now |
| "Stub for testing" | Real implementation with real tests |

### Naming Conventions

- **DO NOT** use words like: `Simulation`, `Mock`, `Stub`, `Fake`, `Placeholder`, `Demo`
- **DO** use: `Strategy`, `Provider`, `Handler`, `Engine`, `Service`

### Hardware-Dependent Features

For features requiring specialized hardware (quantum, HSM, TPM, etc.):

```csharp
// CORRECT: Real hardware integration with graceful degradation
public class TpmKeyStoreStrategy : IKeyStoreStrategy
{
    public async Task<bool> IsAvailableAsync()
    {
        // Actually check for TPM 2.0 hardware
        return await _tpmDriver.DetectTpmAsync();
    }

    public async Task<EncryptionKey> GetKeyAsync(string keyId, CancellationToken ct)
    {
        if (!await IsAvailableAsync())
            throw new HardwareNotAvailableException("TPM 2.0 hardware not detected");

        // Real TPM operations
        return await _tpmDriver.UnsealKeyAsync(keyId, ct);
    }
}
```

### Cutting-Edge Technologies

For truly cutting-edge features (DNA storage, quantum memory, etc.):
- Either integrate with real research APIs/hardware
- Or mark the feature as **"Future Roadmap"** in TODO.md and don't implement it yet
- **NEVER** create a fake/simulated version

---

## Plugin Isolation Rules

### RULE: Plugins Reference ONLY the SDK

```
┌─────────────────────────────────────────────────────────────────────┐
│                        ALLOWED REFERENCES                            │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│   Plugin A ──────► DataWarehouse.SDK                                │
│   Plugin B ──────► DataWarehouse.SDK                                │
│   Plugin C ──────► DataWarehouse.SDK                                │
│                                                                      │
│   ❌ Plugin A ──X──► Plugin B  (FORBIDDEN - direct reference)       │
│   ❌ Plugin B ──X──► Plugin C  (FORBIDDEN - direct reference)       │
│                                                                      │
│   ✅ Plugin A ──► MessageBus ──► Plugin B  (ALLOWED - via messages) │
│                                                                      │
└─────────────────────────────────────────────────────────────────────┘
```

### .csproj File Pattern

```xml
<!-- CORRECT: Only reference SDK -->
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\..\DataWarehouse.SDK\DataWarehouse.SDK.csproj" />
  </ItemGroup>

  <!-- ❌ NEVER DO THIS: -->
  <!-- <ProjectReference Include="..\OtherPlugin\OtherPlugin.csproj" /> -->
</Project>
```

### Inter-Plugin Communication

All plugin-to-plugin communication MUST use the message bus:

```csharp
// CORRECT: Request AI processing via message bus
public class SemanticDeduplicationStrategy : IDeduplicationStrategy
{
    private readonly IMessageBus _messageBus;

    public async Task<string> GetSemanticHashAsync(Stream content, CancellationToken ct)
    {
        // Request embeddings from Intelligence plugin via message bus
        var response = await _messageBus.RequestAsync<EmbeddingResponse>(
            topic: "intelligence.embeddings.generate",
            request: new EmbeddingRequest { Content = content },
            timeout: TimeSpan.FromSeconds(30),
            ct: ct
        );

        if (!response.Success)
        {
            // Graceful degradation: fall back to content hash
            _logger.LogWarning("Intelligence plugin unavailable, falling back to content hash");
            return await ComputeContentHashAsync(content, ct);
        }

        return ComputeHashFromEmbedding(response.Embedding);
    }
}
```

---

## AI-Dependent Features & Graceful Degradation

### Features Requiring Intelligence Plugin

Many advanced features require AI capabilities from the Universal Intelligence plugin (T90).
These features MUST:

1. **Declare the dependency explicitly** in their documentation and TODO.md
2. **Use message bus** to communicate with Intelligence plugin
3. **Fail gracefully** if Intelligence plugin is unavailable
4. **Provide fallback behavior** where possible

### Dependency Declaration Pattern

```csharp
/// <summary>
/// Semantic deduplication using AI-generated embeddings.
/// </summary>
/// <remarks>
/// <b>DEPENDENCY:</b> Requires Universal Intelligence plugin (T90) for embedding generation.
/// If Intelligence plugin is unavailable, falls back to content-hash deduplication.
/// </remarks>
public class SemanticDeduplicationStrategy : IDeduplicationStrategy
{
    public bool RequiresIntelligencePlugin => true;

    public string[] RequiredCapabilities => new[]
    {
        "intelligence.embeddings.generate"
    };
}
```

### Graceful Degradation Patterns

| Feature | With Intelligence | Without Intelligence (Fallback) |
|---------|-------------------|--------------------------------|
| Semantic Deduplication | Embedding-based similarity | Content hash deduplication |
| AI-Driven Tiering | Predicted access patterns | Rule-based tiering |
| Natural Language Queries | Full NL understanding | Keyword matching |
| Anomaly Detection | ML-based detection | Threshold-based detection |
| Predictive Failure | ML prediction | SMART data only |

### Message Topics for Intelligence Plugin

```csharp
// Standard topics for Intelligence plugin communication
public static class IntelligenceTopics
{
    public const string EmbeddingsGenerate = "intelligence.embeddings.generate";
    public const string EmbeddingsCompare = "intelligence.embeddings.compare";
    public const string NlpParse = "intelligence.nlp.parse";
    public const string NlpIntent = "intelligence.nlp.intent";
    public const string AnomalyDetect = "intelligence.anomaly.detect";
    public const string PredictAccess = "intelligence.predict.access";
    public const string KnowledgeQuery = "intelligence.knowledge.query";
    public const string KnowledgeRegister = "intelligence.knowledge.register";
}
```

---

## RULE 14: Explicit Dependency Documentation

> **ABSOLUTE RULE:** ALL inter-plugin dependencies MUST be documented in TODO.md.

### What Must Be Documented

For EVERY feature/strategy that depends on another plugin:

1. **Which plugin it depends on** (e.g., T93 Encryption → T94 Key Management)
2. **The message bus topic used** (e.g., `keystore.get`, `encryption.encrypt`)
3. **Whether it's a hard or soft dependency**
4. **The fallback behavior** when dependency is unavailable

### Dependency Matrix in TODO.md

See `Metadata/TODO.md` section: **"COMPREHENSIVE INTER-PLUGIN DEPENDENCY MATRIX"**

This matrix documents:
- Hard dependencies (→) - Feature fails without it
- Soft dependencies (⇢) - Graceful degradation possible
- Message topics used for communication
- Fallback behavior

### When Adding New Features

Before implementing any feature that uses another plugin:

1. **Check the dependency matrix** in TODO.md
2. **Add your feature** to the matrix if not already there
3. **Implement graceful degradation** for soft dependencies
4. **Document the message topics** your feature uses

```csharp
/// <summary>
/// Encrypts data using the specified cipher.
/// </summary>
/// <remarks>
/// <b>HARD DEPENDENCY:</b> T94 Ultimate Key Management
/// <b>MESSAGE TOPIC:</b> keystore.get
/// <b>FALLBACK:</b> None - encryption requires keys
/// </remarks>
public class AesGcmStrategy : IEncryptionStrategy
{
    // Implementation...
}
```

## Interfaces vs Base Classes

**Keep interfaces minimal** - they define contracts.
**Base classes do the work** - default implementations, metadata, AI integration.

Plugins extend base classes which implement interfaces:
```csharp
// DON'T do this
class MyStorage : IStorageProvider { ... }

// DO this
class MyStorage : StorageProviderPluginBase { ... }
```

---

## MICROKERNEL ARCHITECTURE REFACTOR (IN PROGRESS)

### Current Status
We are in the middle of a major architectural refactor to fully implement the microkernel + plugins pattern. This involves:
1. Creating SDK base classes for all plugin categories (✅ COMPLETE)
2. Implementing 108 individual plugins that extend these base classes (IN PROGRESS)
3. Removing duplicate code from SDK/Kernel after plugins are verified

### Key Files for Refactor
- `Metadata/TODO.md` - Master task list with all 108 plugins by category
- `Metadata/REFACTOR_STATUS.md` - Quick status snapshot for session continuity
- `DataWarehouse.SDK/Contracts/InfrastructurePluginBases.cs` - Infrastructure base classes
- `DataWarehouse.SDK/Contracts/FeaturePluginInterfaces.cs` - Feature plugin base classes
- `DataWarehouse.SDK/Contracts/OrchestrationInterfaces.cs` - Orchestration base classes
- `DataWarehouse.SDK/Contracts/PluginBase.cs` - Core plugin base classes

### Plugin Base Class Hierarchy (Extended)

```
PluginBase (IPlugin)
├── DataTransformationPluginBase (IDataTransformation)
│   └── PipelinePluginBase (runtime ordering)
├── StorageProviderPluginBase (IStorageProvider)
│   └── ListableStoragePluginBase → TieredStoragePluginBase → CacheableStoragePluginBase → IndexableStoragePluginBase
├── MetadataIndexPluginBase (IMetadataIndex)
├── FeaturePluginBase (IFeaturePlugin)
│   ├── InterfacePluginBase (REST, gRPC, SQL)
│   ├── ConsensusPluginBase (IConsensusEngine)
│   ├── RealTimePluginBase (IRealTimeProvider)
│   ├── DeduplicationPluginBase (IDeduplicationProvider)
│   ├── VersioningPluginBase (IVersioningProvider)
│   ├── SnapshotPluginBase (ISnapshotProvider)
│   ├── TelemetryPluginBase (ITelemetryProvider)
│   ├── ThreatDetectionPluginBase (IThreatDetectionProvider)
│   ├── BackupPluginBase (IBackupProvider)
│   ├── OperationsPluginBase (IOperationsProvider)
│   ├── HealthProviderPluginBase (IHealthCheck)
│   ├── RateLimiterPluginBase (IRateLimiter)
│   ├── CircuitBreakerPluginBase (IResiliencePolicy)
│   ├── TransactionManagerPluginBase (ITransactionManager)
│   ├── RaidProviderPluginBase (IRaidProvider)
│   ├── ErasureCodingPluginBase (IErasureCodingProvider)
│   ├── ComplianceProviderPluginBase (IComplianceProvider)
│   ├── IAMProviderPluginBase (IIAMProvider)
│   ├── SearchProviderPluginBase (ISearchProvider)
│   ├── ContentProcessorPluginBase (IContentProcessor)
│   ├── WriteFanOutOrchestratorPluginBase (IWriteFanOutOrchestrator)
│   └── WriteDestinationPluginBase (IWriteDestination)
├── SecurityProviderPluginBase
│   └── AccessControlPluginBase
├── OrchestrationProviderPluginBase
├── IntelligencePluginBase (AI providers)
├── CloudEnvironmentPluginBase (ICloudEnvironment)
├── ReplicationPluginBase (IReplicationService)
├── SerializerPluginBase (ISerializer)
├── SemanticMemoryPluginBase (ISemanticMemory)
├── MetricsPluginBase (IMetricsProvider)
├── GovernancePluginBase (INeuralSentinel)
└── ContainerManagerPluginBase (IContainerManager)
```

### Next Steps (When Resuming)
1. Read `Metadata/REFACTOR_STATUS.md` for current state
2. Read `Metadata/TODO.md` for full task list
3. Continue with **Priority 1: Core Infrastructure** plugins:
   - CircuitBreakerPlugin, RateLimiterPlugin, HealthMonitorPlugin
   - SamlIamPlugin, OAuthIamPlugin
   - GdprCompliancePlugin, HipaaCompliancePlugin

### Plugin Implementation Pattern
```csharp
// Example: Creating a new compliance plugin
namespace DataWarehouse.Plugins.Compliance.Gdpr;

public class GdprCompliancePlugin : ComplianceProviderPluginBase
{
    public override string Id => "com.datawarehouse.compliance.gdpr";
    public override string Name => "GDPR Compliance";
    protected override string SemanticDescription => "GDPR data protection compliance validation";
    protected override string[] SemanticTags => new[] { "compliance", "gdpr", "privacy", "eu" };

    // Implement abstract methods from base class...
}
```

### Directory Structure for New Plugins
```
Plugins/
└── DataWarehouse.Plugins.{Category}/
    ├── DataWarehouse.Plugins.{Category}.csproj
    └── {PluginName}Plugin.cs
```

---

## IMPLEMENTATION WORKFLOW

### Before Starting Any Task

1. Read `Metadata/TODO.md` for the task list
2. Read this file (`Metadata/CLAUDE.md`) for architecture and style guidelines
3. Read `Metadata/RULES.md` for the 12 Absolute Rules
4. Plan implementation according to rules and style guidelines (minimize code duplication, maximize reuse)
5. Implement with full production readiness (no simulations, placeholders, mocks, simplifications, or shortcuts)
6. Add XML documentation for all public entities
7. Add test cases for each feature
8. Update TODO.md with completion status
9. Commit with descriptive message

### Commit Strategy

1. Verify implemented code is fully production-ready
2. If verification fails, continue implementation until it passes
3. Only after passing verification, update TODO.md
4. Commit changes with descriptive message referencing task ID
5. Move to next task

**Note:** Commit after each task. Do NOT wait for an entire phase to complete.

### Production Readiness Checklist

For each feature/plugin, verify:
- [ ] Full error handling with try-catch blocks
- [ ] Input validation for all public methods
- [ ] Thread-safe operations where concurrency is possible
- [ ] Resource disposal (IDisposable pattern)
- [ ] Retry logic for transient failures
- [ ] Graceful degradation when dependencies unavailable
- [ ] XML documentation on ALL public APIs
- [ ] Unit tests covering main functionality

---

## CORE DESIGN PRINCIPLE: Maximum User Configurability

> **PHILOSOPHY:** DataWarehouse provides every capability in a fully configurable way.
> Users have complete freedom to pick, choose, and apply features exactly as they need.

### User Freedom Examples

| Feature | User Options | Implementation Requirement |
|---------|--------------|---------------------------|
| **Encryption** | Always, at-rest only, transit-only, none, or hardware | Per-operation encryption mode selection |
| **Compression** | At rest, during transit, both, or none | Per-operation compression mode selection |
| **WORM Retention** | 1 day to forever | Configurable retention periods |
| **Cipher Selection** | AES, ChaCha20, Serpent, Twofish, FIPS-only, custom | User-selectable algorithms |
| **Key Management** | Direct keys, envelope encryption, HSM-backed, hybrid | Multiple key management modes |
| **Pipeline Order** | User-defined stage ordering | Runtime pipeline configuration |

### Plugin Configurability Checklist

When implementing ANY plugin, verify:
1. [ ] **Configurable Behavior**: Can users enable/disable the feature entirely?
2. [ ] **Selectable Options**: Can users choose between multiple implementations/algorithms?
3. [ ] **Customizable Parameters**: Can users tune numeric values?
4. [ ] **Per-Operation Override**: Can users override defaults on a per-call basis?
5. [ ] **Policy Integration**: Can administrators set organization-wide defaults?
6. [ ] **No Forced Behavior**: Does the plugin avoid forcing unrequested behavior?

---

## PLUGIN IMPLEMENTATION CHECKLIST

For each new plugin:
1. [ ] Create plugin project in `Plugins/DataWarehouse.Plugins.{Name}/`
2. [ ] Implement plugin class extending appropriate base class
3. [ ] Add XML documentation for all public members
4. [ ] Register plugin in solution file `DataWarehouse.slnx`
5. [ ] Add unit tests
6. [ ] Update TODO.md with completion status

### Key Management Plugin Requirements

For each new KEY MANAGEMENT plugin:
- [ ] Extends `KeyStorePluginBase` (NOT `SecurityProviderPluginBase`)
- [ ] Implements `LoadKeyFromStorageAsync()`
- [ ] Implements `SaveKeyToStorageAsync()`
- [ ] Implements `InitializeStorageAsync()`
- [ ] Overrides `CacheExpiration` property
- [ ] Overrides `KeySizeBytes` property
- [ ] If HSM: Also implements `IEnvelopeKeyStore`
- [ ] Does NOT duplicate caching logic
- [ ] Does NOT duplicate initialization logic

### Encryption Plugin Requirements

For each new ENCRYPTION plugin:
- [ ] Extends `EncryptionPluginBase` (NOT `PipelinePluginBase`)
- [ ] Implements `EncryptCoreAsync()`
- [ ] Implements `DecryptCoreAsync()`
- [ ] Implements `GenerateIv()`
- [ ] Overrides `KeySizeBytes`, `IvSizeBytes`, `TagSizeBytes`
- [ ] Does NOT duplicate key management logic
- [ ] Does NOT duplicate statistics tracking
- [ ] Works with both `KeyManagementMode.Direct` and `KeyManagementMode.Envelope`

---

## INTER-PLUGIN COMMUNICATION RULES

> **CRITICAL RULE:** Plugins ONLY reference the SDK. All inter-plugin communication uses the message bus.
> If a dependency is unavailable, the feature MUST fail gracefully (fallback or clear error).

### Dependency Symbols

| Symbol | Meaning |
|--------|---------|
| **->** | Hard dependency (fails without it) |
| **~>** | Soft dependency (graceful degradation) |
| **[M]** | Communication via message bus |
| **[K]** | Key/encryption related |
| **[AI]** | AI/Intelligence related |

### Message Bus Communication Pattern

```csharp
// CORRECT: Request via message bus with graceful fallback
var response = await _messageBus.RequestAsync<EmbeddingResponse>(
    topic: "intelligence.embeddings.generate",
    request: new EmbeddingRequest { Content = content },
    timeout: TimeSpan.FromSeconds(30),
    ct: ct
);

if (!response.Success)
{
    // Graceful degradation
    _logger.LogWarning("Intelligence plugin unavailable, using fallback");
    return await FallbackMethodAsync(content, ct);
}
```

### AI-Dependent Features

Features requiring T90 (Universal Intelligence) MUST:
1. Declare the dependency explicitly in documentation
2. Use message bus to communicate
3. Fail gracefully if unavailable
4. Provide fallback behavior where possible

---

## ULTIMATE/UNIVERSAL PLUGIN CONSOLIDATION RULE

> **CRITICAL RULE:** No task shall create a new standalone plugin if an Ultimate/Universal plugin exists for that category.
> All new functionality MUST be implemented as strategies within the appropriate Ultimate plugin.

### Quick Reference: Which Ultimate Plugin?

| Feature Type | Ultimate Plugin | NOT Individual Plugin |
|--------------|-----------------|----------------------|
| Encryption | `UltimateEncryption` (T93) | ~~AesPlugin~~, ~~ChaCha20Plugin~~ |
| Key Management | `UltimateKeyManagement` (T94) | ~~FileKeyStorePlugin~~, ~~VaultPlugin~~ |
| Compression | `UltimateCompression` (T92) | ~~BrotliPlugin~~, ~~ZstdPlugin~~ |
| Storage | `UltimateStorage` (T97) | ~~S3Storage~~, ~~LocalStorage~~ |
| RAID | `UltimateRAID` (T91) | ~~StandardRaidPlugin~~, ~~ZfsRaidPlugin~~ |
| Access Control | `UltimateAccessControl` (T95) | ~~IntegrityPlugin~~, ~~WormPlugin~~ |
| Compliance | `UltimateCompliance` (T96) | ~~GdprPlugin~~, ~~HipaaPlugin~~ |
| Replication | `UltimateReplication` (T98) | ~~GeoReplicationPlugin~~ |
| AI/Knowledge | `UniversalIntelligence` (T90) | ~~AIAgentsPlugin~~ |
| Observability | `UniversalObservability` (T100) | ~~PrometheusPlugin~~ |
| Interface | `UltimateInterface` (T109) | ~~RestInterfacePlugin~~ |

### Strategy Pattern Implementation

All Ultimate plugins use the Strategy Pattern for extensibility:

```csharp
public interface IEncryptionStrategy
{
    string AlgorithmId { get; }              // "aes-256-gcm", "chacha20-poly1305"
    string DisplayName { get; }
    EncryptionCapabilities Capabilities { get; }

    Task<byte[]> EncryptAsync(byte[] data, EncryptionKey key, CancellationToken ct);
    Task<byte[]> DecryptAsync(byte[] data, EncryptionKey key, CancellationToken ct);
}
```

New algorithms are added as strategies, NOT as new plugins.
