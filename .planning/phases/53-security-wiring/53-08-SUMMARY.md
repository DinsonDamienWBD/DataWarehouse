---
phase: 53-security-wiring
plan: "08"
subsystem: protocol-security
tags: [security, mqtt, websocket, graphql, coap, pentest-remediation, NET-04, NET-05, NET-07, NET-08]
dependency_graph:
  requires: []
  provides: [mqtt-topic-acl, websocket-origin-validation, graphql-depth-limiting, coap-dtls-enforcement]
  affects: [AedsCore, UltimateConnector, UltimateStorage]
tech_stack:
  added: []
  patterns: [topic-acl, origin-validation, query-depth-limiting, dtls-enforcement, csprng-message-ids]
key_files:
  created: []
  modified:
    - Plugins/DataWarehouse.Plugins.AedsCore/ControlPlane/MqttControlPlanePlugin.cs
    - Plugins/DataWarehouse.Plugins.AedsCore/ControlPlane/WebSocketControlPlanePlugin.cs
    - Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Protocol/GraphQlConnectionStrategy.cs
    - Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/IoT/CoApConnectionStrategy.cs
    - Plugins/DataWarehouse.Plugins.UltimateStorage/Strategies/Connectors/GraphQlConnectorStrategy.cs
decisions:
  - "MQTT topic ACL enforced client-side as defense-in-depth (client plugin, not broker)"
  - "WebSocket origin validation uses same-origin default with configurable allowed origins"
  - "GraphQL introspection disabled by default; requires explicit EnableIntrospection=true"
  - "CoAP DTLS fail-safe: throws on init unless AllowInsecureCoap explicitly set to true"
  - "CoAP message IDs randomized via CSPRNG (System.Security.Cryptography.RandomNumberGenerator)"
metrics:
  duration: "13 minutes"
  completed: "2026-02-19T08:59:24Z"
  tasks_completed: 2
  tasks_total: 2
  files_modified: 5
---

# Phase 53 Plan 08: MQTT/WebSocket/GraphQL/CoAP Protocol Hardening Summary

Application-layer protocol security controls for MQTT topic ACL, WebSocket origin validation, GraphQL depth limiting, and CoAP DTLS enforcement, resolving NET-04/05/07/08 pentest findings.

## Findings Resolved

| Finding | CVSS | Protocol | Status | Control Added |
|---------|------|----------|--------|---------------|
| NET-04 | 7.5 | MQTT | RESOLVED | Topic-level ACL with role-based authorization |
| NET-05 | 7.5 | CoAP | MITIGATED | DTLS requirement with fail-safe + CSPRNG message IDs |
| NET-07 | 6.1 | WebSocket | RESOLVED | Origin header validation with same-origin default |
| NET-08 | 5.3 | GraphQL | RESOLVED | Query depth limiting + introspection control |

## Task Execution

### Task 1: MQTT Topic ACL and WebSocket Origin Validation
**Commit:** `53318ba7`

**MQTT (NET-04):**
- Added role-based topic ACL system with regex pattern matching
- Three roles defined: `client` (default), `admin`, `manifest-publisher`
- Client role scoped to own namespace: `aeds/client/{clientId}/#`, subscribed channels, own heartbeat
- Authorization enforced on subscribe, publish, heartbeat, and message receive paths
- Wildcard publish blocked (no `#` or `+` in publish topics)
- All authorization denials logged for audit

**WebSocket (NET-07):**
- Origin validation derived from server URL (ws:// -> http://, wss:// -> https://)
- Default behavior: same-origin only (rejects all cross-origin)
- Configurable allowed origins via `AddAllowedOrigin()` method
- Origin header set on outbound WebSocket connections
- Message envelope origin field validated on received messages
- Message size validation (4MB max) to prevent oversized payload attacks

### Task 2: GraphQL Depth Limiting and CoAP DTLS
**Commit:** `54aad55a`

**GraphQL (NET-08) - Applied to both connector and storage strategies:**
- Query depth limiting: configurable `MaxQueryDepth` (default: 10), rejects deeper queries
- Introspection control: `EnableIntrospection` (default: false), blocks `__schema` and `__type` in production
- Query complexity analysis: `MaxQueryComplexity` (default: 1000), exponential cost per nesting level
- Connection test uses `__typename` when introspection disabled (safe health check)
- List operation returns empty when introspection is disabled

**CoAP (NET-05):**
- DTLS requirement enforced: `UseDtls` (default: true)
- Fail-safe: `AllowInsecureCoap` (default: false) -- throws `InvalidOperationException` on init if DTLS unavailable and insecure not explicitly allowed
- Clear error message with three remediation options (IPSec/VPN, explicit allow, DTLS proxy)
- Security warning logged when insecure mode is explicitly enabled
- CSPRNG-randomized initial message ID via `RandomNumberGenerator.Fill()` to prevent sequential ID prediction

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing Critical Functionality] GraphQL security applied to UltimateStorage connector**
- **Found during:** Task 2
- **Issue:** Plan only mentioned GraphQlConnectionStrategy in UltimateConnector, but GraphQlConnectorStrategy in UltimateStorage also uses introspection queries without protection
- **Fix:** Applied same depth limiting and introspection control to the storage connector strategy
- **Files modified:** Plugins/DataWarehouse.Plugins.UltimateStorage/Strategies/Connectors/GraphQlConnectorStrategy.cs
- **Commit:** 54aad55a

**2. [Rule 3 - Blocking Issue] Logger not accessible from strategy base classes**
- **Found during:** Task 2
- **Issue:** `ConnectionStrategyBase._logger` is private, `UltimateStorageStrategyBase` has no logger. Plan assumed `Logger?.LogWarning()` would work.
- **Fix:** Used `System.Diagnostics.Trace.TraceWarning()` for security warnings in strategy classes where ILogger is not exposed
- **Files modified:** All strategy files in Task 2
- **Commit:** 54aad55a

## Commits

| Hash | Message |
|------|---------|
| `53318ba7` | feat(53-08): add MQTT topic ACL and WebSocket origin validation |
| `54aad55a` | feat(53-08): add GraphQL depth limiting and CoAP DTLS enforcement |

## Verification

- Full solution build: 0 errors, 0 warnings
- MQTT topic ACL patterns found (7 matches in MqttControlPlanePlugin.cs)
- WebSocket origin validation patterns found (24 matches in WebSocketControlPlanePlugin.cs)
- GraphQL depth limiting patterns found (6 depth + 20 introspection matches)
- CoAP DTLS enforcement patterns found (22 matches in CoApConnectionStrategy.cs)

## Self-Check: PASSED

- All 5 modified files exist on disk
- All 2 task commits verified in git log (53318ba7, 54aad55a)
- Summary file exists at .planning/phases/53-security-wiring/53-08-SUMMARY.md
