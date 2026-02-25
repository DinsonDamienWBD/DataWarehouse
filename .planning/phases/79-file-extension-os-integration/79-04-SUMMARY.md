---
phase: 79-file-extension-os-integration
plan: 04
subsystem: VirtualDiskEngine.FileExtension.Import
tags: [import, format-detection, magic-bytes, vhd, vhdx, vmdk, qcow2, vdi]
dependency-graph:
  requires: [79-01]
  provides: [FEXT-06, FEXT-07]
  affects: [VdeCreator, DwvdContentDetector]
tech-stack:
  added: []
  patterns: [magic-byte-detection, span-based-parsing, async-file-io, progress-reporting]
key-files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/FileExtension/Import/VirtualDiskFormat.cs
    - DataWarehouse.SDK/VirtualDiskEngine/FileExtension/Import/FormatDetector.cs
    - DataWarehouse.SDK/VirtualDiskEngine/FileExtension/Import/ImportResult.cs
    - DataWarehouse.SDK/VirtualDiskEngine/FileExtension/Import/ImportSuggestion.cs
    - DataWarehouse.SDK/VirtualDiskEngine/FileExtension/Import/DwvdImporter.cs
  modified: []
decisions:
  - "Priority-ordered magic detection: DWVD > VHDX > QCOW2 > VMDK > VDI > VHD (most specific first)"
  - "Raw/Img distinguished only by extension (.raw vs .img); both require sector-aligned size > 512"
  - "VDI detected via 0xBEDA107F signature at offset 64 (not header text, for reliability)"
  - "VHDX logical size read from metadata region at 1MB offset with Virtual Disk Size GUID lookup"
  - "Source data copy is byte-level sequential; sparse-aware copying deferred to future optimization"
metrics:
  duration: 5min
  completed: 2026-02-23T16:13:00Z
  tasks: 2
  files: 5
---

# Phase 79 Plan 04: Virtual Disk Import Engine Summary

Magic-byte format detection for 7 foreign formats (VHD/VHDX/VMDK/QCOW2/VDI/RAW/IMG) with import-to-DWVD engine via VdeCreator and non-DWVD import suggestion system.

## What Was Built

### Task 1: VirtualDiskFormat Enum and FormatDetector
- **VirtualDiskFormat** enum: 9 values (Unknown, Dwvd, Vhd, Vhdx, Vmdk, Qcow2, Vdi, Raw, Img)
- **VirtualDiskFormatInfo** static class: GetExtension, GetDescription, GetMagicBytes, GetMagicOffset, SupportsContentDetection
- **FormatDetector** static class: priority-ordered magic detection via BinaryPrimitives and SequenceEqual (zero allocation on span path)
  - DWVD: 0x44575644 at offset 0
  - VHDX: "vhdxfile" at offset 0
  - QCOW2: 0x514649FB big-endian at offset 0
  - VMDK: "KDMV" at offset 0 or "# Disk DescriptorFile" text
  - VDI: 0xBEDA107F little-endian at offset 64
  - VHD: "conectix" at offset 0
  - Raw/Img: extension + size heuristic fallback
- DetectFormatAsync for file-based detection with extension and size

### Task 2: Import Engine, Progress, and Suggestions
- **ImportResult** readonly struct with success/failure factories, timing, and size metrics
- **ImportProgress** readonly struct: PercentComplete (0-100), BytesProcessed, TotalBytes, Phase
- **ImportSuggestion** readonly struct: format name, CLI command, human-readable message
  - CreateSuggestion builds `dw import --from {format} "{source}" --output "{output}"` command
  - TryCreateSuggestion: detect + suggest in one call (returns null for DWVD/Unknown)
- **DwvdImporter** static class:
  - ImportAsync: detect -> read logical size -> create DWVD via VdeCreator -> copy data blocks -> finalize
  - Logical size readers: VHD footer (offset 40 BE), VHDX metadata region (1MB + GUID lookup), VMDK capacity sectors (offset 4 LE), QCOW2 (offset 24 BE), VDI (offset 368 LE)
  - Progress reporting across 4 phases: detecting, creating, copying, finalizing
  - Already-DWVD returns no-op success; Unknown throws NotSupportedException
  - CanImport convenience method for quick importability check

## Commits

| Task | Commit | Description |
|------|--------|-------------|
| 1 | 6cd6b629 | VirtualDiskFormat enum + FormatDetector magic detection |
| 2 | be38fe3d | DwvdImporter + ImportResult + ImportSuggestion |

## Deviations from Plan

None - plan executed exactly as written.

## Verification

- `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj --no-restore` passes with 0 errors, 0 warnings
- All 7 importable formats enumerated in VirtualDiskFormat
- FormatDetector checks DWVD magic first (prevents importing already-DWVD files)
- ImportSuggestion.SuggestedCommand matches `dw import --from {format} "{source}" --output "{output}"` pattern
- DwvdImporter uses VdeCreator.CreateVdeAsync (not custom file creation)
- Magic bytes match official format specifications

## Self-Check: PASSED
