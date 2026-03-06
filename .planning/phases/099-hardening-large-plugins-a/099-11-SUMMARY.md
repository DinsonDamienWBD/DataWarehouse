---
phase: 099-hardening-large-plugins-a
plan: 11
subsystem: connector
tags: [hardening, tdd, ultimateconnector, messaging, nosql, observability, protocol, saas, cross-plugin]

requires:
  - phase: 099-10
    provides: "UltimateConnector findings 1-360 hardened"
provides:
  - "UltimateConnector findings 361-542 hardened with 86 tests"
  - "UltimateConnector FULLY HARDENED (542/542 findings, 305 total tests)"
  - "Phase 099 COMPLETE: 2,347 findings across 3 plugins fully hardened"
affects: [100-01]

tech-stack:
  added: []
  patterns: [notsupportedexception-for-unparseable-protocols, credential-validation, otlp-resourcespans]

key-files:
  created:
    - DataWarehouse.Hardening.Tests/UltimateConnector/Findings361To420Tests.cs
    - DataWarehouse.Hardening.Tests/UltimateConnector/Findings421To480Tests.cs
    - DataWarehouse.Hardening.Tests/UltimateConnector/Findings481To542Tests.cs
  modified:
    - Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Messaging/ApachePulsarConnectionStrategy.cs
    - Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Messaging/AwsEventBridgeConnectionStrategy.cs
    - Plugins/DataWarehouse.Plugins.UltimateConnector/Strategies/Observability/TempoConnectionStrategy.cs

key-decisions:
  - "ApachePulsar ExtractPulsarMessagePayload: throw NotSupportedException (requires official Apache.Pulsar.Client NuGet for binary frame parsing)"
  - "AwsEventBridge: apply AuthCredential as Bearer token in ConnectCoreAsync"
  - "Tempo PushTracesAsync: correct OTLP format from {batches} to {resourceSpans}"

requirements-completed: [HARD-01, HARD-02, HARD-03, HARD-04, HARD-05]

duration: 13m
completed: 2026-03-06
---

# Phase 099 Plan 11: UltimateConnector Hardening Findings 361-542 Summary

**86 hardening tests covering 182 findings: Pulsar NotSupportedException for raw frame parsing, EventBridge credential validation, Tempo OTLP resourceSpans format fix -- UltimateConnector FULLY HARDENED (542/542), Phase 099 COMPLETE (2,347/2,347 across 3 plugins)**

## Performance

- **Duration:** 13 min
- **Started:** 2026-03-06T01:20:52Z
- **Completed:** 2026-03-06T01:33:59Z
- **Tasks:** 2/2
- **Files modified:** 6 (3 test files created + 3 production files modified)

## Accomplishments

- 182 findings (361-542) processed across Legacy, Messaging, NoSQL, Observability, Protocol, SaaS, SpecializedDb, Plugin root, ConnectionPoolManager, and cross-plugin categories
- Fix: ApachePulsar ExtractPulsarMessagePayload now throws NotSupportedException instead of using magic offset Math.Min(20, frame.Length-1)
- Fix: AwsEventBridge ConnectCoreAsync now applies AuthCredential as Bearer token
- Fix: Tempo PushTracesAsync uses correct OTLP format (resourceSpans instead of batches)
- 86 new tests across 3 files covering all finding categories
- Total UltimateConnector tests: 305 (all passing)
- UltimateConnector: FULLY HARDENED (542/542 findings across 10 test files)
- Phase 099: COMPLETE (2,347/2,347 findings across UltimateStorage, UltimateIntelligence, UltimateConnector)

## Test Coverage by Category

| Category | Findings | Tests |
|----------|----------|-------|
| Legacy (Tn3270/Tn5250/VSAM) | 361 | 3 (Theory) |
| Messaging (ActiveMQ through ZeroMQ) | 362-400 | 20 |
| NoSQL (ArangoDB through ScyllaDB) | 401-419 | 11 |
| Observability (AppDynamics through Zabbix) | 420-451 | 18 |
| Protocol (DNS through WebSocket) | 452-467 | 6 |
| SaaS (Airtable through Zendesk) | 468-493 | 14 |
| SpecializedDb (Druid, Ignite, ClickHouse) | 494-496 | 1 |
| Plugin root + ConnectionPoolManager | 497-507 | 4 |
| Cross-plugin (Intelligence, Storage) | 508-531 | 7 |
| InspectCode findings | 532-542 | 5 |

## Task Commits

1. **Task 1: TDD hardening for UltimateConnector findings 361-542** - `77d00c99` (test+fix)
2. **Task 2: Build verification** - verified (0 errors, 305 UltimateConnector tests pass)

## Files Created/Modified

- `DataWarehouse.Hardening.Tests/UltimateConnector/Findings361To420Tests.cs` - 37 tests for findings 361-420
- `DataWarehouse.Hardening.Tests/UltimateConnector/Findings421To480Tests.cs` - 25 tests for findings 421-480
- `DataWarehouse.Hardening.Tests/UltimateConnector/Findings481To542Tests.cs` - 24 tests for findings 481-542
- `Strategies/Messaging/ApachePulsarConnectionStrategy.cs` - ExtractPulsarMessagePayload throws NotSupportedException
- `Strategies/Messaging/AwsEventBridgeConnectionStrategy.cs` - credential validation in ConnectCoreAsync
- `Strategies/Observability/TempoConnectionStrategy.cs` - OTLP resourceSpans format

## Deviations from Plan

None - plan executed exactly as written.

## Verification Results

- `dotnet build DataWarehouse.slnx --no-restore` - 0 errors, 0 warnings
- `dotnet test --filter "FullyQualifiedName~UltimateConnector"` - 305 passed, 0 failed

## Self-Check: PASSED
