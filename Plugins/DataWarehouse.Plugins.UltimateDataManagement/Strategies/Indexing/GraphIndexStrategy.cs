using System.Collections.Concurrent;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Indexing;

/// <summary>
/// Graph relationship indexing strategy with adjacency list storage.
/// Provides efficient graph traversal, path finding, and relationship queries.
/// </summary>
/// <remarks>
/// Features:
/// - Node and edge indexing with labels
/// - Adjacency list storage for O(1) neighbor lookup
/// - Path queries (shortest path, all paths)
/// - Relationship traversal with configurable depth limits
/// - Label-based filtering for nodes and edges
/// - Property indexes on nodes and edges
/// - Bidirectional edge support
/// </remarks>
public sealed class GraphIndexStrategy : IndexingStrategyBase
{
    /// <summary>
    /// Node storage by node ID.
    /// </summary>
    private readonly BoundedDictionary<string, GraphNode> _nodes = new BoundedDictionary<string, GraphNode>(1000);

    /// <summary>
    /// Edge storage by edge ID.
    /// </summary>
    private readonly BoundedDictionary<string, GraphEdge> _edges = new BoundedDictionary<string, GraphEdge>(1000);

    /// <summary>
    /// Adjacency list: nodeId -> list of outgoing edges.
    /// </summary>
    private readonly BoundedDictionary<string, ConcurrentBag<string>> _outgoingEdges = new BoundedDictionary<string, ConcurrentBag<string>>(1000);

    /// <summary>
    /// Reverse adjacency list: nodeId -> list of incoming edges.
    /// </summary>
    private readonly BoundedDictionary<string, ConcurrentBag<string>> _incomingEdges = new BoundedDictionary<string, ConcurrentBag<string>>(1000);

    /// <summary>
    /// Label index for nodes: label -> nodeIds.
    /// </summary>
    private readonly BoundedDictionary<string, ConcurrentBag<string>> _nodeLabelIndex = new BoundedDictionary<string, ConcurrentBag<string>>(1000);

    /// <summary>
    /// Label index for edges: label/type -> edgeIds.
    /// </summary>
    private readonly BoundedDictionary<string, ConcurrentBag<string>> _edgeLabelIndex = new BoundedDictionary<string, ConcurrentBag<string>>(1000);

    /// <summary>
    /// Property indexes on nodes: propertyName -> (value -> nodeIds).
    /// </summary>
    private readonly BoundedDictionary<string, BoundedDictionary<string, ConcurrentBag<string>>> _nodePropertyIndexes = new BoundedDictionary<string, BoundedDictionary<string, ConcurrentBag<string>>>(1000);

    /// <summary>
    /// Property indexes on edges: propertyName -> (value -> edgeIds).
    /// </summary>
    private readonly BoundedDictionary<string, BoundedDictionary<string, ConcurrentBag<string>>> _edgePropertyIndexes = new BoundedDictionary<string, BoundedDictionary<string, ConcurrentBag<string>>>(1000);

    /// <summary>
    /// Default maximum traversal depth to prevent infinite loops.
    /// </summary>
    private readonly int _maxTraversalDepth;

    /// <summary>
    /// Represents a graph node.
    /// </summary>
    public sealed class GraphNode
    {
        /// <summary>Gets the node ID.</summary>
        public required string Id { get; init; }

        /// <summary>Gets the node labels (types).</summary>
        public required HashSet<string> Labels { get; init; }

        /// <summary>Gets the node properties.</summary>
        public required Dictionary<string, object> Properties { get; init; }

        /// <summary>Gets when the node was indexed.</summary>
        public DateTime IndexedAt { get; init; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Represents a graph edge (relationship).
    /// </summary>
    public sealed class GraphEdge
    {
        /// <summary>Gets the edge ID.</summary>
        public required string Id { get; init; }

        /// <summary>Gets the source node ID.</summary>
        public required string SourceId { get; init; }

        /// <summary>Gets the target node ID.</summary>
        public required string TargetId { get; init; }

        /// <summary>Gets the edge type/label.</summary>
        public required string Type { get; init; }

        /// <summary>Gets the edge properties.</summary>
        public required Dictionary<string, object> Properties { get; init; }

        /// <summary>Gets the edge weight (for weighted graphs).</summary>
        public double Weight { get; init; } = 1.0;

        /// <summary>Gets whether this is a bidirectional edge.</summary>
        public bool Bidirectional { get; init; }

        /// <summary>Gets when the edge was indexed.</summary>
        public DateTime IndexedAt { get; init; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Represents a path through the graph.
    /// </summary>
    public sealed class GraphPath
    {
        /// <summary>Gets the ordered list of node IDs in the path.</summary>
        public required List<string> NodeIds { get; init; }

        /// <summary>Gets the ordered list of edge IDs connecting the nodes.</summary>
        public required List<string> EdgeIds { get; init; }

        /// <summary>Gets the total weight of the path.</summary>
        public double TotalWeight { get; init; }

        /// <summary>Gets the path length (number of edges).</summary>
        public int Length => EdgeIds.Count;
    }

    /// <summary>
    /// Defines the direction for traversal queries.
    /// </summary>
    public enum TraversalDirection
    {
        /// <summary>Follow outgoing edges only.</summary>
        Outgoing,
        /// <summary>Follow incoming edges only.</summary>
        Incoming,
        /// <summary>Follow both directions.</summary>
        Both
    }

    /// <summary>
    /// Initializes a new GraphIndexStrategy with default settings.
    /// </summary>
    public GraphIndexStrategy() : this(maxTraversalDepth: 10) { }

    /// <summary>
    /// Initializes a new GraphIndexStrategy with custom settings.
    /// </summary>
    /// <param name="maxTraversalDepth">Maximum depth for traversal queries.</param>
    public GraphIndexStrategy(int maxTraversalDepth = 10)
    {
        _maxTraversalDepth = maxTraversalDepth;
    }

    /// <inheritdoc/>
    public override string StrategyId => "index.graph";

    /// <inheritdoc/>
    public override string DisplayName => "Graph Index";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = false,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 50_000,
        TypicalLatencyMs = 0.5
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Graph relationship index using adjacency lists for efficient traversal. " +
        "Supports path finding, relationship queries, and label-based filtering. " +
        "Best for connected data, social networks, and knowledge graphs.";

    /// <inheritdoc/>
    public override string[] Tags => ["index", "graph", "relationships", "network", "traversal", "paths"];

    /// <inheritdoc/>
    public override long GetDocumentCount() => _nodes.Count + _edges.Count;

    /// <summary>
    /// Gets the count of nodes in the graph.
    /// </summary>
    public int NodeCount => _nodes.Count;

    /// <summary>
    /// Gets the count of edges in the graph.
    /// </summary>
    public int EdgeCount => _edges.Count;

    /// <inheritdoc/>
    public override long GetIndexSize()
    {
        long size = 0;

        // Node storage
        size += _nodes.Count * 200;

        // Edge storage
        size += _edges.Count * 150;

        // Adjacency lists
        size += _outgoingEdges.Values.Sum(bag => bag.Count) * 40;
        size += _incomingEdges.Values.Sum(bag => bag.Count) * 40;

        // Indexes
        foreach (var labelIndex in _nodeLabelIndex.Values)
            size += labelIndex.Count * 40;
        foreach (var labelIndex in _edgeLabelIndex.Values)
            size += labelIndex.Count * 40;

        return size;
    }

    /// <inheritdoc/>
    public override Task<bool> ExistsAsync(string objectId, CancellationToken ct = default)
    {
        return Task.FromResult(_nodes.ContainsKey(objectId) || _edges.ContainsKey(objectId));
    }

    /// <inheritdoc/>
    public override Task ClearAsync(CancellationToken ct = default)
    {
        _nodes.Clear();
        _edges.Clear();
        _outgoingEdges.Clear();
        _incomingEdges.Clear();
        _nodeLabelIndex.Clear();
        _edgeLabelIndex.Clear();
        _nodePropertyIndexes.Clear();
        _edgePropertyIndexes.Clear();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task<IndexResult> IndexCoreAsync(string objectId, IndexableContent content, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        if (content.Metadata == null)
        {
            return Task.FromResult(IndexResult.Failed("No metadata provided for graph indexing"));
        }

        // Determine if this is a node or edge
        var isEdge = content.Metadata.ContainsKey("sourceId") || content.Metadata.ContainsKey("source") ||
                     content.Metadata.ContainsKey("_edge") || content.Metadata.ContainsKey("from");

        int indexed;
        if (isEdge)
        {
            indexed = IndexEdge(objectId, content);
        }
        else
        {
            indexed = IndexNode(objectId, content);
        }

        sw.Stop();
        return Task.FromResult(indexed > 0
            ? IndexResult.Ok(indexed, indexed, sw.Elapsed)
            : IndexResult.Failed("Failed to index graph element"));
    }

    /// <summary>
    /// Indexes a node.
    /// </summary>
    private int IndexNode(string nodeId, IndexableContent content)
    {
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var properties = new Dictionary<string, object>();

        if (content.Metadata != null)
        {
            // Extract labels
            if (content.Metadata.TryGetValue("labels", out var labelsObj))
            {
                if (labelsObj is IEnumerable<string> labelList)
                {
                    foreach (var label in labelList)
                        labels.Add(label);
                }
                else if (labelsObj is string labelStr)
                {
                    foreach (var label in labelStr.Split(',', ';'))
                        labels.Add(label.Trim());
                }
            }
            if (content.Metadata.TryGetValue("label", out var labelObj) && labelObj is string singleLabel)
            {
                labels.Add(singleLabel);
            }
            if (content.Metadata.TryGetValue("type", out var typeObj) && typeObj is string typeStr)
            {
                labels.Add(typeStr);
            }

            // Extract properties (all non-reserved keys)
            var reservedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "labels", "label", "type", "_node", "_edge"
            };

            foreach (var (key, value) in content.Metadata)
            {
                if (!reservedKeys.Contains(key))
                {
                    properties[key] = value;
                }
            }
        }

        // Add content as a property if present
        if (!string.IsNullOrEmpty(content.TextContent))
        {
            properties["_content"] = content.TextContent;
        }

        // Remove existing node if re-indexing
        if (_nodes.ContainsKey(nodeId))
        {
            RemoveNode(nodeId);
        }

        var node = new GraphNode
        {
            Id = nodeId,
            Labels = labels,
            Properties = properties
        };

        _nodes[nodeId] = node;

        // Update label index
        foreach (var label in labels)
        {
            var labelBag = _nodeLabelIndex.GetOrAdd(label, _ => new ConcurrentBag<string>());
            labelBag.Add(nodeId);
        }

        // Update property indexes
        foreach (var (propName, propValue) in properties)
        {
            IndexNodeProperty(nodeId, propName, propValue);
        }

        return 1;
    }

    /// <summary>
    /// Indexes an edge.
    /// </summary>
    private int IndexEdge(string edgeId, IndexableContent content)
    {
        if (content.Metadata == null)
            return 0;

        string? sourceId = null;
        string? targetId = null;
        string edgeType = "RELATED_TO";
        double weight = 1.0;
        bool bidirectional = false;
        var properties = new Dictionary<string, object>();

        // Extract source
        if (content.Metadata.TryGetValue("sourceId", out var srcObj))
            sourceId = srcObj.ToString();
        else if (content.Metadata.TryGetValue("source", out srcObj))
            sourceId = srcObj.ToString();
        else if (content.Metadata.TryGetValue("from", out srcObj))
            sourceId = srcObj.ToString();
        else if (content.Metadata.TryGetValue("fromId", out srcObj))
            sourceId = srcObj.ToString();

        // Extract target
        if (content.Metadata.TryGetValue("targetId", out var tgtObj))
            targetId = tgtObj.ToString();
        else if (content.Metadata.TryGetValue("target", out tgtObj))
            targetId = tgtObj.ToString();
        else if (content.Metadata.TryGetValue("to", out tgtObj))
            targetId = tgtObj.ToString();
        else if (content.Metadata.TryGetValue("toId", out tgtObj))
            targetId = tgtObj.ToString();

        // Extract type
        if (content.Metadata.TryGetValue("type", out var typeObj) && typeObj is string typeStr)
            edgeType = typeStr;
        else if (content.Metadata.TryGetValue("label", out typeObj) && typeObj is string labelStr)
            edgeType = labelStr;
        else if (content.Metadata.TryGetValue("relationship", out typeObj) && typeObj is string relStr)
            edgeType = relStr;

        // Extract weight
        if (content.Metadata.TryGetValue("weight", out var weightObj))
            weight = Convert.ToDouble(weightObj);

        // Extract bidirectional flag
        if (content.Metadata.TryGetValue("bidirectional", out var biObj))
            bidirectional = Convert.ToBoolean(biObj);
        else if (content.Metadata.TryGetValue("undirected", out biObj))
            bidirectional = Convert.ToBoolean(biObj);

        if (string.IsNullOrEmpty(sourceId) || string.IsNullOrEmpty(targetId))
            return 0;

        // Extract properties
        var reservedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sourceId", "source", "from", "fromId",
            "targetId", "target", "to", "toId",
            "type", "label", "relationship",
            "weight", "bidirectional", "undirected",
            "_edge"
        };

        foreach (var (key, value) in content.Metadata)
        {
            if (!reservedKeys.Contains(key))
            {
                properties[key] = value;
            }
        }

        // Remove existing edge if re-indexing
        if (_edges.ContainsKey(edgeId))
        {
            RemoveEdge(edgeId);
        }

        var edge = new GraphEdge
        {
            Id = edgeId,
            SourceId = sourceId,
            TargetId = targetId,
            Type = edgeType,
            Weight = weight,
            Bidirectional = bidirectional,
            Properties = properties
        };

        _edges[edgeId] = edge;

        // Update adjacency lists
        var outgoing = _outgoingEdges.GetOrAdd(sourceId, _ => new ConcurrentBag<string>());
        outgoing.Add(edgeId);

        var incoming = _incomingEdges.GetOrAdd(targetId, _ => new ConcurrentBag<string>());
        incoming.Add(edgeId);

        // For bidirectional edges, add reverse direction
        if (bidirectional)
        {
            var reverseOutgoing = _outgoingEdges.GetOrAdd(targetId, _ => new ConcurrentBag<string>());
            reverseOutgoing.Add(edgeId);

            var reverseIncoming = _incomingEdges.GetOrAdd(sourceId, _ => new ConcurrentBag<string>());
            reverseIncoming.Add(edgeId);
        }

        // Update label index
        var labelBag = _edgeLabelIndex.GetOrAdd(edgeType, _ => new ConcurrentBag<string>());
        labelBag.Add(edgeId);

        // Update property indexes
        foreach (var (propName, propValue) in properties)
        {
            IndexEdgeProperty(edgeId, propName, propValue);
        }

        return 1;
    }

    /// <summary>
    /// Indexes a node property.
    /// </summary>
    private void IndexNodeProperty(string nodeId, string propertyName, object value)
    {
        var valueStr = value.ToString() ?? "";
        var propIndex = _nodePropertyIndexes.GetOrAdd(propertyName,
            _ => new BoundedDictionary<string, ConcurrentBag<string>>(1000));
        var valueBag = propIndex.GetOrAdd(valueStr, _ => new ConcurrentBag<string>());
        valueBag.Add(nodeId);
    }

    /// <summary>
    /// Indexes an edge property.
    /// </summary>
    private void IndexEdgeProperty(string edgeId, string propertyName, object value)
    {
        var valueStr = value.ToString() ?? "";
        var propIndex = _edgePropertyIndexes.GetOrAdd(propertyName,
            _ => new BoundedDictionary<string, ConcurrentBag<string>>(1000));
        var valueBag = propIndex.GetOrAdd(valueStr, _ => new ConcurrentBag<string>());
        valueBag.Add(edgeId);
    }

    /// <inheritdoc/>
    protected override Task<IReadOnlyList<IndexSearchResult>> SearchCoreAsync(
        string query,
        IndexSearchOptions options,
        CancellationToken ct)
    {
        var results = new List<IndexSearchResult>();
        var queryParams = ParseSearchQuery(query);

        if (queryParams.TryGetValue("type", out var queryType))
        {
            switch (queryType.ToLowerInvariant())
            {
                case "node":
                case "nodes":
                    results = SearchNodes(queryParams, options);
                    break;
                case "edge":
                case "edges":
                case "relationship":
                case "relationships":
                    results = SearchEdges(queryParams, options);
                    break;
                case "neighbors":
                case "adjacent":
                    results = SearchNeighbors(queryParams, options);
                    break;
                case "path":
                case "shortestpath":
                    results = SearchShortestPath(queryParams, options);
                    break;
                case "allpaths":
                case "paths":
                    results = SearchAllPaths(queryParams, options);
                    break;
                case "traverse":
                case "traversal":
                    results = SearchTraversal(queryParams, options);
                    break;
                default:
                    results = SearchNodes(queryParams, options);
                    break;
            }
        }
        else if (queryParams.ContainsKey("from") && queryParams.ContainsKey("to"))
        {
            results = SearchShortestPath(queryParams, options);
        }
        else if (queryParams.ContainsKey("nodeId") || queryParams.ContainsKey("node"))
        {
            results = SearchNeighbors(queryParams, options);
        }
        else
        {
            results = SearchNodes(queryParams, options);
        }

        return Task.FromResult<IReadOnlyList<IndexSearchResult>>(results);
    }

    /// <summary>
    /// Searches for nodes by label or property.
    /// </summary>
    /// <param name="params">Query parameters.</param>
    /// <param name="options">Search options.</param>
    /// <returns>Matching nodes.</returns>
    public List<IndexSearchResult> SearchNodes(Dictionary<string, string> @params, IndexSearchOptions options)
    {
        IEnumerable<GraphNode> candidates = _nodes.Values;

        // Filter by label
        if (@params.TryGetValue("label", out var label))
        {
            if (_nodeLabelIndex.TryGetValue(label, out var labelBag))
            {
                var labelNodeIds = labelBag.ToHashSet();
                candidates = candidates.Where(n => labelNodeIds.Contains(n.Id));
            }
            else
            {
                candidates = Enumerable.Empty<GraphNode>();
            }
        }

        // Filter by property
        foreach (var (key, value) in @params)
        {
            if (key.StartsWith("prop.") || key.StartsWith("property."))
            {
                var propName = key.Split('.')[1];
                candidates = candidates.Where(n =>
                    n.Properties.TryGetValue(propName, out var propVal) &&
                    propVal.ToString()?.Equals(value, StringComparison.OrdinalIgnoreCase) == true);
            }
        }

        return candidates
            .Take(options.MaxResults)
            .Select(node => new IndexSearchResult
            {
                ObjectId = node.Id,
                Score = 1.0,
                Snippet = $"Node [{string.Join(", ", node.Labels)}]: {node.Properties.Count} properties",
                Metadata = new Dictionary<string, object>
                {
                    ["_type"] = "node",
                    ["_labels"] = node.Labels.ToList(),
                    ["_properties"] = node.Properties,
                    ["_indexedAt"] = node.IndexedAt
                }
            })
            .ToList();
    }

    /// <summary>
    /// Searches for edges by type or property.
    /// </summary>
    /// <param name="params">Query parameters.</param>
    /// <param name="options">Search options.</param>
    /// <returns>Matching edges.</returns>
    public List<IndexSearchResult> SearchEdges(Dictionary<string, string> @params, IndexSearchOptions options)
    {
        IEnumerable<GraphEdge> candidates = _edges.Values;

        // Filter by type
        if (@params.TryGetValue("edgeType", out var edgeType) || @params.TryGetValue("relationship", out edgeType))
        {
            if (_edgeLabelIndex.TryGetValue(edgeType, out var labelBag))
            {
                var labelEdgeIds = labelBag.ToHashSet();
                candidates = candidates.Where(e => labelEdgeIds.Contains(e.Id));
            }
            else
            {
                candidates = Enumerable.Empty<GraphEdge>();
            }
        }

        // Filter by source
        if (@params.TryGetValue("source", out var sourceId) || @params.TryGetValue("from", out sourceId))
        {
            candidates = candidates.Where(e => e.SourceId.Equals(sourceId, StringComparison.OrdinalIgnoreCase));
        }

        // Filter by target
        if (@params.TryGetValue("target", out var targetId) || @params.TryGetValue("to", out targetId))
        {
            candidates = candidates.Where(e => e.TargetId.Equals(targetId, StringComparison.OrdinalIgnoreCase));
        }

        return candidates
            .Take(options.MaxResults)
            .Select(edge => new IndexSearchResult
            {
                ObjectId = edge.Id,
                Score = 1.0,
                Snippet = $"Edge [{edge.Type}]: {edge.SourceId} -> {edge.TargetId}",
                Metadata = new Dictionary<string, object>
                {
                    ["_type"] = "edge",
                    ["_edgeType"] = edge.Type,
                    ["_sourceId"] = edge.SourceId,
                    ["_targetId"] = edge.TargetId,
                    ["_weight"] = edge.Weight,
                    ["_bidirectional"] = edge.Bidirectional,
                    ["_properties"] = edge.Properties,
                    ["_indexedAt"] = edge.IndexedAt
                }
            })
            .ToList();
    }

    /// <summary>
    /// Searches for neighbors of a node.
    /// </summary>
    /// <param name="params">Query parameters containing nodeId.</param>
    /// <param name="options">Search options.</param>
    /// <returns>Adjacent nodes.</returns>
    public List<IndexSearchResult> SearchNeighbors(Dictionary<string, string> @params, IndexSearchOptions options)
    {
        string? nodeId = null;
        if (@params.TryGetValue("nodeId", out var nid))
            nodeId = nid;
        else if (@params.TryGetValue("node", out nid))
            nodeId = nid;
        else if (@params.TryGetValue("id", out nid))
            nodeId = nid;

        if (string.IsNullOrEmpty(nodeId))
            return new List<IndexSearchResult>();

        var direction = TraversalDirection.Both;
        if (@params.TryGetValue("direction", out var dirStr))
        {
            direction = dirStr.ToLowerInvariant() switch
            {
                "out" or "outgoing" => TraversalDirection.Outgoing,
                "in" or "incoming" => TraversalDirection.Incoming,
                _ => TraversalDirection.Both
            };
        }

        string? edgeTypeFilter = null;
        if (@params.TryGetValue("edgeType", out var etf) || @params.TryGetValue("relationship", out etf))
            edgeTypeFilter = etf;

        var neighbors = GetNeighbors(nodeId, direction, edgeTypeFilter);

        return neighbors
            .Take(options.MaxResults)
            .Select(n => new IndexSearchResult
            {
                ObjectId = n.Node.Id,
                Score = 1.0,
                Snippet = $"Neighbor via [{n.Edge.Type}]: {n.Node.Id}",
                Metadata = new Dictionary<string, object>
                {
                    ["_type"] = "node",
                    ["_labels"] = n.Node.Labels.ToList(),
                    ["_properties"] = n.Node.Properties,
                    ["_connectedVia"] = n.Edge.Type,
                    ["_edgeId"] = n.Edge.Id,
                    ["_edgeWeight"] = n.Edge.Weight
                }
            })
            .ToList();
    }

    /// <summary>
    /// Gets the neighbors of a node.
    /// </summary>
    /// <param name="nodeId">The node ID.</param>
    /// <param name="direction">Direction of edges to follow.</param>
    /// <param name="edgeTypeFilter">Optional edge type filter.</param>
    /// <returns>List of neighboring nodes with their connecting edges.</returns>
    public List<(GraphNode Node, GraphEdge Edge)> GetNeighbors(string nodeId, TraversalDirection direction = TraversalDirection.Both, string? edgeTypeFilter = null)
    {
        var results = new List<(GraphNode, GraphEdge)>();
        var visited = new HashSet<string> { nodeId };

        if (direction == TraversalDirection.Outgoing || direction == TraversalDirection.Both)
        {
            if (_outgoingEdges.TryGetValue(nodeId, out var outgoing))
            {
                foreach (var edgeId in outgoing)
                {
                    if (_edges.TryGetValue(edgeId, out var edge))
                    {
                        if (edgeTypeFilter != null && !edge.Type.Equals(edgeTypeFilter, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var targetId = edge.SourceId == nodeId ? edge.TargetId : edge.SourceId;
                        if (!visited.Contains(targetId) && _nodes.TryGetValue(targetId, out var targetNode))
                        {
                            visited.Add(targetId);
                            results.Add((targetNode, edge));
                        }
                    }
                }
            }
        }

        if (direction == TraversalDirection.Incoming || direction == TraversalDirection.Both)
        {
            if (_incomingEdges.TryGetValue(nodeId, out var incoming))
            {
                foreach (var edgeId in incoming)
                {
                    if (_edges.TryGetValue(edgeId, out var edge))
                    {
                        if (edgeTypeFilter != null && !edge.Type.Equals(edgeTypeFilter, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var sourceId = edge.TargetId == nodeId ? edge.SourceId : edge.TargetId;
                        if (!visited.Contains(sourceId) && _nodes.TryGetValue(sourceId, out var sourceNode))
                        {
                            visited.Add(sourceId);
                            results.Add((sourceNode, edge));
                        }
                    }
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Searches for the shortest path between two nodes.
    /// </summary>
    /// <param name="params">Query parameters containing from and to node IDs.</param>
    /// <param name="options">Search options.</param>
    /// <returns>Nodes along the shortest path.</returns>
    public List<IndexSearchResult> SearchShortestPath(Dictionary<string, string> @params, IndexSearchOptions options)
    {
        string? fromId = null, toId = null;

        if (@params.TryGetValue("from", out var f))
            fromId = f;
        else if (@params.TryGetValue("source", out f))
            fromId = f;
        else if (@params.TryGetValue("start", out f))
            fromId = f;

        if (@params.TryGetValue("to", out var t))
            toId = t;
        else if (@params.TryGetValue("target", out t))
            toId = t;
        else if (@params.TryGetValue("end", out t))
            toId = t;

        if (string.IsNullOrEmpty(fromId) || string.IsNullOrEmpty(toId))
            return new List<IndexSearchResult>();

        string? edgeTypeFilter = null;
        if (@params.TryGetValue("edgeType", out var etf) || @params.TryGetValue("relationship", out etf))
            edgeTypeFilter = etf;

        var maxDepth = _maxTraversalDepth;
        if (@params.TryGetValue("maxDepth", out var mdStr) && int.TryParse(mdStr, out var md))
            maxDepth = md;

        var path = FindShortestPath(fromId, toId, edgeTypeFilter, maxDepth);

        if (path == null)
            return new List<IndexSearchResult>();

        return path.NodeIds
            .Where(id => _nodes.ContainsKey(id))
            .Select((id, index) => new IndexSearchResult
            {
                ObjectId = id,
                Score = 1.0 - (index * 0.1), // Decrease score along path
                Snippet = $"Path step {index + 1}: {id}",
                Metadata = new Dictionary<string, object>
                {
                    ["_type"] = "node",
                    ["_pathPosition"] = index,
                    ["_pathLength"] = path.Length,
                    ["_totalWeight"] = path.TotalWeight,
                    ["_labels"] = _nodes[id].Labels.ToList(),
                    ["_properties"] = _nodes[id].Properties
                }
            })
            .ToList();
    }

    /// <summary>
    /// Finds the shortest path between two nodes using Dijkstra's algorithm.
    /// </summary>
    /// <param name="fromId">Start node ID.</param>
    /// <param name="toId">End node ID.</param>
    /// <param name="edgeTypeFilter">Optional edge type filter.</param>
    /// <param name="maxDepth">Maximum path length.</param>
    /// <returns>The shortest path, or null if no path exists.</returns>
    public GraphPath? FindShortestPath(string fromId, string toId, string? edgeTypeFilter = null, int? maxDepth = null)
    {
        var effectiveMaxDepth = maxDepth ?? _maxTraversalDepth;

        if (!_nodes.ContainsKey(fromId) || !_nodes.ContainsKey(toId))
            return null;

        if (fromId == toId)
            return new GraphPath { NodeIds = new List<string> { fromId }, EdgeIds = new List<string>(), TotalWeight = 0 };

        var distances = new Dictionary<string, double> { [fromId] = 0 };
        var previous = new Dictionary<string, (string NodeId, string EdgeId)>();
        var queue = new PriorityQueue<string, double>();
        var visited = new HashSet<string>();

        queue.Enqueue(fromId, 0);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            if (visited.Contains(current))
                continue;

            visited.Add(current);

            if (current == toId)
            {
                // Reconstruct path
                var nodeIds = new List<string>();
                var edgeIds = new List<string>();
                var node = toId;

                while (previous.ContainsKey(node))
                {
                    nodeIds.Insert(0, node);
                    var (prevNode, edgeId) = previous[node];
                    edgeIds.Insert(0, edgeId);
                    node = prevNode;
                }
                nodeIds.Insert(0, fromId);

                return new GraphPath
                {
                    NodeIds = nodeIds,
                    EdgeIds = edgeIds,
                    TotalWeight = distances[toId]
                };
            }

            // Check depth limit
            var currentPath = ReconstructPath(previous, current, fromId);
            if (currentPath.Count >= effectiveMaxDepth)
                continue;

            foreach (var (neighbor, edge) in GetNeighbors(current, TraversalDirection.Both, edgeTypeFilter))
            {
                if (visited.Contains(neighbor.Id))
                    continue;

                var newDist = distances[current] + edge.Weight;
                if (!distances.TryGetValue(neighbor.Id, out var existingDist) || newDist < existingDist)
                {
                    distances[neighbor.Id] = newDist;
                    previous[neighbor.Id] = (current, edge.Id);
                    queue.Enqueue(neighbor.Id, newDist);
                }
            }
        }

        return null; // No path found
    }

    /// <summary>
    /// Reconstructs a path from the previous map.
    /// </summary>
    private List<string> ReconstructPath(Dictionary<string, (string NodeId, string EdgeId)> previous, string current, string start)
    {
        var path = new List<string> { current };
        while (previous.TryGetValue(current, out var prev) && current != start)
        {
            current = prev.NodeId;
            path.Add(current);
        }
        return path;
    }

    /// <summary>
    /// Searches for all paths between two nodes.
    /// </summary>
    /// <param name="params">Query parameters containing from and to node IDs.</param>
    /// <param name="options">Search options.</param>
    /// <returns>All paths found.</returns>
    public List<IndexSearchResult> SearchAllPaths(Dictionary<string, string> @params, IndexSearchOptions options)
    {
        string? fromId = null, toId = null;

        if (@params.TryGetValue("from", out var f))
            fromId = f;
        else if (@params.TryGetValue("source", out f))
            fromId = f;

        if (@params.TryGetValue("to", out var t))
            toId = t;
        else if (@params.TryGetValue("target", out t))
            toId = t;

        if (string.IsNullOrEmpty(fromId) || string.IsNullOrEmpty(toId))
            return new List<IndexSearchResult>();

        var maxDepth = _maxTraversalDepth;
        if (@params.TryGetValue("maxDepth", out var mdStr) && int.TryParse(mdStr, out var md))
            maxDepth = md;

        string? edgeTypeFilter = null;
        if (@params.TryGetValue("edgeType", out var etf))
            edgeTypeFilter = etf;

        var paths = FindAllPaths(fromId, toId, edgeTypeFilter, maxDepth);

        return paths
            .Take(options.MaxResults)
            .Select((path, index) => new IndexSearchResult
            {
                ObjectId = $"path_{index}",
                Score = 1.0 / (path.Length + 1), // Shorter paths score higher
                Snippet = $"Path {index + 1}: {string.Join(" -> ", path.NodeIds)}",
                Metadata = new Dictionary<string, object>
                {
                    ["_type"] = "path",
                    ["_nodeIds"] = path.NodeIds,
                    ["_edgeIds"] = path.EdgeIds,
                    ["_length"] = path.Length,
                    ["_totalWeight"] = path.TotalWeight
                }
            })
            .ToList();
    }

    /// <summary>
    /// Finds all paths between two nodes using DFS.
    /// </summary>
    /// <param name="fromId">Start node ID.</param>
    /// <param name="toId">End node ID.</param>
    /// <param name="edgeTypeFilter">Optional edge type filter.</param>
    /// <param name="maxDepth">Maximum path length.</param>
    /// <returns>List of all paths.</returns>
    public List<GraphPath> FindAllPaths(string fromId, string toId, string? edgeTypeFilter = null, int? maxDepth = null)
    {
        var effectiveMaxDepth = maxDepth ?? _maxTraversalDepth;
        var paths = new List<GraphPath>();
        var currentPath = new List<string> { fromId };
        var currentEdges = new List<string>();
        var visited = new HashSet<string> { fromId };

        FindAllPathsDfs(fromId, toId, visited, currentPath, currentEdges, paths, edgeTypeFilter, effectiveMaxDepth, 0);

        return paths.OrderBy(p => p.TotalWeight).ToList();
    }

    /// <summary>
    /// DFS helper for finding all paths.
    /// </summary>
    private void FindAllPathsDfs(string current, string target, HashSet<string> visited,
        List<string> currentPath, List<string> currentEdges, List<GraphPath> paths,
        string? edgeTypeFilter, int maxDepth, double currentWeight)
    {
        if (currentPath.Count > maxDepth + 1)
            return;

        if (current == target)
        {
            paths.Add(new GraphPath
            {
                NodeIds = new List<string>(currentPath),
                EdgeIds = new List<string>(currentEdges),
                TotalWeight = currentWeight
            });
            return;
        }

        foreach (var (neighbor, edge) in GetNeighbors(current, TraversalDirection.Both, edgeTypeFilter))
        {
            if (visited.Contains(neighbor.Id))
                continue;

            visited.Add(neighbor.Id);
            currentPath.Add(neighbor.Id);
            currentEdges.Add(edge.Id);

            FindAllPathsDfs(neighbor.Id, target, visited, currentPath, currentEdges, paths,
                edgeTypeFilter, maxDepth, currentWeight + edge.Weight);

            currentPath.RemoveAt(currentPath.Count - 1);
            currentEdges.RemoveAt(currentEdges.Count - 1);
            visited.Remove(neighbor.Id);
        }
    }

    /// <summary>
    /// Searches by traversing the graph from a starting node.
    /// </summary>
    /// <param name="params">Query parameters containing start node and depth.</param>
    /// <param name="options">Search options.</param>
    /// <returns>Nodes discovered during traversal.</returns>
    public List<IndexSearchResult> SearchTraversal(Dictionary<string, string> @params, IndexSearchOptions options)
    {
        string? startId = null;

        if (@params.TryGetValue("start", out var s))
            startId = s;
        else if (@params.TryGetValue("nodeId", out s))
            startId = s;
        else if (@params.TryGetValue("from", out s))
            startId = s;

        if (string.IsNullOrEmpty(startId))
            return new List<IndexSearchResult>();

        var maxDepth = _maxTraversalDepth;
        if (@params.TryGetValue("depth", out var dStr) && int.TryParse(dStr, out var d))
            maxDepth = d;
        else if (@params.TryGetValue("maxDepth", out dStr) && int.TryParse(dStr, out d))
            maxDepth = d;

        var direction = TraversalDirection.Both;
        if (@params.TryGetValue("direction", out var dirStr))
        {
            direction = dirStr.ToLowerInvariant() switch
            {
                "out" or "outgoing" => TraversalDirection.Outgoing,
                "in" or "incoming" => TraversalDirection.Incoming,
                _ => TraversalDirection.Both
            };
        }

        string? edgeTypeFilter = null;
        if (@params.TryGetValue("edgeType", out var etf))
            edgeTypeFilter = etf;

        var traversed = TraverseBfs(startId, direction, edgeTypeFilter, maxDepth);

        return traversed
            .Take(options.MaxResults)
            .Select(item => new IndexSearchResult
            {
                ObjectId = item.Node.Id,
                Score = 1.0 / (item.Depth + 1), // Closer nodes score higher
                Snippet = $"Depth {item.Depth}: {item.Node.Id} [{string.Join(", ", item.Node.Labels)}]",
                Metadata = new Dictionary<string, object>
                {
                    ["_type"] = "node",
                    ["_depth"] = item.Depth,
                    ["_labels"] = item.Node.Labels.ToList(),
                    ["_properties"] = item.Node.Properties
                }
            })
            .ToList();
    }

    /// <summary>
    /// Traverses the graph using BFS from a starting node.
    /// </summary>
    /// <param name="startId">Starting node ID.</param>
    /// <param name="direction">Direction to traverse.</param>
    /// <param name="edgeTypeFilter">Optional edge type filter.</param>
    /// <param name="maxDepth">Maximum traversal depth.</param>
    /// <returns>Nodes discovered with their depth.</returns>
    public List<(GraphNode Node, int Depth)> TraverseBfs(string startId, TraversalDirection direction = TraversalDirection.Both,
        string? edgeTypeFilter = null, int? maxDepth = null)
    {
        var effectiveMaxDepth = maxDepth ?? _maxTraversalDepth;
        var results = new List<(GraphNode, int)>();
        var visited = new HashSet<string>();
        var queue = new Queue<(string NodeId, int Depth)>();

        if (!_nodes.TryGetValue(startId, out var startNode))
            return results;

        queue.Enqueue((startId, 0));
        visited.Add(startId);
        results.Add((startNode, 0));

        while (queue.Count > 0)
        {
            var (currentId, depth) = queue.Dequeue();

            if (depth >= effectiveMaxDepth)
                continue;

            foreach (var (neighbor, _) in GetNeighbors(currentId, direction, edgeTypeFilter))
            {
                if (visited.Contains(neighbor.Id))
                    continue;

                visited.Add(neighbor.Id);
                results.Add((neighbor, depth + 1));
                queue.Enqueue((neighbor.Id, depth + 1));
            }
        }

        return results;
    }

    /// <inheritdoc/>
    protected override Task<bool> RemoveCoreAsync(string objectId, CancellationToken ct)
    {
        // Try to remove as node
        if (_nodes.ContainsKey(objectId))
        {
            RemoveNode(objectId);
            return Task.FromResult(true);
        }

        // Try to remove as edge
        if (_edges.ContainsKey(objectId))
        {
            RemoveEdge(objectId);
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    /// <summary>
    /// Removes a node and all its connected edges.
    /// </summary>
    private void RemoveNode(string nodeId)
    {
        if (!_nodes.TryRemove(nodeId, out var node))
            return;

        // Remove from label index
        foreach (var label in node.Labels)
        {
            if (_nodeLabelIndex.TryGetValue(label, out var labelBag))
            {
                var newBag = new ConcurrentBag<string>(labelBag.Where(id => id != nodeId));
                _nodeLabelIndex[label] = newBag;
            }
        }

        // Remove connected edges
        if (_outgoingEdges.TryRemove(nodeId, out var outgoing))
        {
            foreach (var edgeId in outgoing)
            {
                RemoveEdge(edgeId);
            }
        }

        if (_incomingEdges.TryRemove(nodeId, out var incoming))
        {
            foreach (var edgeId in incoming)
            {
                RemoveEdge(edgeId);
            }
        }
    }

    /// <summary>
    /// Removes an edge.
    /// </summary>
    private void RemoveEdge(string edgeId)
    {
        if (!_edges.TryRemove(edgeId, out var edge))
            return;

        // Remove from adjacency lists
        if (_outgoingEdges.TryGetValue(edge.SourceId, out var outgoing))
        {
            var newBag = new ConcurrentBag<string>(outgoing.Where(id => id != edgeId));
            _outgoingEdges[edge.SourceId] = newBag;
        }

        if (_incomingEdges.TryGetValue(edge.TargetId, out var incoming))
        {
            var newBag = new ConcurrentBag<string>(incoming.Where(id => id != edgeId));
            _incomingEdges[edge.TargetId] = newBag;
        }

        // Handle bidirectional
        if (edge.Bidirectional)
        {
            if (_outgoingEdges.TryGetValue(edge.TargetId, out var reverseOut))
            {
                var newBag = new ConcurrentBag<string>(reverseOut.Where(id => id != edgeId));
                _outgoingEdges[edge.TargetId] = newBag;
            }

            if (_incomingEdges.TryGetValue(edge.SourceId, out var reverseIn))
            {
                var newBag = new ConcurrentBag<string>(reverseIn.Where(id => id != edgeId));
                _incomingEdges[edge.SourceId] = newBag;
            }
        }

        // Remove from label index
        if (_edgeLabelIndex.TryGetValue(edge.Type, out var labelBag))
        {
            var newBag = new ConcurrentBag<string>(labelBag.Where(id => id != edgeId));
            _edgeLabelIndex[edge.Type] = newBag;
        }
    }

    /// <summary>
    /// Parses a search query string into parameters.
    /// </summary>
    private static Dictionary<string, string> ParseSearchQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var parts = query.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var colonIndex = part.IndexOf(':');
            if (colonIndex > 0 && colonIndex < part.Length - 1)
            {
                var key = part[..colonIndex];
                var value = part[(colonIndex + 1)..];
                result[key] = value;
            }
            else if (part.Contains('='))
            {
                var eqIndex = part.IndexOf('=');
                if (eqIndex > 0 && eqIndex < part.Length - 1)
                {
                    var key = part[..eqIndex];
                    var value = part[(eqIndex + 1)..];
                    result[key] = value;
                }
            }
        }

        return result;
    }
}
