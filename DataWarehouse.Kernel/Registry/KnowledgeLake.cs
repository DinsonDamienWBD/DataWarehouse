using System.Collections.Concurrent;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Kernel.Registry;

/// <summary>
/// Central knowledge storage for AI and caching.
/// Stores static (load-time) and dynamic (runtime) knowledge.
/// </summary>
public sealed class KnowledgeLake : IKnowledgeLake, IDisposable
{
    private readonly BoundedDictionary<string, KnowledgeEntry> _entries = new BoundedDictionary<string, KnowledgeEntry>(1000);
    private readonly BoundedDictionary<string, ConcurrentBag<string>> _byTopic = new BoundedDictionary<string, ConcurrentBag<string>>(1000);
    private readonly BoundedDictionary<string, ConcurrentBag<string>> _byPlugin = new BoundedDictionary<string, ConcurrentBag<string>>(1000);
    private readonly Timer? _cleanupTimer;

    public KnowledgeLake(bool enableAutoCleanup = true)
    {
        if (enableAutoCleanup)
        {
            _cleanupTimer = new Timer(_ => _ = ClearExpiredAsync(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }
    }

    public Task StoreAsync(KnowledgeObject knowledge, bool isStatic = false, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var entry = new KnowledgeEntry
        {
            Knowledge = knowledge,
            StoredAt = DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.UtcNow,
            AccessCount = 0,
            TimeToLive = ttl,
            IsStatic = isStatic
        };

        _entries[knowledge.Id] = entry;
        _byTopic.GetOrAdd(knowledge.Topic, _ => new()).Add(knowledge.Id);
        _byPlugin.GetOrAdd(knowledge.SourcePluginId, _ => new()).Add(knowledge.Id);

        return Task.CompletedTask;
    }

    public async Task StoreBatchAsync(IEnumerable<KnowledgeObject> knowledge, bool isStatic = false, CancellationToken ct = default)
    {
        foreach (var k in knowledge)
        {
            await StoreAsync(k, isStatic, ct: ct);
        }
    }

    public Task RemoveAsync(string knowledgeId, CancellationToken ct = default)
    {
        _entries.TryRemove(knowledgeId, out _);
        return Task.CompletedTask;
    }

    public Task RemoveByPluginAsync(string pluginId, CancellationToken ct = default)
    {
        if (_byPlugin.TryRemove(pluginId, out var ids))
        {
            foreach (var id in ids)
            {
                _entries.TryRemove(id, out _);
            }
        }
        return Task.CompletedTask;
    }

    public Task RemoveByTopicAsync(string topic, CancellationToken ct = default)
    {
        if (_byTopic.TryRemove(topic, out var ids))
        {
            foreach (var id in ids)
            {
                _entries.TryRemove(id, out _);
            }
        }
        return Task.CompletedTask;
    }

    public KnowledgeEntry? Get(string knowledgeId)
    {
        if (_entries.TryGetValue(knowledgeId, out var entry))
        {
            entry.LastAccessedAt = DateTimeOffset.UtcNow;
            entry.AccessCount++;
            return entry;
        }
        return null;
    }

    public IReadOnlyList<KnowledgeEntry> GetByTopic(string topic)
    {
        if (!_byTopic.TryGetValue(topic, out var ids)) return Array.Empty<KnowledgeEntry>();
        return ids.Select(id => Get(id)).Where(e => e != null).Cast<KnowledgeEntry>().ToList();
    }

    public IReadOnlyList<KnowledgeEntry> GetByPlugin(string pluginId)
    {
        if (!_byPlugin.TryGetValue(pluginId, out var ids)) return Array.Empty<KnowledgeEntry>();
        return ids.Select(id => Get(id)).Where(e => e != null).Cast<KnowledgeEntry>().ToList();
    }

    public Task<IReadOnlyList<KnowledgeEntry>> QueryAsync(KnowledgeQuery query, CancellationToken ct = default)
    {
        IEnumerable<KnowledgeEntry> results = _entries.Values;

        if (!string.IsNullOrEmpty(query.SourcePluginId))
            results = results.Where(e => e.Knowledge.SourcePluginId == query.SourcePluginId);
        if (!string.IsNullOrEmpty(query.KnowledgeType))
            results = results.Where(e => e.Knowledge.KnowledgeType.Equals(query.KnowledgeType, StringComparison.OrdinalIgnoreCase));
        if (query.OnlyStatic.HasValue)
            results = results.Where(e => e.IsStatic == query.OnlyStatic.Value);
        if (!string.IsNullOrEmpty(query.TopicPattern))
        {
            var pattern = query.TopicPattern.Replace("*", ".*");
            results = results.Where(e => System.Text.RegularExpressions.Regex.IsMatch(e.Knowledge.Topic, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        }
        if (query.RequiredTags?.Length > 0)
        {
            var tags = query.RequiredTags.Select(t => t.ToLowerInvariant()).ToHashSet();
            results = results.Where(e => tags.All(t => e.Knowledge.Tags.Any(kt => kt.Equals(t, StringComparison.OrdinalIgnoreCase))));
        }
        if (!string.IsNullOrEmpty(query.SearchText))
        {
            var search = query.SearchText.ToLowerInvariant();
            results = results.Where(e =>
                e.Knowledge.Topic.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (e.Knowledge.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var list = results.OrderByDescending(e => e.StoredAt).ToList();
        if (query.Limit.HasValue && query.Limit.Value > 0)
            list = list.Take(query.Limit.Value).ToList();

        // Update access stats
        foreach (var entry in list)
        {
            entry.LastAccessedAt = DateTimeOffset.UtcNow;
            entry.AccessCount++;
        }

        return Task.FromResult<IReadOnlyList<KnowledgeEntry>>(list);
    }

    public IReadOnlyList<KnowledgeEntry> GetAllStatic()
    {
        return _entries.Values.Where(e => e.IsStatic).ToList();
    }

    public IReadOnlyList<KnowledgeEntry> GetRecent(int count = 100)
    {
        return _entries.Values.OrderByDescending(e => e.StoredAt).Take(count).ToList();
    }

    public Task InvalidateAsync(string pluginId, CancellationToken ct = default)
    {
        return RemoveByPluginAsync(pluginId, ct);
    }

    public Task ClearExpiredAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var expired = _entries.Values
            .Where(e => e.TimeToLive.HasValue && (now - e.StoredAt) > e.TimeToLive.Value)
            .Select(e => e.Knowledge.Id)
            .ToList();

        foreach (var id in expired)
        {
            _entries.TryRemove(id, out _);
        }
        return Task.CompletedTask;
    }

    public KnowledgeLakeStatistics GetStatistics()
    {
        var all = _entries.Values.ToList();
        return new KnowledgeLakeStatistics
        {
            TotalEntries = all.Count,
            StaticEntries = all.Count(e => e.IsStatic),
            DynamicEntries = all.Count(e => !e.IsStatic),
            ExpiredEntries = all.Count(e => e.TimeToLive.HasValue && (DateTimeOffset.UtcNow - e.StoredAt) > e.TimeToLive.Value),
            TotalAccessCount = all.Sum(e => e.AccessCount),
            EntriesByPlugin = all.GroupBy(e => e.Knowledge.SourcePluginId).ToDictionary(g => g.Key, g => g.Count()),
            EntriesByTopic = all.GroupBy(e => e.Knowledge.Topic).ToDictionary(g => g.Key, g => g.Count())
        };
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
        _entries.Clear();
        _byTopic.Clear();
        _byPlugin.Clear();
    }
}
