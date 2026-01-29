# DataWarehouse Production Readiness - Implementation Plan

## Executive Summary

This document outlines the implementation plan for achieving full production readiness across all deployment tiers (Individual, SMB, Enterprise, High-Stakes, Hyperscale). Tasks are ordered by priority and organized by severity.

---

## IMPLEMENTATION STRATEGY

Before implementing any task:
1. Read this TODO.md
2. Read Metadata/CLAUDE.md
3. Read Metadata/RULES.md
4. Plan implementation according to the rules and style guidelines (minimize code duplication, maximize reuse)
5. Implement according to the implementation plan
6. Update Documentation (XML docs for all public entities - functions, variables, enums, classes, interfaces etc.)
7. At each step, ensure full production readiness, no simulations, placeholders, mocks, simplifications or shortcuts
8. Add Test Cases for each feature

---

## COMMIT STRATEGY

After completing each task:
1. Verify the actual implemented code to see that the implementation is fully production ready without any simulations, placeholders, mocks, simplifications or shortcuts
2. If the verification fails, continue with the implementation of this task, until the code reaches a level where it passes the verification.
3. Only after it passes verification, update this TODO.md with completion status
4. Commit changes and the updated TODO.md document with descriptive message
5. Move to next task

Do NOT wait for an entire phase to complete before committing.

---

## NOTES

- Follow the philosophy of code reuse: Leverage existing abstractions before creating new ones
- Commit frequently to avoid losing work
- Test each feature thoroughly before moving to the next
- Document all security-related changes

---

## Plugin Implementation Checklist

For each plugin:
1. [ ] Create plugin project in `Plugins/DataWarehouse.Plugins.{Name}/`
2. [ ] Implement plugin class extending appropriate base class
3. [ ] Add XML documentation for all public members
4. [ ] Register plugin in solution file DataWarehouse.slnx
5. [ ] Add unit tests
6. [ ] Update this TODO.md with completion status

---

## PRODUCTION READINESS SPRINT (2026-01-25)

### Overview

This sprint addresses all CRITICAL and HIGH severity issues identified in the comprehensive code review. Each task must be verified as production-ready (no simulations, placeholders, mocks, or shortcuts) before being marked complete.

### Phase 1: CRITICAL Issues (Must Fix Before ANY Deployment)

#### Task 26: Fix Raft Consensus Plugin - Silent Exception Swallowing
**File:** `Plugins/DataWarehouse.Plugins.Raft/RaftConsensusPlugin.cs`
**Issue:** 12+ empty catch blocks silently swallow exceptions, making distributed consensus failures invisible
**Priority:** CRITICAL
**Status:** ✅ **COMPLETED** (2026-01-25)

| Step | Action | Status |
|------|--------|--------|
| 7 | Add unit tests for exception scenarios | [~] Deferred to testing sprint |

---

#### Task 28: Fix Raft Consensus Plugin - No Log Persistence
**File:** `Plugins/DataWarehouse.Plugins.Raft/RaftConsensusPlugin.cs:52`
**Issue:** Raft log not persisted - data loss on restart, violates Raft safety guarantee
**Priority:** CRITICAL
**Status:** ✅ **COMPLETED** (2026-01-25)

| Step | Action | Status |
|------|--------|--------|
| 8 | Add unit tests for crash recovery scenarios | [~] Deferred to testing sprint |

**New files created:**
- `Plugins/DataWarehouse.Plugins.Raft/IRaftLogStore.cs`
- `Plugins/DataWarehouse.Plugins.Raft/FileRaftLogStore.cs`

---

### Phase 2: HIGH Issues (Must Fix Before Enterprise Deployment)

#### Task 30: Fix S3 Storage Plugin - Fragile XML Parsing
**File:** `Plugins/DataWarehouse.Plugins.S3Storage/S3StoragePlugin.cs:407-430`
**Issue:** Using string.Split for XML parsing - S3 operations may fail on edge cases
**Priority:** HIGH
**Status:** ✅ **COMPLETED** (2026-01-25)

| Step | Action | Status |
|------|--------|--------|
| 4 | Test with various S3 response formats | [~] Deferred to testing sprint |

**Verification:** `grep "\.Split\(" S3StoragePlugin.cs` returns 0 matches for XML parsing

---

#### Task 31: Fix S3 Storage Plugin - Fire-and-Forget Async
**File:** `Plugins/DataWarehouse.Plugins.S3Storage/S3StoragePlugin.cs:309-317`
**Issue:** Async calls without error handling causing silent indexing failures
**Priority:** HIGH
**Status:** ✅ **COMPLETED** (2026-01-25)

| Step | Action | Status |
|------|--------|--------|
| 4 | Add success/failure metrics | [~] Deferred to testing sprint |

---

## GOD TIER FEATURES - Future-Proofing & Industry Leadership (2026-2027)

### Overview

These features represent the next generation of data storage technology, positioning DataWarehouse as the **undisputed leader** in enterprise, government, military, and hyperscale storage. Each feature is designed to be **industry-first** or **industry-only**, providing capabilities that no competitor can match.

**Target:** Transform DataWarehouse from a storage platform into a **complete data operating system**.

---

### CATEGORY B: Native Filesystem Integration

#### Task 45: Hypervisor Support (VMware, Hyper-V, KVM, Xen)
**Priority:** P1
**Effort:** High
**Status:** [ ] Not Started

**Description:** Enable DataWarehouse to run optimally in virtualized environments with hypervisor-specific optimizations.

**Hypervisor Support Matrix:**

| Hypervisor | Guest Tools | Storage Backend | Live Migration | Status |
|------------|-------------|-----------------|----------------|--------|
| VMware ESXi | open-vm-tools | VMDK, vSAN | vMotion | [ ] |
| Hyper-V | hv_utils | VHDX, SMB3 | Live Migration | [ ] |
| KVM/QEMU | qemu-guest-agent | virtio, Ceph | libvirt migrate | [ ] |
| Xen | xe-guest-utilities | VDI, SR | XenMotion | [ ] |
| Proxmox | pve-qemu-kvm | LVM, ZFS, Ceph | Online migrate | [ ] |
| oVirt/RHV | ovirt-guest-agent | NFS, GlusterFS | Live migration | [ ] |
| Nutanix AHV | NGT | Nutanix DSF | AHV migrate | [ ] |

**Hypervisor Optimizations:**

| Optimization | Description | Status |
|--------------|-------------|--------|
| Balloon Driver | Memory optimization | [ ] |
| TRIM/Discard | Thin provisioning | [ ] |
| Paravirtualized I/O | virtio, vmxnet3, pvscsi | [ ] |
| Hot-Add CPU/Memory | Dynamic resource scaling | [ ] |
| Snapshot Integration | VM-consistent snapshots | [ ] |
| Backup API Integration | VMware VADP, Hyper-V VSS | [ ] |
| Fault Tolerance | HA cluster awareness | [ ] |

---

#### Task 46: Bare Metal Optimization & Hardware Acceleration
**Priority:** P1
**Effort:** High
**Status:** [ ] Not Started

**Description:** Optimize DataWarehouse for bare metal deployments with hardware acceleration support.

**Hardware Acceleration:**

| Hardware | Use Case | API | Status |
|----------|----------|-----|--------|
| Intel QAT | Compression, Encryption | DPDK | [ ] |
| NVIDIA GPU | Vector operations, AI | CUDA | [ ] |
| AMD GPU | Vector operations | ROCm | [ ] |
| Intel AES-NI | AES encryption | Intrinsics | [x] Already used |
| Intel AVX-512 | SIMD operations | Intrinsics | [ ] |
| ARM SVE/SVE2 | SIMD on ARM | Intrinsics | [ ] |
| SmartNIC | Offload networking | DPDK | [ ] |
| FPGA | Custom acceleration | OpenCL | [ ] |
| TPM 2.0 | Key storage | TSS2 | [ ] |
| HSM PCIe | Hardware crypto | PKCS#11 | [ ] |

**Kernel Bypass I/O:**

| Technology | Description | Status |
|------------|-------------|--------|
| DPDK | Data Plane Development Kit | [ ] |
| SPDK | Storage Performance Development Kit | [ ] |
| io_uring | Linux async I/O | [ ] |
| RDMA | Remote Direct Memory Access | [ ] |
| NVMe-oF | NVMe over Fabrics | [ ] |
| iSER | iSCSI Extensions for RDMA | [ ] |

**NUMA Optimization:**

| Feature | Description | Status |
|---------|-------------|--------|
| NUMA-Aware Allocation | Memory affinity | [ ] |
| CPU Pinning | Reduce cache misses | [ ] |
| Interrupt Affinity | NIC IRQ distribution | [ ] |
| Memory Tiering | HBM/DRAM/Optane | [ ] |

---

### CATEGORY D: Industry-First AI Features

> **ARCHITECTURAL NOTE (2026-01-28):** AI capabilities have been consolidated into a unified architecture:
> - **AIAgents Plugin** (`Plugins/DataWarehouse.Plugins.AIAgents/`) - Central AI capability hub with:
>   - Two-level control model: Instance-level capabilities (admin) + User-level provider mappings
>   - ChildrenMirrorParent toggle for simple vs fine-grained sub-capability control
>   - Capabilities: Chat, NLP, Semantics, Generation, Embeddings, Vision, Audio, AIOps, Knowledge
>   - 12 provider implementations using SDK's `DataWarehouse.SDK.AI.IAIProvider` interface
> - **AIInterface Plugin** (`Plugins/DataWarehouse.Plugins.AIInterface/`) - Unified channel interface:
>   - Protocol translation only (no AI work), routes to AIAgents via message bus
>   - Supports: Slack, Teams, Discord, Voice (Alexa/Google/Siri), LLM integrations (ChatGPT/Claude/Webhook)
> - **Deleted Legacy Plugins:** AutonomousDataManagement, NaturalLanguageInterface, SemanticUnderstanding

#### Task 47: Autonomous Data Management (AIOps)
**Priority:** P0 (Industry-First)
**Effort:** Very High
**Status:** ✅ **COMPLETED** (2026-01-28) - **REFACTORED** (2026-01-28)

**Description:** Implement fully autonomous data management where AI handles all routine operations without human intervention.

**Implementation:** Consolidated into `Plugins/DataWarehouse.Plugins.AIAgents/Capabilities/AIOps/`
- Capability Handler: `AIOpsCapabilityHandler.cs` (11 sub-capabilities with ChildrenMirrorParent toggle)
- Engines: `TieringEngine.cs`, `ScalingEngine.cs`, `AnomalyEngine.cs`, `CostEngine.cs`, `SecurityEngine.cs`, `ComplianceEngine.cs`, `CapacityEngine.cs`, `PerformanceEngine.cs`
- Models: `PredictionModels.cs` (ML-based predictions with graceful AI degradation)
- Provider Support: Uses SDK `DataWarehouse.SDK.AI.IAIProvider` interface (provider-agnostic)

**Autonomous Capabilities:**

| Capability | Description | Status |
|------------|-------------|--------|
| Self-Optimizing Tiering | ML-driven data placement | [x] |
| Predictive Scaling | Forecast and auto-scale | [x] |
| Anomaly Auto-Remediation | Detect and fix issues | [x] |
| Cost Optimization | Minimize cloud spend | [x] |
| Security Response | Auto-block threats | [x] |
| Compliance Automation | Auto-enforce policies | [x] |
| Capacity Planning | Predict future needs | [x] |
| Performance Tuning | Auto-adjust settings | [x] |

**AI Models Required:**

| Model | Purpose | Training Data | Status |
|-------|---------|---------------|--------|
| Access Pattern Predictor | Forecast file access | Access logs | [x] |
| Failure Predictor | Predict disk failures | SMART data, telemetry | [x] |
| Anomaly Detector | Identify unusual patterns | Normal operation baseline | [x] |
| Cost Optimizer | Minimize storage costs | Pricing, usage patterns | [x] |
| Query Optimizer | Optimize search queries | Query logs | [x] |

---

#### Task 48: Natural Language Data Interaction
**Priority:** P1 (Industry-First)
**Effort:** High
**Status:** ✅ **COMPLETED** (2026-01-28) - **REFACTORED** (2026-01-28)

**Description:** Allow users to interact with their data using natural language, eliminating the need to learn query syntax.

**Implementation:** Split between two consolidated plugins:

**NLP Capabilities:** `Plugins/DataWarehouse.Plugins.AIAgents/Capabilities/NLP/`
- Capability Handler: `NLPCapabilityHandler.cs` (QueryParsing, IntentDetection, CommandExtraction, FollowUpHandling)
- Engines: `QueryParsingEngine.cs` (40+ intent patterns, AI-enhanced), `ConversationContextEngine.cs` (session management, pronoun resolution)
- Provider-agnostic via SDK `DataWarehouse.SDK.AI.IAIProvider` interface

**Channel Interfaces:** `Plugins/DataWarehouse.Plugins.AIInterface/`
- Main plugin: `AIInterfacePlugin.cs` (channel-agnostic routing via message bus)
- Channels: `SlackChannel.cs`, `TeamsChannel.cs`, `DiscordChannel.cs`
- Voice Channels: `AlexaChannel.cs`, `GoogleAssistantChannel.cs`, `SiriChannel.cs`
- LLM Channels: `ChatGPTPluginChannel.cs`, `ClaudeMCPChannel.cs`, `GenericWebhookChannel.cs`
- Registry: `ChannelRegistry.cs` (enable/disable individual channels per instance)

**Capabilities:**

| Capability | Example | Status |
|------------|---------|--------|
| Natural Language Search | "Find all contracts from 2024 mentioning liability" | [x] |
| Conversational Queries | "How much storage am I using?" "Show by region" | [x] |
| Voice Commands | Integration with Alexa, Siri, Google Assistant | [x] |
| Query Explanation | "Explain why this file was tiered to archive" | [x] |
| Data Storytelling | "Summarize my backup history this month" | [x] |
| Anomaly Explanation | "Why did storage usage spike yesterday?" | [x] |

**Integration Points:**

| Integration | Description | Status |
|-------------|-------------|--------|
| Slack Bot | Query data from Slack | [x] |
| Teams Bot | Microsoft Teams integration | [x] |
| Discord Bot | For developer communities | [x] |
| ChatGPT Plugin | OpenAI plugin marketplace (SDK AI-native) | [x] |
| Claude Integration | Anthropic MCP protocol (SDK AI-native) | [x] |
| Generic LLM Adapter | Copilot, Bard, Llama 3, Gemini (SDK AI-native) | [x] |

---

#### Task 49: Semantic Data Understanding
**Priority:** P1
**Effort:** High
**Status:** ✅ **COMPLETED** (2026-01-28) - **REFACTORED** (2026-01-28)

**Description:** Deep semantic understanding of stored data for intelligent organization and retrieval.

**Implementation:** Consolidated into `Plugins/DataWarehouse.Plugins.AIAgents/Capabilities/Semantics/`
- SDK Interfaces: `DataWarehouse.SDK/AI/ISemanticAnalyzer.cs`
- SDK Base Class: `DataWarehouse.SDK/AI/SemanticAnalyzerBase.cs` (2,063 lines, full implementations)
- Capability Handler: `SemanticsCapabilityHandler.cs` (registry-based capability checking with ChildrenMirrorParent support)
- Engines: `CategorizationEngine.cs`, `EntityExtractionEngine.cs`, `RelationshipEngine.cs`, `DuplicateDetectionEngine.cs`, `SummarizationEngine.cs`, `SentimentEngine.cs`, `PIIDetectionEngine.cs`, `LanguageDetectionEngine.cs`
- Provider-agnostic via SDK IAIProvider, IVectorStore, IKnowledgeGraph interfaces
- All operations return `CapabilityResult<T>` for graceful error handling

**Features:**

| Feature | Description | Status |
|---------|-------------|--------|
| Automatic Categorization | AI-classify files by content | [x] |
| Entity Extraction | Identify people, places, dates | [x] |
| Relationship Discovery | Find connections between files | [x] |
| Duplicate Detection | Semantic (not just hash) dedup | [x] |
| Content Summarization | Auto-generate file summaries | [x] |
| Language Detection | Identify document languages | [x] |
| Sentiment Analysis | For communications storage | [x] |
| PII Detection | Find personal data automatically | [x] |

---

### CATEGORY E: Security Leadership

#### Task 52: Military-Grade Security Hardening
**Priority:** P0 (Government/Military Tier)
**Effort:** Very High
**Status:** [ ] Not Started

**Description:** Implement security features required for handling classified data.

**Classifications Supported:**

| Level | Label | Requirements | Status |
|-------|-------|--------------|--------|
| Unclassified | U | Standard security | [x] |
| Controlled Unclassified | CUI | NIST 800-171 | [ ] |
| Confidential | C | DoD security | [ ] |
| Secret | S | Stricter controls | [ ] |
| Top Secret | TS | Air gap capable | [ ] |
| TS/SCI | TS/SCI | Compartmented | [ ] |

**Military Features:**

| Feature | Description | Status |
|---------|-------------|--------|
| Mandatory Access Control | Bell-LaPadula model | [ ] |
| Multi-Level Security | Handle multiple levels | [ ] |
| Cross-Domain Solution | Controlled data transfer | [ ] |
| Tempest Compliance | Electromagnetic security | [ ] |
| Degaussing Support | Secure data destruction | [ ] |
| Two-Person Integrity | Dual authorization | [ ] |
| Need-to-Know Enforcement | Compartmentalized access | [ ] |

---

### CATEGORY F: Hyperscale & Performance

#### Task 53: Exabyte-Scale Architecture
**Priority:** P0 (Hyperscale Tier)
**Effort:** Very High
**Status:** [ ] Not Started

**Description:** Architect the system to handle exabyte-scale deployments with trillions of objects.

**Scale Targets:**

| Metric | Target | Current | Gap |
|--------|--------|---------|-----|
| Objects | 10 trillion | Millions | Major redesign |
| Storage | 100 EB | PB-scale | Architecture change |
| Requests/sec | 100 million | Thousands | 10,000x improvement |
| Latency (p99) | <10ms | ~100ms | 10x improvement |

**Architecture Changes:**

| Component | Current | Exabyte-Scale | Status |
|-----------|---------|---------------|--------|
| Metadata | B-tree | LSM-tree + Bloom | [ ] |
| Sharding | Hash | Consistent hashing + ranges | [ ] |
| Indexing | Single-node | Distributed (100k shards) | [ ] |
| Caching | Local | Distributed (Redis cluster) | [ ] |
| Replication | Sync | Async with tunable consistency | [ ] |

---

#### Task 54: Sub-Millisecond Latency Tier
**Priority:** P1 (Financial Services)
**Effort:** High
**Status:** [ ] Not Started

**Description:** Create an ultra-low-latency storage tier for high-frequency trading and real-time applications.

**Technologies:**

| Technology | Purpose | Status |
|------------|---------|--------|
| Intel Optane | Persistent memory tier | [ ] |
| NVMe-oF | Remote NVMe access | [ ] |
| RDMA | Kernel bypass networking | [ ] |
| DPDK | User-space networking | [ ] |
| io_uring | Async I/O submission | [ ] |
| Huge Pages | TLB optimization | [ ] |

**Performance Targets:**

| Operation | Target Latency | Status |
|-----------|----------------|--------|
| Read (hot) | <100μs | [ ] |
| Write (ack) | <200μs | [ ] |
| Metadata | <50μs | [ ] |
| Search (indexed) | <1ms | [ ] |

---

#### Task 55: Global Multi-Master Replication
**Priority:** P1
**Effort:** Very High
**Status:** [ ] Not Started

**Description:** Enable true multi-master writes across global regions with conflict resolution.

**Features:**

| Feature | Description | Status |
|---------|-------------|--------|
| Write Anywhere | Accept writes in any region | [ ] |
| Conflict Resolution | Automatic + manual options | [ ] |
| Causal Consistency | Respect causality | [ ] |
| Read-Your-Writes | Session consistency | [ ] |
| Bounded Staleness | Configurable lag | [ ] |

**Conflict Resolution Strategies:**

| Strategy | Use Case | Status |
|----------|----------|--------|
| Last-Write-Wins | Simple, fast | [ ] |
| Vector Clocks | Detect conflicts | [ ] |
| CRDTs | Automatic merge | [x] Partial |
| Custom Resolver | Application-specific | [ ] |
| Human Resolution | Manual intervention | [ ] |

---

### CATEGORY G: Integration & Ecosystem

#### Task 56: Universal Data Connector Framework
**Priority:** P1
**Effort:** High
**Status:** [ ] Not Started

**Description:** Create a framework for connecting to any data source or destination.

**Connectors:**

| Category | Connectors | Status |
|----------|------------|--------|
| Databases | PostgreSQL, MySQL, MongoDB, Cassandra, Redis | [ ] |
| Cloud Storage | S3, Azure Blob, GCS, Backblaze B2, Wasabi | [x] Partial |
| SaaS | Salesforce, HubSpot, Zendesk, Jira | [ ] |
| Messaging | Kafka, RabbitMQ, Pulsar, NATS | [ ] |
| Analytics | Snowflake, Databricks, BigQuery | [ ] |
| Enterprise | SAP, Oracle EBS, Microsoft Dynamics | [ ] |
| Legacy | Mainframe, AS/400, Tape libraries | [ ] |

---

#### Task 57: Plugin Marketplace & Certification
**Priority:** P1
**Effort:** Medium
**Status:** [ ] Not Started

**Description:** Create an ecosystem for third-party plugins with certification and revenue sharing.

**Marketplace Features:**

| Feature | Description | Status |
|---------|-------------|--------|
| Plugin Discovery | Search, filter, recommend | [ ] |
| One-Click Install | Automatic deployment | [ ] |
| Version Management | Upgrade, rollback | [ ] |
| Certification Program | Security review, testing | [ ] |
| Revenue Sharing | Monetization for developers | [ ] |
| Rating & Reviews | Community feedback | [ ] |
| Usage Analytics | Telemetry for developers | [ ] |

---

### CATEGORY H: Sustainability & Compliance

#### Task 58: Carbon-Aware Storage (Industry-First)
**Priority:** P2
**Effort:** Medium
**Status:** [ ] Not Started

**Description:** Optimize storage operations based on carbon intensity of power grid.

**Features:**

| Feature | Description | Status |
|---------|-------------|--------|
| Carbon Intensity API | Real-time grid carbon data | [ ] |
| Carbon-Aware Scheduling | Delay non-urgent ops | [ ] |
| Green Region Preference | Route to renewable regions | [ ] |
| Carbon Reporting | Track and report emissions | [ ] |
| Carbon Offsetting | Integration with offset providers | [ ] |

---

#### Task 59: Comprehensive Compliance Automation
**Priority:** P0
**Effort:** Very High
**Status:** [ ] Not Started

**Description:** Automate compliance for all major regulatory frameworks.

**Frameworks:**

| Framework | Region | Industry | Status |
|-----------|--------|----------|--------|
| GDPR | EU | All | [x] Plugin exists |
| HIPAA | US | Healthcare | [x] Plugin exists |
| PCI-DSS | Global | Financial | [ ] |
| SOX | US | Public companies | [ ] |
| FedRAMP | US | Government | [x] Plugin exists |
| CCPA/CPRA | California | All | [ ] |
| LGPD | Brazil | All | [ ] |
| PIPEDA | Canada | All | [ ] |
| PDPA | Singapore | All | [ ] |
| DORA | EU | Financial | [ ] |
| NIS2 | EU | Critical infrastructure | [ ] |
| TISAX | Germany | Automotive | [ ] |
| ISO 27001 | Global | All | [ ] |
| SOC 2 Type II | Global | All | [x] Plugin exists |

---

### Implementation Roadmap

#### Pre-Phase 1: Pending/Defferred task from previous sprint
Tasks carried over from previous sprints that need to be completed before starting GOD TIER features.
| Tasks | Status |
|-------|--------|
|    6  |   [x]  |
|    11 |   [x]  |
|    25 |   [x]  |
|    26 |   [x]  |
|    28 |   [x]  |
|    30 |   [x]  |
|    31 |   [x]  |
|    37 |   [x]  |

## Include UI/UX improvements, bug fixes, performance optimizations, and minor features from previous sprints that are prerequisites for GOD TIER features.
Task A1: Dashboard Plugin - Add support for:
  |#.| Task                               | Status |
  |--|------------------------------------|--------|
  |a.| Grafana Loki                       | [x]    |
  |b.| Prometheus                         | [x]    |
  |c.| Kibana                             | [x]    |
  |d.| SigNoz                             | [x]    |
  |e.| Zabbix                             | [x]    |
  |f.| VictoriaMetrics                    | [x]    |
  |g.| Netdata                            | [x]    |
  |h.| Perses                             | [x]    |
  |i.| Chronograf                         | [x]    |
  |j.| Datadog                            | [x]    |
  |k.| New Relic                          | [x]    |
  |l.| Dynatrace                          | [x]    |
  |m.| Splunk/Splunk Observability Cloud  | [x]    |
  |n.| Logx.io                            | [x]    |
  |o.| LogicMonitor                       | [x]    |
  |p.| Tableau                            | [x]    |
  |q.| Microsoft Power BI                 | [x]    |
  |r.| Apache Superset                    | [x]    |
  |s.| Metabase                           | [x]    |
  |t.| Geckoboard                         | [x]    |
  |u.| Redash                             | [x]    |

  Verify the existing implementation first for already supported dashboard plugins (partial or full),
  then, implement the above missing pieces one by one.

  Task A2: Improve UI/UX ✅ **COMPLETED** (2026-01-26)
  Concept: A modular live media/installer, sinilar to a Linux Live CD/USB distro where users can pick and choose which components to include in their build or run a live version directly from the media without installation.
    1. [x] Create a full fledged Adapter class in 'DataWarehouse/Metadata/' that can be copy/pasted into any other project.
       This Adapter allows the host project to communicate with DataWarehouse, without needing any direct project reference.
       This is one of the ways how our customers will be integrating/using DataWarehouse with their own applications.
       **Implementation:** `Metadata/Adapter/` - IKernelAdapter.cs, AdapterFactory.cs, AdapterRunner.cs, README.md

    2. [x] Create a base piece that supports 3 modes:
         i. Install and initialize an instance of DataWarehouse
        ii. Connect to an existing DataWarehouse instance - allows configuration of remote instances without having to login to the remote server/machine.
            It can also be used to configure the local or standalone instances also).
       iii. Runs a tiny embedded version of DataWarehouse for local/single-user
       This base piece will be use an excat copy of the Adapter we created in step 1. to become able to perform i ~ iii.
       Thus, this will not only allow us to demo DataWarehouse in a standalone fashion, but also allow customers to embed DataWarehouse
       into their own applications with minimal effort, using a copy of the Adapter (using this base piece as an example, as it already uses the Adapter).
       And for users who want to just run an instance with minimal hassle, they can also use this base piece as it is (maybe run it as a service, autostart at login...),
       without needing any separate 'project' just to run an instance of DataWarehouse.
       The 'ii' mode will also allow users to connect to a remote (or really, any) instances easily, without needing to login to the remote server/machine.
       This mode will provide and support Configuraion management for the connected instances, including configuring the tiny embedded instance present
       in 'iii' mode. They can then save this configuration for 'future use'/'configuration change' for that instance. (In case of the tiny embedded version,
       the configuration can be saved to the live media itself, so that next time the user boots from the live media, their configuration is already present).
       And when they proceed to 'Install' from 'i' mode, the configuration used in 'ii' mode can be used as the default configuration for the installed instance also
       if the user wnats it that way.
       **Implementation:** `Metadata/Adapter/` - DataWarehouseHost.cs, OperatingMode.cs, InstanceConnection.cs

    3. [x] Upgrade the CLI to use the base piece, and support all 3 modes above.
       This will allow users to use the CLI to manage both local embedded instances, as well as any local and remote instances.
       **Implementation:** CLI/Commands/ - InstallCommand.cs, ConnectCommand.cs, EmbeddedCommand.cs + CLI/Integration/

    4. [x] Rename the Launcher project to a more appropriate name.
       Upgrade it to use the base piece, and support all 3 modes above.
       This will allow users to use the GUI to manage both local embedded instances, as well as any local and remote instances.
       **Implementation:** Launcher/Integration/ + updated Adapters

    5. [x] Can we make both the CLI and GUI support the 'condition' of the instance of DataWarehouse they are connected to (local embedded, remote instance...).
       For example if running a local SDK & Kernel standalone instance (without any plugins), both will allow commands and messages that can call the
       in-memory storage engine and the basic features provided by the standalone Kernel. But as the user adds more plugins to the instance, both CLI and GUI
       will automatically gain access to call and use those new features too... This way, both CLI and GUI will be 'aware' of the capabilities of the instance
       they are connected to, but if the user switches to another instance (remote or local) with different capabilities, both CLI and GUI will automatically adjust.
       Even if the user tries to use a feature not supported by the connected instance, both CLI and GUI will just inform the user that the feature is not available
       in the current instance, without error or crash.
       **Implementation:** DataWarehouse.Shared/ - CapabilityManager.cs, CommandRegistry.cs

    6. [x] Can we make both CLI and GUI share as much code as possible, especially the business logic layer? The idea is that the CLI and GUI will have the same
      features and capabilities, and sharing code will ensure consistency between both interfaces, and we can avoid duplication of effort in implementing features
      in both places.
      **Implementation:** DataWarehouse.Shared/ - InstanceManager.cs, MessageBridge.cs, Models/

Implementing this 'Live Media/Installer' concept will greatly enhance the usability and flexibility of DataWarehouse, making it easier for users to get started, 
by both being able to run a local instance easily for testing purposes without having to modify their system by installing anything, as well as allow user 
to remotly configure and manage any instance of DataWarehouse, embed DataWarehouse into their own applications with minimal effort etc., offering a great deal of versatility.

Task A3: Implement support for full SQL toolset compatibility ✅ **COMPLETED** (2026-01-26)
  i. [x] Implement Database Import plugin (import SQL/Server/MySQL/PostgreSQL/Oracle/SQLite/NoSQL etc. databses and tables into DataWarehouse)
       **Implementation:** Plugins/DataWarehouse.Plugins.DatabaseImport/
 ii. [x] Implement Federated Query plugin (allow querying accross heterogenious data, databases and tables from DataWarehouse in one go)
       **Implementation:** Plugins/DataWarehouse.Plugins.FederatedQuery/
iii. [x] Implement Schema Registry (Track imported database and table structures, versions and changes over time)
       **Implementation:** Plugins/DataWarehouse.Plugins.SchemaRegistry/
 iv. [x] Implement PostgreSQL Wire Protocol (allows SQL tools supporting PostgreSQL protocol to connect)
       **Implementation:** Plugins/DataWarehouse.Plugins.PostgresWireProtocol/
  v. [x] Implement SQL Toolset Compatibility - All major protocols implemented:
       **Protocols:** TDS (SQL Server), MySQL, Oracle TNS, PostgreSQL Wire, ODBC, JDBC, ADO.NET, NoSQL (MongoDB/Redis)
       **Implementation:** Plugins/DataWarehouse.Plugins.{TdsProtocol,MySqlProtocol,OracleTnsProtocol,OdbcDriver,JdbcBridge,AdoNetProvider,NoSqlProtocol}/
 vi. SQL Tools Compatibility Matrix (all protocols now supported):
|#  |Tool                                                                           | Status | Notes                          |
|---|-------------------------------------------------------------------------------|--------|--------------------------------|
|1. |SSMS (TDS protocol)                                                            | [x]    | Via TDS protocol               |
|2. |Azure Data Studio                                                              | [x]    | Via TDS protocol               |
|3. |DBeaver                                                                        | [x]    | Via PostgreSQL driver          |
|4. |HeidiSQL                                                                       | [x]    | Via MySQL protocol             |
|5. |DataGrip                                                                       | [x]    | Via PostgreSQL driver          |
|6. |Squirrel SQL                                                                   | [x]    | Via JDBC PostgreSQL            |
|7. |Navicat                                                                        | [x]    | Via PostgreSQL driver          |
|8. |TablePlus                                                                      | [x]    | Via PostgreSQL driver          |
|9. |Valentina Studio                                                               | [x]    | Via PostgreSQL driver          |
|10.|Beekeeper Studio                                                               | [x]    | Via PostgreSQL driver          |
|11.|OmniDB                                                                         | [x]    | Via PostgreSQL driver          |
|12.|DbVisualizer                                                                   | [x]    | Via JDBC PostgreSQL            |
|13.|SQL Workbench/J                                                                | [x]    | Via JDBC PostgreSQL            |
|14.|Aqua Data Studio                                                               | [x]    | Via PostgreSQL driver          |
|15.|RazorSQL                                                                       | [x]    | Via PostgreSQL driver          |
|16.|DbSchema                                                                       | [x]    | Via PostgreSQL driver          |
|17.|FlySpeed SQL Query                                                             | [x]    | Via ODBC/JDBC PostgreSQL       |
|18.|MySQL Workbench (for MySQL compatibility mode)                                 | [x]    | Via MySQL protocol             |
|19.|Postico (for PostgreSQL compatibility mode)                                    | [x]    | Native PostgreSQL              |
|20.|PostgreSQL Wire Protocol                                                       | [x]    | Core protocol implemented      |
|21.|ODBC/JDBC/ADO.NET/ADO drivers for various programming languages and frameworks | [x]    | Via PostgreSQL drivers         |
|22.|SQL Alchemy                                                                    | [x]    | Via psycopg2/asyncpg           |
|23.|Entity Framework                                                               | [x]    | Via Npgsql                     |
|24.|Hibernate                                                                      | [x]    | Via JDBC PostgreSQL            |
|25.|Django ORM                                                                     | [x]    | Via psycopg2                   |
|26.|Sequelize                                                                      | [x]    | Via pg (node-postgres)         |
|27.|Knex.js/Node.js/Python libraries                                               | [x]    | Via pg/psycopg2                |
|28.|LINQ to SQL                                                                    | [x]    | Via Npgsql                     |
|29.|PHP PDO                                                                        | [x]    | Via pdo_pgsql                  |
|30.|Power BI                                                                       | [x]    | Via PostgreSQL connector       |
|31.|pgAdmin (for PostgreSQL compatibility mode)                                    | [x]    | Native PostgreSQL              |
|32.|Adminer                                                                        | [x]    | Via PostgreSQL driver          |
|33.|SQLyog (for MySQL compatibility mode)                                          | [x]    | Via MySQL protocol             |
|34.|SQL Maestro                                                                    | [x]    | PostgreSQL mode supported      |
|35.|Toad for SQL Server/MySQL/PostgreSQL                                           | [x]    | PostgreSQL mode supported      |
|36.|SQL Developer (for Oracle compatibility mode)                                  | [x]    | Via Oracle TNS protocol        |
|37.|PL/SQL Developer (for Oracle compatibility mode)                               | [x]    | Via Oracle TNS protocol        |
|38.|SQL*Plus (for Oracle compatibility mode)                                       | [x]    | Via Oracle TNS protocol        |
|39.|SQLite tools (for SQLite compatibility mode)                                   | [ ]    | Different architecture         |
|40.|NoSQL tools (for NoSQL compatibility modes)                                    | [x]    | Via MongoDB/Redis protocols    |
|41.|REST API/GraphQL clients (for REST/GraphQL compatibility modes)                | [x]    | Via existing REST/GraphQL APIs |

* Many of these tools can connect via standard protocols (TDS, PostgreSQL Wire Protocol, MySQL protocol etc.).
  Instead of implementing each tool individually, we can focus on supporting the underlying protocols and standards,
  so implementing support for those protocols in DataWarehouse will enable compatibility with multiple tools at once.

---

#### Phase 3: AI & Intelligence (Q3-Q4 2026) ✅ **COMPLETED** (2026-01-28)
| Task | Description | Priority | Status |
|------|-------------|----------|--------|
| 47 | Autonomous Data Management | P0 | ✅ Complete |
| 48 | Natural Language Interface | P1 | ✅ Complete |
| 49 | Semantic Understanding | P1 | ✅ Complete |
| 39 | AI-Powered CLI | P1 | ✅ Complete |

**Task 39: AI-Powered CLI**
- **Implementation:** `DataWarehouse.Shared/Services/`
- `NaturalLanguageProcessor.cs` (980 lines) - Pattern + AI fallback command parsing
- `ConversationContext.cs` (420 lines) - Multi-turn conversation support
- `CLILearningStore.cs` (613 lines) - Learning from user corrections
- Provider-agnostic via SDK IAIProvider interface

#### Phase 4: Scale & Performance (Q4 2026 - Q1 2027)
| Task | Description | Priority |
|------|-------------|----------|
| 53 | Exabyte-Scale Architecture | P0 |
| 54 | Sub-Millisecond Latency | P1 |
| 55 | Global Multi-Master | P1 |
| 46 | Bare Metal Optimization | P1 |

#### Phase 5: Ecosystem (Q1-Q2 2027)
| Task | Description | Priority |
|------|-------------|----------|
| 45 | Hypervisor Support | P1 |
| 52 | Military Security | P0 |
| 56 | Universal Connectors | P1 |
| 57 | Plugin Marketplace | P1 |
| 58 | Carbon-Aware Storage | P2 |
| 59 | Compliance Automation | P0 |

---

### Success Metrics

| Metric | Target | Timeline |
|--------|--------|----------|
| GUI Active Users | 100,000 | Q4 2026 |
| Kubernetes Deployments | 10,000 | Q3 2026 |
| Filesystem Driver Downloads | 50,000 | Q3 2026 |
| Plugin Marketplace Listings | 500 | Q2 2027 |
| Enterprise Customers | 1,000 | Q4 2026 |
| Government Certifications | 5 (FedRAMP, CC, etc.) | Q4 2026 |
| Hyperscale Deployments | 10 | Q1 2027 |

---

### Resource Requirements

| Team | Focus | FTEs |
|------|-------|------|
| GUI/Desktop | Tasks 38, 40, 41, 42 | 6 |
| Containers/K8s | Tasks 43, 44, 45 | 4 |
| Security | Tasks 50, 51, 52 | 5 |
| AI/ML | Tasks 47, 48, 49 | 4 |
| Performance | Tasks 53, 54, 55, 46 | 5 |
| Ecosystem | Tasks 56, 57, 58, 59 | 4 |
| CLI | Task 39 | 2 |

**Total: 30 FTEs**

## Verification Checklist

After implementing fixes:

| Check | Status |
|-------|--------|
| All unit tests pass | [ ] |
| Database plugins tested against real databases | [ ] |
| RAID rebuild tested with actual data | [ ] |
| Geo-replication syncs data across regions | [ ] |
| All 10 RAID plugins use SharedRaidUtilities | [ ] |
| RAID parity calculations verified with test vectors | [ ] |

---

### Verification Protocol

After each task completion:

1. **Build Verification:** `dotnet build DataWarehouse.slnx`
2. **Runtime Test:** Verify no `NotImplementedException` or empty catches in changed code
3. **No Placeholders:** Grep for TODO, FIXME, HACK, SIMULATION, PLACEHOLDER
4. **Production Ready:** No mocks, simulations, hardcoded values, or shortcuts
5. **Update TODO.md:** Mark task complete only after verification passes
6. **Commit:** Create atomic commit with descriptive message

---

## Tamper-Proof Storage Provider Implementation Plan

### Overview

A military/government-grade tamper-proof storage system providing cryptographic integrity verification, blockchain-based audit trails, and WORM (Write-Once-Read-Many) disaster recovery capabilities. The system follows a Three-Pillar Architecture: **Live Data** (fast access) + **Blockchain Anchor** (immutable truth) + **WORM Vault** (disaster recovery).

**Design Philosophy:**
- Object-based storage (all retrievals via GUIDs, not sectors/blocks)
- User freedom to choose storage instances (not locked to specific providers)
- Configurable security levels from "I Don't Care" to "Ultra Paranoid"
- Append-only corrections (original data preserved, new version supersedes)
- Mandatory write comments (like git commit messages) for full audit trail
- Tamper attribution (detect WHO tampered when possible)

---

### Architecture: Three Pillars

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                         TAMPER-PROOF STORAGE SYSTEM                          │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│  ┌──────────────────┐  ┌──────────────────┐  ┌──────────────────┐          │
│  │   PILLAR 1:      │  │   PILLAR 2:      │  │   PILLAR 3:      │          │
│  │   LIVE DATA      │  │   BLOCKCHAIN     │  │   WORM VAULT     │          │
│  │                  │  │   ANCHOR         │  │                  │          │
│  │  • Fast access   │  │  • Immutable     │  │  • Disaster      │          │
│  │  • Hot storage   │  │    truth         │  │    recovery      │          │
│  │  • RAID shards   │  │  • Hash chains   │  │  • Legal hold    │          │
│  │  • Metadata      │  │  • Timestamps    │  │  • Compliance    │          │
│  └──────────────────┘  └──────────────────┘  └──────────────────┘          │
│                                                                              │
│  Storage Instances (User-Configurable):                                      │
│  ┌─────────────────────────────────────────────────────────────────────┐   │
│  │ Instance ID        │ Purpose         │ Plugin Type (User Choice)     │   │
│  │────────────────────│─────────────────│──────────────────────────────│   │
│  │ "data"             │ Live data       │ S3Storage, LocalStorage, etc. │   │
│  │ "metadata"         │ Manifests       │ Same or different provider    │   │
│  │ "worm"             │ WORM vault      │ Same or different provider    │   │
│  │ "blockchain"       │ Anchor records  │ Same or different provider    │   │
│  └─────────────────────────────────────────────────────────────────────┘   │
│                                                                              │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

### Five-Phase Write Pipeline

```
USER DATA
    │
    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│ PHASE 1: User-Configurable Transformations (Order Configurable)             │
│ ┌─────────────┐  ┌─────────────┐  ┌─────────────┐                          │
│ │ Compression │─▶│ Encryption  │─▶│ Content Pad │  ◄─ Hides true data size │
│ │ (optional)  │  │ (optional)  │  │ (optional)  │     Covered by hash      │
│ └─────────────┘  └─────────────┘  └─────────────┘                          │
│ Records: Which transformations applied + order → stored in manifest         │
└─────────────────────────────────────────────────────────────────────────────┘
    │
    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│ PHASE 2: System Integrity Hash (Fixed Position - ALWAYS After Phase 1)      │
│ ┌─────────────────────────────────────────────────────────────────────┐     │
│ │ SHA-256/SHA-384/SHA-512/Blake3 hash of transformed data             │     │
│ │ This hash covers: Original data + Phase 1 transformations           │     │
│ │ This hash does NOT cover: Phase 3 shard padding (by design)         │     │
│ └─────────────────────────────────────────────────────────────────────┘     │
└─────────────────────────────────────────────────────────────────────────────┘
    │
    ▼
┌────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│ PHASE 3: RAID Distribution + Shard Padding (Fixed Position)                                            │
│ ┌─────────────────────────────────────────────────────────────────────────────────────────────────┐    │
│ │ 1. Split into N data shards                                                                     │    │
│ │ 2. Pad final shard to uniform size (optional/user configurable, NOT covered by Phase 2 hash)    │    │
│ │ 3. Generate M parity shards                                                                     │    │
│ │ 4. Each shard gets its own shard-level hash                                                     │    │
│ │ 5. Save the optional Shard Padding details also for reversal during read.                       │    │
│ └─────────────────────────────────────────────────────────────────────────────────────────────────┘    │
└────────────────────────────────────────────────────────────────────────────────────────────────────────┘
    │
    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│ PHASE 4: Parallel Storage Writes (Transactional Writes)                     │
│ ┌─────────────────────────────────────────────────────────────────────┐     │
│ │ PARALLEL WRITES TO ALL CONFIGURED TIERS:                            │     │
│ │   • Data Instance → RAID shards                                     │     │
│ │   • Metadata Instance → Manifest + access log entry                 │     │
│ │   • WORM Instance → Full transformed blob + manifest                │     │
│ │   • Blockchain Instance → Batched (see Phase 5)                     │     │
│ │ * Write to all 4 configured tiers in a single transaction.          │     │
│ │ * Write is only considered a success if the whole transaction       │     │
│ │   completes successfully.                                           │     │
│ │ * Allow ROLLBACK on failure. WORM might not allow rollback.         │     │
│ │   So maybe we can leave that data as orphaned... Or use some other  │     │
│ │   proper stratergy. Design accordingly.                             │     │
│ └─────────────────────────────────────────────────────────────────────┘     │
└─────────────────────────────────────────────────────────────────────────────┘
    │
    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│ PHASE 5: Blockchain Anchoring (Batched for Efficiency)                      │
│ ┌─────────────────────────────────────────────────────────────────────┐     │
│ │ Anchor record contains:                                             │     │
│ │   • Object GUID                                                     │     │
│ │   • Phase 2 integrity hash                                          │     │
│ │   • UTC timestamp                                                   │     │
│ │   • Write context (author, comment, session)                        │     │
│ │   • Previous block hash (chain linkage)                             │     │
│ │   • Merkle root (when batching multiple objects)                    │     │
│ └─────────────────────────────────────────────────────────────────────┘     │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

### Five-Phase Read Pipeline

```
READ REQUEST (ObjectGuid, ReadMode)
    │
    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│ PHASE 1: Manifest Retrieval                                                 │
│ ┌─────────────────────────────────────────────────────────────────────┐     │
│ │ Load TamperProofManifest from Metadata Instance                     │     │
│ │ Contains: Expected hash, transformation order, shard map, WORM ref  │     │
│ └─────────────────────────────────────────────────────────────────────┘     │
└─────────────────────────────────────────────────────────────────────────────┘
    │
    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│ PHASE 2: Shard Retrieval + Reconstruction                                   │
│ ┌─────────────────────────────────────────────────────────────────────┐     │
│ │ 1. Load required shards from Data Instance                          │     │
│ │ 2. Verify individual shard hashes                                   │     │
│ │ 3. Reconstruct original transformed blob (Reed-Solomon if needed)   │     │
│ │ 4. Strip shard padding                                              │     │
│ └─────────────────────────────────────────────────────────────────────┘     │
└─────────────────────────────────────────────────────────────────────────────┘
    │
    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│ PHASE 3: Integrity Verification (Conditional on ReadMode)                   │
│ ┌─────────────────────────────────────────────────────────────────────┐     │
│ │ ReadMode.Fast: SKIP (trust shard hashes)                            │     │
│ │ ReadMode.Verified: Compute hash, compare to manifest                │     │
│ │ ReadMode.Audit: + Verify blockchain anchor + Log access             │     │
│ └─────────────────────────────────────────────────────────────────────┘     │
└─────────────────────────────────────────────────────────────────────────────┘
    │
    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│ PHASE 4: Reverse Transformations                                            │
│ ┌─────────────────────────────────────────────────────────────────────┐     │
│ │ Apply inverse of Phase 1 in reverse order:                          │     │
│ │   Strip Content Padding → Decrypt → Decompress                      │     │
│ └─────────────────────────────────────────────────────────────────────┘     │
└─────────────────────────────────────────────────────────────────────────────┘
    │
    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│ PHASE 5: Tamper Response (If Verification Failed)                           │
│ ┌─────────────────────────────────────────────────────────────────────┐     │
│ │ Based on TamperRecoveryBehavior:                                    │     │
│ │   • AutoRecoverSilent: Recover from WORM, log internally            │     │
│ │   • AutoRecoverWithReport: Recover + generate incident report       │     │
│ │   • AlertAndWait: Notify admin, don't serve until resolved          │     │
│ │ + Tamper Attribution: Analyze access logs to identify WHO           │     │
│ └─────────────────────────────────────────────────────────────────────┘     │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

> **Note:** Sample code for T1/T2 (interfaces, enums, classes, base classes) has been removed.
> Implementations are in:
> - **SDK:** `DataWarehouse.SDK/Contracts/TamperProof/` (11 files)
> - **Plugins:** See T1 and T2 implementation summaries below

---

### Implementation Phases
# ** Consider the correct location for implementation. Common functions, enums etc. go in SDK. 
# ** Abstract classes implement common features and lifecycle in SDK.
# ** Only specific implementations go in the plugins.

#### Phase T1: Core Infrastructure (Priority: CRITICAL) - **COMPLETED** (2026-01-29)
| Task | Description | Dependencies | Status |
|------|-------------|--------------|--------|
| T1.1 | Create `IIntegrityProvider` interface and `IntegrityProviderPluginBase` | None | [x] |
| T1.2 | Implement `DefaultIntegrityProvider` with SHA256/SHA384/SHA512/Blake3 | T1.1 | [x] |
| T1.3 | Create `IBlockchainProvider` interface and `BlockchainProviderPluginBase` | None | [x] |
| T1.4 | Implement `LocalBlockchainProvider` (file-based chain for single-node) | T1.3 | [x] |
| T1.5 | Create `IWormStorageProvider` interface and `WormStorageProviderPluginBase` | None | [x] |
| T1.6 | Implement `SoftwareWormProvider` (software-enforced immutability) | T1.5 | [x] |
| T1.7 | Create `IAccessLogProvider` interface and `AccessLogProviderPluginBase` | None | [x] |
| T1.8 | Implement `DefaultAccessLogProvider` (persistent access logging) | T1.7 | [x] |
| T1.9 | Create all configuration classes | None | [x] |
| T1.10 | Create all enum definitions | None | [x] |
| T1.11 | Create all manifest and record structures | None | [x] |
| T1.12 | Create `WriteContext` and `CorrectionContext` classes | None | [x] |

**T1 Implementation Summary:**
- SDK Contracts: `DataWarehouse.SDK/Contracts/TamperProof/` (11 files)
- Plugins: `Plugins/DataWarehouse.Plugins.Integrity/`, `Plugins/DataWarehouse.Plugins.Blockchain.Local/`, `Plugins/DataWarehouse.Plugins.Worm.Software/`, `Plugins/DataWarehouse.Plugins.AccessLog/`
- All builds pass with 0 errors

#### Phase T2: Core Plugin (Priority: HIGH) ✅ COMPLETE
| Task | Description | Dependencies | Status |
|------|-------------|--------------|--------|
| T2.1 | Create `ITamperProofProvider` interface | T1.* | [x] |
| T2.2 | Create `TamperProofProviderPluginBase` base class | T2.1 | [x] |
| T2.3 | Implement Phase 1 write pipeline (user transformations) | T2.2 | [x] |
| T2.4 | Implement Phase 2 write pipeline (integrity hash) | T2.3, T1.2 | [x] |
| T2.5 | Implement Phase 3 write pipeline (RAID + shard padding) | T2.4 | [x] |
| T2.6 | Implement Phase 4 write pipeline (parallel storage writes) | T2.5 | [x] |
| T2.7 | Implement Phase 5 write pipeline (blockchain anchoring) | T2.6, T1.4 | [x] |
| T2.8 | Implement `SecureWriteAsync` combining all phases | T2.7 | [x] |
| T2.9 | Implement mandatory write comment validation | T2.8, T1.12 | [x] |
| T2.10 | Implement access logging on all operations | T2.8, T1.8 | [x] |

**T2 Implementation Summary:**
- SDK: `DataWarehouse.SDK/Contracts/TamperProof/ITamperProofProvider.cs` (interface + base class, 692 lines)
- Plugin: `Plugins/DataWarehouse.Plugins.TamperProof/`
  - `TamperProofPlugin.cs` - Main plugin with 5-phase write/read pipelines
  - `Pipeline/WritePhaseHandlers.cs` - Write phases 1-5 with transactional rollback
  - `Pipeline/ReadPhaseHandlers.cs` - Read phases 1-5 with WORM recovery
- All builds pass with 0 errors

#### Phase T3: Read Pipeline & Verification (Priority: HIGH)

> **DEPENDENCY:** T3.4.2 "Decrypt (if encrypted)" requires T5.0 (SDK Base Classes) to be completed first.
> - T5.0.3 provides `EncryptionPluginBase` with unified decryption infrastructure
> - T5.0.1.4 provides `EncryptionMetadata` record for parsing manifest/header
> - T5.0.1.9 provides `EncryptionConfigMode` enum for config resolution

| Task | Description | Dependencies | Status |
|------|-------------|--------------|--------|
| T3.1 | Implement Phase 1 read (manifest retrieval) | T2.* | [ ] |
| T3.2 | Implement Phase 2 read (shard retrieval + reconstruction) | T3.1 | [ ] |
| T3.2.1 | ↳ Load required shards from Data Instance | T3.2 | [ ] |
| T3.2.2 | ↳ Verify individual shard hashes | T3.2 | [ ] |
| T3.2.3 | ↳ Reconstruct original blob (Reed-Solomon if needed) | T3.2 | [ ] |
| T3.2.4 | ↳ Strip shard padding (if applied) | T3.2 | [ ] |
| T3.3 | Implement Phase 3 read (integrity verification by ReadMode) | T3.2 | [ ] |
| T3.3.1 | ↳ Implement `ReadMode.Fast` (skip verification, trust shard hashes) | T3.3 | [ ] |
| T3.3.2 | ↳ Implement `ReadMode.Verified` (compute hash, compare to manifest) | T3.3 | [ ] |
| T3.3.3 | ↳ Implement `ReadMode.Audit` (full chain + blockchain + access log) | T3.3 | [ ] |
| T3.4 | Implement Phase 4 read (reverse transformations) | T3.3 | [ ] |
| T3.4.1 | ↳ Strip content padding (if applied) | T3.4 | [ ] |
| T3.4.2 | ↳ Decrypt (if encrypted) | T3.4, **T5.0** | [ ] |
| T3.4.3 | ↳ Decompress (if compressed) | T3.4 | [ ] |
| T3.5 | Implement Phase 5 read (tamper response) | T3.4 | [ ] |
| T3.6 | Implement `SecureReadAsync` with all ReadModes | T3.5 | [ ] |
| T3.7 | Implement tamper detection and incident creation | T3.6 | [ ] |
| T3.8 | Implement tamper attribution analysis | T3.7, T1.8 | [ ] |
| T3.8.1 | ↳ Correlate access logs with tampering window | T3.8 | [ ] |
| T3.8.2 | ↳ Identify suspect principals (single/multiple/none) | T3.8 | [ ] |
| T3.8.3 | ↳ Detect access log tampering (sophisticated attack) | T3.8 | [ ] |
| T3.9 | Implement `GetTamperIncidentAsync` with attribution | T3.8 | [ ] |

**Read Modes:**
| Mode | Verification Level | Use Case |
|------|-------------------|----------|
| `Fast` | Skip verification (trust shard hashes only) | Bulk reads, internal replication, performance-critical |
| `Verified` | Compute hash, compare to manifest | Default mode, balance of security and performance |
| `Audit` | Full chain verification + blockchain anchor + access logging | Compliance audits, legal discovery, incident investigation |

#### Phase T4: Recovery & Advanced Features (Priority: MEDIUM)

**Recovery Behaviors (User-Configurable):**
| Task | Description | Dependencies | Status |
|------|-------------|--------------|--------|
| T4.1 | Implement `TamperRecoveryBehavior` enum and configuration | T3.* | [ ] |
| T4.1.1 | ↳ `AutoRecoverSilent`: Recover from WORM, log internally only | T4.1 | [ ] |
| T4.1.2 | ↳ `AutoRecoverWithReport`: Recover + generate incident report | T4.1 | [ ] |
| T4.1.3 | ↳ `AlertAndWait`: Notify admin, block reads until resolved | T4.1 | [ ] |
| T4.1.4 | ↳ `ManualOnly`: Never auto-recover, require explicit admin action | T4.1 | [ ] |
| T4.1.5 | ↳ `FailClosed`: Reject all operations on tamper detection | T4.1 | [ ] |
| T4.2 | Implement `RecoverFromWormAsync` manual recovery | T4.1 | [ ] |

**Append-Only Corrections:**
| Task | Description | Dependencies | Status |
|------|-------------|--------------|--------|
| T4.3 | Implement `SecureCorrectAsync` (append-only corrections) | T4.2 | [ ] |
| T4.3.1 | ↳ Create new version, never delete original | T4.3 | [ ] |
| T4.3.2 | ↳ Link new version to superseded version in manifest | T4.3 | [ ] |
| T4.3.3 | ↳ Anchor correction in blockchain with supersedes reference | T4.3 | [ ] |
| T4.4 | Implement `AuditAsync` with full chain verification | T4.3 | [ ] |

**Seal Mechanism & Instance State:**
| Task | Description | Dependencies | Status |
|------|-------------|--------------|--------|
| T4.5 | Implement seal mechanism (lock structural config after first write) | T4.4 | [ ] |
| T4.5.1 | ↳ Lock: Storage instances, RAID config, hash algorithm, blockchain mode | T4.5 | [ ] |
| T4.5.2 | ↳ Allow: Recovery behavior, read mode, logging, alerts | T4.5 | [ ] |
| T4.5.3 | ↳ Persist seal state and validate on startup | T4.5 | [ ] |
| T4.6 | Implement instance degradation state machine | T4.5 | [ ] |
| T4.6.1 | ↳ State transitions and event notifications | T4.6 | [ ] |
| T4.6.2 | ↳ Automatic state detection based on provider health | T4.6 | [ ] |
| T4.6.3 | ↳ Admin override for manual state changes | T4.6 | [ ] |

**Blockchain Consensus Modes:**
| Task | Description | Dependencies | Status |
|------|-------------|--------------|--------|
| T4.7 | Implement `BlockchainMode` enum and mode selection | T4.6 | [ ] |
| T4.7.1 | ↳ `SingleWriter`: Local file-based chain, single instance | T4.7 | [ ] |
| T4.7.2 | ↳ `RaftConsensus`: Multi-node consensus, majority required | T4.7 | [ ] |
| T4.7.3 | ↳ `ExternalAnchor`: Periodic anchoring to public blockchain | T4.7 | [ ] |
| T4.8 | Implement blockchain batching (N objects or T seconds) | T4.7 | [ ] |
| T4.8.1 | ↳ Merkle root calculation for batched anchors | T4.8 | [ ] |

**WORM Provider Wrapping (Any Storage Provider):**
| Task | Description | Dependencies | Status |
|------|-------------|--------------|--------|
| T4.9 | Implement `IWormWrapper` interface for wrapping any `IStorageProvider` | T4.8 | [ ] |
| T4.9.1 | ↳ Software immutability wrapper (admin bypass with logging) | T4.9 | [ ] |
| T4.9.2 | ↳ Hardware integration detection (S3 Object Lock, Azure Immutable) | T4.9 | [ ] |
| T4.10 | Implement `S3ObjectLockWormPlugin` (AWS S3 Object Lock) | T4.9 | [ ] |
| T4.11 | Implement `AzureImmutableBlobWormPlugin` (Azure Immutable Blob) | T4.10 | [ ] |

**Padding Configuration (User-Configurable):**
| Task | Description | Dependencies | Status |
|------|-------------|--------------|--------|
| T4.12 | Implement `ContentPaddingMode` configuration | T4.11 | [ ] |
| T4.12.1 | ↳ `None`: No padding | T4.12 | [ ] |
| T4.12.2 | ↳ `SecureRandom`: Cryptographically secure random bytes | T4.12 | [ ] |
| T4.12.3 | ↳ `Chaff`: Plausible-looking dummy data | T4.12 | [ ] |
| T4.12.4 | ↳ `FixedSize`: Pad to fixed block size (e.g., 4KB, 64KB) | T4.12 | [ ] |
| T4.13 | Implement `ShardPaddingMode` configuration | T4.12 | [ ] |
| T4.13.1 | ↳ `None`: Variable shard sizes | T4.13 | [ ] |
| T4.13.2 | ↳ `UniformSize`: Pad to largest shard size | T4.13 | [ ] |
| T4.13.3 | ↳ `FixedBlock`: Pad to configured block boundary | T4.13 | [ ] |

**Transactional Writes with Atomicity:**
| Task | Description | Dependencies | Status |
|------|-------------|--------------|--------|
| T4.14 | Implement `TransactionalWriteManager` with atomicity guarantee | T4.13 | [ ] |
| T4.14.1 | ↳ Write ordering: Data → Metadata → WORM → Blockchain queue | T4.14 | [ ] |
| T4.14.2 | ↳ Implement `TransactionFailureBehavior` enum | T4.14 | [ ] |
| T4.14.3 | ↳ Implement `TransactionFailurePhase` enum | T4.14 | [ ] |
| T4.14.4 | ↳ Rollback on failure: Data, Metadata (WORM orphaned) | T4.14 | [ ] |
| T4.14.5 | ↳ Implement `OrphanedWormRecord` structure | T4.14 | [ ] |
| T4.14.6 | ↳ Implement `OrphanStatus` enum | T4.14 | [ ] |
| T4.14.7 | ↳ WORM orphan tracking registry | T4.14 | [ ] |
| T4.14.8 | ↳ Background orphan cleanup job (compliance-aware) | T4.14 | [ ] |
| T4.14.9 | ↳ Orphan recovery mechanism (link to retry) | T4.14 | [ ] |
| T4.14.10 | ↳ Transaction timeout and retry configuration | T4.14 | [ ] |

**TransactionFailureBehavior Enum:**
| Value | Description |
|-------|-------------|
| `RollbackAndOrphan` | Rollback Data+Metadata, mark WORM as orphan (default) |
| `RollbackAndRetry` | Rollback, then retry N times before orphaning |
| `FailFast` | Rollback immediately, no retry, throw exception |
| `OrphanAndContinue` | Mark WORM as orphan, return partial success status |
| `BlockUntilResolved` | Hold transaction, alert admin, wait for manual resolution |

**TransactionFailurePhase Enum:**
| Value | Description |
|-------|-------------|
| `DataWrite` | Failed writing shards to data instance |
| `MetadataWrite` | Failed writing manifest to metadata instance |
| `WormWrite` | Failed writing to WORM (rare - usually succeeds first) |
| `BlockchainQueue` | Failed queuing blockchain anchor |

**OrphanedWormRecord Structure:**
```csharp
public record OrphanedWormRecord
{
    public Guid OrphanId { get; init; }            // Unique orphan identifier
    public Guid OriginalObjectId { get; init; }    // Intended object GUID
    public string WormInstanceId { get; init; }    // Which WORM instance
    public string WormPath { get; init; }          // Path in WORM storage
    public DateTimeOffset CreatedAt { get; init; } // When orphan was created
    public string FailureReason { get; init; }     // Why transaction failed
    public TransactionFailurePhase FailedPhase { get; init; }  // Which phase failed
    public string FailedInstanceId { get; init; }  // Which storage instance failed
    public byte[] ContentHash { get; init; }       // Hash of orphaned content
    public long ContentSize { get; init; }         // Size of orphaned content
    public WriteContext OriginalContext { get; init; }  // Original write context
    public OrphanStatus Status { get; init; }      // Current orphan status
    public DateTimeOffset? ExpiresAt { get; init; } // Compliance expiry (when can be purged)
    public Guid? LinkedTransactionId { get; init; } // If recovered/linked to retry
    public int RetryCount { get; init; }           // Number of retry attempts
}
```

**OrphanStatus Enum:**
| Value | Description |
|-------|-------------|
| `Active` | Orphan exists in WORM, not yet processed |
| `PendingReview` | Flagged for admin review |
| `MarkedForPurge` | Compliance period expired, can be deleted |
| `Purged` | Orphan deleted from WORM (record kept for audit) |
| `Recovered` | Orphan was recovered into valid object |
| `LinkedToRetry` | Orphan linked to successful retry transaction |

**Background Operations:**
| Task | Description | Dependencies | Status |
|------|-------------|--------------|--------|
| T4.15 | Implement background integrity scanner (configurable intervals) | T4.14 | [ ] |

**Additional Integrity Algorithms:**
| Task | Description | Dependencies | Status |
|------|-------------|--------------|--------|
| T4.16 | Implement SHA-3 family (Keccak-based NIST standard) | T4.15 | [ ] |
| T4.16.1 | ↳ SHA3-256 | T4.16 | [ ] |
| T4.16.2 | ↳ SHA3-384 | T4.16 | [ ] |
| T4.16.3 | ↳ SHA3-512 | T4.16 | [ ] |
| T4.17 | Implement Keccak family (original, pre-NIST) | T4.16 | [ ] |
| T4.17.1 | ↳ Keccak-256 | T4.17 | [ ] |
| T4.17.2 | ↳ Keccak-384 | T4.17 | [ ] |
| T4.17.3 | ↳ Keccak-512 | T4.17 | [ ] |
| T4.18 | Implement HMAC variants (keyed hashes) | T4.17 | [ ] |
| T4.18.1 | ↳ HMAC-SHA256 (keyed) | T4.18 | [ ] |
| T4.18.2 | ↳ HMAC-SHA384 (keyed) | T4.18 | [ ] |
| T4.18.3 | ↳ HMAC-SHA512 (keyed) | T4.18 | [ ] |
| T4.18.4 | ↳ HMAC-SHA3-256 (keyed) | T4.18 | [ ] |
| T4.18.5 | ↳ HMAC-SHA3-384 (keyed) | T4.18 | [ ] |
| T4.18.6 | ↳ HMAC-SHA3-512 (keyed) | T4.18 | [ ] |
| T4.19 | Implement Salted hash variants (per-object random salt) | T4.18 | [ ] |
| T4.19.1 | ↳ Salted-SHA256 | T4.19 | [ ] |
| T4.19.2 | ↳ Salted-SHA512 | T4.19 | [ ] |
| T4.19.3 | ↳ Salted-SHA3-256 | T4.19 | [ ] |
| T4.19.4 | ↳ Salted-SHA3-512 | T4.19 | [ ] |
| T4.19.5 | ↳ Salted-Blake3 | T4.19 | [ ] |
| T4.20 | Implement Salted HMAC variants (key + per-object salt) | T4.19 | [ ] |
| T4.20.1 | ↳ Salted-HMAC-SHA256 | T4.20 | [ ] |
| T4.20.2 | ↳ Salted-HMAC-SHA512 | T4.20 | [ ] |
| T4.20.3 | ↳ Salted-HMAC-SHA3-256 | T4.20 | [ ] |
| T4.20.4 | ↳ Salted-HMAC-SHA3-512 | T4.20 | [ ] |

**Additional Compression Algorithms:**
| Task | Description | Dependencies | Status |
|------|-------------|--------------|--------|
| T4.21 | Implement classic/simple compression algorithms | T4.20 | [ ] |
| T4.21.1 | ↳ RLE (Run-Length Encoding) | T4.21 | [ ] |
| T4.21.2 | ↳ Huffman coding | T4.21 | [ ] |
| T4.21.3 | ↳ LZW (Lempel-Ziv-Welch) | T4.21 | [ ] |
| T4.22 | Implement dictionary-based compression | T4.21 | [ ] |
| T4.22.1 | ↳ BZip2 (Burrows-Wheeler + Huffman) | T4.22 | [ ] |
| T4.22.2 | ↳ LZMA (7-Zip algorithm) | T4.22 | [ ] |
| T4.22.3 | ↳ LZMA2 (improved LZMA with streaming) | T4.22 | [ ] |
| T4.22.4 | ↳ Snappy (Google, optimized for speed) | T4.22 | [ ] |
| T4.23 | Implement statistical/context compression | T4.22 | [ ] |
| T4.23.1 | ↳ PPM (Prediction by Partial Matching) | T4.23 | [ ] |
| T4.23.2 | ↳ NNCP (Neural Network Compression) | T4.23 | [ ] |
| T4.23.3 | ↳ Schumacher Compression | T4.23 | [ ] |

**Compression Algorithm Reference:**
| Algorithm | Type | Speed | Ratio | Status | Use Case |
|-----------|------|-------|-------|--------|----------|
| GZip | Dictionary (DEFLATE) | Fast | Good | ✅ Implemented | General purpose, wide compatibility |
| Deflate | Dictionary (LZ77+Huffman) | Fast | Good | ✅ Implemented | HTTP compression, ZIP files |
| Brotli | Dictionary (LZ77+Huffman+Context) | Medium | Better | ✅ Implemented | Web content, text |
| LZ4 | Dictionary (LZ77) | Very Fast | Lower | ✅ Implemented | Real-time, databases |
| Zstd | Dictionary (FSE+Huffman) | Fast | Excellent | ✅ Implemented | Best all-around |
| RLE | Simple | Very Fast | Variable | [ ] Pending | Simple patterns, bitmaps |
| Huffman | Statistical | Fast | Good | [ ] Pending | Building block, education |
| LZW | Dictionary | Fast | Good | [ ] Pending | GIF, legacy systems |
| BZip2 | Block-sorting | Slow | Excellent | [ ] Pending | Large files, archives |
| LZMA/LZMA2 | Dictionary | Slow | Best | [ ] Pending | 7-Zip, XZ archives |
| Snappy | Dictionary | Very Fast | Lower | [ ] Pending | Google systems, speed-critical |
| PPM | Statistical | Slow | Excellent | [ ] Pending | Text, high compression |
| NNCP | Neural | Very Slow | Best | [ ] Pending | Research, maximum compression |
| Schumacher | Proprietary | Variable | Variable | [ ] Pending | Specialized use cases |

**Additional Encryption Algorithms:**

> **CRITICAL: All New Encryption Plugins MUST Extend `EncryptionPluginBase`**
>
> **DEPENDENCY:** T4.25-T4.29 depend on T5.0 (SDK Base Classes). Complete T5.0 first.
>
> After T5.0 completion, ALL new encryption plugins (except educational ciphers):
> - **MUST extend `EncryptionPluginBase`** (NOT `PipelinePluginBase` directly)
> - Get composable key management (Direct and Envelope modes) for free via inheritance
> - Only need to implement algorithm-specific `EncryptCoreAsync()` and `DecryptCoreAsync()`
> - Automatically support pairing with ANY `IKeyStore` or `IEnvelopeKeyStore`
>
> **Exception:** Educational/historical ciphers (T4.24) may extend `PipelinePluginBase` directly if no real key management.

| Task | Description | Base Class | Dependencies | Status |
|------|-------------|------------|--------------|--------|
| T4.24 | Implement historical/educational ciphers (NOT for production) | `PipelinePluginBase` | T4.23 | [ ] |
| T4.24.1 | ↳ Caesar/ROT13 (educational only) | `PipelinePluginBase` | T4.24 | [ ] |
| T4.24.2 | ↳ XOR cipher (educational only) | `PipelinePluginBase` | T4.24 | [ ] |
| T4.24.3 | ↳ Vigenère cipher (educational only) | `PipelinePluginBase` | T4.24 | [ ] |
| T4.25 | Implement legacy ciphers (compatibility only) | `EncryptionPluginBase` | T5.0, T4.24 | [ ] |
| T4.25.1 | ↳ DES (56-bit, legacy) | `EncryptionPluginBase` | T4.25 | [ ] |
| T4.25.2 | ↳ 3DES/Triple-DES (112/168-bit, legacy) | `EncryptionPluginBase` | T4.25 | [ ] |
| T4.25.3 | ↳ RC4 (stream cipher, legacy/WEP) | `EncryptionPluginBase` | T4.25 | [ ] |
| T4.25.4 | ↳ Blowfish (64-bit block, legacy) | `EncryptionPluginBase` | T4.25 | [ ] |
| T4.26 | Implement AES key size variants | `EncryptionPluginBase` | T5.0, T4.25 | [ ] |
| T4.26.1 | ↳ AES-128-GCM | `EncryptionPluginBase` | T4.26 | [ ] |
| T4.26.2 | ↳ AES-192-GCM | `EncryptionPluginBase` | T4.26 | [ ] |
| T4.26.3 | ↳ AES-256-CBC (for compatibility) | `EncryptionPluginBase` | T4.26 | [ ] |
| T4.26.4 | ↳ AES-256-CTR (counter mode) | `EncryptionPluginBase` | T4.26 | [ ] |
| T4.26.5 | ↳ AES-NI hardware acceleration detection | - | T4.26 | [ ] |
| T4.27 | Implement asymmetric/public-key encryption | `EncryptionPluginBase` | T5.0, T4.26 | [ ] |
| T4.27.1 | ↳ RSA-2048 | `EncryptionPluginBase` | T4.27 | [ ] |
| T4.27.2 | ↳ RSA-4096 | `EncryptionPluginBase` | T4.27 | [ ] |
| T4.27.3 | ↳ ECDH/ECDSA (Elliptic Curve) | `EncryptionPluginBase` | T4.27 | [ ] |
| T4.28 | Implement post-quantum cryptography | `EncryptionPluginBase` | T5.0, T4.27 | [ ] |
| T4.28.1 | ↳ ML-KEM (Kyber, NIST PQC standard) | `EncryptionPluginBase` | T4.28 | [ ] |
| T4.28.2 | ↳ ML-DSA (Dilithium, signatures) | `EncryptionPluginBase` | T4.28 | [ ] |
| T4.29 | Implement special-purpose encryption | `EncryptionPluginBase` | T5.0, T4.28 | [ ] |
| T4.29.1 | ↳ One-Time Pad (OTP) | `EncryptionPluginBase` | T4.29 | [ ] |
| T4.29.2 | ↳ XTS-AES (disk encryption mode) | `EncryptionPluginBase` | T4.29 | [ ] |

**Encryption Algorithm Reference:**
| Algorithm | Type | Key Size | Base Class | Envelope Mode | Status | Security | Use Case |
|-----------|------|----------|------------|---------------|--------|----------|----------|
| AES-256-GCM | Symmetric | 256-bit | `EncryptionPluginBase` | ✅ Supported | ✅ Implemented | Strong | Primary encryption |
| ChaCha20-Poly1305 | Symmetric | 256-bit | `EncryptionPluginBase` | ✅ Supported | ✅ Implemented | Strong | Mobile, no AES-NI |
| Twofish | Symmetric | 256-bit | `EncryptionPluginBase` | ✅ Supported | ✅ Implemented | Strong | AES finalist |
| Serpent | Symmetric | 256-bit | `EncryptionPluginBase` | ✅ Supported | ✅ Implemented | Very Strong | High security |
| FIPS | Symmetric | Various | `EncryptionPluginBase` | ✅ Supported | ✅ Implemented | Certified | Government compliance |
| ZeroKnowledge | Symmetric | 256-bit | `EncryptionPluginBase` | ✅ Supported | ✅ Implemented | Strong | Client-side + ZK proofs |
| Caesar/ROT13 | Substitution | None | `PipelinePluginBase` | ❌ N/A | [ ] Pending | ❌ None | Educational only |
| XOR | Stream | Variable | `PipelinePluginBase` | ❌ N/A | [ ] Pending | ❌ Weak | Educational only |
| Vigenère | Substitution | Variable | `PipelinePluginBase` | ❌ N/A | [ ] Pending | ❌ Weak | Educational only |
| DES | Symmetric | 56-bit | `EncryptionPluginBase` | ✅ Supported | [ ] Pending | ❌ Broken | Legacy compatibility |
| 3DES | Symmetric | 112/168-bit | `EncryptionPluginBase` | ✅ Supported | [ ] Pending | ⚠️ Weak | Legacy compatibility |
| RC4 | Stream | 40-2048-bit | `EncryptionPluginBase` | ✅ Supported | [ ] Pending | ❌ Broken | Legacy (WEP) |
| Blowfish | Symmetric | 32-448-bit | `EncryptionPluginBase` | ✅ Supported | [ ] Pending | ⚠️ Aging | Legacy compatibility |
| AES-128-GCM | Symmetric | 128-bit | `EncryptionPluginBase` | ✅ Supported | [ ] Pending | Strong | Performance-critical |
| AES-192-GCM | Symmetric | 192-bit | `EncryptionPluginBase` | ✅ Supported | [ ] Pending | Strong | Middle ground |
| RSA-2048 | Asymmetric | 2048-bit | `EncryptionPluginBase` | ✅ Supported | [ ] Pending | Strong | Key exchange |
| RSA-4096 | Asymmetric | 4096-bit | `EncryptionPluginBase` | ✅ Supported | [ ] Pending | Very Strong | High security |
| ML-KEM (Kyber) | Post-Quantum | Various | `EncryptionPluginBase` | ✅ Supported | [ ] Pending | Quantum-Safe | Future-proof |
| One-Time Pad | Perfect | ≥ Message | `EncryptionPluginBase` | ✅ Supported | [ ] Pending | Perfect | Theoretical max |

---

### Encryption Metadata Requirements (CRITICAL for Decryption)

> **IMPORTANT: This pattern applies to ALL encryption, not just tamper-proof storage.**
> - **Standalone encryption:** EncryptionMetadata stored in **ciphertext header** (binary prefix)
> - **Tamper-proof storage:** EncryptionMetadata stored in **TamperProofManifest** (JSON field)
>
> The principle is identical: store write-time config WITH the data so read-time can always decrypt correctly.
> The only difference is WHERE the metadata is stored (header vs manifest).

**Problem:** When reading encrypted data back, the system MUST know:
1. Which encryption algorithm was used
2. Which key management mode (Direct vs Envelope)
3. Key identifiers (key ID for Direct, wrapped DEK + KEK ID for Envelope)

**Solution:** Store encryption metadata in the **TamperProofManifest** (for tamper-proof storage) or **ciphertext header** (for standalone encryption).

**Encryption Metadata Structure:**
```csharp
/// <summary>
/// Metadata stored with encrypted data to enable decryption.
/// Stored in TamperProofManifest.EncryptionMetadata or embedded in ciphertext header.
/// </summary>
public record EncryptionMetadata
{
    /// <summary>Encryption plugin ID (e.g., "aes256gcm", "chacha20", "twofish256")</summary>
    public string EncryptionPluginId { get; init; } = "";

    /// <summary>Key management mode: Direct or Envelope</summary>
    public KeyManagementMode KeyMode { get; init; }

    /// <summary>For Direct mode: Key ID in the key store</summary>
    public string? KeyId { get; init; }

    /// <summary>For Envelope mode: Wrapped DEK (encrypted by HSM)</summary>
    public byte[]? WrappedDek { get; init; }

    /// <summary>For Envelope mode: KEK identifier in HSM</summary>
    public string? KekId { get; init; }

    /// <summary>Key store plugin ID used (for verification/routing)</summary>
    public string? KeyStorePluginId { get; init; }

    /// <summary>Algorithm-specific parameters (IV, nonce, tag location, etc.)</summary>
    public Dictionary<string, object> AlgorithmParams { get; init; } = new();
}
```

**Where Metadata is Stored:**

| Storage Mode | Metadata Location | Format |
|--------------|-------------------|--------|
| **Tamper-Proof Storage** | `TamperProofManifest.EncryptionMetadata` | JSON in manifest |
| **Standalone Encryption** | Ciphertext header | Binary prefix |
| **Database/SQL TDE** | Encryption key table | SQL metadata |

**Read Path with Metadata:**
```
1. Load manifest or parse ciphertext header
2. Extract EncryptionMetadata
3. Determine encryption plugin from EncryptionPluginId
4. Determine key management mode from KeyMode:
   - Direct: Get key from IKeyStore using KeyId
   - Envelope: Unwrap DEK using VaultKeyStorePlugin with WrappedDek + KekId
5. Decrypt using appropriate encryption plugin
```

**Benefits:**
- ✅ Any encryption algorithm can be paired with any key management at runtime
- ✅ Metadata ensures correct decryption even if defaults change
- ✅ Supports migration between key management modes
- ✅ Enables key rotation without re-encryption (for envelope mode)

---

### Key Management + Tamper-Proof Storage Integration

> **CRITICAL:** This section defines how per-user key management configuration works with tamper-proof storage.

**The Challenge:**
- Per-user configuration is resolved at runtime from user preferences
- Tamper-proof storage requires deterministic decryption (MUST work even if preferences change)
- What if user changes their key management preferences between write and read?
- What if IKeyManagementConfigProvider returns different results over time?

**Solution: Write-Time Config Stored in Manifest, Read-Time Uses Manifest**

```
┌─────────────────────────────────────────────────────────────────────────┐
│           TAMPER-PROOF WRITE PATH (Config Resolved → Stored)            │
├─────────────────────────────────────────────────────────────────────────┤
│  1. User initiates write                                                 │
│  2. Resolve KeyManagementConfig (args → user prefs → defaults)           │
│  3. Encrypt data using resolved config                                   │
│  4. Create TamperProofManifest with EncryptionMetadata:                  │
│     - EncryptionPluginId: "aes256gcm"                                    │
│     - KeyMode: Envelope                                                  │
│     - WrappedDek: [encrypted DEK bytes]                                  │
│     - KekId: "azure-kek-finance"                                         │
│     - KeyStorePluginId: "vault-azure"  ◄── STORED FOR DECRYPTION        │
│  5. Hash manifest + data, anchor to blockchain                           │
│  6. Store to WORM for disaster recovery                                  │
└─────────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────────┐
│           TAMPER-PROOF READ PATH (Config from Manifest Only)            │
├─────────────────────────────────────────────────────────────────────────┤
│  1. User initiates read                                                  │
│  2. Load TamperProofManifest                                             │
│  3. Verify integrity (hash chain, blockchain anchor)                     │
│  4. Extract EncryptionMetadata from manifest                             │
│     *** IGNORE current user preferences ***                              │
│  5. Resolve key store from stored KeyStorePluginId                       │
│  6. Decrypt using stored config:                                         │
│     - Mode: from manifest (Envelope)                                     │
│     - KEK: from manifest (azure-kek-finance)                             │
│     - Key Store: from manifest (vault-azure)                             │
│  7. Return decrypted data                                                │
└─────────────────────────────────────────────────────────────────────────┘
```

**Key Principles:**
| Operation | Config Source | Rationale |
|-----------|---------------|-----------|
| **WRITE** | User's current config (resolved per-operation) | User chooses encryption settings |
| **READ** | Manifest's stored config | Ensures decryption always works, even if user prefs change |

**Why This Works for Tamper-Proof:**
1. **Deterministic Decryption**: Config stored with data → can always decrypt
2. **Tamper Detection**: If someone modifies EncryptionMetadata in manifest → integrity hash fails
3. **Audit Trail**: Manifest shows exactly what config was used at write time
4. **Flexibility Preserved**: Different objects can use different configs (per-user at write time)
5. **No Degradation**: Read performance is the same (no config resolution needed)

**Configuration Modes for Tamper-Proof Storage:**

The `TamperProofStorageProvider` can be configured with different encryption flexibility levels:

| Mode | Description | Use Case |
|------|-------------|----------|
| `PerObjectConfig` | Each object stores its own EncryptionMetadata (DEFAULT) | Multi-tenant, mixed compliance |
| `FixedConfig` | All objects use same config (sealed at first write) | Single-tenant, strict compliance |
| `PolicyEnforced` | Per-object, but must match tenant policy | Enterprise with compliance rules |

```csharp
// MODE 1: PerObjectConfig (DEFAULT) - Maximum flexibility
var tamperProof = new TamperProofStorageProvider(new TamperProofConfig
{
    EncryptionConfigMode = EncryptionConfigMode.PerObjectConfig,
    // Each object's manifest stores its own EncryptionMetadata
    // Different users can use different configs
});

// MODE 2: FixedConfig - Strict consistency
var tamperProofStrict = new TamperProofStorageProvider(new TamperProofConfig
{
    EncryptionConfigMode = EncryptionConfigMode.FixedConfig,
    FixedEncryptionConfig = new EncryptionMetadata
    {
        EncryptionPluginId = "aes256gcm",
        KeyMode = KeyManagementMode.Envelope,
        KeyStorePluginId = "vault-hsm",
        KekId = "master-kek"
    }
    // ALL objects MUST use this config - enforced at write time
    // First write seals this config
});

// MODE 3: PolicyEnforced - Flexibility within policy
var tamperProofPolicy = new TamperProofStorageProvider(new TamperProofConfig
{
    EncryptionConfigMode = EncryptionConfigMode.PolicyEnforced,
    EncryptionPolicy = new EncryptionPolicy
    {
        AllowedModes = [KeyManagementMode.Envelope],  // Must be envelope
        AllowedKeyStores = ["vault-azure", "vault-aws"],  // Only HSM backends
        RequireHsmBackedKek = true  // KEK must be in HSM
    }
    // Per-object config allowed, but must satisfy policy
});
```

**EncryptionMetadata in TamperProofManifest:**
```csharp
public class TamperProofManifest
{
    // ... existing fields ...

    /// <summary>
    /// Encryption configuration used for this object.
    /// CRITICAL: Used on READ to decrypt, ignoring current user preferences.
    /// </summary>
    public EncryptionMetadata? EncryptionMetadata { get; set; }
}

public record EncryptionMetadata
{
    /// <summary>Encryption plugin used (e.g., "aes256gcm")</summary>
    public string EncryptionPluginId { get; init; } = "";

    /// <summary>Key management mode used</summary>
    public KeyManagementMode KeyMode { get; init; }

    /// <summary>For Direct mode: Key ID in the key store</summary>
    public string? KeyId { get; init; }

    /// <summary>For Envelope mode: Wrapped DEK</summary>
    public byte[]? WrappedDek { get; init; }

    /// <summary>For Envelope mode: KEK identifier</summary>
    public string? KekId { get; init; }

    /// <summary>Key store plugin ID (for resolving on read)</summary>
    public string? KeyStorePluginId { get; init; }

    /// <summary>Algorithm-specific params (IV, nonce, tag location)</summary>
    public Dictionary<string, object> AlgorithmParams { get; init; } = new();

    /// <summary>Timestamp when encryption was performed</summary>
    public DateTime EncryptedAt { get; init; }

    /// <summary>User/tenant who encrypted (for audit)</summary>
    public string? EncryptedBy { get; init; }
}
```

**Summary - Flexibility vs Tamper-Proof:**
| Aspect | Standalone Encryption | Tamper-Proof Storage |
|--------|----------------------|----------------------|
| **Write Config** | Per-user, per-operation | Per-user, per-operation |
| **Read Config** | Per-user, per-operation | FROM MANIFEST (stored at write) |
| **Config Storage** | Ciphertext header | TamperProofManifest |
| **Integrity** | Tag verification only | Hash chain + blockchain + WORM |
| **Can Change Prefs?** | Yes, affects new ops | Yes, but read uses original config |
| **Degradation** | None | None (manifest has all needed info) |

**Integrity Algorithm Reference:**
| Category | Algorithms | Key Required | Salt | Use Case |
|----------|------------|--------------|------|----------|
| **SHA-2** | SHA-256, SHA-384, SHA-512 | No | No | Standard integrity verification |
| **SHA-3** | SHA3-256, SHA3-384, SHA3-512 | No | No | NIST standard, quantum-resistant design |
| **Keccak** | Keccak-256, Keccak-384, Keccak-512 | No | No | Original (pre-NIST), used by Ethereum |
| **Blake3** | Blake3 | No | No | Fastest, modern, parallel-friendly |
| **HMAC** | HMAC-SHA256/384/512, HMAC-SHA3-256/384/512 | Yes | No | Keyed authentication, prevents length extension |
| **Salted** | Salted-SHA256/512, Salted-SHA3, Salted-Blake3 | No | Yes | Per-object salt prevents rainbow tables |
| **Salted HMAC** | Salted-HMAC-SHA256/512, Salted-HMAC-SHA3 | Yes | Yes | Maximum security: key + per-object salt |

**Note:**
- **HMAC** uses a secret key for authentication (proves data wasn't tampered AND you have the key)
- **Salted** adds a random per-object salt stored in manifest (prevents precomputation attacks)
- **Salted HMAC** combines both: key-based authentication with per-object salt randomization

**Instance Degradation States:**
| State | Cause | Impact | User Action |
|-------|-------|--------|-------------|
| `Healthy` | All systems operational | Full functionality | None |
| `Degraded` | Some shards unavailable, but reconstructible | Reads slower, writes may be slower | Monitor, plan maintenance |
| `DegradedReadOnly` | Parity exhausted, cannot guarantee writes | Reads work, writes blocked | Urgent: restore storage |
| `DegradedVerifyOnly` | Blockchain unavailable, cannot anchor | Reads verified, writes unanchored | Restore blockchain |
| `DegradedNoRecovery` | WORM unavailable | Cannot auto-recover from tampering | Critical: restore WORM |
| `Offline` | Primary storage unavailable | No operations possible | Emergency: restore storage |
| `Corrupted` | Tampering detected, recovery failed | Data integrity compromised | Incident response required |

**Seal Mechanism:**
After the first write operation, structural configuration becomes immutable:
- **Locked after seal:** Storage instances, RAID configuration, hash algorithm, blockchain mode, WORM provider
- **Configurable always:** Recovery behavior, read mode defaults, logging verbosity, alert thresholds, padding modes

#### Phase T5: Ultra Paranoid Mode (Priority: LOW)

**Goal:** Maximum security for government/military-grade deployments

---

### Key Management Architecture (EXISTING - Composable Plugins)

**IMPORTANT:** The key management infrastructure is **already implemented** with composable plugins.
Encryption plugins can use **any** `IKeyStore` implementation for key management:

```
┌─────────────────────────────────────────────────────────────────────────────────────┐
│                    COMPOSABLE KEY MANAGEMENT ARCHITECTURE                            │
├─────────────────────────────────────────────────────────────────────────────────────┤
│                                                                                      │
│   ENCRYPTION PLUGINS (ALL use IKeyStore - composable key management)                 │
│   ┌─────────────┐ ┌─────────────┐ ┌─────────────┐ ┌─────────────┐ ┌─────────────┐   │
│   │ AES-256-GCM │ │ ChaCha20    │ │ Twofish-256 │ │ Serpent-256 │ │ FIPS-140-2  │   │
│   │ ✅ IKeyStore│ │ ✅ IKeyStore│ │ ✅ IKeyStore│ │ ✅ IKeyStore│ │ ✅ IKeyStore│   │
│   └──────┬──────┘ └──────┬──────┘ └──────┬──────┘ └──────┬──────┘ └──────┬──────┘   │
│          │               │               │               │               │           │
│   ┌──────┴──────┐                                                                    │
│   │ZeroKnowledge│                                                                    │
│   │ ✅ IKeyStore│ (6 plugins total, all support composable key management)          │
│   └──────┬──────┘                                                                    │
│          └───────────────────────────────────────────────────────────────────────────│
│                                           │                                          │
│                                           ▼                                          │
│                              ┌───────────────────────┐                               │
│                              │      IKeyStore        │                               │
│                              │ interface (SDK)       │                               │
│                              └───────────┬───────────┘                               │
│                                          │                                           │
│            ┌─────────────────────────────┼─────────────────────────────┐             │
│            ▼                             ▼                             ▼             │
│   ┌─────────────────────┐   ┌─────────────────────────┐   ┌─────────────────────┐   │
│   │  FileKeyStorePlugin │   │   VaultKeyStorePlugin   │   │  KeyRotationPlugin  │   │
│   │  ✅ Implemented     │   │   ✅ Implemented        │   │  ✅ Implemented     │   │
│   ├─────────────────────┤   ├─────────────────────────┤   ├─────────────────────┤   │
│   │ 4-Tier Protection:  │   │ HSM/Cloud Integration:  │   │ Features:           │   │
│   │ • DPAPI (Windows)   │   │ • HashiCorp Vault       │   │ • Auto rotation     │   │
│   │ • CredentialManager │   │ • Azure Key Vault       │   │ • Key versioning    │   │
│   │ • Database-backed   │   │ • AWS KMS               │   │ • Re-encryption     │   │
│   │ • Password (PBKDF2) │   │ • Google Cloud KMS      │   │ • Audit trail       │   │
│   │                     │   │                         │   │ • Wraps IKeyStore   │   │
│   │ Use Case: Local     │   │ Use Case: Enterprise    │   │ Use Case: Layered   │   │
│   │ deployments         │   │ HSM/envelope encryption │   │ on any IKeyStore    │   │
│   └─────────────────────┘   └──────────┬──────────────┘   └─────────────────────┘   │
│                                        │                                             │
│                                        ▼                                             │
│                           ┌───────────────────────┐                                  │
│                           │    IVaultBackend      │                                  │
│                           │ (internal interface)  │                                  │
│                           ├───────────────────────┤                                  │
│                           │ • WrapKeyAsync()      │ ◄── ENVELOPE ENCRYPTION!        │
│                           │ • UnwrapKeyAsync()    │     (already implemented)       │
│                           │ • GetKeyAsync()       │                                  │
│                           │ • CreateKeyAsync()    │                                  │
│                           └───────────┬───────────┘                                  │
│                                       │                                              │
│              ┌────────────────────────┼────────────────────────┐                     │
│              ▼                        ▼                        ▼                     │
│     ┌─────────────────┐     ┌─────────────────┐     ┌─────────────────┐             │
│     │ HashiCorpVault  │     │  AzureKeyVault  │     │    AwsKms       │             │
│     │ Backend         │     │  Backend        │     │    Backend      │             │
│     │ ✅ Implemented  │     │  ✅ Implemented │     │  ✅ Implemented │             │
│     └─────────────────┘     └─────────────────┘     └─────────────────┘             │
│                                                                                      │
└─────────────────────────────────────────────────────────────────────────────────────┘
```

**Existing Key Store Plugins:**
| Plugin | Type | Features | Status |
|--------|------|----------|--------|
| `FileKeyStorePlugin` | Local/File | DPAPI, CredentialManager, Database, PBKDF2 tiers | ✅ Implemented |
| `VaultKeyStorePlugin` | HSM/Cloud | HashiCorp Vault, Azure Key Vault, AWS KMS, Google KMS + **WrapKey/UnwrapKey** | ✅ Implemented |
| `KeyRotationPlugin` | Layer | Wraps any IKeyStore, adds rotation, versioning, audit | ✅ Implemented |
| `SecretManagementPlugin` | Secret Mgmt | Secure secret storage with access control | ✅ Implemented |

**VaultKeyStorePlugin Already Supports Envelope Encryption:**
```csharp
// VaultKeyStorePlugin's IVaultBackend interface (ALREADY EXISTS):
internal interface IVaultBackend
{
    Task<byte[]> WrapKeyAsync(string keyId, byte[] dataKey);   // ◄── Wrap DEK with KEK
    Task<byte[]> UnwrapKeyAsync(string keyId, byte[] wrappedKey); // ◄── Unwrap DEK
    Task<byte[]> GetKeyAsync(string keyId);
    Task<byte[]> CreateKeyAsync(string keyId);
    // ...
}
```

---

### T5.0: SDK Base Classes and Plugin Refactoring (FOUNDATION - Must Complete First)

> **CRITICAL: This section establishes the SDK foundation that ALL encryption and key management work depends on.**
>
> **DEPENDENCY ORDER: T5.0 MUST be completed BEFORE Phase T3 (Read Pipeline & Verification).**
> - T3.4.2 "Decrypt (if encrypted)" requires `EncryptionPluginBase` infrastructure from T5.0.3
> - T3.4.2 needs `EncryptionMetadata` from manifests to resolve decryption config
> - Without T5.0, decryption in T3 would require hard-coded assumptions about key management
>
> **STORAGE PROVIDERS NOTE:** Storage providers (S3, Local, Azure, etc.) do NOT need modification for this feature.
> Encryption/decryption happens at the **pipeline level** (`PipelinePluginBase`), which is independent of storage.
> The storage provider only sees already-encrypted bytes - it has no knowledge of encryption at all.
>
> **Problem Identified:** Currently, all 6 encryption plugins and all key management plugins have duplicated code for:
> - Key management (getting keys, security context validation)
> - Key caching and initialization patterns
> - Statistics tracking and message handling
>
> **Solution:** Create abstract base classes in the SDK that provide common functionality, then refactor existing plugins to extend these base classes. All new plugins MUST extend these base classes.

#### Current Plugin Hierarchy (What Exists Today)

```
PluginBase (SDK)
├── DataTransformationPluginBase (SDK, IDataTransformation)
│   └── PipelinePluginBase (SDK, for ordered pipeline stages)
│       └── AesEncryptionPlugin, ChaCha20EncryptionPlugin, etc. (Plugins)
│           └── ⚠️ DUPLICATED: Key management logic in each plugin
│
├── SecurityProviderPluginBase (SDK)
│   └── FileKeyStorePlugin, VaultKeyStorePlugin (Plugins, implement IKeyStore)
│       └── ⚠️ DUPLICATED: Caching, initialization, validation logic
```

#### Target Plugin Hierarchy (After T5.0)

```
PluginBase (SDK)
├── DataTransformationPluginBase (SDK, IDataTransformation)
│   └── PipelinePluginBase (SDK)
│       └── EncryptionPluginBase (SDK, NEW - T5.0.1) ◄── Common key management
│           └── AesEncryptionPlugin, ChaCha20EncryptionPlugin, etc. (Refactored)
│
├── SecurityProviderPluginBase (SDK)
│   └── KeyStorePluginBase (SDK, NEW - T5.0.2, implements IKeyStore) ◄── Common caching
│       └── FileKeyStorePlugin, VaultKeyStorePlugin (Refactored)
```

---

#### T5.0.1: SDK Types for Composable Key Management

| Task | Component | Location | Description | Status |
|------|-----------|----------|-------------|--------|
| T5.0.1 | SDK Key Management Types | DataWarehouse.SDK/Security/ | Shared types for key management | [ ] |
| T5.0.1.1 | `KeyManagementMode` enum | IKeyStore.cs | `Direct` (key from IKeyStore) vs `Envelope` (DEK wrapped by HSM KEK) | [ ] |
| T5.0.1.2 | `IEnvelopeKeyStore` interface | IKeyStore.cs | Extends IKeyStore with `WrapKeyAsync`/`UnwrapKeyAsync` | [ ] |
| T5.0.1.3 | `EnvelopeHeader` class | EnvelopeHeader.cs | Serialize/deserialize envelope header: WrappedDEK, KekId, etc. | [ ] |
| T5.0.1.4 | `EncryptionMetadata` record | EncryptionMetadata.cs | Full metadata: plugin ID, mode, key IDs, algorithm params | [ ] |
| T5.0.1.5 | `KeyManagementConfig` record | KeyManagementConfig.cs | Per-user configuration: mode, key store, KEK ID, etc. | [ ] |
| T5.0.1.6 | `IKeyManagementConfigProvider` interface | IKeyManagementConfigProvider.cs | Resolve per-user/per-tenant key management preferences | [ ] |
| T5.0.1.7 | `IKeyStoreRegistry` interface | IKeyStoreRegistry.cs | Registry for resolving plugin IDs to key store instances | [ ] |
| T5.0.1.8 | `DefaultKeyStoreRegistry` implementation | DefaultKeyStoreRegistry.cs | Default in-memory implementation of IKeyStoreRegistry | [ ] |
| T5.0.1.9 | `EncryptionConfigMode` enum | EncryptionConfigMode.cs | Per-object vs fixed vs policy-enforced configuration | [ ] |

**KeyManagementMode Enum:**
```csharp
/// <summary>
/// Determines how encryption keys are managed.
/// User-configurable option for all encryption plugins.
/// </summary>
public enum KeyManagementMode
{
    /// <summary>
    /// Direct mode (DEFAULT): Key is retrieved directly from any IKeyStore.
    /// Works with: FileKeyStorePlugin, VaultKeyStorePlugin, KeyRotationPlugin, etc.
    /// </summary>
    Direct,

    /// <summary>
    /// Envelope mode: A unique DEK is generated per object, wrapped by HSM KEK,
    /// and stored in the ciphertext header. Requires IEnvelopeKeyStore.
    /// Works with: VaultKeyStorePlugin (or any IEnvelopeKeyStore implementation).
    /// </summary>
    Envelope
}
```

**IEnvelopeKeyStore Interface:**
```csharp
/// <summary>
/// Extended key store interface that supports envelope encryption operations.
/// Required for KeyManagementMode.Envelope.
/// </summary>
public interface IEnvelopeKeyStore : IKeyStore
{
    /// <summary>
    /// Wrap a Data Encryption Key (DEK) with a Key Encryption Key (KEK).
    /// The KEK never leaves the HSM.
    /// </summary>
    Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context);

    /// <summary>
    /// Unwrap a previously wrapped DEK using the KEK in the HSM.
    /// </summary>
    Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context);
}
```

**EncryptionConfigMode Enum (T5.0.1.9):**
```csharp
/// <summary>
/// Determines how encryption configuration is managed for a storage context.
/// Applies to BOTH tamper-proof storage (manifest) and standalone encryption (ciphertext header).
/// </summary>
public enum EncryptionConfigMode
{
    /// <summary>
    /// DEFAULT: Each object stores its own EncryptionMetadata.
    /// - Tamper-proof: EncryptionMetadata stored in TamperProofManifest
    /// - Standalone: EncryptionMetadata stored in ciphertext header
    /// Use case: Multi-tenant deployments, mixed compliance requirements.
    /// </summary>
    PerObjectConfig,

    /// <summary>
    /// All objects MUST use the same encryption configuration.
    /// Configuration is sealed after first write - cannot be changed.
    /// Any write with different config will be rejected.
    /// Use case: Single-tenant deployments, strict compliance (all data same encryption).
    /// </summary>
    FixedConfig,

    /// <summary>
    /// Per-object configuration allowed, but must satisfy tenant/org policy.
    /// Policy defines: allowed encryption algorithms, required key modes, allowed key stores.
    /// Writes that violate policy are rejected with detailed error.
    /// Use case: Enterprise with compliance rules but per-user flexibility within bounds.
    /// </summary>
    PolicyEnforced
}
```

---

#### T5.0.2: KeyStorePluginBase Abstract Class

| Task | Component | Location | Description | Status |
|------|-----------|----------|-------------|--------|
| T5.0.2 | `KeyStorePluginBase` | SDK/Contracts/PluginBase.cs | Abstract base for all key management plugins | [ ] |
| T5.0.2.1 | ↳ Key caching infrastructure | Common | `ConcurrentDictionary<string, CachedKey>`, cache expiration | [ ] |
| T5.0.2.2 | ↳ Initialization pattern | Common | `EnsureInitializedAsync()`, thread-safe init with `SemaphoreSlim` | [ ] |
| T5.0.2.3 | ↳ Security context validation | Common | `ValidateAccess()`, `ValidateAdminAccess()` | [ ] |
| T5.0.2.4 | ↳ Standard message handling | Common | `keystore.*.create`, `keystore.*.get`, `keystore.*.rotate` | [ ] |
| T5.0.2.5 | ↳ Abstract storage methods | Abstract | `LoadKeyFromStorageAsync()`, `SaveKeyToStorageAsync()` | [ ] |

**KeyStorePluginBase Design:**
```csharp
/// <summary>
/// Abstract base class for all key management plugins.
/// Provides common caching, initialization, and validation logic.
/// All key management plugins MUST extend this class.
/// </summary>
public abstract class KeyStorePluginBase : SecurityProviderPluginBase, IKeyStore
{
    // COMMON INFRASTRUCTURE (implemented in base)
    protected readonly ConcurrentDictionary<string, CachedKey> KeyCache;
    protected readonly SemaphoreSlim InitLock;
    protected string CurrentKeyId;
    protected bool Initialized;

    // CONFIGURATION (override in derived classes)
    protected abstract TimeSpan CacheExpiration { get; }
    protected abstract int KeySizeBytes { get; }
    protected virtual bool RequireAuthentication => true;
    protected virtual bool RequireAdminForCreate => true;

    // IKeyStore IMPLEMENTATION (common logic, calls abstract methods)
    public async Task<string> GetCurrentKeyIdAsync() { /* uses EnsureInitializedAsync */ }
    public byte[] GetKey(string keyId) { /* sync wrapper */ }
    public async Task<byte[]> GetKeyAsync(string keyId, ISecurityContext context) { /* cache + validation + abstract */ }
    public async Task<byte[]> CreateKeyAsync(string keyId, ISecurityContext context) { /* validation + abstract */ }

    // ABSTRACT METHODS (implement in derived classes)
    protected abstract Task<byte[]?> LoadKeyFromStorageAsync(string keyId);
    protected abstract Task SaveKeyToStorageAsync(string keyId, byte[] key);
    protected abstract Task InitializeStorageAsync();

    // COMMON UTILITIES (used by derived classes)
    protected async Task EnsureInitializedAsync() { /* thread-safe init pattern */ }
    protected void ValidateAccess(ISecurityContext context) { /* common validation */ }
    protected void ValidateAdminAccess(ISecurityContext context) { /* admin validation */ }
}
```

---

#### T5.0.3: EncryptionPluginBase Abstract Class

| Task | Component | Location | Description | Status |
|------|-----------|----------|-------------|--------|
| T5.0.3 | `EncryptionPluginBase` | SDK/Contracts/PluginBase.cs | Abstract base for all encryption plugins | [ ] |
| T5.0.3.1 | ↳ Key store resolution | Common | `GetKeyStore()` from args, config, or kernel context | [ ] |
| T5.0.3.2 | ↳ Security context resolution | Common | `GetSecurityContext()` from args or config | [ ] |
| T5.0.3.3 | ↳ Key management mode support | Common | `KeyManagementMode` property, Direct vs Envelope | [ ] |
| T5.0.3.4 | ↳ Envelope key handling | Common | `GetKeyForEncryption()`, `GetKeyForDecryption()` with envelope support | [ ] |
| T5.0.3.5 | ↳ Statistics tracking | Common | Encryption/decryption counts, bytes processed | [ ] |
| T5.0.3.6 | ↳ Key access logging | Common | Audit trail for key usage | [ ] |
| T5.0.3.7 | ↳ Abstract encrypt/decrypt | Abstract | `EncryptCoreAsync()`, `DecryptCoreAsync()` | [ ] |

**EncryptionPluginBase Design (Per-User Configuration):**
```csharp
/// <summary>
/// Abstract base class for all encryption plugins with composable key management.
/// Supports per-user, per-operation configuration for maximum flexibility.
/// All encryption plugins MUST extend this class.
/// </summary>
public abstract class EncryptionPluginBase : PipelinePluginBase, IDisposable
{
    // DEFAULT CONFIGURATION (fallback when no user preference or explicit override)
    protected IKeyStore? DefaultKeyStore;
    protected KeyManagementMode DefaultKeyManagementMode = KeyManagementMode.Direct;
    protected IEnvelopeKeyStore? DefaultEnvelopeKeyStore;
    protected string? DefaultKekKeyId;

    // PER-USER CONFIGURATION PROVIDER (optional, for multi-tenant)
    protected IKeyManagementConfigProvider? ConfigProvider;

    // STATISTICS (common tracking - aggregated across all users)
    protected readonly object StatsLock = new();
    protected long EncryptionCount;
    protected long DecryptionCount;
    protected long TotalBytesEncrypted;
    protected long TotalBytesDecrypted;

    // KEY ACCESS AUDIT (common - tracks per key ID)
    protected readonly ConcurrentDictionary<string, DateTime> KeyAccessLog = new();

    // CONFIGURATION (override in derived classes)
    protected abstract int KeySizeBytes { get; }  // e.g., 32 for AES-256
    protected abstract int IvSizeBytes { get; }   // e.g., 12 for GCM
    protected abstract int TagSizeBytes { get; }  // e.g., 16 for GCM

    // CONFIGURATION RESOLUTION (per-operation)
    /// <summary>
    /// Resolves key management configuration for this operation.
    /// Priority: 1. Explicit args, 2. User preferences, 3. Plugin defaults
    /// </summary>
    protected async Task<ResolvedKeyManagementConfig> ResolveConfigAsync(
        Dictionary<string, object> args,
        ISecurityContext context)
    {
        // 1. Check for explicit overrides in args
        if (TryGetConfigFromArgs(args, out var argsConfig))
            return argsConfig;

        // 2. Check for user preferences via ConfigProvider
        if (ConfigProvider != null)
        {
            var userConfig = await ConfigProvider.GetConfigAsync(context);
            if (userConfig != null)
                return ResolveFromUserConfig(userConfig, context);
        }

        // 3. Fall back to plugin defaults
        return new ResolvedKeyManagementConfig
        {
            Mode = DefaultKeyManagementMode,
            KeyStore = DefaultKeyStore,
            EnvelopeKeyStore = DefaultEnvelopeKeyStore,
            KekKeyId = DefaultKekKeyId
        };
    }

    // COMMON KEY MANAGEMENT (uses resolved config)
    protected async Task<(byte[] key, string keyId, EnvelopeHeader? envelope)> GetKeyForEncryptionAsync(
        ResolvedKeyManagementConfig config, ISecurityContext context);
    protected async Task<byte[]> GetKeyForDecryptionAsync(
        EnvelopeHeader? envelope, string? keyId, ResolvedKeyManagementConfig config, ISecurityContext context);

    // OnWrite/OnRead IMPLEMENTATION (resolves config first, then processes)
    protected override async Task<Stream> OnWriteAsync(Stream input, IKernelContext context, Dictionary<string, object> args)
    {
        var securityContext = GetSecurityContext(args);
        var config = await ResolveConfigAsync(args, securityContext);  // Per-user resolution!
        var (key, keyId, envelope) = await GetKeyForEncryptionAsync(config, securityContext);
        // ... encrypt using resolved config ...
    }

    // ABSTRACT METHODS (implement in derived classes - algorithm-specific)
    protected abstract Task<Stream> EncryptCoreAsync(Stream input, byte[] key, byte[] iv, IKernelContext context);
    protected abstract Task<(Stream data, byte[] tag)> DecryptCoreAsync(Stream input, byte[] key, IKernelContext context);
    protected abstract byte[] GenerateIv();
    protected abstract int CalculateHeaderSize(bool envelopeMode);
}

/// <summary>
/// Resolved configuration for a single operation (after applying priority rules).
/// </summary>
internal record ResolvedKeyManagementConfig
{
    public KeyManagementMode Mode { get; init; }
    public IKeyStore? KeyStore { get; init; }
    public IEnvelopeKeyStore? EnvelopeKeyStore { get; init; }
    public string? KekKeyId { get; init; }
}
```

**Per-User, Per-Operation Configuration (Maximum Flexibility):**

The key management configuration is resolved **per-operation** (each OnWrite/OnRead call), NOT per-instance. This enables:
- Same plugin instance serves multiple users with different configurations
- User A uses Envelope mode + Azure HSM, User B uses Direct mode + FileKeyStore
- Configuration can change between operations for the same user
- Multi-tenant deployments with different security requirements per tenant

**Configuration Resolution Order (highest priority first):**
1. **Explicit args** - passed directly to OnWrite/OnRead
2. **User preferences** - resolved via `IKeyManagementConfigProvider` from `ISecurityContext`
3. **Plugin defaults** - set at construction time (fallback)

```csharp
// Plugin instance with DEFAULT configuration (fallback only)
var aesPlugin = new AesEncryptionPlugin(new AesEncryptionConfig
{
    // These are DEFAULTS - can be overridden per-user or per-operation
    DefaultKeyManagementMode = KeyManagementMode.Direct,
    DefaultKeyStore = new FileKeyStorePlugin(),

    // Optional: User preference resolver for multi-tenant scenarios
    KeyManagementConfigProvider = new DatabaseKeyManagementConfigProvider(dbContext)
});

// SCENARIO 1: User1 writes with explicit Envelope mode override
await aesPlugin.OnWriteAsync(data, context, new Dictionary<string, object>
{
    ["keyManagementMode"] = KeyManagementMode.Envelope,
    ["envelopeKeyStore"] = azureVaultPlugin,
    ["kekKeyId"] = "azure-kek-user1"
});

// SCENARIO 2: User2 writes - uses their stored preferences (Direct + HashiCorp)
// Preferences resolved automatically from ISecurityContext.UserId
await aesPlugin.OnWriteAsync(data, contextUser2, args);  // No explicit override

// SCENARIO 3: User3 writes - no preferences stored, uses plugin defaults
await aesPlugin.OnWriteAsync(data, contextUser3, args);  // Falls back to defaults
```

**IKeyManagementConfigProvider Interface:**
```csharp
/// <summary>
/// Resolves key management configuration per-user/per-tenant.
/// Implement this to store user preferences in database, config files, etc.
/// </summary>
public interface IKeyManagementConfigProvider
{
    /// <summary>
    /// Get key management configuration for a user/tenant.
    /// Returns null if no preferences stored (use defaults).
    /// </summary>
    Task<KeyManagementConfig?> GetConfigAsync(ISecurityContext context);

    /// <summary>
    /// Save user preferences for key management.
    /// </summary>
    Task SaveConfigAsync(ISecurityContext context, KeyManagementConfig config);
}

/// <summary>
/// User-specific key management configuration.
/// </summary>
public record KeyManagementConfig
{
    /// <summary>Direct or Envelope mode</summary>
    public KeyManagementMode Mode { get; init; } = KeyManagementMode.Direct;

    /// <summary>Key store plugin ID or instance for Direct mode</summary>
    public string? KeyStorePluginId { get; init; }
    public IKeyStore? KeyStore { get; init; }

    /// <summary>Envelope key store for Envelope mode</summary>
    public string? EnvelopeKeyStorePluginId { get; init; }
    public IEnvelopeKeyStore? EnvelopeKeyStore { get; init; }

    /// <summary>KEK identifier for Envelope mode</summary>
    public string? KekKeyId { get; init; }

    /// <summary>Preferred encryption algorithm (for systems with multiple)</summary>
    public string? PreferredEncryptionPluginId { get; init; }
}
```

**Multi-Tenant Example:**
```csharp
// Single AES plugin instance serves ALL users
var aesPlugin = new AesEncryptionPlugin(new AesEncryptionConfig
{
    KeyManagementConfigProvider = new TenantConfigProvider(tenantDb)
});

// User1 (Tenant: FinanceCorp) - compliance requires HSM envelope encryption
// Their config in DB: { Mode: Envelope, EnvelopeKeyStorePluginId: "azure-hsm", KekKeyId: "finance-kek" }
await aesPlugin.OnWriteAsync(data, user1Context, args);  // → Envelope + Azure HSM

// User2 (Tenant: StartupXYZ) - cost-conscious, uses file-based keys
// Their config in DB: { Mode: Direct, KeyStorePluginId: "file-keystore" }
await aesPlugin.OnWriteAsync(data, user2Context, args);  // → Direct + FileKeyStore

// User3 (Tenant: GovAgency) - requires AWS GovCloud KMS
// Their config in DB: { Mode: Envelope, EnvelopeKeyStorePluginId: "aws-govcloud", KekKeyId: "gov-kek" }
await aesPlugin.OnWriteAsync(data, user3Context, args);  // → Envelope + AWS GovCloud

// All three users share the SAME plugin instance!
```

**Flexibility Matrix:**
| Configuration Level | When Resolved | Use Case |
|---------------------|---------------|----------|
| **Explicit args** | Per-operation | One-off overrides, testing, migration |
| **User preferences** | Per-user via ISecurityContext | Multi-tenant SaaS, compliance per customer |
| **Plugin defaults** | At construction | Single-tenant deployments, fallback |

**Example IKeyManagementConfigProvider Implementations:**
```csharp
// EXAMPLE 1: Database-backed provider for multi-tenant SaaS
public class DatabaseKeyManagementConfigProvider : IKeyManagementConfigProvider
{
    private readonly IDbContext _db;
    private readonly IKeyStoreRegistry _keyStoreRegistry;  // Resolves plugin IDs to instances

    public async Task<KeyManagementConfig?> GetConfigAsync(ISecurityContext context)
    {
        // Look up user/tenant preferences from database
        var record = await _db.KeyManagementConfigs
            .FirstOrDefaultAsync(c => c.TenantId == context.TenantId);

        if (record == null) return null;  // Use defaults

        return new KeyManagementConfig
        {
            Mode = record.Mode,
            KeyStorePluginId = record.KeyStorePluginId,
            KeyStore = _keyStoreRegistry.GetKeyStore(record.KeyStorePluginId),
            EnvelopeKeyStorePluginId = record.EnvelopeKeyStorePluginId,
            EnvelopeKeyStore = _keyStoreRegistry.GetEnvelopeKeyStore(record.EnvelopeKeyStorePluginId),
            KekKeyId = record.KekKeyId
        };
    }

    public async Task SaveConfigAsync(ISecurityContext context, KeyManagementConfig config)
    {
        // Save preferences to database
        var record = await _db.KeyManagementConfigs
            .FirstOrDefaultAsync(c => c.TenantId == context.TenantId);

        if (record == null)
        {
            record = new KeyManagementConfigRecord { TenantId = context.TenantId };
            _db.KeyManagementConfigs.Add(record);
        }

        record.Mode = config.Mode;
        record.KeyStorePluginId = config.KeyStorePluginId;
        record.EnvelopeKeyStorePluginId = config.EnvelopeKeyStorePluginId;
        record.KekKeyId = config.KekKeyId;

        await _db.SaveChangesAsync();
    }
}

// EXAMPLE 2: JSON config file provider for simpler deployments
public class JsonFileKeyManagementConfigProvider : IKeyManagementConfigProvider
{
    private readonly string _configPath;
    private readonly IKeyStoreRegistry _keyStoreRegistry;

    public async Task<KeyManagementConfig?> GetConfigAsync(ISecurityContext context)
    {
        var filePath = Path.Combine(_configPath, $"{context.UserId}.json");
        if (!File.Exists(filePath)) return null;

        var json = await File.ReadAllTextAsync(filePath);
        return JsonSerializer.Deserialize<KeyManagementConfig>(json);
    }

    public async Task SaveConfigAsync(ISecurityContext context, KeyManagementConfig config)
    {
        var filePath = Path.Combine(_configPath, $"{context.UserId}.json");
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(filePath, json);
    }
}

// EXAMPLE 3: In-memory provider for testing
public class InMemoryKeyManagementConfigProvider : IKeyManagementConfigProvider
{
    private readonly ConcurrentDictionary<string, KeyManagementConfig> _configs = new();

    public Task<KeyManagementConfig?> GetConfigAsync(ISecurityContext context)
    {
        _configs.TryGetValue(context.UserId, out var config);
        return Task.FromResult(config);
    }

    public Task SaveConfigAsync(ISecurityContext context, KeyManagementConfig config)
    {
        _configs[context.UserId] = config;
        return Task.CompletedTask;
    }
}
```

**IKeyStoreRegistry Interface (for resolving plugin IDs to instances):**
```csharp
/// <summary>
/// Registry for resolving key store plugin IDs to instances.
/// Used by IKeyManagementConfigProvider to resolve stored plugin IDs.
/// </summary>
public interface IKeyStoreRegistry
{
    void Register(string pluginId, IKeyStore keyStore);
    void RegisterEnvelope(string pluginId, IEnvelopeKeyStore envelopeKeyStore);
    IKeyStore? GetKeyStore(string? pluginId);
    IEnvelopeKeyStore? GetEnvelopeKeyStore(string? pluginId);
}
```

---

#### T5.0.4: Refactor Existing Key Management Plugins

> **CRITICAL:** All existing key management plugins MUST be refactored to extend `KeyStorePluginBase`.
> This eliminates duplicated code and ensures consistent behavior.

| Task | Plugin | Current | Target | Description | Status |
|------|--------|---------|--------|-------------|--------|
| T5.0.4 | Refactor key management plugins | - | - | Migrate to KeyStorePluginBase | [ ] |
| T5.0.4.1 | `FileKeyStorePlugin` | `SecurityProviderPluginBase` | `KeyStorePluginBase` | Remove duplicated caching/init, implement abstract storage methods | [ ] |
| T5.0.4.2 | `VaultKeyStorePlugin` | `SecurityProviderPluginBase` | `KeyStorePluginBase` + `IEnvelopeKeyStore` | Remove duplicated caching/init, implement abstract storage methods, add IEnvelopeKeyStore | [ ] |
| T5.0.4.3 | `KeyRotationPlugin` | Custom | `KeyStorePluginBase` (decorator) | Verify compatibility with base class pattern | [ ] |
| T5.0.4.4 | `SecretManagementPlugin` | Custom | Verify/Align | Ensure consistent with KeyStorePluginBase pattern | [ ] |

**FileKeyStorePlugin Refactoring:**
```csharp
// BEFORE: Duplicated logic
public sealed class FileKeyStorePlugin : SecurityProviderPluginBase, IKeyStore
{
    private readonly ConcurrentDictionary<string, CachedKey> _keyCache;  // ◄── DUPLICATED
    private readonly SemaphoreSlim _lock;                                 // ◄── DUPLICATED
    private bool _initialized;                                            // ◄── DUPLICATED
    // ... 200+ lines of duplicated infrastructure code ...
}

// AFTER: Focused implementation
public sealed class FileKeyStorePlugin : KeyStorePluginBase
{
    private readonly FileKeyStoreConfig _config;
    private readonly IKeyProtectionTier[] _tiers;

    // ONLY implement what's unique to file-based storage:
    protected override TimeSpan CacheExpiration => _config.CacheExpiration;
    protected override int KeySizeBytes => _config.KeySizeBytes;

    protected override async Task<byte[]?> LoadKeyFromStorageAsync(string keyId)
    {
        // File-specific: load from disk, decrypt with tier
    }

    protected override async Task SaveKeyToStorageAsync(string keyId, byte[] key)
    {
        // File-specific: encrypt with tier, save to disk
    }

    protected override async Task InitializeStorageAsync()
    {
        // File-specific: ensure directory exists, load metadata
    }
}
```

---

#### T5.0.5: Refactor Existing Encryption Plugins

> **CRITICAL:** All existing encryption plugins MUST be refactored to extend `EncryptionPluginBase`.
> This eliminates duplicated key management code and enables envelope mode support.

| Task | Plugin | Current | Target | Description | Status |
|------|--------|---------|--------|-------------|--------|
| T5.0.5 | Refactor encryption plugins | - | - | Migrate to EncryptionPluginBase | [ ] |
| T5.0.5.1 | `AesEncryptionPlugin` | `PipelinePluginBase` | `EncryptionPluginBase` | Remove duplicated key mgmt, implement abstract encrypt/decrypt | [ ] |
| T5.0.5.2 | `ChaCha20EncryptionPlugin` | `PipelinePluginBase` | `EncryptionPluginBase` | Remove duplicated key mgmt, implement abstract encrypt/decrypt | [ ] |
| T5.0.5.3 | `TwofishEncryptionPlugin` | `PipelinePluginBase` | `EncryptionPluginBase` | Remove duplicated key mgmt, implement abstract encrypt/decrypt | [ ] |
| T5.0.5.4 | `SerpentEncryptionPlugin` | `PipelinePluginBase` | `EncryptionPluginBase` | Remove duplicated key mgmt, implement abstract encrypt/decrypt | [ ] |
| T5.0.5.5 | `FipsEncryptionPlugin` | `PipelinePluginBase` | `EncryptionPluginBase` | Remove duplicated key mgmt, implement abstract encrypt/decrypt | [ ] |
| T5.0.5.6 | `ZeroKnowledgeEncryptionPlugin` | `PipelinePluginBase` | `EncryptionPluginBase` | Remove duplicated key mgmt, implement abstract encrypt/decrypt | [ ] |

**AesEncryptionPlugin Refactoring:**
```csharp
// BEFORE: Duplicated key management logic (~150 lines)
public sealed class AesEncryptionPlugin : PipelinePluginBase, IDisposable
{
    private IKeyStore? _keyStore;                                       // ◄── DUPLICATED
    private ISecurityContext? _securityContext;                         // ◄── DUPLICATED
    private readonly ConcurrentDictionary<string, DateTime> _keyAccessLog; // ◄── DUPLICATED
    private long _encryptionCount;                                      // ◄── DUPLICATED
    // ... 150+ lines of duplicated key management code ...

    private IKeyStore GetKeyStore(...) { /* DUPLICATED in all 6 plugins */ }
    private ISecurityContext GetSecurityContext(...) { /* DUPLICATED in all 6 plugins */ }
}

// AFTER: Focused AES implementation
public sealed class AesEncryptionPlugin : EncryptionPluginBase
{
    // ONLY implement what's unique to AES:
    protected override int KeySizeBytes => 32;  // AES-256
    protected override int IvSizeBytes => 12;   // GCM nonce
    protected override int TagSizeBytes => 16;  // GCM tag

    protected override async Task<Stream> EncryptCoreAsync(Stream input, byte[] key, byte[] iv, IKernelContext context)
    {
        // AES-specific: AesGcm.Encrypt()
    }

    protected override async Task<(Stream data, byte[] tag)> DecryptCoreAsync(Stream input, byte[] key, IKernelContext context)
    {
        // AES-specific: AesGcm.Decrypt()
    }

    protected override byte[] GenerateIv()
    {
        return RandomNumberGenerator.GetBytes(IvSizeBytes);
    }
}
```

---

#### T5.0.6: Requirements for New Plugins

> **MANDATORY:** All new encryption and key management plugins MUST follow these requirements.

**New Key Management Plugins (T5.4.1-T5.4.6) Requirements:**

| Requirement | Description |
|-------------|-------------|
| **MUST extend `KeyStorePluginBase`** | Do NOT implement `IKeyStore` directly |
| **MUST implement abstract methods** | `LoadKeyFromStorageAsync`, `SaveKeyToStorageAsync`, `InitializeStorageAsync` |
| **MAY implement `IEnvelopeKeyStore`** | If the backend supports HSM wrap/unwrap operations |
| **MUST use inherited caching** | Do NOT implement custom caching logic |
| **MUST use inherited validation** | Do NOT implement custom security context validation |

**New Encryption Plugins (T4.24-T4.29) Requirements:**

| Requirement | Description |
|-------------|-------------|
| **MUST extend `EncryptionPluginBase`** | Do NOT extend `PipelinePluginBase` directly |
| **MUST implement abstract methods** | `EncryptCoreAsync`, `DecryptCoreAsync`, `GenerateIv` |
| **MUST support both key modes** | Direct and Envelope modes via inherited infrastructure |
| **MUST use inherited key management** | Do NOT implement custom key store resolution |
| **MUST use inherited statistics** | Do NOT implement custom encryption/decryption counters |
| **Exception: Educational ciphers (T4.24)** | May extend `PipelinePluginBase` directly if no key management |

**Plugin Compliance Checklist:**
```
For each new KEY MANAGEMENT plugin:
  [ ] Extends KeyStorePluginBase (NOT SecurityProviderPluginBase directly)
  [ ] Implements LoadKeyFromStorageAsync()
  [ ] Implements SaveKeyToStorageAsync()
  [ ] Implements InitializeStorageAsync()
  [ ] Overrides CacheExpiration property
  [ ] Overrides KeySizeBytes property
  [ ] If HSM: Also implements IEnvelopeKeyStore
  [ ] Does NOT duplicate caching logic
  [ ] Does NOT duplicate initialization logic

For each new ENCRYPTION plugin:
  [ ] Extends EncryptionPluginBase (NOT PipelinePluginBase directly)
  [ ] Implements EncryptCoreAsync()
  [ ] Implements DecryptCoreAsync()
  [ ] Implements GenerateIv()
  [ ] Overrides KeySizeBytes, IvSizeBytes, TagSizeBytes
  [ ] Does NOT duplicate key management logic
  [ ] Does NOT duplicate statistics tracking
  [ ] Works with both KeyManagementMode.Direct and KeyManagementMode.Envelope
```

---

#### T5.0 Summary

| Task | Description | Dependencies | Status |
|------|-------------|--------------|--------|
| **T5.0** | **SDK Base Classes and Plugin Refactoring** | None | [ ] |
| T5.0.1 | SDK Key Management Types (enums, interfaces, classes incl. EncryptionConfigMode) | - | [ ] |
| T5.0.2 | `KeyStorePluginBase` abstract class | T5.0.1 | [ ] |
| T5.0.3 | `EncryptionPluginBase` abstract class | T5.0.1 | [ ] |
| T5.0.4 | Refactor existing key management plugins (4 plugins) | T5.0.2 | [ ] |
| T5.0.5 | Refactor existing encryption plugins (6 plugins) | T5.0.3 | [ ] |
| T5.0.6 | Document requirements for new plugins | T5.0.4, T5.0.5 | [ ] |

**Benefits of T5.0:**
- ✅ Eliminates ~500+ lines of duplicated code across plugins
- ✅ Ensures consistent caching, initialization, and validation
- ✅ Enables envelope mode support via shared infrastructure
- ✅ Simplifies new plugin development (implement only algorithm-specific logic)
- ✅ Guarantees composability: ANY encryption + ANY key management at runtime
- ✅ User-configurable key management mode (Direct vs Envelope)

---

### T5.1: Envelope Mode for ALL Encryption Plugins

> **DEPENDENCY:** T5.1 depends on T5.0 (SDK Base Classes). Complete T5.0 first.
>
> After T5.0 is complete, envelope mode support is **built into `EncryptionPluginBase`**.
> T5.1 tasks are now primarily about testing and documentation.

**What's Already Done:**
- ✅ HSM backends (HashiCorp, Azure, AWS) → `VaultKeyStorePlugin`
- ✅ WrapKey/UnwrapKey operations → `IVaultBackend` interface
- ✅ Key storage with versioning → `VaultKeyStorePlugin` + `KeyRotationPlugin`
- ✅ **All 6 encryption plugins use `IKeyStore`** → Already support composable key management

**After T5.0 Completion:**
- ✅ `KeyManagementMode` enum → T5.0.1.1
- ✅ `IEnvelopeKeyStore` interface → T5.0.1.2
- ✅ `EnvelopeHeader` helper class → T5.0.1.3
- ✅ Envelope mode in `EncryptionPluginBase` → T5.0.3.4
- ✅ All 6 encryption plugins refactored → T5.0.5

**Existing Encryption Plugins (ALL extend EncryptionPluginBase after T5.0):**
| Plugin | Algorithm | Base Class | Direct Mode | Envelope Mode |
|--------|-----------|------------|-------------|---------------|
| `AesEncryptionPlugin` | AES-256-GCM | `EncryptionPluginBase` | ✅ Inherited | ✅ Inherited |
| `ChaCha20EncryptionPlugin` | ChaCha20-Poly1305 | `EncryptionPluginBase` | ✅ Inherited | ✅ Inherited |
| `TwofishEncryptionPlugin` | Twofish-256-CTR-HMAC | `EncryptionPluginBase` | ✅ Inherited | ✅ Inherited |
| `SerpentEncryptionPlugin` | Serpent-256-CTR-HMAC | `EncryptionPluginBase` | ✅ Inherited | ✅ Inherited |
| `FipsEncryptionPlugin` | AES-256-GCM (FIPS 140-2) | `EncryptionPluginBase` | ✅ Inherited | ✅ Inherited |
| `ZeroKnowledgeEncryptionPlugin` | AES-256-GCM + ZK proofs | `EncryptionPluginBase` | ✅ Inherited | ✅ Inherited |

**Remaining T5.1 Tasks (Post-T5.0):**
| Task | Component | Description | Status |
|------|-----------|-------------|--------|
| T5.1.1 | Google Cloud KMS backend | Add to `VaultKeyStorePlugin` (config exists, impl partial) | [ ] |
| T5.1.2 | Envelope mode integration tests | Test all 6 plugins with envelope mode | [ ] |
| T5.1.3 | Envelope mode documentation | Usage examples, configuration guide | [ ] |
| T5.1.4 | Envelope mode benchmarks | Compare Direct vs Envelope performance | [ ] |

---

### T5.2-T5.3: Additional Encryption & Padding

| Task | Component | Description | Status |
|------|-----------|-------------|--------|
| T5.2 | `KyberEncryptionPlugin` | Post-quantum cryptography (NIST PQC ML-KEM), **MUST use IKeyStore** | [ ] |
| T5.3 | `ChaffPaddingPlugin` | Traffic analysis protection via dummy writes | [ ] |

---

### T5.4: Additional Key Management Plugins (Composable Architecture)

> **DEPENDENCY:** T5.4 depends on T5.0 (SDK Base Classes). Complete T5.0 first.
>
> **CRITICAL: All New Key Management Plugins MUST Extend `KeyStorePluginBase`**
>
> After T5.0 completion:
> - `KeyStorePluginBase` provides common caching, initialization, and validation
> - All new plugins extend `KeyStorePluginBase` (NOT `SecurityProviderPluginBase` directly)
> - Plugins that support HSM wrap/unwrap also implement `IEnvelopeKeyStore`
> - Users can pair ANY encryption with ANY key management at runtime

**Existing Key Management Plugins (✅ Extend KeyStorePluginBase after T5.0.4):**
| Plugin | Base Class | Envelope Support | Features | Status |
|--------|------------|------------------|----------|--------|
| `FileKeyStorePlugin` | `KeyStorePluginBase` | ❌ No | DPAPI, CredentialManager, Database, PBKDF2 4-tier | ✅ → Refactor T5.0.4.1 |
| `VaultKeyStorePlugin` | `KeyStorePluginBase` + `IEnvelopeKeyStore` | ✅ Yes | HashiCorp, Azure, AWS KMS + WrapKey/UnwrapKey | ✅ → Refactor T5.0.4.2 |
| `KeyRotationPlugin` | `KeyStorePluginBase` (decorator) | Passthrough | Wraps any IKeyStore, adds rotation/versioning/audit | ✅ → Refactor T5.0.4.3 |
| `SecretManagementPlugin` | `KeyStorePluginBase` | ❌ No | Secure secret storage with access control | ✅ → Verify T5.0.4.4 |

**New Key Management Plugins (T5.4) - MUST Extend KeyStorePluginBase:**

| Task | Component | Base Class | Description | Status |
|------|-----------|------------|-------------|--------|
| T5.4 | Additional key management plugins | - | More options for composable key management | [ ] |
| T5.4.1 | `ShamirSecretKeyStorePlugin` | `KeyStorePluginBase` | M-of-N key splitting (Shamir's Secret Sharing) | [ ] |
| T5.4.1.1 | ↳ Key split generation | - | Split key into N shares | [ ] |
| T5.4.1.2 | ↳ Key reconstruction | - | Reconstruct from M shares | [ ] |
| T5.4.1.3 | ↳ Share distribution | - | Securely distribute shares to custodians | [ ] |
| T5.4.1.4 | ↳ Share rotation | - | Rotate shares without changing key | [ ] |
| T5.4.2 | `Pkcs11KeyStorePlugin` | `KeyStorePluginBase` + `IEnvelopeKeyStore` | PKCS#11 HSM interface (generic HSM support) | [ ] |
| T5.4.2.1 | ↳ Token enumeration | - | List available PKCS#11 tokens | [ ] |
| T5.4.2.2 | ↳ Key operations | - | Generate, import, wrap, unwrap via PKCS#11 | [ ] |
| T5.4.3 | `TpmKeyStorePlugin` | `KeyStorePluginBase` | TPM 2.0 hardware security | [ ] |
| T5.4.3.1 | ↳ TPM key sealing | - | Seal keys to PCR state | [ ] |
| T5.4.3.2 | ↳ TPM key unsealing | - | Unseal with attestation | [ ] |
| T5.4.4 | `YubikeyKeyStorePlugin` | `KeyStorePluginBase` | YubiKey/FIDO2 hardware tokens | [ ] |
| T5.4.4.1 | ↳ PIV slot support | - | Use PIV slots for key storage | [ ] |
| T5.4.4.2 | ↳ Challenge-response | - | HMAC-SHA1 challenge-response | [ ] |
| T5.4.5 | `PasswordDerivedKeyStorePlugin` | `KeyStorePluginBase` | Argon2id/scrypt key derivation | [ ] |
| T5.4.5.1 | ↳ Argon2id derivation | - | Memory-hard KDF | [ ] |
| T5.4.5.2 | ↳ scrypt derivation | - | Alternative memory-hard KDF | [ ] |
| T5.4.6 | `MultiPartyKeyStorePlugin` | `KeyStorePluginBase` + `IEnvelopeKeyStore` | Multi-party computation (MPC) key management | [ ] |
| T5.4.6.1 | ↳ Threshold signatures | - | t-of-n signing without key reconstruction | [ ] |
| T5.4.6.2 | ↳ Distributed key generation | - | Generate keys without single point of failure | [ ] |

**Key Management Plugin Reference:**
| Plugin | Base Class | IEnvelopeKeyStore | Security Level | Use Case |
|--------|------------|-------------------|----------------|----------|
| `FileKeyStorePlugin` | `KeyStorePluginBase` | ❌ | Medium | Local/development deployments |
| `VaultKeyStorePlugin` | `KeyStorePluginBase` | ✅ Yes | High | Enterprise HSM/cloud deployments |
| `KeyRotationPlugin` | `KeyStorePluginBase` | Passthrough | N/A (layer) | Add rotation to any IKeyStore |
| `ShamirSecretKeyStorePlugin` | `KeyStorePluginBase` | ❌ | Very High | M-of-N custodian scenarios |
| `Pkcs11KeyStorePlugin` | `KeyStorePluginBase` | ✅ Yes | Very High | Generic HSM hardware |
| `TpmKeyStorePlugin` | `KeyStorePluginBase` | ❌ | High | Hardware-bound keys |
| `YubikeyKeyStorePlugin` | `KeyStorePluginBase` | ❌ | High | User-owned hardware tokens |
| `PasswordDerivedKeyStorePlugin` | `KeyStorePluginBase` | ❌ | Medium-High | Password-based encryption |
| `MultiPartyKeyStorePlugin` | `KeyStorePluginBase` | ✅ Yes | Maximum | Zero single-point-of-failure |

**Composability Example:**
```csharp
// ANY encryption can use ANY key management - user choice at runtime
var encryption = new AesEncryptionPlugin(new AesEncryptionConfig
{
    KeyStore = keyManagementChoice switch
    {
        "file" => new FileKeyStorePlugin(),
        "hsm-hashicorp" => new VaultKeyStorePlugin(hashicorpConfig),
        "hsm-azure" => new VaultKeyStorePlugin(azureConfig),
        "hsm-aws" => new VaultKeyStorePlugin(awsConfig),
        "shamir" => new ShamirSecretKeyStorePlugin(shamirConfig),
        "pkcs11" => new Pkcs11KeyStorePlugin(pkcs11Config),
        "tpm" => new TpmKeyStorePlugin(),
        "yubikey" => new YubikeyKeyStorePlugin(),
        "password" => new PasswordDerivedKeyStorePlugin(passwordConfig),
        "mpc" => new MultiPartyKeyStorePlugin(mpcConfig),
        "rotation" => new KeyRotationPlugin(innerKeyStore),  // Wrap any of the above
        _ => throw new ArgumentException("Unknown key management")
    }
});
```

---

### T5.5-T5.6: Geo-Distribution

| Task | Component | Description | Status |
|------|-----------|-------------|--------|
| T5.5 | `GeoWormPlugin` | Geo-dispersed WORM replication across regions | [ ] |
| T5.6 | `GeoDistributedShardingPlugin` | Geo-dispersed data sharding (shards across continents) | [ ] |

**Configuration (Same Plugin, Different Key Sources):**
```csharp
// MODE 1: DIRECT - Key from any IKeyStore (existing behavior, DEFAULT)
// Works with ALL 6 encryption plugins today
var directConfig = new AesEncryptionConfig  // Or ChaCha20, Twofish, Serpent, FIPS, ZK
{
    KeyManagementMode = KeyManagementMode.Direct,  // Default
    KeyStore = new FileKeyStorePlugin()            // Or ANY IKeyStore
    // OR: KeyStore = new VaultKeyStorePlugin(...)  // Even HSM, but no envelope
    // OR: KeyStore = new KeyRotationPlugin(...)    // With rotation layer
};

// MODE 2: ENVELOPE - DEK wrapped by HSM, stored in ciphertext (T5.1)
// After T5.1 implementation, works with ALL 6 encryption plugins
var envelopeConfig = new AesEncryptionConfig  // Or ChaCha20, Twofish, Serpent, FIPS, ZK
{
    KeyManagementMode = KeyManagementMode.Envelope,
    EnvelopeKeyStore = new VaultKeyStorePlugin(new VaultConfig
    {
        // Pick ONE HSM backend:
        HashiCorpVault = new HashiCorpVaultConfig { Address = "https://vault:8200", Token = "..." },
        // OR: AzureKeyVault = new AzureKeyVaultConfig { VaultUrl = "https://myvault.vault.azure.net" },
        // OR: AwsKms = new AwsKmsConfig { Region = "us-east-1", DefaultKeyId = "alias/my-kek" }
    }),
    KekKeyId = "alias/my-kek"  // Which KEK to use for wrapping DEKs
};

// Both modes use the SAME encryption plugins - no code duplication!
var plugin = new AesEncryptionPlugin(envelopeConfig);
```

**Envelope Mode Header Format (Shared by All Plugins):**
```
DIRECT MODE (existing):     [KeyIdLength:4][KeyId:var][...plugin-specific...][Ciphertext]
ENVELOPE MODE (T5.1):       [Mode:1][WrappedDekLen:2][WrappedDEK:var][KekIdLen:1][KekId:var][...plugin-specific...][Ciphertext]
```

**Key Management Mode Comparison:**
| Mode | Key Provider | Key Source | Security | Use Case |
|------|--------------|------------|----------|----------|
| `Direct` | Any `IKeyStore` | Key retrieved directly | High | General use |
| `Envelope` | `VaultKeyStorePlugin` | DEK wrapped by HSM KEK | Maximum | Gov/military compliance |

**Envelope Mode Benefits:**
- KEK never leaves HSM hardware - impossible to extract
- DEK is unique per object - limits blast radius
- Key rotation = re-wrap DEKs, NOT re-encrypt data
- Compliance: PCI-DSS, HIPAA, FedRAMP
- **Works with ALL 6 encryption algorithms** (after T5.1 implementation)

**Goal:** Maximum compression for archival/cold storage

| Task | Component | Description | Status |
|------|-----------|-------------|--------|
| T5.7 | `PaqCompressionPlugin` | PAQ8/PAQ9 extreme compression (slow but best ratio) | [ ] |
| T5.8 | `ZpaqCompressionPlugin` | ZPAQ journaling archiver with deduplication | [ ] |
| T5.9 | `CmixCompressionPlugin` | CMIX context-mixing compressor (experimental) | [ ] |

**Goal:** Database integration and metadata handling

| Task | Component | Description | Status |
|------|-----------|-------------|--------|
| T5.10 | `SqlTdeMetadataPlugin` | SQL TDE (Transparent Data Encryption) metadata import/export | [ ] |
| T5.11 | `DatabaseEncryptionKeyPlugin` | DEK/KEK management for imported encrypted databases | [ ] |

**Goal:** Audit-ready documentation and compliance

| Task | Component | Description | Status |
|------|-----------|-------------|--------|
| T5.12 | Compliance Report Generator | SOC2, HIPAA, FedRAMP, GDPR reports | [ ] |
| T5.13 | Chain-of-Custody Export | PDF/JSON export for legal discovery | [ ] |
| T5.14 | Dashboard Integration | Real-time integrity status widgets | [ ] |
| T5.15 | Alert Integrations | Email, Slack, PagerDuty, OpsGenie | [ ] |
| T5.16 | Tamper Incident Workflow | Automated incident ticket creation | [ ] |

#### Phase T6: Testing & Documentation (Priority: HIGH)
| Task | Description | Dependencies | Status |
|------|-------------|--------------|--------|
| T6.1 | Unit tests for integrity provider | T1.2 | [ ] |
| T6.2 | Unit tests for blockchain provider | T1.4 | [ ] |
| T6.3 | Unit tests for WORM provider | T1.6 | [ ] |
| T6.4 | Unit tests for access log provider | T1.8 | [ ] |
| T6.5 | Integration tests for write pipeline | T2.8 | [ ] |
| T6.6 | Integration tests for read pipeline | T3.6 | [ ] |
| T6.7 | Integration tests for tamper detection + attribution | T3.9 | [ ] |
| T6.8 | Integration tests for recovery scenarios | T4.4 | [ ] |
| T6.9 | Integration tests for correction workflow | T4.5 | [ ] |
| T6.10 | Integration tests for degradation state transitions | T4.8 | [ ] |
| T6.11 | Integration tests for hardware WORM providers | T4.11 | [ ] |
| T6.12 | Performance benchmarks | T4.* | [ ] |
| T6.13 | XML documentation for all public APIs | T4.* | [ ] |
| T6.14 | Update CLAUDE.md with tamper-proof documentation | T6.13 | [ ] |

---

### File Structure

```
DataWarehouse.SDK/
├── Contracts/
│   ├── TamperProof/
│   │   ├── ITamperProofProvider.cs
│   │   ├── IIntegrityProvider.cs
│   │   ├── IBlockchainProvider.cs
│   │   ├── IWormStorageProvider.cs
│   │   ├── IAccessLogProvider.cs
│   │   ├── TamperProofConfiguration.cs
│   │   ├── TamperProofManifest.cs
│   │   ├── WriteContext.cs
│   │   ├── AccessLogEntry.cs
│   │   ├── TamperIncidentReport.cs
│   │   └── TamperProofEnums.cs
│   └── PluginBases/
│       ├── TamperProofProviderPluginBase.cs
│       ├── IntegrityProviderPluginBase.cs
│       ├── BlockchainProviderPluginBase.cs
│       ├── WormStorageProviderPluginBase.cs
│       └── AccessLogProviderPluginBase.cs

Plugins/
├── DataWarehouse.Plugins.TamperProof/
│   ├── TamperProofPlugin.cs (main orchestrator)
│   ├── Pipeline/
│   │   ├── WritePhase1Handler.cs
│   │   ├── WritePhase2Handler.cs
│   │   ├── WritePhase3Handler.cs
│   │   ├── WritePhase4Handler.cs
│   │   ├── WritePhase5Handler.cs
│   │   ├── ReadPhase1Handler.cs
│   │   ├── ReadPhase2Handler.cs
│   │   ├── ReadPhase3Handler.cs
│   │   ├── ReadPhase4Handler.cs
│   │   └── ReadPhase5Handler.cs
│   ├── Attribution/
│   │   └── TamperAttributionAnalyzer.cs
│   └── DataWarehouse.Plugins.TamperProof.csproj
│
│   # Integrity Providers (T1, T4.16-T4.20)
├── DataWarehouse.Plugins.Integrity/
│   ├── DefaultIntegrityPlugin.cs (SHA-256/384/512, Blake3)
│   └── DataWarehouse.Plugins.Integrity.csproj
├── DataWarehouse.Plugins.Integrity.Sha3/
│   ├── Sha3IntegrityPlugin.cs (SHA3-256/384/512 - NIST standard)
│   └── DataWarehouse.Plugins.Integrity.Sha3.csproj
├── DataWarehouse.Plugins.Integrity.Keccak/
│   ├── KeccakIntegrityPlugin.cs (Keccak-256/384/512 - original pre-NIST)
│   └── DataWarehouse.Plugins.Integrity.Keccak.csproj
├── DataWarehouse.Plugins.Integrity.Hmac/
│   ├── HmacIntegrityPlugin.cs (HMAC-SHA256/384/512, HMAC-SHA3-256/384/512)
│   └── DataWarehouse.Plugins.Integrity.Hmac.csproj
├── DataWarehouse.Plugins.Integrity.Salted/
│   ├── SaltedHashIntegrityPlugin.cs (Salted-SHA256/512, Salted-SHA3, Salted-Blake3)
│   └── DataWarehouse.Plugins.Integrity.Salted.csproj
├── DataWarehouse.Plugins.Integrity.SaltedHmac/
│   ├── SaltedHmacIntegrityPlugin.cs (Salted-HMAC-SHA256/512, Salted-HMAC-SHA3)
│   └── DataWarehouse.Plugins.Integrity.SaltedHmac.csproj
│
│   # Blockchain Providers (T1, T4.9)
├── DataWarehouse.Plugins.Blockchain.Local/
│   ├── LocalBlockchainPlugin.cs
│   └── DataWarehouse.Plugins.Blockchain.Local.csproj
├── DataWarehouse.Plugins.Blockchain.Raft/
│   ├── RaftBlockchainPlugin.cs (consensus mode)
│   └── DataWarehouse.Plugins.Blockchain.Raft.csproj
│
│   # WORM Providers (T1, T4.10-T4.11, T5.5)
├── DataWarehouse.Plugins.Worm.Software/
│   ├── SoftwareWormPlugin.cs
│   └── DataWarehouse.Plugins.Worm.Software.csproj
├── DataWarehouse.Plugins.Worm.S3ObjectLock/
│   ├── S3ObjectLockWormPlugin.cs (AWS hardware WORM)
│   └── DataWarehouse.Plugins.Worm.S3ObjectLock.csproj
├── DataWarehouse.Plugins.Worm.AzureImmutable/
│   ├── AzureImmutableWormPlugin.cs (Azure hardware WORM)
│   └── DataWarehouse.Plugins.Worm.AzureImmutable.csproj
├── DataWarehouse.Plugins.Worm.GeoDispersed/
│   ├── GeoWormPlugin.cs (multi-region WORM)
│   └── DataWarehouse.Plugins.Worm.GeoDispersed.csproj
│
│   # Access Logging (T1)
├── DataWarehouse.Plugins.AccessLog/
│   ├── DefaultAccessLogPlugin.cs
│   └── DataWarehouse.Plugins.AccessLog.csproj
│
│   # Envelope Key Management - HSM Providers (T5.1.4-T5.1.7)
│   # Note: These ADD envelope mode to existing AesEncryptionPlugin - no separate encryption plugin needed
├── DataWarehouse.SDK/Security/
│   ├── IKeyProvider.cs (Direct vs Envelope key management abstraction)
│   ├── DirectKeyProvider.cs (existing behavior - key from IKeyStore)
│   ├── EnvelopeKeyProvider.cs (DEK + HSM wrapping)
│   └── IHsmProvider.cs (HSM abstraction interface)
├── DataWarehouse.Plugins.Hsm.AwsKms/
│   ├── AwsKmsHsmProvider.cs (AWS KMS integration)
│   └── DataWarehouse.Plugins.Hsm.AwsKms.csproj
├── DataWarehouse.Plugins.Hsm.AzureKeyVault/
│   ├── AzureKeyVaultHsmProvider.cs (Azure Key Vault integration)
│   └── DataWarehouse.Plugins.Hsm.AzureKeyVault.csproj
├── DataWarehouse.Plugins.Hsm.HashiCorpVault/
│   ├── HashiCorpVaultHsmProvider.cs (HashiCorp Vault integration)
│   └── DataWarehouse.Plugins.Hsm.HashiCorpVault.csproj
│
│   # Post-Quantum Encryption (T5.2)
├── DataWarehouse.Plugins.Encryption.Kyber/
│   ├── KyberEncryptionPlugin.cs (post-quantum NIST PQC ML-KEM)
│   └── DataWarehouse.Plugins.Encryption.Kyber.csproj
│
│   # Additional Compression Algorithms (T4.21-T4.23)
├── DataWarehouse.Plugins.Compression.Rle/
│   ├── RleCompressionPlugin.cs (Run-Length Encoding)
│   └── DataWarehouse.Plugins.Compression.Rle.csproj
├── DataWarehouse.Plugins.Compression.Huffman/
│   ├── HuffmanCompressionPlugin.cs (Huffman coding)
│   └── DataWarehouse.Plugins.Compression.Huffman.csproj
├── DataWarehouse.Plugins.Compression.Lzw/
│   ├── LzwCompressionPlugin.cs (Lempel-Ziv-Welch)
│   └── DataWarehouse.Plugins.Compression.Lzw.csproj
├── DataWarehouse.Plugins.Compression.Bzip2/
│   ├── Bzip2CompressionPlugin.cs (Burrows-Wheeler + Huffman)
│   └── DataWarehouse.Plugins.Compression.Bzip2.csproj
├── DataWarehouse.Plugins.Compression.Lzma/
│   ├── LzmaCompressionPlugin.cs (LZMA/LZMA2, 7-Zip)
│   └── DataWarehouse.Plugins.Compression.Lzma.csproj
├── DataWarehouse.Plugins.Compression.Snappy/
│   ├── SnappyCompressionPlugin.cs (Google, speed-optimized)
│   └── DataWarehouse.Plugins.Compression.Snappy.csproj
├── DataWarehouse.Plugins.Compression.Ppm/
│   ├── PpmCompressionPlugin.cs (Prediction by Partial Matching)
│   └── DataWarehouse.Plugins.Compression.Ppm.csproj
├── DataWarehouse.Plugins.Compression.Nncp/
│   ├── NncpCompressionPlugin.cs (Neural Network Compression)
│   └── DataWarehouse.Plugins.Compression.Nncp.csproj
├── DataWarehouse.Plugins.Compression.Schumacher/
│   ├── SchumacherCompressionPlugin.cs (Schumacher algorithm)
│   └── DataWarehouse.Plugins.Compression.Schumacher.csproj
│
│   # Additional Encryption Algorithms (T4.24-T4.29)
├── DataWarehouse.Plugins.Encryption.Educational/
│   ├── CaesarCipherPlugin.cs (Caesar/ROT13, educational)
│   ├── XorCipherPlugin.cs (XOR cipher, educational)
│   ├── VigenereCipherPlugin.cs (Vigenère cipher, educational)
│   └── DataWarehouse.Plugins.Encryption.Educational.csproj
├── DataWarehouse.Plugins.Encryption.Legacy/
│   ├── DesEncryptionPlugin.cs (DES, legacy compatibility)
│   ├── TripleDesEncryptionPlugin.cs (3DES, legacy)
│   ├── Rc4EncryptionPlugin.cs (RC4/WEP, legacy)
│   ├── BlowfishEncryptionPlugin.cs (Blowfish, legacy)
│   └── DataWarehouse.Plugins.Encryption.Legacy.csproj
├── DataWarehouse.Plugins.Encryption.AesVariants/
│   ├── Aes128GcmPlugin.cs (AES-128-GCM)
│   ├── Aes192GcmPlugin.cs (AES-192-GCM)
│   ├── Aes256CbcPlugin.cs (AES-256-CBC)
│   ├── Aes256CtrPlugin.cs (AES-256-CTR)
│   ├── AesNiDetector.cs (Hardware acceleration)
│   └── DataWarehouse.Plugins.Encryption.AesVariants.csproj
├── DataWarehouse.Plugins.Encryption.Asymmetric/
│   ├── Rsa2048Plugin.cs (RSA-2048)
│   ├── Rsa4096Plugin.cs (RSA-4096)
│   ├── EcdhPlugin.cs (Elliptic Curve Diffie-Hellman)
│   └── DataWarehouse.Plugins.Encryption.Asymmetric.csproj
├── DataWarehouse.Plugins.Encryption.PostQuantum/
│   ├── MlKemPlugin.cs (ML-KEM/Kyber)
│   ├── MlDsaPlugin.cs (ML-DSA/Dilithium)
│   └── DataWarehouse.Plugins.Encryption.PostQuantum.csproj
├── DataWarehouse.Plugins.Encryption.Special/
│   ├── OneTimePadPlugin.cs (OTP, perfect secrecy)
│   ├── XtsAesPlugin.cs (XTS-AES disk encryption)
│   └── DataWarehouse.Plugins.Encryption.Special.csproj
│
│   # Ultra Paranoid Padding/Obfuscation (T5.3)
├── DataWarehouse.Plugins.Padding.Chaff/
│   ├── ChaffPaddingPlugin.cs (traffic analysis protection)
│   └── DataWarehouse.Plugins.Padding.Chaff.csproj
│
│   # Ultra Paranoid Key Management (T5.4)
├── DataWarehouse.Plugins.KeyManagement.Shamir/
│   ├── ShamirSecretPlugin.cs (M-of-N key splitting)
│   └── DataWarehouse.Plugins.KeyManagement.Shamir.csproj
│
│   # Geo-Dispersed Distribution (T5.6)
├── DataWarehouse.Plugins.GeoDistributedSharding/
│   ├── GeoDistributedShardingPlugin.cs (shards across continents)
│   └── DataWarehouse.Plugins.GeoDistributedSharding.csproj
│
│   # Ultra Paranoid Compression (T5.7-T5.9) - Extreme ratios
├── DataWarehouse.Plugins.Compression.Paq/
│   ├── PaqCompressionPlugin.cs (PAQ8/PAQ9 extreme compression)
│   └── DataWarehouse.Plugins.Compression.Paq.csproj
├── DataWarehouse.Plugins.Compression.Zpaq/
│   ├── ZpaqCompressionPlugin.cs (ZPAQ journaling archiver)
│   └── DataWarehouse.Plugins.Compression.Zpaq.csproj
├── DataWarehouse.Plugins.Compression.Cmix/
│   ├── CmixCompressionPlugin.cs (context-mixing compressor)
│   └── DataWarehouse.Plugins.Compression.Cmix.csproj
│
│   # Database Encryption Integration (T5.10-T5.11)
├── DataWarehouse.Plugins.DatabaseEncryption.Tde/
│   ├── SqlTdeMetadataPlugin.cs (SQL Server/Oracle/PostgreSQL TDE)
│   └── DataWarehouse.Plugins.DatabaseEncryption.Tde.csproj
└── DataWarehouse.Plugins.DatabaseEncryption.KeyManagement/
    ├── DatabaseEncryptionKeyPlugin.cs (DEK/KEK management)
    └── DataWarehouse.Plugins.DatabaseEncryption.KeyManagement.csproj
```

---

### Security Level Quick Reference

| Level | Integrity | Blockchain | WORM | Encryption | Padding | Use Case |
|-------|-----------|------------|------|------------|---------|----------|
| `Minimal` | SHA-256 | None | None | None | None | Development/testing only |
| `Basic` | SHA-256 | Local (file) | Software | AES-256 | None | Small business, non-regulated |
| `Standard` | SHA-384 | Local (file) | Software | AES-256-GCM | Content | General enterprise |
| `Enhanced` | SHA-512 | Raft consensus | Software | AES-256-GCM | Content+Shard | Regulated industries |
| `High` | Blake3 | Raft + external anchor | Hardware (S3/Azure) | Envelope | Full | Financial, healthcare |
| `Maximum` | Blake3+HMAC | Raft + public blockchain | Hardware + geo-replicated | Kyber+AES | Full+Chaff | Government, military |

---

### Key Design Decisions

#### 1. Blockchain Batching Strategy
- **Single-object mode:** Immediate anchor after each write (highest integrity, higher latency)
- **Batch mode:** Collect N objects or wait T seconds, then anchor with Merkle root (better throughput)
- **Raft consensus mode:** Anchor must be confirmed by majority of nodes before success
- **External anchor mode:** Periodically anchor Merkle root to public blockchain (Bitcoin, Ethereum)

#### 2. WORM Implementation Choices

**WORM Wraps ANY Storage Provider:**
The WORM layer is a wrapper that can be applied to any `IStorageProvider`. This allows users to choose their preferred storage backend while adding immutability guarantees on top.

```
┌─────────────────────────────────────────────┐
│           WORM Wrapper Layer                │
│  (Software immutability OR Hardware detect) │
├─────────────────────────────────────────────┤
│         ANY IStorageProvider                │
│  (S3, Azure, Local, MinIO, GCS, etc.)       │
└─────────────────────────────────────────────┘
```

| Type | Provider | Bypass Possible | Use Case |
|------|----------|-----------------|----------|
| Software | `SoftwareWormPlugin` | Admin override (logged) | Development, testing, small deployments |
| S3 Object Lock | `S3ObjectLockWormPlugin` | Not possible (AWS enforced) | AWS-hosted production |
| Azure Immutable | `AzureImmutableBlobWormPlugin` | Not possible (Azure enforced) | Azure-hosted production |
| Geo-WORM | `GeoWormPlugin` | Requires multiple region compromise | Maximum security |

#### 2b. Blockchain Consensus Modes

| Mode | Writers | Consensus | Latency | Use Case |
|------|---------|-----------|---------|----------|
| `SingleWriter` | 1 | None (local file) | Lowest | Single-node, development, small deployments |
| `RaftConsensus` | N (odd) | Majority required | Medium | Multi-node production, HA requirements |
| `ExternalAnchor` | N/A | Public blockchain | Highest | Maximum auditability, legal requirements |

#### 3. Correction Flow (Append-Only)
```
Original Data (v1) → Hash₁ → Blockchain₁
                              │
Correction Request ──────────►│
                              ▼
New Data (v2) → Hash₂ → Blockchain₂ (includes reference to v1)
                              │
                              ▼
v1 marked as "superseded" but NEVER deleted
Both v1 and v2 remain in WORM forever
```

#### 4. Transactional Write Strategy with WORM Orphan Handling
```
BEGIN TRANSACTION
  ├─ Write to Data Instance (shards)
  ├─ Write to Metadata Instance (manifest)
  ├─ Write to WORM Instance (full blob) ← Cannot rollback!
  └─ Queue blockchain anchor

IF ANY STEP FAILS:
  ├─ Rollback Data Instance
  ├─ Rollback Metadata Instance
  ├─ WORM: Mark as orphan (cannot delete)
  │   └─ Orphan record: { guid, timestamp, reason: "tx_rollback" }
  └─ Cancel blockchain anchor

Orphan cleanup: Background process can mark orphans for eventual compliance expiry
```

---

### Storage Tier Configuration

Users configure storage instances independently. Each instance can use any compatible storage provider:

| Instance | Purpose | Required | Example Configurations |
|----------|---------|----------|------------------------|
| `data` | Live RAID shards | Yes | LocalStorage, S3, Azure Blob, MinIO |
| `metadata` | Manifests, indexes | Yes | Same as data, or dedicated fast storage |
| `worm` | Immutable backup | Recommended | S3 Object Lock, Azure Immutable, Software WORM |
| `blockchain` | Anchor records | Recommended | Local file, Raft cluster, external chain |

**Configuration Example:**
```csharp
var config = new TamperProofConfiguration
{
    // Storage instance configuration (user chooses ANY provider)
    StorageInstances = new Dictionary<string, string>
    {
        ["data"] = "s3://my-bucket/data/",
        ["metadata"] = "s3://my-bucket/metadata/",
        ["worm"] = "s3://my-worm-bucket/?objectLock=true",  // WORM wraps S3
        ["blockchain"] = "local://./blockchain/"
    },

    // Core settings (locked after seal)
    SecurityLevel = SecurityLevel.Enhanced,
    RaidConfiguration = RaidConfiguration.Raid6(dataShards: 8, parityShards: 2),
    HashAlgorithm = HashAlgorithmType.SHA512,

    // Blockchain consensus mode
    BlockchainMode = BlockchainMode.RaftConsensus,  // or SingleWriter, ExternalAnchor
    BlockchainBatchSize = 100,        // Batch N objects before anchoring
    BlockchainBatchTimeout = TimeSpan.FromSeconds(30),  // Or anchor after T seconds

    // WORM configuration
    WormMode = WormMode.HardwareIntegrated,  // or SoftwareEnforced
    WormProvider = "s3-object-lock",  // Wraps any IStorageProvider

    // Padding configuration (user-configurable)
    ContentPaddingMode = ContentPaddingMode.SecureRandom,  // or None, Chaff, FixedSize
    ContentPaddingBlockSize = 4096,   // For FixedSize mode
    ShardPaddingMode = ShardPaddingMode.UniformSize,  // or None, FixedBlock

    // Recovery behavior (configurable always, even after seal)
    RecoveryBehavior = TamperRecoveryBehavior.AutoRecoverWithReport,
    DefaultReadMode = ReadMode.Verified,  // Fast, Verified, Audit

    // Transactional write settings
    WriteTransactionTimeout = TimeSpan.FromMinutes(5),
    WriteRetryCount = 3
};
```

---

### Content Padding vs Shard Padding

| Type | When Applied | Covered by Hash | Purpose |
|------|--------------|-----------------|---------|
| **Content Padding** | Phase 1 (user transformations) | Yes | Hides true data size from observers |
| **Shard Padding** | Phase 3 (RAID distribution) | No (shard-level only) | Uniform shard sizes for RAID efficiency |

**Content Padding Modes (User-Configurable):**
| Mode | Description | Security Level |
|------|-------------|----------------|
| `None` | No padding applied | Low (size visible) |
| `SecureRandom` | Cryptographically secure random bytes | High |
| `Chaff` | Plausible-looking dummy data (structured noise) | Maximum (traffic analysis resistant) |
| `FixedSize` | Pad to fixed block boundary (4KB, 64KB, etc.) | Medium |

**Shard Padding Modes:**
| Mode | Description | Storage Overhead |
|------|-------------|------------------|
| `None` | Variable shard sizes (last shard smaller) | Minimal |
| `UniformSize` | Pad final shard to match largest | Low |
| `FixedBlock` | Pad all shards to configured boundary | Configurable |

**Content Padding:** User-configurable, applied before integrity hash. Adds random bytes to obscure actual data length. Useful when data size itself is sensitive information.

**Shard Padding:** System-applied, after integrity hash. Pads final shard to match others for uniform RAID stripe size. Not security-relevant, purely for storage efficiency.

---

### Tamper Attribution

When tampering is detected, the system analyzes access logs to attribute responsibility:

| Scenario | Attribution Confidence | Evidence |
|----------|------------------------|----------|
| Single accessor in window | High | Only one principal touched the data |
| Multiple accessors | Medium | List of suspects provided |
| No logged access | External/Physical | Indicates bypass of normal access paths |
| Access log also tampered | Very Low | Sophisticated attack, forensic investigation needed |

Attribution data is included in `TamperIncidentReport` for compliance and incident response.

---

### Mandatory Write Comments

Like git commits, every write operation requires metadata:

```csharp
var context = new WriteContext
{
    Author = "john.doe@company.com",      // Required: Principal performing write
    Comment = "Q4 2025 financial report", // Required: Human-readable description
    SessionId = Guid.NewGuid(),           // Optional: Correlate related writes
    ClientIp = "192.168.1.100",           // Auto-captured: Client IP address
    Timestamp = DateTimeOffset.UtcNow     // Auto-set: UTC timestamp
};

await tamperProof.SecureWriteAsync(objectId, data, context, cancellationToken);
```

This creates a complete audit trail for every change, enabling compliance reporting and forensic investigation.

---

### Security Considerations

1. **Write Comment Validation**: All write operations MUST include non-empty `Author` and `Comment` fields. Empty or whitespace-only values are rejected.

2. **Access Logging**: Every operation (read, write, correct, admin) is logged with principal, timestamp, client IP, and session ID for attribution.

3. **Tamper Attribution Analysis**:
   - Compare tampering detection time with access log history
   - Look for write operations in time window before detection
   - If only one principal accessed during window → high confidence attribution
   - If multiple principals → list all as suspects
   - If no logged access → indicates external/physical tampering

4. **Seal Mechanism**: Structural configuration (storage instances, RAID config, hash algorithm) becomes immutable after first write. Only behavioral settings (recovery behavior, read mode) can change.

5. **WORM Integrity**: WORM storage is the ultimate source of truth. Software WORM can be bypassed by admins (logged); hardware WORM (S3 Object Lock) cannot.

6. **Blockchain Immutability**: Each anchor includes previous block hash, creating tamper-evident chain. Batch anchoring uses Merkle trees for efficiency.

---

*Section added: 2026-01-29*
*Author: Claude AI*

---

*Document updated: 2026-01-25*
*Next review: 2026-02-15*
