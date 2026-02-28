using System.Diagnostics;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.Indexing;

/// <summary>
/// LDA/BERTopic-style topic modeling index for exabyte-scale memory navigation.
/// Provides automatic topic discovery, topic evolution tracking, and topic-based
/// navigation for AI agents.
///
/// Features:
/// - Automatic topic discovery from content
/// - Topic evolution over time
/// - Cross-topic relationships
/// - Topic-based navigation
/// - Topic summaries for AI understanding
/// </summary>
public sealed class TopicModelIndex : ContextIndexBase
{
    private readonly BoundedDictionary<string, TopicNode> _topics = new BoundedDictionary<string, TopicNode>(1000);
    private readonly BoundedDictionary<string, TopicEntry> _entries = new BoundedDictionary<string, TopicEntry>(1000);
    private readonly BoundedDictionary<string, Dictionary<string, float>> _entryTopicDistribution = new BoundedDictionary<string, Dictionary<string, float>>(1000);
    private readonly BoundedDictionary<string, TopicEvolution> _topicEvolution = new BoundedDictionary<string, TopicEvolution>(1000);
    private readonly BoundedDictionary<(string, string), float> _topicSimilarity = new BoundedDictionary<(string, string), float>(1000);
    // Finding 3167/3168: Inner mutable objects (TopicEvolution.Events, distribution dicts)
    // require a dedicated lock since BoundedDictionary only protects the outer map.
    private readonly object _innerMutationLock = new();

    private const int DefaultTopicCount = 50;
    private const float TopicAssignmentThreshold = 0.1f;
    private const int TopWordsPerTopic = 10;

    /// <inheritdoc/>
    public override string IndexId => "index-topic-model";

    /// <inheritdoc/>
    public override string IndexName => "Topic Model Index";

    /// <summary>
    /// Gets or sets the number of topics to discover.
    /// </summary>
    public int TargetTopicCount { get; set; } = DefaultTopicCount;

    /// <inheritdoc/>
    public override async Task<ContextQueryResult> QueryAsync(ContextQuery query, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var results = new List<IndexedContextEntry>();

            // Find relevant topics first
            var relevantTopics = FindRelevantTopics(query.SemanticQuery, 5);

            // Get entries from relevant topics
            foreach (var topic in relevantTopics)
            {
                var topicEntries = GetEntriesForTopic(topic.TopicId)
                    .Where(e => MatchesFilters(e, query))
                    .Select(e => ScoreEntry(e, query, topic))
                    .Where(e => e.RelevanceScore >= query.MinRelevance);

                results.AddRange(topicEntries);
            }

            // Also search by text for entries not strongly associated with topics
            var queryWords = query.SemanticQuery.ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);

            foreach (var entry in _entries.Values)
            {
                if (results.Any(r => r.ContentId == entry.ContentId))
                    continue;

                if (!MatchesFilters(entry, query))
                    continue;

                var relevance = CalculateTextRelevance(entry, queryWords);
                if (relevance >= query.MinRelevance)
                {
                    results.Add(CreateEntryResult(entry, relevance, query.IncludeHierarchyPath, query.DetailLevel));
                }
            }

            // Sort and limit
            results = results
                .GroupBy(r => r.ContentId)
                .Select(g => g.OrderByDescending(r => r.RelevanceScore).First())
                .OrderByDescending(r => r.RelevanceScore)
                .Take(query.MaxResults)
                .ToList();

            sw.Stop();
            RecordQuery(sw.Elapsed);

            // Generate topic clusters
            var clusters = query.ClusterResults
                ? GenerateTopicClusters(results)
                : null;

            return new ContextQueryResult
            {
                Entries = results,
                NavigationSummary = GenerateTopicNavigationSummary(relevantTopics, results),
                ClusterCounts = clusters,
                TotalMatchingEntries = results.Count,
                QueryDuration = sw.Elapsed,
                RelatedTopics = relevantTopics.SelectMany(t => t.TopWords.Take(3)).Distinct().Take(10).ToArray(),
                WasTruncated = results.Count >= query.MaxResults
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            sw.Stop();
            RecordQuery(sw.Elapsed);
            throw;
        }
    }

    /// <inheritdoc/>
    public override Task<ContextNode?> GetNodeAsync(string nodeId, CancellationToken ct = default)
    {
        if (_topics.TryGetValue(nodeId, out var topic))
        {
            return Task.FromResult<ContextNode?>(TopicToNode(topic));
        }
        return Task.FromResult<ContextNode?>(null);
    }

    /// <inheritdoc/>
    public override Task<IEnumerable<ContextNode>> GetChildrenAsync(string? parentId, int depth = 1, CancellationToken ct = default)
    {
        if (parentId == null)
        {
            // Return all topics
            var nodes = _topics.Values
                .OrderByDescending(t => t.DocumentCount)
                .Take(50)
                .Select(TopicToNode)
                .ToList();

            return Task.FromResult<IEnumerable<ContextNode>>(nodes);
        }

        // Get related topics
        if (_topics.TryGetValue(parentId, out var topic))
        {
            var relatedTopics = GetRelatedTopics(parentId, 10)
                .Where(t => _topics.ContainsKey(t.TopicId))
                .Select(t => TopicToNode(_topics[t.TopicId]))
                .ToList();

            return Task.FromResult<IEnumerable<ContextNode>>(relatedTopics);
        }

        return Task.FromResult<IEnumerable<ContextNode>>(Array.Empty<ContextNode>());
    }

    /// <inheritdoc/>
    public override Task IndexContentAsync(string contentId, byte[] content, ContextMetadata metadata, CancellationToken ct = default)
    {
        var textContent = System.Text.Encoding.UTF8.GetString(content);

        var entry = new TopicEntry
        {
            ContentId = contentId,
            ContentSizeBytes = content.Length,
            Summary = metadata.Summary ?? GenerateSummary(textContent),
            Tags = metadata.Tags ?? Array.Empty<string>(),
            Embedding = metadata.Embedding,
            Tier = metadata.Tier,
            Scope = metadata.Scope ?? "default",
            CreatedAt = metadata.CreatedAt ?? DateTimeOffset.UtcNow,
            ImportanceScore = CalculateImportance(content, metadata),
            Pointer = new ContextPointer
            {
                StorageBackend = "memory",
                Path = $"entries/{contentId}",
                Offset = 0,
                Length = content.Length
            },
            Words = ExtractWords(textContent)
        };

        _entries[contentId] = entry;

        // Assign to topics
        var topicDistribution = AssignToTopics(entry);
        _entryTopicDistribution[contentId] = topicDistribution;

        // Update topic statistics
        foreach (var (topicId, weight) in topicDistribution)
        {
            if (_topics.TryGetValue(topicId, out var topic))
            {
                _topics[topicId] = topic with
                {
                    DocumentCount = topic.DocumentCount + 1,
                    LastUpdated = DateTimeOffset.UtcNow
                };

                // Track evolution
                TrackTopicEvolution(topicId, "document_added", contentId);
            }
        }

        MarkUpdated();

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task UpdateIndexAsync(string contentId, IndexUpdate update, CancellationToken ct = default)
    {
        if (!_entries.TryGetValue(contentId, out var entry))
            return Task.CompletedTask;

        if (update.NewSummary != null)
            entry = entry with { Summary = update.NewSummary };

        if (update.NewTags != null)
            entry = entry with { Tags = update.NewTags };

        if (update.RecordAccess)
        {
            entry = entry with
            {
                AccessCount = entry.AccessCount + 1,
                LastAccessedAt = DateTimeOffset.UtcNow
            };
        }

        _entries[contentId] = entry;
        MarkUpdated();

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task RemoveFromIndexAsync(string contentId, CancellationToken ct = default)
    {
        if (_entries.TryRemove(contentId, out _))
        {
            if (_entryTopicDistribution.TryRemove(contentId, out var distribution))
            {
                foreach (var (topicId, _) in distribution)
                {
                    if (_topics.TryGetValue(topicId, out var topic))
                    {
                        _topics[topicId] = topic with
                        {
                            DocumentCount = topic.DocumentCount - 1,
                            LastUpdated = DateTimeOffset.UtcNow
                        };
                    }
                }
            }
            MarkUpdated();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task<IndexStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        var totalContentBytes = _entries.Values.Sum(e => e.ContentSizeBytes);
        var indexSizeBytes = EstimateIndexSize();

        var stats = GetBaseStatistics(
            _entries.Count,
            totalContentBytes,
            indexSizeBytes,
            _topics.Count,
            1 // Flat topic structure
        );

        var byTier = _entries.Values
            .GroupBy(e => e.Tier)
            .ToDictionary(g => g.Key, g => (long)g.Count());

        return Task.FromResult(stats with
        {
            EntriesByTier = byTier
        });
    }

    /// <inheritdoc/>
    public override async Task OptimizeAsync(CancellationToken ct = default)
    {
        // Recalculate topic model
        await RefitTopicModel(ct);

        // Update topic similarities
        UpdateTopicSimilarities();

        // Prune empty topics
        PruneEmptyTopics();

        await base.OptimizeAsync(ct);
    }

    #region Topic Operations

    /// <summary>
    /// Gets all discovered topics.
    /// </summary>
    /// <returns>List of topics.</returns>
    public IList<TopicInfo> GetTopics()
    {
        return _topics.Values
            .OrderByDescending(t => t.DocumentCount)
            .Select(TopicToInfo)
            .ToList();
    }

    /// <summary>
    /// Gets a specific topic by ID.
    /// </summary>
    /// <param name="topicId">Topic identifier.</param>
    /// <returns>Topic information if found.</returns>
    public TopicInfo? GetTopic(string topicId)
    {
        return _topics.TryGetValue(topicId, out var topic)
            ? TopicToInfo(topic)
            : null;
    }

    /// <summary>
    /// Finds topics relevant to a query.
    /// </summary>
    /// <param name="query">Search query.</param>
    /// <param name="topK">Maximum topics to return.</param>
    /// <returns>Relevant topics.</returns>
    public IList<TopicInfo> FindRelevantTopics(string query, int topK = 5)
    {
        var queryWords = query.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet();

        return _topics.Values
            .Select(t => (Topic: t, Score: CalculateTopicRelevance(t, queryWords)))
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Where(x => x.Score > 0)
            .Select(x => TopicToInfo(x.Topic))
            .ToList();
    }

    /// <summary>
    /// Gets topics related to a given topic.
    /// </summary>
    /// <param name="topicId">Reference topic.</param>
    /// <param name="topK">Maximum topics to return.</param>
    /// <returns>Related topics.</returns>
    public IList<TopicInfo> GetRelatedTopics(string topicId, int topK = 5)
    {
        if (!_topics.ContainsKey(topicId))
            return Array.Empty<TopicInfo>();

        var similarities = _topicSimilarity
            .Where(kvp => kvp.Key.Item1 == topicId || kvp.Key.Item2 == topicId)
            .Select(kvp => (
                OtherTopicId: kvp.Key.Item1 == topicId ? kvp.Key.Item2 : kvp.Key.Item1,
                Similarity: kvp.Value
            ))
            .OrderByDescending(x => x.Similarity)
            .Take(topK);

        return similarities
            .Where(x => _topics.ContainsKey(x.OtherTopicId))
            .Select(x => TopicToInfo(_topics[x.OtherTopicId]))
            .ToList();
    }

    /// <summary>
    /// Gets entries for a topic.
    /// </summary>
    /// <param name="topicId">Topic identifier.</param>
    /// <returns>Entries in the topic.</returns>
    public IEnumerable<TopicEntry> GetEntriesForTopic(string topicId)
    {
        return _entryTopicDistribution
            .Where(kvp => kvp.Value.ContainsKey(topicId) && kvp.Value[topicId] >= TopicAssignmentThreshold)
            .OrderByDescending(kvp => kvp.Value[topicId])
            .Select(kvp => _entries.TryGetValue(kvp.Key, out var entry) ? entry : null)
            .Where(e => e != null)!;
    }

    /// <summary>
    /// Gets the topic distribution for an entry.
    /// </summary>
    /// <param name="contentId">Content identifier.</param>
    /// <returns>Topic distribution.</returns>
    public Dictionary<string, float>? GetEntryTopicDistribution(string contentId)
    {
        return _entryTopicDistribution.TryGetValue(contentId, out var distribution)
            ? distribution
            : null;
    }

    /// <summary>
    /// Gets topic evolution over time.
    /// </summary>
    /// <param name="topicId">Topic identifier.</param>
    /// <returns>Evolution history.</returns>
    public TopicEvolutionInfo? GetTopicEvolution(string topicId)
    {
        if (!_topicEvolution.TryGetValue(topicId, out var evolution))
            return null;

        return new TopicEvolutionInfo
        {
            TopicId = topicId,
            CreatedAt = evolution.CreatedAt,
            EventCount = evolution.Events.Count,
            GrowthTrend = CalculateGrowthTrend(evolution),
            RecentEvents = evolution.Events.TakeLast(10)
                .Select(e => new TopicEventInfo
                {
                    Timestamp = e.Timestamp,
                    EventType = e.EventType,
                    Details = e.Details
                })
                .ToList()
        };
    }

    /// <summary>
    /// Creates a new topic manually.
    /// </summary>
    /// <param name="name">Topic name.</param>
    /// <param name="description">Topic description.</param>
    /// <param name="topWords">Key words for the topic.</param>
    /// <returns>Created topic.</returns>
    public TopicInfo CreateTopic(string name, string description, string[] topWords)
    {
        var topicId = $"topic:{Guid.NewGuid():N}";

        var topic = new TopicNode
        {
            TopicId = topicId,
            Name = name,
            Description = description,
            TopWords = topWords,
            DocumentCount = 0,
            Coherence = 1.0f,
            IsManual = true,
            CreatedAt = DateTimeOffset.UtcNow,
            LastUpdated = DateTimeOffset.UtcNow
        };

        _topics[topicId] = topic;
        _topicEvolution[topicId] = new TopicEvolution
        {
            TopicId = topicId,
            CreatedAt = DateTimeOffset.UtcNow,
            Events = new List<TopicEvent>
            {
                new() { Timestamp = DateTimeOffset.UtcNow, EventType = "created", Details = "Manually created" }
            }
        };

        MarkUpdated();
        return TopicToInfo(topic);
    }

    /// <summary>
    /// Merges two topics.
    /// </summary>
    /// <param name="topicId1">First topic.</param>
    /// <param name="topicId2">Second topic.</param>
    /// <param name="newName">Name for merged topic.</param>
    /// <returns>Merged topic.</returns>
    public TopicInfo? MergeTopics(string topicId1, string topicId2, string newName)
    {
        if (!_topics.TryGetValue(topicId1, out var topic1) ||
            !_topics.TryGetValue(topicId2, out var topic2))
            return null;

        // Create merged topic
        var mergedTopicId = $"topic:{Guid.NewGuid():N}";
        var mergedWords = topic1.TopWords.Concat(topic2.TopWords)
            .GroupBy(w => w)
            .OrderByDescending(g => g.Count())
            .Take(TopWordsPerTopic)
            .Select(g => g.Key)
            .ToArray();

        var mergedTopic = new TopicNode
        {
            TopicId = mergedTopicId,
            Name = newName,
            Description = $"Merged from {topic1.Name} and {topic2.Name}",
            TopWords = mergedWords,
            DocumentCount = topic1.DocumentCount + topic2.DocumentCount,
            Coherence = (topic1.Coherence + topic2.Coherence) / 2,
            IsManual = true,
            CreatedAt = DateTimeOffset.UtcNow,
            LastUpdated = DateTimeOffset.UtcNow
        };

        _topics[mergedTopicId] = mergedTopic;

        // Update entry distributions
        // Finding 3168: Inner Dictionary<string,float> values are not thread-safe; lock mutations.
        foreach (var kvp in _entryTopicDistribution)
        {
            var dist = kvp.Value;
            lock (_innerMutationLock)
            {
                var weight1 = dist.TryGetValue(topicId1, out var w1) ? w1 : 0;
                var weight2 = dist.TryGetValue(topicId2, out var w2) ? w2 : 0;

                if (weight1 > 0 || weight2 > 0)
                {
                    dist.Remove(topicId1);
                    dist.Remove(topicId2);
                    dist[mergedTopicId] = weight1 + weight2;
                }
            }
        }

        // Remove old topics
        _topics.TryRemove(topicId1, out _);
        _topics.TryRemove(topicId2, out _);

        MarkUpdated();
        return TopicToInfo(mergedTopic);
    }

    #endregion

    #region Private Helper Methods

    private Dictionary<string, float> AssignToTopics(TopicEntry entry)
    {
        var distribution = new Dictionary<string, float>();

        // If no topics exist, create initial topic
        if (_topics.IsEmpty)
        {
            var topicId = CreateInitialTopic(entry);
            distribution[topicId] = 1.0f;
            return distribution;
        }

        // Calculate relevance to each topic
        foreach (var topic in _topics.Values)
        {
            var relevance = CalculateTopicRelevance(topic, entry.Words.ToHashSet());
            if (relevance >= TopicAssignmentThreshold)
            {
                distribution[topic.TopicId] = relevance;
            }
        }

        // If no topic matches well, create a new one
        if (distribution.Count == 0 && _topics.Count < TargetTopicCount)
        {
            var topicId = CreateTopicFromEntry(entry);
            distribution[topicId] = 1.0f;
        }
        else if (distribution.Count == 0)
        {
            // Assign to most relevant topic even if below threshold
            var bestTopic = _topics.Values
                .Select(t => (Topic: t, Score: CalculateTopicRelevance(t, entry.Words.ToHashSet())))
                .OrderByDescending(x => x.Score)
                .First();

            distribution[bestTopic.Topic.TopicId] = bestTopic.Score;
        }

        // Normalize
        var total = distribution.Values.Sum();
        if (total > 0)
        {
            foreach (var key in distribution.Keys.ToList())
            {
                distribution[key] /= total;
            }
        }

        return distribution;
    }

    private string CreateInitialTopic(TopicEntry entry)
    {
        var topicId = $"topic:{Guid.NewGuid():N}";
        var topWords = entry.Words
            .GroupBy(w => w)
            .OrderByDescending(g => g.Count())
            .Take(TopWordsPerTopic)
            .Select(g => g.Key)
            .ToArray();

        var topic = new TopicNode
        {
            TopicId = topicId,
            Name = GenerateTopicName(topWords),
            Description = $"Auto-discovered topic: {string.Join(", ", topWords.Take(5))}",
            TopWords = topWords,
            DocumentCount = 1,
            Coherence = 1.0f,
            IsManual = false,
            CreatedAt = DateTimeOffset.UtcNow,
            LastUpdated = DateTimeOffset.UtcNow
        };

        _topics[topicId] = topic;
        InitializeTopicEvolution(topicId);

        return topicId;
    }

    private string CreateTopicFromEntry(TopicEntry entry)
    {
        return CreateInitialTopic(entry);
    }

    private void InitializeTopicEvolution(string topicId)
    {
        _topicEvolution[topicId] = new TopicEvolution
        {
            TopicId = topicId,
            CreatedAt = DateTimeOffset.UtcNow,
            Events = new List<TopicEvent>
            {
                new() { Timestamp = DateTimeOffset.UtcNow, EventType = "created", Details = "Auto-discovered" }
            }
        };
    }

    private void TrackTopicEvolution(string topicId, string eventType, string details)
    {
        if (!_topicEvolution.TryGetValue(topicId, out var evolution))
        {
            InitializeTopicEvolution(topicId);
            evolution = _topicEvolution[topicId];
        }

        // Finding 3167: TopicEvolution.Events is a List<T> — guard all mutations.
        lock (_innerMutationLock)
        {
            evolution.Events.Add(new TopicEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                EventType = eventType,
                Details = details
            });

            // Limit history size
            if (evolution.Events.Count > 1000)
            {
                evolution.Events = evolution.Events.TakeLast(500).ToList();
            }
        }
    }

    private float CalculateTopicRelevance(TopicNode topic, HashSet<string> words)
    {
        if (topic.TopWords.Length == 0 || words.Count == 0)
            return 0f;

        var matchCount = topic.TopWords.Count(tw => words.Contains(tw));
        return (float)matchCount / topic.TopWords.Length;
    }

    private async Task RefitTopicModel(CancellationToken ct)
    {
        // Recalculate topic word distributions based on all entries
        foreach (var topic in _topics.Values)
        {
            var topicEntries = GetEntriesForTopic(topic.TopicId).ToList();
            if (topicEntries.Count == 0) continue;

            var wordCounts = topicEntries
                .SelectMany(e => e.Words)
                .GroupBy(w => w)
                .OrderByDescending(g => g.Count())
                .Take(TopWordsPerTopic)
                .Select(g => g.Key)
                .ToArray();

            _topics[topic.TopicId] = topic with
            {
                TopWords = wordCounts,
                LastUpdated = DateTimeOffset.UtcNow
            };

            TrackTopicEvolution(topic.TopicId, "refit", $"Words updated: {string.Join(", ", wordCounts.Take(5))}");
        }
    }

    private void UpdateTopicSimilarities()
    {
        var topicIds = _topics.Keys.ToList();

        for (int i = 0; i < topicIds.Count; i++)
        {
            for (int j = i + 1; j < topicIds.Count; j++)
            {
                var topic1 = _topics[topicIds[i]];
                var topic2 = _topics[topicIds[j]];

                var overlap = topic1.TopWords.Intersect(topic2.TopWords).Count();
                var similarity = (float)overlap / Math.Max(topic1.TopWords.Length, topic2.TopWords.Length);

                _topicSimilarity[(topicIds[i], topicIds[j])] = similarity;
            }
        }
    }

    private void PruneEmptyTopics()
    {
        var emptyTopics = _topics.Values
            .Where(t => t.DocumentCount == 0 && !t.IsManual)
            .Select(t => t.TopicId)
            .ToList();

        foreach (var topicId in emptyTopics)
        {
            _topics.TryRemove(topicId, out _);
            _topicEvolution.TryRemove(topicId, out _);
        }
    }

    private bool MatchesFilters(TopicEntry entry, ContextQuery query)
    {
        if (query.Scope != null && !entry.Scope.Equals(query.Scope, StringComparison.OrdinalIgnoreCase))
            return false;

        if (query.RequiredTags?.Length > 0)
        {
            if (!query.RequiredTags.All(t => entry.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)))
                return false;
        }

        if (query.TimeRange != null)
        {
            if (query.TimeRange.Start.HasValue && entry.CreatedAt < query.TimeRange.Start.Value)
                return false;
            if (query.TimeRange.End.HasValue && entry.CreatedAt > query.TimeRange.End.Value)
                return false;
        }

        return true;
    }

    private IndexedContextEntry ScoreEntry(TopicEntry entry, ContextQuery query, TopicInfo relevantTopic)
    {
        var topicWeight = _entryTopicDistribution.TryGetValue(entry.ContentId, out var dist)
            ? (dist.TryGetValue(relevantTopic.TopicId, out var w) ? w : 0f)
            : 0f;

        var textRelevance = CalculateTextRelevance(entry, query.SemanticQuery.ToLowerInvariant().Split(' '));
        var relevance = topicWeight * 0.6f + textRelevance * 0.3f + entry.ImportanceScore * 0.1f;

        return CreateEntryResult(entry, relevance, query.IncludeHierarchyPath, query.DetailLevel);
    }

    private float CalculateTextRelevance(TopicEntry entry, string[] queryWords)
    {
        if (queryWords.Length == 0) return 0f;

        var summaryLower = entry.Summary.ToLowerInvariant();
        var matchCount = queryWords.Count(w => summaryLower.Contains(w));
        return (float)matchCount / queryWords.Length;
    }

    private IndexedContextEntry CreateEntryResult(TopicEntry entry, float relevance, bool includePath, SummaryLevel detailLevel)
    {
        string? path = null;
        if (includePath)
        {
            var topTopic = _entryTopicDistribution.TryGetValue(entry.ContentId, out var dist)
                ? dist.OrderByDescending(kvp => kvp.Value).FirstOrDefault().Key
                : null;

            if (topTopic != null && _topics.TryGetValue(topTopic, out var topic))
            {
                path = $"topics/{topic.Name}";
            }
        }

        return new IndexedContextEntry
        {
            ContentId = entry.ContentId,
            HierarchyPath = path,
            RelevanceScore = relevance,
            Summary = TruncateSummary(entry.Summary, detailLevel),
            SemanticTags = entry.Tags,
            ContentSizeBytes = entry.ContentSizeBytes,
            Pointer = entry.Pointer,
            CreatedAt = entry.CreatedAt,
            LastAccessedAt = entry.LastAccessedAt,
            AccessCount = entry.AccessCount,
            ImportanceScore = entry.ImportanceScore,
            Tier = entry.Tier
        };
    }

    private Dictionary<string, int>? GenerateTopicClusters(IList<IndexedContextEntry> results)
    {
        var clusters = new Dictionary<string, int>();

        foreach (var result in results)
        {
            if (_entryTopicDistribution.TryGetValue(result.ContentId, out var dist))
            {
                var topTopic = dist.OrderByDescending(kvp => kvp.Value).FirstOrDefault().Key;
                if (topTopic != null && _topics.TryGetValue(topTopic, out var topic))
                {
                    clusters.TryGetValue(topic.Name, out var count);
                    clusters[topic.Name] = count + 1;
                }
            }
        }

        return clusters.Count > 0 ? clusters : null;
    }

    private string GenerateTopicNavigationSummary(IList<TopicInfo> relevantTopics, IList<IndexedContextEntry> results)
    {
        if (results.Count == 0)
            return "No matching entries found. Try exploring different topics.";

        if (relevantTopics.Count == 0)
            return $"Found {results.Count} entries not strongly associated with any topic.";

        var topicNames = string.Join(", ", relevantTopics.Take(3).Select(t => t.Name));
        return $"Found {results.Count} entries across topics: {topicNames}. " +
               "Navigate by topic for focused exploration.";
    }

    private ContextNode TopicToNode(TopicNode topic)
    {
        return new ContextNode
        {
            NodeId = topic.TopicId,
            ParentId = null,
            Name = topic.Name,
            Summary = topic.Description,
            Level = 0,
            TotalEntryCount = topic.DocumentCount,
            Tags = topic.TopWords,
            LastUpdated = topic.LastUpdated
        };
    }

    private TopicInfo TopicToInfo(TopicNode topic)
    {
        return new TopicInfo
        {
            TopicId = topic.TopicId,
            Name = topic.Name,
            Description = topic.Description,
            TopWords = topic.TopWords,
            DocumentCount = topic.DocumentCount,
            Coherence = topic.Coherence,
            IsManual = topic.IsManual,
            CreatedAt = topic.CreatedAt
        };
    }

    private static string GenerateTopicName(string[] topWords)
    {
        if (topWords.Length == 0) return "Unknown Topic";
        if (topWords.Length == 1) return topWords[0];
        return $"{topWords[0]} & {topWords[1]}";
    }

    private static string[] ExtractWords(string text)
    {
        return text.ToLowerInvariant()
            .Split(new[] { ' ', '\t', '\n', '\r', '.', ',', '!', '?', ';', ':', '"', '\'', '(', ')', '[', ']' },
                   StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && w.Length <= 30)
            .Where(w => !IsStopWord(w))
            .ToArray();
    }

    private static readonly HashSet<string> StopWords = new()
    {
        "the", "a", "an", "and", "or", "but", "in", "on", "at", "to", "for",
        "of", "with", "by", "from", "as", "is", "was", "are", "were", "been",
        "be", "have", "has", "had", "do", "does", "did", "will", "would", "could",
        "should", "may", "might", "must", "can", "this", "that", "these", "those",
        "it", "its", "they", "their", "them", "we", "our", "you", "your", "he",
        "she", "his", "her", "who", "which", "what", "where", "when", "why", "how"
    };

    private static bool IsStopWord(string word) => StopWords.Contains(word);

    private static string GenerateSummary(string text)
    {
        if (text.Length <= 200) return text;
        var cutoff = text.LastIndexOf('.', 200);
        if (cutoff < 50) cutoff = 200;
        return text[..cutoff] + "...";
    }

    private static string TruncateSummary(string summary, SummaryLevel level)
    {
        var maxLength = level switch
        {
            SummaryLevel.Minimal => 50,
            SummaryLevel.Brief => 150,
            SummaryLevel.Detailed => 500,
            SummaryLevel.Full => int.MaxValue,
            _ => 150
        };

        return summary.Length <= maxLength ? summary : summary[..maxLength] + "...";
    }

    private static double CalculateGrowthTrend(TopicEvolution evolution)
    {
        var recent = evolution.Events.Where(e => e.Timestamp >= DateTimeOffset.UtcNow.AddDays(-7)).Count();
        var older = evolution.Events.Where(e => e.Timestamp >= DateTimeOffset.UtcNow.AddDays(-14) &&
                                                 e.Timestamp < DateTimeOffset.UtcNow.AddDays(-7)).Count();

        return older > 0 ? (double)(recent - older) / older : 0;
    }

    private long EstimateIndexSize()
    {
        var topicSize = _topics.Count * 1024;
        var entryIndexSize = _entries.Count * 256;
        var distributionSize = _entryTopicDistribution.Count * 128;
        return topicSize + entryIndexSize + distributionSize;
    }

    #endregion

    #region Internal Types

    private sealed record TopicNode
    {
        public required string TopicId { get; init; }
        public required string Name { get; init; }
        public required string Description { get; init; }
        public string[] TopWords { get; init; } = Array.Empty<string>();
        public long DocumentCount { get; init; }
        public float Coherence { get; init; }
        public bool IsManual { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset LastUpdated { get; init; }
    }

    public sealed record TopicEntry
    {
        public required string ContentId { get; init; }
        public long ContentSizeBytes { get; init; }
        public required string Summary { get; init; }
        public string[] Tags { get; init; } = Array.Empty<string>();
        public float[]? Embedding { get; init; }
        public MemoryTier Tier { get; init; }
        public required string Scope { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? LastAccessedAt { get; init; }
        public int AccessCount { get; init; }
        public float ImportanceScore { get; init; }
        public required ContextPointer Pointer { get; init; }
        public string[] Words { get; init; } = Array.Empty<string>();
    }

    private sealed class TopicEvolution
    {
        public required string TopicId { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public List<TopicEvent> Events { get; set; } = new();
    }

    private sealed record TopicEvent
    {
        public DateTimeOffset Timestamp { get; init; }
        public required string EventType { get; init; }
        public string? Details { get; init; }
    }

    #endregion
}

#region Topic Types

/// <summary>
/// Information about a topic.
/// </summary>
public record TopicInfo
{
    /// <summary>Topic identifier.</summary>
    public required string TopicId { get; init; }

    /// <summary>Topic name.</summary>
    public required string Name { get; init; }

    /// <summary>Topic description.</summary>
    public required string Description { get; init; }

    /// <summary>Top words in this topic.</summary>
    public string[] TopWords { get; init; } = Array.Empty<string>();

    /// <summary>Number of documents in this topic.</summary>
    public long DocumentCount { get; init; }

    /// <summary>Topic coherence score (0-1).</summary>
    public float Coherence { get; init; }

    /// <summary>Whether this topic was manually created.</summary>
    public bool IsManual { get; init; }

    /// <summary>When the topic was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Topic evolution information.
/// </summary>
public record TopicEvolutionInfo
{
    /// <summary>Topic identifier.</summary>
    public required string TopicId { get; init; }

    /// <summary>When the topic was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Number of evolution events.</summary>
    public int EventCount { get; init; }

    /// <summary>Growth trend (positive = growing).</summary>
    public double GrowthTrend { get; init; }

    /// <summary>Recent events.</summary>
    public IList<TopicEventInfo> RecentEvents { get; init; } = Array.Empty<TopicEventInfo>();
}

/// <summary>
/// A topic evolution event.
/// </summary>
public record TopicEventInfo
{
    /// <summary>When the event occurred.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Event type.</summary>
    public required string EventType { get; init; }

    /// <summary>Event details.</summary>
    public string? Details { get; init; }
}

#endregion
