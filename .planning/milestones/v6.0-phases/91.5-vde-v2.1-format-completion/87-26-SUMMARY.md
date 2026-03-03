---
phase: 91.5-vde-v2.1-format-completion
plan: 87-26
subsystem: vde-format
tags: [spatiotemporal, geohash, hilbert-curve, inode-module, extent, stex, vopt-39, iot, lidar, drone]

# Dependency graph
requires:
  - phase: 91.5-vde-v2.1-format-completion
    provides: ExtendedInode512 with DataOsModuleArea, InodeExtent, DeltaExtentModule pattern
provides:
  - SpatioTemporalModule: 6B inode module at fixed offset 0x1FA (bit 24) with coordinate system and Hilbert curve params
  - SpatioTemporalExtent: 64B 4D extent pointer with 16-byte geohash, time range, and bounding box query support
  - EncodeGeohash/DecodeGeohash: interleaved-bit WGS-84 lat/lon encoding to 16-byte binary geohash
  - ComputeHilbertIndex: XY-to-Hilbert curve index for spatial block clustering
  - VOPT-39 (STEX, Module bit 24): complete 4D spatiotemporal extent addressing
affects: [VdeMountPipeline, extent-allocator, IoT/LiDAR/drone-telemetry ingestion, bounding-box query engine]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "6B fixed-offset inode module in DataOsModuleArea following DeltaExtentModule pattern"
    - "64B spatiotemporal extent = reinterpretation of 2x32B standard extent slots (MaxDirectExtents=3)"
    - "Interleaved-bit binary geohash encoding (longitude even bits, latitude odd bits)"
    - "Hilbert XY-to-index via rotate/reflect algorithm, O(order) iterations, zero allocation"
    - "4D bounding box query: bit-masked geohash range comparison + time range overlap"

key-files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/Format/SpatioTemporalModule.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Format/SpatioTemporalExtent.cs
  modified: []

key-decisions:
  - "SpatioTemporalModule layout: [CoordinateSystem:2][Precision:2][HilbertOrder:2] = 6B LE at inode offset 0x1FA (byte 506)"
  - "MaxDirectExtents=3 represents 3x64B slots reinterpreting 6x32B standard extent area"
  - "ExtentsPerIndirectBlock=63: floor(4080/64) per 4096-byte block minus 16-byte trailer"
  - "Geohash uses interleaved longitude (even bits) / latitude (odd bits) encoding for Morton/geohash compatibility"
  - "Hilbert curve uses standard rotate/reflect algorithm: O(order) iterations, uint arithmetic, no heap allocation"
  - "OverlapsBoundingBox uses bit-masked byte comparison for precision-aware partial-byte geohash comparison"
  - "SpatioCoordinateSystem: Wgs84=0, Ecef=1, LocalCartesian=2, UtmZone=3 (UTM zone in upper 8 bits of Precision)"

patterns-established:
  - "STEX module bit 24: when present in ModuleManifest, direct extents reinterpreted as SpatioTemporalExtent array"
  - "DefaultWgs84 static instance: WGS-84, precision=64 bits, HilbertOrder=16 for standard IoT/drone workloads"

# Metrics
duration: 3min
completed: 2026-03-02
---

# Phase 91.5 Plan 87-26: STEX Spatiotemporal 4D Extent Addressing Summary

**STEX inode module (bit 24) and 64B SpatioTemporalExtent with 16-byte geohash encoding, Hilbert curve block clustering, and 4D bounding-box query support for IoT/LiDAR/drone telemetry (VOPT-39)**

## Performance

- **Duration:** 3 min
- **Started:** 2026-03-02T12:29:33Z
- **Completed:** 2026-03-02T12:32:54Z
- **Tasks:** 1
- **Files modified:** 2

## Accomplishments

- `SpatioTemporalModule`: 6B inode module at fixed offset 0x1FA (byte 506), stores `SpatioCoordinateSystem` (ushort), `Precision` (ushort, bits of geohash resolution), and `HilbertOrder` (ushort, Hilbert curve order). Full LE serialization/deserialization. `DefaultWgs84` static instance with precision=64 (~10cm) and order=16.
- `SpatioTemporalExtent`: 64B spatiotemporal extent encoding `SpatialGeohash:16 + TimeEpochStart:8 + TimeEpochEnd:8 + StartBlock:8 + BlockCount:4 + Flags:4 + ExpectedHash:16`. `MaxDirectExtents=3`, `ExtentsPerIndirectBlock=63`.
- Geohash utilities: `EncodeGeohash` (interleaved-bit lon/lat to 16 bytes), `DecodeGeohash` (centre of geohash cell), `ComputeHilbertIndex` (XY-to-Hilbert via rotate/reflect algorithm, zero allocation).
- 4D query helpers: `ContainsPoint`, `OverlapsTimeRange`, `OverlapsBoundingBox` (bit-masked partial-byte geohash comparison + time range overlap).
- Build: 0 errors, 0 warnings.

## Task Commits

1. **Task 1: SpatioTemporalModule and SpatioTemporalExtent** - `9be04e0a` (feat)

## Files Created/Modified

- `DataWarehouse.SDK/VirtualDiskEngine/Format/SpatioTemporalModule.cs` - 6B inode module (STEX, bit 24) at inode offset 0x1FA, coordinate system + Hilbert curve params, LE serialization
- `DataWarehouse.SDK/VirtualDiskEngine/Format/SpatioTemporalExtent.cs` - 64B spatiotemporal extent with geohash encoding, Hilbert index computation, 4D bounding box query helpers

## Decisions Made

- `SpatioTemporalModule` layout: `[CoordinateSystem:2][Precision:2][HilbertOrder:2]` = 6B LE at inode byte 506, following the `DeltaExtentModule` pattern established in 87-23.
- `MaxDirectExtents = 3`: six standard 32-byte extent slots reinterpreted as three 64-byte spatiotemporal entries. `ExtentsPerIndirectBlock = 63` = floor(4080 / 64).
- Geohash encoding uses interleaved longitude (even bits) / latitude (odd bits) consistent with standard geohash conventions, enabling direct comparison with Morton-curve sorted indexes.
- Hilbert curve index uses the standard rotate/reflect algorithm with `uint` arithmetic and no heap allocation. Takes `hilbertOrder` bits per axis from the interleaved geohash.
- `OverlapsBoundingBox` applies a bit-mask (`0xFF << (8 - remainBits)`) for partial-byte precision comparison, avoiding false positives at cell boundaries.
- `SpatioCoordinateSystem.UtmZone`: UTM zone number encoded in upper 8 bits of `Precision` field as documented in XML summary.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- VOPT-39 STEX module complete. VdeMountPipeline can activate spatiotemporal mode by reading bit 24 from ModuleManifest and reinterpreting the direct extent slots as `SpatioTemporalExtent` arrays.
- `ComputeHilbertIndex` is ready for use by the VDE extent allocator to sort IoT/LiDAR/drone telemetry blocks by Hilbert index for optimal NVMe scatter-gather DMA prefetch on bounding-box queries.
- No blockers for Phase 92 (VDE Decorator Chain).

---
*Phase: 91.5-vde-v2.1-format-completion*
*Completed: 2026-03-02*
