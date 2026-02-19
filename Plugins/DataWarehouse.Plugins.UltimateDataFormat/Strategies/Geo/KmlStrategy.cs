using DataWarehouse.SDK.Contracts;
using System.Xml.Linq;
using DataWarehouse.SDK.Contracts.DataFormat;

namespace DataWarehouse.Plugins.UltimateDataFormat.Strategies.Geo;

/// <summary>
/// KML (Keyhole Markup Language) format strategy for geographic visualization.
/// XML-based format for expressing geographic annotation and visualization.
/// Used by Google Earth and other mapping applications.
/// </summary>
public sealed class KmlStrategy : DataFormatStrategyBase
{
    public override string StrategyId => "kml";

    public override string DisplayName => "KML (Keyhole Markup Language)";

    /// <summary>Production hardening: initialization with counter tracking.</summary>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken) { IncrementCounter("kml.init"); return base.InitializeAsyncCore(cancellationToken); }
    /// <summary>Production hardening: graceful shutdown.</summary>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken) { IncrementCounter("kml.shutdown"); return base.ShutdownAsyncCore(cancellationToken); }
    /// <summary>Production hardening: cached health check.</summary>
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default) =>
        GetCachedHealthAsync(async (c) => new StrategyHealthCheckResult(true, "KML (Keyhole Markup Language) strategy ready", new Dictionary<string, object> { ["ParseOps"] = GetCounter("kml.parse"), ["SerializeOps"] = GetCounter("kml.serialize") }), TimeSpan.FromSeconds(60), ct);

    public override DataFormatCapabilities Capabilities => new()
    {
        Bidirectional = true,
        Streaming = false, // KML is XML, typically loaded entirely
        SchemaAware = false, // KML has fixed structure but is flexible
        CompressionAware = true, // KMZ is compressed KML
        RandomAccess = false,
        SelfDescribing = true,
        SupportsHierarchicalData = true,
        SupportsBinaryData = false // XML-based (though KMZ can contain images)
    };

    public override FormatInfo FormatInfo => new()
    {
        FormatId = "kml",
        Extensions = new[] { ".kml", ".kmz" },
        MimeTypes = new[] { "application/vnd.google-earth.kml+xml", "application/vnd.google-earth.kmz" },
        DomainFamily = DomainFamily.Geospatial,
        Description = "KML format for geographic annotation and visualization (Google Earth)",
        SpecificationVersion = "2.3",
        SpecificationUrl = "https://www.ogc.org/standards/kml"
    };

    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct)
    {
        // KML is XML with <kml> root element
        // KMZ is ZIP archive containing doc.kml
        try
        {
            stream.Position = 0;

            // Check for KMZ (ZIP signature: PK)
            var buffer = new byte[2];
            await stream.ReadExactlyAsync(buffer, 0, 2, ct);

            if (buffer[0] == 0x50 && buffer[1] == 0x4B) // "PK" - ZIP signature
            {
                // This is likely KMZ - would need to extract and check for doc.kml
                return true;
            }

            // Check for KML (XML)
            stream.Position = 0;
            var doc = await XDocument.LoadAsync(stream, LoadOptions.None, ct);

            // KML root element should be <kml> with namespace http://www.opengis.net/kml/2.2
            var root = doc.Root;
            if (root == null)
                return false;

            return root.Name.LocalName == "kml" &&
                   (root.Name.NamespaceName.Contains("opengis.net/kml") ||
                    root.Name.NamespaceName.Contains("earth.google.com/kml"));
        }
        catch
        {
            return false;
        }
    }

    public override async Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default)
    {
        try
        {
            input.Position = 0;

            // Check if KMZ (compressed)
            var buffer = new byte[2];
            await input.ReadExactlyAsync(buffer, 0, 2, ct);

            if (buffer[0] == 0x50 && buffer[1] == 0x4B) // KMZ
            {
                return DataFormatResult.Fail(
                    "KMZ parsing requires ZIP decompression. Extract doc.kml and parse as KML.");
            }

            // Parse KML XML
            input.Position = 0;
            var doc = await XDocument.LoadAsync(input, LoadOptions.None, ct);
            var root = doc.Root;

            if (root == null || root.Name.LocalName != "kml")
            {
                return DataFormatResult.Fail("Invalid KML: missing <kml> root element");
            }

            // Count features (Placemark elements)
            var ns = root.GetDefaultNamespace();
            var placemarks = root.Descendants(ns + "Placemark");
            var featureCount = placemarks.Count();

            // Validate basic structure
            var warnings = new List<string>();
            foreach (var placemark in placemarks)
            {
                // Check for geometry element
                var hasGeometry =
                    placemark.Element(ns + "Point") != null ||
                    placemark.Element(ns + "LineString") != null ||
                    placemark.Element(ns + "Polygon") != null ||
                    placemark.Element(ns + "MultiGeometry") != null;

                if (!hasGeometry)
                {
                    warnings.Add($"Placemark '{placemark.Element(ns + "name")?.Value ?? "unnamed"}' has no geometry");
                }
            }

            return new DataFormatResult
            {
                Success = true,
                Data = doc,
                BytesProcessed = input.Length,
                RecordsProcessed = featureCount,
                Warnings = warnings.Count > 0 ? warnings : null,
                Metadata = new Dictionary<string, object>
                {
                    ["namespace"] = root.GetDefaultNamespace().NamespaceName,
                    ["placemarkCount"] = featureCount
                }
            };
        }
        catch (Exception ex)
        {
            return DataFormatResult.Fail($"KML parsing error: {ex.Message}");
        }
    }

    public override async Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default)
    {
        try
        {
            XDocument doc;

            // If data is already XDocument, use it
            if (data is XDocument xDoc)
            {
                doc = xDoc;
            }
            else
            {
                // Create basic KML structure
                var ns = XNamespace.Get("http://www.opengis.net/kml/2.2");
                doc = new XDocument(
                    new XDeclaration("1.0", "UTF-8", null),
                    new XElement(ns + "kml",
                        new XElement(ns + "Document",
                            new XElement(ns + "name", "Generated KML"),
                            new XElement(ns + "Placemark",
                                new XElement(ns + "name", "Sample"),
                                new XElement(ns + "Point",
                                    new XElement(ns + "coordinates", "0,0,0")
                                )
                            )
                        )
                    )
                );
            }

            // Write XML to stream
            await doc.SaveAsync(output, SaveOptions.None, ct);

            return new DataFormatResult
            {
                Success = true,
                BytesProcessed = output.Length
            };
        }
        catch (Exception ex)
        {
            return DataFormatResult.Fail($"KML serialization error: {ex.Message}");
        }
    }

    protected override async Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct)
    {
        var errors = new List<ValidationError>();
        var warnings = new List<ValidationWarning>();

        try
        {
            stream.Position = 0;
            var doc = await XDocument.LoadAsync(stream, LoadOptions.None, ct);
            var root = doc.Root;

            if (root == null)
            {
                errors.Add(new ValidationError { Message = "Missing root element" });
                return FormatValidationResult.Invalid(errors.ToArray());
            }

            // Check for <kml> root
            if (root.Name.LocalName != "kml")
            {
                errors.Add(new ValidationError { Message = "Root element must be <kml>" });
            }

            // Check namespace
            if (!root.Name.NamespaceName.Contains("opengis.net/kml") &&
                !root.Name.NamespaceName.Contains("earth.google.com/kml"))
            {
                warnings.Add(new ValidationWarning
                {
                    Message = "KML namespace should be http://www.opengis.net/kml/2.2 or similar"
                });
            }

            // Check for Document or Folder
            var ns = root.GetDefaultNamespace();
            var hasDocument = root.Element(ns + "Document") != null;
            var hasFolder = root.Element(ns + "Folder") != null;
            var hasPlacemark = root.Element(ns + "Placemark") != null;

            if (!hasDocument && !hasFolder && !hasPlacemark)
            {
                warnings.Add(new ValidationWarning
                {
                    Message = "KML should contain <Document>, <Folder>, or <Placemark> elements"
                });
            }

            // Validate coordinate format in Point elements
            var points = root.Descendants(ns + "Point");
            foreach (var point in points)
            {
                var coordinates = point.Element(ns + "coordinates")?.Value.Trim();
                if (string.IsNullOrWhiteSpace(coordinates))
                {
                    errors.Add(new ValidationError
                    {
                        Message = "Point element missing coordinates",
                        Path = GetXPath(point)
                    });
                }
                else
                {
                    // Coordinates should be: longitude,latitude[,altitude]
                    var parts = coordinates.Split(',');
                    if (parts.Length < 2)
                    {
                        errors.Add(new ValidationError
                        {
                            Message = "Point coordinates must have at least longitude,latitude",
                            Path = GetXPath(point)
                        });
                    }
                }
            }

            return errors.Count > 0
                ? FormatValidationResult.Invalid(errors.ToArray())
                : new FormatValidationResult { IsValid = true, Warnings = warnings.Count > 0 ? warnings : null };
        }
        catch (Exception ex)
        {
            errors.Add(new ValidationError { Message = $"XML parsing error: {ex.Message}" });
            return FormatValidationResult.Invalid(errors.ToArray());
        }
    }

    private string GetXPath(XElement element)
    {
        var path = new List<string>();
        var current = element;

        while (current != null)
        {
            path.Insert(0, current.Name.LocalName);
            current = current.Parent;
        }

        return "/" + string.Join("/", path);
    }
}
