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
