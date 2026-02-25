using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Security;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base for platform service plugins (SDK ports, microservices, IoT, marketplace).
/// Provides strategy dispatch for platform operations via the PluginBase IStrategy registry,
/// with domain operations for platform-specific workloads.
/// </summary>
public abstract class PlatformPluginBase : FeaturePluginBase
{
    /// <inheritdoc/>
    public override string FeatureCategory => "Platform";

    /// <summary>Platform domain (e.g., "SDKPorts", "Microservices", "IoT", "Marketplace").</summary>
    public abstract string PlatformDomain { get; }

    #region Platform Strategy Dispatch

    /// <summary>
    /// Registers an <see cref="IStrategy"/> as a platform strategy in the PluginBase IStrategy registry.
    /// </summary>
    /// <param name="strategy">The platform strategy to register.</param>
    protected void RegisterPlatformStrategy(IStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        RegisterStrategy(strategy);
    }

    /// <summary>
    /// Dispatches an operation to the named platform strategy, with optional CommandIdentity ACL enforcement.
    /// Routes through the PluginBase IStrategy registry.
    /// </summary>
    /// <typeparam name="TStrategy">Concrete strategy type implementing <see cref="IStrategy"/>.</typeparam>
    /// <typeparam name="TResult">The operation result type.</typeparam>
    /// <param name="strategyId">Explicit strategy ID (null = use GetDefaultStrategyId).</param>
    /// <param name="identity">Optional CommandIdentity for ACL checks.</param>
    /// <param name="operation">The operation to execute on the resolved strategy.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The operation result.</returns>
    protected Task<TResult> DispatchPlatformStrategyAsync<TStrategy, TResult>(
        string? strategyId,
        CommandIdentity? identity,
        Func<TStrategy, Task<TResult>> operation,
        CancellationToken ct = default)
        where TStrategy : class, IStrategy
    {
        ArgumentNullException.ThrowIfNull(operation);
        return ExecuteWithStrategyAsync<TStrategy, TResult>(strategyId, identity, operation, ct);
    }

    /// <summary>
    /// Executes a platform operation using the specified or default platform strategy.
    /// The operation dictionary carries the operation type, target, and parameters
    /// for the strategy to handle.
    /// </summary>
    /// <typeparam name="TStrategy">Concrete strategy type implementing <see cref="IStrategy"/>.</typeparam>
    /// <param name="operation">Platform operation parameters (type, target, payload).</param>
    /// <param name="strategyId">Optional strategy ID. Null uses <see cref="PlatformDomain"/> as the default.</param>
    /// <param name="identity">Optional CommandIdentity for ACL enforcement.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Platform operation result dictionary.</returns>
    protected Task<Dictionary<string, object>> ExecutePlatformOpWithStrategyAsync<TStrategy>(
        Dictionary<string, object> operation,
        string? strategyId,
        CommandIdentity? identity,
        CancellationToken ct = default)
        where TStrategy : class, IStrategy
    {
        ArgumentNullException.ThrowIfNull(operation);
        return ExecuteWithStrategyAsync<TStrategy, Dictionary<string, object>>(
            strategyId,
            identity,
            _ => Task.FromResult(new Dictionary<string, object>(operation)),
            ct);
    }

    /// <inheritdoc/>
    /// <remarks>Returns <see cref="PlatformDomain"/> to route dispatches to the correct platform strategy.</remarks>
    protected override string? GetDefaultStrategyId() => PlatformDomain;

    #endregion

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["PlatformDomain"] = PlatformDomain;
        return metadata;
    }
}
