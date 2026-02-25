---
phase: 86-adaptive-index-engine
plan: 12
subsystem: VDE Adaptive Index Engine
tags: [io_uring, block-device, native-interop, zero-copy, linux]
dependency_graph:
  requires: [86-01]
  provides: [IoUringBindings, IoUringBlockDevice]
  affects: [VDE I/O path, block device factory]
tech_stack:
  added: [liburing P/Invoke, NativeMemory.AlignedAlloc, io_uring registered buffers]
  patterns: [ring-per-thread, ConcurrentDictionary thread rings, factory fallback, O_DIRECT]
key_files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/AdaptiveIndex/IoUringBindings.cs
    - DataWarehouse.SDK/VirtualDiskEngine/AdaptiveIndex/IoUringBlockDevice.cs
  modified: []
decisions:
  - DllImport over LibraryImport for broader compatibility with opaque struct refs
  - ConcurrentDictionary thread-ring indexing over ThreadStatic to avoid S2696 analyzer error
  - O_DIRECT for all io_uring file access to bypass page cache
  - IoUring struct 216 bytes opaque to cover liburing internal layout on x86-64
metrics:
  duration: 6min
  completed: 2026-02-23T20:51:00Z
  tasks_completed: 2
  tasks_total: 2
  files_created: 2
  files_modified: 0
---

# Phase 86 Plan 12: io_uring Native Integration Summary

P/Invoke bindings to liburing.so with registered-buffer zero-copy IBlockDevice, ring-per-thread scalability, NVMe passthrough, and graceful FileBlockDevice fallback on non-Linux.

## Task Execution

### Task 1: io_uring P/Invoke Bindings
**Commit:** `f502081e`
**File:** `DataWarehouse.SDK/VirtualDiskEngine/AdaptiveIndex/IoUringBindings.cs`

Implemented complete P/Invoke surface for io_uring:
- **Structs**: IoUringParams, IoSqRingOffsets, IoCqRingOffsets, IoUringSqe (64B explicit layout), IoUringCqe (16B), IoUring (216B opaque), IoVec
- **P/Invoke methods**: QueueInitParams, QueueExit, GetSqe, Submit, WaitCqe, PeekBatchCqe, CqeSeen, RegisterBuffers, UnregisterBuffers, RegisterFiles, PosixOpen, PosixClose
- **SQE prep helpers**: PrepRead, PrepWrite, PrepReadFixed, PrepWriteFixed, PrepUringCmd
- **Constants**: IORING_OP_READ(22), IORING_OP_WRITE(23), IORING_OP_READ_FIXED(4), IORING_OP_WRITE_FIXED(5), IORING_OP_URING_CMD(80), IORING_SETUP_SQPOLL, O_DIRECT, O_RDWR
- **Availability**: Lazy<bool> checking Linux OS + NativeLibrary.TryLoad("liburing")

### Task 2: IoUringBlockDevice with Registered Buffers and Fallback
**Commit:** `6ec1f801`
**File:** `DataWarehouse.SDK/VirtualDiskEngine/AdaptiveIndex/IoUringBlockDevice.cs`

Implemented IBlockDevice using io_uring for maximum I/O throughput:
- **Initialization**: O_DIRECT file open, NativeMemory.AlignedAlloc (4096 alignment) for queueDepth buffers, registered with io_uring
- **ReadBlockAsync/WriteBlockAsync**: Acquire registered buffer, prep fixed read/write, submit, wait CQE, copy to/from managed memory
- **ReadBlocksAsync**: Batched submission of all reads in single io_uring_submit, collect all CQEs
- **NvmePassthroughAsync**: IORING_OP_URING_CMD for raw NVMe commands on /dev/nvme* devices
- **Ring-per-thread**: ConcurrentDictionary<int, IoUring> keyed by managed thread ID; shared ring fallback with SemaphoreSlim when thread count exceeds ProcessorCount
- **Factory**: `Create()` returns IoUringBlockDevice on Linux with liburing, FileBlockDevice otherwise
- **Dispose**: Unregisters buffers, frees NativeMemory, exits rings, closes fd

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] ConcurrentDictionary over ThreadStatic for ring-per-thread**
- **Found during:** Task 2 build verification
- **Issue:** Sonar S2696 error: setting [ThreadStatic] fields from instance methods is flagged as error
- **Fix:** Replaced ThreadStatic with ConcurrentDictionary<int, IoUring> keyed by Environment.CurrentManagedThreadId
- **Files modified:** IoUringBlockDevice.cs
- **Commit:** 6ec1f801

**2. [Rule 3 - Blocking] DllImport over LibraryImport**
- **Found during:** Task 1 design
- **Issue:** Plan specified [LibraryImport] but ref/out parameters on opaque 216-byte structs cause source generator issues
- **Fix:** Used [DllImport] with managed entry point names for all liburing/libc bindings
- **Files modified:** IoUringBindings.cs
- **Commit:** f502081e

## Verification

- Both files exist under `DataWarehouse.SDK/VirtualDiskEngine/AdaptiveIndex/`
- `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj --no-restore` succeeds with 0 errors, 0 warnings
- IoUringBindings.IsAvailable returns false on Windows (Lazy<bool> detection, no crash)
- Factory method Create() returns FileBlockDevice on non-Linux platforms

## Self-Check: PASSED

- [x] IoUringBindings.cs exists
- [x] IoUringBlockDevice.cs exists
- [x] Commit f502081e found (Task 1)
- [x] Commit 6ec1f801 found (Task 2)
- [x] Build: 0 errors, 0 warnings
