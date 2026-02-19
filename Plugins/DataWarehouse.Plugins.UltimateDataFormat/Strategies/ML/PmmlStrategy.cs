using DataWarehouse.SDK.Contracts;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DataWarehouse.SDK.Contracts.DataFormat;

namespace DataWarehouse.Plugins.UltimateDataFormat.Strategies.ML;

/// <summary>
/// PMML (Predictive Model Markup Language) format strategy.
/// PMML is XML-based standard for representing predictive models.
/// </summary>
public sealed class PmmlStrategy : DataFormatStrategyBase
{
    public override string StrategyId => "pmml";

    public override string DisplayName => "PMML";

    /// <summary>Production hardening: initialization with counter tracking.</summary>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken) { IncrementCounter("pmml.init"); return base.InitializeAsyncCore(cancellationToken); }
    /// <summary>Production hardening: graceful shutdown.</summary>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken) { IncrementCounter("pmml.shutdown"); return base.ShutdownAsyncCore(cancellationToken); }
    /// <summary>Production hardening: cached health check.</summary>
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default) =>
        GetCachedHealthAsync(async (c) => new StrategyHealthCheckResult(true, "PMML strategy ready", new Dictionary<string, object> { ["ParseOps"] = GetCounter("pmml.parse"), ["SerializeOps"] = GetCounter("pmml.serialize") }), TimeSpan.FromSeconds(60), ct);

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
        FormatId = "pmml",
        Extensions = new[] { ".pmml" },
        MimeTypes = new[] { "application/pmml+xml", "application/xml", "text/xml" },
        DomainFamily = DomainFamily.MachineLearning,
        Description = "Predictive Model Markup Language - XML standard for ML models",
        SpecificationVersion = "4.4",
        SpecificationUrl = "http://dmg.org/pmml/v4-4/GeneralStructure.html"
    };

    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[512];
        var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
        if (bytesRead == 0)
            return false;

        var text = Encoding.UTF8.GetString(buffer, 0, bytesRead);

        // Check for <PMML> root element with version attribute
        return text.Contains("<?xml") && text.Contains("<PMML") && text.Contains("version=");
    }

    public override async Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default)
    {
        try
        {
            var doc = await Task.Run(() => XDocument.Load(input), ct);
            var pmml = ParsePmmlModel(doc);

            return DataFormatResult.Ok(pmml, input.Length, 1);
        }
        catch (Exception ex)
        {
            return DataFormatResult.Fail($"PMML parsing failed: {ex.Message}");
        }
    }

    public override async Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default)
    {
        try
        {
            if (data is not PmmlModel pmml)
                return DataFormatResult.Fail("Data must be PmmlModel");

            var doc = SerializePmmlModel(pmml);
            await Task.Run(() => doc.Save(output), ct);

            return DataFormatResult.Ok(null, output.Length, 1);
        }
        catch (Exception ex)
        {
            return DataFormatResult.Fail($"PMML serialization failed: {ex.Message}");
        }
    }

    protected override async Task<FormatSchema?> ExtractSchemaCoreAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            var doc = await Task.Run(() => XDocument.Load(stream), ct);
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

            var fields = new List<SchemaField>();

            // Parse DataDictionary
            var dataDictionary = doc.Descendants(ns + "DataDictionary").FirstOrDefault();
            if (dataDictionary != null)
            {
                foreach (var dataField in dataDictionary.Elements(ns + "DataField"))
                {
                    var name = dataField.Attribute("name")?.Value ?? "";
                    var optype = dataField.Attribute("optype")?.Value ?? "";
                    var dataType = dataField.Attribute("dataType")?.Value ?? "";

                    fields.Add(new SchemaField
                    {
                        Name = name,
                        DataType = $"{dataType} ({optype})",
                        Nullable = true,
                        Description = $"PMML data field: {optype}"
                    });
                }
            }

            // Parse MiningSchema (model inputs/outputs)
            var miningSchema = doc.Descendants(ns + "MiningSchema").FirstOrDefault();
            if (miningSchema != null)
            {
                foreach (var miningField in miningSchema.Elements(ns + "MiningField"))
                {
                    var name = miningField.Attribute("name")?.Value ?? "";
                    var usageType = miningField.Attribute("usageType")?.Value ?? "active";

                    // Find corresponding DataField
                    var existing = fields.FirstOrDefault(f => f.Name == name);
                    if (existing != null)
                    {
                        existing = existing with
                        {
                            Description = $"{existing.Description} - Usage: {usageType}"
                        };
                    }
                }
            }

            return new FormatSchema
            {
                Name = "PMML Model Schema",
                SchemaType = "pmml",
                Fields = fields,
                RawSchema = "Extracted from PMML DataDictionary and MiningSchema"
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    protected override async Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct)
    {
        try
        {
            var doc = await Task.Run(() => XDocument.Load(stream), ct);
            var errors = new List<ValidationError>();

            // Check for PMML root
            if (doc.Root?.Name.LocalName != "PMML")
            {
                errors.Add(new ValidationError { Message = "Missing <PMML> root element" });
                return FormatValidationResult.Invalid(errors.ToArray());
            }

            var ns = doc.Root.Name.Namespace;

            // Check version attribute
            var version = doc.Root.Attribute("version")?.Value;
            if (string.IsNullOrEmpty(version))
                errors.Add(new ValidationError { Message = "Missing version attribute on <PMML>" });

            // Check for Header
            if (doc.Descendants(ns + "Header").FirstOrDefault() == null)
                errors.Add(new ValidationError { Message = "Missing required <Header> element" });

            // Check for DataDictionary
            if (doc.Descendants(ns + "DataDictionary").FirstOrDefault() == null)
                errors.Add(new ValidationError { Message = "Missing required <DataDictionary> element" });

            // Check for at least one model element
            var modelElements = new[]
            {
                "AssociationModel", "ClusteringModel", "GeneralRegressionModel",
                "MiningModel", "NaiveBayesModel", "NearestNeighborModel",
                "NeuralNetwork", "RegressionModel", "RuleSetModel",
                "SequenceModel", "SupportVectorMachineModel", "TextModel",
                "TimeSeriesModel", "TreeModel"
            };

            bool hasModel = modelElements.Any(m => doc.Descendants(ns + m).Any());
            if (!hasModel)
                errors.Add(new ValidationError { Message = "Missing model element (e.g., TreeModel, RegressionModel, etc.)" });

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

    #region PMML Model Parsing/Serialization

    private PmmlModel ParsePmmlModel(XDocument doc)
    {
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

        var version = doc.Root?.Attribute("version")?.Value ?? "";

        // Parse Header
        var header = doc.Descendants(ns + "Header").FirstOrDefault();
        var application = header?.Element(ns + "Application")?.Attribute("name")?.Value ?? "";
        var timestamp = header?.Element(ns + "Timestamp")?.Value ?? "";

        // Parse DataDictionary
        var dataDictionary = doc.Descendants(ns + "DataDictionary").FirstOrDefault();
        var dataFields = dataDictionary?
            .Elements(ns + "DataField")
            .Select(df => new PmmlDataField
            {
                Name = df.Attribute("name")?.Value ?? "",
                Optype = df.Attribute("optype")?.Value ?? "",
                DataType = df.Attribute("dataType")?.Value ?? ""
            })
            .ToList() ?? new List<PmmlDataField>();

        // Detect model type
        var modelType = "Unknown";
        if (doc.Descendants(ns + "TreeModel").Any()) modelType = "TreeModel";
        else if (doc.Descendants(ns + "RegressionModel").Any()) modelType = "RegressionModel";
        else if (doc.Descendants(ns + "NeuralNetwork").Any()) modelType = "NeuralNetwork";
        else if (doc.Descendants(ns + "ClusteringModel").Any()) modelType = "ClusteringModel";
        else if (doc.Descendants(ns + "SupportVectorMachineModel").Any()) modelType = "SupportVectorMachineModel";

        return new PmmlModel
        {
            Version = version,
            Application = application,
            Timestamp = timestamp,
            DataFields = dataFields,
            ModelType = modelType
        };
    }

    private XDocument SerializePmmlModel(PmmlModel pmml)
    {
        var ns = XNamespace.Get("http://www.dmg.org/PMML-4_4");

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(ns + "PMML",
                new XAttribute("version", pmml.Version),
                new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"),

                // Header
                new XElement(ns + "Header",
                    new XElement(ns + "Application",
                        new XAttribute("name", pmml.Application)
                    ),
                    new XElement(ns + "Timestamp", pmml.Timestamp)
                ),

                // DataDictionary
                new XElement(ns + "DataDictionary",
                    new XAttribute("numberOfFields", pmml.DataFields.Count),
                    pmml.DataFields.Select(df =>
                        new XElement(ns + "DataField",
                            new XAttribute("name", df.Name),
                            new XAttribute("optype", df.Optype),
                            new XAttribute("dataType", df.DataType)
                        )
                    )
                ),

                // Placeholder model (TreeModel)
                new XElement(ns + pmml.ModelType,
                    new XAttribute("functionName", "classification"),
                    new XElement(ns + "MiningSchema",
                        pmml.DataFields.Select(df =>
                            new XElement(ns + "MiningField",
                                new XAttribute("name", df.Name)
                            )
                        )
                    )
                )
            )
        );

        return doc;
    }

    #endregion
}

/// <summary>
/// Represents a PMML model.
/// </summary>
public sealed class PmmlModel
{
    public required string Version { get; init; }
    public required string Application { get; init; }
    public required string Timestamp { get; init; }
    public required List<PmmlDataField> DataFields { get; init; }
    public required string ModelType { get; init; }
}

/// <summary>
/// Represents a PMML data field.
/// </summary>
public sealed class PmmlDataField
{
    public required string Name { get; init; }
    public required string Optype { get; init; } // "categorical", "continuous", "ordinal"
    public required string DataType { get; init; } // "string", "integer", "double", etc.
}
