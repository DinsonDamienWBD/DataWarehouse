---
phase: 87-vde-scalable-internals
plan: 12
subsystem: VDE Encryption & Compression
tags: [encryption, compression, aes-gcm, brotli, per-extent, vopt-23, vopt-24]
dependency_graph:
  requires: ["87-04"]
  provides: ["PerExtentEncryptor", "PerExtentCompressor", "CompressedExtent", "EncryptionStats", "CompressionStats", "VdeCompressionLevel"]
  affects: ["VDE I/O pipeline", "extent processing"]
tech_stack:
  added: ["AES-256-GCM via AesGcm", "HKDF nonce derivation", "BrotliStream compression"]
  patterns: ["per-extent crypto (1 nonce per extent)", "threshold-based skip compression", "optional dictionary pre-training"]
key_files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/Encryption/PerExtentEncryptor.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Compression/PerExtentCompressor.cs
  modified:
    - DataWarehouse.SDK/VirtualDiskEngine/Sql/PredicatePushdownPlanner.cs
decisions:
  - "HKDF-SHA256 for deterministic 12-byte nonce derivation from extent position (enables random-access decryption)"
  - "Brotli over Zstd for compression (BCL built-in, no external dependency)"
  - "VdeCompressionLevel custom enum to avoid SDK CompressionLevel conflict"
  - "95% threshold for skip-compression (not worth overhead if < 5% savings)"
metrics:
  duration: "7min"
  completed: "2026-02-23T21:47:00Z"
---

# Phase 87 Plan 12: Per-Extent Encryption & Compression Summary

AES-256-GCM per-extent encryption with HKDF-derived deterministic nonce (256x overhead reduction vs per-block) and Brotli per-extent compression with optional 8KB dictionary pre-training for repetitive data patterns.

## What Was Built

### PerExtentEncryptor (VOPT-23)
- AES-256-GCM encryption operating on entire extents as single crypto units
- Deterministic 12-byte nonce derived via HKDF-SHA256 from `StartBlock + LogicalOffset`
- Enables random-access decryption without stored nonce table
- Wire format: Nonce(12) + Ciphertext(N) + AuthTag(16) = 28 bytes overhead per extent
- For a 256-block extent: 28 bytes vs 28 * 256 = 7168 bytes in per-block mode (256x reduction)
- `EncryptionStats` struct tracks extents encrypted, bytes processed, total encrypt time
- Secure key disposal via `CryptographicOperations.ZeroMemory` on `Dispose()`

### PerExtentCompressor (VOPT-24)
- Brotli compression on extent-sized units (captures cross-block patterns)
- Optional dictionary pre-training from first 8KB of extent data for repetitive patterns
- Threshold-based skip: if compressed >= 95% of original, returns uncompressed
- `CompressedExtent` readonly record struct with Data, OriginalSize, CompressedSize, Dictionary, Ratio
- `CompressionStats` tracks extents compressed, average ratio, bytes saved
- `VdeCompressionLevel` enum: Fastest, Optimal, SmallestSize (avoids SDK enum conflict)
- Thread-safe statistics via Interlocked + CAS double updates

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Pre-existing BitOperations build error in RoaringBitmapTagIndex**
- **Found during:** Task 1 build verification
- **Issue:** `BitOperations.RoundUpToPowerOf2` not resolved despite `using System.Numerics`
- **Fix:** Fully qualified `System.Numerics.BitOperations.RoundUpToPowerOf2`
- **Files modified:** DataWarehouse.SDK/VirtualDiskEngine/Index/RoaringBitmapTagIndex.cs

**2. [Rule 3 - Blocking] Nullability error in PredicatePushdownPlanner**
- **Found during:** Task 1 build verification (appeared from parallel agent commit)
- **Issue:** `Predicate<ZoneMapEntry>?[]` did not match `Predicate<ZoneMapEntry>[]` return type
- **Fix:** Added null-forgiving operator on return
- **Files modified:** DataWarehouse.SDK/VirtualDiskEngine/Sql/PredicatePushdownPlanner.cs
- **Commit:** 7426a87a

## Verification

- `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj --no-restore` -- 0 errors, 0 warnings
- Encryption wire format: Nonce(12) + Ciphertext(N) + Tag(16)
- Nonce derivation is deterministic from extent position via HKDF
- Compression threshold correctly skips compression when ratio >= 0.95

## Commits

| Commit | Description |
|--------|-------------|
| 993ed4c8 | feat(87-12): per-extent encryption and compression (VOPT-23, VOPT-24) |
| 7426a87a | fix(87-12): fix nullability error in PredicatePushdownPlanner |

## Self-Check: PASSED
