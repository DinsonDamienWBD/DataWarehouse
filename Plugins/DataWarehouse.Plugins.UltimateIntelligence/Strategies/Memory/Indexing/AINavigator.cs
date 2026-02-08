using System.Diagnostics;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.Indexing;

/// <summary>
/// High-level AI navigation API for context exploration.
/// Provides intuitive methods for AI agents to navigate exabyte-scale memory
/// without needing to understand the underlying index structure.
///
/// This is the primary interface AI agents should use to find relevant context.
/// </summary>
public interface IAINavigator
{
    /// <summary>
    /// AI asks a question, gets exactly what it needs.
    /// </summary>
    /// <param name="question">Natural language question about the context.</param>
    /// <param name="options">Navigation options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Navigation result with answers and pointers.</returns>
    Task<NavigationResult> NavigateAsync(string question, NavigationOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// AI can "zoom" into a specific topic or area.
    /// </summary>
    /// <param name="currentPath">Current location in the hierarchy.</param>
    /// <param name="focusQuery">What to focus on within this area.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Detailed view of the focused area.</returns>
    Task<NavigationResult> DrillDownAsync(string currentPath, string focusQuery, CancellationToken ct = default);

    /// <summary>
    /// AI can "zoom out" to get broader context.
    /// </summary>
    /// <param name="currentPath">Current location in the hierarchy.</param>
    /// <param name="levels">Number of levels to zoom out.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Broader context view.</returns>
    Task<NavigationResult> ZoomOutAsync(string currentPath, int levels = 1, CancellationToken ct = default);

    /// <summary>
    /// AI can get an overview of what's available.
    /// </summary>
    /// <param name="scope">Optional scope to limit the overview.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Overview of available context.</returns>
    Task<ContextOverviewResult> GetOverviewAsync(string? scope = null, CancellationToken ct = default);

    /// <summary>
    /// AI can explore related concepts or entities.
    /// </summary>
    /// <param name="topic">Topic or entity to explore from.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Related topics and navigation suggestions.</returns>
    Task<ExplorationResult> ExploreAsync(string topic, CancellationToken ct = default);

    /// <summary>
    /// AI can get temporal context - what changed recently or over time.
    /// </summary>
    /// <param name="since">Start of time period.</param>
    /// <param name="scope">Optional scope filter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Temporal context summary.</returns>
    Task<TemporalContextResult> GetTemporalContextAsync(DateTimeOffset since, string? scope = null, CancellationToken ct = default);
}

/// <summary>
/// Implementation of the AI Navigator that orchestrates the composite index.
/// </summary>
public sealed class AINavigator : IAINavigator
{
    private readonly CompositeContextIndex _index;
    private readonly HierarchicalSummaryIndex? _hierarchicalIndex;
    private readonly SemanticClusterIndex? _clusterIndex;
    private readonly EntityRelationshipIndex? _entityIndex;
    private readonly TemporalContextIndex? _temporalIndex;
    private readonly TopicModelIndex? _topicIndex;

    /// <summary>
    /// Initializes the AI Navigator with a composite index.
    /// </summary>
    /// <param name="compositeIndex">The composite index to navigate.</param>
    public AINavigator(CompositeContextIndex compositeIndex)
    {
        _index = compositeIndex;

        // Get references to specialized indexes for direct access
        _hierarchicalIndex = _index.Indexes.Values.OfType<HierarchicalSummaryIndex>().FirstOrDefault();
        _clusterIndex = _index.Indexes.Values.OfType<SemanticClusterIndex>().FirstOrDefault();
        _entityIndex = _index.Indexes.Values.OfType<EntityRelationshipIndex>().FirstOrDefault();
        _temporalIndex = _index.Indexes.Values.OfType<TemporalContextIndex>().FirstOrDefault();
        _topicIndex = _index.Indexes.Values.OfType<TopicModelIndex>().FirstOrDefault();
    }

    /// <summary>
    /// Initializes the AI Navigator with an index manager.
    /// </summary>
    /// <param name="manager">The index manager.</param>
    public AINavigator(IndexManager manager) : this(manager.CompositeIndex)
    {
    }

    /// <inheritdoc/>
    public async Task<NavigationResult> NavigateAsync(string question, NavigationOptions? options = null, CancellationToken ct = default)
    {
        options ??= new NavigationOptions();
        var sw = Stopwatch.StartNew();

        // Build context query from question
        var query = new ContextQuery
        {
            SemanticQuery = question,
            MaxResults = options.MaxResults,
            MinRelevance = options.MinRelevance,
            DetailLevel = options.DetailLevel,
            IncludeHierarchyPath = true,
            ClusterResults = true,
            Scope = options.Scope,
            TimeRange = options.TimeRange
        };

        // Execute query
        var result = await _index.QueryAsync(query, ct);

        sw.Stop();

        // Build navigation result
        var answer = GenerateAnswer(question, result, options);
        var suggestions = GenerateSuggestions(question, result);

        return new NavigationResult
        {
            Success = true,
            Answer = answer,
            RelevantPointers = result.Entries.Select(e => e.Pointer).ToList(),
            EntrySummaries = result.Entries.Select(e => new EntrySummary
            {
                ContentId = e.ContentId,
                Summary = e.Summary ?? "",
                Relevance = e.RelevanceScore,
                Path = e.HierarchyPath
            }).ToList(),
            SuggestedNextQuery = suggestions.FirstOrDefault(),
            SuggestedQueries = suggestions,
            RelatedTopics = result.RelatedTopics,
            NavigationHints = GenerateNavigationHints(result),
            QueryDuration = sw.Elapsed,
            TotalMatchingEntries = result.TotalMatchingEntries
        };
    }

    /// <inheritdoc/>
    public async Task<NavigationResult> DrillDownAsync(string currentPath, string focusQuery, CancellationToken ct = default)
    {
        if (_hierarchicalIndex == null)
        {
            return new NavigationResult
            {
                Success = false,
                ErrorMessage = "Hierarchical navigation is not available."
            };
        }

        var drillResult = await _hierarchicalIndex.DrillDownAsync(currentPath, focusQuery, ct);

        if (!drillResult.Success)
        {
            return new NavigationResult
            {
                Success = false,
                ErrorMessage = drillResult.ErrorMessage
            };
        }

        return new NavigationResult
        {
            Success = true,
            Answer = drillResult.CurrentNode != null
                ? $"Drilled down to '{drillResult.CurrentNode.Name}': {drillResult.CurrentNode.Summary}"
                : "Drilled down successfully.",
            RelevantPointers = drillResult.RelevantEntries?
                .Select(e => e.Pointer)
                .ToList() ?? new List<ContextPointer>(),
            EntrySummaries = drillResult.RelevantEntries?
                .Select(e => new EntrySummary
                {
                    ContentId = e.ContentId,
                    Summary = e.Summary ?? "",
                    Relevance = e.RelevanceScore,
                    Path = e.HierarchyPath
                })
                .ToList() ?? new List<EntrySummary>(),
            SuggestedNextQuery = drillResult.SuggestedPaths?.FirstOrDefault(),
            SuggestedQueries = drillResult.SuggestedPaths,
            NavigationHints = new[]
            {
                drillResult.CanDrillDeeper
                    ? "You can drill deeper into sub-topics."
                    : "You've reached the most detailed level.",
                $"Found {drillResult.MatchingSubNodes?.Count ?? 0} matching sub-areas."
            }
        };
    }

    /// <inheritdoc/>
    public async Task<NavigationResult> ZoomOutAsync(string currentPath, int levels = 1, CancellationToken ct = default)
    {
        if (_hierarchicalIndex == null)
        {
            return new NavigationResult
            {
                Success = false,
                ErrorMessage = "Hierarchical navigation is not available."
            };
        }

        // Find current node
        var pathParts = currentPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var currentNodeId = await FindNodeIdByPath(pathParts, ct);

        if (currentNodeId == null)
        {
            return new NavigationResult
            {
                Success = false,
                ErrorMessage = $"Path not found: {currentPath}"
            };
        }

        var zoomResult = await _hierarchicalIndex.ZoomOutAsync(currentNodeId, levels, ct);

        if (!zoomResult.Success)
        {
            return new NavigationResult
            {
                Success = false,
                ErrorMessage = zoomResult.ErrorMessage
            };
        }

        return new NavigationResult
        {
            Success = true,
            Answer = zoomResult.BroaderSummary,
            RelatedTopics = zoomResult.SiblingNodes?
                .Select(n => n.Name)
                .ToArray(),
            NavigationHints = new[]
            {
                $"Zoomed out {levels} level(s) to broader context.",
                $"There are {zoomResult.SiblingNodes?.Count ?? 0} sibling areas at this level."
            }
        };
    }

    /// <inheritdoc/>
    public async Task<ContextOverviewResult> GetOverviewAsync(string? scope = null, CancellationToken ct = default)
    {
        // Get statistics from all indexes
        var stats = await _index.GetStatisticsAsync(ct);

        // Get hierarchical overview if available
        ContextOverview? hierarchyOverview = null;
        if (_hierarchicalIndex != null)
        {
            hierarchyOverview = await _hierarchicalIndex.GetOverviewAsync(2, ct);
        }

        // Get top topics if available
        IList<TopicInfo>? topTopics = null;
        if (_topicIndex != null)
        {
            topTopics = _topicIndex.GetTopics().Take(10).ToList();
        }

        // Get top entities if available
        IList<EntityInfo>? topEntities = null;
        if (_entityIndex != null)
        {
            topEntities = _entityIndex.GetTopEntities(10);
        }

        // Get recent activity if available
        RecentActivitySummary? recentActivity = null;
        if (_temporalIndex != null)
        {
            recentActivity = _temporalIndex.GetRecentActivity(24);
        }

        // Build scope summaries
        var scopeSummaries = new List<ScopeSummary>();
        if (stats.EntriesByScope != null)
        {
            foreach (var (scopeName, count) in stats.EntriesByScope)
            {
                if (scope != null && !scopeName.Equals(scope, StringComparison.OrdinalIgnoreCase))
                    continue;

                scopeSummaries.Add(new ScopeSummary
                {
                    Scope = scopeName,
                    EntryCount = count,
                    Description = $"Content in the {scopeName} domain."
                });
            }
        }

        return new ContextOverviewResult
        {
            TotalEntries = stats.TotalEntries,
            TotalContentBytes = stats.TotalContentBytes,
            LastUpdated = stats.LastUpdated,
            RootSummary = hierarchyOverview?.RootSummary ?? $"Context index with {stats.TotalEntries} entries.",
            Scopes = scopeSummaries,
            TopDomains = hierarchyOverview?.TopLevelDomains?
                .Select(d => new DomainSummary
                {
                    Name = d.Name,
                    EntryCount = d.TotalEntryCount,
                    Summary = d.Summary
                })
                .ToList() ?? new List<DomainSummary>(),
            TopTopics = topTopics?
                .Select(t => new TopicSummaryInfo
                {
                    Name = t.Name,
                    DocumentCount = t.DocumentCount,
                    TopWords = t.TopWords.Take(5).ToArray()
                })
                .ToList() ?? new List<TopicSummaryInfo>(),
            TopEntities = topEntities?
                .Select(e => new EntitySummaryInfo
                {
                    Name = e.Name,
                    Type = e.EntityType ?? "unknown",
                    ContentCount = e.ContentCount,
                    Importance = e.ImportanceScore
                })
                .ToList() ?? new List<EntitySummaryInfo>(),
            RecentActivitySummary = recentActivity != null
                ? $"{recentActivity.EntriesCreated} created, {recentActivity.EntriesModified} modified, {recentActivity.EntriesAccessed} accessed in the last {recentActivity.HoursAnalyzed} hours."
                : null,
            NavigationSuggestions = GenerateOverviewSuggestions(hierarchyOverview, topTopics, topEntities)
        };
    }

    /// <inheritdoc/>
    public async Task<ExplorationResult> ExploreAsync(string topic, CancellationToken ct = default)
    {
        var explorations = new List<string>();
        var relatedConcepts = new List<RelatedConceptInfo>();

        // Explore via entities if available
        if (_entityIndex != null)
        {
            var entities = _entityIndex.FindEntities(topic, 5);
            foreach (var entity in entities)
            {
                var relationships = _entityIndex.GetRelationships(entity.EntityId);
                foreach (var rel in relationships.Take(3))
                {
                    relatedConcepts.Add(new RelatedConceptInfo
                    {
                        Concept = rel.OtherEntityName ?? rel.OtherEntityId,
                        RelationType = rel.RelationshipType,
                        Strength = rel.Weight,
                        Source = "entity-graph"
                    });
                }
            }
        }

        // Explore via topics if available
        if (_topicIndex != null)
        {
            var relevantTopics = _topicIndex.FindRelevantTopics(topic, 5);
            foreach (var topicInfo in relevantTopics)
            {
                foreach (var word in topicInfo.TopWords.Take(3))
                {
                    if (!relatedConcepts.Any(c => c.Concept.Equals(word, StringComparison.OrdinalIgnoreCase)))
                    {
                        relatedConcepts.Add(new RelatedConceptInfo
                        {
                            Concept = word,
                            RelationType = "topic-related",
                            Strength = 0.7f,
                            Source = "topic-model"
                        });
                    }
                }

                var related = _topicIndex.GetRelatedTopics(topicInfo.TopicId, 3);
                foreach (var relTopic in related)
                {
                    explorations.Add($"Explore topic: {relTopic.Name}");
                }
            }
        }

        // Explore via clusters if available
        if (_clusterIndex != null)
        {
            // Get semantic neighbors
            var queryResult = await _clusterIndex.QueryAsync(new ContextQuery
            {
                SemanticQuery = topic,
                MaxResults = 5,
                MinRelevance = 0.5f
            }, ct);

            foreach (var entry in queryResult.Entries)
            {
                if (entry.SemanticTags != null)
                {
                    foreach (var tag in entry.SemanticTags.Take(2))
                    {
                        if (!relatedConcepts.Any(c => c.Concept.Equals(tag, StringComparison.OrdinalIgnoreCase)))
                        {
                            relatedConcepts.Add(new RelatedConceptInfo
                            {
                                Concept = tag,
                                RelationType = "co-occurs",
                                Strength = entry.RelevanceScore * 0.5f,
                                Source = "semantic-cluster"
                            });
                        }
                    }
                }
            }
        }

        // Build exploration suggestions
        explorations.AddRange(relatedConcepts
            .OrderByDescending(c => c.Strength)
            .Take(5)
            .Select(c => $"Explore: {c.Concept} ({c.RelationType})"));

        return new ExplorationResult
        {
            OriginalTopic = topic,
            RelatedConcepts = relatedConcepts
                .OrderByDescending(c => c.Strength)
                .Take(20)
                .ToList(),
            SuggestedExplorations = explorations.Distinct().Take(10).ToArray(),
            ConceptMap = BuildConceptMap(topic, relatedConcepts)
        };
    }

    /// <inheritdoc/>
    public async Task<TemporalContextResult> GetTemporalContextAsync(DateTimeOffset since, string? scope = null, CancellationToken ct = default)
    {
        if (_temporalIndex == null)
        {
            return new TemporalContextResult
            {
                Since = since,
                Summary = "Temporal indexing is not available."
            };
        }

        var changes = _temporalIndex.GetChangesSince(since);
        var activity = _temporalIndex.GetRecentActivity((int)(DateTimeOffset.UtcNow - since).TotalHours);

        // Get trend analysis
        var hours = (int)(DateTimeOffset.UtcNow - since).TotalHours;
        var windowSize = hours > 168 ? TimeSpan.FromDays(1) : TimeSpan.FromHours(6);
        var windowCount = Math.Min(20, hours / (int)windowSize.TotalHours);
        var trends = _temporalIndex.DetectTrends(windowSize, Math.Max(2, windowCount));

        return new TemporalContextResult
        {
            Since = since,
            Summary = $"Since {since:yyyy-MM-dd HH:mm}: {changes.TotalChanges} total changes " +
                     $"({changes.CreatedEntries.Count} created, {changes.ModifiedEntries.Count} modified).",
            CreatedCount = changes.CreatedEntries.Count,
            ModifiedCount = changes.ModifiedEntries.Count,
            AccessedCount = changes.AccessedEntries.Count,
            TopCreated = changes.CreatedEntries.Take(10)
                .Select(e => new TemporalEntryBrief
                {
                    ContentId = e.ContentId,
                    Summary = e.Summary,
                    Timestamp = e.CreatedAt
                })
                .ToList(),
            TopModified = changes.ModifiedEntries.Take(10)
                .Select(e => new TemporalEntryBrief
                {
                    ContentId = e.ContentId,
                    Summary = e.Summary,
                    Timestamp = e.ModifiedAt
                })
                .ToList(),
            Trends = new TrendInfo
            {
                GrowthRate = trends.OverallGrowthRate,
                IsGrowing = trends.IsGrowing,
                IsDeclining = trends.IsDeclining,
                EmergingTopics = trends.EmergingTopics,
                PeakActivityTime = trends.PeakWindow?.WindowStart
            },
            ActivityByPeriod = trends.Windows.Select(w => new PeriodActivity
            {
                PeriodStart = w.WindowStart,
                PeriodEnd = w.WindowEnd,
                EntryCount = w.EntryCount,
                TopTags = w.TopTags
            }).ToList()
        };
    }

    #region Private Helper Methods

    private async Task<string?> FindNodeIdByPath(string[] pathParts, CancellationToken ct)
    {
        if (_hierarchicalIndex == null || pathParts.Length == 0)
            return null;

        // Navigate through hierarchy
        var currentId = "root";
        foreach (var part in pathParts)
        {
            var children = await _hierarchicalIndex.GetChildrenAsync(currentId, 1, ct);
            var match = children.FirstOrDefault(c =>
                c.Name.Equals(part, StringComparison.OrdinalIgnoreCase));

            if (match == null)
                return null;

            currentId = match.NodeId;
        }

        return currentId;
    }

    private string GenerateAnswer(string question, ContextQueryResult result, NavigationOptions options)
    {
        if (result.Entries.Count == 0)
        {
            return $"No relevant information found for: '{question}'. " +
                   "Try broadening your search or exploring related topics.";
        }

        var topEntry = result.Entries.First();
        var answerParts = new List<string>();

        // Include top result summary as primary answer
        if (!string.IsNullOrEmpty(topEntry.Summary))
        {
            answerParts.Add(topEntry.Summary);
        }

        // Add navigation context
        if (result.Entries.Count > 1)
        {
            answerParts.Add($"Found {result.TotalMatchingEntries} related items.");
        }

        if (result.ClusterCounts?.Count > 0)
        {
            var topClusters = result.ClusterCounts
                .OrderByDescending(kvp => kvp.Value)
                .Take(3)
                .Select(kvp => kvp.Key);
            answerParts.Add($"Topics: {string.Join(", ", topClusters)}.");
        }

        return string.Join(" ", answerParts);
    }

    private string[] GenerateSuggestions(string question, ContextQueryResult result)
    {
        var suggestions = new List<string>();

        if (result.SuggestedQueries != null)
        {
            suggestions.AddRange(result.SuggestedQueries);
        }

        if (result.RelatedTopics?.Length > 0)
        {
            suggestions.AddRange(result.RelatedTopics
                .Take(3)
                .Select(t => $"Tell me about {t}"));
        }

        if (result.ClusterCounts?.Count > 0)
        {
            var topCluster = result.ClusterCounts.OrderByDescending(kvp => kvp.Value).First().Key;
            suggestions.Add($"Drill down into {topCluster}");
        }

        return suggestions.Distinct().Take(5).ToArray();
    }

    private string[] GenerateNavigationHints(ContextQueryResult result)
    {
        var hints = new List<string>();

        if (result.WasTruncated)
        {
            hints.Add("Results were truncated. Refine your query for more specific results.");
        }

        if (result.ClusterCounts?.Count > 3)
        {
            hints.Add("Results span multiple topics. Consider focusing on a specific cluster.");
        }

        if (!string.IsNullOrEmpty(result.NavigationSummary))
        {
            hints.Add(result.NavigationSummary);
        }

        return hints.ToArray();
    }

    private string[] GenerateOverviewSuggestions(
        ContextOverview? hierarchy,
        IList<TopicInfo>? topics,
        IList<EntityInfo>? entities)
    {
        var suggestions = new List<string>();

        if (hierarchy?.TopLevelDomains?.Count > 0)
        {
            suggestions.Add($"Explore domain: {hierarchy.TopLevelDomains.First().Name}");
        }

        if (topics?.Count > 0)
        {
            suggestions.Add($"Explore topic: {topics.First().Name}");
        }

        if (entities?.Count > 0)
        {
            suggestions.Add($"Learn about entity: {entities.First().Name}");
        }

        suggestions.Add("Get recent changes");
        suggestions.Add("Search for specific content");

        return suggestions.ToArray();
    }

    private ConceptMapNode BuildConceptMap(string centerTopic, IList<RelatedConceptInfo> related)
    {
        return new ConceptMapNode
        {
            Concept = centerTopic,
            IsCenter = true,
            Children = related
                .GroupBy(r => r.RelationType)
                .Select(g => new ConceptMapNode
                {
                    Concept = g.Key,
                    IsCenter = false,
                    Children = g.Select(c => new ConceptMapNode
                    {
                        Concept = c.Concept,
                        Weight = c.Strength,
                        IsCenter = false
                    }).ToList()
                })
                .ToList()
        };
    }

    #endregion
}

#region Navigation Types

/// <summary>
/// Options for navigation queries.
/// </summary>
public record NavigationOptions
{
    /// <summary>Maximum results to return.</summary>
    public int MaxResults { get; init; } = 20;

    /// <summary>Minimum relevance threshold.</summary>
    public float MinRelevance { get; init; } = 0.3f;

    /// <summary>Level of detail for summaries.</summary>
    public SummaryLevel DetailLevel { get; init; } = SummaryLevel.Brief;

    /// <summary>Optional scope filter.</summary>
    public string? Scope { get; init; }

    /// <summary>Optional time range filter.</summary>
    public TimeRange? TimeRange { get; init; }

    /// <summary>Whether to include related concepts.</summary>
    public bool IncludeRelated { get; init; } = true;
}

/// <summary>
/// Result of a navigation query.
/// </summary>
public record NavigationResult
{
    /// <summary>Whether the navigation succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Error message if failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Direct answer to the question if possible.</summary>
    public string? Answer { get; init; }

    /// <summary>Pointers to relevant content for reading more.</summary>
    public IList<ContextPointer> RelevantPointers { get; init; } = Array.Empty<ContextPointer>();

    /// <summary>Brief summaries of relevant entries.</summary>
    public IList<EntrySummary> EntrySummaries { get; init; } = Array.Empty<EntrySummary>();

    /// <summary>Suggested next query if more specificity is needed.</summary>
    public string? SuggestedNextQuery { get; init; }

    /// <summary>Multiple suggested queries for exploration.</summary>
    public string[]? SuggestedQueries { get; init; }

    /// <summary>Related topics that might be relevant.</summary>
    public string[]? RelatedTopics { get; init; }

    /// <summary>Hints for navigating the results.</summary>
    public string[]? NavigationHints { get; init; }

    /// <summary>Time taken for the query.</summary>
    public TimeSpan QueryDuration { get; init; }

    /// <summary>Total matching entries (may exceed returned).</summary>
    public long TotalMatchingEntries { get; init; }
}

/// <summary>
/// Brief summary of an entry.
/// </summary>
public record EntrySummary
{
    /// <summary>Content identifier.</summary>
    public required string ContentId { get; init; }

    /// <summary>Brief summary.</summary>
    public required string Summary { get; init; }

    /// <summary>Relevance score.</summary>
    public float Relevance { get; init; }

    /// <summary>Hierarchy path.</summary>
    public string? Path { get; init; }
}

/// <summary>
/// Overview of available context.
/// </summary>
public record ContextOverviewResult
{
    /// <summary>Total number of entries.</summary>
    public long TotalEntries { get; init; }

    /// <summary>Total content size in bytes.</summary>
    public long TotalContentBytes { get; init; }

    /// <summary>When the context was last updated.</summary>
    public DateTimeOffset LastUpdated { get; init; }

    /// <summary>Root summary of all context.</summary>
    public required string RootSummary { get; init; }

    /// <summary>Available scopes/domains.</summary>
    public IList<ScopeSummary> Scopes { get; init; } = Array.Empty<ScopeSummary>();

    /// <summary>Top-level domains.</summary>
    public IList<DomainSummary> TopDomains { get; init; } = Array.Empty<DomainSummary>();

    /// <summary>Top topics.</summary>
    public IList<TopicSummaryInfo> TopTopics { get; init; } = Array.Empty<TopicSummaryInfo>();

    /// <summary>Top entities.</summary>
    public IList<EntitySummaryInfo> TopEntities { get; init; } = Array.Empty<EntitySummaryInfo>();

    /// <summary>Summary of recent activity.</summary>
    public string? RecentActivitySummary { get; init; }

    /// <summary>Suggested navigation paths.</summary>
    public string[]? NavigationSuggestions { get; init; }
}

/// <summary>
/// Summary of a scope.
/// </summary>
public record ScopeSummary
{
    /// <summary>Scope name.</summary>
    public required string Scope { get; init; }

    /// <summary>Number of entries in this scope.</summary>
    public long EntryCount { get; init; }

    /// <summary>Brief description.</summary>
    public string? Description { get; init; }
}

/// <summary>
/// Summary of a domain.
/// </summary>
public record DomainSummary
{
    /// <summary>Domain name.</summary>
    public required string Name { get; init; }

    /// <summary>Number of entries.</summary>
    public long EntryCount { get; init; }

    /// <summary>Domain summary.</summary>
    public required string Summary { get; init; }
}

/// <summary>
/// Brief topic information.
/// </summary>
public record TopicSummaryInfo
{
    /// <summary>Topic name.</summary>
    public required string Name { get; init; }

    /// <summary>Number of documents.</summary>
    public long DocumentCount { get; init; }

    /// <summary>Key words.</summary>
    public string[] TopWords { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Brief entity information.
/// </summary>
public record EntitySummaryInfo
{
    /// <summary>Entity name.</summary>
    public required string Name { get; init; }

    /// <summary>Entity type.</summary>
    public required string Type { get; init; }

    /// <summary>Number of content items.</summary>
    public long ContentCount { get; init; }

    /// <summary>Importance score.</summary>
    public float Importance { get; init; }
}

/// <summary>
/// Result of exploration.
/// </summary>
public record ExplorationResult
{
    /// <summary>The original topic explored.</summary>
    public required string OriginalTopic { get; init; }

    /// <summary>Related concepts discovered.</summary>
    public IList<RelatedConceptInfo> RelatedConcepts { get; init; } = Array.Empty<RelatedConceptInfo>();

    /// <summary>Suggested explorations.</summary>
    public string[] SuggestedExplorations { get; init; } = Array.Empty<string>();

    /// <summary>Concept map centered on the topic.</summary>
    public ConceptMapNode? ConceptMap { get; init; }
}

/// <summary>
/// Information about a related concept.
/// </summary>
public record RelatedConceptInfo
{
    /// <summary>The related concept.</summary>
    public required string Concept { get; init; }

    /// <summary>Type of relationship.</summary>
    public required string RelationType { get; init; }

    /// <summary>Strength of the relationship (0-1).</summary>
    public float Strength { get; init; }

    /// <summary>Source of this relationship.</summary>
    public string? Source { get; init; }
}

/// <summary>
/// Node in a concept map.
/// </summary>
public record ConceptMapNode
{
    /// <summary>The concept.</summary>
    public required string Concept { get; init; }

    /// <summary>Whether this is the center node.</summary>
    public bool IsCenter { get; init; }

    /// <summary>Weight/importance of this node.</summary>
    public float Weight { get; init; } = 1.0f;

    /// <summary>Child nodes.</summary>
    public IList<ConceptMapNode> Children { get; init; } = Array.Empty<ConceptMapNode>();
}

/// <summary>
/// Result of temporal context query.
/// </summary>
public record TemporalContextResult
{
    /// <summary>Start of the time period.</summary>
    public DateTimeOffset Since { get; init; }

    /// <summary>Summary of temporal context.</summary>
    public required string Summary { get; init; }

    /// <summary>Number of entries created.</summary>
    public int CreatedCount { get; init; }

    /// <summary>Number of entries modified.</summary>
    public int ModifiedCount { get; init; }

    /// <summary>Number of entries accessed.</summary>
    public int AccessedCount { get; init; }

    /// <summary>Top created entries.</summary>
    public IList<TemporalEntryBrief> TopCreated { get; init; } = Array.Empty<TemporalEntryBrief>();

    /// <summary>Top modified entries.</summary>
    public IList<TemporalEntryBrief> TopModified { get; init; } = Array.Empty<TemporalEntryBrief>();

    /// <summary>Trend information.</summary>
    public TrendInfo? Trends { get; init; }

    /// <summary>Activity by time period.</summary>
    public IList<PeriodActivity> ActivityByPeriod { get; init; } = Array.Empty<PeriodActivity>();
}

/// <summary>
/// Brief temporal entry information.
/// </summary>
public record TemporalEntryBrief
{
    /// <summary>Content identifier.</summary>
    public required string ContentId { get; init; }

    /// <summary>Entry summary.</summary>
    public required string Summary { get; init; }

    /// <summary>Relevant timestamp.</summary>
    public DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// Trend information.
/// </summary>
public record TrendInfo
{
    /// <summary>Overall growth rate.</summary>
    public double GrowthRate { get; init; }

    /// <summary>Whether content is growing.</summary>
    public bool IsGrowing { get; init; }

    /// <summary>Whether content is declining.</summary>
    public bool IsDeclining { get; init; }

    /// <summary>Emerging topics.</summary>
    public string[] EmergingTopics { get; init; } = Array.Empty<string>();

    /// <summary>Peak activity time.</summary>
    public DateTimeOffset? PeakActivityTime { get; init; }
}

/// <summary>
/// Activity in a time period.
/// </summary>
public record PeriodActivity
{
    /// <summary>Period start.</summary>
    public DateTimeOffset PeriodStart { get; init; }

    /// <summary>Period end.</summary>
    public DateTimeOffset PeriodEnd { get; init; }

    /// <summary>Number of entries.</summary>
    public int EntryCount { get; init; }

    /// <summary>Top tags in this period.</summary>
    public string[] TopTags { get; init; } = Array.Empty<string>();
}

#endregion
