using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts
{
    /// <summary>
    /// Root interface for all strategy implementations in the DataWarehouse SDK.
    /// Strategies are workers -- they perform a specific operation within a domain.
    /// Intelligence, capability registration, and knowledge bank access belong at
    /// the plugin level (see AD-05).
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Root strategy interface -- AD-05 flat hierarchy")]
    public interface IStrategy : IDisposable, IAsyncDisposable
    {
        /// <summary>
        /// Gets the unique machine-readable identifier for this strategy.
        /// Used for routing, logging, and capability registration by the parent plugin.
        /// </summary>
        string StrategyId { get; }

        /// <summary>
        /// Gets the human-readable display name of this strategy.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Gets a description of what this strategy does and when to use it.
        /// </summary>
        string Description { get; }

        /// <summary>
        /// Gets the strategy characteristics metadata (performance profile, hardware
        /// requirements, supported features). The parent plugin uses these to register
        /// capabilities and make selection decisions.
        /// </summary>
        IReadOnlyDictionary<string, object> Characteristics { get; }

        /// <summary>
        /// Initializes the strategy, acquiring any resources needed for operation.
        /// Called by the parent plugin during its own initialization phase.
        /// </summary>
        /// <param name="cancellationToken">Token to cancel the initialization.</param>
        /// <returns>A task representing the asynchronous initialization operation.</returns>
        Task InitializeAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Shuts down the strategy, releasing all acquired resources.
        /// Called by the parent plugin during its own shutdown phase.
        /// </summary>
        /// <param name="cancellationToken">Token to cancel the shutdown.</param>
        /// <returns>A task representing the asynchronous shutdown operation.</returns>
        Task ShutdownAsync(CancellationToken cancellationToken = default);
    }
}
