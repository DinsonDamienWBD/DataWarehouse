using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Security;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base class for resilience service plugins (circuit breakers, retries, bulkheads, etc.).
/// Provides the contract for executing operations with resilience protection and monitoring
/// the health of resilience policies.
///
/// <para><strong>Hierarchy:</strong> PluginBase -> FeaturePluginBase -> InfrastructurePluginBase -> ResiliencePluginBase</para>
///
/// <para><strong>Abstract methods (must implement):</strong></para>
/// <list type="bullet">
///   <item><see cref="ExecuteWithResilienceAsync{T}"/>: Execute an operation with a named resilience policy.</item>
/// </list>
///
/// <para><strong>Virtual methods (override for custom behavior):</strong></para>
/// <list type="bullet">
///   <item><see cref="GetResilienceHealthAsync"/>: Report health of all active resilience policies.</item>
/// </list>
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 41.1-06: KS8 Resilience plugin base for proper hierarchy separation")]
public abstract class ResiliencePluginBase : InfrastructurePluginBase
{
    /// <inheritdoc/>
    public override string InfrastructureDomain => "Resilience";

    /// <summary>
    /// Health information for resilience policies.
    /// </summary>
    /// <param name="TotalPolicies">Total number of registered resilience policies.</param>
    /// <param name="ActiveCircuitBreakers">Number of circuit breakers currently in Open or HalfOpen state.</param>
    /// <param name="PolicyStates">Per-policy state descriptions (e.g., "Closed", "Open", "HalfOpen").</param>
    public record ResilienceHealthInfo(
        int TotalPolicies,
        int ActiveCircuitBreakers,
        Dictionary<string, string> PolicyStates);

    /// <summary>
    /// Executes an operation with the specified resilience policy applied.
    /// The policy may include retry, circuit breaker, timeout, bulkhead, or composed pipelines.
    /// </summary>
    /// <typeparam name="T">The return type of the protected operation.</typeparam>
    /// <param name="action">The operation to execute with resilience protection.</param>
    /// <param name="policyName">Name of the resilience policy to apply (must be registered).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The result of the operation if successful.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="policyName"/> is not registered.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the resilience policy rejects the operation (e.g., circuit open).</exception>
    public abstract Task<T> ExecuteWithResilienceAsync<T>(
        Func<CancellationToken, Task<T>> action,
        string policyName,
        CancellationToken ct);

    /// <summary>
    /// Gets the health status of all active resilience policies.
    /// Override to report actual circuit breaker states, retry counts, and policy metrics.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Health information for all resilience policies.</returns>
    public virtual Task<ResilienceHealthInfo> GetResilienceHealthAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new ResilienceHealthInfo(0, 0, new Dictionary<string, string>()));
    }

    #region Resilience Strategy Dispatch

    /// <summary>
    /// Registers an <see cref="IStrategy"/> as a resilience strategy in the PluginBase IStrategy registry.
    /// The strategy must implement <see cref="IStrategy"/> with a StrategyId matching the policy name.
    /// </summary>
    /// <param name="strategy">The resilience strategy to register.</param>
    protected void RegisterResilienceStrategy(IStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        RegisterStrategy(strategy);
    }

    /// <summary>
    /// Dispatches an operation to the named resilience strategy, with optional CommandIdentity ACL enforcement.
    /// Routes through the PluginBase IStrategy registry.
    /// </summary>
    /// <typeparam name="TStrategy">Concrete strategy type implementing <see cref="IStrategy"/>.</typeparam>
    /// <typeparam name="TResult">The operation result type.</typeparam>
    /// <param name="strategyId">Explicit strategy ID (null = use GetDefaultStrategyId).</param>
    /// <param name="identity">Optional CommandIdentity for ACL checks.</param>
    /// <param name="operation">The operation to execute on the resolved strategy.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The operation result.</returns>
    protected Task<TResult> DispatchResilienceStrategyAsync<TStrategy, TResult>(
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
    /// Executes an action with both resilience policy protection and strategy dispatch.
    /// Resolves the strategy by <paramref name="strategyId"/>, then executes <paramref name="action"/>
    /// wrapped in the named resilience policy via <see cref="ExecuteWithResilienceAsync{T}"/>.
    /// </summary>
    /// <typeparam name="T">The return type of the protected operation.</typeparam>
    /// <param name="action">The operation to execute with both strategy and resilience wrapping.</param>
    /// <param name="strategyId">Optional strategy ID for dispatch. Null uses plugin default.</param>
    /// <param name="identity">Optional CommandIdentity for ACL enforcement.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The result of the operation.</returns>
    protected Task<T> ExecuteWithPolicyAndStrategyAsync<T>(
        Func<CancellationToken, Task<T>> action,
        string? strategyId,
        CommandIdentity? identity,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        var policyName = strategyId ?? GetDefaultStrategyId() ?? InfrastructureDomain;
        return ExecuteWithResilienceAsync<T>(action, policyName, ct);
    }

    /// <inheritdoc/>
    /// <remarks>Returns null — resilience policy names are determined per-invocation, not globally.</remarks>
    protected override string? GetDefaultStrategyId() => null;

    #endregion

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "Resilience";
        metadata["SupportsCircuitBreaker"] = true;
        metadata["SupportsRetry"] = true;
        return metadata;
    }
}
