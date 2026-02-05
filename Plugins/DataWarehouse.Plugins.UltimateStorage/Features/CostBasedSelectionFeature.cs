using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Features
{
    /// <summary>
    /// Cost-Based Selection Feature (C5) - Automatic cost-aware backend selection and tracking.
    ///
    /// Features:
    /// - Per-backend cost configuration (per GB storage, per operation, per egress)
    /// - Automatic cheapest backend selection for writes
    /// - Cost-aware retrieval (prefer cheaper backends when multiple copies exist)
    /// - Monthly cost tracking and reporting
    /// - Cost projections based on current usage
    /// - Cost optimization recommendations
    /// - Budget alerts and limits
    /// - Cost breakdown by operation type and backend
    /// </summary>
    public sealed class CostBasedSelectionFeature : IDisposable
    {
        private readonly StorageStrategyRegistry _registry;
        private readonly ConcurrentDictionary<string, BackendCostConfig> _costConfigs = new();
        private readonly ConcurrentDictionary<string, BackendUsageMetrics> _usageMetrics = new();
        private readonly List<CostEntry> _costHistory = new();
        private readonly object _historyLock = new();
        private readonly Timer _costCalculationTimer;
        private bool _disposed;

        // Configuration
        private TimeSpan _costCalculationInterval = TimeSpan.FromHours(1);
        private decimal _monthlyBudgetLimit = 0; // 0 = no limit
        private bool _enableBudgetAlerts = false;
        private bool _enableCostOptimization = true;

        // Statistics
        private long _totalOperations;
        private long _costOptimizedSelections;
        private long _budgetAlertsTriggered;

        /// <summary>
        /// Initializes a new instance of the CostBasedSelectionFeature.
        /// </summary>
        /// <param name="registry">The storage strategy registry.</param>
        public CostBasedSelectionFeature(StorageStrategyRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));

            // Initialize default cost configurations
            InitializeDefaultCostConfigs();

            // Start background cost calculation
            _costCalculationTimer = new Timer(
                callback: _ => CalculateCurrentCosts(),
                state: null,
                dueTime: _costCalculationInterval,
                period: _costCalculationInterval);
        }

        /// <summary>
        /// Gets the total number of cost-optimized backend selections.
        /// </summary>
        public long CostOptimizedSelections => Interlocked.Read(ref _costOptimizedSelections);

        /// <summary>
        /// Gets the total number of budget alerts triggered.
        /// </summary>
        public long BudgetAlertsTriggered => Interlocked.Read(ref _budgetAlertsTriggered);

        /// <summary>
        /// Gets or sets the monthly budget limit (0 = no limit).
        /// </summary>
        public decimal MonthlyBudgetLimit
        {
            get => _monthlyBudgetLimit;
            set => _monthlyBudgetLimit = value >= 0 ? value : 0;
        }

        /// <summary>
        /// Gets or sets whether cost optimization is enabled.
        /// </summary>
        public bool EnableCostOptimization
        {
            get => _enableCostOptimization;
            set => _enableCostOptimization = value;
        }

        /// <summary>
        /// Configures cost parameters for a backend.
        /// </summary>
        /// <param name="backendId">Backend ID.</param>
        /// <param name="config">Cost configuration.</param>
        public void ConfigureBackendCost(string backendId, BackendCostConfig config)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(backendId);
            ArgumentNullException.ThrowIfNull(config);

            config.BackendId = backendId;
            _costConfigs[backendId] = config;

            // Initialize usage metrics if not exists
            _usageMetrics.TryAdd(backendId, new BackendUsageMetrics
            {
                BackendId = backendId
            });
        }

        /// <summary>
        /// Gets cost configuration for a backend.
        /// </summary>
        /// <param name="backendId">Backend ID.</param>
        /// <returns>Cost configuration or null if not found.</returns>
        public BackendCostConfig? GetBackendCostConfig(string backendId)
        {
            return _costConfigs.TryGetValue(backendId, out var config) ? config : null;
        }

        /// <summary>
        /// Selects the cheapest backend for storing data of a given size.
        /// </summary>
        /// <param name="dataSizeBytes">Size of data to store.</param>
        /// <param name="tier">Optional tier requirement.</param>
        /// <returns>Backend selection result.</returns>
        public BackendSelectionResult SelectCheapestBackend(long dataSizeBytes, StorageTier? tier = null)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            Interlocked.Increment(ref _totalOperations);

            var candidates = tier.HasValue
                ? _registry.GetStrategiesByTier(tier.Value).ToList()
                : _registry.GetAllStrategies().ToList();

            if (candidates.Count == 0)
            {
                return new BackendSelectionResult
                {
                    Success = false,
                    ErrorMessage = "No suitable backends found"
                };
            }

            // Calculate cost for each candidate
            var costComparisons = new List<(string backendId, decimal totalCost)>();

            foreach (var backend in candidates)
            {
                if (!_costConfigs.TryGetValue(backend.StrategyId, out var costConfig))
                {
                    continue;
                }

                var storageCost = CalculateStorageCost(dataSizeBytes, costConfig);
                var writeCost = costConfig.CostPerWriteOperation;

                var totalCost = storageCost + writeCost;
                costComparisons.Add((backend.StrategyId, totalCost));
            }

            if (costComparisons.Count == 0)
            {
                // No cost config available, select first available
                return new BackendSelectionResult
                {
                    Success = true,
                    SelectedBackend = candidates[0].StrategyId,
                    EstimatedCost = 0,
                    Reason = "No cost configuration available"
                };
            }

            // Select cheapest
            var cheapest = costComparisons.OrderBy(c => c.totalCost).First();

            Interlocked.Increment(ref _costOptimizedSelections);

            return new BackendSelectionResult
            {
                Success = true,
                SelectedBackend = cheapest.backendId,
                EstimatedCost = cheapest.totalCost,
                Reason = "Lowest cost backend",
                Alternatives = costComparisons
                    .Where(c => c.backendId != cheapest.backendId)
                    .OrderBy(c => c.totalCost)
                    .Take(3)
                    .Select(c => new BackendAlternative
                    {
                        BackendId = c.backendId,
                        EstimatedCost = c.totalCost
                    })
                    .ToList()
            };
        }

        /// <summary>
        /// Selects the most cost-effective backend for reading data.
        /// Considers both storage cost and egress cost.
        /// </summary>
        /// <param name="dataSizeBytes">Size of data to read.</param>
        /// <param name="availableBackends">List of backends where data is available.</param>
        /// <returns>Backend selection result.</returns>
        public BackendSelectionResult SelectCheapestReadBackend(
            long dataSizeBytes,
            IReadOnlyList<string> availableBackends)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(availableBackends);

            if (availableBackends.Count == 0)
            {
                return new BackendSelectionResult
                {
                    Success = false,
                    ErrorMessage = "No backends specified"
                };
            }

            if (availableBackends.Count == 1)
            {
                return new BackendSelectionResult
                {
                    Success = true,
                    SelectedBackend = availableBackends[0],
                    EstimatedCost = 0,
                    Reason = "Only one backend available"
                };
            }

            // Calculate read cost for each backend
            var costComparisons = new List<(string backendId, decimal totalCost)>();

            foreach (var backendId in availableBackends)
            {
                if (!_costConfigs.TryGetValue(backendId, out var costConfig))
                {
                    continue;
                }

                var readCost = costConfig.CostPerReadOperation;
                var egressCost = CalculateEgressCost(dataSizeBytes, costConfig);

                var totalCost = readCost + egressCost;
                costComparisons.Add((backendId, totalCost));
            }

            if (costComparisons.Count == 0)
            {
                // No cost config, select first
                return new BackendSelectionResult
                {
                    Success = true,
                    SelectedBackend = availableBackends[0],
                    EstimatedCost = 0,
                    Reason = "No cost configuration available"
                };
            }

            var cheapest = costComparisons.OrderBy(c => c.totalCost).First();

            return new BackendSelectionResult
            {
                Success = true,
                SelectedBackend = cheapest.backendId,
                EstimatedCost = cheapest.totalCost,
                Reason = "Lowest egress cost"
            };
        }

        /// <summary>
        /// Records usage for cost tracking.
        /// </summary>
        /// <param name="backendId">Backend ID.</param>
        /// <param name="operation">Operation type.</param>
        /// <param name="dataSizeBytes">Size of data involved.</param>
        public void RecordUsage(string backendId, CostOperationType operation, long dataSizeBytes)
        {
            if (string.IsNullOrWhiteSpace(backendId))
            {
                return;
            }

            var metrics = _usageMetrics.GetOrAdd(backendId, _ => new BackendUsageMetrics
            {
                BackendId = backendId
            });

            switch (operation)
            {
                case CostOperationType.Write:
                    Interlocked.Increment(ref metrics.WriteOperations);
                    Interlocked.Add(ref metrics.BytesStored, dataSizeBytes);
                    break;

                case CostOperationType.Read:
                    Interlocked.Increment(ref metrics.ReadOperations);
                    Interlocked.Add(ref metrics.BytesRead, dataSizeBytes);
                    break;

                case CostOperationType.Delete:
                    Interlocked.Increment(ref metrics.DeleteOperations);
                    Interlocked.Add(ref metrics.BytesDeleted, dataSizeBytes);
                    break;

                case CostOperationType.List:
                    Interlocked.Increment(ref metrics.ListOperations);
                    break;
            }

            // Calculate and record cost
            if (_costConfigs.TryGetValue(backendId, out var config))
            {
                var cost = CalculateOperationCost(operation, dataSizeBytes, config);

                lock (_historyLock)
                {
                    _costHistory.Add(new CostEntry
                    {
                        BackendId = backendId,
                        Timestamp = DateTime.UtcNow,
                        Operation = operation,
                        DataSizeBytes = dataSizeBytes,
                        Cost = cost
                    });
                }
            }
        }

        /// <summary>
        /// Gets the current month-to-date cost for a backend.
        /// </summary>
        /// <param name="backendId">Backend ID.</param>
        /// <returns>Month-to-date cost.</returns>
        public decimal GetMonthToDateCost(string backendId)
        {
            var startOfMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);

            lock (_historyLock)
            {
                return _costHistory
                    .Where(e => e.BackendId == backendId && e.Timestamp >= startOfMonth)
                    .Sum(e => e.Cost);
            }
        }

        /// <summary>
        /// Gets the total month-to-date cost across all backends.
        /// </summary>
        /// <returns>Total month-to-date cost.</returns>
        public decimal GetTotalMonthToDateCost()
        {
            var startOfMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);

            lock (_historyLock)
            {
                return _costHistory
                    .Where(e => e.Timestamp >= startOfMonth)
                    .Sum(e => e.Cost);
            }
        }

        /// <summary>
        /// Gets cost breakdown by backend for the current month.
        /// </summary>
        /// <returns>Dictionary of backend ID to cost.</returns>
        public Dictionary<string, decimal> GetCostBreakdown()
        {
            var startOfMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);

            lock (_historyLock)
            {
                return _costHistory
                    .Where(e => e.Timestamp >= startOfMonth)
                    .GroupBy(e => e.BackendId)
                    .ToDictionary(g => g.Key, g => g.Sum(e => e.Cost));
            }
        }

        /// <summary>
        /// Gets cost projection for the end of month based on current usage.
        /// </summary>
        /// <returns>Projected end-of-month cost.</returns>
        public decimal GetProjectedMonthlyCost()
        {
            var now = DateTime.UtcNow;
            var startOfMonth = new DateTime(now.Year, now.Month, 1);
            var endOfMonth = startOfMonth.AddMonths(1);

            var daysInMonth = (endOfMonth - startOfMonth).TotalDays;
            var daysSoFar = (now - startOfMonth).TotalDays;

            if (daysSoFar < 1)
            {
                return 0;
            }

            var costSoFar = GetTotalMonthToDateCost();
            var dailyAverage = costSoFar / (decimal)daysSoFar;

            return dailyAverage * (decimal)daysInMonth;
        }

        /// <summary>
        /// Gets cost optimization recommendations.
        /// </summary>
        /// <returns>List of recommendations.</returns>
        public List<CostRecommendation> GetOptimizationRecommendations()
        {
            var recommendations = new List<CostRecommendation>();

            // Check budget status
            if (_monthlyBudgetLimit > 0)
            {
                var projected = GetProjectedMonthlyCost();
                if (projected > _monthlyBudgetLimit)
                {
                    recommendations.Add(new CostRecommendation
                    {
                        Type = RecommendationType.BudgetExceeded,
                        Title = "Projected cost exceeds budget",
                        Description = $"Projected monthly cost (${projected:F2}) exceeds budget limit (${_monthlyBudgetLimit:F2})",
                        PotentialSavings = projected - _monthlyBudgetLimit
                    });
                }
            }

            // Analyze backend costs
            var breakdown = GetCostBreakdown();
            var mostExpensive = breakdown.OrderByDescending(kvp => kvp.Value).FirstOrDefault();

            if (mostExpensive.Value > 0)
            {
                recommendations.Add(new CostRecommendation
                {
                    Type = RecommendationType.ExpensiveBackend,
                    Title = $"High cost on backend '{mostExpensive.Key}'",
                    Description = $"Backend '{mostExpensive.Key}' accounts for ${mostExpensive.Value:F2} this month. Consider migrating cold data to cheaper storage.",
                    PotentialSavings = 0 // Would need more analysis
                });
            }

            return recommendations;
        }

        #region Private Methods

        private void InitializeDefaultCostConfigs()
        {
            // Example costs (would be configured per deployment)

            // Local storage - very cheap but limited
            ConfigureBackendCost("filesystem", new BackendCostConfig
            {
                BackendId = "filesystem",
                CostPerGBMonthly = 0.01m,
                CostPerWriteOperation = 0.0001m,
                CostPerReadOperation = 0.0001m,
                CostPerDeleteOperation = 0.0001m,
                CostPerListOperation = 0.0001m,
                CostPerGBEgress = 0m
            });

            // S3 Standard - moderate cost, high performance
            ConfigureBackendCost("s3-standard", new BackendCostConfig
            {
                BackendId = "s3-standard",
                CostPerGBMonthly = 0.023m,
                CostPerWriteOperation = 0.005m / 1000,
                CostPerReadOperation = 0.0004m / 1000,
                CostPerDeleteOperation = 0m,
                CostPerListOperation = 0.005m / 1000,
                CostPerGBEgress = 0.09m
            });

            // S3 Glacier - very cheap storage, expensive retrieval
            ConfigureBackendCost("s3-glacier", new BackendCostConfig
            {
                BackendId = "s3-glacier",
                CostPerGBMonthly = 0.004m,
                CostPerWriteOperation = 0.05m / 1000,
                CostPerReadOperation = 0.10m,
                CostPerDeleteOperation = 0m,
                CostPerListOperation = 0.05m / 1000,
                CostPerGBEgress = 0.09m
            });

            // Azure Blob (Cool tier)
            ConfigureBackendCost("azure-cool", new BackendCostConfig
            {
                BackendId = "azure-cool",
                CostPerGBMonthly = 0.01m,
                CostPerWriteOperation = 0.10m / 10000,
                CostPerReadOperation = 0.01m / 10000,
                CostPerDeleteOperation = 0m,
                CostPerListOperation = 0.10m / 10000,
                CostPerGBEgress = 0.087m
            });
        }

        private void CalculateCurrentCosts()
        {
            if (_disposed)
            {
                return;
            }

            var totalCost = GetTotalMonthToDateCost();

            // Check budget alert
            if (_enableBudgetAlerts && _monthlyBudgetLimit > 0)
            {
                var projected = GetProjectedMonthlyCost();
                if (projected > _monthlyBudgetLimit)
                {
                    Interlocked.Increment(ref _budgetAlertsTriggered);
                    // In real implementation, would trigger alert notification
                }
            }
        }

        private decimal CalculateStorageCost(long dataSizeBytes, BackendCostConfig config)
        {
            var sizeGB = dataSizeBytes / (1024.0m * 1024.0m * 1024.0m);
            return sizeGB * config.CostPerGBMonthly;
        }

        private decimal CalculateEgressCost(long dataSizeBytes, BackendCostConfig config)
        {
            var sizeGB = dataSizeBytes / (1024.0m * 1024.0m * 1024.0m);
            return sizeGB * config.CostPerGBEgress;
        }

        private decimal CalculateOperationCost(
            CostOperationType operation,
            long dataSizeBytes,
            BackendCostConfig config)
        {
            var operationCost = operation switch
            {
                CostOperationType.Write => config.CostPerWriteOperation,
                CostOperationType.Read => config.CostPerReadOperation,
                CostOperationType.Delete => config.CostPerDeleteOperation,
                CostOperationType.List => config.CostPerListOperation,
                _ => 0m
            };

            // Add egress cost for reads
            if (operation == CostOperationType.Read)
            {
                operationCost += CalculateEgressCost(dataSizeBytes, config);
            }

            return operationCost;
        }

        #endregion

        /// <summary>
        /// Disposes resources.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _costCalculationTimer.Dispose();
            _costConfigs.Clear();
            _usageMetrics.Clear();
            lock (_historyLock)
            {
                _costHistory.Clear();
            }
        }
    }

    #region Supporting Types

    /// <summary>
    /// Backend cost configuration.
    /// </summary>
    public sealed class BackendCostConfig
    {
        /// <summary>Backend ID.</summary>
        public string BackendId { get; set; } = string.Empty;

        /// <summary>Cost per GB per month for storage.</summary>
        public decimal CostPerGBMonthly { get; init; }

        /// <summary>Cost per write operation.</summary>
        public decimal CostPerWriteOperation { get; init; }

        /// <summary>Cost per read operation.</summary>
        public decimal CostPerReadOperation { get; init; }

        /// <summary>Cost per delete operation.</summary>
        public decimal CostPerDeleteOperation { get; init; }

        /// <summary>Cost per list operation.</summary>
        public decimal CostPerListOperation { get; init; }

        /// <summary>Cost per GB of data egress (outbound transfer).</summary>
        public decimal CostPerGBEgress { get; init; }
    }

    /// <summary>
    /// Backend usage metrics for cost calculation.
    /// </summary>
    public sealed class BackendUsageMetrics
    {
        /// <summary>Backend ID.</summary>
        public string BackendId { get; init; } = string.Empty;

        /// <summary>Number of write operations.</summary>
        public long WriteOperations;

        /// <summary>Number of read operations.</summary>
        public long ReadOperations;

        /// <summary>Number of delete operations.</summary>
        public long DeleteOperations;

        /// <summary>Number of list operations.</summary>
        public long ListOperations;

        /// <summary>Total bytes stored.</summary>
        public long BytesStored;

        /// <summary>Total bytes read.</summary>
        public long BytesRead;

        /// <summary>Total bytes deleted.</summary>
        public long BytesDeleted;
    }

    /// <summary>
    /// Cost operation type.
    /// </summary>
    public enum CostOperationType
    {
        /// <summary>Write operation.</summary>
        Write,

        /// <summary>Read operation.</summary>
        Read,

        /// <summary>Delete operation.</summary>
        Delete,

        /// <summary>List operation.</summary>
        List
    }

    /// <summary>
    /// Cost entry for history tracking.
    /// </summary>
    public sealed class CostEntry
    {
        /// <summary>Backend ID.</summary>
        public string BackendId { get; init; } = string.Empty;

        /// <summary>Timestamp of operation.</summary>
        public DateTime Timestamp { get; init; }

        /// <summary>Operation type.</summary>
        public CostOperationType Operation { get; init; }

        /// <summary>Size of data involved.</summary>
        public long DataSizeBytes { get; init; }

        /// <summary>Cost of operation.</summary>
        public decimal Cost { get; init; }
    }

    /// <summary>
    /// Backend selection result.
    /// </summary>
    public sealed class BackendSelectionResult
    {
        /// <summary>Whether selection succeeded.</summary>
        public bool Success { get; init; }

        /// <summary>Selected backend ID.</summary>
        public string? SelectedBackend { get; init; }

        /// <summary>Estimated cost for the operation.</summary>
        public decimal EstimatedCost { get; init; }

        /// <summary>Reason for selection.</summary>
        public string? Reason { get; init; }

        /// <summary>Alternative backends with their costs.</summary>
        public List<BackendAlternative> Alternatives { get; init; } = new();

        /// <summary>Error message if selection failed.</summary>
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Alternative backend option.
    /// </summary>
    public sealed class BackendAlternative
    {
        /// <summary>Backend ID.</summary>
        public string BackendId { get; init; } = string.Empty;

        /// <summary>Estimated cost.</summary>
        public decimal EstimatedCost { get; init; }
    }

    /// <summary>
    /// Cost optimization recommendation.
    /// </summary>
    public sealed class CostRecommendation
    {
        /// <summary>Recommendation type.</summary>
        public RecommendationType Type { get; init; }

        /// <summary>Recommendation title.</summary>
        public string Title { get; init; } = string.Empty;

        /// <summary>Detailed description.</summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>Potential monthly savings if implemented.</summary>
        public decimal PotentialSavings { get; init; }
    }

    /// <summary>
    /// Recommendation type.
    /// </summary>
    public enum RecommendationType
    {
        /// <summary>Budget exceeded.</summary>
        BudgetExceeded,

        /// <summary>Expensive backend usage.</summary>
        ExpensiveBackend,

        /// <summary>Inefficient data placement.</summary>
        InefficientPlacement,

        /// <summary>High egress costs.</summary>
        HighEgressCost
    }

    #endregion
}
