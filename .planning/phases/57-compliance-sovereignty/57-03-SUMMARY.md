---
phase: 57-compliance-sovereignty
plan: 03
subsystem: compliance
tags: [sovereignty-zones, tag-matching, declarative-governance, zone-registry, gdpr, hipaa, pipl, apac, cross-border]

# Dependency graph
requires:
  - 57-01 (ISovereigntyZone, ZoneAction, CompliancePassport types)
provides:
  - SovereigntyZone class implementing ISovereigntyZone with tag-based rule evaluation
  - TagPatternMatcher with exact, prefix-wildcard, suffix-wildcard matching
  - SovereigntyZoneBuilder for fluent zone construction
  - DeclarativeZoneRegistry with 31 pre-configured sovereignty zones
  - Jurisdiction-based zone lookup, custom zone registration, zone deactivation
affects: [57-04, 57-05, 57-06]

# Tech stack
added: []
patterns: [builder-pattern, tag-pattern-matching, most-restrictive-wins]

# Key files
created:
  - Plugins/DataWarehouse.Plugins.UltimateCompliance/Strategies/SovereigntyMesh/SovereigntyZoneStrategy.cs
  - Plugins/DataWarehouse.Plugins.UltimateCompliance/Strategies/SovereigntyMesh/DeclarativeZoneRegistry.cs
modified: []

# Decisions
key-decisions:
  - Severity order for most-restrictive-wins: Allow < RequireApproval < RequireEncryption < RequireAnonymization < Quarantine < Deny
  - Passport-aware adjustment: full coverage de-escalates by 1 level, missing/incomplete escalates by 1 level
  - Industry and supranational zones use "*" wildcard jurisdiction to match all countries

# Metrics
duration: 223s
completed: 2026-02-19T17:37:17Z
tasks: 2/2
files-created: 2
files-modified: 0
total-lines: ~908
---

# Phase 57 Plan 03: Declarative Sovereignty Zones Summary

Declarative sovereignty zones with tag-based rule evaluation, wildcard pattern matching, passport-aware action escalation/de-escalation, and 31 pre-configured zones spanning EU, Americas, APAC, ME/Africa, Industry, and Supranational regulatory regions.

## What Was Built

### Task 1: SovereigntyZoneStrategy (SovereigntyZone + Builder + TagPatternMatcher)

**SovereigntyZone** implements `ISovereigntyZone` with full evaluation pipeline:
1. Inactive zones return Allow immediately (no blocking)
2. Context tags matched against ActionRules using TagPatternMatcher
3. Multiple matching rules resolved via most-restrictive-wins (Deny > Quarantine > RequireAnonymization > RequireEncryption > RequireApproval > Allow)
4. Passport-aware adjustment: if passport covers all required regulations, action de-escalates one level; if missing/incomplete, escalates one level

**TagPatternMatcher** supports three modes:
- Exact: `"classification:secret"` matches `"classification:secret"`
- Prefix wildcard: `"pii:*"` matches `"pii:name"`, `"pii:email"`
- Suffix wildcard: `"*:eu"` matches `"jurisdiction:eu"`, `"region:eu"`
- All matching is case-insensitive

**SovereigntyZoneBuilder** provides fluent API: `.WithId()`, `.WithName()`, `.InJurisdictions()`, `.Requiring()`, `.WithRule()`, `.Build()`

### Task 2: DeclarativeZoneRegistry (31 Pre-configured Zones)

Extends `ComplianceStrategyBase` with StrategyId `"sovereignty-zone-registry"`.

**EU Zones (6):** eu-gdpr-zone, eu-eea-zone, eu-dora-zone, eu-nis2-zone, eu-ai-act-zone, eu-ehealth-zone
**Americas Zones (5):** us-hipaa-zone, us-fedramp-zone, us-ccpa-zone, br-lgpd-zone, ca-pipeda-zone
**APAC Zones (8):** cn-pipl-zone, jp-appi-zone, sg-pdpa-zone, au-privacy-zone, kr-pipa-zone, in-pdpb-zone, hk-pdpo-zone, tw-pdpa-zone
**ME/Africa Zones (5):** ae-pdpl-zone, sa-pdpl-zone, za-popia-zone, qa-pdpl-zone, ng-ndpr-zone
**Industry Zones (4):** financial-global-zone, healthcare-global-zone, iso27001-zone, worm-sec-zone
**Supranational Zones (3):** five-eyes-zone, apec-cbpr-zone, adequacy-zone

Public API:
- `RegisterZoneAsync` - add custom zones
- `GetZoneAsync` - retrieve by ID
- `GetZonesForJurisdictionAsync` - find all zones for a jurisdiction code
- `GetAllZonesAsync` - return all registered zones
- `DeactivateZoneAsync` - mark zone inactive

`CheckComplianceCoreAsync` validates cross-jurisdiction transfers by checking zone coverage and regulation alignment.

## Deviations from Plan

None - plan executed exactly as written.

## Verification

- Build: 0 errors, 0 warnings
- SovereigntyZoneStrategy.cs: 325 lines (min 200 required)
- DeclarativeZoneRegistry.cs: 583 lines (min 300 required)
- 31 zones registered covering 6 regulatory regions
- Tag pattern matching with exact, prefix-wildcard, suffix-wildcard support
- Passport-aware escalation/de-escalation implemented
- Thread-safe via ConcurrentDictionary
