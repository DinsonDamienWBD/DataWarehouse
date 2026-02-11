# Phase 12: AEDS System - Research

**Researched:** 2026-02-11
**Domain:** Autonomous Edge Distribution System (AEDS)
**Confidence:** HIGH

## Summary

Phase 12 implements the **Active Enterprise Distribution System (AEDS)**, a comprehensive push-based content distribution architecture for autonomous edge deployment. AEDS separates concerns into Control Plane (signaling) and Data Plane (bulk transfers), enabling efficient server-to-client and client-to-client distribution of intent manifests with cryptographic signatures and zero-trust pairing.

The existing codebase has substantial AEDS foundation already implemented:
- **AEDS Core** plugin with 4 plugins (AedsCorePlugin, ClientCourierPlugin, ServerDispatcherPlugin, Http2DataPlanePlugin)
- Complete SDK contracts in `AedsInterfaces.cs` (980 lines) and `AedsPluginBases.cs` (702 lines)
- Intent Manifest signing, validation, and execution framework
- Zero-trust client registration with trust levels and verification PINs

**Primary recommendation:** Focus on implementing missing transport layers (WebSocket/MQTT/gRPC control plane, QUIC/HTTP3/WebTransport data plane) and extension features (swarm intelligence, delta sync, PreCog, notifications, policy engine) to complete the AEDS ecosystem.

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| System.Net.WebSockets | .NET 9 | WebSocket control plane transport | Native .NET async duplex messaging for low-latency signaling |
| MQTTnet | 4.x | MQTT control plane transport | Industry-standard pub/sub for IoT/edge, lightweight persistent connections |
| Grpc.Net.Client | 2.x | gRPC control plane transport | Efficient streaming RPC for server-to-client push with protobuf serialization |
| System.Net.Http | .NET 9 | HTTP/2 data plane (existing) | Already implemented in Http2DataPlanePlugin |
| System.Net.Quic | .NET 9 | QUIC data plane (future) | Native HTTP/3 and raw QUIC for multiplexed bulk transfers |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| Microsoft.Extensions.Logging | .NET 9 | Diagnostic logging | All AEDS plugins (already used consistently) |
| System.Security.Cryptography | .NET 9 | Manifest signing/verification | Ed25519, RSA-PSS, ECDSA signatures for code signing |
| System.IO.Pipelines | .NET 9 | High-performance streaming | Data plane chunked transfers with backpressure |
| System.Threading.Channels | .NET 9 | Async producer/consumer queues | Job queue management, manifest processing pipelines |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| MQTTnet | Mosquitto (C wrapper) | MQTTnet is pure .NET, cross-platform, and well-maintained |
| Grpc.Net.Client | grpc-dotnet | Grpc.Net.Client is the official replacement for older Grpc.Core |
| System.Net.Quic | QuicNet (3rd party) | System.Net.Quic is native .NET 9, no external dependencies |

**Installation:**
```bash
# Control Plane Transports
dotnet add package MQTTnet --version 4.x
dotnet add package Grpc.Net.Client --version 2.x

# Data Plane Transports (QUIC/HTTP3)
# System.Net.Quic is built-in to .NET 9, no package needed
```

## Architecture Patterns

### Recommended Project Structure
```
Plugins/DataWarehouse.Plugins.AedsCore/
├── AedsCorePlugin.cs              # Core orchestration (existing)
├── ClientCourierPlugin.cs         # Client-side agent (existing)
├── ServerDispatcherPlugin.cs      # Server-side dispatcher (existing)
├── Http2DataPlanePlugin.cs        # HTTP/2 data plane (existing)
├── ControlPlane/
│   ├── WebSocketControlPlanePlugin.cs    # WebSocket transport
│   ├── MqttControlPlanePlugin.cs         # MQTT transport
│   └── GrpcControlPlanePlugin.cs         # gRPC streaming transport
├── DataPlane/
│   ├── QuicDataPlanePlugin.cs            # Raw QUIC transport
│   ├── Http3DataPlanePlugin.cs           # HTTP/3 transport
│   └── WebTransportDataPlanePlugin.cs    # WebTransport (future)
├── Extensions/
│   ├── SwarmIntelligencePlugin.cs        # P2P mesh distribution
│   ├── DeltaSyncPlugin.cs                # Delta/diff sync
│   ├── PreCogPlugin.cs                   # Predictive prefetch
│   ├── MulePlugin.cs                     # Air-gap USB transport
│   ├── GlobalDeduplicationPlugin.cs      # Cross-client dedup
│   ├── NotificationPlugin.cs             # Toast/modal notifications
│   ├── CodeSigningPlugin.cs              # Release key verification
│   ├── PolicyEnginePlugin.cs             # Client-side policy evaluation
│   └── ZeroTrustPairingPlugin.cs         # Zero-trust pairing flow
```

### Pattern 1: Intent Manifest Distribution
**What:** Server creates signed manifest, dispatches to targets via control plane, clients download payload via data plane
**When to use:** All AEDS distribution scenarios
**Example:**
```csharp
// Server: Create and queue manifest
var manifest = new IntentManifest
{
    ManifestId = Guid.NewGuid().ToString(),
    CreatedAt = DateTimeOffset.UtcNow,
    Action = ActionPrimitive.Notify,
    DeliveryMode = DeliveryMode.Broadcast,
    Targets = new[] { "channel-updates" },
    Priority = 80,
    Payload = new PayloadDescriptor
    {
        PayloadId = contentHash,
        Name = "SoftwareUpdate-v2.1.0.exe",
        ContentType = "application/octet-stream",
        SizeBytes = 15_000_000,
        ContentHash = "sha256:abc123...",
        DeltaAvailable = false
    },
    Signature = signer.Sign(manifest)
};

var jobId = await dispatcher.QueueJobAsync(manifest);

// Client: Sentinel receives manifest, executor downloads and acts
sentinel.ManifestReceived += async (sender, e) =>
{
    var result = await executor.ExecuteAsync(e.Manifest, config);
};
```

### Pattern 2: Control Plane + Data Plane Separation
**What:** Control plane sends small signaling messages (manifests, heartbeats), data plane transfers bulk payloads
**When to use:** All AEDS implementations to optimize bandwidth and connection management
**Example:**
```csharp
// Control Plane: Persistent WebSocket/MQTT connection
await controlPlane.ConnectAsync(new ControlPlaneConfig(
    ServerUrl: "wss://server.example.com/control",
    ClientId: clientId,
    HeartbeatInterval: TimeSpan.FromSeconds(30)
));

await foreach (var manifest in controlPlane.ReceiveManifestsAsync(ct))
{
    // Data Plane: HTTP/2 download only when manifest received
    using var stream = await dataPlane.DownloadAsync(
        manifest.Payload.PayloadId,
        new DataPlaneConfig(
            ServerUrl: "https://server.example.com/data",
            MaxConcurrentChunks: 4,
            ChunkSizeBytes: 1_048_576
        )
    );
    // Save to disk
}
```

### Pattern 3: Zero-Trust Client Registration
**What:** New clients register with public key, receive verification PIN, admin approves to elevate trust level
**When to use:** Initial client onboarding to AEDS network
**Example:**
```csharp
// Client: Register with server
var registration = new ClientRegistration(
    ClientName: Environment.MachineName,
    PublicKey: Convert.ToBase64String(myPublicKey),
    VerificationPin: GeneratePin(), // Show to user for admin verification
    Capabilities: ClientCapabilities.ReceivePassive | ClientCapabilities.Interactive
);

var client = await dispatcher.RegisterClientAsync(registration);
// client.TrustLevel == ClientTrustLevel.PendingVerification

// Admin: Verify PIN and elevate trust
await dispatcher.UpdateClientTrustAsync(
    client.ClientId,
    ClientTrustLevel.Trusted,
    adminId: "admin-001"
);
```

### Pattern 4: Channel Subscription
**What:** Clients subscribe to broadcast channels, manifests sent to channels reach all subscribers
**When to use:** Software updates, policy distribution, broadcast notifications
**Example:**
```csharp
// Server: Create channel
var channel = await dispatcher.CreateChannelAsync(new ChannelCreation(
    Name: "critical-updates",
    Description: "Critical security updates",
    SubscriptionType: SubscriptionType.Mandatory, // Cannot unsubscribe
    MinTrustLevel: ClientTrustLevel.Trusted
));

// Client: Subscribe to channel
await controlPlane.SubscribeChannelAsync(channel.ChannelId);

// Server: Broadcast manifest to all channel subscribers
var manifest = new IntentManifest
{
    DeliveryMode = DeliveryMode.Broadcast,
    Targets = new[] { channel.ChannelId },
    // ...
};
await dispatcher.QueueJobAsync(manifest);
```

### Anti-Patterns to Avoid
- **Mixing Control and Data on Same Connection:** Don't send large payloads over control plane - use data plane for bulk transfers
- **Unsigned Manifests in Production:** Always require signatures except for dev/test with explicit AllowUnsigned configuration
- **Blocking Execute Actions Without Verification:** Execute action REQUIRES Signature.IsReleaseKey = true, reject otherwise
- **No Heartbeat Timeout:** Implement heartbeat expiry to detect dead connections (90 seconds recommended)
- **Synchronous Manifest Processing:** Process manifests asynchronously in background tasks to avoid blocking control plane

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| WebSocket reconnection | Custom retry logic | MQTTnet or SignalR | MQTTnet handles reconnection with exponential backoff, connection state management |
| MQTT protocol | Byte-level MQTT parsing | MQTTnet library | MQTT protocol is complex (QoS levels, session persistence, will messages) |
| gRPC streaming | HTTP/2 manual framing | Grpc.Net.Client | gRPC handles protobuf serialization, deadlines, cancellation, backpressure |
| QUIC multiplexing | Stream management | System.Net.Quic | QUIC stream lifecycle (bidirectional, unidirectional, flow control) is complex |
| Chunk integrity verification | Custom checksums | Built-in SHA-256 + chunk hashes | Manifest already includes ChunkHashes array for per-chunk verification |
| File watching debounce | Manual timers | FileSystemWatcher with SemaphoreSlim | Debounce pattern with cancellation tokens prevents duplicate sync on rapid changes |
| Progress reporting | Polling file size | IProgress&lt;TransferProgress&gt; | Existing ProgressReportingStream pattern reports during stream reads |
| Job queue priority | Manual sorting | PriorityQueue&lt;T&gt; (.NET 6+) | Built-in priority queue with O(log n) enqueue/dequeue |

**Key insight:** AEDS separates concerns (control vs. data plane, server vs. client) to leverage specialized protocols and libraries for each layer. Don't rebuild protocol implementations that exist as mature, tested libraries.

## Common Pitfalls

### Pitfall 1: Control Plane Connection Leaks
**What goes wrong:** Control plane connections remain open after client stops, consuming server resources
**Why it happens:** No heartbeat monitoring or connection timeout enforcement
**How to avoid:** Implement heartbeat expiry checks on server side - mark clients offline after 90 seconds without heartbeat
**Warning signs:** Server memory growth, open connection count increases over time, clients marked online when actually offline

### Pitfall 2: Data Plane Download Without Integrity Verification
**What goes wrong:** Downloaded payload corrupted or tampered, no verification before execution
**Why it happens:** Skipping ContentHash or ChunkHashes verification to "save time"
**How to avoid:** Always compute SHA-256 of downloaded stream and compare to Payload.ContentHash before saving
**Warning signs:** Intermittent execution failures, "file corrupted" errors, security incidents

### Pitfall 3: Execute Action Without Release Key Verification
**What goes wrong:** Arbitrary code execution from unsigned or dev-signed manifests
**Why it happens:** Not checking Signature.IsReleaseKey before executing
**How to avoid:** In ClientCourierPlugin.ProcessManifestAsync, reject Execute action unless manifest.Signature.IsReleaseKey == true
**Warning signs:** Security audit failures, unauthorized code execution, malware distribution

### Pitfall 4: Blocking Control Plane Thread During Data Plane Download
**What goes wrong:** Control plane cannot receive new manifests while downloading large payload
**Why it happens:** Synchronous download in manifest processing loop
**How to avoid:** Process manifests in background tasks (`Task.Run(() => ProcessManifestAsync(...))`), keep control plane responsive
**Warning signs:** Manifests queued but not received, heartbeat failures during large downloads, control plane timeouts

### Pitfall 5: No Delta Sync for Large Files
**What goes wrong:** Re-downloading 500 MB when only 5 MB changed
**Why it happens:** Not implementing delta sync or not checking Payload.DeltaAvailable
**How to avoid:** Check DeltaAvailable flag, use DownloadDeltaAsync with baseVersion, apply diff locally
**Warning signs:** Excessive bandwidth usage, slow update distribution, metered connection complaints

### Pitfall 6: Forgot to Dispose FileSystemWatcher
**What goes wrong:** Memory leak from watchers that never dispose
**Why it happens:** ClientCourierPlugin creates watchers but doesn't track them for disposal
**How to avoid:** Store watchers in `_watchers` dictionary, dispose all in StopCourierAsync
**Warning signs:** Memory growth proportional to interactive file count, GC pressure, handle leaks

## Code Examples

Verified patterns from official sources:

### Control Plane Plugin Base Implementation
```csharp
// Source: DataWarehouse.SDK/Contracts/AedsPluginBases.cs
public abstract class ControlPlaneTransportPluginBase : FeaturePluginBase, IControlPlaneTransport
{
    public abstract string TransportId { get; }
    public bool IsConnected { get; protected set; }
    protected ControlPlaneConfig? Config { get; private set; }

    // Implement these in derived classes:
    protected abstract Task EstablishConnectionAsync(ControlPlaneConfig config, CancellationToken ct);
    protected abstract Task CloseConnectionAsync();
    protected abstract Task TransmitManifestAsync(IntentManifest manifest, CancellationToken ct);
    protected abstract IAsyncEnumerable<IntentManifest> ListenForManifestsAsync(CancellationToken ct);
    protected abstract Task TransmitHeartbeatAsync(HeartbeatMessage heartbeat, CancellationToken ct);
    protected abstract Task JoinChannelAsync(string channelId, CancellationToken ct);
    protected abstract Task LeaveChannelAsync(string channelId, CancellationToken ct);
}
```

### Data Plane Plugin Base Implementation
```csharp
// Source: DataWarehouse.SDK/Contracts/AedsPluginBases.cs
public abstract class DataPlaneTransportPluginBase : FeaturePluginBase, IDataPlaneTransport
{
    public abstract string TransportId { get; }

    // Implement these in derived classes:
    protected abstract Task<Stream> FetchPayloadAsync(
        string payloadId,
        DataPlaneConfig config,
        IProgress<TransferProgress>? progress,
        CancellationToken ct);

    protected abstract Task<Stream> FetchDeltaAsync(
        string payloadId,
        string baseVersion,
        DataPlaneConfig config,
        IProgress<TransferProgress>? progress,
        CancellationToken ct);

    protected abstract Task<string> PushPayloadAsync(
        Stream data,
        PayloadMetadata metadata,
        DataPlaneConfig config,
        IProgress<TransferProgress>? progress,
        CancellationToken ct);
}
```

### Manifest Validation with Priority Scoring
```csharp
// Source: Plugins/DataWarehouse.Plugins.AedsCore/AedsCorePlugin.cs
public int ComputePriorityScore(IntentManifest manifest)
{
    int score = manifest.Priority;

    // Boost for expiring manifests
    if (manifest.ExpiresAt.HasValue)
    {
        var timeToExpiry = manifest.ExpiresAt.Value - DateTimeOffset.UtcNow;
        if (timeToExpiry < TimeSpan.FromHours(1))
            score += 20;
        else if (timeToExpiry < TimeSpan.FromHours(24))
            score += 10;
    }

    // Boost based on action urgency
    score += manifest.Action switch
    {
        ActionPrimitive.Execute => 15,
        ActionPrimitive.Interactive => 10,
        ActionPrimitive.Notify => 5,
        ActionPrimitive.Custom => 5,
        ActionPrimitive.Passive => 0,
        _ => 0
    };

    // Slight penalty for broadcast (affects many clients)
    if (manifest.DeliveryMode == DeliveryMode.Broadcast && manifest.Targets.Length > 100)
        score -= 5;

    return Math.Max(0, Math.Min(150, score)); // Clamp to [0, 150]
}
```

### Progress Reporting Stream Wrapper
```csharp
// Source: Plugins/DataWarehouse.Plugins.AedsCore/Http2DataPlanePlugin.cs
internal class ProgressReportingStream : Stream
{
    private readonly Stream _innerStream;
    private readonly long _totalBytes;
    private readonly IProgress<TransferProgress> _progress;
    private long _bytesTransferred;
    private readonly DateTime _startTime;

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var bytesRead = await _innerStream.ReadAsync(buffer, offset, count, ct);
        if (bytesRead > 0)
        {
            _bytesTransferred += bytesRead;
            var elapsed = DateTime.UtcNow - _startTime;
            var bytesPerSecond = elapsed.TotalSeconds > 0
                ? _bytesTransferred / elapsed.TotalSeconds
                : 0;

            _progress.Report(new TransferProgress(
                BytesTransferred: _bytesTransferred,
                TotalBytes: _totalBytes,
                PercentComplete: (_bytesTransferred * 100.0) / _totalBytes,
                BytesPerSecond: bytesPerSecond,
                EstimatedRemaining: TimeSpan.FromSeconds((_totalBytes - _bytesTransferred) / bytesPerSecond)
            ));
        }
        return bytesRead;
    }
}
```

### Client-Server Job Processing
```csharp
// Source: Plugins/DataWarehouse.Plugins.AedsCore/ServerDispatcherPlugin.cs
protected override async Task ProcessJobAsync(string jobId, CancellationToken ct)
{
    // Update status to InProgress
    _jobs[jobId] = job with { Status = JobStatus.InProgress };

    var targetClients = await ResolveTargetsAsync(manifest, ct);
    int delivered = 0, failed = 0;

    foreach (var client in targetClients)
    {
        try
        {
            // Send manifest via Control Plane
            await _controlPlane.SendManifestAsync(manifest, ct);
            delivered++;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deliver to client {ClientId}", client.ClientId);
            failed++;
        }
    }

    // Update final status
    var finalStatus = failed == 0 ? JobStatus.Completed :
                     delivered == 0 ? JobStatus.Failed :
                     JobStatus.PartiallyCompleted;

    _jobs[jobId] = job with
    {
        Status = finalStatus,
        DeliveredCount = delivered,
        FailedCount = failed,
        CompletedAt = DateTimeOffset.UtcNow
    };
}
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Polling for updates | Push-based intent manifests | AEDS design (2024) | Real-time distribution, no polling overhead |
| Single HTTP connection | Control + Data plane separation | AEDS design (2024) | Optimized bandwidth, persistent signaling |
| Trust on first use | Zero-trust with PIN verification | AEDS design (2024) | Secure onboarding, admin approval required |
| Full file transfers | Delta sync support | AEDS design (2024) | Bandwidth savings for large file updates |
| Unsigned packages | Cryptographic manifest signing | AEDS design (2024) | Code signing, release key verification |

**Deprecated/outdated:**
- **Polling-based update checks**: AEDS uses push-based manifests via control plane
- **Unauthenticated client connections**: All clients require registration with public key and trust level verification

## Open Questions

1. **WebTransport support**
   - What we know: WebTransport (HTTP/3 over QUIC with bidirectional streams) is proposed for data plane
   - What's unclear: .NET 9 System.Net.Quic may not support WebTransport API yet (only raw QUIC and HTTP/3)
   - Recommendation: Implement QUIC and HTTP/3 first, defer WebTransport to future .NET version with API support

2. **Swarm Intelligence P2P coordination**
   - What we know: ClientCapabilities.P2PMesh flag exists, intent is peer-to-peer distribution
   - What's unclear: P2P discovery mechanism (mDNS? Central coordinator? BitTorrent DHT?)
   - Recommendation: Start with server-coordinated P2P (server provides peer list), defer DHT to future enhancement

3. **PreCog predictive prefetch**
   - What we know: PreCog should prefetch content before explicit manifest (AI/ML-driven)
   - What's unclear: Integration with UniversalIntelligence (T90) for prediction models
   - Recommendation: Implement basic heuristics first (time-of-day patterns, historical usage), then delegate to Intelligence plugin

4. **Manifest signature verification implementation**
   - What we know: AedsCorePlugin.VerifySignatureAsync has TODO for actual cryptographic verification
   - What's unclear: Integration with UltimateKeyManagement (T94) for key retrieval
   - Recommendation: Use UltimateKeyManagement via message bus to resolve KeyId, verify with System.Security.Cryptography

5. **Air-gap mule transport**
   - What we know: ClientCapabilities.AirGapMule flag exists, tri-mode USB plugin exists (T79)
   - What's unclear: How AEDS integrates with tri-mode USB for offline manifest transport
   - Recommendation: AEDS writes manifests + payloads to USB in sentinel mode, tri-mode USB ingests on target network

## Sources

### Primary (HIGH confidence)
- DataWarehouse.SDK/Contracts/AedsPluginBases.cs - Base classes for Control Plane, Data Plane, Server Dispatcher, Client Sentinel, Client Executor
- DataWarehouse.SDK/Distribution/IAedsCore.cs - Complete AEDS interface definitions (enums, records, interfaces)
- Plugins/DataWarehouse.Plugins.AedsCore/ - Existing implementations (AedsCore, ClientCourier, ServerDispatcher, Http2DataPlane plugins)
- DataWarehouse.SDK/Contracts/Distribution/AedsInterfaces.cs - SDK contracts for AEDS (duplicate of IAedsCore.cs)

### Secondary (MEDIUM confidence)
- .NET 9 System.Net.Quic documentation - QUIC and HTTP/3 support
- MQTTnet GitHub repository - MQTT library for .NET
- Grpc.Net.Client documentation - gRPC streaming for .NET

### Tertiary (LOW confidence)
- WebTransport W3C draft - Still evolving specification, .NET support unclear

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH - Existing plugins use System.Net.Http, MQTTnet and Grpc.Net.Client are standard .NET libraries
- Architecture: HIGH - Base classes and interfaces fully defined in SDK, existing plugins demonstrate patterns
- Pitfalls: HIGH - Code review of existing implementations reveals common errors and TODOs

**Research date:** 2026-02-11
**Valid until:** 2026-03-31 (60 days - relatively stable AEDS design, but .NET 9+ features evolving)

## Next Steps for Planning

Phase 12 implementation should be divided into 4 plans aligned with roadmap:

1. **12-01: AEDS Core (AEDS-C)** - Verify existing AedsCore, ClientCourier, ServerDispatcher plugins, complete TODOs (signature verification, multicast targeting)
2. **12-02: AEDS Control Plane (AEDS-CP)** - Implement WebSocket, MQTT, gRPC streaming control plane transports
3. **12-03: AEDS Data Plane (AEDS-DP)** - Implement QUIC, HTTP/3, WebTransport (future) data plane transports
4. **12-04: AEDS Extensions (AEDS-X)** - Implement swarm intelligence, delta sync, PreCog, mule, global dedup, notification, code signing, policy engine, zero-trust pairing plugins

**Dependencies:** Phase 4 (replication T98), Phase 5 (TamperProof pipeline), Phase 6 (UltimateInterface T109 for protocol strategies)

**Integration points:** UltimateKeyManagement (T94) for signing keys, UniversalIntelligence (T90) for PreCog predictions, tri-mode USB (T79) for air-gap mule
