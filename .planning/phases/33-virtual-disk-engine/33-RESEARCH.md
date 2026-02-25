# Phase 33: Virtual Disk Engine - Research

**Researched:** 2026-02-16
**Domain:** Block-level storage engine, filesystem internals, on-disk data structures
**Confidence:** HIGH

## Summary

Phase 33 builds a complete virtual disk engine (VDE) within the DataWarehouse SDK -- a real filesystem-like storage layer with block allocation, metadata management, write-ahead logging, crash recovery, B-Tree indexes, copy-on-write semantics, and integrity verification. This is the most architecturally complex phase in v3.0, requiring deep understanding of filesystem internals.

The existing codebase has a complete UltimateFilesystem plugin with 14 concrete strategies (detection, drivers, caching, container, quota, overlay) and a WinFsp/FUSE driver pair, but **all existing code operates on top of OS filesystems** -- none implements a custom block-level storage engine. The `ContainerPackedStrategy` is a pure stub with a comment `// In production, would read from container format`. The VDE is entirely new construction that will live in the SDK as internal engine types, with a single integration point as a new `IStorageStrategy` backend registered in UltimateStorage.

**Primary recommendation:** Build VDE as a new SDK namespace (`DataWarehouse.SDK.VirtualDiskEngine`) with clean subsystem interfaces, using the existing `System.IO.Hashing` package for checksums and following the established `IStorageStrategy`/`StorageStrategyBase` pattern for the integration backend. Each subsystem should be independently testable with in-memory backing for unit tests.

## Existing Codebase Assessment

### UltimateFilesystem Plugin (Status: REAL, but irrelevant to VDE)

| File | LOC (approx) | Status | Relevance to VDE |
|------|------|--------|-------------------|
| `UltimateFilesystemPlugin.cs` | 547 | Real plugin with message handlers, strategy registry, statistics | LOW -- operates on OS filesystems, not block-level |
| `FilesystemStrategyBase.cs` | 259 | Real base class + IFilesystemStrategy interface + registry | LOW -- detection/driver interface, not storage engine |
| `Strategies/DetectionStrategies.cs` | 389 | Real detection (NTFS/ext4/btrfs/ZFS/APFS) using DriveInfo | NONE -- OS filesystem detection |
| `Strategies/DriverStrategies.cs` | 288 | Real drivers (POSIX/DirectIO/io_uring/mmap/Windows/Async) | LOW -- I/O driver patterns may inform block I/O layer |
| `Strategies/SpecializedStrategies.cs` | 335 | ContainerPacked (STUB), OverlayFs, BlockCache, QuotaEnforcement, XFS, ReFS | MEDIUM -- ContainerPacked is the natural target for VDE |

**Key finding:** `ContainerPackedStrategy` (line 7-46 in SpecializedStrategies.cs) is a pure stub. Its `ReadBlockAsync` and `WriteBlockAsync` are plain `FileStream` operations with no container logic. The comment on line 28 reads `// In production, would read from container format`. This is precisely what VDE implements.

### WinFsp/FUSE Drivers (Status: REAL, significant)

| Component | Status | Relevance |
|-----------|--------|-----------|
| WinFspDriver (16 files) | Real P/Invoke-based WinFsp implementation with file operations, security, caching, VSS, BitLocker, Shell extension | MEDIUM -- demonstrates file-level semantics the VDE must support |
| FuseDriver (14 files) | Real FUSE implementation with POSIX permissions, xattrs, overlayfs, systemd | MEDIUM -- demonstrates POSIX semantics the VDE must map to |

**Insight:** Both drivers maintain in-memory `ConcurrentDictionary<string, FileEntry>` for file metadata and `ConcurrentDictionary<string, byte[]>` for data. The VDE replaces these in-memory stores with persistent on-disk structures (inode table + block allocation).

### SDK Storage Infrastructure (Status: REAL, critical integration target)

| Component | Path | Status | Integration Impact |
|-----------|------|--------|-------------------|
| `IObjectStorageCore` | `SDK/Storage/IObjectStorageCore.cs` | Real interface (8 methods) | VDE implements this interface |
| `PathStorageAdapter` | `SDK/Storage/PathStorageAdapter.cs` | Real URI-to-key translator | VDE is accessed through this adapter |
| `StoragePluginBase` | `SDK/Contracts/Hierarchy/DataPipeline/StoragePluginBase.cs` | Real abstract base | VDE plugin inherits this |
| `IStorageStrategy` | `SDK/Contracts/Storage/StorageStrategy.cs` | Real interface (10 methods) | VDE registers as one of these |
| `StorageStrategyBase` | `SDK/Contracts/Storage/StorageStrategy.cs` | Real base class with retry/metrics | VDE strategy inherits this |

**Registration pattern:** UltimateStorage uses a `StorageStrategyRegistry` (ConcurrentDictionary of `IStorageStrategy` by `strategyId`). Strategies are auto-discovered via assembly scanning. The VDE backend will be discovered automatically if placed in the same or a scanned assembly.

### Existing Tree/Index Structures

| Location | Structure | Relevance |
|----------|-----------|-----------|
| `SDK/Infrastructure/Distributed/LoadBalancing/ConsistentHashRing.cs` | Hash ring using XxHash32 | LOW -- different use case, but confirms XxHash usage pattern |
| `Plugins/UltimateDataManagement/Strategies/Indexing/CompositeIndexStrategy.cs` | In-memory composite index | LOW -- application-level indexing, not disk-level |
| `Plugins/UltimateDataManagement/Strategies/Indexing/TemporalIndexStrategy.cs` | Time-based index | NONE -- unrelated |
| `Plugins/UltimateStorageProcessing/Strategies/Data/IndexBuildingStrategy.cs` | Index building | LOW -- strategy-level, not disk structure |

**Key finding:** No existing B-Tree or on-disk tree structure exists in the codebase. The VDE B-Tree must be built from scratch.

### Existing Checksum/Hashing Infrastructure

| Component | Detail | Relevance |
|-----------|--------|-----------|
| `System.IO.Hashing` (NuGet 10.0.3) | Already in SDK.csproj, used by ConsistentHashRing | HIGH -- XxHash32/64/128/3 and Crc32 available |
| `SHA256.HashData` in CryptographicAlgorithmRegistry | Crypto-grade hashing | LOW -- too slow for per-block checksumming |
| XxHash32 usage in ConsistentHashRing | `System.IO.Hashing.XxHash32.HashToUInt32(bytes)` | HIGH -- exact pattern to follow |

**Key finding:** The SDK already has `System.IO.Hashing 10.0.3` as a dependency. This provides `XxHash3`, `XxHash64`, `XxHash128`, `XxHash32`, and `Crc32` -- all candidates for block-level checksumming. XxHash3 is the recommended choice (fastest, 64-bit output).

### Existing Binary Format Patterns

| Location | Pattern | Relevance |
|----------|---------|-----------|
| `SDK/Contracts/Compression/CompressionStrategy.cs` | Magic bytes for format detection | LOW -- application-level |
| `Plugins/SelfEmulatingObjects/SelfEmulatingObjectsPlugin.cs` | Magic number pattern | LOW -- different domain |
| `SDK/Contracts/ActiveStoragePluginBases.cs` | Magic number usage | LOW |

**Key finding:** No existing binary container file format with superblock/versioning exists. The DWVD container format is entirely new.

## Architecture Patterns

### Recommended Namespace Structure

```
DataWarehouse.SDK/
├── VirtualDiskEngine/
│   ├── BlockAllocation/
│   │   ├── IBlockAllocator.cs          # Interface for block allocation
│   │   ├── BitmapAllocator.cs          # Bitmap-based free space tracking
│   │   ├── ExtentTree.cs              # Extent-based allocation for contiguous regions
│   │   └── FreeSpaceManager.cs        # Coordinates bitmap + extent tree
│   ├── Metadata/
│   │   ├── IInodeTable.cs             # Interface for inode operations
│   │   ├── InodeTable.cs              # Inode storage and lookup
│   │   ├── DirectoryEntry.cs          # Directory entry structure
│   │   ├── InodeStructure.cs          # Inode data structure (permissions, timestamps, etc.)
│   │   └── NamespaceTree.cs           # Hierarchical namespace management
│   ├── Journal/
│   │   ├── IWriteAheadLog.cs          # WAL interface
│   │   ├── WriteAheadLog.cs           # WAL implementation
│   │   ├── JournalEntry.cs            # Log entry structure
│   │   └── CheckpointManager.cs       # Checkpoint/recovery management
│   ├── Container/
│   │   ├── ContainerFormat.cs         # Superblock, magic bytes, versioning
│   │   ├── ContainerFile.cs           # Open/create/validate container files
│   │   └── FormatMigrator.cs          # Version migration support
│   ├── Index/
│   │   ├── IBTreeIndex.cs             # B-Tree interface
│   │   ├── BTree.cs                   # On-disk B-Tree implementation
│   │   ├── BTreeNode.cs              # B-Tree node structure
│   │   └── BulkLoader.cs             # Bulk loading optimization
│   ├── CopyOnWrite/
│   │   ├── ICowEngine.cs             # CoW interface
│   │   ├── CowBlockManager.cs        # CoW block reference management
│   │   ├── SnapshotManager.cs        # Snapshot creation/deletion
│   │   └── SpaceReclaimer.cs         # Reference-counted space reclamation
│   ├── Integrity/
│   │   ├── IBlockChecksummer.cs       # Checksum interface
│   │   ├── BlockChecksummer.cs        # Per-block checksum computation/verification
│   │   └── CorruptionDetector.cs      # Corruption detection and reporting
│   ├── VirtualDiskEngine.cs           # Main engine facade coordinating all subsystems
│   └── VdeStorageStrategy.cs          # IStorageStrategy implementation for UltimateStorage
```

### Pattern 1: Subsystem Interface Isolation

**What:** Each VDE subsystem (allocator, inode, WAL, B-Tree, CoW, checksum) exposes a clean interface that the VDE engine facade uses. Subsystems do not reference each other directly -- the engine coordinates them.

**When to use:** Always. This enables independent unit testing, future replacement, and clear dependency flow.

**Example:**
```csharp
// Source: Design pattern for VDE
public interface IBlockAllocator
{
    long AllocateBlock(CancellationToken ct = default);
    long[] AllocateExtent(int blockCount, CancellationToken ct = default);
    void FreeBlock(long blockNumber, CancellationToken ct = default);
    void FreeExtent(long startBlock, int blockCount, CancellationToken ct = default);
    long FreeBlockCount { get; }
    long TotalBlockCount { get; }
}
```

### Pattern 2: Block I/O Layer

**What:** All on-disk operations go through a single `IBlockDevice` abstraction that reads/writes fixed-size blocks by block number. This decouples the storage engine from the OS file system.

**When to use:** Every subsystem that reads/writes disk.

**Example:**
```csharp
public interface IBlockDevice : IDisposable
{
    int BlockSize { get; }
    long BlockCount { get; }
    Task ReadBlockAsync(long blockNumber, Memory<byte> buffer, CancellationToken ct = default);
    Task WriteBlockAsync(long blockNumber, ReadOnlyMemory<byte> data, CancellationToken ct = default);
    Task FlushAsync(CancellationToken ct = default);
}

// File-backed implementation
public sealed class FileBlockDevice : IBlockDevice
{
    private readonly FileStream _stream;
    private readonly int _blockSize;
    // ...
}
```

### Pattern 3: Container File as IBlockDevice

**What:** The DWVD container file IS the block device. Opening a container creates an `IBlockDevice` backed by the container file. The superblock occupies block 0.

**When to use:** Container creation and opening.

### Anti-Patterns to Avoid

- **Monolithic engine class:** Do NOT put all VDE logic in one file. Each subsystem is independently complex enough to warrant its own classes. The engine facade coordinates them.
- **String-based block addressing:** Block numbers must be `long`, not strings. The key-to-block mapping happens at the VDE storage strategy level, not in the engine.
- **In-memory-only data structures:** All data structures (bitmap, inode table, B-Tree nodes, WAL entries) MUST be persistable to disk blocks. No `ConcurrentDictionary` as primary storage.
- **Ignoring endianness:** All on-disk formats must use little-endian byte order (standard for modern architectures). Use `BinaryPrimitives.WriteInt64LittleEndian` and similar.
- **Skipping fsync/flush:** After WAL writes and checkpoints, `FlushAsync` must be called on the underlying file. Data integrity depends on this.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Block checksumming | Custom hash function | `System.IO.Hashing.XxHash3` (already in SDK) | XxHash3 is 3x faster than CRC32, built-in, well-tested |
| Binary serialization of structures | Custom byte packing | `BinaryPrimitives` + `MemoryMarshal` (.NET BCL) | Correct endianness, no allocation, span-based |
| Thread-safe concurrent access | Lock-based synchronization | `ReaderWriterLockSlim` + `SemaphoreSlim` (.NET BCL) | Proven patterns, used throughout existing codebase |
| Storage strategy registration | Custom discovery | Inherit `StorageStrategyBase`, auto-discovered by `StorageStrategyRegistry` | Existing pattern in UltimateStorage |
| Configuration/options | Custom config parsing | Records with `init` setters (C# pattern used throughout SDK) | Consistent with SDK style |

**Key insight:** The VDE is fundamentally about data structure design on disk. The implementation is pure C# with no external dependencies beyond what the SDK already has. The complexity is algorithmic (B-Tree balancing, CoW reference counting, WAL replay ordering), not dependency management.

## Common Pitfalls

### Pitfall 1: WAL Write Ordering
**What goes wrong:** WAL entries written to disk out of order, causing incorrect replay sequence after crash.
**Why it happens:** Async I/O can reorder writes. OS page cache can flush pages in any order.
**How to avoid:** Use sequential log with monotonic sequence numbers. Flush WAL entries before writing data blocks. Use `FileOptions.WriteThrough` or explicit `Flush` after each WAL write group.
**Warning signs:** Tests pass but crash recovery produces inconsistent state.

### Pitfall 2: CoW Reference Count Leaks
**What goes wrong:** Blocks are never freed because reference count never reaches zero, or blocks are freed while still referenced.
**Why it happens:** Reference counting is notoriously hard. Off-by-one errors in snapshot creation/deletion.
**How to avoid:** Single point of truth for reference counts. Always wrap increment/decrement in atomic operations. Test with many snapshots created and deleted in various orders.
**Warning signs:** Free space shrinks monotonically despite deletes. Use-after-free when accessing snapshotted data.

### Pitfall 3: B-Tree Node Split During Write
**What goes wrong:** B-Tree node split updates parent pointer, but crash occurs between child split and parent update, leaving orphaned nodes.
**Why it happens:** B-Tree split is a multi-block operation that is not atomic at the block level.
**How to avoid:** WAL-protect all B-Tree modifications. Log the entire split operation (new child, new key in parent, pointer update) as a single WAL transaction. On replay, either all changes apply or none.
**Warning signs:** B-Tree traversal finds missing keys or reaches dead-end nodes after crash recovery.

### Pitfall 4: Superblock Corruption = Total Data Loss
**What goes wrong:** Single superblock corruption makes the entire container unreadable.
**Why it happens:** Superblock is written during format/checkpoint. Crash during write corrupts it.
**How to avoid:** Maintain two copies of the superblock (block 0 and a mirror at a fixed offset). Write the mirror first, then the primary. On open, if primary is corrupt, use mirror. This is how ext4, btrfs, and ZFS all handle it.
**Warning signs:** Container open fails with "invalid magic bytes" after abnormal shutdown.

### Pitfall 5: Block Size Alignment
**What goes wrong:** Data structures span block boundaries, causing partial reads and correctness issues.
**Why it happens:** Structure sizes not aligned to block size, or variable-length data packed without padding.
**How to avoid:** Every on-disk structure is designed to fit within a block or span a known number of complete blocks. Directory entries, B-Tree nodes, and inode structures all pack to block boundaries.
**Warning signs:** Corruption that appears randomly depending on data size.

### Pitfall 6: Checksum Overhead Exceeding Target
**What goes wrong:** Checksumming every block on read adds >5% overhead (VDE-07 requirement: <5%).
**Why it happens:** Using SHA256 instead of XxHash, or computing checksum on every small read.
**How to avoid:** Use XxHash3 (throughput >10 GB/s on modern CPUs). Cache verified blocks so re-reads skip checksum. Only verify on initial read from disk.
**Warning signs:** Benchmark shows read throughput significantly lower than raw FileStream.

## Code Examples

### Block Device File Implementation
```csharp
// Pattern: File-backed block device
public sealed class FileBlockDevice : IBlockDevice
{
    private readonly FileStream _stream;
    private readonly int _blockSize;
    private readonly long _blockCount;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public FileBlockDevice(string path, int blockSize, long blockCount)
    {
        _blockSize = blockSize;
        _blockCount = blockCount;
        _stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.None, blockSize, FileOptions.Asynchronous | FileOptions.RandomAccess);

        // Pre-allocate file to full size
        if (_stream.Length < (long)blockSize * blockCount)
            _stream.SetLength((long)blockSize * blockCount);
    }

    public int BlockSize => _blockSize;
    public long BlockCount => _blockCount;

    public async Task ReadBlockAsync(long blockNumber, Memory<byte> buffer, CancellationToken ct)
    {
        var offset = blockNumber * _blockSize;
        await RandomAccess.ReadAsync(_stream.SafeFileHandle, buffer[.._blockSize], offset, ct);
    }

    public async Task WriteBlockAsync(long blockNumber, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var offset = blockNumber * _blockSize;
        await _writeLock.WaitAsync(ct);
        try
        {
            await RandomAccess.WriteAsync(_stream.SafeFileHandle, data[.._blockSize], offset, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        await _stream.FlushAsync(ct);
    }
}
```

### Superblock Structure
```csharp
// Pattern: On-disk superblock layout
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Superblock
{
    // Magic: "DWVD" = 0x44_57_56_44
    public uint Magic;
    public ushort FormatVersion;
    public ushort Flags;
    public int BlockSize;
    public long TotalBlocks;
    public long FreeBlocks;
    public long InodeTableBlock;    // Block number of inode table start
    public long BitmapBlock;        // Block number of free space bitmap start
    public long BTreeRootBlock;     // Block number of B-Tree root
    public long WalStartBlock;      // Block number of WAL area start
    public long WalEndBlock;        // Block number of WAL area end
    public long CreatedTimestamp;    // Unix epoch ticks
    public long ModifiedTimestamp;
    public long CheckpointTimestamp;
    public ulong Checksum;          // XxHash64 of this struct (excluding checksum field)
    // Padding to fill a full block
}
```

### XxHash Block Checksumming
```csharp
// Source: System.IO.Hashing API (already in SDK via NuGet)
using System.IO.Hashing;

public static class BlockChecksum
{
    public static ulong Compute(ReadOnlySpan<byte> blockData)
    {
        return XxHash3.HashToUInt64(blockData);
    }

    public static bool Verify(ReadOnlySpan<byte> blockData, ulong expectedChecksum)
    {
        return XxHash3.HashToUInt64(blockData) == expectedChecksum;
    }
}
```

### B-Tree Node Layout
```csharp
// Pattern: Fixed-size B-Tree node that fits in one block
public struct BTreeNodeHeader
{
    public ushort KeyCount;         // Number of keys in this node
    public ushort Flags;            // Leaf=0x01, Root=0x02
    public long ParentBlock;        // Block number of parent node (-1 for root)
    public long NextLeafBlock;      // Block number of next leaf (for range queries)
    public long PrevLeafBlock;      // Block number of previous leaf
}
// Keys and child pointers follow header in the block:
// [Header][Key0][ChildPtr0][Key1][ChildPtr1]...[KeyN][ChildPtrN][ChildPtrN+1]
// For a 4096-byte block with 128-byte keys and 8-byte pointers:
// MaxKeys = (4096 - sizeof(Header)) / (128 + 8) - 1 = ~29 keys per node
```

### IStorageStrategy Registration
```csharp
// Pattern: VDE registers as UltimateStorage backend
// Source: Existing pattern from UltimateStorage/StorageStrategyRegistry.cs
public sealed class VdeStorageStrategy : StorageStrategyBase
{
    private VirtualDiskEngine? _engine;

    public override string StrategyId => "vde-container";
    public override string Name => "Virtual Disk Engine";
    public override StorageTier Tier => StorageTier.Hot;
    public override StorageCapabilities Capabilities => new()
    {
        SupportsVersioning = true,  // via snapshots
        SupportsMetadata = true,
        SupportsLocking = true,
        SupportsTiering = false,
        SupportsEncryption = false, // handled by pipeline
        SupportsCompression = false,
        SupportsStreaming = true,
        SupportsMultipart = false,
        ConsistencyModel = ConsistencyModel.Strong
    };

    protected override async Task<StorageObjectMetadata> StoreAsyncCore(
        string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
    {
        // Translate key to inode, allocate blocks, write data, update metadata
        // ...
    }
    // ... other methods
}
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| CRC32 block checksums | xxHash3 (non-cryptographic, ~30 GB/s) | ~2020 | 10x faster checksumming, <1% overhead on NVMe |
| Journaling (ext3/ext4) | CoW + WAL hybrid (btrfs, ZFS model) | ~2015 | Simpler crash recovery, atomic snapshots |
| Fixed inode count | Dynamic inode allocation | ~2010 (ext4 flex_bg) | No "out of inodes" failures |
| Single superblock | Dual superblock with mirror | Standard practice | Survives superblock corruption |
| Separate bitmap per allocation group | Single bitmap + extent tree | Modern filesystems | Better large allocation performance |
| System.Security.Cryptography for all hashing | System.IO.Hashing for non-crypto | .NET 7+ | 10-100x faster for integrity checks |
| `RandomAccess` API | `RandomAccess.ReadAsync` / `WriteAsync` | .NET 6+ | Scatter-gather I/O, no seek needed, thread-safe |

**Deprecated/outdated:**
- CRC32 for block checksumming: Too slow for modern NVMe speeds. Use XxHash3.
- SHA256 for integrity: Overkill for data corruption detection. Reserve for cryptographic verification only.
- `FileStream.Seek + Read/Write`: Prefer `RandomAccess` API for random-access block I/O (no seek state, thread-safe).

## Recommended Plan Structure

### Plan 33-01: Container File Format + Block Allocation Engine

**Scope:** VDE-01 (Block Allocation) + VDE-04 (Container Format)
**Why combined:** The container format defines the physical layout that the block allocator operates within. They are inseparable -- the superblock contains allocator metadata (bitmap block, total/free blocks), and the bitmap is stored as blocks within the container.
**Includes:**
- `IBlockDevice` interface and `FileBlockDevice` implementation
- Superblock structure with DWVD magic, versioning, dual-superblock mirroring
- Container create/open/validate/close operations
- Bitmap allocator for free space tracking (O(1) single block)
- Extent tree for contiguous multi-block allocation (O(log n))
- `FreeSpaceManager` coordinating bitmap + extent tree
- Format validation on open (magic check, checksum verification)
- Format version migration skeleton (v1 only for now)

**Estimated complexity:** HIGH (foundational layer, everything depends on this)

### Plan 33-02: Inode/Metadata Management

**Scope:** VDE-02
**Depends on:** 33-01 (needs block device and allocator)
**Includes:**
- Inode data structure (permissions, timestamps, size, block pointers, xattrs)
- Inode table stored in allocated blocks
- Directory entry structure (name + inode number)
- Namespace tree (hierarchical directory traversal)
- Hard link support (multiple directory entries pointing to same inode)
- Soft link support (inode with target path stored in data blocks)
- Permission model (rwx user/group/other)
- Extended attributes (key-value pairs stored with inode)
- Inode allocation/deallocation

**Estimated complexity:** HIGH (complex data structure with many edge cases)

### Plan 33-03: Write-Ahead Log (WAL) + Crash Recovery

**Scope:** VDE-03
**Depends on:** 33-01 (needs block device)
**Includes:**
- WAL area in container (reserved block range)
- Journal entry structure (sequence number, operation type, before/after images)
- Write ordering: WAL entry committed before data block written
- Checkpoint mechanism (flush all dirty data, advance WAL tail, truncate log)
- Crash recovery replay (scan WAL from last checkpoint, replay committed entries)
- WAL space reclamation (circular buffer or compaction)
- Transaction grouping (multiple operations as atomic unit)
- Performance: sequential writes within 10% of raw block speed

**Estimated complexity:** HIGH (crash recovery correctness is critical)

### Plan 33-04: B-Tree On-Disk Index

**Scope:** VDE-05
**Depends on:** 33-01 (needs block device and allocator), 33-03 (WAL-protected modifications)
**Includes:**
- B-Tree node structure fitting in one block (configurable order based on block size)
- Node split and merge operations
- O(log n) lookup, insert, delete
- Range queries (in-order leaf traversal via next-leaf pointers)
- Bulk loading (bottom-up construction for initial population)
- Concurrent readers with single writer (reader-writer lock per node)
- WAL integration (all structural modifications are journaled)
- Variable-length keys with prefix compression

**Estimated complexity:** HIGH (correct B-Tree implementation is notoriously subtle)

### Plan 33-05: Copy-on-Write Engine + Snapshots

**Scope:** VDE-06
**Depends on:** 33-01 (block allocator for new blocks), 33-02 (inode references), 33-04 (B-Tree for ref counts)
**Includes:**
- Block reference counting (stored in B-Tree or dedicated structure)
- CoW write path: read-copy-modify instead of in-place update
- Snapshot creation: clone root inode, increment all block reference counts
- Snapshot as read-only view (writes to snapshot return error)
- Clone operation: snapshot + allow writes (new blocks on modification)
- Space reclamation: free blocks when reference count drops to zero
- Snapshot listing and metadata
- Snapshot deletion with cascading deref

**Estimated complexity:** HIGH (reference counting + concurrent access is the hardest subsystem)

### Plan 33-06: Block-Level Checksumming

**Scope:** VDE-07
**Depends on:** 33-01 (block device)
**Includes:**
- Per-block checksum stored in dedicated checksum blocks (not inline with data)
- XxHash3 for non-cryptographic integrity (already in SDK via System.IO.Hashing)
- Read-time verification: every block read verifies checksum before returning data
- Corruption detection and reporting via health monitoring
- Checksum cache for recently verified blocks (avoid re-verification on re-read)
- Performance target: <5% overhead on sequential reads
- Batch verification for integrity scans
- Integration with VDE health reporting

**Estimated complexity:** MEDIUM (algorithmically simple, performance tuning needed)

### Plan 33-07: VDE Integration as Storage Backend

**Scope:** VDE-08
**Depends on:** All prior plans (33-01 through 33-06)
**Includes:**
- `VdeStorageStrategy : StorageStrategyBase` implementing all IStorageStrategy methods
- Key-to-inode mapping (key = path in VDE namespace)
- Stream-based store/retrieve using VDE block allocation and inode management
- Container lifecycle management (create on first use, open on subsequent)
- Registration in StorageStrategyRegistry (auto-discovered)
- StorageAddress integration (`StorageAddress.FromContainer("path/to/file.dwvd")`)
- Health reporting (free space, checksum errors, WAL size)
- Performance benchmarks against baseline FileStream

**Estimated complexity:** MEDIUM (integration/plumbing, not new algorithms)

## Wave Ordering and Dependencies

```
Wave 1 (Foundation): 33-01 (Container + Block Allocation)
    |
    v
Wave 2 (Core, parallel): 33-02 (Inode/Metadata) || 33-03 (WAL) || 33-06 (Checksumming)
    |                          |
    +-------+------------------+
            |
            v
Wave 3 (Index): 33-04 (B-Tree)
    |
    v
Wave 4 (CoW): 33-05 (Copy-on-Write + Snapshots)
    |
    v
Wave 5 (Integration): 33-07 (VDE Storage Backend)
```

**Rationale:**
- **Wave 1** must complete first -- everything reads/writes blocks and needs allocation.
- **Wave 2** can run in parallel: Inode management uses the block device but doesn't need WAL yet (WAL integration is added later). Checksumming is independent. WAL needs only the block device.
- **Wave 3** (B-Tree) needs block allocation (Wave 1) and should be WAL-protected (Wave 2).
- **Wave 4** (CoW) needs all prior subsystems: block allocator for new blocks, inodes for snapshot metadata, B-Tree for reference counts, WAL for atomicity.
- **Wave 5** wires everything together into the storage strategy interface.

**Strict sequential dependency chain:**
33-01 --> [33-02, 33-03, 33-06] --> 33-04 --> 33-05 --> 33-07

## Risk Assessment

### Overall Risk: HIGH

This is the most complex phase in v3.0. The VDE is essentially building a filesystem from scratch. Key risks:

| Risk | Severity | Likelihood | Mitigation |
|------|----------|------------|------------|
| Crash recovery bugs in WAL replay | CRITICAL | HIGH | Extensive crash simulation testing; dual superblock; conservative WAL flush policy |
| B-Tree corruption from split/merge bugs | CRITICAL | MEDIUM | WAL-protect all structural modifications; extensive property-based testing |
| CoW reference count leaks | HIGH | HIGH | Single ownership model; audit all increment/decrement paths; add ref-count verification tool |
| Block checksum overhead >5% | MEDIUM | LOW | XxHash3 is extremely fast (>10 GB/s); use checksum cache; lazy verification |
| Integration breaks existing 130+ storage strategies | HIGH | LOW | VDE is additive (new strategy, doesn't modify existing); full build verification |
| Container file format lock-in | MEDIUM | MEDIUM | Version field in superblock; design migration path from v1; keep format simple |
| Performance regression from too many layers | MEDIUM | MEDIUM | Profile early; use ArrayPool for buffers; minimize copies; benchmark against raw FileStream |

### Complexity Budget

This phase has 8 plans spanning ~4,000-6,000 lines of new code (estimated). Each subsystem is individually complex:
- Block allocator: ~500 LOC
- Inode/metadata: ~800 LOC
- WAL: ~600 LOC
- Container format: ~400 LOC
- B-Tree: ~800 LOC
- CoW engine: ~700 LOC
- Checksumming: ~300 LOC
- Integration strategy: ~500 LOC

Total estimated: ~4,600 LOC of new SDK code, all in `DataWarehouse.SDK.VirtualDiskEngine`.

## Open Questions

1. **Block size choice**
   - What we know: 4096 bytes is the standard filesystem block size and matches most physical sectors.
   - What's unclear: Whether a larger block size (8K, 16K) would be better for VDE's use case (storing potentially large objects).
   - Recommendation: Default to 4096, make configurable in container format. Superblock records the block size. All subsystems use the container's block size.

2. **WAL area sizing**
   - What we know: WAL needs to be large enough to hold a full checkpoint's worth of changes.
   - What's unclear: How large should the WAL area be relative to the container?
   - Recommendation: Default to 1% of container size, minimum 1MB, configurable at creation. Checkpoint when WAL reaches 75% capacity.

3. **B-Tree key size**
   - What we know: Keys are storage object keys (string paths like "namespace/category/name").
   - What's unclear: Maximum key length to support.
   - Recommendation: Variable-length keys with a 256-byte maximum. Store keys inline in B-Tree nodes for short keys, overflow to data blocks for keys >64 bytes.

4. **Snapshot storage limit**
   - What we know: Snapshots are zero-cost at creation (CoW).
   - What's unclear: Is there a practical limit on number of snapshots?
   - Recommendation: No hard limit, but document that each snapshot adds overhead to writes (CoW check) and deletes (reference count decrement chain). Recommend <100 snapshots for production.

## Sources

### Primary (HIGH confidence)
- **Codebase analysis** -- All 35+ source files read and analyzed directly
  - `Plugins/DataWarehouse.Plugins.UltimateFilesystem/` (5 files, 1,818 LOC total)
  - `Plugins/DataWarehouse.Plugins.WinFspDriver/` (16 files)
  - `Plugins/DataWarehouse.Plugins.FuseDriver/` (14 files)
  - `DataWarehouse.SDK/Storage/` (all files)
  - `DataWarehouse.SDK/Contracts/Storage/StorageStrategy.cs`
  - `DataWarehouse.SDK/Contracts/Hierarchy/DataPipeline/StoragePluginBase.cs`
  - `DataWarehouse.SDK/Infrastructure/Distributed/LoadBalancing/ConsistentHashRing.cs`
- **System.IO.Hashing** -- NuGet 10.0.3, already in SDK.csproj, provides XxHash3/32/64/128 and Crc32
- **.NET RandomAccess API** -- Available in .NET 6+, used for thread-safe random block I/O
- **SDK csproj** -- net10.0 target framework confirmed

### Secondary (MEDIUM confidence)
- Filesystem design patterns from ext4, btrfs, ZFS documentation (general knowledge)
- B-Tree algorithms from standard computer science literature
- WAL/crash recovery patterns from database systems (PostgreSQL, SQLite)

### Tertiary (LOW confidence)
- Performance estimates for XxHash3 throughput (>10 GB/s) -- based on published benchmarks, not verified on this specific hardware

## Metadata

**Confidence breakdown:**
- Existing code assessment: HIGH -- all files read directly, every assertion verified
- Standard stack: HIGH -- using only .NET BCL + existing SDK dependencies (System.IO.Hashing)
- Architecture: HIGH -- clean subsystem decomposition follows proven filesystem design
- Pitfalls: HIGH -- based on well-documented filesystem engineering challenges
- Performance estimates: MEDIUM -- based on published benchmarks, need validation on target hardware

**Research date:** 2026-02-16
**Valid until:** 2026-03-16 (stable domain -- filesystem engineering doesn't change fast)
