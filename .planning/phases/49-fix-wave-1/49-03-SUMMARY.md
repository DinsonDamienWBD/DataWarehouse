# 49-03: P1 Security Findings Inventory

**Status:** Inventory of high-severity security findings requiring remediation before production

## P1 Security Findings from Phase 43 (Automated Scan)

### Insecure Random Usage (CWE-338)

| Category | Count | Risk | Recommendation |
|----------|-------|------|----------------|
| Raft election timing (predictable timeouts) | 1 | HIGH (consensus manipulation) | Replace with RandomNumberGenerator.GetInt32 | 2h |
| Metric sampling bias (StatsDStrategy) | 1 | MEDIUM (skewed observability) | Replace Random.Shared with CSPRNG | 1h |
| UI/Dashboard mock data | 120+ | LOW (demo data) | P2 -- centralize in MockDataGenerator | Defer |
| Test/sample data generation | 40+ | LOW (test fixtures) | P2 -- no security impact | Defer |

**Actionable P1 Random fixes:** 2 instances (Raft + StatsD), rest are P2/benign

### Null Suppression (CWE-476) -- 75 occurrences

| Category | Count | Action |
|----------|-------|--------|
| Security plugins (AccessControl, TamperProof, KeyManagement) | ~10 | **P1: Audit and replace** |
| SDK infrastructure (initialization order dependent) | ~46 | P2: Replace with required properties |
| Plugin initialization (SDK lifecycle dependent) | ~29 | P2: Replace with explicit null checks |

**Actionable P1:** Audit ~10 null! in security-critical plugins (2-4h)

### Exception Swallowing (CWE-390)

| Category | Count | Action |
|----------|-------|--------|
| Security plugins (AccessControl, TamperProof, Raft) | ~6 bare catch{} | **P1: Add logging** |
| I/O retry loops (MQTT, CoAP) | ~10 | P2: Acceptable but add logging |
| Scanning/detection loops | ~20+ | P2: Expected failure pattern |

**Actionable P1:** 6 bare catch blocks in security code (2h)

## P1 Security Findings from Phase 47 (Penetration Testing)

| ID | Finding | Location | CVSS | Effort |
|----|---------|----------|------|--------|
| MED-01 | No secure deletion for on-disk data | File.Delete throughout | 6.5 | 8-12h |
| MED-02 | security.json has default file permissions | DataWarehouseHost.cs | 6.2 | 2h |
| MED-03 | Cloud connectors skip cert validation (public internet) | DynamoDB, CosmosDB strategies | 6.0 | 2h |
| MED-04 | No rate limiting on Launcher API | LauncherHttpServer.cs | 5.5 | 4-6h |
| MED-05 | mTLS not universally enforced for inter-node | Multiple transport files | 5.0 | 8-12h |
| MED-06 | Exception swallowing in security plugins (6 bare catch) | CanaryStrategy, PBAC | 4.5 | 2h |

## P1 Security Findings from Phase 44 (Domain Audit)

| ID | Finding | Source | Location | Effort |
|----|---------|--------|----------|--------|
| H-1 | ML-KEM strategy IDs claim "ml-kem" but implement NTRU | 44-02 | UltimateEncryption NTRU strategies | 2h (rename) |
| H-2 | Merkle proof not stored in blockchain anchors | 44-02 | UltimateBlockchain | 8-16h |
| H-3 | Digital signatures not verified (RSA-PSS, ECDSA, EdDSA) | 44-02 | UltimateDataIntegrity | 4-8h (audit) |
| M-6 | ABAC regex without timeout (ReDoS risk) | 44-02 | AbacStrategy.cs:346 | 1h |
| M-E1 | Election timeout randomization missing | 44-04 | UltimateConsensus | 2h |
| M-E4 | Follower election timeout missing (no auto-recovery) | 44-04 | UltimateConsensus | 4h |

## Summary

- **Actionable P1 Security Items:** ~20 distinct findings
- **Quick Wins (< 2h each):** Raft Random fix, StatsD Random fix, ABAC regex timeout, ML-KEM rename, security.json permissions, exception logging (6 items)
- **Medium Effort (2-8h):** Null! audit in security plugins, Launcher rate limiting, password file permissions, cloud cert validation
- **Larger Effort (8-16h):** Secure deletion, mTLS enforcement, Merkle proof storage, digital signature audit
- **Total Estimated Effort:** 50-80 hours
