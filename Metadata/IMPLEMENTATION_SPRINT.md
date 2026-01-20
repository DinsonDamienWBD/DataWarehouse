# DataWarehouse - Production Hardening Implementation Sprint

**Created:** 2026-01-20
**Status:** IN PROGRESS

---

## Phase 1: Security Critical (CRITICAL 1-12)

### C1. Key Encryption at Rest (DPAPI/KeyVault)
**File:** `DataWarehouse.SDK/Infrastructure/EncryptionAtRest.cs`
**Status:** ✅ COMPLETE
- [x] Implement DPAPI wrapper for Windows
- [x] Implement KeyVault integration for cloud
- [x] Implement key derivation function (PBKDF2/Argon2)
- [x] Add master key rotation support
- [x] Integrate with VaultKeyStorePlugin

### C2. Dormant Node Data Encryption (AES-256-GCM)
**File:** `DataWarehouse.SDK/Federation/DormantNode.cs`
**Status:** ✅ COMPLETE
- [x] Encrypt node manifest during dehydration
- [x] Encrypt object data in dormant storage
- [x] Add key escrow for recovery
- [x] Implement secure key exchange on hydration

### C3. Path Traversal Vulnerability Fix
**Files:** `Transport.cs`, `ObjectStore.cs`, `VFS.cs`
**Status:** ✅ COMPLETE
- [x] Sanitize all file paths in FileTransportDriver
- [x] Validate VFS paths prevent escape
- [x] Add path canonicalization
- [x] Add security tests

### C4. Message Size Limits
**Files:** `Transport.cs`, `Protocol.cs`
**Status:** ✅ COMPLETE
- [x] Add configurable max message size (default 64MB)
- [x] Implement chunked transfer for large messages
- [x] Add streaming support for large payloads
- [x] Reject oversized messages early

### C5. Proper NAT Authentication
**File:** `DataWarehouse.SDK/Federation/NatTraversal.cs`
**Status:** ✅ COMPLETE
- [x] Add STUN/TURN server authentication
- [x] Implement peer verification during hole punch
- [x] Add challenge-response for relay authentication
- [x] Integrate with capability tokens

### C6. HTTP Listening Implementation (Kestrel)
**File:** `DataWarehouse.SDK/Federation/Transport.cs`
**Status:** ✅ COMPLETE
- [x] Replace HTTP stub with Kestrel listener
- [x] Add TLS/mTLS support
- [x] Implement WebSocket for bidirectional comm
- [x] Add HTTP/2 and HTTP/3 support

### C7. Distributed Lock Handlers
**File:** `DataWarehouse.SDK/Federation/Protocol.cs`
**Status:** ✅ COMPLETE
- [x] Complete DistributedLock message handlers
- [x] Add lock acquisition timeout
- [x] Add lock holder tracking
- [x] Add lock inheritance on node failure

### C8. Chunk Reference Counting
**File:** `DataWarehouse.SDK/Federation/ObjectStore.cs`
**Status:** ✅ COMPLETE
- [x] Add reference counter to chunks
- [x] Implement garbage collection for zero-ref chunks
- [x] Add cross-object chunk deduplication
- [x] Add atomic ref count operations

### C9. Capability Verification Handlers
**File:** `DataWarehouse.SDK/Federation/Capabilities.cs`
**Status:** ✅ COMPLETE
- [x] Complete verify message handlers
- [x] Add capability refresh protocol
- [x] Add revocation list checking
- [x] Add capability chain validation

### C10. Lock TTL and Expiration
**File:** `DataWarehouse.SDK/Federation/Protocol.cs`
**Status:** ✅ COMPLETE
- [x] Add TTL to all distributed locks
- [x] Implement automatic lock expiration
- [x] Add lock renewal heartbeat
- [x] Add configurable timeout policies

### C11. Bounded Caches with LRU Eviction
**Files:** Multiple caches in SDK/Kernel
**Status:** ✅ COMPLETE
- [x] Add LRU eviction to ObjectStore cache
- [x] Add LRU eviction to routing cache
- [x] Add LRU eviction to resolution cache
- [x] Add memory pressure integration

### C12. Race Conditions in Pool Operations
**File:** `DataWarehouse.SDK/Federation/StoragePool.cs`
**Status:** ✅ COMPLETE
- [x] Add locking to pool member add/remove
- [x] Fix concurrent placement policy updates
- [x] Add atomic rebalance operations
- [x] Add concurrent iteration safety

---

## Phase 2: HIGH Priority Issues (H1-H18)

### H1. Connection Leak in TCP Transport
**File:** `DataWarehouse.SDK/Federation/Transport.cs`
**Status:** ✅ COMPLETE
- [x] Add connection pooling with max connections
- [x] Implement idle connection cleanup
- [x] Add connection health checks
- [x] Fix dispose pattern for connections

### H2. Relay Session Timeouts
**File:** `DataWarehouse.SDK/Federation/Transport.cs`
**Status:** ✅ COMPLETE
- [x] Add session timeout tracking
- [x] Implement keepalive for relay sessions
- [x] Add automatic session cleanup
- [x] Add session resumption support

### H3. Comprehensive Audit Logging
**Files:** All federation operations
**Status:** ✅ COMPLETE
- [x] Add audit events for all object operations
- [x] Add audit events for capability grants/revokes
- [x] Add audit events for node join/leave
- [x] Integrate with ImmutableAuditTrail

### H4. Capability Audit Trail
**File:** `DataWarehouse.SDK/Federation/Capabilities.cs`
**Status:** ✅ COMPLETE
- [x] Log all capability creations
- [x] Log all capability usages
- [x] Log all capability expirations
- [x] Add capability usage metrics

### H5. Heartbeat Timestamp Validation
**File:** `DataWarehouse.SDK/Federation/Protocol.cs`
**Status:** ✅ COMPLETE
- [x] Validate heartbeat timestamps against drift
- [x] Reject stale heartbeats
- [x] Add clock sync verification
- [x] Add NTP drift tolerance config

### H6. Performance Optimization for Group Queries
**File:** `DataWarehouse.SDK/Federation/Groups.cs`
**Status:** ✅ COMPLETE
- [x] Add group membership caching
- [x] Add nested group flattening cache
- [x] Optimize recursive membership resolution
- [x] Add indexed group lookups

### H7. Rate Limiting for Relay Gateway
**File:** `DataWarehouse.SDK/Federation/Transport.cs`
**Status:** ✅ COMPLETE
- [x] Add per-node rate limiting
- [x] Add per-operation rate limiting
- [x] Add bandwidth throttling
- [x] Add quota management

### H8. Proper Error Logging (Silent Catches)
**Files:** Multiple files with silent catches
**Status:** ✅ COMPLETE
- [x] Fix silent catches in Transport.cs (lines 156, 332-335, 687-692)
- [x] Add structured logging to all catch blocks
- [x] Add error categorization
- [x] Add error aggregation for monitoring

### H9-H18. Additional HIGH Items

### H9. Fix VFS Concurrent Access Patterns
**File:** `DataWarehouse.SDK/Federation/VFS.cs`
**Status:** ✅ COMPLETE
**Extends:** `VirtualFilesystem` class (already has ReaderWriterLockSlim)
- [x] Add async lock support for long-running operations (`VfsAsyncLockManager`)
- [x] Add optimistic concurrency with version vectors (`VfsVersionVector`)
- [x] Add file-level locking for atomic operations (`AtomicVfsOperation`)
- [x] Add deadlock detection and timeout (`DetectDeadlock`)

### H10. Object Versioning Conflict Resolution
**File:** `DataWarehouse.SDK/Federation/ObjectStore.cs`
**Status:** ✅ COMPLETE
**Extends:** `ObjectManifest` class (already has VectorClock)
- [x] Add three-way merge support (`ThreeWayMergeResolver`)
- [x] Add custom conflict resolver interface (`IConflictResolver`)
- [x] Add conflict visualization for manual resolution (`ConflictVisualization`)
- [x] Add automatic resolution strategies (LWW, First, Merge)

### H11. Proper Consensus Handoff
**File:** `DataWarehouse.SDK/Federation/Protocol.cs`
**Status:** ✅ COMPLETE
**Extends:** `BullyCoordinator` class
- [x] Add graceful leader resignation (`ConsensusHandoff.InitiateHandoffAsync`)
- [x] Add follower promotion protocol (handoff phases)
- [x] Add state transfer during handoff (`TransferStateAsync`)
- [x] Add handoff verification (`HandoffState`, `HandoffResult`)

### H12. Node Graceful Shutdown Protocol
**File:** `DataWarehouse.SDK/Federation/Protocol.cs`
**Status:** ✅ COMPLETE
**Extends:** `GossipDiscovery`, `QuorumReplicator`
- [x] Add shutdown announcement broadcast (`GracefulShutdownProtocol`)
- [x] Add data migration before shutdown (`MigrateDataAsync`)
- [x] Add connection draining (`DrainConnectionsAsync`)
- [x] Add shutdown acknowledgment protocol (`AcknowledgeShutdown`)

### H13. Metadata Sync Race Conditions
**File:** `DataWarehouse.SDK/Federation/Protocol.cs`
**Status:** ✅ COMPLETE
**Extends:** `CrdtMetadataSync` class
- [x] Add versioned sync barriers (`SyncBarrier`, `SyncBarrierHandle`)
- [x] Add sync operation ordering (`OrderedMetadataSync`)
- [x] Add conflict-free merge verification (barrier lock)
- [x] Add sync progress checkpoints (`SyncCheckpoint`)

### H14. Replication Lag Monitoring
**File:** `DataWarehouse.SDK/Federation/Protocol.cs`
**Status:** ✅ COMPLETE
**Extends:** `QuorumReplicator` class
- [x] Add lag tracking per replica (`ReplicationLagMonitor`)
- [x] Add lag threshold alerts (`LagAlert` event)
- [x] Add catch-up replication mode (`IsCatchingUp`)
- [x] Add lag metrics export (`GetAllLags`)

### H15. Proper Quorum Validation
**File:** `DataWarehouse.SDK/Federation/Protocol.cs`
**Status:** ✅ COMPLETE
**Extends:** `QuorumReplicator` class
- [x] Add dynamic quorum recalculation (`QuorumValidator`)
- [x] Add quorum loss detection (`IsQuorumLost`)
- [x] Add read-your-writes consistency (`ValidateReadYourWrites`)
- [x] Add quorum witness support (`GetWitnessNodes`)

### H16. Object Repair Verification
**File:** `DataWarehouse.SDK/Federation/ObjectStore.cs`
**Status:** ✅ COMPLETE
**Extends:** `ContentAddressableObjectStore`
- [x] Add post-repair integrity check (`ObjectRepairVerifier.VerifyRepairAsync`)
- [x] Add repair audit logging (`RepairRecord`, `RepairVerified` event)
- [x] Add repair success metrics (`RepairMetrics`)
- [x] Add automatic re-repair on failure (`AutoReRepairAsync`)

### H17. Routing Table Stale Entry Cleanup
**File:** `DataWarehouse.SDK/Federation/Routing.cs`
**Status:** ✅ COMPLETE
**Extends:** `RoutingTable` class (already has LRU)
- [x] Add TTL-based entry expiration (`RoutingTableCleanup`)
- [x] Add background stale entry sweep (`SweepStaleEntries`)
- [x] Add route freshness scoring (`GetFreshnessScore`)
- [x] Add proactive route refresh (`ProactiveRefreshAsync`)

### H18. Federation Health Dashboard Metrics
**File:** `DataWarehouse.SDK/Federation/FederationHealth.cs` (NEW)
**Status:** ✅ COMPLETE
**Extends:** Uses all Federation classes
- [x] Add unified health aggregator (`FederationHealthAggregator`)
- [x] Add per-node health scores (`GetNodeHealthScore`, `NodeHealthInfo`)
- [x] Add federation-wide metrics (`FederationMetric`, `RecordMetric`)
- [x] Add health trend analysis (`AnalyzeHealthTrends`)

---

## Phase 3: Hyperscale Features (H1-H8)

### HS1. Full Erasure Coding Implementation
**File:** `DataWarehouse.SDK/Infrastructure/HyperscaleFeatures.cs`
**Status:** ✅ COMPLETE
- [x] Add Rabin fingerprinting for content-defined chunking (`RabinFingerprinting` class)
- [x] Implement streaming encoder for large files (`StreamingErasureCoder` class)
- [x] Add adaptive parameter tuning based on failure rates (`AdaptiveParameterTuner` class)
- [x] Add parallel encoding/decoding (`ParallelErasureCoder` class)

### HS2. Geo-Distributed Consensus Enhancement
**File:** `DataWarehouse.SDK/Infrastructure/HyperscaleFeatures.cs`
**Status:** ✅ COMPLETE
- [x] Add region-aware leader election (`RegionAwareLeaderElection`)
- [x] Implement hierarchical consensus (`HierarchicalConsensus` with Local/Regional/Global)
- [x] Add cross-region lease management (`CrossRegionLeaseManager`)
- [x] Add partition healing automation (`PartitionHealer`)

### HS3. Petabyte-Scale Index Sharding
**File:** `DataWarehouse.SDK/Infrastructure/HyperscaleFeatures.cs`
**Status:** ✅ COMPLETE
- [x] Add dynamic shard splitting (`DynamicShardManager.SplitShardAsync`)
- [x] Add shard rebalancing (`MergeShardsAsync` with minimal data movement)
- [x] Add consistent hash shard routing (`GetShardForKey`)
- [x] Add shard status tracking (Active/Splitting/Merging/Retired)

### HS4. Predictive Tiering ML Model
**File:** `DataWarehouse.SDK/Infrastructure/HyperscaleFeatures.cs`
**Status:** ✅ COMPLETE
- [x] Add access pattern feature extraction (`ExtractFeatures` with 6 features)
- [x] Add ML model with gradient descent (`TrainOnFeedback`)
- [x] Add temperature scoring (`PredictOptimalTier` Hot/Warm/Cool/Archive)
- [x] Add tiering plan generation (`GenerateTieringPlanAsync`)

### HS5. Chaos Engineering Scheduling
**File:** `DataWarehouse.SDK/Infrastructure/HyperscaleFeatures.cs`
**Status:** ✅ COMPLETE
- [x] Add experiment scheduler (`ChaosExperimentScheduler`)
- [x] Add experiment result aggregation (6 chaos types)
- [x] Add target node controls (TargetNodes list)
- [x] Add experiment abort/status tracking

### HS6. Hyperscale Observability Dashboards
**File:** `DataWarehouse.SDK/Infrastructure/HyperscaleFeatures.cs`
**Status:** ✅ COMPLETE
- [x] Add Prometheus metrics export (`PrometheusExporter.ExportMetrics`)
- [x] Add Grafana dashboard generation (`GrafanaDashboardGenerator`)
- [x] Add distributed tracing (`DistributedTracer` with spans/events)
- [x] Add metric types (Counter/Gauge/Histogram)

### HS7. Kubernetes Operator CRD Finalization
**File:** `DataWarehouse.SDK/Infrastructure/HyperscaleFeatures.cs`
**Status:** ✅ COMPLETE
- [x] Add complete CRD schema definitions (`CrdSchemaGenerator`)
- [x] Add admission webhook validation (`AdmissionWebhookValidator`)
- [x] Add status subresource updates (`StatusUpdater`)
- [x] Add finalizer cleanup logic (`FinalizerManager`)

### HS8. S3 API 100% Compatibility
**File:** `DataWarehouse.SDK/Infrastructure/HyperscaleFeatures.cs`
**Status:** ✅ COMPLETE
- [x] Add all S3 operations (`CopyObjectAsync`, `DeleteObjectsAsync`, tagging)
- [x] Add S3 Select query support (`SelectObjectContentAsync`)
- [x] Add lifecycle configuration API (`PutBucketLifecycleAsync`)
- [x] Add replication configuration API (`PutBucketReplicationAsync`)

---

## Phase 4: Scenario Implementation (1-5)

### Scenario 1: Cloud Share (U1 → DWH → U2)
**File:** `DataWarehouse.SDK/Federation/CloudShare.cs` (NEW)
**Status:** ✅ COMPLETE
**Extends:** `VirtualFilesystem`, `CapabilityIssuer`, `NamespaceProjectionService`
- [x] Add `CloudShareManager` for share lifecycle
- [x] Add `ShareLink` with expiry and access limits
- [x] Add user-specific `VfsProjection` per share
- [x] Add share access audit logging

### Scenario 2: Sneakernet (U1 → USB → U2)
**File:** `DataWarehouse.SDK/Federation/DormantNode.cs` (Enhanced)
**Status:** ✅ COMPLETE
**Extends:** `NodeDehydrator`, `NodeHydrator`, `DormantNodeWatcher`
**Reuse:** Existing encryption support (AES-256-GCM), DormantNodeManifest
- [x] Add `IncrementalSyncManager` for partial transfers (delta sync using checksums)
- [x] Add `SneakernetConflictQueue` for offline changes with three-way merge
- [x] Add `AutoResyncTrigger` integration with DormantNodeWatcher
- [x] Add `OfflineChangeTracker` for tracking modifications during disconnect

### Scenario 3: Unified Pool (DWH + DW1)
**File:** `DataWarehouse.SDK/Federation/StoragePool.cs` (Enhanced)
**Status:** ✅ COMPLETE
**Extends:** `StoragePool`, `UnionPool`, `PoolPlacementPolicy`, `StoragePoolRegistry`
**Reuse:** Existing pool member management, consistent hashing, role-based selection
- [x] Add `PoolDiscoveryProtocol` with mDNS and gossip-based discovery
- [x] Add `PoolAutoJoinManager` for seamless node integration
- [x] Add `FederatedPoolRouter` for transparent object routing
- [x] Add `PoolDeduplicationService` with cross-pool content hashing
- [x] Add `PoolCapacityMonitor` for aggregated stats and alerts

### Scenario 4: P2P Direct Link
**File:** `DataWarehouse.SDK/Federation/NatTraversal.cs` (Enhanced)
**Status:** ✅ COMPLETE
**Extends:** `StunClient`, `HolePunchingDriver`, `RelayGateway`, `NatTraversalService`, `P2PConnectionManager`
**Reuse:** Existing STUN/TURN, UDP hole punching, relay gateway infrastructure
- [x] Add `IceLiteCandidateGatherer` for RFC 8445 candidate collection
- [x] Add `ConnectivityChecker` with priority-ordered candidate pairs
- [x] Add `RelayFallbackChain` with latency-based selection
- [x] Add `LinkQualityMonitor` tracking jitter, packet loss, RTT, bandwidth

### Scenario 5: Multi-Region Federation
**File:** `DataWarehouse.SDK/Federation/MultiRegion.cs` (NEW)
**Status:** ✅ COMPLETE
**Extends:** `GeoDistributedConsensus`, `QuorumReplicator`, `RoutingTable`
- [x] Add `Region` entity with latency matrix
- [x] Add region-aware data placement policy
- [x] Add cross-region async replication
- [x] Add regional failover orchestration

---

## Phase 5: Enterprise Features

### E1. Single File Deploy
**File:** `DataWarehouse.SDK/Infrastructure/SingleFileDeploy.cs` (NEW)
**Status:** ✅ COMPLETE
**Extends:** Uses `ZeroConfigurationStartup`
- [x] Add `EmbeddedResourceManager` for bundled plugins
- [x] Add `ConfigurationEmbedder` for default config
- [x] Add `PluginExtractor` for runtime extraction
- [x] Add `AutoUpdateManager` with version check

### E2. ACID Transactions
**File:** `DataWarehouse.SDK/Infrastructure/AcidTransactions.cs` (NEW)
**Status:** ✅ COMPLETE
**Extends:** Uses `ITransactionScope` from Kernel
- [x] Add `WriteAheadLog` with append-only storage
- [x] Add `IsolationLevel` enum (ReadUncommitted to Serializable)
- [x] Add `DistributedTransactionCoordinator` (2PC protocol)
- [x] Add `Savepoint` support for partial rollback

### E3. Full Encryption at Rest
**File:** `DataWarehouse.SDK/Infrastructure/EncryptionAtRest.cs` (Enhanced)
**Status:** ✅ COMPLETE
**Extends:** `IKeyEncryptionProvider`, `EncryptionAtRestManager`, `DpapiKeyEncryptionProvider`, `KeyVaultEncryptionProvider`
**Reuse:** Existing DPAPI, KeyVault, Local providers, envelope encryption
- [x] Add `FullDiskEncryptionProvider` abstraction for whole-volume encryption
- [x] Add `PerFileEncryptionMode` with automatic per-file key generation
- [x] Add `KeyHierarchy` implementing master → tenant → data key structure
- [x] Add `SecureKeyStorage` with hardware (TPM), file, and cloud backends
- [x] Add `KeyRotationScheduler` for automated key rotation

### E4. Distributed Services Tier 2
**File:** `DataWarehouse.SDK/Infrastructure/DistributedServicesPhase5.cs` (NEW)
**Status:** ✅ COMPLETE
**Extends:** Uses `QuorumReplicator`, `CrdtMetadataSync`
- [x] Add `DistributedLockService` with fencing tokens
- [x] Add `DistributedCounterService` (CRDT G-Counter)
- [x] Add `DistributedQueueService` for work distribution
- [x] Add `DistributedConfigService` for cluster-wide config

---

## Phase 6: Storage Backend Integration

### SB1. Full MinIO Support
**File:** `DataWarehouse.SDK/Infrastructure/StorageBackends.cs` (Enhanced)
**Status:** ✅ COMPLETE
**Extends:** `MinioExtendedClient`, `S3StoragePlugin`
**Reuse:** Existing S3 API implementation, multi-part uploads, SSE support
- [x] Add `MinioAdminManager` for cluster management (info, heal, metrics)
- [x] Add `BucketNotificationManager` for S3-compatible event triggers
- [x] Add `IlmPolicyManager` for object lifecycle management
- [x] Add `MinioIdentityProvider` for LDAP/OIDC/service accounts
- [x] Add `MinioClusterHealthMonitor` for multi-node status

### SB2. Full Ceph Support
**File:** `DataWarehouse.SDK/Infrastructure/StorageBackends.cs` (Enhanced)
**Status:** ✅ COMPLETE
**Extends:** `CephStorageClient`, storage orchestration patterns
**Reuse:** S3 API compatibility, health monitoring patterns
- [x] Add `RadosGatewayManager` for S3-compatible object storage
- [x] Add `RbdBlockStorageManager` for block device access
- [x] Add `CephFsManager` for POSIX filesystem access
- [x] Add `CrushMapAwarePlacement` for failure-domain-aware placement
- [x] Add `CephClusterMonitor` for health and performance metrics

### SB3. Full TrueNAS Support
**File:** `DataWarehouse.SDK/Infrastructure/StorageBackends.cs` (Enhanced)
**Status:** ✅ COMPLETE
**Extends:** `TrueNasClient`, storage orchestration patterns
**Reuse:** Storage orchestration patterns, health monitoring
- [x] Add `ZfsPoolManager` for pool operations (create, scrub, status)
- [x] Add `DatasetManager` for dataset CRUD operations
- [x] Add `ZfsSnapshotManager` for point-in-time recovery
- [x] Add `TrueNasHealthMonitor` for SMART, pool, and replication status

---

## Phase 7: Compliance & Security

### CS1. Full Audit Trails
**File:** `DataWarehouse.SDK/Infrastructure/CompliancePhase7.cs` (NEW)
**Status:** ✅ COMPLETE
**Extends:** Uses `ImmutableAuditTrail` from HighStakesFeatures
- [x] Add `AuditSyncManager` for cross-node replication
- [x] Add `AuditExporter` (CSV, JSON, SIEM/Splunk format)
- [x] Add `AuditRetentionPolicy` with auto-purge
- [x] Add `AuditQueryEngine` with filter/search

### CS2. Full HSM Integration
**File:** `DataWarehouse.SDK/Infrastructure/CompliancePhase7.cs` (NEW)
**Status:** ✅ COMPLETE
**Extends:** Implements `IHsmProvider` from HighStakesFeatures
- [x] Add `Pkcs11HsmProvider` for generic HSM
- [x] Add `AwsCloudHsmProvider` for AWS
- [x] Add `AzureDedicatedHsmProvider` for Azure
- [ ] Add `ThalesLunaProvider` for on-premise (future)

### CS3. Regulatory Compliance Framework
**File:** `DataWarehouse.SDK/Infrastructure/CompliancePhase7.cs` (NEW)
**Status:** ✅ COMPLETE
**Extends:** Uses `WormComplianceManager`, `ImmutableAuditTrail`
- [x] Add `ComplianceChecker` with pluggable rules
- [x] Add `Soc2ComplianceRules` validation
- [x] Add `HipaaComplianceRules` validation
- [x] Add `GdprComplianceRules` (data residency, right to delete)
- [x] Add `PciDssComplianceRules` validation

### CS4. Durability Guarantees (11 9s)
**File:** `DataWarehouse.SDK/Infrastructure/CompliancePhase7.cs` (NEW)
**Status:** ✅ COMPLETE
**Extends:** Uses `AdaptiveErasureCoding`, `QuorumReplicator`
- [x] Add `DurabilityCalculator` (Markov model)
- [x] Add `ReplicationAdvisor` for factor recommendations
- [x] Add `GeoDistributionChecker` for failure domain analysis
- [x] Add `DurabilityMonitor` for continuous verification

---

## Phase 8: Edge & Managed Services

### EM1. Edge Locations
**File:** `DataWarehouse.SDK/Infrastructure/EdgeManagedPhase8.cs` (NEW)
**Status:** ✅ COMPLETE
**Extends:** Uses `RoutingTable`, `QuorumReplicator`
- [x] Add `EdgeNodeDetector` (latency-based classification)
- [x] Add `EdgeOriginSync` for cache population
- [x] Add `CacheInvalidationProtocol` (pub/sub based)
- [x] Add `EdgeHealthMonitor` with failover

### EM2. Managed Services Platform
**File:** `DataWarehouse.SDK/Infrastructure/EdgeManagedPhase8.cs` (NEW)
**Status:** ✅ COMPLETE
**Extends:** Uses `CryptographicTenantIsolation`, `RoleBasedAccessControl`
- [x] Add `ServiceProvisioner` for tenant onboarding
- [x] Add `TenantLifecycleManager` (create, suspend, delete)
- [x] Add `UsageMeteringService` for resource tracking
- [x] Add `BillingIntegration` hooks (Stripe, AWS Marketplace)

### EM3. Full IAM Integration
**File:** `DataWarehouse.SDK/Infrastructure/IamIntegration.cs` (NEW)
**Status:** 🔄 IN PROGRESS
**Extends:** `CapabilityIssuer`, `GroupRegistry`, `AccessControlPluginBase`
**Reuse:** Existing capability tokens, group membership, ACL infrastructure
- [ ] Add `IIdentityProvider` interface for pluggable identity backends
- [ ] Add `AwsIamProvider` (STS AssumeRole, IAM policies)
- [ ] Add `AzureAdProvider` (OAuth2/OIDC, Graph API integration)
- [ ] Add `GoogleCloudIamProvider` (service accounts, workload identity)
- [ ] Add `OidcSamlBridge` for enterprise SSO with claim mapping
- [ ] Add `IamSessionManager` for session lifecycle and token refresh

### EM4. Storage Type Detection & AI Processing
**File:** `DataWarehouse.SDK/Infrastructure/StorageIntelligence.cs` (NEW)
**Status:** 🔄 IN PROGRESS
**Extends:** `AIProviderRegistry`, `SearchOrchestratorBase`, `Manifest`
**Reuse:** Existing AI provider infrastructure, metadata indexing
- [ ] Add `StorageTypeDetector` with magic bytes, MIME type, and content analysis
- [ ] Add `IntelligentContentClassifier` using AI for semantic classification
- [ ] Add `AutoTieringRecommender` based on access patterns and content type
- [ ] Add `ContentExtractionPipeline` for text/metadata extraction
- [ ] Add `SmartSearchIndexer` for automatic full-text and vector indexing

---

## Implementation Order

### Week 1-2: Security Critical
1. C1: Key Encryption at Rest
2. C2: Dormant Node Encryption
3. C3: Path Traversal Fix
4. C4: Message Size Limits
5. C5: NAT Authentication

### Week 3-4: Functionality Critical
6. C6: HTTP Listening (Kestrel)
7. C7: Distributed Lock Handlers
8. C8: Chunk Reference Counting
9. C9: Capability Verification
10. C10: Lock TTL/Expiration

### Week 5-6: Reliability
11. C11: Bounded Caches
12. C12: Race Condition Fixes
13. H1-H8: HIGH priority fixes

### Week 7-8: Compliance & Integration
14. Audit Trails
15. HSM Integration
16. Storage Backends (MinIO, Ceph, TrueNAS)
17. IAM Integration

---

*This document will be updated as each task is completed.*
*Last Updated: 2026-01-20*
