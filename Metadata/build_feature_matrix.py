#!/usr/bin/env python3
"""Build Feature Verification Matrix for v4.0 milestone.

Reads FeatureCatalog.txt (3,008 features across 85 domains), maps them to v4.0
capability domains, deduplicates, and adds aspirational features per plugin.

Aspirational features are loaded from aspirational_features.py (850+ features
across 68 plugins/projects).
"""
import re
import sys
from collections import defaultdict
from datetime import datetime

sys.path.insert(0, "C:/Temp/DataWarehouse/DataWarehouse/Metadata")
from aspirational_features import get_all_aspirational

# ============================================================================
# V4.0 DOMAIN MAPPING: Map FeatureCatalog source domains to v4.0 domains
# ============================================================================

DOMAIN_MAP = {
    # Domain 1: Data Pipeline
    "Data Compression": 1,
    "Data Integration & ETL": 1,
    "Data Format & Serialization": 1,
    "Streaming & Real-Time Data": 1,
    "Workflow Orchestration": 1,
    "Storage Processing": 1,

    # Domain 2: Storage & Persistence
    "Storage Backends": 2,
    "RAID & Erasure Coding": 2,
    "Database Wire Protocols": 2,
    "Database Storage": 2,
    "Data Lake & Lakehouse": 2,
    "Virtual Disk Engine": 2,
    "Resource Management": 2,

    # Domain 3: Security & Cryptography
    "Encryption & Cryptography": 3,
    "Key Management": 3,
    "Access Control & Identity": 3,
    "Access Control & Identity (Services)": 3,
    "Data Integrity & Hashing": 3,
    "Blockchain Integration": 3,
    "TamperProof": 3,

    # Domain 4: Media & Format Processing
    "Media Transcoding": 4,
    "Data Format & Scientific": 4,
    "Documentation Generation": 4,

    # Domain 5: Distributed Systems
    "Replication & Sync": 5,
    "Consensus & Coordination": 5,
    "Resilience & Fault Tolerance": 5,

    # Domain 6: Hardware & Platform
    "Hardware Integration": 6,
    "SDK Ports & Cross-Language": 6,
    "Platform Services": 6,

    # Domain 7: Edge / IoT
    "IoT Integration": 7,
    "Edge Computing": 7,
    "RTOS Bridge": 7,

    # Domain 8: AEDS & Service Architecture
    "AEDS (Autonomous Distribution)": 8,

    # Domain 9: Air-Gap & Isolated
    "Air-Gap & Offline": 9,

    # Domain 10: Filesystem & Virtual Storage
    "Filesystem & Virtual Storage": 10,

    # Domain 11: Compute & Processing
    "Compute Runtimes": 11,
    "Serverless & FaaS": 11,
    "Self-Emulating Objects": 11,

    # Domain 12: Transport & Networking
    "Interface & API Protocols": 12,
    "Adaptive Transport": 12,
    "Data Transit": 12,
    "Microservices Architecture": 12,

    # Domain 13: Intelligence & AI
    "AI & Intelligence": 13,
    "AI & Intelligence (Services)": 13,
    "Data Catalog & Discovery": 13,
    "Data Fabric": 13,

    # Domain 14: Observability & Operations
    "Observability & Monitoring": 14,
    "Dashboard & Visualization": 14,

    # Domain 15: Data Governance & Compliance
    "Compliance & Regulatory": 15,
    "Data Governance": 15,
    "Data Quality": 15,
    "Data Lineage": 15,
    "Data Privacy": 15,
    "Data Protection & Backup": 15,
    "Data Management & Lifecycle": 15,
    "Data Mesh": 15,

    # Domain 16: Cloud & Deployment
    "Deployment & CI/CD": 16,
    "Multi-Cloud": 16,
    "Sustainability": 16,

    # Domain 17: CLI & GUI
    "CLI": 17,
    "GUI": 17,
}

DOMAIN_NAMES = {
    1: "Data Pipeline (Write/Read/Process)",
    2: "Storage & Persistence",
    3: "Security & Cryptography",
    4: "Media & Format Processing",
    5: "Distributed Systems & Replication",
    6: "Hardware & Platform Integration",
    7: "Edge / IoT",
    8: "AEDS & Service Architecture",
    9: "Air-Gap & Isolated Deployment",
    10: "Filesystem & Virtual Storage",
    11: "Compute & Processing",
    12: "Adaptive Transport & Networking",
    13: "Self-Emulating Objects & Intelligence",
    14: "Observability & Operations",
    15: "Data Governance & Compliance",
    16: "Docker / Kubernetes / Cloud",
    17: "CLI & GUI Dynamic Intelligence",
}

# ============================================================================
# ASPIRATIONAL FEATURES
# Loaded from aspirational_features.py (850+ features across 68 plugins)
# Old inline dict removed — now using get_all_aspirational() from import
# ============================================================================

# Legacy inline aspirational features (kept for reference, not used in generation)
# Actual features loaded from aspirational_features.py via get_all_aspirational()
_LEGACY_ASPIRATIONAL = {
    # Domain 1: Data Pipeline
    1: [
        "Streaming pipeline with backpressure — automatically throttle ingestion when downstream is slow",
        "Pipeline step retry with exponential backoff — individual stage failure doesn't kill the whole pipeline",
        "Pipeline analytics dashboard — throughput per stage, bottleneck identification, latency heatmaps",
        "Pipeline versioning — save and restore pipeline configurations, A/B test pipeline variants",
        "Data validation rules engine — configurable per-stage validation (schema, range, format, custom)",
        "Pipeline dependency graph visualization — show data flow across all active pipelines",
        "Dead letter queue for failed items — failed pipeline items captured for later inspection/replay",
        "Pipeline scheduling — cron-style scheduled pipeline execution (hourly, daily, weekly)",
        "Pipeline cost estimation — predict storage/compute costs before running a pipeline",
        "Multi-format pipeline — single pipeline handles mixed input formats (CSV + JSON + Parquet)",
        "Pipeline templates — pre-built templates for common patterns (ETL, CDC, streaming ingest)",
        "Incremental pipeline processing — only process changed/new data since last run",
        "Pipeline checkpoint/resume — resume from last checkpoint after crash",
        "Parallel fan-out processing — split pipeline into parallel branches for throughput",
        "Pipeline alerting — notify on completion, failure, SLA breach, data quality issues",
        "Compression ratio advisor — recommend best algorithm per data type/size",
        "Encryption performance advisor — recommend cipher based on data sensitivity and throughput needs",
        "Format auto-conversion — automatically convert between formats in pipeline (e.g., CSV to Parquet)",
        "Pipeline lineage tracking — automatic lineage capture for every transformation step",
        "Pipeline audit log — who ran what pipeline, when, with what parameters, what changed",
    ],
    # Domain 2: Storage & Persistence
    2: [
        "Storage cost optimizer — analyze usage patterns and recommend cheaper storage tiers",
        "Hot-path caching with automatic eviction — LRU/LFU/ARC cache in front of storage",
        "Multi-region geo-replication with automatic failover — write to nearest, read from any",
        "Storage quota management — per-tenant/per-user quotas with soft/hard limits and alerts",
        "Storage analytics dashboard — capacity trends, IOPS, latency percentiles, cost breakdown",
        "Data lifecycle automation — automatic tier migration (hot->warm->cold->archive->delete)",
        "Deduplication across storage backends — global dedup with reference counting",
        "Storage health monitoring — predictive failure detection, SMART data analysis",
        "Backup scheduler — configurable automated backup with retention policies",
        "Point-in-time recovery — restore data to any previous point in time",
        "Storage migration wizard — live-migrate data between backends with zero downtime",
        "Object versioning with diff — compare versions, show what changed",
        "Bulk import/export — high-throughput batch operations with progress tracking",
        "Storage inventory scan — catalog all stored objects with metadata/size/age/access frequency",
        "Cross-backend search — unified search across all storage backends",
        "Storage compliance reports — show encryption status, retention compliance per object",
        "RAID rebuild progress monitor — real-time rebuild status with ETA",
        "Storage tiering advisor — AI-driven recommendations for optimal tier placement",
        "Immutable storage mode — WORM-style write-once storage for regulatory compliance",
        "Storage federation — virtual unified namespace across multiple storage backends",
    ],
    # Domain 3: Security & Cryptography
    3: [
        "Key ceremony workflow — multi-party key generation with audit trail",
        "Crypto agility — swap encryption algorithms without re-encrypting existing data",
        "Quantum-safe migration wizard — assess quantum risk and migrate to PQC algorithms",
        "Security posture dashboard — real-time view of encryption coverage, key age, access patterns",
        "Automated key rotation scheduler — policy-driven rotation with zero-downtime",
        "Certificate lifecycle management — auto-renewal, expiration alerts, revocation",
        "Secrets scanner — detect leaked credentials in logs, configs, code",
        "Access audit report generator — who accessed what, when, how often, from where",
        "Role mining — analyze actual access patterns and suggest optimal RBAC roles",
        "Privilege escalation detection — alert when users gain unexpected permissions",
        "Zero-knowledge proof verification — prove data properties without revealing data",
        "Compliance gap analysis — compare current security posture against FIPS/PCI/SOC2",
        "Encryption coverage report — show which data is encrypted, with what algorithm, key age",
        "Security incident response automation — automatic lockdown on detected breach",
        "Multi-factor authentication flow designer — customize MFA flows per sensitivity level",
        "Cross-tenant security isolation verification — prove tenants cannot access each other's data",
        "Hardware security module health monitoring — HSM availability, performance, capacity",
        "Secure key escrow — disaster recovery key backup with multi-party unlock",
        "Data classification engine — automatic sensitivity classification (PII, PHI, financial)",
        "Threat intelligence feed integration — real-time threat data influencing access decisions",
    ],
    # Domain 4: Media & Format Processing
    4: [
        "Format compatibility matrix — show which formats can convert to which",
        "Batch format conversion — convert entire directories with progress tracking",
        "Thumbnail generation — auto-generate thumbnails for images and videos",
        "Video streaming preparation — adaptive bitrate encoding (HLS/DASH) from source video",
        "Image optimization — lossy/lossless optimization for web delivery",
        "OCR text extraction — extract searchable text from images and scanned PDFs",
        "Document preview generation — render previews without native applications",
        "Metadata extraction — EXIF, XMP, Dublin Core, DICOM tags from media files",
        "Format validation — verify files actually match their declared format",
        "Scientific data visualization — auto-generate charts from Parquet/HDF5/Arrow data",
        "3D model preview — render 3D model previews without specialized software",
        "Audio transcription — speech-to-text for audio files",
        "Watermarking — invisible/visible watermarks on images and documents",
        "Format migration planner — plan migration from legacy to modern formats",
        "Healthcare format anonymization — remove PII from DICOM/HL7 while preserving clinical data",
    ],
    # Domain 5: Distributed Systems
    5: [
        "Cluster topology visualization — real-time view of nodes, leaders, replicas, health",
        "Split-brain detection and automatic resolution — detect and heal network partitions",
        "Replication lag monitoring — alert when replicas fall behind primary",
        "Conflict resolution strategy advisor — recommend strategy based on data type and access pattern",
        "Automatic cluster rebalancing — redistribute data when nodes join/leave",
        "Cross-datacenter replication dashboard — show sync status, lag, bandwidth per link",
        "Consistency level selector — per-operation consistency (eventual, causal, strong, linearizable)",
        "Partition tolerance testing — simulate network partitions and verify correct behavior",
        "Quorum calculator — verify cluster meets quorum requirements for each operation",
        "Node decommission workflow — safely remove node with data migration and verification",
        "Automatic leader failover with zero data loss — guaranteed RPO=0 failover",
        "Gossip protocol health dashboard — membership view, heartbeat status, suspicion state",
        "Multi-region consensus — consensus across geographically distributed nodes",
        "Read repair on inconsistency detection — automatically fix stale reads",
        "Anti-entropy repair scheduling — periodic full-sync to catch missed updates",
    ],
    # Domain 6: Hardware & Platform
    6: [
        "Hardware capability discovery report — inventory of available acceleration (GPU, QAT, TPM, HSM)",
        "Platform-specific optimization advisor — recommend settings based on detected hardware",
        "Cross-platform deployment validator — verify same binary works on Windows/Linux/macOS",
        "Hardware benchmark suite — test actual throughput of detected hardware",
        "Driver auto-installer — detect missing drivers and install from bundle",
        "NUMA topology visualization — show memory/CPU affinity for optimization",
        "Power management integration — hibernate/sleep-aware data persistence",
        "USB device detection for air-gap transfer — detect connected USB devices for data import/export",
        "ARM architecture support — native ARM64 builds for Graviton/Apple Silicon",
        "Embedded system profiler — assess capability limits for edge deployment",
    ],
    # Domain 7: Edge / IoT
    7: [
        "Device fleet management dashboard — monitor all edge devices from central console",
        "Over-the-air update for edge nodes — push configuration and code updates remotely",
        "Edge-to-cloud sync scheduler — batch sync edge data to cloud on schedule",
        "Sensor data aggregation — pre-aggregate at edge before cloud upload (reduce bandwidth)",
        "Offline-first data collection — collect data when disconnected, sync when available",
        "Edge compute resource monitoring — CPU/memory/storage usage per edge device",
        "IoT device provisioning — zero-touch enrollment of new devices",
        "Edge ML model deployment — push trained models to edge for local inference",
        "Protocol gateway — translate between IoT protocols (MQTT/CoAP/HTTP)",
        "Digital twin dashboard — visualize twin state, history, and what-if simulations",
        "Battery/power management — optimize operations based on remaining power",
        "Mesh network visualization — show network topology, signal strength, routing paths",
        "Geofencing — trigger actions when devices enter/leave geographic zones",
        "Predictive maintenance — ML-based prediction of device failures",
        "Edge security — certificate-based device identity, encrypted local storage",
    ],
    # Domain 8: AEDS & Service Architecture
    8: [
        "Client heartbeat monitoring dashboard — see all connected clients, their health, last seen",
        "Bandwidth throttling per client — limit bandwidth per client connection",
        "Update rollback on client failure — if client-side update fails, automatically rollback",
        "Distribution job progress tracking — real-time progress of file distribution to all clients",
        "Client group management — organize clients into groups for targeted distributions",
        "Staged rollout — distribute to 1% then 10% then 100% of clients with health checks between stages",
        "Client capability reporting — each client reports its capabilities (disk, CPU, bandwidth)",
        "Priority-based distribution queue — urgent distributions jump the queue",
        "Peer-to-peer distribution — clients share content with each other to reduce server load",
        "Distribution scheduling — schedule distributions for off-hours",
        "Client-side storage management — manage local cache/storage limits on clients",
        "Manifest signing and verification — all distribution manifests cryptographically signed",
        "Channel access control — per-channel subscription permissions",
        "Distribution audit trail — who distributed what to whom, when, what happened",
        "Multi-server federation — multiple distribution servers with load balancing",
        "Geographic-aware routing — route distributions via nearest server/CDN node",
        "Checkpoint-based resume — resume interrupted distributions from last checkpoint",
        "Distribution deduplication — don't re-send files clients already have",
        "Client self-update — clients can update their own DataWarehouse installation",
        "Health-based client removal — automatically remove unhealthy clients from active distribution",
    ],
    # Domain 9: Air-Gap
    9: [
        "Air-gap transfer wizard — guided UI for USB-based data import/export",
        "Transfer manifest with integrity verification — every USB transfer has a manifest with checksums",
        "Classified data handling — automatic classification labels on exported data",
        "Air-gap update mechanism — apply software updates via physical media",
        "Cross-enclave transfer — transfer between security domains with policy enforcement",
        "Air-gap audit trail — log all physical media transfers with chain of custody",
        "Portable execution environment — run directly from USB without installation",
        "Steganographic covert channel — hide data in innocuous files for covert transfer",
        "Data diode emulation — one-way data flow for high-security environments",
        "Hardware encryption for portable media — encrypt all data written to USB/external drives",
    ],
    # Domain 10: Filesystem
    10: [
        "Virtual disk performance benchmarks — IOPS, throughput, latency for VDE",
        "Snapshot management UI — create, list, restore, delete snapshots",
        "Filesystem integrity checker — verify filesystem consistency (like fsck)",
        "Quota management per directory/user — filesystem-level quotas",
        "File locking for concurrent access — advisory and mandatory file locks",
        "Filesystem events/notifications — inotify-style change notifications",
        "Mount point health monitoring — detect and alert on mount failures",
        "Filesystem compression — transparent per-file compression (like ZFS)",
        "Deduplication at filesystem level — inline/post-process dedup",
        "Filesystem encryption — per-file or per-directory encryption layers",
    ],
    # Domain 11: Compute
    11: [
        "Compute job scheduler — schedule and queue compute tasks with priority",
        "Resource usage metering — track CPU/memory/GPU usage per job for billing",
        "WASM marketplace — discover and install WASM compute modules",
        "Compute sandbox security audit — verify sandboxed execution cannot escape",
        "GPU job management — allocate GPU resources, manage CUDA/ROCm contexts",
        "Serverless cold start optimization dashboard — track and optimize cold start times",
        "Function versioning and rollback — version serverless functions, rollback on error",
        "Compute cost estimation — predict cost before launching compute job",
        "Distributed compute monitoring — track progress of distributed compute tasks",
        "Compute template library — pre-built templates for common compute patterns",
    ],
    # Domain 12: Transport
    12: [
        "API gateway with rate limiting — per-client rate limits, throttling, burst control",
        "API versioning management — support multiple API versions simultaneously",
        "Protocol performance comparison — benchmark different protocols for user's workload",
        "WebSocket connection pool management — monitor active connections, limits, health",
        "GraphQL schema explorer — interactive schema browser with documentation",
        "gRPC reflection and discovery — auto-discover available gRPC services",
        "API documentation auto-generation — OpenAPI/Swagger from live API",
        "Request/response logging — configurable request/response capture for debugging",
        "Circuit breaker dashboard — visualize circuit states, trip reasons, recovery",
        "Service mesh integration — integrate with Istio/Linkerd/Consul for service discovery",
        "API mocking for development — mock API responses during development/testing",
        "Bandwidth usage reporting — per-endpoint, per-client bandwidth tracking",
        "Protocol upgrade negotiation — automatically use best available protocol",
        "API health endpoint — standardized health check endpoint for all APIs",
        "Cross-origin resource sharing (CORS) management — configurable CORS policies",
    ],
    # Domain 13: Intelligence
    13: [
        "AI model A/B testing — compare model performance with live traffic",
        "Inference cost tracking per query — track and report AI API costs",
        "Model versioning with rollback — version AI models, rollback on regression",
        "AI provider health monitoring — track provider uptime, latency, error rates",
        "Prompt template management — versioned prompt templates for consistent AI use",
        "AI output quality scoring — automatic quality assessment of AI responses",
        "Knowledge graph explorer — visual graph browser with search and filter",
        "Embedding quality dashboard — track embedding quality, drift, coverage",
        "Anomaly detection tuning UI — configure sensitivity, alert thresholds",
        "ML training pipeline — train custom models on user data with AutoML",
        "Feature store — centralized feature storage for ML model training",
        "Model explainability — explain AI decisions with SHAP/LIME values",
        "AI ethics compliance — bias detection, fairness monitoring, transparency reports",
        "Multi-model ensemble — combine outputs from multiple models for better results",
        "AI cost budget — set AI spending limits per tenant/project with alerts",
        "Knowledge base Q&A — natural language Q&A over stored data using RAG",
        "AI-powered data profiling — automatic statistical profiling of datasets",
        "Intelligent caching — AI predicts which data to cache based on access patterns",
        "Semantic search across all data — natural language search over all stored content",
        "AI-driven anomaly root cause analysis — not just detect anomalies, explain why",
    ],
    # Domain 14: Observability
    14: [
        "Unified observability dashboard — single pane of glass for metrics, traces, logs",
        "Custom metric creation — users define custom metrics without code changes",
        "Alert routing and escalation — PagerDuty/OpsGenie/Slack integration",
        "SLA monitoring — track SLA compliance with automated reporting",
        "Log aggregation and search — centralized log search with full-text and structured queries",
        "Trace-based debugging — click-through from dashboard to distributed trace",
        "Resource usage forecasting — predict future resource needs based on trends",
        "Incident timeline — automatic timeline of events leading to an incident",
        "Performance regression detection — automatically detect performance degradation",
        "Cost monitoring — track infrastructure costs with anomaly detection",
        "Plugin health scorecard — per-plugin health score with contributing factors",
        "Capacity planning — project when resources will be exhausted",
        "Custom dashboard builder — drag-and-drop dashboard creation",
        "Export to Grafana/Datadog — export metrics in standard formats",
        "Audit log explorer — search and filter security/change audit logs",
    ],
    # Domain 15: Governance & Compliance
    15: [
        "Compliance dashboard — real-time compliance status per regulation (GDPR, HIPAA, SOC2)",
        "Data catalog search — find data assets by name, tag, owner, sensitivity",
        "Data quality scorecard — per-dataset quality scores with trend tracking",
        "Lineage impact analysis — show what downstream systems are affected by a change",
        "Data retention policy enforcement — automatic enforcement of retention/deletion policies",
        "PII discovery and classification — scan data for PII with ML-based detection",
        "Right-to-erasure automation — automated GDPR deletion with verification",
        "Data sovereignty mapping — visualize where data resides geographically",
        "Compliance audit report generator — auto-generate reports for auditors",
        "Data stewardship workflow — assign data owners, approval workflows",
        "Data quality rules engine — configurable quality rules with auto-remediation",
        "Master data management — golden record resolution across sources",
        "Data sharing agreements — manage inter-org data sharing with policy enforcement",
        "Privacy-preserving analytics — query data without exposing raw PII",
        "Regulatory change monitor — alert when regulations change that affect compliance",
        "Data catalog AI enrichment — automatically tag and describe data assets with AI",
        "Cross-regulation mapping — show which controls satisfy multiple regulations",
        "Data breach notification workflow — automated breach assessment and notification",
        "Consent management — track user consent for data processing activities",
        "Data access request portal — self-service portal for data access requests",
    ],
    # Domain 16: Cloud & Deployment
    16: [
        "One-click deployment — deploy entire DataWarehouse stack with one command",
        "Infrastructure-as-Code templates — Terraform, Pulumi, CloudFormation templates",
        "Multi-cloud cost comparison — compare costs across AWS/Azure/GCP for user's workload",
        "Auto-scaling policies — configure scale-out/scale-in rules per component",
        "Blue/green deployment with traffic shifting — gradual traffic migration",
        "Canary deployment with automatic rollback — health-based automatic rollback",
        "Deployment rollback — one-click rollback to previous version",
        "Cloud-agnostic configuration — same config file works across all clouds",
        "Container image scanning — scan Docker images for vulnerabilities",
        "Kubernetes operator — custom K8s operator for DataWarehouse lifecycle management",
        "Helm chart — parameterized Helm chart for K8s deployment",
        "Carbon footprint tracking — estimate carbon impact of deployments",
        "Multi-region deployment orchestrator — coordinate deployments across regions",
        "Deployment health verification — automated post-deploy health checks",
        "Environment promotion — promote configs from dev to staging to production",
    ],
    # Domain 17: CLI & GUI
    17: [
        "Interactive CLI wizard — guided setup for complex operations",
        "CLI autocomplete with fuzzy matching — typo-tolerant command completion",
        "CLI output formatting — JSON, table, CSV, YAML output options",
        "CLI scripting mode — machine-readable output for automation",
        "CLI command history with search — search previous commands",
        "GUI dark mode — full dark/light theme support",
        "GUI responsive design — works on desktop, tablet, and mobile",
        "GUI keyboard shortcuts — power user keyboard navigation",
        "GUI drag-and-drop file upload — upload files via drag-and-drop",
        "GUI real-time notifications — toast notifications for events",
        "GUI plugin management — install/uninstall/configure plugins from GUI",
        "GUI user preferences — per-user settings persistence",
        "CLI progress bars — visual progress for long operations",
        "GUI onboarding tour — first-time user guided tour",
        "CLI alias support — user-defined command aliases",
    ],
}

# ============================================================================
# PARSE FEATURE CATALOG
# ============================================================================

def parse_catalog(filepath):
    """Parse FeatureCatalog.txt into {source_domain: [feature_names]}."""
    domains = {}
    current_domain = None
    with open(filepath, 'r', encoding='utf-8') as f:
        for line in f:
            line = line.rstrip()
            m = re.match(r'^=== (.+?) \((\d+) features?\) ===$', line)
            if m:
                current_domain = m.group(1)
                domains[current_domain] = []
                continue
            if current_domain and line.startswith('  - '):
                feature = line.strip('- ').strip()
                if feature and len(feature) > 1:  # Skip single-char entries
                    domains[current_domain].append(feature)
    return domains


def map_to_v4_domains(catalog_domains):
    """Map source domains to v4.0 domain numbers."""
    v4_domains = defaultdict(list)
    unmapped = []

    for source_domain, features in catalog_domains.items():
        domain_num = None
        # Try exact match
        if source_domain in DOMAIN_MAP:
            domain_num = DOMAIN_MAP[source_domain]
        else:
            # Try fuzzy match
            source_lower = source_domain.lower()
            for key, num in DOMAIN_MAP.items():
                if key.lower() in source_lower or source_lower in key.lower():
                    domain_num = num
                    break

        if domain_num is None:
            # Try keyword-based matching
            kw_map = {
                'compress': 1, 'pipeline': 1, 'etl': 1, 'transform': 1, 'workflow': 1, 'streaming': 1,
                'storage': 2, 'raid': 2, 'database': 2, 'disk': 2, 'lake': 2,
                'encrypt': 3, 'key': 3, 'security': 3, 'access': 3, 'identity': 3, 'crypto': 3, 'hash': 3, 'tamper': 3,
                'media': 4, 'transcode': 4, 'video': 4, 'image': 4, 'format': 4, 'document': 4,
                'replication': 5, 'consensus': 5, 'resilien': 5, 'fault': 5,
                'hardware': 6, 'platform': 6, 'sdk port': 6,
                'iot': 7, 'edge': 7, 'sensor': 7, 'rtos': 7,
                'aeds': 8, 'distribution': 8, 'courier': 8, 'dispatch': 8,
                'air.gap': 9, 'offline': 9, 'air gap': 9,
                'filesystem': 10, 'fuse': 10, 'vde': 10, 'mount': 10,
                'compute': 11, 'wasm': 11, 'serverless': 11, 'runtime': 11, 'emulat': 11,
                'interface': 12, 'api': 12, 'transport': 12, 'protocol': 12, 'grpc': 12, 'rest': 12, 'microservice': 12, 'transit': 12,
                'intelligen': 13, 'ai': 13, 'ml': 13, 'knowledge': 13, 'catalog': 13, 'fabric': 13, 'vector': 13,
                'observ': 14, 'monitor': 14, 'metric': 14, 'dashboard': 14, 'telem': 14,
                'compliance': 15, 'governance': 15, 'quality': 15, 'lineage': 15, 'privacy': 15, 'protection': 15, 'gdpr': 15, 'hipaa': 15, 'mesh': 15,
                'deploy': 16, 'cloud': 16, 'docker': 16, 'kubernetes': 16, 'terraform': 16, 'sustain': 16,
                'cli': 17, 'gui': 17, 'command': 17,
            }
            for kw, num in kw_map.items():
                if kw in source_lower:
                    domain_num = num
                    break

        if domain_num is None:
            # Default based on other heuristics
            if 'connector' in source_domain.lower() or 'external' in source_domain.lower():
                domain_num = 12
            elif 'application' in source_domain.lower() or 'marketplace' in source_domain.lower():
                domain_num = 12
            elif 'test' in source_domain.lower() or 'benchmark' in source_domain.lower():
                domain_num = 14  # Observability/Operations
            else:
                domain_num = 1  # Default
                unmapped.append(source_domain)

        for feature in features:
            v4_domains[domain_num].append((feature, source_domain))

    return v4_domains, unmapped


def deduplicate_features(features_list):
    """Remove duplicate features within a domain."""
    seen = set()
    result = []
    for feature, source in features_list:
        key = feature.lower().replace(' ', '').replace('-', '').replace('_', '')
        if key not in seen:
            seen.add(key)
            result.append((feature, source))
    return result


def generate_matrix(v4_domains, aspirational):
    """Generate the full matrix as markdown."""
    lines = []
    lines.append("# DataWarehouse v4.0 Feature Verification Matrix")
    lines.append("")
    lines.append(f"Generated: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
    lines.append("")

    total_verified = 0
    total_aspirational = 0
    domain_stats = {}

    for domain_num in sorted(DOMAIN_NAMES.keys()):
        features = deduplicate_features(v4_domains.get(domain_num, []))
        asp_features = aspirational.get(domain_num, [])  # list of (feature, plugin) tuples
        total_verified += len(features)
        total_aspirational += len(asp_features)
        domain_stats[domain_num] = (len(features), len(asp_features))

    # Statistics
    lines.append("## Statistics")
    lines.append("")
    lines.append(f"| Category | Count |")
    lines.append(f"|----------|-------|")
    lines.append(f"| Code-Derived Features (deduplicated) | {total_verified} |")
    lines.append(f"| Aspirational Features | {total_aspirational} |")
    lines.append(f"| **Total Verification Items** | **{total_verified + total_aspirational}** |")
    lines.append("")
    lines.append("### Per-Domain Breakdown")
    lines.append("")
    lines.append(f"| Domain | Code-Derived | Aspirational | Total |")
    lines.append(f"|--------|-------------|-------------|-------|")
    for domain_num in sorted(DOMAIN_NAMES.keys()):
        name = DOMAIN_NAMES[domain_num]
        cd, asp = domain_stats[domain_num]
        lines.append(f"| {domain_num}. {name} | {cd} | {asp} | {cd + asp} |")
    lines.append("")

    # Purpose
    lines.append("## Purpose")
    lines.append("")
    lines.append("This matrix serves as a comprehensive gap analysis tool for v4.0 certification.")
    lines.append("It combines reverse-engineered features (from 8,603 class declarations) with")
    lines.append("aspirational features that customers would reasonably expect from each plugin.")
    lines.append("")
    lines.append("**Verification status (with production readiness %):**")
    lines.append("- `[ ] 0%` = Not yet verified")
    lines.append("- `[x] 100%` = PASS — feature is fully production-ready")
    lines.append("- `[~] NN%` = PARTIAL — exists but needs completion (estimate % done)")
    lines.append("- `[!] 0%` = MISSING — gap identified, implementation needed")
    lines.append("- `[N/A]` = Not applicable or deferred to v5.0+")
    lines.append("")
    lines.append("**Readiness scoring guide:**")
    lines.append("- **100%**: Fully implemented, tested, production-ready, documented")
    lines.append("- **80-99%**: Core logic done, needs polish (error handling, edge cases, docs)")
    lines.append("- **50-79%**: Partial implementation exists, significant work remaining")
    lines.append("- **20-49%**: Scaffolding/strategy exists, core logic not yet implemented")
    lines.append("- **1-19%**: Interface/base class exists, no real implementation")
    lines.append("- **0%**: Nothing exists, would be new implementation")
    lines.append("")
    lines.append("**Future milestone prioritization (v5.0+):**")
    lines.append("- Start from features nearest to 100% (quick wins, minor completion)")
    lines.append("- Progress toward 0% features (new implementation)")
    lines.append("- This ensures the most value is delivered with the least effort first")
    lines.append("")
    lines.append("**Feature sources:**")
    lines.append("- **Code-Derived:** Reverse-engineered from class names, inheritance, and file paths")
    lines.append("- **Aspirational (per plugin):** What enterprise/military/hyperscale/developer customers would EXPECT")
    lines.append("")

    # Domain sections
    for domain_num in sorted(DOMAIN_NAMES.keys()):
        name = DOMAIN_NAMES[domain_num]
        features = deduplicate_features(v4_domains.get(domain_num, []))
        asp_features = aspirational.get(domain_num, [])

        lines.append(f"---")
        lines.append(f"")
        lines.append(f"## Domain {domain_num}: {name}")
        lines.append(f"")

        if features:
            lines.append(f"### Code-Derived Features ({len(features)})")
            lines.append(f"")
            for feature, source in sorted(features, key=lambda x: x[0].lower()):
                lines.append(f"- [ ] 0% {feature} — (Source: {source})")
            lines.append("")

        if asp_features:
            lines.append(f"### Aspirational Features ({len(asp_features)})")
            lines.append(f"")
            # Group by plugin/source
            by_plugin = defaultdict(list)
            for feat, plugin in asp_features:
                by_plugin[plugin].append(feat)
            for plugin in sorted(by_plugin.keys()):
                lines.append(f"**{plugin}:**")
                for feat in by_plugin[plugin]:
                    lines.append(f"- [ ] 0% {feat}")
                lines.append("")

    return "\n".join(lines)


def main():
    catalog_path = "C:/Temp/DataWarehouse/DataWarehouse/Metadata/FeatureCatalog.txt"
    output_path = "C:/Temp/DataWarehouse/DataWarehouse/Metadata/FeatureVerificationMatrix.md"

    print("Parsing feature catalog...")
    catalog_domains = parse_catalog(catalog_path)
    total_features = sum(len(v) for v in catalog_domains.values())
    print(f"  Parsed {len(catalog_domains)} source domains, {total_features} features")

    print("Mapping to v4.0 domains...")
    v4_domains, unmapped = map_to_v4_domains(catalog_domains)
    if unmapped:
        print(f"  WARNING: {len(unmapped)} unmapped source domains (defaulted to Domain 1): {unmapped}")

    total_deduped = 0
    for domain_num in sorted(v4_domains.keys()):
        deduped = deduplicate_features(v4_domains[domain_num])
        total_deduped += len(deduped)
        print(f"  Domain {domain_num}: {len(deduped)} unique features (from {len(v4_domains[domain_num])} raw)")

    print(f"\nTotal deduplicated code-derived features: {total_deduped}")

    print("\nLoading expanded aspirational features from aspirational_features.py...")
    asp_by_domain = get_all_aspirational()
    total_asp = sum(len(v) for v in asp_by_domain.values())
    print(f"  Loaded {total_asp} aspirational features across {len(asp_by_domain)} domains")

    print("\nGenerating matrix...")
    content = generate_matrix(v4_domains, asp_by_domain)

    with open(output_path, 'w', encoding='utf-8') as f:
        f.write(content)

    total_lines = content.count('\n')
    total_items = content.count('- [ ]')
    print(f"\nOutput: {output_path}")
    print(f"  {total_lines} lines")
    print(f"  {total_items} total verification items")
    print(f"  ({total_deduped} code-derived + {total_asp} aspirational)")
    print("\nDone!")


if __name__ == '__main__':
    main()
