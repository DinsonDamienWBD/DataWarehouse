# Codebase Structure

**Analysis Date:** 2026-02-10

## Directory Layout

```
DataWarehouse/
├── DataWarehouse.CLI/              # Command-line interface entry point
│   ├── Program.cs                  # Main entry, command building, natural language
│   ├── Commands/                   # CLI-specific command extensions
│   ├── ShellCompletions/           # Bash, Zsh, Fish, PowerShell completions
│   └── Integration/                # Instance connection logic
├── DataWarehouse.Dashboard/        # Web ASP.NET Core dashboard
│   ├── Controllers/                # REST API endpoints
│   ├── Hubs/                       # SignalR real-time updates
│   ├── Services/                   # Dashboard-specific services
│   ├── Models/                     # View/API models
│   ├── Pages/                      # Razor pages
│   ├── Middleware/                 # Auth, logging middleware
│   └── wwwroot/                    # Static assets
├── DataWarehouse.GUI/              # Desktop GUI (Blazor/WinForms)
│   ├── Components/                 # UI components
│   └── Services/                   # GUI services
├── DataWarehouse.Kernel/           # Core orchestration engine
│   ├── DataWarehouseKernel.cs      # Main kernel orchestrator (IDataWarehouse)
│   ├── Configuration/              # KernelConfiguration, KernelBuilder
│   ├── Messaging/                  # DefaultMessageBus, message routing
│   ├── Pipeline/                   # PipelineOrchestrator, stages, config
│   ├── Plugins/                    # PluginLoader, InMemoryStoragePlugin
│   ├── Storage/                    # KernelStorageService, ContainerManager
│   ├── Infrastructure/             # KernelLogger, KernelContext, MemoryPressureMonitor
│   └── PluginRegistry.cs           # Plugin discovery and management
├── DataWarehouse.Shared/           # Shared business logic across all UIs
│   ├── Commands/                   # Command implementations (storage, backup, plugin, health, etc.)
│   │   ├── CommandExecutor.cs      # Central command dispatcher
│   │   ├── StorageCommands.cs      # Storage pool operations
│   │   ├── BackupCommands.cs       # Backup/restore operations
│   │   ├── PluginCommands.cs       # Plugin enable/disable/reload
│   │   ├── HealthCommands.cs       # Health/metrics monitoring
│   │   ├── RaidCommands.cs         # RAID configuration
│   │   ├── AuditCommands.cs        # Audit log operations
│   │   ├── ConfigCommands.cs       # Configuration management
│   │   ├── BenchmarkCommands.cs    # Performance benchmarks
│   │   ├── SystemCommands.cs       # System info, capabilities
│   │   ├── RecordCommands.cs       # Command recording for replay
│   │   └── UndoCommands.cs         # Undo/rollback support
│   ├── Services/                   # Shared service implementations
│   │   ├── InstanceManager.cs      # Local/remote instance connection
│   │   ├── CapabilityManager.cs    # Feature availability detection
│   │   ├── CommandHistory.cs       # Command history for interactive mode
│   │   ├── CommandRecorder.cs      # Session recording
│   │   ├── UndoManager.cs          # Undo/rollback state management
│   │   ├── NaturalLanguageProcessor.cs  # Intent parsing + AI fallback
│   │   └── ConsoleRenderer.cs      # Output formatting
│   ├── Models/                     # Shared data models
│   ├── Integration/                # Cross-boundary integration
│   └── MessageBridge.cs            # Kernel message publishing
├── DataWarehouse.SDK/              # Plugin contract library
│   ├── Contracts/                  # Core interfaces (IPlugin, IDataWarehouse, etc.)
│   │   ├── IPlugin.cs              # Base plugin contract
│   │   ├── IDataWarehouse.cs       # Kernel interface
│   │   ├── IStorageProvider.cs     # Storage abstraction
│   │   ├── IDataTransformation.cs  # Pipeline stage contract
│   │   ├── IFeaturePlugin.cs       # Background feature plugins
│   │   ├── MessageTopics.cs        # Standard message topic names
│   │   └── (140+ plugin base classes by category)
│   ├── Primitives/                 # Core value types
│   │   ├── PluginMessage.cs        # Message structure
│   │   ├── PluginCategory.cs       # Enum: Storage, Transformation, Monitoring, etc.
│   │   ├── OperatingMode.cs        # Enum: Workstation, Server, Edge, Cloud
│   │   └── PipelineConfiguration.cs # Pipeline stage ordering
│   ├── Services/                   # Service interfaces
│   │   ├── IAIProvider.cs          # AI integration contract
│   │   └── ISemanticAnalyzer.cs    # Semantic analysis
│   ├── AI/                         # AI/ML supporting code
│   │   ├── IAIProvider.cs
│   │   ├── SemanticAnalyzerBase.cs
│   │   ├── VectorOperations.cs
│   │   └── KnowledgeObject.cs
│   ├── Connectors/                 # Data connector strategies
│   ├── Security/                   # Encryption, auth contracts
│   ├── Storage/                    # Storage-related contracts
│   ├── Compliance/                 # Compliance automation contracts
│   ├── Configuration/              # Fault tolerance, load balancing configs
│   ├── Extensions/                 # LINQ extensions
│   ├── Utilities/                  # Helper utilities
│   ├── Attributes/                 # Plugin metadata attributes
│   └── Distribution/               # Distribution/replication contracts
├── DataWarehouse.Tests/            # Test suite (organized by domain)
│   ├── Kernel/                     # Kernel functionality tests
│   ├── Messaging/                  # Message bus tests
│   ├── Pipeline/                   # Pipeline orchestration tests
│   ├── Storage/                    # Storage provider tests
│   ├── Integration/                # End-to-end integration tests
│   ├── Dashboard/                  # Dashboard controller tests
│   ├── Compliance/                 # Compliance feature tests
│   ├── Database/                   # Database connector tests
│   ├── Replication/                # Replication tests
│   ├── Hyperscale/                 # Large-scale operation tests
│   ├── Infrastructure/             # Infrastructure layer tests
│   └── Telemetry/                  # Monitoring/observability tests
├── DataWarehouse.Benchmarks/       # Performance benchmarks (BenchmarkDotNet)
├── DataWarehouse.Launcher/         # Application launcher/bootstrapper
│   ├── Adapters/                   # OS-specific launchers
│   └── Integration/                # Kernel initialization
├── Plugins/                        # 140+ optional plugin assemblies
│   ├── DataWarehouse.Plugins.AccessLog/          # Access logging
│   ├── DataWarehouse.Plugins.AccessPrediction/   # ML-based predictions
│   ├── DataWarehouse.Plugins.Alerting/           # Alert management
│   ├── DataWarehouse.Plugins.Compression/        # Compression strategies
│   ├── DataWarehouse.Plugins.Encryption/         # Encryption providers
│   ├── DataWarehouse.Plugins.Storage*/           # Storage backends (filesystem, S3, Azure, etc.)
│   ├── DataWarehouse.Plugins.Raid/               # RAID implementations
│   ├── DataWarehouse.Plugins.Replication/        # Replication strategies (Raft, CRDT, multi-master)
│   ├── DataWarehouse.Plugins.Monitoring/         # Monitoring integrations (Prometheus, Datadog, etc.)
│   ├── DataWarehouse.Plugins.Dashboard/          # Dashboard integrations (Grafana, Kibana, etc.)
│   ├── DataWarehouse.Plugins.Protocol*/          # Protocol adapters (MySQL, PostgreSQL, Oracle, etc.)
│   ├── DataWarehouse.Plugins.AI*/                # AI features (intelligence, interface)
│   └── DataWarehouse.Plugins.Ultimate*/          # Premium feature bundles
├── Metadata/                       # Configuration and documentation
│   ├── Adapter/                    # Adapter configuration
│   │   ├── IKernelAdapter.cs
│   │   ├── AdapterFactory.cs
│   │   └── AdapterRunner.cs
│   ├── CLAUDE.md                   # Claude AI instructions
│   ├── RULES.md                    # Project rules
│   ├── TODO.md                     # Task tracking
│   └── CODE_REVIEW_REPORT.md       # Code review findings
├── .planning/                      # GSD execution planning
│   ├── codebase/                   # This analysis (ARCHITECTURE.md, STRUCTURE.md, etc.)
│   └── phases/                     # Implementation phase plans
├── .github/                        # GitHub workflows and CI/CD
├── Directory.Build.props           # MSBuild properties for all projects
└── DataWarehouse.slnx              # Solution file (newer format)
```

## Directory Purposes

**DataWarehouse.CLI:**
- Purpose: Command-line interface and natural language processor
- Contains: Main entry point, command parsing, shell completions, interactive mode
- Key files: `Program.cs` (150+ lines showing architecture), `InteractiveMode.cs`, `ShellCompletionGenerator.cs`

**DataWarehouse.Dashboard:**
- Purpose: Web-based administration and monitoring dashboard
- Contains: ASP.NET Core REST API controllers, SignalR hubs for real-time updates, Razor pages
- Key files: Controllers for each domain (StorageController, BackupController, etc.), Security middleware

**DataWarehouse.Kernel:**
- Purpose: Core runtime orchestration engine
- Contains: Plugin loading, message bus, pipeline orchestration, storage management
- Key files: `DataWarehouseKernel.cs` (477 lines, main orchestrator), `PipelineOrchestrator.cs`, `PluginRegistry.cs`

**DataWarehouse.Shared:**
- Purpose: Cross-cutting business logic shared by all UIs
- Contains: Command implementations, instance/capability management, command history and undo
- Key files: `CommandExecutor.cs`, all Commands/*.cs files, services in Services/

**DataWarehouse.SDK:**
- Purpose: Plugin contract library and primitives
- Contains: IPlugin hierarchy, message types, configuration models, AI/compliance interfaces
- Key files: `Contracts/IPlugin.cs`, `Contracts/MessageTopics.cs`, `Primitives/PluginMessage.cs`

**DataWarehouse.Tests:**
- Purpose: Comprehensive test coverage
- Contains: Unit tests, integration tests, performance tests organized by domain
- Key files: Tests mirroring production structure (e.g., Tests/Kernel/*, Tests/Pipeline/*)

**Plugins/:**
- Purpose: Optional plugin extensions
- Contains: 140+ plugin implementations providing storage, replication, monitoring, protocol adapters
- Pattern: Each plugin is a separate assembly with its own dependencies

**Metadata/:**
- Purpose: Configuration, documentation, adapter patterns
- Contains: CLAUDE.md (AI instructions), RULES.md (coding rules), adapter pattern definitions

## Key File Locations

**Entry Points:**
- `DataWarehouse.CLI/Program.cs`: CLI main entry (System.CommandLine parsing, natural language processing)
- `DataWarehouse.Dashboard/Program.cs` or startup equivalent: Web dashboard initialization
- `DataWarehouse.GUI/Program.cs`: Desktop GUI initialization
- `DataWarehouse.Launcher/Program.cs`: Application launcher bootstrap

**Configuration:**
- `DataWarehouse.slnx`: Solution manifest (newer format with project definitions)
- `Directory.Build.props`: Shared MSBuild properties, version, copyright
- `DataWarehouse.Kernel/Configuration/KernelConfiguration.cs`: Kernel settings
- `DataWarehouse.Kernel/Configuration/KernelBuilder.cs`: Fluent kernel configuration

**Core Logic:**
- `DataWarehouse.Kernel/DataWarehouseKernel.cs`: Main kernel orchestrator
- `DataWarehouse.Kernel/Pipeline/PipelineOrchestrator.cs`: Pipeline execution engine
- `DataWarehouse.Kernel/Messaging/DefaultMessageBus.cs`: Event messaging
- `DataWarehouse.Kernel/PluginRegistry.cs`: Plugin discovery and management
- `DataWarehouse.Shared/Commands/CommandExecutor.cs`: Command dispatch center

**Testing:**
- `DataWarehouse.Tests/Kernel/`: Kernel unit and integration tests
- `DataWarehouse.Tests/Pipeline/`: Pipeline orchestration tests
- `DataWarehouse.Tests/Integration/`: End-to-end integration tests
- `DataWarehouse.Tests/Storage/`: Storage provider tests

## Naming Conventions

**Files:**
- Classes: `PascalCase.cs` (e.g., `CommandExecutor.cs`, `StorageCommands.cs`)
- Interfaces: `IPascalCase.cs` (e.g., `IPlugin.cs`, `IStorageProvider.cs`)
- Tests: `{TargetClass}Tests.cs` (e.g., `CommandExecutorTests.cs`)

**Directories:**
- Features: Feature name (e.g., `Pipeline/`, `Storage/`, `Messaging/`)
- Categories: Domain name plural (e.g., `Commands/`, `Services/`, `Contracts/`)
- Plugins: `DataWarehouse.Plugins.{FeatureName}` (e.g., `DataWarehouse.Plugins.Raid`)

**Namespaces:**
- Convention: `DataWarehouse.{ProjectName}.{DirectoryPath}`
- Examples: `DataWarehouse.Kernel.Pipeline`, `DataWarehouse.Shared.Commands`, `DataWarehouse.SDK.Contracts`

## Where to Add New Code

**New Feature (e.g., backup management):**
- Primary code: `DataWarehouse.Shared/Commands/BackupCommands.cs` (command implementation)
- Tests: `DataWarehouse.Tests/Integration/BackupTests.cs`
- Plugin (if needed): `Plugins/DataWarehouse.Plugins.Backup/`
- Command registration: Add to `CommandExecutor.RegisterBuiltInCommands()` in `DataWarehouse.Shared/Commands/CommandExecutor.cs`
- CLI integration: Add command builder method (CreateBackupCommand) to `DataWarehouse.CLI/Program.cs`

**New Plugin:**
- Create directory: `Plugins/DataWarehouse.Plugins.{FeatureName}/`
- Create project file: `DataWarehouse.Plugins.{FeatureName}.csproj`
- Implement interface: Class implementing IPlugin subtype from SDK (e.g., IStorageProvider)
- Update solution: Add project reference to `DataWarehouse.slnx`
- Plugin loading: Kernel auto-discovers from `Plugins/` directory on init

**New Command:**
- Implementation: `DataWarehouse.Shared/Commands/{DomainName}Commands.cs`
- Command class: Inherit from `CommandBase` or implement `ICommand` directly
- Registration: Add to executor's `RegisterBuiltInCommands()` or lazy register in `Resolve()`
- CLI mapping: Add command builder method to `DataWarehouse.CLI/Program.cs` rootCommand
- Tests: Create corresponding test in `DataWarehouse.Tests/`

**Utilities:**
- Shared helpers: `DataWarehouse.SDK/Utilities/` (accessible to all)
- Service-specific: `DataWarehouse.Shared/Services/` (available to commands)
- Plugin-specific: Within the plugin's own namespace

**Configuration:**
- Kernel config: `DataWarehouse.Kernel/Configuration/KernelConfiguration.cs`
- Pipeline config: `DataWarehouse.SDK/Primitives/PipelineConfiguration.cs`
- Feature flags: `Metadata/RULES.md` or environment variables

## Special Directories

**Plugins/:**
- Purpose: Optional plugin assemblies
- Generated: No (manually created)
- Committed: Yes
- 140+ plugins covering storage, replication, monitoring, protocol adapters, AI features

**DataWarehouse.Tests/:**
- Purpose: Test suite organized by domain
- Generated: No (manually written)
- Committed: Yes
- Pattern: Parallel structure to production code

**Metadata/:**
- Purpose: Configuration, documentation, adapter patterns
- Generated: Partially (CLAUDE.md may be generated)
- Committed: Yes
- Contains: RULES.md, TODO.md, CODE_REVIEW_REPORT.md, CLAUDE.md

**.planning/:**
- Purpose: GSD execution planning
- Generated: Yes (by orchestrator)
- Committed: No
- Contains: Phase plans and this codebase analysis

**.github/workflows/:**
- Purpose: CI/CD pipeline definitions
- Generated: No (manually configured)
- Committed: Yes

**bin/, obj/:**
- Purpose: Build output
- Generated: Yes
- Committed: No

---

*Structure analysis: 2026-02-10*
