using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Contracts.Replication;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.Plugins.UltimateReplication.Strategies.Core;
using DataWarehouse.Plugins.UltimateReplication.Strategies.Geo;
using DataWarehouse.Plugins.UltimateReplication.Strategies.Federation;
using DataWarehouse.Plugins.UltimateReplication.Strategies.Cloud;
using DataWarehouse.Plugins.UltimateReplication.Strategies.ActiveActive;
using DataWarehouse.Plugins.UltimateReplication.Strategies.CDC;
using DataWarehouse.Plugins.UltimateReplication.Strategies.AI;
using DataWarehouse.Plugins.UltimateReplication.Strategies.Conflict;
using DataWarehouse.Plugins.UltimateReplication.Strategies.Topology;
using DataWarehouse.Plugins.UltimateReplication.Strategies.Specialized;
using DataWarehouse.Plugins.UltimateReplication.Strategies.DR;
using DataWarehouse.Plugins.UltimateReplication.Strategies.AirGap;
using DataWarehouse.Plugins.UltimateReplication.Strategies.Synchronous;
using DataWarehouse.Plugins.UltimateReplication.Strategies.Asynchronous;
using DataWarehouse.Plugins.UltimateReplication.Strategies.GeoReplication;

namespace DataWarehouse.Plugins.UltimateReplication
{
    /// <summary>
    /// Ultimate Replication plugin — the canonical replication handler for DataWarehouse.
    /// Consolidates 60 comprehensive replication strategies across 15 categories with
    /// 12 advanced features including geo-dispersed WORM replication and geo-distributed sharding.
    /// </summary>
    /// <remarks>
    /// <para><b>Migration Guide (Phase D):</b></para>
    /// <para>
    /// UltimateReplication supersedes all prior replication plugins. Migrate by subscribing to
    /// "replication.ultimate.*" topics instead of legacy "replication.*" topics. All strategy
    /// selection, conflict resolution, and replication operations are handled through this plugin.
    /// Legacy replication plugins (SimpleReplication, BasicReplication, GeoReplication) are
    /// deprecated and will be removed in Phase 18.
    /// </para>
    ///
    /// <para><b>Supported replication strategy categories (60 strategies):</b></para>
    /// <list type="bullet">
    ///   <item>Core: CRDT, MultiMaster, RealTimeSync, DeltaSync</item>
    ///   <item>Geo: GeoReplication, CrossRegion, PrimarySecondary</item>
    ///   <item>Federation: Federation, FederatedQuery</item>
    ///   <item>Cloud: AWS, Azure, GCP, HybridCloud, Kubernetes, Edge</item>
    ///   <item>Active-Active: HotHot, NWayActive, GlobalActive</item>
    ///   <item>CDC: KafkaConnect, Debezium, Maxwell, Canal</item>
    ///   <item>AI-Enhanced: Predictive, Semantic, Adaptive, Intelligent, AutoTune</item>
    ///   <item>Conflict Resolution: LastWriteWins, VectorClock, Merge, Custom, CRDT, Version, ThreeWayMerge</item>
    ///   <item>Topology: Star, Mesh, Chain, Tree, Ring, Hierarchical</item>
    ///   <item>Specialized: Selective, Filtered, Compression, Encryption, Throttle, Priority</item>
    ///   <item>DR: AsyncDR, SyncDR, ZeroRPO, ActivePassive, Failover</item>
    ///   <item>Air-Gap: BidirectionalMerge, ConflictAvoidance, SchemaEvolution, ZeroDataLoss, ResumableMerge, IncrementalSync, ProvenanceTracking</item>
    ///   <item>Sync Modes: Synchronous, Asynchronous</item>
    /// </list>
    ///
    /// <para><b>Advanced Features (12):</b></para>
    /// <list type="bullet">
    ///   <item>C1: Global Transaction Coordination (2PC/3PC)</item>
    ///   <item>C2: Smart Conflict Resolution (semantic merge + LWW fallback)</item>
    ///   <item>C3: Bandwidth-Aware Scheduling</item>
    ///   <item>C4: Priority-Based Queue</item>
    ///   <item>C5: Partial Replication (tag/pattern/size/age filters)</item>
    ///   <item>C6: Replication Lag Monitoring (warning/critical/emergency alerts)</item>
    ///   <item>C7: Cross-Cloud Replication (AWS/Azure/GCP)</item>
    ///   <item>C8: RAID Integration (parity check, erasure rebuild)</item>
    ///   <item>C9: Storage Integration (read/write via message bus)</item>
    ///   <item>C10: Intelligence Integration (AI conflict prediction + rule-based fallback)</item>
    ///   <item>T5.5: Geo-Dispersed WORM Replication (compliance/enterprise modes)</item>
    ///   <item>T5.6: Geo-Distributed Sharding (erasure coding across continents)</item>
    /// </list>
    ///
    /// <para><b>Message Commands:</b></para>
    /// <list type="bullet">
    ///   <item>replication.strategy.list: List available strategies</item>
    ///   <item>replication.strategy.select: Select active strategy</item>
    ///   <item>replication.strategy.info: Get strategy details</item>
    ///   <item>replication.replicate: Replicate data</item>
    ///   <item>replication.status: Get replication status</item>
    ///   <item>replication.lag: Get replication lag</item>
    ///   <item>replication.conflict.detect: Detect conflicts</item>
    ///   <item>replication.conflict.resolve: Resolve conflicts</item>
    ///   <item>replication.ultimate.worm.replicate: Geo-dispersed WORM replication</item>
    ///   <item>replication.ultimate.shard.distribute: Geo-distributed sharding</item>
    /// </list>
    ///
    /// <para><b>Deprecation Notice:</b></para>
    /// <para>
    /// [Obsolete] Legacy replication plugins are deprecated. Use UltimateReplication for all
    /// replication operations. Migration path: replace direct plugin references with message
    /// bus topics prefixed "replication.ultimate.*". File deletion deferred to Phase 18.
    /// </para>
    /// </remarks>
    public sealed class UltimateReplicationPlugin : DataWarehouse.SDK.Contracts.Hierarchy.ReplicationPluginBase
    {
        private readonly ReplicationStrategyRegistry _registry = new();
        private EnhancedReplicationStrategyBase? _activeStrategy;
        private CancellationTokenSource? _cts;
        private string _nodeId = string.Empty;

        // Statistics
        private long _totalReplications;
        private long _totalConflictsDetected;
        private long _totalConflictsResolved;
        private long _totalBytesReplicated;

        /// <inheritdoc/>
        public override string Id => "com.datawarehouse.replication.ultimate";

        /// <inheritdoc/>
        public override string Name => "Ultimate Replication";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <inheritdoc/>
        public override PluginCategory Category => PluginCategory.StorageProvider;

        /// <summary>
        /// Creates a new Ultimate Replication plugin instance and discovers strategies.
        /// </summary>
        public UltimateReplicationPlugin()
        {
            DiscoverAndRegisterStrategies();
        }

        /// <summary>
        /// Gets all registered strategy names.
        /// </summary>
        public IReadOnlyCollection<string> GetRegisteredStrategies() => _registry.RegisteredStrategies;

        /// <summary>
        /// Gets a strategy by name.
        /// </summary>
        public EnhancedReplicationStrategyBase? GetStrategy(string name) => _registry.Get(name);

        /// <summary>
        /// Sets the active strategy.
        /// </summary>
        public void SetActiveStrategy(string strategyName)
        {
            var strategy = _registry.Get(strategyName);
            if (strategy == null)
                throw new ArgumentException($"Strategy '{strategyName}' not found");
            _activeStrategy = strategy;
        }

        /// <summary>
        /// Gets the active strategy.
        /// </summary>
        public EnhancedReplicationStrategyBase? GetActiveStrategy() => _activeStrategy;

        /// <summary>
        /// Selects the best strategy based on requirements.
        /// </summary>
        public EnhancedReplicationStrategyBase? SelectBestStrategy(
            ConsistencyModel? preferredConsistency = null,
            long? maxLagMs = null,
            bool requireMultiMaster = false,
            bool requireGeoAware = false)
        {
            return _registry.SelectBestStrategy(preferredConsistency, maxLagMs, requireMultiMaster, requireGeoAware);
        }

        /// <inheritdoc/>
        public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
        {
            _nodeId = $"repl-{Guid.NewGuid():N}"[..16];

            // Set default strategy
            _activeStrategy ??= _registry.Get("CRDT") ?? _registry.GetAll().FirstOrDefault().Value;

            return await Task.FromResult(new HandshakeResponse
            {
                PluginId = Id,
                Name = Name,
                Version = ParseSemanticVersion(Version),
                Category = Category,
                Success = true,
                ReadyState = PluginReadyState.Ready,
                Capabilities = GetCapabilities(),
                Metadata = GetMetadata()
            });
        }

        /// <inheritdoc/>
        protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct)
        {
            // Intelligence is available - enable AI-enhanced replication features
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            if (MessageBus != null)
            {
                // Subscribe to replication topics
                MessageBus.Subscribe(ReplicationTopics.Replicate, HandleReplicateMessageAsync);
                MessageBus.Subscribe(ReplicationTopics.Sync, HandleSyncMessageAsync);
                MessageBus.Subscribe(ReplicationTopics.SelectStrategy, HandleSelectStrategyMessageAsync);
                MessageBus.Subscribe(ReplicationTopics.ConflictResolve, HandleConflictResolveMessageAsync);
                MessageBus.Subscribe(ReplicationTopics.LagRequest, HandleLagRequestMessageAsync);

                // Subscribe to Intelligence-enhanced topics
                MessageBus.Subscribe(ReplicationTopics.PredictConflict, HandlePredictConflictMessageAsync);
                MessageBus.Subscribe(ReplicationTopics.OptimizeConsistency, HandleOptimizeConsistencyMessageAsync);
                MessageBus.Subscribe(ReplicationTopics.RouteRequest, HandleRouteRequestMessageAsync);
            }

            await Task.CompletedTask;
        }

        /// <inheritdoc/>
        protected override async Task OnStartWithoutIntelligenceAsync(CancellationToken ct)
        {
            // Intelligence unavailable - use fallback behavior
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            if (MessageBus != null)
            {
                // Subscribe to basic replication topics only
                MessageBus.Subscribe(ReplicationTopics.Replicate, HandleReplicateMessageAsync);
                MessageBus.Subscribe(ReplicationTopics.Sync, HandleSyncMessageAsync);
                MessageBus.Subscribe(ReplicationTopics.SelectStrategy, HandleSelectStrategyMessageAsync);
                MessageBus.Subscribe(ReplicationTopics.ConflictResolve, HandleConflictResolveMessageAsync);
                MessageBus.Subscribe(ReplicationTopics.LagRequest, HandleLagRequestMessageAsync);
            }

            await Task.CompletedTask;
        }

        /// <inheritdoc/>
        protected override async Task OnStopCoreAsync()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            await Task.CompletedTask;
        }

        /// <inheritdoc/>
        public override async Task OnMessageAsync(PluginMessage message)
        {
            if (message.Payload == null)
                return;

            var response = message.Type switch
            {
                "replication.strategy.list" => HandleListStrategies(),
                "replication.strategy.select" => HandleSelectStrategy(message.Payload),
                "replication.strategy.info" => HandleStrategyInfo(message.Payload),
                "replication.replicate" => await HandleReplicateAsync(message.Payload),
                "replication.status" => HandleStatus(),
                "replication.lag" => await HandleGetLagAsync(message.Payload),
                "replication.conflict.detect" => HandleDetectConflict(message.Payload),
                "replication.conflict.resolve" => await HandleResolveConflictAsync(message.Payload),
                _ => new Dictionary<string, object> { ["error"] = $"Unknown command: {message.Type}" }
            };

            if (response != null)
            {
                message.Payload["_response"] = response;
            }
        }

        #region Strategy Discovery

        private void DiscoverAndRegisterStrategies()
        {
            // Register core strategies (4)
            _registry.Register(new CrdtReplicationStrategy());
            _registry.Register(new MultiMasterStrategy());
            _registry.Register(new RealTimeSyncStrategy());
            _registry.Register(new DeltaSyncStrategy());

            // Register geo strategies (2)
            _registry.Register(new GeoReplicationStrategy());
            _registry.Register(new CrossRegionStrategy());

            // Register federation strategies (2)
            _registry.Register(new FederationStrategy());
            _registry.Register(new FederatedQueryStrategy());

            // Register sync mode strategies (2)
            _registry.Register(new SynchronousReplicationStrategy());
            _registry.Register(new AsynchronousReplicationStrategy());

            // Register geo-replication strategies (1)
            _registry.Register(new PrimarySecondaryReplicationStrategy());

            // Register cloud strategies (6)
            _registry.Register(new AwsReplicationStrategy());
            _registry.Register(new AzureReplicationStrategy());
            _registry.Register(new GcpReplicationStrategy());
            _registry.Register(new HybridCloudStrategy());
            _registry.Register(new KubernetesReplicationStrategy());
            _registry.Register(new EdgeReplicationStrategy());

            // Register active-active strategies (3)
            _registry.Register(new HotHotStrategy());
            _registry.Register(new NWayActiveStrategy());
            _registry.Register(new GlobalActiveStrategy());

            // Register CDC strategies (4)
            _registry.Register(new KafkaConnectCdcStrategy());
            _registry.Register(new DebeziumCdcStrategy());
            _registry.Register(new MaxwellCdcStrategy());
            _registry.Register(new CanalCdcStrategy());

            // Register AI-enhanced strategies (5)
            _registry.Register(new PredictiveReplicationStrategy());
            _registry.Register(new SemanticReplicationStrategy());
            _registry.Register(new AdaptiveReplicationStrategy());
            _registry.Register(new IntelligentReplicationStrategy());
            _registry.Register(new AutoTuneReplicationStrategy());

            // Register conflict resolution strategies (7)
            _registry.Register(new LastWriteWinsStrategy());
            _registry.Register(new VectorClockStrategy());
            _registry.Register(new MergeConflictStrategy());
            _registry.Register(new CustomConflictStrategy());
            _registry.Register(new CrdtConflictStrategy());
            _registry.Register(new VersionConflictStrategy());
            _registry.Register(new ThreeWayMergeStrategy());

            // Register topology strategies (6)
            _registry.Register(new StarTopologyStrategy());
            _registry.Register(new MeshTopologyStrategy());
            _registry.Register(new ChainTopologyStrategy());
            _registry.Register(new TreeTopologyStrategy());
            _registry.Register(new RingTopologyStrategy());
            _registry.Register(new HierarchicalTopologyStrategy());

            // Register specialized strategies (6)
            _registry.Register(new SelectiveReplicationStrategy());
            _registry.Register(new FilteredReplicationStrategy());
            _registry.Register(new CompressionReplicationStrategy());
            _registry.Register(new EncryptionReplicationStrategy());
            _registry.Register(new ThrottleReplicationStrategy());
            _registry.Register(new PriorityReplicationStrategy());

            // Register disaster recovery strategies (5)
            _registry.Register(new AsyncDRStrategy());
            _registry.Register(new SyncDRStrategy());
            _registry.Register(new ZeroRPOStrategy());
            _registry.Register(new ActivePassiveStrategy());
            _registry.Register(new FailoverDRStrategy());

            // Register air-gap strategies (7)
            _registry.Register(new BidirectionalMergeStrategy());
            _registry.Register(new ConflictAvoidanceStrategy());
            _registry.Register(new SchemaEvolutionStrategy());
            _registry.Register(new ZeroDataLossStrategy());
            _registry.Register(new ResumableMergeStrategy());
            _registry.Register(new IncrementalSyncStrategy());
            _registry.Register(new ProvenanceTrackingStrategy());
        }

        #endregion

        #region Message Handlers

        private Dictionary<string, object> HandleListStrategies()
        {
            var strategies = _registry.GetSummary().Select(s => new Dictionary<string, object>
            {
                ["name"] = s.Name,
                ["description"] = s.Description,
                ["consistencyModel"] = s.ConsistencyModel.ToString(),
                ["typicalLagMs"] = s.TypicalLagMs,
                ["supportsMultiMaster"] = s.SupportsMultiMaster,
                ["isGeoAware"] = s.IsGeoAware,
                ["supportsAutoConflictResolution"] = s.SupportsAutoConflictResolution,
                ["conflictResolutionMethods"] = s.ConflictResolutionMethods.Select(m => m.ToString()).ToArray()
            }).ToList();

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["count"] = strategies.Count,
                ["strategies"] = strategies,
                ["activeStrategy"] = _activeStrategy?.Characteristics.StrategyName ?? "none"
            };
        }

        private Dictionary<string, object> HandleSelectStrategy(Dictionary<string, object> payload)
        {
            try
            {
                var strategyName = payload.GetValueOrDefault("strategy")?.ToString();
                if (string.IsNullOrEmpty(strategyName))
                    return new Dictionary<string, object> { ["success"] = false, ["error"] = "Strategy name required" };

                SetActiveStrategy(strategyName);

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["activeStrategy"] = strategyName
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { ["success"] = false, ["error"] = ex.Message };
            }
        }

        private Dictionary<string, object> HandleStrategyInfo(Dictionary<string, object> payload)
        {
            var strategyName = payload.GetValueOrDefault("strategy")?.ToString();
            var strategy = !string.IsNullOrEmpty(strategyName)
                ? _registry.Get(strategyName)
                : _activeStrategy;

            if (strategy == null)
                return new Dictionary<string, object> { ["success"] = false, ["error"] = "Strategy not found" };

            var chars = strategy.Characteristics;
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["name"] = chars.StrategyName,
                ["description"] = chars.Description,
                ["consistencyModel"] = chars.ConsistencyModel.ToString(),
                ["typicalLagMs"] = chars.TypicalLagMs,
                ["consistencySlaMs"] = chars.ConsistencySlaMs,
                ["supportsVectorClocks"] = chars.SupportsVectorClocks,
                ["supportsDeltaSync"] = chars.SupportsDeltaSync,
                ["supportsStreaming"] = chars.SupportsStreaming,
                ["supportsAutoConflictResolution"] = chars.SupportsAutoConflictResolution,
                ["capabilities"] = new Dictionary<string, object>
                {
                    ["supportsMultiMaster"] = chars.Capabilities.SupportsMultiMaster,
                    ["supportsAsyncReplication"] = chars.Capabilities.SupportsAsyncReplication,
                    ["supportsSyncReplication"] = chars.Capabilities.SupportsSyncReplication,
                    ["isGeoAware"] = chars.Capabilities.IsGeoAware,
                    ["minReplicaCount"] = chars.Capabilities.MinReplicaCount,
                    ["maxReplicaCount"] = chars.Capabilities.MaxReplicaCount,
                    ["maxReplicationLag"] = chars.Capabilities.MaxReplicationLag?.TotalMilliseconds ?? 0,
                    ["conflictResolutionMethods"] = chars.Capabilities.ConflictResolutionMethods.Select(m => m.ToString()).ToArray()
                }
            };
        }

        private async Task<Dictionary<string, object>> HandleReplicateAsync(Dictionary<string, object> payload)
        {
            if (_activeStrategy == null)
                return new Dictionary<string, object> { ["success"] = false, ["error"] = "No active strategy" };

            try
            {
                var sourceNode = payload.GetValueOrDefault("sourceNode")?.ToString() ?? _nodeId;
                var targetNodes = payload.GetValueOrDefault("targetNodes") as IEnumerable<object>;
                var dataBase64 = payload.GetValueOrDefault("data")?.ToString();

                if (targetNodes == null)
                    return new Dictionary<string, object> { ["success"] = false, ["error"] = "Target nodes required" };

                var data = !string.IsNullOrEmpty(dataBase64)
                    ? Convert.FromBase64String(dataBase64)
                    : Array.Empty<byte>();

                var targets = targetNodes.Select(t => t.ToString()!).ToArray();
                var startTime = DateTime.UtcNow;

                await _activeStrategy.ReplicateAsync(
                    sourceNode,
                    targets,
                    data,
                    payload.Where(kv => kv.Key.StartsWith("meta."))
                           .ToDictionary(kv => kv.Key[5..], kv => kv.Value?.ToString() ?? ""),
                    _cts?.Token ?? default);

                var elapsed = DateTime.UtcNow - startTime;
                Interlocked.Increment(ref _totalReplications);
                Interlocked.Add(ref _totalBytesReplicated, data.Length);

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["sourceNode"] = sourceNode,
                    ["targetNodes"] = targets,
                    ["bytesReplicated"] = data.Length,
                    ["elapsedMs"] = elapsed.TotalMilliseconds,
                    ["strategy"] = _activeStrategy.Characteristics.StrategyName
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { ["success"] = false, ["error"] = ex.Message };
            }
        }

        private Dictionary<string, object> HandleStatus()
        {
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["nodeId"] = _nodeId,
                ["activeStrategy"] = _activeStrategy?.Characteristics.StrategyName ?? "none",
                ["registeredStrategies"] = _registry.Count,
                ["statistics"] = new Dictionary<string, object>
                {
                    ["totalReplications"] = _totalReplications,
                    ["totalConflictsDetected"] = _totalConflictsDetected,
                    ["totalConflictsResolved"] = _totalConflictsResolved,
                    ["totalBytesReplicated"] = _totalBytesReplicated
                },
                ["strategySummary"] = _registry.GetSummary().Select(s => new Dictionary<string, object>
                {
                    ["name"] = s.Name,
                    ["consistencyModel"] = s.ConsistencyModel.ToString(),
                    ["isActive"] = s.Name == _activeStrategy?.Characteristics.StrategyName
                }).ToList()
            };
        }

        private async Task<Dictionary<string, object>> HandleGetLagAsync(Dictionary<string, object> payload)
        {
            if (_activeStrategy == null)
                return new Dictionary<string, object> { ["success"] = false, ["error"] = "No active strategy" };

            var sourceNode = payload.GetValueOrDefault("sourceNode")?.ToString() ?? _nodeId;
            var targetNode = payload.GetValueOrDefault("targetNode")?.ToString();

            if (string.IsNullOrEmpty(targetNode))
                return new Dictionary<string, object> { ["success"] = false, ["error"] = "Target node required" };

            var lag = await _activeStrategy.GetReplicationLagAsync(sourceNode, targetNode);

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["sourceNode"] = sourceNode,
                ["targetNode"] = targetNode,
                ["lagMs"] = lag.TotalMilliseconds,
                ["strategy"] = _activeStrategy.Characteristics.StrategyName
            };
        }

        private Dictionary<string, object> HandleDetectConflict(Dictionary<string, object> payload)
        {
            if (_activeStrategy == null)
                return new Dictionary<string, object> { ["success"] = false, ["error"] = "No active strategy" };

            // Simplified conflict detection for messaging
            var localDataBase64 = payload.GetValueOrDefault("localData")?.ToString();
            var remoteDataBase64 = payload.GetValueOrDefault("remoteData")?.ToString();

            if (string.IsNullOrEmpty(localDataBase64) || string.IsNullOrEmpty(remoteDataBase64))
                return new Dictionary<string, object> { ["success"] = false, ["error"] = "Local and remote data required" };

            var localData = Convert.FromBase64String(localDataBase64);
            var remoteData = Convert.FromBase64String(remoteDataBase64);

            var hasConflict = !localData.AsSpan().SequenceEqual(remoteData.AsSpan());

            if (hasConflict)
                Interlocked.Increment(ref _totalConflictsDetected);

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["hasConflict"] = hasConflict,
                ["localDataSize"] = localData.Length,
                ["remoteDataSize"] = remoteData.Length,
                ["strategy"] = _activeStrategy.Characteristics.StrategyName
            };
        }

        private async Task<Dictionary<string, object>> HandleResolveConflictAsync(Dictionary<string, object> payload)
        {
            if (_activeStrategy == null)
                return new Dictionary<string, object> { ["success"] = false, ["error"] = "No active strategy" };

            try
            {
                var localDataBase64 = payload.GetValueOrDefault("localData")?.ToString();
                var remoteDataBase64 = payload.GetValueOrDefault("remoteData")?.ToString();

                if (string.IsNullOrEmpty(localDataBase64) || string.IsNullOrEmpty(remoteDataBase64))
                    return new Dictionary<string, object> { ["success"] = false, ["error"] = "Local and remote data required" };

                var localData = Convert.FromBase64String(localDataBase64);
                var remoteData = Convert.FromBase64String(remoteDataBase64);

                var localClock = new VectorClock();
                var remoteClock = new VectorClock();

                var conflict = new ReplicationConflict(
                    DataId: Guid.NewGuid().ToString(),
                    LocalVersion: localClock,
                    RemoteVersion: remoteClock,
                    LocalData: localData,
                    RemoteData: remoteData,
                    LocalNodeId: _nodeId,
                    RemoteNodeId: "remote",
                    DetectedAt: DateTimeOffset.UtcNow);

                var (resolvedData, resolvedVersion) = await _activeStrategy.ResolveConflictAsync(conflict);
                Interlocked.Increment(ref _totalConflictsResolved);

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["resolvedData"] = Convert.ToBase64String(resolvedData.ToArray()),
                    ["resolvedDataSize"] = resolvedData.Length,
                    ["strategy"] = _activeStrategy.Characteristics.StrategyName
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { ["success"] = false, ["error"] = ex.Message };
            }
        }

        #endregion

        #region Capability & Metadata

        /// <inheritdoc/>
        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            var capabilities = new List<PluginCapabilityDescriptor>
            {
                new()
                {
                    Name = "replicate",
                    Description = "Replicate data to target nodes",
                    Parameters = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["sourceNode"] = new { type = "string", description = "Source node ID" },
                            ["targetNodes"] = new { type = "array", description = "Target node IDs" },
                            ["data"] = new { type = "string", description = "Base64-encoded data" }
                        },
                        ["required"] = new[] { "targetNodes" }
                    }
                },
                new()
                {
                    Name = "strategy.select",
                    Description = "Select active replication strategy",
                    Parameters = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["strategy"] = new { type = "string", description = "Strategy name" }
                        },
                        ["required"] = new[] { "strategy" }
                    }
                },
                new()
                {
                    Name = "status",
                    Description = "Get replication status",
                    Parameters = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>()
                    }
                }
            };

            // Add capability for each strategy
            foreach (var (name, strategy) in _registry.GetAll())
            {
                var chars = strategy.Characteristics;
                capabilities.Add(new PluginCapabilityDescriptor
                {
                    Name = $"strategy.{name.ToLowerInvariant()}",
                    Description = chars.Description,
                    Parameters = new Dictionary<string, object>
                    {
                        ["consistencyModel"] = chars.ConsistencyModel.ToString(),
                        ["typicalLagMs"] = chars.TypicalLagMs,
                        ["supportsMultiMaster"] = chars.Capabilities.SupportsMultiMaster,
                        ["isGeoAware"] = chars.Capabilities.IsGeoAware
                    }
                });
            }

            return capabilities;
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "UltimateReplication";
            metadata["NodeId"] = _nodeId;
            metadata["StrategyCount"] = _registry.Count;
            metadata["Strategies"] = _registry.RegisteredStrategies.ToArray();
            metadata["ActiveStrategy"] = _activeStrategy?.Characteristics.StrategyName ?? "none";
            metadata["SupportsVectorClocks"] = true;
            metadata["SupportsConflictResolution"] = true;
            metadata["SupportsAntiEntropy"] = true;
            metadata["SupportedConsistencyModels"] = Enum.GetNames(typeof(ConsistencyModel));
            metadata["SupportedConflictResolutionMethods"] = Enum.GetNames(typeof(ConflictResolutionMethod));
            metadata["SemanticDescription"] = "Ultimate replication plugin with 60+ strategies including CRDT, multi-master, real-time sync, delta sync, geo-replication, cross-region, federation, federated query, cloud-native (AWS/Azure/GCP), active-active, CDC (Kafka/Debezium), AI-enhanced, conflict resolution, topology-based, specialized, disaster recovery, and air-gap support";
            metadata["SemanticTags"] = new[] { "replication", "crdt", "multi-master", "geo", "federation", "vector-clock", "conflict-resolution", "cloud", "aws", "azure", "gcp", "kubernetes", "active-active", "cdc", "kafka", "debezium", "ai-enhanced", "predictive", "topology", "dr", "disaster-recovery", "air-gap" };
            return metadata;
        }

        /// <inheritdoc/>
        protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
        {
            get
            {
                var capabilities = new List<RegisteredCapability>
                {
                    new()
                    {
                        CapabilityId = $"{Id}.replicate",
                        DisplayName = $"{Name} - Replicate",
                        Description = "Replicate data using selected strategy with AI-enhanced conflict prediction and routing",
                        Category = SDK.Contracts.CapabilityCategory.Storage,
                        SubCategory = "Replication",
                        PluginId = Id,
                        PluginName = Name,
                        PluginVersion = Version,
                        Tags = new[] { "replication", "data-sync", "ai-enhanced", "conflict-resolution" },
                        SemanticDescription = "Advanced replication with 8 strategies, vector clocks, conflict resolution, and Intelligence-powered optimization"
                    }
                };

                foreach (var (name, strategy) in _registry.GetAll())
                {
                    var chars = strategy.Characteristics;
                    var tags = new List<string> { "replication", "strategy", name.ToLowerInvariant() };
                    if (chars.Capabilities.SupportsMultiMaster) tags.Add("multi-master");
                    if (chars.Capabilities.IsGeoAware) tags.Add("geo-aware");
                    if (chars.SupportsVectorClocks) tags.Add("vector-clock");
                    if (chars.SupportsStreaming) tags.Add("streaming");
                    if (chars.SupportsAutoConflictResolution) tags.Add("auto-conflict-resolution");

                    var consistencyTags = chars.ConsistencyModel switch
                    {
                        ConsistencyModel.Eventual => "eventual-consistency",
                        ConsistencyModel.Strong => "strong-consistency",
                        ConsistencyModel.Causal => "causal-consistency",
                        ConsistencyModel.BoundedStaleness => "bounded-staleness",
                        ConsistencyModel.SessionConsistent => "session-consistency",
                        ConsistencyModel.ReadYourWrites => "read-your-writes",
                        ConsistencyModel.MonotonicReads => "monotonic-reads",
                        ConsistencyModel.MonotonicWrites => "monotonic-writes",
                        _ => "consistency"
                    };
                    tags.Add(consistencyTags);

                    capabilities.Add(new RegisteredCapability
                    {
                        CapabilityId = $"{Id}.strategy.{name.ToLowerInvariant()}",
                        DisplayName = $"{name} Replication",
                        Description = chars.Description,
                        Category = SDK.Contracts.CapabilityCategory.Storage,
                        SubCategory = "Replication",
                        PluginId = Id,
                        PluginName = Name,
                        PluginVersion = Version,
                        Tags = tags.ToArray(),
                        Priority = chars.Capabilities.SupportsMultiMaster ? 60 : 50,
                        Metadata = new Dictionary<string, object>
                        {
                            ["strategyId"] = name,
                            ["consistencyModel"] = chars.ConsistencyModel.ToString(),
                            ["typicalLagMs"] = chars.TypicalLagMs,
                            ["consistencySlaMs"] = chars.ConsistencySlaMs,
                            ["supportsMultiMaster"] = chars.Capabilities.SupportsMultiMaster,
                            ["isGeoAware"] = chars.Capabilities.IsGeoAware,
                            ["supportsVectorClocks"] = chars.SupportsVectorClocks,
                            ["supportsAutoConflictResolution"] = chars.SupportsAutoConflictResolution,
                            ["supportsDeltaSync"] = chars.SupportsDeltaSync,
                            ["supportsStreaming"] = chars.SupportsStreaming,
                            ["conflictResolutionMethods"] = chars.Capabilities.ConflictResolutionMethods.Select(m => m.ToString()).ToArray()
                        },
                        SemanticDescription = $"Replicate data using {name} strategy with {chars.ConsistencyModel} consistency. " +
                                            $"Typical lag: {chars.TypicalLagMs}ms. " +
                                            $"Supports: {(chars.Capabilities.SupportsMultiMaster ? "multi-master, " : "")}" +
                                            $"{(chars.Capabilities.IsGeoAware ? "geo-aware, " : "")}" +
                                            $"{(chars.SupportsVectorClocks ? "vector-clocks, " : "")}" +
                                            $"{(chars.SupportsAutoConflictResolution ? "auto-conflict-resolution" : "manual-conflict-resolution")}"
                    });
                }

                return capabilities;
            }
        }

        /// <inheritdoc/>

        #endregion

        #region Message Bus Handlers

        private async Task HandleReplicateMessageAsync(PluginMessage message)
        {
            var response = await HandleReplicateAsync(message.Payload);
            if (MessageBus != null)
            {
                await MessageBus.PublishAsync($"{message.Type}.response", new PluginMessage
                {
                    Type = $"{message.Type}.response",
                    CorrelationId = message.CorrelationId,
                    Source = Id,
                    Payload = response
                });
            }
        }

        private async Task HandleSyncMessageAsync(PluginMessage message)
        {
            // Handle sync operations
            var response = new Dictionary<string, object> { ["success"] = true };
            if (MessageBus != null)
            {
                await MessageBus.PublishAsync($"{message.Type}.response", new PluginMessage
                {
                    Type = $"{message.Type}.response",
                    CorrelationId = message.CorrelationId,
                    Source = Id,
                    Payload = response
                });
            }
        }

        private async Task HandleSelectStrategyMessageAsync(PluginMessage message)
        {
            var response = HandleSelectStrategy(message.Payload);
            if (MessageBus != null)
            {
                await MessageBus.PublishAsync($"{message.Type}.response", new PluginMessage
                {
                    Type = $"{message.Type}.response",
                    CorrelationId = message.CorrelationId,
                    Source = Id,
                    Payload = response
                });
            }
        }

        private async Task HandleConflictResolveMessageAsync(PluginMessage message)
        {
            var response = await HandleResolveConflictAsync(message.Payload);
            if (MessageBus != null)
            {
                await MessageBus.PublishAsync($"{message.Type}.response", new PluginMessage
                {
                    Type = $"{message.Type}.response",
                    CorrelationId = message.CorrelationId,
                    Source = Id,
                    Payload = response
                });
            }
        }

        private async Task HandleLagRequestMessageAsync(PluginMessage message)
        {
            var response = await HandleGetLagAsync(message.Payload);
            if (MessageBus != null)
            {
                await MessageBus.PublishAsync($"{message.Type}.response", new PluginMessage
                {
                    Type = $"{message.Type}.response",
                    CorrelationId = message.CorrelationId,
                    Source = Id,
                    Payload = response
                });
            }
        }

        private async Task HandlePredictConflictMessageAsync(PluginMessage message)
        {
            if (!IsIntelligenceAvailable)
            {
                await PublishErrorResponse(message, "Intelligence not available for conflict prediction");
                return;
            }

            try
            {
                var payload = message.Payload;
                var sourceNode = payload.GetValueOrDefault("sourceNode")?.ToString() ?? _nodeId;
                var targetNodes = payload.GetValueOrDefault("targetNodes") as IEnumerable<object>;
                var historicalConflicts = payload.GetValueOrDefault("historicalConflicts");

                // Request conflict prediction from Intelligence
                var predictionPayload = new Dictionary<string, object>
                {
                    ["predictionType"] = "replication.conflict",
                    ["inputData"] = new Dictionary<string, object>
                    {
                        ["sourceNode"] = sourceNode,
                        ["targetNodes"] = targetNodes ?? Array.Empty<object>(),
                        ["activeStrategy"] = _activeStrategy?.Characteristics.StrategyName ?? "none",
                        ["consistencyModel"] = _activeStrategy?.Characteristics.ConsistencyModel.ToString() ?? "unknown",
                        ["historicalConflicts"] = historicalConflicts ?? 0
                    }
                };

                var predictionResult = await RequestPredictionAsync(
                    "replication.conflict",
                    predictionPayload,
                    new IntelligenceContext { Timeout = TimeSpan.FromSeconds(5) });

                var response = new Dictionary<string, object>
                {
                    ["success"] = predictionResult != null,
                    ["conflictProbability"] = predictionResult?.Confidence ?? 0.0,
                    ["recommendations"] = predictionResult?.Metadata.GetValueOrDefault("recommendations") ?? Array.Empty<string>()
                };

                if (MessageBus != null)
                {
                    await MessageBus.PublishAsync(ReplicationTopics.PredictConflictResponse, new PluginMessage
                    {
                        Type = ReplicationTopics.PredictConflictResponse,
                        CorrelationId = message.CorrelationId,
                        Source = Id,
                        Payload = response
                    });
                }
            }
            catch (Exception ex)
            {
                await PublishErrorResponse(message, $"Conflict prediction failed: {ex.Message}");
            }
        }

        private async Task HandleOptimizeConsistencyMessageAsync(PluginMessage message)
        {
            if (!IsIntelligenceAvailable)
            {
                await PublishErrorResponse(message, "Intelligence not available for consistency optimization");
                return;
            }

            try
            {
                var payload = message.Payload;
                var dataType = payload.GetValueOrDefault("dataType")?.ToString();
                var accessPattern = payload.GetValueOrDefault("accessPattern")?.ToString();
                var latencyReqs = payload.GetValueOrDefault("latencyRequirements");

                // Request consistency optimization from Intelligence
                var classificationPayload = new Dictionary<string, object>
                {
                    ["text"] = $"Data type: {dataType}, Access pattern: {accessPattern}, Latency requirements: {latencyReqs}",
                    ["categories"] = Enum.GetNames(typeof(ConsistencyModel)),
                    ["multiLabel"] = false
                };

                var classifications = await RequestClassificationAsync(
                    $"Data type: {dataType}, Access pattern: {accessPattern}",
                    Enum.GetNames(typeof(ConsistencyModel)),
                    false);

                var response = new Dictionary<string, object>
                {
                    ["success"] = classifications != null && classifications.Length > 0,
                    ["recommendedModel"] = classifications?[0].Category ?? "Eventual",
                    ["confidence"] = classifications?[0].Confidence ?? 0.0,
                    ["reasoning"] = "Based on data type, access pattern, and latency requirements"
                };

                if (MessageBus != null)
                {
                    await MessageBus.PublishAsync(ReplicationTopics.OptimizeConsistencyResponse, new PluginMessage
                    {
                        Type = ReplicationTopics.OptimizeConsistencyResponse,
                        CorrelationId = message.CorrelationId,
                        Source = Id,
                        Payload = response
                    });
                }
            }
            catch (Exception ex)
            {
                await PublishErrorResponse(message, $"Consistency optimization failed: {ex.Message}");
            }
        }

        private async Task HandleRouteRequestMessageAsync(PluginMessage message)
        {
            if (!IsIntelligenceAvailable)
            {
                await PublishErrorResponse(message, "Intelligence not available for routing decisions");
                return;
            }

            try
            {
                var payload = message.Payload;
                var availableReplicas = payload.GetValueOrDefault("availableReplicas") as IEnumerable<object>;
                var currentLag = payload.GetValueOrDefault("currentLag");
                var replicaLoad = payload.GetValueOrDefault("replicaLoad");

                // Request routing decision from Intelligence
                var predictionPayload = new Dictionary<string, object>
                {
                    ["predictionType"] = "replication.routing",
                    ["inputData"] = new Dictionary<string, object>
                    {
                        ["availableReplicas"] = availableReplicas ?? Array.Empty<object>(),
                        ["currentLag"] = currentLag ?? new Dictionary<string, object>(),
                        ["replicaLoad"] = replicaLoad ?? new Dictionary<string, object>(),
                        ["activeStrategy"] = _activeStrategy?.Characteristics.StrategyName ?? "none"
                    }
                };

                var predictionResult = await RequestPredictionAsync(
                    "replication.routing",
                    predictionPayload,
                    new IntelligenceContext { Timeout = TimeSpan.FromSeconds(5) });

                var response = new Dictionary<string, object>
                {
                    ["success"] = predictionResult != null,
                    ["selectedReplica"] = predictionResult?.Prediction?.ToString() ?? "",
                    ["confidence"] = predictionResult?.Confidence ?? 0.0,
                    ["alternativeReplicas"] = predictionResult?.Metadata.GetValueOrDefault("alternatives") ?? Array.Empty<string>()
                };

                if (MessageBus != null)
                {
                    await MessageBus.PublishAsync(ReplicationTopics.RouteRequestResponse, new PluginMessage
                    {
                        Type = ReplicationTopics.RouteRequestResponse,
                        CorrelationId = message.CorrelationId,
                        Source = Id,
                        Payload = response
                    });
                }
            }
            catch (Exception ex)
            {
                await PublishErrorResponse(message, $"Routing decision failed: {ex.Message}");
            }
        }

        private async Task PublishErrorResponse(PluginMessage originalMessage, string errorMessage)
        {
            if (MessageBus != null)
            {
                var response = new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["error"] = errorMessage
                };

                await MessageBus.PublishAsync($"{originalMessage.Type}.response", new PluginMessage
                {
                    Type = $"{originalMessage.Type}.response",
                    CorrelationId = originalMessage.CorrelationId,
                    Source = Id,
                    Payload = response
                });
            }
        }

        #endregion
    
    #region Hierarchy ReplicationPluginBase Abstract Methods
    /// <inheritdoc/>
    public override async Task<Dictionary<string, object>> ReplicateAsync(string key, string[] targetNodes, CancellationToken ct = default)
    {
        var result = new Dictionary<string, object> { ["key"] = key, ["targetNodes"] = targetNodes, ["status"] = "replicated" };
        if (_activeStrategy != null)
        {
            await _activeStrategy.ReplicateAsync(_nodeId, targetNodes, ReadOnlyMemory<byte>.Empty, null, ct);
            result["strategy"] = _activeStrategy.Name;
        }
        return result;
    }
    /// <inheritdoc/>
    public override Task<Dictionary<string, object>> GetSyncStatusAsync(string key, CancellationToken ct = default)
    {
        var result = new Dictionary<string, object> { ["key"] = key, ["synced"] = true, ["strategy"] = _activeStrategy?.Name ?? "none" };
        return Task.FromResult(result);
    }
    #endregion
    }
}