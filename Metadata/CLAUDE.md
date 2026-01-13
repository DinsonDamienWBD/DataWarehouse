# CLAUDE.md - AI Assistant Context for DataWarehouse

## Project Overview

DataWarehouse is a production-grade, AI-native data warehouse SDK built in C#. It uses a plugin-based architecture where all components communicate via messages rather than direct function calls.

## Repository Structure

```
DataWarehouse/
├── DataWarehouse.SDK/           # Core SDK library
│   ├── AI/                      # AI integration (IAIProvider, models)
│   ├── Attributes/              # Custom attributes (e.g., PluginPriority)
│   ├── Contracts/               # Interface contracts and base classes
│   ├── Extensions/              # Extension methods
│   ├── Governance/              # Governance contracts
│   ├── IO/                      # I/O utilities (streams, adapters)
│   ├── Primitives/              # Core types (Enums, Configuration, Manifest)
│   ├── Security/                # Security contracts and access control
│   ├── Services/                # Core services (PluginRegistry)
│   └── Utilities/               # Helper utilities
├── Metadata/                    # Project documentation
│   ├── CLAUDE.md                # This file - AI context
│   ├── README.md                # Project readme
│   ├── RULES.md                 # The 12 Absolute Rules
│   └── TODO.md                  # Task tracking
└── DataWarehouse.slnx           # Solution file
```

## Key Concepts

### Message-Based Architecture
- Components communicate via `PluginMessage` class, not direct method calls
- All message handling is async (`OnMessageAsync`)
- Responses use standardized `MessageResponse` format
- Use `IMessageBus` for all inter-plugin communication
- Standard topics defined in `MessageTopics` static class

### Plugin Categories and Base Classes
All plugins MUST extend category-specific abstract base classes:

| Category | Base Class | Purpose |
|----------|-----------|---------|
| Storage | `StorageProviderPluginBase` | S3, Local, IPFS storage |
| Transform | `DataTransformationPluginBase` | Compression, Encryption |
| Pipeline | `PipelinePluginBase` | Pipeline stages with runtime ordering |
| Metadata | `MetadataIndexPluginBase` | SQLite, Postgres indexing |
| Features | `FeaturePluginBase` | Active features (SQL Listener) |
| Interface | `InterfacePluginBase` | REST, SQL, gRPC, WebSocket |
| Intelligence | `IntelligencePluginBase` | AI/ML providers |
| Security | `SecurityProviderPluginBase` | ACL, Auth |
| Orchestration | `OrchestrationProviderPluginBase` | Workflow, Consensus |

### Plugin Directory Structure
```
Plugins/DataWarehouse.Plugins.{Category}.{Name}/
  Bootstrapper/Init.cs      # Plugin entry point
  Engine/{Name}Engine.cs    # Core logic (stateless)
  Service/{Name}Service.cs  # Optional: stateful services
  Models/{Name}Models.cs    # Optional: data models
```

## AI-Native Architecture

### AI Provider Abstraction (`IAIProvider`)
- Completely AI/LLM agnostic - supports OpenAI, Claude, Copilot, Ollama, etc.
- New providers can be added without SDK changes
- Capabilities-based discovery (`AICapabilities` enum)
- Streaming support for real-time responses
- Function calling support for tool use

### Key AI Types
```csharp
IAIProvider          // Core provider interface
IAIProviderRegistry  // Runtime registration/discovery
AIRequest/AIResponse // Provider-agnostic request/response
AICapabilities       // Flags: TextCompletion, Embeddings, etc.
```

## Pipeline Orchestration

### Runtime Ordering (`IPipelineOrchestrator`)
- Default order: Compress → Encrypt
- User-overridable at runtime
- Supports new algorithms (e.g., compress-after-encrypt)
- Validation of stage compatibility

### Pipeline Configuration
```csharp
// Default configuration
var config = PipelineConfiguration.CreateDefault();
// Stages: Compression (order=100) → Encryption (order=200)

// User override for special compression
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

## Message Bus

### IMessageBus Interface
- Publish/Subscribe pattern for decoupled communication
- Request/Response pattern for synchronous needs
- Pattern matching subscriptions (e.g., "storage.*")
- Standard topics in `MessageTopics` class

### Standard Topics
```csharp
MessageTopics.StorageSave      // storage.save
MessageTopics.PipelineExecute  // pipeline.execute
MessageTopics.AIQuery          // ai.query
MessageTopics.ConfigChanged    // config.changed
```

## Critical Patterns

### Property Override Pattern (NOT Assignment)
```csharp
// WRONG - causes CS0200 error
public MyPlugin() : base(...) {
    SemanticDescription = "..."; // ERROR!
}

// CORRECT - use property override
protected override string SemanticDescription => "...";
protected override string[] SemanticTags => new[] { "tag1", "tag2" };
```

### Plugin ID Format
```csharp
// Use stable, hierarchical identifiers
public override string Id => "com.company.plugin.compression.gzip";
```

## The 12 Absolute Rules (Summary)

See `RULES.md` for full details. Key points:

1. **Production-Ready** - Full error handling, validation, thread safety
2. **Comprehensive Documentation** - XML docs on ALL public APIs
3. **Maximum Code Reuse** - No duplication, use base classes
4. **Message-Based Architecture** - No direct function calls
5. **Standardized Plugin Architecture** - Follow directory structure
6. **CategoryBase Classes** - ALWAYS extend base classes
7. **AI-Native Integration** - Semantic descriptions, tags, profiles
8. **Error Handling & Resilience** - Retry logic, circuit breakers
9. **Performance & Scalability** - Async I/O, connection pooling, streaming
10. **Testing & Validation** - Dependency injection, testable code
11. **Security & Safety** - Input validation, encryption, audit logging
12. **Task Tracking** - Update TODO.md before/during/after work

## Forbidden Practices

- TODO comments in code
- Placeholders or simulated responses
- Hardcoded test data
- Console logging (use proper logging)
- Empty catch blocks
- Magic numbers
- Generic `Exception` types
- Direct function calls between plugins
- Direct plugin-to-plugin references

## Interfaces (Minimal, Generic, Future-Proof)

| Interface | Purpose |
|-----------|---------|
| `IPlugin` | Core plugin contract (Id, Name, Category, OnMessageAsync) |
| `IKernelContext` | Kernel services access (logging, plugin lookup) |
| `IPluginCapability` | Capability exposure for AI agents |
| `IExecutionContext` | Execution context with security |
| `IAIProvider` | AI-agnostic provider interface |
| `IAIProviderRegistry` | Runtime AI provider management |
| `IPipelineOrchestrator` | Runtime pipeline ordering |
| `IMessageBus` | Inter-plugin communication |
| `IDataTransformation` | Data mutation contract |
| `IStorageProvider` | Storage contract |
| `IMetadataIndex` | Metadata/search contract |
| `IFeaturePlugin` | Lifecycle-managed features |

## Build and Development

- Solution file: `DataWarehouse.slnx`
- SDK project: `DataWarehouse.SDK/DataWarehouse.SDK.csproj`

## Common Tasks

### Adding a New Plugin
1. Create directory: `Plugins/DataWarehouse.Plugins.{Category}.{Name}/`
2. Add `Bootstrapper/Init.cs` extending appropriate base class
3. Implement required abstract members
4. Override semantic properties (description, tags)
5. Update TODO.md with task status

### Adding a New AI Provider
1. Implement `IAIProvider` interface
2. Register with `IAIProviderRegistry` at startup
3. No SDK changes required

### Adding a New Pipeline Stage
1. Extend `PipelinePluginBase`
2. Set `SubCategory`, `DefaultOrder`, dependencies
3. Implement `OnWrite` and `OnRead` transformations
4. Register with `IPipelineOrchestrator`
