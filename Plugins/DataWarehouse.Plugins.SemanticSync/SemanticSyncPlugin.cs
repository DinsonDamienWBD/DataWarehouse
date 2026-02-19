// <copyright file="SemanticSyncPlugin.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System.Collections.Concurrent;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.SemanticSync;

/// <summary>
/// Semantic Sync Protocol Plugin - Comprehensive semantic-aware synchronization capabilities.
/// Implements semantic conflict classification, summary-vs-raw routing, conflict resolution,
/// bandwidth-aware fidelity control, and edge inference integration for distributed data sync.
/// </summary>
/// <remarks>
/// <para>
/// This plugin hosts all semantic sync strategies added by subsequent plans:
/// </para>
/// <list type="bullet">
///   <item>Plan 03: Semantic classification strategies</item>
///   <item>Plan 04: Summary-vs-raw routing strategies</item>
///   <item>Plan 05: Semantic conflict resolution strategies</item>
///   <item>Plan 06: Bandwidth-aware fidelity control strategies</item>
///   <item>Plan 07: Edge inference integration strategies</item>
/// </list>
/// <para>
/// Follows the OrchestrationPluginBase pattern established by UltimateEdgeComputing.
/// All strategies are registered via the <see cref="RegisterStrategy"/> method and
/// retrieved via <see cref="GetStrategy{T}"/>.
/// </para>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic conflict resolution")]
public sealed class SemanticSyncPlugin : OrchestrationPluginBase
{
    private readonly ConcurrentDictionary<string, StrategyBase> _strategies = new();
    private readonly List<IDisposable> _subscriptions = new();
    private bool _initialized;

    /// <summary>Gets the plugin identifier.</summary>
    public override string Id => "semantic-sync-protocol";

    /// <summary>Gets the plugin name.</summary>
    public override string Name => "Semantic Sync Protocol";

    /// <summary>Gets the plugin version.</summary>
    public override string Version => "5.0.0";

    /// <inheritdoc/>
    public override string OrchestrationMode => "SemanticSync";

    /// <summary>Gets the plugin category.</summary>
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <summary>
    /// Gets all registered strategy names for diagnostic purposes.
    /// </summary>
    public IReadOnlyCollection<string> RegisteredStrategyNames => _strategies.Keys.ToArray();

    /// <summary>
    /// Gets the count of registered strategies.
    /// </summary>
    public int StrategyCount => _strategies.Count;

    /// <summary>
    /// Registers a strategy with the specified name.
    /// Thread-safe; overwrites any previously registered strategy with the same name.
    /// </summary>
    /// <param name="name">The unique name for the strategy.</param>
    /// <param name="strategy">The strategy instance to register.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="name"/> or <paramref name="strategy"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is empty or whitespace.</exception>
    public void RegisterStrategy(string name, StrategyBase strategy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(strategy);

        _strategies[name] = strategy;
    }

    /// <summary>
    /// Gets a registered strategy by name, cast to the specified type.
    /// </summary>
    /// <typeparam name="T">The expected strategy type.</typeparam>
    /// <param name="name">The strategy name.</param>
    /// <returns>The strategy cast to <typeparamref name="T"/>, or null if not found or wrong type.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="name"/> is null.</exception>
    public T? GetStrategy<T>(string name) where T : StrategyBase
    {
        ArgumentNullException.ThrowIfNull(name);

        return _strategies.TryGetValue(name, out var strategy) ? strategy as T : null;
    }

    /// <summary>
    /// Gets a registered strategy by name without type casting.
    /// </summary>
    /// <param name="name">The strategy name.</param>
    /// <returns>The strategy, or null if not found.</returns>
    public StrategyBase? GetStrategy(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return _strategies.TryGetValue(name, out var strategy) ? strategy : null;
    }

    /// <summary>
    /// Gets all registered strategies as a read-only dictionary.
    /// </summary>
    /// <returns>A read-only view of all registered strategies.</returns>
    public IReadOnlyDictionary<string, StrategyBase> GetAllStrategies() => _strategies;

    /// <summary>
    /// Removes a strategy by name, disposing it if applicable.
    /// </summary>
    /// <param name="name">The strategy name to remove.</param>
    /// <returns>True if the strategy was found and removed; otherwise false.</returns>
    public bool RemoveStrategy(string name)
    {
        if (_strategies.TryRemove(name, out var strategy))
        {
            strategy.Dispose();
            return true;
        }

        return false;
    }

    /// <inheritdoc/>
    protected override async Task OnStartCoreAsync(CancellationToken ct)
    {
        if (_initialized)
            return;

        await base.OnStartCoreAsync(ct);

        // Subscribe to semantic sync message bus topics
        SubscribeToTopics();

        // Initialize all registered strategies
        foreach (var kvp in _strategies)
        {
            await kvp.Value.InitializeAsync(ct).ConfigureAwait(false);
        }

        _initialized = true;
    }

    /// <inheritdoc/>
    protected override async Task OnStopCoreAsync()
    {
        // Unsubscribe from all topics
        foreach (var subscription in _subscriptions)
        {
            try { subscription.Dispose(); }
            catch { /* Best-effort cleanup */ }
        }
        _subscriptions.Clear();

        // Shut down all strategies
        foreach (var kvp in _strategies)
        {
            try
            {
                await kvp.Value.ShutdownAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort: continue shutting down remaining strategies
            }
        }

        _initialized = false;

        await base.OnStopCoreAsync();
    }

    /// <summary>
    /// Subscribes to semantic sync message bus topics for coordination with other plugins.
    /// </summary>
    private void SubscribeToTopics()
    {
        if (MessageBus == null)
            return;

        var topics = new[]
        {
            "semantic-sync.classify",
            "semantic-sync.route",
            "semantic-sync.conflict",
            "semantic-sync.fidelity"
        };

        foreach (var topic in topics)
        {
            try
            {
                var subscription = MessageBus.Subscribe(topic, HandleSemanticSyncMessageAsync);
                _subscriptions.Add(subscription);
            }
            catch
            {
                // Non-fatal: topic subscription failures degrade functionality but don't prevent operation
            }
        }
    }

    /// <summary>
    /// Handles incoming semantic sync messages by routing to the appropriate strategy.
    /// </summary>
    /// <param name="message">The incoming plugin message.</param>
    /// <returns>A task representing the asynchronous handling operation.</returns>
    private async Task HandleSemanticSyncMessageAsync(PluginMessage message)
    {
        if (message.Payload.TryGetValue("strategy", out var strategyNameObj)
            && strategyNameObj is string strategyName
            && _strategies.TryGetValue(strategyName, out _))
        {
            // Strategy-specific handling will be implemented by Plans 03-07
            // as each strategy type is added to the plugin.
            if (MessageBus != null)
            {
                await MessageBus.PublishAsync("semantic-sync.ack", new PluginMessage
                {
                    Type = "semantic-sync.ack",
                    CorrelationId = message.CorrelationId,
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["status"] = "received",
                        ["strategy"] = strategyName,
                        ["timestamp"] = DateTimeOffset.UtcNow
                    }
                }).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var subscription in _subscriptions)
            {
                try { subscription.Dispose(); }
                catch { /* Best-effort cleanup */ }
            }
            _subscriptions.Clear();

            foreach (var kvp in _strategies)
            {
                try { kvp.Value.Dispose(); }
                catch { /* Best-effort cleanup */ }
            }
            _strategies.Clear();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    protected override async ValueTask DisposeAsyncCore()
    {
        foreach (var subscription in _subscriptions)
        {
            try { subscription.Dispose(); }
            catch { /* Best-effort cleanup */ }
        }
        _subscriptions.Clear();

        foreach (var kvp in _strategies)
        {
            try { await kvp.Value.DisposeAsync().ConfigureAwait(false); }
            catch { /* Best-effort cleanup */ }
        }
        _strategies.Clear();

        await base.DisposeAsyncCore();
    }
}
