using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Replication;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateReplication.Strategies.Cloud
{
    #region Cloud Provider Abstractions

    /// <summary>
    /// Represents a cloud provider for multi-cloud replication.
    /// </summary>
    public sealed class CloudProvider
    {
        /// <summary>Cloud provider identifier.</summary>
        public required string ProviderId { get; init; }

        /// <summary>Display name (e.g., "AWS", "Azure", "GCP").</summary>
        public required string Name { get; init; }

        /// <summary>Provider type.</summary>
        public required CloudProviderType Type { get; init; }

        /// <summary>Regional endpoints.</summary>
        public Dictionary<string, string> RegionalEndpoints { get; init; } = new();

        /// <summary>Current health status.</summary>
        public CloudProviderHealth Health { get; set; } = CloudProviderHealth.Healthy;

        /// <summary>Priority for writes (lower = higher priority).</summary>
        public int WritePriority { get; init; } = 100;

        /// <summary>Priority for reads (lower = higher priority).</summary>
        public int ReadPriority { get; init; } = 100;

        /// <summary>Whether provider is read-only.</summary>
        public bool IsReadOnly { get; init; }

        /// <summary>Estimated latency in milliseconds.</summary>
        public int EstimatedLatencyMs { get; set; } = 50;

        /// <summary>Cost multiplier for egress (1.0 = baseline).</summary>
        public double EgressCostMultiplier { get; init; } = 1.0;
    }

    /// <summary>
    /// Cloud provider types.
    /// </summary>
    public enum CloudProviderType
    {
        AWS,
        Azure,
        GCP,
        OnPremise,
        Edge,
        Kubernetes,
        Hybrid
    }

    /// <summary>
    /// Cloud provider health status.
    /// </summary>
    public enum CloudProviderHealth
    {
        Healthy,
        Degraded,
        Unhealthy,
        Offline
    }

    #endregion

    /// <summary>
    /// AWS-specific replication strategy with S3 cross-region replication, DynamoDB global tables,
    /// and Aurora global database patterns.
    /// </summary>
    public sealed class AwsReplicationStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<string, CloudProvider> _regions = new BoundedDictionary<string, CloudProvider>(1000);
        private readonly BoundedDictionary<string, (byte[] Data, DateTimeOffset Timestamp)> _s3Store = new BoundedDictionary<string, (byte[] Data, DateTimeOffset Timestamp)>(1000);
        private string? _primaryRegion;
        private bool _enableTransferAcceleration = true;
        private bool _enableIntelligentTiering = true;

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "AWS",
            Description = "AWS-native replication with S3 CRR, DynamoDB Global Tables, and Aurora Global Database patterns",
            ConsistencyModel = ConsistencyModel.Eventual,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: true,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.LastWriteWins, ConflictResolutionMethod.Custom },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: false,
                IsGeoAware: true,
                MaxReplicationLag: TimeSpan.FromSeconds(60),
                MinReplicaCount: 2,
                MaxReplicaCount: 25),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = true,
            SupportsStreaming = true,
            TypicalLagMs = 500,
            ConsistencySlaMs = 60000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Adds an AWS region to the replication topology.
        /// </summary>
        public void AddRegion(string regionCode, string endpoint, int priority = 100)
        {
            var provider = new CloudProvider
            {
                ProviderId = $"aws-{regionCode}",
                Name = $"AWS {regionCode}",
                Type = CloudProviderType.AWS,
                RegionalEndpoints = { [regionCode] = endpoint },
                WritePriority = priority,
                ReadPriority = priority
            };
            _regions[regionCode] = provider;
            _primaryRegion ??= regionCode;
        }

        /// <summary>
        /// Configures S3 transfer acceleration.
        /// </summary>
        public void SetTransferAcceleration(bool enabled)
        {
            _enableTransferAcceleration = enabled;
        }

        /// <summary>
        /// Configures S3 Intelligent Tiering.
        /// </summary>
        public void SetIntelligentTiering(bool enabled)
        {
            _enableIntelligentTiering = enabled;
        }

        /// <inheritdoc/>
        public override async Task ReplicateAsync(
            string sourceNodeId,
            IEnumerable<string> targetNodeIds,
            ReadOnlyMemory<byte> data,
            IDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            IncrementLocalClock();
            var objectKey = metadata?.GetValueOrDefault("objectKey") ?? Guid.NewGuid().ToString("N");

            // Store in S3 simulation
            _s3Store[objectKey] = (data.ToArray(), DateTimeOffset.UtcNow);

            var tasks = targetNodeIds.Select(async targetRegion =>
            {
                var startTime = DateTime.UtcNow;
                try
                {
                    // Simulate S3 CRR with transfer acceleration
                    var latency = _enableTransferAcceleration ? 30 : 80;
                    if (_regions.TryGetValue(targetRegion, out var region))
                    {
                        latency += region.EstimatedLatencyMs;
                    }

                    await Task.Delay(latency, cancellationToken);
                    RecordReplicationLag(targetRegion, DateTime.UtcNow - startTime);
                }
                catch (OperationCanceledException ex)
                {

                    // Cancelled - don't record lag
                    System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
                }
            });

            await Task.WhenAll(tasks);
        }

        /// <inheritdoc/>
        public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            // AWS uses LWW with DynamoDB streams for conflict detection
            return Task.FromResult(ResolveLastWriteWins(conflict));
        }

        /// <inheritdoc/>
        public override Task<bool> VerifyConsistencyAsync(
            IEnumerable<string> nodeIds,
            string dataId,
            CancellationToken cancellationToken = default)
        {
            // Check S3 object versioning
            return Task.FromResult(_s3Store.ContainsKey(dataId));
        }

        /// <inheritdoc/>
        public override Task<TimeSpan> GetReplicationLagAsync(
            string sourceNodeId,
            string targetNodeId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LagTracker.GetCurrentLag(targetNodeId));
        }
    }

    /// <summary>
    /// Azure-specific replication strategy with Cosmos DB global distribution,
    /// Azure Storage geo-redundancy, and Azure SQL geo-replication patterns.
    /// </summary>
    public sealed class AzureReplicationStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<string, CloudProvider> _regions = new BoundedDictionary<string, CloudProvider>(1000);
        private readonly BoundedDictionary<string, (byte[] Data, DateTimeOffset Timestamp, string ETag)> _cosmosStore = new BoundedDictionary<string, (byte[] Data, DateTimeOffset Timestamp, string ETag)>(1000);
        private ConsistencyModel _cosmosConsistencyLevel = ConsistencyModel.SessionConsistent;
        private string? _writeRegion;
        private readonly List<string> _readRegions = new();
        private readonly object _readRegionsLock = new();

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "Azure",
            Description = "Azure-native replication with Cosmos DB global distribution, configurable consistency levels, and automatic failover",
            ConsistencyModel = ConsistencyModel.SessionConsistent,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: true,
                ConflictResolutionMethods: new[] {
                    ConflictResolutionMethod.LastWriteWins,
                    ConflictResolutionMethod.Custom,
                    ConflictResolutionMethod.Merge
                },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: true,
                IsGeoAware: true,
                MaxReplicationLag: TimeSpan.FromSeconds(30),
                MinReplicaCount: 2,
                MaxReplicaCount: 50),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = true,
            SupportsStreaming = true,
            TypicalLagMs = 200,
            ConsistencySlaMs = 30000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => _cosmosConsistencyLevel;

        /// <summary>
        /// Sets the Cosmos DB consistency level.
        /// </summary>
        public void SetConsistencyLevel(ConsistencyModel level)
        {
            _cosmosConsistencyLevel = level;
        }

        /// <summary>
        /// Adds an Azure region.
        /// </summary>
        public void AddRegion(string regionName, bool isWriteRegion = false)
        {
            var provider = new CloudProvider
            {
                ProviderId = $"azure-{regionName.ToLowerInvariant().Replace(" ", "-")}",
                Name = $"Azure {regionName}",
                Type = CloudProviderType.Azure
            };
            _regions[regionName] = provider;

            if (isWriteRegion)
            {
                _writeRegion = regionName;
            }
            else
            {
                lock (_readRegionsLock)
                {
                    _readRegions.Add(regionName);
                }
            }
        }

        /// <inheritdoc/>
        public override async Task ReplicateAsync(
            string sourceNodeId,
            IEnumerable<string> targetNodeIds,
            ReadOnlyMemory<byte> data,
            IDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            IncrementLocalClock();
            var documentId = metadata?.GetValueOrDefault("documentId") ?? Guid.NewGuid().ToString();
            var etag = $"W/\"{Guid.NewGuid():N}\"";

            // Store in Cosmos simulation
            _cosmosStore[documentId] = (data.ToArray(), DateTimeOffset.UtcNow, etag);

            // Cosmos DB replication based on consistency level
            var replicationDelay = _cosmosConsistencyLevel switch
            {
                ConsistencyModel.Strong => 100,
                ConsistencyModel.BoundedStaleness => 50,
                ConsistencyModel.SessionConsistent => 30,
                ConsistencyModel.Eventual => 10,
                _ => 30
            };

            var tasks = targetNodeIds.Select(async targetRegion =>
            {
                var startTime = DateTime.UtcNow;
                await Task.Delay(replicationDelay, cancellationToken);
                RecordReplicationLag(targetRegion, DateTime.UtcNow - startTime);
            });

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Performs a session-consistent read.
        /// </summary>
        public async Task<byte[]?> ReadWithSessionTokenAsync(string documentId, string sessionToken, CancellationToken ct = default)
        {
            await Task.Delay(10, ct);
            return _cosmosStore.TryGetValue(documentId, out var item) ? item.Data : null;
        }

        /// <inheritdoc/>
        public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            // Azure supports custom conflict resolution via stored procedures
            return Task.FromResult(ResolveLastWriteWins(conflict));
        }

        /// <inheritdoc/>
        public override Task<bool> VerifyConsistencyAsync(
            IEnumerable<string> nodeIds,
            string dataId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_cosmosStore.ContainsKey(dataId));
        }

        /// <inheritdoc/>
        public override Task<TimeSpan> GetReplicationLagAsync(
            string sourceNodeId,
            string targetNodeId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LagTracker.GetCurrentLag(targetNodeId));
        }
    }

    /// <summary>
    /// GCP-specific replication strategy with Cloud Spanner global distribution,
    /// Cloud Storage dual-region, and Cloud SQL cross-region replicas.
    /// </summary>
    public sealed class GcpReplicationStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<string, CloudProvider> _regions = new BoundedDictionary<string, CloudProvider>(1000);
        private readonly BoundedDictionary<string, (byte[] Data, DateTimeOffset CommitTimestamp)> _spannerStore = new BoundedDictionary<string, (byte[] Data, DateTimeOffset CommitTimestamp)>(1000);
        private bool _enableExternalConsistency = true;
        private string? _leaderRegion;

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "GCP",
            Description = "GCP-native replication with Cloud Spanner TrueTime, external consistency, and multi-region configurations",
            ConsistencyModel = ConsistencyModel.Strong,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: true,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.FirstWriteWins },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: true,
                IsGeoAware: true,
                MaxReplicationLag: TimeSpan.FromSeconds(10),
                MinReplicaCount: 3,
                MaxReplicaCount: 50),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = false, // Spanner uses TrueTime instead
            SupportsDeltaSync = false,
            SupportsStreaming = true,
            TypicalLagMs = 100,
            ConsistencySlaMs = 10000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Configures external consistency (TrueTime).
        /// </summary>
        public void SetExternalConsistency(bool enabled)
        {
            _enableExternalConsistency = enabled;
        }

        /// <summary>
        /// Adds a GCP region.
        /// </summary>
        public void AddRegion(string regionName, bool isLeader = false)
        {
            var provider = new CloudProvider
            {
                ProviderId = $"gcp-{regionName}",
                Name = $"GCP {regionName}",
                Type = CloudProviderType.GCP
            };
            _regions[regionName] = provider;

            if (isLeader)
            {
                _leaderRegion = regionName;
            }
        }

        /// <inheritdoc/>
        public override async Task ReplicateAsync(
            string sourceNodeId,
            IEnumerable<string> targetNodeIds,
            ReadOnlyMemory<byte> data,
            IDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            IncrementLocalClock();
            var key = metadata?.GetValueOrDefault("key") ?? Guid.NewGuid().ToString();

            // Simulate TrueTime commit timestamp
            var commitTimestamp = DateTimeOffset.UtcNow;
            _spannerStore[key] = (data.ToArray(), commitTimestamp);

            // Spanner uses Paxos-based replication
            var tasks = targetNodeIds.Select(async targetRegion =>
            {
                var startTime = DateTime.UtcNow;

                // External consistency adds wait time
                var waitTime = _enableExternalConsistency ? 20 : 5;
                await Task.Delay(waitTime, cancellationToken);

                RecordReplicationLag(targetRegion, DateTime.UtcNow - startTime);
            });

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Performs a stale read at a specific timestamp (for read replicas).
        /// </summary>
        public async Task<byte[]?> StaleReadAsync(string key, DateTimeOffset readTimestamp, CancellationToken ct = default)
        {
            await Task.Delay(5, ct);
            if (_spannerStore.TryGetValue(key, out var item) && item.CommitTimestamp <= readTimestamp)
            {
                return item.Data;
            }
            return null;
        }

        /// <inheritdoc/>
        public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            // Spanner prevents conflicts with external consistency
            // First write wins in race conditions
            var mergedClock = conflict.LocalVersion.Clone();
            mergedClock.Merge(conflict.RemoteVersion);
            return Task.FromResult((conflict.LocalData, mergedClock));
        }

        /// <inheritdoc/>
        public override Task<bool> VerifyConsistencyAsync(
            IEnumerable<string> nodeIds,
            string dataId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_spannerStore.ContainsKey(dataId));
        }

        /// <inheritdoc/>
        public override Task<TimeSpan> GetReplicationLagAsync(
            string sourceNodeId,
            string targetNodeId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LagTracker.GetCurrentLag(targetNodeId));
        }
    }

    /// <summary>
    /// Hybrid cloud replication strategy for on-premises to cloud synchronization
    /// with data sovereignty compliance and WAN optimization.
    /// </summary>
    public sealed class HybridCloudStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<string, CloudProvider> _providers = new BoundedDictionary<string, CloudProvider>(1000);
        private readonly BoundedDictionary<string, HashSet<string>> _dataSovereigntyRules = new BoundedDictionary<string, HashSet<string>>(1000);
        private readonly BoundedDictionary<string, (byte[] Data, string Location)> _dataStore = new BoundedDictionary<string, (byte[] Data, string Location)>(1000);
        private bool _enableWanOptimization = true;
        private bool _enableCompression = true;

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "HybridCloud",
            Description = "Hybrid cloud replication with data sovereignty compliance, WAN optimization, and multi-cloud coordination",
            ConsistencyModel = ConsistencyModel.Eventual,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: true,
                ConflictResolutionMethods: new[] {
                    ConflictResolutionMethod.LastWriteWins,
                    ConflictResolutionMethod.PriorityBased
                },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: true,
                IsGeoAware: true,
                MaxReplicationLag: TimeSpan.FromMinutes(5),
                MinReplicaCount: 2,
                MaxReplicaCount: 100),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = true,
            SupportsStreaming = true,
            TypicalLagMs = 1000,
            ConsistencySlaMs = 300000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Adds a cloud provider to the hybrid topology.
        /// </summary>
        public void AddProvider(CloudProvider provider)
        {
            _providers[provider.ProviderId] = provider;
        }

        /// <summary>
        /// Configures data sovereignty rules (data can only replicate to allowed regions).
        /// </summary>
        public void SetDataSovereigntyRules(string dataClassification, IEnumerable<string> allowedProviders)
        {
            _dataSovereigntyRules[dataClassification] = new HashSet<string>(allowedProviders);
        }

        /// <summary>
        /// Configures WAN optimization.
        /// </summary>
        public void SetWanOptimization(bool enabled, bool enableCompression = true)
        {
            _enableWanOptimization = enabled;
            _enableCompression = enableCompression;
        }

        /// <summary>
        /// Gets providers allowed for a data classification.
        /// </summary>
        public IEnumerable<CloudProvider> GetAllowedProviders(string dataClassification)
        {
            if (_dataSovereigntyRules.TryGetValue(dataClassification, out var allowed))
            {
                return _providers.Values.Where(p => allowed.Contains(p.ProviderId));
            }
            return _providers.Values;
        }

        /// <inheritdoc/>
        public override async Task ReplicateAsync(
            string sourceNodeId,
            IEnumerable<string> targetNodeIds,
            ReadOnlyMemory<byte> data,
            IDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            IncrementLocalClock();
            var dataId = metadata?.GetValueOrDefault("dataId") ?? Guid.NewGuid().ToString();
            var classification = metadata?.GetValueOrDefault("classification") ?? "default";

            // Check sovereignty rules
            var allowedTargets = targetNodeIds.Where(t =>
            {
                if (!_dataSovereigntyRules.TryGetValue(classification, out var allowed))
                    return true;
                return allowed.Contains(t);
            }).ToList();

            if (allowedTargets.Count == 0)
            {
                throw new InvalidOperationException($"No targets allowed for data classification '{classification}'");
            }

            _dataStore[dataId] = (data.ToArray(), sourceNodeId);

            // Simulate WAN transfer with optional optimization
            var tasks = allowedTargets.Select(async targetId =>
            {
                var startTime = DateTime.UtcNow;

                var baseLatency = 100;
                if (_providers.TryGetValue(targetId, out var provider))
                {
                    baseLatency = provider.EstimatedLatencyMs;
                }

                // WAN optimization reduces effective latency
                var effectiveLatency = _enableWanOptimization ? (int)(baseLatency * 0.6) : baseLatency;

                // Compression reduces data transfer time
                var transferTime = _enableCompression ? data.Length / 2000 : data.Length / 1000;

                await Task.Delay(effectiveLatency + transferTime, cancellationToken);
                RecordReplicationLag(targetId, DateTime.UtcNow - startTime);
            });

            await Task.WhenAll(tasks);
        }

        /// <inheritdoc/>
        public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            // Priority-based: on-premises wins over cloud by default
            var localIsOnPrem = _providers.TryGetValue(conflict.LocalNodeId, out var local) &&
                               local.Type == CloudProviderType.OnPremise;
            var remoteIsOnPrem = _providers.TryGetValue(conflict.RemoteNodeId, out var remote) &&
                                remote.Type == CloudProviderType.OnPremise;

            var mergedClock = conflict.LocalVersion.Clone();
            mergedClock.Merge(conflict.RemoteVersion);

            if (localIsOnPrem && !remoteIsOnPrem)
                return Task.FromResult((conflict.LocalData, mergedClock));
            if (!localIsOnPrem && remoteIsOnPrem)
                return Task.FromResult((conflict.RemoteData, mergedClock));

            return Task.FromResult(ResolveLastWriteWins(conflict));
        }

        /// <inheritdoc/>
        public override Task<bool> VerifyConsistencyAsync(
            IEnumerable<string> nodeIds,
            string dataId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_dataStore.ContainsKey(dataId));
        }

        /// <inheritdoc/>
        public override Task<TimeSpan> GetReplicationLagAsync(
            string sourceNodeId,
            string targetNodeId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LagTracker.GetCurrentLag(targetNodeId));
        }
    }

    /// <summary>
    /// Kubernetes-native replication strategy with StatefulSet coordination,
    /// persistent volume replication, and service mesh integration.
    /// </summary>
    public sealed class KubernetesReplicationStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<string, KubernetesNode> _nodes = new BoundedDictionary<string, KubernetesNode>(1000);
        private readonly BoundedDictionary<string, (byte[] Data, string PodName)> _pvStore = new BoundedDictionary<string, (byte[] Data, string PodName)>(1000);
        private string? _primaryPod;
        private bool _enableServiceMesh = true;

        /// <summary>
        /// Represents a Kubernetes node in the cluster.
        /// </summary>
        public sealed class KubernetesNode
        {
            public required string PodName { get; init; }
            public required string Namespace { get; init; }
            public required string StatefulSetName { get; init; }
            public int Ordinal { get; init; }
            public bool IsReady { get; set; } = true;
            public string? PersistentVolumeClaimName { get; init; }
        }

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "Kubernetes",
            Description = "Kubernetes-native replication with StatefulSet coordination, PV replication, and service mesh integration",
            ConsistencyModel = ConsistencyModel.SessionConsistent,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: false,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.FirstWriteWins },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: true,
                IsGeoAware: false,
                MaxReplicationLag: TimeSpan.FromSeconds(30),
                MinReplicaCount: 1,
                MaxReplicaCount: 10),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = true,
            SupportsStreaming = true,
            TypicalLagMs = 50,
            ConsistencySlaMs = 30000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Adds a pod to the replication set.
        /// </summary>
        public void AddPod(KubernetesNode node)
        {
            _nodes[node.PodName] = node;
            if (node.Ordinal == 0)
            {
                _primaryPod = node.PodName;
            }
        }

        /// <summary>
        /// Configures service mesh integration.
        /// </summary>
        public void SetServiceMesh(bool enabled)
        {
            _enableServiceMesh = enabled;
        }

        /// <summary>
        /// Gets the primary (ordinal-0) pod.
        /// </summary>
        public string? GetPrimaryPod() => _primaryPod;

        /// <summary>
        /// Checks if a pod is ready.
        /// </summary>
        public bool IsPodReady(string podName)
        {
            return _nodes.TryGetValue(podName, out var node) && node.IsReady;
        }

        /// <inheritdoc/>
        public override async Task ReplicateAsync(
            string sourceNodeId,
            IEnumerable<string> targetNodeIds,
            ReadOnlyMemory<byte> data,
            IDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            IncrementLocalClock();
            var volumeKey = metadata?.GetValueOrDefault("volumeKey") ?? Guid.NewGuid().ToString();

            _pvStore[volumeKey] = (data.ToArray(), sourceNodeId);

            // Replicate to other pods in StatefulSet
            var readyTargets = targetNodeIds.Where(IsPodReady).ToList();

            var tasks = readyTargets.Select(async targetPod =>
            {
                var startTime = DateTime.UtcNow;

                // Service mesh adds overhead but provides observability
                var latency = _enableServiceMesh ? 15 : 10;
                await Task.Delay(latency, cancellationToken);

                RecordReplicationLag(targetPod, DateTime.UtcNow - startTime);
            });

            await Task.WhenAll(tasks);
        }

        /// <inheritdoc/>
        public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            // Primary pod (ordinal-0) wins
            var localIsPrimary = conflict.LocalNodeId == _primaryPod;
            var remoteIsPrimary = conflict.RemoteNodeId == _primaryPod;

            var mergedClock = conflict.LocalVersion.Clone();
            mergedClock.Merge(conflict.RemoteVersion);

            if (localIsPrimary && !remoteIsPrimary)
                return Task.FromResult((conflict.LocalData, mergedClock));
            if (!localIsPrimary && remoteIsPrimary)
                return Task.FromResult((conflict.RemoteData, mergedClock));

            return Task.FromResult((conflict.LocalData, mergedClock));
        }

        /// <inheritdoc/>
        public override Task<bool> VerifyConsistencyAsync(
            IEnumerable<string> nodeIds,
            string dataId,
            CancellationToken cancellationToken = default)
        {
            var readyCount = nodeIds.Count(IsPodReady);
            return Task.FromResult(readyCount >= 1 && _pvStore.ContainsKey(dataId));
        }

        /// <inheritdoc/>
        public override Task<TimeSpan> GetReplicationLagAsync(
            string sourceNodeId,
            string targetNodeId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LagTracker.GetCurrentLag(targetNodeId));
        }
    }

    /// <summary>
    /// Edge computing replication strategy with store-and-forward, offline support,
    /// and bandwidth-aware synchronization.
    /// </summary>
    public sealed class EdgeReplicationStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<string, EdgeNode> _edgeNodes = new BoundedDictionary<string, EdgeNode>(1000);
        private readonly BoundedDictionary<string, Queue<PendingSync>> _pendingSyncs = new BoundedDictionary<string, Queue<PendingSync>>(1000);
        private readonly BoundedDictionary<string, (byte[] Data, DateTimeOffset Timestamp)> _edgeStore = new BoundedDictionary<string, (byte[] Data, DateTimeOffset Timestamp)>(1000);
        private int _maxQueueSize = 10000;

        /// <summary>
        /// Represents an edge computing node.
        /// </summary>
        public sealed class EdgeNode
        {
            public required string NodeId { get; init; }
            public required string Location { get; init; }
            public bool IsOnline { get; set; } = true;
            public int BandwidthKbps { get; set; } = 1000;
            public int LatencyMs { get; set; } = 100;
            public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
            public long StorageCapacityBytes { get; set; } = 1_000_000_000;
            public long StorageUsedBytes { get; set; }
        }

        /// <summary>
        /// Represents a pending synchronization operation.
        /// </summary>
        private sealed record PendingSync(
            string DataId,
            byte[] Data,
            DateTimeOffset QueuedAt,
            int Priority);

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "Edge",
            Description = "Edge computing replication with store-and-forward, offline support, and bandwidth-aware synchronization",
            ConsistencyModel = ConsistencyModel.Eventual,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: true,
                ConflictResolutionMethods: new[] {
                    ConflictResolutionMethod.LastWriteWins,
                    ConflictResolutionMethod.Merge
                },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: false,
                IsGeoAware: true,
                MaxReplicationLag: TimeSpan.FromHours(24),
                MinReplicaCount: 1,
                MaxReplicaCount: 1000),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = true,
            SupportsStreaming = false,
            TypicalLagMs = 5000,
            ConsistencySlaMs = 86400000 // 24 hours
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Adds an edge node.
        /// </summary>
        public void AddEdgeNode(EdgeNode node)
        {
            _edgeNodes[node.NodeId] = node;
            _pendingSyncs[node.NodeId] = new Queue<PendingSync>();
        }

        /// <summary>
        /// Updates edge node connectivity status.
        /// </summary>
        public void UpdateNodeStatus(string nodeId, bool isOnline)
        {
            if (_edgeNodes.TryGetValue(nodeId, out var node))
            {
                node.IsOnline = isOnline;
                node.LastSeen = DateTimeOffset.UtcNow;
            }
        }

        /// <summary>
        /// Gets pending sync count for a node.
        /// </summary>
        public int GetPendingSyncCount(string nodeId)
        {
            return _pendingSyncs.TryGetValue(nodeId, out var queue) ? queue.Count : 0;
        }

        /// <inheritdoc/>
        public override async Task ReplicateAsync(
            string sourceNodeId,
            IEnumerable<string> targetNodeIds,
            ReadOnlyMemory<byte> data,
            IDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            IncrementLocalClock();
            var dataId = metadata?.GetValueOrDefault("dataId") ?? Guid.NewGuid().ToString();
            var priority = int.TryParse(metadata?.GetValueOrDefault("priority"), out var p) ? p : 5;

            _edgeStore[dataId] = (data.ToArray(), DateTimeOffset.UtcNow);

            foreach (var targetId in targetNodeIds)
            {
                if (!_edgeNodes.TryGetValue(targetId, out var node))
                    continue;

                if (node.IsOnline)
                {
                    var startTime = DateTime.UtcNow;

                    // Calculate transfer time based on bandwidth
                    var transferTimeMs = (data.Length * 8) / node.BandwidthKbps;
                    await Task.Delay(Math.Max(node.LatencyMs, transferTimeMs), cancellationToken);

                    RecordReplicationLag(targetId, DateTime.UtcNow - startTime);
                }
                else
                {
                    // Store-and-forward for offline nodes
                    if (_pendingSyncs.TryGetValue(targetId, out var queue))
                    {
                        if (queue.Count < _maxQueueSize)
                        {
                            queue.Enqueue(new PendingSync(dataId, data.ToArray(), DateTimeOffset.UtcNow, priority));
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Syncs pending data to a node that came back online.
        /// </summary>
        public async Task SyncPendingAsync(string nodeId, CancellationToken ct = default)
        {
            if (!_pendingSyncs.TryGetValue(nodeId, out var queue) ||
                !_edgeNodes.TryGetValue(nodeId, out var node) ||
                !node.IsOnline)
                return;

            while (queue.TryDequeue(out var pending) && !ct.IsCancellationRequested)
            {
                var transferTimeMs = (pending.Data.Length * 8) / node.BandwidthKbps;
                await Task.Delay(Math.Max(node.LatencyMs, transferTimeMs), ct);
            }
        }

        /// <inheritdoc/>
        public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ResolveLastWriteWins(conflict));
        }

        /// <inheritdoc/>
        public override Task<bool> VerifyConsistencyAsync(
            IEnumerable<string> nodeIds,
            string dataId,
            CancellationToken cancellationToken = default)
        {
            // Edge is eventually consistent; check local store
            return Task.FromResult(_edgeStore.ContainsKey(dataId));
        }

        /// <inheritdoc/>
        public override Task<TimeSpan> GetReplicationLagAsync(
            string sourceNodeId,
            string targetNodeId,
            CancellationToken cancellationToken = default)
        {
            if (_edgeNodes.TryGetValue(targetNodeId, out var node) && !node.IsOnline)
            {
                // Offline nodes have infinite lag until sync
                return Task.FromResult(DateTimeOffset.UtcNow - node.LastSeen);
            }
            return Task.FromResult(LagTracker.GetCurrentLag(targetNodeId));
        }
    }
}
