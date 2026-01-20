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
**Status:** ⬜ TODO (Enhance existing `GeoDistributedConsensus`)
- [ ] Add region-aware leader election (prefer local leaders)
- [ ] Implement hierarchical consensus (local + global quorums)
- [ ] Add cross-region lease management with latency awareness
- [ ] Add partition healing automation with split-brain prevention

### HS3. Petabyte-Scale Index Sharding
**File:** `DataWarehouse.SDK/Infrastructure/HyperscaleFeatures.cs`
**Status:** ⬜ TODO (Enhance existing `DistributedBPlusTree`)
- [ ] Add dynamic shard splitting on threshold
- [ ] Add shard rebalancing with minimal data movement
- [ ] Add cross-shard range query optimization
- [ ] Add shard compaction scheduling

### HS4. Predictive Tiering ML Model
**File:** `DataWarehouse.SDK/Infrastructure/HyperscaleFeatures.cs`
**Status:** ⬜ TODO (Enhance existing `PredictiveTiering`)
- [ ] Add access pattern feature extraction
- [ ] Add simple ML model (linear regression for access prediction)
- [ ] Add temperature scoring for hot/warm/cold classification
- [ ] Add pre-warming based on predicted patterns

### HS5. Chaos Engineering Scheduling
**File:** `DataWarehouse.SDK/Infrastructure/HyperscaleFeatures.cs`
**Status:** ⬜ TODO (Enhance existing `ChaosEngineeringFramework`)
- [ ] Add experiment scheduler with cron support
- [ ] Add experiment result aggregation
- [ ] Add blast radius controls
- [ ] Add automatic rollback triggers

### HS6. Hyperscale Observability Dashboards
**File:** `DataWarehouse.SDK/Infrastructure/HyperscaleFeatures.cs`
**Status:** ⬜ TODO (Enhance existing `HyperscaleObservability`)
- [ ] Add Prometheus metrics export format
- [ ] Add Grafana dashboard JSON generation
- [ ] Add distributed tracing span export
- [ ] Add log aggregation hooks

### HS7. Kubernetes Operator CRD Finalization
**File:** `DataWarehouse.SDK/Infrastructure/HyperscaleFeatures.cs`
**Status:** ⬜ TODO (Enhance existing `KubernetesOperator`)
- [ ] Add complete CRD schema definitions
- [ ] Add admission webhook validation
- [ ] Add status subresource updates
- [ ] Add finalizer cleanup logic

### HS8. S3 API 100% Compatibility
**File:** `DataWarehouse.SDK/Infrastructure/HyperscaleFeatures.cs`
**Status:** ⬜ TODO (Enhance existing `S3CompatibleApi`)
- [ ] Add all S3 operations (copy, batch delete, tagging)
- [ ] Add S3 Select query support
- [ ] Add lifecycle configuration API
- [ ] Add replication configuration API

---

## Phase 4: Scenario Implementation (1-5)

### Scenario 1: Cloud Share (U1 → DWH → U2)
**File:** `DataWarehouse.SDK/Federation/CloudShare.cs` (NEW)
**Status:** ⬜ TODO
**Extends:** `VirtualFilesystem`, `CapabilityIssuer`, `NamespaceProjectionService`
- [ ] Add `CloudShareManager` for share lifecycle
- [ ] Add `ShareLink` with expiry and access limits
- [ ] Add user-specific `VfsProjection` per share
- [ ] Add share access audit logging

### Scenario 2: Sneakernet (U1 → USB → U2)
**File:** `DataWarehouse.SDK/Federation/DormantNode.cs` (Enhance)
**Status:** ⬜ TODO
**Extends:** `DormantNodeManager`, `FileTransportDriver`
- [ ] Add encrypted manifest export with password
- [ ] Add incremental sync for partial transfers
- [ ] Add conflict queue for offline changes
- [ ] Add auto-resync trigger on USB mount detection

### Scenario 3: Unified Pool (DWH + DW1)
**File:** `DataWarehouse.SDK/Federation/StoragePool.cs` (Enhance)
**Status:** ⬜ TODO
**Extends:** `StoragePool`, `UnionPool`, `PoolPlacementPolicy`
- [ ] Add pool discovery and auto-join protocol
- [ ] Add transparent `FederationRouter` integration
- [ ] Add pool-wide `ContentAddressableDeduplication`
- [ ] Add pool capacity aggregation and alerts

### Scenario 4: P2P Direct Link
**File:** `DataWarehouse.SDK/Federation/NatTraversal.cs` (Enhance)
**Status:** ⬜ TODO
**Extends:** `StunClient`, `HolePunchingDriver`, `RelayGateway`
- [ ] Add ICE-lite candidate gathering
- [ ] Add connectivity check with priority ordering
- [ ] Add relay fallback chain with latency preference
- [ ] Add link quality metrics (jitter, packet loss, RTT)

### Scenario 5: Multi-Region Federation
**File:** `DataWarehouse.SDK/Federation/MultiRegion.cs` (NEW)
**Status:** ⬜ TODO
**Extends:** `GeoDistributedConsensus`, `QuorumReplicator`, `RoutingTable`
- [ ] Add `Region` entity with latency matrix
- [ ] Add region-aware data placement policy
- [ ] Add cross-region async replication
- [ ] Add regional failover orchestration

---

## Phase 5: Enterprise Features

### E1. Single File Deploy
**File:** `DataWarehouse.SDK/Infrastructure/SingleFileDeploy.cs` (NEW)
**Status:** ⬜ TODO
**Extends:** Uses `ZeroConfigurationStartup`
- [ ] Add `EmbeddedResourceManager` for bundled plugins
- [ ] Add `ConfigurationEmbedder` for default config
- [ ] Add `PluginExtractor` for runtime extraction
- [ ] Add `AutoUpdateManager` with version check

### E2. ACID Transactions
**File:** `DataWarehouse.SDK/Infrastructure/AcidTransactions.cs` (NEW)
**Status:** ⬜ TODO
**Extends:** Uses `ITransactionScope` from Kernel
- [ ] Add `WriteAheadLog` with append-only storage
- [ ] Add `IsolationLevel` enum (ReadUncommitted to Serializable)
- [ ] Add `DistributedTransactionCoordinator` (2PC protocol)
- [ ] Add `Savepoint` support for partial rollback

### E3. Full Encryption at Rest
**File:** `DataWarehouse.SDK/Infrastructure/EncryptionAtRest.cs` (Enhance)
**Status:** ⬜ TODO
**Extends:** Uses `FipsCompliantCryptoModule`, `IKeyStore`
- [ ] Add `FullDiskEncryptionProvider` abstraction
- [ ] Add `PerFileEncryptionMode` with per-file keys
- [ ] Add `KeyHierarchy` (master → tenant → data keys)
- [ ] Add `SecureKeyStorage` with multiple backends

### E4. Distributed Services Tier 2
**File:** `DataWarehouse.SDK/Infrastructure/DistributedServices.cs` (NEW)
**Status:** ⬜ TODO
**Extends:** Uses `QuorumReplicator`, `CrdtMetadataSync`
- [ ] Add `DistributedLockService` with fencing tokens
- [ ] Add `DistributedCounterService` (CRDT G-Counter)
- [ ] Add `DistributedQueueService` for work distribution
- [ ] Add `DistributedConfigService` for cluster-wide config

---

## Phase 6: Storage Backend Integration

### SB1. Full MinIO Support
**File:** `Plugins/DataWarehouse.Plugins.S3Storage/MinioSupport.cs` (NEW)
**Status:** ⬜ TODO
**Extends:** `S3StoragePlugin`
- [ ] Add `MinioAdminClient` for cluster management
- [ ] Add `BucketNotificationManager` for event triggers
- [ ] Add `IlmPolicyManager` for lifecycle rules
- [ ] Add `MinioIdentityProvider` for IAM integration

### SB2. Full Ceph Support
**File:** `Plugins/DataWarehouse.Plugins.CephStorage/CephStoragePlugin.cs` (NEW)
**Status:** ⬜ TODO
**Extends:** `StorageProviderPluginBase`
- [ ] Add `RadosGatewayClient` for object storage
- [ ] Add `RbdBlockStorage` for block devices
- [ ] Add `CephFsMount` for filesystem access
- [ ] Add `CrushMapAwareness` for placement optimization

### SB3. Full TrueNAS Support
**File:** `Plugins/DataWarehouse.Plugins.TrueNasStorage/TrueNasStoragePlugin.cs` (NEW)
**Status:** ⬜ TODO
**Extends:** `StorageProviderPluginBase`
- [ ] Add `TrueNasApiClient` for REST API
- [ ] Add `ZfsPoolManager` for pool operations
- [ ] Add `DatasetManager` for dataset CRUD
- [ ] Add `SnapshotManager` for point-in-time recovery

---

## Phase 7: Compliance & Security

### CS1. Full Audit Trails
**File:** `DataWarehouse.SDK/Infrastructure/AuditTrails.cs` (NEW)
**Status:** ⬜ TODO
**Extends:** Uses `ImmutableAuditTrail` from HighStakesFeatures
- [ ] Add `AuditSyncManager` for cross-node replication
- [ ] Add `AuditExporter` (CSV, JSON, SIEM/Splunk format)
- [ ] Add `AuditRetentionPolicy` with auto-purge
- [ ] Add `AuditQueryEngine` with filter/search

### CS2. Full HSM Integration
**File:** `DataWarehouse.SDK/Infrastructure/HsmProviders.cs` (NEW)
**Status:** ⬜ TODO
**Extends:** Implements `IHsmProvider` from HighStakesFeatures
- [ ] Add `Pkcs11Provider` for generic HSM
- [ ] Add `AwsCloudHsmProvider` for AWS
- [ ] Add `AzureDedicatedHsmProvider` for Azure
- [ ] Add `ThalesLunaProvider` for on-premise

### CS3. Regulatory Compliance Framework
**File:** `DataWarehouse.SDK/Infrastructure/RegulatoryCompliance.cs` (NEW)
**Status:** ⬜ TODO
**Extends:** Uses `WormComplianceManager`, `ImmutableAuditTrail`
- [ ] Add `ComplianceChecker` with pluggable rules
- [ ] Add `Soc2ComplianceRules` validation
- [ ] Add `HipaaComplianceRules` validation
- [ ] Add `GdprComplianceRules` (data residency, right to delete)
- [ ] Add `PciDssComplianceRules` validation

### CS4. Durability Guarantees (11 9s)
**File:** `DataWarehouse.SDK/Infrastructure/DurabilityGuarantees.cs` (NEW)
**Status:** ⬜ TODO
**Extends:** Uses `AdaptiveErasureCoding`, `QuorumReplicator`
- [ ] Add `DurabilityCalculator` (Markov model)
- [ ] Add `ReplicationAdvisor` for factor recommendations
- [ ] Add `GeoDistributionChecker` for failure domain analysis
- [ ] Add `DurabilityMonitor` for continuous verification

---

## Phase 8: Edge & Managed Services

### EM1. Edge Locations
**File:** `DataWarehouse.SDK/Infrastructure/EdgeComputing.cs` (NEW)
**Status:** ⬜ TODO
**Extends:** Uses `RoutingTable`, `QuorumReplicator`
- [ ] Add `EdgeNodeDetector` (latency-based classification)
- [ ] Add `EdgeOriginSync` for cache population
- [ ] Add `CacheInvalidationProtocol` (pub/sub based)
- [ ] Add `EdgeHealthMonitor` with failover

### EM2. Managed Services Platform
**File:** `DataWarehouse.SDK/Infrastructure/ManagedServices.cs` (NEW)
**Status:** ⬜ TODO
**Extends:** Uses `CryptographicTenantIsolation`, `RoleBasedAccessControl`
- [ ] Add `ServiceProvisioner` for tenant onboarding
- [ ] Add `TenantLifecycleManager` (create, suspend, delete)
- [ ] Add `UsageMeteringService` for resource tracking
- [ ] Add `BillingIntegration` hooks (Stripe, AWS Marketplace)

### EM3. Full IAM Integration
**File:** `DataWarehouse.SDK/Infrastructure/IamIntegration.cs` (NEW)
**Status:** ⬜ TODO
**Extends:** Uses `CapabilityIssuer`, `GroupRegistry`
- [ ] Add `AwsIamProvider` (STS AssumeRole)
- [ ] Add `AzureAdProvider` (OAuth2/OIDC)
- [ ] Add `GoogleCloudIamProvider` (service accounts)
- [ ] Add `OidcSamlBridge` for enterprise SSO

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
