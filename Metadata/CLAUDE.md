# CLAUDE.md - AI Assistant Context for DataWarehouse

## Project Overview

DataWarehouse is a production-grade, AI-native data warehouse SDK built in C#. It uses a plugin-based architecture where all components communicate via messages rather than direct function calls.

## Repository Structure

```
DataWarehouse/
├── DataWarehouse.SDK/           # Core SDK library
│   ├── AI/                      # AI integration components
│   ├── Attributes/              # Custom attributes (e.g., PluginPriority)
│   ├── Contracts/               # Interface contracts
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

### Plugin Categories and Base Classes
All plugins MUST extend category-specific abstract base classes:

| Category | Base Class | Purpose |
|----------|-----------|---------|
| Storage | `StorageProviderBase` | S3, Local, IPFS storage |
| Features | `FeaturePluginBase` | Tiering, Caching |
| Interface | `InterfacePluginBase` | REST, SQL, gRPC |
| Metadata | `MetadataProviderBase` | SQLite, Postgres |
| Intelligence | `IntelligencePluginBase` | AI/Governance |
| Orchestration | `OrchestrationPluginBase` | Raft consensus |
| Security | `SecurityProviderBase` | Security/ACL |
| Pipeline | `PipelinePluginBase` | GZip, AES |

### Plugin Directory Structure
```
Plugins/DataWarehouse.Plugins.{Category}.{Name}/
  Bootstrapper/Init.cs      # Plugin entry point
  Engine/{Name}Engine.cs    # Core logic (stateless)
  Service/{Name}Service.cs  # Optional: stateful services
  Models/{Name}Models.cs    # Optional: data models
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

### AI-Native Requirements
Every component must include:
- Semantic descriptions in natural language
- Semantic tags for categorization
- Performance profiles for optimization
- Usage examples for learning
- Standardized parameter schemas (JSON Schema)

## The 12 Absolute Rules (Summary)

See `RULES.md` for full details. Key points:

1. **Production-Ready** - Full error handling, validation, thread safety
2. **Comprehensive Documentation** - XML docs on ALL public APIs
3. **Maximum Code Reuse** - No duplication, use base classes
4. **Message-Based Architecture** - No direct function calls
5. **Standardized Plugin Architecture** - Follow directory structure
6. **CategoryBase Classes** - ALWAYS extend base classes, never implement interfaces directly
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
- Direct function calls between components

## Build and Development

- Solution file: `DataWarehouse.slnx`
- SDK project: `DataWarehouse.SDK/DataWarehouse.SDK.csproj`
- Target framework: See project file for current target

## Common Tasks

### Adding a New Plugin
1. Create directory: `Plugins/DataWarehouse.Plugins.{Category}.{Name}/`
2. Add `Bootstrapper/Init.cs` extending appropriate `CategoryBase`
3. Implement `Engine/{Name}Engine.cs` with core logic
4. Override semantic properties (description, tags)
5. Update TODO.md with task status

### Modifying SDK Components
1. Follow existing patterns in the relevant folder
2. Add XML documentation to all public members
3. Ensure thread safety where applicable
4. Add appropriate logging and error handling
