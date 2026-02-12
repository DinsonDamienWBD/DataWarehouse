PluginBase  →   The Lowest abstract base class for all plugins.
            →   Contains the base lifecycle and common functionality for all plugins. Already fully implemented in a 100% production ready state.
            →   Implements methods such as initialize(), execute(), and shutdown().
            →   Also implements common error handling and logging mechanisms.
            →   Also implements Capability registry and Knowledge Registry & the Capability & knowledge registeration process for all plugins.
            →   UltimateIntelligence plugin inherits/extends this base class (so, it also can register its capabilities and knowledge into the system knowledge bank).

IntelligentPluginBase   →   Next level abstract base class for all plugins.   
                        →   Inherits/Extends from PluginBase and implements socket into which UltimateIntelligence can plug in.
                        →   This class is an abstract base class for all intelligent plugins that require integration with UltimateIntelligence.
                        →   It defines the interface and lifecycle methods specific to intelligent plugins, such as connect_to_ultimate_intelligence() and disconnect_from_ultimate_intelligence().
                        →   It also implements additional error handling and logging mechanisms specific to intelligent plugins.
                        →   It allows graceful degradation if UltimateIntelligence is not available. Manual mode ALWAYS works.
                        →   This allows every plugin that inherits from IntelligentPluginBase to seamlessly integrate with UltimateIntelligence while still adhering to the core functionalities defined in PluginBase.
                        →   This one base class allows all other plugins to integrate with UltimateIntelligence without code duplication, with support for ALL AI features like NLP, Semantics, Context Awareness, Learning, Reasoning, Planning, Decision Making, Problem Solving, Creativity, Adaptability, Emotional Intelligence, Social Intelligence, and everything else UltimateIntelligence offers.

Feature specific plugin classes 
(e.g.,  EncryptionPluginBase, 
        CompressionPluginBase, 
        StoragePluginBase, etc.)    →   Next level abstract base classes for specific feature plugins.   
                                    →   Inherit/Extend from IntelligentPluginBase.
                                    →   Each of these classes implements feature-specific COMMON functionality while leveraging the integration capabilities provided by IntelligentPluginBase.
                                    →   This structure ensures that all intelligent plugins can seamlessly interact with UltimateIntelligence while maintaining their unique COMMON features and behaviors as standardized through this base class.

Ultimate Plugins    →   Concrete implementations of specific feature plugins.   
                    →   Inherit/Extend from their respective feature-specific plugin base classes (e.g., UltimateEncryptionPlugin inherits from EncryptionPluginBase).
                    →   These classes implement the specific unique functionalities and behaviors required for their respective features while also automatically leveraging the intelligent capabilities provided by IntelligentPluginBase.
                    →   This structure allows for a clear separation of concerns, where each UltimateXXX plugin can focus on its unique functionalities while still benefiting from the Knowledge bank, Capability register and the intelligece integration with UltimateIntelligence.

This refactored structure ensures a clean and maintainable codebase by promoting code reuse and reducing duplication. It also allows for easier future enhancements and modifications, as changes to the core intelligent functionalities can be made in IntelligentPluginBase without affecting the individual UltimateXXX plugins directly.

# Tasks
## Phase 1: Refactor PluginBase and Create IntelligentPluginBase
- [ ] Upgrade PluginBase class with all common functionalities.
- [ ] Make sure PluginBase class has Knowledge Registry & Capability Registry and the necessary registeration methods implemented.
- [ ] Make sure UltimateIntelligence plugin inherits from PluginBase.
- [ ] Create IntelligentPluginBase class that extends PluginBase.
- [ ] Implement socket for UltimateIntelligence in IntelligentPluginBase.
- [ ] Implement graceful degradation in IntelligentPluginBase for when UltimateIntelligence is not available.
- [ ] Create feature-specific plugin base classes (e.g., EncryptionPluginBase, CompressionPluginBase, StoragePluginBase) that inherit from IntelligentPluginBase.
- [ ] Implement common functionalities for each feature-specific plugin base class.
- [ ] Build and verify that these new mechanisms work seamlessly and efficiently within the SDK.

## Phase 2: Refactor StratergyBase
- [ ] Verify and if necessary, refactor the StratergyBase class in a similar way
- [ ] Implement common functionalities in the abstracted base stratergy class
- [ ] Implement a multi-tier base class structure similar to PluginBase, each tier adding more extended but common functionalities
- [ ] Finally, implement the plugin stratergy specific base classes
- [ ] Build and verify that these new mechanisms work seamlessly and efficiently within the SDK.

## Phase 3: Update SDK to support Auto-scaling, Load Balancing, P2P, Auto-Sync (Online & Offline - Air-Gapped), Auto-Tier, Auto-Governance
- [ ] Auto-scaling and load balancing support: User can deploy DataWarehouse on a laptop, slowly as their storage capacity needs grow, DataWarehouse can automatically prompt the user to allow it to 'grow', and ask the user to provide a network/server layer. When user provides the necessary information, DataWarehouse can provide a script or package that the user just needs to execute on that server, and it will automatically deploy an instance of DataWarehouse with the same settings as the original laptop instance, and then the user can link his laptop instance with this server instance. As soon as linked, (depending on the 'Automatically grow' configurations), the system should automatically handle data distribution and load balancing across the nodes. Growth shouldn't be limited to just one server, but can be a cluster of servers, or even cloud instances. The system should be able to monitor the storage usage and performance metrics, and when it detects that the current nodes are reaching their limits, it can prompt the user to add more nodes to the cluster. The user can then provide the necessary information for the new nodes, and the system will handle the deployment and integration of these new nodes into the existing cluster. This way, DataWarehouse can seamlessly scale out as the user's storage needs grow, without any downtime or manual data migration.
- [ ] Implement auto-scaling, load balancing, P2P, auto-sync (online & offline - Air-Gapped), auto-tier, auto-governance mechanisms in the proper plugin base classes (e.g., StoragePluginBase). 
- [ ] Build and verify that these new mechanisms work seamlessly and efficiently within the SDK.

## Phase 3.5: Decouple Plugins and Kernel
** Make sure to verify and if necessary, update ALL the plugins to ensure the below behaviour: **
- [ ] Make sure that no plugin or the Kernel depends on any other plugin or Kernel directly. 
      * All plugins and kernel should only depend in the SDK. This way, we can ensure that all plugins can be used independently and flexibly, and we can also ensure that the UltimateIntelligence plugin can be used with any plugin without any compatibility issues. The UltimateIntelligence plugin should be designed in a way that it can seamlessly integrate with any plugin that inherits from IntelligentPluginBase, regardless of the specific functionalities of that plugin. This will allow us to maintain a clean and modular architecture while still providing powerful intelligent capabilities across all plugins.
- [ ] Make sure that all plugins can register their capabilities and knowledge into the system knowledge bank, and that UltimateIntelligence can leverage this information to provide enhanced functionalities. 
      * This will ensure that all plugins can benefit from the intelligent capabilities provided by UltimateIntelligence, while still maintaining their unique functionalities and behaviors.
- [ ] Make sure that all plugins can leverage the auto-scaling, load balancing, P2P, auto-sync (online & offline - Air-Gapped), auto-tier, auto-governance features implemented in the SDK, and that these features work seamlessly and efficiently across all plugins. 
      * This will ensure that all plugins can benefit from these powerful features, while still maintaining their unique functionalities and behaviors.
- [ ] Make sure that all communications between plugins and kernel are done through Commands/Messages via the message bus only.
      * This will ensure that all plugins and kernel are decoupled and can communicate with each other in a flexible and modular way, without any direct dependencies. This will also allow us to maintain a clean and scalable architecture, where we can easily add or remove plugins without affecting the overall system.
- [ ] Kernel can make use of the registered capabilities and knowledge in the system knowledge bank to make informed decisions about which plugins to use for specific tasks, and how to route commands/messages between plugins.
      * This will allow the kernel to optimize the execution of tasks by leveraging the unique capabilities and knowledge of each plugin, while still maintaining a high level of flexibility and modularity in the system architecture.

## Phase 4A: Update Ultimate Plugins
### For each Ultimate plugin (e.g., UltimateEncryptionPlugin, UltimateCompressionPlugin, UltimateStoragePlugin):
- [ ] Update the Ultimate plugins to inherit from their respective feature-specific plugin base classes (They already have implemented their unique functionalities as stratergies).
- [ ] Update Ultimate Plugins to leverage the new auto-scaling, load balancing, P2P, auto-sync (online & offline - Air-Gapped), auto-tier, auto-governance features from the updated StoragePluginBase.
- [ ] Verify that the new structure ensures that all plugins can seamlessly integrate with UltimateIntelligence
- [ ] Verify that the new structure ensures auto-scaling, load balancing, P2P, auto-sync (online & offline - Air-Gapped), auto-tier, auto-governance features work seamlessly and efficiently in the Ultimate plugins.
- [ ] Build and verify that these new mechanisms work seamlessly and efficiently within the Ultimate Plugin.
## Phase 4B: Update Ultimate plugin Stratergies
- [ ] Verify and if necessary update all stratergies so that all stratergies in the ultimate plugins are updated to leverage the new stratergy base class structure.

## Phase 5A: Update Standalone Plugins
- [ ] Since standalone plugins are unique, they might not fit into the new plugin specific base class structure directly. Instead make sure that they inherit/extend the IntelligentPluginBase to leverage the UltimateIntelligence integration at the very least.
- [ ] Verify that the new structure ensures auto-scaling, load balancing, P2P, auto-sync (online & offline - Air-Gapped), auto-tier, auto-governance features work seamlessly and efficiently with the standalone plugins.
- [ ] Build and verify that these new mechanisms work seamlessly and efficiently within the Ultimate Plugin.
## Phase 5B: Update Standalone plugin Stratergies
- [ ] Verify and if necessary update all stratergies so that all stratergies in the standalone plugins are updated to leverage the new stratergy base class structure.

## Phase 5C: Fix DataWarehouse.CLI
- [ ] The NuGet package System.Commandline.NamingConventionBinder has been deprecated and is no longer maintained. As a result, the DataWarehouse.CLI project, which relies on this package, is currently broken. I have removed the dependency on this deprecated package. Update the DataWarehouse.CLI project to use an alternative approach for command-line parsing and binding that does not rely on the deprecated package or older versions of System.Commandline.

## Phase 6: Testing & Documentation
- [ ] Conduct a full solution build to make sure everything compiles and links correctly.
- [ ] Update Test Cases to cover new base classes and their functionalities.
- [ ] Run all existing and new test cases to ensure everything works as expected.


Current Hirerarchy:

  PluginBase (root — 3,777 lines)
  │
  ├── DataTransformationPluginBase
  │   └── PipelinePluginBase
  │       ├── EncryptionPluginBase
  │       ├── CompressionPluginBase
  │       ├── TransitEncryptionPluginBase
  │       ├── TransitCompressionPluginBase
  │       └── UltimateIntelligencePlugin ★
  │
  ├── FeaturePluginBase
  │   │
  │   ├── IntelligenceAwarePluginBase (AI socket + graceful degradation)
  │   │   ├── IntelligenceAwareEncryptionPluginBase
  │   │   │   └── UltimateEncryptionPlugin ★
  │   │   ├── IntelligenceAwareCompressionPluginBase
  │   │   │   └── UltimateCompressionPlugin ★
  │   │   ├── IntelligenceAwareStoragePluginBase
  │   │   │   └── UltimateStoragePlugin ★
  │   │   ├── IntelligenceAwareAccessControlPluginBase
  │   │   │   └── UltimateAccessControlPlugin ★
  │   │   ├── IntelligenceAwareKeyManagementPluginBase
  │   │   │   └── UltimateKeyManagementPlugin ★
  │   │   ├── IntelligenceAwareCompliancePluginBase
  │   │   │   └── UltimateCompliancePlugin ★
  │   │   ├── IntelligenceAwareInterfacePluginBase
  │   │   │   └── UltimateInterfacePlugin ★
  │   │   ├── IntelligenceAwareConnectorPluginBase
  │   │   │   └── UltimateConnectorPlugin ★
  │   │   ├── IntelligenceAwareDataManagementPluginBase
  │   │   │   ├── UltimateDataGovernancePlugin ★
  │   │   │   ├── UltimateDataCatalogPlugin ★
  │   │   │   ├── UltimateDataManagementPlugin ★
  │   │   │   ├── UltimateDataQualityPlugin ★
  │   │   │   ├── UltimateDataPrivacyPlugin ★
  │   │   │   ├── UltimateDataLakePlugin ★
  │   │   │   └── UltimateDataMeshPlugin ★
  │   │   ├── IntelligenceAwareDatabasePluginBase
  │   │   │
  │   │   └── (generic IntelligenceAwarePluginBase — no specialized sub-base)
  │   │       ├── UltimateDocGenPlugin ★
  │   │       ├── UltimateDeploymentPlugin ★
  │   │       ├── UltimateIoTIntegrationPlugin ★
  │   │       ├── UltimateDataFormatPlugin ★
  │   │       ├── UltimateStreamingDataPlugin ★
  │   │       ├── UltimateRTOSBridgePlugin ★
  │   │       ├── UltimateResiliencePlugin ★
  │   │       ├── UltimateComputePlugin ★
  │   │       ├── UltimateDataFabricPlugin ★
  │   │       ├── UltimateReplicationPlugin ★
  │   │       ├── UltimateStorageProcessingPlugin ★
  │   │       ├── UltimateDatabaseStoragePlugin ★
  │   │       ├── UltimateDatabaseProtocolPlugin ★
  │   │       ├── UltimateDataProtectionPlugin ★
  │   │       ├── UltimateWorkflowPlugin ★
  │   │       ├── UltimateRAIDPlugin ★
  │   │       ├── UltimateSustainabilityPlugin ★
  │   │       ├── UltimateServerlessPlugin ★
  │   │       ├── UltimateMultiCloudPlugin ★
  │   │       ├── UltimateDataIntegrationPlugin ★
  │   │       ├── UltimateMicroservicesPlugin ★
  │   │       └── UltimateSDKPortsPlugin ★
  │   │
  │   ├── InterfacePluginBase
  │   ├── ReplicationPluginBase
  │   ├── ConsensusPluginBase
  │   ├── RealTimePluginBase
  │   ├── ContainerManagerPluginBase
  │   ├── DataConnectorPluginBase
  │   │   ├── DatabaseConnectorPluginBase
  │   │   ├── MessagingConnectorPluginBase
  │   │   └── SaaSConnectorPluginBase
  │   ├── HardwareAcceleratorPluginBase
  │   │   ├── QatAcceleratorPluginBase
  │   │   └── GpuAcceleratorPluginBase
  │   ├── RaidProviderPluginBase
  │   ├── ComplianceProviderPluginBase
  │   ├── ThreatDetectionPluginBase
  │   ├── WormStorageProviderPluginBase
  │   ├── TamperProofProviderPluginBase
  │   ├── BlockchainProviderPluginBase
  │   ├── ShardManagerPluginBase
  │   ├── ... (60+ more feature-specific bases)
  │   │
  │   └── (directly from FeaturePluginBase — missing IntelligenceAware layer)
  │       ├── UltimateResourceManagerPlugin ★
  │       ├── UltimateDataTransitPlugin ★
  │       ├── UltimateDataLineagePlugin ★
  │       └── UltimateFilesystemPlugin ★
  │
  ├── StorageProviderPluginBase
  │   ├── ListableStoragePluginBase
  │   │   ├── TieredStoragePluginBase
  │   │   └── CacheableStoragePluginBase
  │   │       └── IndexableStoragePluginBase
  │   │           ├── HybridStoragePluginBase<TConfig>
  │   │           └── HybridDatabasePluginBase<TConfig>
  │   └── LowLatencyStoragePluginBase
  │
  ├── SecurityProviderPluginBase
  │   ├── AccessControlPluginBase
  │   ├── KeyStorePluginBase
  │   ├── MandatoryAccessControlPluginBase
  │   ├── MultiLevelSecurityPluginBase
  │   ├── TwoPersonIntegrityPluginBase
  │   └── SecureDestructionPluginBase
  │
  ├── IntelligencePluginBase
  ├── MetadataIndexPluginBase
  ├── OrchestrationProviderPluginBase
  ├── CloudEnvironmentPluginBase
  ├── SerializerPluginBase
  ├── SemanticMemoryPluginBase
  ├── MetricsPluginBase
  ├── GovernancePluginBase
  │
  └── UltimateEdgeComputingPlugin ★ (directly from PluginBase)

  Legend: ★ = concrete Ultimate plugin (43 total)

  Key observations:
  - 37 of 43 Ultimate plugins use IntelligenceAwarePluginBase (correct)
  - 4 plugins skip the IntelligenceAware layer (ResourceManager, DataTransit, DataLineage, Filesystem) — these are Phase 27 migration targets
  - UltimateIntelligencePlugin inherits from PipelinePluginBase (correct — it IS the intelligence provider, not a consumer)
  - UltimateEdgeComputingPlugin inherits directly from PluginBase (Phase 27 migration target)
  - 111+ base classes total across the SDK


  To-Be-Refactored Hierarchy:
  (See .planning/ARCHITECTURE_DECISIONS.md for full rationale — AD-01 through AD-07)

  ═══════════════════════════════════════════════════════════════════════
  PLUGIN BASE CLASS HIERARCHY (after refactor)
  ═══════════════════════════════════════════════════════════════════════

  Principles:
  - Every plugin (except UltimateIntelligencePlugin) inherits IntelligenceAwarePluginBase
  - Two branches: DataPipelinePluginBase (data flows through) vs FeaturePluginBase (provides services)
  - Specialized bases (TieredStorage, CacheableStorage, etc.) become composable services, not inheritance
  - Object/key-based storage is the universal core; PathStorageAdapter provides URI translation
  - Single EncryptionPluginBase and CompressionPluginBase (no AtRest/Transit split — strategies handle that)

  IPlugin (interface contract)
  └── PluginBase (lifecycle, capability registry, knowledge registry, IDisposable/IAsyncDisposable)
      │   - Initialize(), Execute(), Shutdown() with CancellationToken
      │   - RegisterCapability(), QueryCapabilities(), DeregisterCapability()
      │   - RegisterKnowledge(), QueryKnowledge() via ConcurrentDictionary cache
      │   - Dispose(bool) pattern with GC.SuppressFinalize
      │   - Error handling, structured logging, message bus subscription
      │
      ├── UltimateIntelligencePlugin ★ (IS the intelligence provider — inherits PluginBase directly)
      │   Rationale: It cannot plug into itself. It provides the AI socket that others consume.
      │
      └── IntelligenceAwarePluginBase (AI socket, graceful degradation)
          │   - ConnectToUltimateIntelligence() / DisconnectFromUltimateIntelligence()
          │   - Graceful degradation: manual mode ALWAYS works if AI unavailable
          │   - All AI features: NLP, Semantics, Context, Learning, Reasoning, Planning, etc.
          │
          ├─── DataPipelinePluginBase (plugins that data flows THROUGH)
          │    │   - Pipeline stage registration, ordering, back-pressure
          │    │   - Throughput/latency metrics per stage
          │    │   - Input/output data type declarations
          │    │
          │    ├── DataTransformationPluginBase (mutates/transforms data)
          │    │   │   - Transform(Stream input) → Stream output pattern
          │    │   │   - Bidirectional support (encode/decode, encrypt/decrypt, compress/decompress)
          │    │   │
          │    │   ├── EncryptionPluginBase
          │    │   │   └── UltimateEncryptionPlugin ★ ............ (30+ strategies: AES, Serpent, ChaCha20, etc.)
          │    │   │
          │    │   └── CompressionPluginBase
          │    │       └── UltimateCompressionPlugin ★ ........... (40+ strategies: LZ4, Zstd, BWT, PPM, etc.)
          │    │
          │    ├── StoragePluginBase (data persistence — object/key-based core)
          │    │   │   - StoreAsync(key, Stream, metadata), RetrieveAsync(key), DeleteAsync(key), ListAsync(prefix)
          │    │   │   - StorageObjectMetadata (ETag, ContentType, Tier, VersionId, CustomMetadata)
          │    │   │   - Composable services (not inheritance): ITierManager, ICacheManager, IStorageIndex,
          │    │   │     IConnectionRegistry, IHealthMonitor — extracted from old TieredStorage/CacheableStorage/etc. bases
          │    │   │   - PathStorageAdapter: translates URI/file-path operations → object/key operations
          │    │   │
          │    │   ├── UltimateStoragePlugin ★ ................... (130+ strategies: S3, Azure, Local, Redis, IPFS, etc.)
          │    │   ├── UltimateStorageProcessingPlugin ★ ......... (43 strategies)
          │    │   ├── UltimateDatabaseStoragePlugin ★ ........... (49 strategies)
          │    │   └── UltimateDatabaseProtocolPlugin ★
          │    │
          │    ├── ReplicationPluginBase (distributes/replicates data across nodes)
          │    │   │   - ReplicateAsync(), SyncAsync(), ResolveConflictAsync()
          │    │   │   - Consistency models: Eventual, Strong, ReadAfterWrite
          │    │   │
          │    │   ├── UltimateReplicationPlugin ★ ............... (60+ strategies: MultiMaster, Sharding, etc.)
          │    │   └── UltimateRAIDPlugin ★ ...................... (50+ strategies: RAID levels, Erasure, etc.)
          │    │
          │    ├── DataTransitPluginBase (moves data between nodes/systems)
          │    │   │   - TransferAsync(), ReceiveAsync(), ResumeAsync()
          │    │   │   - Chunked, Delta, P2P, Multi-path, Store-and-forward
          │    │   │
          │    │   └── UltimateDataTransitPlugin ★ ............... (6 protocols + features)
          │    │
          │    └── IntegrityPluginBase (verifies/proves data at pipeline boundaries)
          │        │   - Strategies: TamperProof, Blockchain, WORM, HashChain
          │        │   - VerifyAsync(), ProveAsync(), AuditAsync()
          │        │
          │        └── (TamperProof strategies live here — no separate Ultimate plugin, uses strategies)
          │
          └─── FeaturePluginBase (plugins that provide services/capabilities, don't process data directly)
               │
               ├── SecurityPluginBase (authentication, authorization, keys, threat detection)
               │   │   Sub-bases exist because SEPARATE Ultimate plugins handle each sub-domain
               │   │
               │   ├── AccessControlPluginBase
               │   │   └── UltimateAccessControlPlugin ★ ........ (142 strategies: RBAC, ABAC, MAC, ZeroTrust, etc.)
               │   │
               │   ├── KeyManagementPluginBase
               │   │   └── UltimateKeyManagementPlugin ★ ........ (68 strategies: HSM, FROST, Vault, PostQuantum, etc.)
               │   │
               │   ├── CompliancePluginBase
               │   │   └── UltimateCompliancePlugin ★ ........... (145 strategies: GDPR, HIPAA, SOC2, FedRAMP, etc.)
               │   │
               │   ├── ThreatDetectionPluginBase
               │   │
               │   └── DataProtectionPluginBase
               │       └── UltimateDataProtectionPlugin ★ ....... (35 strategies)
               │
               ├── InterfacePluginBase (external access protocols)
               │   │   - StartAsync(), HandleRequestAsync(), StopAsync()
               │   │   - Connectors merge here (connectors ARE interfaces to external systems)
               │   │
               │   ├── UltimateInterfacePlugin ★ ................. (68 strategies: REST, gRPC, GraphQL, SQL Wire, etc.)
               │   └── UltimateConnectorPlugin ★ ................. (280 strategies: SQL, NoSQL, SaaS, Messaging, etc.)
               │
               ├── DataManagementPluginBase (catalog, quality, lineage, governance, privacy)
               │   │   Sub-bases for each sub-domain
               │   │
               │   ├── UltimateDataGovernancePlugin ★
               │   ├── UltimateDataCatalogPlugin ★
               │   ├── UltimateDataQualityPlugin ★
               │   ├── UltimateDataLineagePlugin ★
               │   ├── UltimateDataManagementPlugin ★ ........... (78 strategies)
               │   ├── UltimateDataPrivacyPlugin ★
               │   ├── UltimateDataLakePlugin ★
               │   ├── UltimateDataMeshPlugin ★
               │   └── UltimateDataFabricPlugin ★
               │
               ├── ComputePluginBase (processing runtimes — WASM, containers, GPU, sandbox)
               │   │   - Hardware accelerator bases merge here (GPU, QAT are compute strategies)
               │   │
               │   ├── UltimateComputePlugin ★ ................... (83 strategies: WASM, Container, GPU, Enclave, etc.)
               │   └── UltimateServerlessPlugin ★
               │
               ├── ObservabilityPluginBase (monitoring, metrics, tracing, health, alerting)
               │   │   - Metrics, Telemetry, HealthProvider bases merge here (they're observability strategies)
               │   │
               │   └── UniversalObservabilityPlugin ★ ............ (55 strategies: Prometheus, Jaeger, etc.)
               │
               ├── StreamingPluginBase (message queues, event streams, IoT, industrial)
               │   │
               │   ├── UltimateStreamingDataPlugin ★ ............. (17 strategies: Kafka, MQTT, AMQP, etc.)
               │   ├── UltimateIoTIntegrationPlugin ★
               │   └── UltimateRTOSBridgePlugin ★
               │
               ├── MediaPluginBase (video, image, 3D, transcoding)
               │   │
               │   └── (Media strategies: H.264, H.265, VP9, AV1, RAW, GPU textures, etc.)
               │
               ├── FormatPluginBase (data formats — columnar, graph, scientific, lakehouse)
               │   │
               │   └── UltimateDataFormatPlugin ★ ................ (28 strategies: Parquet, Arrow, etc.)
               │
               ├── InfrastructurePluginBase (deployment, resilience, sustainability, multi-cloud)
               │   │
               │   ├── UltimateDeploymentPlugin ★
               │   ├── UltimateResiliencePlugin ★
               │   ├── UltimateSustainabilityPlugin ★ ........... (45 strategies)
               │   └── UltimateMultiCloudPlugin ★
               │
               ├── OrchestrationPluginBase (workflows, edge computing, data integration)
               │   │
               │   ├── UltimateWorkflowPlugin ★
               │   ├── UltimateEdgeComputingPlugin ★
               │   └── UltimateDataIntegrationPlugin ★
               │
               └── PlatformPluginBase (SDK ports, microservices, marketplace)
                   │
                   ├── UltimateSDKPortsPlugin ★
                   ├── UltimateMicroservicesPlugin ★
                   └── UltimateDocGenPlugin ★

  Legend: ★ = concrete Ultimate/standalone plugin (43 total)


  ═══════════════════════════════════════════════════════════════════════
  STRATEGY BASE CLASS HIERARCHY (after refactor)
  ═══════════════════════════════════════════════════════════════════════

  Principles:
  - Plugin = Container/Orchestrator (collection of ways to do a similar thing)
  - Strategy = Worker (ONE specific way to do it — the actual algorithm/implementation)
  - Flat hierarchy: only 2 levels deep (StrategyBase → DomainBase → ConcreteStrategy)
  - NO IntelligenceAwareStrategyBase — intelligence belongs at the plugin level ONLY
  - NO capability registry on strategies — strategy IS a capability of its parent plugin
  - NO knowledge bank access — plugin registers runtime knowledge on strategy's behalf
  - NO message bus access — plugin orchestrates all messaging
  - Strategies declare characteristics (metadata); plugins register them as capabilities

  Knowledge flow:
    Plugin knowledge (static): "AES, Serpent available; AES disabled, Serpent enabled"
    Strategy knowledge (runtime): "Block X encrypted with Serpent-256, key ID abc"
    → Strategy produces runtime knowledge as RETURN VALUES
    → Plugin registers it in the knowledge bank on behalf of the strategy

  Intelligence flow:
    Plugin has AI socket → asks AI "which strategy?" → gets recommendation
    → passes recommendation as OPTIONS to strategy method
    → strategy executes with given options (doesn't know AI was involved)
    → plugin collects strategy metrics, feeds back to AI for learning

  IStrategy (interface — Name, Description, Characteristics)
  └── StrategyBase (abstract root)
      │   - InitializeAsync(CancellationToken) / ShutdownAsync(CancellationToken)
      │   - IDisposable / IAsyncDisposable with proper pattern
      │   - string Name, string Description (identity)
      │   - IReadOnlyDictionary<string, object> Characteristics (metadata, NOT capabilities)
      │   - bool IsInitialized, bool IsDisposed (state)
      │   - Structured logging hooks
      │
      │   What StrategyBase does NOT have:
      │   ✗ Intelligence/AI integration
      │   ✗ Capability registry
      │   ✗ Knowledge bank access
      │   ✗ Message bus access
      │   ✗ Pipeline awareness
      │
      ├── EncryptionStrategyBase
      │   │   + EncryptAsync(Stream, EncryptionOptions, CT) → EncryptionResult
      │   │   + DecryptAsync(Stream, DecryptionOptions, CT) → Stream
      │   │   + SupportedKeySizes, SupportsAuthentication, RequiresIV (characteristics)
      │   │
      │   └── AesGcmStrategy, AesCbcStrategy, SerpentStrategy, ChaCha20Strategy,
      │       TwofishStrategy, BlowfishStrategy, OtpStrategy, ... (12+ strategies)
      │
      ├── CompressionStrategyBase
      │   │   + CompressAsync(Stream, CompressionOptions, CT) → CompressionResult
      │   │   + DecompressAsync(Stream, CT) → Stream
      │   │   + CompressionLevel, SupportsStreaming, IsLossless (characteristics)
      │   │
      │   └── LZ4Strategy, ZstdStrategy, BwtStrategy, PpmStrategy, SnappyStrategy,
      │       BrotliStrategy, DeflateStrategy, ... (59+ strategies)
      │
      ├── StorageStrategyBase
      │   │   + StoreAsync(string key, Stream, StorageObjectMetadata, CT) → StorageResult
      │   │   + RetrieveAsync(string key, CT) → (Stream, StorageObjectMetadata)
      │   │   + DeleteAsync(string key, CT), ListAsync(string prefix, CT)
      │   │   + ExistsAsync(string key, CT) → StorageObjectMetadata?
      │   │   + SupportsTiering, SupportsVersioning, SupportsEncryption (characteristics)
      │   │
      │   └── LocalFileStrategy, AzureBlobStrategy, S3Strategy, RedisStrategy,
      │       MongoGridFsStrategy, IpfsStrategy, CephStrategy, ... (130+ strategies)
      │
      ├── SecurityStrategyBase
      │   │   + AuthenticateAsync(credentials, CT) → AuthResult
      │   │   + AuthorizeAsync(principal, resource, action, CT) → bool
      │   │   + ValidateAsync(token, CT) → ValidationResult
      │   │
      │   └── RbacStrategy, AbacStrategy, MacStrategy, ZeroTrustStrategy,
      │       MlsStrategy, TwoPersonStrategy, ... (142+ strategies)
      │
      ├── KeyManagementStrategyBase
      │   │   + GenerateKeyAsync(spec, CT) → KeyMaterial
      │   │   + RotateKeyAsync(keyId, CT) → KeyMaterial
      │   │   + StoreKeyAsync(keyId, material, CT), RetrieveKeyAsync(keyId, CT)
      │   │
      │   └── HsmStrategy, VaultStrategy, FrostStrategy, PostQuantumStrategy,
      │       SoftwareKeyStore, CloudKmsStrategy, ... (68+ strategies)
      │
      ├── ComplianceStrategyBase
      │   │   + AuditAsync(scope, CT) → AuditReport
      │   │   + VerifyAsync(policy, data, CT) → ComplianceResult
      │   │   + ReportAsync(framework, period, CT) → ComplianceReport
      │   │
      │   └── GdprStrategy, HipaaStrategy, Soc2Strategy, FedRampStrategy,
      │       PciDssStrategy, Iso27001Strategy, ... (145+ strategies)
      │
      ├── InterfaceStrategyBase
      │   │   + StartAsync(CT), StopAsync(CT)
      │   │   + HandleRequestAsync(request, CT) → response
      │   │   + Protocol, Port, IsSecure (characteristics)
      │   │
      │   └── RestStrategy, GrpcStrategy, GraphQlStrategy, WebSocketStrategy,
      │       SqlWireStrategy, MqttStrategy, ... (68+ strategies)
      │
      ├── ConnectorStrategyBase
      │   │   + ConnectAsync(config, CT), DisconnectAsync(CT)
      │   │   + ExecuteAsync(command, CT) → result
      │   │   + Built-in: retry logic with exponential backoff, connection pooling
      │   │
      │   └── SqlServerConnector, PostgresConnector, MongoConnector,
      │       KafkaConnector, SalesforceConnector, ... (280+ strategies)
      │
      ├── ComputeStrategyBase
      │   │   + ExecuteAsync(workload, CT) → ComputeResult
      │   │   + SandboxAsync(code, limits, CT) → ExecutionResult
      │   │   + ResourceLimits, SupportsGpu, SupportsIsolation (characteristics)
      │   │
      │   └── WasmStrategy, ContainerStrategy, GpuStrategy, EnclaveStrategy,
      │       SandboxStrategy, ServerlessStrategy, ... (83+ strategies)
      │
      ├── ObservabilityStrategyBase
      │   │   + EmitMetricsAsync(metrics, CT)
      │   │   + TraceAsync(activity, CT)
      │   │   + HealthCheckAsync(CT) → HealthStatus
      │   │
      │   └── PrometheusStrategy, JaegerStrategy, OtlpStrategy,
      │       ElasticApmStrategy, DatadogStrategy, ... (55+ strategies)
      │
      ├── ReplicationStrategyBase
      │   │   + ReplicateAsync(source, target, CT) → ReplicationResult
      │   │   + SyncAsync(nodes, CT), ResolveConflictAsync(conflicts, CT)
      │   │
      │   └── MultiMasterStrategy, RaftStrategy, CrdtStrategy,
      │       ErasureCodingStrategy, GeoReplicationStrategy, ... (60+ strategies)
      │
      ├── MediaStrategyBase
      │   │   + TranscodeAsync(input, outputFormat, CT) → Stream
      │   │   + ExtractMetadataAsync(input, CT) → MediaMetadata
      │   │
      │   └── H264Strategy, H265Strategy, Vp9Strategy, Av1Strategy,
      │       JpegStrategy, PngStrategy, RawStrategy, ... (20+ strategies)
      │
      ├── StreamingStrategyBase
      │   │   + PublishAsync(topic, message, CT)
      │   │   + SubscribeAsync(topic, handler, CT), AcknowledgeAsync(messageId, CT)
      │   │
      │   └── KafkaStrategy, MqttStrategy, AmqpStrategy,
      │       EventHubStrategy, KinesisStrategy, ... (17+ strategies)
      │
      ├── FormatStrategyBase
      │   │   + SerializeAsync(data, CT) → Stream
      │   │   + DeserializeAsync(Stream, CT) → data
      │   │   + ValidateSchemaAsync(data, schema, CT) → bool
      │   │
      │   └── ParquetStrategy, ArrowStrategy, AvroStrategy,
      │       ProtobufStrategy, JsonStrategy, ... (28+ strategies)
      │
      ├── TransitStrategyBase
      │   │   + TransferAsync(source, destination, CT) → TransferResult
      │   │   + ReceiveAsync(CT) → Stream, ResumeAsync(transferId, CT)
      │   │
      │   └── ChunkedStrategy, DeltaStrategy, P2PStrategy,
      │       MultiPathStrategy, StoreAndForwardStrategy, ... (11+ strategies)
      │
      └── DataManagementStrategyBase
          │   + CatalogAsync(asset, CT), TraceLineageAsync(asset, CT) → LineageGraph
          │   + AssessQualityAsync(dataset, rules, CT) → QualityReport
          │
          └── LineageStrategy, CatalogStrategy, QualityStrategy,
              GovernanceStrategy, PrivacyStrategy, ... (78+ strategies)

  Total: ~1,500+ concrete strategies across ~16 domain bases
  Boilerplate eliminated: ~1,000 lines of duplicated intelligence/capability/dispose code

