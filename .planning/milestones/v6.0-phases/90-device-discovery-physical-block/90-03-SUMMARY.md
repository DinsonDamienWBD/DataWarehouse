---
phase: 90-device-discovery-physical-block
plan: 03
subsystem: VDE Physical Device Pool Management
tags: [device-pool, storage-tier, locality, metadata-codec, bare-metal-bootstrap]
dependency_graph:
  requires: ["90-01"]
  provides: ["DevicePoolManager", "PoolMetadataCodec", "DevicePoolDescriptor", "StorageTier", "LocalityTag"]
  affects: ["UltimateFilesystem", "SDK.VirtualDiskEngine.PhysicalDevice"]
tech_stack:
  added: ["SHA-256 pool metadata checksum", "JSON pool descriptor serialization"]
  patterns: ["BoundedDictionary LRU registry", "Reserved-sector binary codec", "Best-effort metadata consistency"]
key_files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/PhysicalDevice/DevicePoolDescriptor.cs
    - Plugins/DataWarehouse.Plugins.UltimateFilesystem/DeviceManagement/PoolMetadataCodec.cs
    - Plugins/DataWarehouse.Plugins.UltimateFilesystem/DeviceManagement/DevicePoolManager.cs
  modified: []
decisions:
  - "StorageTier enum with using-alias to disambiguate from SDK.Contracts.StorageTier and Primitives.StorageTier"
  - "PoolMetadataCodec uses System.Text.Json with JsonStringEnumConverter for human-readable reserved-sector payload"
  - "BoundedDictionary(100) for pool registry, BoundedDictionary(500) for device-to-pool reverse lookup"
  - "Best-effort metadata writes: device failures mark member degraded, do not fail pool operation"
metrics:
  duration: "5min"
  completed: "2026-02-24T00:27:33Z"
  tasks_completed: 2
  tasks_total: 2
  files_created: 3
  files_modified: 0
---

# Phase 90 Plan 03: Device Pool Management Summary

Named device pools with tier classification (Hot/Warm/Cold/Frozen/Archive), hierarchical locality tags (rack/datacenter/region/zone), and reserved-sector metadata persistence via 4KB binary codec with magic number 0x44575030 and SHA-256 checksum.

## What Was Built

### Task 1: DevicePoolDescriptor SDK Types
- `StorageTier` enum: Hot, Warm, Cold, Frozen, Archive for media-to-tier mapping
- `LocalityTag` record: hierarchical placement with Rack/Datacenter/Region/Zone (all default to "default")
- `PoolMemberDescriptor` record: DeviceId, DevicePath, MediaType, CapacityBytes, ReservedBytes, IsActive
- `DevicePoolDescriptor` record: PoolId, PoolName, Tier, Locality, Members, capacity fields, MetadataVersion, extensible Properties
- `StorageTierClassifier`: NVMe/RAMDisk->Hot, SSD->Warm, HDD->Cold, Tape->Frozen, others->Cold

### Task 2: PoolMetadataCodec and DevicePoolManager
**PoolMetadataCodec** (sealed class):
- Reserved sector layout: 4KB block with magic (0x44575030), version (int32 LE), payload length (int32 LE), SHA-256 checksum (32 bytes), JSON payload (UTF-8), zero-padded
- `SerializePoolMetadata`: produces 4KB block from DevicePoolDescriptor
- `DeserializePoolMetadata`: returns null on magic/checksum mismatch
- `ValidatePoolMetadata`: quick check without full deserialization

**DevicePoolManager** (sealed, IAsyncDisposable):
- `CreatePoolAsync`: validates uniqueness/online/not-in-pool, auto-classifies tier from majority media type, reserves block 0, writes metadata
- `GetPoolAsync` / `GetPoolByNameAsync` / `GetAllPoolsAsync`: in-memory lookups
- `AddDeviceToPoolAsync`: expand pool, write metadata to new + existing devices
- `RemoveDeviceFromPoolAsync`: shrink pool, update remaining member metadata
- `DeletePoolAsync`: clear metadata, remove from registry
- `ScanForPoolsAsync`: bare-metal bootstrap by reading block 0 from raw devices, handles both 4K and 512-byte sector sizes
- `UpdatePoolLocalityAsync`: update locality tags, re-persist
- `GetPoolsByTierAsync` / `GetPoolsByLocalityAsync`: filtered queries

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] StorageTier namespace ambiguity**
- **Found during:** Task 2 build
- **Issue:** `StorageTier` exists in SDK.Contracts, Primitives.Enums, and Storage.StorageStrategy namespaces, causing CS0104 ambiguous reference
- **Fix:** Added `using StorageTier = DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice.StorageTier;` alias in DevicePoolManager.cs
- **Files modified:** DevicePoolManager.cs
- **Commit:** aa53026e

**2. [Rule 1 - Bug] Nullability mismatch in GetPoolAsync**
- **Found during:** Task 2 build
- **Issue:** CS8619 nullability mismatch: `Task.FromResult(device)` where device is non-nullable but return type is `Task<DevicePoolDescriptor?>`
- **Fix:** Changed to `Task.FromResult<DevicePoolDescriptor?>(pool)` with explicit type parameter
- **Files modified:** DevicePoolManager.cs
- **Commit:** aa53026e

## Verification

- `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj --no-restore`: 0 errors, 0 warnings
- `dotnet build Plugins/DataWarehouse.Plugins.UltimateFilesystem/DataWarehouse.Plugins.UltimateFilesystem.csproj --no-restore`: 0 errors, 0 warnings
- `ScanForPoolsAsync` confirmed in DevicePoolManager (bare-metal bootstrap)
- `0x44575030` magic number confirmed in PoolMetadataCodec
- All 12 public API methods on DevicePoolManager implemented

## Commits

| Task | Commit | Description |
|------|--------|-------------|
| 1 | c82028d2 | SDK types: StorageTier, LocalityTag, PoolMemberDescriptor, DevicePoolDescriptor, StorageTierClassifier |
| 2 | aa53026e | PoolMetadataCodec (4KB binary codec) + DevicePoolManager (pool lifecycle) |

## Self-Check: PASSED
