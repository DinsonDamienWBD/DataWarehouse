# Phase 21: UltimateDataTransit - Research

**Researched:** 2026-02-11
**Domain:** Data transport strategy orchestration (physical data movement mechanics)
**Confidence:** HIGH

## Summary

UltimateDataTransit is a new Ultimate-pattern plugin that provides user-selectable data transport strategies -- the physical "how" of data movement. This is distinct from AEDS (which handles distribution/delivery semantics) and AdaptiveTransport (T78, which handles protocol morphing). UltimateDataTransit focuses on the strategy-pattern orchestration of transport mechanisms: direct transfers (HTTP/2, HTTP/3, gRPC, FTP/SFTP, SCP/rsync), chunked/resumable transfers, P2P swarm, delta/differential sync, multi-path parallel, store-and-forward, with composable layers for compression-in-transit and encryption-in-transit.

The SDK already has rich transit-layer contracts in `DataWarehouse.SDK.Security.Transit` (ITransitEncryption, ITransitCompression, ITransitEncryptionStage, ITranscryptionService, ICommonCipherPresets) and AEDS transport contracts (IDataPlaneTransport, IControlPlaneTransport). The UltimateDataTransit plugin needs its own SDK strategy interface (`IDataTransitStrategy`) in a new `DataWarehouse.SDK.Contracts.Transit` namespace, following the same pattern as IEncryptionStrategy, IReplicationStrategy, IStreamingStrategy, etc. Each transport mechanism becomes a strategy. The plugin orchestrator discovers, registers, and auto-selects strategies based on endpoint capabilities, network conditions, and cost/QoS policies.

**Primary recommendation:** Create SDK contracts in `DataWarehouse.SDK/Contracts/Transit/` defining `IDataTransitStrategy`, capabilities, configuration types, and `DataTransitStrategyBase`. Implement the plugin as `DataWarehouse.Plugins.UltimateDataTransit` with a sealed `UltimateDataTransitPlugin` orchestrator extending `FeaturePluginBase`, using strategy pattern, message bus for cross-plugin delegation, and composable pipeline layers.

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| .NET 10 | net10.0 | Target framework | Project standard |
| System.Net.Http | built-in | HTTP/2 and HTTP/3 transfers | Native .NET support |
| System.Net.Quic | built-in | QUIC/HTTP/3 transport | .NET 10 native QUIC |
| Grpc.Net.Client | 2.x | gRPC streaming transfers | Standard .NET gRPC |
| System.IO.Compression | built-in | Compression-in-transit layer | Native .NET compression |
| System.Security.Cryptography | built-in | Encryption-in-transit layer | Native .NET crypto |
| DataWarehouse.SDK | local | Plugin SDK, contracts, base classes | Mandatory per rules |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| SSH.NET | latest | SFTP/SCP transfers | SFTP/SCP strategy implementation |
| FluentFTP | latest | FTP/FTPS transfers | FTP strategy implementation |
| System.IO.Hashing | built-in | Rolling hash for delta sync | Delta/differential transfer checksums |
| System.Threading.Channels | built-in | Async producer/consumer pipelines | Internal transfer coordination |
| System.IO.Pipelines | built-in | High-performance I/O | Streaming transfer pipelines |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| SSH.NET for SFTP | Renci.SshNet | SSH.NET is the active fork, Renci.SshNet is legacy |
| FluentFTP for FTP | System.Net.FtpClient | FluentFTP is more actively maintained, richer API |
| Custom delta sync | xdelta3, bsdiff | These are native binaries; rolling-hash in managed code avoids native deps |

**Installation:**
```xml
<!-- In .csproj -->
<PackageReference Include="Grpc.Net.Client" Version="2.*" />
<PackageReference Include="SSH.NET" Version="*" />
<PackageReference Include="FluentFTP" Version="*" />
```

## Architecture Patterns

### Recommended Project Structure
```
DataWarehouse.SDK/
  Contracts/
    Transit/
      IDataTransitStrategy.cs          # Strategy interface + capabilities
      DataTransitTypes.cs              # Transfer config, result, progress types
      DataTransitStrategyBase.cs       # Abstract base with common logic
      ITransitOrchestrator.cs          # Orchestrator interface (discover/select/execute)
      TransitAuditTypes.cs             # Audit trail types
      TransitQoSTypes.cs               # QoS/throttling/cost types

Plugins/
  DataWarehouse.Plugins.UltimateDataTransit/
    DataWarehouse.Plugins.UltimateDataTransit.csproj
    UltimateDataTransitPlugin.cs       # Main orchestrator plugin (sealed)
    TransitStrategyRegistry.cs         # Strategy discovery/registration
    Strategies/
      Direct/
        Http2TransitStrategy.cs        # HTTP/2 direct transfer
        Http3TransitStrategy.cs        # HTTP/3 over QUIC
        GrpcStreamingTransitStrategy.cs # gRPC bidirectional streaming
        FtpTransitStrategy.cs          # FTP/FTPS transfer
        SftpTransitStrategy.cs         # SFTP transfer
        ScpRsyncTransitStrategy.cs     # SCP + rsync-like transfer
      Chunked/
        ChunkedResumableStrategy.cs    # Chunked upload/download with resume
        DeltaDifferentialStrategy.cs   # Binary delta/differential sync
      Distributed/
        P2PSwarmStrategy.cs            # BitTorrent-style peer swarm
        MultiPathParallelStrategy.cs   # Parallel multi-path transfer
      Offline/
        StoreAndForwardStrategy.cs     # Offline/sneakernet transfer
      Layers/
        CompressionInTransitLayer.cs   # Composable compression decorator
        EncryptionInTransitLayer.cs    # Composable encryption decorator
      QoS/
        QoSThrottlingManager.cs        # Bandwidth throttling + priority
        CostAwareRouter.cs             # Cost-based route selection
    Audit/
      TransitAuditService.cs           # Audit trail for all transfers
```

### Pattern 1: Strategy Pattern with Auto-Selection
**What:** Each transport mechanism is an `IDataTransitStrategy`. The orchestrator discovers all registered strategies and auto-selects based on endpoint capabilities and transfer context.
**When to use:** Always -- this is the core pattern for the plugin.
**Example:**
```csharp
// SDK contract
public interface IDataTransitStrategy
{
    string StrategyId { get; }
    string Name { get; }
    TransitCapabilities Capabilities { get; }

    Task<bool> IsAvailableAsync(TransitEndpoint endpoint, CancellationToken ct = default);
    Task<TransitResult> TransferAsync(TransitRequest request, IProgress<TransitProgress>? progress = null, CancellationToken ct = default);
    Task<TransitResult> ResumeTransferAsync(string transferId, IProgress<TransitProgress>? progress = null, CancellationToken ct = default);
    Task CancelTransferAsync(string transferId, CancellationToken ct = default);
    Task<TransitHealthStatus> GetHealthAsync(CancellationToken ct = default);
}
```

### Pattern 2: Composable Decorator Layers
**What:** Compression-in-transit and encryption-in-transit are implemented as decorators wrapping base strategies, not baked into each strategy.
**When to use:** For composable layers (compression, encryption) that can wrap any transport strategy.
**Example:**
```csharp
// Composable layer wrapping any strategy
internal sealed class EncryptionInTransitLayer : IDataTransitStrategy
{
    private readonly IDataTransitStrategy _inner;
    private readonly ITransitEncryption _encryption;

    public async Task<TransitResult> TransferAsync(TransitRequest request, ...)
    {
        var encryptedStream = await _encryption.EncryptStreamForTransitAsync(
            request.DataStream, outputStream, options, context, ct);
        return await _inner.TransferAsync(request.WithStream(outputStream), progress, ct);
    }
}
```

### Pattern 3: Message Bus Integration (Rule 4)
**What:** Cross-plugin delegation via message bus. UltimateDataTransit publishes transfer events and can request encryption/compression from other plugins.
**When to use:** All cross-plugin communication.
**Example:**
```csharp
// Publish transfer audit events
await MessageBus.PublishAsync("transit.transfer.started", new PluginMessage
{
    SourcePluginId = Id,
    Action = "transfer.started",
    Payload = new Dictionary<string, object>
    {
        ["transferId"] = transferId,
        ["strategyId"] = selectedStrategy.StrategyId,
        ["endpoint"] = endpoint.ToString(),
        ["sizeBytes"] = request.SizeBytes
    }
});
```

### Pattern 4: Cost-Aware Route Selection
**What:** When multiple strategies can reach an endpoint, select based on cost, performance, and policy constraints.
**When to use:** Multi-path parallel transfers, cost-constrained environments.
**Example:**
```csharp
public record TransitCostProfile
{
    public decimal CostPerGB { get; init; }
    public decimal FixedCostPerTransfer { get; init; }
    public TransitCostTier Tier { get; init; } // Free, Metered, Premium
    public bool IsMetered { get; init; }
}
```

### Anti-Patterns to Avoid
- **Direct plugin references:** NEVER reference UltimateEncryption or UltimateCompression directly. Use message bus or SDK interfaces.
- **Baking encryption into strategies:** Use composable layers instead. Each strategy should handle raw transport only.
- **Synchronous I/O:** All transfer operations must be fully async with CancellationToken support.
- **Mutable shared state:** Use ConcurrentDictionary, Interlocked, SemaphoreSlim for thread safety.
- **Simulated protocols:** Rule 13 -- real SSH.NET for SFTP, real System.Net.Quic for HTTP/3, real gRPC for streaming. No mocks.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| SFTP/SCP transfers | Custom SSH implementation | SSH.NET library | SSH protocol complexity, key exchange, channel management |
| FTP/FTPS | Custom FTP client | FluentFTP library | FTP protocol state machine, TLS integration, passive mode |
| HTTP/2 multiplexing | Custom HTTP/2 framing | System.Net.Http with HttpVersion.Version20 | HTTP/2 framing, HPACK, flow control |
| QUIC transport | Custom QUIC | System.Net.Quic | QUIC is extremely complex (connection migration, 0-RTT) |
| gRPC streaming | Custom protocol | Grpc.Net.Client | Protobuf serialization, flow control, metadata |
| Content hashing | Custom hash | System.IO.Hashing + System.Security.Cryptography | Performance, correctness, FIPS compliance |
| Transit encryption | Custom crypto | SDK ITransitEncryption | Cipher negotiation, key management, audit |
| Transit compression | Custom compression | SDK ITransitCompression | Algorithm negotiation, entropy detection |

**Key insight:** Transport protocols are notoriously complex at the edge cases (timeouts, retries, partial failures, connection migration). Use battle-tested libraries for the actual protocol handling and focus implementation effort on the orchestration, strategy selection, chunking, and composable layer design.

## Common Pitfalls

### Pitfall 1: Not Handling Partial Transfer Failures
**What goes wrong:** A 10 GB file transfer fails at 8 GB and must restart from zero.
**Why it happens:** Not implementing chunked/resumable transfer with server-side tracking.
**How to avoid:** Every strategy that supports large transfers MUST implement `ResumeTransferAsync`. Track chunk manifests with checksums (SHA-256 per chunk). Store transfer state to `IKernelStorageService`.
**Warning signs:** TransferAsync method has no checkpoint logic. No `transferId` tracking.

### Pitfall 2: Blocking on Synchronous Protocol Libraries
**What goes wrong:** Thread pool starvation under high concurrency.
**Why it happens:** Some protocol libraries (especially FTP) have synchronous APIs that get wrapped with Task.Run.
**How to avoid:** Use truly async APIs. FluentFTP has `ConnectAsync`, `UploadAsync`, etc. SSH.NET has `BeginUpload`/`EndUpload`. Prefer async variants.
**Warning signs:** `Task.Run(() => syncMethod())` in transfer code.

### Pitfall 3: No Backpressure in P2P Swarm
**What goes wrong:** Fast peers overwhelm slow peers, causing OOM or network congestion collapse.
**Why it happens:** Not implementing flow control between swarm participants.
**How to avoid:** Use System.Threading.Channels with bounded capacity for piece queues. Implement credit-based flow control between peers. Limit concurrent piece downloads.
**Warning signs:** Unbounded ConcurrentQueue for piece tracking.

### Pitfall 4: Encryption Layer Ordering Bugs
**What goes wrong:** Data gets compressed after encryption (no compression benefit) or encrypted twice.
**Why it happens:** Composable layers applied in wrong order or double-applied.
**How to avoid:** Enforce layer ordering: compress-then-encrypt for outbound, decrypt-then-decompress for inbound. Make layers idempotent-safe with metadata markers.
**Warning signs:** No ordering enforcement in the decorator chain.

### Pitfall 5: QoS Throttling Starvation
**What goes wrong:** Low-priority transfers never complete because high-priority transfers consume all bandwidth.
**Why it happens:** Simple priority queue without minimum bandwidth guarantees.
**How to avoid:** Use weighted fair queueing. Guarantee minimum bandwidth per priority tier. Use token bucket or leaky bucket for rate limiting.
**Warning signs:** Priority-only scheduling with no floor for lower priorities.

### Pitfall 6: Transfer ID Collisions
**What goes wrong:** Two transfers share the same ID, causing data corruption.
**Why it happens:** Using simple counters or non-unique identifiers.
**How to avoid:** Use `Guid.NewGuid()` for transfer IDs. Include strategy ID prefix for debuggability.
**Warning signs:** Incrementing integer transfer IDs.

## Code Examples

### SDK Strategy Interface Pattern
```csharp
// Source: Follows IStreamingStrategy, IReplicationStrategy pattern from existing SDK
namespace DataWarehouse.SDK.Contracts.Transit;

/// <summary>
/// Capabilities of a data transit strategy.
/// </summary>
public record TransitCapabilities
{
    public bool SupportsResumable { get; init; }
    public bool SupportsStreaming { get; init; }
    public bool SupportsDelta { get; init; }
    public bool SupportsMultiPath { get; init; }
    public bool SupportsP2P { get; init; }
    public bool SupportsOffline { get; init; }
    public bool SupportsCompression { get; init; }
    public bool SupportsEncryption { get; init; }
    public long MaxTransferSizeBytes { get; init; }
    public IReadOnlyList<string> SupportedProtocols { get; init; } = [];
}

/// <summary>
/// Transit endpoint descriptor.
/// </summary>
public record TransitEndpoint
{
    public required string Uri { get; init; }
    public string? Protocol { get; init; }
    public string? AuthToken { get; init; }
    public Dictionary<string, string>? Options { get; init; }
}

/// <summary>
/// Request to transfer data.
/// </summary>
public record TransitRequest
{
    public required string TransferId { get; init; }
    public required TransitEndpoint Source { get; init; }
    public required TransitEndpoint Destination { get; init; }
    public Stream? DataStream { get; init; }
    public long SizeBytes { get; init; }
    public string? ContentHash { get; init; }
    public TransitQoSPolicy? QoSPolicy { get; init; }
    public TransitLayerConfig? Layers { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Result of a transfer operation.
/// </summary>
public record TransitResult
{
    public required string TransferId { get; init; }
    public required bool Success { get; init; }
    public long BytesTransferred { get; init; }
    public TimeSpan Duration { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ContentHash { get; init; }
    public string StrategyUsed { get; init; } = "";
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Transfer progress reporting.
/// </summary>
public record TransitProgress
{
    public string TransferId { get; init; } = "";
    public long BytesTransferred { get; init; }
    public long TotalBytes { get; init; }
    public double PercentComplete { get; init; }
    public double BytesPerSecond { get; init; }
    public TimeSpan EstimatedRemaining { get; init; }
    public string CurrentPhase { get; init; } = "";
}
```

### Plugin Orchestrator Pattern
```csharp
// Source: Follows UltimateEncryptionPlugin pattern
namespace DataWarehouse.Plugins.UltimateDataTransit;

public sealed class UltimateDataTransitPlugin : FeaturePluginBase, IDisposable
{
    private readonly TransitStrategyRegistry _registry;
    private readonly ConcurrentDictionary<string, ActiveTransfer> _activeTransfers = new();

    public override string Id => "com.datawarehouse.transit.ultimate";
    public override string Name => "Ultimate Data Transit";
    public override string Version => "1.0.0";
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    public UltimateDataTransitPlugin()
    {
        _registry = new TransitStrategyRegistry();
        // Auto-discover strategies via reflection
        DiscoverStrategies();
    }

    private void DiscoverStrategies()
    {
        var strategyTypes = GetType().Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(IDataTransitStrategy).IsAssignableFrom(t));
        foreach (var type in strategyTypes)
        {
            var strategy = (IDataTransitStrategy)Activator.CreateInstance(type)!;
            _registry.Register(strategy);
        }
    }

    public async Task<IDataTransitStrategy> SelectStrategyAsync(
        TransitRequest request, CancellationToken ct = default)
    {
        var candidates = _registry.GetAll()
            .Where(s => s.Capabilities.SupportedProtocols.Contains(request.Destination.Protocol ?? ""));

        // Filter by availability
        var available = new List<(IDataTransitStrategy Strategy, int Score)>();
        foreach (var candidate in candidates)
        {
            if (await candidate.IsAvailableAsync(request.Destination, ct))
            {
                var score = ScoreStrategy(candidate, request);
                available.Add((candidate, score));
            }
        }

        return available.OrderByDescending(a => a.Score).First().Strategy;
    }
}
```

### Message Bus Topics
```csharp
// Transit-specific message topics
public static class TransitMessageTopics
{
    public const string TransferStarted = "transit.transfer.started";
    public const string TransferProgress = "transit.transfer.progress";
    public const string TransferCompleted = "transit.transfer.completed";
    public const string TransferFailed = "transit.transfer.failed";
    public const string TransferResumed = "transit.transfer.resumed";
    public const string TransferCancelled = "transit.transfer.cancelled";

    public const string StrategyRegistered = "transit.strategy.registered";
    public const string StrategyHealthChanged = "transit.strategy.health";

    public const string QoSPolicyChanged = "transit.qos.changed";
    public const string CostRouteChanged = "transit.cost.route.changed";

    public const string AuditEntry = "transit.audit.entry";
}
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| FTP for file transfer | SFTP/SCP over SSH, HTTP/2, gRPC | 2010s+ | Security, multiplexing, streaming |
| Single-path transfer | Multi-path parallel (MPTCP, application-level) | 2015+ | Better bandwidth utilization |
| Full file re-transfer | Delta/differential sync (rsync, bsdiff) | 1990s+ (rsync), ongoing refinement | Reduced bandwidth for updates |
| HTTP/1.1 | HTTP/2 (multiplexing) and HTTP/3 (QUIC) | 2015 (H2), 2022 (H3) | Lower latency, connection migration |
| Centralized download | P2P swarm (BitTorrent-style) | 2001+ | Scalable distribution, reduced server load |
| Manual offline transfer | Store-and-forward with manifest validation | Ongoing | Air-gap compliance, edge computing |

**Deprecated/outdated:**
- Plain FTP (unencrypted) -- use FTPS or SFTP instead
- HTTP/1.1 for bulk transfer -- use HTTP/2 minimum, prefer HTTP/3
- Custom TCP framing -- use established protocols (gRPC, HTTP/2)

## Relationship to Existing Components

### AEDS (T60) Data Plane
The AEDS system has its own `IDataPlaneTransport` and `DataPlaneTransportPluginBase` for distribution-specific transport (HTTP/3, QUIC, HTTP/2, WebTransport). UltimateDataTransit is a **separate, more general** system for user-selectable data movement strategies. Key differences:
- AEDS transport is distribution-centric (server pushes to clients)
- UltimateDataTransit is point-to-point, peer-to-peer, or multipath
- AEDS transport plugins are `DataPlaneTransportPluginBase` (AEDS-specific)
- UltimateDataTransit strategies are `IDataTransitStrategy` (general purpose)
- UltimateDataTransit can be used BY AEDS as a transport backend via message bus

### AdaptiveTransport (T78)
The AdaptiveTransport plugin provides protocol morphing -- dynamically switching between TCP, QUIC, reliable UDP, and store-and-forward based on network conditions. This is **complementary** to UltimateDataTransit:
- AdaptiveTransport handles the network-level protocol selection/switching
- UltimateDataTransit handles the application-level transfer strategy (chunked, delta, P2P, etc.)
- UltimateDataTransit could use AdaptiveTransport as an underlying transport mechanism via message bus

### Transit Encryption/Compression (SDK)
The SDK already has extensive transit security contracts (`ITransitEncryption`, `ITransitCompression`, `ITransitEncryptionStage`). UltimateDataTransit uses these as composable layers:
- Encryption-in-transit layer delegates to `ITransitEncryption` (from UltimateEncryption plugin)
- Compression-in-transit layer delegates to `ITransitCompression` (from UltimateCompression plugin)
- Communication via message bus, NOT direct reference

## Open Questions

1. **SDK Contract Location**
   - What we know: Strategy interfaces go in `DataWarehouse.SDK/Contracts/Transit/`
   - What's unclear: Whether to reuse existing `DataWarehouse.SDK.Security.Transit` namespace for non-security transit types
   - Recommendation: Use separate `DataWarehouse.SDK.Contracts.Transit` namespace for transport strategies, keep `Security.Transit` for encryption/compression. Clean separation of concerns.

2. **Strategy Base Class Hierarchy**
   - What we know: Strategies should extend a `DataTransitStrategyBase`
   - What's unclear: Whether the base should extend `FeaturePluginBase` (making each strategy a sub-plugin) or be a plain abstract class (strategy is just a component within the plugin)
   - Recommendation: Plain abstract class. Strategies are NOT plugins -- they are components registered within the single `UltimateDataTransitPlugin` orchestrator. This matches UltimateEncryption's pattern where strategies (AesGcmStrategy, etc.) are not plugins themselves.

3. **P2P Swarm Tracker**
   - What we know: P2P needs peer discovery and piece tracking
   - What's unclear: Whether to integrate with existing AEDS SwarmIntelligencePlugin (AEDS-X1) or build standalone
   - Recommendation: Build standalone P2P swarm strategy but design to interoperate with AEDS swarm via message bus.

## Sources

### Primary (HIGH confidence)
- `DataWarehouse.SDK/Security/Transit/ITransitEncryption.cs` -- Transit encryption interface, cipher negotiation
- `DataWarehouse.SDK/Security/Transit/ITransitCompression.cs` -- Transit compression interface, algorithm negotiation
- `DataWarehouse.SDK/Security/Transit/TransitEncryptionTypes.cs` -- Transit security levels, cipher presets, endpoint capabilities
- `DataWarehouse.SDK/Security/Transit/TransitCompressionTypes.cs` -- Compression modes, policies, capabilities
- `DataWarehouse.SDK/Security/Transit/ITransitEncryptionStage.cs` -- Pipeline stage for transit encryption
- `DataWarehouse.SDK/Security/Transit/ICommonCipherPresets.cs` -- Cipher preset provider interface
- `DataWarehouse.SDK/Security/Transit/ITranscryptionService.cs` -- Re-encryption service interface
- `DataWarehouse.SDK/Contracts/TransitEncryptionPluginBases.cs` -- Base classes for transit encryption plugins
- `DataWarehouse.SDK/Contracts/TransitCompressionPluginBases.cs` -- Base classes for transit compression plugins
- `DataWarehouse.SDK/Contracts/IPlugin.cs` -- Plugin interface, IKernelContext, IKernelStorageService
- `DataWarehouse.SDK/Contracts/IPluginCapability.cs` -- Capability interface
- `DataWarehouse.SDK/Contracts/IMessageBus.cs` -- Message bus, MessageTopics, MessageResponse
- `DataWarehouse.SDK/Contracts/PluginBase.cs` -- PluginBase, PipelinePluginBase hierarchy
- `DataWarehouse.SDK/Contracts/AedsPluginBases.cs` -- AEDS transport plugin base classes
- `DataWarehouse.SDK/Contracts/Distribution/AedsInterfaces.cs` -- AEDS distribution interfaces
- `DataWarehouse.SDK/Contracts/Encryption/EncryptionStrategy.cs` -- IEncryptionStrategy pattern reference
- `DataWarehouse.SDK/Contracts/Streaming/StreamingStrategy.cs` -- IStreamingStrategy pattern reference
- `DataWarehouse.SDK/Contracts/Replication/ReplicationStrategy.cs` -- IReplicationStrategy pattern reference
- `DataWarehouse.SDK/Primitives/Enums.cs` -- PluginCategory, CapabilityCategory enums
- `Plugins/DataWarehouse.Plugins.UltimateEncryption/UltimateEncryptionPlugin.cs` -- Reference Ultimate plugin structure
- `Plugins/DataWarehouse.Plugins.AdaptiveTransport/AdaptiveTransportPlugin.cs` -- Reference transport plugin
- `Metadata/CLAUDE.md` -- Project rules, architecture, Rule 13, plugin isolation

### Secondary (MEDIUM confidence)
- `Metadata/TODO.md` -- Task tracking, AEDS task IDs, transport plugin definitions

### Tertiary (LOW confidence)
- None -- all findings verified against primary SDK sources.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH -- All libraries are well-known .NET ecosystem libraries, verified available for .NET 10
- Architecture: HIGH -- Directly follows established patterns from 10+ existing Ultimate plugins in this codebase
- Pitfalls: HIGH -- Based on direct analysis of SDK contracts and well-known transport engineering challenges
- SDK Contracts: HIGH -- Multiple existing strategy interfaces (IEncryptionStrategy, IReplicationStrategy, IStreamingStrategy) provide clear pattern

**Research date:** 2026-02-11
**Valid until:** 2026-03-11 (stable domain, 30 days)

---

## Appendix: Full Strategy Inventory

### Plan 21-01: Plugin Orchestrator + Direct Transfer Strategies
| Strategy | Protocol | Use Case |
|----------|----------|----------|
| Http2TransitStrategy | HTTP/2 | Standard web-compatible transfer |
| Http3TransitStrategy | HTTP/3 (QUIC) | Low-latency, connection migration |
| GrpcStreamingTransitStrategy | gRPC | Bidirectional streaming, metadata-rich |
| FtpTransitStrategy | FTP/FTPS | Legacy system integration |
| SftpTransitStrategy | SFTP | Secure file transfer over SSH |
| ScpRsyncTransitStrategy | SCP/rsync | Unix-style secure copy, incremental |

**Also:** TransitStrategyRegistry, UltimateDataTransitPlugin orchestrator, auto-discovery, IDataTransitStrategy SDK contract.

### Plan 21-02: Chunked/Resumable + Delta/Differential
| Strategy | Mechanism | Use Case |
|----------|-----------|----------|
| ChunkedResumableStrategy | Chunked upload/download with manifests | Large files, unreliable connections |
| DeltaDifferentialStrategy | Rolling hash + binary diff | Updating large files with small changes |

**Also:** Chunk manifest persistence, integrity verification (SHA-256 per chunk), resume state management.

### Plan 21-03: P2P Swarm + Multi-Path Parallel
| Strategy | Mechanism | Use Case |
|----------|-----------|----------|
| P2PSwarmStrategy | BitTorrent-style piece distribution | Distributing large files to many recipients |
| MultiPathParallelStrategy | Parallel transfers over multiple routes | Maximum bandwidth utilization |

**Also:** Peer discovery, piece selection, credit-based flow control, path scoring.

### Plan 21-04: Store-and-Forward + QoS + Cost-Aware Routing
| Strategy | Mechanism | Use Case |
|----------|-----------|----------|
| StoreAndForwardStrategy | Offline transfer with manifest validation | Air-gap, edge, sneakernet |
| QoSThrottlingManager | Token bucket rate limiting, priority queues | Bandwidth management |
| CostAwareRouter | Cost-weighted route selection | Cloud egress optimization |

### Plan 21-05: Composable Layers + Audit Trail + Cross-Plugin Integration
| Component | Mechanism | Use Case |
|-----------|-----------|----------|
| CompressionInTransitLayer | Decorator wrapping ITransitCompression | Composable compression |
| EncryptionInTransitLayer | Decorator wrapping ITransitEncryption | Composable encryption |
| TransitAuditService | Structured audit log via message bus | Compliance, debugging |
| Cross-plugin delegation | Message bus topics for transit events | Integration with AEDS, replication |
