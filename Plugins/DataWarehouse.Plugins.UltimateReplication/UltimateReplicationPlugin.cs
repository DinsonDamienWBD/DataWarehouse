using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Contracts.Replication;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.Plugins.UltimateReplication.Strategies.Core;
using DataWarehouse.Plugins.UltimateReplication.Strategies.Geo;
using DataWarehouse.Plugins.UltimateReplication.Strategies.Federation;

namespace DataWarehouse.Plugins.UltimateReplication
{
    /// <summary>
    /// Ultimate Replication plugin consolidating 8 comprehensive replication strategies.
    /// Provides CRDT-based, multi-master, real-time, delta, geo, cross-region, federation,
    /// and federated query replication with full vector clock support, conflict resolution,
    /// lag tracking, and anti-entropy protocols.
    /// </summary>
    /// <remarks>
    /// Supported replication strategies:
    /// <list type="bullet">
    ///   <item>CRDT: Conflict-free Replicated Data Types (G-Counter, PN-Counter, OR-Set, LWW-Register)</item>
    ///   <item>MultiMaster: Bidirectional sync with configurable conflict resolution</item>
    ///   <item>RealTimeSync: Sub-second lag via WebSocket/gRPC streaming and CDC</item>
    ///   <item>DeltaSync: Binary diff computation with version chain tracking</item>
    ///   <item>GeoReplication: Region-aware routing with WAN optimization</item>
    ///   <item>CrossRegion: Async replication with bounded staleness and auto-failover</item>
    ///   <item>Federation: Multiple data source coordination with distributed transactions</item>
    ///   <item>FederatedQuery: Query routing with result aggregation and read preferences</item>
    /// </list>
    ///
    /// Message Commands:
    /// - replication.strategy.list: List available strategies
    /// - replication.strategy.select: Select active strategy
    /// - replication.strategy.info: Get strategy details
    /// - replication.replicate: Replicate data
    /// - replication.status: Get replication status
    /// - replication.lag: Get replication lag
    /// - replication.conflict.detect: Detect conflicts
    /// - replication.conflict.resolve: Resolve conflicts
    /// </remarks>
    public sealed class UltimateReplicationPlugin : IntelligenceAwarePluginBase
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
            // Register core strategies
            _registry.Register(new CrdtReplicationStrategy());
            _registry.Register(new MultiMasterStrategy());
            _registry.Register(new RealTimeSyncStrategy());
            _registry.Register(new DeltaSyncStrategy());

            // Register geo strategies
            _registry.Register(new GeoReplicationStrategy());
            _registry.Register(new CrossRegionStrategy());

            // Register federation strategies
            _registry.Register(new FederationStrategy());
            _registry.Register(new FederatedQueryStrategy());
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
            metadata["SemanticDescription"] = "Ultimate replication plugin with 8 strategies including CRDT, multi-master, real-time sync, delta sync, geo-replication, cross-region, federation, and federated query support";
            metadata["SemanticTags"] = new[] { "replication", "crdt", "multi-master", "geo", "federation", "vector-clock", "conflict-resolution" };
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
    }
}
