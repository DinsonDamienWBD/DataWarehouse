---
phase: 63-universal-fabric
plan: 01
subsystem: SDK/Storage
tags: [storage-addressing, dw-namespace, uri-parsing, discriminated-union]
dependency_graph:
  requires: []
  provides: [DwBucketAddress, DwNodeAddress, DwClusterAddress, DwAddressParser]
  affects: [StorageAddress, StorageAddressKind]
tech_stack:
  added: []
  patterns: [sealed-record-variants, uri-scheme-parsing, discriminated-union-extension]
key_files:
  created:
    - DataWarehouse.SDK/Storage/DwNamespace.cs
    - DataWarehouse.SDK/Storage/DwAddressParser.cs
  modified:
    - DataWarehouse.SDK/Storage/StorageAddress.cs
    - DataWarehouse.SDK/Storage/StorageAddressKind.cs
decisions:
  - "Used string-based re-parsing in DwAddressParser.Parse(Uri) to avoid .NET Uri class mangling custom dw:// scheme authorities"
  - "Removed SdkCompatibility from factory methods since attribute is type-only; kept on record variants"
metrics:
  duration: 156s
  completed: 2026-02-19T21:37:10Z
  tasks: 2/2
  files_created: 2
  files_modified: 2
---

# Phase 63 Plan 01: dw:// Namespace Addressing Summary

Three sealed record variants (DwBucketAddress, DwNodeAddress, DwClusterAddress) extending StorageAddress discriminated union with full dw:// URI parsing and S3-compatible validation.

## What Was Done

### Task 1: Add dw:// StorageAddress variants and StorageAddressKind entries
**Commit:** 14df04a0

- Added `DwBucket`, `DwNode`, `DwCluster` entries to `StorageAddressKind` enum
- Created `DwNamespace.cs` with three sealed record variants:
  - `DwBucketAddress(string Bucket, string ObjectPath)` -- dw://mybucket/path/to/object
  - `DwNodeAddress(string NodeId, string ObjectPath)` -- dw://node@hostname/path
  - `DwClusterAddress(string ClusterName, string Key)` -- dw://cluster:name/key
- Each variant overrides `Kind`, `ToKey()`, and `ToUri()` with correct formatting
- Added `FromDwBucket`, `FromDwNode`, `FromDwCluster` factory methods to `StorageAddress`
- Integrated "dw" case into `FromUri()` switch, delegating to `DwAddressParser.Parse()`

### Task 2: Create DwAddressParser with format detection and validation
**Commit:** 1fe2562a

- Created `DwAddressParser` static class with:
  - `Parse(string dwUri)` -- parses dw:// URI string into correct variant
  - `Parse(Uri uri)` -- parses Uri with dw scheme (re-parses from OriginalString to avoid .NET Uri mangling)
  - `TryParse(string dwUri, out StorageAddress? address)` -- non-throwing variant
- Format detection: `node@` prefix = node-based, `cluster:` prefix = cluster-based, default = bucket-based
- Validation rules:
  - Bucket names: 3-63 chars, lowercase alphanumeric + hyphens, no leading/trailing hyphen (S3-compatible)
  - Node IDs: valid hostname or IP address
  - Cluster names: 1-255 chars, alphanumeric + hyphens + dots
  - Object paths: max 1024 chars, no null bytes, no path traversal (..)
- Clear `FormatException` messages for all validation failures

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Removed SdkCompatibility attribute from factory methods**
- **Found during:** Task 1
- **Issue:** `[SdkCompatibility]` attribute is declared with `AttributeTargets` that only allows class/struct/enum/interface/delegate -- applying it to methods caused CS0592
- **Fix:** Removed the attribute from the three `FromDw*` factory methods; kept it on the record type declarations where it is valid
- **Files modified:** DataWarehouse.SDK/Storage/StorageAddress.cs
- **Commit:** 14df04a0

## Build Verification

All errors in the SDK build are pre-existing in `DataWarehouse.SDK/Storage/Fabric/` (IBackendRegistry.cs, IS3CompatibleServer.cs, IStorageFabric.cs) from future phase plans. Zero errors introduced by this plan's changes.

## Self-Check: PASSED

- [x] DataWarehouse.SDK/Storage/DwNamespace.cs -- FOUND
- [x] DataWarehouse.SDK/Storage/DwAddressParser.cs -- FOUND
- [x] Commit 14df04a0 -- FOUND
- [x] Commit 1fe2562a -- FOUND
