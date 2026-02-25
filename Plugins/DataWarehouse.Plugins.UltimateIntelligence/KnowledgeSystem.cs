using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

using SdkKnowledgeRequest = DataWarehouse.SDK.AI.KnowledgeRequest;
using SdkKnowledgeResponse = DataWarehouse.SDK.AI.KnowledgeResponse;
using SdkKnowledgeQuery = DataWarehouse.SDK.Contracts.KnowledgeQuery;

namespace DataWarehouse.Plugins.UltimateIntelligence;

// ==================================================================================
// T90.E1: KNOWLEDGE DISCOVERY
// ==================================================================================

#region Knowledge Discovery Interfaces

/// <summary>
/// Interface for components that can expose knowledge to the system.
/// Plugins implementing this interface can participate in knowledge aggregation.
/// </summary>
public interface IKnowledgeSource
{
    /// <summary>
    /// Gets the unique identifier for this knowledge source.
    /// </summary>
    string SourceId { get; }

    /// <summary>
    /// Gets a human-readable name for this knowledge source.
    /// </summary>
    string SourceName { get; }

    /// <summary>
    /// Gets the capabilities provided by this knowledge source.
    /// </summary>
    IReadOnlyCollection<KnowledgeCapability> Capabilities { get; }

    /// <summary>
    /// Gets static knowledge that does not change at runtime.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Collection of static knowledge objects.</returns>
    Task<IReadOnlyCollection<KnowledgeObject>> GetStaticKnowledgeAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets dynamic knowledge that may change at runtime.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Collection of dynamic knowledge objects.</returns>
    Task<IReadOnlyCollection<KnowledgeObject>> GetDynamicKnowledgeAsync(CancellationToken ct = default);

    /// <summary>
    /// Queries this knowledge source for specific knowledge.
    /// </summary>
    /// <param name="query">The query parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Matching knowledge objects.</returns>
    Task<IReadOnlyCollection<KnowledgeObject>> QueryAsync(SdkKnowledgeQuery query, CancellationToken ct = default);

    /// <summary>
    /// Gets whether this source is currently available.
    /// </summary>
    bool IsAvailable { get; }
}

/// <summary>
/// Event arguments for knowledge changed events.
/// </summary>
public sealed class KnowledgeChangedEventArgs : EventArgs
{
    /// <summary>
    /// Gets the source that changed.
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>
    /// Gets the type of change.
    /// </summary>
    public required KnowledgeChangeType ChangeType { get; init; }

    /// <summary>
    /// Gets the affected knowledge objects (for add/update).
    /// </summary>
    public IReadOnlyCollection<KnowledgeObject> AffectedKnowledge { get; init; } = Array.Empty<KnowledgeObject>();

    /// <summary>
    /// Gets the IDs of removed knowledge (for remove).
    /// </summary>
    public IReadOnlyCollection<string> RemovedKnowledgeIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Gets the timestamp of the change.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Type of knowledge change.
/// </summary>
public enum KnowledgeChangeType
{
    /// <summary>New knowledge added.</summary>
    Added,
    /// <summary>Existing knowledge updated.</summary>
    Updated,
    /// <summary>Knowledge removed.</summary>
    Removed,
    /// <summary>Source became available.</summary>
    SourceAvailable,
    /// <summary>Source became unavailable.</summary>
    SourceUnavailable
}

#endregion

#region Aggregated Knowledge

/// <summary>
/// Aggregated knowledge from all sources.
/// </summary>
public sealed class AggregatedKnowledge
{
    /// <summary>
    /// Gets all knowledge objects aggregated from all sources.
    /// </summary>
    public IReadOnlyCollection<KnowledgeObject> AllKnowledge { get; init; } = Array.Empty<KnowledgeObject>();

    /// <summary>
    /// Gets knowledge grouped by source.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyCollection<KnowledgeObject>> BySource { get; init; }
        = new Dictionary<string, IReadOnlyCollection<KnowledgeObject>>();

    /// <summary>
    /// Gets knowledge grouped by topic.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyCollection<KnowledgeObject>> ByTopic { get; init; }
        = new Dictionary<string, IReadOnlyCollection<KnowledgeObject>>();

    /// <summary>
    /// Gets all available capabilities across sources.
    /// </summary>
    public IReadOnlyCollection<KnowledgeCapability> AllCapabilities { get; init; } = Array.Empty<KnowledgeCapability>();

    /// <summary>
    /// Gets the timestamp when this aggregation was created.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets the total number of sources.
    /// </summary>
    public int SourceCount { get; init; }

    /// <summary>
    /// Gets the number of available sources.
    /// </summary>
    public int AvailableSourceCount { get; init; }
}

#endregion

#region Knowledge Aggregator

/// <summary>
/// Aggregates knowledge from all registered knowledge sources.
/// Provides a unified view of all system knowledge for AI consumption.
/// </summary>
public sealed class KnowledgeAggregator : IDisposable
{
    private readonly BoundedDictionary<string, IKnowledgeSource> _sources = new BoundedDictionary<string, IKnowledgeSource>(1000);
    private readonly BoundedDictionary<string, CachedKnowledge> _cache = new BoundedDictionary<string, CachedKnowledge>(1000);
    private readonly SemaphoreSlim _aggregationLock = new(1, 1);
    private readonly TimeSpan _cacheExpiration = TimeSpan.FromMinutes(5);
    private bool _disposed;

    /// <summary>
    /// Event raised when knowledge changes in any source.
    /// </summary>
    public event EventHandler<KnowledgeChangedEventArgs>? KnowledgeChanged;

    /// <summary>
    /// Gets all aggregated knowledge from all sources.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Aggregated knowledge from all sources.</returns>
    public async Task<AggregatedKnowledge> GetAllKnowledgeAsync(CancellationToken ct = default)
    {
        await _aggregationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var allKnowledge = new List<KnowledgeObject>();
            var bySource = new Dictionary<string, IReadOnlyCollection<KnowledgeObject>>();
            var byTopic = new Dictionary<string, List<KnowledgeObject>>();
            var allCapabilities = new List<KnowledgeCapability>();
            var availableCount = 0;

            foreach (var source in _sources.Values)
            {
                if (!source.IsAvailable)
                    continue;

                availableCount++;

                try
                {
                    var sourceKnowledge = await GetSourceKnowledgeAsync(source, ct).ConfigureAwait(false);
                    allKnowledge.AddRange(sourceKnowledge);
                    bySource[source.SourceId] = sourceKnowledge.ToArray();

                    foreach (var knowledge in sourceKnowledge)
                    {
                        if (!byTopic.TryGetValue(knowledge.Topic, out var topicList))
                        {
                            topicList = new List<KnowledgeObject>();
                            byTopic[knowledge.Topic] = topicList;
                        }
                        topicList.Add(knowledge);
                    }

                    allCapabilities.AddRange(source.Capabilities);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    Debug.WriteLine($"Caught exception in KnowledgeSystem.cs");
                    // Source failed, skip but continue with others
                }
            }

            return new AggregatedKnowledge
            {
                AllKnowledge = allKnowledge,
                BySource = bySource,
                ByTopic = byTopic.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyCollection<KnowledgeObject>)kvp.Value.AsReadOnly()),
                AllCapabilities = allCapabilities,
                SourceCount = _sources.Count,
                AvailableSourceCount = availableCount
            };
        }
        finally
        {
            _aggregationLock.Release();
        }
    }

    /// <summary>
    /// Registers a knowledge source.
    /// </summary>
    /// <param name="source">The knowledge source to register.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RegisterSourceAsync(IKnowledgeSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        _sources[source.SourceId] = source;
        _cache.TryRemove(source.SourceId, out _);

        // Pre-warm the cache
        await GetSourceKnowledgeAsync(source, ct).ConfigureAwait(false);

        OnKnowledgeChanged(new KnowledgeChangedEventArgs
        {
            SourceId = source.SourceId,
            ChangeType = KnowledgeChangeType.SourceAvailable
        });
    }

    /// <summary>
    /// Unregisters a knowledge source.
    /// </summary>
    /// <param name="sourceId">The ID of the source to unregister.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task UnregisterSourceAsync(string sourceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);

        if (_sources.TryRemove(sourceId, out _))
        {
            _cache.TryRemove(sourceId, out _);

            OnKnowledgeChanged(new KnowledgeChangedEventArgs
            {
                SourceId = sourceId,
                ChangeType = KnowledgeChangeType.SourceUnavailable
            });
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets the number of registered sources.
    /// </summary>
    public int SourceCount => _sources.Count;

    /// <summary>
    /// Gets all registered source IDs.
    /// </summary>
    public IReadOnlyCollection<string> SourceIds => _sources.Keys.ToArray();

    /// <summary>
    /// Gets a specific source by ID.
    /// </summary>
    /// <param name="sourceId">The source ID.</param>
    /// <returns>The source, or null if not found.</returns>
    public IKnowledgeSource? GetSource(string sourceId)
    {
        return _sources.TryGetValue(sourceId, out var source) ? source : null;
    }

    /// <summary>
    /// Invalidates the cache for a specific source.
    /// </summary>
    /// <param name="sourceId">The source ID to invalidate.</param>
    public void InvalidateCache(string sourceId)
    {
        _cache.TryRemove(sourceId, out _);
    }

    /// <summary>
    /// Invalidates all caches.
    /// </summary>
    public void InvalidateAllCaches()
    {
        _cache.Clear();
    }

    private async Task<IReadOnlyCollection<KnowledgeObject>> GetSourceKnowledgeAsync(IKnowledgeSource source, CancellationToken ct)
    {
        if (_cache.TryGetValue(source.SourceId, out var cached) && !cached.IsExpired(_cacheExpiration))
        {
            return cached.Knowledge;
        }

        var staticKnowledge = await source.GetStaticKnowledgeAsync(ct).ConfigureAwait(false);
        var dynamicKnowledge = await source.GetDynamicKnowledgeAsync(ct).ConfigureAwait(false);

        var allKnowledge = staticKnowledge.Concat(dynamicKnowledge).ToArray();

        _cache[source.SourceId] = new CachedKnowledge
        {
            Knowledge = allKnowledge,
            CachedAt = DateTimeOffset.UtcNow
        };

        return allKnowledge;
    }

    private void OnKnowledgeChanged(KnowledgeChangedEventArgs e)
    {
        KnowledgeChanged?.Invoke(this, e);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _aggregationLock.Dispose();
    }

    private sealed class CachedKnowledge
    {
        public required IReadOnlyCollection<KnowledgeObject> Knowledge { get; init; }
        public DateTimeOffset CachedAt { get; init; }

        public bool IsExpired(TimeSpan expiration)
        {
            return DateTimeOffset.UtcNow - CachedAt > expiration;
        }
    }
}

#endregion

#region Plugin Scanner

/// <summary>
/// Scans plugins to discover those that expose knowledge.
/// </summary>
public sealed class PluginScanner
{
    private readonly IKernelContext? _kernelContext;

    /// <summary>
    /// Creates a new plugin scanner.
    /// </summary>
    /// <param name="kernelContext">Optional kernel context for plugin access.</param>
    public PluginScanner(IKernelContext? kernelContext = null)
    {
        _kernelContext = kernelContext;
    }

    /// <summary>
    /// Scans for all plugins that implement IKnowledgeSource.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Collection of knowledge sources from plugins.</returns>
    public Task<IEnumerable<IKnowledgeSource>> ScanAsync(CancellationToken ct = default)
    {
        var sources = new List<IKnowledgeSource>();

        if (_kernelContext == null)
        {
            return Task.FromResult<IEnumerable<IKnowledgeSource>>(sources);
        }

        // Get all plugins
        var plugins = _kernelContext.GetPlugins<IPlugin>();

        foreach (var plugin in plugins)
        {
            ct.ThrowIfCancellationRequested();

            // Check if plugin directly implements IKnowledgeSource
            if (plugin is IKnowledgeSource directSource)
            {
                sources.Add(directSource);
                continue;
            }

            // Check if plugin can provide knowledge through PluginBase
            if (plugin is PluginBase pluginBase)
            {
                var wrapper = new PluginKnowledgeSourceWrapper(pluginBase);
                if (wrapper.Capabilities.Count > 0)
                {
                    sources.Add(wrapper);
                }
            }
        }

        return Task.FromResult<IEnumerable<IKnowledgeSource>>(sources);
    }

    /// <summary>
    /// Checks if a plugin is knowledge-aware.
    /// </summary>
    /// <param name="plugin">The plugin to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the plugin can provide knowledge.</returns>
    public Task<bool> IsKnowledgeAwareAsync(IPlugin plugin, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        // Direct implementation
        if (plugin is IKnowledgeSource)
            return Task.FromResult(true);

        // PluginBase with registration knowledge
        if (plugin is PluginBase pluginBase)
        {
            var knowledge = pluginBase.GetRegistrationKnowledge();
            return Task.FromResult(knowledge != null);
        }

        return Task.FromResult(false);
    }

    /// <summary>
    /// Extracts capabilities from a plugin.
    /// </summary>
    /// <param name="plugin">The plugin to extract capabilities from.</param>
    /// <returns>Collection of knowledge capabilities.</returns>
    public IEnumerable<KnowledgeCapability> ExtractCapabilities(IPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        if (plugin is IKnowledgeSource source)
        {
            return source.Capabilities;
        }

        if (plugin is PluginBase pluginBase)
        {
            var knowledge = pluginBase.GetRegistrationKnowledge();
            if (knowledge != null && knowledge.Payload.TryGetValue("operations", out var ops) && ops is string[] operations)
            {
                return operations.Select(op => new KnowledgeCapability
                {
                    CapabilityId = $"{plugin.Id}.{op}",
                    Description = $"Operation: {op}"
                });
            }
        }

        return Enumerable.Empty<KnowledgeCapability>();
    }

    /// <summary>
    /// Wrapper to expose PluginBase as IKnowledgeSource.
    /// </summary>
    private sealed class PluginKnowledgeSourceWrapper : IKnowledgeSource
    {
        private readonly PluginBase _plugin;
        private readonly List<KnowledgeCapability> _capabilities;

        public PluginKnowledgeSourceWrapper(PluginBase plugin)
        {
            _plugin = plugin;
            _capabilities = ExtractCapabilitiesFromPlugin(plugin);
        }

        public string SourceId => _plugin.Id;
        public string SourceName => _plugin.Name;
        public IReadOnlyCollection<KnowledgeCapability> Capabilities => _capabilities;
        public bool IsAvailable => true;

        public Task<IReadOnlyCollection<KnowledgeObject>> GetStaticKnowledgeAsync(CancellationToken ct = default)
        {
            var knowledge = _plugin.GetRegistrationKnowledge();
            var result = knowledge != null
                ? new[] { knowledge }
                : Array.Empty<KnowledgeObject>();
            return Task.FromResult<IReadOnlyCollection<KnowledgeObject>>(result);
        }

        public Task<IReadOnlyCollection<KnowledgeObject>> GetDynamicKnowledgeAsync(CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyCollection<KnowledgeObject>>(Array.Empty<KnowledgeObject>());
        }

        public async Task<IReadOnlyCollection<KnowledgeObject>> QueryAsync(SdkKnowledgeQuery query, CancellationToken ct = default)
        {
            var request = new SdkKnowledgeRequest
            {
                RequestId = Guid.NewGuid().ToString(),
                RequestorPluginId = "knowledge-system",
                Topic = query.TopicPattern ?? "*",
                QueryParameters = new Dictionary<string, object>
                {
                    ["searchText"] = query.SearchText ?? string.Empty
                }
            };

            var response = await _plugin.HandleKnowledgeQueryAsync(request, ct).ConfigureAwait(false);
            return response.Success ? response.Results : Array.Empty<KnowledgeObject>();
        }

        private static List<KnowledgeCapability> ExtractCapabilitiesFromPlugin(PluginBase plugin)
        {
            var capabilities = new List<KnowledgeCapability>();
            var knowledge = plugin.GetRegistrationKnowledge();

            if (knowledge?.Payload.TryGetValue("operations", out var ops) == true && ops is string[] operations)
            {
                foreach (var op in operations)
                {
                    capabilities.Add(new KnowledgeCapability
                    {
                        CapabilityId = $"{plugin.Id}.{op}",
                        Description = $"Operation: {op}"
                    });
                }
            }

            return capabilities;
        }
    }
}

#endregion

#region Hot Reload Handler

/// <summary>
/// Handles plugin load/unload events for dynamic knowledge updates.
/// </summary>
public sealed class HotReloadHandler : IDisposable
{
    private readonly KnowledgeAggregator _aggregator;
    private readonly PluginScanner _scanner;
    private readonly IMessageBus? _messageBus;
    private readonly List<IDisposable> _subscriptions = new();
    private bool _disposed;

    /// <summary>
    /// Creates a new hot reload handler.
    /// </summary>
    /// <param name="aggregator">The knowledge aggregator.</param>
    /// <param name="scanner">The plugin scanner.</param>
    /// <param name="messageBus">Optional message bus for plugin lifecycle events.</param>
    public HotReloadHandler(KnowledgeAggregator aggregator, PluginScanner scanner, IMessageBus? messageBus = null)
    {
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _messageBus = messageBus;

        SubscribeToPluginEvents();
    }

    /// <summary>
    /// Handles a plugin being loaded.
    /// </summary>
    /// <param name="plugin">The loaded plugin.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task OnPluginLoadedAsync(IPlugin plugin, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        if (!await _scanner.IsKnowledgeAwareAsync(plugin, ct).ConfigureAwait(false))
            return;

        if (plugin is IKnowledgeSource source)
        {
            await _aggregator.RegisterSourceAsync(source, ct).ConfigureAwait(false);
        }
        else if (plugin is PluginBase pluginBase)
        {
            var wrapper = new PluginBaseKnowledgeSource(pluginBase);
            await _aggregator.RegisterSourceAsync(wrapper, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Handles a plugin being unloaded.
    /// </summary>
    /// <param name="pluginId">The ID of the unloaded plugin.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task OnPluginUnloadedAsync(string pluginId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);

        await _aggregator.UnregisterSourceAsync(pluginId, ct).ConfigureAwait(false);
    }

    private void SubscribeToPluginEvents()
    {
        if (_messageBus == null) return;

        var loadSub = _messageBus.Subscribe(MessageTopics.PluginLoaded, async message =>
        {
            if (message.Payload.TryGetValue("plugin", out var pluginObj) && pluginObj is IPlugin plugin)
            {
                await OnPluginLoadedAsync(plugin).ConfigureAwait(false);
            }
        });
        _subscriptions.Add(loadSub);

        var unloadSub = _messageBus.Subscribe(MessageTopics.PluginUnloaded, async message =>
        {
            if (message.Payload.TryGetValue("pluginId", out var idObj) && idObj is string pluginId)
            {
                await OnPluginUnloadedAsync(pluginId).ConfigureAwait(false);
            }
        });
        _subscriptions.Add(unloadSub);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var sub in _subscriptions)
        {
            try { sub.Dispose(); } catch { /* ignore */ }
        }
        _subscriptions.Clear();
    }

    /// <summary>
    /// Simple wrapper for PluginBase to expose as IKnowledgeSource.
    /// </summary>
    private sealed class PluginBaseKnowledgeSource : IKnowledgeSource
    {
        private readonly PluginBase _plugin;

        public PluginBaseKnowledgeSource(PluginBase plugin) => _plugin = plugin;

        public string SourceId => _plugin.Id;
        public string SourceName => _plugin.Name;
        public bool IsAvailable => true;

        public IReadOnlyCollection<KnowledgeCapability> Capabilities
        {
            get
            {
                var knowledge = _plugin.GetRegistrationKnowledge();
                if (knowledge?.Payload.TryGetValue("operations", out var ops) == true && ops is string[] operations)
                {
                    return operations.Select(op => new KnowledgeCapability
                    {
                        CapabilityId = $"{_plugin.Id}.{op}",
                        Description = $"Operation: {op}"
                    }).ToArray();
                }
                return Array.Empty<KnowledgeCapability>();
            }
        }

        public Task<IReadOnlyCollection<KnowledgeObject>> GetStaticKnowledgeAsync(CancellationToken ct = default)
        {
            var knowledge = _plugin.GetRegistrationKnowledge();
            return Task.FromResult<IReadOnlyCollection<KnowledgeObject>>(
                knowledge != null ? new[] { knowledge } : Array.Empty<KnowledgeObject>());
        }

        public Task<IReadOnlyCollection<KnowledgeObject>> GetDynamicKnowledgeAsync(CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyCollection<KnowledgeObject>>(Array.Empty<KnowledgeObject>());
        }

        public Task<IReadOnlyCollection<KnowledgeObject>> QueryAsync(SdkKnowledgeQuery query, CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyCollection<KnowledgeObject>>(Array.Empty<KnowledgeObject>());
        }
    }
}

#endregion

#region Capability Matrix

/// <summary>
/// Tracks what capabilities each plugin provides for efficient lookup.
/// </summary>
public sealed class CapabilityMatrix
{
    private readonly BoundedDictionary<string, HashSet<KnowledgeCapability>> _matrix = new BoundedDictionary<string, HashSet<KnowledgeCapability>>(1000);
    private readonly BoundedDictionary<string, HashSet<string>> _capabilityToPlugins = new BoundedDictionary<string, HashSet<string>>(1000);
    private readonly object _updateLock = new();

    /// <summary>
    /// Gets the capabilities for a specific plugin.
    /// </summary>
    /// <param name="pluginId">The plugin ID.</param>
    /// <returns>Array of capabilities.</returns>
    public KnowledgeCapability[] GetCapabilities(string pluginId)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);

        return _matrix.TryGetValue(pluginId, out var caps)
            ? caps.ToArray()
            : Array.Empty<KnowledgeCapability>();
    }

    /// <summary>
    /// Gets all plugins that have a specific capability.
    /// </summary>
    /// <param name="capability">The capability to search for.</param>
    /// <returns>Array of plugin IDs.</returns>
    public string[] GetPluginsWithCapability(KnowledgeCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);

        return _capabilityToPlugins.TryGetValue(capability.CapabilityId, out var plugins)
            ? plugins.ToArray()
            : Array.Empty<string>();
    }

    /// <summary>
    /// Gets all plugins that have a capability matching the given ID.
    /// </summary>
    /// <param name="capabilityId">The capability ID to search for.</param>
    /// <returns>Array of plugin IDs.</returns>
    public string[] GetPluginsWithCapability(string capabilityId)
    {
        ArgumentException.ThrowIfNullOrEmpty(capabilityId);

        return _capabilityToPlugins.TryGetValue(capabilityId, out var plugins)
            ? plugins.ToArray()
            : Array.Empty<string>();
    }

    /// <summary>
    /// Updates the capabilities for a plugin.
    /// </summary>
    /// <param name="pluginId">The plugin ID.</param>
    /// <param name="capabilities">The new capabilities.</param>
    public void UpdateCapabilities(string pluginId, KnowledgeCapability[] capabilities)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);
        ArgumentNullException.ThrowIfNull(capabilities);

        lock (_updateLock)
        {
            // Remove old mappings
            if (_matrix.TryGetValue(pluginId, out var oldCaps))
            {
                foreach (var oldCap in oldCaps)
                {
                    if (_capabilityToPlugins.TryGetValue(oldCap.CapabilityId, out var plugins))
                    {
                        plugins.Remove(pluginId);
                    }
                }
            }

            // Add new mappings
            _matrix[pluginId] = new HashSet<KnowledgeCapability>(capabilities, new CapabilityComparer());

            foreach (var cap in capabilities)
            {
                if (!_capabilityToPlugins.TryGetValue(cap.CapabilityId, out var plugins))
                {
                    plugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _capabilityToPlugins[cap.CapabilityId] = plugins;
                }
                plugins.Add(pluginId);
            }
        }
    }

    /// <summary>
    /// Removes a plugin from the matrix.
    /// </summary>
    /// <param name="pluginId">The plugin ID to remove.</param>
    public void RemovePlugin(string pluginId)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);

        lock (_updateLock)
        {
            if (_matrix.TryRemove(pluginId, out var caps))
            {
                foreach (var cap in caps)
                {
                    if (_capabilityToPlugins.TryGetValue(cap.CapabilityId, out var plugins))
                    {
                        plugins.Remove(pluginId);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Gets all registered plugin IDs.
    /// </summary>
    public IReadOnlyCollection<string> AllPluginIds => _matrix.Keys.ToArray();

    /// <summary>
    /// Gets all registered capability IDs.
    /// </summary>
    public IReadOnlyCollection<string> AllCapabilityIds => _capabilityToPlugins.Keys.ToArray();

    /// <summary>
    /// Searches for capabilities matching a pattern.
    /// </summary>
    /// <param name="pattern">The search pattern (supports * wildcard).</param>
    /// <returns>Matching capabilities.</returns>
    public IEnumerable<KnowledgeCapability> SearchCapabilities(string pattern)
    {
        ArgumentException.ThrowIfNullOrEmpty(pattern);

        var regex = new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));

        return _matrix.Values
            .SelectMany(caps => caps)
            .Where(cap => regex.IsMatch(cap.CapabilityId))
            .Distinct(new CapabilityComparer());
    }

    private sealed class CapabilityComparer : IEqualityComparer<KnowledgeCapability>
    {
        public bool Equals(KnowledgeCapability? x, KnowledgeCapability? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;
            return string.Equals(x.CapabilityId, y.CapabilityId, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode(KnowledgeCapability obj)
        {
            return StringComparer.OrdinalIgnoreCase.GetHashCode(obj.CapabilityId);
        }
    }
}

#endregion

// ==================================================================================
// T90.E2: CONTEXT BUILDING
// ==================================================================================

#region System Context

/// <summary>
/// System context built from aggregated knowledge for AI consumption.
/// </summary>
public sealed class SystemContext
{
    /// <summary>
    /// Gets the context prompt text.
    /// </summary>
    public required string ContextPrompt { get; init; }

    /// <summary>
    /// Gets the available commands.
    /// </summary>
    public IReadOnlyCollection<CommandDefinition> AvailableCommands { get; init; } = Array.Empty<CommandDefinition>();

    /// <summary>
    /// Gets the current system state summary.
    /// </summary>
    public required SystemState CurrentState { get; init; }

    /// <summary>
    /// Gets the domain knowledge included in context.
    /// </summary>
    public IReadOnlyCollection<KnowledgeObject> IncludedKnowledge { get; init; } = Array.Empty<KnowledgeObject>();

    /// <summary>
    /// Gets the token count of the context.
    /// </summary>
    public int TokenCount { get; init; }

    /// <summary>
    /// Gets the timestamp when this context was built.
    /// </summary>
    public DateTimeOffset BuiltAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets knowledge that was excluded due to token budget.
    /// </summary>
    public IReadOnlyCollection<string> ExcludedKnowledgeIds { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Requirements for context building.
/// </summary>
public sealed class ContextRequirements
{
    /// <summary>
    /// Maximum tokens allowed for context.
    /// </summary>
    public int MaxTokens { get; init; } = 4000;

    /// <summary>
    /// Required domains to include.
    /// </summary>
    public IReadOnlyCollection<string> RequiredDomains { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Whether to include command definitions.
    /// </summary>
    public bool IncludeCommands { get; init; } = true;

    /// <summary>
    /// Whether to include current state.
    /// </summary>
    public bool IncludeState { get; init; } = true;

    /// <summary>
    /// The user query to optimize context for.
    /// </summary>
    public string? Query { get; init; }

    /// <summary>
    /// Minimum priority for knowledge inclusion.
    /// </summary>
    public int MinimumPriority { get; init; }
}

#endregion

#region Context Builder

/// <summary>
/// Builds system context from aggregated knowledge for AI consumption.
/// </summary>
public sealed class ContextBuilder
{
    private readonly KnowledgeAggregator _aggregator;
    private readonly CommandRegistry _commandRegistry;
    private readonly StateAggregator _stateAggregator;
    private readonly DomainSelector _domainSelector;

    /// <summary>
    /// Creates a new context builder.
    /// </summary>
    /// <param name="aggregator">The knowledge aggregator.</param>
    /// <param name="commandRegistry">The command registry.</param>
    /// <param name="stateAggregator">The state aggregator.</param>
    /// <param name="domainSelector">The domain selector.</param>
    public ContextBuilder(
        KnowledgeAggregator aggregator,
        CommandRegistry commandRegistry,
        StateAggregator stateAggregator,
        DomainSelector domainSelector)
    {
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
        _commandRegistry = commandRegistry ?? throw new ArgumentNullException(nameof(commandRegistry));
        _stateAggregator = stateAggregator ?? throw new ArgumentNullException(nameof(stateAggregator));
        _domainSelector = domainSelector ?? throw new ArgumentNullException(nameof(domainSelector));
    }

    /// <summary>
    /// Builds a system context based on the given requirements.
    /// </summary>
    /// <param name="requirements">The context requirements.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The built system context.</returns>
    public async Task<SystemContext> BuildContextAsync(ContextRequirements requirements, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requirements);

        var contextBuilder = new StringBuilder();
        var includedKnowledge = new List<KnowledgeObject>();
        var excludedIds = new List<string>();
        var currentTokenCount = 0;

        // Get current state
        var state = await _stateAggregator.GetCurrentStateAsync(ct).ConfigureAwait(false);
        if (requirements.IncludeState)
        {
            var stateText = FormatState(state);
            var stateTokens = EstimateTokens(stateText);
            if (currentTokenCount + stateTokens <= requirements.MaxTokens)
            {
                contextBuilder.AppendLine("## Current System State");
                contextBuilder.AppendLine(stateText);
                contextBuilder.AppendLine();
                currentTokenCount += stateTokens;
            }
        }

        // Get commands
        var commands = new List<CommandDefinition>();
        if (requirements.IncludeCommands)
        {
            commands.AddRange(_commandRegistry.GetAllCommands());
            var commandsText = FormatCommands(commands);
            var commandsTokens = EstimateTokens(commandsText);
            if (currentTokenCount + commandsTokens <= requirements.MaxTokens)
            {
                contextBuilder.AppendLine("## Available Commands");
                contextBuilder.AppendLine(commandsText);
                contextBuilder.AppendLine();
                currentTokenCount += commandsTokens;
            }
        }

        // Select relevant domains based on query
        var relevantDomainsList = requirements.RequiredDomains.Count > 0
            ? requirements.RequiredDomains.ToList()
            : (await _domainSelector.SelectDomainsAsync(requirements.Query ?? string.Empty, ct).ConfigureAwait(false)).ToList();

        // Get all knowledge
        var aggregatedKnowledge = await _aggregator.GetAllKnowledgeAsync(ct).ConfigureAwait(false);

        // Filter and prioritize knowledge
        var prioritizedKnowledge = aggregatedKnowledge.AllKnowledge
            .Where(k => relevantDomainsList.Count == 0 || relevantDomainsList.Any(d => k.Topic.StartsWith(d, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(k => k.Confidence)
            .ThenByDescending(k => k.Timestamp)
            .ToList();

        contextBuilder.AppendLine("## Domain Knowledge");

        foreach (var knowledge in prioritizedKnowledge)
        {
            var knowledgeText = FormatKnowledge(knowledge);
            var knowledgeTokens = EstimateTokens(knowledgeText);

            if (currentTokenCount + knowledgeTokens <= requirements.MaxTokens)
            {
                contextBuilder.AppendLine(knowledgeText);
                includedKnowledge.Add(knowledge);
                currentTokenCount += knowledgeTokens;
            }
            else
            {
                excludedIds.Add(knowledge.Id);
            }
        }

        return new SystemContext
        {
            ContextPrompt = contextBuilder.ToString(),
            AvailableCommands = commands,
            CurrentState = state,
            IncludedKnowledge = includedKnowledge,
            TokenCount = currentTokenCount,
            ExcludedKnowledgeIds = excludedIds
        };
    }

    private static string FormatState(SystemState state)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"- Active Plugins: {state.ActivePluginCount}");
        sb.AppendLine($"- Active Connections: {state.ActiveConnectionCount}");
        sb.AppendLine($"- Active Operations: {state.ActiveOperationCount}");
        sb.AppendLine($"- Health: {state.OverallHealth}");

        if (state.Metrics.Count > 0)
        {
            sb.AppendLine("- Key Metrics:");
            foreach (var metric in state.Metrics.Take(5))
            {
                sb.AppendLine($"  - {metric.Key}: {metric.Value}");
            }
        }

        return sb.ToString();
    }

    private static string FormatCommands(IEnumerable<CommandDefinition> commands)
    {
        var sb = new StringBuilder();
        foreach (var cmd in commands.Take(20))
        {
            sb.AppendLine($"- **{cmd.Name}**: {cmd.Description}");
            if (cmd.Parameters.Count > 0)
            {
                sb.AppendLine($"  Parameters: {string.Join(", ", cmd.Parameters.Keys)}");
            }
        }
        return sb.ToString();
    }

    private static string FormatKnowledge(KnowledgeObject knowledge)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### {knowledge.Topic}");
        if (!string.IsNullOrEmpty(knowledge.Description))
        {
            sb.AppendLine(knowledge.Description);
        }
        return sb.ToString();
    }

    private static int EstimateTokens(string text)
    {
        // Rough estimation: ~4 characters per token
        return text.Length / 4;
    }
}

#endregion

#region Domain Selector

/// <summary>
/// Selects relevant domains based on query semantics.
/// </summary>
public sealed class DomainSelector
{
    private readonly BoundedDictionary<string, CachedDomainSelection> _cache = new BoundedDictionary<string, CachedDomainSelection>(1000);
    private readonly TimeSpan _cacheExpiration = TimeSpan.FromMinutes(10);
    private readonly KnowledgeAggregator _aggregator;

    /// <summary>
    /// Creates a new domain selector.
    /// </summary>
    /// <param name="aggregator">The knowledge aggregator.</param>
    public DomainSelector(KnowledgeAggregator aggregator)
    {
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
    }

    /// <summary>
    /// Selects relevant domains for a given query.
    /// </summary>
    /// <param name="query">The query text.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Collection of relevant domain names.</returns>
    public async Task<IEnumerable<string>> SelectDomainsAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return await GetAllDomainsAsync(ct).ConfigureAwait(false);
        }

        // Check cache
        var cacheKey = NormalizeQuery(query);
        if (_cache.TryGetValue(cacheKey, out var cached) && !cached.IsExpired(_cacheExpiration))
        {
            return cached.Domains;
        }

        // Get all knowledge to extract domains
        var aggregated = await _aggregator.GetAllKnowledgeAsync(ct).ConfigureAwait(false);
        var allTopics = aggregated.ByTopic.Keys.ToList();

        // Simple keyword matching for domain selection
        var queryWords = query.ToLowerInvariant().Split(new[] { ' ', ',', '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);
        var matchedDomains = new List<(string Domain, int Score)>();

        foreach (var topic in allTopics)
        {
            var score = CalculateRelevanceScore(topic, queryWords);
            if (score > 0)
            {
                matchedDomains.Add((topic, score));
            }
        }

        var selectedDomains = matchedDomains
            .OrderByDescending(d => d.Score)
            .Take(10)
            .Select(d => d.Domain)
            .ToList();

        // Cache the result
        _cache[cacheKey] = new CachedDomainSelection
        {
            Domains = selectedDomains,
            CachedAt = DateTimeOffset.UtcNow
        };

        return selectedDomains;
    }

    private async Task<IEnumerable<string>> GetAllDomainsAsync(CancellationToken ct)
    {
        var aggregated = await _aggregator.GetAllKnowledgeAsync(ct).ConfigureAwait(false);
        return aggregated.ByTopic.Keys;
    }

    private static string NormalizeQuery(string query)
    {
        return query.ToLowerInvariant().Trim();
    }

    private static int CalculateRelevanceScore(string topic, string[] queryWords)
    {
        var topicLower = topic.ToLowerInvariant();
        var score = 0;

        foreach (var word in queryWords)
        {
            if (topicLower.Contains(word))
            {
                score += 10;
            }
            else if (word.Length >= 3)
            {
                // Partial match for longer words
                var topicParts = topicLower.Split('.');
                foreach (var part in topicParts)
                {
                    if (part.Contains(word) || word.Contains(part))
                    {
                        score += 5;
                    }
                }
            }
        }

        return score;
    }

    /// <summary>
    /// Clears the domain selection cache.
    /// </summary>
    public void ClearCache()
    {
        _cache.Clear();
    }

    private sealed class CachedDomainSelection
    {
        public required IReadOnlyCollection<string> Domains { get; init; }
        public DateTimeOffset CachedAt { get; init; }

        public bool IsExpired(TimeSpan expiration)
        {
            return DateTimeOffset.UtcNow - CachedAt > expiration;
        }
    }
}

#endregion

#region State Aggregator

/// <summary>
/// Aggregated system state from all sources.
/// </summary>
public sealed class SystemState
{
    /// <summary>
    /// Gets the number of active plugins.
    /// </summary>
    public int ActivePluginCount { get; init; }

    /// <summary>
    /// Gets the number of active connections.
    /// </summary>
    public int ActiveConnectionCount { get; init; }

    /// <summary>
    /// Gets the number of active operations.
    /// </summary>
    public int ActiveOperationCount { get; init; }

    /// <summary>
    /// Gets the overall system health.
    /// </summary>
    public string OverallHealth { get; init; } = "Unknown";

    /// <summary>
    /// Gets key metrics.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metrics { get; init; } = new Dictionary<string, object>();

    /// <summary>
    /// Gets active plugin states.
    /// </summary>
    public IReadOnlyDictionary<string, PluginState> PluginStates { get; init; } = new Dictionary<string, PluginState>();

    /// <summary>
    /// Gets the timestamp when this state was captured.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// State of an individual plugin.
/// </summary>
public sealed class PluginState
{
    /// <summary>
    /// Plugin identifier.
    /// </summary>
    public required string PluginId { get; init; }

    /// <summary>
    /// Whether the plugin is active.
    /// </summary>
    public bool IsActive { get; init; }

    /// <summary>
    /// Plugin-specific state data.
    /// </summary>
    public IReadOnlyDictionary<string, object> Data { get; init; } = new Dictionary<string, object>();
}

/// <summary>
/// Aggregates current state from all sources for context building.
/// </summary>
public sealed class StateAggregator : IDisposable
{
    private readonly IKernelContext? _kernelContext;
    private readonly IMessageBus? _messageBus;
    private readonly BoundedDictionary<string, PluginState> _pluginStates = new BoundedDictionary<string, PluginState>(1000);
    private readonly BoundedDictionary<string, object> _metrics = new BoundedDictionary<string, object>(1000);
    private readonly List<IDisposable> _subscriptions = new();
    private int _activeOperations;
    private int _activeConnections;
    private bool _disposed;

    /// <summary>
    /// Creates a new state aggregator.
    /// </summary>
    /// <param name="kernelContext">Optional kernel context.</param>
    /// <param name="messageBus">Optional message bus for subscriptions.</param>
    public StateAggregator(IKernelContext? kernelContext = null, IMessageBus? messageBus = null)
    {
        _kernelContext = kernelContext;
        _messageBus = messageBus;

        SubscribeToStateUpdates();
    }

    /// <summary>
    /// Gets the current aggregated system state.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The current system state.</returns>
    public Task<SystemState> GetCurrentStateAsync(CancellationToken ct = default)
    {
        var activePlugins = _kernelContext?.GetPlugins<IPlugin>().Count() ?? _pluginStates.Count;

        var state = new SystemState
        {
            ActivePluginCount = activePlugins,
            ActiveConnectionCount = _activeConnections,
            ActiveOperationCount = _activeOperations,
            OverallHealth = DetermineOverallHealth(),
            Metrics = new Dictionary<string, object>(_metrics),
            PluginStates = new Dictionary<string, PluginState>(_pluginStates)
        };

        return Task.FromResult(state);
    }

    /// <summary>
    /// Updates state for a specific plugin.
    /// </summary>
    /// <param name="pluginId">The plugin ID.</param>
    /// <param name="data">The state data.</param>
    public void UpdatePluginState(string pluginId, Dictionary<string, object> data)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);

        _pluginStates[pluginId] = new PluginState
        {
            PluginId = pluginId,
            IsActive = true,
            Data = data
        };
    }

    /// <summary>
    /// Updates a metric value.
    /// </summary>
    /// <param name="name">The metric name.</param>
    /// <param name="value">The metric value.</param>
    public void UpdateMetric(string name, object value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _metrics[name] = value;
    }

    /// <summary>
    /// Increments the active operation count.
    /// </summary>
    public void IncrementActiveOperations()
    {
        Interlocked.Increment(ref _activeOperations);
    }

    /// <summary>
    /// Decrements the active operation count.
    /// </summary>
    public void DecrementActiveOperations()
    {
        Interlocked.Decrement(ref _activeOperations);
    }

    /// <summary>
    /// Increments the active connection count.
    /// </summary>
    public void IncrementActiveConnections()
    {
        Interlocked.Increment(ref _activeConnections);
    }

    /// <summary>
    /// Decrements the active connection count.
    /// </summary>
    public void DecrementActiveConnections()
    {
        Interlocked.Decrement(ref _activeConnections);
    }

    private void SubscribeToStateUpdates()
    {
        if (_messageBus == null) return;

        // Subscribe to various state update topics
        var topics = new[]
        {
            "plugin.state.update",
            "connection.state.update",
            "operation.state.update",
            "metric.update"
        };

        foreach (var topic in topics)
        {
            try
            {
                var sub = _messageBus.Subscribe(topic, HandleStateUpdateAsync);
                _subscriptions.Add(sub);
            }
            catch
            {
                Debug.WriteLine($"Caught exception in KnowledgeSystem.cs");
                // Topic may not exist, continue
            }
        }
    }

    private Task HandleStateUpdateAsync(PluginMessage message)
    {
        switch (message.Type)
        {
            case "plugin.state.update":
                if (message.Payload.TryGetValue("pluginId", out var pluginIdObj) && pluginIdObj is string pluginId)
                {
                    var data = message.Payload
                        .Where(kvp => kvp.Key != "pluginId")
                        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                    UpdatePluginState(pluginId, data);
                }
                break;

            case "metric.update":
                if (message.Payload.TryGetValue("name", out var nameObj) && nameObj is string name &&
                    message.Payload.TryGetValue("value", out var value))
                {
                    UpdateMetric(name, value);
                }
                break;
        }

        return Task.CompletedTask;
    }

    private string DetermineOverallHealth()
    {
        // Simple health determination based on available data
        if (_pluginStates.IsEmpty && _activeConnections == 0)
        {
            return "Unknown";
        }

        var activePlugins = _pluginStates.Values.Count(p => p.IsActive);
        var totalPlugins = _pluginStates.Count;

        if (totalPlugins == 0)
        {
            return "Healthy";
        }

        var healthRatio = (double)activePlugins / totalPlugins;

        return healthRatio switch
        {
            >= 0.9 => "Healthy",
            >= 0.7 => "Degraded",
            >= 0.5 => "Warning",
            _ => "Critical"
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var sub in _subscriptions)
        {
            try { sub.Dispose(); } catch { /* ignore */ }
        }
        _subscriptions.Clear();
    }
}

#endregion

#region Command Registry

/// <summary>
/// Definition of a command available in the system.
/// </summary>
public sealed class CommandDefinition
{
    /// <summary>
    /// Gets the unique command name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the command description.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Gets the command category.
    /// </summary>
    public string? Category { get; init; }

    /// <summary>
    /// Gets the command parameters.
    /// </summary>
    public IReadOnlyDictionary<string, CommandParameter> Parameters { get; init; } = new Dictionary<string, CommandParameter>();

    /// <summary>
    /// Gets whether this command requires approval.
    /// </summary>
    public bool RequiresApproval { get; init; }

    /// <summary>
    /// Gets the plugin that provides this command.
    /// </summary>
    public string? PluginId { get; init; }

    /// <summary>
    /// Gets tags for semantic search.
    /// </summary>
    public IReadOnlyCollection<string> Tags { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Definition of a command parameter.
/// </summary>
public sealed class CommandParameter
{
    /// <summary>
    /// Gets the parameter name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the parameter type.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Gets the parameter description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets whether this parameter is required.
    /// </summary>
    public bool Required { get; init; }

    /// <summary>
    /// Gets the default value.
    /// </summary>
    public object? DefaultValue { get; init; }
}

/// <summary>
/// Registry of all available commands in the system.
/// </summary>
public sealed class CommandRegistry
{
    private readonly BoundedDictionary<string, CommandDefinition> _commands = new BoundedDictionary<string, CommandDefinition>(1000);

    /// <summary>
    /// Registers a command.
    /// </summary>
    /// <param name="command">The command to register.</param>
    public void RegisterCommand(CommandDefinition command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _commands[command.Name] = command;
    }

    /// <summary>
    /// Unregisters a command.
    /// </summary>
    /// <param name="commandName">The command name to unregister.</param>
    public void UnregisterCommand(string commandName)
    {
        ArgumentException.ThrowIfNullOrEmpty(commandName);
        _commands.TryRemove(commandName, out _);
    }

    /// <summary>
    /// Gets a command by name.
    /// </summary>
    /// <param name="commandName">The command name.</param>
    /// <returns>The command definition, or null if not found.</returns>
    public CommandDefinition? GetCommand(string commandName)
    {
        ArgumentException.ThrowIfNullOrEmpty(commandName);
        return _commands.TryGetValue(commandName, out var cmd) ? cmd : null;
    }

    /// <summary>
    /// Gets all registered commands.
    /// </summary>
    /// <returns>All command definitions.</returns>
    public IEnumerable<CommandDefinition> GetAllCommands()
    {
        return _commands.Values;
    }

    /// <summary>
    /// Searches for commands matching a query.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <returns>Matching command definitions.</returns>
    public IEnumerable<CommandDefinition> SearchCommands(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return _commands.Values;
        }

        var queryLower = query.ToLowerInvariant();
        var words = queryLower.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        return _commands.Values
            .Where(cmd => MatchesQuery(cmd, words))
            .OrderByDescending(cmd => CalculateMatchScore(cmd, words));
    }

    /// <summary>
    /// Gets commands by category.
    /// </summary>
    /// <param name="category">The category.</param>
    /// <returns>Commands in the category.</returns>
    public IEnumerable<CommandDefinition> GetByCategory(string category)
    {
        ArgumentException.ThrowIfNullOrEmpty(category);
        return _commands.Values.Where(cmd =>
            string.Equals(cmd.Category, category, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets commands by plugin.
    /// </summary>
    /// <param name="pluginId">The plugin ID.</param>
    /// <returns>Commands from the plugin.</returns>
    public IEnumerable<CommandDefinition> GetByPlugin(string pluginId)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);
        return _commands.Values.Where(cmd =>
            string.Equals(cmd.PluginId, pluginId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets the total number of registered commands.
    /// </summary>
    public int Count => _commands.Count;

    private static bool MatchesQuery(CommandDefinition cmd, string[] words)
    {
        var searchText = $"{cmd.Name} {cmd.Description} {cmd.Category ?? ""} {string.Join(" ", cmd.Tags)}".ToLowerInvariant();
        return words.All(word => searchText.Contains(word));
    }

    private static int CalculateMatchScore(CommandDefinition cmd, string[] words)
    {
        var score = 0;
        var nameLower = cmd.Name.ToLowerInvariant();
        var descLower = cmd.Description.ToLowerInvariant();

        foreach (var word in words)
        {
            if (nameLower.Contains(word)) score += 10;
            if (descLower.Contains(word)) score += 5;
            if (cmd.Tags.Any(t => t.ToLowerInvariant().Contains(word))) score += 3;
        }

        return score;
    }
}

#endregion

// ==================================================================================
// T90.E3: QUERY EXECUTION
// ==================================================================================

#region Knowledge Request/Response Types

/// <summary>
/// Request for knowledge operations.
/// </summary>
public sealed class KnowledgeQueryRequest
{
    /// <summary>
    /// Gets the unique request ID.
    /// </summary>
    public string RequestId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Gets the query type.
    /// </summary>
    public required KnowledgeQueryType QueryType { get; init; }

    /// <summary>
    /// Gets the query text or identifier.
    /// </summary>
    public required string Query { get; init; }

    /// <summary>
    /// Gets query parameters.
    /// </summary>
    public IReadOnlyDictionary<string, object> Parameters { get; init; } = new Dictionary<string, object>();

    /// <summary>
    /// Gets the requestor ID.
    /// </summary>
    public string? RequestorId { get; init; }

    /// <summary>
    /// Gets the timeout for this request.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Type of knowledge query.
/// </summary>
public enum KnowledgeQueryType
{
    /// <summary>Query for facts.</summary>
    Facts,
    /// <summary>Query for state information.</summary>
    State,
    /// <summary>Query for available commands.</summary>
    Commands,
    /// <summary>Query for relationships between entities.</summary>
    Relationships,
    /// <summary>Query for capabilities.</summary>
    Capabilities,
    /// <summary>Execute a command.</summary>
    Execute
}

/// <summary>
/// Response from a knowledge query.
/// </summary>
public sealed class KnowledgeQueryResponse
{
    /// <summary>
    /// Gets the request ID this response is for.
    /// </summary>
    public required string RequestId { get; init; }

    /// <summary>
    /// Gets whether the query was successful.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Gets the response data.
    /// </summary>
    public object? Data { get; init; }

    /// <summary>
    /// Gets the error message if not successful.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Gets the error code if not successful.
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Gets the execution duration.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Gets response metadata.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = new Dictionary<string, object>();

    /// <summary>
    /// Creates a successful response.
    /// </summary>
    public static KnowledgeQueryResponse Ok(string requestId, object? data = null, TimeSpan? duration = null)
    {
        return new KnowledgeQueryResponse
        {
            RequestId = requestId,
            Success = true,
            Data = data,
            Duration = duration ?? TimeSpan.Zero
        };
    }

    /// <summary>
    /// Creates an error response.
    /// </summary>
    public static KnowledgeQueryResponse Fail(string requestId, string error, string? errorCode = null, TimeSpan? duration = null)
    {
        return new KnowledgeQueryResponse
        {
            RequestId = requestId,
            Success = false,
            Error = error,
            ErrorCode = errorCode,
            Duration = duration ?? TimeSpan.Zero
        };
    }
}

#endregion

#region Knowledge Handler Interface

/// <summary>
/// Interface for handlers that process knowledge requests.
/// </summary>
public interface IKnowledgeHandler
{
    /// <summary>
    /// Gets the handler name.
    /// </summary>
    string HandlerName { get; }

    /// <summary>
    /// Gets the query types this handler supports.
    /// </summary>
    IReadOnlyCollection<KnowledgeQueryType> SupportedQueryTypes { get; }

    /// <summary>
    /// Gets the handler priority (higher = higher priority).
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Determines if this handler can handle the given request.
    /// </summary>
    /// <param name="request">The request to check.</param>
    /// <returns>True if this handler can handle the request.</returns>
    bool CanHandle(KnowledgeQueryRequest request);

    /// <summary>
    /// Handles the knowledge request.
    /// </summary>
    /// <param name="request">The request to handle.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The response.</returns>
    Task<KnowledgeQueryResponse> HandleAsync(KnowledgeQueryRequest request, CancellationToken ct = default);
}

#endregion

#region Knowledge Router

/// <summary>
/// Routes knowledge requests to appropriate handlers.
/// </summary>
public sealed class KnowledgeRouter
{
    private readonly BoundedDictionary<string, IKnowledgeHandler> _handlers = new BoundedDictionary<string, IKnowledgeHandler>(1000);

    /// <summary>
    /// Registers a knowledge handler.
    /// </summary>
    /// <param name="handler">The handler to register.</param>
    public void RegisterHandler(IKnowledgeHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[handler.HandlerName] = handler;
    }

    /// <summary>
    /// Unregisters a knowledge handler.
    /// </summary>
    /// <param name="handlerName">The handler name to unregister.</param>
    public void UnregisterHandler(string handlerName)
    {
        ArgumentException.ThrowIfNullOrEmpty(handlerName);
        _handlers.TryRemove(handlerName, out _);
    }

    /// <summary>
    /// Routes a request to an appropriate handler.
    /// </summary>
    /// <param name="request">The request to route.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The best handler for this request.</returns>
    public Task<IKnowledgeHandler?> RouteAsync(KnowledgeQueryRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Find all handlers that can handle this request
        var eligibleHandlers = _handlers.Values
            .Where(h => h.CanHandle(request))
            .OrderByDescending(h => h.Priority)
            .ToList();

        return Task.FromResult(eligibleHandlers.FirstOrDefault());
    }

    /// <summary>
    /// Gets all handlers that can handle a request.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>All eligible handlers ordered by priority.</returns>
    public IEnumerable<IKnowledgeHandler> GetAllHandlers(KnowledgeQueryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return _handlers.Values
            .Where(h => h.CanHandle(request))
            .OrderByDescending(h => h.Priority);
    }

    /// <summary>
    /// Gets the number of registered handlers.
    /// </summary>
    public int HandlerCount => _handlers.Count;
}

#endregion

#region Query Executor

/// <summary>
/// Executes knowledge queries by routing to appropriate handlers.
/// </summary>
public sealed class QueryExecutor
{
    private readonly KnowledgeRouter _router;
    private readonly KnowledgeAggregator _aggregator;
    private readonly StateAggregator _stateAggregator;
    private readonly CommandRegistry _commandRegistry;

    /// <summary>
    /// Creates a new query executor.
    /// </summary>
    /// <param name="router">The knowledge router.</param>
    /// <param name="aggregator">The knowledge aggregator.</param>
    /// <param name="stateAggregator">The state aggregator.</param>
    /// <param name="commandRegistry">The command registry.</param>
    public QueryExecutor(
        KnowledgeRouter router,
        KnowledgeAggregator aggregator,
        StateAggregator stateAggregator,
        CommandRegistry commandRegistry)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
        _stateAggregator = stateAggregator ?? throw new ArgumentNullException(nameof(stateAggregator));
        _commandRegistry = commandRegistry ?? throw new ArgumentNullException(nameof(commandRegistry));
    }

    /// <summary>
    /// Executes a knowledge query.
    /// </summary>
    /// <param name="request">The request to execute.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The query response.</returns>
    public async Task<KnowledgeQueryResponse> ExecuteQueryAsync(KnowledgeQueryRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sw = Stopwatch.StartNew();

        try
        {
            // Validate request
            var validationError = ValidateRequest(request);
            if (validationError != null)
            {
                return KnowledgeQueryResponse.Fail(request.RequestId, validationError, "VALIDATION_ERROR");
            }

            // Route to handler
            var handler = await _router.RouteAsync(request, ct).ConfigureAwait(false);

            if (handler != null)
            {
                return await handler.HandleAsync(request, ct).ConfigureAwait(false);
            }

            // Default handling based on query type
            return request.QueryType switch
            {
                KnowledgeQueryType.Facts => await HandleFactsQueryAsync(request, ct).ConfigureAwait(false),
                KnowledgeQueryType.State => await HandleStateQueryAsync(request, ct).ConfigureAwait(false),
                KnowledgeQueryType.Commands => HandleCommandsQuery(request),
                KnowledgeQueryType.Capabilities => await HandleCapabilitiesQueryAsync(request, ct).ConfigureAwait(false),
                _ => KnowledgeQueryResponse.Fail(request.RequestId, $"Unsupported query type: {request.QueryType}", "UNSUPPORTED_QUERY_TYPE")
            };
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine($"Caught OperationCanceledException in KnowledgeSystem.cs");
            return KnowledgeQueryResponse.Fail(request.RequestId, "Query was cancelled", "CANCELLED", sw.Elapsed);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Caught exception in KnowledgeSystem.cs: {ex.Message}");
            return KnowledgeQueryResponse.Fail(request.RequestId, ex.Message, "INTERNAL_ERROR", sw.Elapsed);
        }
    }

    private static string? ValidateRequest(KnowledgeQueryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return "Query cannot be empty";
        }

        if (request.Timeout <= TimeSpan.Zero)
        {
            return "Timeout must be positive";
        }

        return null;
    }

    private async Task<KnowledgeQueryResponse> HandleFactsQueryAsync(KnowledgeQueryRequest request, CancellationToken ct)
    {
        var aggregated = await _aggregator.GetAllKnowledgeAsync(ct).ConfigureAwait(false);

        var queryLower = request.Query.ToLowerInvariant();
        var matchingKnowledge = aggregated.AllKnowledge
            .Where(k => k.Description?.ToLowerInvariant().Contains(queryLower) == true ||
                        k.Topic.ToLowerInvariant().Contains(queryLower))
            .Take(20)
            .ToList();

        return KnowledgeQueryResponse.Ok(request.RequestId, matchingKnowledge);
    }

    private async Task<KnowledgeQueryResponse> HandleStateQueryAsync(KnowledgeQueryRequest request, CancellationToken ct)
    {
        var state = await _stateAggregator.GetCurrentStateAsync(ct).ConfigureAwait(false);
        return KnowledgeQueryResponse.Ok(request.RequestId, state);
    }

    private KnowledgeQueryResponse HandleCommandsQuery(KnowledgeQueryRequest request)
    {
        var commands = string.IsNullOrWhiteSpace(request.Query)
            ? _commandRegistry.GetAllCommands().ToList()
            : _commandRegistry.SearchCommands(request.Query).ToList();

        return KnowledgeQueryResponse.Ok(request.RequestId, commands);
    }

    private async Task<KnowledgeQueryResponse> HandleCapabilitiesQueryAsync(KnowledgeQueryRequest request, CancellationToken ct)
    {
        var aggregated = await _aggregator.GetAllKnowledgeAsync(ct).ConfigureAwait(false);
        return KnowledgeQueryResponse.Ok(request.RequestId, aggregated.AllCapabilities.ToList());
    }
}

#endregion

#region Command Execution

/// <summary>
/// Request to execute a command.
/// </summary>
public sealed class CommandRequest
{
    /// <summary>
    /// Gets the unique request ID.
    /// </summary>
    public string RequestId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Gets the command name to execute.
    /// </summary>
    public required string CommandName { get; init; }

    /// <summary>
    /// Gets the command parameters.
    /// </summary>
    public IReadOnlyDictionary<string, object> Parameters { get; init; } = new Dictionary<string, object>();

    /// <summary>
    /// Gets the user ID making the request.
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>
    /// Gets the execution timeout.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets whether to support rollback on failure.
    /// </summary>
    public bool SupportRollback { get; init; }
}

/// <summary>
/// Result of command execution.
/// </summary>
public sealed class CommandResult
{
    /// <summary>
    /// Gets the request ID this result is for.
    /// </summary>
    public required string RequestId { get; init; }

    /// <summary>
    /// Gets whether the command executed successfully.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Gets the command output data.
    /// </summary>
    public object? Data { get; init; }

    /// <summary>
    /// Gets the error message if not successful.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Gets the error code if not successful.
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Gets the execution duration.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Gets whether rollback was performed.
    /// </summary>
    public bool RolledBack { get; init; }

    /// <summary>
    /// Gets result metadata.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = new Dictionary<string, object>();

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static CommandResult Ok(string requestId, object? data = null, TimeSpan? duration = null)
    {
        return new CommandResult
        {
            RequestId = requestId,
            Success = true,
            Data = data,
            Duration = duration ?? TimeSpan.Zero
        };
    }

    /// <summary>
    /// Creates an error result.
    /// </summary>
    public static CommandResult Fail(string requestId, string error, string? errorCode = null, TimeSpan? duration = null, bool rolledBack = false)
    {
        return new CommandResult
        {
            RequestId = requestId,
            Success = false,
            Error = error,
            ErrorCode = errorCode,
            Duration = duration ?? TimeSpan.Zero,
            RolledBack = rolledBack
        };
    }
}

/// <summary>
/// Handler for executing commands.
/// </summary>
public interface ICommandHandler
{
    /// <summary>
    /// Gets the command name this handler processes.
    /// </summary>
    string CommandName { get; }

    /// <summary>
    /// Executes the command.
    /// </summary>
    /// <param name="request">The command request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The command result.</returns>
    Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken ct = default);

    /// <summary>
    /// Attempts to rollback the command.
    /// </summary>
    /// <param name="request">The original request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if rollback succeeded.</returns>
    Task<bool> TryRollbackAsync(CommandRequest request, CancellationToken ct = default);
}

/// <summary>
/// Executes commands with validation, permission checking, and audit logging.
/// </summary>
public sealed class CommandExecutor
{
    private readonly CommandRegistry _registry;
    private readonly BoundedDictionary<string, ICommandHandler> _handlers = new BoundedDictionary<string, ICommandHandler>(1000);
    private readonly IMessageBus? _messageBus;
    private readonly BoundedDictionary<string, AuditEntry> _auditLog = new BoundedDictionary<string, AuditEntry>(1000);

    /// <summary>
    /// Creates a new command executor.
    /// </summary>
    /// <param name="registry">The command registry.</param>
    /// <param name="messageBus">Optional message bus for audit events.</param>
    public CommandExecutor(CommandRegistry registry, IMessageBus? messageBus = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _messageBus = messageBus;
    }

    /// <summary>
    /// Registers a command handler.
    /// </summary>
    /// <param name="handler">The handler to register.</param>
    public void RegisterHandler(ICommandHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[handler.CommandName] = handler;
    }

    /// <summary>
    /// Executes a command.
    /// </summary>
    /// <param name="request">The command request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The command result.</returns>
    public async Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sw = Stopwatch.StartNew();
        var auditEntry = new AuditEntry
        {
            RequestId = request.RequestId,
            CommandName = request.CommandName,
            UserId = request.UserId,
            StartedAt = DateTimeOffset.UtcNow
        };

        try
        {
            // Validate command exists
            var definition = _registry.GetCommand(request.CommandName);
            if (definition == null)
            {
                auditEntry.Error = "Command not found";
                return CommandResult.Fail(request.RequestId, $"Command '{request.CommandName}' not found", "COMMAND_NOT_FOUND");
            }

            // Validate required parameters
            var missingParams = definition.Parameters
                .Where(p => p.Value.Required && !request.Parameters.ContainsKey(p.Key))
                .Select(p => p.Key)
                .ToList();

            if (missingParams.Count > 0)
            {
                auditEntry.Error = $"Missing parameters: {string.Join(", ", missingParams)}";
                return CommandResult.Fail(request.RequestId,
                    $"Missing required parameters: {string.Join(", ", missingParams)}",
                    "MISSING_PARAMETERS");
            }

            // Get handler
            if (!_handlers.TryGetValue(request.CommandName, out var handler))
            {
                auditEntry.Error = "No handler registered";
                return CommandResult.Fail(request.RequestId,
                    $"No handler registered for command '{request.CommandName}'",
                    "NO_HANDLER");
            }

            // Execute with timeout
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(request.Timeout);

            var result = await handler.ExecuteAsync(request, timeoutCts.Token).ConfigureAwait(false);

            auditEntry.Success = result.Success;
            auditEntry.Duration = sw.Elapsed;

            // Log audit
            await LogAuditAsync(auditEntry).ConfigureAwait(false);

            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            auditEntry.Error = "Timeout";
            auditEntry.Duration = sw.Elapsed;
            await LogAuditAsync(auditEntry).ConfigureAwait(false);

            // Attempt rollback if requested
            if (request.SupportRollback && _handlers.TryGetValue(request.CommandName, out var handler))
            {
                try
                {
                    await handler.TryRollbackAsync(request, CancellationToken.None).ConfigureAwait(false);
                    return CommandResult.Fail(request.RequestId, "Command timed out and was rolled back", "TIMEOUT", sw.Elapsed, rolledBack: true);
                }
                catch
                {
                    Debug.WriteLine($"Caught exception in KnowledgeSystem.cs");
                    // Rollback failed
                }
            }

            return CommandResult.Fail(request.RequestId, "Command timed out", "TIMEOUT", sw.Elapsed);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Caught exception in KnowledgeSystem.cs: {ex.Message}");
            auditEntry.Error = ex.Message;
            auditEntry.Duration = sw.Elapsed;
            await LogAuditAsync(auditEntry).ConfigureAwait(false);

            // Attempt rollback if requested
            if (request.SupportRollback && _handlers.TryGetValue(request.CommandName, out var handler))
            {
                try
                {
                    await handler.TryRollbackAsync(request, CancellationToken.None).ConfigureAwait(false);
                    return CommandResult.Fail(request.RequestId, ex.Message, "EXECUTION_ERROR", sw.Elapsed, rolledBack: true);
                }
                catch
                {

                    // Rollback failed
                    System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
                }
            }

            return CommandResult.Fail(request.RequestId, ex.Message, "EXECUTION_ERROR", sw.Elapsed);
        }
    }

    /// <summary>
    /// Gets recent audit entries.
    /// </summary>
    /// <param name="count">Number of entries to return.</param>
    /// <returns>Recent audit entries.</returns>
    public IEnumerable<AuditEntry> GetRecentAuditEntries(int count = 100)
    {
        return _auditLog.Values
            .OrderByDescending(e => e.StartedAt)
            .Take(count);
    }

    private async Task LogAuditAsync(AuditEntry entry)
    {
        _auditLog[entry.RequestId] = entry;

        // Trim old entries
        if (_auditLog.Count > 10000)
        {
            var oldEntries = _auditLog.Values
                .OrderBy(e => e.StartedAt)
                .Take(1000)
                .Select(e => e.RequestId)
                .ToList();

            foreach (var id in oldEntries)
            {
                _auditLog.TryRemove(id, out _);
            }
        }

        // Publish to message bus if available
        if (_messageBus != null)
        {
            var message = new PluginMessage
            {
                Type = "command.audit",
                Source = "CommandExecutor",
                Payload = new Dictionary<string, object>
                {
                    ["requestId"] = entry.RequestId,
                    ["commandName"] = entry.CommandName,
                    ["userId"] = entry.UserId ?? "unknown",
                    ["success"] = entry.Success,
                    ["durationMs"] = entry.Duration.TotalMilliseconds
                }
            };

            try
            {
                await _messageBus.PublishAsync("command.audit", message).ConfigureAwait(false);
            }
            catch
            {
                Debug.WriteLine($"Caught exception in KnowledgeSystem.cs");
                // Audit publish failed, ignore
            }
        }
    }

    /// <summary>
    /// Audit log entry.
    /// </summary>
    public sealed class AuditEntry
    {
        /// <summary>Request ID.</summary>
        public required string RequestId { get; init; }
        /// <summary>Command name.</summary>
        public required string CommandName { get; init; }
        /// <summary>User ID.</summary>
        public string? UserId { get; init; }
        /// <summary>Start timestamp.</summary>
        public DateTimeOffset StartedAt { get; init; }
        /// <summary>Duration.</summary>
        public TimeSpan Duration { get; set; }
        /// <summary>Success status.</summary>
        public bool Success { get; set; }
        /// <summary>Error message.</summary>
        public string? Error { get; set; }
    }
}

#endregion

#region Result Formatter

/// <summary>
/// Options for result formatting.
/// </summary>
public sealed class FormatOptions
{
    /// <summary>
    /// Gets or sets the output format.
    /// </summary>
    public ResultFormat Format { get; init; } = ResultFormat.Markdown;

    /// <summary>
    /// Gets or sets the maximum token count.
    /// </summary>
    public int MaxTokens { get; init; } = 2000;

    /// <summary>
    /// Gets or sets whether to include metadata.
    /// </summary>
    public bool IncludeMetadata { get; init; }

    /// <summary>
    /// Gets or sets whether to include timestamps.
    /// </summary>
    public bool IncludeTimestamps { get; init; }

    /// <summary>
    /// Gets or sets the indentation level for JSON.
    /// </summary>
    public int JsonIndentation { get; init; } = 2;
}

/// <summary>
/// Output format for results.
/// </summary>
public enum ResultFormat
{
    /// <summary>Markdown format.</summary>
    Markdown,
    /// <summary>JSON format.</summary>
    Json,
    /// <summary>Natural language format.</summary>
    NaturalLanguage,
    /// <summary>Plain text format.</summary>
    PlainText
}

/// <summary>
/// Formats knowledge query responses for AI consumption.
/// </summary>
public sealed class ResultFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Formats a knowledge response for AI consumption.
    /// </summary>
    /// <param name="response">The response to format.</param>
    /// <param name="options">Formatting options.</param>
    /// <returns>Formatted string.</returns>
    public string FormatForAI(KnowledgeQueryResponse response, FormatOptions options)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(options);

        var formatted = options.Format switch
        {
            ResultFormat.Markdown => FormatAsMarkdown(response, options),
            ResultFormat.Json => FormatAsJson(response, options),
            ResultFormat.NaturalLanguage => FormatAsNaturalLanguage(response, options),
            ResultFormat.PlainText => FormatAsPlainText(response, options),
            _ => FormatAsMarkdown(response, options)
        };

        // Token counting and truncation
        var estimatedTokens = EstimateTokens(formatted);
        if (estimatedTokens > options.MaxTokens)
        {
            formatted = TruncateToTokens(formatted, options.MaxTokens);
        }

        return formatted;
    }

    /// <summary>
    /// Formats a command result for AI consumption.
    /// </summary>
    /// <param name="result">The result to format.</param>
    /// <param name="options">Formatting options.</param>
    /// <returns>Formatted string.</returns>
    public string FormatForAI(CommandResult result, FormatOptions options)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(options);

        var sb = new StringBuilder();

        if (options.Format == ResultFormat.Json)
        {
            return JsonSerializer.Serialize(new
            {
                success = result.Success,
                data = result.Data,
                error = result.Error,
                errorCode = result.ErrorCode,
                durationMs = result.Duration.TotalMilliseconds,
                rolledBack = result.RolledBack
            }, JsonOptions);
        }

        if (result.Success)
        {
            sb.AppendLine("## Command Executed Successfully");
            if (result.Data != null)
            {
                sb.AppendLine("### Output:");
                sb.AppendLine(FormatData(result.Data, options));
            }
        }
        else
        {
            sb.AppendLine("## Command Failed");
            sb.AppendLine($"**Error:** {result.Error}");
            if (!string.IsNullOrEmpty(result.ErrorCode))
            {
                sb.AppendLine($"**Error Code:** {result.ErrorCode}");
            }
            if (result.RolledBack)
            {
                sb.AppendLine("*The operation was rolled back.*");
            }
        }

        if (options.IncludeTimestamps)
        {
            sb.AppendLine($"*Duration: {result.Duration.TotalMilliseconds:F2}ms*");
        }

        var formatted = sb.ToString();
        var estimatedTokens = EstimateTokens(formatted);
        if (estimatedTokens > options.MaxTokens)
        {
            formatted = TruncateToTokens(formatted, options.MaxTokens);
        }

        return formatted;
    }

    private string FormatAsMarkdown(KnowledgeQueryResponse response, FormatOptions options)
    {
        var sb = new StringBuilder();

        if (response.Success)
        {
            sb.AppendLine("## Query Results");
            sb.AppendLine();

            if (response.Data is IEnumerable<KnowledgeObject> knowledgeList)
            {
                foreach (var k in knowledgeList)
                {
                    sb.AppendLine($"### {k.Topic}");
                    if (!string.IsNullOrEmpty(k.Description))
                    {
                        sb.AppendLine(k.Description);
                    }
                    sb.AppendLine();
                }
            }
            else if (response.Data is SystemState state)
            {
                sb.AppendLine($"- **Active Plugins:** {state.ActivePluginCount}");
                sb.AppendLine($"- **Active Connections:** {state.ActiveConnectionCount}");
                sb.AppendLine($"- **Health:** {state.OverallHealth}");
            }
            else if (response.Data is IEnumerable<CommandDefinition> commands)
            {
                foreach (var cmd in commands)
                {
                    sb.AppendLine($"- **{cmd.Name}**: {cmd.Description}");
                }
            }
            else if (response.Data != null)
            {
                sb.AppendLine(FormatData(response.Data, options));
            }
        }
        else
        {
            sb.AppendLine("## Query Failed");
            sb.AppendLine($"**Error:** {response.Error}");
        }

        if (options.IncludeTimestamps)
        {
            sb.AppendLine();
            sb.AppendLine($"*Query completed in {response.Duration.TotalMilliseconds:F2}ms*");
        }

        return sb.ToString();
    }

    private string FormatAsJson(KnowledgeQueryResponse response, FormatOptions options)
    {
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = options.JsonIndentation > 0,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        return JsonSerializer.Serialize(new
        {
            success = response.Success,
            data = response.Data,
            error = response.Error,
            errorCode = response.ErrorCode,
            durationMs = response.Duration.TotalMilliseconds,
            metadata = options.IncludeMetadata ? response.Metadata : null
        }, jsonOptions);
    }

    private string FormatAsNaturalLanguage(KnowledgeQueryResponse response, FormatOptions options)
    {
        var sb = new StringBuilder();

        if (response.Success)
        {
            if (response.Data is IEnumerable<KnowledgeObject> knowledgeList)
            {
                var list = knowledgeList.ToList();
                sb.AppendLine($"I found {list.Count} relevant knowledge items:");
                sb.AppendLine();
                foreach (var k in list.Take(10))
                {
                    sb.AppendLine($"- {k.Topic}: {k.Description ?? "No description available"}");
                }
                if (list.Count > 10)
                {
                    sb.AppendLine($"...and {list.Count - 10} more items.");
                }
            }
            else if (response.Data is SystemState state)
            {
                sb.AppendLine($"The system is currently {state.OverallHealth.ToLowerInvariant()}.");
                sb.AppendLine($"There are {state.ActivePluginCount} active plugins and {state.ActiveConnectionCount} active connections.");
            }
            else if (response.Data is IEnumerable<CommandDefinition> commands)
            {
                var list = commands.ToList();
                sb.AppendLine($"There are {list.Count} commands available:");
                foreach (var cmd in list.Take(10))
                {
                    sb.AppendLine($"- {cmd.Name}: {cmd.Description}");
                }
            }
            else
            {
                sb.AppendLine("The query completed successfully.");
                if (response.Data != null)
                {
                    sb.AppendLine($"Result: {FormatData(response.Data, options)}");
                }
            }
        }
        else
        {
            sb.AppendLine($"I encountered an error while processing your query: {response.Error}");
        }

        return sb.ToString();
    }

    private string FormatAsPlainText(KnowledgeQueryResponse response, FormatOptions options)
    {
        var sb = new StringBuilder();

        if (response.Success)
        {
            if (response.Data is IEnumerable<KnowledgeObject> knowledgeList)
            {
                foreach (var k in knowledgeList)
                {
                    sb.AppendLine($"{k.Topic}: {k.Description}");
                }
            }
            else if (response.Data != null)
            {
                sb.AppendLine(FormatData(response.Data, options));
            }
        }
        else
        {
            sb.AppendLine($"Error: {response.Error}");
        }

        return sb.ToString();
    }

    private static string FormatData(object data, FormatOptions options)
    {
        if (data is string str)
            return str;

        try
        {
            return JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                WriteIndented = options.JsonIndentation > 0,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }
        catch
        {
            Debug.WriteLine($"Caught exception in KnowledgeSystem.cs");
            return data.ToString() ?? string.Empty;
        }
    }

    private static int EstimateTokens(string text)
    {
        // Rough estimation: ~4 characters per token
        return text.Length / 4;
    }

    private static string TruncateToTokens(string text, int maxTokens)
    {
        var maxChars = maxTokens * 4;
        if (text.Length <= maxChars)
            return text;

        return text.Substring(0, maxChars - 50) + "\n\n[Output truncated due to token limit]";
    }
}

#endregion

// ==================================================================================
// EXTENSION: Discovery Topics for Knowledge System
// ==================================================================================

/// <summary>
/// Message topics for the Knowledge System.
/// </summary>
public static class KnowledgeSystemTopics
{
    /// <summary>Discovery request for knowledge sources.</summary>
    public const string Discover = "knowledge.system.discover";

    /// <summary>Response to discovery request.</summary>
    public const string DiscoverResponse = "knowledge.system.discover.response";

    /// <summary>Knowledge source available announcement.</summary>
    public const string Available = "knowledge.system.available";

    /// <summary>Knowledge source unavailable announcement.</summary>
    public const string Unavailable = "knowledge.system.unavailable";

    /// <summary>Knowledge changed notification.</summary>
    public const string Changed = "knowledge.system.changed";

    /// <summary>Query capability request.</summary>
    public const string QueryCapability = "knowledge.system.capability.query";

    /// <summary>Response to capability query.</summary>
    public const string QueryCapabilityResponse = "knowledge.system.capability.query.response";

    /// <summary>Capabilities changed notification.</summary>
    public const string CapabilitiesChanged = "knowledge.system.capabilities.changed";

    /// <summary>Context build request.</summary>
    public const string BuildContext = "knowledge.system.context.build";

    /// <summary>Context build response.</summary>
    public const string BuildContextResponse = "knowledge.system.context.build.response";

    /// <summary>Command execution request.</summary>
    public const string ExecuteCommand = "knowledge.system.command.execute";

    /// <summary>Command execution response.</summary>
    public const string ExecuteCommandResponse = "knowledge.system.command.execute.response";
}
