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

### VDE Advanced Features (VADV)

- [ ] **VADV-01**: VDE-level index/metadata separation across VDEs (data on VDE1, index on VDE2, metadata on VDE3 — user-configurable)
- [ ] **VADV-02**: Online defragmentation via region indirection (background block relocation, zero downtime)
- [ ] **VADV-03**: VDE federation across geographic regions (cross-region namespace resolution, geo-aware routing)

### Policy Advanced Features (PADV)

- [ ] **PADV-01**: Policy marketplace (import/export policy templates, community-shared profiles)
- [ ] **PADV-02**: Policy simulation sandbox (run full workload against hypothetical policy, report impact before apply)
- [ ] **PADV-03**: Policy compliance scoring (score deployment against regulatory templates, gap analysis report)

### Dynamic Scaling / Hyperscale (HSCL)

- [ ] **HSCL-01**: Dynamic write concurrency — stripe count auto-scales based on NVMe queue depth and CPU core count (default 64, max 1024); dual WAL default with configurable single-WAL fallback for constrained environments
- [ ] **HSCL-02**: Dynamic inode extents — initial 4 extents, grows to 8/16/32 on demand by consuming inode padding bytes; indirect extent block for beyond 32; shrinks on extent free; InodeLayoutDescriptor records current extent count
- [ ] **HSCL-03**: Dynamic B-Tree — branching factor computed from block size + avg key size (recomputable online); B-Tree Forest for keyspace partitioning; cache auto-tuned from available RAM (5% or 1,000 nodes min); memory-mapped I/O option
- [ ] **HSCL-04**: Dynamic inode cache — auto-sized from available RAM (10% or 10K min); memory-mapped inode table option (OS page cache as L2); tiered cache (L1 hot in-process, L2 mmap, L3 on-disk) for billion-object VDEs
- [ ] **HSCL-05**: Dynamic DVV region — default 256 bytes (32 nodes), auto-grows via online Replication State Region expansion (WAL-journaled); hierarchical DVV at 10K+ nodes (per-region DVV, higher-level cross-region DVV)
- [ ] **HSCL-06**: Cluster node scaling — no hard upper limit; default 100, protocol designed for 10K+; SWIM gossip O(log N) per event; Raft within regions (≤100 nodes), CRDT across regions
- [ ] **HSCL-07**: Replication topology scaling — default 100 replicas, configurable to cluster size; tree-based replication (origin→relays→replicas) at 1,000+; configurable topology (star/tree/mesh/chain)
- [ ] **HSCL-08**: Message bus hyperscale — default 1K msg/s per publisher, scalable to 1M+; bounded channel with configurable backpressure (drop-oldest/block-writer/shed-load); per-topic limits; batch delivery; optional zero-GC off-heap ring buffer
- [ ] **HSCL-09**: Auto-tuning on environment change — all scaling parameters recorded in VDE Superblock Extended Metadata; VDE opened on different hardware auto-tunes to new environment (cache sizes, stripe count, WAL size)
- [ ] **HSCL-10**: Hyperscale integration tests — verify: 64K+ concurrent writers, 1M+ inode VDE, 10K+ node DVV, tree replication with 1000+ endpoints, message bus at 100K+ msg/s with backpressure, dynamic extent growth/shrink

### Metadata Residency Strategy (MRES)

- [ ] **MRES-01**: `MetadataResidencyMode` enum (VdeOnly, VdePrimary, PluginOnly) in SDK — per-feature, per-module configuration with defaults table
- [ ] **MRES-02**: `WriteStrategy` enum (Atomic, VdeFirstSync, VdeFirstLazy) in SDK — configurable per metadata type, with lazy flush interval setting
- [ ] **MRES-03**: `ReadStrategy` enum (VdeFallback, VdeStrict) in SDK — VdeFallback reads VDE first then falls back to plugin metadata on corruption
- [ ] **MRES-04**: `CorruptionAction` enum (FallbackAndRepair, FallbackOnly, FailAndAlert, Quarantine) — configurable corruption response with auto-repair capability
- [ ] **MRES-05**: Hardware key reference pattern — `KeyType: HARDWARE_EXTERNAL` in Encryption Header key slot with `KeyStoreRef` URI, `WrappedKey` zeroed, plugin delegates to external key store (HSM, YubiKey, Cloud KMS, FROST)
- [ ] **MRES-06**: Lazy migration protocol — when module is enabled on existing VDE, empty inode fields trigger Tier 2 fallback on read, background task writes VDE metadata from plugin source (respects PluginOnly exclusions)
- [ ] **MRES-07**: Eager migration command — `dw vde migrate-metadata <vde> --module <module> --strategy eager [--max-iops N]` batch-migrates plugin metadata into VDE format with throttling
- [ ] **MRES-08**: VDE creation residency prompts — `dw vde create` prompts for default residency strategy, auto-detects hardware-backed keys (sets PluginOnly), shows sensitive metadata review table
- [ ] **MRES-09**: Corruption recovery flow — on VDE metadata read failure with VdePrimary+VdeFallback: fall back to plugin, log discrepancy, auto-repair VDE metadata, emit message bus CorruptionDetected event
- [ ] **MRES-10**: Mixed-state read path — VDE engine handles inodes with some fields populated (Tier 1) and some empty (Tier 2) within the same VDE; per-field fallback, not per-inode
- [ ] **MRES-11**: Residency policy in Policy Vault — metadata residency configuration persisted in VDE Policy Vault so residency decisions travel with the VDE
- [ ] **MRES-12**: Metadata residency tests — integration tests covering: VdeOnly corruption (data loss), VdePrimary corruption (fallback + repair), PluginOnly with hardware key, lazy migration, eager migration, mixed-state reads, atomic write rollback

### Deployment Topology & CLI Modes (DPLY)

- [ ] **DPLY-01**: Deployment topology selector — CLI/GUI support DW-only, VDE-only, or DW+VDE deployment options; user picks topology at install time (`dw install --topology dw-only|vde-only|dw+vde`)
- [ ] **DPLY-02**: VDE-only deployment — slim host that runs VDE engine without full DW kernel; remote DW instance connects over network to manage VDE (e.g., VDE on server, DW on laptop)
- [ ] **DPLY-03**: DW-only deployment — full DW kernel without local VDE; connects to remote VDE instances over network for storage
- [ ] **DPLY-04**: DW+VDE co-located deployment — both DW kernel and VDE engine on same host (current default behavior, made explicit)
- [ ] **DPLY-05**: VDE Composer/Builder CLI integration — `dw vde create --modules security,tags,replication` command using install mode (mode c) to compose a new VDE with selected modules
- [ ] **DPLY-06**: VDE Composer/Builder GUI page — GUI wizard for VDE module selection, topology preview, and deployment (mirrors CLI capabilities)
- [ ] **DPLY-07**: Shell handler registration — installer registers `.dwvd` file extension shell handlers (open/inspect/verify) via mode c install flow on Windows (registry), Linux (freedesktop), macOS (UTI)
- [ ] **DPLY-08**: File extension registration integration — mode c install automatically registers `.dwvd` extension, `.dw` script extension, and secondary extensions (`.dwvd.snap`, `.dwvd.delta`, etc.)
- [ ] **DPLY-09**: Fix `ServerCommands.ShowStatusAsync()` stub — replace hardcoded mock data (fake uptime, fake connection counts) with real server status query via `InstanceManager`/HTTP API
- [ ] **DPLY-10**: Fix `ServerCommands.StartServerAsync()`/`StopServerAsync()` stubs — replace `Task.Delay` simulation with real server lifecycle management
- [ ] **DPLY-11**: Fix sync-over-async bug in CLI `Program.cs` — replace obsolete `FindLocalLiveInstance()` call with `await FindLocalLiveInstanceAsync()`
- [ ] **DPLY-12**: Deployment mode tests — integration tests for all three CLI modes (connect/live/install) with all topology combinations (DW-only/VDE-only/DW+VDE), including VDE composer flow
- [ ] **DPLY-13**: GUI mode-selection page — startup wizard that presents mode a (connect), mode b (live), mode c (install/deploy) with topology selection for mode c

### Competitive Edge (EDGE)

- [ ] **EDGE-01**: VDE-native block export for NAS/SAN protocols — SMB/NFS/iSCSI/FC/NVMe-oF strategies bypass plugin layer and serve blocks directly from VDE regions via zero-copy I/O path; protocol strategies already exist, this adds VDE-direct fast path
- [ ] **EDGE-02**: Native Active Directory / Kerberos authentication — SPNEGO negotiation, Kerberos ticket validation, AD group-to-DW-role mapping, service principal registration (beyond existing LDAP strategy)
- [ ] **EDGE-03**: TLA+ formal verification model — specify and model-check VDE format invariants (superblock consistency, WAL recovery correctness, region directory atomicity), Raft consensus protocol, and Bε-tree index operations (message buffer flush, node split, tombstone propagation)
- [ ] **EDGE-04**: TLA+ CI integration — TLC model checker runs in CI pipeline; any specification violation fails the build; models cover at minimum: WAL crash recovery, concurrent VDE open, Raft leader election, Bε-tree flush/split, address width promotion
- [ ] **EDGE-05**: OS-level security hardening — seccomp-bpf syscall filtering on Linux, pledge/unveil on OpenBSD, W^X enforcement verification, ASLR validation at startup, AppArmor/SELinux profile generation for DW processes
- [ ] **EDGE-06**: Process compartmentalization — each plugin runs in a separate security domain (Linux: seccomp + namespaces, Windows: job objects + integrity levels); untrusted code (WASM, user plugins) runs in Xen-style compartments with minimal privilege
- [ ] **EDGE-07**: Streaming SQL engine (ksqlDB-equivalent) — continuous queries over streaming data, windowed aggregations (tumbling/hopping/session/sliding), materialized views that update in real-time, stream-table joins
- [ ] **EDGE-08**: Streaming performance targets — sustain 1M+ events/sec per node with exactly-once processing semantics, watermark-based late event handling, backpressure propagation to producers
- [ ] **EDGE-09**: Deterministic I/O mode — bounded-latency I/O paths for safety-critical deployments: pre-allocated buffers, no dynamic allocation on hot path, worst-case execution time (WCET) annotations, deadline-based scheduling
- [ ] **EDGE-10**: Safety certification preparation (DO-178C/IEC 61508) — traceability matrix from requirements to code to tests, MC/DC coverage measurement, static analysis with zero warnings, formal proof of critical algorithm termination
- [ ] **EDGE-11**: Variable-width block addressing — VDE superblock declares AddressWidth (32/48/64/128 bits); all on-disk pointers use exactly that width; default 32-bit (covers 16TB, 4 bytes/pointer); online width promotion when VDE grows past capacity (background pointer rewrite, VDE stays mounted); shrinking after compaction; MinReaderVersion enforces compatibility
- [ ] **EDGE-12**: ML pipeline integration — in-VDE feature store, model versioning, training data lineage, inference result caching in Intelligence Cache region; integrates with existing UltimateIntelligence AI providers
- [ ] **EDGE-13**: Native Delta Lake / Iceberg table operations — VDE-native transaction log (not just file-based), atomic multi-table commits, time-travel queries reading VDE snapshots directly, schema evolution via Policy Vault

### Adaptive Index Engine (AIE)

- [ ] **AIE-01**: Morphing spectrum Level 0-1 — Direct Pointer Array (1–64 objects, O(1) read/write, zero metadata) and Sorted Array (64–4K objects, binary search O(log N), cache-line-friendly). Initial state for every new VDE.
- [ ] **AIE-02**: Morphing spectrum Level 2 — ART (Adaptive Radix Tree) for 4K–256K objects: 4 adaptive node types (Node4/Node16/Node48/Node256), SIMD-accelerated Node16 search via Vector128, O(k) lookup, path compression, 8.1 bytes/key average
- [ ] **AIE-03**: Morphing spectrum Level 3 — Bε-tree (B-epsilon tree) for 256K–1B objects: message buffers in internal nodes (ε=0.5), batched write flush, tombstone deletes, write I/O cost O(log_B N / √B) = 64x fewer I/Os than B-tree; ART persists as L0 write buffer absorbing bursts
- [ ] **AIE-04**: Morphing spectrum Level 4 — Bε-tree + ALEX Learned Overlay for 1B–100B objects: ML model trained on actual key distribution predicts leaf position O(1), fallback to Bε-tree traversal on miss, incremental retrain on distribution drift
- [ ] **AIE-05**: Morphing spectrum Level 5 — Sharded Bε-tree Forest for 100B–10T objects: Hilbert curve partitions keyspace, each shard is independent Bε-tree at its own morph level, learned routing model routes to shard in O(1), auto-split on size threshold, auto-merge on low utilization
- [ ] **AIE-06**: Morphing spectrum Level 6 — Distributed Probabilistic Routing for 10T+ objects: CRUSH distributes shards across cluster, Bloofi hierarchical bloom filter for O(1) "which node?" routing, Clock-SI for snapshot isolation without central oracle, each node runs local AIE
- [ ] **AIE-07**: Bidirectional morphing — backward morph when data shrinks: when live key count drops below 30% of level's threshold, IndexMorphAdvisor triggers compaction + demotion; all morph-down transitions are WAL-journaled, crash-safe, zero-downtime; a 1T dataset that deletes 99.9% morphs backward to sorted array automatically
- [ ] **AIE-08**: IndexMorphAdvisor — autonomous background decision engine observing: object count, read/write ratio, key distribution entropy, cache hit rate, I/O latency P50/P99, memory pressure, storage device characteristics (NVMe queue depth, rotational vs flash); self-tuning thresholds (if promotion causes latency regression, auto-revert and adjust); all decisions logged to VDE Audit Log; admin override via IndexMorphPolicy
- [ ] **AIE-09**: Index Striping (RAID-0) — single logical index striped across N VDE regions or devices; parallel read fan-out, round-robin write; N auto-tuned from NVMe queue count and CPU cores
- [ ] **AIE-10**: Index Mirroring (RAID-1) — hot index replicated to M copies for read concurrency; synchronous or async writes configurable; corruption detection triggers rebuild from healthy mirror
- [ ] **AIE-11**: Index Sharding — key range divided into S shards, each shard is independent AIE at its own morph level (shard A with 50M keys = Bε-tree, adjacent shard B with 500 keys = sorted array); shard boundaries auto-adjust for even distribution
- [ ] **AIE-12**: Index Tiering (L1/L2/L3) — hot keys promoted to in-memory ART (L1), warm keys in Bε-tree on NVMe (L2), cold keys in learned index on archive (L3); promotion/demotion driven by count-min sketch access frequency tracker (O(1), fixed 64KB memory, periodic decay)
- [ ] **AIE-13**: Zero-downtime morph transitions — every transition is: WAL-journaled (crash recovers to pre or post state), copy-on-write (old structure serves reads until new is complete, atomic pointer swap), background (dedicated thread pool, configurable max CPU% default 10%), measurable (progress via observability), cancellable (admin can abort mid-flight, clean rollback)
- [ ] **AIE-14**: Legacy B-tree compatibility — existing BTree.cs preserved as LegacyBTreeIndex for v1.0 VDE; new VDEs default to AIE Level 0; `dw migrate` upgrades legacy B-tree to Bε-tree
- [ ] **AIE-15**: Bw-Tree lock-free variant — delta records prepended via CAS, epoch-based GC, mapping table for page indirection; used for concurrent metadata cache and session-scoped indexes; 5-18x speedup over ReaderWriterLockSlim
- [ ] **AIE-16**: Masstree for hot-path namespace lookup — trie-of-B+-trees with 8-byte key slicing, optimistic readers, lightweight per-node writer locks; replaces ConcurrentDictionary on VDE namespace tree hot path
- [ ] **AIE-17**: Disruptor pattern for message bus — pre-allocated power-of-2 ring buffer, cache-line-padded sequences (64-byte StructLayout), sequence barriers, pluggable wait strategies; replaces Channel<T> for intra-process messaging; target 100M+ msgs/sec
- [ ] **AIE-18**: Extendible hashing inode table — dynamic O(1) amortized lookup, directory doubles on overflow (pointer copies only, zero data rehash); replaces fixed linear inode array; supports trillion-object inode tables
- [ ] **AIE-19**: io_uring native Linux I/O — `[LibraryImport]` source-generated bindings directly to `liburing.so` (NOT managed wrapper); `NativeMemory.AlignedAlloc` for DMA-capable pre-pinned pages; ring-per-thread; registered buffers; SQPoll mode; NVMe passthrough via `IORING_OP_URING_CMD` for raw block device; graceful fallback to `RandomAccess` on non-Linux; target 33x throughput
- [ ] **AIE-20**: HNSW + PQ with GPU acceleration — CPU path: pure C# HNSW graph with Vector256/Avx2 SIMD distance computation + Product Quantization ADC lookup tables; GPU path: ILGPU (C# → PTX compiler) for batch distance computation and parallel graph traversal, no separate CUDA toolkit needed; alternative cuVS P/Invoke path for maximum throughput when NVIDIA cuVS library is present; transparent fallback: GPU → SIMD → scalar
- [ ] **AIE-21**: Hilbert space-filling curve engine — multi-dimensional → 1D mapping with best locality preservation (3-11x speedup over Z-order); used for spatial queries, Bε-tree Forest partitioning, and Lakehouse data layout
- [ ] **AIE-22**: Trained Zstd dictionary support — `ZstdNet.DictBuilder.TrainFromBuffer` from VDE data sample; dictionary stored in VDE Compression Dictionary Region; 2-5x ratio improvement for 4KB blocks; auto-retrain on data distribution change
- [ ] **AIE-23**: SIMD-accelerated hot paths — Vector256/Avx2 for: bloom filter multi-probe, ART Node16 key search, cosine/dot-product/euclidean in VectorOperations, XxHash checksum, bitmap scanning; runtime detection via `Avx2.IsSupported` with scalar fallback
- [ ] **AIE-24**: Bloofi distributed pre-filter — hierarchical multi-dimensional Bloom filter for "which node has this key?" routing; O(1) existence check; reduces cross-node queries by ~90%
- [ ] **AIE-25**: Clock-SI distributed transactions — snapshot isolation via local physical clocks, no centralized timestamp oracle; augmented 2PC; 50%+ latency reduction for read-only distributed transactions
- [ ] **AIE-26**: Persistent extent tree — checkpoint extent tree to VDE region (currently in-memory, rebuilt from bitmap on load); incremental checkpointing; eliminates O(N) bitmap scan on mount
- [ ] **AIE-27**: Count-min sketch for access tracking — fixed-memory (64KB default) probabilistic frequency counter for Index Tiering decisions; O(1) update/query; periodic decay (halve counters) to adapt to changing workloads

### Ecosystem Compatibility (ECOS)

- [ ] **ECOS-01**: Verify PostgreSQL wire protocol — audit existing `PostgreSqlProtocolStrategy.cs` (964 lines) for completeness: startup handshake, authentication (cleartext/MD5/SCRAM-SHA-256), simple query protocol, extended query protocol (Parse/Bind/Describe/Execute/Sync), COPY protocol, error/notice handling, SSL negotiation, parameter status; test with psql, pgAdmin, DBeaver, SQLAlchemy, npgsql, JDBC; fix any gaps
- [ ] **ECOS-02**: PostgreSQL wire protocol integration with SQL engine — verify `PostgreSqlProtocolStrategy` correctly routes queries to `SqlParserEngine` → `CostBasedQueryPlanner` → `QueryExecutionEngine`; map PostgreSQL types to DW `ColumnDataType`; implement `\d` catalog queries; prepared statement caching; transaction support (BEGIN/COMMIT/ROLLBACK mapping to MVCC)
- [ ] **ECOS-03**: Verify Parquet support — audit existing `ParquetCompatibleWriter.cs` (666 lines) and `ParquetStrategy.cs`; test round-trip read/write with pandas, PyArrow, Spark; verify column pruning, row group skipping, all data types; test compressed codecs (Snappy, Gzip, Zstd via delegation); fix any gaps
- [ ] **ECOS-04**: Verify Arrow support — audit existing `ArrowStrategy.cs`; test Arrow IPC read/write with PyArrow; verify zero-copy reads, schema fidelity, all data types; implement Arrow Flight protocol (gRPC-based high-throughput transfer) if absent
- [ ] **ECOS-05**: Verify ORC support — audit existing `OrcStrategy.cs`; test read/write with Hive/Spark; verify stripe statistics for predicate pushdown, type mapping, compression codecs
- [ ] **ECOS-06**: Parquet/Arrow VDE integration — VDE columnar regions (VOPT-16) use Arrow memory format internally; Parquet read → Arrow → VDE columnar is zero-copy; VDE columnar → Parquet export is zero-copy; zone maps (VOPT-17) populated from Parquet row group statistics
- [ ] **ECOS-07**: Python SDK — gRPC client generated from `.proto` definitions; covers connect, store, retrieve, query (SQL via PostgreSQL protocol or gRPC), tag, search, stream; published on PyPI; tested with pandas, Jupyter, Airflow integration examples
- [ ] **ECOS-08**: Java SDK — gRPC client for enterprise integration (Spark, Kafka Connect, Flink, Spring); JAR on Maven Central; JDBC driver wrapping PostgreSQL wire protocol
- [ ] **ECOS-09**: Go SDK — gRPC client for cloud-native/DevOps tooling; Go module on pkg.go.dev; used by Terraform provider
- [ ] **ECOS-10**: Rust SDK — gRPC or direct binary protocol client for performance-critical and embedded use cases; crate on crates.io
- [ ] **ECOS-11**: JavaScript/TypeScript SDK — gRPC-web or REST client for web dashboards and Node.js integrations; npm package
- [ ] **ECOS-12**: SDK `.proto` definitions — single source of truth protobuf definitions for all DW operations (store/retrieve/query/tag/search/stream/admin); all language SDKs generated from same protos; proto versioning strategy
- [ ] **ECOS-13**: Terraform provider — `terraform-provider-datawarehouse` managing DW instances, VDEs, users, policies, plugins, replication topology; written in Go with Terraform Plugin SDK v2; published to Terraform Registry; tested with `terraform plan/apply/destroy` lifecycle
- [ ] **ECOS-14**: Pulumi provider — generated from Terraform provider via `pulumi-terraform-bridge`; supports Python, TypeScript, Go, C# SDKs
- [ ] **ECOS-15**: Helm chart — `helm-chart-datawarehouse` for Kubernetes deployment; StatefulSet, PVC, ConfigMap, Secret, Service, Ingress; supports single-node and clustered modes; tested with `helm install/upgrade/rollback`
- [ ] **ECOS-16**: Jepsen distributed correctness testing — test harness deploying N DW nodes in Docker containers; fault injection (network partition, process kill, clock skew, disk corruption); workloads (register, set, list-append, bank); validate against consistency models (linearizable, serializable, snapshot-isolation) using Elle checker
- [ ] **ECOS-17**: Jepsen test coverage — minimum test scenarios: linearizability under partition, Raft leader election under partition, CRDT convergence under delay, WAL crash recovery, MVCC snapshot isolation, DVV replication consistency, split-brain prevention
- [ ] **ECOS-18**: Connection pooling SDK contract — `IConnectionPool<TConnection>` with configurable min/max connections, idle timeout, health checking, connection validation; implementations for TCP (Raft, replication, fabric), gRPC channel (client SDKs), HTTP/2 (REST, Arrow Flight); per-node connection limits

### Dynamic Subsystem Scaling (DSCL)

- [ ] **DSCL-01**: SDK scaling contract — `IScalableSubsystem` interface with `BoundedCache<TKey,TValue>` (configurable eviction: LRU/ARC/TTL, auto-sized from RAM), `IPersistentBackingStore` (pluggable file/DB/VDE persistence, lazy-load on cache miss), `IScalingPolicy` (runtime-reconfigurable Max* limits via Instance→Tenant→User hierarchy), `IBackpressureAware` (drop-oldest/block-producer/shed-load/degrade-quality)
- [ ] **DSCL-02**: BoundedCache SDK implementation — generic `BoundedCache<TKey,TValue>` replacing unbounded ConcurrentDictionary pattern; supports LRU, ARC, and TTL eviction; ghost list adaptation; auto-sizing from available RAM; thread-safe; O(1) get/put
- [ ] **DSCL-03**: PersistentBackingStore SDK implementations — file-based (JSON/binary), VDE-region-based, and database-backed implementations of `IPersistentBackingStore`; lazy load on cache miss; write-through or write-behind configurable
- [ ] **DSCL-04**: Blockchain dynamic scaling — replace `List<Block>` with segmented mmap'd page store (64MB segments); fix `long→int` cast for >2B blocks; shard journal by block range; per-tier locks; bounded manifest/validation caches with LRU; configurable `MaxConcurrentWrites`
- [ ] **DSCL-05**: AEDS dynamic scaling — bounded LRU caches for manifests/validations/jobs/clients/channels; per-collection locks replacing single SemaphoreSlim; dynamically adjustable `MaxConcurrentChunks` and `ChunkSizeBytes`; partitioned job queues
- [ ] **DSCL-06**: WASM runtime scaling — configurable `MaxPages` per-module (not hardcoded 256); dynamically adjustable `MaxConcurrentExecutions` based on system load; module state persistence; warm instance pooling
- [ ] **DSCL-07**: Consensus (Raft) scaling — multi-Raft group support (partition state across groups of ≤100 nodes); segmented log store (one file per 10K entries, mmap'd hot segments); connection pooling for RPC; dynamic election timeouts from network latency
- [ ] **DSCL-08**: Message bus scaling — runtime reconfiguration of `KernelLimitsConfig` without restart; persistent message queuing option (WAL-backed) for durability beyond 100K limit; topic partitioning for horizontal scale-out; backpressure signaling to producers; Disruptor integration for hot paths
- [ ] **DSCL-09**: Replication scaling — per-key/per-namespace strategy selection (not single global active strategy); persistent WAL-backed replication queue; dynamic replica discovery/management; streaming conflict comparison (not full blob `SequenceEqual`)
- [ ] **DSCL-10**: Streaming implementation — implement `PublishAsync`/`SubscribeAsync` (currently stubs); wire `ScalabilityConfig` to auto-scaling logic; implement backpressure strategies; checkpoint storage integration for exactly-once processing
- [ ] **DSCL-11**: ACL scaling — replace `List<PolicyAccessDecision> _auditLog` with ring buffer or externalized store; configurable weighted evaluation threshold; parallel strategy evaluation option; configurable behavior analysis thresholds
- [ ] **DSCL-12**: Resilience fix and scaling — fix `ExecuteWithResilienceAsync` to actually apply strategy resilience logic; dynamically adjustable circuit breaker and bulkhead options at runtime; adaptive thresholds from observed latency/error rates; distributed circuit breaker state sharing across nodes
- [ ] **DSCL-13**: DataCatalog scaling — persistent asset/relationship/glossary store; LRU cache with eviction; pagination for listing operations; sharding for large catalogs
- [ ] **DSCL-14**: Governance scaling — persistent policy/ownership/classification store; TTL cache; parallel strategy evaluation across frameworks
- [ ] **DSCL-15**: Lineage scaling — persistent graph store (or graph DB strategy delegation); graph partitioning; pagination for lineage queries; configurable max traversal depth
- [ ] **DSCL-16**: DataMesh scaling — persistent domain/product/consumer/share/policy store; cross-domain federation with distributed state; bounded caches
- [ ] **DSCL-17**: Compression scaling — adaptive buffer size based on input size and available memory; parallel chunk compression coordination; per-request `QualityLevel` configuration
- [ ] **DSCL-18**: Encryption scaling — runtime hardware capability re-detection (for container environments); dynamically adjustable `MaxConcurrentMigrations`; per-algorithm parallelism
- [ ] **DSCL-19**: Search scaling — proper inverted index infrastructure (replace ConcurrentDictionary iteration); search result pagination/streaming; vector index sharding for horizontal scaling
- [ ] **DSCL-20**: Database storage scaling — fix `HandleRetrieveAsync` to stream data (not `MemoryStream.ToArray()`); query result pagination/streaming; parallel health checks across strategies
- [ ] **DSCL-21**: Filesystem scaling — dynamic I/O scheduling with configurable queue depths; runtime kernel bypass re-detection; integration with `ResourceManager` for I/O quota enforcement
- [ ] **DSCL-22**: Backup/DataProtection scaling — dynamically adjustable `MaxConcurrentJobs` (currently hardcoded 4) based on I/O capacity; backup operation persistence for crash recovery; backup chain management with configurable retention
- [ ] **DSCL-23**: Pipeline scaling — configurable maximum pipeline depth; concurrent transaction limits; `CapturedState` size limits per stage; parallel rollback for independent stages
- [ ] **DSCL-24**: DataFabric scaling — runtime-configurable `MaxNodes` per topology (currently static Star=1000/Mesh=500/Federated=10000); dynamic topology switching based on node count; health-based auto-remove/add of nodes
- [ ] **DSCL-25**: TamperProof scaling — per-tier locks replacing single SemaphoreSlim; bounded caches; configurable RAID shard counts based on data size; background scan with throttling
- [ ] **DSCL-26**: Compliance scaling — parallel `CheckAllComplianceAsync` across strategies; compliance check result caching with TTL; configurable concurrent check limits
- [ ] **DSCL-27**: Universal plugin migration — all 60 plugins migrated from unbounded `ConcurrentDictionary` entity stores to `BoundedCache` + `IPersistentBackingStore` pattern; all `Max*` limits reconfigurable via policy engine; verified no state loss on restart

### VDE Scalable Internals (VOPT)

- [ ] **VOPT-01**: Allocation groups — VDE data region divided into configurable allocation groups (default 128MB each); each group has own bitmap, free-space count, per-group lock; allocations are group-local for zero contention; group count grows as VDE grows; single group for small VDEs (<128MB) = zero overhead
- [ ] **VOPT-02**: Allocation group descriptor table — dedicated VDE region storing group metadata (bitmap offset, free count, lock state, allocation policy per group); first-fit (sequential) vs best-fit (random) configurable per group
- [ ] **VOPT-03**: ARC cache L1 — Adaptive Replacement Cache in-process: self-tuning T1 (recency) / T2 (frequency) lists with B1/B2 ghost lists; replaces TTL-only DefaultCacheManager; auto-sized from available RAM
- [ ] **VOPT-04**: ARC cache L2 — memory-mapped VDE regions for zero-copy read; OS page cache manages eviction; activated when VDE > available RAM; configurable per-region
- [ ] **VOPT-05**: ARC cache L3 — NVMe read cache for tiered storage; warm blocks written to dedicated fast device separate from primary VDE; configurable `l3Device` path
- [ ] **VOPT-06**: Compact inode (64 bytes) — objects ≤48 bytes stored INLINE in inode (zero block allocation); covers config files, small records, tag-only entries; declared via `InodeLayout: Compact64` in superblock
- [ ] **VOPT-07**: Standard inode (256 bytes) — fix NotSupportedException for indirect/double-indirect/triple-indirect blocks; support files up to 64GB with 4KB blocks; maintain backward compatibility with v1.0 inodes
- [ ] **VOPT-08**: Extended inode (512+ bytes) — inline xattrs, nanosecond timestamps, compression dictionary reference, per-object encryption IV, MVCC version chain pointer; for metadata-rich objects
- [ ] **VOPT-09**: Extent-based inode addressing — replace 12 direct + indirect pointers with extent tree (start block + length); ≤4 extents stored inline in inode, overflow to extent block; 1TB file = ~64 extents vs ~268M indirect pointers
- [ ] **VOPT-10**: Mixed inode layout — superblock `InodeLayout: Mixed` enables per-object auto-selection (Compact64/Standard256/Extended512) based on content size at write time; InodeLayoutDescriptor tracks per-object format
- [ ] **VOPT-11**: Sub-block packing — multiple small objects share one block (tail-merging); allocation group secondary bitmap tracks sub-block allocation; eliminates internal fragmentation for small objects
- [ ] **VOPT-12**: MVCC — WAL-based multi-version concurrency control; writers append WAL records with transaction ID; readers acquire snapshot (WAL sequence number) and see only versions ≤ snapshot; version chain via inode VersionChainHead pointer; dedicated MVCC Region for old versions
- [ ] **VOPT-13**: MVCC garbage collection — background vacuum removes versions older than oldest active snapshot + configurable retention window; incremental (process N versions per cycle); concurrent with normal operations
- [ ] **VOPT-14**: MVCC isolation levels — Read Committed (default), Snapshot Isolation (full MVCC), Serializable (predicate locks); configurable per-VDE and per-query
- [ ] **VOPT-15**: SQL OLTP optimization — prepared query cache (parsed + planned queries by fingerprint), merge join for sorted Bε-tree range scans, index-only scans using zone maps
- [ ] **VOPT-16**: Columnar VDE regions — optional per-table columnar storage region with run-length + dictionary encoding; `CREATE TABLE ... WITH (FORMAT=COLUMNAR)`; stored in dedicated VDE region
- [ ] **VOPT-17**: Zone maps — per-extent min/max/null_count metadata in extent headers; query planner uses zone maps for predicate pushdown; skip entire extents without reading data
- [ ] **VOPT-18**: SIMD vectorized SQL execution — `Vector256<float>` / `Avx2` for SUM/COUNT/MIN/MAX/AVG and comparison predicates in ColumnarEngine; runtime `Avx2.IsSupported` with scalar fallback; process 8 values per instruction
- [ ] **VOPT-19**: SQL spill-to-disk — aggregation exceeding memory budget spills to temp VDE region (hash partitioned); configurable per-query memory limit; automatic cleanup on query completion
- [ ] **VOPT-20**: SQL predicate pushdown — WHERE clauses pushed into storage scan layer; combined with zone maps, skip >90% of data for selective queries; works for both row and columnar formats
- [ ] **VOPT-21**: Persistent tag index with roaring bitmaps — replace in-memory ConcurrentDictionary + HashSet<long> with persistent inverted index in VDE Tag Index region using Roaring bitmaps (~2 bytes/element, O(1) AND/OR/NOT)
- [ ] **VOPT-22**: Tag bloom filter per allocation group — 1KB bloom per group for O(1) "does this group contain tag=X?" check; enables group-level skip during tag queries
- [ ] **VOPT-23**: Per-extent encryption — encrypt contiguous block ranges as one unit; one IV per extent instead of per block; bulk AES-NI processing; reduces crypto overhead for large objects by up to 256x
- [ ] **VOPT-24**: Per-extent compression — compress contiguous block ranges as one unit; one compression dictionary per extent; better ratio than per-block for small block sizes (4KB)
- [ ] **VOPT-25**: Hierarchical checksums — per-block XxHash64 (fast) + per-extent CRC32C (medium) + per-object Merkle root (strong); Merkle tree in Integrity Tree region enables binary-search corruption localization
- [ ] **VOPT-26**: Extent-aware CoW snapshots — mark entire extents as shared (not individual blocks); reduces snapshot metadata by orders of magnitude for large files; extent-level reference counting
- [ ] **VOPT-27**: Extent-aware replication delta — replication ships changed extents (not blocks); larger granularity with proportionally less tracking metadata
- [ ] **VOPT-28**: VDE online defragmentation — background extent compaction within allocation groups; merge adjacent free extents; configurable I/O budget; WAL-journaled for crash safety

## Out of Scope

| Feature | Reason |
|---------|--------|
| New plugin creation | v6.0 consolidates, doesn't add new plugins |
| UI/Dashboard changes | PolicyEngine via existing REST/gRPC/CLI |
| VDE hypervisor integration | DWVD is not a VM disk format |
| Real-time AI inference | AI observation is async/background only |
| AFP protocol | Legacy Apple protocol superseded by SMB; existing SMB strategy covers macOS |
| Hardware manufacturing | DW runs on any hardware; we don't make hardware |

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| SDKF-01 | Phase 68 | Pending |
| SDKF-02 | Phase 68 | Pending |
| SDKF-03 | Phase 68 | Pending |
| SDKF-04 | Phase 68 | Pending |
| SDKF-05 | Phase 68 | Pending |
| SDKF-06 | Phase 68 | Pending |
| SDKF-07 | Phase 68 | Pending |
| SDKF-08 | Phase 68 | Pending |
| SDKF-09 | Phase 68 | Pending |
| SDKF-10 | Phase 68 | Pending |
| SDKF-11 | Phase 68 | Pending |
| SDKF-12 | Phase 68 | Pending |
| CASC-01 | Phase 70 | Pending |
| CASC-02 | Phase 70 | Pending |
| CASC-03 | Phase 70 | Pending |
| CASC-04 | Phase 70 | Pending |
| CASC-05 | Phase 70 | Pending |
| CASC-06 | Phase 70 | Pending |
| CASC-07 | Phase 70 | Pending |
| CASC-08 | Phase 70 | Pending |
| PERF-01 | Phase 76 | Pending |
| PERF-02 | Phase 76 | Pending |
| PERF-03 | Phase 76 | Pending |
| PERF-04 | Phase 76 | Pending |
| PERF-05 | Phase 76 | Pending |
| PERF-06 | Phase 76 | Pending |
| PERF-07 | Phase 76 | Pending |
| AIPI-01 | Phase 77 | Pending |
| AIPI-02 | Phase 77 | Pending |
| AIPI-03 | Phase 77 | Pending |
| AIPI-04 | Phase 77 | Pending |
| AIPI-05 | Phase 77 | Pending |
| AIPI-06 | Phase 77 | Pending |
| AIPI-07 | Phase 77 | Pending |
| AIPI-08 | Phase 77 | Pending |
| AIPI-09 | Phase 77 | Pending |
| AIPI-10 | Phase 77 | Pending |
| AIPI-11 | Phase 77 | Pending |
| AUTH-01 | Phase 75 | Pending |
| AUTH-02 | Phase 75 | Pending |
| AUTH-03 | Phase 75 | Pending |
| AUTH-04 | Phase 75 | Pending |
| AUTH-05 | Phase 75 | Pending |
| AUTH-06 | Phase 75 | Pending |
| AUTH-07 | Phase 75 | Pending |
| AUTH-08 | Phase 75 | Pending |
| AUTH-09 | Phase 75 | Pending |
| PERS-01 | Phase 69 | Pending |
| PERS-02 | Phase 69 | Pending |
| PERS-03 | Phase 69 | Pending |
| PERS-04 | Phase 69 | Pending |
| PERS-05 | Phase 69 | Pending |
| PERS-06 | Phase 69 | Pending |
| PERS-07 | Phase 69 | Pending |
| VDEF-01 | Phase 71 | Pending |
| VDEF-02 | Phase 71 | Pending |
| VDEF-03 | Phase 71 | Pending |
| VDEF-04 | Phase 71 | Pending |
| VDEF-05 | Phase 71 | Pending |
| VDEF-06 | Phase 71 | Pending |
| VDEF-07 | Phase 71 | Pending |
| VDEF-08 | Phase 71 | Pending |
| VDEF-09 | Phase 71 | Pending |
| VDEF-10 | Phase 71 | Pending |
| VDEF-11 | Phase 71 | Pending |
| VDEF-12 | Phase 71 | Pending |
| VDEF-13 | Phase 71 | Pending |
| VDEF-14 | Phase 71 | Pending |
| VDEF-15 | Phase 71 | Pending |
| VDEF-16 | Phase 71 | Pending |
| VDEF-17 | Phase 71 | Pending |
| VDEF-18 | Phase 71 | Pending |
| VREG-01 | Phase 72 | Pending |
| VREG-02 | Phase 72 | Pending |
| VREG-03 | Phase 72 | Pending |
| VREG-04 | Phase 72 | Pending |
| VREG-05 | Phase 72 | Pending |
| VREG-06 | Phase 72 | Pending |
| VREG-07 | Phase 72 | Pending |
| VREG-08 | Phase 72 | Pending |
| VREG-09 | Phase 72 | Pending |
| VREG-10 | Phase 73 | Pending |
| VREG-11 | Phase 73 | Pending |
| VREG-12 | Phase 73 | Pending |
| VREG-13 | Phase 73 | Pending |
| VREG-14 | Phase 73 | Pending |
| VREG-15 | Phase 73 | Pending |
| VREG-16 | Phase 73 | Pending |
| VREG-17 | Phase 73 | Pending |
| VREG-18 | Phase 73 | Pending |
| VTMP-01 | Phase 74 | Pending |
| VTMP-02 | Phase 74 | Pending |
| VTMP-03 | Phase 74 | Pending |
| VTMP-04 | Phase 74 | Pending |
| VTMP-05 | Phase 74 | Pending |
| VTMP-06 | Phase 74 | Pending |
| VTMP-07 | Phase 74 | Pending |
| VTMP-08 | Phase 74 | Pending |
| VTMP-09 | Phase 74 | Pending |
| VTMP-10 | Phase 74 | Pending |
| OMOD-01 | Phase 78 | Pending |
| OMOD-02 | Phase 78 | Pending |
| OMOD-03 | Phase 78 | Pending |
| OMOD-04 | Phase 78 | Pending |
| OMOD-05 | Phase 78 | Pending |
| OMOD-06 | Phase 78 | Pending |
| OMOD-07 | Phase 78 | Pending |
| FEXT-01 | Phase 79 | Pending |
| FEXT-02 | Phase 79 | Pending |
| FEXT-03 | Phase 79 | Pending |
| FEXT-04 | Phase 79 | Pending |
| FEXT-05 | Phase 79 | Pending |
| FEXT-06 | Phase 79 | Pending |
| FEXT-07 | Phase 79 | Pending |
| FEXT-08 | Phase 79 | Pending |
| TIER-01 | Phase 80 | Pending |
| TIER-02 | Phase 80 | Pending |
| TIER-03 | Phase 80 | Pending |
| TIER-04 | Phase 80 | Pending |
| TIER-05 | Phase 80 | Pending |
| PLUG-01 | Phase 82 | Pending |
| PLUG-02 | Phase 82 | Pending |
| PLUG-03 | Phase 82 | Pending |
| PLUG-04 | Phase 82 | Pending |
| PLUG-05 | Phase 82 | Pending |
| PLUG-06 | Phase 82 | Pending |
| MIGR-01 | Phase 81 | Pending |
| MIGR-02 | Phase 81 | Pending |
| MIGR-03 | Phase 81 | Pending |
| MIGR-04 | Phase 81 | Pending |
| MIGR-05 | Phase 81 | Pending |
| MIGR-06 | Phase 81 | Pending |
| INTG-01 | Phase 83 | Pending |
| INTG-02 | Phase 83 | Pending |
| INTG-03 | Phase 83 | Pending |
| INTG-04 | Phase 83 | Pending |
| INTG-05 | Phase 83 | Pending |
| INTG-06 | Phase 83 | Pending |
| INTG-07 | Phase 83 | Pending |
| INTG-08 | Phase 83 | Pending |
| VADV-01 | Phase 73 | Pending |
| VADV-02 | Phase 78 | Pending |
| VADV-03 | Phase 73 | Pending |
| PADV-01 | Phase 69 | Pending |
| PADV-02 | Phase 76 | Pending |
| PADV-03 | Phase 70 | Pending |
| HSCL-01 | Phase 71 | Pending |
| HSCL-02 | Phase 71 | Pending |
| HSCL-03 | Phase 71 | Pending |
| HSCL-04 | Phase 71 | Pending |
| HSCL-05 | Phase 73 | Pending |
| HSCL-06 | Phase 68 | Pending |
| HSCL-07 | Phase 68 | Pending |
| HSCL-08 | Phase 68 | Pending |
| HSCL-09 | Phase 71 | Pending |
| HSCL-10 | Phase 83 | Pending |
| MRES-01 | Phase 68 | Pending |
| MRES-02 | Phase 68 | Pending |
| MRES-03 | Phase 68 | Pending |
| MRES-04 | Phase 68 | Pending |
| MRES-05 | Phase 71 | Pending |
| MRES-06 | Phase 78 | Pending |
| MRES-07 | Phase 78 | Pending |
| MRES-08 | Phase 84 | Pending |
| MRES-09 | Phase 68 | Pending |
| MRES-10 | Phase 68 | Pending |
| MRES-11 | Phase 71 | Pending |
| MRES-12 | Phase 83 | Pending |
| DPLY-01 | Phase 84 | Pending |
| DPLY-02 | Phase 84 | Pending |
| DPLY-03 | Phase 84 | Pending |
| DPLY-04 | Phase 84 | Pending |
| DPLY-05 | Phase 84 | Pending |
| DPLY-06 | Phase 84 | Pending |
| DPLY-07 | Phase 84 | Pending |
| DPLY-08 | Phase 84 | Pending |
| DPLY-09 | Phase 84 | Pending |
| DPLY-10 | Phase 84 | Pending |
| DPLY-11 | Phase 84 | Pending |
| DPLY-12 | Phase 84 | Pending |
| DPLY-13 | Phase 84 | Pending |
| EDGE-01 | Phase 85 | Pending |
| EDGE-02 | Phase 85 | Pending |
| EDGE-03 | Phase 85 | Pending |
| EDGE-04 | Phase 85 | Pending |
| EDGE-05 | Phase 85 | Pending |
| EDGE-06 | Phase 85 | Pending |
| EDGE-07 | Phase 85 | Pending |
| EDGE-08 | Phase 85 | Pending |
| EDGE-09 | Phase 85 | Pending |
| EDGE-10 | Phase 85 | Pending |
| EDGE-11 | Phase 85 | Pending |
| EDGE-12 | Phase 85 | Pending |
| EDGE-13 | Phase 85 | Pending |
| AIE-01 | Phase 86 | Pending |
| AIE-02 | Phase 86 | Pending |
| AIE-03 | Phase 86 | Pending |
| AIE-04 | Phase 86 | Pending |
| AIE-05 | Phase 86 | Pending |
| AIE-06 | Phase 86 | Pending |
| AIE-07 | Phase 86 | Pending |
| AIE-08 | Phase 86 | Pending |
| AIE-09 | Phase 86 | Pending |
| AIE-10 | Phase 86 | Pending |
| AIE-11 | Phase 86 | Pending |
| AIE-12 | Phase 86 | Pending |
| AIE-13 | Phase 86 | Pending |
| AIE-14 | Phase 86 | Pending |
| AIE-15 | Phase 86 | Pending |
| AIE-16 | Phase 86 | Pending |
| AIE-17 | Phase 86 | Pending |
| AIE-18 | Phase 86 | Pending |
| AIE-19 | Phase 86 | Pending |
| AIE-20 | Phase 86 | Pending |
| AIE-21 | Phase 86 | Pending |
| AIE-22 | Phase 86 | Pending |
| AIE-23 | Phase 86 | Pending |
| AIE-24 | Phase 86 | Pending |
| AIE-25 | Phase 86 | Pending |
| AIE-26 | Phase 86 | Pending |
| AIE-27 | Phase 86 | Pending |
| ECOS-01 | Phase 89 | Pending |
| ECOS-02 | Phase 89 | Pending |
| ECOS-03 | Phase 89 | Pending |
| ECOS-04 | Phase 89 | Pending |
| ECOS-05 | Phase 89 | Pending |
| ECOS-06 | Phase 89 | Pending |
| ECOS-07 | Phase 89 | Pending |
| ECOS-08 | Phase 89 | Pending |
| ECOS-09 | Phase 89 | Pending |
| ECOS-10 | Phase 89 | Pending |
| ECOS-11 | Phase 89 | Pending |
| ECOS-12 | Phase 89 | Pending |
| ECOS-13 | Phase 89 | Pending |
| ECOS-14 | Phase 89 | Pending |
| ECOS-15 | Phase 89 | Pending |
| ECOS-16 | Phase 89 | Pending |
| ECOS-17 | Phase 89 | Pending |
| ECOS-18 | Phase 89 | Pending |
| DSCL-01 | Phase 88 | Pending |
| DSCL-02 | Phase 88 | Pending |
| DSCL-03 | Phase 88 | Pending |
| DSCL-04 | Phase 88 | Pending |
| DSCL-05 | Phase 88 | Pending |
| DSCL-06 | Phase 88 | Pending |
| DSCL-07 | Phase 88 | Pending |
| DSCL-08 | Phase 88 | Pending |
| DSCL-09 | Phase 88 | Pending |
| DSCL-10 | Phase 88 | Pending |
| DSCL-11 | Phase 88 | Pending |
| DSCL-12 | Phase 88 | Pending |
| DSCL-13 | Phase 88 | Pending |
| DSCL-14 | Phase 88 | Pending |
| DSCL-15 | Phase 88 | Pending |
| DSCL-16 | Phase 88 | Pending |
| DSCL-17 | Phase 88 | Pending |
| DSCL-18 | Phase 88 | Pending |
| DSCL-19 | Phase 88 | Pending |
| DSCL-20 | Phase 88 | Pending |
| DSCL-21 | Phase 88 | Pending |
| DSCL-22 | Phase 88 | Pending |
| DSCL-23 | Phase 88 | Pending |
| DSCL-24 | Phase 88 | Pending |
| DSCL-25 | Phase 88 | Pending |
| DSCL-26 | Phase 88 | Pending |
| DSCL-27 | Phase 88 | Pending |
| VOPT-01 | Phase 87 | Pending |
| VOPT-02 | Phase 87 | Pending |
| VOPT-03 | Phase 87 | Pending |
| VOPT-04 | Phase 87 | Pending |
| VOPT-05 | Phase 87 | Pending |
| VOPT-06 | Phase 87 | Pending |
| VOPT-07 | Phase 87 | Pending |
| VOPT-08 | Phase 87 | Pending |
| VOPT-09 | Phase 87 | Pending |
| VOPT-10 | Phase 87 | Pending |
| VOPT-11 | Phase 87 | Pending |
| VOPT-12 | Phase 87 | Pending |
| VOPT-13 | Phase 87 | Pending |
| VOPT-14 | Phase 87 | Pending |
| VOPT-15 | Phase 87 | Pending |
| VOPT-16 | Phase 87 | Pending |
| VOPT-17 | Phase 87 | Pending |
| VOPT-18 | Phase 87 | Pending |
| VOPT-19 | Phase 87 | Pending |
| VOPT-20 | Phase 87 | Pending |
| VOPT-21 | Phase 87 | Pending |
| VOPT-22 | Phase 87 | Pending |
| VOPT-23 | Phase 87 | Pending |
| VOPT-24 | Phase 87 | Pending |
| VOPT-25 | Phase 87 | Pending |
| VOPT-26 | Phase 87 | Pending |
| VOPT-27 | Phase 87 | Pending |
| VOPT-28 | Phase 87 | Pending |

**Coverage:**
- v6.0 requirements: 294 total (SDKF:12, CASC:8, PERF:7, AIPI:11, AUTH:9, PERS:7, VDEF:18, VREG:18, VTMP:10, OMOD:7, FEXT:8, TIER:5, PLUG:6, MIGR:6, INTG:8, VADV:3, PADV:3, HSCL:10, MRES:12, DPLY:13, EDGE:13, AIE:27, ECOS:18, DSCL:27, VOPT:28)
- Mapped to phases: 294
- Unmapped: 0

---
*Requirements defined: 2026-02-20*
*Last updated: 2026-02-20 after Ecosystem Compatibility design*