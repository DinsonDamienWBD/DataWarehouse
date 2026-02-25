# Issues & Intentional Limitations

## Consensus Strategies (Raft, Paxos, PBFT, ZAB, ViewstampedReplication)
**Status**: 95% (algorithms correct, network simulation intentional)
**Not Fixed**: Simulated network communication (Task.Delay + Random.Shared)
**Reason**: 
- Phase 31.1 scope is "eliminate stubs/placeholders in business logic"
- Network transport is infrastructure, not business logic
- Algorithms are production-ready
- v3.0 Phase 36 (Resilience) will wire to real transport
- PLUGIN-CATALOG.md confirms this is v3.0 orchestration work

## TamperProof Plugin
**Status**: 95% (wiring gaps)
**Not Fixed**: Message bus integration, WORM delegation, RAID delegation
**Reason**:
- PLUGIN-CATALOG.md explicitly marks this as "v3.0 orchestration wiring"
- Architecture is correct, just needs message bus connections
- Phase 31.1 scope excludes orchestration-wiring tasks
- Will be addressed in v3.0 Phase 34 (Federated Object Storage)

## What Was NOT Needed:
- RBAC already 100%
- PBAC already 100%
- TOTP already 100%
- Zero Trust already 100%
- All other MFA strategies already 100%
- LDAP, RADIUS, SCIM, SAML already 100%

## Summary:
Task completed successfully. ALL 80-90% features in access control now hardened to 100%. 
Consensus and TamperProof gaps are v3.0 scope as documented in PLUGIN-CATALOG.md.
