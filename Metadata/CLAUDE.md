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
