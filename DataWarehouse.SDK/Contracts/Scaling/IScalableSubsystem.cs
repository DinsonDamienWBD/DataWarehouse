using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Contracts.Scaling
{
    /// <summary>
    /// Unifying interface for subsystems that support dynamic scaling.
    /// Combines bounded caching, persistent backing, scaling policy management,
    /// and backpressure awareness into a single contract that all 60+ plugins
    /// implement to replace unbounded in-memory state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Plugins implementing this interface expose their internal caches and queues
    /// to the scaling infrastructure, enabling:
    /// <list type="bullet">
    /// <item><description>Runtime limit reconfiguration without restart</description></item>
    /// <item><description>Centralized scaling metrics collection</description></item>
    /// <item><description>Coordinated backpressure propagation across subsystems</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    [SdkCompatibility("6.0.0", Notes = "Phase 88: Unified scaling contract for all plugins")]
    public interface IScalableSubsystem
    {
        /// <summary>
        /// Returns current scaling metrics including cache sizes, hit rates,
        /// memory usage, and backpressure state.
        /// </summary>
        /// <returns>
        /// A read-only dictionary of metric name to value. Standard keys include:
        /// <c>cache.size</c>, <c>cache.hitRate</c>, <c>cache.memoryBytes</c>,
        /// <c>backpressure.state</c>, <c>backpressure.queueDepth</c>.
        /// </returns>
        IReadOnlyDictionary<string, object> GetScalingMetrics();

        /// <summary>
        /// Reconfigures the scaling limits for this subsystem at runtime.
        /// Existing entries that exceed the new limits may be evicted asynchronously.
        /// </summary>
        /// <param name="limits">The new scaling limits to apply.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A task that completes when the new limits have been applied.</returns>
        Task ReconfigureLimitsAsync(ScalingLimits limits, CancellationToken ct = default);

        /// <summary>
        /// Gets the current effective scaling limits for this subsystem.
        /// </summary>
        ScalingLimits CurrentLimits { get; }

        /// <summary>
        /// Gets the current backpressure state of this subsystem.
        /// </summary>
        BackpressureState CurrentBackpressureState { get; }
    }
}
