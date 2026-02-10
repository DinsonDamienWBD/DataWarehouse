# Phase 2: Core Infrastructure (Intelligence, RAID, Compression) - Research

**Researched:** 2026-02-10
**Domain:** C# .NET 10.0 / Plugin Architecture / Strategy Pattern / AI Integration
**Confidence:** HIGH

## Summary

Phase 2 implements three major infrastructure plugins using the SDK foundation established in Phase 1. The UniversalIntelligence plugin provides unified AI/ML capabilities through provider strategies, the UltimateRAID plugin implements 50+ RAID levels via strategy pattern, and the UltimateCompression plugin delivers comprehensive compression algorithms. All three follow the established SDK architecture with strategy-based extensibility, message bus communication, and no direct inter-plugin dependencies.

**Key Architectural Pattern:** Plugin isolation via ProjectReference to SDK only, with all inter-plugin communication through message bus. Strategies registered via auto-discovery and exposed through unified interfaces.

**Primary recommendation:** Complete existing partial implementations following the established patterns already in codebase. Many structures exist but are incomplete - verify completeness before implementing.

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| .NET SDK | 10.0 | Runtime platform | Latest LTS with C# latest features (record types, init-only, required properties) |
| Microsoft.Extensions.Logging.Abstractions | 10.0.2 | Logging infrastructure | Standard .NET logging abstraction used across SDK |
| Newtonsoft.Json | 13.0.4 | JSON serialization | SDK dependency for KnowledgeObject and message serialization |
| System.IO.Hashing | 10.0.2 | Hashing operations | High-performance hash functions for integrity checks |

### Intelligence Plugin
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| (AI Provider SDKs) | Latest stable | AI provider integrations | OpenAI, Anthropic, Azure, AWS, HuggingFace client libraries |
| (Vector Store SDKs) | Latest stable | Vector database clients | Pinecone, Weaviate, Milvus, Qdrant, Chroma, pgVector clients |
| (Graph Database SDKs) | Latest stable | Knowledge graph backends | Neo4j driver, graph database clients |

### RAID Plugin
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| System.Management | 10.0.* | SMART monitoring (Windows only) | Windows disk health monitoring |
| (Intel ISA-L bindings) | TBD | Hardware acceleration | Erasure coding performance optimization |

### Compression Plugin
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| K4os.Compression.LZ4 | 1.3.* | LZ4 compression | Industry-standard fast compression |
| K4os.Compression.LZ4.Streams | 1.3.* | LZ4 streaming | Streaming compression support |
| ZstdSharp.Port | 0.8.* | Zstandard compression | Modern high-ratio compression |
| SharpCompress | 0.44.5 | Archive formats | Multi-format compression support |
| Snappier | 1.3.0 | Snappy compression | Google's fast compression |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Newtonsoft.Json | System.Text.Json | System.Text.Json is faster but Newtonsoft provides more flexibility for complex scenarios; SDK uses Newtonsoft |
| SharpCompress | Native P/Invoke | SharpCompress provides managed code safety but P/Invoke could offer better performance for specific formats |
| ZstdSharp.Port | Native libzstd P/Invoke | Pure C# port is easier to deploy but native would be faster |

**Installation:**
```bash
# SDK packages already installed in Phase 1
# Intelligence Plugin
cd Plugins/DataWarehouse.Plugins.UltimateIntelligence
dotnet add package [AI provider SDKs as needed]

# RAID Plugin
cd Plugins/DataWarehouse.Plugins.UltimateRAID
# System.Management already in csproj

# Compression Plugin
cd Plugins/DataWarehouse.Plugins.UltimateCompression
# All compression packages already in csproj
```

## Architecture Patterns

### Recommended Project Structure
```
Plugins/DataWarehouse.Plugins.UltimateIntelligence/
├── Strategies/
│   ├── Providers/           # AI provider strategies (OpenAI, Claude, etc.)
│   ├── VectorStores/        # Vector database strategies
│   ├── KnowledgeGraphs/     # Graph database strategies
│   ├── Features/            # AI-powered features (semantic search, classification)
│   └── Memory/              # Long-term memory subsystem
├── IIntelligenceStrategy.cs # Strategy interface
├── IntelligenceStrategyBase.cs # Base implementation
├── UltimateIntelligencePlugin.cs # Plugin orchestrator
└── KnowledgeSystem.cs       # Knowledge envelope management

Plugins/DataWarehouse.Plugins.UltimateRAID/
├── Strategies/
│   ├── Standard/            # RAID 0-6, 1E, 5E, 6E
│   ├── Nested/              # RAID 10, 01, 50, 60, 100
│   ├── Extended/            # Advanced RAID levels
│   ├── ZFS/                 # RAID-Z, RAID-Z2, RAID-Z3
│   ├── Vendor/              # NetApp, Synology, etc.
│   ├── Erasure/             # Reed-Solomon, LRC, ISA-L
│   └── Adaptive/            # AI-driven RAID
├── Features/
│   ├── Monitoring.cs        # Health monitoring
│   ├── PerformanceOptimization.cs # Performance tuning
│   ├── RaidLevelMigration.cs # Level migration
│   └── BadBlockRemapping.cs  # Self-healing
├── IRaidStrategy.cs         # Strategy interface
├── RaidStrategyBase.cs      # Base implementation
├── UltimateRaidPlugin.cs    # Plugin orchestrator
└── RaidStrategyRegistry.cs  # Strategy auto-discovery

Plugins/DataWarehouse.Plugins.UltimateCompression/
├── Strategies/
│   ├── LzFamily/            # LZ4, LZ77, LZ78, LZMA, Zstd, etc.
│   ├── Transform/           # BWT, MTF, Brotli, Bzip2
│   ├── EntropyCoding/       # Huffman, Arithmetic, ANS, RANS
│   ├── Delta/               # Delta, XDelta, Bsdiff
│   ├── ContextMixing/       # PAQ, ZPAQ, PPM
│   ├── Domain/              # Time-series, DNA, media-specific
│   ├── Archive/             # ZIP, RAR, 7z, TAR
│   ├── Transit/             # In-transit compression strategies
│   └── Emerging/            # Experimental algorithms
├── UltimateCompressionPlugin.cs # Plugin orchestrator
└── [Strategy implementations]
```

### Pattern 1: Strategy Registration via Auto-Discovery
**What:** Strategies self-register through reflection-based discovery at plugin initialization
**When to use:** When you have many strategy implementations and want extensibility without manual registration

**Example:**
```csharp
// Source: DataWarehouse.Plugins.UltimateRAID/UltimateRaidPlugin.cs
public sealed class UltimateRaidPlugin : IntelligenceAwarePluginBase
{
    private readonly RaidStrategyRegistry _registry;

    public UltimateRaidPlugin()
    {
        _registry = new RaidStrategyRegistry();
        DiscoverAndRegisterStrategies();
    }

    private void DiscoverAndRegisterStrategies()
    {
        // Auto-discover strategies in this assembly
        _registry.DiscoverStrategies(Assembly.GetExecutingAssembly());
    }
}
```

### Pattern 2: Intelligence-Aware Plugin Base
**What:** Plugins inherit from IntelligenceAwarePluginBase to get AI integration for free
**When to use:** When plugin functionality can be enhanced with AI/ML capabilities

**Example:**
```csharp
// Source: SDK pattern used in UltimateRaidPlugin.cs
public sealed class UltimateRaidPlugin : IntelligenceAwarePluginBase
{
    protected override Task OnStartWithIntelligenceAsync(CancellationToken ct)
    {
        // Subscribe to Intelligence-enhanced topics
        MessageBus?.Subscribe(RaidTopics.PredictFailure, HandlePredictFailureAsync);
        MessageBus?.Subscribe(RaidTopics.OptimizeLevel, HandleOptimizeLevelAsync);
        return Task.CompletedTask;
    }

    protected override Task OnStartWithoutIntelligenceAsync(CancellationToken ct)
    {
        // Basic operation without AI enhancements
        return Task.CompletedTask;
    }

    private async Task HandlePredictFailureAsync(PluginMessage message)
    {
        if (!IsIntelligenceAvailable) return;

        var prediction = await RequestPredictionAsync(
            "disk-failure",
            predictionData,
            new IntelligenceContext { Timeout = TimeSpan.FromSeconds(10) }
        );

        message.Payload["failureProbability"] = prediction.Confidence;
    }
}
```

### Pattern 3: KnowledgeObject Envelope for Inter-Plugin Communication
**What:** Standardized message format for AI-enhanced data exchange between plugins
**When to use:** When plugins need to share typed data with provenance, versioning, and temporal tracking

**Example:**
```csharp
// Source: DataWarehouse.SDK/AI/Knowledge/KnowledgeObject.cs
var knowledgeObj = KnowledgeObject.Create(
    KnowledgeObjectType.Query,
    KnowledgePayload.Json(requestData),
    sourcePluginId: "com.datawarehouse.raid.ultimate",
    targetPluginId: "com.datawarehouse.intelligence.ultimate"
);

// With provenance tracking
knowledgeObj = knowledgeObj with
{
    Provenance = KnowledgeProvenance.FromPlugin(
        "com.datawarehouse.raid.ultimate",
        TrustScore.Simple(0.95)
    )
};

// Send via message bus
await MessageBus.SendAsync(topic, knowledgeObj);
```

### Pattern 4: CompressionStrategyBase for Algorithm Implementations
**What:** Abstract base class providing statistics, content detection, and stream handling
**When to use:** Every compression algorithm implementation inherits this

**Example:**
```csharp
// Source: DataWarehouse.SDK/Contracts/Compression/CompressionStrategy.cs
public class Lz4Strategy : CompressionStrategyBase
{
    public Lz4Strategy(CompressionLevel level) : base(level) { }

    public override CompressionCharacteristics Characteristics => new()
    {
        AlgorithmName = "LZ4",
        TypicalCompressionRatio = 0.45,
        CompressionSpeed = 10,      // Extremely fast
        DecompressionSpeed = 10,
        CompressionMemoryUsage = 64 * 1024,
        DecompressionMemoryUsage = 64 * 1024,
        SupportsStreaming = true,
        SupportsParallelCompression = false,
        SupportsParallelDecompression = false,
        SupportsRandomAccess = false
    };

    protected override byte[] CompressCore(byte[] input)
    {
        // LZ4 implementation using K4os.Compression.LZ4
        return LZ4Pickler.Pickle(input, LZ4Level.L00_FAST);
    }

    protected override byte[] DecompressCore(byte[] input)
    {
        return LZ4Pickler.Unpickle(input);
    }

    // Streaming and async methods inherited from base
}
```

### Pattern 5: RaidStrategyBase for RAID Level Implementations
**What:** Abstract base class providing parity calculation, stripe distribution, health monitoring
**When to use:** Every RAID level implementation inherits this

**Example:**
```csharp
// Source: DataWarehouse.SDK/Contracts/RAID/RaidStrategy.cs
public class Raid5Strategy : RaidStrategyBase
{
    public override RaidLevel Level => RaidLevel.Raid5;

    public override RaidCapabilities Capabilities => new(
        RedundancyLevel: 1,
        MinDisks: 3,
        MaxDisks: null,
        StripeSize: 65536,
        EstimatedRebuildTimePerTB: TimeSpan.FromHours(4),
        ReadPerformanceMultiplier: 2.5,
        WritePerformanceMultiplier: 1.5,
        CapacityEfficiency: 0.75,  // (n-1)/n efficiency
        SupportsHotSpare: true,
        SupportsOnlineExpansion: true,
        RequiresUniformDiskSize: false
    );

    public override StripeInfo CalculateStripe(long blockIndex, int diskCount)
    {
        var parityDisk = (int)(blockIndex % diskCount);
        var dataDisks = Enumerable.Range(0, diskCount)
            .Where(i => i != parityDisk)
            .ToArray();

        return new StripeInfo(
            StripeIndex: blockIndex,
            DataDisks: dataDisks,
            ParityDisks: new[] { parityDisk },
            ChunkSize: Capabilities.StripeSize,
            DataChunkCount: diskCount - 1,
            ParityChunkCount: 1
        );
    }

    protected override ReadOnlyMemory<byte> CalculateXorParity(IEnumerable<ReadOnlyMemory<byte>> dataChunks)
    {
        // XOR parity calculation inherited from base
        return base.CalculateXorParity(dataChunks);
    }
}
```

### Anti-Patterns to Avoid
- **Direct Plugin References:** Never add ProjectReference between plugins. Use SDK + message bus only.
- **Simulations/Mocks in Production Code:** Rule 13 violation. All strategies must be real implementations.
- **Hardcoded Strategy Lists:** Use auto-discovery via reflection, not manual registration.
- **Synchronous Message Bus Calls:** Always use async/await for message bus operations.
- **Missing Intelligence Availability Checks:** Always check `IsIntelligenceAvailable` before using AI features.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| AI Provider Integration | Custom HTTP clients for each AI service | Existing provider SDKs (OpenAI SDK, Anthropic SDK, Azure.AI, etc.) | Each provider has authentication nuances, rate limiting, error handling, streaming support - SDKs handle these edge cases |
| Vector Similarity Search | Custom vector distance calculations | Vector store SDKs (Pinecone, Weaviate clients) | Index optimization, approximate nearest neighbor algorithms, distributed search require specialized data structures |
| Erasure Coding (Reed-Solomon) | Manual Galois field arithmetic | Intel ISA-L library or existing Reed-Solomon implementations | Polynomial math over GF(256), hardware acceleration, SIMD optimizations are complex and error-prone |
| SMART Disk Monitoring | Raw disk sector parsing | System.Management (Windows), smartmontools (Linux) | SMART attribute interpretation varies by vendor, requires low-level disk access, parsing is complex |
| Compression Format Parsing | Custom archive format readers | SharpCompress library | Archive formats have complex headers, multi-volume support, encryption, CRC checks |
| Hash-based Content Detection | Custom magic byte detection | System.IO.Hashing + predefined signatures | File format signatures have many variants, partial matches, need continuous updates |
| Parity Calculation | Naive byte-by-byte XOR loops | SIMD-optimized parity functions (Vector<T> in .NET) | Modern CPUs can XOR 16-32 bytes per instruction; hand-rolled loops are 10-20x slower |
| LZ4/Zstd Compression | Manual dictionary building, entropy coding | K4os.Compression.LZ4, ZstdSharp.Port | These algorithms have tuned parameters, streaming modes, dictionary training that took years to optimize |

**Key insight:** Phase 1 SDK already provides the base classes (CompressionStrategyBase, RaidStrategyBase, IAIProvider). Implementations should delegate complex operations to proven libraries rather than reimplementing algorithms.

## Common Pitfalls

### Pitfall 1: Assuming Global Configuration Means No Project-Level Config
**What goes wrong:** Developers assume because SDK has global configuration that plugins can't have project-scoped settings
**Why it happens:** Misunderstanding of .NET project configuration scopes
**How to avoid:** Plugins can define their own config scopes: global defaults in SDK, plugin-specific config in plugin project, strategy-specific config per strategy instance
**Warning signs:** Plugin constructors trying to access global config for plugin-specific settings; no way to configure individual AI providers differently

### Pitfall 2: Incomplete Strategy Interface Implementations
**What goes wrong:** Strategies implement interfaces but leave methods throwing NotImplementedException, or only implement sync methods when async is required
**Why it happens:** Interface segregation - copying boilerplate without understanding requirements
**How to avoid:** Use SDK base classes (CompressionStrategyBase, RaidStrategyBase, IntelligenceStrategyBase) which provide complete default implementations; override only what's needed
**Warning signs:** Build succeeds but runtime throws NotImplementedException; async methods delegating to Task.Run(SyncMethod) everywhere

### Pitfall 3: Message Bus Payload Type Mismatches
**What goes wrong:** Plugin sends Dictionary<string, object> payload but receiving plugin expects specific type, causing InvalidCastException
**Why it happens:** Message bus is untyped; no compile-time validation of payload structure
**How to avoid:** Define payload contracts in SDK (e.g., RaidTopics constants, standard payload key names); validate payload keys before casting; use TryGetValue pattern with type checks
**Warning signs:** Plugins communicate in development but fail in integration; ArgumentException on missing payload keys

### Pitfall 4: Intelligence Not Available Assumptions
**What goes wrong:** Code assumes Intelligence plugin is always available and calls AI features without checking `IsIntelligenceAvailable`
**Why it happens:** Development environment has Intelligence enabled; production/test environments may not
**How to avoid:** Always check `IsIntelligenceAvailable` before calling AI methods; provide fallback behavior; implement OnStartWithoutIntelligenceAsync for degraded mode
**Warning signs:** NullReferenceException when accessing MessageBus AI methods; plugin fails to start when Intelligence is disabled

### Pitfall 5: Missing RAID Strategy Temp Files Exclusion
**What goes wrong:** Build includes old/incomplete strategy files causing compilation errors
**Why it happens:** UltimateRAID.csproj has `<Compile Remove="Strategies\**\*.cs" />` to exclude incomplete implementations
**How to avoid:** Keep exclusion until strategies are verified complete; remove exclusion per-strategy as each is completed and tested
**Warning signs:** Random compilation errors in strategy files; duplicate type definitions

### Pitfall 6: Compression Strategy Entropy Detection False Positives
**What goes wrong:** Already-compressed files get compressed again, causing expansion; encrypted files incorrectly identified as compressible
**Why it happens:** Naive entropy checks without magic byte detection; not using CompressionStrategyBase.ShouldCompress()
**How to avoid:** Use base class content type detection (checks magic bytes first, then entropy); respect IsCompressedFormat() and IsMediaFormat() results
**Warning signs:** Compressed files grow after "compression"; performance degradation from double-compression attempts

### Pitfall 7: RAID Rebuild Priority Conflicts
**What goes wrong:** Multiple concurrent rebuilds saturate I/O, causing array degradation or timeouts
**Why it happens:** No rebuild throttling; each strategy manages its own rebuild independently
**How to avoid:** UltimateRaidPlugin has `MaxConcurrentRebuilds` setting; implement rebuild queue at plugin level, not strategy level
**Warning signs:** Array performance drops to near-zero during rebuild; health checks timing out during rebuild operations

### Pitfall 8: Vector Store Client Disposal Leaks
**What goes wrong:** Vector store connections leak, causing "too many open connections" errors over time
**Why it happens:** AI provider strategies create clients but don't implement IDisposable properly; plugin disposal doesn't cascade to strategies
**How to avoid:** Implement IDisposable in strategy base classes; UltimateIntelligencePlugin.Dispose() must dispose all registered strategies; use `using` pattern for per-request clients
**Warning signs:** Memory usage grows over time; socket exhaustion errors after extended operation

### Pitfall 9: Knowledge Object Temporal Query Misuse
**What goes wrong:** Temporal queries (AsOf, Between) fail or return incorrect results
**Why it happens:** KnowledgeTimeline not updated with AddVersion(); queries on objects without temporal tracking
**How to avoid:** Update timeline on every knowledge object mutation; use KnowledgeObject.WithNewVersion() to create versioned copies; populate TemporalContext when versioning matters
**Warning signs:** GetAsOf() returns null for valid timestamps; version history missing entries

### Pitfall 10: Compression Statistics Thread Safety
**What goes wrong:** Concurrent compression operations cause statistics corruption or race conditions
**Why it happens:** Multiple threads calling Compress/Decompress on same strategy instance without thread-safe stats
**How to avoid:** CompressionStrategyBase uses lock (_statsLock) for all statistics updates; don't override GetStatistics() without proper locking
**Warning signs:** Statistics counters decrease inexplicably; AverageCompressionRatio becomes NaN or negative

## Code Examples

Verified patterns from official sources:

### UniversalIntelligence: AI Provider Strategy Pattern
```csharp
// Source: DataWarehouse.SDK/AI/IAIProvider.cs
public interface IAIProvider
{
    string ProviderId { get; }
    string DisplayName { get; }
    bool IsAvailable { get; }
    AICapabilities Capabilities { get; }

    Task<AIResponse> CompleteAsync(AIRequest request, CancellationToken ct = default);
    IAsyncEnumerable<AIStreamChunk> CompleteStreamingAsync(AIRequest request, CancellationToken ct = default);
    Task<float[]> GetEmbeddingsAsync(string text, CancellationToken ct = default);
}

// Implementation pattern for OpenAI provider
public class OpenAiProviderStrategy : IAIProvider
{
    private readonly OpenAIClient _client;

    public string ProviderId => "openai";
    public string DisplayName => "OpenAI";
    public bool IsAvailable => _client != null;
    public AICapabilities Capabilities => AICapabilities.All;

    public async Task<AIResponse> CompleteAsync(AIRequest request, CancellationToken ct = default)
    {
        var completion = await _client.GetChatCompletionsAsync(
            request.Model ?? "gpt-4",
            new ChatCompletionsOptions
            {
                Messages = { new ChatMessage(ChatRole.User, request.Prompt) },
                MaxTokens = request.MaxTokens,
                Temperature = request.Temperature
            },
            ct
        );

        return new AIResponse
        {
            Success = true,
            Content = completion.Value.Choices[0].Message.Content,
            FinishReason = completion.Value.Choices[0].FinishReason.ToString(),
            Usage = new AIUsage
            {
                PromptTokens = completion.Value.Usage.PromptTokens,
                CompletionTokens = completion.Value.Usage.CompletionTokens
            }
        };
    }
}
```

### UltimateRAID: Strategy Capabilities Declaration
```csharp
// Source: Plugins/DataWarehouse.Plugins.UltimateRAID/UltimateRaidPlugin.cs
protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
{
    get
    {
        var capabilities = new List<RegisteredCapability>();

        // Per-strategy capabilities with metadata
        foreach (var strategy in _registry.GetAllStrategies())
        {
            capabilities.Add(new RegisteredCapability
            {
                CapabilityId = $"{Id}.strategy.{strategy.StrategyId}",
                DisplayName = $"RAID {strategy.RaidLevel} - {strategy.StrategyName}",
                Description = $"{strategy.StrategyName} ({strategy.Category})",
                Category = CapabilityCategory.Storage,
                SubCategory = $"RAID-{strategy.RaidLevel}",
                Tags = new[] { "raid", "storage", $"raid-{strategy.RaidLevel}" },
                Metadata = new Dictionary<string, object>
                {
                    ["strategyId"] = strategy.StrategyId,
                    ["minimumDisks"] = strategy.MinimumDisks,
                    ["faultTolerance"] = strategy.FaultTolerance,
                    ["storageEfficiency"] = strategy.StorageEfficiency
                }
            });
        }

        return capabilities;
    }
}
```

### UltimateCompression: Streaming Compression with Statistics
```csharp
// Source: DataWarehouse.SDK/Contracts/Compression/CompressionStrategy.cs
public class ZstdStrategy : CompressionStrategyBase
{
    public override CompressionCharacteristics Characteristics => new()
    {
        AlgorithmName = "Zstandard",
        TypicalCompressionRatio = 0.35,
        CompressionSpeed = 7,
        DecompressionSpeed = 9,
        SupportsStreaming = true,
        SupportsParallelCompression = true,
        SupportsRandomAccess = true,
        OptimalBlockSize = 131072  // 128KB blocks
    };

    protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
    {
        return new ZstdSharp.CompressionStream(output, (int)Level, leaveOpen);
    }

    protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
    {
        return new ZstdSharp.DecompressionStream(input, leaveOpen);
    }

    // Content detection inherited from base
    public override bool ShouldCompress(ReadOnlySpan<byte> input)
    {
        // Base class checks: size threshold, magic bytes, entropy
        return base.ShouldCompress(input);
    }

    // Statistics tracking automatic via base class
    // GetStatistics() thread-safe via base class lock
}
```

### Knowledge Object Creation and Provenance
```csharp
// Source: DataWarehouse.SDK/AI/Knowledge/KnowledgeObject.cs
// Creating a knowledge request
var request = KnowledgeObject.Create(
    KnowledgeObjectType.Query,
    KnowledgePayload.Json(new { query = "compress this data", algorithm = "auto" }),
    sourcePluginId: "client-plugin",
    targetPluginId: "com.datawarehouse.compression.ultimate"
);

// Adding provenance for AI-derived knowledge
var inferredKnowledge = KnowledgeObject.Create(
    KnowledgeObjectType.Response,
    KnowledgePayload.Json(new { recommendedAlgorithm = "zstd", confidence = 0.92 }),
    sourcePluginId: "com.datawarehouse.intelligence.ultimate"
);

inferredKnowledge = inferredKnowledge with
{
    Provenance = KnowledgeProvenance.FromInference(
        "intelligence-classifier",
        new ProvenanceChain
        {
            SourceObjectIds = { request.ObjectId },
            DerivationMethod = "ml-classification",
            Confidence = 0.92
        },
        TrustScore.FromFactors(new Dictionary<string, double>
        {
            ["model_accuracy"] = 0.94,
            ["training_data_quality"] = 0.90,
            ["prediction_confidence"] = 0.92
        })
    )
};
```

### Message Bus Subscription and Handling
```csharp
// Source: Plugins/DataWarehouse.Plugins.UltimateRAID/UltimateRaidPlugin.cs
protected override Task OnStartCoreAsync(CancellationToken ct)
{
    if (MessageBus != null)
    {
        MessageBus.Subscribe(RaidTopics.Write, HandleWriteAsync);
        MessageBus.Subscribe(RaidTopics.Read, HandleReadAsync);
        MessageBus.Subscribe(RaidTopics.Health, HandleHealthCheckAsync);
    }
    return Task.CompletedTask;
}

private async Task HandleWriteAsync(PluginMessage message)
{
    // Validate payload structure
    if (!message.Payload.TryGetValue("strategyId", out var sidObj) || sidObj is not string strategyId)
    {
        throw new ArgumentException("Missing 'strategyId' parameter");
    }

    if (!message.Payload.TryGetValue("lba", out var lbaObj) || lbaObj is not long lba)
    {
        throw new ArgumentException("Missing 'lba' parameter");
    }

    if (!message.Payload.TryGetValue("data", out var dataObj) || dataObj is not byte[] data)
    {
        throw new ArgumentException("Missing or invalid 'data' parameter");
    }

    // Execute operation
    var strategy = _registry.GetStrategy(strategyId)
        ?? throw new ArgumentException($"RAID strategy '{strategyId}' not found");

    await strategy.WriteAsync(lba, data);

    // Return result in payload
    message.Payload["bytesWritten"] = data.Length;
    message.Payload["success"] = true;
}
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Separate RAID/Compression plugins per level | Unified plugin with strategy pattern | Phase 1 design | Single plugin reduces orchestration complexity, strategy auto-discovery enables extensibility |
| Direct plugin-to-plugin calls | Message bus + KnowledgeObject envelope | Phase 1 SDK | Enables AI-enhanced features, provenance tracking, temporal queries |
| Custom AI integrations per plugin | UniversalIntelligence plugin with IAIProvider abstraction | Phase 2 requirement AI-01 | Single integration point, provider switching without code changes |
| Synchronous compression APIs | Async/streaming with CompressionStrategyBase | SDK design | Supports large file compression without memory exhaustion |
| Manual RAID stripe calculations | CalculateStripe() abstraction | SDK RaidStrategyBase | Strategy implementations don't reimplement stripe math |
| Hardcoded strategy registration | Reflection-based auto-discovery | Current pattern | New strategies added without modifying plugin code |

**Deprecated/outdated:**
- **Separate AI provider plugins**: Replaced by single UniversalIntelligence with provider strategies
- **Manual strategy factory pattern**: Replaced by RaidStrategyRegistry.DiscoverStrategies()
- **Synchronous-only compression**: All strategies must support async via CompressionStrategyBase
- **Global-only configuration**: Plugins support plugin-scoped and strategy-scoped configuration

## Open Questions

1. **AI Provider SDK Versions**
   - What we know: Plugins will integrate OpenAI, Anthropic, Azure, AWS, HuggingFace, Ollama
   - What's unclear: Exact NuGet package names and version constraints for each provider
   - Recommendation: Research each provider SDK during T90 implementation; use latest stable versions; add to UltimateIntelligence.csproj as needed

2. **Intel ISA-L Bindings for Erasure Coding**
   - What we know: RAID plugin needs hardware-accelerated erasure coding for performance
   - What's unclear: Best .NET binding for Intel ISA-L (native P/Invoke vs managed wrapper)
   - Recommendation: Start with managed Reed-Solomon implementation, benchmark, then add ISA-L as performance optimization if needed

3. **SMART Monitoring Cross-Platform Support**
   - What we know: System.Management works for Windows; Linux needs different approach
   - What's unclear: Best cross-platform abstraction for SMART data
   - Recommendation: Conditional compilation (#if Windows) for System.Management; Linux can use smartmontools via Process or leave unimplemented initially

4. **KnowledgeObject Message Size Limits**
   - What we know: KnowledgeObject can contain arbitrary payloads
   - What's unclear: Message bus size limits for large payloads (e.g., embedding vectors)
   - Recommendation: Design for reference-based payloads (e.g., store large vectors in vector store, pass only IDs in KnowledgeObject)

5. **Compression Strategy Selection Algorithm**
   - What we know: CompressionStrategyBase has ShouldCompress() and content type detection
   - What's unclear: Whether UltimateCompression should provide automatic algorithm selection or require explicit choice
   - Recommendation: Implement both - explicit selection via strategyId, auto-selection via "adaptive" strategy that uses content detection + Intelligence if available

6. **RAID Array Persistence**
   - What we know: RAID strategies manage in-memory state during operation
   - What's unclear: Whether array configuration (disk mapping, stripe size) should persist to disk for recovery
   - Recommendation: Implement in-memory first, add optional persistence using SDK serialization patterns if needed

## Sources

### Primary (HIGH confidence)
- DataWarehouse.SDK codebase - verified complete base classes for all three plugin types
- DataWarehouse.SDK/AI/IAIProvider.cs - AI provider interface specification
- DataWarehouse.SDK/AI/Knowledge/KnowledgeObject.cs - Complete KnowledgeObject implementation with 1198 lines
- DataWarehouse.SDK/Contracts/Compression/CompressionStrategy.cs - Complete compression base class with 1145 lines
- DataWarehouse.SDK/Contracts/RAID/RaidStrategy.cs - Complete RAID base class with 939 lines
- Plugins/DataWarehouse.Plugins.UltimateRAID/UltimateRaidPlugin.cs - Complete plugin implementation with 1000 lines
- Plugins/DataWarehouse.Plugins.UltimateRAID/IRaidStrategy.cs - Plugin-specific RAID interface
- Project file analysis - NuGet package versions verified from .csproj files

### Secondary (MEDIUM confidence)
- .NET 10.0 documentation - C# language features (record types, init-only, required properties)
- K4os.Compression.LZ4, ZstdSharp, SharpCompress package documentation - verified via NuGet.org

### Tertiary (LOW confidence)
- Intel ISA-L bindings - needs verification for .NET availability and API
- AI provider SDK versions - needs verification during implementation
- Cross-platform SMART monitoring - requires research for Linux/macOS approaches

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH - All packages verified in existing .csproj files or standard .NET libraries
- Architecture: HIGH - Patterns extracted from existing complete SDK implementations
- Pitfalls: HIGH - Based on identified patterns in codebase (e.g., `<Compile Remove>` exclusion, IntelligenceAwarePluginBase availability checks)
- AI Provider Integration: MEDIUM - Interface defined, but specific provider SDK details need verification
- Cross-platform SMART: LOW - Windows-only implementation exists, Linux/macOS needs research

**Research date:** 2026-02-10
**Valid until:** 2026-03-12 (30 days - stable SDK, but AI provider SDKs evolve rapidly)
