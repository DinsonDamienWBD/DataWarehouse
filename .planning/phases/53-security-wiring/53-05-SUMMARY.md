---
phase: 53-security-wiring
plan: 05
subsystem: distributed-protocol-security
tags: [security, swim, crdt, mdns, federation, hmac, authentication, dist-findings]
dependency_graph:
  requires: []
  provides: [swim-hmac-auth, crdt-auth, mdns-verification, federation-heartbeat-auth, hardware-probe-validation]
  affects: [SwimClusterMembership, CrdtReplicationSync, SdkCrdtTypes, MdnsServiceDiscovery, ZeroConfigClusterBootstrap, FederationOrchestrator, HardwareProbeFactory]
tech_stack:
  added: []
  patterns: [HMAC-SHA256-message-auth, dead-node-quorum, timestamp-bounding, network-prefix-filtering, validating-decorator]
key_files:
  created:
    - DataWarehouse.SDK/Hardware/ValidatingHardwareProbe (inline in HardwareProbeFactory.cs)
  modified:
    - DataWarehouse.SDK/Infrastructure/Distributed/Membership/SwimClusterMembership.cs
    - DataWarehouse.SDK/Infrastructure/Distributed/Membership/SwimProtocolState.cs
    - DataWarehouse.SDK/Infrastructure/Distributed/Replication/CrdtReplicationSync.cs
    - DataWarehouse.SDK/Infrastructure/Distributed/Replication/SdkCrdtTypes.cs
    - DataWarehouse.SDK/Infrastructure/Distributed/Discovery/MdnsServiceDiscovery.cs
    - DataWarehouse.SDK/Infrastructure/Distributed/Discovery/ZeroConfigClusterBootstrap.cs
    - DataWarehouse.SDK/Federation/Orchestration/FederationOrchestrator.cs
    - DataWarehouse.SDK/Hardware/HardwareProbeFactory.cs
decisions:
  - HMAC-SHA256 chosen for gossip authentication (standard, fast, available in .NET BCL)
  - Dead node quorum default set to 2 (prevents single-message eviction while remaining responsive)
  - Timestamp skew tolerance set to 1 hour (balances clock drift tolerance with MaxValue attack prevention)
  - ValidatingHardwareProbe uses decorator pattern to avoid modifying each platform probe
  - CRDT HMAC uses payload-appended scheme (last 32 bytes) for gossip compatibility
metrics:
  duration: ~12 minutes
  completed: 2026-02-19
  tasks: 2/2
  files_modified: 8
---

# Phase 53 Plan 05: SWIM/CRDT/mDNS/Federation Authentication Summary

HMAC-SHA256 authentication for all distributed gossip protocols with timestamp bounding and mDNS verification gates.

## Findings Resolved

| Finding | CVSS | Resolution |
|---------|------|------------|
| DIST-03 | 8.6 | SWIM gossip HMAC-authenticated; dead node quorum; state change rate limiting |
| DIST-04 | 8.1 | CRDT gossip HMAC-authenticated; unknown nodes rejected |
| DIST-06 | 7.3 | mDNS discovery requires cluster verification before auto-join; network prefix filtering |
| DIST-07 | 7.1 | Federation heartbeats rejected from unknown nodes; values validated; topology changes rate-limited |
| DIST-08 | 6.5 | LWW Register timestamps bounded to 1hr future max; CRDT items with future timestamps rejected |
| DIST-09 | 5.3 | Hardware probe results sanitized via ValidatingHardwareProbe decorator; string injection prevented |

## Task 1: HMAC Authentication for SWIM Gossip and CRDT Replication

**Commit:** 36a8ba16

### DIST-03: SWIM Membership Poisoning (CVSS 8.6)

- Added `ClusterSecret` property to `SwimConfiguration` for HMAC-SHA256 key
- `SwimMessage.Serialize()` computes HMAC over message payload when secret is set
- `SwimMessage.Deserialize()` verifies HMAC using `CryptographicOperations.FixedTimeEquals` (timing-safe)
- Messages with missing or invalid HMAC are silently rejected (return null)
- `HandleDeadMessage` now requires configurable quorum (default 2) of independent Dead reports
- Single Dead message transitions node to Suspected, not Dead
- Rate limiting: max 1 state change per node per second (configurable via `MaxStateChangesPerNodePerSecond`)

### DIST-04: CRDT State Injection (CVSS 8.1)

- Added `ClusterSecret` to `CrdtReplicationSyncConfiguration`
- Outgoing CRDT batches have HMAC-SHA256 appended (last 32 bytes of payload)
- Incoming CRDT gossip verifies HMAC before processing
- Sender validation: `OriginNodeId` checked against `IClusterMembership.GetMembers()`
- Updates from unknown nodes are rejected with sync failure event

### DIST-08: LWW Timestamp Attack (CVSS 6.5)

- `SdkLWWRegister.Merge()` rejects timestamps > 1 hour in the future
- `CrdtReplicationSync.ProcessRemoteItem()` rejects items with future timestamps
- `MaxTimestampSkew = TimeSpan.FromHours(1)` prevents `DateTimeOffset.MaxValue` poisoning

## Task 2: Secure mDNS Discovery, Federation Heartbeat, and Hardware Probes

**Commit:** b53c988e

### DIST-06: mDNS Spoofing (CVSS 7.3)

- `MdnsConfiguration.RequireClusterVerification` (default: true) gates auto-join
- Discovered nodes marked `Verified = false` until credential validation
- `ZeroConfigClusterBootstrap` skips unverified nodes when `RequireClusterVerification` is true
- `AllowedNetworkPrefixes` restricts which network ranges are accepted for mDNS
- `MarkServiceVerified()` method for post-TLS-handshake verification
- Full audit trail logging for all discovery/join/leave/verify events

### DIST-07: Federation Heartbeat Spoofing (CVSS 7.1)

- `SendHeartbeatAsync` rejects heartbeats from nodes not in topology
- Unknown node heartbeats publish `federation.heartbeat.rejected` event
- Heartbeat values validated: HealthScore [0.0, 1.0], FreeBytes >= 0, FreeBytes <= TotalBytes
- Timestamp bounded to 1 hour future max
- Topology change requests rate-limited via `MinTopologyChangeIntervalSeconds` (default: 5s)

### DIST-09: Hardware Probe Result Validation (CVSS 5.3)

- `ValidatingHardwareProbe` decorator wraps all platform probes
- String fields truncated to 1024 chars, control characters stripped
- Required fields (DeviceId, Name) enforced -- invalid devices filtered out
- Properties dictionary keys/values sanitized
- Factory method `HardwareProbeFactory.Create()` automatically wraps probes

## Deviations from Plan

None -- plan executed exactly as written.

## Build Verification

- `dotnet build DataWarehouse.slnx`: 0 errors, 0 warnings
- All 8 modified files compile cleanly
- No breaking changes to public API (all additions are opt-in via configuration)
