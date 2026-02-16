# Phase 34: Federated Object Storage & Translation Layer - Research

## Overview

**Goal**: Build a federated object storage layer that routes requests across multiple storage nodes using both object-language (UUID-based) and filepath-language (path-based) addressing, with permission-aware and location-aware routing, cross-node replication awareness, and a manifest/catalog service.

**Dependencies**:
- Phase 32: StorageAddress abstraction (for universal addressing)
- Phase 33: Virtual Disk Engine (for local storage engine backend)
- Phase 29: Raft consensus (for manifest replication and split-brain prevention)
- Existing: UltimateAccessControl (for permission checks), IClusterMembership (for node discovery)

## Requirements Summary

| Req | Description | Key Components |
|-----|-------------|----------------|
| FOS-01 | Dual-Head Router | Request classification (Object vs FilePath), routing pipeline selection |
| FOS-02 | UUID Object Addressing | UUID v7 generation, globally unique identity, manifest integration |
| FOS-03 | Permission-Aware Routing | ACL integration at router level, pre-flight permission checks |
| FOS-04 | Location-Aware Routing | Topology model, geographic proximity, health-weighted routing |
| FOS-05 | Federation Orchestrator | Node registration, health monitoring, graceful scale-in/out |
| FOS-06 | Manifest/Catalog Service | UUID-to-location mapping, Raft-based consistency |
| FOS-07 | Replication-Aware Reads | Replica preference, consistency levels, fallback chain |

## Architecture Approach

### 1. Dual-Head Router (FOS-01)

**Classification Engine**:
- Examine incoming request to determine language type
- Object Language signals: UUID format, metadata query patterns, object operations
- FilePath Language signals: path separators, directory listing verbs, filesystem operations
- Ambiguous requests use configurable default with override hints

**Implementation Strategy**:
- `IStorageRouter` interface with `RouteRequest(StorageRequest)` method
- `DualHeadRouter` implementation with pluggable `IRequestClassifier`
- Object pipeline delegates to UUID-based storage layer
- FilePath pipeline delegates to VDE or traditional file storage
- Classification is O(1) via pattern matching on request properties

### 2. UUID Object Addressing (FOS-02)

**UUID Strategy**:
- Use UUID v7 (time-ordered) for natural time-range queries
- Every object gets globally unique ID at creation time
- Location-independent identity: same UUID = same object regardless of storage node
- Manifest provides O(1) UUID-to-location lookup

**Integration Points**:
- Extend `IObjectStorageCore` with UUID-aware methods
- `StorageAddress.ObjectKeyAddress` variants can wrap UUIDs
- Backward compatible: existing string keys continue to work

### 3. Permission-Aware Routing (FOS-03)

**ACL Integration**:
- Router intercepts all requests BEFORE reaching storage nodes
- Call `UltimateAccessControl` via message bus at routing time
- Cached credentials with configurable staleness tolerance (default: 5 minutes)
- Deny-early pattern: failed ACL check returns 403 immediately
- Cache hit rate target: >95% for repeated access patterns

**Message Flow**:
```
Request → Router → ACL Check (cached) → [DENY or ROUTE to storage node]
```

### 4. Location-Aware Routing (FOS-04)

**Topology Model**:
- Network topology levels: same-rack, same-DC, cross-DC, cross-region
- Geographic proximity calculated from node metadata (lat/long or region labels)
- Node health scores from Federation Orchestrator health monitoring
- Current load metrics from node heartbeat messages

**Routing Policies**:
- Latency-optimized: prefer same-rack > same-DC > cross-DC
- Throughput-optimized: prefer least-loaded node regardless of location
- Cost-optimized: prefer on-prem > cloud storage, cheap regions > expensive regions
- Configurable per request or per tenant

### 5. Federation Orchestrator (FOS-05)

**Cluster Lifecycle**:
- Node registration: nodes announce via SWIM membership (Phase 29)
- Topology management: nodes declare rack/DC/region in metadata
- Health monitoring: periodic heartbeats with resource metrics
- Graceful scale-in/out: data rebalancing via manifest updates
- Split-brain prevention: Raft quorum (from Phase 29) for all topology changes

**Node Metadata**:
- NodeId, Address, Port (from IClusterMembership)
- Capacity (total/free bytes), Rack, Datacenter, Region labels
- Health score (0.0-1.0), Last heartbeat timestamp

### 6. Manifest/Catalog Service (FOS-06)

**Design**:
- Authoritative mapping: `UUID → List<NodeId>` (supports replication)
- Backed by Raft state machine for linearizable consistency
- Batch lookup API: resolve 100K UUIDs/sec target
- Range query support: UUID v7 prefix enables time-range queries
- Consistency guarantees: linearizable writes, serializable reads

**Replication**:
- Manifest itself is replicated via Raft (from Phase 29)
- Manifest updates are Raft log entries
- After node failure, manifest reflects correct replica locations
- Manifest state is consistent across all cluster nodes

### 7. Replication-Aware Reads (FOS-07)

**Read Routing Strategy**:
- Consistency levels:
  - `Eventual`: read from any replica (lowest latency)
  - `BoundedStaleness`: read from replica within N seconds of leader
  - `Strong`: always read from Raft leader (highest consistency)
- Replica preference: local > same-rack > same-DC > remote
- Fallback chain: try progressively more distant replicas on failure
- Replica failure triggers transparent fallback within timeout

**Implementation**:
- `IReplicaSelector` interface with consistency level parameter
- `LocationAwareReplicaSelector` uses topology + health scores
- `ConsistencyLevelPolicy` enforces staleness bounds

## Wave Structure

**Wave 1** (Foundations):
- 34-01: Dual-head router + request classification engine
- 34-02: UUID v7 addressing + object identity model

**Wave 2** (Routing Intelligence):
- 34-03: Permission-aware routing (depends on 34-01)
- 34-04: Location-aware routing (depends on 34-01)

**Wave 3** (Cluster Management):
- 34-05: Federation orchestrator (depends on Phase 29 SWIM + Raft)
- 34-06: Manifest/catalog service (depends on Phase 29 Raft, 34-02 UUID, 34-05 orchestrator)

**Wave 4** (Replication):
- 34-07: Cross-node replication-aware reads (depends on 34-04 location routing, 34-06 manifest)

## Namespace Structure

All code in `DataWarehouse.SDK/Federation/` namespace:

```
DataWarehouse.SDK/Federation/
├── Routing/
│   ├── IStorageRouter.cs
│   ├── DualHeadRouter.cs
│   ├── IRequestClassifier.cs
│   ├── RequestLanguage.cs (enum: Object, FilePath)
│   └── RoutingPipeline.cs
├── Addressing/
│   ├── UuidObjectAddress.cs
│   ├── UuidGenerator.cs (UUID v7 factory)
│   └── IObjectIdentityProvider.cs
├── Authorization/
│   ├── PermissionAwareRouter.cs
│   └── IPermissionCache.cs
├── Topology/
│   ├── ITopologyProvider.cs
│   ├── NodeTopology.cs (rack/DC/region metadata)
│   ├── LocationAwareRouter.cs
│   └── RoutingPolicy.cs (enum: Latency, Throughput, Cost)
├── Orchestration/
│   ├── IFederationOrchestrator.cs
│   ├── FederationOrchestrator.cs
│   ├── NodeRegistration.cs
│   └── ClusterTopology.cs
├── Catalog/
│   ├── IManifestService.cs
│   ├── RaftBackedManifest.cs
│   ├── ObjectLocationEntry.cs
│   └── ManifestStateMachine.cs (Raft state machine)
└── Replication/
    ├── IReplicaSelector.cs
    ├── LocationAwareReplicaSelector.cs
    ├── ConsistencyLevel.cs (enum)
    └── ReplicaFallbackChain.cs
```

## Integration Points

**StorageAddress (Phase 32)**:
- `ObjectKeyAddress` can wrap UUID strings
- `FromObjectKey(uuid.ToString())` creates UUID-addressed storage

**Virtual Disk Engine (Phase 33)**:
- FilePath language requests route to VDE
- Object language requests route to federated storage layer

**Raft Consensus (Phase 29)**:
- Manifest service uses `IRaftConsensusEngine` for state machine replication
- Topology changes go through Raft log for split-brain prevention

**UltimateAccessControl**:
- Router calls `accesscontrol.check` via message bus before routing
- Permission cache reduces message bus overhead

**SWIM Membership (Phase 29)**:
- Federation orchestrator uses `IClusterMembership` for node discovery
- Node health scores from SWIM failure detector

## Success Criteria

1. **Dual-Head Router**: Correctly classifies 100% of UUID requests as Object, 100% of path requests as FilePath, mixed requests use hints
2. **UUID Addressing**: Store object on node A, query by UUID from node B — object found via manifest in O(1)
3. **Permission Routing**: Request without permission denied at router (never reaches storage), cache hit rate >95%
4. **Location Routing**: Request from US-East routes to US-East replica when available, unhealthy nodes excluded
5. **Orchestrator**: Add/remove node → data rebalances, network partition → split-brain prevented via Raft
6. **Manifest**: Resolves 100K UUIDs/sec, manifest consistent across all nodes, reflects correct replicas after failure
7. **Replication Reads**: Eventual consistency reads from nearest replica, strong consistency reads from leader, fallback on failure

## Key Design Decisions

**Why UUID v7?**
- Time-ordered: natural chronological sorting without secondary index
- Globally unique: no coordination needed for ID generation
- Compatible with existing string-based APIs via implicit conversion

**Why Raft for Manifest?**
- Linearizable writes ensure all nodes see same UUID-to-location mapping
- Split-brain prevention critical for data safety
- Already implemented in Phase 29

**Why Dual-Head Router?**
- Backward compatibility: existing path-based code continues to work
- Future-proofing: object-based semantics enable location-independent storage
- Clean separation: different pipelines for different semantics

**Why Permission-Aware Routing?**
- Security: deny early, never send unauthorized requests to storage nodes
- Performance: cached ACL checks avoid repeated message bus round-trips
- Auditability: all permission denials logged at router level

## Testing Strategy

Each plan includes verification via:
- Unit tests for core logic (classification, UUID generation, routing selection)
- Build verification (zero errors)
- Integration tests in Phase 41 (comprehensive audit)

Wave 4 (34-07) should include end-to-end test:
- Create 3-node cluster (US-East, US-West, EU-West)
- Store object on US-East with 3x replication
- Query from US-West client with eventual consistency → US-West replica
- Query with strong consistency → US-East leader
- Fail US-West node → fallback to US-East or EU-West

## Dependencies on Existing SDK

- `IObjectStorageCore` (existing storage interface)
- `StorageAddress` (Phase 32)
- `IClusterMembership` (Phase 29)
- `IRaftConsensusEngine` (Phase 29)
- Message bus for `accesscontrol.check` calls
- `System.IO.Hashing` for checksumming
- `System.Text.Json` for serialization

## Non-Goals (Out of Scope)

- Data rebalancing algorithms (future work)
- Automatic replica count adjustment (future work)
- Cross-cluster federation (v4.0 feature)
- Custom consistency models beyond eventual/bounded/strong
