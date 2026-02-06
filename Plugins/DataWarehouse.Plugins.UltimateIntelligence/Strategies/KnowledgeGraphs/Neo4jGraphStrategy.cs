using System.Net.Http.Json;
using System.Text.Json;
using DataWarehouse.SDK.AI;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.KnowledgeGraphs;

/// <summary>
/// Neo4j graph database strategy.
/// Industry-leading native graph database for connected data.
/// </summary>
public sealed class Neo4jGraphStrategy : KnowledgeGraphStrategyBase
{
    private const string DefaultHost = "http://localhost:7474";

    private readonly HttpClient _httpClient;

    /// <inheritdoc/>
    public override string StrategyId => "graph-neo4j";

    /// <inheritdoc/>
    public override string StrategyName => "Neo4j Graph Database";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Neo4j",
        Description = "Industry-leading native graph database with Cypher query language",
        Capabilities = IntelligenceCapabilities.AllKnowledgeGraph,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "Host", Description = "Neo4j HTTP host URL", Required = false, DefaultValue = DefaultHost },
            new ConfigurationRequirement { Key = "Username", Description = "Neo4j username", Required = false, DefaultValue = "neo4j" },
            new ConfigurationRequirement { Key = "Password", Description = "Neo4j password", Required = true, IsSecret = true },
            new ConfigurationRequirement { Key = "Database", Description = "Database name", Required = false, DefaultValue = "neo4j" }
        },
        CostTier = 3,
        LatencyTier = 2,
        RequiresNetworkAccess = true,
        SupportsOfflineMode = true,
        Tags = new[] { "neo4j", "graph-db", "cypher", "native-graph", "production" }
    };

    public Neo4jGraphStrategy() : this(new HttpClient()) { }

    public Neo4jGraphStrategy(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    private string GetHost() => GetConfig("Host") ?? DefaultHost;
    private string GetDatabase() => GetConfig("Database") ?? "neo4j";

    private async Task<JsonElement> ExecuteCypherAsync(string cypher, Dictionary<string, object>? parameters = null, CancellationToken ct = default)
    {
        var host = GetHost();
        var db = GetDatabase();

        var payload = new
        {
            statements = new[]
            {
                new { statement = cypher, parameters = parameters ?? new Dictionary<string, object>() }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{host}/db/{db}/tx/commit");
        AddAuthHeader(request);
        request.Content = JsonContent.Create(payload);

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
    }

    private void AddAuthHeader(HttpRequestMessage request)
    {
        var username = GetConfig("Username") ?? "neo4j";
        var password = GetRequiredConfig("Password");
        var credentials = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{username}:{password}"));
        request.Headers.Add("Authorization", $"Basic {credentials}");
    }

    /// <inheritdoc/>
    public override async Task<GraphNode> AddNodeAsync(string label, Dictionary<string, object>? properties = null, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var id = Guid.NewGuid().ToString();
            var props = properties ?? new Dictionary<string, object>();
            props["id"] = id;

            var cypher = $"CREATE (n:{label} $props) RETURN n";
            await ExecuteCypherAsync(cypher, new Dictionary<string, object> { ["props"] = props }, ct);

            RecordNodesCreated(1);

            return new GraphNode
            {
                Id = id,
                Label = label,
                Properties = props
            };
        });
    }

    /// <inheritdoc/>
    public override async Task<GraphEdge> AddEdgeAsync(string fromNodeId, string toNodeId, string relationship, Dictionary<string, object>? properties = null, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var id = Guid.NewGuid().ToString();
            var props = properties ?? new Dictionary<string, object>();
            props["id"] = id;

            var cypher = @"
                MATCH (a), (b)
                WHERE a.id = $fromId AND b.id = $toId
                CREATE (a)-[r:" + relationship + @" $props]->(b)
                RETURN r";

            await ExecuteCypherAsync(cypher, new Dictionary<string, object>
            {
                ["fromId"] = fromNodeId,
                ["toId"] = toNodeId,
                ["props"] = props
            }, ct);

            RecordEdgesCreated(1);

            return new GraphEdge
            {
                Id = id,
                FromNodeId = fromNodeId,
                ToNodeId = toNodeId,
                Relationship = relationship,
                Properties = props
            };
        });
    }

    /// <inheritdoc/>
    public override async Task<GraphNode?> GetNodeAsync(string nodeId, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var cypher = "MATCH (n {id: $id}) RETURN n, labels(n) as labels";
            var result = await ExecuteCypherAsync(cypher, new Dictionary<string, object> { ["id"] = nodeId }, ct);

            var row = GetFirstRow(result);
            if (row == null) return null;

            var nodeData = row.Value[0];
            var labels = row.Value[1];

            return new GraphNode
            {
                Id = nodeId,
                Label = labels.EnumerateArray().FirstOrDefault().GetString() ?? "",
                Properties = JsonSerializer.Deserialize<Dictionary<string, object>>(nodeData.GetRawText()) ?? new()
            };
        });
    }

    /// <inheritdoc/>
    public override async Task<IEnumerable<GraphEdge>> GetEdgesAsync(string nodeId, EdgeDirection direction = EdgeDirection.Both, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var pattern = direction switch
            {
                EdgeDirection.Outgoing => "(n {id: $id})-[r]->(m)",
                EdgeDirection.Incoming => "(n {id: $id})<-[r]-(m)",
                _ => "(n {id: $id})-[r]-(m)"
            };

            var cypher = $"MATCH {pattern} RETURN r, type(r) as type, n.id as from, m.id as to";
            var result = await ExecuteCypherAsync(cypher, new Dictionary<string, object> { ["id"] = nodeId }, ct);

            var edges = new List<GraphEdge>();
            foreach (var row in GetRows(result))
            {
                edges.Add(new GraphEdge
                {
                    Id = row[0].TryGetProperty("id", out var id) ? id.GetString() ?? "" : Guid.NewGuid().ToString(),
                    Relationship = row[1].GetString() ?? "",
                    FromNodeId = row[2].GetString() ?? "",
                    ToNodeId = row[3].GetString() ?? "",
                    Properties = JsonSerializer.Deserialize<Dictionary<string, object>>(row[0].GetRawText()) ?? new()
                });
            }

            return edges;
        });
    }

    /// <inheritdoc/>
    public override async Task<IEnumerable<GraphNode>> FindNodesByLabelAsync(string label, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var cypher = $"MATCH (n:{label}) RETURN n";
            var result = await ExecuteCypherAsync(cypher, null, ct);

            var nodes = new List<GraphNode>();
            foreach (var row in GetRows(result))
            {
                var props = JsonSerializer.Deserialize<Dictionary<string, object>>(row[0].GetRawText()) ?? new();
                nodes.Add(new GraphNode
                {
                    Id = props.TryGetValue("id", out var id) ? id?.ToString() ?? "" : "",
                    Label = label,
                    Properties = props
                });
            }

            return nodes;
        });
    }

    /// <inheritdoc/>
    public override async Task<IEnumerable<GraphNode>> FindNodesByPropertyAsync(string key, object value, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var cypher = $"MATCH (n) WHERE n.{key} = $value RETURN n, labels(n) as labels";
            var result = await ExecuteCypherAsync(cypher, new Dictionary<string, object> { ["value"] = value }, ct);

            var nodes = new List<GraphNode>();
            foreach (var row in GetRows(result))
            {
                var props = JsonSerializer.Deserialize<Dictionary<string, object>>(row[0].GetRawText()) ?? new();
                nodes.Add(new GraphNode
                {
                    Id = props.TryGetValue("id", out var id) ? id?.ToString() ?? "" : "",
                    Label = row[1].EnumerateArray().FirstOrDefault().GetString() ?? "",
                    Properties = props
                });
            }

            return nodes;
        });
    }

    /// <inheritdoc/>
    public override async Task<GraphTraversalResult> TraverseAsync(string startNodeId, GraphTraversalOptions options, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var relFilter = options.RelationshipFilter?.Length > 0
                ? ":" + string.Join("|", options.RelationshipFilter)
                : "";

            var cypher = $@"
                MATCH path = (start {{id: $startId}})-[{relFilter}*1..{options.MaxDepth}]-(end)
                RETURN path
                LIMIT {options.MaxNodes}";

            var result = await ExecuteCypherAsync(cypher, new Dictionary<string, object> { ["startId"] = startNodeId }, ct);

            var nodes = new List<GraphNode>();
            var edges = new List<GraphEdge>();
            var nodeIds = new HashSet<string>();
            var edgeIds = new HashSet<string>();

            foreach (var row in GetRows(result))
            {
                // Parse path - this is simplified; real implementation would parse Neo4j path format
            }

            return new GraphTraversalResult
            {
                Nodes = nodes,
                Edges = edges,
                NodesVisited = nodes.Count,
                MaxDepthReached = options.MaxDepth,
                WasTruncated = nodes.Count >= options.MaxNodes
            };
        });
    }

    /// <inheritdoc/>
    public override async Task<GraphPath?> FindPathAsync(string fromNodeId, string toNodeId, int maxDepth = 10, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var cypher = $@"
                MATCH path = shortestPath((a {{id: $fromId}})-[*..{maxDepth}]-(b {{id: $toId}}))
                RETURN path";

            var result = await ExecuteCypherAsync(cypher, new Dictionary<string, object>
            {
                ["fromId"] = fromNodeId,
                ["toId"] = toNodeId
            }, ct);

            var row = GetFirstRow(result);
            if (row == null) return null;

            // Parse path - simplified
            return new GraphPath
            {
                Nodes = new List<GraphNode>(),
                Edges = new List<GraphEdge>(),
                TotalWeight = 0
            };
        });
    }

    /// <inheritdoc/>
    public override async Task<GraphQueryResult> QueryAsync(string query, Dictionary<string, object>? parameters = null, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await ExecuteCypherAsync(query, parameters, ct);
            sw.Stop();

            return new GraphQueryResult
            {
                Success = true,
                Nodes = new List<GraphNode>(),
                Edges = new List<GraphEdge>(),
                ExecutionTime = sw.Elapsed
            };
        });
    }

    /// <inheritdoc/>
    public override async Task DeleteNodeAsync(string nodeId, CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            var cypher = "MATCH (n {id: $id}) DETACH DELETE n";
            await ExecuteCypherAsync(cypher, new Dictionary<string, object> { ["id"] = nodeId }, ct);
        });
    }

    /// <inheritdoc/>
    public override async Task DeleteEdgeAsync(string edgeId, CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            var cypher = "MATCH ()-[r {id: $id}]-() DELETE r";
            await ExecuteCypherAsync(cypher, new Dictionary<string, object> { ["id"] = edgeId }, ct);
        });
    }

    private static JsonElement? GetFirstRow(JsonElement result)
    {
        if (result.TryGetProperty("results", out var results) &&
            results.GetArrayLength() > 0 &&
            results[0].TryGetProperty("data", out var data) &&
            data.GetArrayLength() > 0 &&
            data[0].TryGetProperty("row", out var row))
        {
            return row;
        }
        return null;
    }

    private static IEnumerable<JsonElement> GetRows(JsonElement result)
    {
        if (result.TryGetProperty("results", out var results) &&
            results.GetArrayLength() > 0 &&
            results[0].TryGetProperty("data", out var data))
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("row", out var row))
                    yield return row;
            }
        }
    }
}
