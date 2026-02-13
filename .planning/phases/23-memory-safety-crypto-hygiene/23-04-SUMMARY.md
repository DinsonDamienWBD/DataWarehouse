---
phase: 23-memory-safety-crypto-hygiene
plan: 04
subsystem: security
tags: [key-rotation, algorithm-agility, hmac-sha256, authenticated-message-bus, replay-protection]

# Dependency graph
requires:
  - phase: 23-memory-safety-crypto-hygiene
    provides: IDisposable on PluginBase (Plan 23-01), ZeroMemory patterns (Plan 23-02)
provides:
  - IKeyRotationPolicy with time/usage/volume/event triggers
  - IKeyRotatable interface extending IKeyStore with rotation operations
  - ICryptographicAlgorithmRegistry with DefaultCryptographicAlgorithmRegistry (SHA256/384/512)
  - IAuthenticatedMessageBus with HMAC-SHA256 signing and nonce-based replay protection
  - PluginMessage Signature/Nonce/ExpiresAt fields for authenticated messaging
affects: [24-plugin-hierarchy, 25a-strategy-hierarchy, 28-distributed-infrastructure]

# Tech tracking
tech-stack:
  added: []
  patterns: [key-rotation-policy, algorithm-agility-registry, authenticated-message-bus, hmac-signing, replay-protection]

key-files:
  created:
    - DataWarehouse.SDK/Security/IKeyRotationPolicy.cs
    - DataWarehouse.SDK/Security/CryptographicAlgorithmRegistry.cs
  modified:
    - DataWarehouse.SDK/Contracts/IMessageBus.cs
    - DataWarehouse.SDK/Utilities/PluginDetails.cs
    - DataWarehouse.SDK/Security/IKeyStore.cs

key-decisions:
  - "IKeyRotatable extends IKeyStore (not PluginBase) for clean composition"
  - "DefaultCryptographicAlgorithmRegistry uses one-shot static APIs (SHA256.HashData, HMACSHA256.HashData) for zero allocation"
  - "Authentication is opt-in per topic via ConfigureAuthentication -- not all topics need signing"
  - "PluginMessage auth fields use set (not init) since bus populates them after construction"
  - "All changes purely additive -- zero breaking changes to existing IMessageBus consumers"

patterns-established:
  - "Key rotation: IKeyRotationPolicy evaluates KeyRotationMetadata, returns KeyRotationDecision with urgency"
  - "Algorithm agility: ICryptographicAlgorithmRegistry for hash/HMAC selection, no hardcoded algorithms"
  - "Message auth: IAuthenticatedMessageBus signs on publish, verifies on receive, per-topic configuration"
  - "Replay protection: nonce + ExpiresAt + bounded nonce window for duplicate detection"

# Metrics
duration: ~8min
completed: 2026-02-14
---

# Phase 23 Plan 04: Key Rotation Policy, Algorithm Agility, Authenticated Message Bus Summary

**IKeyRotationPolicy with 4 trigger types, ICryptographicAlgorithmRegistry for hash/HMAC agility, IAuthenticatedMessageBus with HMAC-SHA256 and replay protection**

## Performance

- **Duration:** ~8 min
- **Tasks:** 2
- **Files modified:** 5 (2 created, 3 modified)

## Accomplishments
- Created IKeyRotationPolicy with configurable triggers (time-based, usage-based, data-volume, event-based)
- Created IKeyRotatable extending IKeyStore with RotateKeyAsync, GetRotationMetadataAsync, EvaluateAllKeysAsync
- Created ICryptographicAlgorithmRegistry with DefaultCryptographicAlgorithmRegistry (SHA256/384/512 hash and HMAC)
- Created IAuthenticatedMessageBus extending IMessageBus with per-topic HMAC signing, key rotation, replay detection
- Added Signature, Nonce, ExpiresAt properties to PluginMessage for authenticated messaging
- Added MessageAuthenticationOptions, MessageVerificationResult, MessageVerificationFailure types
- Added AuthenticatedMessageTopics (security.*, keystore.*, plugin.*, system.* prefixes)
- Added seealso reference from IKeyStore to IKeyRotatable for discoverability
- All changes 100% additive with zero breaking changes

## Task Commits

1. **Tasks 1-2: Key rotation + algorithm agility + authenticated message bus** - `34b36c3` (feat)

## Files Created/Modified
- `DataWarehouse.SDK/Security/IKeyRotationPolicy.cs` - IKeyRotationPolicy, IKeyRotatable, KeyRotationDecision, KeyRotationTrigger, KeyRotationMetadata, KeyRotationResult, RotationUrgency
- `DataWarehouse.SDK/Security/CryptographicAlgorithmRegistry.cs` - ICryptographicAlgorithmRegistry, DefaultCryptographicAlgorithmRegistry
- `DataWarehouse.SDK/Contracts/IMessageBus.cs` - IAuthenticatedMessageBus, MessageAuthenticationOptions, MessageVerificationResult, MessageVerificationFailure, AuthenticatedMessageTopics, security event topics
- `DataWarehouse.SDK/Utilities/PluginDetails.cs` - PluginMessage.Signature, .Nonce, .ExpiresAt
- `DataWarehouse.SDK/Security/IKeyStore.cs` - seealso reference to IKeyRotatable

## Decisions Made
- IKeyRotatable extends IKeyStore directly (not via PluginBase) for clean composition -- any key store can opt into rotation
- DefaultCryptographicAlgorithmRegistry uses .NET one-shot static APIs (SHA256.HashData, HMACSHA256.HashData) for zero-allocation hashing
- IAuthenticatedMessageBus is opt-in per topic -- ConfigureAuthentication(topic, options) enables signing only where needed
- PluginMessage auth fields use `set` instead of `init` because the message bus populates them post-construction
- Grace period on signing key rotation (RotateSigningKey) allows in-flight messages to verify against old key

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
None.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Key rotation contracts ready for implementation in key store plugins
- Algorithm agility registry ready for integration into encryption pipelines
- Authenticated message bus contract ready for kernel implementation
- All Phase 23 requirements complete -- ready for Phase 24 (Plugin Hierarchy)

---
*Phase: 23-memory-safety-crypto-hygiene*
*Completed: 2026-02-14*
