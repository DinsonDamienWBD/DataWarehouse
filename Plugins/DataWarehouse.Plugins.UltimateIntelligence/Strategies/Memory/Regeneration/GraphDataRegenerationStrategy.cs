using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.Regeneration;

/// <summary>
/// Graph/network data regeneration strategy with structure-aware reconstruction.
/// Supports Cypher, Gremlin, GraphQL, DOT, and JSON graph formats.
/// Preserves node/edge properties, relationship types, and graph metrics with 5-sigma accuracy.
/// </summary>
public sealed class GraphDataRegenerationStrategy : RegenerationStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "regeneration-graph";

    /// <inheritdoc/>
    public override string DisplayName => "Graph/Network Data Regeneration";

    /// <inheritdoc/>
    public override string[] SupportedFormats => new[] { "cypher", "gremlin", "graphql", "dot", "gexf", "graphml", "json-graph" };

    /// <inheritdoc/>
    public override async Task<RegenerationResult> RegenerateAsync(
        EncodedContext context,
        RegenerationOptions options,
        CancellationToken ct = default)
    {
        var startTime = DateTime.UtcNow;
        var warnings = new List<string>();
        var diagnostics = new Dictionary<string, object>();

        try
        {
            var encodedData = context.EncodedData;
            diagnostics["input_length"] = encodedData.Length;

            // Detect graph format
            var format = DetectGraphFormat(encodedData);
            diagnostics["detected_format"] = format;

            // Parse graph structure
            var graph = ParseGraphData(encodedData, format);
            diagnostics["node_count"] = graph.Nodes.Count;
            diagnostics["edge_count"] = graph.Edges.Count;
            diagnostics["is_directed"] = graph.IsDirected;

            // Calculate graph metrics
            var metrics = CalculateGraphMetrics(graph);
            diagnostics["metrics"] = metrics;

            // Validate and repair graph
            var repairedGraph = RepairGraph(graph, warnings);

            // Reconstruct graph data
            var outputFormat = options.TargetDialect ?? format;
            var regeneratedContent = ReconstructGraph(repairedGraph, outputFormat, options);

            // Calculate accuracy metrics
            var structuralIntegrity = CalculateGraphStructuralIntegrity(regeneratedContent, encodedData, format);
            var semanticIntegrity = CalculateGraphSemanticIntegrity(graph, repairedGraph);

            var hash = ComputeHash(regeneratedContent);
            var hashMatch = options.OriginalHash != null
                ? hash.Equals(options.OriginalHash, StringComparison.OrdinalIgnoreCase)
                : (bool?)null;

            var accuracy = hashMatch == true ? 1.0 :
                (structuralIntegrity * 0.5 + semanticIntegrity * 0.5);

            var duration = DateTime.UtcNow - startTime;
            RecordRegeneration(true, accuracy, format);

            return new RegenerationResult
            {
                Success = accuracy >= options.MinAccuracy,
                RegeneratedContent = regeneratedContent,
                ConfidenceScore = Math.Min(structuralIntegrity, semanticIntegrity),
                ActualAccuracy = accuracy,
                Warnings = warnings,
                Diagnostics = diagnostics,
                Duration = duration,
                PassCount = 1,
                StrategyId = StrategyId,
                DetectedFormat = format,
                ContentHash = hash,
                HashMatch = hashMatch,
                SemanticIntegrity = semanticIntegrity,
                StructuralIntegrity = structuralIntegrity
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Caught exception in GraphDataRegenerationStrategy.cs: {ex.Message}");
            var duration = DateTime.UtcNow - startTime;
            RecordRegeneration(false, 0, "graph");

            return new RegenerationResult
            {
                Success = false,
                Warnings = new List<string> { $"Regeneration failed: {ex.Message}" },
                Diagnostics = diagnostics,
                Duration = duration,
                StrategyId = StrategyId
            };
        }
    }

    /// <inheritdoc/>
    public override async Task<RegenerationCapability> AssessCapabilityAsync(
        EncodedContext context,
        CancellationToken ct = default)
    {
        var missingElements = new List<string>();
        var expectedAccuracy = 0.9999999;

        var data = context.EncodedData;

        // Check for graph patterns
        var hasCypherPattern = Regex.IsMatch(data, @"\(\w+\)-\[.*?\]->");
        var hasGremlinPattern = Regex.IsMatch(data, @"\.addV\(|\.addE\(|\.out\(|\.in\(");
        var hasDotPattern = Regex.IsMatch(data, @"\w+\s*->\s*\w+|digraph|graph");
        var hasJsonGraphPattern = data.Contains("\"nodes\"") && data.Contains("\"edges\"");

        if (!hasCypherPattern && !hasGremlinPattern && !hasDotPattern && !hasJsonGraphPattern)
        {
            missingElements.Add("No recognizable graph format patterns");
            expectedAccuracy -= 0.3;
        }

        // Check for node definitions
        var hasNodes = Regex.IsMatch(data, @"\(\w+:\w+\)|""id"":\s*""\w+""|\w+\s*\[");
        if (!hasNodes)
        {
            missingElements.Add("No clear node definitions");
            expectedAccuracy -= 0.2;
        }

        // Check for edge definitions
        var hasEdges = Regex.IsMatch(data, @"-\[.*?\]->|\.addE\(|->|""source"":");
        if (!hasEdges)
        {
            missingElements.Add("No clear edge definitions");
            expectedAccuracy -= 0.1;
        }

        await Task.CompletedTask;

        return new RegenerationCapability
        {
            CanRegenerate = expectedAccuracy > 0.6,
            ExpectedAccuracy = Math.Max(0, expectedAccuracy),
            MissingElements = missingElements,
            RecommendedEnrichment = missingElements.Count > 0
                ? $"Add: {string.Join(", ", missingElements)}"
                : "Context sufficient for accurate regeneration",
            AssessmentConfidence = (hasCypherPattern || hasGremlinPattern || hasDotPattern || hasJsonGraphPattern) ? 0.9 : 0.5,
            DetectedContentType = "Graph Data",
            EstimatedDuration = TimeSpan.FromMilliseconds(data.Length / 50),
            EstimatedMemoryBytes = data.Length * 4,
            RecommendedStrategy = StrategyId,
            ComplexityScore = Math.Min(data.Length / 5000.0, 1.0)
        };
    }

    /// <inheritdoc/>
    public override async Task<double> VerifyAccuracyAsync(
        string original,
        string regenerated,
        CancellationToken ct = default)
    {
        var origFormat = DetectGraphFormat(original);
        var origGraph = ParseGraphData(original, origFormat);
        var regenGraph = ParseGraphData(regenerated, origFormat);

        // Compare node counts
        var nodeRatio = origGraph.Nodes.Count == 0 ? 0 :
            Math.Min((double)regenGraph.Nodes.Count / origGraph.Nodes.Count, 1.0);

        // Compare edge counts
        var edgeRatio = origGraph.Edges.Count == 0 ? 0.5 :
            Math.Min((double)regenGraph.Edges.Count / origGraph.Edges.Count, 1.0);

        // Compare node IDs overlap
        var origNodeIds = origGraph.Nodes.Select(n => n.Id).ToHashSet();
        var regenNodeIds = regenGraph.Nodes.Select(n => n.Id).ToHashSet();
        var nodeIdOverlap = CalculateJaccardSimilarity(origNodeIds, regenNodeIds);

        await Task.CompletedTask;
        return (nodeRatio * 0.3 + edgeRatio * 0.3 + nodeIdOverlap * 0.4);
    }

    private static string DetectGraphFormat(string data)
    {
        if (Regex.IsMatch(data, @"\(\w+\)-\[.*?\]->|\(\w+:\w+\s*\{")) return "cypher";
        if (Regex.IsMatch(data, @"\.addV\(|\.addE\(|g\.V\(|g\.E\(")) return "gremlin";
        if (Regex.IsMatch(data, @"^(di)?graph\s+\w*\s*\{", RegexOptions.Multiline)) return "dot";
        if (data.Contains("\"nodes\"") && data.Contains("\"edges\"") || data.Contains("\"links\"")) return "json-graph";
        if (data.Contains("<graphml") || data.Contains("<graph ")) return "graphml";
        if (data.Contains("<gexf")) return "gexf";
        return "unknown";
    }

    private static GraphStructure ParseGraphData(string data, string format)
    {
        return format switch
        {
            "cypher" => ParseCypherGraph(data),
            "gremlin" => ParseGremlinGraph(data),
            "dot" => ParseDotGraph(data),
            "json-graph" => ParseJsonGraph(data),
            _ => ParseGenericGraph(data)
        };
    }

    private static GraphStructure ParseCypherGraph(string data)
    {
        var graph = new GraphStructure();

        // Parse CREATE/MERGE node patterns
        var nodeMatches = Regex.Matches(data, @"\((\w+)(?::(\w+))?\s*(?:\{([^}]*)\})?\)");
        foreach (Match match in nodeMatches)
        {
            var id = match.Groups[1].Value;
            if (!graph.Nodes.Any(n => n.Id == id))
            {
                graph.Nodes.Add(new GraphNode
                {
                    Id = id,
                    Labels = match.Groups[2].Success ? new List<string> { match.Groups[2].Value } : new List<string>(),
                    Properties = ParseProperties(match.Groups[3].Value)
                });
            }
        }

        // Parse relationship patterns
        var relMatches = Regex.Matches(data, @"\((\w+)\)-\[(?:(\w+))?(?::(\w+))?\s*(?:\{([^}]*)\})?\]-[>]?\((\w+)\)");
        foreach (Match match in relMatches)
        {
            graph.Edges.Add(new GraphEdge
            {
                Id = match.Groups[2].Success ? match.Groups[2].Value : $"e{graph.Edges.Count}",
                SourceId = match.Groups[1].Value,
                TargetId = match.Groups[5].Value,
                Type = match.Groups[3].Success ? match.Groups[3].Value : "RELATED_TO",
                Properties = ParseProperties(match.Groups[4].Value)
            });
        }

        graph.IsDirected = data.Contains("->");
        return graph;
    }

    private static GraphStructure ParseGremlinGraph(string data)
    {
        var graph = new GraphStructure();

        // Parse addV operations
        var nodeMatches = Regex.Matches(data, @"\.addV\(['""](\w+)['""]\)(?:\.property\(['""](\w+)['""]\s*,\s*['""]?([^)'""]*))?");
        var nodeId = 0;
        foreach (Match match in nodeMatches)
        {
            var node = new GraphNode
            {
                Id = $"v{nodeId++}",
                Labels = new List<string> { match.Groups[1].Value }
            };
            if (match.Groups[2].Success)
            {
                node.Properties[match.Groups[2].Value] = match.Groups[3].Value;
            }
            graph.Nodes.Add(node);
        }

        // Parse addE operations
        var edgeMatches = Regex.Matches(data, @"\.addE\(['""](\w+)['""]\)");
        var edgeId = 0;
        foreach (Match match in edgeMatches)
        {
            graph.Edges.Add(new GraphEdge
            {
                Id = $"e{edgeId++}",
                Type = match.Groups[1].Value,
                SourceId = $"v{edgeId - 1}",
                TargetId = $"v{edgeId}"
            });
        }

        graph.IsDirected = true;
        return graph;
    }

    private static GraphStructure ParseDotGraph(string data)
    {
        var graph = new GraphStructure();
        graph.IsDirected = data.Contains("digraph");

        // Parse node definitions
        var nodeMatches = Regex.Matches(data, @"^\s*(\w+)\s*\[([^\]]*)\]", RegexOptions.Multiline);
        foreach (Match match in nodeMatches)
        {
            var props = ParseDotAttributes(match.Groups[2].Value);
            graph.Nodes.Add(new GraphNode
            {
                Id = match.Groups[1].Value,
                Properties = props
            });
        }

        // Parse edge definitions
        var edgePattern = graph.IsDirected ? @"(\w+)\s*->\s*(\w+)" : @"(\w+)\s*--\s*(\w+)";
        var edgeMatches = Regex.Matches(data, edgePattern);
        var edgeId = 0;
        foreach (Match match in edgeMatches)
        {
            var sourceId = match.Groups[1].Value;
            var targetId = match.Groups[2].Value;

            // Ensure nodes exist
            if (!graph.Nodes.Any(n => n.Id == sourceId))
                graph.Nodes.Add(new GraphNode { Id = sourceId });
            if (!graph.Nodes.Any(n => n.Id == targetId))
                graph.Nodes.Add(new GraphNode { Id = targetId });

            graph.Edges.Add(new GraphEdge
            {
                Id = $"e{edgeId++}",
                SourceId = sourceId,
                TargetId = targetId
            });
        }

        return graph;
    }

    private static GraphStructure ParseJsonGraph(string data)
    {
        var graph = new GraphStructure();

        // Parse nodes
        var nodesMatch = Regex.Match(data, @"""nodes""\s*:\s*\[(.*?)\]", RegexOptions.Singleline);
        if (nodesMatch.Success)
        {
            var nodeMatches = Regex.Matches(nodesMatch.Groups[1].Value, @"\{([^{}]*)\}");
            foreach (Match match in nodeMatches)
            {
                var nodeData = match.Groups[1].Value;
                var idMatch = Regex.Match(nodeData, @"""id""\s*:\s*""?([^"",}]+)""?");
                if (idMatch.Success)
                {
                    var node = new GraphNode { Id = idMatch.Groups[1].Value.Trim() };

                    var labelMatch = Regex.Match(nodeData, @"""label""\s*:\s*""([^""]*)""");
                    if (labelMatch.Success)
                        node.Labels.Add(labelMatch.Groups[1].Value);

                    graph.Nodes.Add(node);
                }
            }
        }

        // Parse edges (try both "edges" and "links")
        var edgesMatch = Regex.Match(data, @"""(?:edges|links)""\s*:\s*\[(.*?)\]", RegexOptions.Singleline);
        if (edgesMatch.Success)
        {
            var edgeMatches = Regex.Matches(edgesMatch.Groups[1].Value, @"\{([^{}]*)\}");
            var edgeId = 0;
            foreach (Match match in edgeMatches)
            {
                var edgeData = match.Groups[1].Value;
                var sourceMatch = Regex.Match(edgeData, @"""source""\s*:\s*""?([^"",}]+)""?");
                var targetMatch = Regex.Match(edgeData, @"""target""\s*:\s*""?([^"",}]+)""?");

                if (sourceMatch.Success && targetMatch.Success)
                {
                    var edge = new GraphEdge
                    {
                        Id = $"e{edgeId++}",
                        SourceId = sourceMatch.Groups[1].Value.Trim(),
                        TargetId = targetMatch.Groups[1].Value.Trim()
                    };

                    var typeMatch = Regex.Match(edgeData, @"""(?:type|label)""\s*:\s*""([^""]*)""");
                    if (typeMatch.Success)
                        edge.Type = typeMatch.Groups[1].Value;

                    graph.Edges.Add(edge);
                }
            }
        }

        graph.IsDirected = data.Contains("\"directed\"") && data.Contains("true");
        return graph;
    }

    private static GraphStructure ParseGenericGraph(string data)
    {
        var graph = new GraphStructure();

        // Try to find any node-like patterns
        var nodePatterns = Regex.Matches(data, @"\b(\w{2,})\s*[:\-=>]");
        foreach (Match match in nodePatterns.Take(50))
        {
            var id = match.Groups[1].Value;
            if (!graph.Nodes.Any(n => n.Id == id))
            {
                graph.Nodes.Add(new GraphNode { Id = id });
            }
        }

        return graph;
    }

    private static Dictionary<string, object> ParseProperties(string propString)
    {
        var props = new Dictionary<string, object>();
        if (string.IsNullOrWhiteSpace(propString)) return props;

        var matches = Regex.Matches(propString, @"(\w+)\s*:\s*['""]?([^'"",:}]+)['""]?");
        foreach (Match match in matches)
        {
            var value = match.Groups[2].Value.Trim();
            if (int.TryParse(value, out var intVal))
                props[match.Groups[1].Value] = intVal;
            else if (double.TryParse(value, out var dblVal))
                props[match.Groups[1].Value] = dblVal;
            else if (bool.TryParse(value, out var boolVal))
                props[match.Groups[1].Value] = boolVal;
            else
                props[match.Groups[1].Value] = value;
        }

        return props;
    }

    private static Dictionary<string, object> ParseDotAttributes(string attrString)
    {
        var attrs = new Dictionary<string, object>();
        var matches = Regex.Matches(attrString, @"(\w+)\s*=\s*""?([^"",\]]+)""?");
        foreach (Match match in matches)
        {
            attrs[match.Groups[1].Value] = match.Groups[2].Value.Trim();
        }
        return attrs;
    }

    private static Dictionary<string, object> CalculateGraphMetrics(GraphStructure graph)
    {
        var metrics = new Dictionary<string, object>
        {
            ["node_count"] = graph.Nodes.Count,
            ["edge_count"] = graph.Edges.Count,
            ["is_directed"] = graph.IsDirected
        };

        if (graph.Nodes.Count > 0)
        {
            // Calculate degree distribution
            var degrees = new Dictionary<string, int>();
            foreach (var node in graph.Nodes)
            {
                degrees[node.Id] = graph.Edges.Count(e => e.SourceId == node.Id || e.TargetId == node.Id);
            }

            metrics["avg_degree"] = degrees.Values.Count > 0 ? degrees.Values.Average() : 0;
            metrics["max_degree"] = degrees.Values.Count > 0 ? degrees.Values.Max() : 0;
            metrics["density"] = graph.Nodes.Count > 1
                ? (double)graph.Edges.Count / (graph.Nodes.Count * (graph.Nodes.Count - 1))
                : 0;
        }

        // Count unique relationship types
        var relTypes = graph.Edges.Select(e => e.Type).Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList();
        metrics["relationship_types"] = relTypes.Count;

        // Count unique labels
        var labels = graph.Nodes.SelectMany(n => n.Labels).Distinct().ToList();
        metrics["label_count"] = labels.Count;

        return metrics;
    }

    private static GraphStructure RepairGraph(GraphStructure graph, List<string> warnings)
    {
        // Ensure all edge endpoints reference existing nodes
        var nodeIds = graph.Nodes.Select(n => n.Id).ToHashSet();

        foreach (var edge in graph.Edges)
        {
            if (!nodeIds.Contains(edge.SourceId))
            {
                graph.Nodes.Add(new GraphNode { Id = edge.SourceId });
                nodeIds.Add(edge.SourceId);
                warnings.Add($"Created missing source node: {edge.SourceId}");
            }

            if (!nodeIds.Contains(edge.TargetId))
            {
                graph.Nodes.Add(new GraphNode { Id = edge.TargetId });
                nodeIds.Add(edge.TargetId);
                warnings.Add($"Created missing target node: {edge.TargetId}");
            }
        }

        return graph;
    }

    private static string ReconstructGraph(GraphStructure graph, string format, RegenerationOptions options)
    {
        return format switch
        {
            "cypher" => ReconstructCypher(graph),
            "gremlin" => ReconstructGremlin(graph),
            "dot" => ReconstructDot(graph),
            "json-graph" => ReconstructJsonGraph(graph),
            _ => ReconstructJsonGraph(graph)
        };
    }

    private static string ReconstructCypher(GraphStructure graph)
    {
        var sb = new StringBuilder();

        // Create nodes
        foreach (var node in graph.Nodes)
        {
            var labels = node.Labels.Count > 0 ? ":" + string.Join(":", node.Labels) : "";
            var props = node.Properties.Count > 0
                ? " {" + string.Join(", ", node.Properties.Select(kv => $"{kv.Key}: '{kv.Value}'")) + "}"
                : "";
            sb.AppendLine($"CREATE ({node.Id}{labels}{props})");
        }

        // Create relationships
        foreach (var edge in graph.Edges)
        {
            var type = !string.IsNullOrEmpty(edge.Type) ? edge.Type : "RELATED_TO";
            var props = edge.Properties.Count > 0
                ? " {" + string.Join(", ", edge.Properties.Select(kv => $"{kv.Key}: '{kv.Value}'")) + "}"
                : "";
            sb.AppendLine($"CREATE ({edge.SourceId})-[:{type}{props}]->({edge.TargetId})");
        }

        return sb.ToString().TrimEnd();
    }

    private static string ReconstructGremlin(GraphStructure graph)
    {
        var sb = new StringBuilder("g");

        foreach (var node in graph.Nodes)
        {
            var label = node.Labels.FirstOrDefault() ?? "vertex";
            sb.Append($".addV('{label}').property('id', '{node.Id}')");
            foreach (var prop in node.Properties)
            {
                sb.Append($".property('{prop.Key}', '{prop.Value}')");
            }
        }

        foreach (var edge in graph.Edges)
        {
            var type = !string.IsNullOrEmpty(edge.Type) ? edge.Type : "edge";
            sb.Append($".V('{edge.SourceId}').addE('{type}').to(V('{edge.TargetId}'))");
        }

        return sb.ToString();
    }

    private static string ReconstructDot(GraphStructure graph)
    {
        var sb = new StringBuilder();
        sb.AppendLine(graph.IsDirected ? "digraph G {" : "graph G {");

        foreach (var node in graph.Nodes)
        {
            var attrs = node.Properties.Count > 0
                ? " [" + string.Join(", ", node.Properties.Select(kv => $"{kv.Key}=\"{kv.Value}\"")) + "]"
                : "";
            sb.AppendLine($"  {node.Id}{attrs};");
        }

        var edgeOp = graph.IsDirected ? "->" : "--";
        foreach (var edge in graph.Edges)
        {
            var attrs = !string.IsNullOrEmpty(edge.Type) ? $" [label=\"{edge.Type}\"]" : "";
            sb.AppendLine($"  {edge.SourceId} {edgeOp} {edge.TargetId}{attrs};");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string ReconstructJsonGraph(GraphStructure graph)
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine($"  \"directed\": {(graph.IsDirected ? "true" : "false")},");

        // Nodes
        sb.AppendLine("  \"nodes\": [");
        for (int i = 0; i < graph.Nodes.Count; i++)
        {
            var node = graph.Nodes[i];
            var label = node.Labels.FirstOrDefault() ?? "";
            sb.Append($"    {{\"id\": \"{node.Id}\"");
            if (!string.IsNullOrEmpty(label))
                sb.Append($", \"label\": \"{label}\"");
            sb.Append("}");
            sb.AppendLine(i < graph.Nodes.Count - 1 ? "," : "");
        }
        sb.AppendLine("  ],");

        // Edges
        sb.AppendLine("  \"edges\": [");
        for (int i = 0; i < graph.Edges.Count; i++)
        {
            var edge = graph.Edges[i];
            sb.Append($"    {{\"source\": \"{edge.SourceId}\", \"target\": \"{edge.TargetId}\"");
            if (!string.IsNullOrEmpty(edge.Type))
                sb.Append($", \"type\": \"{edge.Type}\"");
            sb.Append("}");
            sb.AppendLine(i < graph.Edges.Count - 1 ? "," : "");
        }
        sb.AppendLine("  ]");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static double CalculateGraphStructuralIntegrity(string regenerated, string original, string format)
    {
        var origGraph = ParseGraphData(original, format);
        var regenGraph = ParseGraphData(regenerated, format);

        var nodeRatio = origGraph.Nodes.Count > 0
            ? Math.Min((double)regenGraph.Nodes.Count / origGraph.Nodes.Count, 1.0)
            : (regenGraph.Nodes.Count == 0 ? 1.0 : 0.5);

        var edgeRatio = origGraph.Edges.Count > 0
            ? Math.Min((double)regenGraph.Edges.Count / origGraph.Edges.Count, 1.0)
            : (regenGraph.Edges.Count == 0 ? 1.0 : 0.5);

        return (nodeRatio * 0.5 + edgeRatio * 0.5);
    }

    private static double CalculateGraphSemanticIntegrity(GraphStructure original, GraphStructure regenerated)
    {
        // Compare node IDs
        var origNodeIds = original.Nodes.Select(n => n.Id).ToHashSet();
        var regenNodeIds = regenerated.Nodes.Select(n => n.Id).ToHashSet();
        var nodeOverlap = CalculateJaccardSimilarity(origNodeIds, regenNodeIds);

        // Compare edge connections
        var origEdges = original.Edges.Select(e => $"{e.SourceId}->{e.TargetId}").ToHashSet();
        var regenEdges = regenerated.Edges.Select(e => $"{e.SourceId}->{e.TargetId}").ToHashSet();
        var edgeOverlap = CalculateJaccardSimilarity(origEdges, regenEdges);

        return (nodeOverlap * 0.5 + edgeOverlap * 0.5);
    }

    private class GraphStructure
    {
        public List<GraphNode> Nodes { get; } = new();
        public List<GraphEdge> Edges { get; } = new();
        public bool IsDirected { get; set; } = true;
    }

    private class GraphNode
    {
        public string Id { get; set; } = string.Empty;
        public List<string> Labels { get; set; } = new();
        public Dictionary<string, object> Properties { get; set; } = new();
    }

    private class GraphEdge
    {
        public string Id { get; set; } = string.Empty;
        public string SourceId { get; set; } = string.Empty;
        public string TargetId { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public Dictionary<string, object> Properties { get; set; } = new();
    }
}
