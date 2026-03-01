using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataProtection
{
    /// <summary>
    /// Ultimate Data Protection plugin providing 40+ backup and recovery strategies.
    /// Supports full, incremental, CDP, snapshots, archives, cloud backup, disaster recovery,
    /// database-aware backup, and Kubernetes workload protection with Intelligence-driven optimization.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Supported protection categories:
    /// </para>
    /// <list type="bullet">
    ///   <item>Full Backup: Streaming, parallel, block-level, SnapMirror-style</item>
    ///   <item>Incremental: Change tracking, journal-based, checksum, timestamp, forever-incremental</item>
    ///   <item>CDP: Journal, replication, snapshot, hybrid continuous protection</item>
    ///   <item>Snapshots: COW, ROW, VSS, LVM, ZFS, cloud snapshots</item>
    ///   <item>Archive: Tape, cold storage, WORM, compliance, tiered</item>
    ///   <item>Cloud: S3, Azure Blob, GCS, multi-cloud</item>
    ///   <item>Disaster Recovery: Active-passive, active-active, pilot light, warm standby, cross-region</item>
    ///   <item>Database: SQL Server, PostgreSQL, MySQL, Oracle RMAN, MongoDB, Cassandra</item>
    ///   <item>Kubernetes: Velero, etcd, PVC, Helm, CRD backup</item>
    ///   <item>Intelligence: Predictive, anomaly-aware, optimized retention, smart recovery</item>
    /// </list>
    /// </remarks>
    public sealed class UltimateDataProtectionPlugin : SecurityPluginBase
    {
        // NOTE(65.4-07): _registry is retained as a typed lookup layer for domain-specific interfaces
        // (IDataProtectionStrategy, DataProtectionCapabilities, etc.). Strategies also registered via
        // base-class RegisterStrategy() for unified lifecycle management via PluginBase.StrategyRegistry.
        private readonly DataProtectionStrategyRegistry _registry = new();
        private readonly BoundedDictionary<string, object> _activeOperations = new BoundedDictionary<string, object>(1000);

        /// <inheritdoc/>
        public override string Id => "com.datawarehouse.dataprotection.ultimate";

        /// <inheritdoc/>
        public override string Name => "Ultimate Data Protection";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override string SecurityDomain => "DataProtection";

        /// <inheritdoc/>
        public override PluginCategory Category => PluginCategory.FeatureProvider;

        /// <summary>
        /// Gets the strategy registry for accessing and managing strategies (typed lookup thin wrapper).
        /// </summary>
        public DataProtectionStrategyRegistry Registry => _registry;

        /// <summary>
        /// Initializes a new instance of the UltimateDataProtectionPlugin.
        /// Discovers and registers all available strategies.
        /// </summary>
        public UltimateDataProtectionPlugin()
        {
            DiscoverAndRegisterStrategies();
        }

        /// <summary>
        /// Gets a strategy by ID.
        /// </summary>
        /// <param name="strategyId">The strategy ID.</param>
        /// <returns>The strategy, or null if not found.</returns>
        public IDataProtectionStrategy? GetStrategy(string strategyId)
        {
            return _registry.GetStrategy(strategyId);
        }

        /// <summary>
        /// Gets all registered strategy IDs.
        /// </summary>
        public IReadOnlyCollection<string> GetRegisteredStrategies()
        {
            return _registry.StrategyIds;
        }

        /// <summary>
        /// Selects the best strategy for a backup scenario.
        /// </summary>
        /// <param name="category">Preferred category.</param>
        /// <param name="requiredCapabilities">Required capabilities.</param>
        /// <returns>Best matching strategy.</returns>
        public IDataProtectionStrategy? SelectStrategy(
            DataProtectionCategory? category = null,
            DataProtectionCapabilities requiredCapabilities = DataProtectionCapabilities.None)
        {
            return _registry.SelectBestStrategy(category, requiredCapabilities);
        }

        /// <summary>
        /// Creates a backup using the specified strategy.
        /// </summary>
        /// <param name="strategyId">Strategy ID to use.</param>
        /// <param name="request">Backup request parameters.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Backup result.</returns>
        public async Task<BackupResult> CreateBackupAsync(
            string strategyId,
            BackupRequest request,
            CancellationToken ct = default)
        {
            var strategy = _registry.GetRequiredStrategy(strategyId);
            return await strategy.CreateBackupAsync(request, ct);
        }

        /// <summary>
        /// Restores from a backup.
        /// </summary>
        /// <param name="strategyId">Strategy ID to use.</param>
        /// <param name="request">Restore request parameters.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Restore result.</returns>
        public async Task<RestoreResult> RestoreAsync(
            string strategyId,
            RestoreRequest request,
            CancellationToken ct = default)
        {
            var strategy = _registry.GetRequiredStrategy(strategyId);
            return await strategy.RestoreAsync(request, ct);
        }

        /// <summary>
        /// Lists backups matching the query.
        /// </summary>
        /// <param name="strategyId">Strategy ID to query.</param>
        /// <param name="query">Query parameters.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Matching backup entries.</returns>
        public async Task<IEnumerable<BackupCatalogEntry>> ListBackupsAsync(
            string strategyId,
            BackupListQuery query,
            CancellationToken ct = default)
        {
            var strategy = _registry.GetRequiredStrategy(strategyId);
            return await strategy.ListBackupsAsync(query, ct);
        }

        /// <summary>
        /// Lists all backups across all strategies.
        /// </summary>
        /// <param name="query">Query parameters.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>All matching backup entries.</returns>
        public async Task<IEnumerable<BackupCatalogEntry>> ListAllBackupsAsync(
            BackupListQuery query,
            CancellationToken ct = default)
        {
            var results = new List<BackupCatalogEntry>();
            foreach (var strategy in _registry.Strategies)
            {
                var entries = await strategy.ListBackupsAsync(query, ct);
                results.AddRange(entries);
            }
            return results.OrderByDescending(e => e.CreatedAt);
        }

        /// <summary>
        /// Validates a backup.
        /// </summary>
        /// <param name="strategyId">Strategy ID.</param>
        /// <param name="backupId">Backup ID to validate.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Validation result.</returns>
        public async Task<ValidationResult> ValidateBackupAsync(
            string strategyId,
            string backupId,
            CancellationToken ct = default)
        {
            var strategy = _registry.GetRequiredStrategy(strategyId);
            return await strategy.ValidateBackupAsync(backupId, ct);
        }

        /// <summary>
        /// Gets aggregated statistics from all strategies.
        /// </summary>
        public DataProtectionStatistics GetStatistics()
        {
            return _registry.GetAggregatedStatistics();
        }

        #region Plugin Lifecycle

        /// <inheritdoc/>
        protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct)
        {
            await base.OnStartWithIntelligenceAsync(ct);

            // Configure Intelligence for all strategies
            _registry.ConfigureIntelligence(MessageBus);

            // Register capabilities with Intelligence
            if (MessageBus != null)
            {
                await MessageBus.PublishAsync(IntelligenceTopics.QueryCapability, new PluginMessage
                {
                    Type = "capability.register",
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["pluginId"] = Id,
                        ["pluginName"] = Name,
                        ["pluginType"] = "dataprotection",
                        ["capabilities"] = new Dictionary<string, object>
                        {
                            ["strategyCount"] = _registry.Count,
                            ["categories"] = _registry.GetCategorySummary(),
                            ["aggregatedCapabilities"] = _registry.GetAggregatedCapabilities().ToString(),
                            ["supportsPredictiveBackup"] = true,
                            ["supportsAnomalyDetection"] = true,
                            ["supportsSmartRecovery"] = true
                        },
                        ["semanticDescription"] = SemanticDescription,
                        ["tags"] = SemanticTags
                    }
                }, ct);

                // Subscribe to Intelligence requests
                SubscribeToIntelligenceRequests();
            }
        }

        /// <inheritdoc/>
        protected override Task OnStartWithoutIntelligenceAsync(CancellationToken ct)
        {
            // Plugin works without Intelligence, just with reduced functionality
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        protected override Task OnStartCoreAsync(CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        #endregion

        #region Capability and Knowledge Registration

        /// <inheritdoc/>
        protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
        {
            get
            {
                var capabilities = new List<RegisteredCapability>
                {
                    new RegisteredCapability
                    {
                        CapabilityId = $"{Id}.backup",
                        DisplayName = $"{Name} - Backup",
                        Description = "Create backups using various strategies",
                        Category = SDK.Contracts.CapabilityCategory.Custom,
                        SubCategory = "DataProtection",
                        PluginId = Id,
                        PluginName = Name,
                        PluginVersion = Version,
                        Tags = new[] { "backup", "dataprotection", "recovery" }
                    },
                    new RegisteredCapability
                    {
                        CapabilityId = $"{Id}.restore",
                        DisplayName = $"{Name} - Restore",
                        Description = "Restore data from backups",
                        Category = SDK.Contracts.CapabilityCategory.Custom,
                        SubCategory = "DataProtection",
                        PluginId = Id,
                        PluginName = Name,
                        PluginVersion = Version,
                        Tags = new[] { "restore", "dataprotection", "recovery" }
                    },
                    new RegisteredCapability
                    {
                        CapabilityId = $"{Id}.validate",
                        DisplayName = $"{Name} - Validate",
                        Description = "Validate backup integrity",
                        Category = SDK.Contracts.CapabilityCategory.Custom,
                        SubCategory = "DataProtection",
                        PluginId = Id,
                        PluginName = Name,
                        PluginVersion = Version,
                        Tags = new[] { "validation", "dataprotection", "integrity" }
                    }
                };

                // Add capabilities from all strategies
                capabilities.AddRange(_registry.GetAllStrategyCapabilities());

                return capabilities;
            }
        }

        /// <inheritdoc/>
        protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
        {
            var knowledge = new List<KnowledgeObject>(base.GetStaticKnowledge());

            // Add plugin-level knowledge
            knowledge.Add(new KnowledgeObject
            {
                Id = $"{Id}.overview",
                Topic = "dataprotection",
                SourcePluginId = Id,
                SourcePluginName = Name,
                KnowledgeType = "capability",
                Description = $"Ultimate Data Protection with {_registry.Count} strategies",
                Payload = new Dictionary<string, object>
                {
                    ["strategyCount"] = _registry.Count,
                    ["categories"] = Enum.GetNames<DataProtectionCategory>(),
                    ["supportedCapabilities"] = _registry.GetAggregatedCapabilities().ToString()
                },
                Tags = new[] { "dataprotection", "backup", "recovery", "overview" },
                Confidence = 1.0f,
                Timestamp = DateTimeOffset.UtcNow
            });

            // Add knowledge from all strategies
            knowledge.AddRange(_registry.GetAllStrategyKnowledge());

            return knowledge;
        }

        /// <summary>
        /// Semantic description for AI discovery.
        /// </summary>
        public string SemanticDescription =>
            "Ultimate data protection plugin providing 40+ backup and recovery strategies. " +
            "Supports full, incremental, CDP, snapshots, archives, cloud backup, disaster recovery, " +
            "database-aware backup, and Kubernetes workload protection with AI-driven optimization.";

        /// <summary>
        /// Semantic tags for AI discovery.
        /// </summary>
        public string[] SemanticTags => new[]
        {
            "backup", "restore", "recovery", "dataprotection", "cdp", "snapshot",
            "disaster-recovery", "cloud-backup", "database-backup", "kubernetes",
            "incremental", "full-backup", "archive", "retention", "ai-enhanced"
        };

        #endregion

        #region Message Handling

        /// <inheritdoc/>
        public override async Task OnMessageAsync(PluginMessage message)
        {
            switch (message.Type)
            {
                case "dataprotection.list.strategies":
                    await HandleListStrategiesAsync(message);
                    break;

                case "dataprotection.select.strategy":
                    await HandleSelectStrategyAsync(message);
                    break;

                case "dataprotection.backup.request":
                    await HandleBackupRequestAsync(message);
                    break;

                case "dataprotection.restore.request":
                    await HandleRestoreRequestAsync(message);
                    break;

                case "dataprotection.statistics":
                    await HandleStatisticsRequestAsync(message);
                    break;

                default:
                    await base.OnMessageAsync(message);
                    break;
            }
        }

        private Task HandleListStrategiesAsync(PluginMessage message)
        {
            var categoryFilter = message.Payload.TryGetValue("category", out var catObj) && catObj is string catStr
                && Enum.TryParse<DataProtectionCategory>(catStr, true, out var cat)
                ? cat
                : (DataProtectionCategory?)null;

            var strategies = categoryFilter.HasValue
                ? _registry.GetByCategory(categoryFilter.Value)
                : _registry.Strategies;

            var strategyList = strategies.Select(s => new Dictionary<string, object>
            {
                ["id"] = s.StrategyId,
                ["name"] = s.StrategyName,
                ["category"] = s.Category.ToString(),
                ["capabilities"] = s.Capabilities.ToString()
            }).ToList();

            message.Payload["strategies"] = strategyList;
            message.Payload["count"] = strategyList.Count;
            message.Payload["success"] = true;
            return Task.CompletedTask;
        }

        private Task HandleSelectStrategyAsync(PluginMessage message)
        {
            var categoryFilter = message.Payload.TryGetValue("category", out var catObj) && catObj is string catStr
                && Enum.TryParse<DataProtectionCategory>(catStr, true, out var cat)
                ? cat
                : (DataProtectionCategory?)null;

            var requiredCaps = DataProtectionCapabilities.None;
            if (message.Payload.TryGetValue("requiredCapabilities", out var capsObj) && capsObj is string capsStr
                && Enum.TryParse<DataProtectionCapabilities>(capsStr, true, out var caps))
            {
                requiredCaps = caps;
            }

            var strategy = _registry.SelectBestStrategy(categoryFilter, requiredCaps);

            if (strategy != null)
            {
                message.Payload["success"] = true;
                message.Payload["strategyId"] = strategy.StrategyId;
                message.Payload["strategyName"] = strategy.StrategyName;
                message.Payload["category"] = strategy.Category.ToString();
                message.Payload["capabilities"] = strategy.Capabilities.ToString();
            }
            else
            {
                message.Payload["success"] = false;
                message.Payload["error"] = "No matching strategy found for the specified criteria";
            }

            return Task.CompletedTask;
        }

        private async Task HandleBackupRequestAsync(PluginMessage message)
        {
            var strategyId = message.Payload.TryGetValue("strategyId", out var sidObj) && sidObj is string sid
                ? sid : null;

            if (string.IsNullOrEmpty(strategyId))
            {
                message.Payload["success"] = false;
                message.Payload["error"] = "Missing 'strategyId' parameter";
                return;
            }

            var strategy = _registry.GetStrategy(strategyId);
            if (strategy == null)
            {
                message.Payload["success"] = false;
                message.Payload["error"] = $"Strategy '{strategyId}' not found";
                return;
            }

            var request = new BackupRequest
            {
                BackupName = message.Payload.TryGetValue("backupName", out var nameObj) && nameObj is string name ? name : null,
                Sources = message.Payload.TryGetValue("sources", out var srcObj) && srcObj is IEnumerable<string> sources
                    ? sources.ToArray() : Array.Empty<string>(),
                Destination = message.Payload.TryGetValue("destination", out var destObj) && destObj is string dest ? dest : null,
                EnableCompression = !message.Payload.TryGetValue("enableCompression", out var compObj) || compObj is not false,
                EnableEncryption = message.Payload.TryGetValue("enableEncryption", out var encObj) && encObj is true
            };

            var result = await strategy.CreateBackupAsync(request);

            message.Payload["success"] = result.Success;
            message.Payload["backupId"] = result.BackupId;
            message.Payload["strategyId"] = strategyId;
            message.Payload["totalBytes"] = result.TotalBytes;
            message.Payload["storedBytes"] = result.StoredBytes;
            message.Payload["fileCount"] = result.FileCount;
            if (result.ErrorMessage != null)
                message.Payload["error"] = result.ErrorMessage;
        }

        private async Task HandleRestoreRequestAsync(PluginMessage message)
        {
            var strategyId = message.Payload.TryGetValue("strategyId", out var sidObj) && sidObj is string sid
                ? sid : null;
            var backupId = message.Payload.TryGetValue("backupId", out var bidObj) && bidObj is string bid
                ? bid : null;

            if (string.IsNullOrEmpty(strategyId) || string.IsNullOrEmpty(backupId))
            {
                message.Payload["success"] = false;
                message.Payload["error"] = "Missing 'strategyId' or 'backupId' parameter";
                return;
            }

            var strategy = _registry.GetStrategy(strategyId);
            if (strategy == null)
            {
                message.Payload["success"] = false;
                message.Payload["error"] = $"Strategy '{strategyId}' not found";
                return;
            }

            var request = new RestoreRequest
            {
                BackupId = backupId,
                TargetPath = message.Payload.TryGetValue("targetPath", out var tpObj) && tpObj is string tp ? tp : null,
                OverwriteExisting = message.Payload.TryGetValue("overwrite", out var owObj) && owObj is true
            };

            var result = await strategy.RestoreAsync(request);

            message.Payload["success"] = result.Success;
            message.Payload["restoreId"] = result.RestoreId;
            message.Payload["strategyId"] = strategyId;
            message.Payload["totalBytes"] = result.TotalBytes;
            message.Payload["fileCount"] = result.FileCount;
            if (result.ErrorMessage != null)
                message.Payload["error"] = result.ErrorMessage;
        }

        private Task HandleStatisticsRequestAsync(PluginMessage message)
        {
            var stats = _registry.GetAggregatedStatistics();

            message.Payload["success"] = true;
            message.Payload["totalBackups"] = stats.TotalBackups;
            message.Payload["successfulBackups"] = stats.SuccessfulBackups;
            message.Payload["failedBackups"] = stats.FailedBackups;
            message.Payload["totalRestores"] = stats.TotalRestores;
            message.Payload["successfulRestores"] = stats.SuccessfulRestores;
            message.Payload["failedRestores"] = stats.FailedRestores;
            message.Payload["totalBytesBackedUp"] = stats.TotalBytesBackedUp;
            message.Payload["totalBytesStored"] = stats.TotalBytesStored;
            message.Payload["totalBytesRestored"] = stats.TotalBytesRestored;
            message.Payload["deduplicationRatio"] = stats.DeduplicationRatio;
            message.Payload["spaceSavingsPercent"] = stats.SpaceSavingsPercent;
            message.Payload["totalValidations"] = stats.TotalValidations;
            message.Payload["registeredStrategies"] = _registry.Count;
            message.Payload["categorySummary"] = _registry.GetCategorySummary()
                .ToDictionary(kv => kv.Key.ToString(), kv => (object)kv.Value);
            return Task.CompletedTask;
        }

        private void SubscribeToIntelligenceRequests()
        {
            if (MessageBus == null) return;

            // Subscribe to recommendation requests
            MessageBus.Subscribe(DataProtectionTopics.IntelligenceRecommendation, async msg =>
            {
                if (msg.Payload.TryGetValue("scenario", out var scenarioObj) && scenarioObj is string scenario)
                {
                    var recommendation = RecommendStrategy(scenario, msg.Payload);
                    await MessageBus.PublishAsync(DataProtectionTopics.IntelligenceRecommendationResponse, new PluginMessage
                    {
                        Type = "dataprotection.recommendation.response",
                        CorrelationId = msg.CorrelationId,
                        Source = Id,
                        Payload = recommendation
                    });
                }
            });

            // Subscribe to recovery point requests
            MessageBus.Subscribe(DataProtectionTopics.IntelligenceRecoveryPoint, async msg =>
            {
                if (msg.Payload.TryGetValue("backupId", out var backupIdObj) && backupIdObj is string backupId)
                {
                    var recommendation = await RecommendRecoveryPointAsync(backupId, msg.Payload);
                    await MessageBus.PublishAsync(DataProtectionTopics.IntelligenceRecoveryPointResponse, new PluginMessage
                    {
                        Type = "dataprotection.recovery.point.response",
                        CorrelationId = msg.CorrelationId,
                        Source = Id,
                        Payload = recommendation
                    });
                }
            });
        }

        private Dictionary<string, object> RecommendStrategy(string scenario, Dictionary<string, object> context)
        {
            // AI-driven strategy recommendation
            DataProtectionCategory? category = null;
            DataProtectionCapabilities required = DataProtectionCapabilities.None;

            switch (scenario.ToLowerInvariant())
            {
                case "database":
                    category = DataProtectionCategory.FullBackup;
                    required = DataProtectionCapabilities.DatabaseAware | DataProtectionCapabilities.ApplicationAware;
                    break;
                case "kubernetes":
                    required = DataProtectionCapabilities.KubernetesIntegration;
                    break;
                case "cloud":
                    required = DataProtectionCapabilities.CloudTarget;
                    break;
                case "realtime":
                case "cdp":
                    category = DataProtectionCategory.ContinuousProtection;
                    break;
                case "disaster-recovery":
                case "dr":
                    category = DataProtectionCategory.DisasterRecovery;
                    break;
                case "archive":
                    category = DataProtectionCategory.Archive;
                    break;
            }

            var strategy = _registry.SelectBestStrategy(category, required);

            return new Dictionary<string, object>
            {
                ["success"] = strategy != null,
                ["recommendedStrategy"] = strategy?.StrategyId ?? "streaming-full-backup",
                ["strategyName"] = strategy?.StrategyName ?? "Streaming Full Backup",
                ["category"] = strategy?.Category.ToString() ?? category?.ToString() ?? "FullBackup",
                ["reasoning"] = $"Selected based on scenario '{scenario}' requirements"
            };
        }

        private Task<Dictionary<string, object>> RecommendRecoveryPointAsync(string backupId, Dictionary<string, object> context)
        {
            // Analyze registered strategies to find the best recovery point.
            // Collect all catalog entries synchronously from strategy registries where available.
            var allEntries = new List<BackupCatalogEntry>();
            foreach (var strategy in _registry.Strategies)
            {
                try
                {
                    // Use synchronous catalog access if the strategy exposes it
                    var entries = strategy.GetCatalogEntries();
                    allEntries.AddRange(entries);
                }
                catch
                {
                    // Strategy doesn't support sync catalog access; skip
                }
            }

            var candidates = allEntries
                .Where(e => !e.IsCorrupted)
                .OrderByDescending(e => e.IsVerified ? 1 : 0)
                .ThenByDescending(e => e.CreatedAt)
                .ToList();

            if (candidates.Count == 0)
            {
                // No catalog entries; return the input backupId with low confidence
                return Task.FromResult(new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["recommendedBackupId"] = backupId,
                    ["confidence"] = 0.5,
                    ["reasoning"] = "No catalog entries available; returning requested backup ID as-is"
                });
            }

            // Prefer the requested backup if it is among valid candidates, otherwise suggest best.
            var best = candidates.FirstOrDefault(e => e.BackupId == backupId) ?? candidates[0];
            var isRequested = best.BackupId == backupId;
            var confidence = best.IsVerified ? 0.95 : 0.7;

            return Task.FromResult(new Dictionary<string, object>
            {
                ["success"] = true,
                ["recommendedBackupId"] = best.BackupId,
                ["confidence"] = confidence,
                ["reasoning"] = isRequested
                    ? $"Requested backup '{backupId}' is valid and {(best.IsVerified ? "integrity-verified" : "not yet verified")}"
                    : $"Requested backup not found; most recent valid backup '{best.BackupId}' recommended (age: {(DateTimeOffset.UtcNow - best.CreatedAt).TotalHours:F1}h)"
            });
        }

        #endregion

        #region Strategy Discovery

        /// <summary>
        /// Discovers and registers all data protection strategies via reflection.
        /// Dual-registers each strategy: domain registry for typed dispatch + base RegisterStrategy() for unified lifecycle (AD-65.4).
        /// </summary>
        private void DiscoverAndRegisterStrategies()
        {
            var strategyTypes = GetType().Assembly
                .GetTypes()
                .Where(t => !t.IsAbstract && typeof(DataProtectionStrategyBase).IsAssignableFrom(t));

            foreach (var strategyType in strategyTypes)
            {
                try
                {
                    if (Activator.CreateInstance(strategyType) is DataProtectionStrategyBase strategy)
                    {
                        // Register in domain registry for typed dispatch (IDataProtectionStrategy, capability filtering, etc.)
                        _registry.Register(strategy);

                        // Also register via PluginBase base-class registry for unified strategy lifecycle (AD-65.4)
                        RegisterStrategy(strategy);
                    }
                }
                catch
                {

                    // Strategy failed to instantiate, skip
                    System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
                }
            }
        }

        #endregion

        #region Metadata

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["Description"] = "Ultimate Data Protection plugin with 40+ backup and recovery strategies";
            metadata["StrategyCount"] = _registry.Count;
            metadata["Categories"] = _registry.GetCategorySummary();
            metadata["AggregatedCapabilities"] = _registry.GetAggregatedCapabilities().ToString();
            metadata["SupportsIntelligentBackup"] = true;
            metadata["SupportsAnomalyDetection"] = true;
            metadata["SupportsSmartRecovery"] = true;
            return metadata;
        }

        #endregion
    }
}
