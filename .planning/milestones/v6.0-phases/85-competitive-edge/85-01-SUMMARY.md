---
phase: 85-competitive-edge
plan: 01
subsystem: VirtualDiskEngine.BlockExport
tags: [zero-copy, block-export, protocol-fast-path, memory-mapped-io]
dependency_graph:
  requires: [IBlockDevice, RegionDirectory, RegionPointerTable, SuperblockV2]
  provides: [IVdeBlockExporter, ZeroCopyBlockReader, VdeBlockExportPath, BlockExportCapabilities, ProtocolHint, ExportStatistics]
  affects: [NAS/SAN protocol strategies, storage plugin pipeline]
tech_stack:
  added: [MemoryMappedFile, MemoryMappedViewAccessor]
  patterns: [zero-copy I/O, memory-mapped file access, Interlocked thread-safe counters, encrypted-region fallback]
key_files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/BlockExport/IVdeBlockExporter.cs
    - DataWarehouse.SDK/VirtualDiskEngine/BlockExport/ZeroCopyBlockReader.cs
    - DataWarehouse.SDK/VirtualDiskEngine/BlockExport/VdeBlockExportPath.cs
  modified: []
decisions:
  - ZeroCopyBlockReader reads entire mapped file into managed byte array for ReadOnlyMemory slicing (MemoryMappedViewAccessor has no Memory<byte> API)
  - VdeBlockExportPath auto-detects encrypted regions via RegionFlags.Encrypted and falls back to IBlockDevice
  - Environment.TickCount64 for latency tracking (ms precision, no allocation)
  - ExportStatistics as readonly record struct for zero-alloc snapshot reads
metrics:
  duration: 5min
  completed: 2026-02-24T19:21:00Z
  tasks: 2
  files: 3
---

# Phase 85 Plan 01: VDE-Native Block Export Summary

Zero-copy memory-mapped block export path for SMB/NFS/iSCSI/FC/NVMe-oF protocol strategies bypassing plugin pipeline.

## What Was Built

### IVdeBlockExporter Contract
- `ReadBlockZeroCopy(long blockAddress)` returns `ReadOnlyMemory<byte>` directly from mapped file
- `ReadBlocksAsync(long startBlock, int count, Memory<byte> destination)` for batch contiguous reads
- `TryGetRegionForBlock(long blockAddress, out RegionPointer region)` for region ownership lookup
- `GetCapabilities()` returns `BlockExportCapabilities` flags (ZeroCopy, BatchRead, ScatterGather, DirectMemoryMap)

### ZeroCopyBlockReader Implementation
- Memory-maps VDE file via `MemoryMappedFile.CreateFromFile` with `MemoryMappedFileAccess.Read`
- Caches `MemoryMappedFile` + `MemoryMappedViewAccessor` for lifetime of reader
- `ReadBlockZeroCopy` returns slices from pre-read managed buffer (no intermediate allocation)
- Falls back to `IBlockDevice.ReadBlockAsync` when memory mapping unavailable
- Region directory lookup via linear scan of active regions
- `IDisposable` for deterministic cleanup of mapped resources

### VdeBlockExportPath Fast Path
- `ServeBlockAsync(long blockAddress, ProtocolHint hint)` main entry point for protocol strategies
- `ServeBatchAsync(long startBlock, int count, Memory<byte> destination, ProtocolHint hint)` for multi-block protocols
- `ProtocolHint` enum: Smb, Nfs, Iscsi, FibreChannel, NvmeOverFabric, Generic
- Automatic encrypted-region detection via `RegionFlags.Encrypted` with `IBlockDevice` fallback
- `ExportStatistics` record: TotalBlocksServed, ZeroCopyHits, FallbackCount, AverageLatencyMicroseconds
- Thread-safe via `Interlocked` operations on all counters
- Optional `ILogger` for diagnostic output on fallback events

## Deviations from Plan

None - plan executed exactly as written.

## Commits

| Task | Commit | Description |
|------|--------|-------------|
| 1 | a47fef9e | IVdeBlockExporter contract and ZeroCopyBlockReader |
| 2 | c5ed04a0 | VdeBlockExportPath protocol fast path |

## Verification

- `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj` passes with 0 new errors, 0 new warnings
- All 3 files exist under `DataWarehouse.SDK/VirtualDiskEngine/BlockExport/`
- IVdeBlockExporter defines ReadBlockZeroCopy, ReadBlocksAsync, TryGetRegionForBlock, GetCapabilities
- ZeroCopyBlockReader implements IVdeBlockExporter with MemoryMappedFile
- VdeBlockExportPath provides protocol-agnostic fast path with ProtocolHint
- Pre-existing errors in TlaPlusModels.cs (6 errors) unrelated to this plan

## Self-Check: PASSED

- [x] IVdeBlockExporter.cs exists
- [x] ZeroCopyBlockReader.cs exists
- [x] VdeBlockExportPath.cs exists
- [x] Commit a47fef9e exists
- [x] Commit c5ed04a0 exists
- [x] Build passes (0 new errors)
