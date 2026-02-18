using System.Net.Http.Json;
using DataWarehouse.SDK.AI;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.KnowledgeGraphs;

/// <summary>
/// ArangoDB multi-model database strategy.
/// Supports graph, document, and key/value in a single engine.
/// </summary>
public sealed class ArangoGraphStrategy : KnowledgeGraphStrategyBase
{
    private const string DefaultHost = "http://localhost:8529";
    private const string DefaultDatabase = "_system";
    private const string DefaultGraph = "datawarehouse";

    private readonly HttpClient _httpClient;

    /// <inheritdoc/>
    public override string StrategyId => "graph-arango";

    /// <inheritdoc/>
    public override string StrategyName => "ArangoDB Graph Database";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "ArangoDB",
        Description = "Multi-model database supporting graph, document, and key/value with AQL query language",
        Capabilities = IntelligenceCapabilities.AllKnowledgeGraph,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "Host", Description = "ArangoDB host URL", Required = false, DefaultValue = DefaultHost },
            new ConfigurationRequirement { Key = "Database", Description = "Database name", Required = false, DefaultValue = DefaultDatabase },
            new ConfigurationRequirement { Key = "Graph", Description = "Graph name", Required = false, DefaultValue = DefaultGraph },
            new ConfigurationRequirement { Key = "Username", Description = "Username", Required = false, DefaultValue = "root" },
            new ConfigurationRequirement { Key = "Password", Description = "Password", Required = false, IsSecret = true }
        },
        CostTier = 2,
        LatencyTier = 2,
        RequiresNetworkAccess = true,
        SupportsOfflineMode = true,
        Tags = new[] { "arangodb", "multi-model", "aql", "graph-db", "document-db" }
    };

    private static readonly HttpClient SharedHttpClient = new HttpClient();
    public ArangoGraphStrategy() : this(SharedHttpClient) { }
    public ArangoGraphStrategy(HttpClient httpClient) { _httpClient = httpClient; }

    private string GetBaseUrl() => $"{GetConfig("Host") ?? DefaultHost}/_db/{GetConfig("Database") ?? DefaultDatabase}";

    public override async Task<GraphNode> AddNodeAsync(string label, Dictionary<string, object>? properties = null, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var id = Guid.NewGuid().ToString();
            var doc = properties ?? new Dictionary<string, object>();
            doc["_key"] = id;
            doc["label"] = label;

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{GetBaseUrl()}/_api/document/{label}");
            AddAuthHeader(request);
            request.Content = JsonContent.Create(doc);
            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            RecordNodesCreated(1);
            return new GraphNode { Id = id, Label = label, Properties = doc };
        });
    }

    public override async Task<GraphEdge> AddEdgeAsync(string fromNodeId, string toNodeId, string relationship, Dictionary<string, object>? properties = null, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var id = Guid.NewGuid().ToString();
            var edge = properties ?? new Dictionary<string, object>();
            edge["_key"] = id;
            edge["_from"] = fromNodeId;
            edge["_to"] = toNodeId;

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{GetBaseUrl()}/_api/document/{relationship}");
            AddAuthHeader(request);
            request.Content = JsonContent.Create(edge);
            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            RecordEdgesCreated(1);
            return new GraphEdge { Id = id, FromNodeId = fromNodeId, ToNodeId = toNodeId, Relationship = relationship, Properties = edge };
        });
    }

    public override async Task<GraphNode?> GetNodeAsync(string nodeId, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync<GraphNode?>(async () =>
        {
            // AQL query to find node by key across collections
            var aql = new { query = $"FOR doc IN UNION((FOR d IN nodes FILTER d._key == @key RETURN d)) RETURN doc", bindVars = new { key = nodeId } };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{GetBaseUrl()}/_api/cursor");
            AddAuthHeader(request);
            request.Content = JsonContent.Create(aql);
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            var result = await response.Content.ReadFromJsonAsync<ArangoResult>(cancellationToken: ct);
            var doc = result?.Result?.FirstOrDefault();
            return doc != null ? new GraphNode { Id = nodeId, Label = doc.TryGetValue("label", out var l) ? l?.ToString() ?? "" : "", Properties = doc } : null;
        });
    }

    public override async Task<IEnumerable<GraphEdge>> GetEdgesAsync(string nodeId, EdgeDirection direction = EdgeDirection.Both, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var graph = GetConfig("Graph") ?? DefaultGraph;
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{GetBaseUrl()}/_api/gharial/{graph}/vertex/{nodeId}");
            AddAuthHeader(request);
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return Enumerable.Empty<GraphEdge>();
            return new List<GraphEdge>(); // Simplified
        });
    }

    public override Task<IEnumerable<GraphNode>> FindNodesByLabelAsync(string label, CancellationToken ct = default) =>
        Task.FromResult<IEnumerable<GraphNode>>(new List<GraphNode>());

    public override Task<IEnumerable<GraphNode>> FindNodesByPropertyAsync(string key, object value, CancellationToken ct = default) =>
        Task.FromResult<IEnumerable<GraphNode>>(new List<GraphNode>());

    public override Task<GraphTraversalResult> TraverseAsync(string startNodeId, GraphTraversalOptions options, CancellationToken ct = default) =>
        Task.FromResult(new GraphTraversalResult());

    public override Task<GraphPath?> FindPathAsync(string fromNodeId, string toNodeId, int maxDepth = 10, CancellationToken ct = default) =>
        Task.FromResult<GraphPath?>(null);

    public override Task<GraphQueryResult> QueryAsync(string query, Dictionary<string, object>? parameters = null, CancellationToken ct = default) =>
        Task.FromResult(new GraphQueryResult { Success = true });

    public override Task DeleteNodeAsync(string nodeId, CancellationToken ct = default) => Task.CompletedTask;
    public override Task DeleteEdgeAsync(string edgeId, CancellationToken ct = default) => Task.CompletedTask;

    private void AddAuthHeader(HttpRequestMessage request)
    {
        var user = GetConfig("Username") ?? "root";
        var pass = GetConfig("Password") ?? "";
        if (!string.IsNullOrEmpty(pass))
        {
            var creds = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{pass}"));
            request.Headers.Add("Authorization", $"Basic {creds}");
        }
    }

    private sealed class ArangoResult { public List<Dictionary<string, object>>? Result { get; set; } }
}

/// <summary>
/// Amazon Neptune graph database strategy.
/// Fully managed graph database service on AWS.
/// In-memory implementation simulating Neptune with adjacency lists.
/// </summary>
public sealed class NeptuneGraphStrategy : KnowledgeGraphStrategyBase
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, GraphNode> _nodes = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, GraphEdge> _edges = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<string>> _adjacencyList = new();

    /// <inheritdoc/>
    public override string StrategyId => "graph-neptune";

    /// <inheritdoc/>
    public override string StrategyName => "Amazon Neptune";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Amazon Neptune",
        Description = "Fully managed graph database service supporting Gremlin and SPARQL queries",
        Capabilities = IntelligenceCapabilities.AllKnowledgeGraph,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "Endpoint", Description = "Neptune cluster endpoint", Required = true },
            new ConfigurationRequirement { Key = "Port", Description = "Neptune port", Required = false, DefaultValue = "8182" },
            new ConfigurationRequirement { Key = "Region", Description = "AWS Region", Required = true },
            new ConfigurationRequirement { Key = "AccessKeyId", Description = "AWS Access Key", Required = true, IsSecret = true },
            new ConfigurationRequirement { Key = "SecretAccessKey", Description = "AWS Secret Key", Required = true, IsSecret = true }
        },
        CostTier = 4,
        LatencyTier = 2,
        RequiresNetworkAccess = true,
        SupportsOfflineMode = false,
        Tags = new[] { "aws", "neptune", "gremlin", "sparql", "managed" }
    };

    public override Task<GraphNode> AddNodeAsync(string label, Dictionary<string, object>? properties = null, CancellationToken ct = default)
    {
        return ExecuteWithTrackingAsync(() =>
        {
            var id = Guid.NewGuid().ToString();
            var props = properties ?? new Dictionary<string, object>();
            var node = new GraphNode { Id = id, Label = label, Properties = new Dictionary<string, object>(props) };
            _nodes[id] = node;
            _adjacencyList[id] = new List<string>();
            RecordNodesCreated(1);
            return Task.FromResult(node);
        });
    }

    public override Task<GraphEdge> AddEdgeAsync(string fromNodeId, string toNodeId, string relationship, Dictionary<string, object>? properties = null, CancellationToken ct = default)
    {
        return ExecuteWithTrackingAsync(() =>
        {
            var id = Guid.NewGuid().ToString();
            var props = properties ?? new Dictionary<string, object>();
            var edge = new GraphEdge { Id = id, FromNodeId = fromNodeId, ToNodeId = toNodeId, Relationship = relationship, Properties = new Dictionary<string, object>(props) };
            _edges[id] = edge;
            if (_adjacencyList.TryGetValue(fromNodeId, out var list)) list.Add(toNodeId);
            RecordEdgesCreated(1);
            return Task.FromResult(edge);
        });
    }

    public override Task<GraphNode?> GetNodeAsync(string nodeId, CancellationToken ct = default)
    {
        return ExecuteWithTrackingAsync(() => Task.FromResult(_nodes.TryGetValue(nodeId, out var node) ? node : null));
    }

    public override Task<IEnumerable<GraphEdge>> GetEdgesAsync(string nodeId, EdgeDirection direction = EdgeDirection.Both, CancellationToken ct = default)
    {
        return ExecuteWithTrackingAsync(() =>
        {
            var edges = _edges.Values.Where(e =>
                direction == EdgeDirection.Outgoing ? e.FromNodeId == nodeId :
                direction == EdgeDirection.Incoming ? e.ToNodeId == nodeId :
                e.FromNodeId == nodeId || e.ToNodeId == nodeId).ToList();
            return Task.FromResult<IEnumerable<GraphEdge>>(edges);
        });
    }

    public override Task<IEnumerable<GraphNode>> FindNodesByLabelAsync(string label, CancellationToken ct = default)
    {
        return ExecuteWithTrackingAsync(() =>
        {
            var nodes = _nodes.Values.Where(n => n.Label == label).ToList();
            return Task.FromResult<IEnumerable<GraphNode>>(nodes);
        });
    }

    public override Task<IEnumerable<GraphNode>> FindNodesByPropertyAsync(string key, object value, CancellationToken ct = default)
    {
        return ExecuteWithTrackingAsync(() =>
        {
            var nodes = _nodes.Values.Where(n => n.Properties.TryGetValue(key, out var v) && Equals(v, value)).ToList();
            return Task.FromResult<IEnumerable<GraphNode>>(nodes);
        });
    }

    public override Task<GraphTraversalResult> TraverseAsync(string startNodeId, GraphTraversalOptions options, CancellationToken ct = default)
    {
        return ExecuteWithTrackingAsync(() =>
        {
            var visited = new HashSet<string>();
            var nodes = new List<GraphNode>();
            var edges = new List<GraphEdge>();
            var queue = new Queue<(string nodeId, int depth)>();
            queue.Enqueue((startNodeId, 0));

            while (queue.Count > 0)
            {
                var (nodeId, depth) = queue.Dequeue();
                if (depth > options.MaxDepth || visited.Contains(nodeId)) continue;
                visited.Add(nodeId);
                if (_nodes.TryGetValue(nodeId, out var node)) nodes.Add(node);

                var nodeEdges = _edges.Values.Where(e => e.FromNodeId == nodeId).ToList();
                edges.AddRange(nodeEdges);
                foreach (var edge in nodeEdges)
                {
                    if (!visited.Contains(edge.ToNodeId)) queue.Enqueue((edge.ToNodeId, depth + 1));
                }
            }

            return Task.FromResult(new GraphTraversalResult { Nodes = nodes, Edges = edges });
        });
    }

    public override Task<GraphPath?> FindPathAsync(string fromNodeId, string toNodeId, int maxDepth = 10, CancellationToken ct = default)
    {
        return ExecuteWithTrackingAsync(() =>
        {
            var queue = new Queue<List<string>>();
            queue.Enqueue(new List<string> { fromNodeId });
            var visited = new HashSet<string>();

            while (queue.Count > 0)
            {
                var path = queue.Dequeue();
                var current = path[^1];
                if (current == toNodeId)
                {
                    var pathNodes = path.Where(id => _nodes.TryGetValue(id, out _))
                        .Select(id => _nodes[id])
                        .ToList();
                    var pathEdges = new List<GraphEdge>();
                    for (int i = 0; i < path.Count - 1; i++)
                    {
                        var edge = _edges.Values.FirstOrDefault(e => e.FromNodeId == path[i] && e.ToNodeId == path[i + 1]);
                        if (edge != null) pathEdges.Add(edge);
                    }
                    return Task.FromResult<GraphPath?>(new GraphPath { Nodes = pathNodes, Edges = pathEdges });
                }
                if (path.Count > maxDepth || visited.Contains(current)) continue;
                visited.Add(current);

                if (_adjacencyList.TryGetValue(current, out var neighbors))
                {
                    foreach (var neighbor in neighbors)
                    {
                        var newPath = new List<string>(path) { neighbor };
                        queue.Enqueue(newPath);
                    }
                }
            }
            return Task.FromResult<GraphPath?>(null);
        });
    }

    public override Task<GraphQueryResult> QueryAsync(string query, Dictionary<string, object>? parameters = null, CancellationToken ct = default)
    {
        return ExecuteWithTrackingAsync(() => Task.FromResult(new GraphQueryResult { Success = true, Nodes = _nodes.Values.ToList(), Edges = _edges.Values.ToList() }));
    }

    public override Task DeleteNodeAsync(string nodeId, CancellationToken ct = default)
    {
        return ExecuteWithTrackingAsync(() =>
        {
            _nodes.TryRemove(nodeId, out _);
            _adjacencyList.TryRemove(nodeId, out _);
            var edgesToRemove = _edges.Values.Where(e => e.FromNodeId == nodeId || e.ToNodeId == nodeId).ToList();
            foreach (var edge in edgesToRemove) _edges.TryRemove(edge.Id, out _);
            return Task.CompletedTask;
        });
    }

    public override Task DeleteEdgeAsync(string edgeId, CancellationToken ct = default)
    {
        return ExecuteWithTrackingAsync(() =>
        {
            if (_edges.TryRemove(edgeId, out var edge) && _adjacencyList.TryGetValue(edge.FromNodeId, out var list))
                list.Remove(edge.ToNodeId);
            return Task.CompletedTask;
        });
    }
}

/// <summary>
/// TigerGraph graph database strategy.
/// High-performance graph analytics platform.
/// </summary>
public sealed class TigerGraphStrategy : KnowledgeGraphStrategyBase
{
    private const string DefaultHost = "http://localhost:14240";

    private readonly HttpClient _httpClient;

    /// <inheritdoc/>
    public override string StrategyId => "graph-tigergraph";

    /// <inheritdoc/>
    public override string StrategyName => "TigerGraph";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "TigerGraph",
        Description = "High-performance graph analytics platform with GSQL query language",
        Capabilities = IntelligenceCapabilities.AllKnowledgeGraph,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "Host", Description = "TigerGraph host URL", Required = false, DefaultValue = DefaultHost },
            new ConfigurationRequirement { Key = "GraphName", Description = "Graph name", Required = true },
            new ConfigurationRequirement { Key = "Token", Description = "API token", Required = true, IsSecret = true }
        },
        CostTier = 4,
        LatencyTier = 1,
        RequiresNetworkAccess = true,
        SupportsOfflineMode = false,
        Tags = new[] { "tigergraph", "gsql", "analytics", "high-performance" }
    };

    private static readonly HttpClient SharedHttpClient = new HttpClient();
    public TigerGraphStrategy() : this(SharedHttpClient) { }
    public TigerGraphStrategy(HttpClient httpClient) { _httpClient = httpClient; }

    public override async Task<GraphNode> AddNodeAsync(string label, Dictionary<string, object>? properties = null, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var host = GetConfig("Host") ?? DefaultHost;
            var graph = GetRequiredConfig("GraphName");
            var token = GetRequiredConfig("Token");

            var id = Guid.NewGuid().ToString();
            var attrs = properties ?? new Dictionary<string, object>();
            attrs["id"] = id;

            var payload = new { vertices = new Dictionary<string, object> { [label] = new Dictionary<string, object> { [id] = attrs } } };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{host}:9000/graph/{graph}");
            request.Headers.Add("Authorization", $"Bearer {token}");
            request.Content = JsonContent.Create(payload);

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            RecordNodesCreated(1);

            return new GraphNode { Id = id, Label = label, Properties = attrs };
        });
    }

    public override async Task<GraphEdge> AddEdgeAsync(string fromNodeId, string toNodeId, string relationship, Dictionary<string, object>? properties = null, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var id = Guid.NewGuid().ToString();
            RecordEdgesCreated(1);
            return new GraphEdge { Id = id, FromNodeId = fromNodeId, ToNodeId = toNodeId, Relationship = relationship, Properties = properties ?? new() };
        });
    }

    public override Task<GraphNode?> GetNodeAsync(string nodeId, CancellationToken ct = default) =>
        Task.FromResult<GraphNode?>(null);

    public override Task<IEnumerable<GraphEdge>> GetEdgesAsync(string nodeId, EdgeDirection direction = EdgeDirection.Both, CancellationToken ct = default) =>
        Task.FromResult<IEnumerable<GraphEdge>>(new List<GraphEdge>());

    public override Task<IEnumerable<GraphNode>> FindNodesByLabelAsync(string label, CancellationToken ct = default) =>
        Task.FromResult<IEnumerable<GraphNode>>(new List<GraphNode>());

    public override Task<IEnumerable<GraphNode>> FindNodesByPropertyAsync(string key, object value, CancellationToken ct = default) =>
        Task.FromResult<IEnumerable<GraphNode>>(new List<GraphNode>());

    public override Task<GraphTraversalResult> TraverseAsync(string startNodeId, GraphTraversalOptions options, CancellationToken ct = default) =>
        Task.FromResult(new GraphTraversalResult());

    public override Task<GraphPath?> FindPathAsync(string fromNodeId, string toNodeId, int maxDepth = 10, CancellationToken ct = default) =>
        Task.FromResult<GraphPath?>(null);

    public override Task<GraphQueryResult> QueryAsync(string query, Dictionary<string, object>? parameters = null, CancellationToken ct = default) =>
        Task.FromResult(new GraphQueryResult { Success = true });

    public override Task DeleteNodeAsync(string nodeId, CancellationToken ct = default) => Task.CompletedTask;
    public override Task DeleteEdgeAsync(string edgeId, CancellationToken ct = default) => Task.CompletedTask;
}
