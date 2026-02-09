using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies.Graph;

/// <summary>
/// Graph visualization export utilities.
/// Supports multiple graph visualization formats for use with visualization libraries.
/// </summary>
/// <remarks>
/// Supported formats:
/// - D3.js JSON (force-directed graphs)
/// - Cytoscape.js JSON (complex graph analysis)
/// - GraphML (XML-based interchange)
/// - DOT (Graphviz)
/// - GEXF (Gephi)
/// - JSON Graph Format (JGF)
/// - Adjacency Matrix (for analysis tools)
/// </remarks>
public static class GraphVisualizationExport
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    #region D3.js Export

    /// <summary>
    /// Exports graph to D3.js force-directed JSON format.
    /// </summary>
    /// <param name="nodes">Graph nodes.</param>
    /// <param name="edges">Graph edges.</param>
    /// <param name="options">Export options.</param>
    /// <returns>D3.js JSON string.</returns>
    public static string ExportToD3Json(
        IEnumerable<GraphVisualizationNode> nodes,
        IEnumerable<GraphVisualizationEdge> edges,
        D3ExportOptions? options = null)
    {
        options ??= new D3ExportOptions();

        var nodeList = nodes.ToList();
        var nodeIndex = nodeList.Select((n, i) => (n.Id, i)).ToDictionary(x => x.Id, x => x.i);

        var d3Graph = new D3Graph
        {
            Nodes = nodeList.Select(n => new D3Node
            {
                Id = n.Id,
                Label = n.Label,
                Group = n.Group,
                Size = n.Size ?? options.DefaultNodeSize,
                Color = n.Color ?? options.DefaultNodeColor,
                Properties = options.IncludeProperties ? n.Properties : null
            }).ToList(),
            Links = edges.Select(e => new D3Link
            {
                Source = options.UseIndices ? (object)nodeIndex.GetValueOrDefault(e.SourceId, 0) : e.SourceId,
                Target = options.UseIndices ? (object)nodeIndex.GetValueOrDefault(e.TargetId, 0) : e.TargetId,
                Value = e.Weight ?? 1.0,
                Label = e.Label,
                Color = e.Color ?? options.DefaultEdgeColor
            }).ToList()
        };

        return JsonSerializer.Serialize(d3Graph, JsonOptions);
    }

    #endregion

    #region Cytoscape.js Export

    /// <summary>
    /// Exports graph to Cytoscape.js JSON format.
    /// </summary>
    /// <param name="nodes">Graph nodes.</param>
    /// <param name="edges">Graph edges.</param>
    /// <param name="options">Export options.</param>
    /// <returns>Cytoscape.js JSON string.</returns>
    public static string ExportToCytoscapeJson(
        IEnumerable<GraphVisualizationNode> nodes,
        IEnumerable<GraphVisualizationEdge> edges,
        CytoscapeExportOptions? options = null)
    {
        options ??= new CytoscapeExportOptions();

        var elements = new List<object>();

        // Add nodes
        foreach (var node in nodes)
        {
            var data = new Dictionary<string, object?>
            {
                ["id"] = node.Id,
                ["label"] = node.Label ?? node.Id
            };

            if (node.Group != null) data["group"] = node.Group;
            if (node.Size.HasValue) data["size"] = node.Size;
            if (node.Color != null) data["color"] = node.Color;

            if (options.IncludeProperties && node.Properties != null)
            {
                foreach (var prop in node.Properties)
                {
                    data[prop.Key] = prop.Value;
                }
            }

            var position = node.X.HasValue && node.Y.HasValue
                ? new { x = node.X.Value, y = node.Y.Value }
                : null;

            elements.Add(new
            {
                group = "nodes",
                data,
                position
            });
        }

        // Add edges
        int edgeIndex = 0;
        foreach (var edge in edges)
        {
            var data = new Dictionary<string, object?>
            {
                ["id"] = edge.Id ?? $"edge_{edgeIndex++}",
                ["source"] = edge.SourceId,
                ["target"] = edge.TargetId
            };

            if (edge.Label != null) data["label"] = edge.Label;
            if (edge.Weight.HasValue) data["weight"] = edge.Weight;
            if (edge.Color != null) data["color"] = edge.Color;

            if (options.IncludeProperties && edge.Properties != null)
            {
                foreach (var prop in edge.Properties)
                {
                    data[prop.Key] = prop.Value;
                }
            }

            elements.Add(new
            {
                group = "edges",
                data
            });
        }

        var cytoscape = new { elements };
        return JsonSerializer.Serialize(cytoscape, JsonOptions);
    }

    #endregion

    #region GraphML Export

    /// <summary>
    /// Exports graph to GraphML XML format.
    /// </summary>
    /// <param name="nodes">Graph nodes.</param>
    /// <param name="edges">Graph edges.</param>
    /// <param name="options">Export options.</param>
    /// <returns>GraphML XML string.</returns>
    public static string ExportToGraphML(
        IEnumerable<GraphVisualizationNode> nodes,
        IEnumerable<GraphVisualizationEdge> edges,
        GraphMLExportOptions? options = null)
    {
        options ??= new GraphMLExportOptions();

        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = Encoding.UTF8
        };

        var sb = new StringBuilder();
        using var writer = XmlWriter.Create(sb, settings);

        writer.WriteStartDocument();
        writer.WriteStartElement("graphml", "http://graphml.graphdrawing.org/xmlns");

        // Define attribute keys
        WriteGraphMLKey(writer, "label", "node", "string");
        WriteGraphMLKey(writer, "size", "node", "double");
        WriteGraphMLKey(writer, "color", "node", "string");
        WriteGraphMLKey(writer, "group", "node", "string");
        WriteGraphMLKey(writer, "x", "node", "double");
        WriteGraphMLKey(writer, "y", "node", "double");
        WriteGraphMLKey(writer, "weight", "edge", "double");
        WriteGraphMLKey(writer, "edgeLabel", "edge", "string");
        WriteGraphMLKey(writer, "edgeColor", "edge", "string");

        // Start graph
        writer.WriteStartElement("graph");
        writer.WriteAttributeString("id", options.GraphId);
        writer.WriteAttributeString("edgedefault", options.Directed ? "directed" : "undirected");

        // Write nodes
        foreach (var node in nodes)
        {
            writer.WriteStartElement("node");
            writer.WriteAttributeString("id", node.Id);

            if (node.Label != null)
                WriteGraphMLData(writer, "label", node.Label);
            if (node.Size.HasValue)
                WriteGraphMLData(writer, "size", node.Size.Value.ToString(CultureInfo.InvariantCulture));
            if (node.Color != null)
                WriteGraphMLData(writer, "color", node.Color);
            if (node.Group != null)
                WriteGraphMLData(writer, "group", node.Group);
            if (node.X.HasValue)
                WriteGraphMLData(writer, "x", node.X.Value.ToString(CultureInfo.InvariantCulture));
            if (node.Y.HasValue)
                WriteGraphMLData(writer, "y", node.Y.Value.ToString(CultureInfo.InvariantCulture));

            writer.WriteEndElement(); // node
        }

        // Write edges
        int edgeCount = 0;
        foreach (var edge in edges)
        {
            writer.WriteStartElement("edge");
            writer.WriteAttributeString("id", edge.Id ?? $"e{edgeCount++}");
            writer.WriteAttributeString("source", edge.SourceId);
            writer.WriteAttributeString("target", edge.TargetId);

            if (edge.Weight.HasValue)
                WriteGraphMLData(writer, "weight", edge.Weight.Value.ToString(CultureInfo.InvariantCulture));
            if (edge.Label != null)
                WriteGraphMLData(writer, "edgeLabel", edge.Label);
            if (edge.Color != null)
                WriteGraphMLData(writer, "edgeColor", edge.Color);

            writer.WriteEndElement(); // edge
        }

        writer.WriteEndElement(); // graph
        writer.WriteEndElement(); // graphml
        writer.WriteEndDocument();

        return sb.ToString();
    }

    private static void WriteGraphMLKey(XmlWriter writer, string id, string @for, string type)
    {
        writer.WriteStartElement("key");
        writer.WriteAttributeString("id", id);
        writer.WriteAttributeString("for", @for);
        writer.WriteAttributeString("attr.name", id);
        writer.WriteAttributeString("attr.type", type);
        writer.WriteEndElement();
    }

    private static void WriteGraphMLData(XmlWriter writer, string key, string value)
    {
        writer.WriteStartElement("data");
        writer.WriteAttributeString("key", key);
        writer.WriteString(value);
        writer.WriteEndElement();
    }

    #endregion

    #region DOT (Graphviz) Export

    /// <summary>
    /// Exports graph to DOT (Graphviz) format.
    /// </summary>
    /// <param name="nodes">Graph nodes.</param>
    /// <param name="edges">Graph edges.</param>
    /// <param name="options">Export options.</param>
    /// <returns>DOT format string.</returns>
    public static string ExportToDot(
        IEnumerable<GraphVisualizationNode> nodes,
        IEnumerable<GraphVisualizationEdge> edges,
        DotExportOptions? options = null)
    {
        options ??= new DotExportOptions();

        var sb = new StringBuilder();
        var graphType = options.Directed ? "digraph" : "graph";
        var edgeOp = options.Directed ? "->" : "--";

        sb.AppendLine($"{graphType} {EscapeDotId(options.GraphName)} {{");

        // Graph attributes
        if (!string.IsNullOrEmpty(options.RankDir))
            sb.AppendLine($"  rankdir={options.RankDir};");
        if (!string.IsNullOrEmpty(options.Layout))
            sb.AppendLine($"  layout={options.Layout};");

        // Default node attributes
        sb.AppendLine($"  node [shape={options.DefaultNodeShape}];");

        // Write nodes
        foreach (var node in nodes)
        {
            var attrs = new List<string>();

            if (node.Label != null)
                attrs.Add($"label=\"{EscapeDotString(node.Label)}\"");
            if (node.Color != null)
                attrs.Add($"fillcolor=\"{node.Color}\" style=filled");
            if (node.Size.HasValue)
                attrs.Add($"width={node.Size.Value.ToString(CultureInfo.InvariantCulture)}");
            if (node.Shape != null)
                attrs.Add($"shape={node.Shape}");

            var attrStr = attrs.Count > 0 ? $" [{string.Join(", ", attrs)}]" : "";
            sb.AppendLine($"  {EscapeDotId(node.Id)}{attrStr};");
        }

        // Write edges
        foreach (var edge in edges)
        {
            var attrs = new List<string>();

            if (edge.Label != null)
                attrs.Add($"label=\"{EscapeDotString(edge.Label)}\"");
            if (edge.Weight.HasValue)
                attrs.Add($"weight={edge.Weight.Value.ToString(CultureInfo.InvariantCulture)}");
            if (edge.Color != null)
                attrs.Add($"color=\"{edge.Color}\"");

            var attrStr = attrs.Count > 0 ? $" [{string.Join(", ", attrs)}]" : "";
            sb.AppendLine($"  {EscapeDotId(edge.SourceId)} {edgeOp} {EscapeDotId(edge.TargetId)}{attrStr};");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string EscapeDotId(string id)
    {
        // Quote if contains special characters
        if (id.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
        {
            return $"\"{EscapeDotString(id)}\"";
        }
        return id;
    }

    private static string EscapeDotString(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
    }

    #endregion

    #region GEXF (Gephi) Export

    /// <summary>
    /// Exports graph to GEXF (Gephi) XML format.
    /// </summary>
    /// <param name="nodes">Graph nodes.</param>
    /// <param name="edges">Graph edges.</param>
    /// <param name="options">Export options.</param>
    /// <returns>GEXF XML string.</returns>
    public static string ExportToGexf(
        IEnumerable<GraphVisualizationNode> nodes,
        IEnumerable<GraphVisualizationEdge> edges,
        GexfExportOptions? options = null)
    {
        options ??= new GexfExportOptions();

        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = Encoding.UTF8
        };

        var sb = new StringBuilder();
        using var writer = XmlWriter.Create(sb, settings);

        writer.WriteStartDocument();
        writer.WriteStartElement("gexf", "http://www.gexf.net/1.3");
        writer.WriteAttributeString("version", "1.3");

        // Meta
        writer.WriteStartElement("meta");
        writer.WriteAttributeString("lastmodifieddate", DateTime.UtcNow.ToString("yyyy-MM-dd"));
        writer.WriteElementString("creator", options.Creator ?? "DataWarehouse GraphExport");
        if (!string.IsNullOrEmpty(options.Description))
            writer.WriteElementString("description", options.Description);
        writer.WriteEndElement();

        // Graph
        writer.WriteStartElement("graph");
        writer.WriteAttributeString("mode", options.Dynamic ? "dynamic" : "static");
        writer.WriteAttributeString("defaultedgetype", options.Directed ? "directed" : "undirected");

        // Attributes
        if (options.IncludeAttributes)
        {
            writer.WriteStartElement("attributes");
            writer.WriteAttributeString("class", "node");
            writer.WriteAttributeString("mode", "static");

            writer.WriteStartElement("attribute");
            writer.WriteAttributeString("id", "0");
            writer.WriteAttributeString("title", "group");
            writer.WriteAttributeString("type", "string");
            writer.WriteEndElement();

            writer.WriteEndElement(); // attributes
        }

        // Nodes
        writer.WriteStartElement("nodes");
        foreach (var node in nodes)
        {
            writer.WriteStartElement("node");
            writer.WriteAttributeString("id", node.Id);
            writer.WriteAttributeString("label", node.Label ?? node.Id);

            // Viz namespace for visual attributes
            if (node.Color != null || node.Size.HasValue || node.X.HasValue)
            {
                if (node.Color != null)
                {
                    var color = ParseHexColor(node.Color);
                    writer.WriteStartElement("viz", "color", "http://www.gexf.net/1.3/viz");
                    writer.WriteAttributeString("r", color.R.ToString());
                    writer.WriteAttributeString("g", color.G.ToString());
                    writer.WriteAttributeString("b", color.B.ToString());
                    writer.WriteEndElement();
                }

                if (node.Size.HasValue)
                {
                    writer.WriteStartElement("viz", "size", "http://www.gexf.net/1.3/viz");
                    writer.WriteAttributeString("value", node.Size.Value.ToString(CultureInfo.InvariantCulture));
                    writer.WriteEndElement();
                }

                if (node.X.HasValue && node.Y.HasValue)
                {
                    writer.WriteStartElement("viz", "position", "http://www.gexf.net/1.3/viz");
                    writer.WriteAttributeString("x", node.X.Value.ToString(CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("y", node.Y.Value.ToString(CultureInfo.InvariantCulture));
                    writer.WriteEndElement();
                }
            }

            // Custom attributes
            if (options.IncludeAttributes && node.Group != null)
            {
                writer.WriteStartElement("attvalues");
                writer.WriteStartElement("attvalue");
                writer.WriteAttributeString("for", "0");
                writer.WriteAttributeString("value", node.Group);
                writer.WriteEndElement();
                writer.WriteEndElement();
            }

            writer.WriteEndElement(); // node
        }
        writer.WriteEndElement(); // nodes

        // Edges
        writer.WriteStartElement("edges");
        int edgeCount = 0;
        foreach (var edge in edges)
        {
            writer.WriteStartElement("edge");
            writer.WriteAttributeString("id", edge.Id ?? edgeCount.ToString());
            writer.WriteAttributeString("source", edge.SourceId);
            writer.WriteAttributeString("target", edge.TargetId);

            if (edge.Weight.HasValue)
                writer.WriteAttributeString("weight", edge.Weight.Value.ToString(CultureInfo.InvariantCulture));
            if (edge.Label != null)
                writer.WriteAttributeString("label", edge.Label);

            writer.WriteEndElement();
            edgeCount++;
        }
        writer.WriteEndElement(); // edges

        writer.WriteEndElement(); // graph
        writer.WriteEndElement(); // gexf
        writer.WriteEndDocument();

        return sb.ToString();
    }

    private static (byte R, byte G, byte B) ParseHexColor(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6)
        {
            return (
                byte.Parse(hex[..2], NumberStyles.HexNumber),
                byte.Parse(hex[2..4], NumberStyles.HexNumber),
                byte.Parse(hex[4..6], NumberStyles.HexNumber)
            );
        }
        return (128, 128, 128);
    }

    #endregion

    #region JSON Graph Format (JGF) Export

    /// <summary>
    /// Exports graph to JSON Graph Format (JGF).
    /// </summary>
    /// <param name="nodes">Graph nodes.</param>
    /// <param name="edges">Graph edges.</param>
    /// <param name="options">Export options.</param>
    /// <returns>JGF JSON string.</returns>
    public static string ExportToJgf(
        IEnumerable<GraphVisualizationNode> nodes,
        IEnumerable<GraphVisualizationEdge> edges,
        JgfExportOptions? options = null)
    {
        options ??= new JgfExportOptions();

        var jgfNodes = new Dictionary<string, object>();
        foreach (var node in nodes)
        {
            var nodeData = new Dictionary<string, object?>
            {
                ["label"] = node.Label ?? node.Id
            };

            if (options.IncludeMetadata)
            {
                if (node.Group != null) nodeData["group"] = node.Group;
                if (node.Size.HasValue) nodeData["size"] = node.Size;
                if (node.Color != null) nodeData["color"] = node.Color;
                if (node.Properties != null)
                {
                    foreach (var prop in node.Properties)
                    {
                        nodeData[prop.Key] = prop.Value;
                    }
                }
            }

            jgfNodes[node.Id] = nodeData;
        }

        var jgfEdges = edges.Select(e =>
        {
            var edgeData = new Dictionary<string, object?>
            {
                ["source"] = e.SourceId,
                ["target"] = e.TargetId
            };

            if (e.Label != null) edgeData["relation"] = e.Label;
            if (e.Weight.HasValue) edgeData["weight"] = e.Weight;

            if (options.IncludeMetadata)
            {
                if (e.Color != null) edgeData["color"] = e.Color;
                if (e.Properties != null)
                {
                    edgeData["metadata"] = e.Properties;
                }
            }

            return edgeData;
        }).ToList();

        var jgf = new
        {
            graph = new
            {
                type = options.Type ?? "graph",
                directed = options.Directed,
                label = options.Label,
                nodes = jgfNodes,
                edges = jgfEdges
            }
        };

        return JsonSerializer.Serialize(jgf, JsonOptions);
    }

    #endregion

    #region Adjacency Matrix Export

    /// <summary>
    /// Exports graph as an adjacency matrix.
    /// </summary>
    /// <param name="nodes">Graph nodes.</param>
    /// <param name="edges">Graph edges.</param>
    /// <param name="options">Export options.</param>
    /// <returns>Adjacency matrix result.</returns>
    public static AdjacencyMatrixResult ExportToAdjacencyMatrix(
        IEnumerable<GraphVisualizationNode> nodes,
        IEnumerable<GraphVisualizationEdge> edges,
        AdjacencyMatrixOptions? options = null)
    {
        options ??= new AdjacencyMatrixOptions();

        var nodeList = nodes.ToList();
        var nodeIds = nodeList.Select(n => n.Id).ToList();
        var nodeIndex = nodeIds.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        var n = nodeList.Count;

        var matrix = new double[n, n];

        // Initialize with default value
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                matrix[i, j] = options.DefaultValue;
            }
        }

        // Fill matrix
        foreach (var edge in edges)
        {
            if (nodeIndex.TryGetValue(edge.SourceId, out var i) &&
                nodeIndex.TryGetValue(edge.TargetId, out var j))
            {
                var weight = edge.Weight ?? 1.0;
                matrix[i, j] = weight;

                if (!options.Directed)
                {
                    matrix[j, i] = weight;
                }
            }
        }

        return new AdjacencyMatrixResult
        {
            NodeIds = nodeIds,
            Matrix = matrix
        };
    }

    /// <summary>
    /// Exports adjacency matrix to CSV format.
    /// </summary>
    public static string ExportMatrixToCsv(AdjacencyMatrixResult matrix)
    {
        var sb = new StringBuilder();
        var n = matrix.NodeIds.Count;

        // Header row
        sb.Append(',');
        sb.AppendLine(string.Join(",", matrix.NodeIds));

        // Data rows
        for (int i = 0; i < n; i++)
        {
            sb.Append(matrix.NodeIds[i]);
            for (int j = 0; j < n; j++)
            {
                sb.Append(',');
                sb.Append(matrix.Matrix[i, j].ToString(CultureInfo.InvariantCulture));
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    #endregion
}

#region Data Models

/// <summary>
/// Graph node for visualization.
/// </summary>
public sealed class GraphVisualizationNode
{
    /// <summary>Gets or sets the node ID.</summary>
    public required string Id { get; set; }

    /// <summary>Gets or sets the display label.</summary>
    public string? Label { get; set; }

    /// <summary>Gets or sets the group/category.</summary>
    public string? Group { get; set; }

    /// <summary>Gets or sets the node size.</summary>
    public double? Size { get; set; }

    /// <summary>Gets or sets the node color (hex format).</summary>
    public string? Color { get; set; }

    /// <summary>Gets or sets the node shape.</summary>
    public string? Shape { get; set; }

    /// <summary>Gets or sets the X position.</summary>
    public double? X { get; set; }

    /// <summary>Gets or sets the Y position.</summary>
    public double? Y { get; set; }

    /// <summary>Gets or sets additional properties.</summary>
    public Dictionary<string, object>? Properties { get; set; }
}

/// <summary>
/// Graph edge for visualization.
/// </summary>
public sealed class GraphVisualizationEdge
{
    /// <summary>Gets or sets the edge ID.</summary>
    public string? Id { get; set; }

    /// <summary>Gets or sets the source node ID.</summary>
    public required string SourceId { get; set; }

    /// <summary>Gets or sets the target node ID.</summary>
    public required string TargetId { get; set; }

    /// <summary>Gets or sets the edge label.</summary>
    public string? Label { get; set; }

    /// <summary>Gets or sets the edge weight.</summary>
    public double? Weight { get; set; }

    /// <summary>Gets or sets the edge color.</summary>
    public string? Color { get; set; }

    /// <summary>Gets or sets additional properties.</summary>
    public Dictionary<string, object>? Properties { get; set; }
}

#endregion

#region Export Options

/// <summary>
/// D3.js export options.
/// </summary>
public sealed class D3ExportOptions
{
    /// <summary>Gets or sets whether to use indices instead of IDs.</summary>
    public bool UseIndices { get; set; } = false;

    /// <summary>Gets or sets whether to include properties.</summary>
    public bool IncludeProperties { get; set; } = true;

    /// <summary>Gets or sets the default node size.</summary>
    public double DefaultNodeSize { get; set; } = 10;

    /// <summary>Gets or sets the default node color.</summary>
    public string DefaultNodeColor { get; set; } = "#1f77b4";

    /// <summary>Gets or sets the default edge color.</summary>
    public string DefaultEdgeColor { get; set; } = "#999";
}

/// <summary>
/// Cytoscape.js export options.
/// </summary>
public sealed class CytoscapeExportOptions
{
    /// <summary>Gets or sets whether to include properties.</summary>
    public bool IncludeProperties { get; set; } = true;

    /// <summary>Gets or sets whether to include positions.</summary>
    public bool IncludePositions { get; set; } = true;
}

/// <summary>
/// GraphML export options.
/// </summary>
public sealed class GraphMLExportOptions
{
    /// <summary>Gets or sets the graph ID.</summary>
    public string GraphId { get; set; } = "G";

    /// <summary>Gets or sets whether the graph is directed.</summary>
    public bool Directed { get; set; } = true;
}

/// <summary>
/// DOT export options.
/// </summary>
public sealed class DotExportOptions
{
    /// <summary>Gets or sets the graph name.</summary>
    public string GraphName { get; set; } = "G";

    /// <summary>Gets or sets whether the graph is directed.</summary>
    public bool Directed { get; set; } = true;

    /// <summary>Gets or sets the rank direction (TB, BT, LR, RL).</summary>
    public string? RankDir { get; set; }

    /// <summary>Gets or sets the layout engine.</summary>
    public string? Layout { get; set; }

    /// <summary>Gets or sets the default node shape.</summary>
    public string DefaultNodeShape { get; set; } = "ellipse";
}

/// <summary>
/// GEXF export options.
/// </summary>
public sealed class GexfExportOptions
{
    /// <summary>Gets or sets whether the graph is directed.</summary>
    public bool Directed { get; set; } = true;

    /// <summary>Gets or sets whether the graph is dynamic.</summary>
    public bool Dynamic { get; set; } = false;

    /// <summary>Gets or sets whether to include attributes.</summary>
    public bool IncludeAttributes { get; set; } = true;

    /// <summary>Gets or sets the creator.</summary>
    public string? Creator { get; set; }

    /// <summary>Gets or sets the description.</summary>
    public string? Description { get; set; }
}

/// <summary>
/// JGF export options.
/// </summary>
public sealed class JgfExportOptions
{
    /// <summary>Gets or sets whether the graph is directed.</summary>
    public bool Directed { get; set; } = true;

    /// <summary>Gets or sets whether to include metadata.</summary>
    public bool IncludeMetadata { get; set; } = true;

    /// <summary>Gets or sets the graph type.</summary>
    public string? Type { get; set; }

    /// <summary>Gets or sets the graph label.</summary>
    public string? Label { get; set; }
}

/// <summary>
/// Adjacency matrix export options.
/// </summary>
public sealed class AdjacencyMatrixOptions
{
    /// <summary>Gets or sets whether the graph is directed.</summary>
    public bool Directed { get; set; } = true;

    /// <summary>Gets or sets the default value for no edge.</summary>
    public double DefaultValue { get; set; } = 0;
}

/// <summary>
/// Adjacency matrix result.
/// </summary>
public sealed class AdjacencyMatrixResult
{
    /// <summary>Gets or sets the ordered node IDs.</summary>
    public required List<string> NodeIds { get; init; }

    /// <summary>Gets or sets the adjacency matrix.</summary>
    public required double[,] Matrix { get; init; }
}

#endregion

#region D3.js Data Structures

internal sealed class D3Graph
{
    [JsonPropertyName("nodes")]
    public required List<D3Node> Nodes { get; init; }

    [JsonPropertyName("links")]
    public required List<D3Link> Links { get; init; }
}

internal sealed class D3Node
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("group")]
    public string? Group { get; init; }

    [JsonPropertyName("size")]
    public double Size { get; init; }

    [JsonPropertyName("color")]
    public string? Color { get; init; }

    [JsonPropertyName("properties")]
    public Dictionary<string, object>? Properties { get; init; }
}

internal sealed class D3Link
{
    [JsonPropertyName("source")]
    public required object Source { get; init; }

    [JsonPropertyName("target")]
    public required object Target { get; init; }

    [JsonPropertyName("value")]
    public double Value { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("color")]
    public string? Color { get; init; }
}

#endregion
