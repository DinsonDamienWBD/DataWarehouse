using DataWarehouse.SDK.Contracts;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.DataFormat;

namespace DataWarehouse.Plugins.UltimateDataFormat.Strategies.Geo;

/// <summary>
/// GeoJSON format strategy for encoding geographic data structures.
/// JSON-based format for simple geographic features with geometries (Point, LineString, Polygon, etc.).
/// RFC 7946 compliant with WGS84 coordinate reference system.
/// </summary>
public sealed class GeoJsonStrategy : DataFormatStrategyBase
{
    public override string StrategyId => "geojson";

    public override string DisplayName => "GeoJSON";

    /// <summary>Production hardening: initialization with counter tracking.</summary>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken) { IncrementCounter("geojson.init"); return base.InitializeAsyncCore(cancellationToken); }
    /// <summary>Production hardening: graceful shutdown.</summary>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken) { IncrementCounter("geojson.shutdown"); return base.ShutdownAsyncCore(cancellationToken); }
    /// <summary>Production hardening: cached health check.</summary>
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default) =>
        GetCachedHealthAsync(async (c) => new StrategyHealthCheckResult(true, "GeoJSON strategy ready", new Dictionary<string, object> { ["ParseOps"] = GetCounter("geojson.parse"), ["SerializeOps"] = GetCounter("geojson.serialize") }), TimeSpan.FromSeconds(60), ct);

    public override DataFormatCapabilities Capabilities => new()
    {
        Bidirectional = true,
        Streaming = true,
        SchemaAware = false, // GeoJSON structure is fixed but flexible
        CompressionAware = false,
        RandomAccess = false,
        SelfDescribing = true,
        SupportsHierarchicalData = true,
        SupportsBinaryData = false
    };

    public override FormatInfo FormatInfo => new()
    {
        FormatId = "geojson",
        Extensions = new[] { ".geojson", ".json" },
        MimeTypes = new[] { "application/geo+json", "application/vnd.geo+json" },
        DomainFamily = DomainFamily.Geospatial,
        Description = "GeoJSON format for encoding geographic data structures",
        SpecificationVersion = "RFC 7946",
        SpecificationUrl = "https://tools.ietf.org/html/rfc7946"
    };

    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct)
    {
        // GeoJSON is JSON with specific structure
        // Must have "type" property with value: "Feature", "FeatureCollection", or geometry type
        try
        {
            stream.Position = 0;
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return false;

            // Check for "type" property
            if (!root.TryGetProperty("type", out var typeProperty))
                return false;

            if (typeProperty.ValueKind != JsonValueKind.String)
                return false;

            var type = typeProperty.GetString();

            // Valid GeoJSON types
            var validTypes = new HashSet<string>
            {
                "Feature", "FeatureCollection",
                "Point", "LineString", "Polygon",
                "MultiPoint", "MultiLineString", "MultiPolygon",
                "GeometryCollection"
            };

            return validTypes.Contains(type ?? "");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public override async Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default)
    {
        try
        {
            input.Position = 0;
            using var doc = await JsonDocument.ParseAsync(input, cancellationToken: ct);
            var root = doc.RootElement;

            // Validate GeoJSON structure
            if (!root.TryGetProperty("type", out var typeProperty))
            {
                return DataFormatResult.Fail("Missing 'type' property in GeoJSON");
            }

            var type = typeProperty.GetString();
            var warnings = new List<string>();

            // Validate based on type
            if (type == "FeatureCollection")
            {
                if (!root.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
                {
                    return DataFormatResult.Fail("FeatureCollection must have 'features' array");
                }

                var featureCount = 0;
                foreach (var feature in features.EnumerateArray())
                {
                    featureCount++;
                    if (!ValidateFeature(feature, warnings))
                    {
                        return DataFormatResult.Fail($"Invalid feature at index {featureCount - 1}");
                    }
                }

                return new DataFormatResult
                {
                    Success = true,
                    Data = root.Clone(),
                    BytesProcessed = input.Length,
                    RecordsProcessed = featureCount,
                    Warnings = warnings.Count > 0 ? warnings : null
                };
            }
            else if (type == "Feature")
            {
                if (!ValidateFeature(root, warnings))
                {
                    return DataFormatResult.Fail("Invalid Feature structure");
                }

                return new DataFormatResult
                {
                    Success = true,
                    Data = root.Clone(),
                    BytesProcessed = input.Length,
                    RecordsProcessed = 1,
                    Warnings = warnings.Count > 0 ? warnings : null
                };
            }
            else
            {
                // Geometry object
                if (!ValidateGeometry(root, warnings))
                {
                    return DataFormatResult.Fail("Invalid Geometry structure");
                }

                return new DataFormatResult
                {
                    Success = true,
                    Data = root.Clone(),
                    BytesProcessed = input.Length,
                    RecordsProcessed = 1,
                    Warnings = warnings.Count > 0 ? warnings : null
                };
            }
        }
        catch (JsonException ex)
        {
            return DataFormatResult.Fail($"JSON parsing error: {ex.Message}");
        }
    }

    public override async Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default)
    {
        try
        {
            // Serialize using JsonSerializer
            var options = new JsonSerializerOptions
            {
                WriteIndented = context.Options?.ContainsKey("indent") == true &&
                               (bool)context.Options["indent"],
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            await JsonSerializer.SerializeAsync(output, data, options, ct);

            return new DataFormatResult
            {
                Success = true,
                BytesProcessed = output.Length
            };
        }
        catch (Exception ex)
        {
            return DataFormatResult.Fail($"Serialization error: {ex.Message}");
        }
    }

    protected override async Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct)
    {
        var errors = new List<ValidationError>();
        var warnings = new List<ValidationWarning>();

        try
        {
            stream.Position = 0;
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            // Check type property
            if (!root.TryGetProperty("type", out var typeProperty))
            {
                errors.Add(new ValidationError { Message = "Missing required 'type' property" });
                return FormatValidationResult.Invalid(errors.ToArray());
            }

            var type = typeProperty.GetString();
            var validTypes = new HashSet<string>
            {
                "Feature", "FeatureCollection",
                "Point", "LineString", "Polygon",
                "MultiPoint", "MultiLineString", "MultiPolygon",
                "GeometryCollection"
            };

            if (!validTypes.Contains(type ?? ""))
            {
                errors.Add(new ValidationError { Message = $"Invalid type: {type}" });
            }

            // Validate structure based on type
            if (type == "FeatureCollection")
            {
                if (!root.TryGetProperty("features", out var features))
                {
                    errors.Add(new ValidationError { Message = "FeatureCollection missing 'features' array" });
                }
                else if (features.ValueKind != JsonValueKind.Array)
                {
                    errors.Add(new ValidationError { Message = "'features' must be an array" });
                }
            }
            else if (type == "Feature")
            {
                if (!root.TryGetProperty("geometry", out _))
                {
                    errors.Add(new ValidationError { Message = "Feature missing 'geometry' property" });
                }
            }

            return errors.Count > 0
                ? FormatValidationResult.Invalid(errors.ToArray())
                : new FormatValidationResult { IsValid = true, Warnings = warnings.Count > 0 ? warnings : null };
        }
        catch (JsonException ex)
        {
            errors.Add(new ValidationError { Message = $"JSON parsing error: {ex.Message}" });
            return FormatValidationResult.Invalid(errors.ToArray());
        }
    }

    private bool ValidateFeature(JsonElement feature, List<string> warnings)
    {
        if (!feature.TryGetProperty("type", out var type) || type.GetString() != "Feature")
        {
            return false;
        }

        if (!feature.TryGetProperty("geometry", out var geometry))
        {
            warnings.Add("Feature missing 'geometry' property");
            return true; // Null geometry is allowed
        }

        return ValidateGeometry(geometry, warnings);
    }

    private bool ValidateGeometry(JsonElement geometry, List<string> warnings)
    {
        if (geometry.ValueKind == JsonValueKind.Null)
        {
            return true; // Null geometry is valid
        }

        if (!geometry.TryGetProperty("type", out var type))
        {
            return false;
        }

        if (!geometry.TryGetProperty("coordinates", out var coordinates) &&
            type.GetString() != "GeometryCollection")
        {
            return false;
        }

        return true;
    }
}
