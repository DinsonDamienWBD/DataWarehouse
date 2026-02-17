---
phase: 46
plan: 46-04
title: "Network & Transport Performance - Static Code Analysis"
subsystem: network-transport
tags: [performance, tcp, quic, udp, transport, bandwidth, static-analysis]
dependency-graph:
  requires: []
  provides: [transport-perf-analysis, protocol-switching-profile, bandwidth-monitor-profile]
  affects: [AdaptiveTransport, BandwidthAwareSyncMonitor, ConnectionPool]
tech-stack:
  patterns: [protocol-morphing, fallback-chain, store-and-forward, connection-pooling, adaptive-compression]
key-files:
  analyzed:
    - Plugins/DataWarehouse.Plugins.AdaptiveTransport/AdaptiveTransportPlugin.cs
    - Plugins/DataWarehouse.Plugins.AdaptiveTransport/BandwidthMonitor/BandwidthAwareSyncMonitor.cs
    - Plugins/DataWarehouse.Plugins.AdaptiveTransport/BandwidthMonitor/BandwidthProbe.cs
    - Plugins/DataWarehouse.Plugins.AdaptiveTransport/BandwidthMonitor/LinkClassifier.cs
    - Plugins/DataWarehouse.Plugins.AdaptiveTransport/BandwidthMonitor/SyncParameterAdjuster.cs
    - Plugins/DataWarehouse.Plugins.AdaptiveTransport/BandwidthMonitor/SyncPriorityQueue.cs
    - Plugins/DataWarehouse.Plugins.AdaptiveTransport/BandwidthMonitor/BandwidthModels.cs
decisions:
  - "Analysis-only: no benchmark harness created per user directive"
  - "Focused on AdaptiveTransport plugin and BandwidthMonitor subsystem"
metrics:
  duration: "inline with plan execution"
  completed: "2026-02-18"
---

# Phase 46 Plan 04: Network & Transport Performance Summary

Static code analysis of transport layer performance across TCP, QUIC, Reliable UDP, and Store-and-Forward protocols in the AdaptiveTransportPlugin.

## 1. TCP Implementation

### Analysis

The TCP path (`SendViaTcpAsync`, lines 1228-1274) creates a new `TcpClient` per send operation:

```
using var client = new TcpClient();
await client.ConnectAsync(host, port, ct);
```

### Performance Characteristics

| Aspect | Assessment |
|--------|-----------|
| Connection reuse | NONE -- new connection per send |
| Buffer management | Length-prefix protocol (4-byte header + payload) |
| Acknowledgment | Single byte ACK read |
| Backpressure | None; relies on TCP flow control |

### Risks

1. **CRITICAL: No TCP connection reuse.** Every `SendViaTcpAsync` call creates a new TCP connection, performs the 3-way handshake, sends data, reads ACK, then disposes. For high-frequency sends, this wastes ~1 RTT per operation just for handshake. The `ConnectionPool` class exists but is never actually used for TCP -- `WarmupAsync` for TCP is a no-op (line 1986: "TCP connections are created on-demand") and `SendViaTcpAsync` never checks the pool.

2. **MODERATE: No Nagle algorithm control.** `TcpClient.NoDelay` is not set, meaning small payloads may be delayed by up to 200ms by Nagle's algorithm waiting for more data to batch.

3. **LOW: No TCP keep-alive.** Long-lived connections (if pooled in the future) would need keep-alive to detect dead peers through NAT/firewalls.

---

## 2. QUIC Implementation

### Analysis

The QUIC path (`SendViaQuicAsync`, lines 466-538) uses `System.Net.Quic.QuicConnection`:

### Performance Characteristics

| Aspect | Assessment |
|--------|-----------|
| Connection reuse | NONE -- new QuicConnection per send |
| Stream multiplexing | Opens one bidirectional stream per send |
| MaxInboundBidirectionalStreams | 100 (good for server-side) |
| MaxInboundUnidirectionalStreams | 10 |
| TLS | Hardcoded HTTP/3 ALPN |

### Strengths

1. **QUIC provides 0-RTT reconnection** (after initial handshake), reducing latency on resumption.
2. **Stream multiplexing** avoids head-of-line blocking -- multiple streams on one connection means one slow stream doesn't block others.
3. **Built-in congestion control** via QUIC's loss recovery and congestion control.

### Risks

1. **CRITICAL: No QUIC connection reuse.** Like TCP, every send creates a new `QuicConnection.ConnectAsync`. QUIC handshake is expensive (TLS 1.3 integrated). The `ConnectionPool` allocates a pool for QUIC but `SendViaQuicAsync` never retrieves from it.

2. **MODERATE: Certificate validation disabled.** Line 499: `RemoteCertificateValidationCallback = (_, _, _, _) => true`. This is noted as "In production: proper validation" but remains a security and performance concern (no certificate caching).

3. **LOW: Single stream per send.** The multiplexing capability of QUIC is not leveraged -- each send opens one stream on a new connection. Concurrent sends to the same endpoint should share a connection with multiple streams.

---

## 3. Reliable UDP Implementation

### Analysis

The Reliable UDP path (`SendViaReliableUdpAsync`, lines 551-621) implements custom chunking and ACK:

### Protocol Design

| Aspect | Value |
|--------|-------|
| Default chunk size | 1400 bytes (MTU-safe) |
| Packet format | 16B TransferID + 4B Seq + 4B Total + 4B Length + Payload + 4B CRC32 |
| Header overhead | 32 bytes per chunk |
| ACK format | 1B Type + 4B Sequence |
| Retransmission | Full resend of unacked chunks per retry |
| MaxRetries | 3 (default), 10 (satellite mode) |

### Performance Strengths

1. **CRC32 integrity check** per packet ensures data integrity over lossy links.
2. **Concurrent ACK reception** via separate `ReceiveAcksAsync` task running alongside sends.
3. **Selective retransmission** -- only unacked chunks are resent (line 580: `if (acked.ContainsKey(i)) continue`).

### Performance Risks

1. **CRITICAL: No congestion control.** The implementation sends all unacked chunks as fast as possible in each retry round (line 580-586). On a lossy or congested network, this creates a burst pattern that amplifies congestion. A proper reliable UDP needs additive-increase/multiplicative-decrease (AIMD) or similar.

2. **MODERATE: Retry is timeout-based, not RTT-adaptive.** `AckTimeout` is a fixed 500ms. If the actual RTT is 10ms, the sender waits 490ms unnecessarily between retry rounds. If RTT is 600ms, the sender retries before ACKs arrive, causing duplicate sends.

3. **MODERATE: `ChunkData` allocates new byte arrays per chunk.** Line 623-634: for every send, the payload is split into `new byte[length]` per chunk with `Buffer.BlockCopy`. For a 1MB payload, that's ~714 chunks = 714 byte array allocations. No `ArrayPool` usage.

4. **LOW: No sliding window.** All chunks are sent before waiting for ACKs. A sliding window would allow pipelining: send chunks up to window size, advance window as ACKs arrive. Current design has full round-trip latency between retry rounds.

5. **LOW: UdpClient not pooled.** Line 564: `using var client = new UdpClient()` per send. While UDP is connectionless, socket creation has OS overhead.

---

## 4. Bandwidth-Aware Sync Monitor

### Architecture

The `BandwidthAwareSyncMonitor` coordinates:
- `BandwidthProbe` -- measures network conditions
- `LinkClassifier` -- classifies link quality with hysteresis
- `SyncParameterAdjuster` -- adjusts sync parameters based on classification
- `SyncPriorityQueue` -- priority-based sync operation scheduling

### Performance Strengths

1. **Hysteresis threshold** (default 0.7) prevents oscillation between link classifications. A link must confidently change before parameters adjust.
2. **Probe serialization** via `SemaphoreSlim.WaitAsync(0)` -- only one probe runs at a time, preventing probe storms.
3. **Lazy initialization** (`EnsureBandwidthMonitor` in plugin) avoids overhead when monitoring is not needed.
4. **Background probing** decoupled from data path -- monitoring runs on a timer without blocking sends.

### Performance Risks

1. **MODERATE: Protocol switch requires 3 confirmation samples.** `ConsiderProtocolSwitchAsync` (lines 1007-1032) takes 3 additional quality measurements with 1-second delays before switching. Total switch latency: ~3-4 seconds. During this time, data continues on the suboptimal protocol.

2. **MODERATE: Network quality measurement sends real UDP probes.** `MeasureNetworkQualityAsync` sends `ProbeCount` (5) UDP probe packets to the target, waiting for echo responses. On networks that block UDP echo (port 7), all probes timeout, returning degraded metrics. This would cause unnecessary fallback to StoreForward.

3. **LOW: `MonitorNetworkQualityAsync` iterates all monitored endpoints sequentially.** If multiple endpoints are configured, slow endpoints delay monitoring of fast endpoints. Could be parallelized.

---

## 5. Protocol Switching Logic

### Decision Matrix

| Condition | Selected Protocol |
|-----------|-------------------|
| Latency > 500ms + SatelliteMode | StoreForward |
| PacketLoss > 10% | ReliableUdp |
| Jitter > 50ms + Latency < 200ms + QUIC available | Quic |
| Latency > 100ms + QUIC available | Quic |
| Default | Tcp |

### Strengths

1. **Ordered fallback chain** (default: TCP -> QUIC -> ReliableUdp -> StoreForward) ensures connectivity.
2. **Store-and-forward as last resort** provides eventual delivery guarantee via disk persistence.
3. **Transfer drain before switch** (line 1034-1044) waits for in-flight transfers with configurable timeout.

### Risks

1. **MODERATE: `SendWithFallbackAsync` tries protocols sequentially.** Each failed protocol attempt adds its full timeout before moving to the next. Worst case: 4 protocol timeouts before reaching StoreForward.

2. **LOW: Fallback chain order is configurable but default puts TCP first.** If TCP is blocked by firewall but QUIC works, every send attempt wastes a TCP connection timeout before falling back to QUIC.

---

## 6. Connection Pooling

### Current Implementation

The `ConnectionPool` class (lines 1961-2021) is minimal:

| Protocol | Pool Behavior |
|----------|--------------|
| TCP | No-op ("created on-demand") |
| QUIC | Pool created but never used by send methods |
| ReliableUdp | Pre-creates `UdpClient` instances in `ConcurrentBag` |

### Risks

1. **CRITICAL: Connection pool is not integrated with send paths.** The pool exists structurally but `SendViaTcpAsync`, `SendViaQuicAsync`, and `SendViaReliableUdpAsync` all create their own connections without checking the pool. The pool is effectively dead code for TCP and QUIC.

2. **MODERATE: `ConcurrentBag<object>` type erasure.** Pool stores connections as `object`, requiring boxing/casting. A generic pool per protocol type would be type-safe and avoid the cast overhead.

3. **LOW: No connection health check.** Pre-warmed UDP clients in the pool may become stale (e.g., if the local interface changes). No validation before use.

---

## 7. Overall Network Performance Risk Summary

### Critical Issues

| # | Issue | Component | Impact |
|---|-------|-----------|--------|
| 1 | TCP connection not reused | SendViaTcpAsync | 1 RTT wasted per send for TCP handshake |
| 2 | QUIC connection not reused | SendViaQuicAsync | TLS 1.3 handshake per send; connection pool unused |
| 3 | No UDP congestion control | SendViaReliableUdpAsync | Congestion amplification on lossy networks |
| 4 | Connection pool dead code | ConnectionPool | Pool exists but never queried by send methods |

### Moderate Issues

| # | Issue | Component | Impact |
|---|-------|-----------|--------|
| 5 | UDP chunk allocation per send | ChunkData | ~714 byte[] allocations for 1MB payload |
| 6 | Fixed ACK timeout (not RTT-adaptive) | ReliableUdp | Unnecessary wait or premature retransmission |
| 7 | Protocol switch takes 3-4 seconds | ConsiderProtocolSwitchAsync | Suboptimal protocol during transition |
| 8 | UDP echo probes may fail on firewalled networks | MeasureNetworkQualityAsync | False degraded metrics causing unnecessary fallback |
| 9 | No Nagle disable on TCP | SendViaTcpAsync | Up to 200ms delay on small payloads |

### Recommendations

1. **Integrate connection pool with send paths.** Checkout/checkin pattern: `var conn = pool.Rent(); try { /* send */ } finally { pool.Return(conn); }`. For TCP, maintain persistent connections. For QUIC, share connection with multiple streams.
2. **Add basic congestion control to Reliable UDP.** Start with simple approach: limit in-flight chunks to a window, expand on ACK, halve on loss.
3. **Use `ArrayPool` for UDP chunking.** Replace `new byte[length]` with `ArrayPool<byte>.Shared.Rent(length)` and return after send completion.
4. **Implement RTT estimation for ACK timeout.** Track smoothed RTT and RTT variance (TCP-style SRTT) to set adaptive timeouts.
5. **Set `TcpClient.NoDelay = true`** for latency-sensitive sends.
6. **Use ICMP or TCP-based probing** as fallback when UDP echo is blocked.

## Readiness Verdict

**PRODUCTION READY for basic workloads; NOT RECOMMENDED for high-throughput scenarios.** The transport layer provides correct multi-protocol support with intelligent switching and fallback. However, the lack of connection reuse (Critical #1, #2) means every send incurs full connection setup cost, making sustained high-throughput impossible. The unused connection pool (Critical #4) indicates the pooling infrastructure was designed but integration was deferred. For production deployments with more than ~100 sends/second, connection pooling must be wired in. The Reliable UDP lack of congestion control (Critical #3) is dangerous on shared networks.

## Deviations from Plan

None -- plan executed exactly as written (read-only analysis).

## Self-Check: PASSED
