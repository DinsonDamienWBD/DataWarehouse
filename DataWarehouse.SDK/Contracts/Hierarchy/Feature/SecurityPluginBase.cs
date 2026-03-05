using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Security;
using DataWarehouse.SDK.Security;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base for security service plugins (access control, key management, compliance, threat detection).
/// </summary>
public abstract class SecurityPluginBase : FeaturePluginBase
{
    /// <inheritdoc/>
    public override string FeatureCategory => "Security";

    /// <summary>Security sub-domain (e.g., "AccessControl", "KeyManagement", "Compliance").</summary>
    public abstract string SecurityDomain { get; }

    /// <summary>
    /// Optional key store reference for security plugins that need key access.
    /// Set via <see cref="SetKeyStore"/>.
    /// </summary>
    protected IKeyStore? KeyStore { get; private set; }

    /// <summary>
    /// Configures the key store for this security plugin.
    /// </summary>
    /// <param name="keyStore">The key store instance.</param>
    public virtual void SetKeyStore(IKeyStore keyStore)
    {
        KeyStore = keyStore ?? throw new System.ArgumentNullException(nameof(keyStore));
    }

    /// <summary>
    /// Retrieves a key as a <see cref="NativeKeyHandle"/> for secure zero-copy operations.
    /// Delegates to <see cref="IKeyStore.GetKeyNativeAsync"/> on the configured key store.
    /// The caller MUST dispose the returned handle to trigger secure wipe.
    /// </summary>
    /// <param name="keyId">The key identifier.</param>
    /// <param name="context">Security context for ACL validation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="NativeKeyHandle"/> containing the key material in unmanaged memory.</returns>
    /// <exception cref="System.InvalidOperationException">Thrown if no key store is configured.</exception>
    protected virtual Task<NativeKeyHandle> GetKeyNativeAsync(string keyId, ISecurityContext context, CancellationToken ct = default)
    {
        var keyStore = KeyStore
            ?? throw new System.InvalidOperationException("No key store configured. Call SetKeyStore first.");
        return keyStore.GetKeyNativeAsync(keyId, context, ct);
    }

    /// <summary>AI hook: Evaluate access with intelligence. Defaults to deny (fail-closed).</summary>
    protected virtual Task<Dictionary<string, object>> EvaluateAccessWithIntelligenceAsync(Dictionary<string, object> request, CancellationToken ct = default)
        => Task.FromResult(new Dictionary<string, object> { ["allowed"] = false, ["reason"] = "No intelligence provider configured" });

    /// <summary>AI hook: Detect anomalous access patterns. Defaults to flagging as anomalous (fail-closed).</summary>
    protected virtual Task<bool> DetectAnomalousAccessAsync(Dictionary<string, object> context, CancellationToken ct = default)
        => Task.FromResult(true);

    #region Typed Security Strategy Registry

    /// <summary>
    /// Lazy-initialized typed registry for <see cref="ISecurityStrategy"/> instances.
    /// </summary>
    private StrategyRegistry<ISecurityStrategy>? _securityStrategyRegistry;

    /// <summary>Lock protecting lazy initialization of <see cref="_securityStrategyRegistry"/>.</summary>
    private readonly object _securityRegistryLock = new();

    /// <summary>
    /// Gets the typed security strategy registry. Lazily initialized on first access.
    /// </summary>
    protected StrategyRegistry<ISecurityStrategy> SecurityStrategyRegistry
    {
        get
        {
            if (_securityStrategyRegistry is not null) return _securityStrategyRegistry;
            lock (_securityRegistryLock)
            {
                _securityStrategyRegistry ??= new StrategyRegistry<ISecurityStrategy>(s => s.StrategyId);
            }
            return _securityStrategyRegistry;
        }
    }

    /// <summary>
    /// Registers a security strategy with the typed registry.
    /// </summary>
    /// <param name="strategy">The security strategy to register.</param>
    protected void RegisterSecurityStrategy(ISecurityStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        SecurityStrategyRegistry.Register(strategy);
    }

    /// <summary>
    /// Dispatches an operation to the specified or default security strategy,
    /// with optional CommandIdentity ACL enforcement.
    /// </summary>
    /// <typeparam name="TResult">The operation result type.</typeparam>
    /// <param name="explicitStrategyId">Explicit strategy ID (null = use GetDefaultStrategyId).</param>
    /// <param name="identity">Optional CommandIdentity for ACL checks. When null, ACL is skipped.</param>
    /// <param name="dataContext">Context about the request for strategy selection.</param>
    /// <param name="operation">The operation to execute on the resolved security strategy.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The operation result.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no strategy is found for the given ID.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the identity is denied by the ACL provider.</exception>
    protected async Task<TResult> DispatchSecurityStrategyAsync<TResult>(
        string? explicitStrategyId,
        CommandIdentity? identity,
        Dictionary<string, object>? dataContext,
        Func<ISecurityStrategy, Task<TResult>> operation,
        CancellationToken ct = default)
    {
        var strategyId = !string.IsNullOrEmpty(explicitStrategyId)
            ? explicitStrategyId
            : GetDefaultStrategyId()
                ?? throw new InvalidOperationException(
                    "No strategy specified and no default configured. " +
                    "Call RegisterSecurityStrategy and specify a strategyId, or configure a default.");

        // ACL check if provider is configured
        if (identity != null && StrategyAclProvider != null)
        {
            if (!StrategyAclProvider.IsStrategyAllowed(strategyId, identity))
                throw new UnauthorizedAccessException(
                    $"Principal '{identity.EffectivePrincipalId}' is not authorized to use security strategy '{strategyId}'.");
        }

        var strategy = SecurityStrategyRegistry.Get(strategyId)
            ?? throw new InvalidOperationException(
                $"Security strategy '{strategyId}' is not registered. " +
                $"Call RegisterSecurityStrategy before dispatching.");

        return await operation(strategy).ConfigureAwait(false);
    }

    #endregion

    #region Domain Operations (Strategy-Dispatched)

    /// <summary>
    /// Authenticates credentials using the specified or default security strategy.
    /// Resolves the strategy from the typed <see cref="SecurityStrategyRegistry"/> with
    /// optional CommandIdentity ACL enforcement.
    /// </summary>
    /// <param name="credentials">The credentials to authenticate.</param>
    /// <param name="strategyId">Optional strategy ID. When null, default strategy applies.</param>
    /// <param name="identity">Optional identity for ACL enforcement.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A security decision indicating whether authentication succeeded.</returns>
    protected virtual async Task<SecurityDecision> AuthenticateWithStrategyAsync(
        Dictionary<string, object> credentials,
        string? strategyId = null,
        CommandIdentity? identity = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        return await DispatchSecurityStrategyAsync<SecurityDecision>(
            strategyId,
            identity,
            credentials,
            async strategy =>
            {
                var userId = credentials.TryGetValue("userId", out var uid) ? uid?.ToString() ?? "unknown" : "unknown";
                var resource = credentials.TryGetValue("resource", out var res) ? res?.ToString() ?? "*" : "*";
                var context = new SecurityContext
                {
                    UserId = userId,
                    Resource = resource,
                    Operation = "authenticate"
                };
                return await strategy.EvaluateAsync(context, ct).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Authorizes access to a resource using the specified or default security strategy.
    /// Resolves the strategy from the typed <see cref="SecurityStrategyRegistry"/> with
    /// optional CommandIdentity ACL enforcement.
    /// </summary>
    /// <param name="resource">The resource being accessed.</param>
    /// <param name="action">The action being performed on the resource.</param>
    /// <param name="strategyId">Optional strategy ID. When null, default strategy applies.</param>
    /// <param name="identity">Optional identity for ACL enforcement.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A security decision indicating whether authorization is granted.</returns>
    protected virtual async Task<SecurityDecision> AuthorizeWithStrategyAsync(
        string resource,
        string action,
        string? strategyId = null,
        CommandIdentity? identity = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        return await DispatchSecurityStrategyAsync<SecurityDecision>(
            strategyId,
            identity,
            new Dictionary<string, object> { ["resource"] = resource, ["action"] = action },
            async strategy =>
            {
                var userId = identity?.EffectivePrincipalId ?? "anonymous";
                var context = new SecurityContext
                {
                    UserId = userId,
                    Resource = resource,
                    Operation = action
                };
                return await strategy.EvaluateAsync(context, ct).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    protected override string? GetDefaultStrategyId() => null;

    #endregion

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["SecurityDomain"] = SecurityDomain;
        return metadata;
    }
}
