---
phase: 46
plan: 46-05
title: "Memory & Resource Profiling - Static Code Analysis"
subsystem: memory-management
tags: [performance, memory, arraypool, loh, bounded-collections, disposable, static-analysis]
dependency-graph:
  requires: []
  provides: [memory-perf-analysis, bounded-collection-audit, disposable-audit, loh-risk-profile]
  affects: [BoundedMemoryRuntime, MemoryBudgetTracker, ObjectPool, SpanBuffer, BatchProcessor]
tech-stack:
  patterns: [arraypool, object-pool, bounded-channel, span-buffer, memory-budget, gc-pressure-monitoring]
key-files:
  analyzed:
    - DataWarehouse.SDK/Edge/Memory/BoundedMemoryRuntime.cs
    - DataWarehouse.SDK/Edge/Memory/MemoryBudgetTracker.cs
    - DataWarehouse.SDK/Edge/Memory/MemorySettings.cs
    - DataWarehouse.SDK/Primitives/Performance/PerformanceUtilities.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Journal/WriteAheadLog.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Index/BTree.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Container/ContainerFile.cs
    - DataWarehouse.SDK/VirtualDiskEngine/BlockAllocation/BitmapAllocator.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Integrity/CorruptionDetector.cs
    - DataWarehouse.SDK/VirtualDiskEngine/CopyOnWrite/CowBlockManager.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Metadata/InodeTable.cs
    - DataWarehouse.SDK/Contracts/Hierarchy/DataPipeline/EncryptionPluginBase.cs
    - DataWarehouse.Kernel/Messaging/AdvancedMessageBus.cs
    - DataWarehouse.Shared/MessageBridge.cs
decisions:
  - "Analysis-only: no benchmark harness created per user directive"
  - "Covered SDK, Kernel, and key Plugin memory patterns"
metrics:
  duration: "inline with plan execution"
  completed: "2026-02-18"
---

# Phase 46 Plan 05: Memory & Resource Profiling Summary

Static code analysis of memory management patterns, ArrayPool usage, bounded collections, LOH risks, and IDisposable compliance across the codebase.

## 1. Bounded Collections Audit

### Bounded Channel Usage (System.Threading.Channels)

The codebase makes extensive use of `Channel.CreateBounded<T>` for backpressure-aware queuing:

| Location | Capacity | FullMode | Purpose |
|----------|----------|----------|---------|
| MessageBridge | 100 | Wait | In-process message bridge |
| GossipReplicator | Config.MaxPendingMessages | DropOldest | Gossip message queue |
| P2PSwarmStrategy | PieceQueueCapacity | Wait | Piece download scheduling |
| ParallelExecutionStrategies | 100 | Wait | Parallel execution pipeline |
| ConcreteChannels (Intelligence) | QueueCapacity (config) | DropOldest or Wait | Priority message queue |
| IndexManager (Intelligence) | MaxQueueSize (config) | Wait | Background indexing tasks |
| IntelligenceGateway | capacity (param) | Wait | Inbound/outbound message channels |
| BackpressureHandling (Streaming) | BufferCapacity (config) | Block/DropNewest/DropOldest | Stream event buffering |
| PipelinedExecutionStrategy | bufferSize (param) | Wait | Pipeline stage buffers |
| WriteBehindCacheStrategy | MaxQueueSize (config) | Wait | Write-behind operation queue |

### Bounded Dictionary Usage

| Location | Max Capacity | Eviction | Purpose |
|----------|-------------|----------|---------|
| AdvancedMessageBus._pendingMessages | MaxPendingMessages (config) | Oldest by timestamp | Pending message tracking |
| AdvancedMessageBus._messageGroups | MaxMessageGroups (config) | Oldest by timestamp | Message group coordination |
| CrdtReplicationSync._dataStore | 100,000 (MaxStoredItems) | LRU by LastModified | CRDT data store |
| RaftConsensusEngine._persistent.Log | 10,000 (MaxLogEntries) | Oldest entries | Raft log compaction |
| SwimClusterMembership._recentUpdates | 100 (MaxRecentUpdates) | FIFO dequeue | Recent membership updates |

### Assessment

**GOOD: Bounded collections are used consistently across the codebase.** Every major queue, channel, and collection has explicit capacity limits. The `BoundedChannelOptions` pattern is standard and the `BoundedConcurrentDictionary` in AdvancedMessageBus is a custom bounded dictionary with proper eviction. No unbounded `ConcurrentQueue` or `ConcurrentBag` was found in hot paths (except the connection pool `ConcurrentBag<object>` which is pre-sized).

**Exception: ORSet tag growth** (documented in Plan 46-03) and `ConcurrentDictionary<string, SwimMemberState>` in SwimClusterMembership which grows with cluster membership (naturally bounded by cluster size).

---

## 2. ArrayPool Usage Analysis

### Files Using ArrayPool<byte>

20 files across the codebase use `ArrayPool`. Key patterns:

#### SDK Core (Hot Paths)

| File | Pattern | Assessment |
|------|---------|-----------|
| **EncryptionPluginBase** | `ArrayPool<byte>.Shared.Rent/Return` for encryption buffers | GOOD: returns buffers in finally block |
| **VirtualDiskEngine** | `ArrayPool<byte>.Shared.Rent` for block I/O buffers | GOOD: critical path for all VDE operations |
| **InodeTable** | `ArrayPool<byte>.Shared.Rent` for inode serialization | GOOD: high-frequency metadata operations |
| **CowBlockManager** | `ArrayPool<byte>.Shared.Rent` for copy-on-write block copies | GOOD: avoids LOH for block-sized buffers |
| **WriteAheadLog** | `ArrayPool<byte>.Shared.Rent` for journal entry buffers | GOOD: WAL is write-intensive |
| **BTree** | `ArrayPool<byte>.Shared.Rent` for index page I/O | GOOD: index traversal is read-heavy |
| **BulkLoader** | `ArrayPool<byte>.Shared.Rent` for bulk data loading | GOOD: batch operations |
| **CorruptionDetector** | `ArrayPool<byte>.Shared.Rent` for checksum computation buffers | GOOD: integrity scanning |
| **ContainerFile** | `ArrayPool<byte>.Shared.Rent` for container I/O | GOOD: file-level operations |
| **BitmapAllocator** | `ArrayPool<byte>.Shared.Rent` for allocation bitmap | GOOD: allocation tracking |
| **BoundedMemoryRuntime/MemoryBudgetTracker** | Custom `ArrayPool<byte>.Create` with budget tracking | GOOD: edge-specific pool with ceiling enforcement |

#### Plugin Layer

| File | Pattern | Assessment |
|------|---------|-----------|
| **DatabaseProtocolStrategyBase** | `ArrayPool<byte>.Shared.Rent` for protocol buffers | GOOD |
| **ConcreteChannels (Intelligence)** | `ArrayPool<byte>.Shared.Rent` for channel message buffers | GOOD |
| **ScmStrategy (Storage)** | `ArrayPool<byte>.Shared.Rent` for SCM I/O | GOOD |
| **NvmeDiskStrategy (Storage)** | `ArrayPool<byte>.Shared.Rent` for NVMe I/O | GOOD |

#### SDK Utilities

| File | Pattern | Assessment |
|------|---------|-----------|
| **PerformanceUtilities.SpanBuffer<T>** | Wraps `ArrayPool<T>.Shared` in IDisposable pattern | GOOD: clear-on-return for security |
| **PerformanceUtilities.ObjectPool<T>** | Generic ConcurrentBag-based pool (max 100) | GOOD: bounded with configurable max |

### Hot Paths Without ArrayPool

| File/Method | Allocation Pattern | Risk |
|------------|-------------------|------|
| **AdaptiveTransport.ChunkData** | `new byte[length]` per UDP chunk | MODERATE: 700+ allocs for 1MB payload |
| **AdaptiveTransport.CreateReliablePacket** | `new byte[28 + payload.Length]` per packet | MODERATE: allocates per packet |
| **ConsistentHashRing.ComputeHash** | `Encoding.UTF8.GetBytes(key)` | LOW: small allocs on lookup hot path |
| **SdkCrdtTypes serialization** | `JsonSerializer.SerializeToUtf8Bytes` returns new byte[] | MODERATE: per-serialize allocation |

---

## 3. Large Object Heap (LOH) Risk Analysis

The .NET LOH threshold is 85,000 bytes (85KB). Arrays larger than this are allocated on the LOH, which has expensive Gen2 GC collection.

### Potential LOH Allocations

| Location | Array Size | Risk |
|----------|-----------|------|
| **MemoryBudgetTracker** | `ArrayPool.Rent(size)` where size can be up to `ArrayPoolMaxArraySize` (1MB default) | MITIGATED: ArrayPool reuses LOH arrays instead of allocating new ones |
| **SpanBuffer<T>** | `ArrayPool<T>.Shared.Rent(size)` where size is caller-controlled | MITIGATED: ArrayPool handles LOH arrays |
| **VDE block operations** | Block size is typically 4KB-64KB | LOW: under LOH threshold for default block sizes |
| **CrdtReplicationSync.SerializeBatch** | `JsonSerializer.SerializeToUtf8Bytes(syncItems)` for batch of 100 items | MODERATE: if batch payload exceeds 85KB, LOH allocation occurs |
| **ReliableUDP large payloads** | Full `data` array passed to `SendViaReliableUdpAsync` | LOW: chunked before send, original array is caller-owned |
| **EncryptionPluginBase** | Rents buffer matching input size | MITIGATED: uses ArrayPool for LOH-sized buffers |

### Assessment

**GOOD: LOH risk is well-mitigated.** The consistent use of `ArrayPool<byte>.Shared` throughout the VDE and encryption pipeline means LOH-sized arrays are rented and returned rather than allocated fresh. The `BoundedMemoryRuntime` creates a custom ArrayPool with `maxArrayLength` of 1MB, ensuring large arrays are pooled and reused.

**Remaining risk:** JSON serialization outputs (`SerializeToUtf8Bytes`) return new arrays that may land on LOH for large payloads. This is a framework limitation -- `JsonSerializer` does not support writing to pooled buffers without using `Utf8JsonWriter` + `IBufferWriter<byte>`.

---

## 4. BoundedMemoryRuntime Analysis

### Architecture

```
BoundedMemoryRuntime (singleton)
  -> MemoryBudgetTracker
       -> ArrayPool<byte>.Create(maxArraySize: 1MB, maxArraysPerBucket: 50)
       -> GC.GetTotalMemory() for usage tracking
       -> Proactive Gen1 GC at 85% threshold
       -> Aggressive Gen2 GC at ceiling
```

### Configuration Defaults

| Parameter | Default | Purpose |
|-----------|---------|---------|
| MemoryCeiling | 128 MB | Hard limit for edge devices |
| ArrayPoolMaxArraySize | 1 MB | Largest poolable array |
| GcPressureThreshold | 0.85 (85%) | Proactive GC trigger point |
| Enabled | false (opt-in) | Zero overhead when disabled |

### Performance Strengths

1. **Opt-in design** (`Enabled = false` default) means zero overhead for non-edge deployments. All methods short-circuit when disabled.
2. **Proactive Gen1 GC** at 85% ceiling prevents sudden Gen2 pauses. The 10-second monitoring interval is reasonable.
3. **Custom ArrayPool** with 50 arrays per bucket provides dedicated pool separate from `ArrayPool.Shared`, preventing contention with other subsystems.
4. **Ceiling enforcement** with progressive GC: attempts Gen2 compact before throwing OOM.

### Performance Risks

1. **MODERATE: `GC.GetTotalMemory(false)` called on every `Rent()`.** Line 68: `if (CurrentUsage + size > Ceiling)` reads `GC.GetTotalMemory` which is a relatively expensive API call (acquires GC info, not free). On high-frequency buffer rent paths, this adds overhead. A sampled approach (check every Nth call) would reduce cost.

2. **MODERATE: Aggressive Gen2 GC on ceiling breach.** Line 71: `GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true)` freezes all threads for full heap compaction. In a production system handling requests, this causes a latency spike.

3. **LOW: `Interlocked.Add` tracking on every Rent/Return.** `_allocatedBytes` is atomically incremented/decremented on every buffer operation. Under high concurrency, this creates cache-line contention across cores.

4. **LOW: Singleton pattern prevents per-tenant memory isolation.** All callers share one ceiling. In multi-tenant edge scenarios, one tenant's memory pressure affects all others.

---

## 5. IDisposable / IAsyncDisposable Compliance

### Audit Summary

Across 71 files referencing IDisposable/IAsyncDisposable in the SDK:

#### Correct Implementation Patterns

| Component | Pattern | Assessment |
|-----------|---------|-----------|
| **RaftConsensusEngine** | `IDisposable`: cancels CTS, disposes semaphore, unsubscribes events | GOOD |
| **SwimClusterMembership** | `IDisposable`: cancels CTS, disposes semaphore, unsubscribes events | GOOD |
| **ConsistentHashRing** | `IDisposable`: disposes ReaderWriterLockSlim | GOOD |
| **CrdtReplicationSync** | `IDisposable`: cancels CTS, disposes semaphore, unsubscribes gossip | GOOD |
| **GossipReplicator** | `IDisposable`: cancels CTS, disposes semaphore | GOOD |
| **BoundedMemoryRuntime** | `IDisposable`: disposes monitor timer | GOOD |
| **SpanBuffer<T>** | `IDisposable`: returns array to pool, clears | GOOD |
| **BatchProcessor<T>** | `IAsyncDisposable`: cancels CTS, flushes, disposes semaphore | GOOD |
| **VirtualDiskEngine** | `IDisposable`: disposes all sub-components | GOOD |
| **WriteAheadLog** | `IDisposable`: flushes and closes file | GOOD |
| **BTree** | `IDisposable`: flushes dirty pages | GOOD |
| **ContainerFile** | `IDisposable`: closes file handle | GOOD |
| **ConnectionPool** | `IAsyncDisposable`: disposes all pooled connections | GOOD |

#### Potential Issues

| Component | Issue | Severity |
|-----------|-------|----------|
| **AdaptiveTransportPlugin** | Inherits `StreamingPluginBase` but does not implement `IDisposable`. The `_qualityMonitorTimer`, `_switchLock`, and `_bandwidthMonitor` are not disposed in a `Dispose()` method. `StopAsync()` handles cleanup but is not guaranteed to be called. | MODERATE |
| **BandwidthAwareSyncMonitor** | `IDisposable`: disposes timer and semaphore. However, `_probe`, `_classifier`, `_adjuster`, and `_syncQueue` are not checked for disposability. | LOW |
| **MemoryBudgetTracker** | Does NOT implement `IDisposable`. The internal `ArrayPool` created via `ArrayPool<byte>.Create()` is not disposable in .NET, so this is technically fine but the tracker itself holds no disposal contract for cleanup. | LOW (informational) |
| **Event handler unsubscription** | Raft, SWIM, and CRDT all properly unsubscribe from events in `Dispose()`. No detected event handler leak patterns. | GOOD |

---

## 6. Memory Leak Risk Analysis

### Event Handler Leaks

| Component | Event | Subscribe | Unsubscribe | Assessment |
|-----------|-------|-----------|-------------|-----------|
| RaftConsensusEngine | `_network.OnPeerEvent` | StartAsync | Stop() + Dispose() | GOOD (double-unsubscribe is safe) |
| RaftConsensusEngine | `_membership.OnMembershipChanged` | StartAsync | Stop() + Dispose() | GOOD |
| SwimClusterMembership | `_network.OnPeerEvent` | Constructor | Dispose() | GOOD |
| SwimClusterMembership | `_gossip.OnGossipReceived` | Constructor | Dispose() | GOOD |
| CrdtReplicationSync | `_gossip.OnGossipReceived` | Constructor | Dispose() | GOOD |
| BandwidthAwareSyncMonitor | `OnLinkClassChanged` | Public event | Caller responsibility | LOW risk: only used internally by plugin |

### Closure/Lambda Risks

| Location | Pattern | Risk |
|----------|---------|------|
| `Timer` callbacks throughout codebase | `async _ => await Method()` | LOW: timer is disposed, preventing further invocations |
| `Task.Run(() => RunLoopAsync(ct))` | Captures `CancellationToken` | SAFE: token is from a CTS that is disposed |
| LINQ `.Select` in Raft vote/heartbeat | Captures local variables | SAFE: short-lived; tasks complete or timeout quickly |

### Unmanaged Resource Risks

| Component | Unmanaged Resource | Handling |
|-----------|-------------------|----------|
| `UdpClient` in ReliableUdp sends | Socket handle | GOOD: `using var client` pattern |
| `TcpClient` in TCP sends | Socket handle | GOOD: `using var client` pattern |
| `QuicConnection` in QUIC sends | QUIC handle + TLS state | GOOD: `await using var connection` pattern |
| `ConnectionPool` UDP clients | Socket handles in ConcurrentBag | GOOD: `DisposeAsync` iterates and disposes all |
| `ReaderWriterLockSlim` in ConsistentHashRing | OS synchronization primitive | GOOD: disposed in `Dispose()` |
| `SemaphoreSlim` across distributed components | OS synchronization primitive | GOOD: all properly disposed |

---

## 7. Memory Performance Risk Summary

### Critical Issues

None identified. Memory management across the codebase is consistently well-implemented.

### Moderate Issues

| # | Issue | Component | Impact |
|---|-------|-----------|--------|
| 1 | `GC.GetTotalMemory` on every Rent() call | MemoryBudgetTracker | CPU overhead on high-frequency buffer paths |
| 2 | Aggressive blocking Gen2 GC on ceiling breach | MemoryBudgetTracker | Latency spike (all threads frozen) |
| 3 | AdaptiveTransportPlugin missing IDisposable | AdaptiveTransportPlugin | Timer, semaphore, monitor not disposed if StopAsync not called |
| 4 | UDP chunk allocation without ArrayPool | AdaptiveTransport.ChunkData | GC pressure for large payloads |
| 5 | JSON serialization LOH risk for large batches | CrdtReplicationSync | SerializeToUtf8Bytes may allocate on LOH |

### Low Issues

| # | Issue | Component | Impact |
|---|-------|-----------|--------|
| 6 | Interlocked contention on _allocatedBytes | MemoryBudgetTracker | Cache-line bouncing under high concurrency |
| 7 | Singleton ceiling prevents multi-tenant isolation | BoundedMemoryRuntime | One tenant's usage affects all |

### Recommendations

1. **Sample `GC.GetTotalMemory` calls.** Check every 100th Rent() or use a timer-based snapshot rather than per-call.
2. **Implement IDisposable on AdaptiveTransportPlugin.** Dispose `_qualityMonitorTimer`, `_switchLock`, and `_bandwidthMonitor` to prevent resource leaks if `StopAsync` is not called.
3. **Use ArrayPool for UDP chunking** in `ChunkData` and `CreateReliablePacket`.
4. **Consider `IBufferWriter<byte>` for CRDT serialization** to avoid LOH allocations from `SerializeToUtf8Bytes` on large batches.
5. **Add memory pressure metrics** to the `BoundedMemoryRuntime` for observability (current usage, peak usage, GC trigger count).

## Readiness Verdict

**PRODUCTION READY.** Memory management is one of the strongest aspects of this codebase. The consistent ArrayPool usage across the VDE, encryption, and storage layers eliminates the most common .NET performance pitfall (LOH fragmentation). The BoundedMemoryRuntime provides a proper edge-device memory ceiling with progressive GC pressure relief. IDisposable compliance is excellent across all core SDK components with proper event unsubscription and resource cleanup. The identified moderate issues are optimization opportunities, not correctness problems. The only actionable item is adding IDisposable to AdaptiveTransportPlugin to prevent resource leaks in abnormal shutdown paths.

## Deviations from Plan

None -- plan executed exactly as written (read-only analysis).

## Self-Check: PASSED
