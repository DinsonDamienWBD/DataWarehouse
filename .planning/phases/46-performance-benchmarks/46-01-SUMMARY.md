---
phase: 46
plan: 46-01
title: "Data Pipeline Performance - Static Code Analysis"
subsystem: data-pipeline
tags: [performance, compression, encryption, pipeline, static-analysis]
dependency-graph:
  requires: []
  provides: [pipeline-perf-analysis, compression-perf-profile, encryption-perf-profile]
  affects: [UltimateCompression, UltimateEncryption, SDK-Pipeline]
tech-stack:
  patterns: [strategy-pattern, stream-chaining, ArrayPool, AEAD, hardware-accel]
key-files:
  analyzed:
    - DataWarehouse.SDK/Contracts/IPipelineOrchestrator.cs
    - DataWarehouse.SDK/Primitives/PipelineConfig.cs
    - DataWarehouse.SDK/Contracts/Pipeline/IPipelineTransaction.cs
    - DataWarehouse.SDK/Contracts/Compression/CompressionStrategy.cs
    - DataWarehouse.SDK/Contracts/Encryption/EncryptionStrategy.cs
    - DataWarehouse.SDK/Contracts/Hierarchy/DataPipeline/CompressionPluginBase.cs
    - DataWarehouse.SDK/Contracts/Hierarchy/DataPipeline/EncryptionPluginBase.cs
    - DataWarehouse.SDK/Contracts/Hierarchy/DataPipeline/StoragePluginBase.cs
    - Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/LzFamily/Lz4Strategy.cs
    - Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/LzFamily/ZstdStrategy.cs
    - Plugins/DataWarehouse.Plugins.UltimateCompression/Strategies/Transform/BrotliStrategy.cs
    - Plugins/DataWarehouse.Plugins.UltimateEncryption/Strategies/Aes/AesGcmStrategy.cs
decisions:
  - "Analysis-only: no benchmark harness created per user directive"
  - "Focused on SDK base classes and concrete strategy implementations"
metrics:
  duration: "static-analysis"
  completed: "2026-02-18"
---

# Phase 46 Plan 01: Data Pipeline Performance - Static Code Analysis

**One-liner:** Static analysis of write/read pipeline revealing strong streaming APIs and hardware-accelerated crypto, with byte-array duplication in encryption as the primary bottleneck risk.

## Pipeline Architecture Overview

The DataWarehouse pipeline follows a configurable stage-based model:

**Write Pipeline (Default):** Compress (order 100) -> Encrypt (order 200)
**Read Pipeline:** Reverse order automatically

Key interfaces:
- `IPipelineOrchestrator` orchestrates stage execution via `ExecuteWritePipelineAsync`/`ExecuteReadPipelineAsync`
- `PipelineContext` tracks intermediate streams with `IDisposable` for cleanup
- `IPipelineTransaction` provides rollback support with WAL-style stage recording

---

## Performance Strengths

### 1. Streaming Compression APIs (GOOD)

All three primary compression strategies (LZ4, Zstd, Brotli) implement proper streaming via `CreateCompressionStream`/`CreateDecompressionStream`:

| Algorithm | Library | Streaming | Parallel | Speed Rating | Ratio |
|-----------|---------|-----------|----------|-------------|-------|
| LZ4 | K4os.Compression.LZ4 | Yes (LZ4Stream) | No | 10/10 | ~0.55 |
| Zstd | ZstdSharp | Yes (CompressionStream) | Yes | 8/10 | ~0.35 |
| Brotli | System.IO.Compression | Yes (BrotliStream) | No | 3/10 | ~0.30 |

**Strength:** All compression strategies use native streaming wrappers, avoiding full-buffer materialization on the hot path. The `CompressionStrategyBase.CreateCompressionStream()` delegates to library-native stream types.

### 2. Content-Aware Compression Bypass (GOOD)

`CompressionStrategyBase.ShouldCompress(ReadOnlySpan<byte>)` performs entropy analysis via Shannon entropy calculation using `stackalloc int[256]` -- zero heap allocation for the frequency table. This detects already-compressed, encrypted, or media data and bypasses compression, avoiding wasted CPU cycles.

### 3. Hardware-Accelerated Encryption (GOOD)

`AesGcmStrategy` uses `System.Security.Cryptography.AesGcm` which is the .NET wrapper around the OS cryptographic provider:
- On x86/x64: Uses AES-NI hardware instructions when available
- On ARM: Uses ARMv8 Crypto Extensions
- Authenticated encryption (AEAD) in a single pass -- no separate HMAC step

`CipherCapabilities.AeadCipher` correctly declares: `IsHardwareAcceleratable = true`, `SupportsParallelism = true`.

### 4. ArrayPool Usage in Encryption Envelope (GOOD)

`EncryptionPluginBase.DecryptWithEnvelopeAsync()` uses `ArrayPool<byte>.Shared.Rent(4096)` for the header buffer with proper `try/finally` return and `clearArray: true` for security-sensitive data wiping.

### 5. Statistics Tracking Infrastructure (GOOD)

Both `CompressionStrategyBase` and `EncryptionStrategyBase` track:
- Operation counts (thread-safe via `lock`/`Interlocked`)
- Bytes processed, throughput calculations
- Compression ratios per-instance
- Failure counts

This enables runtime performance monitoring without external instrumentation.

### 6. Pipeline Transaction Rollback (GOOD)

`IPipelineTransaction` records each stage execution with `ExecutedStageInfo` including `CapturedState`. On failure, `RollbackAsync()` reverses stages in order. This prevents partial writes but has minimal performance overhead since state capture is dictionary-based (no stream cloning).

---

## Performance Risks

### RISK 1: Byte Array Duplication in Encryption (HIGH)

**Location:** `EncryptionStrategyBase.EncryptAsync()` / `AesGcmStrategy.EncryptCoreAsync()`

The encryption interface is `Task<byte[]> EncryptAsync(byte[] plaintext, ...)` -- it takes a full byte array and returns a new byte array. For a 100MB payload:
1. Input: 100MB byte array already in memory
2. `AesGcm.Encrypt()` outputs to a new 100MB `ciphertext` array
3. `CombineIvAndCiphertext()` creates yet another array (nonce + ciphertext + tag = ~100MB + 28 bytes)

**Impact:** 3x memory for the payload size during encryption. For a 1GB payload, this means ~3GB RAM just for the encryption stage.

**Root cause:** The `IEncryptionStrategy` interface mandates `byte[]` input/output rather than `Stream` or `Span<byte>` for the core encrypt/decrypt path. The streaming path exists in `EncryptionPluginBase.EncryptCoreAsync(Stream, byte[], byte[])` but the strategy-level interface does not expose it.

### RISK 2: CompressCore Uses byte[] Not Streams (MEDIUM)

**Location:** `CompressionStrategyBase.Compress(byte[])` / `CompressCore(byte[])`

The synchronous `Compress(byte[])` method calls `CompressCore(byte[])` which materializes both input and output as full byte arrays. For LZ4: `LZ4Pickler.Pickle(input)` creates a copy. For Zstd: `compressor.Wrap(input).ToArray()` also creates a copy.

**Mitigation:** The streaming path (`CreateCompressionStream`) IS available and the pipeline orchestrator uses `Stream`-based APIs. This risk is only realized when callers use the byte-array API instead of the stream API.

### RISK 3: Default Async Compression Falls Back to ThreadPool (MEDIUM)

**Location:** `CompressionStrategyBase.CompressAsyncCore()` (line 725-728)

```csharp
protected virtual Task<byte[]> CompressAsyncCore(byte[] input, CancellationToken ct)
{
    return Task.Run(() => CompressCore(input), ct);
}
```

The default async implementation wraps synchronous compression in `Task.Run()`, consuming a ThreadPool thread. For high-concurrency scenarios with many parallel compression operations, this can starve the ThreadPool.

**Mitigation:** Individual strategies can override. LZ4/Zstd/Brotli do NOT override this default, so they use the ThreadPool fallback.

### RISK 4: BrotliStrategy Quality=11 by Default (MEDIUM)

**Location:** `BrotliStrategy.CompressCore()` (line 56)

```csharp
var encoder = new BrotliEncoder(quality: 11, window: 22);
```

Quality 11 is the maximum Brotli compression level. This is extremely slow (compression speed rating: 3/10 in characteristics). For a pipeline that processes data on every write, this is a significant latency hit. The streaming path uses `CompressionLevel.Optimal` which is more balanced, but the byte-array path uses quality 11.

### RISK 5: Pipeline Context Intermediate Stream Tracking (LOW)

**Location:** `PipelineContext.IntermediateStreams` (line 258)

The `PipelineContext` tracks intermediate streams as `List<Stream>?` for disposal. If a pipeline has many stages, each generating intermediate streams, the list grows. However, this is a list of references only -- the actual memory concern is the stream contents, which are disposed properly.

### RISK 6: Lock Contention in Compression Statistics (LOW)

**Location:** `CompressionStrategyBase._statsLock` (line 480)

Statistics updates use `lock (_statsLock)` rather than `Interlocked` operations. Under extremely high throughput (thousands of compress operations per second), this could become a contention point. The encryption base class correctly uses `Interlocked` for statistics.

---

## Bottleneck Analysis

### Per-Stage Latency Estimate (10MB Payload)

| Stage | Operation | Estimated Bottleneck | Notes |
|-------|-----------|---------------------|-------|
| Validation | Schema/size check | Negligible | O(1) metadata checks |
| Transform | Content adaptation | Low | Depends on transform type |
| **Compress (LZ4)** | 10MB input | **~2-5ms** | LZ4 at ~2-4 GB/s throughput |
| **Compress (Zstd)** | 10MB input | **~5-15ms** | Zstd level 3 at ~500 MB/s |
| **Compress (Brotli)** | 10MB input | **~100-500ms** | Brotli Q11 at ~20-100 MB/s |
| **Encrypt (AES-256-GCM)** | 10MB input | **~1-3ms** | AES-NI at ~3-6 GB/s |
| Store | Write to backend | Variable | Depends on storage backend |

**Identified bottleneck:** Brotli at quality 11 is the slowest stage by an order of magnitude. For typical write pipelines, LZ4 or Zstd with AES-256-GCM encryption would be sub-20ms total for 10MB.

### Memory Allocation Profile

| Operation | Allocations | Notes |
|-----------|------------|-------|
| Pipeline context creation | 3 small objects | Dictionary, List, etc. |
| Compression (streaming) | 1 buffer (~64-128KB) | Library-internal buffers |
| Compression (byte-array) | 2x payload size | Input + output arrays |
| Encryption (byte-array) | 3x payload size | Input + ciphertext + combined |
| CombineIvAndCiphertext | 1x payload | Final combined array |

---

## Optimization Recommendations

### Priority 1: Add Streaming Encryption Interface

Add `Stream`-based encrypt/decrypt to `IEncryptionStrategy`:
```csharp
Task<Stream> EncryptStreamAsync(Stream plaintext, byte[] key, byte[]? ad, CancellationToken ct);
Task<Stream> DecryptStreamAsync(Stream ciphertext, byte[] key, byte[]? ad, CancellationToken ct);
```
This would eliminate the 3x memory overhead for large payloads by processing data in chunks.

### Priority 2: Reduce Brotli Default Quality

Change `BrotliStrategy.CompressCore()` from quality 11 to quality 4-6 for the byte-array path. Quality 4-6 provides ~90% of the compression ratio at 5-10x the speed.

### Priority 3: Use Interlocked for Compression Stats

Replace `lock (_statsLock)` with `Interlocked.Increment`/`Interlocked.Add` in `CompressionStrategyBase` to match the pattern already used in `EncryptionStrategyBase`.

### Priority 4: Override CompressAsyncCore in Concrete Strategies

LZ4, Zstd, and Brotli strategies should override `CompressAsyncCore` to use their native async APIs or at minimum avoid the `Task.Run` wrapper when the caller is already on a background thread.

---

## Overall Readiness Verdict

**GOOD with caveats.** The pipeline architecture is fundamentally sound:

- Streaming APIs are available and correctly implemented for compression
- Hardware-accelerated AES-256-GCM provides excellent encryption throughput
- Pipeline is configurable, reversible, and transaction-safe
- Content-aware bypass avoids wasting CPU on incompressible data
- Statistics infrastructure enables runtime monitoring

**The main gap** is the byte-array-based encryption interface which forces full materialization for large payloads. For payloads under 10MB (the common case for document storage), this is acceptable. For 100MB+ payloads (media, bulk data), the 3x memory overhead becomes a real concern.

**Estimated pipeline throughput (10MB, LZ4+AES-GCM):** ~500-1000 MB/s (limited by compression)
**Estimated pipeline throughput (10MB, Zstd+AES-GCM):** ~200-400 MB/s
**Estimated pipeline throughput (10MB, Brotli Q11+AES-GCM):** ~20-50 MB/s

---

## Self-Check: PASSED

- [x] 46-01-SUMMARY.md exists (229 lines)
- [x] Frontmatter present with phase, plan, tags, dependency-graph, key-files
- [x] Performance strengths documented (6 items)
- [x] Performance risks documented (6 items with severity ratings)
- [x] Bottleneck analysis with latency estimates
- [x] Optimization recommendations (4 priorities)
- [x] Overall readiness verdict with throughput estimates
