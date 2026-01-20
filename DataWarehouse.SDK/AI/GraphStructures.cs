using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.AI
{
    /// <summary>
    /// Knowledge graph interface for AI-driven relationship discovery and traversal.
    /// Supports entity relationships, semantic connections, and graph-based queries.
    /// </summary>
    public interface IKnowledgeGraph
    {
        /// <summary>
        /// Add a node to the graph.
        /// </summary>
        Task<GraphNode> AddNodeAsync(string label, Dictionary<string, object>? properties = null, CancellationToken ct = default);

        /// <summary>
        /// Add an edge between two nodes.
        /// </summary>
        Task<GraphEdge> AddEdgeAsync(string fromNodeId, string toNodeId, string relationship, Dictionary<string, object>? properties = null, CancellationToken ct = default);

        /// <summary>
        /// Get a node by ID.
        /// </summary>
        Task<GraphNode?> GetNodeAsync(string nodeId, CancellationToken ct = default);

        /// <summary>
        /// Get all edges for a node.
        /// </summary>
        Task<IEnumerable<GraphEdge>> GetEdgesAsync(string nodeId, EdgeDirection direction = EdgeDirection.Both, CancellationToken ct = default);

        /// <summary>
        /// Find nodes by label.
        /// </summary>
        Task<IEnumerable<GraphNode>> FindNodesByLabelAsync(string label, CancellationToken ct = default);

        /// <summary>
        /// Find nodes by property value.
        /// </summary>
        Task<IEnumerable<GraphNode>> FindNodesByPropertyAsync(string key, object value, CancellationToken ct = default);

        /// <summary>
        /// Traverse the graph from a starting node.
        /// </summary>
        Task<GraphTraversalResult> TraverseAsync(string startNodeId, GraphTraversalOptions options, CancellationToken ct = default);

        /// <summary>
        /// Find shortest path between two nodes.
        /// </summary>
        Task<GraphPath?> FindPathAsync(string fromNodeId, string toNodeId, int maxDepth = 10, CancellationToken ct = default);

        /// <summary>
        /// Execute a graph query (implementation-specific query language).
        /// </summary>
        Task<GraphQueryResult> QueryAsync(string query, Dictionary<string, object>? parameters = null, CancellationToken ct = default);

        /// <summary>
        /// Delete a node and its edges.
        /// </summary>
        Task DeleteNodeAsync(string nodeId, CancellationToken ct = default);

        /// <summary>
        /// Delete an edge.
        /// </summary>
        Task DeleteEdgeAsync(string edgeId, CancellationToken ct = default);
    }

    /// <summary>
    /// A node in the knowledge graph.
    /// </summary>
    public class GraphNode
    {
        /// <summary>Unique identifier for this node.</summary>
        public string Id { get; init; } = Guid.NewGuid().ToString();

        /// <summary>Node label/type (e.g., "Document", "Entity", "Concept").</summary>
        public string Label { get; init; } = string.Empty;

        /// <summary>Node properties.</summary>
        public Dictionary<string, object> Properties { get; init; } = new();

        /// <summary>Optional embedding vector for semantic operations.</summary>
        public float[]? Embedding { get; init; }

        /// <summary>Creation timestamp.</summary>
        public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>Last modified timestamp.</summary>
        public DateTimeOffset? ModifiedAt { get; init; }
    }

    /// <summary>
    /// An edge (relationship) between two nodes.
    /// </summary>
    public class GraphEdge
    {
        /// <summary>Unique identifier for this edge.</summary>
        public string Id { get; init; } = Guid.NewGuid().ToString();

        /// <summary>Source node ID.</summary>
        public string FromNodeId { get; init; } = string.Empty;

        /// <summary>Target node ID.</summary>
        public string ToNodeId { get; init; } = string.Empty;

        /// <summary>Relationship type (e.g., "CONTAINS", "REFERENCES", "SIMILAR_TO").</summary>
        public string Relationship { get; init; } = string.Empty;

        /// <summary>Edge properties (e.g., weight, confidence).</summary>
        public Dictionary<string, object> Properties { get; init; } = new();

        /// <summary>Edge weight for traversal algorithms.</summary>
        public float Weight { get; init; } = 1.0f;

        /// <summary>Whether this is a directed edge.</summary>
        public bool IsDirected { get; init; } = true;
    }

    /// <summary>
    /// Direction for edge queries.
    /// </summary>
    public enum EdgeDirection
    {
        /// <summary>Outgoing edges from the node.</summary>
        Outgoing,
        /// <summary>Incoming edges to the node.</summary>
        Incoming,
        /// <summary>Both incoming and outgoing edges.</summary>
        Both
    }

    /// <summary>
    /// Options for graph traversal.
    /// </summary>
    public class GraphTraversalOptions
    {
        /// <summary>Maximum depth to traverse.</summary>
        public int MaxDepth { get; init; } = 5;

        /// <summary>Maximum nodes to visit.</summary>
        public int MaxNodes { get; init; } = 1000;

        /// <summary>Filter by relationship types (null = all).</summary>
        public string[]? RelationshipFilter { get; init; }

        /// <summary>Filter by node labels (null = all).</summary>
        public string[]? LabelFilter { get; init; }

        /// <summary>Traversal strategy.</summary>
        public TraversalStrategy Strategy { get; init; } = TraversalStrategy.BreadthFirst;

        /// <summary>Include node properties in results.</summary>
        public bool IncludeProperties { get; init; } = true;
    }

    /// <summary>
    /// Traversal strategy for graph operations.
    /// </summary>
    public enum TraversalStrategy
    {
        /// <summary>Breadth-first traversal.</summary>
        BreadthFirst,
        /// <summary>Depth-first traversal.</summary>
        DepthFirst,
        /// <summary>Best-first using edge weights.</summary>
        BestFirst
    }

    /// <summary>
    /// Result of a graph traversal operation.
    /// </summary>
    public class GraphTraversalResult
    {
        /// <summary>Visited nodes in traversal order.</summary>
        public List<GraphNode> Nodes { get; init; } = new();

        /// <summary>Traversed edges.</summary>
        public List<GraphEdge> Edges { get; init; } = new();

        /// <summary>Total nodes visited.</summary>
        public int NodesVisited { get; init; }

        /// <summary>Maximum depth reached.</summary>
        public int MaxDepthReached { get; init; }

        /// <summary>Whether traversal was truncated due to limits.</summary>
        public bool WasTruncated { get; init; }
    }

    /// <summary>
    /// A path between two nodes in the graph.
    /// </summary>
    public class GraphPath
    {
        /// <summary>Ordered list of nodes in the path.</summary>
        public List<GraphNode> Nodes { get; init; } = new();

        /// <summary>Edges connecting the nodes.</summary>
        public List<GraphEdge> Edges { get; init; } = new();

        /// <summary>Total path length (number of edges).</summary>
        public int Length => Edges.Count;

        /// <summary>Total path weight (sum of edge weights).</summary>
        public float TotalWeight { get; init; }
    }

    /// <summary>
    /// Result of a graph query.
    /// </summary>
    public class GraphQueryResult
    {
        /// <summary>Whether the query was successful.</summary>
        public bool Success { get; init; }

        /// <summary>Error message if not successful.</summary>
        public string? ErrorMessage { get; init; }

        /// <summary>Returned nodes.</summary>
        public List<GraphNode> Nodes { get; init; } = new();

        /// <summary>Returned edges.</summary>
        public List<GraphEdge> Edges { get; init; } = new();

        /// <summary>Scalar results (for aggregation queries).</summary>
        public Dictionary<string, object> Scalars { get; init; } = new();

        /// <summary>Query execution time.</summary>
        public TimeSpan ExecutionTime { get; init; }
    }

    /// <summary>
    /// Common relationship types for knowledge graphs.
    /// </summary>
    public static class GraphRelationships
    {
        // Structural relationships
        public const string Contains = "CONTAINS";
        public const string PartOf = "PART_OF";
        public const string References = "REFERENCES";
        public const string DependsOn = "DEPENDS_ON";

        // Semantic relationships
        public const string SimilarTo = "SIMILAR_TO";
        public const string RelatedTo = "RELATED_TO";
        public const string DerivedFrom = "DERIVED_FROM";
        public const string InstanceOf = "INSTANCE_OF";

        // Temporal relationships
        public const string Precedes = "PRECEDES";
        public const string Follows = "FOLLOWS";
        public const string CreatedBy = "CREATED_BY";
        public const string ModifiedBy = "MODIFIED_BY";

        // Data relationships
        public const string StoredIn = "STORED_IN";
        public const string IndexedBy = "INDEXED_BY";
        public const string EncryptedWith = "ENCRYPTED_WITH";
        public const string CompressedWith = "COMPRESSED_WITH";
    }
}
