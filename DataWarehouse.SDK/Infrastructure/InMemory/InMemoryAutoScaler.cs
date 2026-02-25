using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Distributed;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Infrastructure.InMemory
{
    /// <summary>
    /// In-memory single-node implementation of <see cref="IAutoScaler"/> and <see cref="IScalingPolicy"/>.
    /// Always returns NoAction since single-node deployments cannot scale.
    /// Scale-out/in requests return informational results indicating single-node mode (DIST-11).
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: In-memory implementation")]
    public sealed class InMemoryAutoScaler : IAutoScaler, IScalingPolicy
    {
        /// <inheritdoc />
        public event Action<ScalingEvent>? OnScalingEvent;

        /// <inheritdoc />
        public string PolicyName => "SingleNode";

        /// <inheritdoc />
        public Task<ScalingDecision> EvaluateAsync(ScalingContext context, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ScalingDecision
            {
                Action = ScalingAction.NoAction,
                Reason = "Single-node mode: scaling not available",
                TargetNodeCount = 1
            });
        }

        /// <inheritdoc />
        public Task<ScalingResult> ScaleOutAsync(ScaleOutRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            OnScalingEvent?.Invoke(new ScalingEvent { EventType = ScalingEventType.ScaleOutFailed, Action = ScalingAction.NoAction, NodesAffected = 0, Timestamp = DateTimeOffset.UtcNow });
            return Task.FromResult(ScalingResult.Error("Scaling not available in single-node mode. Deploy in cluster mode to enable auto-scaling."));
        }

        /// <inheritdoc />
        public Task<ScalingResult> ScaleInAsync(ScaleInRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            OnScalingEvent?.Invoke(new ScalingEvent { EventType = ScalingEventType.ScaleInFailed, Action = ScalingAction.NoAction, NodesAffected = 0, Timestamp = DateTimeOffset.UtcNow });
            return Task.FromResult(ScalingResult.Error("Scaling not available in single-node mode."));
        }

        /// <inheritdoc />
        public ScalingState GetCurrentState() => new()
        {
            CurrentNodeCount = 1,
            IsScaling = false
        };

        /// <inheritdoc />
        public Task<ScalingDecision> ShouldScaleAsync(ScalingMetrics metrics, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ScalingDecision
            {
                Action = ScalingAction.NoAction,
                Reason = "Single-node mode: scaling not available",
                TargetNodeCount = 1
            });
        }
    }
}
