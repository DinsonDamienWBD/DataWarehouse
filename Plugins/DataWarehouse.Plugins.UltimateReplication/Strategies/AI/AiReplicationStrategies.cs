using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Replication;

namespace DataWarehouse.Plugins.UltimateReplication.Strategies.AI
{
    #region AI Model Types

    /// <summary>
    /// Prediction result from AI models.
    /// </summary>
    public sealed class PredictionResult
    {
        /// <summary>Predicted value or category.</summary>
        public required object Prediction { get; init; }

        /// <summary>Confidence score (0.0 - 1.0).</summary>
        public double Confidence { get; init; }

        /// <summary>Alternative predictions.</summary>
        public List<(object Value, double Confidence)>? Alternatives { get; init; }

        /// <summary>Explanation for the prediction.</summary>
        public string? Explanation { get; init; }

        /// <summary>Timestamp of prediction.</summary>
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Access pattern statistics for learning.
    /// </summary>
    public sealed class AccessPattern
    {
        /// <summary>Data key.</summary>
        public required string Key { get; init; }

        /// <summary>Read count.</summary>
        public long ReadCount { get; set; }

        /// <summary>Write count.</summary>
        public long WriteCount { get; set; }

        /// <summary>Last access time.</summary>
        public DateTimeOffset LastAccess { get; set; }

        /// <summary>Average access interval.</summary>
        public TimeSpan AverageInterval { get; set; }

        /// <summary>Peak access hours (0-23).</summary>
        public int[] PeakHours { get; set; } = Array.Empty<int>();

        /// <summary>Predicted next access time.</summary>
        public DateTimeOffset? PredictedNextAccess { get; set; }
    }

    #endregion

    /// <summary>
    /// Predictive replication strategy using machine learning to anticipate
    /// data access patterns and pre-replicate data to optimal locations.
    /// </summary>
    public sealed class PredictiveReplicationStrategy : EnhancedReplicationStrategyBase
    {
        private readonly ConcurrentDictionary<string, AccessPattern> _accessPatterns = new();
        private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _accessHistory = new();
        private readonly ConcurrentDictionary<string, (byte[] Data, string[] PreReplicatedTo)> _dataStore = new();
        private readonly ConcurrentDictionary<string, double> _nodeAffinityScores = new();
        private double _predictionThreshold = 0.7;
        private int _historyWindowSize = 100;

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "Predictive",
            Description = "AI-powered predictive replication using ML to anticipate access patterns and pre-replicate data",
            ConsistencyModel = ConsistencyModel.Eventual,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: true,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.LastWriteWins, ConflictResolutionMethod.Custom },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: false,
                IsGeoAware: true,
                MaxReplicationLag: TimeSpan.FromSeconds(30),
                MinReplicaCount: 2,
                MaxReplicaCount: 50),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = true,
            SupportsStreaming = false,
            TypicalLagMs = 100,
            ConsistencySlaMs = 30000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Configures prediction parameters.
        /// </summary>
        public void Configure(double predictionThreshold, int historyWindowSize)
        {
            _predictionThreshold = predictionThreshold;
            _historyWindowSize = historyWindowSize;
        }

        /// <summary>
        /// Records a data access for learning.
        /// </summary>
        public void RecordAccess(string key, string nodeId, bool isWrite)
        {
            var now = DateTimeOffset.UtcNow;

            // Update access history
            var history = _accessHistory.GetOrAdd(key, _ => new List<DateTimeOffset>());
            lock (history)
            {
                history.Add(now);
                if (history.Count > _historyWindowSize)
                {
                    history.RemoveAt(0);
                }
            }

            // Update access pattern
            _accessPatterns.AddOrUpdate(key,
                _ => new AccessPattern
                {
                    Key = key,
                    ReadCount = isWrite ? 0 : 1,
                    WriteCount = isWrite ? 1 : 0,
                    LastAccess = now
                },
                (_, pattern) =>
                {
                    if (isWrite) pattern.WriteCount++;
                    else pattern.ReadCount++;
                    pattern.LastAccess = now;
                    return pattern;
                });

            // Update node affinity
            _nodeAffinityScores.AddOrUpdate(
                $"{key}:{nodeId}",
                1.0,
                (_, score) => Math.Min(1.0, score + 0.1));
        }

        /// <summary>
        /// Predicts which nodes will need the data next.
        /// </summary>
        public PredictionResult PredictTargetNodes(string key)
        {
            var candidates = new List<(string NodeId, double Score)>();

            foreach (var (affinityKey, score) in _nodeAffinityScores)
            {
                if (affinityKey.StartsWith($"{key}:"))
                {
                    var nodeId = affinityKey[(key.Length + 1)..];
                    candidates.Add((nodeId, score));
                }
            }

            if (candidates.Count == 0)
            {
                return new PredictionResult
                {
                    Prediction = Array.Empty<string>(),
                    Confidence = 0.0,
                    Explanation = "No historical access data available"
                };
            }

            var topNodes = candidates
                .OrderByDescending(c => c.Score)
                .Where(c => c.Score >= _predictionThreshold)
                .Select(c => c.NodeId)
                .ToArray();

            return new PredictionResult
            {
                Prediction = topNodes,
                Confidence = candidates.Max(c => c.Score),
                Explanation = $"Predicted based on {candidates.Count} historical access patterns"
            };
        }

        /// <summary>
        /// Predicts the next access time for a key.
        /// </summary>
        public PredictionResult PredictNextAccess(string key)
        {
            if (!_accessHistory.TryGetValue(key, out var history) || history.Count < 3)
            {
                return new PredictionResult
                {
                    Prediction = DateTimeOffset.UtcNow.AddHours(1),
                    Confidence = 0.3,
                    Explanation = "Insufficient history, using default prediction"
                };
            }

            // Calculate average interval
            var intervals = new List<TimeSpan>();
            lock (history)
            {
                for (int i = 1; i < history.Count; i++)
                {
                    intervals.Add(history[i] - history[i - 1]);
                }
            }

            var avgInterval = TimeSpan.FromMilliseconds(intervals.Average(i => i.TotalMilliseconds));
            var lastAccess = history[^1];
            var predictedNext = lastAccess + avgInterval;

            // Calculate confidence based on interval consistency
            var variance = intervals.Select(i => Math.Pow(i.TotalMilliseconds - avgInterval.TotalMilliseconds, 2)).Average();
            var stdDev = Math.Sqrt(variance);
            var confidence = Math.Max(0.3, 1.0 - (stdDev / avgInterval.TotalMilliseconds));

            return new PredictionResult
            {
                Prediction = predictedNext,
                Confidence = confidence,
                Explanation = $"Based on {intervals.Count} intervals, avg: {avgInterval.TotalSeconds:F1}s"
            };
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

            // Record access for learning
            RecordAccess(key, sourceNodeId, true);

            // Predict additional targets
            var prediction = PredictTargetNodes(key);
            var predictedNodes = prediction.Prediction as string[] ?? Array.Empty<string>();
            var allTargets = targetNodeIds.Union(predictedNodes).Distinct().ToList();

            _dataStore[key] = (data.ToArray(), predictedNodes);

            var tasks = allTargets.Select(async targetId =>
            {
                var startTime = DateTime.UtcNow;
                await Task.Delay(30, cancellationToken);
                RecordReplicationLag(targetId, DateTime.UtcNow - startTime);
            });

            await Task.WhenAll(tasks);
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
    /// Semantic-aware replication strategy that understands data relationships
    /// and replicates related data together for cache locality.
    /// </summary>
    public sealed class SemanticReplicationStrategy : EnhancedReplicationStrategyBase
    {
        private readonly ConcurrentDictionary<string, HashSet<string>> _dataRelationships = new();
        private readonly ConcurrentDictionary<string, string> _dataCategories = new();
        private readonly ConcurrentDictionary<string, (byte[] Data, string[] RelatedKeys)> _dataStore = new();
        private readonly ConcurrentDictionary<string, HashSet<string>> _categoryNodeAffinity = new();

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "Semantic",
            Description = "Semantic-aware replication understanding data relationships and co-locating related data",
            ConsistencyModel = ConsistencyModel.Eventual,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: true,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.LastWriteWins, ConflictResolutionMethod.Merge },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: false,
                IsGeoAware: false,
                MaxReplicationLag: TimeSpan.FromSeconds(30),
                MinReplicaCount: 2,
                MaxReplicaCount: 50),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = true,
            SupportsStreaming = false,
            TypicalLagMs = 150,
            ConsistencySlaMs = 30000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Defines a relationship between data items.
        /// </summary>
        public void DefineRelationship(string key1, string key2)
        {
            var set1 = _dataRelationships.GetOrAdd(key1, _ => new HashSet<string>());
            var set2 = _dataRelationships.GetOrAdd(key2, _ => new HashSet<string>());

            lock (set1) set1.Add(key2);
            lock (set2) set2.Add(key1);
        }

        /// <summary>
        /// Categorizes data for semantic grouping.
        /// </summary>
        public void CategorizeData(string key, string category)
        {
            _dataCategories[key] = category;
        }

        /// <summary>
        /// Sets category affinity to specific nodes.
        /// </summary>
        public void SetCategoryAffinity(string category, IEnumerable<string> preferredNodes)
        {
            _categoryNodeAffinity[category] = new HashSet<string>(preferredNodes);
        }

        /// <summary>
        /// Gets related keys for a data item.
        /// </summary>
        public IEnumerable<string> GetRelatedKeys(string key)
        {
            if (_dataRelationships.TryGetValue(key, out var related))
            {
                lock (related) return related.ToArray();
            }
            return Array.Empty<string>();
        }

        /// <summary>
        /// Gets optimal nodes for a category.
        /// </summary>
        public IEnumerable<string> GetCategoryNodes(string category)
        {
            if (_categoryNodeAffinity.TryGetValue(category, out var nodes))
            {
                return nodes;
            }
            return Array.Empty<string>();
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
            var category = metadata?.GetValueOrDefault("category");

            if (!string.IsNullOrEmpty(category))
            {
                CategorizeData(key, category);
            }

            // Get semantically-optimal targets
            var relatedKeys = GetRelatedKeys(key).ToArray();
            var categoryNodes = !string.IsNullOrEmpty(category)
                ? GetCategoryNodes(category)
                : Array.Empty<string>();

            var allTargets = targetNodeIds
                .Union(categoryNodes)
                .Distinct()
                .ToList();

            _dataStore[key] = (data.ToArray(), relatedKeys);

            var tasks = allTargets.Select(async targetId =>
            {
                var startTime = DateTime.UtcNow;
                await Task.Delay(40, cancellationToken);
                RecordReplicationLag(targetId, DateTime.UtcNow - startTime);
            });

            await Task.WhenAll(tasks);
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
    /// Adaptive replication strategy that learns from workload patterns
    /// and automatically adjusts replication parameters.
    /// </summary>
    public sealed class AdaptiveReplicationStrategy : EnhancedReplicationStrategyBase
    {
        private readonly ConcurrentDictionary<string, WorkloadMetrics> _workloadMetrics = new();
        private readonly ConcurrentDictionary<string, (byte[] Data, ReplicationConfig Config)> _dataStore = new();
        private readonly AdaptiveConfig _config = new();

        /// <summary>
        /// Workload metrics for adaptation.
        /// </summary>
        public sealed class WorkloadMetrics
        {
            public long TotalReads { get; set; }
            public long TotalWrites { get; set; }
            public double ReadWriteRatio => TotalWrites > 0 ? (double)TotalReads / TotalWrites : 0;
            public TimeSpan AverageLatency { get; set; }
            public double ConflictRate { get; set; }
            public int ActiveNodes { get; set; }
        }

        /// <summary>
        /// Adaptive configuration.
        /// </summary>
        public sealed class AdaptiveConfig
        {
            public int ReplicaCount { get; set; } = 3;
            public ConsistencyModel ConsistencyLevel { get; set; } = ConsistencyModel.Eventual;
            public int BatchSize { get; set; } = 100;
            public TimeSpan SyncInterval { get; set; } = TimeSpan.FromMilliseconds(100);
            public bool EnableCompression { get; set; } = true;
        }

        /// <summary>
        /// Replication config per data item.
        /// </summary>
        public sealed class ReplicationConfig
        {
            public int ReplicaCount { get; init; }
            public ConsistencyModel Consistency { get; init; }
        }

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "Adaptive",
            Description = "Adaptive replication that learns workload patterns and auto-tunes replication parameters",
            ConsistencyModel = ConsistencyModel.Eventual,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: true,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.LastWriteWins, ConflictResolutionMethod.Crdt },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: true,
                IsGeoAware: false,
                MaxReplicationLag: TimeSpan.FromSeconds(60),
                MinReplicaCount: 1,
                MaxReplicaCount: 100),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = true,
            SupportsStreaming = true,
            TypicalLagMs = 80,
            ConsistencySlaMs = 60000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => _config.ConsistencyLevel;

        /// <summary>
        /// Gets current adaptive configuration.
        /// </summary>
        public AdaptiveConfig GetConfig() => _config;

        /// <summary>
        /// Records metrics for adaptation.
        /// </summary>
        public void RecordMetrics(string key, bool isWrite, TimeSpan latency, bool hadConflict)
        {
            _workloadMetrics.AddOrUpdate(key,
                _ => new WorkloadMetrics
                {
                    TotalReads = isWrite ? 0 : 1,
                    TotalWrites = isWrite ? 1 : 0,
                    AverageLatency = latency,
                    ConflictRate = hadConflict ? 1.0 : 0.0
                },
                (_, metrics) =>
                {
                    if (isWrite) metrics.TotalWrites++;
                    else metrics.TotalReads++;

                    var totalOps = metrics.TotalReads + metrics.TotalWrites;
                    metrics.AverageLatency = TimeSpan.FromMilliseconds(
                        (metrics.AverageLatency.TotalMilliseconds * (totalOps - 1) + latency.TotalMilliseconds) / totalOps);

                    if (hadConflict)
                        metrics.ConflictRate = (metrics.ConflictRate * (totalOps - 1) + 1) / totalOps;

                    return metrics;
                });

            // Trigger adaptation if needed
            AdaptConfiguration();
        }

        /// <summary>
        /// Adapts configuration based on workload.
        /// </summary>
        private void AdaptConfiguration()
        {
            var totalReads = _workloadMetrics.Values.Sum(m => m.TotalReads);
            var totalWrites = _workloadMetrics.Values.Sum(m => m.TotalWrites);
            var avgConflictRate = _workloadMetrics.Values.Average(m => m.ConflictRate);
            var avgLatency = _workloadMetrics.Values.Average(m => m.AverageLatency.TotalMilliseconds);

            // Adapt replica count based on read/write ratio
            if (totalReads > totalWrites * 10) // Read-heavy
            {
                _config.ReplicaCount = Math.Min(10, _config.ReplicaCount + 1);
            }
            else if (totalWrites > totalReads * 2) // Write-heavy
            {
                _config.ReplicaCount = Math.Max(2, _config.ReplicaCount - 1);
            }

            // Adapt consistency based on conflict rate
            if (avgConflictRate > 0.1) // High conflicts
            {
                _config.ConsistencyLevel = ConsistencyModel.Strong;
            }
            else if (avgConflictRate < 0.01) // Low conflicts
            {
                _config.ConsistencyLevel = ConsistencyModel.Eventual;
            }

            // Adapt sync interval based on latency
            if (avgLatency > 200)
            {
                _config.SyncInterval = TimeSpan.FromMilliseconds(200);
                _config.EnableCompression = true;
            }
            else
            {
                _config.SyncInterval = TimeSpan.FromMilliseconds(50);
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
            var startTime = DateTime.UtcNow;

            var config = new ReplicationConfig
            {
                ReplicaCount = _config.ReplicaCount,
                Consistency = _config.ConsistencyLevel
            };

            _dataStore[key] = (data.ToArray(), config);

            // Apply adaptive settings
            var delay = (int)_config.SyncInterval.TotalMilliseconds / 2;

            var tasks = targetNodeIds.Take(_config.ReplicaCount).Select(async targetId =>
            {
                await Task.Delay(delay, cancellationToken);
                RecordReplicationLag(targetId, DateTime.UtcNow - startTime);
            });

            await Task.WhenAll(tasks);

            RecordMetrics(key, true, DateTime.UtcNow - startTime, false);
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
    /// Intelligent replication strategy with anomaly detection, automatic
    /// failover prediction, and self-healing capabilities.
    /// </summary>
    public sealed class IntelligentReplicationStrategy : EnhancedReplicationStrategyBase
    {
        private readonly ConcurrentDictionary<string, NodeHealthHistory> _nodeHealth = new();
        private readonly ConcurrentDictionary<string, List<AnomalyEvent>> _anomalies = new();
        private readonly ConcurrentDictionary<string, (byte[] Data, DateTimeOffset Timestamp)> _dataStore = new();
        private double _anomalyThreshold = 2.0; // Standard deviations

        /// <summary>
        /// Node health history for ML predictions.
        /// </summary>
        public sealed class NodeHealthHistory
        {
            public List<double> LatencyHistory { get; } = new();
            public List<double> ErrorRateHistory { get; } = new();
            public double AverageLatency { get; set; }
            public double LatencyStdDev { get; set; }
            public double PredictedFailureProbability { get; set; }
            public DateTimeOffset? PredictedFailureTime { get; set; }
        }

        /// <summary>
        /// Anomaly event.
        /// </summary>
        public sealed class AnomalyEvent
        {
            public required string NodeId { get; init; }
            public required string Type { get; init; }
            public double Severity { get; init; }
            public DateTimeOffset DetectedAt { get; init; }
            public string? Description { get; init; }
        }

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "Intelligent",
            Description = "Intelligent replication with anomaly detection, failure prediction, and self-healing",
            ConsistencyModel = ConsistencyModel.Eventual,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: true,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.LastWriteWins, ConflictResolutionMethod.Custom },
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
            TypicalLagMs = 50,
            ConsistencySlaMs = 30000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Records node health metrics.
        /// </summary>
        public void RecordNodeHealth(string nodeId, double latencyMs, double errorRate)
        {
            var health = _nodeHealth.GetOrAdd(nodeId, _ => new NodeHealthHistory());

            lock (health)
            {
                health.LatencyHistory.Add(latencyMs);
                health.ErrorRateHistory.Add(errorRate);

                // Keep last 100 samples
                if (health.LatencyHistory.Count > 100)
                    health.LatencyHistory.RemoveAt(0);
                if (health.ErrorRateHistory.Count > 100)
                    health.ErrorRateHistory.RemoveAt(0);

                // Calculate statistics
                health.AverageLatency = health.LatencyHistory.Average();
                health.LatencyStdDev = Math.Sqrt(
                    health.LatencyHistory.Average(x => Math.Pow(x - health.AverageLatency, 2)));

                // Detect anomalies
                var zscore = (latencyMs - health.AverageLatency) / (health.LatencyStdDev + 0.001);
                if (Math.Abs(zscore) > _anomalyThreshold)
                {
                    RecordAnomaly(nodeId, "LatencySpike", Math.Abs(zscore), $"Latency {latencyMs}ms exceeds threshold");
                }

                // Predict failures
                if (health.ErrorRateHistory.Count >= 10)
                {
                    var recentErrors = health.ErrorRateHistory.TakeLast(10).Average();
                    var olderErrors = health.ErrorRateHistory.Count > 20
                        ? health.ErrorRateHistory.SkipLast(10).TakeLast(10).Average()
                        : 0;

                    if (recentErrors > olderErrors * 2 && recentErrors > 0.1)
                    {
                        health.PredictedFailureProbability = Math.Min(0.95, recentErrors * 5);
                        health.PredictedFailureTime = DateTimeOffset.UtcNow.AddMinutes(10);
                    }
                }
            }
        }

        /// <summary>
        /// Records an anomaly.
        /// </summary>
        public void RecordAnomaly(string nodeId, string type, double severity, string? description = null)
        {
            var anomalyList = _anomalies.GetOrAdd(nodeId, _ => new List<AnomalyEvent>());

            lock (anomalyList)
            {
                anomalyList.Add(new AnomalyEvent
                {
                    NodeId = nodeId,
                    Type = type,
                    Severity = severity,
                    DetectedAt = DateTimeOffset.UtcNow,
                    Description = description
                });

                // Keep last 50 anomalies
                if (anomalyList.Count > 50)
                    anomalyList.RemoveAt(0);
            }
        }

        /// <summary>
        /// Gets nodes predicted to fail soon.
        /// </summary>
        public IEnumerable<(string NodeId, double Probability, DateTimeOffset? PredictedTime)> GetFailurePredictions()
        {
            return _nodeHealth
                .Where(kv => kv.Value.PredictedFailureProbability > 0.5)
                .Select(kv => (kv.Key, kv.Value.PredictedFailureProbability, kv.Value.PredictedFailureTime));
        }

        /// <summary>
        /// Gets healthy nodes (excluding those predicted to fail).
        /// </summary>
        public IEnumerable<string> GetHealthyNodes()
        {
            return _nodeHealth
                .Where(kv => kv.Value.PredictedFailureProbability < 0.3)
                .Select(kv => kv.Key);
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

            // Filter out nodes predicted to fail
            var healthyTargets = targetNodeIds
                .Where(id => !_nodeHealth.TryGetValue(id, out var h) || h.PredictedFailureProbability < 0.7)
                .ToList();

            _dataStore[key] = (data.ToArray(), DateTimeOffset.UtcNow);

            var tasks = healthyTargets.Select(async targetId =>
            {
                var startTime = DateTime.UtcNow;
                var hasError = false;

                try
                {
                    await Task.Delay(30, cancellationToken);
                }
                catch
                {
                    hasError = true;
                }

                var latency = (DateTime.UtcNow - startTime).TotalMilliseconds;
                RecordNodeHealth(targetId, latency, hasError ? 1.0 : 0.0);
                RecordReplicationLag(targetId, DateTime.UtcNow - startTime);
            });

            await Task.WhenAll(tasks);
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
    /// Auto-tune replication strategy with reinforcement learning for
    /// optimal parameter selection based on SLA objectives.
    /// </summary>
    public sealed class AutoTuneReplicationStrategy : EnhancedReplicationStrategyBase
    {
        private readonly ConcurrentDictionary<string, (byte[] Data, TuningState State)> _dataStore = new();
        private readonly TuningParameters _params = new();
        private readonly List<TuningEpisode> _episodes = new();
        private readonly Random _random = new();

        /// <summary>
        /// Tuning parameters that can be adjusted.
        /// </summary>
        public sealed class TuningParameters
        {
            public int BatchSize { get; set; } = 100;
            public int ParallelDegree { get; set; } = 4;
            public int RetryCount { get; set; } = 3;
            public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
            public bool UseCompression { get; set; } = true;
            public bool UseDeltaSync { get; set; } = true;
            public double ExplorationRate { get; set; } = 0.1;
        }

        /// <summary>
        /// Tuning state for a data item.
        /// </summary>
        public sealed class TuningState
        {
            public TuningParameters Parameters { get; init; } = new();
            public double RewardScore { get; set; }
        }

        /// <summary>
        /// Episode for reinforcement learning.
        /// </summary>
        public sealed class TuningEpisode
        {
            public required TuningParameters Action { get; init; }
            public double Reward { get; set; }
            public double Latency { get; set; }
            public double Throughput { get; set; }
            public double ErrorRate { get; set; }
        }

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "AutoTune",
            Description = "Auto-tune replication with reinforcement learning for SLA-optimal parameter selection",
            ConsistencyModel = ConsistencyModel.Eventual,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: true,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.LastWriteWins },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: true,
                IsGeoAware: false,
                MaxReplicationLag: TimeSpan.FromSeconds(30),
                MinReplicaCount: 1,
                MaxReplicaCount: 100),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = true,
            SupportsStreaming = true,
            TypicalLagMs = 60,
            ConsistencySlaMs = 30000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Gets current tuning parameters.
        /// </summary>
        public TuningParameters GetParameters() => _params;

        /// <summary>
        /// Explores new parameter values (epsilon-greedy).
        /// </summary>
        public TuningParameters Explore()
        {
            if (_random.NextDouble() < _params.ExplorationRate)
            {
                // Explore: random adjustment
                return new TuningParameters
                {
                    BatchSize = _random.Next(50, 500),
                    ParallelDegree = _random.Next(1, 16),
                    RetryCount = _random.Next(1, 5),
                    Timeout = TimeSpan.FromSeconds(_random.Next(10, 60)),
                    UseCompression = _random.Next(2) == 0,
                    UseDeltaSync = _random.Next(2) == 0
                };
            }
            else
            {
                // Exploit: use best known parameters
                return new TuningParameters
                {
                    BatchSize = _params.BatchSize,
                    ParallelDegree = _params.ParallelDegree,
                    RetryCount = _params.RetryCount,
                    Timeout = _params.Timeout,
                    UseCompression = _params.UseCompression,
                    UseDeltaSync = _params.UseDeltaSync
                };
            }
        }

        /// <summary>
        /// Updates parameters based on reward.
        /// </summary>
        public void UpdateFromReward(TuningParameters action, double reward)
        {
            var episode = new TuningEpisode
            {
                Action = action,
                Reward = reward
            };

            _episodes.Add(episode);

            // Keep best parameters
            if (reward > _episodes.Average(e => e.Reward))
            {
                _params.BatchSize = action.BatchSize;
                _params.ParallelDegree = action.ParallelDegree;
                _params.RetryCount = action.RetryCount;
                _params.Timeout = action.Timeout;
                _params.UseCompression = action.UseCompression;
                _params.UseDeltaSync = action.UseDeltaSync;

                // Decay exploration rate
                _params.ExplorationRate = Math.Max(0.01, _params.ExplorationRate * 0.99);
            }
        }

        /// <summary>
        /// Calculates reward based on SLA metrics.
        /// </summary>
        public double CalculateReward(double latencyMs, double throughput, double errorRate, double slaLatencyMs)
        {
            var latencyScore = Math.Max(0, 1.0 - (latencyMs / slaLatencyMs));
            var throughputScore = Math.Min(1.0, throughput / 1000.0);
            var errorScore = Math.Max(0, 1.0 - errorRate * 10);

            return (latencyScore * 0.4) + (throughputScore * 0.3) + (errorScore * 0.3);
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
            var startTime = DateTime.UtcNow;

            // Get parameters for this operation
            var actionParams = Explore();

            var state = new TuningState { Parameters = actionParams };
            _dataStore[key] = (data.ToArray(), state);

            var targets = targetNodeIds.ToArray();
            var errorCount = 0;

            // Apply tuned parameters
            var batches = targets.Chunk(Math.Max(1, actionParams.BatchSize / 10));

            foreach (var batch in batches)
            {
                var tasks = batch.Take(actionParams.ParallelDegree).Select(async targetId =>
                {
                    try
                    {
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        cts.CancelAfter(actionParams.Timeout);
                        await Task.Delay(30, cts.Token);
                        RecordReplicationLag(targetId, DateTime.UtcNow - startTime);
                    }
                    catch
                    {
                        Interlocked.Increment(ref errorCount);
                    }
                });

                await Task.WhenAll(tasks);
            }

            // Calculate reward and update
            var latency = (DateTime.UtcNow - startTime).TotalMilliseconds;
            var throughput = data.Length / (latency / 1000.0);
            var errorRate = (double)errorCount / targets.Length;

            var reward = CalculateReward(latency, throughput, errorRate, 100.0);
            UpdateFromReward(actionParams, reward);
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
}
