# DataWarehouse SDK Tasks

## Completed - SDK Foundation

### Build Error Fixes
- [x] Fix CS0738: IPlugin.Id type mismatch (string vs Guid)
- [x] Fix CS0246: PluginMessage missing using directive
- [x] Fix CS0246: IExecutionContext missing using directive
- [x] Fix CS0535: PluginBase missing OnMessageAsync implementation

### AI Infrastructure
- [x] Add IAIProvider (AI-agnostic provider interface)
- [x] Add VectorOperations (embeddings, similarity, IVectorStore)
- [x] Add GraphStructures (IKnowledgeGraph, nodes, edges, traversal)
- [x] Add MathUtilities (statistics, normalization, activation functions)

### Pipeline & Messaging
- [x] Add IPipelineOrchestrator (runtime ordering)
- [x] Add IMessageBus (plugin communication)

### Interface Consolidation
- [x] Create base classes for all interfaces (no duplication)
- [x] Interfaces kept minimal - base classes do heavy lifting

## Abstract Base Classes (16 total)

| Class | Implements | Purpose |
|-------|------------|---------|
| `PluginBase` | IPlugin | Core plugin functionality |
| `DataTransformationPluginBase` | IDataTransformation | Compression, Encryption |
| `StorageProviderPluginBase` | IStorageProvider | S3, Local, IPFS |
| `ListableStoragePluginBase` | IListableStorage | Storage with file listing |
| `TieredStoragePluginBase` | ITieredStorage | Hot/Cold/Archive tiering |
| `MetadataIndexPluginBase` | IMetadataIndex | SQLite, Postgres indexing |
| `FeaturePluginBase` | IFeaturePlugin | Lifecycle-managed features |
| `SecurityProviderPluginBase` | - | ACL, Auth, Encryption |
| `OrchestrationProviderPluginBase` | - | Workflow orchestration |
| `IntelligencePluginBase` | - | AI/ML plugins |
| `InterfacePluginBase` | - | REST, gRPC, SQL interfaces |
| `PipelinePluginBase` | - | Pipeline stages (runtime order) |
| `ConsensusPluginBase` | IConsensusEngine | Raft, Paxos consensus |
| `RealTimePluginBase` | IRealTimeProvider | Pub/Sub, Change feeds |
| `CloudEnvironmentPluginBase` | ICloudEnvironment | AWS, Azure, GCP, Local |

## Interfaces (Minimal, kept for contracts)

| Interface | Used By | Purpose |
|-----------|---------|---------|
| `IPlugin` | PluginBase | Core contract |
| `IStorageProvider` | StorageProviderPluginBase | Storage operations |
| `IFeaturePlugin` | FeaturePluginBase | Lifecycle management |
| `IDataTransformation` | DataTransformationPluginBase | Transform operations |
| `IMetadataIndex` | MetadataIndexPluginBase | Index operations |
| `IListableStorage` | ListableStoragePluginBase | File enumeration |
| `ITieredStorage` | TieredStoragePluginBase | Tier management |
| `IConsensusEngine` | ConsensusPluginBase | Distributed consensus |
| `IRealTimeProvider` | RealTimePluginBase | Event publishing |
| `ICloudEnvironment` | CloudEnvironmentPluginBase | Cloud detection |
| `IAIProvider` | IntelligencePluginBase | AI operations |
| `IPipelineOrchestrator` | Kernel | Runtime pipeline ordering |
| `IMessageBus` | Kernel | Plugin communication |
| `IVectorStore` | - | Vector storage |
| `IKnowledgeGraph` | - | Graph operations |

## AI Folder Structure

```
DataWarehouse.SDK/AI/
├── IAIProvider.cs        # AI-agnostic provider interface
├── VectorOperations.cs   # Embeddings, similarity, IVectorStore
├── GraphStructures.cs    # Knowledge graphs, nodes, edges
├── MathUtilities.cs      # Statistics, normalization, activation
└── Runtime/
    └── CapabilityResult.cs
```

## Next Steps (Future)

- [ ] Implement Kernel (orchestrator)
- [ ] Implement default MessageBus
- [ ] Implement default PipelineOrchestrator
- [ ] Create sample plugins
