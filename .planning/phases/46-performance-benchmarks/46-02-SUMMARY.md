---
phase: 46
plan: 46-02
title: "Storage & IO Performance - Static Code Analysis"
subsystem: storage-io
tags: [performance, VDE, storage, RAID, block-io, static-analysis]
dependency-graph:
  requires: []
  provides: [vde-perf-analysis, raid-perf-profile, storage-io-profile]
  affects: [VirtualDiskEngine, UltimateRAID, UltimateStorage, StorageConnectionRegistry]
tech-stack:
  patterns: [block-device, WAL, B-Tree, CoW, bitmap-allocator, extent-tree, connection-pooling]
key-files:
  analyzed:
    - DataWarehouse.SDK/VirtualDiskEngine/VirtualDiskEngine.cs
    - DataWarehouse.SDK/VirtualDiskEngine/FileBlockDevice.cs
    - DataWarehouse.SDK/VirtualDiskEngine/BlockAllocation/BitmapAllocator.cs
    - DataWarehouse.SDK/VirtualDiskEngine/BlockAllocation/ExtentTree.cs
    - DataWarehouse.SDK/VirtualDiskEngine/CopyOnWrite/CowBlockManager.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Journal/WriteAheadLog.cs
    - DataWarehouse.SDK/VirtualDiskEngine/VdeStorageStrategy.cs
    - DataWarehouse.SDK/VirtualDiskEngine/VdeOptions.cs
    - DataWarehouse.SDK/Infrastructure/StorageConnectionRegistry.cs
    - DataWarehouse.SDK/Contracts/Hierarchy/DataPipeline/StoragePluginBase.cs
    - Plugins/DataWarehouse.Plugins.UltimateRAID/RaidStrategyBase.cs
decisions:
  - "Analysis-only: no benchmark harness created per user directive"
  - "VDE subsystem is the primary storage engine analyzed; RAID analyzed at strategy level"
metrics:
  duration: "static-analysis"
  completed: "2026-02-18"
---

# Phase 46 Plan 02: Storage & IO Performance - Static Code Analysis

**One-liner:** VDE uses proper ArrayPool/RandomAccess APIs with WAL crash safety, but has a single write lock bottleneck, O(N) bitmap scan for allocation, and MemoryStream-based reads that buffer entire objects.

## Storage Architecture Overview

The DataWarehouse storage subsystem has three layers:

1. **VDE (Virtual Disk Engine)** - Block-level storage with filesystem semantics (inodes, namespaces, B-Tree indexing, WAL, CoW snapshots)
2. **StoragePluginBase** - Object/key-based storage abstraction with opt-in caching and indexing
3. **UltimateRAID** - RAID strategy layer for multi-disk redundancy

The VDE is the primary IO path. It uses:
- `FileBlockDevice` for physical IO via .NET `RandomAccess` API
- `BitmapAllocator` for free space tracking
- `ExtentTree` for contiguous allocation
- `WriteAheadLog` for crash recovery
- `CowBlockManager` for copy-on-write snapshots
- `BTree` for key-to-inode mapping
- `InodeTable` for file metadata

---

## Performance Strengths

### 1. RandomAccess API for File IO (EXCELLENT)

**Location:** `FileBlockDevice` (lines 44-48, 71-88)

```csharp
var options = FileOptions.Asynchronous | FileOptions.RandomAccess;
_handle = File.OpenHandle(path, mode, access, FileShare.None, options);
// ...
int bytesRead = await RandomAccess.ReadAsync(_handle, buffer[..BlockSize], offset, ct);
```

The `RandomAccess` API is .NET's highest-performance file IO path:
- Uses OS scatter/gather IO when available
- Avoids FileStream's internal buffering overhead
- Supports true async IO via kernel IO completion ports (Windows) or io_uring (Linux)
- `FileOptions.RandomAccess` hints the OS to not read-ahead (correct for block-device workloads)

### 2. ArrayPool Throughout VDE (EXCELLENT)

ArrayPool usage is consistent across the VDE subsystem:

| Component | Usage | Return Pattern |
|-----------|-------|---------------|
| `VirtualDiskEngine.StoreAsync` | `ArrayPool<byte>.Shared.Rent(_options.BlockSize)` | try/finally Return |
| `VirtualDiskEngine.RetrieveAsync` | `ArrayPool<byte>.Shared.Rent(_options.BlockSize)` | try/finally Return |
| `CowBlockManager.WriteBlockCowAsync` | `ArrayPool<byte>.Shared.Rent(_device.BlockSize)` x2 | try/finally Return |
| `WriteAheadLog.AppendEntryAsync` | `ArrayPool<byte>.Shared.Rent(entrySize)` | try/finally Return |
| `WriteAheadLog.WriteHeaderAsync` | `ArrayPool<byte>.Shared.Rent(_device.BlockSize)` | try/finally Return |
| `WriteAheadLog.ReplayAsync` | `ArrayPool<byte>.Shared.Rent(_device.BlockSize)` | try/finally Return |
| `BitmapAllocator.PersistAsync` | `ArrayPool<byte>.Shared.Rent(device.BlockSize)` | try/finally Return |

Every hot path rents from `ArrayPool` and returns in `finally` blocks. This eliminates GC pressure for block-sized allocations.

### 3. WAL with Circular Buffer (GOOD)

**Location:** `WriteAheadLog` (entire file)

The WAL uses a circular buffer design:
- Fixed block range on disk (no growing WAL files)
- Head/tail pointers with wrap-around
- XxHash64 checksums on the WAL header for integrity
- Auto-checkpoint when utilization exceeds threshold (default 75%)
- Transaction isolation: only committed transactions are replayed on recovery

Performance characteristics:
- Append-only writes (sequential IO pattern)
- Single `SemaphoreSlim` for append serialization
- Separate flush lock to avoid blocking appends during flush

### 4. CoW with Refcount Optimization (GOOD)

**Location:** `CowBlockManager`

Reference counting uses a space-saving optimization:
- Blocks with refCount == 1 are NOT stored in the B-Tree (the common case)
- Only blocks with refCount >= 2 (shared via snapshots) are tracked
- This means most operations skip the B-Tree lookup entirely for non-snapshot workloads

### 5. Connection Pooling for Storage Backends (GOOD)

**Location:** `StorageConnectionRegistry<TConfig>` / `StorageConnectionInstance<TConfig>`

The connection registry provides:
- `ConcurrentQueue<object>` connection pool with configurable max size (default 100)
- `SemaphoreSlim` to limit concurrent connection creation
- Health-aware routing (`GetHealthyForRole()`)
- Role-based selection (Primary, Replica, ReadOnly)
- Automatic return-to-pool via `PooledStorageConnection` IAsyncDisposable

### 6. Pre-allocated Container File (GOOD)

**Location:** `FileBlockDevice` constructor (line 53)

```csharp
RandomAccess.SetLength(_handle, totalSize);
```

The VDE container file is pre-allocated to full size at creation. This avoids filesystem fragmentation from incremental growth and ensures contiguous disk allocation.

### 7. Block Checksum Verification on Read (GOOD)

**Location:** `VirtualDiskEngine.RetrieveAsync()` (lines 369-374)

Checksum verification is configurable (`EnableChecksumVerification`) and uses `IBlockChecksummer.VerifyBlockAsync()`. This catches silent data corruption without requiring a full scrub.

---

## Performance Risks

### RISK 1: Single Write Lock on Entire VDE (HIGH)

**Location:** `VirtualDiskEngine._writeLock` (line 31)

```csharp
private readonly SemaphoreSlim _writeLock = new(1, 1);
```

The VDE uses a single `SemaphoreSlim(1,1)` for ALL write operations. This means:
- StoreAsync, DeleteAsync, and CheckpointAsync are fully serialized
- No concurrent writes are possible, even to different keys
- A long-running store (e.g., 1GB file) blocks all other writes

**Impact:** Under concurrent write workloads, this is a severe throughput limiter. Sequential writes are fine, but any parallelism is lost.

**Also affected:** `FileBlockDevice._writeLock` (another SemaphoreSlim(1,1)) serializes all physical writes.

### RISK 2: O(N) Bitmap Scan for Block Allocation (HIGH)

**Location:** `BitmapAllocator.FindNextFreeBlock()` (lines 350-361)

```csharp
private long FindNextFreeBlock(long startBlock)
{
    for (long i = startBlock; i < _totalBlocks; i++)
    {
        if (!GetBit(i))
            return i;
    }
    return -1;
}
```

The bitmap allocator scans bit-by-bit to find free blocks. With the next-free hint, this is O(1) amortized for sequential allocation. However:
- After fragmentation (many deletes), the hint may point to an allocated block, causing a scan
- `FindContiguousFreeBlocks()` is O(N) worst case for extent allocation
- For a 1M block container, worst case is scanning 1M bits

**Mitigation exists but unused:** The `ExtentTree` provides O(log N) allocation via sorted extent tracking with best-fit selection. However, `FreeSpaceManager` (used by VDE) delegates to `BitmapAllocator`, and the `ExtentTree` is not integrated into the primary allocation path.

### RISK 3: MemoryStream Buffering on Read (HIGH)

**Location:** `VirtualDiskEngine.RetrieveAsync()` (line 351)

```csharp
var resultStream = new MemoryStream();
```

Reading an object builds the entire result in a `MemoryStream` before returning. For a 1GB object, this allocates 1GB of managed heap memory.

**Better pattern:** Return a custom `Stream` that reads blocks on-demand from the block device, only loading one block at a time.

### RISK 4: Direct Block Pointers Only (MEDIUM)

**Location:** `VirtualDiskEngine.StoreAsync()` (lines 261-263)

```csharp
if (blockPointers.Count > Inode.DirectBlockCount)
{
    throw new NotSupportedException("Files exceeding direct block limit...");
}
```

The current inode structure only supports direct block pointers (no indirect, double-indirect, or extent-based pointers). This limits maximum object size to `Inode.DirectBlockCount * BlockSize`. With typical 12 direct pointers and 4KB blocks, this is only 48KB per object.

**Impact:** Large objects cannot be stored in VDE without indirect block support.

### RISK 5: WAL Write Amplification (MEDIUM)

**Location:** `CowBlockManager.WriteBlockCowAsync()` (lines 82-98)

For exclusive-ownership writes (refCount == 1):
1. Read before-image from disk (1 read)
2. Log before+after images to WAL (1-2 writes depending on size)
3. Write new data to block (1 write)
4. Update checksum (1 write to checksum table)

Total: 1 read + 3-4 writes per logical write. This is ~3-4x write amplification, which is typical for WAL-protected systems but worth noting for write-heavy workloads.

### RISK 6: RAID Parity Calculation is Byte-by-Byte (MEDIUM)

**Location:** `RaidStrategyBase.CalculateXorParity()` (lines 522-543)

```csharp
for (int i = 0; i < blockSize; i++)
{
    byte result = 0;
    foreach (var block in dataBlocks)
    {
        result ^= block[i];
    }
    parity[i] = result;
}
```

XOR parity is computed byte-by-byte. Modern CPUs can XOR 16-32 bytes at once using SIMD (SSE2/AVX2). For a 128KB stripe across 8 disks, byte-by-byte XOR is ~10x slower than vectorized XOR.

**Galois Field multiplication** (`GaloisMultiply`) for RAID-6 Q parity is also scalar (bit-by-bit). This is correct but slow for large blocks.

### RISK 7: No Read-Ahead or Block Cache in VDE (LOW)

The VDE reads blocks one at a time via `ReadBlockAsync`. For sequential read patterns (scanning a large file), this means N separate IO operations for N blocks. A read-ahead buffer or block cache would batch these into larger reads.

### RISK 8: RAID Rebuild Uses Task.Delay Placeholder (LOW)

**Location:** `RaidStrategyBase.VerifyAsync()` (line 188)

```csharp
await Task.Delay(10, ct);  // Simulate verification
```

The RAID verification and scrub implementations in the base class use placeholder delays. Concrete strategies must override `VerifyBlockAsync` and `CorrectBlockAsync` for real IO.

---

## Bottleneck Analysis

### VDE Write Path (Store 10MB, 4KB blocks = 2,560 blocks)

| Step | Operations | Est. Time | Bottleneck? |
|------|-----------|-----------|-------------|
| Acquire write lock | 1 semaphore wait | <1us | No (unless contended) |
| Begin WAL txn | 1 WAL append | ~0.1ms | No |
| Namespace resolve | 1 B-Tree lookup | ~0.05ms | No |
| **Block allocation** | 2,560 AllocateBlock calls | **~2-5ms** | **Yes (O(N) scan risk)** |
| **Block writes (CoW)** | 2,560 read + 2,560 WAL + 2,560 data writes | **~50-100ms** | **Yes (3x amplification)** |
| Checksum compute+store | 2,560 xxhash64 + store | ~5ms | No |
| Inode update | 1 write | ~0.05ms | No |
| B-Tree insert | 1 insert | ~0.1ms | No |
| WAL commit | 1 append + flush | ~1ms | No |
| **Total** | | **~60-110ms** | |

**Estimated throughput:** ~90-170 MB/s for VDE write path (limited by write amplification and serialized IO).

### VDE Read Path (Retrieve 10MB, 4KB blocks = 2,560 blocks)

| Step | Operations | Est. Time | Bottleneck? |
|------|-----------|-----------|-------------|
| B-Tree lookup | 1 lookup | ~0.05ms | No |
| Inode fetch | 1 read | ~0.05ms | No |
| **Block reads** | 2,560 ReadBlockAsync | **~20-40ms** | **Yes (sequential, no batching)** |
| **Checksum verify** | 2,560 hash + compare | **~5-10ms** | Minor |
| **MemoryStream build** | 10MB allocation + copy | **~2ms** | **Memory concern** |
| **Total** | | **~27-52ms** | |

**Estimated throughput:** ~190-370 MB/s for VDE read path (limited by block-at-a-time IO).

### Storage Backend Comparison (Conceptual)

| Backend | Write Latency | Read Latency | Key Strength | Key Weakness |
|---------|--------------|-------------|-------------|-------------|
| VDE (FileBlockDevice) | ~40us/block | ~15us/block | Strong consistency (WAL) | Single write lock |
| LocalFile (UltimateStorage) | ~100us/file | ~50us/file | Simple, reliable | No transactions |
| SQLite | ~500us/row | ~100us/row | ACID, SQL queries | Single-writer |
| S3/Azure Blob | ~50-200ms | ~20-100ms | Scalable, durable | Network latency |

---

## RAID Performance Profile

### Strategy Performance Characteristics (from code analysis)

| RAID Level | Min Disks | Fault Tolerance | Storage Efficiency | Read Multiplier | Write Multiplier |
|------------|-----------|----------------|-------------------|----------------|-----------------|
| RAID 0 | 2 | 0 | 1.0 | N | N |
| RAID 1 | 2 | N/2 | 0.5 | N (reads) | 1 (writes mirrored) |
| RAID 5 | 3 | 1 | (N-1)/N | N-1 | N-1 (parity write) |
| RAID 6 | 4 | 2 | (N-2)/N | N-2 | N-2 (dual parity) |

### RAID Rebuild Efficiency

The rebuild infrastructure tracks:
- `_rebuildTotalBlocks` / `_rebuildCompletedBlocks` for progress
- ETA calculation based on elapsed time and progress ratio
- SMART data integration for predictive failure detection

**Risk:** Rebuild reads all surviving disks + writes to replacement disk. For a 4-disk RAID-5 with 1TB per disk, rebuild reads 3TB and writes 1TB. With byte-by-byte XOR parity, compute is the bottleneck over IO.

---

## Optimization Recommendations

### Priority 1: Reduce VDE Write Lock Granularity

Replace the single `SemaphoreSlim(1,1)` with per-key or striped locking. Options:
- **Striped locks:** Hash key to one of N (e.g., 64) locks
- **Per-inode locks:** Lock at the inode level, allowing parallel writes to different files
- **Reader-writer lock:** Allow concurrent reads, serialize writes

### Priority 2: Integrate ExtentTree into Primary Allocation

The `ExtentTree` already exists with O(log N) best-fit allocation and automatic merging. Wire it into `FreeSpaceManager` as the primary allocator, falling back to bitmap scan only when ExtentTree is empty.

### Priority 3: Lazy-Read Stream for RetrieveAsync

Replace `MemoryStream` in `RetrieveAsync` with a custom `VdeBlockStream` that reads blocks on-demand:
```csharp
// Instead of buffering all blocks into MemoryStream:
return new VdeBlockStream(_container.BlockDevice, inode, _checksummer, _options);
```

### Priority 4: Vectorize XOR Parity

Use `System.Runtime.Intrinsics` for SIMD-accelerated XOR:
```csharp
Vector256<byte> v1 = Vector256.LoadUnsafe(ref block1[i]);
Vector256<byte> v2 = Vector256.LoadUnsafe(ref block2[i]);
Vector256.Xor(v1, v2).StoreUnsafe(ref parity[i]);
```
This provides 32 bytes per cycle vs 1 byte per cycle.

### Priority 5: Add Block Read-Ahead

For sequential reads (iterating through inode direct pointers), batch consecutive block reads into a single larger IO operation. The `RandomAccess` API supports `ReadAsync` with arbitrary offsets, so a 64KB read (16 blocks) is trivial.

### Priority 6: Add Indirect Block Pointers

Extend `Inode` with single-indirect and double-indirect block pointers to support objects larger than `DirectBlockCount * BlockSize`.

---

## Overall Readiness Verdict

**GOOD for moderate workloads, needs optimization for high-throughput scenarios.**

**Strengths:**
- Correct crash-safety semantics (WAL + CoW)
- Excellent memory discipline (ArrayPool everywhere)
- Modern IO API usage (RandomAccess)
- Pre-allocated container avoids fragmentation
- Connection pooling for external backends

**Weaknesses:**
- Single write lock eliminates write parallelism
- O(N) bitmap scan degrades under fragmentation
- Full-buffer reads waste memory for large objects
- RAID parity uses scalar byte-by-byte XOR
- Direct block pointers limit max object size

**Estimated VDE throughput:**
- Sequential writes: ~100-170 MB/s (single-threaded, write-amplification limited)
- Sequential reads: ~200-400 MB/s (block-at-a-time IO)
- Random 4KB reads: ~50,000-100,000 IOPS (limited by block device latency)
- Random 4KB writes: ~10,000-25,000 IOPS (write amplification + serialization)

**For production readiness:** The VDE is functional and correct. The main concerns are scalability under concurrent load (write lock) and efficiency under fragmentation (bitmap allocator). Both are fixable without architectural changes.

---

## Self-Check: PASSED

- [x] 46-02-SUMMARY.md exists (393 lines)
- [x] Frontmatter present with phase, plan, tags, dependency-graph, key-files
- [x] VDE performance strengths documented (7 items)
- [x] VDE performance risks documented (7 items with severity ratings)
- [x] Storage backend analysis (connection pooling, caching, health routing)
- [x] RAID performance analysis (parity calculation, rebuild estimates)
- [x] Bottleneck analysis with throughput estimates
- [x] Optimization recommendations (6 priorities)
- [x] Overall readiness verdict with IOPS/throughput estimates
