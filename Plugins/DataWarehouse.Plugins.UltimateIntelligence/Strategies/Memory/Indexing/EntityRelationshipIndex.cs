using System.Diagnostics;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.Indexing;

/// <summary>
/// Knowledge graph index for exabyte-scale memory navigation.
/// Extracts entities from all content, tracks relationships between entities,
/// and provides graph-based querying and traversal capabilities.
///
/// Features:
/// - Automatic entity extraction from content
/// - Relationship tracking between entities
/// - Query by entity and relationship type
/// - Traverse relationship paths
/// - Entity importance scoring based on connectivity
/// </summary>
public sealed class EntityRelationshipIndex : ContextIndexBase
{
    private readonly BoundedDictionary<string, EntityNode> _entities = new BoundedDictionary<string, EntityNode>(1000);
    private readonly BoundedDictionary<string, RelationshipEdge> _relationships = new BoundedDictionary<string, RelationshipEdge>(1000);
    private readonly BoundedDictionary<string, HashSet<string>> _entityToContent = new BoundedDictionary<string, HashSet<string>>(1000);
    private readonly BoundedDictionary<string, HashSet<string>> _contentToEntities = new BoundedDictionary<string, HashSet<string>>(1000);
    private readonly BoundedDictionary<string, ContentEntry> _content = new BoundedDictionary<string, ContentEntry>(1000);
    private readonly BoundedDictionary<string, HashSet<string>> _outgoingEdges = new BoundedDictionary<string, HashSet<string>>(1000);
    private readonly BoundedDictionary<string, HashSet<string>> _incomingEdges = new BoundedDictionary<string, HashSet<string>>(1000);

    private static readonly Regex EntityPattern = new(@"\b[A-Z][a-z]+(?:\s+[A-Z][a-z]+)*\b", RegexOptions.Compiled);
    private static readonly string[] CommonRelationships = { "related_to", "part_of", "contains", "references", "similar_to", "depends_on" };

    /// <inheritdoc/>
    public override string IndexId => "index-entity-relationship";

    /// <inheritdoc/>
    public override string IndexName => "Entity Relationship Index";

    /// <inheritdoc/>
    public override async Task<ContextQueryResult> QueryAsync(ContextQuery query, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var results = new List<IndexedContextEntry>();
            var queryEntities = ExtractEntities(query.SemanticQuery);

            // Search by entity matching
            var matchingContent = new HashSet<string>();

            foreach (var entity in queryEntities)
            {
                // Find entities that match the query
                var matchingEntities = _entities.Values
                    .Where(e => e.Name.Contains(entity, StringComparison.OrdinalIgnoreCase) ||
                               e.Aliases.Any(a => a.Contains(entity, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                foreach (var matchedEntity in matchingEntities)
                {
                    if (_entityToContent.TryGetValue(matchedEntity.EntityId, out var contentIds))
                    {
                        foreach (var contentId in contentIds)
                        {
                            matchingContent.Add(contentId);
                        }
                    }
                }
            }

            // Also search content summaries
            var queryLower = query.SemanticQuery.ToLowerInvariant();
            foreach (var content in _content.Values)
            {
                if (content.Summary.Contains(queryLower, StringComparison.OrdinalIgnoreCase))
                {
                    matchingContent.Add(content.ContentId);
                }
            }

            // Build results
            foreach (var contentId in matchingContent)
            {
                if (!_content.TryGetValue(contentId, out var content))
                    continue;

                if (!MatchesFilters(content, query))
                    continue;

                var relevance = CalculateRelevance(content, queryEntities, query);
                if (relevance < query.MinRelevance)
                    continue;

                results.Add(CreateEntryResult(content, relevance, query.IncludeHierarchyPath, query.DetailLevel));
            }

            // Sort and limit
            results = results
                .OrderByDescending(r => r.RelevanceScore)
                .Take(query.MaxResults)
                .ToList();

            sw.Stop();
            RecordQuery(sw.Elapsed);

            return new ContextQueryResult
            {
                Entries = results,
                NavigationSummary = GenerateEntityNavigationSummary(queryEntities, results),
                TotalMatchingEntries = results.Count,
                QueryDuration = sw.Elapsed,
                RelatedTopics = GetRelatedEntities(queryEntities, 10),
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
        if (_entities.TryGetValue(nodeId, out var entity))
        {
            return Task.FromResult<ContextNode?>(EntityToNode(entity));
        }
        return Task.FromResult<ContextNode?>(null);
    }

    /// <inheritdoc/>
    public override Task<IEnumerable<ContextNode>> GetChildrenAsync(string? parentId, int depth = 1, CancellationToken ct = default)
    {
        if (parentId == null)
        {
            // Return top entities by importance
            var topEntities = _entities.Values
                .OrderByDescending(e => e.ImportanceScore)
                .Take(100)
                .Select(EntityToNode)
                .ToList();

            return Task.FromResult<IEnumerable<ContextNode>>(topEntities);
        }

        // Get related entities
        var related = GetRelatedEntitiesFromGraph(parentId, depth);
        var nodes = related
            .Where(e => _entities.ContainsKey(e))
            .Select(e => EntityToNode(_entities[e]))
            .ToList();

        return Task.FromResult<IEnumerable<ContextNode>>(nodes);
    }

    /// <inheritdoc/>
    public override async Task IndexContentAsync(string contentId, byte[] content, ContextMetadata metadata, CancellationToken ct = default)
    {
        var textContent = System.Text.Encoding.UTF8.GetString(content);

        // Extract entities
        var extractedEntities = ExtractEntities(textContent);
        var providedEntities = metadata.Entities ?? Array.Empty<string>();
        var allEntities = extractedEntities.Concat(providedEntities).Distinct().ToList();

        // Create content entry
        var entry = new ContentEntry
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
            }
        };

        _content[contentId] = entry;
        _contentToEntities[contentId] = new HashSet<string>();

        // Register entities
        foreach (var entityName in allEntities)
        {
            var entityId = NormalizeEntityId(entityName);
            await RegisterEntity(entityId, entityName, contentId, ct);
        }

        // Infer relationships between entities mentioned in same content
        await InferRelationships(allEntities, contentId, ct);

        MarkUpdated();
    }

    /// <inheritdoc/>
    public override Task UpdateIndexAsync(string contentId, IndexUpdate update, CancellationToken ct = default)
    {
        if (!_content.TryGetValue(contentId, out var content))
            return Task.CompletedTask;

        if (update.NewSummary != null)
            content = content with { Summary = update.NewSummary };

        if (update.NewTags != null)
            content = content with { Tags = update.NewTags };

        if (update.RecordAccess)
        {
            content = content with
            {
                AccessCount = content.AccessCount + 1,
                LastAccessedAt = DateTimeOffset.UtcNow
            };

            // Boost importance of accessed entities
            if (_contentToEntities.TryGetValue(contentId, out var entityIds))
            {
                foreach (var entityId in entityIds)
                {
                    if (_entities.TryGetValue(entityId, out var entity))
                    {
                        _entities[entityId] = entity with
                        {
                            AccessCount = entity.AccessCount + 1,
                            ImportanceScore = RecalculateEntityImportance(entity)
                        };
                    }
                }
            }
        }

        _content[contentId] = content;
        MarkUpdated();

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task RemoveFromIndexAsync(string contentId, CancellationToken ct = default)
    {
        if (_content.TryRemove(contentId, out _))
        {
            // Remove entity links
            if (_contentToEntities.TryRemove(contentId, out var entityIds))
            {
                foreach (var entityId in entityIds)
                {
                    if (_entityToContent.TryGetValue(entityId, out var contentIds))
                    {
                        contentIds.Remove(contentId);

                        // Recalculate entity importance
                        if (_entities.TryGetValue(entityId, out var entity))
                        {
                            _entities[entityId] = entity with
                            {
                                ContentCount = entity.ContentCount - 1,
                                ImportanceScore = RecalculateEntityImportance(entity with { ContentCount = entity.ContentCount - 1 })
                            };
                        }
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
        var totalContentBytes = _content.Values.Sum(c => c.ContentSizeBytes);
        var indexSizeBytes = EstimateIndexSize();

        var stats = GetBaseStatistics(
            _content.Count,
            totalContentBytes,
            indexSizeBytes,
            _entities.Count,
            0 // Flat structure
        );

        var byTier = _content.Values
            .GroupBy(c => c.Tier)
            .ToDictionary(g => g.Key, g => (long)g.Count());

        return Task.FromResult(stats with
        {
            EntriesByTier = byTier
        });
    }

    #region Entity and Relationship Operations

    /// <summary>
    /// Gets an entity by its identifier.
    /// </summary>
    /// <param name="entityId">The entity identifier.</param>
    /// <returns>The entity if found.</returns>
    public EntityInfo? GetEntity(string entityId)
    {
        return _entities.TryGetValue(entityId, out var entity)
            ? new EntityInfo
            {
                EntityId = entity.EntityId,
                Name = entity.Name,
                EntityType = entity.EntityType,
                ImportanceScore = entity.ImportanceScore,
                ContentCount = entity.ContentCount,
                RelationshipCount = CountRelationships(entityId)
            }
            : null;
    }

    /// <summary>
    /// Finds entities by name pattern.
    /// </summary>
    /// <param name="pattern">Search pattern.</param>
    /// <param name="limit">Maximum results.</param>
    /// <returns>Matching entities.</returns>
    public IList<EntityInfo> FindEntities(string pattern, int limit = 50)
    {
        return _entities.Values
            .Where(e => e.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
                       e.Aliases.Any(a => a.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(e => e.ImportanceScore)
            .Take(limit)
            .Select(e => new EntityInfo
            {
                EntityId = e.EntityId,
                Name = e.Name,
                EntityType = e.EntityType,
                ImportanceScore = e.ImportanceScore,
                ContentCount = e.ContentCount,
                RelationshipCount = CountRelationships(e.EntityId)
            })
            .ToList();
    }

    /// <summary>
    /// Gets relationships for an entity.
    /// </summary>
    /// <param name="entityId">The entity identifier.</param>
    /// <param name="relationshipType">Optional filter by relationship type.</param>
    /// <param name="direction">Relationship direction filter.</param>
    /// <returns>Matching relationships.</returns>
    public IList<RelationshipInfo> GetRelationships(string entityId, string? relationshipType = null, RelationshipDirection direction = RelationshipDirection.Both)
    {
        var results = new List<RelationshipInfo>();

        if (direction != RelationshipDirection.Incoming)
        {
            if (_outgoingEdges.TryGetValue(entityId, out var outgoing))
            {
                foreach (var edgeId in outgoing)
                {
                    if (_relationships.TryGetValue(edgeId, out var edge))
                    {
                        if (relationshipType == null || edge.RelationshipType == relationshipType)
                        {
                            results.Add(EdgeToRelationship(edge, "outgoing"));
                        }
                    }
                }
            }
        }

        if (direction != RelationshipDirection.Outgoing)
        {
            if (_incomingEdges.TryGetValue(entityId, out var incoming))
            {
                foreach (var edgeId in incoming)
                {
                    if (_relationships.TryGetValue(edgeId, out var edge))
                    {
                        if (relationshipType == null || edge.RelationshipType == relationshipType)
                        {
                            results.Add(EdgeToRelationship(edge, "incoming"));
                        }
                    }
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Finds a path between two entities.
    /// </summary>
    /// <param name="fromEntityId">Source entity.</param>
    /// <param name="toEntityId">Target entity.</param>
    /// <param name="maxDepth">Maximum path length.</param>
    /// <returns>Path if found.</returns>
    public EntityPath? FindPath(string fromEntityId, string toEntityId, int maxDepth = 6)
    {
        if (!_entities.ContainsKey(fromEntityId) || !_entities.ContainsKey(toEntityId))
            return null;

        // BFS to find shortest path
        var visited = new HashSet<string>();
        var queue = new Queue<(string EntityId, List<string> Path)>();
        queue.Enqueue((fromEntityId, new List<string> { fromEntityId }));

        while (queue.Count > 0)
        {
            var (current, path) = queue.Dequeue();

            if (current == toEntityId)
            {
                return new EntityPath
                {
                    FromEntityId = fromEntityId,
                    ToEntityId = toEntityId,
                    Entities = path.Select(id => _entities[id].Name).ToList(),
                    Relationships = GetPathRelationships(path),
                    PathLength = path.Count - 1
                };
            }

            if (path.Count > maxDepth || visited.Contains(current))
                continue;

            visited.Add(current);

            foreach (var neighbor in GetNeighbors(current))
            {
                if (!visited.Contains(neighbor))
                {
                    var newPath = new List<string>(path) { neighbor };
                    queue.Enqueue((neighbor, newPath));
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Adds an explicit relationship between entities.
    /// </summary>
    /// <param name="fromEntityId">Source entity.</param>
    /// <param name="toEntityId">Target entity.</param>
    /// <param name="relationshipType">Type of relationship.</param>
    /// <param name="properties">Optional properties.</param>
    /// <returns>Relationship identifier.</returns>
    public string AddRelationship(string fromEntityId, string toEntityId, string relationshipType, Dictionary<string, object>? properties = null)
    {
        var edgeId = $"rel:{fromEntityId}:{relationshipType}:{toEntityId}";

        var edge = new RelationshipEdge
        {
            EdgeId = edgeId,
            FromEntityId = fromEntityId,
            ToEntityId = toEntityId,
            RelationshipType = relationshipType,
            Weight = 1.0f,
            Properties = properties ?? new Dictionary<string, object>(),
            CreatedAt = DateTimeOffset.UtcNow,
            LastAccessedAt = null,
            AccessCount = 0
        };

        _relationships[edgeId] = edge;

        if (!_outgoingEdges.ContainsKey(fromEntityId))
            _outgoingEdges[fromEntityId] = new HashSet<string>();
        _outgoingEdges[fromEntityId].Add(edgeId);

        if (!_incomingEdges.ContainsKey(toEntityId))
            _incomingEdges[toEntityId] = new HashSet<string>();
        _incomingEdges[toEntityId].Add(edgeId);

        MarkUpdated();
        return edgeId;
    }

    /// <summary>
    /// Gets top entities by importance.
    /// </summary>
    /// <param name="limit">Maximum results.</param>
    /// <param name="entityType">Optional type filter.</param>
    /// <returns>Top entities.</returns>
    public IList<EntityInfo> GetTopEntities(int limit = 50, string? entityType = null)
    {
        var query = _entities.Values.AsEnumerable();

        if (entityType != null)
            query = query.Where(e => e.EntityType == entityType);

        return query
            .OrderByDescending(e => e.ImportanceScore)
            .Take(limit)
            .Select(e => new EntityInfo
            {
                EntityId = e.EntityId,
                Name = e.Name,
                EntityType = e.EntityType,
                ImportanceScore = e.ImportanceScore,
                ContentCount = e.ContentCount,
                RelationshipCount = CountRelationships(e.EntityId)
            })
            .ToList();
    }

    /// <summary>
    /// Gets entities that are central to the knowledge graph (high connectivity).
    /// </summary>
    /// <param name="limit">Maximum results.</param>
    /// <returns>Central entities.</returns>
    public IList<EntityInfo> GetCentralEntities(int limit = 20)
    {
        return _entities.Values
            .Select(e => new
            {
                Entity = e,
                Centrality = CalculateCentrality(e.EntityId)
            })
            .OrderByDescending(x => x.Centrality)
            .Take(limit)
            .Select(x => new EntityInfo
            {
                EntityId = x.Entity.EntityId,
                Name = x.Entity.Name,
                EntityType = x.Entity.EntityType,
                ImportanceScore = x.Entity.ImportanceScore,
                ContentCount = x.Entity.ContentCount,
                RelationshipCount = CountRelationships(x.Entity.EntityId),
                CentralityScore = x.Centrality
            })
            .ToList();
    }

    #endregion

    #region Private Helper Methods

    private async Task RegisterEntity(string entityId, string entityName, string contentId, CancellationToken ct)
    {
        if (!_entities.ContainsKey(entityId))
        {
            _entities[entityId] = new EntityNode
            {
                EntityId = entityId,
                Name = entityName,
                EntityType = InferEntityType(entityName),
                Aliases = new List<string>(),
                ContentCount = 1,
                AccessCount = 0,
                ImportanceScore = 0.5f,
                CreatedAt = DateTimeOffset.UtcNow,
                LastUpdated = DateTimeOffset.UtcNow
            };
            _entityToContent[entityId] = new HashSet<string>();
        }
        else
        {
            var entity = _entities[entityId];
            _entities[entityId] = entity with
            {
                ContentCount = entity.ContentCount + 1,
                ImportanceScore = RecalculateEntityImportance(entity with { ContentCount = entity.ContentCount + 1 })
            };
        }

        _entityToContent[entityId].Add(contentId);
        _contentToEntities[contentId].Add(entityId);
    }

    private async Task InferRelationships(List<string> entityNames, string contentId, CancellationToken ct)
    {
        // Entities mentioned in the same content are likely related
        for (int i = 0; i < entityNames.Count; i++)
        {
            for (int j = i + 1; j < entityNames.Count; j++)
            {
                var fromId = NormalizeEntityId(entityNames[i]);
                var toId = NormalizeEntityId(entityNames[j]);

                // Check if relationship already exists
                var edgeId = $"rel:{fromId}:co_occurs:{toId}";
                var reverseEdgeId = $"rel:{toId}:co_occurs:{fromId}";

                if (!_relationships.ContainsKey(edgeId) && !_relationships.ContainsKey(reverseEdgeId))
                {
                    AddRelationship(fromId, toId, "co_occurs", new Dictionary<string, object>
                    {
                        ["source_content"] = contentId
                    });
                }
                else
                {
                    // Increase weight for existing relationship
                    if (_relationships.TryGetValue(edgeId, out var edge))
                    {
                        _relationships[edgeId] = edge with { Weight = edge.Weight + 0.1f };
                    }
                }
            }
        }
    }

    private static List<string> ExtractEntities(string text)
    {
        var matches = EntityPattern.Matches(text);
        return matches
            .Select(m => m.Value)
            .Where(e => e.Length >= 3 && e.Length <= 50)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToList();
    }

    private static string NormalizeEntityId(string entityName)
    {
        return entityName.ToLowerInvariant().Replace(" ", "_");
    }

    private static string InferEntityType(string entityName)
    {
        if (entityName.EndsWith("Company") || entityName.EndsWith("Corp") || entityName.EndsWith("Inc"))
            return "organization";
        if (entityName.Contains("@") || entityName.StartsWith("http"))
            return "reference";
        return "concept";
    }

    private float RecalculateEntityImportance(EntityNode entity)
    {
        var baseScore = 0.3f;

        // Content count factor
        baseScore += Math.Min(entity.ContentCount * 0.05f, 0.3f);

        // Relationship count factor
        var relCount = CountRelationships(entity.EntityId);
        baseScore += Math.Min(relCount * 0.03f, 0.2f);

        // Access count factor
        baseScore += Math.Min(entity.AccessCount * 0.01f, 0.1f);

        return Math.Clamp(baseScore, 0f, 1f);
    }

    private int CountRelationships(string entityId)
    {
        var count = 0;
        if (_outgoingEdges.TryGetValue(entityId, out var outgoing))
            count += outgoing.Count;
        if (_incomingEdges.TryGetValue(entityId, out var incoming))
            count += incoming.Count;
        return count;
    }

    private float CalculateCentrality(string entityId)
    {
        // Simple degree centrality
        return CountRelationships(entityId) / (float)Math.Max(1, _relationships.Count);
    }

    private IEnumerable<string> GetNeighbors(string entityId)
    {
        var neighbors = new HashSet<string>();

        if (_outgoingEdges.TryGetValue(entityId, out var outgoing))
        {
            foreach (var edgeId in outgoing)
            {
                if (_relationships.TryGetValue(edgeId, out var edge))
                {
                    neighbors.Add(edge.ToEntityId);
                }
            }
        }

        if (_incomingEdges.TryGetValue(entityId, out var incoming))
        {
            foreach (var edgeId in incoming)
            {
                if (_relationships.TryGetValue(edgeId, out var edge))
                {
                    neighbors.Add(edge.FromEntityId);
                }
            }
        }

        return neighbors;
    }

    private HashSet<string> GetRelatedEntitiesFromGraph(string entityId, int depth)
    {
        var result = new HashSet<string>();
        var visited = new HashSet<string>();
        var queue = new Queue<(string Id, int Depth)>();

        queue.Enqueue((entityId, 0));

        while (queue.Count > 0)
        {
            var (current, currentDepth) = queue.Dequeue();

            if (visited.Contains(current) || currentDepth > depth)
                continue;

            visited.Add(current);

            foreach (var neighbor in GetNeighbors(current))
            {
                result.Add(neighbor);
                if (currentDepth < depth)
                {
                    queue.Enqueue((neighbor, currentDepth + 1));
                }
            }
        }

        return result;
    }

    private List<string> GetPathRelationships(List<string> path)
    {
        var relationships = new List<string>();

        for (int i = 0; i < path.Count - 1; i++)
        {
            var from = path[i];
            var to = path[i + 1];

            if (_outgoingEdges.TryGetValue(from, out var edges))
            {
                foreach (var edgeId in edges)
                {
                    if (_relationships.TryGetValue(edgeId, out var edge) && edge.ToEntityId == to)
                    {
                        relationships.Add(edge.RelationshipType);
                        break;
                    }
                }
            }
        }

        return relationships;
    }

    private bool MatchesFilters(ContentEntry content, ContextQuery query)
    {
        if (query.Scope != null && !content.Scope.Equals(query.Scope, StringComparison.OrdinalIgnoreCase))
            return false;

        if (query.RequiredTags?.Length > 0)
        {
            if (!query.RequiredTags.All(t => content.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)))
                return false;
        }

        if (query.TimeRange != null)
        {
            if (query.TimeRange.Start.HasValue && content.CreatedAt < query.TimeRange.Start.Value)
                return false;
            if (query.TimeRange.End.HasValue && content.CreatedAt > query.TimeRange.End.Value)
                return false;
        }

        return true;
    }

    private float CalculateRelevance(ContentEntry content, List<string> queryEntities, ContextQuery query)
    {
        var relevance = 0f;

        // Entity overlap
        if (_contentToEntities.TryGetValue(content.ContentId, out var contentEntities))
        {
            var overlap = queryEntities.Count(qe =>
                contentEntities.Any(ce => _entities.TryGetValue(ce, out var entity) &&
                    entity.Name.Contains(qe, StringComparison.OrdinalIgnoreCase)));

            relevance += queryEntities.Count > 0 ? (float)overlap / queryEntities.Count * 0.5f : 0f;
        }

        // Text matching
        var queryLower = query.SemanticQuery.ToLowerInvariant();
        if (content.Summary.Contains(queryLower, StringComparison.OrdinalIgnoreCase))
            relevance += 0.3f;

        // Importance boost
        relevance += content.ImportanceScore * 0.2f;

        return Math.Clamp(relevance, 0f, 1f);
    }

    private IndexedContextEntry CreateEntryResult(ContentEntry content, float relevance, bool includePath, SummaryLevel detailLevel)
    {
        string? path = null;
        if (includePath && _contentToEntities.TryGetValue(content.ContentId, out var entityIds))
        {
            var topEntity = entityIds
                .Where(id => _entities.ContainsKey(id))
                .OrderByDescending(id => _entities[id].ImportanceScore)
                .FirstOrDefault();

            if (topEntity != null && _entities.TryGetValue(topEntity, out var entity))
            {
                path = $"entities/{entity.EntityType}/{entity.Name}";
            }
        }

        return new IndexedContextEntry
        {
            ContentId = content.ContentId,
            HierarchyPath = path,
            RelevanceScore = relevance,
            Summary = TruncateSummary(content.Summary, detailLevel),
            SemanticTags = content.Tags,
            ContentSizeBytes = content.ContentSizeBytes,
            Pointer = content.Pointer,
            CreatedAt = content.CreatedAt,
            LastAccessedAt = content.LastAccessedAt,
            AccessCount = content.AccessCount,
            ImportanceScore = content.ImportanceScore,
            Tier = content.Tier
        };
    }

    private string GenerateEntityNavigationSummary(List<string> queryEntities, IList<IndexedContextEntry> results)
    {
        if (results.Count == 0)
            return "No content found for the specified entities. Try exploring related entities.";

        var foundEntities = new HashSet<string>();
        foreach (var result in results)
        {
            if (_contentToEntities.TryGetValue(result.ContentId, out var entityIds))
            {
                foreach (var id in entityIds.Take(3))
                {
                    if (_entities.TryGetValue(id, out var entity))
                        foundEntities.Add(entity.Name);
                }
            }
        }

        var entityList = string.Join(", ", foundEntities.Take(5));
        return $"Found {results.Count} content items related to entities: {entityList}. " +
               "Use entity traversal to explore related concepts.";
    }

    private string[]? GetRelatedEntities(List<string> queryEntities, int limit)
    {
        var related = new HashSet<string>();

        foreach (var entityName in queryEntities)
        {
            var entityId = NormalizeEntityId(entityName);
            var neighbors = GetNeighbors(entityId);

            foreach (var neighborId in neighbors)
            {
                if (_entities.TryGetValue(neighborId, out var neighbor))
                {
                    related.Add(neighbor.Name);
                }
            }
        }

        return related.Take(limit).ToArray();
    }

    private ContextNode EntityToNode(EntityNode entity)
    {
        return new ContextNode
        {
            NodeId = entity.EntityId,
            ParentId = null,
            Name = entity.Name,
            Summary = $"Entity '{entity.Name}' ({entity.EntityType}) appears in {entity.ContentCount} content items with {CountRelationships(entity.EntityId)} relationships.",
            Level = 0,
            ChildCount = CountRelationships(entity.EntityId),
            TotalEntryCount = entity.ContentCount,
            Tags = new[] { entity.EntityType },
            LastUpdated = entity.LastUpdated
        };
    }

    private RelationshipInfo EdgeToRelationship(RelationshipEdge edge, string direction)
    {
        string otherEntityId = direction == "outgoing" ? edge.ToEntityId : edge.FromEntityId;
        string? otherEntityName = _entities.TryGetValue(otherEntityId, out var other) ? other.Name : null;

        return new RelationshipInfo
        {
            RelationshipId = edge.EdgeId,
            RelationshipType = edge.RelationshipType,
            Direction = direction,
            OtherEntityId = otherEntityId,
            OtherEntityName = otherEntityName,
            Weight = edge.Weight,
            Properties = edge.Properties
        };
    }

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

    private long EstimateIndexSize()
    {
        var entitySize = _entities.Count * 512;
        var relationshipSize = _relationships.Count * 256;
        var contentIndexSize = _content.Count * 128;
        return entitySize + relationshipSize + contentIndexSize;
    }

    #endregion

    #region Internal Types

    private sealed record EntityNode
    {
        public required string EntityId { get; init; }
        public required string Name { get; init; }
        public required string EntityType { get; init; }
        public List<string> Aliases { get; init; } = new();
        public long ContentCount { get; init; }
        public int AccessCount { get; init; }
        public float ImportanceScore { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset LastUpdated { get; init; }
    }

    private sealed record RelationshipEdge
    {
        public required string EdgeId { get; init; }
        public required string FromEntityId { get; init; }
        public required string ToEntityId { get; init; }
        public required string RelationshipType { get; init; }
        public float Weight { get; init; }
        public Dictionary<string, object> Properties { get; init; } = new();
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? LastAccessedAt { get; init; }
        public int AccessCount { get; init; }
    }

    private sealed record ContentEntry
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
    }

    #endregion
}

#region Entity Types

/// <summary>
/// Information about an entity.
/// </summary>
public record EntityInfo
{
    /// <summary>Entity identifier.</summary>
    public required string EntityId { get; init; }

    /// <summary>Entity display name.</summary>
    public required string Name { get; init; }

    /// <summary>Entity type (person, organization, concept, etc.).</summary>
    public string? EntityType { get; init; }

    /// <summary>Importance score (0-1).</summary>
    public float ImportanceScore { get; init; }

    /// <summary>Number of content items mentioning this entity.</summary>
    public long ContentCount { get; init; }

    /// <summary>Number of relationships.</summary>
    public int RelationshipCount { get; init; }

    /// <summary>Centrality score in the graph.</summary>
    public float? CentralityScore { get; init; }
}

/// <summary>
/// Information about a relationship.
/// </summary>
public record RelationshipInfo
{
    /// <summary>Relationship identifier.</summary>
    public required string RelationshipId { get; init; }

    /// <summary>Type of relationship.</summary>
    public required string RelationshipType { get; init; }

    /// <summary>Direction (outgoing, incoming).</summary>
    public required string Direction { get; init; }

    /// <summary>The other entity in the relationship.</summary>
    public required string OtherEntityId { get; init; }

    /// <summary>Name of the other entity.</summary>
    public string? OtherEntityName { get; init; }

    /// <summary>Relationship weight/strength.</summary>
    public float Weight { get; init; }

    /// <summary>Additional properties.</summary>
    public Dictionary<string, object>? Properties { get; init; }
}

/// <summary>
/// Direction filter for relationship queries.
/// </summary>
public enum RelationshipDirection
{
    /// <summary>Outgoing relationships only.</summary>
    Outgoing,

    /// <summary>Incoming relationships only.</summary>
    Incoming,

    /// <summary>Both directions.</summary>
    Both
}

/// <summary>
/// A path between two entities.
/// </summary>
public record EntityPath
{
    /// <summary>Source entity identifier.</summary>
    public required string FromEntityId { get; init; }

    /// <summary>Target entity identifier.</summary>
    public required string ToEntityId { get; init; }

    /// <summary>Entity names along the path.</summary>
    public required IList<string> Entities { get; init; }

    /// <summary>Relationship types along the path.</summary>
    public required IList<string> Relationships { get; init; }

    /// <summary>Number of hops in the path.</summary>
    public int PathLength { get; init; }
}

#endregion
