using DataWarehouse.SDK.Contracts;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.DataFormat;

namespace DataWarehouse.Plugins.UltimateDataFormat.Strategies.Text;

/// <summary>
/// JSON format strategy using System.Text.Json.
/// Supports parsing and serialization of JSON data with hierarchical structure support.
/// </summary>
public sealed class JsonStrategy : DataFormatStrategyBase
{
    public override string StrategyId => "json";

    public override string DisplayName => "JSON";

    /// <summary>Production hardening: initialization with counter tracking.</summary>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken) { IncrementCounter("json.init"); return base.InitializeAsyncCore(cancellationToken); }
    /// <summary>Production hardening: graceful shutdown.</summary>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken) { IncrementCounter("json.shutdown"); return base.ShutdownAsyncCore(cancellationToken); }
    /// <summary>Production hardening: cached health check.</summary>
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default) =>
        GetCachedHealthAsync(async (c) => new StrategyHealthCheckResult(true, "JSON strategy ready", new Dictionary<string, object> { ["ParseOps"] = GetCounter("json.parse"), ["SerializeOps"] = GetCounter("json.serialize") }), TimeSpan.FromSeconds(60), ct);

    public override DataFormatCapabilities Capabilities => new()
    {
        Bidirectional = true,
        Streaming = true,
        SchemaAware = false,
        CompressionAware = false,
        RandomAccess = false,
        SelfDescribing = true,
        SupportsHierarchicalData = true,
        SupportsBinaryData = false
    };

    public override FormatInfo FormatInfo => new()
    {
        FormatId = "json",
        Extensions = new[] { ".json", ".geojson" },
        MimeTypes = new[] { "application/json", "text/json" },
        DomainFamily = DomainFamily.General,
        Description = "JavaScript Object Notation - lightweight data interchange format",
        SpecificationVersion = "RFC 8259",
        SpecificationUrl = "https://tools.ietf.org/html/rfc8259"
    };

    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct)
    {
        // Read first byte
        var firstByte = stream.ReadByte();
        if (firstByte == -1)
            return false;

        // JSON starts with { or [ (after optional whitespace)
        while (firstByte == ' ' || firstByte == '\t' || firstByte == '\r' || firstByte == '\n')
        {
            firstByte = stream.ReadByte();
            if (firstByte == -1)
                return false;
        }

        return firstByte == '{' || firstByte == '[';
    }

    public override async Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default)
    {
        if (input == null)
            throw new ArgumentNullException(nameof(input));

        try
        {
            var startPosition = input.Position;
            var data = await JsonSerializer.DeserializeAsync<JsonElement>(input, cancellationToken: ct);
            var bytesProcessed = input.Position - startPosition;

            return DataFormatResult.Ok(data, bytesProcessed);
        }
        catch (JsonException ex)
        {
            return DataFormatResult.Fail($"JSON parsing failed: {ex.Message}");
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

            var options = new JsonSerializerOptions
            {
                WriteIndented = context.Options?.ContainsKey("indent") == true
                    && context.Options["indent"] switch
                    {
                        bool b => b,
                        string s => bool.TryParse(s, out var bv) && bv,
                        _ => false
                    }
            };

            await JsonSerializer.SerializeAsync(output, data, options, ct);
            var bytesProcessed = output.Position - startPosition;

            return DataFormatResult.Ok(null, bytesProcessed);
        }
        catch (JsonException ex)
        {
            return DataFormatResult.Fail($"JSON serialization failed: {ex.Message}");
        }
    }

    protected override async Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct)
    {
        if (stream == null)
            throw new ArgumentNullException(nameof(stream));

        try
        {
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return FormatValidationResult.Valid;
        }
        catch (JsonException ex)
        {
            return FormatValidationResult.Invalid(new ValidationError
            {
                Message = ex.Message,
                LineNumber = ex.LineNumber,
                ByteOffset = ex.BytePositionInLine
            });
        }
    }
}
