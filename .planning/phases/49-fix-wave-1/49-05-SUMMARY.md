# 49-05: P1 Domain-Specific Findings Inventory

**Status:** Inventory of domain-specific issues from Phase 44 deep audits and Phase 45-46 verification

## Domain 1-2: Data Pipeline + Storage (Phase 44-01)

| ID | Finding | Severity | Location | Effort |
|----|---------|----------|----------|--------|
| M-2 | RAID scrubbing virtual methods should be abstract (silent no-op) | MEDIUM | RaidStrategyBase.cs:277-290 | 2h |
| M-3 | RAID simulation stubs in production code (Task.CompletedTask, new byte[]) | MEDIUM | StandardRaidStrategies.cs:152-162 | 16-24h |
| L-5 | VDE indirect block support missing (file size limited to direct blocks) | LOW | VirtualDiskEngine.cs | 40-60h |

## Domain 3: Security + Crypto (Phase 44-02)

| ID | Finding | Severity | Location | Effort |
|----|---------|----------|----------|--------|
| M-1 | ChaCha20-HMAC should be deprecated (prefer ChaCha20-Poly1305) | MEDIUM | ChaCha20Strategy | 1h (docs) |
| M-3 | MAC strategy not audited | MEDIUM | AccessControl MAC strategy | 2h (audit) |
| M-4 | DAC strategy not audited | MEDIUM | AccessControl DAC strategy | 2h (audit) |
| M-5 | Key revocation mechanism not found | MEDIUM | UltimateKeyManagement | 8-12h |
| L-1 | Key destroy missing explicit memory clearing in plugin Dispose | LOW | UltimateKeyManagement | 2h |

## Domain 4: Media + Formats (Phase 44-03)

| ID | Finding | Severity | Location | Effort |
|----|---------|----------|----------|--------|
| M-1 | Mock transcoding (returns FFmpeg args, not real media) | MEDIUM | MediaTranscodingPlugin | 160-240h |
| M-3 | Metadata preservation not implemented (EXIF, ID3, XMP) | MEDIUM | MediaTranscodingPlugin | 12-16h |

## Domain 5: Distributed Systems (Phase 44-04)

| ID | Finding | Severity | Location | Effort |
|----|---------|----------|----------|--------|
| M-8 | 3 CRDT types missing (G-Set, 2P-Set, RGA) | MEDIUM | SdkCrdtTypes.cs | 12-16h |
| L-10 | Linearizability not provided (eventual consistency only) | LOW | CrdtReplicationSync | Document |
| L-11 | No backpressure when followers lag | LOW | ReplicationLagMonitoringFeature | 8h |

## Domain 6-7: Hardware + Edge + IoT (Phase 44-05)

| ID | Finding | Severity | Location | Effort |
|----|---------|----------|----------|--------|
| M-1 | NVMe VM detection too conservative | MEDIUM | NvmePassthrough.cs:128-135 | 2h |
| M-2 | NUMA API deferred (30-50% throughput loss on multi-socket) | MEDIUM | NumaAllocator.cs:149-157 | 16-24h |
| M-3 | QAT encryption not implemented | MEDIUM | QatAccelerator.cs:376-401 | 16-24h |
| M-4 | Memory exhaustion throws instead of evicting | MEDIUM | BoundedMemoryRuntime.cs:75 | Document |

## Domain 8-10: AEDS + Air-Gap + Filesystem (Phase 44-06)

| ID | Finding | Severity | Location | Effort |
|----|---------|----------|----------|--------|
| M-1 | Launcher profile system not located | MEDIUM | Launcher directory | 4-8h |
| M-2 | FUSE/WinFsp mount cycle E2E integration pending | MEDIUM | FuseDriver | 8-16h |

## Domain 11-13: Compute + Transport + Intelligence (Phase 44-07)

| ID | Finding | Severity | Location | Effort |
|----|---------|----------|----------|--------|
| M-1 | WASM via CLI tools (~100ms overhead per execution) | MEDIUM | UltimateCompute | Future (native lib) |
| M-2 | Bandwidth estimation is heuristic, not measured | MEDIUM | AdaptiveTransport | 4-8h |
| M-3 | SQL-over-object execution is mocked | MEDIUM | UltimateInterface SqlInterfaceStrategy | 40-60h |
| M-4 | AI providers use raw HttpClient (not official SDKs) | MEDIUM | UltimateIntelligence | 20-30h |
| M-5 | Self-emulating object lifecycle (snapshot/rollback) missing | MEDIUM | UltimateInterface | 16-24h |

## Domain 14-16: Observability + Governance + Cloud (Phase 44-09)

| ID | Finding | Severity | Location | Effort |
|----|---------|----------|----------|--------|
| H-1 | Multi-cloud storage stubs (empty MemoryStream) | HIGH | UltimateMultiCloud AWS/Azure/GCP | 40-60h |
| H-2 | Multi-cloud compute stubs (fake instance IDs) | HIGH | UltimateMultiCloud compute | 20-30h |
| H-3 | No cloud SDK NuGet dependencies | HIGH | UltimateMultiCloud | Architectural |
| M-1 | Compliance handler hardcodes true | MEDIUM | UltimateDataGovernance | 2h |
| M-2 | Data catalog lineage single-hop only (no BFS) | MEDIUM | UltimateDataCatalog | 8-12h |
| M-3 | No deployment manifests (Docker/K8s/Helm) | MEDIUM | No Deployment/ directory | 8-16h |

## Domain 17: CLI/GUI (Phase 44-08)

| ID | Finding | Severity | Location | Effort |
|----|---------|----------|----------|--------|
| H-1 | Dynamic command wiring gap (hot-reload missing final wire) | HIGH | CommandExecutor/DynamicCommandRegistry | 4-8h |
| M-1 | Configure mode not enforced | MEDIUM | CLI mode guard | 2-4h |
| M-2 | AI NLP fallback dead code (TryGetAIRegistry always null) | MEDIUM | NaturalLanguageProcessor | 4h |
| M-3 | Dynamic completions excluded from shell scripts | MEDIUM | ShellCompletionGenerator | 2h |
| M-4 | No interactive install wizard | MEDIUM | CLI install pipeline | 8-16h |
| M-5 | No plugin-contributed dashboard panels | MEDIUM | GUI static pages | 16-24h |

## Tier Verification Gaps (Phase 45)

| Tier | Gap | Severity | Effort |
|------|-----|----------|--------|
| 5-6 | HSM crypto operations are stubs (PKCS#11 throws) | HIGH | 40-60h |
| 5-6 | Secure deletion not fully wired (message bus pending) | MEDIUM | 8-12h |
| 5-6 | InMemoryAuditTrail evicts at 100K (breaks immutability) | MEDIUM | Configure WORM |
| 7 | Multi-Raft is single-group only (no multi-group coordinator) | MEDIUM | 40-60h |
| 7 | Auto-scaler only has InMemory stub | MEDIUM | 20-30h |
| 7 | Cloud adapters are strategy stubs | HIGH | See Domain 16 |
| 7 | Multi-tenant isolation at context level only | MEDIUM | 20-30h |

## Performance Findings (Phase 46)

| ID | Finding | Severity | Location | Effort |
|----|---------|----------|----------|--------|
| P-1 | VDE single write lock bottleneck | MEDIUM | VirtualDiskEngine.cs | 16-24h |
| P-2 | O(N) bitmap scan for block allocation | MEDIUM | BitmapAllocator.cs | 8-12h |
| P-3 | MemoryStream-based reads buffer entire objects | MEDIUM | VdeStorageStrategy | 8-12h |
| P-4 | Byte-array duplication in encryption envelope | MEDIUM | EncryptionPluginBase | 4-8h |

## Cross-Platform Test Gaps (Phase 48-04)

- 53 files use OS-specific detection, 0 cross-platform tests
- 41 files use hardware probes (WMI/sysfs/system_profiler), all tests use NullHardwareProbe

## Summary

- **Total domain-specific findings:** ~65 items across 17 domains
- **HIGH findings:** 7 (multi-cloud stubs, HSM stubs, dynamic command wiring, cloud SDK gap)
- **MEDIUM findings:** ~45 (spread across all domains)
- **LOW findings:** ~13
- **Estimated total effort:** ~700-1100 hours (heavily weighted by multi-cloud and transcoding implementations)
