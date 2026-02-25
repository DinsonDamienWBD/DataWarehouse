# SDK Reference: What You Get For Free

This document describes the complete feature set provided by the DataWarehouse SDK to plugin and strategy implementers. Everything described here is **included by default**—you don't need to implement it yourself.

---

## 1. Plugin Base Class Hierarchy

### Overview

All plugins inherit from `PluginBase` at the root, which provides universal functionality. Below `PluginBase` are two primary branches introduced in v3.0:

- **Feature Branch**: Services that provide capabilities to the system
- **DataPipeline Branch**: Stages that data flows through for transformation/persistence

```
PluginBase (root, abstract)
├── IntelligenceAwarePluginBase (AI-aware extensions)
│   ├── FeaturePluginBase (services, static lifecycle)
│   │   ├── SecurityPluginBase
│   │   ├── InterfacePluginBase
│   │   ├── DataManagementPluginBase
│   │   ├── ComputePluginBase
│   │   ├── ObservabilityPluginBase
│   │   ├── ResiliencePluginBase
│   │   ├── StreamingPluginBase
│   │   ├── FormatPluginBase
│   │   ├── MediaPluginBase
│   │   ├── OrchestrationPluginBase
│   │   ├── PlatformPluginBase
│   │   └── InfrastructurePluginBase
│   │
│   └── DataPipelinePluginBase (data-transforming stages)
│       ├── EncryptionPluginBase
│       ├── CompressionPluginBase
│       ├── StoragePluginBase
│       ├── ReplicationPluginBase
│       ├── DataTransitPluginBase
│       ├── DataTransformationPluginBase
│       └── IntegrityPluginBase
```

### PluginBase (Root)

**What it provides:**

- **Lifecycle**: `InitializeAsync`, `ActivateAsync`, `ExecuteAsync`, `ShutdownAsync`, hot-reload (`OnReloadingAsync`, `OnReloadedAsync`)
- **State Persistence**: `SaveStateAsync`, `LoadStateAsync`, `DeleteStateAsync`, `ListStateKeysAsync` via `IPluginStateStore`
- **Strategy Registry**: Generic `StrategyRegistry<IStrategy>` with discovery, registration, and dispatch
- **Capability Registration**: Auto-registration on handshake via `GetCapabilityRegistrations()`
- **Knowledge System**: Capability knowledge registration, storage, and query response via KnowledgeLake
- **Message Bus**: Auto-injected for inter-plugin communication
- **Health Checks**: `CheckHealthAsync()` defaults to Healthy
- **Configuration Injection**: `SystemConfiguration` automatically injected, propagates to strategies
- **Bounded Collections**: `CreateBoundedDictionary`, `CreateBoundedList`, `CreateBoundedQueue` with LRU eviction and auto-tracking
- **Scaling Support**: `IScalableSubsystem` implementation with metrics and backpressure reporting
- **Policy Engine**: `PolicyContext` injected for v6.0 policy enforcement
- **Dispose Pattern**: Both sync and async disposal, auto-disposes strategies and tracked collections

**What you MUST override:**

- `Id` (unique stable identifier, e.g., "com.company.plugin.name")
- `Name` (human-readable name)
- `Category` (PluginCategory enum value)

**What you SHOULD override:**

- `DeclaredCapabilities` – describe what your plugin can do
- `GetCapabilityRegistrations()` – rich capability metadata
- `GetStaticKnowledge()` – plugin documentation for AI systems
- `GetDefaultStrategyId()` – default strategy when none specified
- Lifecycle hooks: `InitializeAsync`, `ExecuteAsync`, `ShutdownAsync`
- Health check: override `CheckHealthAsync()` for custom health reporting

---

### IntelligenceAwarePluginBase

Extends `PluginBase` with automatic Universal Intelligence (T90) discovery and helper methods.

**What it provides:**

- **Intelligence Discovery**: Auto-discovery on `StartAsync()`, with capability caching (60-second TTL)
- **AI Operations**: Protected methods for embeddings, classification, anomaly detection, predictions, completeness, summarization, entity extraction, PII detection, semantic search, memory operations
- **Long-term Memory**: `StoreMemoryAsync()`, `RecallMemoriesAsync()`, `ConsolidateMemoriesAsync()`
- **Tabular Models**: `PredictTabularAsync()`, `TrainTabularModelAsync()`, `ExplainTabularPredictionAsync()`
- **Agent Execution**: `ExecuteAgentTaskAsync()`, `RegisterAgentToolAsync()`, `GetAgentStateAsync()`
- **Evolving Intelligence**: `LearnFromInteractionAsync()`, `GetExpertiseScoreAsync()`, `AdaptIntelligenceBehaviorAsync()`
- **Message Handlers**: Typed request/response handlers via `RegisterHandler<TRequest, TResponse>()` and fire-and-forget notifications
- **Policy Recommendations**: Receives recommendations via `OnRecommendationReceivedAsync()` callback
- **IAiHook Implementation**: `Observations` emitter, `Recommendations` receiver

**Lifecycle hooks (override to customize):**

- `OnStartWithIntelligenceAsync()` – called when Intelligence is available
- `OnStartWithoutIntelligenceAsync()` – called when Intelligence is unavailable
- `OnStartCoreAsync()` – common startup logic
- `OnIntelligenceAvailableAsync()` – notification when Intelligence becomes available
- `OnIntelligenceUnavailableAsync()` – notification when Intelligence becomes unavailable

---

### FeaturePluginBase

Extends `IntelligenceAwarePluginBase` for service-oriented plugins (Security, Interface, Compute, etc.).

**Semantic differences from DataPipeline:**

- Services that **provide capabilities** to the system
- Observe, enforce, or serve data (they don't mutate it)
- Have independent lifecycle (start/stop) not tied to pipeline ordering
- May expose external interfaces (REST, gRPC) or enforce policies

**What it adds:**

- `SupportsHotReload` – advertise whether hot-reload is supported
- `FeatureCategory` – string grouping (e.g., "Security", "Compute")
- Metadata marks this as "Feature" branch for orchestration

---

### DataPipelinePluginBase

Extends `IntelligenceAwarePluginBase` for data-transforming stages (Encryption, Compression, Storage, etc.).

**Semantic differences from Feature:**

- Data **flows through** these plugins (enters, transforms, exits)
- Participate in pipeline ordering (lower `DefaultPipelineOrder` runs first)
- Can signal back-pressure to throttle upstream
- May mutate data (encryption, compression) or persist/move it

**What it adds:**

- `DefaultPipelineOrder` – execution order in pipeline (default 100, lower = earlier)
- `AllowBypass` – can this stage be skipped based on content analysis?
- `RequiredPrecedingStages` – stages that must run before this one
- `IncompatibleStages` – stages that conflict with this one
- `MutatesData` – does this transform data, or move/persist it?
- Metadata marks this as "DataPipeline" branch for orchestration

---

## 2. Strategy Base Class Hierarchy

### StrategyBase (Root)

All strategies inherit from `StrategyBase`, which provides:

**Lifecycle:**
- `InitializeAsync()` – acquire resources (idempotent)
- `ShutdownAsync()` – release resources
- `InitializeAsyncCore()` – override for custom init
- `ShutdownAsyncCore()` – override for custom shutdown

**Counters:**
- `IncrementCounter(name)` – thread-safe counter tracking
- `GetCounter(name)` – retrieve counter value
- `GetAllCounters()` – snapshot all counters
- `ResetCounters()` – zero all counters

**Health & Configuration:**
- `GetCachedHealthAsync(healthCheck, cacheDuration?)` – cached health check with TTL
- `SystemConfiguration` – read-only access to global config
- `InjectConfiguration(config)` – called by owning plugin

**Utilities:**
- `ExecuteWithRetryAsync<T>(operation, maxRetries, baseDelay)` – retry with exponential backoff + jitter
- `IsTransientException(ex)` – override to classify transient errors
- `EnsureInitializedAsync(initCore)` – double-check locking pattern
- `ThrowIfNotInitialized()` – guard for methods requiring init
- `EnsureNotDisposed()` – guard against use-after-dispose

**Metadata:**
- `StrategyId` – unique stable identifier (abstract, must override)
- `Name` – human-readable name (abstract, must override)
- `Description` – what this strategy does (default: "{Name} strategy")
- `Characteristics` – performance profile, hardware requirements (override to provide)
- `Applicability` – StrategyApplicability enum (Both, DataOnly, DeviceOnly)

**Legacy MessageBus (for compatibility):**
- `ConfigureIntelligence(messageBus)` – opt-in message bus for event publishing
- `MessageBus` – nullable message bus reference
- `IsIntelligenceAvailable` – guard for bus access

**What you MUST override:**
- `StrategyId` – unique identifier
- `Name` – display name

**What you SHOULD override:**
- `Description` – explain when to use this strategy
- `Characteristics` – performance/hardware profile
- `Applicability` – specify if Data-only or Device-only
- `InitializeAsyncCore()` – acquire resources
- `ShutdownAsyncCore()` – release resources

---

### StrategyApplicability Enum

Introduced in v6.0, this enum declares at what level a strategy operates:

```csharp
public enum StrategyApplicability
{
    Both,        // Data-level AND device-level (default, backward compatible)
    DataOnly,    // Object/data transformations only (e.g., format codecs)
    DeviceOnly   // Block-level operations only (e.g., device RAID, NVRAM)
}
```

The dual-level architecture (v6.0) uses this to route strategy invocations correctly.

---

## 3. What Every Plugin Gets For Free

### Lifecycle Management

```csharp
// Three-phase initialization:
// Phase 1: Constructor (zero dependencies)
// Phase 2: InitializeAsync() – MessageBus available, local setup
// Phase 3: ActivateAsync() – distributed coordination available

public override async Task InitializeAsync(CancellationToken ct)
{
    // Always call base first
    await base.InitializeAsync(ct);

    // Your init logic here
    // StateStore, StrategyRegistry, MessageBus all available
}

public override async Task ActivateAsync(CancellationToken ct)
{
    // Called after all plugins initialized, cluster membership resolved
    // Use for cross-node discovery and coordination
    await base.ActivateAsync(ct);
}

public override async Task ExecuteAsync(CancellationToken ct)
{
    // Main processing logic
    await base.ExecuteAsync(ct);
}

public override async Task ShutdownAsync(CancellationToken ct)
{
    // Clean up before disposal
    await base.ShutdownAsync(ct);
}
```

---

### State Persistence

Every plugin automatically gets a state store that routes through the message bus:

```csharp
// Save state (calls OnBeforeStatePersistAsync, then OnAfterStatePersistAsync)
var data = System.Text.Encoding.UTF8.GetBytes("my state");
await SaveStateAsync("my-key", data, cancellationToken);

// Load state
var loaded = await LoadStateAsync("my-key", cancellationToken);
if (loaded != null)
{
    var state = System.Text.Encoding.UTF8.GetString(loaded);
}

// List all keys for this plugin
var keys = await ListStateKeysAsync(cancellationToken);

// Delete state
await DeleteStateAsync("my-key", cancellationToken);

// Customize storage backend
protected override IPluginStateStore? CreateCustomStateStore()
{
    // Return custom implementation or null to disable persistence
    return MessageBus != null ? new DefaultPluginStateStore(MessageBus) : null;
}

// Hooks for persistence lifecycle
protected override Task OnBeforeStatePersistAsync(CancellationToken ct)
{
    // Validate, encrypt, or transform data before storage
    return Task.CompletedTask;
}

protected override Task OnAfterStatePersistAsync(CancellationToken ct)
{
    // Emit metrics or notifications after storage completes
    return Task.CompletedTask;
}
```

---

### Strategy Registry & Dispatch

Generic, type-safe strategy management:

```csharp
// Register a strategy
var encryptionStrategy = new AES256Strategy();
RegisterStrategy(encryptionStrategy);

// Discover strategies from assembly (auto-instantiates concrete types)
int discovered = DiscoverStrategiesFromAssembly(typeof(MyPlugin).Assembly);

// Resolve a strategy by ID
var strategy = ResolveStrategy<IEncryptionStrategy>("aes-256-gcm");
if (strategy != null) { /* use it */ }

// Resolve with fallback to default
var strategy = ResolveStrategyOrDefault<IEncryptionStrategy>(null); // uses default

// Resolve with ACL enforcement
var identity = new CommandIdentity { EffectivePrincipalId = "user@domain" };
try
{
    var strategy = ResolveStrategy<IEncryptionStrategy>("aes-256-gcm", identity);
}
catch (UnauthorizedAccessException)
{
    // Principal denied access to this strategy
}

// Execute operation against a strategy (primary dispatch pattern)
var result = await ExecuteWithStrategyAsync<IEncryptionStrategy, byte[]>(
    strategyId: "aes-256-gcm",
    identity: commandIdentity,
    operation: async (strategy) => await strategy.EncryptAsync(data, ct),
    ct: cancellationToken
);

// Set and get default strategy
StrategyRegistry.SetDefault("aes-256-gcm");
var defaultStrategy = StrategyRegistry.GetDefault();

// Override default per plugin
protected override string? GetDefaultStrategyId() => "aes-256-gcm";

// Provide optional ACL
protected IStrategyAclProvider? StrategyAclProvider { get; set; }
```

---

### Capability Registration

Automatic capability registration and knowledge creation:

```csharp
// Declare capabilities (basic)
protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
    => new[]
    {
        new RegisteredCapability { CapabilityId = "encrypt", ... },
        new RegisteredCapability { CapabilityId = "decrypt", ... }
    };

// Provide detailed registrations
protected override List<RegisteredCapability> GetCapabilityRegistrations()
{
    return new List<RegisteredCapability>
    {
        new RegisteredCapability
        {
            CapabilityId = $"{Id}.encrypt",
            DisplayName = "Encrypt Data",
            Description = "Encrypts data using selected strategy",
            Category = CapabilityCategory.Security,
            PluginId = Id,
            PluginName = Name,
            PluginVersion = Version,
            Tags = new[] { "encryption", "data-protection" }
        }
    };
}

// Capabilities auto-register on handshake
// No manual registration needed—it happens automatically
```

---

### Knowledge System

Plugins automatically register themselves with the Knowledge Lake for AI discovery:

```csharp
// Provide static knowledge (loaded at startup)
protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
{
    return new[]
    {
        KnowledgeObject.CreateCapabilityKnowledge(
            Id,
            Name,
            new[] { "encrypt", "decrypt" },
            new Dictionary<string, object>
            {
                ["version"] = Version,
                ["algorithms"] = new[] { "aes-256-gcm", "chacha20-poly1305" }
            }
        )
    };
}

// Build strategy knowledge (auto-populated from registry)
protected override Dictionary<string, object>? GetStrategyKnowledge()
{
    // Default: auto-populates from registry when strategies registered
    return base.GetStrategyKnowledge();
}

// Build configuration knowledge
protected override KnowledgeObject BuildConfigurationKnowledge()
{
    return new KnowledgeObject
    {
        Topic = "plugin.configuration",
        Payload = GetConfigurationState()
    };
}

// Build statistics knowledge
protected override KnowledgeObject? BuildStatisticsKnowledge()
{
    return new KnowledgeObject
    {
        Topic = "plugin.statistics",
        Payload = new Dictionary<string, object>
        {
            ["bytesEncrypted"] = _bytesEncrypted,
            ["operationsCount"] = _operationCount
        }
    };
}

// Handle dynamic knowledge queries
protected override Task<IReadOnlyList<KnowledgeObject>> HandleDynamicKnowledgeQueryAsync(
    KnowledgeRequest request,
    CancellationToken ct)
{
    // Respond to specific query topics
    var topic = request.Topic.ToLowerInvariant();
    if (topic == "plugin.strategies")
    {
        return Task.FromResult(/* strategy knowledge */);
    }
    return Task.FromResult<IReadOnlyList<KnowledgeObject>>(Array.Empty<KnowledgeObject>());
}

// All registration happens automatically during initialization
// Plugins do NOT manually call registration methods
```

---

### Message Bus Communication

Every plugin has access to the message bus for inter-plugin communication:

```csharp
// Published during initialization (set by kernel)
protected IMessageBus? MessageBus { get; private set; }

// Subscribe to topics (typically done in OnKernelServicesInjected)
protected override void OnKernelServicesInjected()
{
    base.OnKernelServicesInjected();

    if (MessageBus == null) return;

    var subscription = MessageBus.Subscribe("my.topic", async (message) =>
    {
        // Handle message
        await ProcessMessageAsync(message);
    });

    // Store subscription for cleanup in Dispose
}

// Publish messages
if (MessageBus != null)
{
    await MessageBus.PublishAsync("my.topic", new PluginMessage
    {
        Type = "my.message.type",
        Payload = new Dictionary<string, object>
        {
            ["key"] = "value"
        }
    });
}
```

---

### Configuration Management

System configuration automatically injected and propagates to strategies:

```csharp
// Always available
protected DataWarehouseConfiguration SystemConfiguration { get; private set; }

// Called by kernel at runtime when configuration changes
public override Task OnConfigurationChangedAsync(DataWarehouseConfiguration newConfig, CancellationToken ct)
{
    // Call base to propagate to strategies
    await base.OnConfigurationChangedAsync(newConfig, ct);

    // Your custom reaction here (e.g., reconnect services)
    return Task.CompletedTask;
}

// Get current configuration state for knowledge reporting
protected override Dictionary<string, object> GetConfigurationState()
{
    return new Dictionary<string, object>
    {
        ["fipsMode"] = SystemConfiguration.SecuritySettings.FipsMode,
        ["defaultAlgorithm"] = "aes-256-gcm"
    };
}
```

---

### Hot Reload Support

Plugins survive code reloads with state and strategies preserved:

```csharp
// Called BEFORE plugin is unloaded
public override async Task OnReloadingAsync(CancellationToken ct)
{
    // Base implementation persists strategy states
    await base.OnReloadingAsync(ct);

    // Save any additional in-memory state
    var customState = SerializeMyState();
    await SaveStateAsync("my.custom.state", customState, ct);
}

// Called AFTER plugin is reloaded with new code
public override async Task OnReloadedAsync(CancellationToken ct)
{
    // Base implementation re-discovers strategies and re-registers capabilities
    await base.OnReloadedAsync(ct);

    // Restore any additional state
    var customState = await LoadStateAsync("my.custom.state", ct);
    if (customState != null)
    {
        RestoreMyState(customState);
    }
}
```

---

### Health Checks

Every plugin reports health status:

```csharp
// Default implementation returns Healthy
// Override to report actual health based on internal state
public override async Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct)
{
    if (_connectionFailed)
    {
        return HealthCheckResult.Degraded("Connection to service failed");
    }

    if (_resourceExhausted)
    {
        return HealthCheckResult.Unhealthy("Resource exhaustion detected");
    }

    return HealthCheckResult.Healthy($"{Name} is healthy");
}
```

---

### Bounded Collections

Every plugin automatically gets memory-bounded collections with LRU eviction:

```csharp
// Create a bounded dictionary (auto-tracked for scaling & disposal)
var cache = CreateBoundedDictionary<string, CachedObject>(
    collectionName: "object-cache",
    maxCapacity: 1000
);

// Use normally—evicts oldest entries when full
cache["key1"] = new CachedObject();
var obj = cache.TryGetValue("key2", out var value) ? value : null;

// Create a bounded list
var history = CreateBoundedList<string>(
    collectionName: "audit-log",
    maxCapacity: 10000
);
history.Add("event 1");
history.Add("event 2");

// Create a bounded queue
var workQueue = CreateBoundedQueue<WorkItem>(
    collectionName: "work-items",
    maxCapacity: 500
);
workQueue.Enqueue(new WorkItem());

// All collections:
// - Automatically tracked for scaling metrics
// - Auto-disposed when plugin disposes
// - Support optional auto-persistence via StateStore
// - Have built-in LRU/FIFO eviction
```

---

### Scaling & Backpressure

Every plugin automatically reports scaling metrics:

```csharp
// Plugin implements IScalableSubsystem (via PluginBase)
public override IReadOnlyDictionary<string, object> GetScalingMetrics()
{
    // Base implementation reports:
    // - cache.size (total entries in all tracked collections)
    // - cache.capacity (total capacity of all tracked collections)
    // - cache.fillRatio (entries / capacity)
    // - backpressure.state (Normal, Warning, Critical, Shedding)
    // - strategy.count (number of registered strategies)

    var metrics = base.GetScalingMetrics();

    // Add custom metrics
    metrics["custom.metric"] = _customValue;

    return metrics;
}

// Reconfigure limits at runtime
public override Task ReconfigureLimitsAsync(Scaling.ScalingLimits limits, CancellationToken ct)
{
    // Base implementation updates limits and recalculates backpressure
    return base.ReconfigureLimitsAsync(limits, ct);
}

// Query current backpressure state
var state = CurrentBackpressureState; // Normal, Warning, Critical, or Shedding

// Backpressure is calculated based on collection fill ratios:
// >= 95% fill → Shedding
// >= 85% fill → Critical
// >= 70% fill → Warning
// < 70% → Normal
```

---

### Policy Engine

All plugins have access to the v6.0 Policy Engine:

```csharp
// PolicyContext automatically injected
protected PolicyContext PolicyContext { get; private set; }

// Check if policies are available
if (PolicyContext.IsAvailable)
{
    // Query policies
    var policies = PolicyContext.GetApplicablePolicies(...);

    // Enforce policy checks
    var allowed = PolicyContext.IsOperationAllowed(...);
}

// Plugins do not set PolicyContext themselves
// The kernel injects it via SetPolicyContext()
```

---

### Disposal Pattern

Both sync and async disposal are fully implemented:

```csharp
// Automatic sync disposal
plugin.Dispose();

// Automatic async disposal
await plugin.DisposeAsync();

// Both paths:
// 1. Dispose all tracked collections
// 2. Shutdown all registered strategies
// 3. Dispose message bus subscriptions
// 4. Unregister from capability registry
// 5. Remove knowledge from knowledge lake

// Override only if you have additional cleanup
protected override void Dispose(bool disposing)
{
    if (disposing)
    {
        // Your cleanup here
    }
    base.Dispose(disposing);
}

protected override async ValueTask DisposeAsyncCore()
{
    // Your async cleanup here
    await base.DisposeAsyncCore();
}
```

---

## 4. What Plugins MUST Override

These abstract members have no default implementation:

```csharp
// Unique stable identifier (e.g., "com.company.plugin.name")
public abstract string Id { get; }

// Human-readable name (e.g., "AES-256 Encryption")
public abstract string Name { get; }

// Plugin category enumeration
public abstract PluginCategory Category { get; }
```

That's it. Everything else is optional.

---

## 5. What Plugins SHOULD Override

These have defaults but commonly need customization:

### Capabilities & Knowledge

```csharp
// Describe what your plugin does
protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
    => new[] { /* your capabilities */ };

// Rich capability metadata
protected override List<RegisteredCapability> GetCapabilityRegistrations()
    => new List<RegisteredCapability> { /* detailed registrations */ };

// Static knowledge for AI discovery
protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    => new[] { /* your knowledge */ };

// Get current configuration state
protected override Dictionary<string, object> GetConfigurationState()
    => new Dictionary<string, object> { /* config values */ };

// Handle dynamic knowledge queries
protected override Task<IReadOnlyList<KnowledgeObject>> HandleDynamicKnowledgeQueryAsync(
    KnowledgeRequest request, CancellationToken ct)
    => Task.FromResult<IReadOnlyList<KnowledgeObject>>(/* your responses */);
```

### Lifecycle

```csharp
// Custom initialization (always call base first)
public override async Task InitializeAsync(CancellationToken ct)
{
    await base.InitializeAsync(ct);
    // Your init
}

// Main execution logic
public override async Task ExecuteAsync(CancellationToken ct)
{
    await base.ExecuteAsync(ct);
    // Your processing
}

// Custom shutdown (always call base)
public override async Task ShutdownAsync(CancellationToken ct)
{
    // Your cleanup
    await base.ShutdownAsync(ct);
}
```

### Health & Configuration

```csharp
// Report actual health status
public override async Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct)
{
    // Your health check logic
}

// React to configuration changes
public override Task OnConfigurationChangedAsync(DataWarehouseConfiguration newConfig, CancellationToken ct)
{
    await base.OnConfigurationChangedAsync(newConfig, ct);
    // Your reaction
}
```

### Strategies

```csharp
// Provide default strategy when none specified
protected override string? GetDefaultStrategyId() => "my-default-strategy";
```

---

## 6. What Strategies Get For Free

### Lifecycle

```csharp
// Initialize (idempotent—safe to call multiple times)
await strategy.InitializeAsync(cancellationToken);

// Shutdown
await strategy.ShutdownAsync(cancellationToken);
```

### Counters

```csharp
// Track operation counts
IncrementCounter("encryption_operations");
var count = GetCounter("encryption_operations");

// Snapshot all counters
var allCounters = GetAllCounters();

// Reset all to zero
ResetCounters();
```

### Retry with Backoff

```csharp
// Execute with exponential backoff + jitter
var result = await ExecuteWithRetryAsync<T>(
    operation: async (ct) => await MyOperation(ct),
    maxRetries: 3,
    baseDelay: TimeSpan.FromMilliseconds(100),
    ct: cancellationToken
);

// Override to classify transient errors
protected override bool IsTransientException(Exception ex)
    => ex is TimeoutException or IOException;
```

### Health Caching

```csharp
// Get cached health check with TTL
var health = await GetCachedHealthAsync(
    healthCheck: async (ct) => await CheckHealth(ct),
    cacheDuration: TimeSpan.FromSeconds(30),
    ct: cancellationToken
);
```

### Configuration Access

```csharp
// Read-only access to global configuration
var fipsEnabled = SystemConfiguration.SecuritySettings.FipsMode;

// Configuration injected by parent plugin
// (called via InjectConfiguration by plugin)
```

---

## 7. What Strategies MUST Override

These abstract members have no default:

```csharp
// Unique stable identifier
public abstract string StrategyId { get; }

// Human-readable name
public abstract string Name { get; }
```

---

## 8. What Strategies SHOULD Override

### Lifecycle

```csharp
// Override to acquire resources
protected override async Task InitializeAsyncCore(CancellationToken cancellationToken)
{
    // Your init logic
}

// Override to release resources
protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken)
{
    // Your cleanup logic
}
```

### Metadata

```csharp
// Describe this strategy
public override string Description => "AES-256-GCM encryption with key rotation";

// Report performance characteristics
public override IReadOnlyDictionary<string, object> Characteristics
    => new Dictionary<string, object>
    {
        ["throughput"] = "1GB/s per core",
        ["cpuIntensive"] = true,
        ["hardware"] = new[] { "AES-NI" }
    };

// Declare level of operation
public override StrategyApplicability Applicability => StrategyApplicability.DataOnly;
```

### Error Handling

```csharp
// Classify errors as transient for retry logic
protected override bool IsTransientException(Exception ex)
    => ex is TimeoutException or OperationCanceledException;
```

---

## 9. Common Patterns

### Discover and Register Strategies

```csharp
public override async Task InitializeAsync(CancellationToken ct)
{
    await base.InitializeAsync(ct);

    // Auto-discover all IEncryptionStrategy implementations in this assembly
    var discovered = DiscoverStrategiesFromAssembly(GetType().Assembly);

    // Set a default
    StrategyRegistry.SetDefault("aes-256-gcm");
}
```

### Strategy Dispatch Pattern

```csharp
public async Task<byte[]> EncryptAsync(byte[] data, string strategyId, CancellationToken ct)
{
    // Execute operation against a strategy
    return await ExecuteWithStrategyAsync<IEncryptionStrategy, byte[]>(
        strategyId: strategyId,
        identity: null, // optional: provide for ACL
        operation: async (strategy) => await strategy.EncryptAsync(data, ct),
        ct: ct
    );
}
```

### ACL Enforcement Pattern

```csharp
// Set an ACL provider (optional)
StrategyAclProvider = new MyAclProvider();

// Resolve strategy—will check ACL if provider set
var strategy = ResolveStrategy<IEncryptionStrategy>("aes-256-gcm", identity);
```

### Intelligence Graceful Degradation

```csharp
public override async Task OnStartWithIntelligenceAsync(CancellationToken ct)
{
    // Intelligence is available—enable enhanced features
    if (HasCapability(IntelligenceCapabilities.Embeddings))
    {
        // Initialize embedding-based features
    }
}

public override async Task OnStartWithoutIntelligenceAsync(CancellationToken ct)
{
    // Intelligence unavailable—use fallback behavior
    // Still fully functional, just without AI enhancements
}
```

---

## 10. Pipeline Orchestration

### IPipelineOrchestrator

The v5.0 Pipeline Orchestrator manages data flow through DataPipeline stages:

```csharp
// Plugins express their position in the pipeline
public override int DefaultPipelineOrder => 50;  // Run early
public override IReadOnlyList<string> RequiredPrecedingStages => new[] { "compression" };
public override IReadOnlyList<string> IncompatibleStages => new[] { "raw-storage" };
public override bool MutatesData => true; // We encrypt the data
```

The kernel uses this metadata to:
1. Order stages correctly (honor dependencies)
2. Detect conflicts (prevent incompatible stages from running together)
3. Optimize pipeline (bypass stages if content doesn't need them)
4. Report throughput and backpressure

---

## 11. Key Design Rules

### AD-05: Strategies Are Workers, Not Orchestrators

Per Architectural Decision AD-05:

- **Strategies do NOT have:**
  - Intelligence/AI capability discovery
  - Capability registry access
  - Knowledge bank access
  - Message bus subscriptions (except legacy ConfigureIntelligence for backward compatibility)
  - Plugin state persistence

- **Strategies ARE:**
  - Pure workers that execute operations
  - Stateless or isolated-state
  - Initialized and managed by their parent plugin
  - Counted, retried, and health-checked by their parent

**Why?** Reduces complexity, prevents distributed deadlocks, centralizes decision-making at the plugin layer.

### Plugin Isolation

Plugins reference ONLY the SDK via `<ProjectReference>`:

```xml
<ItemGroup>
    <ProjectReference Include="...\DataWarehouse.SDK\DataWarehouse.SDK.csproj" />
</ItemGroup>
```

No direct references to other plugins. All communication via MessageBus.

### Communication via MessageBus

All inter-plugin coordination flows through the message bus:

```csharp
await MessageBus.PublishAsync("encryption.strategy.changed", new PluginMessage { ... });
```

### Base Classes Are Mandatory

Never implement `IPlugin` or `IStrategy` directly. Always inherit from the appropriate base class:

- For plugins → inherit from `PluginBase` or one of its domain-specific descendants
- For strategies → inherit from `StrategyBase` or a domain-specific base

### Bounded Collections Everywhere

Use bounded collections instead of unbounded ones to prevent memory exhaustion:

```csharp
// Good
var cache = CreateBoundedDictionary<string, T>("cache", 1000);

// Bad
var cache = new Dictionary<string, T>();
```

### Rule 13: No Stubs/Mocks/Placeholders

Every implementation must be production-ready for real use. No "TODO" implementations, no stubs. If a capability is present, it must work correctly.

---

## 12. Summary: Free Features Checklist

When you inherit from `PluginBase`, you automatically get:

- [x] **Lifecycle management** (Init, Activate, Execute, Shutdown)
- [x] **State persistence** (SaveStateAsync, LoadStateAsync)
- [x] **Strategy registry** (generic, type-safe, auto-discovery)
- [x] **Capability registration** (auto on handshake)
- [x] **Knowledge registration** (auto capability discovery for AI)
- [x] **Message bus** (inter-plugin communication)
- [x] **Health checks** (default: Healthy)
- [x] **Configuration injection** (global + strategy propagation)
- [x] **Hot reload** (state and strategy preservation)
- [x] **Bounded collections** (LRU eviction, auto-tracking)
- [x] **Scaling metrics** (IScalableSubsystem)
- [x] **Backpressure reporting** (collection fill ratio)
- [x] **Policy engine access** (v6.0 enforcement)
- [x] **Disposal** (sync + async, cascading cleanup)

When you inherit from `StrategyBase`, you automatically get:

- [x] **Lifecycle** (Initialize, Shutdown, idempotent)
- [x] **Counters** (thread-safe tracking)
- [x] **Retry logic** (exponential backoff + jitter)
- [x] **Health caching** (with TTL)
- [x] **Configuration access** (read-only, injected)
- [x] **Disposal** (sync + async)

All without writing a single line of code.

---

## 13. Next Steps

Once you understand what's free:

1. **Choose your base class**: PluginBase (or Feature/DataPipeline for higher semantics)
2. **Implement abstract members**: Id, Name, Category (plugins); StrategyId, Name (strategies)
3. **Override key methods**: Capabilities, Lifecycle, Health
4. **Use built-in utilities**: Strategy dispatch, bounded collections, scaling
5. **Follow design rules**: Plugin isolation, message bus communication, no stubs

The SDK does the heavy lifting. You focus on business logic.

---

## 14. SDK Internal Wiring (What Plugins Get for Free)

This section documents the exact wiring that `PluginBase` and `StrategyBase` perform automatically on every plugin's behalf. Plugins inherit this behaviour and do **not** need to implement it themselves. When a plugin needs to extend or react to these flows, it overrides the documented hook method rather than re-implementing the mechanism from scratch.

---

### 14.1 Strategy Lifecycle Wiring

**Automatic — no plugin code required.**

`PluginBase.InitializeAsync` creates the `StateStore`, seeds the knowledge cache, and triggers `OnHandshakeAsync` → `RegisterWithSystemAsync`. It does **not** auto-discover strategies; that is a deliberate call from the plugin's own `InitializeAsync` override so the plugin controls timing.

The standard wiring pattern a plugin performs once in its `InitializeAsync`:

```csharp
public override async Task InitializeAsync(CancellationToken ct)
{
    await base.InitializeAsync(ct);   // StateStore, knowledge cache, registration

    // Discovery: reflects over this assembly for all non-abstract IStrategy types,
    // instantiates each via Activator.CreateInstance, and registers in the registry.
    DiscoverStrategiesFromAssembly(GetType().Assembly);

    // After discovery the plugin may configure strategies individually.
    // The base class propagates SystemConfiguration to all registered StrategyBase
    // instances inside OnConfigurationChangedAsync (see §14.2).
}
```

`DiscoverStrategiesFromAssembly` (on `StrategyRegistry<IStrategy>`) scans the supplied assemblies for concrete types that implement `IStrategy`, instantiates each one with `Activator.CreateInstance`, and calls `StrategyRegistry.Register`. Types that cannot be instantiated (e.g., missing parameterless constructor) are silently skipped.

`PluginBase.DisposeAsync` / `Dispose` reverse the lifecycle automatically:

- Calls `strategy.ShutdownAsync()` on every registered strategy (async path).
- Calls `strategy.Dispose()` / `strategy.DisposeAsync()` on every registered strategy.
- Disposes all tracked bounded collections.
- Unregisters from the capability registry and removes knowledge from the knowledge lake.
- Disposes all message bus subscriptions created during `RegisterWithSystemAsync`.

Plugins override `ShutdownAsync` only to clean up their own resources, not strategy resources.

---

### 14.2 Configuration Propagation

**Automatic — triggered by the kernel at runtime.**

`PluginBase.OnConfigurationChangedAsync` is called by the kernel's `ConfigurationHotReloader` whenever the system configuration changes. The base implementation:

1. Stores the new configuration object in `SystemConfiguration`.
2. Iterates every strategy registered in `_strategyRegistry`.
3. For each strategy that is a `StrategyBase`, calls `sb.InjectConfiguration(newConfig)`.

Strategies therefore always hold a current snapshot of `SystemConfiguration` without any plugin-level code. The plugin overrides `OnConfigurationChangedAsync` only to react to specific values (e.g., resize a connection pool, flush a cache):

```csharp
public override async Task OnConfigurationChangedAsync(
    DataWarehouseConfiguration newConfig,
    CancellationToken ct)
{
    // Always call base first — propagates to all strategies automatically.
    await base.OnConfigurationChangedAsync(newConfig, ct);

    // Plugin-specific reaction (optional).
    if (newConfig.StorageSettings.MaxConcurrency != _currentConcurrency)
        await ResizePoolAsync(newConfig.StorageSettings.MaxConcurrency, ct);
}
```

`StrategyBase.InjectConfiguration` is `internal` — only the SDK can call it. Strategies never call it themselves.

---

### 14.3 Hot Reload Wiring

**Automatic — triggered by the kernel's hot-reload subsystem.**

#### Before reload — `OnReloadingAsync`

The base implementation:

1. Reads all strategy IDs from `_strategyRegistry.GetAll()`.
2. Serializes the ID list as UTF-8 bytes and persists it under the reserved key `__reload.strategy_ids` via `SaveStateAsync`.
3. Calls `strategy.ShutdownAsync(ct)` on every registered strategy (best-effort; exceptions are swallowed to avoid blocking the reload).

#### After reload — `OnReloadedAsync`

The base implementation:

1. Calls `DiscoverStrategiesFromAssembly(GetType().Assembly)` — picks up any newly compiled strategy types in the reloaded assembly.
2. For every strategy now in the registry, calls `sb.InjectConfiguration(SystemConfiguration)` to restore current config.
3. Calls `strategy.InitializeAsync(ct)` on each strategy (best-effort; strategies that are already initialised are idempotent).
4. Calls `RegisterWithSystemAsync(ct)` — re-registers capabilities with the capability registry and re-publishes static knowledge to the knowledge lake.

Plugins override these hooks only to preserve additional in-memory state that lives outside the strategy registry:

```csharp
public override async Task OnReloadingAsync(CancellationToken ct)
{
    await base.OnReloadingAsync(ct);   // saves strategy IDs, shuts down strategies

    // Persist any extra in-memory state the plugin owns.
    await SaveStateAsync("my.state", SerializeMyState(), ct);
}

public override async Task OnReloadedAsync(CancellationToken ct)
{
    await base.OnReloadedAsync(ct);   // re-discovers, re-configures, re-registers

    // Restore the extra state.
    var raw = await LoadStateAsync("my.state", ct);
    if (raw != null) RestoreMyState(raw);
}
```

---

### 14.4 Persistence Wiring

**Automatic — wired in `InitializeAsync`, routed through the message bus.**

`PluginBase.InitializeAsync` calls `CreateCustomStateStore()` and assigns the result to `StateStore`. The default implementation of `CreateCustomStateStore` is:

```csharp
protected virtual IPluginStateStore? CreateCustomStateStore()
    => MessageBus != null ? new DefaultPluginStateStore(MessageBus) : null;
```

`DefaultPluginStateStore` routes every persistence operation through the message bus using these internal topics:

| Topic | Operation |
|---|---|
| `dw.internal.plugin-state.write` | Save data |
| `dw.internal.plugin-state.read` | Load data |
| `dw.internal.plugin-state.delete` | Delete entry |
| `dw.internal.plugin-state.list` | List keys |
| `dw.internal.plugin-state.exists` | Check existence |

All paths use the canonical format `dw://internal/plugin-state/{pluginId}/{key}`. The storage backend (disk, cloud, Raft-replicated) is determined by whatever subscriber handles those topics — the plugin is decoupled from it entirely.

`SaveStateAsync` on `PluginBase` wraps the store call with two hooks:

```csharp
protected async Task SaveStateAsync(string key, byte[] data, CancellationToken ct)
{
    if (StateStore == null) return;
    await OnBeforeStatePersistAsync(ct);          // hook (default: no-op)
    await StateStore.SaveAsync(Id, key, data, ct);
    await OnAfterStatePersistAsync(ct);           // hook (default: no-op)
}
```

Override `OnBeforeStatePersistAsync` to validate or encrypt data before storage; override `OnAfterStatePersistAsync` to emit metrics or audit events after storage completes.

To disable persistence entirely (volatile plugin), return `null`:

```csharp
protected override IPluginStateStore? CreateCustomStateStore() => null;
```

`BoundedList<T>` and `BoundedQueue<T>` created via `CreateBoundedList` / `CreateBoundedQueue` receive the `StateStore` and plugin `Id` directly, enabling optional auto-persistence of their contents without any additional plugin code.

---

### 14.5 Intelligence / MessageBus Wiring

**MessageBus is injected by the kernel; forwarding to strategies is the plugin's responsibility.**

The kernel calls `InjectKernelServices(messageBus, capabilityRegistry, knowledgeLake)` on every plugin before `InitializeAsync`. This method is `sealed` (ISO-01 mitigation, CVSS 9.4) — plugins cannot intercept it. After injection, the protected `OnKernelServicesInjected()` hook fires and plugins may subscribe to bus topics there.

`PluginBase` does **not** automatically forward `MessageBus` to strategies. The plugin forwards it explicitly when needed, typically in `InitializeAsync` after strategy discovery:

```csharp
foreach (var strategy in StrategyRegistry.GetAll())
{
    if (strategy is StrategyBase sb)
        sb.ConfigureIntelligence(MessageBus);  // opt-in per AD-05
}
```

`StrategyBase.ConfigureIntelligence(IMessageBus?)` sets the strategy's `MessageBus` property. Strategies that receive the bus guard all usage behind `IsIntelligenceAvailable`:

```csharp
if (IsIntelligenceAvailable)
{
    await MessageBus!.PublishAsync("my.event", message, ct);
}
```

Per AD-05, strategies do NOT subscribe to bus topics and do NOT access the capability registry or knowledge lake. Those remain the plugin's domain.

`PluginBase.RegisterWithSystemAsync` (called from `OnHandshakeAsync` → `InitializeAsync`) automatically:

1. Calls `RegisterCapabilitiesAsync` — iterates `GetCapabilityRegistrations()` and calls `CapabilityRegistry.RegisterAsync` for each entry not yet registered.
2. Calls `RegisterStaticKnowledgeAsync` — calls `GetStaticKnowledge()` and stores each object in the knowledge lake.
3. Calls `SubscribeToKnowledgeQueries` — subscribes to `knowledge.query.{Id}` on the message bus, routing incoming `KnowledgeRequest` messages to `HandleDynamicKnowledgeQueryAsync`.

On `UnregisterFromSystemAsync` (called from `ShutdownAsync` and `DisposeAsync`), the SDK automatically disposes all knowledge subscriptions, calls `CapabilityRegistry.UnregisterPluginAsync(Id)`, and calls `KnowledgeLake.RemoveByPluginAsync(Id)`.

---

### 14.6 Capability Registration

**Automatic — performed during `InitializeAsync` via `OnHandshakeAsync`.**

`RegisterCapabilitiesAsync` iterates the list returned by `GetCapabilityRegistrations()`, skips any capability ID already in `_registeredCapabilityIds` (idempotent), and calls `CapabilityRegistry.RegisterAsync(capability, ct)`. On success the ID is tracked for cleanup.

The default `GetCapabilityRegistrations()` converts each `PluginCapabilityDescriptor` returned by `GetCapabilities()` into a `RegisteredCapability`, mapping `PluginCategory` to `CapabilityCategory` automatically. Plugins override `GetCapabilityRegistrations()` to supply richer metadata.

Capability registration from strategy `Characteristics` happens at the **plugin** layer (never at the strategy layer, per AD-05): the plugin reads `strategy.Characteristics` during or after discovery and incorporates those values into the capability descriptors it returns from `GetCapabilityRegistrations()`. Strategies never call the registry directly.

On `UnregisterFromSystemAsync` all tracked capability IDs are batch-unregistered via `CapabilityRegistry.UnregisterPluginAsync(Id)`.

---

### 14.7 Scaling / Backpressure Wiring

**Automatic — driven by tracked bounded collections.**

`PluginBase` implements `IScalableSubsystem`. Every collection created via `CreateBoundedDictionary`, `CreateBoundedList`, or `CreateBoundedQueue` is added to `_trackedCollections`.

`GetScalingMetrics()` (default implementation, no override required) computes:

- **`cache.size`** — total `Count` across all tracked collections.
- **`cache.capacity`** — total `MaxCapacity` across all tracked collections.
- **`cache.fillRatio`** — `size / capacity` (0.0 – 1.0).
- **`cache.collectionCount`** — number of tracked collections.
- **`backpressure.state`** — `Normal | Warning | Critical | Shedding`.
- **`strategy.count`** — number of registered strategies (when registry is used).

Backpressure thresholds (applied to the maximum fill ratio across all collections):

| Fill ratio | State |
|---|---|
| < 0.70 | `Normal` |
| >= 0.70 | `Warning` |
| >= 0.85 | `Critical` |
| >= 0.95 | `Shedding` |

`ReconfigureLimitsAsync(ScalingLimits, ct)` updates `_currentLimits` and triggers `RecalculateBackpressureState()` immediately.

Plugins that add custom collections outside the factory methods must override `GetScalingMetrics()` to include them, calling `base.GetScalingMetrics()` to merge the automatic metrics:

```csharp
public override IReadOnlyDictionary<string, object> GetScalingMetrics()
{
    var metrics = new Dictionary<string, object>(base.GetScalingMetrics());
    metrics["pending.uploads"] = _uploadQueue.Count;
    return metrics;
}
```

---

### 14.8 Health Check Wiring

**Provided by `StrategyBase` — no plugin-level plumbing required.**

`StrategyBase.GetCachedHealthAsync` wraps any health-check delegate with a TTL-based cache and a `SemaphoreSlim(1,1)` double-check lock:

```csharp
// Inside a strategy — call this instead of running the check every time.
var result = await GetCachedHealthAsync(
    healthCheck: async (ct) =>
    {
        var ok = await _connection.PingAsync(ct);
        return new StrategyHealthCheckResult(ok, ok ? "Connected" : "Unreachable");
    },
    cacheDuration: TimeSpan.FromSeconds(30),
    ct: cancellationToken
);
```

The cache is per-strategy instance. If `cacheDuration` is omitted the default is 30 seconds. The lock prevents thundering-herd re-computation when the TTL expires under concurrent load.

At the plugin level, `PluginBase.CheckHealthAsync` defaults to `Healthy`. Plugins override it to aggregate strategy health:

```csharp
public override async Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct)
{
    foreach (var s in StrategyRegistry.GetAll().OfType<MyStrategyBase>())
    {
        var h = await s.GetCachedHealthAsync(s.CheckHealthCoreAsync, ct: ct);
        if (!h.IsHealthy)
            return HealthCheckResult.Degraded(h.Message ?? "Strategy unhealthy");
    }
    return HealthCheckResult.Healthy($"{Name} is healthy");
}
```

---

### 14.9 Retry Wiring

**Provided by `StrategyBase` — available to all strategies without any setup.**

`ExecuteWithRetryAsync<T>` implements exponential backoff with cryptographic jitter:

```csharp
var result = await ExecuteWithRetryAsync<MyResult>(
    operation:   async (ct) => await _client.CallAsync(ct),
    maxRetries:  3,
    baseDelay:   TimeSpan.FromMilliseconds(100),
    ct:          cancellationToken
);
// Delays (approximate): 100 ms, 200 ms, 400 ms — each +0–50 ms random jitter.
// Throws AggregateException after maxRetries+1 total attempts.
```

The backoff formula: `delay = baseDelay * 2^attempt + Uniform(0, 50ms)`.

Jitter uses `System.Security.Cryptography.RandomNumberGenerator.GetInt32` — cryptographically random, not `System.Random`.

Only exceptions classified as transient by `IsTransientException` trigger a retry. The default always returns `false` (no retries). Override to classify domain errors:

```csharp
protected override bool IsTransientException(Exception ex)
    => ex is TimeoutException
    or IOException { HResult: unchecked((int)0x80070079) }  // ERROR_SEM_TIMEOUT
    or SocketException { SocketErrorCode: SocketError.TimedOut };
```

Cancellation tokens propagate correctly: `OperationCanceledException` is never retried regardless of `IsTransientException`.

---

### 14.10 Counter Wiring

**Provided by `StrategyBase` — zero setup, thread-safe.**

`StrategyBase` maintains a `BoundedDictionary<string, long>` with capacity 1,000 for counter storage. All four methods are immediately available in any strategy:

```csharp
// Increment (uses Interlocked.Increment internally — safe under concurrent calls)
IncrementCounter("writes");
IncrementCounter("bytes_written");  // call once per byte batch, not per byte

// Read
long writes = GetCounter("writes");

// Snapshot (returns a copy — safe to iterate without locking)
IReadOnlyDictionary<string, long> all = GetAllCounters();

// Reset (clears all entries — useful on rotation/flush boundaries)
ResetCounters();
```

The `BoundedDictionary` backing store provides LRU eviction if more than 1,000 distinct counter names are used, preventing unbounded memory growth from code that generates dynamic counter names. For well-designed strategies this limit is never approached.

Counters are per-instance and in-memory only. They are not persisted through hot reload or plugin restart. If persistence is needed, save counter snapshots in `OnReloadingAsync` via `SaveStateAsync`.

---

### 14.11 Summary: Automatic vs. Override

| Wiring concern | Automatic | Plugin overrides |
|---|---|---|
| Strategy discovery | `DiscoverStrategiesFromAssembly` (explicit call, not implicit) | Timing of discovery call |
| Strategy init / shutdown | SDK calls `InitializeAsync` / `ShutdownAsync` after discovery / before disposal | `InitializeAsyncCore`, `ShutdownAsyncCore` on strategy |
| Config injection to strategies | `OnConfigurationChangedAsync` base | Custom reaction after `base.OnConfigurationChangedAsync` |
| Config injection timing | Kernel calls `InjectConfiguration` before `InitializeAsync` | — |
| State store creation | `CreateCustomStateStore()` default makes `DefaultPluginStateStore` | Return custom or `null` |
| Pre/post persist hooks | Called inside `SaveStateAsync` | `OnBeforeStatePersistAsync`, `OnAfterStatePersistAsync` |
| Hot reload: save strategy IDs | `OnReloadingAsync` base | Extra state in override |
| Hot reload: re-discover + re-register | `OnReloadedAsync` base | Restore extra state in override |
| Capability registration | `RegisterCapabilitiesAsync` in `InitializeAsync` | `GetCapabilityRegistrations()` content |
| Knowledge registration | `RegisterStaticKnowledgeAsync` in `InitializeAsync` | `GetStaticKnowledge()` content |
| Knowledge query subscription | `SubscribeToKnowledgeQueries` in `InitializeAsync` | `HandleDynamicKnowledgeQueryAsync` |
| Capability unregistration | `UnregisterFromSystemAsync` in `ShutdownAsync` / `DisposeAsync` | — |
| MessageBus injection to strategies | NOT automatic — plugin forwards explicitly | Call `sb.ConfigureIntelligence(MessageBus)` after discovery |
| Scaling metrics | `GetScalingMetrics()` base reads tracked collections | Override to add custom metrics |
| Backpressure state | `RecalculateBackpressureState()` on limit change | — |
| Strategy health caching | `GetCachedHealthAsync` on `StrategyBase` | Supply delegate + TTL |
| Retry with backoff | `ExecuteWithRetryAsync` on `StrategyBase` | `IsTransientException` classification |
| Operation counters | `IncrementCounter` / `GetCounter` / etc. on `StrategyBase` | — |
| Disposal cascade | `Dispose` / `DisposeAsync` on base | Add cleanup before `base.Dispose` |

---

## 15. Kernel Architecture & Services

The DataWarehouse Kernel (`DataWarehouseKernel`) is the central orchestrator that loads plugins, routes messages, manages pipelines, and provides storage abstraction. It is the only component that touches all subsystems. Plugins never reference the kernel directly; they interact through the interfaces injected during initialization (`IMessageBus`, `IPluginCapabilityRegistry`, `IKernelContext`).

---

### 15.1 Kernel Lifecycle

#### Construction (via KernelBuilder)

The kernel is created through a fluent builder:

```csharp
var kernel = await KernelBuilder.Create()
    .WithKernelId("my-node")
    .WithOperatingMode(OperatingMode.Server)
    .WithRootPath("/data/warehouse")
    .WithPluginPath("/plugins")
    .UseInMemoryStorage()
    .AutoStartFeatures()
    .WithLogger(logger)
    .WithDefaultPipeline()
    .BuildAndInitializeAsync(ct);
```

`KernelBuilder` methods:

| Method | Purpose |
|---|---|
| `WithKernelId(string)` | Set unique kernel instance identifier |
| `WithOperatingMode(OperatingMode)` | Set mode: Workstation, Server, Hyperscale, etc. |
| `WithRootPath(string)` | Set root data directory |
| `WithPluginPath(string)` | Add plugin DLL search directory |
| `WithPluginPaths(params string[])` | Add multiple plugin directories |
| `WithPipelineConfiguration(PipelineConfiguration)` | Set pipeline configuration |
| `WithDefaultPipeline()` | Use default: Compress then Encrypt |
| `WithPipelineOrder(params string[])` | Custom stage ordering |
| `UseInMemoryStorage()` | Use built-in volatile storage as primary |
| `AutoStartFeatures(bool)` | Auto-start `IFeaturePlugin` instances on load |
| `WithPlugin(IPlugin)` | Pre-register a plugin instance |
| `WithPlugins(params IPlugin[])` | Pre-register multiple plugins |
| `WithPrimaryStorage(IStorageProvider)` | Set primary storage provider |
| `WithCacheStorage(IStorageProvider)` | Set cache storage provider |
| `WithLogger(ILogger<DataWarehouseKernel>)` | Set logger |
| `WithLoggerFactory(ILoggerFactory)` | Set logger factory |
| `Configure(Action<DataWarehouseKernel>)` | Post-build action |
| `Build()` | Build kernel (does not initialize) |
| `BuildAndInitializeAsync(ct)` | Build, initialize, and register pre-added plugins |

#### Startup Sequence (InitializeAsync)

The kernel initialization follows this exact order:

1. **Acquire init lock** -- prevents concurrent initialization via `SemaphoreSlim`.
2. **Load unified configuration** -- reads `./config/datawarehouse-config.xml` (or creates default via `ConfigurationPresets.CreateStandard()`).
3. **Initialize audit log** -- creates `ConfigurationAuditLog` at `./config/config-audit.log`.
4. **Validate environment overrides (INFRA-02)** -- scans `DATAWAREHOUSE_*` environment variables, blocks security-sensitive overrides in production mode (`Server`, `Hyperscale`), logs warnings for sensitive keys.
5. **Audit startup event (INFRA-03)** -- logs `kernel.lifecycle.startup` to the audit log.
6. **Subscribe to auditable events** -- wires up message bus subscriptions for 9 auditable operation classes (config changes, plugin lifecycle, security events, key rotation, replay detection, capability changes, authentication).
7. **Publish `system.startup`** -- broadcasts startup event on the message bus.
8. **Register built-in plugins** -- creates and registers `InMemoryStoragePlugin`; sets it as primary storage if `UseInMemoryStorageByDefault` is configured.
9. **Load plugins from paths** -- iterates configured plugin directories, loads each DLL through `PluginLoader` (security validation, isolated `AssemblyLoadContext`), injects kernel services.
10. **Set pipeline configuration** -- configures `DefaultPipelineOrchestrator` with the kernel's pipeline configuration.
11. **Initialize storage** -- auto-selects primary storage from registered `IStorageProvider` plugins if not explicitly set.
12. **Mark initialized** -- sets `_isInitialized = true`.

#### Plugin Registration (RegisterPluginAsync)

When a plugin is registered (either during initialization or at runtime):

1. **Handshake** -- kernel sends `HandshakeRequest` (KernelId, ProtocolVersion, OperatingMode, RootPath, Timestamp). Plugin returns `HandshakeResponse`.
2. **Inject kernel services** -- if plugin is a `PluginBase`, calls `InjectKernelServices(enforcedMessageBus, capabilityRegistry, null)`. This is a **sealed** method (ISO-01 mitigation) -- plugins cannot intercept it.
3. **Inject configuration** -- calls `InjectConfiguration(currentConfiguration)` on the plugin.
4. **Register in PluginRegistry** -- adds the plugin to the registry by ID and category.
5. **Register pipeline stage** -- if the plugin implements `IDataTransformation`, registers it with the `PipelineOrchestrator`.
6. **Auto-start features** -- if the plugin implements `IFeaturePlugin` and `AutoStartFeatures` is enabled, starts it in a background job.
7. **Audit** -- logs plugin registration to the audit log.
8. **Publish `plugin.loaded`** -- broadcasts the event on the message bus.

#### Plugin Discovery & Loading (PluginLoader)

`PluginLoader` provides secure, isolated plugin loading:

**Security validation pipeline** (per assembly):

| Check | Description |
|---|---|
| File size | Rejects assemblies > `MaxAssemblySize` (default 50MB) |
| Blocklist | Rejects assemblies in `BlockedAssemblies` set |
| Allowed prefixes | If configured, only loads assemblies matching allowed name prefixes |
| SHA-256 hash | Optional verification against known hash manifest |
| Strong name | Optional strong-name signature validation |
| Trusted publishers | Optional whitelist of trusted public key tokens |
| Memory budget | Checks `BoundedMemoryRuntime` for available memory (EDGE-06) |

**Assembly isolation**: Each plugin assembly is loaded into a collectible `AssemblyLoadContext` (`PluginLoadContext`). This enables:

- Plugin unloading without kernel restart
- Assembly-level isolation (dependency conflicts avoided)
- GC-collectible contexts for memory reclamation

**Hot reload** (`ReloadPluginAsync`):

1. Fires `OnPluginReloading` event (phase: Starting).
2. Unloads the old plugin (unregisters from registry, unloads `AssemblyLoadContext`, forces GC).
3. Loads the new assembly version through the security validation pipeline.
4. Re-registers all discovered plugin types.
5. Fires `OnPluginReloaded` event (phase: Completed or Failed).

```csharp
// Reload a single plugin
var result = await pluginLoader.ReloadPluginAsync("com.example.myplugin", ct);

// Reload all loaded plugins
var results = await pluginLoader.ReloadAllAsync(ct);
```

#### Shutdown Sequence (DisposeAsync)

1. **Audit shutdown event** -- logs `kernel.lifecycle.shutdown`.
2. **Signal shutdown** -- cancels the `CancellationTokenSource` shared by all background jobs.
3. **Publish `system.shutdown`** -- broadcasts shutdown event on the message bus.
4. **Stop feature plugins** -- calls `StopAsync()` on each `IFeaturePlugin` with a **30-second per-plugin timeout** (ISO-04). Plugins exceeding the timeout are logged and skipped.
5. **Wait for background jobs** -- waits up to 10 seconds for all background jobs to complete.
6. **Dispose resources** -- disposes `CancellationTokenSource`, `SemaphoreSlim`, `PluginLoader`.

#### Background Jobs

The kernel provides a lightweight background job facility:

```csharp
// Run a background job (auto-linked to kernel shutdown CancellationToken)
string jobId = kernel.RunInBackground(async ct =>
{
    await DoWorkAsync(ct);
}, jobId: "optional-id");
```

Jobs are tracked in a `BoundedDictionary<string, Task>` (max 1000). They auto-remove on completion and are cancelled on kernel shutdown.

---

### 15.2 Plugin Registry

`PluginRegistry` manages all loaded plugins and provides lookup by ID, type, and category.

**Key properties and methods:**

| Member | Signature | Description |
|---|---|---|
| `Count` | `int Count` | Number of registered plugins |
| `OperatingMode` | `OperatingMode OperatingMode` | Current operating mode (for intelligent selection) |
| `Register` | `void Register(IPlugin plugin)` | Register a plugin (replaces existing with same ID) |
| `Unregister` | `bool Unregister(string pluginId)` | Unregister by ID, returns false if not found |
| `GetPluginById` | `IPlugin? GetPluginById(string pluginId)` | Lookup by exact ID |
| `GetPlugin<T>()` | `T? GetPlugin<T>() where T : class, IPlugin` | Get best plugin of type T based on operating mode and quality |
| `GetPlugin<T>(id)` | `T? GetPlugin<T>(string pluginId)` | Get plugin by ID cast to type T |
| `GetPlugins<T>()` | `IEnumerable<T> GetPlugins<T>()` | Get all plugins of type T |
| `GetPluginsByCategory` | `IEnumerable<IPlugin> GetPluginsByCategory(PluginCategory)` | Get all plugins in a category |
| `GetAll` / `GetAllPlugins` | `IEnumerable<IPlugin>` | Get all registered plugins |
| `Contains` | `bool Contains(string pluginId)` | Check if a plugin is registered |
| `GetPluginIds` | `IEnumerable<string> GetPluginIds()` | Get all registered plugin IDs |
| `GetCategorySummary` | `Dictionary<PluginCategory, int>` | Count of plugins per category |
| `Has<T>()` | `bool Has<T>()` | Check if any plugin of type T is registered |

**Intelligent plugin selection** (`GetPlugin<T>()`):

When multiple plugins of the same type are registered, the registry selects the best one:

1. First checks for a plugin whose `PreferredMode` metadata matches the current `OperatingMode`.
2. Falls back to selecting by `QualityLevel` metadata (highest wins; default is 50).
3. If only one candidate exists, returns it directly.

**Primary storage selection:**

The kernel auto-selects primary storage:

```csharp
// Explicit
kernel.SetPrimaryStorage(myStoragePlugin);

// Automatic (during InitializeAsync)
// Uses _registry.GetPlugin<IStorageProvider>() -- best available
```

---

### 15.3 Message Bus (Post Office)

The message bus (`DefaultMessageBus`) is the central nervous system of the DataWarehouse. All inter-plugin communication flows through it.

#### Architecture

```
Publisher --> [Topic Validation] --> [Rate Limiting] --> [Access Enforcement] --> Subscribers
                                                                                  ├── Direct subscriptions
                                                                                  ├── Response subscriptions
                                                                                  └── Pattern subscriptions
```

The kernel maintains two bus references:

- `_messageBus` (raw `DefaultMessageBus`) -- used by the kernel for system lifecycle topics.
- `_enforcedMessageBus` (wrapped with `AccessEnforcementInterceptor`) -- given to plugins. Enforces the `AccessVerificationMatrix`.

#### Messaging Patterns

**1. Pub/Sub (fire-and-forget):**

```csharp
// Publish -- fires handlers concurrently, does not wait
await messageBus.PublishAsync("my.topic", message, ct);

// Publish and wait -- fires all handlers and awaits completion
await messageBus.PublishAndWaitAsync("my.topic", message, ct);
```

**2. Request/Response:**

```csharp
// Send and wait for a response
MessageResponse response = await messageBus.SendAsync("my.topic", message, ct);

// Send with timeout
MessageResponse response = await messageBus.SendAsync("my.topic", message, TimeSpan.FromSeconds(5), ct);

// Response codes:
// response.Success -- true if handled successfully
// MessageResponse.Error(message, code) -- NO_HANDLER, HANDLER_ERROR, TIMEOUT, RATE_LIMITED
```

**3. Pattern subscriptions (wildcard routing):**

```csharp
// Subscribe to all storage events
var sub = messageBus.SubscribePattern("storage.*", async msg => { ... });

// Subscribe to all error events across all subsystems
var sub = messageBus.SubscribePattern("*.error", async msg => { ... });
```

Pattern matching converts glob patterns to regex: `*` matches any sequence, `?` matches a single character.

#### Subscription Management

```csharp
// Subscribe to a topic (returns IDisposable for cleanup)
IDisposable subscription = messageBus.Subscribe("my.topic", async (PluginMessage msg) =>
{
    // Handle message
});

// Subscribe with response capability
IDisposable subscription = messageBus.Subscribe("my.topic", async (PluginMessage msg) =>
{
    return MessageResponse.Success(new Dictionary<string, object> { ["result"] = "ok" });
});

// Unsubscribe all handlers for a topic
messageBus.Unsubscribe("my.topic");

// Dispose individual subscription
subscription.Dispose();

// Query active topics
IEnumerable<string> topics = messageBus.GetActiveTopics();
int count = messageBus.GetSubscriberCount("my.topic");
```

#### Topic Name Validation (BUS-06)

All topic names are validated against injection attacks:

- Must match: `^[a-zA-Z0-9][a-zA-Z0-9._\-]{0,255}$`
- Rejects path traversal (`..`), path separators (`/`, `\`), control characters
- Pattern topics additionally allow `*` and `?` wildcards

#### Per-Publisher Rate Limiting (BUS-03)

Each publisher is rate-limited using a sliding-window algorithm:

- Default: 1000 messages/second per publisher
- Configurable via `RateLimitPerSecond` property
- Publisher identity resolved from `message.Identity?.ActorId ?? message.Source ?? "anonymous"`
- Exceeding the limit throws `InvalidOperationException` (PublishAsync) or returns `RATE_LIMITED` error (SendAsync)

#### Access Enforcement

Plugin-facing message bus is wrapped with an `AccessEnforcementInterceptor` that evaluates every publish/subscribe against the `AccessVerificationMatrix`:

| Rule | Effect |
|---|---|
| `system:*` principal | Full access to all topics |
| `user:*` on `kernel.*` | **DENIED** |
| `user:*` on `security.*` | **DENIED** |
| `user:*` on `system.*` | **DENIED** |
| Authenticated on `storage.*` | Allowed |
| Authenticated on `pipeline.*` | Allowed |
| Authenticated on `intelligence.*` | Allowed |
| Authenticated on `compliance.*` | Allowed |
| Authenticated on `config.*` | Allowed |
| Tenant-scoped principals | Can access resources within their own tenant scope (AUTH-09) |
| Null-identity messages | **DENIED** (fail-closed) |

#### Message Authentication (BUS-02)

`AuthenticatedMessageBusDecorator` adds HMAC-SHA256 signing and replay protection:

```csharp
// Configure authentication for a topic
authBus.ConfigureAuthentication("security.acl", new MessageAuthenticationOptions
{
    RequireSignature = true,
    EnableReplayDetection = true,
    MaxMessageAge = TimeSpan.FromMinutes(5)
});

// Set signing key (minimum 256 bits / 32 bytes)
authBus.SetSigningKey(key);

// Rotate key with grace period (old key accepted during grace period)
authBus.RotateSigningKey(newKey, gracePeriod: TimeSpan.FromMinutes(30));
```

Messages on authenticated topics are signed with HMAC-SHA256 over `topic|sortedPayload|nonce|timestamp`. Verification checks: signature presence, nonce uniqueness (replay detection), message expiry, HMAC validity. During key rotation, both current and previous keys are accepted.

#### Built-in System Topics (MessageTopics)

All topics are defined as constants in `DataWarehouse.SDK.Contracts.MessageTopics`:

**System lifecycle:**

| Constant | Topic | Published by |
|---|---|---|
| `SystemStartup` | `system.startup` | Kernel on initialization |
| `SystemShutdown` | `system.shutdown` | Kernel on disposal |
| `SystemHealthCheck` | `system.healthcheck` | Health monitoring |

**Plugin lifecycle:**

| Constant | Topic | Published by |
|---|---|---|
| `PluginLoaded` | `plugin.loaded` | Kernel on plugin registration |
| `PluginUnloaded` | `plugin.unloaded` | Kernel on plugin unload |
| `PluginError` | `plugin.error` | Plugin error reporting |

**Storage operations:**

| Constant | Topic | Direction |
|---|---|---|
| `StorageSave` | `storage.save` | Command |
| `StorageLoad` | `storage.load` | Command |
| `StorageDelete` | `storage.delete` | Command |
| `StorageSaved` | `storage.saved` | Event |
| `StorageLoaded` | `storage.loaded` | Event |
| `StorageDeleted` | `storage.deleted` | Event |

**Pipeline events:**

| Constant | Topic | Published by |
|---|---|---|
| `PipelineExecute` | `pipeline.execute` | Pipeline orchestrator on start |
| `PipelineCompleted` | `pipeline.completed` | Pipeline orchestrator on success |
| `PipelineError` | `pipeline.error` | Pipeline orchestrator on failure |

**AI/Intelligence:**

| Constant | Topic |
|---|---|
| `AIQuery` | `ai.query` |
| `AIEmbed` | `ai.embed` |
| `AIResponse` | `ai.response` |

**Metadata:**

| Constant | Topic |
|---|---|
| `MetadataIndex` | `metadata.index` |
| `MetadataSearch` | `metadata.search` |
| `MetadataUpdate` | `metadata.update` |

**Security:**

| Constant | Topic |
|---|---|
| `SecurityAuth` | `security.auth` |
| `SecurityACL` | `security.acl` |
| `SecurityAudit` | `security.audit` |
| `AuthKeyRotated` | `security.key.rotated` |
| `AuthSigningKeyChanged` | `security.signing.changed` |
| `AuthReplayDetected` | `security.replay.detected` |

**Configuration:**

| Constant | Topic |
|---|---|
| `ConfigChanged` | `config.changed` |
| `ConfigReload` | `config.reload` |

**Knowledge & Capability:**

| Constant | Topic |
|---|---|
| `KnowledgeRegister` | `knowledge.register` |
| `KnowledgeQuery` | `knowledge.query` |
| `KnowledgeResponse` | `knowledge.response` |
| `KnowledgeUpdate` | `knowledge.update` |
| `CapabilityRegister` | `capability.register` |
| `CapabilityUnregister` | `capability.unregister` |
| `CapabilityQuery` | `capability.query` |
| `CapabilityChanged` | `capability.changed` |

**Internal plugin state (used by DefaultPluginStateStore):**

| Topic | Operation |
|---|---|
| `dw.internal.plugin-state.write` | Save plugin state |
| `dw.internal.plugin-state.read` | Load plugin state |
| `dw.internal.plugin-state.delete` | Delete plugin state entry |
| `dw.internal.plugin-state.list` | List plugin state keys |
| `dw.internal.plugin-state.exists` | Check state existence |

**Topic prefixes** (for pattern subscriptions):

| Constant | Prefix |
|---|---|
| `SecurityPrefix` | `security.` |
| `KeyStorePrefix` | `keystore.` |
| `PluginLifecyclePrefix` | `plugin.` |
| `SystemPrefix` | `system.` |

#### Thread Safety and Concurrency

- All subscription lists are protected by a `Lock` instance (`_subscriptionLock`).
- `PublishAsync` fires handlers via `Task.Run` (fire-and-forget, concurrent).
- `PublishAndWaitAsync` fires handlers concurrently via `Task.WhenAll`.
- `SendAsync` uses the first registered response handler (could be extended to round-robin).
- Pattern subscriptions are stored in a `BoundedDictionary` and checked on every publish (regex match).
- Rate limiters use `ConcurrentQueue` with lock-free sliding window.

---

### 15.4 Knowledge Lake (Knowledge Bank)

`KnowledgeLake` is the central knowledge storage for AI discovery and caching. Plugins register knowledge objects (capabilities, configuration, statistics) that can be queried by other plugins and the Intelligence system.

#### Storage Model

Each knowledge entry contains:

| Field | Type | Description |
|---|---|---|
| `Knowledge` | `KnowledgeObject` | The knowledge payload (Id, Topic, SourcePluginId, Description, Tags, Payload) |
| `StoredAt` | `DateTimeOffset` | When stored |
| `LastAccessedAt` | `DateTimeOffset` | Last access time (updated on every read) |
| `AccessCount` | `long` | Number of times accessed |
| `TimeToLive` | `TimeSpan?` | Optional TTL (null = permanent) |
| `IsStatic` | `bool` | Whether loaded at startup (vs. dynamic runtime knowledge) |

Entries are indexed three ways:

- **By ID** -- primary dictionary (`_entries`)
- **By topic** -- topic-based index (`_byTopic`)
- **By plugin** -- plugin-based index (`_byPlugin`)

#### API

```csharp
// Store knowledge
await knowledgeLake.StoreAsync(knowledgeObject, isStatic: true, ttl: TimeSpan.FromHours(1));

// Store batch
await knowledgeLake.StoreBatchAsync(knowledgeObjects, isStatic: true);

// Remove by ID
await knowledgeLake.RemoveAsync(knowledgeId);

// Remove all knowledge from a plugin
await knowledgeLake.RemoveByPluginAsync(pluginId);

// Remove all knowledge on a topic
await knowledgeLake.RemoveByTopicAsync(topic);

// Direct lookup by ID
KnowledgeEntry? entry = knowledgeLake.Get(knowledgeId);

// Query by topic
IReadOnlyList<KnowledgeEntry> entries = knowledgeLake.GetByTopic("plugin.capabilities");

// Query by plugin
IReadOnlyList<KnowledgeEntry> entries = knowledgeLake.GetByPlugin("com.example.myplugin");

// Advanced query
var results = await knowledgeLake.QueryAsync(new KnowledgeQuery
{
    SourcePluginId = "com.example.plugin",
    KnowledgeType = "capability",
    TopicPattern = "plugin.*",
    RequiredTags = new[] { "encryption" },
    SearchText = "aes",
    OnlyStatic = true,
    Limit = 10
});

// Get all static knowledge
IReadOnlyList<KnowledgeEntry> statics = knowledgeLake.GetAllStatic();

// Get recent entries
IReadOnlyList<KnowledgeEntry> recent = knowledgeLake.GetRecent(count: 50);

// Invalidate (remove) all knowledge from a plugin
await knowledgeLake.InvalidateAsync(pluginId);

// Clear expired entries
await knowledgeLake.ClearExpiredAsync();

// Get statistics
KnowledgeLakeStatistics stats = knowledgeLake.GetStatistics();
// stats.TotalEntries, StaticEntries, DynamicEntries, ExpiredEntries,
// TotalAccessCount, EntriesByPlugin, EntriesByTopic
```

#### TTL and Auto-Cleanup

- Entries with a `TimeToLive` are auto-expired by a background timer (runs every 5 minutes).
- Expired entries are detected by `(now - StoredAt) > TimeToLive`.
- The cleanup timer can be disabled by passing `enableAutoCleanup: false` to the constructor.

#### How Plugins Contribute Knowledge

Plugins contribute knowledge automatically through `PluginBase` wiring:

1. **Static knowledge** -- `GetStaticKnowledge()` override provides knowledge loaded once during `InitializeAsync`.
2. **Capability knowledge** -- auto-built from `GetCapabilityRegistrations()` during handshake.
3. **Strategy knowledge** -- auto-built from the strategy registry via `GetStrategyKnowledge()`.
4. **Configuration knowledge** -- `BuildConfigurationKnowledge()` override.
5. **Statistics knowledge** -- `BuildStatisticsKnowledge()` override.
6. **Dynamic queries** -- `HandleDynamicKnowledgeQueryAsync()` responds to bus queries on `knowledge.query.{pluginId}`.

---

### 15.5 Capability Registry

`PluginCapabilityRegistry` provides "who can do what" discovery across the system. It tracks every capability exposed by every plugin, indexed by category, plugin, and tags.

#### Data Model

`RegisteredCapability` record:

| Field | Type | Description |
|---|---|---|
| `CapabilityId` | `string` | Unique identifier (e.g., `com.example.plugin.encrypt`) |
| `PluginId` | `string` | Owning plugin ID |
| `PluginName` | `string` | Human-readable plugin name |
| `PluginVersion` | `string` | Plugin version |
| `DisplayName` | `string` | Human-readable capability name |
| `Description` | `string?` | What the capability does |
| `Category` | `CapabilityCategory` | Enum: Storage, Security, Compute, etc. |
| `SubCategory` | `string?` | Finer classification |
| `Tags` | `string[]` | Searchable tags |
| `Priority` | `int` | Priority for selection (higher = preferred) |
| `IsAvailable` | `bool` | Whether currently available |
| `RegisteredAt` | `DateTimeOffset` | Registration timestamp |
| `Metadata` | `Dictionary<string, object>` | Additional metadata |

#### Registration API

```csharp
// Register a capability (idempotent -- updates on re-registration)
bool isNew = await registry.RegisterAsync(capability, ct);

// Batch register
int newCount = await registry.RegisterBatchAsync(capabilities, ct);

// Unregister a specific capability
bool removed = await registry.UnregisterAsync(capabilityId, ct);

// Unregister all capabilities from a plugin
int removedCount = await registry.UnregisterPluginAsync(pluginId, ct);

// Set availability for all capabilities of a plugin
await registry.SetPluginAvailabilityAsync(pluginId, isAvailable: false, ct);
```

#### Discovery API

```csharp
// Direct lookup
RegisteredCapability? cap = registry.GetCapability("storage.memory.save");
bool available = registry.IsCapabilityAvailable("storage.memory.save");

// By category
IReadOnlyList<RegisteredCapability> storageCaps = registry.GetByCategory(CapabilityCategory.Storage);

// By plugin
IReadOnlyList<RegisteredCapability> pluginCaps = registry.GetByPlugin("com.example.plugin");

// By tags (intersection -- all tags must match)
IReadOnlyList<RegisteredCapability> tagged = registry.GetByTags("encryption", "aes");

// Find best available capability in a category
RegisteredCapability? best = registry.FindBest(CapabilityCategory.Security, "encryption");

// Get all
IReadOnlyList<RegisteredCapability> all = registry.GetAll();

// Reverse lookup: which plugin owns a capability?
string? pluginId = registry.GetPluginIdForCapability("storage.memory.save");

// Which plugins provide a category?
IReadOnlyList<string> pluginIds = registry.GetPluginIdsForCategory(CapabilityCategory.Storage);

// Advanced query
CapabilityQueryResult result = await registry.QueryAsync(new CapabilityQuery
{
    Category = CapabilityCategory.Storage,
    SubCategory = "Memory",
    PluginId = null,
    OnlyAvailable = true,
    RequiredTags = new[] { "storage" },
    AnyOfTags = new[] { "cache", "memory" },
    ExcludeTags = new[] { "deprecated" },
    SearchText = "memory",
    MinPriority = 5,
    SortBy = "priority",
    SortDescending = true,
    Limit = 10
});
// result.Capabilities, TotalCount, QueryTime, CategoryCounts

// Statistics
CapabilityRegistryStatistics stats = registry.GetStatistics();
// stats.TotalCapabilities, AvailableCapabilities, RegisteredPlugins,
// ByCategory, ByPlugin, TopTags
```

#### Event Notifications

```csharp
// Subscribe to capability lifecycle events
IDisposable sub1 = registry.OnCapabilityRegistered(cap => { /* new capability */ });
IDisposable sub2 = registry.OnCapabilityUnregistered(capId => { /* removed */ });
IDisposable sub3 = registry.OnAvailabilityChanged((capId, available) => { /* toggled */ });
```

#### Message Bus Integration

The registry subscribes to three message bus topics:

| Topic | Behavior |
|---|---|
| `capability.register` | Registers capability from message payload |
| `capability.unregister` | Unregisters capability or plugin capabilities |
| `capability.query` | Executes query and publishes result to `capability.query.response.{correlationId}` |

Changes are published to `capability.changed` with change type: `registered`, `updated`, `unregistered`, `available`, `unavailable`.

---

### 15.6 Pipeline Orchestration

The pipeline manages data flow through transformation stages (compression, encryption, etc.) and terminal stages (storage).

#### DefaultPipelineOrchestrator

The default orchestrator manages static pipeline configurations:

```csharp
// Set configuration
orchestrator.SetConfiguration(config);

// Reset to defaults (Compress -> Encrypt)
orchestrator.ResetToDefaults();

// Register/unregister stages
orchestrator.RegisterStage(transformationPlugin);
orchestrator.UnregisterStage("plugin-id");

// Get registered stages
IEnumerable<PipelineStageInfo> stages = orchestrator.GetRegisteredStages();

// Validate configuration
PipelineValidationResult result = orchestrator.ValidateConfiguration(config);
// result.IsValid, Errors, Warnings, Suggestions
```

**Write pipeline** (`ExecuteWritePipelineAsync`):

1. Gets the current configuration, filters enabled stages, orders by `Order` field.
2. Resolves security context from `PipelineContext` (falls back to `AnonymousSecurityContext`).
3. Publishes `pipeline.execute` event with stage count, user, tenant, correlation ID.
4. For each stage: resets stream position, calls `stage.OnWrite(stream, kernelContext, parameters)`, tracks intermediate streams.
5. On success: publishes `pipeline.completed`, stores intermediate streams in context for caller cleanup.
6. On failure: disposes intermediate streams, publishes `pipeline.error`.

**Read pipeline** (`ExecuteReadPipelineAsync`):

Same as write but stages execute in **reverse order** (`OrderByDescending`) and use `stage.OnRead()` instead of `OnWrite()`.

**Stage resolution priority:**

1. By explicit `PluginId` from configuration.
2. By `SubCategory` match (case-insensitive), preferring highest `QualityLevel`.

#### EnhancedPipelineOrchestrator

The enhanced orchestrator adds policy-based, per-user configuration (T126):

**Key additions:**

| Feature | Description |
|---|---|
| **Hierarchical policies (B1)** | Resolves effective policy: Instance -> UserGroup -> User -> Operation |
| **Universal enforcement (B3)** | ALL operations route through the pipeline, even with no stages |
| **Stage snapshots (B4)** | Records `PipelineStageSnapshot` after each write stage, stored in manifest |
| **Manifest-based read (B5)** | Read pipeline uses per-blob snapshots from manifest (not current policy) |
| **Lazy migration (B6)** | Detects stale policy versions on read, triggers migration if configured |
| **Transit pipelines** | Separate `ExecuteTransitWritePipelineAsync` / `ExecuteTransitReadPipelineAsync` for network-transit stages |
| **Terminal stages** | `ExecuteWritePipelineWithStorageAsync` handles transform + storage fan-out in a single call |
| **Transactions** | `IPipelineTransaction` support with automatic rollback on critical terminal failure |

**Terminal stage execution:**

Terminals represent storage destinations. The enhanced orchestrator supports:

- **Parallel terminals** -- multiple storage targets written concurrently
- **Sequential terminals** -- ordered storage operations
- **After-parallel terminals** -- run after parallel batch completes
- **Critical vs non-critical** -- critical terminal failure triggers rollback
- **Timeout per terminal** -- configurable via `TerminalStagePolicy.Timeout`

```csharp
var result = await enhancedOrchestrator.ExecuteWritePipelineWithStorageAsync(input, context, ct);
// result.TerminalResults -- per-terminal success/failure
// result.TotalDuration -- total pipeline time
// result.Manifest -- updated manifest with snapshots
// result.TransactionId -- transaction ID if transactions were used
```

**PipelineStageSnapshot:**

| Field | Type | Description |
|---|---|---|
| `StageType` | `string` | Stage type (e.g., "Compression", "Encryption") |
| `PluginId` | `string` | Plugin that executed the stage |
| `StrategyName` | `string` | Strategy used within the plugin |
| `Order` | `int` | Execution order |
| `Parameters` | `Dictionary<string, object>` | Stage parameters |
| `ExecutedAt` | `DateTimeOffset` | When executed |
| `PluginVersion` | `string?` | Plugin version at execution time |

#### PipelineMigrationEngine

Handles re-processing existing blobs when pipeline policies change:

| Feature | Description |
|---|---|
| **Background batch migration (D1)** | `StartMigrationAsync(oldPolicy, newPolicy, options)` -- enumerates blobs, re-processes in batches |
| **Lazy migration (D2)** | `MigrateOnAccessAsync(stream, snapshots, targetPolicy)` -- inline migration during read |
| **Throttling (D3)** | `MigrationOptions.MaxBlobsPerSecond` and `Parallelism` |
| **Progress tracking (D4)** | `GetMigrationStatusAsync(jobId)` -- ProcessedBlobs, FailedBlobs, TotalBlobs |
| **Cancellation + rollback (D5)** | `CancelMigrationAsync(jobId)` -- cancels job, rolls back processed blobs |
| **Cross-algorithm migration (D6)** | Reverses old stages via `ReverseStageAsync`, applies new pipeline |
| **Filtering (D7)** | `MigrationFilter` -- by container, owner, tier, tags, date, size |

**ReverseStageAsync** logic:

1. Looks up the `IDataTransformation` plugin by `stage.PluginId`.
2. Calls `plugin.OnRead()` (the inverse of `OnWrite()`) to undo the transformation.
3. Falls back to orchestrator read pipeline if plugin not found.
4. Returns input unchanged as last resort (caller detects via hash verification).

---

### 15.7 Kernel Storage Service

`KernelStorageService` provides the `IKernelStorageService` interface that plugins use to persist data through the kernel.

#### IKernelStorageService Interface

```csharp
public interface IKernelStorageService
{
    Task SaveAsync(string path, Stream data, IDictionary<string, string>? metadata = null, CancellationToken ct = default);
    Task SaveAsync(string path, byte[] data, IDictionary<string, string>? metadata = null, CancellationToken ct = default);
    Task<Stream?> LoadAsync(string path, CancellationToken ct = default);
    Task<byte[]?> LoadBytesAsync(string path, CancellationToken ct = default);
    Task<bool> DeleteAsync(string path, CancellationToken ct = default);
    Task<bool> ExistsAsync(string path, CancellationToken ct = default);
    Task<IReadOnlyList<StorageItemInfo>> ListAsync(string prefix, int limit = 100, int offset = 0, CancellationToken ct = default);
    Task<IDictionary<string, string>?> GetMetadataAsync(string path, CancellationToken ct = default);
}
```

#### The dw:// URI Scheme

All storage paths are converted to `dw://` URIs internally:

```
path = "plugins/myplugin/state.json"
uri  = dw://kernel-storage/plugins/myplugin/state.json
```

Metadata is stored alongside data as `.meta.json` files:

```
data uri     = dw://kernel-storage/plugins/myplugin/state.json
metadata uri = dw://kernel-storage/.metadata/plugins/myplugin/state.json.meta.json
```

#### Metadata Index

`KernelStorageService` maintains an in-memory `BoundedDictionary<string, StorageItemInfo>` index for efficient listing. The index:

- Is updated on every `SaveAsync` and `DeleteAsync` call.
- Is persisted to storage as `.index.json` after every mutation (survives restarts).
- Can be rebuilt from the persisted snapshot via `RebuildIndexAsync()`.
- Supports prefix-filtered, paginated listing via `ListAsync(prefix, limit, offset)`.

#### NullKernelStorageService

A no-op implementation used during the plugin loading phase (before real storage is available):

- All writes succeed silently (no-op).
- All reads return `null`.
- All existence checks return `false`.
- All listings return empty.

This ensures plugins can be loaded even before a storage provider is registered.

---

### 15.8 Container Manager

`ContainerManager` implements namespace/partition management with quota enforcement and access control. Essential for multi-tenant deployments.

#### Container CRUD

```csharp
// Create
ContainerInfo info = await containerManager.CreateContainerAsync(securityContext, "my-container",
    new ContainerOptions { MaxSizeBytes = 10L * 1024 * 1024 * 1024, Tags = { "production" } });

// Get
ContainerInfo? info = await containerManager.GetContainerAsync(securityContext, "my-container");

// List all
await foreach (var container in containerManager.ListContainersAsync(securityContext))
{
    Console.WriteLine(container.DisplayName);
}

// List with prefix filter
await foreach (var container in containerManager.ListContainersAsync(securityContext, prefix: "prod-"))
{
    // Only containers starting with "prod-"
}

// Delete
await containerManager.DeleteContainerAsync(securityContext, "my-container");

// Delete with force (even if non-empty)
await containerManager.DeleteContainerAsync(securityContext, "my-container", force: true);
```

#### Quota Enforcement

```csharp
// Get quota
ContainerQuota quota = await containerManager.GetQuotaAsync(securityContext, "my-container");
// quota.MaxSizeBytes, UsedSizeBytes, MaxItems, UsedItems

// Set quota
await containerManager.SetQuotaAsync(adminContext, "my-container", new ContainerQuota
{
    MaxSizeBytes = 50L * 1024 * 1024 * 1024,
    MaxItems = 500_000
});

// Check if operation would exceed quota (synchronous, fast)
QuotaCheckResult check = containerManager.CheckQuota("my-container",
    additionalBytes: 1024 * 1024,
    additionalItems: 1);
// check.Allowed, Reason, QuotaType, Current, Limit, Requested

// Record usage after successful operation
containerManager.RecordUsage("my-container", bytesChanged: 1024, itemsChanged: 1);
```

Default quotas: 10 GB max size, 1,000,000 max items.

#### Access Control

```csharp
// Grant access
await containerManager.GrantAccessAsync(ownerContext, "my-container", "user@domain",
    ContainerAccessLevel.ReadWrite);

// Revoke access
await containerManager.RevokeAccessAsync(ownerContext, "my-container", "user@domain");

// Check access level
ContainerAccessLevel level = await containerManager.GetAccessLevelAsync(context, "my-container");

// Check if user has at least required level
bool hasAccess = await containerManager.HasAccessAsync(context, "my-container",
    ContainerAccessLevel.ReadWrite);

// List all access entries
await foreach (var entry in containerManager.ListAccessAsync(context, "my-container"))
{
    // entry.UserId, Level, GrantedAt, GrantedBy, ExpiresAt
}
```

The creator automatically receives `Owner` level access.

#### State Management

Containers support four states:

| State | Writes | Reads | Transition |
|---|---|---|---|
| `Active` | Yes | Yes | Default state |
| `Suspended` | No | Yes | `SuspendAsync(name, reason)` |
| `ReadOnly` | No | Yes | Admin action |
| `Deleted` | No | No | `DeleteContainerAsync` |

```csharp
await containerManager.SuspendAsync("my-container", reason: "Maintenance");
await containerManager.ResumeAsync("my-container");
bool canWrite = containerManager.AllowsWrites("my-container");
```

---

### 15.9 Built-in InMemoryStoragePlugin

`InMemoryStoragePlugin` is the kernel's built-in volatile storage provider, registered during initialization as the bootstrap storage.

**Why it exists:** The kernel needs a functional storage provider before external storage plugins are loaded. This allows the kernel to operate as a minimal but complete DataWarehouse for development, testing, caching, and ephemeral workloads.

**Inheritance chain:**

```
PluginBase
  -> IntelligenceAwarePluginBase
    -> DataPipelinePluginBase
      -> StorageProviderPluginBase
        -> InMemoryStoragePlugin (also implements IListableStorage)
```

**Why `CreateCustomStateStore() => null`:** The plugin is volatile -- all data is lost on shutdown. Persisting plugin state through a volatile store would be meaningless, so it explicitly returns `null` to disable state persistence.

**Key properties:**

| Property | Description |
|---|---|
| `Id` | `datawarehouse.kernel.storage.inmemory` |
| `Scheme` | `memory` |
| `Count` | Current item count |
| `TotalSizeBytes` | Total memory used |
| `MaxMemoryBytes` | Configured limit (null = unlimited) |
| `MaxItemCount` | Configured limit (null = unlimited) |
| `MemoryUtilization` | Percentage (0-100) |
| `IsUnderMemoryPressure` | True if utilization >= threshold (default 80%) |

**Features:**

- **LRU eviction** -- when memory or item limits are exceeded, least-recently-used items are evicted first.
- **System memory pressure** -- checks GC memory info every 10 seconds; proactively evicts 10% of items when system memory load > 90%.
- **Eviction callbacks** -- register `Action<EvictionEvent>` to be notified when items are evicted.
- **Configurable presets** -- `InMemoryStorageConfig.SmallCache` (100MB/10K), `.MediumCache` (1GB/100K), `.LargeCache` (10GB/1M), `.Unlimited`.

**Eviction reasons:** `Manual`, `MemoryLimit`, `ItemCountLimit`, `MemoryPressure`, `SystemMemoryPressure`, `Expired`.

**Declared capabilities:** `storage.memory`, `storage.memory.save`, `storage.memory.load`, `storage.memory.delete`, `storage.memory.list`, `storage.memory.eviction`.

---

### 15.10 Kernel Context

`IKernelContext` is the interface through which plugins access kernel services without holding a reference to the kernel itself.

```csharp
public interface IKernelContext
{
    OperatingMode Mode { get; }
    string RootPath { get; }
    IKernelStorageService Storage { get; }

    void LogInfo(string message);
    void LogError(string message, Exception? ex = null);
    void LogWarning(string message);
    void LogDebug(string message);

    T? GetPlugin<T>() where T : class, IPlugin;
    IEnumerable<T> GetPlugins<T>() where T : class, IPlugin;
}
```

**Implementations:**

| Class | Used when | Storage | Plugin access |
|---|---|---|---|
| `KernelContext` (Infrastructure) | Post-initialization; passed to ContainerManager, storage service | Real `KernelStorageService` | Delegates to `DataWarehouseKernel.GetPlugin<T>()` |
| `LoggerKernelContext` | During plugin loading (before full kernel is ready) | `NullKernelStorageService` | Delegates to `PluginRegistry` (may be null) |
| `DefaultKernelContext` (Pipeline) | Pipeline execution when no context provided | `NullKernelStorageService` | Delegates to `PluginRegistry` |
| `NullKernelContext` (Enhanced Pipeline) | Enhanced pipeline fallback | `NullKernelStorageService` | Returns null / empty |

**How plugins receive context:**

- Via `PipelineContext.KernelContext` during pipeline execution.
- Via constructor injection for kernel-internal components (ContainerManager, KernelStorageService).
- Plugins do NOT receive `IKernelContext` directly. They get `IMessageBus`, `IPluginCapabilityRegistry`, and `IKnowledgeLake` via `InjectKernelServices()`, plus plugin-to-plugin discovery via the registry/bus.

---

### 15.11 Security Context

Security context flows through pipelines and operations to enforce identity-based access control.

#### ISecurityContext

```csharp
public interface ISecurityContext
{
    string UserId { get; }
    string? TenantId { get; }
    IEnumerable<string> Roles { get; }
    bool IsSystemAdmin { get; }
}
```

#### AnonymousSecurityContext

When no user context is available (e.g., background jobs, migration), the pipeline uses an anonymous fallback:

```csharp
public sealed class AnonymousSecurityContext : ISecurityContext
{
    public static readonly AnonymousSecurityContext Instance = new();
    public string UserId => "anonymous";
    public string? TenantId => null;
    public IEnumerable<string> Roles => Array.Empty<string>();
    public bool IsSystemAdmin => false;
}
```

#### How Security Context Flows

1. **Pipeline operations** -- `PipelineContext.SecurityContext` carries the user context through write/read pipelines. The orchestrator extracts `UserId` and `TenantId` for policy resolution, audit logging, and distributed tracing.

2. **Message bus** -- `PluginMessage.Identity` carries a `CommandIdentity` with `EffectivePrincipalId`, `ActorId`, and `TenantId`. The `AccessEnforcementInterceptor` evaluates this identity against the `AccessVerificationMatrix` before delivering messages.

3. **Container operations** -- `ISecurityContext` is passed explicitly to every `ContainerManager` method (create, get, list, delete, grant/revoke access, quota).

4. **Strategy dispatch** -- `CommandIdentity` can be passed to `ResolveStrategy<T>(strategyId, identity)` and `ExecuteWithStrategyAsync` for ACL enforcement at the strategy level.

#### AccessVerificationMatrix

The kernel builds a hierarchical access matrix at construction:

- **System level** -- global rules (kernel full access, deny user access to kernel/security/system topics).
- **Tenant level** -- tenant principals can access resources within their own tenant scope (AUTH-09).
- **Default-deny** -- no rules = DENY. The `AccessEnforcementInterceptor` rejects null-identity messages.

Protected environment variables that cannot be overridden in production mode (INFRA-02):

| Variable | Protection |
|---|---|
| `DATAWAREHOUSE_REQUIRE_SIGNED_ASSEMBLIES` | Blocked |
| `DATAWAREHOUSE_VERIFY_SSL` | Blocked |
| `DATAWAREHOUSE_VERIFY_SSL_CERTIFICATES` | Blocked |
| `DATAWAREHOUSE_DISABLE_AUTH` | Blocked |
| `DATAWAREHOUSE_DISABLE_ENCRYPTION` | Blocked |
| `DATAWAREHOUSE_ALLOW_UNSIGNED_PLUGINS` | Blocked |
| `DATAWAREHOUSE_CREATE_DEFAULT_ADMIN` | Warning |
| `DATAWAREHOUSE_DEBUG_MODE` | Warning |
| `DATAWAREHOUSE_DISABLE_RATE_LIMITING` | Warning |

---

### 15.12 Kernel Wiring, Default Implementations & Conflict Surface

This section documents the hidden behaviors, default implementations, and assumptions that the kernel makes about plugins. Plugin auditors should use this as a checklist to detect conflicts between SDK, Kernel, and individual plugins.

---

#### 15.12.1 Kernel Wiring (What Happens Automatically)

These actions are performed **by the kernel** during plugin lifecycle. Plugins do not control them and must be compatible with them.

**During `RegisterPluginAsync` (kernel-driven registration):**

1. **Handshake** -- The kernel calls `plugin.OnHandshakeAsync(request)` with a `HandshakeRequest` containing `KernelId`, `ProtocolVersion`, `OperatingMode`, and `RootPath`. During handshake, `PluginBase.RegisterWithSystemAsync()` auto-registers capabilities with the CapabilityRegistry and static knowledge with the KnowledgeLake.

2. **Service injection** -- If the plugin is a `PluginBase`, the kernel calls `pluginBase.InjectKernelServices(enforcedMessageBus, capabilityRegistry, null)`. This sets `MessageBus`, `CapabilityRegistry`, and `KnowledgeLake` on the plugin. The message bus injected is the **access-enforced** wrapper (not the raw bus), meaning all plugin messages go through the `AccessVerificationMatrix`. After injection, `OnKernelServicesInjected()` is called on the plugin.

3. **Configuration injection** -- If a `DataWarehouseConfiguration` is loaded, the kernel calls `pluginBase.InjectConfiguration(currentConfiguration)`. This propagates the configuration to all registered strategies via `StrategyBase.InjectConfiguration`.

4. **Plugin registry registration** -- The kernel calls `_registry.Register(plugin)`, making the plugin discoverable via `GetPlugin<T>()`, `GetPlugins<T>()`, and `GetPluginsByCategory()`.

5. **Pipeline stage registration** -- If the plugin implements `IDataTransformation`, the kernel calls `_pipelineOrchestrator.RegisterStage(transformation)`. The stage is keyed by `plugin.Id` and indexed by `SubCategory`.

6. **Auto-start feature plugins** -- If the plugin implements `IFeaturePlugin` and `AutoStartFeatures` is true (default), the kernel calls `feature.StartAsync(token)` in a background job. The plugin does not control when this happens.

7. **Audit logging** -- The kernel publishes a `PluginLoaded` message to the message bus and writes an audit log entry for plugin registration.

**During `PluginLoader.LoadPluginAsync` (assembly loading):**

1. **Security validation** -- Assembly size check (max 50MB), blocklist check, allowed prefix check, SHA-256 hash verification (optional), strong name signature check (required in production).

2. **Memory budget check** -- `BoundedMemoryRuntime.Instance.CanAllocate(10MB)` is checked before loading. Plugins are rejected if memory budget is insufficient.

3. **Isolated AssemblyLoadContext** -- Each plugin assembly is loaded into a collectible `PluginLoadContext` for isolation and hot-reload support.

4. **Handshake per type** -- Every `IPlugin` type found in the assembly gets instantiated via `Activator.CreateInstance()` and handshaked individually. Plugins with parameterless constructors only.

**During kernel shutdown (`DisposeAsync`):**

1. **Shutdown signal** -- `_shutdownCts.Cancel()` is called, cancelling all background jobs.

2. **System shutdown event** -- `SystemShutdown` message is published to the bus (best-effort).

3. **Plugin stop with timeout** -- Each `IFeaturePlugin` gets `StopAsync()` called with a **30-second timeout**. Plugins that exceed this timeout are logged as warnings and forcibly abandoned (ISO-04, CVSS 7.1).

4. **Background job drain** -- All background jobs are awaited with a **10-second timeout**.

5. **PluginLoader disposal** -- All `AssemblyLoadContext` instances are unloaded.

**During hot reload (`PluginLoader.ReloadPluginAsync`):**

1. The plugin receives `OnReloadingAsync()` -- PluginBase auto-persists strategy IDs and shuts down strategies.
2. The plugin is unregistered from the registry and the AssemblyLoadContext is unloaded.
3. GC.Collect is forced to release the assembly.
4. The assembly is reloaded and all plugins are re-instantiated and re-handshaked.
5. The plugin receives `OnReloadedAsync()` -- PluginBase re-discovers strategies, re-injects configuration, and re-registers capabilities.
6. **Important**: Message bus subscriptions from the old plugin instance are NOT automatically cleaned up. The old `IDisposable` subscription handles become orphaned if the plugin did not dispose them in `OnReloadingAsync`.

---

#### 15.12.2 Default Implementations the Kernel Provides

**Default Pipeline Configuration:**

- `PipelineConfiguration.CreateDefault()` provides the default stage ordering: **Compress -> Encrypt** (order 100, 200).
- Write pipeline executes stages in ascending `Order`; read pipeline executes in **descending** `Order` (automatic reversal).
- If a stage plugin is not found by `PluginId`, the orchestrator falls back to matching by `SubCategory` (case-insensitive), selecting the highest `QualityLevel` match.
- Missing stages are **skipped with a warning**, not treated as errors. However, missing `Encryption` or `Compress` stages emit error-level logs.
- `PipelineConfiguration` validation checks for: duplicate stage types, missing plugin references, stage dependency violations (`RequiredPrecedingStages`), and stage incompatibilities (`IncompatibleStages`).

**Default Message Routing:**

- Messages are routed by exact topic name match, with additional pattern-matching subscriptions (glob-style `*` and `?`).
- Response subscriptions (request/response pattern) use the **first registered handler** (no round-robin or load balancing).
- All handlers for pub/sub messages are fired concurrently via `Task.Run` (fire-and-forget). Exceptions in handlers are caught and logged but do not propagate to publishers.
- `PublishAndWaitAsync` waits for all handlers to complete (including exceptions, which are caught).

**Default Security Context:**

- When no `ISecurityContext` is available in a `PipelineContext`, the pipeline falls back to `AnonymousSecurityContext.Instance` with `UserId = "anonymous"`, `TenantId = null`, `IsSystemAdmin = false`.
- The `AccessEnforcementInterceptor` on the enforced message bus **rejects null-identity messages** (fail-closed). The `system:*` principal pattern has full access to all resources.

**NullKernelStorageService:**

- During plugin loading (before storage is initialized), the `LoggerKernelContext` provides a `NullKernelStorageService` where all operations return empty/false/null. Plugins that attempt storage during construction or early initialization will silently fail.
- `DefaultPipelineOrchestrator` also creates a `DefaultKernelContext` with a `NullKernelStorageService` and **no-op logging** for pipeline operations when no explicit context is provided.

**InMemoryStoragePlugin as Bootstrap Storage:**

- Registered as the first built-in plugin during `InitializeAsync`.
- Set as primary storage when `UseInMemoryStorageByDefault` is true (default: true).
- Data is **volatile** -- lost on shutdown. No persistence, no replication.
- LRU eviction with configurable memory limits. System memory pressure detection triggers proactive eviction of 10% of items when GC reports >90% memory load.
- Its `CreateCustomStateStore()` returns `null` -- the InMemoryStoragePlugin intentionally has no state persistence.

**Default Storage Auto-Selection:**

- If no primary storage is explicitly set and `UseInMemoryStorageByDefault` is false, the kernel calls `_registry.GetPlugin<IStorageProvider>()` to auto-select the first available storage plugin. Selection uses `OperatingMode` and `QualityLevel` to pick the "best" plugin when multiple are available.

---

#### 15.12.3 Kernel Unique Behaviors (Things Only the Kernel Does)

**Plugin Selection by Operating Mode and Quality:**

- When multiple plugins of the same type are registered, `PluginRegistry.SelectBestPlugin<T>()` selects based on: (1) a plugin's `PreferredMode` metadata matching the kernel's `OperatingMode`, then (2) `QualityLevel` metadata (descending). This selection involves calling `OnHandshakeAsync` on each candidate to read metadata, which has side effects (capability re-registration).

**Plugin Load Ordering:**

- Plugins are loaded sequentially from configured `PluginPaths` in directory order. There is **no dependency-based load ordering**. Plugins that depend on other plugins being present must handle the case where the dependency is not yet loaded (use message bus discovery, not direct references).

**Hot Reload Mechanics:**

- Uses collectible `AssemblyLoadContext` (isCollectible: true).
- On reload: old plugin is unregistered from registry, old ALC is unloaded, GC is forced, new assembly is loaded into a fresh ALC.
- **What is preserved**: Nothing in kernel state is preserved for the plugin -- the plugin is entirely re-instantiated. Strategy state survival depends on `PluginBase.OnReloadingAsync()` persisting to `StateStore` and `OnReloadedAsync()` restoring.
- **What is reset**: Plugin registry entry, pipeline stage registration, capability registry entries, knowledge lake entries. All are re-created from the new plugin instance.
- **What is NOT reset**: Message bus subscriptions from the old instance remain as orphaned handles until GC collects them. The old subscription `IDisposable` handles are lost.

**Pipeline Composition (How Multiple Plugins' Stages Combine):**

- Each `IDataTransformation` plugin is registered as a pipeline stage keyed by `plugin.Id` and indexed by `SubCategory`.
- Stage resolution: first by explicit `PluginId` in config, then by `SubCategory` match (highest `QualityLevel` wins).
- The `EnhancedPipelineOrchestrator` supports hierarchical policies: Instance -> UserGroup -> User -> Operation levels. The `DefaultPipelineOrchestrator` uses a single global `PipelineConfiguration`.
- Both orchestrators support `RequiredPrecedingStages` and `IncompatibleStages` validation.

**Knowledge Lake TTL Enforcement and Auto-Cleanup:**

- `KnowledgeLake` runs a cleanup timer every **5 minutes** that removes entries where `(now - StoredAt) > TimeToLive`.
- `RemoveByPluginAsync(pluginId)` removes all knowledge entries for a given plugin (used during plugin unload).
- Static knowledge (isStatic: true) has no TTL and is never auto-cleaned.

**Container Quota Enforcement:**

- `ContainerManager.CheckQuota()` validates both size (bytes) and item count against the container's quota before allowing writes.
- Default quotas: 10 GB max size, 1,000,000 max items per container.
- `RecordUsage()` must be called after successful operations to keep usage tracking accurate. Plugins that bypass the ContainerManager for storage operations will have incorrect quota tracking.

**Message Bus Rate Limiting:**

- `DefaultMessageBus` enforces per-publisher rate limiting using a `SlidingWindowRateLimiter`.
- Default: **1000 messages/second** per publisher (identified by `message.Identity?.ActorId ?? message.Source ?? "anonymous"`).
- Exceeding the rate limit on `PublishAsync`/`PublishAndWaitAsync` throws `InvalidOperationException`. On `SendAsync` it returns `MessageResponse.Error` with code `"RATE_LIMITED"`.

**Topic Name Validation (BUS-06):**

- All topic names are validated against injection patterns: must be alphanumeric start, then alphanumeric/dots/hyphens/underscores, max 256 chars.
- Path traversal (`..`), path separators (`/`, `\`), and control characters are rejected.
- Pattern subscriptions allow `*` and `?` wildcards.

**Message Authentication (BUS-02):**

- `AuthenticatedMessageBusDecorator` adds HMAC-SHA256 signing and replay detection on configured topics.
- Signing key rotation with grace period: old key is accepted during the grace period.
- Nonce cache tracks seen nonces for 10 minutes to detect replay attacks. Cache limit: 100,000 nonces with LRU eviction.

**Access Enforcement Matrix:**

- Built at kernel construction with these default rules:
  - `system:*` principal has full access to everything.
  - `user:*` principals are DENIED access to `kernel.*`, `security.*`, and `system.*` topics.
  - Authenticated principals can access `storage.*`, `pipeline.*`, `intelligence.*`, `compliance.*`, `config.*`, and general topics.
  - Tenant isolation: tenant-scoped principals can only access resources within their own tenant scope (AUTH-09).
  - Default-deny: no matching rules = DENY.

**Auditable Event Subscriptions:**

- The kernel subscribes to 9 message bus topics for audit logging during initialization: `ConfigChanged`, `PluginUnloaded`, `SecurityACL`, `AuthKeyRotated`, `AuthSigningKeyChanged`, `AuthReplayDetected`, `SecurityAudit`, `CapabilityChanged`, `SecurityAuth`.
- Audit entries are written to `ConfigurationAuditLog` at `./config/config-audit.log`.

---

#### 15.12.4 Potential Conflict Surface (Plugin Audit Checklist)

The following table documents kernel behaviors/assumptions and what can go wrong if a plugin violates them. Use this as a checklist during plugin audits.

| # | Kernel Behavior / Assumption | What Goes Wrong If Violated | How to Detect |
|---|---|---|---|
| C-01 | **Kernel injects the access-enforced message bus**, not the raw bus. Plugins receive `_enforcedMessageBus` which evaluates `AccessVerificationMatrix` on every publish/send. | Plugin messages are silently dropped if the plugin's identity doesn't match any allow rule, or if it tries to access `kernel.*`, `security.*`, or `system.*` topics. Plugin appears to "send" but no handler receives the message. | Check plugin logs for "Access DENIED" warnings. Verify plugin's `CommandIdentity` is set on messages. Test message delivery end-to-end. |
| C-02 | **Kernel auto-starts feature plugins** in a background job if `AutoStartFeatures` is true. `StartAsync` is called in `Task.Run` after registration. | Plugin's `StartAsync` races with other initialization. If `StartAsync` depends on other plugins being registered/started, it may fail non-deterministically. The failure is caught and logged but not propagated. | Check if plugin's `StartAsync` has dependencies on other plugins. Look for "Feature plugin {Id} failed to start" error logs. |
| C-03 | **Pipeline stages are resolved by SubCategory** when no explicit `PluginId` is configured. The highest `QualityLevel` wins among matches. | Two plugins with the same `SubCategory` (e.g., both "Encryption") compete. Only one is used. The "losing" plugin's stage is silently ignored. Plugin may believe it's participating in the pipeline when it's not. | Run `GetRegisteredStages()` and check for duplicate `SubCategory` values. Verify which plugin is actually selected for each stage. |
| C-04 | **Read pipeline reverses write stage order automatically**. The orchestrator uses `OrderByDescending(s => s.Order)` for reads. | If a plugin provides asymmetric OnWrite/OnRead implementations where the reverse order assumption doesn't hold (e.g., a stage that must always run first regardless of direction), data corruption occurs. | Verify that plugin's `OnRead` is the exact inverse of `OnWrite` for all parameter combinations. Test round-trip: write then read produces identical data. |
| C-05 | **Plugin shutdown has a 30-second timeout**. Plugins exceeding this are forcibly abandoned. | Plugin's `StopAsync` takes too long (e.g., flushing large buffers, waiting for network I/O). The plugin is abandoned mid-cleanup, potentially leaving resources leaked, connections open, or state partially persisted. | Profile `StopAsync` duration under load. Ensure all I/O operations respect cancellation tokens. Test shutdown under memory pressure. |
| C-06 | **Message bus subscriptions are NOT auto-cleaned on plugin unload/reload**. Old subscription `IDisposable` handles are orphaned. | After hot reload, the old plugin instance's handlers may still receive messages (until GC collects them). This causes duplicate message processing or exceptions from disposed objects. | Check that plugin disposes all subscription handles in `OnReloadingAsync()` or `ShutdownAsync()`. Store `IDisposable` handles and dispose them explicitly. |
| C-07 | **Rate limit of 1000 msg/sec per publisher**. Publisher identity is `message.Identity?.ActorId ?? message.Source ?? "anonymous"`. | Plugin that publishes at high frequency (e.g., metrics, heartbeats) without setting a unique identity will be rate-limited. If multiple plugins share the "anonymous" identity, they compete for the same 1000 msg/sec budget. | Verify plugin sets `Identity` or `Source` on messages. Check for `InvalidOperationException` with "Rate limit exceeded" in logs. |
| C-08 | **PluginBase.OnHandshakeAsync registers capabilities and knowledge** via `RegisterWithSystemAsync`. This is called during registration AND during `InitializeAsync`. | Capabilities are registered twice -- once during kernel's `RegisterPluginAsync` (handshake) and once during `InitializeAsync` if the plugin calls `base.InitializeAsync()`. The CapabilityRegistry uses `AddOrUpdate` (idempotent), so this is normally safe. However, if the plugin modifies capabilities between the two calls, the second registration overwrites the first. | Check if plugin overrides `OnHandshakeAsync` or `InitializeAsync` and modifies `DeclaredCapabilities` between calls. |
| C-09 | **Kernel stores data under `dw://kernel-storage/` URI prefix**. `KernelStorageService` builds URIs as `dw://{basePath}/{path}` with default basePath `"kernel-storage"`. | Plugin stores data under `dw://kernel-storage/...` paths, colliding with kernel's internal storage (metadata index at `.index.json`, metadata files at `.metadata/`). | Audit all `dw://` URIs used by plugin. Ensure no plugin uses the `kernel-storage` prefix or paths starting with `.index` or `.metadata`. |
| C-10 | **Pipeline uses AnonymousSecurityContext** when `PipelineContext.SecurityContext` is null. Anonymous user has `UserId = "anonymous"`, no roles, not admin. | Plugin pipeline stages that check `SecurityContext.IsSystemAdmin` or specific roles will deny access for anonymous operations. Background jobs and migrations that don't set security context will fail authorization checks. | Check if pipeline stages perform authorization. Ensure all pipeline callers set `SecurityContext` or handle the anonymous fallback gracefully. |
| C-11 | **PluginBase.InjectKernelServices is called once** after handshake. The injected `MessageBus` and `CapabilityRegistry` are set as properties on the plugin instance. | Plugin creates its own `MessageBus` subscription before `InjectKernelServices` is called (e.g., in constructor). The subscription goes to a null bus. Plugin misses messages until it re-subscribes after injection. | Verify plugin does not subscribe to bus in constructor. All bus subscriptions should happen in `OnKernelServicesInjected()`, `InitializeAsync()`, or `StartAsync()`. |
| C-12 | **NullKernelStorageService during plugin loading**. The `IKernelContext.Storage` provided to plugins during load is a no-op that returns null/false for all operations. | Plugin attempts storage I/O during construction, handshake, or early initialization. All reads return null, all writes silently succeed without persisting, all existence checks return false. Plugin may initialize with corrupt/empty state. | Check if plugin reads/writes storage before `StartAsync`. All storage operations should be deferred to `InitializeAsync` or later. |
| C-13 | **Strategies must NOT be orchestrators** (AD-05). Strategies are workers -- no intelligence, capability registry, or message bus access. | Strategy subscribes to message bus topics, registers capabilities, or makes decisions about which other strategies to invoke. This violates the separation of concerns and can cause circular dependencies or deadlocks. | Grep strategy classes for `MessageBus`, `CapabilityRegistry`, `IMessageBus`, `Subscribe`. Strategies should have no bus access. |
| C-14 | **Plugin must call base.StartAsync/StopAsync**. PluginBase lifecycle methods perform auto-registration, state persistence, strategy lifecycle management, and capability registration. | Plugin overrides `StartAsync`/`StopAsync` without calling base. Capabilities are not registered, strategies are not started/stopped, state is not persisted, knowledge is not registered. | Check all plugin lifecycle method overrides for `base.` calls. Automated: search for `override.*StartAsync` without `base.StartAsync`. |
| C-15 | **Kernel publishes system events** on `SystemStartup`, `SystemShutdown`, `PluginLoaded`, `PluginUnloaded` topics. These are published via the raw (non-enforced) bus. | Plugin subscribes to system lifecycle topics via the enforced bus. Access is denied for non-system principals on `system.*` topics. Plugin never receives startup/shutdown notifications. | Check if plugin subscribes to `system.*` topics. These are kernel-only. Use `PluginLoaded`/`PluginUnloaded` topics instead (which are accessible). |
| C-16 | **Topic name validation rejects special characters**. Topics must match `[a-zA-Z0-9][a-zA-Z0-9._\-]{0,255}`. | Plugin tries to subscribe to or publish on topics with slashes, spaces, colons, or other special characters. Subscription/publish throws `ArgumentException`. | Audit all topic strings used by the plugin. Test with the `TopicValidator.ValidateTopic()` method. |
| C-17 | **Plugin assembly must have a parameterless constructor**. `PluginLoader` uses `Activator.CreateInstance(pluginType)`. | Plugin has only constructors with parameters. `Activator.CreateInstance` fails. Plugin is silently skipped during loading. | Check that every `IPlugin` class has a public parameterless constructor. |
| C-18 | **Plugin assembly size must be under 50MB**. `PluginSecurityConfig.MaxAssemblySize` defaults to 50MB. | Plugin assembly (including embedded resources) exceeds 50MB. The `PluginLoader` rejects it with a security validation error. | Check assembly size. Large embedded resources (ML models, data files) should be loaded from external files, not embedded. |
| C-19 | **Capability registry uses AddOrUpdate** (idempotent registration). If two plugins register the same `CapabilityId`, the second registration **overwrites** the first. | Two plugins claim the same capability ID. The second plugin silently takes over the capability. Callers that resolve the capability get the wrong plugin. | Audit all `CapabilityId` values across plugins. Ensure IDs use the `{pluginId}.{capability}` naming convention for uniqueness. |
| C-20 | **KnowledgeLake cleanup runs every 5 minutes**. Entries with expired TTL are removed. `RemoveByPluginAsync` removes all entries for a plugin. | Plugin stores dynamic knowledge with short TTL and expects it to persist. Knowledge disappears after TTL + up to 5 minutes. Plugin that stores knowledge under a different `SourcePluginId` will not have it cleaned up when the plugin unloads. | Verify plugin uses its own `Id` as `SourcePluginId` in knowledge entries. Check TTL values are appropriate. |
| C-21 | **Background jobs are bounded to 1000** via `BoundedDictionary`. Jobs that complete are removed from the dictionary. | Plugin creates many background jobs via `RunInBackground`. After 1000 concurrent jobs, the BoundedDictionary evicts oldest entries (LRU), losing tracking of those jobs. On shutdown, untracked jobs are not awaited. | Check if plugin creates background jobs. Ensure jobs complete in reasonable time. Consider using plugin-internal job management instead of kernel's `RunInBackground`. |
| C-22 | **Plugin's `OnReloadingAsync` must persist all state that needs to survive reload**. The kernel does not preserve any plugin state across reloads. | Plugin has in-memory state (caches, connection pools, pending operations) that is lost on reload. After reload, the plugin starts with empty state, potentially losing data or causing inconsistencies. | Check if plugin has significant in-memory state. Verify `OnReloadingAsync` persists it and `OnReloadedAsync` restores it. |
| C-23 | **Enforced message bus requires `CommandIdentity` on messages**. The `AccessEnforcementInterceptor` rejects messages with null identity (fail-closed). | Plugin publishes messages without setting `Identity` on `PluginMessage`. Messages are rejected by the interceptor. Plugin believes it published successfully but no handlers receive the message. | Check all `PublishAsync`/`SendAsync` calls set `message.Identity`. Test message delivery through the enforced bus. |
| C-24 | **Configuration changes propagate to strategies via `InjectConfiguration`**. When `InjectConfiguration` is called on a plugin, it iterates all registered strategies and calls `sb.InjectConfiguration(newConfig)`. | Plugin registers strategies after initial configuration injection. Late-registered strategies miss the configuration and operate with defaults. | Verify all strategies are registered before or during `InitializeAsync`. Check that strategies handle null/default configuration gracefully. |
| C-25 | **BoundedDictionary (1000 cap) is used extensively** in kernel for subscriptions, stages, rate limiters, capabilities, knowledge entries, containers, quotas, and jobs. | Plugin causes one of these dictionaries to reach 1000 entries, triggering LRU eviction of oldest entries. For subscriptions, this means the oldest topic's subscriptions are silently removed. | Monitor counts of registered items. Be aware that the kernel has a hard cap of 1000 per category. |

---

## 16. Production Audit Results (Phase 90.5)

### 16.1 Audit Scope

Full line-by-line audit of all ~970 .cs files in `DataWarehouse.SDK/` and all files in `DataWarehouse.Kernel/`.
77 findings identified, categorized by severity. See `Metadata/SDK-AUDIT-STATUS.md` for the complete finding-by-finding status table.

### 16.2 SDK Audit Summary

| Category | Total | Fixed | Improved | Deferred (v6.0) | By Design |
|----------|-------|-------|----------|------------------|-----------|
| CRITICAL | 12 | 6 | 6 | 0 | 0 |
| HIGH | 18 | 16 | 1 | 1 | 0 |
| MEDIUM | 24 | 14 | 2 | 3 | 5 |
| LOW | 15 | 14 | 0 | 1 | 0 |
| INFO | 8 | 0 | 0 | 0 | 8 |
| **Total** | **77** | **50** | **9** | **5** | **13** |

### 16.3 Kernel Audit Summary

Kernel is **100% production-ready**. All findings resolved:
- F-07: AdvancedMessageBus catch logging
- F-09: PluginRegistry metadata caching (eliminates sync-over-async)
- F-11: Unnecessary async patterns in ContainerManager, PipelineMigrationEngine, PipelinePluginIntegration
- IPipelineOrchestrator async method additions
- EnhancedPipelineOrchestrator volatile cache pattern
- KernelStorageService real index persistence

### 16.4 Key Fixes Applied

**Sync-over-async elimination:**
- PluginRegistry: Eager metadata caching at Register() time → `SelectBestPlugin` is fully synchronous
- KeyStore: `GetKey()` reads from cache, `GetKeyAsync()` for initial load
- BoundedDictionary/List/Queue: Fire-and-forget dispose + `IAsyncDisposable`
- DisruptorMessageBus: `SpinWait` + `TryWrite` loop replaces `.GetAwaiter().GetResult()`
- WalMessageQueue: `IAsyncDisposable` with proper `FlushAllAsync()` await
- ZeroConfigClusterBootstrap, MdnsServiceDiscovery: CTS cancel pattern

**Hardware integration (from stubs to real):**
- HypervisorDetector: Real `X86Base.CpuId()` leaf 0x40000000
- BalloonDriver: Real `GC.RegisterForFullGCNotification()` integration
- NvmePassthrough: Real `DeviceIoControl` (Windows) + `ioctl` (Linux)
- SecretsManagerKeyStore: Real AWS Secrets Manager with SigV4

**VDE Adaptive Index morph levels wired:**
- Level 0: DirectPointer
- Level 1: SortedArray
- Level 2: AdaptiveRadixTree (ART)
- Level 3: BeTree (B-epsilon tree)
- Level 4: AlexLearnedIndex (wrapping BeTree)
- Level 5: BeTreeForest
- Level 6: DistributedRouting (requires cluster config → v6.0 Phase 91)

**Unnecessary async pattern removal:**
- 6 TamperProof provider bases: `await Task.FromResult(...)` → `Task.FromResult(...)`
- FederationOrchestrator: `await Task.CompletedTask` → `return Task.CompletedTask`
- HardwareAccelerationPluginBases: 5 instances removed
- OnnxWasiNnHost: Direct return

**Security improvements:**
- NamespaceAuthority: HMAC-SHA512 (symmetric) → ECDSA-P256 (asymmetric)
- SlsaVerifier: RSA-PSS primary, HMAC as documented fallback only
- Cloud providers: `IsProductionReady => false` guard prevents accidental production use

### 16.5 Items Deferred to v6.0

| Item | Reason | v6.0 Phase |
|------|--------|------------|
| TPM2/HSM/QAT crypto | Requires hardware; throws `PlatformNotSupportedException` | 90.5 |
| VDE indirect blocks | B-tree redesign required for files >50KB | 86+ |
| External sort >512MB | Spill-to-disk merge sort engine needed | 86+ |
| Hash table bucket merging | Optimization, not blocking | 86+ |
| DistributedRouting morph level | Requires cluster topology | 91 |

### 16.6 SemaphoreSlim.Wait() Patterns (Accepted by Design)

Five instances of synchronous `SemaphoreSlim.Wait()` remain and are accepted:
- **GpuVectorKernels**: GPU kernel launch is inherently synchronous
- **ClockSiTransaction**: Property getters cannot be async
- **FreeSpaceManager**: Allocation hot path, minimal lock scope
- **ArcCacheL3NVMe**: Cache eviction, minimal lock scope
- **DeadlineScheduler**: Dedicated processing thread (not thread pool)
