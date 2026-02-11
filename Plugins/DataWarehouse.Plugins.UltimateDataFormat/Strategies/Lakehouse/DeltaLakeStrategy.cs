using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.DataFormat;

namespace DataWarehouse.Plugins.UltimateDataFormat.Strategies.Lakehouse;

/// <summary>
/// Delta Lake format strategy for ACID table format.
/// Delta Lake = Parquet + transaction log for data lakes.
/// </summary>
public sealed class DeltaLakeStrategy : DataFormatStrategyBase
{
    public override string StrategyId => "deltalake";

    public override string DisplayName => "Delta Lake";

    public override DataFormatCapabilities Capabilities => new()
    {
        Bidirectional = false, // Read-only without Delta Lake library
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
        FormatId = "deltalake",
        Extensions = new[] { ".delta" },
        MimeTypes = new[] { "application/delta-lake" },
        DomainFamily = DomainFamily.Analytics,
        Description = "Delta Lake - ACID table format for data lakes",
        SpecificationVersion = "2.0",
        SpecificationUrl = "https://github.com/delta-io/delta/blob/master/PROTOCOL.md"
    };

    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct)
    {
        // Delta Lake is directory-based format
        // Detection requires checking for _delta_log directory
        // For stream-based detection, check if it's a Delta transaction log JSON file
        var buffer = new byte[512];
        var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
        if (bytesRead == 0)
            return false;

        var text = Encoding.UTF8.GetString(buffer, 0, bytesRead);

        // Check for Delta transaction log JSON structure
        return text.Contains("\"protocol\"") || text.Contains("\"metaData\"") || text.Contains("\"add\"");
    }

    public override Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default)
    {
        // Stub: Full implementation requires Delta Lake library
        // Would need to:
        // 1. Read _delta_log/*.json files to get current table state
        // 2. Read Parquet data files referenced in transaction log
        // 3. Apply tombstones (deleted files)
        // 4. Return table data

        return Task.FromResult(DataFormatResult.Fail(
            "Delta Lake parsing requires Delta Lake library. " +
            "This strategy provides format detection and schema extraction only."));
    }

    public override Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default)
    {
        return Task.FromResult(DataFormatResult.Fail(
            "Delta Lake serialization requires Delta Lake library."));
    }

    protected override async Task<FormatSchema?> ExtractSchemaCoreAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            // Read transaction log JSON
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync();

            // Parse Delta transaction log entries
            var lines = json.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var entry = JsonSerializer.Deserialize<JsonElement>(line);

                // Look for metaData entry with schema
                if (entry.TryGetProperty("metaData", out var metaData))
                {
                    if (metaData.TryGetProperty("schemaString", out var schemaString))
                    {
                        return ParseDeltaSchema(schemaString.GetString() ?? "");
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

            // Validate transaction log format
            var lines = json.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            int lineNum = 0;

            foreach (var line in lines)
            {
                lineNum++;
                try
                {
                    var entry = JsonSerializer.Deserialize<JsonElement>(line);

                    // Each entry must have one of: protocol, metaData, add, remove, commitInfo
                    bool hasValidAction = entry.TryGetProperty("protocol", out _)
                        || entry.TryGetProperty("metaData", out _)
                        || entry.TryGetProperty("add", out _)
                        || entry.TryGetProperty("remove", out _)
                        || entry.TryGetProperty("commitInfo", out _);

                    if (!hasValidAction)
                    {
                        errors.Add(new ValidationError
                        {
                            Message = "Transaction log entry missing valid action",
                            LineNumber = lineNum
                        });
                    }
                }
                catch (JsonException ex)
                {
                    errors.Add(new ValidationError
                    {
                        Message = $"Invalid JSON: {ex.Message}",
                        LineNumber = lineNum
                    });
                }
            }

            return errors.Count == 0
                ? FormatValidationResult.Valid
                : FormatValidationResult.Invalid(errors.ToArray());
        }
        catch (Exception ex)
        {
            return FormatValidationResult.Invalid(new ValidationError { Message = ex.Message });
        }
    }

    private FormatSchema ParseDeltaSchema(string schemaJson)
    {
        // Delta schema is Spark SQL struct type JSON
        // Example: {"type":"struct","fields":[{"name":"id","type":"long","nullable":true}]}
        var schema = JsonSerializer.Deserialize<JsonElement>(schemaJson);
        var fields = new List<SchemaField>();

        if (schema.TryGetProperty("fields", out var fieldsArray))
        {
            foreach (var field in fieldsArray.EnumerateArray())
            {
                var name = field.GetProperty("name").GetString() ?? "";
                var dataType = field.GetProperty("type").GetString() ?? "unknown";
                var nullable = field.TryGetProperty("nullable", out var n) && n.GetBoolean();

                fields.Add(new SchemaField
                {
                    Name = name,
                    DataType = MapSparkTypeToDelta(dataType),
                    Nullable = nullable,
                    Description = field.TryGetProperty("metadata", out var meta)
                        ? meta.ToString()
                        : null
                });
            }
        }

        return new FormatSchema
        {
            Name = "Delta Lake Table",
            SchemaType = "delta",
            Fields = fields,
            RawSchema = schemaJson
        };
    }

    private string MapSparkTypeToDelta(string sparkType)
    {
        return sparkType switch
        {
            "long" => "bigint",
            "integer" => "int",
            "short" => "smallint",
            "byte" => "tinyint",
            "double" => "double",
            "float" => "float",
            "string" => "string",
            "boolean" => "boolean",
            "binary" => "binary",
            "date" => "date",
            "timestamp" => "timestamp",
            _ => sparkType
        };
    }
}
