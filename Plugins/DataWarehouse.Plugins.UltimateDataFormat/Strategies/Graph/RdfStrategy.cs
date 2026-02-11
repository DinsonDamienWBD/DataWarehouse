using System.Text;
using System.Xml;
using System.Xml.Linq;
using DataWarehouse.SDK.Contracts.DataFormat;

namespace DataWarehouse.Plugins.UltimateDataFormat.Strategies.Graph;

/// <summary>
/// RDF (Resource Description Framework) format strategy.
/// Supports RDF/XML and Turtle (.ttl) syntax for semantic web triple data.
/// </summary>
public sealed class RdfStrategy : DataFormatStrategyBase
{
    public override string StrategyId => "rdf";

    public override string DisplayName => "RDF";

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
        FormatId = "rdf",
        Extensions = new[] { ".rdf", ".ttl", ".nt", ".n3" },
        MimeTypes = new[] { "application/rdf+xml", "text/turtle", "application/n-triples" },
        DomainFamily = DomainFamily.General,
        Description = "Resource Description Framework for semantic web graph data",
        SpecificationVersion = "RDF 1.1",
        SpecificationUrl = "https://www.w3.org/TR/rdf11-primer/"
    };

    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[512];
        var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
        if (bytesRead == 0)
            return false;

        var text = Encoding.UTF8.GetString(buffer, 0, bytesRead);

        // Check for RDF/XML: <?xml with <rdf:RDF or <RDF
        if (text.Contains("<?xml") && (text.Contains("<rdf:RDF") || text.Contains("<RDF")))
            return true;

        // Check for Turtle: @prefix or @base
        if (text.Contains("@prefix") || text.Contains("@base"))
            return true;

        // Check for N-Triples: <uri> <uri> "literal" .
        if (text.Contains("<http://") && text.Contains("> <") && text.Contains("> ."))
            return true;

        return false;
    }

    public override async Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default)
    {
        try
        {
            var buffer = new byte[input.Length];
            await input.ReadAsync(buffer, 0, buffer.Length, ct);
            var text = Encoding.UTF8.GetString(buffer);

            List<RdfTriple> triples;

            // Try RDF/XML first
            if (text.Contains("<?xml") || text.Contains("<rdf:RDF"))
            {
                triples = ParseRdfXml(text);
            }
            // Try Turtle
            else if (text.Contains("@prefix") || text.Contains("@base"))
            {
                triples = ParseTurtle(text);
            }
            // Try N-Triples
            else
            {
                triples = ParseNTriples(text);
            }

            return DataFormatResult.Ok(triples, buffer.Length, triples.Count);
        }
        catch (Exception ex)
        {
            return DataFormatResult.Fail($"RDF parsing failed: {ex.Message}");
        }
    }

    public override async Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default)
    {
        try
        {
            if (data is not IEnumerable<RdfTriple> triples)
                return DataFormatResult.Fail("Data must be IEnumerable<RdfTriple>");

            // Default to Turtle serialization
            var turtle = SerializeToTurtle(triples);
            var bytes = Encoding.UTF8.GetBytes(turtle);
            await output.WriteAsync(bytes, 0, bytes.Length, ct);

            return DataFormatResult.Ok(null, bytes.Length, triples.Count());
        }
        catch (Exception ex)
        {
            return DataFormatResult.Fail($"RDF serialization failed: {ex.Message}");
        }
    }

    protected override async Task<FormatSchema?> ExtractSchemaCoreAsync(Stream stream, CancellationToken ct)
    {
        // Extract RDFS/OWL schema information from triples
        var parseResult = await ParseAsync(stream, new DataFormatContext(), ct);
        if (!parseResult.Success || parseResult.Data is not List<RdfTriple> triples)
            return null;

        var schemaFields = new List<SchemaField>();

        // Find rdf:type predicates to identify classes
        var classes = triples
            .Where(t => t.Predicate == "http://www.w3.org/1999/02/22-rdf-syntax-ns#type")
            .Select(t => t.Object)
            .Distinct()
            .ToList();

        foreach (var className in classes)
        {
            schemaFields.Add(new SchemaField
            {
                Name = className,
                DataType = "rdf:Class",
                Nullable = true,
                Description = "RDF class"
            });
        }

        return new FormatSchema
        {
            Name = "RDF Schema",
            SchemaType = "rdfs",
            Fields = schemaFields
        };
    }

    protected override Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct)
    {
        try
        {
            stream.Position = 0;
            var buffer = new byte[stream.Length];
            stream.Read(buffer, 0, buffer.Length);
            var text = Encoding.UTF8.GetString(buffer);

            var errors = new List<ValidationError>();

            // Basic validation: check for triple structure
            if (text.Contains("<?xml"))
            {
                // Validate RDF/XML
                try
                {
                    var doc = XDocument.Parse(text);
                    if (doc.Root?.Name.LocalName != "RDF")
                        errors.Add(new ValidationError { Message = "Missing RDF root element" });
                }
                catch (XmlException ex)
                {
                    errors.Add(new ValidationError { Message = $"Invalid XML: {ex.Message}" });
                }
            }
            else if (text.Contains("@prefix") || text.Contains("<http://"))
            {
                // Validate Turtle/N-Triples: check for proper triple endings
                var lines = text.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("#"));
                foreach (var line in lines)
                {
                    if (!line.Contains("@prefix") && !line.Contains("@base") && !line.TrimEnd().EndsWith("."))
                    {
                        errors.Add(new ValidationError { Message = $"Triple must end with '.': {line.Substring(0, Math.Min(50, line.Length))}" });
                    }
                }
            }

            return Task.FromResult(errors.Count == 0
                ? FormatValidationResult.Valid
                : FormatValidationResult.Invalid(errors.ToArray()));
        }
        catch (Exception ex)
        {
            return Task.FromResult(FormatValidationResult.Invalid(new ValidationError { Message = ex.Message }));
        }
    }

    #region Parsing Helpers

    private List<RdfTriple> ParseRdfXml(string xml)
    {
        var triples = new List<RdfTriple>();
        var doc = XDocument.Parse(xml);
        var rdfNs = XNamespace.Get("http://www.w3.org/1999/02/22-rdf-syntax-ns#");

        foreach (var description in doc.Descendants(rdfNs + "Description"))
        {
            var subject = description.Attribute(rdfNs + "about")?.Value ?? "_:blank";

            foreach (var property in description.Elements())
            {
                var predicate = property.Name.ToString();
                var obj = property.Attribute(rdfNs + "resource")?.Value ?? property.Value;

                triples.Add(new RdfTriple
                {
                    Subject = subject,
                    Predicate = predicate,
                    Object = obj
                });
            }
        }

        return triples;
    }

    private List<RdfTriple> ParseTurtle(string turtle)
    {
        var triples = new List<RdfTriple>();
        var prefixes = new Dictionary<string, string>();

        var lines = turtle.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#"))
                continue;

            // Parse @prefix
            if (trimmed.StartsWith("@prefix"))
            {
                var parts = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    var prefix = parts[1].TrimEnd(':');
                    var uri = parts[2].Trim('<', '>', '.');
                    prefixes[prefix] = uri;
                }
                continue;
            }

            // Parse triple: subject predicate object .
            var tokens = trimmed.TrimEnd('.').Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length >= 3)
            {
                triples.Add(new RdfTriple
                {
                    Subject = ExpandPrefix(tokens[0], prefixes),
                    Predicate = ExpandPrefix(tokens[1], prefixes),
                    Object = ExpandPrefix(tokens[2].Trim(), prefixes)
                });
            }
        }

        return triples;
    }

    private List<RdfTriple> ParseNTriples(string ntriples)
    {
        var triples = new List<RdfTriple>();
        var lines = ntriples.Split('\n');

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#"))
                continue;

            // N-Triples: <subject> <predicate> <object> .
            var match = System.Text.RegularExpressions.Regex.Match(
                trimmed,
                @"<([^>]+)>\s+<([^>]+)>\s+(.+)\s*\."
            );

            if (match.Success)
            {
                triples.Add(new RdfTriple
                {
                    Subject = match.Groups[1].Value,
                    Predicate = match.Groups[2].Value,
                    Object = match.Groups[3].Value.Trim('<', '>', '"')
                });
            }
        }

        return triples;
    }

    private string ExpandPrefix(string token, Dictionary<string, string> prefixes)
    {
        if (token.StartsWith("<") && token.EndsWith(">"))
            return token.Trim('<', '>');

        if (token.Contains(":"))
        {
            var parts = token.Split(':');
            if (prefixes.TryGetValue(parts[0], out var uri))
                return uri + parts[1];
        }

        return token;
    }

    private string SerializeToTurtle(IEnumerable<RdfTriple> triples)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@prefix rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#> .");
        sb.AppendLine();

        foreach (var triple in triples)
        {
            sb.AppendLine($"<{triple.Subject}> <{triple.Predicate}> <{triple.Object}> .");
        }

        return sb.ToString();
    }

    #endregion
}

/// <summary>
/// Represents an RDF triple (subject, predicate, object).
/// </summary>
public sealed class RdfTriple
{
    public required string Subject { get; init; }
    public required string Predicate { get; init; }
    public required string Object { get; init; }
}
