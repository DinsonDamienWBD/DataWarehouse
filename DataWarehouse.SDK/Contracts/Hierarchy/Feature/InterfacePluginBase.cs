using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Interface;
using DataWarehouse.SDK.Security;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base for external interface plugins (REST, gRPC, SQL, WebSocket).
/// Provides typed strategy dispatch for <see cref="IInterfaceStrategy"/> implementations,
/// with domain operations for protocol-agnostic request handling.
/// </summary>
public abstract class InterfacePluginBase : FeaturePluginBase
{
    /// <inheritdoc/>
    public override string FeatureCategory => "Interface";

    /// <summary>Protocol this interface serves (e.g., "REST", "gRPC", "SQL", "WebSocket").</summary>
    public abstract string Protocol { get; }

    /// <summary>Port number, if applicable.</summary>
    public virtual int? Port => null;

    /// <summary>Base path for the interface (e.g., "/api/v1").</summary>
    public virtual string? BasePath => null;

    /// <summary>AI hook: Optimize response for a query.</summary>
    protected virtual Task<Dictionary<string, object>> OptimizeResponseAsync(Dictionary<string, object> query, CancellationToken ct = default)
        => Task.FromResult(new Dictionary<string, object>());

    #region Typed Interface Strategy Registry

    /// <summary>
    /// Lazy-initialized typed registry for <see cref="IInterfaceStrategy"/> instances.
    /// IInterfaceStrategy does not inherit IStrategy, so it uses its own dedicated registry
    /// rather than the PluginBase IStrategy registry.
    /// The key selector uses <see cref="IInterfaceStrategy.Protocol"/>.ToString() as the unique key.
    /// </summary>
    private StrategyRegistry<IInterfaceStrategy>? _interfaceStrategyRegistry;

    /// <summary>Lock protecting lazy initialization of <see cref="_interfaceStrategyRegistry"/>.</summary>
    private readonly object _interfaceRegistryLock = new();

    /// <summary>
    /// Gets the typed interface strategy registry. Lazily initialized on first access.
    /// </summary>
    protected StrategyRegistry<IInterfaceStrategy> InterfaceStrategyRegistry
    {
        get
        {
            if (_interfaceStrategyRegistry is not null) return _interfaceStrategyRegistry;
            lock (_interfaceRegistryLock)
            {
                _interfaceStrategyRegistry ??= new StrategyRegistry<IInterfaceStrategy>(
                    s => s.Protocol.ToString());
            }
            return _interfaceStrategyRegistry;
        }
    }

    /// <summary>
    /// Registers an interface strategy with the typed registry.
    /// The strategy is keyed by its <see cref="IInterfaceStrategy.Protocol"/>.ToString() value.
    /// </summary>
    /// <param name="strategy">The interface strategy to register.</param>
    protected void RegisterInterfaceStrategy(IInterfaceStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        InterfaceStrategyRegistry.Register(strategy);
    }

    /// <summary>
    /// Dispatches an operation to the named interface strategy, with optional CommandIdentity ACL enforcement.
    /// Routes through the typed <see cref="InterfaceStrategyRegistry"/>.
    /// When no explicit strategy is specified, falls back to <see cref="Protocol"/> as the strategy ID.
    /// </summary>
    /// <typeparam name="TResult">The operation result type.</typeparam>
    /// <param name="explicitStrategyId">Explicit strategy ID matching protocol name (null = use Protocol).</param>
    /// <param name="identity">Optional CommandIdentity for ACL checks. When null, ACL is skipped.</param>
    /// <param name="operation">The operation to execute on the resolved interface strategy.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The operation result.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no strategy is found for the given ID.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the identity is denied by the ACL provider.</exception>
    protected async Task<TResult> DispatchInterfaceStrategyAsync<TResult>(
        string? explicitStrategyId,
        CommandIdentity? identity,
        Func<IInterfaceStrategy, Task<TResult>> operation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var strategyId = !string.IsNullOrEmpty(explicitStrategyId)
            ? explicitStrategyId
            : (GetDefaultStrategyId() ?? Protocol);

        // ACL check if provider is configured
        if (identity != null && StrategyAclProvider != null)
        {
            if (!StrategyAclProvider.IsStrategyAllowed(strategyId, identity))
                throw new UnauthorizedAccessException(
                    $"Principal '{identity.EffectivePrincipalId}' is not authorized to use interface strategy '{strategyId}'.");
        }

        var strategy = InterfaceStrategyRegistry.Get(strategyId)
            ?? throw new InvalidOperationException(
                $"Interface strategy '{strategyId}' is not registered. " +
                $"Call RegisterInterfaceStrategy before dispatching.");

        return await operation(strategy).ConfigureAwait(false);
    }

    #endregion

    #region Domain Operations (Strategy-Dispatched)

    /// <summary>
    /// Handles an interface request using the specified or default interface strategy.
    /// Resolves the strategy from the typed <see cref="InterfaceStrategyRegistry"/> with optional ACL enforcement.
    /// The request dictionary is mapped to an <see cref="InterfaceRequest"/> for protocol-agnostic dispatch.
    /// </summary>
    /// <param name="request">Request parameters (method, path, headers, body, query).</param>
    /// <param name="strategyId">Optional strategy ID matching a protocol name. Null uses <see cref="Protocol"/>.</param>
    /// <param name="identity">Optional identity for ACL enforcement.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Response dictionary from the interface strategy.</returns>
    protected async Task<Dictionary<string, object>> HandleRequestWithStrategyAsync(
        Dictionary<string, object> request,
        string? strategyId = null,
        CommandIdentity? identity = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var interfaceRequest = BuildInterfaceRequest(request);

        var response = await DispatchInterfaceStrategyAsync<InterfaceResponse>(
            strategyId,
            identity,
            strategy => strategy.HandleRequestAsync(interfaceRequest, ct),
            ct).ConfigureAwait(false);

        return new Dictionary<string, object>
        {
            ["statusCode"] = response.StatusCode,
            ["isSuccess"] = response.IsSuccess,
            ["body"] = response.Body.ToArray(),
            ["headers"] = response.Headers
        };
    }

    /// <summary>
    /// Builds an <see cref="InterfaceRequest"/> from a generic request dictionary.
    /// Override to provide custom request mapping for protocol-specific needs.
    /// </summary>
    /// <param name="request">Raw request parameters.</param>
    /// <returns>A mapped <see cref="InterfaceRequest"/>.</returns>
    protected virtual InterfaceRequest BuildInterfaceRequest(Dictionary<string, object> request)
    {
        var method = request.TryGetValue("method", out var m) && m is Interface.HttpMethod hm
            ? hm
            : Interface.HttpMethod.GET;
        var path = request.TryGetValue("path", out var p) && p is string ps ? ps : "/";
        return new InterfaceRequest(
            Method: method,
            Path: path,
            Headers: new Dictionary<string, string>(),
            Body: ReadOnlyMemory<byte>.Empty,
            QueryParameters: new Dictionary<string, string>(),
            Protocol: InterfaceProtocol.Unknown);
    }

    /// <inheritdoc/>
    /// <remarks>Returns <see cref="Protocol"/> to route interface dispatches to the correct strategy.</remarks>
    protected override string? GetDefaultStrategyId() => Protocol;

    #endregion

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["Protocol"] = Protocol;
        if (Port.HasValue) metadata["Port"] = Port.Value;
        if (BasePath != null) metadata["BasePath"] = BasePath;
        return metadata;
    }
}
