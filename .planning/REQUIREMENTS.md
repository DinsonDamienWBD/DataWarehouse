# Requirements: DataWarehouse v6.0

**Defined:** 2026-02-20
**Core Value:** Every feature has a multi-level, cascade-aware, AI-tunable policy with format-native VDE integration — no feature is "dumb" or "one-size-fits-all."

## v6.0 Requirements

Requirements for v6.0 Intelligent Policy Engine & Composable VDE. Each maps to roadmap phases.

### SDK Foundation (SDKF)

- [ ] **SDKF-01**: PolicyEngine SDK contracts defined (IPolicyEngine, IEffectivePolicy, IPolicyStore, IPolicyPersistence)
- [ ] **SDKF-02**: FeaturePolicy model with intensity levels, cascade rules, and AI autonomy per feature
- [ ] **SDKF-03**: PolicyLevel enum (Block, Chunk, Object, Container, VDE) with resolution context
- [ ] **SDKF-04**: CascadeStrategy enum (MostRestrictive, Enforce, Inherit, Override, Merge) per feature
- [ ] **SDKF-05**: AiAutonomyLevel enum (ManualOnly, Suggest, SuggestExplain, AutoNotify, AutoSilent)
- [ ] **SDKF-06**: OperationalProfile model with named presets (Speed, Balanced, Standard, Strict, Paranoid) and custom profiles
- [ ] **SDKF-07**: AuthorityChain model (Admin → AI Emergency → Super Admin Quorum)
- [ ] **SDKF-08**: QuorumPolicy with configurable required approvals, approval window, and action list
- [ ] **SDKF-09**: PolicyResolutionContext carrying path, user, hardware, and security context
- [ ] **SDKF-10**: PluginBase extended with PolicyContext (every plugin is policy-aware)
- [ ] **SDKF-11**: IntelligenceAwarePluginBase with IAiHook, ObservationEmitter, RecommendationReceiver
- [ ] **SDKF-12**: UltimateIntelligencePlugin inherits PluginBase directly (NOT IntelligenceAwarePluginBase) to prevent AI-observing-AI loops

### Cascade Resolution Engine (CASC)

- [ ] **CASC-01**: Cascade resolution algorithm resolves effective policy at any path (VDE/Container/Object/Chunk/Block)
- [ ] **CASC-02**: Per-category default cascade strategy applied (Security=MostRestrictive, Performance=Override, Governance=Merge, etc.)
- [ ] **CASC-03**: User can override cascade strategy per feature per level
- [ ] **CASC-04**: Empty intermediate levels skipped in resolution (Object inherits from VDE if Container has no override)
- [ ] **CASC-05**: Enforce at higher level ALWAYS wins over Override at lower level
- [ ] **CASC-06**: In-flight operations use policy snapshot from start time (double-buffered versioned cache)
- [ ] **CASC-07**: Circular reference detection during policy store validation
- [ ] **CASC-08**: Per-tag-key conflict resolver for Merge strategy (configurable: MostRestrictive, Closest, Union)

### Performance Optimization (PERF)

- [ ] **PERF-01**: MaterializedPolicyCache pre-computes effective policies at VDE open time
- [ ] **PERF-02**: BloomFilterSkipIndex for O(1) "has override?" checks per VDE (false positive ~1%, zero false negatives)
- [ ] **PERF-03**: CompiledPolicyDelegate JIT-compiles hot-path policies into direct delegates
- [ ] **PERF-04**: Three-tier fast path: VDE_ONLY (0ns), CONTAINER_STOP (~20ns), FULL_CASCADE (~200ns)
- [ ] **PERF-05**: Check classification: CONNECT_TIME, SESSION_CACHED, PER_OPERATION, DEFERRED, PERIODIC
- [ ] **PERF-06**: Policy recompilation triggered only on policy changes (not on every operation)
- [ ] **PERF-07**: PolicyEngine.Simulate() returns what-if analysis without applying changes

### AI Policy Intelligence (AIPI)

- [ ] **AIPI-01**: AI observation pipeline with zero hot-path impact (async metrics via lock-free ring buffer)
- [ ] **AIPI-02**: HardwareProbe awareness (CPU capabilities, RAM, storage speed, thermal throttling)
- [ ] **AIPI-03**: WorkloadAnalyzer awareness (time-of-day patterns, seasonal, burst detection)
- [ ] **AIPI-04**: ThreatDetector awareness feeds into policy tightening
- [ ] **AIPI-05**: CostAnalyzer awareness (cloud billing, compute cost per algorithm)
- [ ] **AIPI-06**: DataSensitivityAnalyzer (PII detection, classification patterns)
- [ ] **AIPI-07**: PolicyAdvisor produces PolicyRecommendation with rationale chain
- [ ] **AIPI-08**: AI autonomy level configurable per feature, per level (94 features x 5 levels = 470 config points)
- [ ] **AIPI-09**: Hybrid configuration: different autonomy levels for different feature categories
- [ ] **AIPI-10**: Configurable maxCpuOverhead for AI observation (default 1%, auto-throttle if exceeded)
- [ ] **AIPI-11**: AI cannot modify its own configuration (allowSelfModification: false, modificationRequiresQuorum: true)

### Authority Chain & Emergency Override (AUTH)

- [ ] **AUTH-01**: Emergency escalation protocol with time-bounded AI override (default 15 min)
- [ ] **AUTH-02**: Escalation countdown: admin confirms → permanent, admin reverts → restored, timeout → auto-revert
- [ ] **AUTH-03**: Immutable EscalationRecord with tamper-proof hash per escalation
- [ ] **AUTH-04**: Super Admin Quorum with configurable required approvals (e.g., 3-of-5)
- [ ] **AUTH-05**: Quorum actions defined: override AI, change security policy, disable AI, modify quorum, delete VDE, export keys, disable audit
- [ ] **AUTH-06**: Hardware token support for quorum approval (YubiKey, smart card)
- [ ] **AUTH-07**: Time-lock on destructive quorum actions (24hr cooling-off, any super admin can veto)
- [ ] **AUTH-08**: Dead man's switch: no super admin activity for N days → auto-lock to max security
- [ ] **AUTH-09**: Authority resolution order: Quorum → AI Emergency → Admin → System defaults

### Policy Persistence (PERS)

- [ ] **PERS-01**: IPolicyPersistence interface with pluggable implementations
- [ ] **PERS-02**: InMemoryPolicyPersistence (testing, ephemeral)
- [ ] **PERS-03**: FilePolicyPersistence (single-node, VDE sidecar)
- [ ] **PERS-04**: DatabasePolicyPersistence (multi-node with replication)
- [ ] **PERS-05**: TamperProofPolicyPersistence (blockchain-backed, immutable audit)
- [ ] **PERS-06**: HybridPolicyPersistence (policies in DB, audit trail in TamperProof)
- [ ] **PERS-07**: Compliance validation rejects incompatible persistence config (e.g., HIPAA + File audit store)

### Composable VDE Format (VDEF)

- [ ] **VDEF-01**: DWVD v2.0 format with 16-byte magic signature ("DWVD" + version + "dw://" anchor)
- [ ] **VDEF-02**: 4-block Superblock Group (primary superblock, region pointer table, extended metadata, integrity anchor) with mirror
- [ ] **VDEF-03**: Region Directory with 127 indirectable region pointer slots (32 bytes each)
- [ ] **VDEF-04**: Universal Block Trailer (16 bytes: BlockTypeTag, GenerationNumber, XxHash64) on every block
- [ ] **VDEF-05**: 19 composable modules with 32-bit ModuleManifest in Superblock
- [ ] **VDEF-06**: Nibble-encoded ModuleConfig (16 bytes for 32 modules at 16 levels each)
- [ ] **VDEF-07**: Self-describing InodeLayoutDescriptor for variable inode layouts between VDEs
- [ ] **VDEF-08**: Inode size calculated from selected modules (320B minimal to 576B maximum)
- [ ] **VDEF-09**: Inode padding bytes reserved for future module additions without migration
- [ ] **VDEF-10**: Extent-based inode addressing (8 extents x 24 bytes, replaces 12 direct block pointers)
- [ ] **VDEF-11**: Inline tag area in inode (128 bytes for ~4 compact tags when Tags module active)
- [ ] **VDEF-12**: Dual WAL (Metadata WAL + Data WAL) for reduced contention
- [ ] **VDEF-13**: Seven VDE creation profiles (Minimal, Standard, Enterprise, MaxSecurity, Edge/IoT, Analytics, Custom)
- [ ] **VDEF-14**: Runtime VDE composition based on user module selection
- [ ] **VDEF-15**: Sub-4K block sizes supported (512B, 1K, 2K) for IoT/embedded
- [ ] **VDEF-16**: Feature flags: IncompatibleFeatureFlags, ReadOnlyCompatibleFeatureFlags, CompatibleFeatureFlags
- [ ] **VDEF-17**: MinReaderVersion and MinWriterVersion for forward compatibility
- [ ] **VDEF-18**: Thin provisioning with sparse file semantics

### VDE Regions (VREG)

- [ ] **VREG-01**: Policy Vault region (2 blocks, crypto-bound, HMAC-sealed policy definitions)
- [ ] **VREG-02**: Encryption Header region (2 blocks, 63 key slots, KDF params, key rotation)
- [ ] **VREG-03**: Integrity Tree region (Merkle tree, O(log N) verification)
- [ ] **VREG-04**: Tag Index Region (B+-tree with bloom filter)
- [ ] **VREG-05**: Replication State Region (DVV, watermarks, dirty bitmap)
- [ ] **VREG-06**: RAID Metadata Region (shard maps, parity layout, rebuild progress)
- [ ] **VREG-07**: Streaming Append Region (ring buffer, 0% default allocation)
- [ ] **VREG-08**: WORM Immutable Region (append-only, high-water mark)
- [ ] **VREG-09**: Compliance Vault (CompliancePassport records with digital signatures)
- [ ] **VREG-10**: Intelligence Cache (classification, confidence, heat score, tier)
- [ ] **VREG-11**: Cross-VDE Reference Table (dw:// fabric links)
- [ ] **VREG-12**: Compute Code Cache (WASM module directory)
- [ ] **VREG-13**: Snapshot Table (CoW snapshot registry)
- [ ] **VREG-14**: Audit Log Region (append-only, hash-chained, never-truncated)
- [ ] **VREG-15**: Consensus Log Region (per-Raft-group, term/index metadata)
- [ ] **VREG-16**: Compression Dictionary Region (256 dictionaries, 2-byte DictId)
- [ ] **VREG-17**: Metrics Log Region (time-series, auto-compacted)
- [ ] **VREG-18**: Anonymization Table (GDPR mapping, right-to-be-forgotten)

### VDE Identity & Tamper Detection (VTMP)

- [ ] **VTMP-01**: dw:// Namespace Registration Block with Ed25519-signed URI authority
- [ ] **VTMP-02**: Format Fingerprint (BLAKE3 hash of spec revision)
- [ ] **VTMP-03**: Header Integrity Seal (HMAC-BLAKE3, checked on every open)
- [ ] **VTMP-04**: Metadata Chain Hash (rolling hash covering all metadata regions)
- [ ] **VTMP-05**: File Size Sentinel (expected size, truncation detected on open)
- [ ] **VTMP-06**: Last Writer Identity (session ID, timestamp, node ID)
- [ ] **VTMP-07**: TamperResponse enum with 5 levels, configurable in Policy Vault
- [ ] **VTMP-08**: Emergency Recovery Block at fixed block 9 (plaintext, accessible without keys)
- [ ] **VTMP-09**: VDE Health metadata (creation, mount count, error count, state machine)
- [ ] **VTMP-10**: VDE Nesting support (up to 3 levels)

### Online Module Addition (OMOD)

- [ ] **OMOD-01**: Online region addition from free space (zero downtime, WAL-journaled)
- [ ] **OMOD-02**: Inode field addition via claiming reserved padding bytes (lazy init)
- [ ] **OMOD-03**: Background inode table migration when no padding available (crash-safe)
- [ ] **OMOD-04**: New VDE creation + bulk data migration (extent-aware copy)
- [ ] **OMOD-05**: Tier 2 fallback always available (feature works via pipeline)
- [ ] **OMOD-06**: User presented with all 3 options with performance/downtime/risk comparison
- [ ] **OMOD-07**: ModuleManifest and ModuleConfig update atomically

### File Extension & OS Integration (FEXT)

- [ ] **FEXT-01**: .dwvd extension with IANA MIME type (application/vnd.datawarehouse.dwvd)
- [ ] **FEXT-02**: Windows ProgID with shell handlers (open/inspect/verify via dw CLI)
- [ ] **FEXT-03**: Linux freedesktop.org shared-mime-info + /etc/magic
- [ ] **FEXT-04**: macOS UTI (com.datawarehouse.dwvd)
- [ ] **FEXT-05**: Content detection priority (magic → version → namespace → flags → seal)
- [ ] **FEXT-06**: Non-DWVD file handling with import suggestion
- [ ] **FEXT-07**: Import from VHD, VHDX, VMDK, QCOW2, VDI, RAW, IMG
- [ ] **FEXT-08**: Secondary extensions (.dwvd.snap, .dwvd.delta, .dwvd.meta, .dwvd.lock)

### Three-Tier Performance Model (TIER)

- [ ] **TIER-01**: Tier 1 (VDE-Integrated) implemented for all 19 modules
- [ ] **TIER-02**: Tier 2 (Pipeline-Optimized) verified for all features without module integration
- [ ] **TIER-03**: Tier 3 (Basic) verified as fallback for all features
- [ ] **TIER-04**: Per-feature tier mapping documented
- [ ] **TIER-05**: Performance benchmarks comparing Tier 1 vs Tier 2 vs Tier 3

### Plugin Consolidation Audit (PLUG)

- [ ] **PLUG-01**: Review all 17 non-Ultimate plugins for standalone justification
- [ ] **PLUG-02**: Standalone plugins documented with rationale
- [ ] **PLUG-03**: Merge candidates identified with target plugin and migration plan
- [ ] **PLUG-04**: Merge execution (refactor into target as strategy, delete standalone)
- [ ] **PLUG-05**: Build verification after each merge (0 errors, 0 warnings)
- [ ] **PLUG-06**: Test verification after all merges

### Backward Compatibility & Migration (MIGR)

- [ ] **MIGR-01**: v1.0 format VDEs auto-detected and opened in compatibility mode
- [ ] **MIGR-02**: `dw migrate` converts v1.0 to v2.0 format with user-selected modules
- [ ] **MIGR-03**: v5.0 configs auto-migrated to VDE-level policies
- [ ] **MIGR-04**: No multi-level behavior unless admin explicitly configures
- [ ] **MIGR-05**: PolicyEngine transparent for existing deployments
- [ ] **MIGR-06**: AI is OFF by default (ManualOnly for all features)

### Integration Testing (INTG)

- [ ] **INTG-01**: PolicyEngine unit tests (~200)
- [ ] **INTG-02**: Per-feature multi-level tests (~280)
- [ ] **INTG-03**: Cross-feature interaction tests (~50)
- [ ] **INTG-04**: AI behavior tests (~100)
- [ ] **INTG-05**: Performance tests (~30)
- [ ] **INTG-06**: VDE format tests for all modules
- [ ] **INTG-07**: Tamper detection tests for all 5 response levels
- [ ] **INTG-08**: Migration tests (v1.0 → v2.0)

## v7.0+ Future Requirements

- **VFUT-01**: VDE-level index/metadata separation across VDEs
- **VFUT-02**: Online defragmentation via region indirection
- **VFUT-03**: VDE federation across geographic regions
- **PFUT-01**: Policy marketplace (import/export templates)
- **PFUT-02**: Policy simulation sandbox
- **PFUT-03**: Policy compliance scoring

## Out of Scope

| Feature | Reason |
|---------|--------|
| New plugin creation | v6.0 consolidates, doesn't add new plugins |
| UI/Dashboard changes | PolicyEngine via existing REST/gRPC/CLI |
| Deployment/CI/CD | Infrastructure concern |
| VDE hypervisor integration | DWVD is not a VM disk format |
| Real-time AI inference | AI observation is async/background only |

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| (populated by roadmapper) | | |

**Coverage:**
- v6.0 requirements: 120 total
- Mapped to phases: TBD
- Unmapped: TBD

---
*Requirements defined: 2026-02-20*
*Last updated: 2026-02-20 after design discussion*
