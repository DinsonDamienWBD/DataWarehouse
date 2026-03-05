---
phase: 91.5-vde-v2.1-format-completion
plan: 87-24
subsystem: VirtualDiskEngine
tags: [cpsh, wasm, pushdown, smart-extents, io_uring, computational-nvme, vopt-37]
dependency_graph:
  requires: [ModuleDefinitions.cs, ComputeCodeCacheRegion.cs]
  provides: [ComputePushdownModule, WasmPredicateDescriptor, ComputePushdownFlags, FilterExecutionMode]
  affects: [InodeV2 module overflow block layout, io_uring filtered read path]
tech_stack:
  added: []
  patterns: [BinaryPrimitives LE serialization, flags enum, static factory methods, inline/external duality]
key_files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/Format/ComputePushdownModule.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Compression/WasmPredicateDescriptor.cs
  modified: []
decisions:
  - "PredicateFlags serialized as single byte at offset [12] with 3 reserved bytes for alignment; keeps flags extensible without breaking 48B layout"
  - "FilterExecutionMode priority: DriveSideExecution > HostSidePostDma > Disabled; ensures best hardware is always selected"
  - "CreateExternal clears InlinePredicate flag to prevent ambiguous IsInline=true for external refs"
  - "InlinePredicate byte[] always 32 bytes (zeroed padding); avoids null checks in hot path"
metrics:
  duration: 5min
  completed: 2026-03-02T12:27:20Z
  tasks_completed: 1
  files_created: 2
  files_modified: 0
---

# Phase 91.5 Plan 87-24: CPSH Module and WasmPredicateDescriptor Summary

Smart Extents WASM Predicate Pushdown (CPSH, Module bit 19) — per-inode WASM predicate module enabling io_uring filtered reads and computational NVMe drive-side execution.

## Tasks Completed

| # | Task | Commit | Files |
|---|------|--------|-------|
| 1 | ComputePushdownModule and WasmPredicateDescriptor | `ea35a00a` | ComputePushdownModule.cs, WasmPredicateDescriptor.cs |

## What Was Built

### ComputePushdownModule (48-byte inode overflow struct)

Per spec: `[WasmPredicateOffset:8][PredicateLen:4][PredicateFlags:4][InlinePredicate:32]`

- `WasmPredicateOffset` — block offset in ComputeCodeCache region (0 when inline)
- `PredicateLen` — byte length of WASM bytecode
- `PredicateFlags` — `[Flags]` enum: None/InlinePredicate/ComputationalNvme/IoUringFiltered/PrecompileHint
- `InlinePredicate` — 32-byte buffer for small predicates (zero-padded)
- `IsInline` — derived: flags has InlinePredicate AND len <= 32
- `SerializedSize = 48`, `ModuleBitPosition = 19`, `MaxInlinePredicateSize = 32`
- `Serialize(in module, Span<byte>)` / `Deserialize(ReadOnlySpan<byte>)` — full LE round-trip

### WasmPredicateDescriptor (runtime resolver)

Wraps `ComputePushdownModule` to provide typed access to predicate bytecode:

- `IsInline`, `IsComputationalNvmeCapable`, `BytecodeLength` — inspection properties
- `GetInlineBytecode()` — returns `ReadOnlyMemory<byte>` of inline bytecode (throws if not inline)
- `GetExternalBlockOffset()` — returns block offset in ComputeCodeCache (throws if inline)
- `GetRecommendedMode(bool hasComputationalNvme)` — selects DriveSideExecution > HostSidePostDma > Disabled
- `CreateInline(ReadOnlySpan<byte>, ComputePushdownFlags)` — validates <= 32B, sets InlinePredicate flag
- `CreateExternal(long blockOffset, int bytecodeLen, ComputePushdownFlags)` — clears InlinePredicate flag

### FilterExecutionMode enum

- `HostSidePostDma` (0) — io_uring: WASM executes after DMA, before user-space copy
- `DriveSideExecution` (1) — computational NVMe: ARM processor executes WASM on flash
- `Disabled` (2) — falls back to standard non-filtered read path (zero overhead)

## Verification

```
dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj --no-restore
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

## Deviations from Plan

None - plan executed exactly as written.

## Self-Check: PASSED

- [x] `DataWarehouse.SDK/VirtualDiskEngine/Format/ComputePushdownModule.cs` — FOUND
- [x] `DataWarehouse.SDK/VirtualDiskEngine/Compression/WasmPredicateDescriptor.cs` — FOUND
- [x] Commit `ea35a00a` — FOUND (feat(91.5-87-24))
- [x] Build: 0 errors, 0 warnings
- [x] ComputePushdownModule.SerializedSize = 48 (per spec)
- [x] IsInline guards PredicateLen <= 32 (MaxInlinePredicateSize)
