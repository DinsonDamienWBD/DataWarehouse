using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataProtection
{
    /// <summary>
    /// Registry for data protection strategies.
    /// Provides strategy discovery, registration, and lookup capabilities.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The registry supports:
    /// </para>
    /// <list type="bullet">
    ///   <item>Registration and unregistration of strategies</item>
    ///   <item>Lookup by ID, category, or capabilities</item>
    ///   <item>Intelligence integration for AI-driven strategy selection</item>
    ///   <item>Automatic capability aggregation</item>
    /// </list>
    /// </remarks>
    public sealed class DataProtectionStrategyRegistry
    {
        private readonly BoundedDictionary<string, IDataProtectionStrategy> _strategies = new BoundedDictionary<string, IDataProtectionStrategy>(1000);
        private IMessageBus? _messageBus;

        /// <summary>
        /// Event raised when a strategy is registered.
        /// </summary>
        public event EventHandler<IDataProtectionStrategy>? StrategyRegistered;

        /// <summary>
        /// Event raised when a strategy is unregistered.
        /// </summary>
        public event EventHandler<string>? StrategyUnregistered;

        /// <summary>
        /// Gets the number of registered strategies.
        /// </summary>
        public int Count => _strategies.Count;

        /// <summary>
        /// Gets all registered strategy IDs.
        /// </summary>
        public IReadOnlyCollection<string> StrategyIds => _strategies.Keys.ToArray();

        /// <summary>
        /// Gets all registered strategies.
        /// </summary>
        public IReadOnlyCollection<IDataProtectionStrategy> Strategies => _strategies.Values.ToArray();

        /// <summary>
        /// Configures Intelligence integration for all strategies.
        /// </summary>
        /// <param name="messageBus">Message bus for AI communication.</param>
        public void ConfigureIntelligence(IMessageBus? messageBus)
        {
            _messageBus = messageBus;

            // Configure existing strategies
            foreach (var strategy in _strategies.Values)
            {
                if (strategy is DataProtectionStrategyBase baseStrategy)
                {
                    baseStrategy.ConfigureIntelligence(messageBus);
                }
            }
        }

        /// <summary>
        /// Registers a data protection strategy.
        /// </summary>
        /// <param name="strategy">The strategy to register.</param>
        /// <exception cref="ArgumentNullException">Thrown when strategy is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when a strategy with the same ID is already registered.</exception>
        public void Register(IDataProtectionStrategy strategy)
        {
            ArgumentNullException.ThrowIfNull(strategy);

            if (!_strategies.TryAdd(strategy.StrategyId, strategy))
            {
                throw new InvalidOperationException($"Strategy with ID '{strategy.StrategyId}' is already registered.");
            }

            // Configure Intelligence if available
            if (strategy is DataProtectionStrategyBase baseStrategy)
            {
                baseStrategy.ConfigureIntelligence(_messageBus);
            }

            StrategyRegistered?.Invoke(this, strategy);
        }

        /// <summary>
        /// Registers multiple strategies.
        /// </summary>
        /// <param name="strategies">The strategies to register.</param>
        public void RegisterAll(IEnumerable<IDataProtectionStrategy> strategies)
        {
            foreach (var strategy in strategies)
            {
                Register(strategy);
            }
        }

        /// <summary>
        /// Unregisters a strategy by ID.
        /// </summary>
        /// <param name="strategyId">The strategy ID to unregister.</param>
        /// <returns>True if the strategy was unregistered; false if not found.</returns>
        public bool Unregister(string strategyId)
        {
            if (_strategies.TryRemove(strategyId, out _))
            {
                StrategyUnregistered?.Invoke(this, strategyId);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Gets a strategy by ID.
        /// </summary>
        /// <param name="strategyId">The strategy ID.</param>
        /// <returns>The strategy, or null if not found.</returns>
        public IDataProtectionStrategy? GetStrategy(string strategyId)
        {
            _strategies.TryGetValue(strategyId, out var strategy);
            return strategy;
        }

        /// <summary>
        /// Gets a strategy by ID, throwing if not found.
        /// </summary>
        /// <param name="strategyId">The strategy ID.</param>
        /// <returns>The strategy.</returns>
        /// <exception cref="KeyNotFoundException">Thrown when strategy is not found.</exception>
        public IDataProtectionStrategy GetRequiredStrategy(string strategyId)
        {
            if (!_strategies.TryGetValue(strategyId, out var strategy))
            {
                throw new KeyNotFoundException($"Strategy '{strategyId}' not found in registry.");
            }
            return strategy;
        }

        /// <summary>
        /// Checks if a strategy is registered.
        /// </summary>
        /// <param name="strategyId">The strategy ID to check.</param>
        /// <returns>True if registered; otherwise false.</returns>
        public bool Contains(string strategyId)
        {
            return _strategies.ContainsKey(strategyId);
        }

        /// <summary>
        /// Gets all strategies in a specific category.
        /// </summary>
        /// <param name="category">The category to filter by.</param>
        /// <returns>Strategies in the specified category.</returns>
        public IEnumerable<IDataProtectionStrategy> GetByCategory(DataProtectionCategory category)
        {
            return _strategies.Values.Where(s => s.Category == category);
        }

        /// <summary>
        /// Gets all strategies with specific capabilities.
        /// </summary>
        /// <param name="requiredCapabilities">The required capabilities.</param>
        /// <returns>Strategies with all specified capabilities.</returns>
        public IEnumerable<IDataProtectionStrategy> GetByCapabilities(DataProtectionCapabilities requiredCapabilities)
        {
            return _strategies.Values.Where(s =>
                (s.Capabilities & requiredCapabilities) == requiredCapabilities);
        }

        /// <summary>
        /// Gets all strategies with any of the specified capabilities.
        /// </summary>
        /// <param name="capabilities">The capabilities to check.</param>
        /// <returns>Strategies with any of the specified capabilities.</returns>
        public IEnumerable<IDataProtectionStrategy> GetByAnyCapability(DataProtectionCapabilities capabilities)
        {
            return _strategies.Values.Where(s =>
                (s.Capabilities & capabilities) != DataProtectionCapabilities.None);
        }

        /// <summary>
        /// Gets strategies suitable for cloud backup.
        /// </summary>
        public IEnumerable<IDataProtectionStrategy> GetCloudStrategies()
        {
            return GetByCapabilities(DataProtectionCapabilities.CloudTarget);
        }

        /// <summary>
        /// Gets strategies supporting database-aware backup.
        /// </summary>
        public IEnumerable<IDataProtectionStrategy> GetDatabaseStrategies()
        {
            return GetByCapabilities(DataProtectionCapabilities.DatabaseAware);
        }

        /// <summary>
        /// Gets strategies supporting Kubernetes workload protection.
        /// </summary>
        public IEnumerable<IDataProtectionStrategy> GetKubernetesStrategies()
        {
            return GetByCapabilities(DataProtectionCapabilities.KubernetesIntegration);
        }

        /// <summary>
        /// Gets strategies supporting point-in-time recovery.
        /// </summary>
        public IEnumerable<IDataProtectionStrategy> GetPointInTimeStrategies()
        {
            return GetByCapabilities(DataProtectionCapabilities.PointInTimeRecovery);
        }

        /// <summary>
        /// Gets strategies supporting Intelligence-driven optimization.
        /// </summary>
        public IEnumerable<IDataProtectionStrategy> GetIntelligentStrategies()
        {
            return GetByCapabilities(DataProtectionCapabilities.IntelligenceAware);
        }

        /// <summary>
        /// Selects the best strategy for a given backup scenario.
        /// </summary>
        /// <param name="category">Preferred category.</param>
        /// <param name="requiredCapabilities">Required capabilities.</param>
        /// <param name="preferredCapabilities">Preferred but optional capabilities.</param>
        /// <returns>Best matching strategy, or null if none match requirements.</returns>
        public IDataProtectionStrategy? SelectBestStrategy(
            DataProtectionCategory? category = null,
            DataProtectionCapabilities requiredCapabilities = DataProtectionCapabilities.None,
            DataProtectionCapabilities preferredCapabilities = DataProtectionCapabilities.None)
        {
            var candidates = _strategies.Values.AsEnumerable();

            // Filter by category if specified
            if (category.HasValue)
            {
                candidates = candidates.Where(s => s.Category == category.Value);
            }

            // Filter by required capabilities
            if (requiredCapabilities != DataProtectionCapabilities.None)
            {
                candidates = candidates.Where(s =>
                    (s.Capabilities & requiredCapabilities) == requiredCapabilities);
            }

            // Score by preferred capabilities
            return candidates
                .Select(s => new
                {
                    Strategy = s,
                    Score = CountMatchingCapabilities(s.Capabilities, preferredCapabilities)
                })
                .OrderByDescending(x => x.Score)
                .FirstOrDefault()?.Strategy;
        }

        /// <summary>
        /// Gets aggregated capabilities of all registered strategies.
        /// </summary>
        public DataProtectionCapabilities GetAggregatedCapabilities()
        {
            var capabilities = DataProtectionCapabilities.None;
            foreach (var strategy in _strategies.Values)
            {
                capabilities |= strategy.Capabilities;
            }
            return capabilities;
        }

        /// <summary>
        /// Gets aggregated statistics from all strategies.
        /// </summary>
        public DataProtectionStatistics GetAggregatedStatistics()
        {
            var aggregate = new DataProtectionStatistics();

            foreach (var strategy in _strategies.Values)
            {
                var stats = strategy.GetStatistics();
                aggregate.TotalBackups += stats.TotalBackups;
                aggregate.SuccessfulBackups += stats.SuccessfulBackups;
                aggregate.FailedBackups += stats.FailedBackups;
                aggregate.TotalRestores += stats.TotalRestores;
                aggregate.SuccessfulRestores += stats.SuccessfulRestores;
                aggregate.FailedRestores += stats.FailedRestores;
                aggregate.TotalBytesBackedUp += stats.TotalBytesBackedUp;
                aggregate.TotalBytesStored += stats.TotalBytesStored;
                aggregate.TotalBytesRestored += stats.TotalBytesRestored;
                aggregate.TotalValidations += stats.TotalValidations;
                aggregate.SuccessfulValidations += stats.SuccessfulValidations;
                aggregate.FailedValidations += stats.FailedValidations;

                if (!aggregate.LastBackupTime.HasValue || stats.LastBackupTime > aggregate.LastBackupTime)
                    aggregate.LastBackupTime = stats.LastBackupTime;
                if (!aggregate.LastRestoreTime.HasValue || stats.LastRestoreTime > aggregate.LastRestoreTime)
                    aggregate.LastRestoreTime = stats.LastRestoreTime;
            }

            // Calculate weighted averages for throughput
            if (aggregate.SuccessfulBackups > 0)
            {
                aggregate.AverageBackupThroughput = _strategies.Values
                    .Where(s => s.GetStatistics().SuccessfulBackups > 0)
                    .Average(s => s.GetStatistics().AverageBackupThroughput);
            }

            if (aggregate.SuccessfulRestores > 0)
            {
                aggregate.AverageRestoreThroughput = _strategies.Values
                    .Where(s => s.GetStatistics().SuccessfulRestores > 0)
                    .Average(s => s.GetStatistics().AverageRestoreThroughput);
            }

            return aggregate;
        }

        /// <summary>
        /// Gets all strategy knowledge objects for Intelligence registration.
        /// Per AD-05 (Phase 25b): knowledge construction is now plugin/registry responsibility.
        /// </summary>
        public IEnumerable<KnowledgeObject> GetAllStrategyKnowledge()
        {
            foreach (var strategy in _strategies.Values)
            {
                if (strategy is DataProtectionStrategyBase baseStrategy)
                {
                    yield return new KnowledgeObject
                    {
                        Id = $"dataprotection.strategy.{baseStrategy.StrategyId}",
                        Topic = "dataprotection.strategies",
                        SourcePluginId = "com.datawarehouse.dataprotection.ultimate",
                        SourcePluginName = baseStrategy.StrategyName,
                        KnowledgeType = "capability",
                        Description = $"{baseStrategy.StrategyName} data protection strategy providing {baseStrategy.Category} capabilities",
                        Payload = new Dictionary<string, object>
                        {
                            ["strategyId"] = baseStrategy.StrategyId,
                            ["strategyName"] = baseStrategy.StrategyName,
                            ["category"] = baseStrategy.Category.ToString()
                        },
                        Tags = new[] { "dataprotection", "strategy", baseStrategy.Category.ToString().ToLowerInvariant(), baseStrategy.StrategyId.ToLowerInvariant() },
                        Confidence = 1.0f,
                        Timestamp = DateTimeOffset.UtcNow
                    };
                }
            }
        }

        /// <summary>
        /// Gets all strategy capabilities for capability registration.
        /// Per AD-05 (Phase 25b): capability construction is now plugin/registry responsibility.
        /// </summary>
        public IEnumerable<RegisteredCapability> GetAllStrategyCapabilities()
        {
            foreach (var strategy in _strategies.Values)
            {
                if (strategy is DataProtectionStrategyBase baseStrategy)
                {
                    yield return new RegisteredCapability
                    {
                        CapabilityId = $"dataprotection.{baseStrategy.StrategyId}",
                        DisplayName = baseStrategy.StrategyName,
                        Description = $"{baseStrategy.StrategyName} data protection strategy providing {baseStrategy.Category} capabilities",
                        Category = SDK.Contracts.CapabilityCategory.Custom,
                        SubCategory = "DataProtection",
                        PluginId = "com.datawarehouse.dataprotection.ultimate",
                        PluginName = "Ultimate Data Protection",
                        PluginVersion = "1.0.0",
                        Tags = new[] { "dataprotection", "strategy", baseStrategy.Category.ToString().ToLowerInvariant() },
                        SemanticDescription = $"Use {baseStrategy.StrategyName} for {baseStrategy.Category} data protection operations"
                    };
                }
            }
        }

        /// <summary>
        /// Gets a summary of registered strategies by category.
        /// </summary>
        public Dictionary<DataProtectionCategory, int> GetCategorySummary()
        {
            return _strategies.Values
                .GroupBy(s => s.Category)
                .ToDictionary(g => g.Key, g => g.Count());
        }

        private static int CountMatchingCapabilities(DataProtectionCapabilities actual, DataProtectionCapabilities required)
        {
            var matching = actual & required;
            int count = 0;
            while (matching != DataProtectionCapabilities.None)
            {
                count += (int)matching & 1;
                matching = (DataProtectionCapabilities)((int)matching >> 1);
            }
            return count;
        }
    }
}
