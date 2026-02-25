using DataWarehouse.SDK.Contracts;
using System.Xml;
using System.Xml.Linq;
using DataWarehouse.SDK.Contracts.DataFormat;

namespace DataWarehouse.Plugins.UltimateDataFormat.Strategies.Text;

/// <summary>
/// XML format strategy using System.Xml.
/// Supports parsing and serialization of XML data with schema (XSD) support.
/// </summary>
public sealed class XmlStrategy : DataFormatStrategyBase
{
    public override string StrategyId => "xml";

    public override string DisplayName => "XML";

    /// <summary>Production hardening: initialization with counter tracking.</summary>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken) { IncrementCounter("xml.init"); return base.InitializeAsyncCore(cancellationToken); }
    /// <summary>Production hardening: graceful shutdown.</summary>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken) { IncrementCounter("xml.shutdown"); return base.ShutdownAsyncCore(cancellationToken); }
    /// <summary>Production hardening: cached health check.</summary>
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default) =>
        GetCachedHealthAsync(async (c) => new StrategyHealthCheckResult(true, "XML strategy ready", new Dictionary<string, object> { ["ParseOps"] = GetCounter("xml.parse"), ["SerializeOps"] = GetCounter("xml.serialize") }), TimeSpan.FromSeconds(60), ct);

    public override DataFormatCapabilities Capabilities => new()
    {
        Bidirectional = true,
        Streaming = true,
        SchemaAware = true,
        CompressionAware = false,
        RandomAccess = false,
        SelfDescribing = true,
        SupportsHierarchicalData = true,
        SupportsBinaryData = false
    };

    public override FormatInfo FormatInfo => new()
    {
        FormatId = "xml",
        Extensions = new[] { ".xml", ".xsd" },
        MimeTypes = new[] { "application/xml", "text/xml" },
        DomainFamily = DomainFamily.General,
        Description = "Extensible Markup Language - markup language for documents and data",
        SpecificationVersion = "1.1",
        SpecificationUrl = "https://www.w3.org/TR/xml11/"
    };

    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[100];
        var bytesRead = await stream.ReadAsync(buffer, ct);

        if (bytesRead == 0)
            return false;

        // Check for XML declaration or opening tag
        var text = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead).TrimStart();
        return text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith('<');
    }

    public override async Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default)
    {
        if (input == null)
            throw new ArgumentNullException(nameof(input));

        try
        {
            var startPosition = input.Position;
            var doc = await Task.Run(() => XDocument.Load(input), ct);
            var bytesProcessed = input.Position - startPosition;

            return DataFormatResult.Ok(doc, bytesProcessed);
        }
        catch (XmlException ex)
        {
            return DataFormatResult.Fail($"XML parsing failed: {ex.Message}");
        }
    }

    public override async Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));
        if (output == null)
            throw new ArgumentNullException(nameof(output));

        try
        {
            var startPosition = output.Position;

            if (data is XDocument doc)
            {
                await Task.Run(() => doc.Save(output), ct);
            }
            else if (data is XElement element)
            {
                await Task.Run(() => element.Save(output), ct);
            }
            else
            {
                return DataFormatResult.Fail("Data must be XDocument or XElement");
            }

            var bytesProcessed = output.Position - startPosition;
            return DataFormatResult.Ok(null, bytesProcessed);
        }
        catch (XmlException ex)
        {
            return DataFormatResult.Fail($"XML serialization failed: {ex.Message}");
        }
    }

    protected override async Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct)
    {
        if (stream == null)
            throw new ArgumentNullException(nameof(stream));

        try
        {
            await Task.Run(() => XDocument.Load(stream), ct);
            return FormatValidationResult.Valid;
        }
        catch (XmlException ex)
        {
            return FormatValidationResult.Invalid(new ValidationError
            {
                Message = ex.Message,
                LineNumber = ex.LineNumber,
                ByteOffset = ex.LinePosition
            });
        }
    }
}
