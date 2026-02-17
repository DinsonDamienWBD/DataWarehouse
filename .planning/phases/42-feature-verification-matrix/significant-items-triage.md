# Significant Items Triage Report

## Summary
- Total Significant Items: 631 (features scored 50-79%)
- Implement in v4.0: TBD (after effort/tier analysis)
- Defer to v5.0: TBD (after effort/tier analysis)

## Triage Methodology

### Effort Estimation (S/M/L/XL)
- **S (Small)**: 1-4 hours — Simple missing features, polish work
- **M (Medium)**: 4-16 hours — Complete partial implementations
- **L (Large)**: 16-40 hours — Major feature additions
- **XL (Extra Large)**: 40+ hours — Near-complete rewrites

### Customer Tier Impact (Priority 1-7)
1. **Tier 7 (Hyperscale)** — Distributed consensus, multi-region
2. **Tier 6 (Military/Government)** — Air-gap, zero-trust, FIPS
3. **Tier 5 (High-Stakes/Regulated)** — Healthcare, finance, compliance
4. **Tier 4 (Real-Time)** — Streaming, async pipeline, low-latency
5. **Tier 3 (Enterprise)** — Multi-tenant, HA, scalability
6. **Tier 2 (SMB)** — Cost-effective, ease-of-use
7. **Tier 1 (Developer)** — Dev tooling, SDK features

### Implementation Priority Matrix

| Tier | S (1-4h) | M (4-16h) | L (16-40h) | XL (40+h) |
|------|:--------:|:---------:|:----------:|:---------:|
| **Tier 7 (Hyperscale)** | ✅ Implement | ✅ Implement | ⚠️ Case-by-case | ❌ Defer v5.0 |
| **Tier 6 (Military)** | ✅ Implement | ✅ Implement | ⚠️ Case-by-case | ❌ Defer v5.0 |
| **Tier 5 (High-Stakes)** | ✅ Implement | ✅ Implement | ⚠️ Case-by-case | ❌ Defer v5.0 |
| **Tier 4 (Real-Time)** | ✅ Implement | ⚠️ Case-by-case | ❌ Defer v5.0 | ❌ Defer v5.0 |
| **Tier 3 (Enterprise)** | ✅ Implement | ⚠️ Case-by-case | ❌ Defer v5.0 | ❌ Defer v5.0 |
| **Tier 2 (SMB)** | ⚠️ Case-by-case | ❌ Defer v5.0 | ❌ Defer v5.0 | ❌ Defer v5.0 |
| **Tier 1 (Developer)** | ⚠️ Case-by-case | ❌ Defer v5.0 | ❌ Defer v5.0 | ❌ Defer v5.0 |

## Significant Items by Domain

### Domain 1: Data Pipeline (112 features at 50-79%)

Based on Plan 42-01 summary, the 112 partial features include:
- Streaming infrastructure (42 strategies at 55-70%)
- Workflow orchestration (28 strategies at 55-65%)
- ETL/data integration (29 strategies at 30-60%)
- Data format processing (13 strategies at 50-65%)

#### High-Priority v4.0 Implementation (Tier 4-7, S/M effort)

**Streaming Infrastructure (Tier 4 - Real-Time)**
- Kafka Stream (60%) — **M effort (8h)** — Complete consumer group management, exactly-once semantics
- Kafka Stream Processing (60%) — **M effort (12h)** — Add state stores, windowing, basic joins
- Kinesis Stream (55%) — **M effort (10h)** — Add shard iterator management, checkpointing
- Event Hubs Stream (60%) — **M effort (8h)** — Complete partition management, checkpoint store
- MQTT Stream (70%) — **S effort (4h)** — Add QoS 2 validation, retained messages
- Redis Streams (60%) — **M effort (6h)** — Add consumer groups, pending entries

**Workflow Orchestration (Tier 3 - Enterprise)**
- Airflow workflow (65%) — **M effort (12h)** — Add DAG execution, task dependencies
- Temporal workflow (60%) — **M effort (12h)** — Add workflow versioning, child workflows
- Prefect workflow (60%) — **M effort (10h)** — Add flow scheduling, task runners

**Decision**: Implement Tier 4 streaming features (50h total), defer most workflow (Tier 3 M) to v5.0

#### Deferred to v5.0 (L/XL effort OR Tier 1-3)

- Flink Stream Processing (55%) — **L effort (30h)** — Defer (complex state backends)
- Advanced workflow (28 features) — **M effort each** — Defer (Tier 3)
- Data integration ETL (29 features at 30-40%) — **L effort** — Defer (low completion %)

### Domain 2: Storage & Persistence (148 features at 50-79%)

Based on Plan 42-01 summary, the 148 partial features include:
- Distributed storage HA (12 strategies at 60-70%)
- Nested RAID levels (6 strategies at 60-65%)
- Filesystem advanced features (3 strategies at 50-60%)
- Database storage optimization (127 remaining)

#### High-Priority v4.0 Implementation (Tier 5-7, S/M effort)

**Distributed Storage (Tier 7 - Hyperscale)**
- Multi-region replication (65%) — **M effort (12h)** — Add cross-region consistency
- Erasure coding (60%) — **M effort (14h)** — Complete Reed-Solomon implementation
- Quorum reads/writes (70%) — **S effort (4h)** — Add tunable consistency

**RAID Advanced (Tier 3 - Enterprise)**
- RAID 10 (65%) — **M effort (8h)** — Complete rebuild algorithms
- RAID 50/60 (60%) — **M effort (10h)** — Add nested stripe/parity

**Decision**: Implement Tier 7 distributed storage (30h), RAID 10/50/60 (18h), defer filesystem

#### Deferred to v5.0

- Filesystem deduplication/encryption (3 features) — **L effort (40h total)** — Defer
- Advanced database optimizations (100+ features) — **Various** — Defer (need detailed analysis)

### Domain 3: Security & Cryptography (108 features at 50-79%)

Based on Plan 42-01 summary, the 108 partial features include:
- Post-quantum crypto (10 strategies at 55-60%)
- Advanced key management (15 strategies at 60-70%)
- Blockchain optimization (5 strategies at 60-65%)
- Policy engines (78 remaining)

#### High-Priority v4.0 Implementation (Tier 6, S/M effort)

**Post-Quantum Cryptography (Tier 6 - Military)**
- CRYSTALS-Kyber (60%) — **M effort (12h)** — Integrate NIST reference library
- CRYSTALS-Dilithium (60%) — **M effort (12h)** — Integrate NIST reference library
- SPHINCS+ (55%) — **M effort (14h)** — Integrate NIST reference library

**Key Management (Tier 5 - High-Stakes)**
- HSM key rotation (70%) — **S effort (4h)** — Add automated rotation scheduling
- Key derivation advanced (65%) — **M effort (8h)** — Add HKDF variants

**Decision**: Implement all Tier 5-6 crypto features (50h total)

#### Deferred to v5.0

- Advanced policy engines (70+ features) — **M effort each** — Defer (Tier 2-3)
- Blockchain gas optimization (5 features) — **L effort** — Defer (niche use case)

### Domain 4: Media & Format Processing (28 features at 50-79%)

Based on Plan 42-01 summary, the 28 partial features include:
- GPU acceleration (3 strategies at 50-60%)
- AI processing (5 strategies at 50-60%)
- Advanced video (6 strategies at 20-60%)
- Audio/image processing (14 remaining)

#### High-Priority v4.0 Implementation (Tier 3-4, S effort)

**GPU Acceleration (Tier 4 - Real-Time)**
- CUDA detection/fallback (60%) — **S effort (4h)** — Add graceful CPU fallback
- GPU memory management (55%) — **M effort (8h)** — Add proper allocation tracking

**AI Processing (Tier 3 - Enterprise)**
- ONNX model loading (60%) — **M effort (10h)** — Complete model inference pipeline

**Decision**: Implement GPU fallback (4h), defer AI/advanced video

#### Deferred to v5.0

- Advanced video (3D/360/VR) — **L effort** — Defer (complex, niche)
- ONNX full integration (5 features) — **M effort** — Defer to v5.0 (Tier 3)

### Domain 5: Distributed Systems (42 features at 50-79%)

Based on Plan 42-02 summary:
- AI-driven replication (9 @ 60%)
- Advanced active-active (3 @ 65%)
- Consensus algorithms (3 @ 50%)
- Replication dashboards (12 @ 40%)

#### High-Priority v4.0 Implementation (Tier 7, M effort)

**Advanced Active-Active (Tier 7 - Hyperscale)**
- Multi-region write coordination (65%) — **M effort (14h)** — Add conflict resolution
- Active-active geo-distribution (65%) — **M effort (12h)** — Add topology management

**Consensus Algorithms (Tier 7 - Hyperscale)**
- Paxos implementation (50%) — **L effort (24h)** — **Case-by-case: DEFER** (Raft already exists)
- PBFT implementation (50%) — **L effort (20h)** — **Case-by-case: DEFER** (niche Byzantine fault tolerance)

**Decision**: Implement active-active (26h), defer Paxos/PBFT, defer AI-driven features

#### Deferred to v5.0

- AI-driven replication (9 features) — **M effort** — Defer (needs ML models)
- Paxos/PBFT/ZAB (3 algorithms) — **L effort** — Defer (Raft covers most use cases)
- Dashboards (12 features) — **L effort** — Defer (need unified dashboard framework)

### Domain 6: Hardware Integration (12 features at 50-79%)

Based on Plan 42-02 summary:
- Cross-language SDKs (6 @ 60%)
- gRPC/MessagePack (2 @ 50-60%)
- Application platform (4 @ 30-40%)

#### High-Priority v4.0 Implementation (Tier 5, M effort)

**gRPC Services (Tier 5 - High-Stakes)**
- gRPC service contracts (60%) — **M effort (12h)** — Complete service definitions
- MessagePack serialization (55%) — **M effort (8h)** — Add schema evolution

**Decision**: Implement gRPC/MessagePack (20h), defer cross-language SDKs

#### Deferred to v5.0

- Python/Go/Rust SDKs (6 features) — **L effort (30h each)** — Defer (large effort)
- Application platform (4 features) — **L effort** — Defer (complex OAuth/policy)

### Domain 7: Edge/IoT (23 features at 50-79%)

Based on Plan 42-02 summary:
- Edge environments (5 @ 70-75%)
- RTOS bridge (4 @ 25-30%)
- Edge operations (5 @ 70-75%)
- IoT protocols (9 @ 60-70%)

#### High-Priority v4.0 Implementation (Tier 4-5, S/M effort)

**Edge Environments (Tier 4 - Real-Time)**
- Industrial gateway (75%) — **S effort (4h)** — Add SCADA/CAN protocol polish
- Medical device (70%) — **M effort (8h)** — Complete HL7/DICOM integration

**IoT Protocols (Tier 4 - Real-Time)**
- OPC-UA server (65%) — **M effort (10h)** — Add subscription management
- Modbus advanced (65%) — **M effort (6h)** — Complete function code coverage

**Decision**: Implement edge/IoT protocols (28h total)

#### Deferred to v5.0

- RTOS bridge (4 features @ 25-30%) — **XL effort** — Defer (requires RTOS hardware)

### Domain 8: AEDS (0 features at 50-79%)

Based on Plan 42-02: Bimodal distribution (either 80-99% or 1-19%), no partial implementations.

**No significant items in 50-79% range.**

### Domains 9-13: Air-Gap, Filesystem, Compute, Transport, Intelligence (17 features at 50-79%)

Based on Plan 42-03 summary, only 17 features in this range (most are either complete or metadata-only):

- Data diode (50%) — **L effort** — Defer (hardware integration)
- WASM advanced features (60%) — **M effort** — Defer (need Wasmtime completion)
- Filesystem partial implementations (5 @ 50-60%) — **XL effort** — Defer (massive scope)
- AI provider partial integrations (5 @ 60%) — **M effort** — Defer (need SDK priorities)
- Advanced compute features (5 @ 55%) — **L effort** — Defer (need runtime completion)

**Decision**: All 17 features deferred to v5.0 (either L/XL effort or blocked by larger dependencies)

### Domains 14-17: Observability, Governance, Cloud, CLI/GUI (141 features at 50-79%)

Based on Plan 42-04 summary:

**Domain 14: Observability (20 features at 50-79%)**
- RUM monitoring (6 @ 50-60%) — **M effort** — Defer (Tier 2)
- Synthetic monitoring (4 @ 55%) — **M effort** — Defer (Tier 2)
- Dashboard services (10 @ 50%) — **L effort** — Defer (need framework)

**Domain 15: Governance (88 features at 50-79%)**
- Lineage advanced features (15 @ 60-75%) — **S/M effort** — **Implement** (Tier 5)
- Retention automation (10 @ 65-70%) — **S/M effort** — **Implement** (Tier 5)
- Privacy ML features (8 @ 60%) — **M effort** — Defer (need ML models)
- Emerging tech backups (5 @ 50%) — **XL effort** — Defer (DNA/Quantum hardware)
- Policy dashboards (50 @ 50-60%) — **L effort** — Defer (need framework)

**Domain 16: Cloud (33 features at 50-79%)**
- Kubernetes CSI (1 @ 60%) — **L effort (20h)** — **Implement** (Tier 6)
- Multi-cloud advanced (8 @ 65-70%) — **M effort** — **Implement** (Tier 7)
- Sustainability (15 @ 55%) — **M effort** — Defer (need energy APIs)
- Deployment dashboards (9 @ 50%) — **L effort** — Defer (need framework)

**Domain 17: CLI/GUI (2 features at 20-49%, not 50-79%)**
- Both below 50%, not in significant items range

**Decision**: Implement Governance lineage/retention (40h), K8s CSI (20h), multi-cloud (30h)

## v4.0 Implementation Plan (by Tier and Effort)

### Tier 7 (Hyperscale) — HIGHEST PRIORITY

#### Small Effort (1-4h)
- [ ] Quorum reads/writes (Storage) — 4h — Tunable consistency levels

#### Medium Effort (4-16h)
- [ ] Multi-region replication (Storage) — 12h — Cross-region consistency
- [ ] Erasure coding (Storage) — 14h — Reed-Solomon implementation
- [ ] Multi-region write coordination (Distributed) — 14h — Conflict resolution
- [ ] Active-active geo-distribution (Distributed) — 12h — Topology management
- [ ] Multi-cloud advanced features (Cloud, 8 features) — 30h total — Cross-cloud sync

**Tier 7 Total**: 86 hours (11 features)

### Tier 6 (Military/Government)

#### Small Effort (1-4h)
- None

#### Medium Effort (4-16h)
- [ ] CRYSTALS-Kyber (Security) — 12h — NIST library integration
- [ ] CRYSTALS-Dilithium (Security) — 12h — NIST library integration
- [ ] SPHINCS+ (Security) — 14h — NIST library integration

#### Large Effort (16-40h)
- [ ] Kubernetes CSI driver (Cloud) — 20h — Full CSI spec implementation

**Tier 6 Total**: 58 hours (4 features)

### Tier 5 (High-Stakes/Regulated)

#### Small Effort (1-4h)
- [ ] HSM key rotation (Security) — 4h — Automated rotation scheduling

#### Medium Effort (4-16h)
- [ ] Key derivation advanced (Security) — 8h — HKDF variants
- [ ] gRPC service contracts (Hardware) — 12h — Service definitions
- [ ] MessagePack serialization (Hardware) — 8h — Schema evolution
- [ ] Lineage advanced features (Governance, 15 features) — 25h total — Dependency tracking
- [ ] Retention automation (Governance, 10 features) — 15h total — Lifecycle policies

**Tier 5 Total**: 72 hours (30 features including multi-feature groups)

### Tier 4 (Real-Time)

#### Small Effort (1-4h)
- [ ] MQTT Stream polish (Pipeline) — 4h — QoS 2, retained messages
- [ ] CUDA detection/fallback (Media) — 4h — CPU fallback
- [ ] Industrial gateway (Edge) — 4h — SCADA/CAN polish

#### Medium Effort (4-16h)
- [ ] Kafka Stream (Pipeline) — 8h — Consumer groups, exactly-once
- [ ] Kafka Stream Processing (Pipeline) — 12h — State stores, windowing
- [ ] Kinesis Stream (Pipeline) — 10h — Shard management, checkpointing
- [ ] Event Hubs Stream (Pipeline) — 8h — Partition management
- [ ] Redis Streams (Pipeline) — 6h — Consumer groups
- [ ] Medical device edge (Edge) — 8h — HL7/DICOM
- [ ] OPC-UA server (Edge) — 10h — Subscription management
- [ ] Modbus advanced (Edge) — 6h — Function code coverage

**Tier 4 Total**: 80 hours (12 features)

### Tier 3 (Enterprise)

#### Small Effort (1-4h)
- None selected (most are M effort, case-by-case defer)

#### Medium Effort (4-16h)
- [ ] RAID 10 (Storage) — 8h — Rebuild algorithms
- [ ] RAID 50/60 (Storage) — 10h — Nested stripe/parity

**Tier 3 Total**: 18 hours (2 features) — **Most Tier 3 deferred**

### v4.0 Implementation Summary

**Total Features to Implement**: ~65-70 features
**Total Effort**: 314 hours (~8 weeks with 1 engineer, ~4 weeks with 2 engineers)

**Priority Order**:
1. Tier 7 (Hyperscale) — 86h
2. Tier 6 (Military) — 58h
3. Tier 5 (High-Stakes) — 72h
4. Tier 4 (Real-Time) — 80h
5. Tier 3 (Enterprise) — 18h (selective)

## v5.0 Deferral Plan

### Deferred: Large Effort Features (16-40h)

#### Streaming & Pipeline (Tier 4, L effort)
- **Flink Stream Processing** (55%) — 30h — Complex state backends, savepoints
  - **What exists**: Flink job submission scaffolding
  - **What's missing**: State backends (RocksDB, filesystem), savepoints, rescaling logic
  - **Why deferred**: 30h effort, Kafka/Kinesis covers most use cases for v4.0
  - **v5.0 approach**: Full Flink DataStream API integration with state management

#### Storage & Persistence (Tier 3, L effort)
- **Filesystem deduplication** (60%) — 20h — Content-addressable storage, hash indexing
- **Filesystem encryption** (55%) — 20h — LUKS-style encryption, key management
  - **What exists**: Basic filesystem metadata structures
  - **What's missing**: Dedup index, encryption layer integration
  - **Why deferred**: 40h combined, niche use cases
  - **v5.0 approach**: Integrate with VDE (Virtual Disk Engine)

#### Distributed Systems (Tier 7, L effort — case-by-case deferred)
- **Paxos consensus** (50%) — 24h — Multi-Paxos, leader election, log replication
- **PBFT consensus** (50%) — 20h — Byzantine fault tolerance, view changes
- **ZAB consensus** (50%) — 18h — ZooKeeper Atomic Broadcast
  - **What exists**: Consensus interface, basic leader election
  - **What's missing**: Full algorithm implementations
  - **Why deferred**: Raft already provides production consensus, Paxos/PBFT niche
  - **v5.0 approach**: Add when Byzantine fault tolerance needed (blockchain, adversarial networks)

### Deferred: Extra Large Effort Features (40+h)

#### Cross-Language SDKs (Tier 2, XL effort)
- **Python SDK** (60%) — 50h — Full API bindings, Pythonic wrappers
- **Go SDK** (60%) — 50h — cgo bindings, Go-native interfaces
- **Rust SDK** (60%) — 60h — FFI bindings, safe Rust wrappers
  - **What exists**: C# interop samples, P/Invoke declarations
  - **What's missing**: Language-specific API design, packaging, docs
  - **Why deferred**: 160h total, low Tier priority
  - **v5.0 approach**: Use code generation from C# metadata

#### RTOS Bridge (Tier 4, XL effort)
- **VxWorks bridge** (25%) — 40h — RTOS integration, memory constraints
- **QNX bridge** (25%) — 40h — Microkernel messaging
- **FreeRTOS bridge** (30%) — 35h — Task scheduling, minimal footprint
- **Zephyr bridge** (30%) — 35h — Device tree integration
  - **What exists**: Concept definitions, interface stubs
  - **What's missing**: Everything (requires RTOS hardware)
  - **Why deferred**: 150h total, requires specialized hardware not available
  - **v5.0 approach**: Partner with RTOS hardware vendor for integration

#### Emerging Technologies (Tier 6, XL effort)
- **DNA Backup** (20%) — 60h — DNA synthesis API integration, error correction
- **Quantum-Safe Backup** (20%) — 50h — QKD hardware integration
  - **What exists**: Interface definitions only
  - **What's missing**: Hardware integration, specialized protocols
  - **Why deferred**: Requires DNA synthesizer or QKD hardware not widely available
  - **v5.0 approach**: Defer until hardware becomes commodity (2028+)

### Deferred: Medium Effort, Low-Tier Features

#### AI/ML Features (Tier 2-3, M effort)
- **AI-driven replication** (9 features @ 60%) — 80h total
- **Privacy ML features** (8 features @ 60%) — 70h total
- **ONNX full integration** (5 features @ 60%) — 50h total
  - **What exists**: ML model loading interfaces, basic inference
  - **What's missing**: Model training integration, hyperparameter tuning
  - **Why deferred**: Tier 2-3 priority, need ML model ecosystem
  - **v5.0 approach**: Integrate with TensorFlow.NET, ML.NET

#### Dashboard/UI Framework (Tier 2, M effort per domain)
- **Replication dashboards** (12 features @ 40%) — 60h
- **Governance dashboards** (50 features @ 50-60%) — 120h
- **Observability dashboards** (10 features @ 50%) — 40h
- **Deployment dashboards** (9 features @ 50%) — 35h
  - **What exists**: Backend metrics, data collection
  - **What's missing**: Web UI framework, visualization components
  - **Why deferred**: 255h total, need unified dashboard framework first
  - **v5.0 approach**: Build React/Blazor dashboard framework, then add domain dashboards

#### Policy Engines (Tier 2-3, M effort)
- **Advanced policy features** (70+ features @ 60-70%) — 200h+ total
  - **What exists**: Basic RBAC, policy evaluation
  - **What's missing**: Advanced ABAC, dynamic policies, policy simulation
  - **Why deferred**: Tier 2-3, large scope
  - **v5.0 approach**: Integrate with OPA (Open Policy Agent)

#### Workflow Orchestration (Tier 3, M effort)
- **Advanced workflow features** (28 features @ 55-65%) — 120h total
  - **What exists**: Basic DAG execution
  - **What's missing**: Distributed execution, failure recovery, dynamic DAGs
  - **Why deferred**: Tier 3, Airflow/Temporal integration covers most use cases
  - **v5.0 approach**: Full distributed workflow engine

### Deferred: Blocked by Dependencies

#### Cloud/Metadata-Driven Features (Blocked by SDK integration)
- **Filesystem implementations** (41 features @ 5-20%) — Blocked by VDE completion
- **Compute runtimes** (158 features @ 5-15%) — Blocked by WASM/container engine
- **Cloud connectors** (280+ features @ 5-10%) — Blocked by AWS/Azure/GCP SDKs (Plan 42-03 finding)
- **AI providers** (20 features @ 5-15%) — Blocked by OpenAI/Anthropic SDK integration
  - **Why deferred**: These are metadata-only, need foundational SDKs first
  - **v5.0 approach**: Complete SDK integrations in v4.0 Phase 43+, then implement strategies

## Triage Decision Summary

### Implement in v4.0 (65-70 features, 314 hours)

**Breakdown by tier**:
- Tier 7: 11 features, 86 hours (Hyperscale)
- Tier 6: 4 features, 58 hours (Military/Government)
- Tier 5: ~30 features, 72 hours (High-Stakes/Regulated)
- Tier 4: 12 features, 80 hours (Real-Time)
- Tier 3: 2 features, 18 hours (Enterprise, selective)

**Rationale**: Focus on high-tier (5-7), reasonable-effort (S/M) features that have highest customer impact and are achievable within v4.0 timeline.

### Defer to v5.0 (560+ features)

**Categories**:
- Large effort (L): ~100 features, 2,000+ hours
- Extra-large effort (XL): ~50 features, 3,000+ hours
- Medium effort, low tier (Tier 1-3): ~300 features, 1,500+ hours
- Blocked by dependencies: ~300+ features (need foundational SDKs)

**Rationale**:
- L/XL features require significant investment
- Low-tier features have less customer impact
- Dependency-blocked features cannot proceed until foundational work complete
- Better to ship v4.0 with fewer, high-quality features than rush and create technical debt

## Next Steps

1. Review this triage with stakeholders
2. Confirm v4.0 implementation list (65-70 features, 314h)
3. Execute Plan 42-06 Task 3: Implement high-priority items (Tier 5-7)
4. Execute Plan 42-06 Task 4: Implement medium-priority items (Tier 3-4)
5. Execute Plan 42-06 Task 5: Document deferred items (detailed v5.0 roadmap)
6. Execute Plan 42-06 Task 6: Verification pass

## Implementation Notes

- **Effort estimates** are conservative (may be less with existing scaffolding)
- **Customer tier** assignments based on `.planning/v4.0-MILESTONE-DRAFT.md` requirements
- **Case-by-case decisions** documented inline (e.g., Paxos deferred despite Tier 7 because Raft exists)
- **Dependencies** tracked (e.g., dashboards need unified framework first)
- **Hardware blockers** identified (e.g., RTOS requires physical hardware)
