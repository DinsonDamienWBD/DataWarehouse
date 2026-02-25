using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Distributed;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Infrastructure.Distributed.AutoScaling
{
    /// <summary>
    /// Configuration for the production auto-scaler.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 65: Production auto-scaler")]
    public sealed record AutoScalingConfiguration
    {
        /// <summary>CPU utilization threshold to trigger scale-up (0-100). Default: 80%.</summary>
        public double CpuScaleUpThreshold { get; init; } = 80.0;

        /// <summary>CPU utilization threshold to trigger scale-down (0-100). Default: 30%.</summary>
        public double CpuScaleDownThreshold { get; init; } = 30.0;

        /// <summary>Memory utilization threshold to trigger scale-up (0-100). Default: 85%.</summary>
        public double MemoryScaleUpThreshold { get; init; } = 85.0;

        /// <summary>Memory utilization threshold to trigger scale-down (0-100). Default: 40%.</summary>
        public double MemoryScaleDownThreshold { get; init; } = 40.0;

        /// <summary>Queue depth per node to trigger scale-up. Default: 100.</summary>
        public long QueueDepthScaleUpThreshold { get; init; } = 100;

        /// <summary>Queue depth per node to trigger scale-down. Default: 10.</summary>
        public long QueueDepthScaleDownThreshold { get; init; } = 10;

        /// <summary>Minimum seconds between scale events to prevent thrashing. Default: 60.</summary>
        public int CooldownSeconds { get; init; } = 60;

        /// <summary>Minimum number of nodes in the cluster. Default: 1.</summary>
        public int MinNodes { get; init; } = 1;

        /// <summary>Maximum number of nodes in the cluster. Default: 100.</summary>
        public int MaxNodes { get; init; } = 100;

        /// <summary>Interval in seconds between metric evaluation cycles. Default: 15.</summary>
        public int EvaluationIntervalSeconds { get; init; } = 15;
    }

    /// <summary>
    /// Provider for custom scaling metrics beyond CPU/memory/queue depth.
    /// Implementations can supply application-specific signals (e.g., request latency,
    /// error rate, business throughput) for scaling decisions.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 65: Production auto-scaler")]
    public interface IScalingMetricProvider
    {
        /// <summary>
        /// Gets the current value for a named metric.
        /// </summary>
        /// <param name="name">Metric name (e.g., "request_latency_p99", "error_rate").</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The metric value, or null if the metric is not available.</returns>
        Task<double?> GetMetricAsync(string name, CancellationToken ct = default);
    }

    /// <summary>
    /// Executor for scaling actions. Abstraction over cloud/infrastructure APIs
    /// that actually add or remove nodes from the cluster.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 65: Production auto-scaler")]
    public interface IScalingExecutor
    {
        /// <summary>
        /// Adds nodes to the cluster.
        /// </summary>
        /// <param name="additionalNodes">Number of nodes to add.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Result of the scale-up operation.</returns>
        Task<ScalingResult> ScaleUpAsync(int additionalNodes, CancellationToken ct = default);

        /// <summary>
        /// Removes nodes from the cluster.
        /// </summary>
        /// <param name="removeNodes">Number of nodes to remove.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Result of the scale-down operation.</returns>
        Task<ScalingResult> ScaleDownAsync(int removeNodes, CancellationToken ct = default);
    }

    /// <summary>
    /// Default scaling executor that logs decisions but does not perform real infrastructure changes.
    /// Real implementations would call cloud APIs (AWS ASG, Azure VMSS, K8s HPA).
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 65: Production auto-scaler")]
    public sealed class InMemoryScalingExecutor : IScalingExecutor
    {
        private int _simulatedNodeCount;
        private readonly ConcurrentQueue<ScalingEvent> _history = new();

        /// <summary>
        /// Creates a new in-memory scaling executor with the specified initial node count.
        /// </summary>
        public InMemoryScalingExecutor(int initialNodeCount = 1)
        {
            _simulatedNodeCount = initialNodeCount;
        }

        /// <summary>Gets the simulated current node count.</summary>
        public int SimulatedNodeCount => _simulatedNodeCount;

        /// <summary>Gets the scaling event history.</summary>
        public IReadOnlyCollection<ScalingEvent> History => _history.ToArray();

        /// <inheritdoc />
        public Task<ScalingResult> ScaleUpAsync(int additionalNodes, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Add(ref _simulatedNodeCount, additionalNodes);

            _history.Enqueue(new ScalingEvent
            {
                EventType = ScalingEventType.ScaleOutCompleted,
                Action = ScalingAction.ScaleOut,
                NodesAffected = additionalNodes,
                Timestamp = DateTimeOffset.UtcNow,
                Detail = $"Simulated scale-up: added {additionalNodes} nodes (now {_simulatedNodeCount})"
            });

            return Task.FromResult(ScalingResult.Ok(additionalNodes,
                $"Simulated: added {additionalNodes} nodes (total: {_simulatedNodeCount})"));
        }

        /// <inheritdoc />
        public Task<ScalingResult> ScaleDownAsync(int removeNodes, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var newCount = Math.Max(1, Interlocked.Add(ref _simulatedNodeCount, -removeNodes));
            Volatile.Write(ref _simulatedNodeCount, newCount);

            _history.Enqueue(new ScalingEvent
            {
                EventType = ScalingEventType.ScaleInCompleted,
                Action = ScalingAction.ScaleIn,
                NodesAffected = removeNodes,
                Timestamp = DateTimeOffset.UtcNow,
                Detail = $"Simulated scale-down: removed {removeNodes} nodes (now {newCount})"
            });

            return Task.FromResult(ScalingResult.Ok(removeNodes,
                $"Simulated: removed {removeNodes} nodes (total: {newCount})"));
        }
    }

    /// <summary>
    /// Production-ready auto-scaler implementing <see cref="IAutoScaler"/> with resource-based
    /// scaling decisions. Evaluates CPU, memory, and queue depth metrics against configurable
    /// thresholds. Supports aggressive scale-up (50% capacity increase) and conservative
    /// scale-down (1 node at a time) with cooldown to prevent thrashing.
    ///
    /// Replaces <see cref="InMemory.InMemoryAutoScaler"/> for production multi-node deployments.
    /// InMemoryAutoScaler is preserved for single-node and test scenarios.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 65: Production auto-scaler (Tier 7 blocker resolution)")]
    public sealed class ProductionAutoScaler : IAutoScaler, IDisposable
    {
        private readonly AutoScalingConfiguration _config;
        private readonly IScalingExecutor _executor;
        private readonly IScalingMetricProvider? _metricProvider;
        private readonly CancellationTokenSource _cts = new();

        private int _currentNodeCount;
        private volatile bool _isScaling;
        private ScalingAction? _lastAction;
        private DateTimeOffset? _lastScaledAt;
        private Task? _evaluationTask;

        /// <inheritdoc />
        public event Action<ScalingEvent>? OnScalingEvent;

        /// <summary>
        /// Creates a new production auto-scaler.
        /// </summary>
        /// <param name="executor">Scaling executor for performing scale-up/down operations.</param>
        /// <param name="initialNodeCount">Current number of nodes in the cluster.</param>
        /// <param name="config">Auto-scaling configuration. If null, defaults are used.</param>
        /// <param name="metricProvider">Optional custom metric provider for application-specific signals.</param>
        public ProductionAutoScaler(
            IScalingExecutor executor,
            int initialNodeCount = 1,
            AutoScalingConfiguration? config = null,
            IScalingMetricProvider? metricProvider = null)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _config = config ?? new AutoScalingConfiguration();
            _metricProvider = metricProvider;
            _currentNodeCount = Math.Max(1, initialNodeCount);
        }

        /// <summary>
        /// Starts the background evaluation loop that periodically checks metrics
        /// and triggers scaling decisions.
        /// </summary>
        public void StartEvaluationLoop()
        {
            if (_evaluationTask != null) return;
            _evaluationTask = Task.Run(() => RunEvaluationLoopAsync(_cts.Token), _cts.Token);
        }

        /// <inheritdoc />
        public async Task<ScalingDecision> EvaluateAsync(ScalingContext context, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            // Check cooldown
            if (_lastScaledAt.HasValue)
            {
                var elapsed = (DateTimeOffset.UtcNow - _lastScaledAt.Value).TotalSeconds;
                if (elapsed < _config.CooldownSeconds)
                {
                    var decision = new ScalingDecision
                    {
                        Action = ScalingAction.NoAction,
                        Reason = $"Cooldown active: {_config.CooldownSeconds - (int)elapsed}s remaining",
                        TargetNodeCount = context.CurrentNodeCount
                    };
                    FireEvent(ScalingEventType.Evaluated, ScalingAction.NoAction, 0, decision.Reason);
                    return decision;
                }
            }

            // Evaluate scale-up conditions (any threshold breach triggers scale-up)
            var scaleUpReasons = new List<string>();

            if (context.Metrics.CpuUsage >= _config.CpuScaleUpThreshold)
                scaleUpReasons.Add($"CPU {context.Metrics.CpuUsage:F1}% >= {_config.CpuScaleUpThreshold}%");

            if (context.Metrics.MemoryUsage >= _config.MemoryScaleUpThreshold)
                scaleUpReasons.Add($"Memory {context.Metrics.MemoryUsage:F1}% >= {_config.MemoryScaleUpThreshold}%");

            long queuePerNode = context.CurrentNodeCount > 0
                ? context.Metrics.QueueDepth / context.CurrentNodeCount
                : context.Metrics.QueueDepth;
            if (queuePerNode >= _config.QueueDepthScaleUpThreshold)
                scaleUpReasons.Add($"Queue depth/node {queuePerNode} >= {_config.QueueDepthScaleUpThreshold}");

            if (scaleUpReasons.Count > 0 && context.CurrentNodeCount < context.MaxNodeCount)
            {
                // Scale-up aggressive: add 50% capacity (ceil), min 1 node
                int additionalNodes = Math.Max(1, (int)Math.Ceiling(context.CurrentNodeCount * 0.5));
                int targetCount = Math.Min(context.CurrentNodeCount + additionalNodes, context.MaxNodeCount);

                var decision = new ScalingDecision
                {
                    Action = ScalingAction.ScaleOut,
                    Reason = $"Scale up: {string.Join("; ", scaleUpReasons)}",
                    TargetNodeCount = targetCount
                };
                FireEvent(ScalingEventType.Evaluated, ScalingAction.ScaleOut, targetCount - context.CurrentNodeCount, decision.Reason);
                return decision;
            }

            // Evaluate scale-down conditions (ALL metrics must be below threshold)
            bool cpuLow = context.Metrics.CpuUsage <= _config.CpuScaleDownThreshold;
            bool memLow = context.Metrics.MemoryUsage <= _config.MemoryScaleDownThreshold;
            bool queueLow = queuePerNode <= _config.QueueDepthScaleDownThreshold;

            if (cpuLow && memLow && queueLow && context.CurrentNodeCount > context.MinNodeCount)
            {
                // Scale-down conservative: remove 1 node at a time
                int targetCount = Math.Max(context.MinNodeCount, context.CurrentNodeCount - 1);

                var decision = new ScalingDecision
                {
                    Action = ScalingAction.ScaleIn,
                    Reason = $"Scale down: CPU {context.Metrics.CpuUsage:F1}% <= {_config.CpuScaleDownThreshold}%, " +
                             $"Memory {context.Metrics.MemoryUsage:F1}% <= {_config.MemoryScaleDownThreshold}%, " +
                             $"Queue/node {queuePerNode} <= {_config.QueueDepthScaleDownThreshold}",
                    TargetNodeCount = targetCount
                };
                FireEvent(ScalingEventType.Evaluated, ScalingAction.ScaleIn, 1, decision.Reason);
                return decision;
            }

            // No action needed
            var noAction = new ScalingDecision
            {
                Action = ScalingAction.NoAction,
                Reason = "All metrics within acceptable range",
                TargetNodeCount = context.CurrentNodeCount
            };
            FireEvent(ScalingEventType.Evaluated, ScalingAction.NoAction, 0, noAction.Reason);
            return noAction;
        }

        /// <inheritdoc />
        public async Task<ScalingResult> ScaleOutAsync(ScaleOutRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            if (_isScaling)
                return ScalingResult.Error("A scaling operation is already in progress.");

            int newTotal = _currentNodeCount + request.NodeCount;
            if (newTotal > _config.MaxNodes)
                return ScalingResult.Error($"Scale-out would exceed maximum nodes ({_config.MaxNodes}). Current: {_currentNodeCount}, Requested: +{request.NodeCount}");

            _isScaling = true;
            FireEvent(ScalingEventType.ScaleOutStarted, ScalingAction.ScaleOut, request.NodeCount, request.Reason);

            try
            {
                var result = await _executor.ScaleUpAsync(request.NodeCount, ct).ConfigureAwait(false);

                if (result.Success)
                {
                    Interlocked.Add(ref _currentNodeCount, result.NodesAffected);
                    _lastAction = ScalingAction.ScaleOut;
                    _lastScaledAt = DateTimeOffset.UtcNow;
                    FireEvent(ScalingEventType.ScaleOutCompleted, ScalingAction.ScaleOut, result.NodesAffected,
                        $"Scaled out: +{result.NodesAffected} nodes (total: {_currentNodeCount})");
                }
                else
                {
                    FireEvent(ScalingEventType.ScaleOutFailed, ScalingAction.ScaleOut, 0, result.Message);
                }

                return result;
            }
            finally
            {
                _isScaling = false;
            }
        }

        /// <inheritdoc />
        public async Task<ScalingResult> ScaleInAsync(ScaleInRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            if (_isScaling)
                return ScalingResult.Error("A scaling operation is already in progress.");

            int newTotal = _currentNodeCount - request.NodeCount;
            if (newTotal < _config.MinNodes)
                return ScalingResult.Error($"Scale-in would go below minimum nodes ({_config.MinNodes}). Current: {_currentNodeCount}, Requested: -{request.NodeCount}");

            _isScaling = true;
            FireEvent(ScalingEventType.ScaleInStarted, ScalingAction.ScaleIn, request.NodeCount, request.Reason);

            try
            {
                var result = await _executor.ScaleDownAsync(request.NodeCount, ct).ConfigureAwait(false);

                if (result.Success)
                {
                    var updatedCount = Math.Max(_config.MinNodes, Interlocked.Add(ref _currentNodeCount, -result.NodesAffected));
                    Volatile.Write(ref _currentNodeCount, updatedCount);
                    _lastAction = ScalingAction.ScaleIn;
                    _lastScaledAt = DateTimeOffset.UtcNow;
                    FireEvent(ScalingEventType.ScaleInCompleted, ScalingAction.ScaleIn, result.NodesAffected,
                        $"Scaled in: -{result.NodesAffected} nodes (total: {_currentNodeCount})");
                }
                else
                {
                    FireEvent(ScalingEventType.ScaleInFailed, ScalingAction.ScaleIn, 0, result.Message);
                }

                return result;
            }
            finally
            {
                _isScaling = false;
            }
        }

        /// <inheritdoc />
        public ScalingState GetCurrentState() => new()
        {
            CurrentNodeCount = _currentNodeCount,
            IsScaling = _isScaling,
            LastAction = _lastAction,
            LastScaledAt = _lastScaledAt
        };

        /// <summary>
        /// Collects current resource metrics from the local process.
        /// Uses System.Diagnostics.Process for CPU and GC.GetGCMemoryInfo() for managed memory.
        /// </summary>
        internal ScalingMetrics CollectLocalMetrics(long queueDepth = 0, long activeConnections = 0)
        {
            double cpuUsage = 0;
            try
            {
                using var process = Process.GetCurrentProcess();
                var cpuTime = process.TotalProcessorTime;
                var wallTime = DateTimeOffset.UtcNow - process.StartTime;
                if (wallTime.TotalMilliseconds > 0)
                {
                    cpuUsage = (cpuTime.TotalMilliseconds / (wallTime.TotalMilliseconds * Environment.ProcessorCount)) * 100.0;
                    cpuUsage = Math.Min(100.0, Math.Max(0.0, cpuUsage));
                }
            }
            catch
            {
                // CPU metrics may not be available on all platforms
            }

            double memoryUsage = 0;
            try
            {
                var gcInfo = GC.GetGCMemoryInfo();
                long totalAvailable = gcInfo.TotalAvailableMemoryBytes;
                long heapSize = gcInfo.HeapSizeBytes;
                if (totalAvailable > 0)
                {
                    memoryUsage = ((double)heapSize / totalAvailable) * 100.0;
                    memoryUsage = Math.Min(100.0, Math.Max(0.0, memoryUsage));
                }
            }
            catch
            {
                // Memory metrics may not be available on all platforms
            }

            return new ScalingMetrics
            {
                CpuUsage = cpuUsage,
                MemoryUsage = memoryUsage,
                StorageUsage = 0, // Storage monitoring requires platform-specific APIs
                ActiveConnections = activeConnections,
                QueueDepth = queueDepth
            };
        }

        private async Task RunEvaluationLoopAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_config.EvaluationIntervalSeconds));

            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var metrics = CollectLocalMetrics();
                    var context = new ScalingContext
                    {
                        Metrics = metrics,
                        CurrentNodeCount = _currentNodeCount,
                        MinNodeCount = _config.MinNodes,
                        MaxNodeCount = _config.MaxNodes
                    };

                    var decision = await EvaluateAsync(context, ct).ConfigureAwait(false);

                    // Auto-execute scaling decisions
                    switch (decision.Action)
                    {
                        case ScalingAction.ScaleOut:
                            int nodesToAdd = decision.TargetNodeCount - _currentNodeCount;
                            if (nodesToAdd > 0)
                            {
                                await ScaleOutAsync(new ScaleOutRequest
                                {
                                    NodeCount = nodesToAdd,
                                    Reason = decision.Reason
                                }, ct).ConfigureAwait(false);
                            }
                            break;

                        case ScalingAction.ScaleIn:
                            int nodesToRemove = _currentNodeCount - decision.TargetNodeCount;
                            if (nodesToRemove > 0)
                            {
                                await ScaleInAsync(new ScaleInRequest
                                {
                                    NodeCount = nodesToRemove,
                                    Reason = decision.Reason
                                }, ct).ConfigureAwait(false);
                            }
                            break;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // Evaluation loop failure should not crash the scaler
                }
            }
        }

        private void FireEvent(ScalingEventType eventType, ScalingAction action, int nodesAffected, string? detail)
        {
            OnScalingEvent?.Invoke(new ScalingEvent
            {
                EventType = eventType,
                Action = action,
                NodesAffected = nodesAffected,
                Timestamp = DateTimeOffset.UtcNow,
                Detail = detail
            });
        }

        /// <summary>
        /// Disposes the auto-scaler, stopping the background evaluation loop.
        /// </summary>
        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
