using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Transit
{
    /// <summary>
    /// Orchestrator interface for data transit operations.
    /// Provides strategy selection based on request characteristics and endpoint protocol,
    /// transfer execution with automatic strategy routing, and fleet health monitoring.
    /// </summary>
    /// <remarks>
    /// The orchestrator is the primary entry point for data transfer operations.
    /// It maintains a registry of available strategies, selects the optimal one for each
    /// transfer request, and manages the transfer lifecycle.
    /// </remarks>
    public interface ITransitOrchestrator
    {
        /// <summary>
        /// Selects the best transit strategy for the given request based on endpoint protocol,
        /// data size, required capabilities, and strategy availability.
        /// </summary>
        /// <param name="request">The transfer request to find a strategy for.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The selected strategy.</returns>
        /// <exception cref="InvalidOperationException">Thrown when no suitable strategy is found.</exception>
        Task<IDataTransitStrategy> SelectStrategyAsync(TransitRequest request, CancellationToken ct = default);

        /// <summary>
        /// Executes a data transfer using the best available strategy.
        /// The orchestrator handles strategy selection, transfer tracking, and event publishing.
        /// </summary>
        /// <param name="request">The transfer request.</param>
        /// <param name="progress">Optional progress reporter.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The result of the transfer operation.</returns>
        Task<TransitResult> TransferAsync(TransitRequest request, IProgress<TransitProgress>? progress = null, CancellationToken ct = default);

        /// <summary>
        /// Gets all strategies currently registered with the orchestrator.
        /// </summary>
        /// <returns>A read-only collection of registered strategies.</returns>
        IReadOnlyCollection<IDataTransitStrategy> GetRegisteredStrategies();

        /// <summary>
        /// Gets the health status of all registered strategies.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Health status for each registered strategy.</returns>
        Task<IReadOnlyCollection<TransitHealthStatus>> GetHealthAsync(CancellationToken ct = default);
    }
}
