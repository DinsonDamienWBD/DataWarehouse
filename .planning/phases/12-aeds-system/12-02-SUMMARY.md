---
phase: 12-aeds-system
plan: 02
subsystem: aeds-control-plane
tags:
  - websocket
  - mqtt
  - grpc
  - streaming
  - bidirectional
  - reconnection
  - heartbeat
dependency-graph:
  requires:
    - DataWarehouse.SDK.Distribution.IAedsCore
    - DataWarehouse.SDK.Contracts.ControlPlaneTransportPluginBase
  provides:
    - WebSocketControlPlanePlugin (websocket transport)
    - MqttControlPlanePlugin (mqtt transport)
    - GrpcControlPlanePlugin (grpc transport)
  affects:
    - AEDS control plane transport layer
tech-stack:
  added:
    - MQTTnet 4.* (MQTT client library)
    - Grpc.Net.Client 2.* (gRPC streaming client)
    - Google.Protobuf 3.* (protobuf serialization for gRPC)
  patterns:
    - Persistent connections with heartbeat monitoring
    - Exponential backoff reconnection (1s, 2s, 4s, 8s, 16s, max 32s)
    - Channel<T> buffering for async enumerable manifest receiving
    - SemaphoreSlim for thread-safe send operations
    - Background tasks with CancellationToken propagation
key-files:
  created:
    - Plugins/DataWarehouse.Plugins.AedsCore/ControlPlane/WebSocketControlPlanePlugin.cs (508 lines)
    - Plugins/DataWarehouse.Plugins.AedsCore/ControlPlane/MqttControlPlanePlugin.cs (493 lines)
    - Plugins/DataWarehouse.Plugins.AedsCore/ControlPlane/GrpcControlPlanePlugin.cs (572 lines)
  modified:
    - Plugins/DataWarehouse.Plugins.AedsCore/DataWarehouse.Plugins.AedsCore.csproj (added 3 package references)
decisions:
  - name: JSON-over-gRPC encoding
    rationale: Simplified implementation for Phase 12 without requiring protobuf compilation; maintains type safety via SDK contracts
  - name: Manual reconnection for MQTT
    rationale: MQTTnet 4.x removed WithAutoReconnect() builder method; implemented manual reconnection with exponential backoff
  - name: WebSocket heartbeat expiry at 90 seconds
    rationale: Balances connection monitoring reliability with reasonable timeout for network interruptions
  - name: MQTT QoS levels
    rationale: QoS 1 (at least once) for manifests and channels ensures delivery; QoS 0 (at most once) for heartbeats reduces overhead
metrics:
  duration_minutes: 9
  completed_date: 2026-02-11
  tasks_completed: 2
  files_created: 3
  files_modified: 1
  lines_added: 1573
---

# Phase 12 Plan 02: Control Plane Transports Summary

**One-liner:** WebSocket, MQTT, and gRPC control plane transports with persistent connections, heartbeat monitoring, and exponential backoff reconnection for AEDS low-bandwidth signaling.

## Objective

Implemented 3 Control Plane transport plugins (WebSocket, MQTT, gRPC streaming) for low-bandwidth signaling in AEDS, enabling persistent connections for manifest distribution and heartbeat monitoring across multiple deployment scenarios.

## Implementation

### WebSocket Control Plane Plugin

**File:** `Plugins/DataWarehouse.Plugins.AedsCore/ControlPlane/WebSocketControlPlanePlugin.cs` (508 lines)

**Key Features:**
- Persistent WebSocket connection using `System.Net.WebSockets.ClientWebSocket` (native .NET)
- Heartbeat monitoring with 90-second timeout (triggers reconnection if no message received)
- Automatic reconnection with exponential backoff: 1s, 2s, 4s, 8s, 16s, max 32s
- Async enumerable manifest receiving via `Channel<IntentManifest>` buffering
- Channel subscription/unsubscription via control messages (`{"type": "subscribe", "channelId": "..."}`)
- Thread-safe send operations using `SemaphoreSlim` (WebSocket not thread-safe for concurrent sends)
- Clean shutdown with cancellation token propagation

**Protocol:**
- Message format: JSON text messages over WebSocket
- Server URL: `wss://server.example.com/aeds/control`
- Authorization: Bearer token in WebSocket request headers
- Message types: `manifest`, `heartbeat`, `ack`, `subscribe`, `unsubscribe`

**Use Cases:** Ideal for web-based clients, browser-based dashboards, and scenarios requiring WebSocket-compatible infrastructure.

---

### MQTT Control Plane Plugin

**File:** `Plugins/DataWarehouse.Plugins.AedsCore/ControlPlane/MqttControlPlanePlugin.cs` (493 lines)

**Key Features:**
- Persistent sessions with `CleanSession = false` for reliable delivery
- QoS 1 (at least once delivery) for manifests and channel subscriptions
- QoS 0 (at most once delivery) for heartbeats to reduce overhead
- Manual reconnection with exponential backoff (MQTTnet 4.x API compatibility)
- Topic-based routing for unicast, broadcast, and channel delivery modes
- Channel buffering for async enumerable manifest receiving

**Topic Structure:**
- Personal topic: `aeds/client/{clientId}/manifests` (QoS 1)
- Channel topic: `aeds/channel/{channelId}` (QoS 1)
- Heartbeat topic: `aeds/heartbeat/{clientId}` (QoS 0)

**Protocol:**
- Library: MQTTnet 4.*
- Server URL: `mqtt://localhost:1883` or `mqtts://broker.example.com:8883`
- Authentication: Username/password credentials from AuthToken (format: `username:password`)
- Keep-alive interval: Configured from `HeartbeatInterval`

**Use Cases:** Ideal for IoT/edge deployments, mobile clients, and scenarios requiring lightweight pub/sub messaging with broker-based routing.

---

### gRPC Control Plane Plugin

**File:** `Plugins/DataWarehouse.Plugins.AedsCore/ControlPlane/GrpcControlPlanePlugin.cs` (572 lines)

**Key Features:**
- Bidirectional streaming using `AsyncDuplexStreamingCall<ControlMessage, ControlMessage>`
- JSON-over-gRPC encoding (no protobuf compilation required for Phase 12)
- Manual reconnection with exponential backoff (1s, 2s, 4s, 8s, 16s, max 32s)
- Deadline propagation for timeout handling
- Channel management with proper lifecycle and cleanup
- Thread-safe send operations using `SemaphoreSlim`

**Protocol:**
- Service: `AedsControlPlane.StreamManifests` (DuplexStreaming RPC)
- Message wrapper: Generic `ControlMessage` with `Type` (string) and `Payload` (ByteString)
- Serialization: JSON payloads encoded as Base64 in protobuf ByteString
- Message types: `manifest`, `heartbeat`, `control`, `ack`

**Server Definition:**
```csharp
public static class AedsControlPlane
{
    public class AedsControlPlaneClient
    {
        public AsyncDuplexStreamingCall<ControlMessage, ControlMessage> StreamManifests(...)
    }
}
```

**Use Cases:** Ideal for high-performance RPC-heavy workloads, microservice architectures, and scenarios requiring low latency with HTTP/2 multiplexing.

---

## Transport Selection Guidance

| Transport | Best For | Connection Model | Reconnection | Deployment |
|-----------|----------|------------------|--------------|------------|
| **WebSocket** | Web clients, browsers, dashboards | Persistent bidirectional | Exponential backoff (manual) | WSS-compatible infrastructure |
| **MQTT** | IoT/edge devices, mobile clients | Persistent pub/sub | Exponential backoff (manual) | MQTT broker (Mosquitto, EMQX, AWS IoT Core) |
| **gRPC** | Microservices, high-performance RPC | Bidirectional streaming | Exponential backoff (manual) | gRPC-compatible HTTP/2 server |

---

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking Issue] Added Google.Protobuf package dependency**
- **Found during:** Task 2 (gRPC plugin implementation)
- **Issue:** gRPC plugin compilation failed with "The type or namespace name 'Google' could not be found" error; Grpc.Net.Client 2.* requires Google.Protobuf for ByteString usage
- **Fix:** Added `<PackageReference Include="Google.Protobuf" Version="3.*" />` to DataWarehouse.Plugins.AedsCore.csproj
- **Files modified:** Plugins/DataWarehouse.Plugins.AedsCore/DataWarehouse.Plugins.AedsCore.csproj
- **Commit:** 069879e

**2. [Rule 1 - Bug] Fixed MQTTnet 4.x API compatibility**
- **Found during:** Task 2 (MQTT plugin build)
- **Issue:** MQTTnet 4.x removed `WithAutoReconnect()` builder method and `AutoReconnect` property on `MqttClientOptions` (breaking API change from 3.x)
- **Fix:** Implemented manual reconnection logic in `OnDisconnectedAsync` event handler with exponential backoff; added `_mqttOptions`, `_connectionCts`, `_reconnectAttempt` fields; created `ReconnectAsync()` method that resubscribes to all topics after reconnection
- **Files modified:** Plugins/DataWarehouse.Plugins.AedsCore/ControlPlane/MqttControlPlanePlugin.cs
- **Commit:** 069879e

**3. [Rule 1 - Bug] Fixed MQTTnet 4.x deprecated TLS API**
- **Found during:** Task 2 (build warning CS0618)
- **Issue:** `WithTls()` method marked obsolete in MQTTnet 4.x
- **Fix:** Changed `optionsBuilder.WithTls()` to `optionsBuilder.WithTlsOptions(o => o.UseTls())`
- **Files modified:** Plugins/DataWarehouse.Plugins.AedsCore/ControlPlane/MqttControlPlanePlugin.cs
- **Commit:** 069879e

---

## Verification Results

**Build Status:** ✅ PASSED (0 errors, 45 warnings - all pre-existing SDK warnings)

**Verification Checklist:**
- ✅ Build passes for AedsCore plugin project with 0 errors
- ✅ 3 Control Plane transport files exist in ControlPlane/ subfolder
- ✅ All 3 plugins inherit from ControlPlaneTransportPluginBase
- ✅ MQTTnet 4.*, Grpc.Net.Client 2.*, and Google.Protobuf 3.* packages added to .csproj
- ✅ Zero TODOs, NotImplementedException, or placeholders in any Control Plane plugin
- ✅ Reconnection logic implemented (WebSocket/gRPC manual, MQTT manual with event handler)
- ✅ Heartbeat monitoring with 90-second expiry detection (WebSocket)
- ✅ Line count requirements met: WebSocket 508 > 200, MQTT 493 > 200, gRPC 572 > 200

---

## Success Criteria

All success criteria met:

1. ✅ 3 Control Plane transport plugins implemented (WebSocket, MQTT, gRPC)
2. ✅ All inherit from ControlPlaneTransportPluginBase and override abstract methods
3. ✅ Each transport uses recommended library (System.Net.WebSockets, MQTTnet 4.x, Grpc.Net.Client 2.x)
4. ✅ Persistent connections with reconnection logic (exponential backoff)
5. ✅ Heartbeat monitoring with 90-second timeout (WebSocket explicit, MQTT/gRPC via keep-alive/ping)
6. ✅ Async enumerable manifest receiving via Channel<IntentManifest> buffering
7. ✅ Channel subscription/unsubscription implemented
8. ✅ Zero forbidden patterns (Rule 13 compliant)
9. ✅ Build passes with 0 errors

---

## Reconnection Strategies

All three transports implement exponential backoff reconnection:

**Backoff Sequence:** 1s, 2s, 4s, 8s, 16s, max 32s

**WebSocket:**
- Triggered by: WebSocketException on send/receive, close frame received, heartbeat expiry (90 seconds)
- Implementation: `ConnectWithRetryAsync()` called from `ReconnectAsync()`
- State restoration: Manifest channel preserved across reconnections

**MQTT:**
- Triggered by: Disconnect event (connection lost, broker unavailable)
- Implementation: `OnDisconnectedAsync()` event handler calls `ReconnectAsync()`
- State restoration: Resubscribes to all topics in `_subscribedTopics` after reconnection

**gRPC:**
- Triggered by: RpcException with StatusCode.Unavailable, stream ended, receive loop error
- Implementation: `ReconnectAsync()` called from `RunReceiveLoopAsync()`
- State restoration: Manifest channel preserved across reconnections

---

## Package Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| MQTTnet | 4.* | MQTT client library for pub/sub messaging |
| Grpc.Net.Client | 2.* | gRPC client for bidirectional streaming |
| Google.Protobuf | 3.* | Protobuf serialization for gRPC ByteString wrapper |

---

## Testing Recommendations

**WebSocket:**
```csharp
var config = new ControlPlaneConfig(
    ServerUrl: "wss://localhost:5001/aeds/control",
    ClientId: "test-client-1",
    AuthToken: "bearer-token-here",
    HeartbeatInterval: TimeSpan.FromSeconds(30),
    ReconnectDelay: TimeSpan.FromSeconds(1)
);

var plugin = new WebSocketControlPlanePlugin(logger);
await plugin.ConnectAsync(config, cts.Token);

await foreach (var manifest in plugin.ReceiveManifestsAsync(cts.Token))
{
    Console.WriteLine($"Received manifest: {manifest.ManifestId}");
}
```

**MQTT:**
```bash
# Install MQTT broker
docker run -d -p 1883:1883 -p 9001:9001 eclipse-mosquitto

# Set environment variable
export AEDS_MQTT_BROKER_URL=mqtt://localhost:1883
```

**gRPC:**
- Requires gRPC server implementing `AedsControlPlane.StreamManifests` RPC
- Server should accept `ControlMessage` objects with JSON payloads

---

## Self-Check: PASSED

**Created files verification:**
```
FOUND: Plugins/DataWarehouse.Plugins.AedsCore/ControlPlane/WebSocketControlPlanePlugin.cs
FOUND: Plugins/DataWarehouse.Plugins.AedsCore/ControlPlane/MqttControlPlanePlugin.cs
FOUND: Plugins/DataWarehouse.Plugins.AedsCore/ControlPlane/GrpcControlPlanePlugin.cs
```

**Commits verification:**
```
FOUND: 3a2ab4e (Task 1: WebSocketControlPlanePlugin)
FOUND: 069879e (Task 2: MqttControlPlanePlugin + GrpcControlPlanePlugin)
```

**Build verification:**
```
dotnet build Plugins/DataWarehouse.Plugins.AedsCore/DataWarehouse.Plugins.AedsCore.csproj
✅ Build succeeded: 0 errors, 45 warnings (pre-existing SDK warnings)
```

---

## Next Steps

**Phase 12 Plan 03:** Implement Server Dispatcher and Client Sentinel plugins for AEDS orchestration layer.

---

**Completed:** 2026-02-11
**Duration:** 9 minutes
**Commits:** 2 (3a2ab4e, 069879e)
**Lines Added:** 1573
**Files Created:** 3
