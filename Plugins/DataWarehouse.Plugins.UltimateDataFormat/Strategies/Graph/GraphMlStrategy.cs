using System.Text;
using System.Xml;
using System.Xml.Linq;
using DataWarehouse.SDK.Contracts.DataFormat;

namespace DataWarehouse.Plugins.UltimateDataFormat.Strategies.Graph;

/// <summary>
/// GraphML format strategy for XML-based graph serialization.
/// Supports nodes, edges, and data attributes.
/// </summary>
public sealed class GraphMlStrategy : DataFormatStrategyBase
{
    public override string StrategyId => "graphml";

    public override string DisplayName => "GraphML";

    public override DataFormatCapabilities Capabilities => new()
    {
        Bidirectional = true,
        Streaming = false,
        SchemaAware = true,
        CompressionAware = false,
        RandomAccess = false,
        SelfDescribing = true,
        SupportsHierarchicalData = true,
        SupportsBinaryData = false
    };

    public override FormatInfo FormatInfo => new()
    {
        FormatId = "graphml",
        Extensions = new[] { ".graphml" },
        MimeTypes = new[] { "application/graphml+xml" },
        DomainFamily = DomainFamily.General,
        Description = "GraphML - XML-based graph serialization format",
        SpecificationVersion = "1.0",
        SpecificationUrl = "http://graphml.graphdrawing.org/"
    };

    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[256];
        var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
        if (bytesRead == 0)
            return false;

        var text = Encoding.UTF8.GetString(buffer, 0, bytesRead);

        // Check for <graphml> root element
        return text.Contains("<?xml") && text.Contains("<graphml");
    }

    public override async Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default)
    {
        try
        {
            var doc = await Task.Run(() => XDocument.Load(input), ct);
            var graphmlNs = doc.Root?.Name.Namespace ?? XNamespace.None;

            var graph = new GraphMlGraph();

            // Parse key definitions (schema)
            var keys = doc.Descendants(graphmlNs + "key")
                .Select(k => new GraphMlKey
                {
                    Id = k.Attribute("id")?.Value ?? "",
                    For = k.Attribute("for")?.Value ?? "",
                    AttrName = k.Attribute("attr.name")?.Value ?? "",
                    AttrType = k.Attribute("attr.type")?.Value ?? "string"
                })
                .ToList();
            graph.Keys = keys;

            // Parse nodes
            var nodes = doc.Descendants(graphmlNs + "node")
                .Select(n => new GraphMlNode
                {
                    Id = n.Attribute("id")?.Value ?? "",
                    Data = n.Elements(graphmlNs + "data")
                        .ToDictionary(
                            d => d.Attribute("key")?.Value ?? "",
                            d => d.Value
                        )
                })
                .ToList();
            graph.Nodes = nodes;

            // Parse edges
            var edges = doc.Descendants(graphmlNs + "edge")
                .Select(e => new GraphMlEdge
                {
                    Id = e.Attribute("id")?.Value,
                    Source = e.Attribute("source")?.Value ?? "",
                    Target = e.Attribute("target")?.Value ?? "",
                    Directed = e.Attribute("directed")?.Value == "true",
                    Data = e.Elements(graphmlNs + "data")
                        .ToDictionary(
                            d => d.Attribute("key")?.Value ?? "",
                            d => d.Value
                        )
                })
                .ToList();
            graph.Edges = edges;

            return DataFormatResult.Ok(graph, input.Length, nodes.Count + edges.Count);
        }
        catch (Exception ex)
        {
            return DataFormatResult.Fail($"GraphML parsing failed: {ex.Message}");
        }
    }

    public override async Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default)
    {
        try
        {
            if (data is not GraphMlGraph graph)
                return DataFormatResult.Fail("Data must be GraphMlGraph");

            var graphmlNs = XNamespace.Get("http://graphml.graphdrawing.org/xmlns");
            var xsiNs = XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance");

            var doc = new XDocument(
                new XDeclaration("1.0", "UTF-8", null),
                new XElement(graphmlNs + "graphml",
                    new XAttribute(XNamespace.Xmlns + "xsi", xsiNs),
                    new XAttribute(xsiNs + "schemaLocation", "http://graphml.graphdrawing.org/xmlns http://graphml.graphdrawing.org/xmlns/1.0/graphml.xsd"),

                    // Add key definitions
                    graph.Keys.Select(k =>
                        new XElement(graphmlNs + "key",
                            new XAttribute("id", k.Id),
                            new XAttribute("for", k.For),
                            new XAttribute("attr.name", k.AttrName),
                            new XAttribute("attr.type", k.AttrType)
                        )
                    ),

                    // Add graph
                    new XElement(graphmlNs + "graph",
                        new XAttribute("id", "G"),
                        new XAttribute("edgedefault", "directed"),

                        // Add nodes
                        graph.Nodes.Select(n =>
                            new XElement(graphmlNs + "node",
                                new XAttribute("id", n.Id),
                                n.Data.Select(d =>
                                    new XElement(graphmlNs + "data",
                                        new XAttribute("key", d.Key),
                                        d.Value
                                    )
                                )
                            )
                        ),

                        // Add edges
                        graph.Edges.Select(e =>
                            new XElement(graphmlNs + "edge",
                                e.Id != null ? new XAttribute("id", e.Id) : null,
                                new XAttribute("source", e.Source),
                                new XAttribute("target", e.Target),
                                new XAttribute("directed", e.Directed.ToString().ToLower()),
                                e.Data.Select(d =>
                                    new XElement(graphmlNs + "data",
                                        new XAttribute("key", d.Key),
                                        d.Value
                                    )
                                )
                            )
                        )
                    )
                )
            );

            await Task.Run(() => doc.Save(output), ct);

            return DataFormatResult.Ok(null, output.Length, graph.Nodes.Count + graph.Edges.Count);
        }
        catch (Exception ex)
        {
            return DataFormatResult.Fail($"GraphML serialization failed: {ex.Message}");
        }
    }

    protected override async Task<FormatSchema?> ExtractSchemaCoreAsync(Stream stream, CancellationToken ct)
    {
        var parseResult = await ParseAsync(stream, new DataFormatContext(), ct);
        if (!parseResult.Success || parseResult.Data is not GraphMlGraph graph)
            return null;

        var schemaFields = new List<SchemaField>();

        // Extract node attributes
        foreach (var key in graph.Keys.Where(k => k.For == "node"))
        {
            schemaFields.Add(new SchemaField
            {
                Name = $"node.{key.AttrName}",
                DataType = key.AttrType,
                Nullable = true,
                Description = $"Node attribute: {key.AttrName}"
            });
        }

        // Extract edge attributes
        foreach (var key in graph.Keys.Where(k => k.For == "edge"))
        {
            schemaFields.Add(new SchemaField
            {
                Name = $"edge.{key.AttrName}",
                DataType = key.AttrType,
                Nullable = true,
                Description = $"Edge attribute: {key.AttrName}"
            });
        }

        return new FormatSchema
        {
            Name = "GraphML Schema",
            SchemaType = "graphml",
            Fields = schemaFields
        };
    }

    protected override async Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct)
    {
        try
        {
            var doc = await Task.Run(() => XDocument.Load(stream), ct);
            var errors = new List<ValidationError>();

            // Check for graphml root
            if (doc.Root?.Name.LocalName != "graphml")
            {
                errors.Add(new ValidationError { Message = "Missing <graphml> root element" });
                return FormatValidationResult.Invalid(errors.ToArray());
            }

            var graphmlNs = doc.Root.Name.Namespace;

            // Check for graph element
            var graphElement = doc.Descendants(graphmlNs + "graph").FirstOrDefault();
            if (graphElement == null)
            {
                errors.Add(new ValidationError { Message = "Missing <graph> element" });
                return FormatValidationResult.Invalid(errors.ToArray());
            }

            // Validate edge references
            var nodeIds = doc.Descendants(graphmlNs + "node")
                .Select(n => n.Attribute("id")?.Value)
                .Where(id => id != null)
                .ToHashSet();

            foreach (var edge in doc.Descendants(graphmlNs + "edge"))
            {
                var source = edge.Attribute("source")?.Value;
                var target = edge.Attribute("target")?.Value;

                if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target))
                {
                    errors.Add(new ValidationError { Message = "Edge missing source or target attribute" });
                }
                else if (!nodeIds.Contains(source) || !nodeIds.Contains(target))
                {
                    errors.Add(new ValidationError { Message = $"Edge references non-existent node: {source} -> {target}" });
                }
            }

            return errors.Count == 0
                ? FormatValidationResult.Valid
                : FormatValidationResult.Invalid(errors.ToArray());
        }
        catch (XmlException ex)
        {
            return FormatValidationResult.Invalid(new ValidationError { Message = $"Invalid XML: {ex.Message}" });
        }
        catch (Exception ex)
        {
            return FormatValidationResult.Invalid(new ValidationError { Message = ex.Message });
        }
    }
}

/// <summary>
/// Represents a GraphML graph structure.
/// </summary>
public sealed class GraphMlGraph
{
    public List<GraphMlKey> Keys { get; set; } = new();
    public List<GraphMlNode> Nodes { get; set; } = new();
    public List<GraphMlEdge> Edges { get; set; } = new();
}

/// <summary>
/// GraphML key definition (attribute schema).
/// </summary>
public sealed class GraphMlKey
{
    public required string Id { get; init; }
    public required string For { get; init; } // "node", "edge", "graph"
    public required string AttrName { get; init; }
    public required string AttrType { get; init; } // "string", "int", "double", etc.
}

/// <summary>
/// GraphML node.
/// </summary>
public sealed class GraphMlNode
{
    public required string Id { get; init; }
    public Dictionary<string, string> Data { get; set; } = new();
}

/// <summary>
/// GraphML edge.
/// </summary>
public sealed class GraphMlEdge
{
    public string? Id { get; init; }
    public required string Source { get; init; }
    public required string Target { get; init; }
    public bool Directed { get; init; }
    public Dictionary<string, string> Data { get; set; } = new();
}
