---
phase: 89-ecosystem-compatibility
plan: 01
subsystem: Ecosystem Compatibility - Proto Service Definitions
tags: [grpc, protobuf, ecosystem, service-definitions, versioning]
dependency_graph:
  requires: []
  provides: [proto-service-definitions, proto-mirror-types, proto-versioning]
  affects: [GrpcServiceGenerator, GrpcServiceDescriptor]
tech_stack:
  added: []
  patterns: [proto-mirror-types, length-prefixed-binary-serialization, file-scoped-helpers]
key_files:
  created:
    - DataWarehouse.SDK/Contracts/Ecosystem/DataWarehouseService.proto
    - DataWarehouse.SDK/Contracts/Ecosystem/ProtoServiceDefinitions.cs
    - DataWarehouse.SDK/Contracts/Ecosystem/ProtoVersioningStrategy.cs
  modified: []
decisions:
  - ProtoColumnType enum maps 1:1 to ColumnDataType values (Int32=0 through Null=8)
  - Length-prefixed binary serialization (not actual proto wire format) for simplicity
  - FrozenDictionary for deprecated field registry (zero-allocation lookup)
  - Major version match required for client compatibility
key-decisions:
  - "Proto mirror types use length-prefixed binary ToBytes/FromBytes for runtime gRPC serving"
  - "DataWarehouseGrpcServices static registry provides all 6 service descriptors"
metrics:
  duration: 6min
  completed: 2026-02-23T23:41:00Z
  tasks_completed: 2
  tasks_total: 2
  files_created: 3
  files_modified: 0
---

# Phase 89 Plan 01: Canonical Proto Service Definitions Summary

Canonical protobuf3 service definitions for all 6 DataWarehouse operations with C# mirror types for runtime gRPC serving and a versioning strategy for backward compatibility.

## What Was Built

### Task 1: .proto Service Definitions (DataWarehouseService.proto)
Comprehensive proto3 specification file serving as the single source of truth for multi-language SDK generation. Contains 6 services with 22 RPC methods total:

- **StorageService** (6 RPCs): Store, Retrieve (stream), Delete, Exists, GetMetadata, List (stream)
- **QueryService** (3 RPCs): Execute (stream), Prepare, ExecutePrepared (stream)
- **TagService** (3 RPCs): SetTags, GetTags, RemoveTags
- **SearchService** (2 RPCs): Search (stream), SearchByTags (stream)
- **StreamService** (3 RPCs): StreamStore (client-stream), StreamRetrieve (stream), Subscribe (stream)
- **AdminService** (4 RPCs): GetHealth, GetCapabilities, ManagePlugin, GetMetrics

All message types have reserved field ranges (100-199) for future evolution. Uses `google.protobuf.Timestamp` for time fields, `map<string, string>` for metadata, proper streaming annotations.

### Task 2: C# Mirror Types and Versioning Strategy

**ProtoServiceDefinitions.cs**: 17 C# record types mirroring proto messages with `[SdkCompatibility("6.0.0")]` attributes, nullable annotations, and `ToBytes()`/`FromBytes()` serialization methods:
- ProtoStoreRequest/Response, ProtoRetrieveRequest/Chunk
- ProtoQueryRequest/ResultBatch (with ProtoColumnDescriptor and ProtoRowData)
- ProtoSearchRequest/Result, ProtoHealthResponse, ProtoCapabilitiesResponse
- ProtoColumnType enum (9 values mapping 1:1 to ColumnDataType)
- DataWarehouseGrpcServices.GetServiceDescriptors() returning descriptors for all 6 services

**ProtoVersioningStrategy.cs**: Version compatibility checking and migration:
- CurrentVersion = "1.0.0"
- IsCompatible() checks major version match
- GetDeprecatedFields() returns deprecated field numbers per service/method
- MigrateRequest() for forward-migrating old client payloads
- FrozenDictionary-based deprecated field registry

## Deviations from Plan

None - plan executed exactly as written.

## Verification

- .proto file contains 6 services with all specified RPC methods
- C# mirror types compile (0 errors, 0 warnings)
- ProtoVersioningStrategy.IsCompatible("1.0.0") returns true, IsCompatible("2.0.0") returns false
- DataWarehouseGrpcServices.GetServiceDescriptors() returns 6 service descriptors
- `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj --no-restore` succeeds

## Commits

| Task | Commit | Description |
|------|--------|-------------|
| 1 | c941f97a | Proto service definitions (.proto file) |
| 2 | 7bd63921 | C# mirror types + versioning strategy |

## Self-Check: PASSED

All 3 created files exist and both commits verified.
