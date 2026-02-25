# Architecture

**Analysis Date:** 2026-02-10

## Pattern Overview

**Overall:** Plugin-based modular kernel with layered abstraction - CLI/Dashboard → Shared Commands → Kernel → Plugin Registry + Storage

**Key Characteristics:**
- Plugin-based extensibility with dynamic loading from assemblies
- Kernel-centric orchestration for all operations
- Message bus for inter-plugin communication
- Pipeline-based data transformations with runtime configuration
- Abstracted storage layer supporting multiple providers
- Command pattern for all user-facing operations
- Feature-based capability discovery and command filtering

## Layers

**Presentation Layer:**
- Purpose: User-facing interfaces (CLI, Dashboard, GUI)
- Location: `DataWarehouse.CLI/`, `DataWarehouse.Dashboard/`, `DataWarehouse.GUI/`
- Contains: Command builders, shell completions, interactive modes, API controllers, UI components
- Depends on: DataWarehouse.Shared (commands, services)
- Used by: End users via CLI/web/desktop

**Command/Intent Layer:**
- Purpose: Parse user input (structured CLI or natural language) into command intents
- Location: `DataWarehouse.CLI/Program.cs` (natural language processor), `DataWarehouse.Shared/Commands/`
- Contains: CommandExecutor, individual command implementations (storage, backup, plugin, health, etc.), CommandResult models
- Depends on: DataWarehouse.Kernel, DataWarehouse.SDK (contracts)
- Used by: CLI/Dashboard to execute operations

**Shared Services Layer:**
- Purpose: Cross-cutting business logic and instance management
- Location: `DataWarehouse.Shared/`
- Contains: InstanceManager, CapabilityManager, CommandExecutor, CommandHistory, UndoManager, CommandRecorder, NaturalLanguageProcessor
- Depends on: DataWarehouse.Kernel, DataWarehouse.SDK
- Used by: All entry points (CLI, Dashboard)

**Kernel/Orchestration Layer:**
- Purpose: Central runtime orchestration - plugin management, messaging, pipeline execution
- Location: `DataWarehouse.Kernel/`
- Contains: DataWarehouseKernel (main), PluginRegistry, DefaultMessageBus, DefaultPipelineOrchestrator, KernelConfiguration
- Depends on: DataWarehouse.SDK contracts and primitives
- Used by: Shared services, all plugins

**Plugin System:**
- Purpose: Modular feature extensions - storage, compression, encryption, UI integrations, monitoring
- Location: `Plugins/` (140+ plugins), `DataWarehouse.Kernel/Plugins/`
- Contains: IPlugin implementations by category (storage, transformation, feature, monitoring)
- Depends on: DataWarehouse.SDK contracts
- Used by: Kernel via PluginRegistry

**SDK/Contracts Layer:**
- Purpose: Unified interfaces and primitives for plugins and kernel
- Location: `DataWarehouse.SDK/`
- Contains: IPlugin, IDataWarehouse, IStorageProvider, IDataTransformation, message contracts, AI interfaces, connectors, compliance models
- Depends on: Nothing (bottom layer)
- Used by: Kernel, all plugins

**Storage Abstraction:**
- Purpose: Pluggable storage backends
- Location: `DataWarehouse.Kernel/Storage/`, `Plugins/DataWarehouse.Plugins.*/`
- Contains: IStorageProvider implementation, KernelStorageService, InMemoryStoragePlugin
- Depends on: SDK contracts
- Used by: Kernel, pipeline stages for persistence

## Data Flow

**Command Execution Flow:**

1. User input (CLI flags, natural language, web request)
2. CLI/Dashboard parses input → CommandIntent
3. CommandExecutor resolves command by name
4. CommandExecutor.ExecuteAsync(commandName, parameters)
5. Specific command (StorageCommands, BackupCommands, etc.) executes
6. Command sends messages to Kernel via PluginRegistry or directly interacts with storage
7. Plugins process messages (compression, encryption, replication, monitoring)
8. Results returned through pipeline as CommandResult
9. Renderer formats output (JSON, table, YAML, CSV)

**Data Transformation Pipeline:**

1. Input stream → ExecuteWritePipelineAsync(stream, PipelineContext)
2. PipelineOrchestrator iterates WriteStages in configured order
3. Default: Compression → Encryption (runtime-overridable)
4. Each IDataTransformation stage processes stream sequentially
5. Output stream written to storage provider
6. On read: ExecuteReadPipelineAsync reverses stages (Decompression ← Decryption)

**Message/Event Flow:**

1. Kernel publishes system events (SystemStartup, PluginLoaded, SystemShutdown)
2. Plugins subscribe via messageBus.Subscribe(topic, handler)
3. Commands publish domain events (ConfigChanged, DataChanged, etc.)
4. Message topics defined in SDK.Contracts.MessageTopics
5. All messaging is async/non-blocking via DefaultMessageBus

**State Management:**

- Kernel maintains: PluginRegistry, PipelineOrchestrator state, storage providers
- Shared services maintain: InstanceManager (connection state), CapabilityManager (feature availability)
- Plugins maintain their own state via IKernelStorageService
- CommandHistory records executed commands for undo/replay
- NaturalLanguageProcessor persists learning to `%APPDATA%/DataWarehouse/CLI/learning.json`

## Key Abstractions

**DataWarehouseKernel (IDataWarehouse):**
- Purpose: Central orchestrator for all kernel operations
- Examples: `DataWarehouse.Kernel/DataWarehouseKernel.cs`
- Pattern: Singleton-like, initialized async, manages lifecycle of plugins and storage

**ICommand:**
- Purpose: Unified command execution contract
- Examples: `DataWarehouse.Shared/Commands/StorageCommands.cs`, `BackupCommands.cs`, `PluginCommands.cs`
- Pattern: Each command implements ExecuteAsync(context, parameters), RequiredFeatures list, metadata

**IPlugin (with subtypes):**
- Purpose: Extensibility contract for all plugin types
- Examples: IStorageProvider, IDataTransformation, IFeaturePlugin, IMonitoringPlugin
- Pattern: Async handshake, message handling, optional feature plugins auto-started in background

**IPipelineOrchestrator:**
- Purpose: Manage data transformation chains
- Examples: `DataWarehouse.Kernel/Pipeline/PipelineOrchestrator.cs`
- Pattern: Stages registered dynamically, configuration stored in PipelineConfiguration, default is Compress→Encrypt

**IStorageProvider:**
- Purpose: Abstract storage backend
- Examples: InMemoryStoragePlugin, filesystem plugins, cloud plugins
- Pattern: Stream-based I/O, metadata support, list/exists operations

**InstanceManager:**
- Purpose: Manage local and remote DataWarehouse instances
- Examples: `DataWarehouse.Shared/InstanceManager.cs`
- Pattern: ConnectLocal, ConnectRemote, ConnectInProcess for different deployment modes

## Entry Points

**CLI:**
- Location: `DataWarehouse.CLI/Program.cs` (Main method)
- Triggers: Command-line invocation
- Responsibilities: Parse args, route to interactive/natural-language/structured modes, show welcome screen

**Web Dashboard:**
- Location: `DataWarehouse.Dashboard/`
- Triggers: HTTP requests to /api/*, SignalR hub connections
- Responsibilities: REST API controllers, real-time updates via SignalR hubs

**GUI Desktop:**
- Location: `DataWarehouse.GUI/`
- Triggers: Application launch
- Responsibilities: Blazor/WinForms components, local service integration

**Plugin Loader:**
- Location: `DataWarehouse.Kernel/Plugins/PluginLoader.cs`, `DataWarehouseKernel.LoadPluginsFromPathsAsync()`
- Triggers: Kernel initialization, optional manual plugin load command
- Responsibilities: Discover assemblies, reflect IPlugin implementations, instantiate and handshake

## Error Handling

**Strategy:** Layered error handling with user-friendly messages at presentation layer

**Patterns:**
- CommandResult.Fail(message, exception) returns structured error to renderer
- Plugin handshakes return HandshakeResponse.Success=false with ErrorMessage
- CancellationToken propagates from CLI through command execution
- Background jobs catch and log exceptions without crashing kernel
- Storage operations wrapped in try-catch with fallback behavior
- Pipeline validation throws InvalidOperationException on bad configuration

## Cross-Cutting Concerns

**Logging:**
- ILogger<T> from Microsoft.Extensions.Logging
- Kernel logger passed to components during initialization
- Plugin loader catches exceptions and logs per-assembly failures

**Validation:**
- CommandResult validation for command parameters
- PipelineConfiguration.ValidateConfiguration() checks stage availability
- Capability checking in CommandExecutor.ExecuteAsync prevents unavailable commands

**Authentication:**
- Not implemented in core (optional via plugins)
- Instance profiles support local/remote connections
- Environment variables for AI provider keys (OPENAI_API_KEY, ANTHROPIC_API_KEY)

---

*Architecture analysis: 2026-02-10*
