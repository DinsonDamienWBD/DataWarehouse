using DataWarehouse.SDK.Contracts;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.DataFormat;

namespace DataWarehouse.Plugins.UltimateDataFormat.Strategies.Lakehouse;

/// <summary>
/// Apache Iceberg format strategy for table format on data lakes.
/// Iceberg provides schema evolution, partitioning, and time travel.
/// </summary>
public sealed class IcebergStrategy : DataFormatStrategyBase
{
    public override string StrategyId => "iceberg";

    public override string DisplayName => "Apache Iceberg";

    /// <summary>Production hardening: initialization with counter tracking.</summary>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken) { IncrementCounter("iceberg.init"); return base.InitializeAsyncCore(cancellationToken); }
    /// <summary>Production hardening: graceful shutdown.</summary>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken) { IncrementCounter("iceberg.shutdown"); return base.ShutdownAsyncCore(cancellationToken); }
    /// <summary>Production hardening: cached health check.</summary>
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default) =>
        GetCachedHealthAsync(async (c) => new StrategyHealthCheckResult(true, "Apache Iceberg strategy ready", new Dictionary<string, object> { ["ParseOps"] = GetCounter("iceberg.parse"), ["SerializeOps"] = GetCounter("iceberg.serialize") }), TimeSpan.FromSeconds(60), ct);

    public override DataFormatCapabilities Capabilities => new()
    {
        Bidirectional = false, // Read-only without Iceberg library
        Streaming = false,
        SchemaAware = true,
        CompressionAware = true,
        RandomAccess = true,
        SelfDescribing = true,
        SupportsHierarchicalData = true,
        SupportsBinaryData = false
    };

    public override FormatInfo FormatInfo => new()
    {
        FormatId = "iceberg",
        Extensions = new[] { ".iceberg" },
        MimeTypes = new[] { "application/iceberg" },
        DomainFamily = DomainFamily.Analytics,
        Description = "Apache Iceberg - open table format for huge analytic datasets",
        SpecificationVersion = "2",
        SpecificationUrl = "https://iceberg.apache.org/spec/"
    };

    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct)
    {
        // Iceberg is directory-based format with metadata/ directory
        // For stream detection, check if it's an Iceberg metadata JSON file
        var buffer = new byte[512];
        var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
        if (bytesRead == 0)
            return false;

        var text = Encoding.UTF8.GetString(buffer, 0, bytesRead);

        // Check for Iceberg metadata structure
        return (text.Contains("\"format-version\"") && text.Contains("\"table-uuid\""))
            || text.Contains("\"metadata-file\"")
            || (text.Contains("\"schema\"") && text.Contains("\"partition-spec\""));
    }

    public override Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default)
    {
        // Stub: Full implementation requires Apache Iceberg library
        // Would need to:
        // 1. Read metadata JSON to get current snapshot
        // 2. Read manifest files
        // 3. Read data files (Parquet/ORC/Avro)
        // 4. Apply deletes
        // 5. Return table data

        return Task.FromResult(DataFormatResult.Fail(
            "Apache Iceberg parsing requires Iceberg library. " +
            "This strategy provides format detection and schema extraction only."));
    }

    public override Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default)
    {
        return Task.FromResult(DataFormatResult.Fail(
            "Apache Iceberg serialization requires Iceberg library."));
    }

    protected override async Task<FormatSchema?> ExtractSchemaCoreAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            // Read Iceberg metadata JSON
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync();

            var metadata = JsonSerializer.Deserialize<JsonElement>(json);

            // Extract schema from metadata
            if (metadata.TryGetProperty("schema", out var schemaElement))
            {
                return ParseIcebergSchema(schemaElement);
            }

            // Try current-schema for newer format versions
            if (metadata.TryGetProperty("current-schema-id", out var currentSchemaId))
            {
                if (metadata.TryGetProperty("schemas", out var schemas))
                {
                    var schemaId = currentSchemaId.GetInt32();
                    foreach (var schema in schemas.EnumerateArray())
                    {
                        if (schema.TryGetProperty("schema-id", out var id) && id.GetInt32() == schemaId)
                        {
                            return ParseIcebergSchema(schema);
                        }
                    }
                }
            }

            return null;
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
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync();

            var errors = new List<ValidationError>();

            // Parse and validate Iceberg metadata
            var metadata = JsonSerializer.Deserialize<JsonElement>(json);

            // Required fields for Iceberg table metadata
            if (!metadata.TryGetProperty("format-version", out _))
                errors.Add(new ValidationError { Message = "Missing required field: format-version" });

            if (!metadata.TryGetProperty("table-uuid", out _))
                errors.Add(new ValidationError { Message = "Missing required field: table-uuid" });

            if (!metadata.TryGetProperty("location", out _))
                errors.Add(new ValidationError { Message = "Missing required field: location" });

            // Schema validation
            bool hasSchema = metadata.TryGetProperty("schema", out _)
                || (metadata.TryGetProperty("schemas", out _) && metadata.TryGetProperty("current-schema-id", out _));

            if (!hasSchema)
                errors.Add(new ValidationError { Message = "Missing schema definition" });

            // Partition spec validation
            bool hasPartitionSpec = metadata.TryGetProperty("partition-spec", out _)
                || (metadata.TryGetProperty("partition-specs", out _) && metadata.TryGetProperty("default-spec-id", out _));

            if (!hasPartitionSpec)
                errors.Add(new ValidationError { Message = "Missing partition spec definition" });

            // Sort order validation (optional in v1, required in v2)
            if (metadata.TryGetProperty("format-version", out var version) && version.GetInt32() >= 2)
            {
                if (!metadata.TryGetProperty("sort-orders", out _) || !metadata.TryGetProperty("default-sort-order-id", out _))
                    errors.Add(new ValidationError { Message = "Format version 2 requires sort-orders and default-sort-order-id" });
            }

            return errors.Count == 0
                ? FormatValidationResult.Valid
                : FormatValidationResult.Invalid(errors.ToArray());
        }
        catch (JsonException ex)
        {
            return FormatValidationResult.Invalid(new ValidationError { Message = $"Invalid JSON: {ex.Message}" });
        }
        catch (Exception ex)
        {
            return FormatValidationResult.Invalid(new ValidationError { Message = ex.Message });
        }
    }

    private FormatSchema ParseIcebergSchema(JsonElement schemaElement)
    {
        var fields = new List<SchemaField>();

        // Iceberg schema has "fields" array
        if (schemaElement.TryGetProperty("fields", out var fieldsArray))
        {
            foreach (var field in fieldsArray.EnumerateArray())
            {
                var fieldInfo = ParseIcebergField(field);
                if (fieldInfo != null)
                    fields.Add(fieldInfo);
            }
        }

        var schemaId = schemaElement.TryGetProperty("schema-id", out var id)
            ? id.GetInt32().ToString()
            : "unknown";

        return new FormatSchema
        {
            Name = $"Iceberg Schema {schemaId}",
            SchemaType = "iceberg",
            Version = schemaId,
            Fields = fields,
            RawSchema = schemaElement.ToString()
        };
    }

    private SchemaField? ParseIcebergField(JsonElement field)
    {
        if (!field.TryGetProperty("id", out var id) ||
            !field.TryGetProperty("name", out var name) ||
            !field.TryGetProperty("type", out var type))
            return null;

        var required = field.TryGetProperty("required", out var req) && req.GetBoolean();
        var doc = field.TryGetProperty("doc", out var d) ? d.GetString() : null;

        // Handle complex types
        var dataType = ParseIcebergType(type);

        // Handle nested fields for struct types
        List<SchemaField>? nestedFields = null;
        if (type.ValueKind == JsonValueKind.Object && type.TryGetProperty("type", out var typeStr) && typeStr.GetString() == "struct")
        {
            if (type.TryGetProperty("fields", out var structFields))
            {
                nestedFields = new List<SchemaField>();
                foreach (var nestedField in structFields.EnumerateArray())
                {
                    var nested = ParseIcebergField(nestedField);
                    if (nested != null)
                        nestedFields.Add(nested);
                }
            }
        }

        return new SchemaField
        {
            Name = name.GetString() ?? "",
            DataType = dataType,
            Nullable = !required,
            Description = doc,
            NestedFields = nestedFields
        };
    }

    private string ParseIcebergType(JsonElement type)
    {
        // Primitive type
        if (type.ValueKind == JsonValueKind.String)
        {
            return type.GetString() ?? "unknown";
        }

        // Complex type
        if (type.ValueKind == JsonValueKind.Object)
        {
            if (type.TryGetProperty("type", out var typeStr))
            {
                var typeName = typeStr.GetString();
                return typeName switch
                {
                    "struct" => "struct",
                    "list" => "array",
                    "map" => "map",
                    "fixed" => "fixed",
                    "decimal" => type.TryGetProperty("precision", out var p) && type.TryGetProperty("scale", out var s)
                        ? $"decimal({p.GetInt32()},{s.GetInt32()})"
                        : "decimal",
                    _ => typeName ?? "unknown"
                };
            }
        }

        return "unknown";
    }
}
