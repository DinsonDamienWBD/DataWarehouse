using System.Collections.Concurrent;
using System.Diagnostics;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Kernel.Registry;

/// <summary>
/// Central registry for plugin capabilities in the DataWarehouse kernel.
/// Provides discovery of "who can do what" across the entire system.
/// Thread-safe and optimized for fast lookups.
/// </summary>
public sealed class PluginCapabilityRegistry : IPluginCapabilityRegistry, IDisposable
{
    private readonly ConcurrentDictionary<string, RegisteredCapability> _capabilities = new();
    private readonly ConcurrentDictionary<CapabilityCategory, ConcurrentBag<string>> _byCategory = new();
    private readonly ConcurrentDictionary<string, ConcurrentBag<string>> _byPlugin = new();
    private readonly ConcurrentDictionary<string, ConcurrentBag<string>> _byTag = new();

    private readonly List<Action<RegisteredCapability>> _onRegistered = new();
    private readonly List<Action<string>> _onUnregistered = new();
    private readonly List<Action<string, bool>> _onAvailabilityChanged = new();
    private readonly object _handlersLock = new();

    private readonly IMessageBus? _messageBus;
    private readonly IDisposable? _registerSubscription;
    private readonly IDisposable? _unregisterSubscription;
    private readonly IDisposable? _querySubscription;

    private long _totalRegistrations;
    private long _totalQueries;

    /// <summary>
    /// Creates a new capability registry.
    /// </summary>
    /// <param name="messageBus">Optional message bus for cross-plugin communication.</param>
    public PluginCapabilityRegistry(IMessageBus? messageBus = null)
    {
        _messageBus = messageBus;

        // Initialize category bags
        foreach (CapabilityCategory category in Enum.GetValues<CapabilityCategory>())
        {
            _byCategory[category] = new ConcurrentBag<string>();
        }

        // Subscribe to message bus topics if available
        if (_messageBus != null)
        {
            _registerSubscription = _messageBus.Subscribe("capability.register", HandleRegisterMessageAsync);
            _unregisterSubscription = _messageBus.Subscribe("capability.unregister", HandleUnregisterMessageAsync);
            _querySubscription = _messageBus.Subscribe("capability.query", HandleQueryMessageAsync);
        }
    }

    #region Registration

    /// <inheritdoc/>
    public Task<bool> RegisterAsync(RegisteredCapability capability, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentException.ThrowIfNullOrWhiteSpace(capability.CapabilityId);

        // IDEMPOTENT: Use AddOrUpdate instead of TryAdd to handle reload scenarios
        // If capability already exists, update it (in case plugin was reloaded)
        var isNew = false;
        _capabilities.AddOrUpdate(
            capability.CapabilityId,
            // Add factory: new capability
            addValueFactory: key =>
            {
                isNew = true;
                Interlocked.Increment(ref _totalRegistrations);
                return capability;
            },
            // Update factory: existing capability (plugin reload)
            updateValueFactory: (key, existing) =>
            {
                // If the plugin ID matches, this is a safe update (same plugin re-registering)
                // If plugin ID differs, this is a conflict - log and replace
                isNew = existing.PluginId != capability.PluginId;
                return capability;
            }
        );

        // Always index on registration (handles both new and update)
        // ConcurrentBag doesn't support removal, so duplicates are OK
        // Discovery methods filter by existence in _capabilities dictionary

        // Index by category
        _byCategory.GetOrAdd(capability.Category, _ => new ConcurrentBag<string>())
            .Add(capability.CapabilityId);

        // Index by plugin
        _byPlugin.GetOrAdd(capability.PluginId, _ => new ConcurrentBag<string>())
            .Add(capability.CapabilityId);

        // Index by tags
        foreach (var tag in capability.Tags)
        {
            _byTag.GetOrAdd(tag.ToLowerInvariant(), _ => new ConcurrentBag<string>())
                .Add(capability.CapabilityId);
        }

        // Always notify (both new and update)
        NotifyRegistered(capability);

        // Publish to message bus
        PublishCapabilityChanged(capability.CapabilityId, isNew ? "registered" : "updated");

        return Task.FromResult(isNew);
    }

    /// <inheritdoc/>
    public async Task<int> RegisterBatchAsync(IEnumerable<RegisteredCapability> capabilities, CancellationToken ct = default)
    {
        var count = 0;
        foreach (var capability in capabilities)
        {
            if (await RegisterAsync(capability, ct))
            {
                count++;
            }
        }
        return count;
    }

    /// <inheritdoc/>
    public Task<bool> UnregisterAsync(string capabilityId, CancellationToken ct = default)
    {
        if (_capabilities.TryRemove(capabilityId, out var capability))
        {
            // Note: ConcurrentBag doesn't support removal, so indexes become stale
            // This is acceptable as GetByXxx methods filter for existence

            NotifyUnregistered(capabilityId);
            PublishCapabilityChanged(capabilityId, "unregistered");

            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public async Task<int> UnregisterPluginAsync(string pluginId, CancellationToken ct = default)
    {
        if (!_byPlugin.TryGetValue(pluginId, out var capabilityIds))
        {
            return 0;
        }

        var count = 0;
        foreach (var capabilityId in capabilityIds.ToArray())
        {
            if (await UnregisterAsync(capabilityId, ct))
            {
                count++;
            }
        }

        // Clean up plugin index
        _byPlugin.TryRemove(pluginId, out _);

        return count;
    }

    /// <inheritdoc/>
    public Task SetPluginAvailabilityAsync(string pluginId, bool isAvailable, CancellationToken ct = default)
    {
        if (!_byPlugin.TryGetValue(pluginId, out var capabilityIds))
        {
            return Task.CompletedTask;
        }

        foreach (var capabilityId in capabilityIds)
        {
            if (_capabilities.TryGetValue(capabilityId, out var capability))
            {
                // Create updated capability (records are immutable)
                var updated = capability with { IsAvailable = isAvailable };
                _capabilities[capabilityId] = updated;

                NotifyAvailabilityChanged(capabilityId, isAvailable);
            }
        }

        PublishCapabilityChanged(pluginId, isAvailable ? "available" : "unavailable");

        return Task.CompletedTask;
    }

    #endregion

    #region Discovery

    /// <inheritdoc/>
    public RegisteredCapability? GetCapability(string capabilityId)
    {
        _capabilities.TryGetValue(capabilityId, out var capability);
        return capability;
    }

    /// <inheritdoc/>
    public bool IsCapabilityAvailable(string capabilityId)
    {
        return _capabilities.TryGetValue(capabilityId, out var capability) && capability.IsAvailable;
    }

    /// <inheritdoc/>
    public Task<CapabilityQueryResult> QueryAsync(CapabilityQuery query, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _totalQueries);
        var sw = Stopwatch.StartNew();

        IEnumerable<RegisteredCapability> results = _capabilities.Values;

        // Apply filters
        if (query.Category.HasValue)
        {
            results = results.Where(c => c.Category == query.Category.Value);
        }

        if (!string.IsNullOrEmpty(query.SubCategory))
        {
            results = results.Where(c =>
                string.Equals(c.SubCategory, query.SubCategory, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(query.PluginId))
        {
            results = results.Where(c => c.PluginId == query.PluginId);
        }

        if (query.OnlyAvailable)
        {
            results = results.Where(c => c.IsAvailable);
        }

        if (query.RequiredTags?.Length > 0)
        {
            var requiredLower = query.RequiredTags.Select(t => t.ToLowerInvariant()).ToHashSet();
            results = results.Where(c =>
                requiredLower.All(tag => c.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase))));
        }

        if (query.AnyOfTags?.Length > 0)
        {
            var anyOfLower = query.AnyOfTags.Select(t => t.ToLowerInvariant()).ToHashSet();
            results = results.Where(c =>
                c.Tags.Any(t => anyOfLower.Contains(t.ToLowerInvariant())));
        }

        if (query.ExcludeTags?.Length > 0)
        {
            var excludeLower = query.ExcludeTags.Select(t => t.ToLowerInvariant()).ToHashSet();
            results = results.Where(c =>
                !c.Tags.Any(t => excludeLower.Contains(t.ToLowerInvariant())));
        }

        if (!string.IsNullOrEmpty(query.SearchText))
        {
            var searchLower = query.SearchText.ToLowerInvariant();
            results = results.Where(c =>
                c.DisplayName.Contains(searchLower, StringComparison.OrdinalIgnoreCase) ||
                (c.Description?.Contains(searchLower, StringComparison.OrdinalIgnoreCase) ?? false) ||
                c.CapabilityId.Contains(searchLower, StringComparison.OrdinalIgnoreCase));
        }

        if (query.MinPriority.HasValue)
        {
            results = results.Where(c => c.Priority >= query.MinPriority.Value);
        }

        // Materialize for counting
        var materialized = results.ToList();
        var totalCount = materialized.Count;

        // Calculate category counts
        var categoryCounts = materialized
            .GroupBy(c => c.Category)
            .ToDictionary(g => g.Key, g => g.Count());

        // Sort
        if (!string.IsNullOrEmpty(query.SortBy))
        {
            materialized = query.SortBy.ToLowerInvariant() switch
            {
                "priority" => query.SortDescending
                    ? materialized.OrderByDescending(c => c.Priority).ToList()
                    : materialized.OrderBy(c => c.Priority).ToList(),
                "name" or "displayname" => query.SortDescending
                    ? materialized.OrderByDescending(c => c.DisplayName).ToList()
                    : materialized.OrderBy(c => c.DisplayName).ToList(),
                "registered" or "registeredat" => query.SortDescending
                    ? materialized.OrderByDescending(c => c.RegisteredAt).ToList()
                    : materialized.OrderBy(c => c.RegisteredAt).ToList(),
                "category" => query.SortDescending
                    ? materialized.OrderByDescending(c => c.Category).ToList()
                    : materialized.OrderBy(c => c.Category).ToList(),
                _ => materialized
            };
        }
        else
        {
            // Default: sort by priority descending
            materialized = materialized.OrderByDescending(c => c.Priority).ToList();
        }

        // Apply limit
        if (query.Limit.HasValue && query.Limit.Value > 0)
        {
            materialized = materialized.Take(query.Limit.Value).ToList();
        }

        sw.Stop();

        return Task.FromResult(new CapabilityQueryResult
        {
            Capabilities = materialized.AsReadOnly(),
            TotalCount = totalCount,
            QueryTime = sw.Elapsed,
            CategoryCounts = categoryCounts
        });
    }

    /// <inheritdoc/>
    public IReadOnlyList<RegisteredCapability> GetByCategory(CapabilityCategory category)
    {
        if (!_byCategory.TryGetValue(category, out var ids))
        {
            return Array.Empty<RegisteredCapability>();
        }

        return ids
            .Select(id => GetCapability(id))
            .Where(c => c != null)
            .Cast<RegisteredCapability>()
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc/>
    public IReadOnlyList<RegisteredCapability> GetByPlugin(string pluginId)
    {
        if (!_byPlugin.TryGetValue(pluginId, out var ids))
        {
            return Array.Empty<RegisteredCapability>();
        }

        return ids
            .Select(id => GetCapability(id))
            .Where(c => c != null)
            .Cast<RegisteredCapability>()
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc/>
    public IReadOnlyList<RegisteredCapability> GetByTags(params string[] tags)
    {
        if (tags.Length == 0)
        {
            return Array.Empty<RegisteredCapability>();
        }

        var firstTag = tags[0].ToLowerInvariant();
        if (!_byTag.TryGetValue(firstTag, out var ids))
        {
            return Array.Empty<RegisteredCapability>();
        }

        var results = ids
            .Select(id => GetCapability(id))
            .Where(c => c != null)
            .Cast<RegisteredCapability>();

        // Filter by remaining tags
        if (tags.Length > 1)
        {
            var remainingTags = tags.Skip(1).Select(t => t.ToLowerInvariant()).ToHashSet();
            results = results.Where(c =>
                remainingTags.All(tag => c.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase))));
        }

        return results.ToList().AsReadOnly();
    }

    /// <inheritdoc/>
    public RegisteredCapability? FindBest(CapabilityCategory category, params string[] requiredTags)
    {
        var candidates = GetByCategory(category)
            .Where(c => c.IsAvailable);

        if (requiredTags.Length > 0)
        {
            var tagsLower = requiredTags.Select(t => t.ToLowerInvariant()).ToHashSet();
            candidates = candidates.Where(c =>
                tagsLower.All(tag => c.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase))));
        }

        return candidates.OrderByDescending(c => c.Priority).FirstOrDefault();
    }

    /// <inheritdoc/>
    public IReadOnlyList<RegisteredCapability> GetAll()
    {
        return _capabilities.Values.ToList().AsReadOnly();
    }

    /// <inheritdoc/>
    public string? GetPluginIdForCapability(string capabilityId)
    {
        return GetCapability(capabilityId)?.PluginId;
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> GetPluginIdsForCategory(CapabilityCategory category)
    {
        return GetByCategory(category)
            .Select(c => c.PluginId)
            .Distinct()
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc/>
    public CapabilityRegistryStatistics GetStatistics()
    {
        var all = _capabilities.Values.ToList();

        var tagCounts = all
            .SelectMany(c => c.Tags)
            .GroupBy(t => t.ToLowerInvariant())
            .Select(g => (Tag: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .Take(20)
            .ToList();

        return new CapabilityRegistryStatistics
        {
            TotalCapabilities = all.Count,
            AvailableCapabilities = all.Count(c => c.IsAvailable),
            RegisteredPlugins = all.Select(c => c.PluginId).Distinct().Count(),
            ByCategory = all.GroupBy(c => c.Category).ToDictionary(g => g.Key, g => g.Count()),
            ByPlugin = all.GroupBy(c => c.PluginId).ToDictionary(g => g.Key, g => g.Count()),
            TopTags = tagCounts
        };
    }

    #endregion

    #region Events

    /// <inheritdoc/>
    public IDisposable OnCapabilityRegistered(Action<RegisteredCapability> handler)
    {
        lock (_handlersLock)
        {
            _onRegistered.Add(handler);
        }
        return new Unsubscriber(() =>
        {
            lock (_handlersLock)
            {
                _onRegistered.Remove(handler);
            }
        });
    }

    /// <inheritdoc/>
    public IDisposable OnCapabilityUnregistered(Action<string> handler)
    {
        lock (_handlersLock)
        {
            _onUnregistered.Add(handler);
        }
        return new Unsubscriber(() =>
        {
            lock (_handlersLock)
            {
                _onUnregistered.Remove(handler);
            }
        });
    }

    /// <inheritdoc/>
    public IDisposable OnAvailabilityChanged(Action<string, bool> handler)
    {
        lock (_handlersLock)
        {
            _onAvailabilityChanged.Add(handler);
        }
        return new Unsubscriber(() =>
        {
            lock (_handlersLock)
            {
                _onAvailabilityChanged.Remove(handler);
            }
        });
    }

    private void NotifyRegistered(RegisteredCapability capability)
    {
        List<Action<RegisteredCapability>> handlers;
        lock (_handlersLock)
        {
            handlers = _onRegistered.ToList();
        }
        foreach (var handler in handlers)
        {
            try { handler(capability); } catch { /* ignore */ }
        }
    }

    private void NotifyUnregistered(string capabilityId)
    {
        List<Action<string>> handlers;
        lock (_handlersLock)
        {
            handlers = _onUnregistered.ToList();
        }
        foreach (var handler in handlers)
        {
            try { handler(capabilityId); } catch { /* ignore */ }
        }
    }

    private void NotifyAvailabilityChanged(string capabilityId, bool isAvailable)
    {
        List<Action<string, bool>> handlers;
        lock (_handlersLock)
        {
            handlers = _onAvailabilityChanged.ToList();
        }
        foreach (var handler in handlers)
        {
            try { handler(capabilityId, isAvailable); } catch { /* ignore */ }
        }
    }

    #endregion

    #region Message Bus Handlers

    private async Task HandleRegisterMessageAsync(PluginMessage message)
    {
        if (message.Payload.TryGetValue("Capability", out var capObj) && capObj is RegisteredCapability capability)
        {
            await RegisterAsync(capability);
        }
    }

    private async Task HandleUnregisterMessageAsync(PluginMessage message)
    {
        if (message.Payload.TryGetValue("CapabilityId", out var idObj) && idObj is string capabilityId)
        {
            await UnregisterAsync(capabilityId);
        }
        else if (message.Payload.TryGetValue("PluginId", out var pluginIdObj) && pluginIdObj is string pluginId)
        {
            await UnregisterPluginAsync(pluginId);
        }
    }

    private async Task HandleQueryMessageAsync(PluginMessage message)
    {
        if (_messageBus == null) return;

        CapabilityQuery? query = null;

        if (message.Payload.TryGetValue("Query", out var queryObj) && queryObj is CapabilityQuery q)
        {
            query = q;
        }
        else
        {
            // Build query from individual parameters
            query = new CapabilityQuery
            {
                Category = message.Payload.TryGetValue("Category", out var cat) && cat is CapabilityCategory c ? c : null,
                PluginId = message.Payload.TryGetValue("PluginId", out var pid) && pid is string p ? p : null,
                RequiredTags = message.Payload.TryGetValue("Tags", out var tags) && tags is string[] t ? t : null,
                SearchText = message.Payload.TryGetValue("Search", out var search) && search is string s ? s : null
            };
        }

        var result = await QueryAsync(query);

        var responseMessage = new PluginMessage
        {
            Type = "capability.query.response",
            CorrelationId = message.CorrelationId,
            Payload = new Dictionary<string, object>
            {
                ["Result"] = result,
                ["TotalCount"] = result.TotalCount
            }
        };

        var responseTopic = $"capability.query.response.{message.CorrelationId}";
        await _messageBus.PublishAsync(responseTopic, responseMessage);
    }

    private void PublishCapabilityChanged(string id, string changeType)
    {
        if (_messageBus == null) return;

        var message = new PluginMessage
        {
            Type = "capability.changed",
            Payload = new Dictionary<string, object>
            {
                ["Id"] = id,
                ["ChangeType"] = changeType,
                ["Timestamp"] = DateTimeOffset.UtcNow
            }
        };

        _ = _messageBus.PublishAsync("capability.changed", message);
    }

    #endregion

    /// <inheritdoc/>
    public void Dispose()
    {
        _registerSubscription?.Dispose();
        _unregisterSubscription?.Dispose();
        _querySubscription?.Dispose();
        _capabilities.Clear();
        _byCategory.Clear();
        _byPlugin.Clear();
        _byTag.Clear();
    }

    private sealed class Unsubscriber : IDisposable
    {
        private readonly Action _unsubscribe;
        public Unsubscriber(Action unsubscribe) => _unsubscribe = unsubscribe;
        public void Dispose() => _unsubscribe();
    }
}
