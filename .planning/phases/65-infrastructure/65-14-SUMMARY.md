---
phase: 65-infrastructure
plan: 14
subsystem: interface-api-generation
tags: [api, openapi, grpc, graphql, websocket, dynamic, code-generation]
dependency_graph:
  requires:
    - IPluginCapabilityRegistry (SDK)
    - RegisteredCapability model
    - CapabilityCategory enum
    - InterfaceTypes (HttpMethod, InterfaceProtocol)
  provides:
    - DynamicApiGenerator (capability-to-API-model conversion)
    - OpenApiSpecGenerator (OpenAPI 3.1 JSON)
    - GrpcServiceGenerator (.proto IDL + runtime descriptors)
    - GraphQlSchemaGenerator (SDL with Query/Mutation/Subscription)
    - WebSocketApiGenerator (channel definitions + TypeScript SDK)
  affects:
    - Interface plugin strategies (REST, gRPC, GraphQL, WebSocket)
    - Launcher HTTP layer (serves OpenAPI spec and Swagger UI)
    - Client SDK generation pipeline
tech_stack:
  added: []
  patterns:
    - Convention-based endpoint generation from capability categories
    - Auto-regeneration on capability change events
    - Protocol-specific code generation from shared DynamicApiModel
key_files:
  created:
    - DataWarehouse.SDK/Contracts/Interface/DynamicApiGenerator.cs
    - DataWarehouse.SDK/Contracts/Interface/OpenApiSpecGenerator.cs
    - DataWarehouse.SDK/Contracts/Interface/GrpcServiceGenerator.cs
    - DataWarehouse.SDK/Contracts/Interface/GraphQlSchemaGenerator.cs
    - DataWarehouse.SDK/Contracts/Interface/WebSocketApiGenerator.cs
  modified: []
decisions:
  - Convention-based endpoint mapping by CapabilityCategory (Storage->CRUD, Query->POST, Streaming->WebSocket, others->Management)
  - OpenAPI 3.1 with Bearer JWT security scheme and per-plugin tags
  - gRPC server streaming for query/search operations, unary for everything else
  - GraphQL Connection-based pagination for all list queries
  - WebSocket message envelope with type/channel/payload/correlationId structure
  - TypeScript client SDK generated alongside channel definitions
metrics:
  duration: ~6 minutes
  completed: 2026-02-19
  tasks_completed: 2
  tasks_total: 2
  files_created: 5
  total_lines: 2298
---

# Phase 65 Plan 14: Dynamic API Generation Summary

Convention-based multi-protocol API generation from IPluginCapabilityRegistry producing OpenAPI 3.1, gRPC .proto, GraphQL SDL, and WebSocket channel definitions with auto-regeneration on capability changes.

## What Was Built

### DynamicApiGenerator (574 lines)
Core engine that reads IPluginCapabilityRegistry and converts registered capabilities into a DynamicApiModel. Uses convention-based mapping:
- Storage/Archival categories: GET/POST/PUT/DELETE CRUD endpoints
- Search/Analytics/Database categories: POST query endpoints
- Pipeline/Replication/Transit/Transport categories: WebSocket streaming endpoints
- All other categories: GET/PUT management endpoints

Subscribes to capability registration, unregistration, and availability change events for automatic model regeneration.

API model types defined: ApiEndpoint, ApiDataType, ApiParameter, ApiProperty, ApiParameterLocation, DynamicApiModel.

### OpenApiSpecGenerator (457 lines)
Generates OpenAPI 3.1 JSON specification from DynamicApiModel:
- Paths grouped by URL template with method-specific operations
- Component schemas with $ref references for shared types
- Bearer JWT security scheme
- Per-plugin tags for endpoint grouping
- Standard error responses (400, 401, 404, 500) with ErrorResponse schema
- Streaming endpoints documented with WebSocket upgrade notes

### GrpcServiceGenerator (365 lines)
Generates Protocol Buffer (.proto) files and runtime GrpcServiceDescriptor:
- One service per plugin with RPCs matching capabilities
- Server streaming for query/search operations
- Standard type mapping (string, int32, int64, bool, bytes, Timestamp, Struct)
- CapabilityList/CapabilityInfo common messages
- Runtime descriptors for reflection-based serving without build-time protoc

### GraphQlSchemaGenerator (445 lines)
Generates GraphQL Schema Definition Language:
- Query type: GET endpoints as queries, query endpoints with Connection pagination
- Mutation type: POST/PUT/DELETE as create/update/delete mutations
- Subscription type: streaming endpoints as real-time event subscriptions
- Custom scalars: DateTime, BigInt, JSON, Base64
- Relay-style Connection types: ResultConnection, ResultEdge, PageInfo
- System queries: capabilities, systemHealth

### WebSocketApiGenerator (457 lines)
Generates WebSocket channel definitions and TypeScript client SDK:
- Streaming capabilities get ServerToClient event channels
- Per-plugin bidirectional command channels
- System-level event and command channels
- Standard message envelope: type, channel, payload, correlationId, timestamp
- TypeScript SDK with WebSocketMessage, CommandMessage, EventMessage, ResponseMessage interfaces
- Channel map constant with all definitions

## Deviations from Plan

None - plan executed exactly as written.

## Verification

- SDK build: PASSED (0 errors, 0 warnings)
- Kernel build: PASSED (0 errors, 0 warnings)
- DynamicApiGenerator reads IPluginCapabilityRegistry and converts to DynamicApiModel: CONFIRMED
- OpenApiSpecGenerator produces OpenAPI 3.1 JSON with paths for storage, query, management: CONFIRMED
- GrpcServiceGenerator produces .proto with service definitions and streaming RPCs: CONFIRMED
- GraphQlSchemaGenerator produces SDL with Query, Mutation, Subscription types: CONFIRMED
- WebSocketApiGenerator produces channel definitions with envelope format: CONFIRMED

## Commits

| Task | Commit | Description |
|------|--------|-------------|
| 1 | 65436d98 | DynamicApiGenerator + OpenApiSpecGenerator |
| 2 | 814ec5b9 | GrpcServiceGenerator + GraphQlSchemaGenerator + WebSocketApiGenerator |

## Self-Check: PASSED

All 5 created files found on disk. Both commits (65436d98, 814ec5b9) verified in git log.
