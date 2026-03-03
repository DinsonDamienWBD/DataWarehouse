---
phase: 91.5-vde-v2.1-format-completion
plan: 60
subsystem: vde-io
tags: [raw-partition, aligned-memory, direct-io, native-interop, dma, block-device]

# Dependency graph
requires:
  - phase: 91.5-vde-v2.1-format-completion
    provides: "AlignedMemoryOwner, IBatchBlockDevice, BlockDeviceOptions (87-56)"
provides:
  - "IMemoryAllocator interface for DI-injectable aligned memory allocation"
  - "AlignedMemoryAllocator for stateless one-off aligned buffer creation"
  - "AlignedMemoryPool for reusable fixed-size aligned buffer pooling"
  - "RawPartitionBlockDevice for direct partition I/O without filesystem"
  - "RawPartitionNativeMethods for cross-platform geometry detection"
  - "BlockDeviceOptions.AllowNonDwvd for explicit safety override"
affects: [vde-mount-pipeline, block-device-factory, storage-backends]

# Tech tracking
tech-stack:
  added: [NativeMemory.AlignedAlloc, ConcurrentBag, P/Invoke LibraryImport]
  patterns: [fail-closed-safety-validation, platform-dispatched-native-methods, pooled-buffer-recycling]

key-files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/IO/IMemoryAllocator.cs
    - DataWarehouse.SDK/VirtualDiskEngine/IO/AlignedMemoryAllocator.cs
    - DataWarehouse.SDK/VirtualDiskEngine/IO/AlignedMemoryPool.cs
    - DataWarehouse.SDK/VirtualDiskEngine/IO/RawPartitionBlockDevice.cs
    - DataWarehouse.SDK/VirtualDiskEngine/IO/RawPartitionNativeMethods.cs
  modified:
    - DataWarehouse.SDK/VirtualDiskEngine/IO/BlockDeviceOptions.cs

key-decisions:
  - "Fail-closed DWVD magic validation: partition must contain 0x44575644 magic or AllowNonDwvd must be explicitly true"
  - "AlignedMemoryPool uses ConcurrentBag with overflow disposal instead of pre-allocation"
  - "RawPartitionBlockDevice implements IBatchBlockDevice (not IDirectBlockDevice) per plan spec"

patterns-established:
  - "Fail-closed safety: raw device operations require explicit opt-in to bypass safety checks"
  - "Platform-dispatched native methods: single public API, OS detection at call site"
  - "Pooled buffer pattern: PooledBufferOwner wraps inner owner, returns to pool on Dispose"

# Metrics
duration: 6min
completed: 2026-03-02
---

# Phase 91.5 Plan 60: Raw Partition Block Device and Aligned Memory Infrastructure Summary

**RawPartitionBlockDevice with fail-closed DWVD magic validation, cross-platform geometry detection via P/Invoke, and IMemoryAllocator/AlignedMemoryPool for DMA-compatible buffer management**

## Performance

- **Duration:** 6 min
- **Started:** 2026-03-02T14:39:16Z
- **Completed:** 2026-03-02T14:45:08Z
- **Tasks:** 2
- **Files modified:** 6

## Accomplishments
- IMemoryAllocator interface with two implementations: stateless AlignedMemoryAllocator and pooled AlignedMemoryPool with ConcurrentBag recycling
- RawPartitionBlockDevice opens raw device paths (/dev/nvme0n1p1, \\.\PhysicalDrive0, /dev/rdisk2) with DWVD magic safety validation
- Cross-platform geometry detection via Windows DeviceIoControl, Linux ioctl (BLKSSZGET/BLKGETSIZE64), macOS ioctl (DKIOCGETBLOCKSIZE/DKIOCGETBLOCKCOUNT)
- AllowNonDwvd property on BlockDeviceOptions for explicit opt-in to bypass safety check

## Task Commits

Each task was committed atomically:

1. **Task 1: IMemoryAllocator, AlignedMemoryAllocator, and AlignedMemoryPool** - `be2a37e9` (feat)
2. **Task 2: RawPartitionBlockDevice with safety validation** - `528f85dc` (feat)

## Files Created/Modified
- `DataWarehouse.SDK/VirtualDiskEngine/IO/IMemoryAllocator.cs` - DI-injectable memory allocator interface with Alignment property
- `DataWarehouse.SDK/VirtualDiskEngine/IO/AlignedMemoryAllocator.cs` - Stateless allocator creating new AlignedMemoryOwner per call
- `DataWarehouse.SDK/VirtualDiskEngine/IO/AlignedMemoryPool.cs` - Fixed-size buffer pool with ConcurrentBag recycling and overflow disposal
- `DataWarehouse.SDK/VirtualDiskEngine/IO/RawPartitionBlockDevice.cs` - Direct partition I/O with DWVD magic safety validation
- `DataWarehouse.SDK/VirtualDiskEngine/IO/RawPartitionNativeMethods.cs` - Cross-platform P/Invoke for disk geometry detection
- `DataWarehouse.SDK/VirtualDiskEngine/IO/BlockDeviceOptions.cs` - Added AllowNonDwvd property

## Decisions Made
- Fail-closed DWVD magic validation: raw partitions must contain DWVD signature or AllowNonDwvd must be explicitly set
- AlignedMemoryPool uses ConcurrentBag with overflow disposal rather than pre-allocation to avoid upfront memory cost
- RawPartitionBlockDevice implements IBatchBlockDevice (not IDirectBlockDevice) as specified in plan
- Used NET7_0_OR_GREATER conditional compilation for LibraryImport (source-generated) vs DllImport fallback

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
None

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- All IO/ infrastructure complete: FileBlockDevice, DirectFileBlockDevice, IoUring, IoRing, WindowsOverlapped, Kqueue, SPDK, RawPartition
- IMemoryAllocator ready for DI injection across all direct I/O code paths
- BlockDeviceFactory can now wire up RawPartition implementation (enum value 7 already exists)

## Self-Check: PASSED

All 5 created files verified on disk. Both task commits (be2a37e9, 528f85dc) verified in git log.

---
*Phase: 91.5-vde-v2.1-format-completion*
*Completed: 2026-03-02*
