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

### Interface Definitions

#### IIntegrityProvider
```csharp
namespace DataWarehouse.SDK.Contracts;

/// <summary>
/// Provides cryptographic integrity operations for tamper-proof storage.
/// </summary>
public interface IIntegrityProvider
{
    /// <summary>Supported hash algorithms (SHA256, SHA384, SHA512, Blake3).</summary>
    IReadOnlyList<HashAlgorithmType> SupportedAlgorithms { get; }

    /// <summary>Computes integrity hash using specified algorithm.</summary>
    Task<IntegrityHash> ComputeHashAsync(Stream data, HashAlgorithmType algorithm, CancellationToken ct = default);

    /// <summary>Verifies data matches expected hash.</summary>
    Task<IntegrityVerificationResult> VerifyAsync(Stream data, IntegrityHash expectedHash, CancellationToken ct = default);

    /// <summary>Computes hash for a single shard with shard-specific metadata.</summary>
    Task<ShardHash> ComputeShardHashAsync(byte[] shardData, int shardIndex, Guid objectId, CancellationToken ct = default);
}
```

#### IBlockchainProvider
```csharp
namespace DataWarehouse.SDK.Contracts;

/// <summary>
/// Provides blockchain-based immutable audit trail for tamper-proof storage.
/// </summary>
public interface IBlockchainProvider
{
    /// <summary>Anchors a single object's integrity proof to the blockchain.</summary>
    Task<BlockchainAnchor> AnchorAsync(AnchorRequest request, CancellationToken ct = default);

    /// <summary>Anchors multiple objects in a single Merkle-tree batch for efficiency.</summary>
    Task<BatchAnchorResult> AnchorBatchAsync(IEnumerable<AnchorRequest> requests, CancellationToken ct = default);

    /// <summary>Verifies an object's blockchain anchor is valid and unchanged.</summary>
    Task<AnchorVerificationResult> VerifyAnchorAsync(Guid objectId, IntegrityHash expectedHash, CancellationToken ct = default);

    /// <summary>Retrieves full audit chain for an object (all versions, corrections).</summary>
    Task<AuditChain> GetAuditChainAsync(Guid objectId, CancellationToken ct = default);

    /// <summary>Gets the latest block information.</summary>
    Task<BlockInfo> GetLatestBlockAsync(CancellationToken ct = default);
}
```

#### IWormStorageProvider
```csharp
namespace DataWarehouse.SDK.Contracts;

/// <summary>
/// Provides WORM (Write-Once-Read-Many) storage for disaster recovery and compliance.
/// </summary>
public interface IWormStorageProvider
{
    /// <summary>WORM enforcement mode (Software, HardwareIntegrated, Hybrid).</summary>
    WormEnforcementMode EnforcementMode { get; }

    /// <summary>Writes data to WORM storage with retention policy.</summary>
    Task<WormWriteResult> WriteAsync(Guid objectId, Stream data, WormRetentionPolicy retention, WriteContext context, CancellationToken ct = default);

    /// <summary>Reads data from WORM storage (always read-only).</summary>
    Task<Stream> ReadAsync(Guid objectId, CancellationToken ct = default);

    /// <summary>Checks if object exists and is within retention period.</summary>
    Task<WormObjectStatus> GetStatusAsync(Guid objectId, CancellationToken ct = default);

    /// <summary>Extends retention period (can only extend, never shorten).</summary>
    Task ExtendRetentionAsync(Guid objectId, DateTimeOffset newExpiry, CancellationToken ct = default);

    /// <summary>Places legal hold on object (prevents deletion even after retention expires).</summary>
    Task PlaceLegalHoldAsync(Guid objectId, string holdId, string reason, CancellationToken ct = default);

    /// <summary>Removes legal hold (requires appropriate authorization).</summary>
    Task RemoveLegalHoldAsync(Guid objectId, string holdId, CancellationToken ct = default);
}
```

#### ITamperProofProvider
```csharp
namespace DataWarehouse.SDK.Contracts;

/// <summary>
/// Main interface for tamper-proof storage operations combining integrity, blockchain, and WORM.
/// </summary>
public interface ITamperProofProvider
{
    /// <summary>Current configuration.</summary>
    TamperProofConfiguration Configuration { get; }

    /// <summary>Current instance states.</summary>
    IReadOnlyDictionary<string, InstanceDegradationState> InstanceStates { get; }

    /// <summary>Whether the structural configuration has been sealed (first write occurred).</summary>
    bool IsSealed { get; }

    /// <summary>
    /// Writes data with full tamper-proof protection.
    /// </summary>
    /// <param name="objectId">Unique identifier for the object.</param>
    /// <param name="data">Data stream to store.</param>
    /// <param name="context">Required write context including author and comment.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Write result with manifest reference and blockchain anchor.</returns>
    Task<SecureWriteResult> SecureWriteAsync(Guid objectId, Stream data, WriteContext context, CancellationToken ct = default);

    /// <summary>
    /// Reads data with specified verification level.
    /// </summary>
    Task<SecureReadResult> SecureReadAsync(Guid objectId, ReadMode mode = ReadMode.Verified, CancellationToken ct = default);

    /// <summary>
    /// Creates an append-only correction to existing data.
    /// Original data remains in WORM; new version supersedes it.
    /// </summary>
    /// <param name="objectId">Object to correct (will get new version).</param>
    /// <param name="correctedData">New data stream.</param>
    /// <param name="context">Required context with correction reason.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Correction result linking old and new versions.</returns>
    Task<CorrectionResult> SecureCorrectAsync(Guid objectId, Stream correctedData, CorrectionContext context, CancellationToken ct = default);

    /// <summary>
    /// Performs full audit of an object including blockchain verification.
    /// </summary>
    Task<AuditResult> AuditAsync(Guid objectId, CancellationToken ct = default);

    /// <summary>
    /// Manually triggers recovery from WORM for a tampered object.
    /// </summary>
    Task<RecoveryResult> RecoverFromWormAsync(Guid objectId, CancellationToken ct = default);

    /// <summary>
    /// Gets tamper incident report with attribution if available.
    /// </summary>
    Task<TamperIncidentReport?> GetTamperIncidentAsync(Guid objectId, CancellationToken ct = default);
}
```

#### IAccessLogProvider
```csharp
namespace DataWarehouse.SDK.Contracts;

/// <summary>
/// Provides access logging for tamper attribution.
/// </summary>
public interface IAccessLogProvider
{
    /// <summary>Records an access event.</summary>
    Task LogAccessAsync(AccessLogEntry entry, CancellationToken ct = default);

    /// <summary>Retrieves access history for an object within a time range.</summary>
    Task<IReadOnlyList<AccessLogEntry>> GetAccessHistoryAsync(
        Guid objectId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default);

    /// <summary>Queries access logs for potential tampering attribution.</summary>
    Task<IReadOnlyList<AccessLogEntry>> QuerySuspiciousAccessAsync(
        Guid objectId,
        DateTimeOffset tamperDetectedAt,
        TimeSpan lookbackWindow,
        CancellationToken ct = default);
}
```

---

### Enum Definitions

```csharp
namespace DataWarehouse.SDK.Contracts;

/// <summary>Hash algorithm for integrity verification.</summary>
public enum HashAlgorithmType
{
    SHA256,
    SHA384,
    SHA512,
    Blake3
}

/// <summary>Consensus mode for multi-node deployments.</summary>
public enum ConsensusMode
{
    /// <summary>Single writer, no consensus needed. Fast but no HA.</summary>
    SingleWriter,

    /// <summary>Raft-based consensus for writes. Slower but consistent.</summary>
    RaftConsensus
}

/// <summary>WORM enforcement mechanism.</summary>
public enum WormEnforcementMode
{
    /// <summary>Software-enforced immutability (can be bypassed with admin access).</summary>
    Software,

    /// <summary>Hardware-enforced (e.g., AWS S3 Object Lock, Azure Immutable Blob).</summary>
    HardwareIntegrated,

    /// <summary>Software primary with hardware backup verification.</summary>
    Hybrid
}

/// <summary>Behavior when tampering is detected during read.</summary>
public enum TamperRecoveryBehavior
{
    /// <summary>Automatically recover from WORM, log internally, serve recovered data.</summary>
    AutoRecoverSilent,

    /// <summary>Recover from WORM + generate incident report + alert admin.</summary>
    AutoRecoverWithReport,

    /// <summary>Do not serve data. Alert admin and wait for manual intervention.</summary>
    AlertAndWait
}

/// <summary>Read verification level.</summary>
public enum ReadMode
{
    /// <summary>Skip full verification, trust shard-level hashes. Fastest.</summary>
    Fast,

    /// <summary>Verify reconstructed data against manifest hash. Default.</summary>
    Verified,

    /// <summary>Full verification including blockchain anchor check. Slowest but complete.</summary>
    Audit
}

/// <summary>Instance degradation state.</summary>
public enum InstanceDegradationState
{
    /// <summary>All operations normal.</summary>
    Healthy,

    /// <summary>Some redundancy lost but fully operational.</summary>
    Degraded,

    /// <summary>Read-only mode (writes blocked).</summary>
    DegradedReadOnly,

    /// <summary>Operating but WORM recovery unavailable.</summary>
    DegradedNoRecovery,

    /// <summary>Instance unreachable.</summary>
    Offline,

    /// <summary>Data corruption detected, requires intervention.</summary>
    Corrupted
}

/// <summary>Type of access for logging.</summary>
public enum AccessType
{
    Read,
    Write,
    Correct,
    Delete,
    MetadataRead,
    MetadataWrite,
    AdminOperation,
    SystemMaintenance
}

/// <summary>Tamper attribution confidence level.</summary>
public enum AttributionConfidence
{
    /// <summary>Cannot determine who tampered (e.g., external/physical access).</summary>
    Unknown,

    /// <summary>Possible suspect based on access patterns.</summary>
    Suspected,

    /// <summary>Strong correlation with specific access.</summary>
    Likely,

    /// <summary>Definitive attribution (e.g., logged operation + hash mismatch).</summary>
    Confirmed
}
```

---

### Configuration Classes

```csharp
namespace DataWarehouse.SDK.Contracts;

/// <summary>
/// Main configuration for tamper-proof storage.
/// Structural settings become immutable after first write (sealed).
/// </summary>
public class TamperProofConfiguration
{
    // === STRUCTURAL (Immutable after seal) ===

    /// <summary>Storage instances configuration.</summary>
    public required StorageInstancesConfig StorageInstances { get; init; }

    /// <summary>RAID configuration for data sharding.</summary>
    public required RaidConfig Raid { get; init; }

    /// <summary>Hash algorithm for integrity verification.</summary>
    public HashAlgorithmType HashAlgorithm { get; init; } = HashAlgorithmType.SHA256;

    /// <summary>Consensus mode (cannot change after first write).</summary>
    public ConsensusMode ConsensusMode { get; init; } = ConsensusMode.SingleWriter;

    /// <summary>WORM enforcement mode.</summary>
    public WormEnforcementMode WormMode { get; init; } = WormEnforcementMode.Software;

    // === BEHAVIORAL (Can change at runtime) ===

    /// <summary>Behavior when tampering is detected. Changeable at runtime.</summary>
    public TamperRecoveryBehavior RecoveryBehavior { get; set; } = TamperRecoveryBehavior.AutoRecoverWithReport;

    /// <summary>Default read mode. Changeable at runtime.</summary>
    public ReadMode DefaultReadMode { get; set; } = ReadMode.Verified;

    /// <summary>Blockchain batching configuration. Changeable at runtime.</summary>
    public BlockchainBatchConfig BlockchainBatching { get; set; } = new();

    /// <summary>Alert/notification settings. Changeable at runtime.</summary>
    public AlertConfig Alerts { get; set; } = new();
}

/// <summary>
/// Configuration for the four storage instances.
/// </summary>
public class StorageInstancesConfig
{
    /// <summary>Primary data storage instance.</summary>
    public required StorageInstanceConfig Data { get; init; }

    /// <summary>Metadata/manifest storage instance.</summary>
    public required StorageInstanceConfig Metadata { get; init; }

    /// <summary>WORM vault storage instance.</summary>
    public required StorageInstanceConfig Worm { get; init; }

    /// <summary>Blockchain anchor storage instance.</summary>
    public required StorageInstanceConfig Blockchain { get; init; }
}

/// <summary>
/// Configuration for a single storage instance.
/// </summary>
public class StorageInstanceConfig
{
    /// <summary>Unique instance identifier (e.g., "data", "worm").</summary>
    public required string InstanceId { get; init; }

    /// <summary>Plugin ID to use (e.g., "com.datawarehouse.storage.s3").</summary>
    public required string PluginId { get; init; }

    /// <summary>Plugin-specific configuration.</summary>
    public Dictionary<string, object> PluginConfig { get; init; } = new();
}

/// <summary>
/// RAID configuration for data sharding.
/// </summary>
public class RaidConfig
{
    /// <summary>Number of data shards.</summary>
    public int DataShards { get; init; } = 4;

    /// <summary>Number of parity shards.</summary>
    public int ParityShards { get; init; } = 2;

    /// <summary>Fixed shard size in bytes (for uniform shards). 0 = variable.</summary>
    public long FixedShardSize { get; init; } = 0;

    /// <summary>Shard padding configuration.</summary>
    public ShardPaddingConfig Padding { get; init; } = new();
}

/// <summary>
/// Configuration for shard padding (Phase 3 padding, NOT covered by integrity hash).
/// </summary>
public class ShardPaddingConfig
{
    /// <summary>Whether to pad final shard to uniform size.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Padding byte value (default 0x00).</summary>
    public byte PaddingByte { get; init; } = 0x00;

    /// <summary>Whether to use random padding (more secure but not reproducible).</summary>
    public bool UseRandomPadding { get; init; } = false;
}

/// <summary>
/// Configuration for content padding (Phase 1 padding, covered by integrity hash).
/// </summary>
public class ContentPaddingConfig
{
    /// <summary>Whether content padding is enabled.</summary>
    public bool Enabled { get; init; } = false;

    /// <summary>Pad to multiple of this size (e.g., 4096 for block alignment).</summary>
    public int PadToMultipleOf { get; init; } = 4096;

    /// <summary>Minimum padding to add (to hide true size).</summary>
    public int MinimumPadding { get; init; } = 0;

    /// <summary>Maximum padding to add (randomized within min-max range).</summary>
    public int MaximumPadding { get; init; } = 4096;
}

/// <summary>
/// Blockchain batching configuration.
/// </summary>
public class BlockchainBatchConfig
{
    /// <summary>Maximum objects per batch anchor.</summary>
    public int MaxBatchSize { get; init; } = 100;

    /// <summary>Maximum time to wait before flushing batch.</summary>
    public TimeSpan MaxBatchDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Whether to wait for anchor confirmation before returning.</summary>
    public bool WaitForConfirmation { get; init; } = true;
}

/// <summary>
/// Alert configuration for tamper incidents.
/// </summary>
public class AlertConfig
{
    /// <summary>Webhook URLs to notify on tamper detection.</summary>
    public List<string> WebhookUrls { get; init; } = new();

    /// <summary>Email addresses for alerts.</summary>
    public List<string> EmailAddresses { get; init; } = new();

    /// <summary>Whether to publish to message bus topic.</summary>
    public bool PublishToMessageBus { get; init; } = true;

    /// <summary>Message bus topic for alerts.</summary>
    public string MessageBusTopic { get; init; } = "tamperproof.alerts";
}
```

---

### Manifest and Record Structures

```csharp
namespace DataWarehouse.SDK.Contracts;

/// <summary>
/// The manifest stored for each tamper-proof object.
/// Contains all information needed to verify and reconstruct the object.
/// </summary>
public class TamperProofManifest
{
    /// <summary>Object unique identifier.</summary>
    public required Guid ObjectId { get; init; }

    /// <summary>Version number (1 for original, 2+ for corrections).</summary>
    public required int Version { get; init; }

    /// <summary>Write context (author, comment, timestamp).</summary>
    public required WriteContextRecord WriteContext { get; init; }

    /// <summary>Phase 1 transformation records in order applied.</summary>
    public required List<PipelineStageRecord> TransformationStages { get; init; }

    /// <summary>Phase 2 integrity hash (covers original data + Phase 1 transforms).</summary>
    public required IntegrityHash IntegrityHash { get; init; }

    /// <summary>Phase 3 RAID distribution record.</summary>
    public required RaidRecord Raid { get; init; }

    /// <summary>Content padding record (if Phase 1 padding was applied).</summary>
    public ContentPaddingRecord? ContentPadding { get; init; }

    /// <summary>Original unpadded, untransformed data size.</summary>
    public required long OriginalSize { get; init; }

    /// <summary>Size after Phase 1 transformations (before RAID).</summary>
    public required long TransformedSize { get; init; }

    /// <summary>WORM storage reference.</summary>
    public required WormReference WormRef { get; init; }

    /// <summary>Blockchain anchor reference.</summary>
    public required BlockchainAnchorReference BlockchainRef { get; init; }

    /// <summary>Previous version reference (for corrections).</summary>
    public Guid? PreviousVersionId { get; init; }

    /// <summary>Correction reason (if this is a correction).</summary>
    public string? CorrectionReason { get; init; }

    /// <summary>Manifest creation timestamp (UTC).</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Records write context information (author, comment, session).
/// </summary>
public class WriteContextRecord
{
    /// <summary>Author/principal who performed the write.</summary>
    public required string Author { get; init; }

    /// <summary>Mandatory comment explaining the write (like git commit message).</summary>
    public required string Comment { get; init; }

    /// <summary>Session identifier for correlation.</summary>
    public string? SessionId { get; init; }

    /// <summary>Source system or application.</summary>
    public string? SourceSystem { get; init; }

    /// <summary>Client IP address (if available).</summary>
    public string? ClientIp { get; init; }

    /// <summary>UTC timestamp of the write.</summary>
    public required DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// Records a pipeline transformation stage.
/// </summary>
public class PipelineStageRecord
{
    /// <summary>Stage type (e.g., "Compression", "Encryption", "ContentPadding").</summary>
    public required string StageType { get; init; }

    /// <summary>Plugin ID that performed the transformation.</summary>
    public required string PluginId { get; init; }

    /// <summary>Order in which this stage was applied.</summary>
    public required int Order { get; init; }

    /// <summary>Stage-specific parameters (for reversibility).</summary>
    public Dictionary<string, object> Parameters { get; init; } = new();
}

/// <summary>
/// Records RAID distribution details.
/// </summary>
public class RaidRecord
{
    /// <summary>Total number of data shards.</summary>
    public required int DataShardCount { get; init; }

    /// <summary>Total number of parity shards.</summary>
    public required int ParityShardCount { get; init; }

    /// <summary>Individual shard records.</summary>
    public required List<ShardRecord> Shards { get; init; }

    /// <summary>Shard padding configuration used.</summary>
    public ShardPaddingRecord? ShardPadding { get; init; }
}

/// <summary>
/// Records details for a single shard.
/// </summary>
public class ShardRecord
{
    /// <summary>Shard index (0-based).</summary>
    public required int Index { get; init; }

    /// <summary>Whether this is a parity shard.</summary>
    public required bool IsParity { get; init; }

    /// <summary>Shard size in bytes.</summary>
    public required long Size { get; init; }

    /// <summary>Shard-level integrity hash.</summary>
    public required ShardHash Hash { get; init; }

    /// <summary>Storage location key within Data instance.</summary>
    public required string StorageKey { get; init; }

    /// <summary>Bytes of padding added to this shard (for final data shard).</summary>
    public int PaddingBytes { get; init; } = 0;
}

/// <summary>
/// Records shard padding details.
/// </summary>
public class ShardPaddingRecord
{
    /// <summary>Which shard was padded (usually last data shard).</summary>
    public required int PaddedShardIndex { get; init; }

    /// <summary>Original size before padding.</summary>
    public required long OriginalSize { get; init; }

    /// <summary>Size after padding.</summary>
    public required long PaddedSize { get; init; }

    /// <summary>Padding byte used (if not random).</summary>
    public byte? PaddingByte { get; init; }
}

/// <summary>
/// Records content padding details (Phase 1).
/// </summary>
public class ContentPaddingRecord
{
    /// <summary>Original size before padding.</summary>
    public required long OriginalSize { get; init; }

    /// <summary>Size after padding.</summary>
    public required long PaddedSize { get; init; }

    /// <summary>Padding alignment used.</summary>
    public required int Alignment { get; init; }
}

/// <summary>
/// Reference to WORM storage.
/// </summary>
public class WormReference
{
    /// <summary>WORM storage key.</summary>
    public required string StorageKey { get; init; }

    /// <summary>Retention expiry date.</summary>
    public required DateTimeOffset RetentionExpiry { get; init; }

    /// <summary>Whether legal hold is active.</summary>
    public bool HasLegalHold { get; init; } = false;
}

/// <summary>
/// Reference to blockchain anchor.
/// </summary>
public class BlockchainAnchorReference
{
    /// <summary>Block number containing the anchor.</summary>
    public required long BlockNumber { get; init; }

    /// <summary>Transaction/record ID within block.</summary>
    public required string TransactionId { get; init; }

    /// <summary>Merkle proof path (if batched).</summary>
    public List<string>? MerkleProofPath { get; init; }

    /// <summary>Anchor timestamp.</summary>
    public required DateTimeOffset AnchoredAt { get; init; }
}
```

---

### Write Context and Access Log Structures

```csharp
namespace DataWarehouse.SDK.Contracts;

/// <summary>
/// Required context for all write operations.
/// Like a git commit, every write MUST have an author and comment.
/// </summary>
public class WriteContext
{
    /// <summary>
    /// Author/principal performing the write. REQUIRED.
    /// Can be username, service account, or system identifier.
    /// </summary>
    public required string Author { get; init; }

    /// <summary>
    /// Comment explaining what is being written and why. REQUIRED.
    /// Like a git commit message - should explain the purpose.
    /// </summary>
    public required string Comment { get; init; }

    /// <summary>
    /// Optional session identifier for correlation across operations.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Optional source system identifier.
    /// </summary>
    public string? SourceSystem { get; init; }

    /// <summary>
    /// Optional additional metadata.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Context for correction operations (extends WriteContext with reason).
/// </summary>
public class CorrectionContext : WriteContext
{
    /// <summary>
    /// Reason for the correction. REQUIRED.
    /// Should explain what was wrong with the original data.
    /// </summary>
    public required string CorrectionReason { get; init; }

    /// <summary>
    /// Reference to the original object being corrected.
    /// </summary>
    public required Guid OriginalObjectId { get; init; }
}

/// <summary>
/// Access log entry for tamper attribution.
/// </summary>
public class AccessLogEntry
{
    /// <summary>Unique log entry identifier.</summary>
    public required Guid EntryId { get; init; }

    /// <summary>Object that was accessed.</summary>
    public required Guid ObjectId { get; init; }

    /// <summary>Type of access.</summary>
    public required AccessType AccessType { get; init; }

    /// <summary>Principal who performed the access.</summary>
    public required string Principal { get; init; }

    /// <summary>UTC timestamp of access.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Session identifier (if available).</summary>
    public string? SessionId { get; init; }

    /// <summary>Client IP address (if available).</summary>
    public string? ClientIp { get; init; }

    /// <summary>User agent or application identifier.</summary>
    public string? UserAgent { get; init; }

    /// <summary>Whether the access succeeded.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>Error message if access failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Hash computed during access (for writes).</summary>
    public string? ComputedHash { get; init; }

    /// <summary>Additional context.</summary>
    public Dictionary<string, object>? Context { get; init; }
}

/// <summary>
/// Tamper incident report with attribution.
/// </summary>
public class TamperIncidentReport
{
    /// <summary>Unique incident identifier.</summary>
    public required Guid IncidentId { get; init; }

    /// <summary>Affected object.</summary>
    public required Guid ObjectId { get; init; }

    /// <summary>When tampering was detected.</summary>
    public required DateTimeOffset DetectedAt { get; init; }

    /// <summary>Expected hash from manifest.</summary>
    public required string ExpectedHash { get; init; }

    /// <summary>Actual hash computed from data.</summary>
    public required string ActualHash { get; init; }

    /// <summary>Which storage instance had tampered data.</summary>
    public required string AffectedInstance { get; init; }

    /// <summary>Which shards were affected (if RAID).</summary>
    public List<int>? AffectedShards { get; init; }

    /// <summary>Recovery action taken.</summary>
    public required TamperRecoveryBehavior RecoveryAction { get; init; }

    /// <summary>Whether recovery succeeded.</summary>
    public required bool RecoverySucceeded { get; init; }

    // === ATTRIBUTION ===

    /// <summary>Confidence level of attribution.</summary>
    public required AttributionConfidence AttributionConfidence { get; init; }

    /// <summary>Suspected principal (if attribution available).</summary>
    public string? SuspectedPrincipal { get; init; }

    /// <summary>Access log entries analyzed for attribution.</summary>
    public List<AccessLogEntry>? RelatedAccessLogs { get; init; }

    /// <summary>Estimated time window when tampering occurred.</summary>
    public DateTimeOffset? EstimatedTamperTimeFrom { get; init; }

    /// <summary>Estimated time window when tampering occurred.</summary>
    public DateTimeOffset? EstimatedTamperTimeTo { get; init; }

    /// <summary>Attribution reasoning explanation.</summary>
    public string? AttributionReasoning { get; init; }

    /// <summary>Whether tampering appears to be internal or external.</summary>
    public bool? IsInternalTampering { get; init; }
}
```

---

### Result Types

```csharp
namespace DataWarehouse.SDK.Contracts;

/// <summary>Result of a secure write operation.</summary>
public class SecureWriteResult
{
    public required Guid ObjectId { get; init; }
    public required int Version { get; init; }
    public required IntegrityHash IntegrityHash { get; init; }
    public required string ManifestKey { get; init; }
    public required BlockchainAnchorReference BlockchainAnchor { get; init; }
    public required WormReference WormReference { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>Result of a secure read operation.</summary>
public class SecureReadResult
{
    public required Guid ObjectId { get; init; }
    public required int Version { get; init; }
    public required Stream Data { get; init; }
    public required ReadMode ModeUsed { get; init; }
    public required IntegrityVerificationResult Verification { get; init; }
    public TamperIncidentReport? TamperIncident { get; init; }
    public bool WasRecoveredFromWorm { get; init; } = false;
}

/// <summary>Result of a correction operation.</summary>
public class CorrectionResult
{
    public required Guid OriginalObjectId { get; init; }
    public required int OriginalVersion { get; init; }
    public required Guid NewObjectId { get; init; }
    public required int NewVersion { get; init; }
    public required string CorrectionReason { get; init; }
    public required SecureWriteResult WriteResult { get; init; }
    public required BlockchainAnchorReference CorrectionAnchor { get; init; }
}

/// <summary>Result of an audit operation.</summary>
public class AuditResult
{
    public required Guid ObjectId { get; init; }
    public required bool IntegrityValid { get; init; }
    public required bool BlockchainValid { get; init; }
    public required bool WormValid { get; init; }
    public required AuditChain AuditChain { get; init; }
    public List<TamperIncidentReport>? HistoricalIncidents { get; init; }
    public required DateTimeOffset AuditedAt { get; init; }
}

/// <summary>Result of a recovery operation.</summary>
public class RecoveryResult
{
    public required Guid ObjectId { get; init; }
    public required bool Succeeded { get; init; }
    public required string RecoverySource { get; init; } // "WORM" or "RAID-Parity"
    public IntegrityHash? RecoveredDataHash { get; init; }
    public string? ErrorMessage { get; init; }
    public required DateTimeOffset RecoveredAt { get; init; }
}

/// <summary>Integrity hash value.</summary>
public class IntegrityHash
{
    public required HashAlgorithmType Algorithm { get; init; }
    public required byte[] Value { get; init; }
    public string HexString => Convert.ToHexString(Value);
}

/// <summary>Shard-level hash.</summary>
public class ShardHash
{
    public required int ShardIndex { get; init; }
    public required Guid ObjectId { get; init; }
    public required byte[] Value { get; init; }
}

/// <summary>Result of integrity verification.</summary>
public class IntegrityVerificationResult
{
    public required bool IsValid { get; init; }
    public IntegrityHash? ExpectedHash { get; init; }
    public IntegrityHash? ActualHash { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>Audit chain showing all versions and corrections.</summary>
public class AuditChain
{
    public required Guid ObjectId { get; init; }
    public required List<AuditChainEntry> Entries { get; init; }
}

/// <summary>Single entry in audit chain.</summary>
public class AuditChainEntry
{
    public required int Version { get; init; }
    public required IntegrityHash Hash { get; init; }
    public required BlockchainAnchorReference Anchor { get; init; }
    public required WriteContextRecord WriteContext { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public Guid? SupersededBy { get; init; }
    public string? CorrectionReason { get; init; }
}
```

---

### Plugin Base Classes

```csharp
namespace DataWarehouse.SDK.Contracts;

/// <summary>
/// Base class for tamper-proof storage provider plugins.
/// </summary>
public abstract class TamperProofProviderPluginBase : FeaturePluginBase, ITamperProofProvider
{
    // Constructor receives dependencies via DI:
    // - IStorageProvider[] storageProviders (for Data, Metadata, WORM, Blockchain instances)
    // - IIntegrityProvider integrityProvider
    // - IBlockchainProvider blockchainProvider
    // - IWormStorageProvider wormProvider
    // - IPipelineOrchestrator pipelineOrchestrator
    // - IAccessLogProvider accessLogProvider
    // - IRaidProvider raidProvider

    protected abstract TamperProofConfiguration DefaultConfiguration { get; }

    // Abstract methods for subclass implementation
    protected abstract Task OnSealedAsync(CancellationToken ct);
    protected abstract Task<TamperIncidentReport> AnalyzeTamperAttributionAsync(
        Guid objectId,
        DateTimeOffset tamperDetectedAt,
        CancellationToken ct);
}

/// <summary>
/// Base class for integrity provider plugins.
/// </summary>
public abstract class IntegrityProviderPluginBase : FeaturePluginBase, IIntegrityProvider
{
    protected abstract byte[] ComputeHashCore(Stream data, HashAlgorithmType algorithm);
}

/// <summary>
/// Base class for blockchain provider plugins.
/// </summary>
public abstract class BlockchainProviderPluginBase : FeaturePluginBase, IBlockchainProvider
{
    protected abstract Task<BlockchainAnchor> CreateAnchorAsync(AnchorRequest request, CancellationToken ct);
    protected abstract Task<bool> ValidateChainIntegrityAsync(CancellationToken ct);
}

/// <summary>
/// Base class for WORM storage provider plugins.
/// </summary>
public abstract class WormStorageProviderPluginBase : FeaturePluginBase, IWormStorageProvider
{
    protected abstract Task EnforceRetentionAsync(Guid objectId, WormRetentionPolicy policy, CancellationToken ct);
}

/// <summary>
/// Base class for access log provider plugins.
/// </summary>
public abstract class AccessLogProviderPluginBase : FeaturePluginBase, IAccessLogProvider
{
    protected abstract Task PersistLogEntryAsync(AccessLogEntry entry, CancellationToken ct);
}
```

---

### Implementation Phases
# ** Consider the correct location for implementation. Common functions, enums etc. go in SDK. 
# ** Abstract classes implement common features and lifecycle in SDK.
# ** Only specific implementations go in the plugins.

#### Phase T1: Core Infrastructure (Priority: CRITICAL)
| Task | Description | Dependencies | Status |
|------|-------------|--------------|--------|
| T1.1 | Create `IIntegrityProvider` interface and `IntegrityProviderPluginBase` | None | [ ] |
| T1.2 | Implement `DefaultIntegrityProvider` with SHA256/SHA384/SHA512/Blake3 | T1.1 | [ ] |
| T1.3 | Create `IBlockchainProvider` interface and `BlockchainProviderPluginBase` | None | [ ] |
| T1.4 | Implement `LocalBlockchainProvider` (file-based chain for single-node) | T1.3 | [ ] |
| T1.5 | Create `IWormStorageProvider` interface and `WormStorageProviderPluginBase` | None | [ ] |
| T1.6 | Implement `SoftwareWormProvider` (software-enforced immutability) | T1.5 | [ ] |
| T1.7 | Create `IAccessLogProvider` interface and `AccessLogProviderPluginBase` | None | [ ] |
| T1.8 | Implement `DefaultAccessLogProvider` (persistent access logging) | T1.7 | [ ] |
| T1.9 | Create all configuration classes | None | [ ] |
| T1.10 | Create all enum definitions | None | [ ] |
| T1.11 | Create all manifest and record structures | None | [ ] |
| T1.12 | Create `WriteContext` and `CorrectionContext` classes | None | [ ] |

#### Phase T2: Core Plugin (Priority: HIGH)
| Task | Description | Dependencies | Status |
|------|-------------|--------------|--------|
| T2.1 | Create `ITamperProofProvider` interface | T1.* | [ ] |
| T2.2 | Create `TamperProofProviderPluginBase` base class | T2.1 | [ ] |
| T2.3 | Implement Phase 1 write pipeline (user transformations) | T2.2 | [ ] |
| T2.4 | Implement Phase 2 write pipeline (integrity hash) | T2.3, T1.2 | [ ] |
| T2.5 | Implement Phase 3 write pipeline (RAID + shard padding) | T2.4 | [ ] |
| T2.6 | Implement Phase 4 write pipeline (parallel storage writes) | T2.5 | [ ] |
| T2.7 | Implement Phase 5 write pipeline (blockchain anchoring) | T2.6, T1.4 | [ ] |
| T2.8 | Implement `SecureWriteAsync` combining all phases | T2.7 | [ ] |
| T2.9 | Implement mandatory write comment validation | T2.8, T1.12 | [ ] |
| T2.10 | Implement access logging on all operations | T2.8, T1.8 | [ ] |

#### Phase T3: Read Pipeline & Verification (Priority: HIGH)
| Task | Description | Dependencies | Status |
|------|-------------|--------------|--------|
| T3.1 | Implement Phase 1 read (manifest retrieval) | T2.* | [ ] |
| T3.2 | Implement Phase 2 read (shard retrieval + reconstruction) | T3.1 | [ ] |
| T3.3 | Implement Phase 3 read (integrity verification by ReadMode) | T3.2 | [ ] |
| T3.4 | Implement Phase 4 read (reverse transformations) | T3.3 | [ ] |
| T3.5 | Implement Phase 5 read (tamper response) | T3.4 | [ ] |
| T3.6 | Implement `SecureReadAsync` with all ReadModes | T3.5 | [ ] |
| T3.7 | Implement tamper detection and incident creation | T3.6 | [ ] |
| T3.8 | Implement tamper attribution analysis | T3.7, T1.8 | [ ] |
| T3.9 | Implement `GetTamperIncidentAsync` with attribution | T3.8 | [ ] |

#### Phase T4: Recovery & Advanced Features (Priority: MEDIUM)
| Task | Description | Dependencies | Status |
|------|-------------|--------------|--------|
| T4.1 | Implement `AutoRecoverSilent` recovery behavior | T3.* | [ ] |
| T4.2 | Implement `AutoRecoverWithReport` recovery behavior | T4.1 | [ ] |
| T4.3 | Implement `AlertAndWait` recovery behavior | T4.2 | [ ] |
| T4.4 | Implement `RecoverFromWormAsync` manual recovery | T4.3 | [ ] |
| T4.5 | Implement `SecureCorrectAsync` (append-only corrections) | T4.4 | [ ] |
| T4.6 | Implement `AuditAsync` with full chain verification | T4.5 | [ ] |
| T4.7 | Implement seal mechanism (lock structural config after first write) | T4.6 | [ ] |
| T4.8 | Implement instance degradation state management | T4.7 | [ ] |
| T4.9 | Implement `RaftConsensus` mode support | T4.8 | [ ] |
| T4.10 | Implement `HardwareIntegrated` WORM mode (S3 Object Lock, Azure Immutable) | T4.9 | [ ] |

#### Phase T5: Testing & Documentation (Priority: HIGH)
| Task | Description | Dependencies | Status |
|------|-------------|--------------|--------|
| T5.1 | Unit tests for integrity provider | T1.2 | [ ] |
| T5.2 | Unit tests for blockchain provider | T1.4 | [ ] |
| T5.3 | Unit tests for WORM provider | T1.6 | [ ] |
| T5.4 | Unit tests for access log provider | T1.8 | [ ] |
| T5.5 | Integration tests for write pipeline | T2.8 | [ ] |
| T5.6 | Integration tests for read pipeline | T3.6 | [ ] |
| T5.7 | Integration tests for tamper detection + attribution | T3.9 | [ ] |
| T5.8 | Integration tests for recovery scenarios | T4.4 | [ ] |
| T5.9 | Integration tests for correction workflow | T4.5 | [ ] |
| T5.10 | Performance benchmarks | T4.* | [ ] |
| T5.11 | XML documentation for all public APIs | T4.* | [ ] |
| T5.12 | Update CLAUDE.md with tamper-proof documentation | T5.11 | [ ] |

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
├── DataWarehouse.Plugins.Integrity/
│   ├── DefaultIntegrityPlugin.cs
│   └── DataWarehouse.Plugins.Integrity.csproj
├── DataWarehouse.Plugins.Blockchain.Local/
│   ├── LocalBlockchainPlugin.cs
│   └── DataWarehouse.Plugins.Blockchain.Local.csproj
├── DataWarehouse.Plugins.Worm.Software/
│   ├── SoftwareWormPlugin.cs
│   └── DataWarehouse.Plugins.Worm.Software.csproj
├── DataWarehouse.Plugins.Worm.S3ObjectLock/
│   ├── S3ObjectLockWormPlugin.cs
│   └── DataWarehouse.Plugins.Worm.S3ObjectLock.csproj
└── DataWarehouse.Plugins.AccessLog/
    ├── DefaultAccessLogPlugin.cs
    └── DataWarehouse.Plugins.AccessLog.csproj
```

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
