using YamlDotNet.Serialization;
using DataWarehouse.SDK.Contracts.DataFormat;

namespace DataWarehouse.Plugins.UltimateDataFormat.Strategies.Text;

/// <summary>
/// YAML format strategy using YamlDotNet.
/// Supports parsing and serialization of YAML data with hierarchical structure support.
/// </summary>
public sealed class YamlStrategy : DataFormatStrategyBase
{
    public override string StrategyId => "yaml";

    public override string DisplayName => "YAML";

    public override DataFormatCapabilities Capabilities => new()
    {
        Bidirectional = true,
        Streaming = false,
        SchemaAware = false,
        CompressionAware = false,
        RandomAccess = false,
        SelfDescribing = true,
        SupportsHierarchicalData = true,
        SupportsBinaryData = false
    };

    public override FormatInfo FormatInfo => new()
    {
        FormatId = "yaml",
        Extensions = new[] { ".yaml", ".yml" },
        MimeTypes = new[] { "application/x-yaml", "text/yaml" },
        DomainFamily = DomainFamily.General,
        Description = "YAML Ain't Markup Language - human-friendly data serialization",
        SpecificationVersion = "1.2",
        SpecificationUrl = "https://yaml.org/spec/1.2/spec.html"
    };

    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[256];
        var bytesRead = await stream.ReadAsync(buffer, ct);

        if (bytesRead == 0)
            return false;

        var text = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead).TrimStart();

        // Check for YAML markers
        return text.StartsWith("---") ||
               text.Contains(": ") ||
               text.Contains(":\n") ||
               text.Contains(":\r\n");
    }

    public override async Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default)
    {
        if (input == null)
            throw new ArgumentNullException(nameof(input));

        try
        {
            var startPosition = input.Position;
            using var reader = new StreamReader(input, leaveOpen: true);
            var content = await reader.ReadToEndAsync(ct);

            var deserializer = new DeserializerBuilder().Build();
            var data = deserializer.Deserialize(content);

            var bytesProcessed = input.Position - startPosition;
            return DataFormatResult.Ok(data, bytesProcessed);
        }
        catch (Exception ex)
        {
            return DataFormatResult.Fail($"YAML parsing failed: {ex.Message}");
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

            var serializer = new SerializerBuilder().Build();
            var yaml = serializer.Serialize(data);

            using var writer = new StreamWriter(output, leaveOpen: true);
            await writer.WriteAsync(yaml.AsMemory(), ct);
            await writer.FlushAsync(ct);

            var bytesProcessed = output.Position - startPosition;
            return DataFormatResult.Ok(null, bytesProcessed);
        }
        catch (Exception ex)
        {
            return DataFormatResult.Fail($"YAML serialization failed: {ex.Message}");
        }
    }

    protected override async Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct)
    {
        if (stream == null)
            throw new ArgumentNullException(nameof(stream));

        try
        {
            using var reader = new StreamReader(stream, leaveOpen: true);
            var content = await reader.ReadToEndAsync(ct);

            var deserializer = new DeserializerBuilder().Build();
            deserializer.Deserialize(content);

            return FormatValidationResult.Valid;
        }
        catch (Exception ex)
        {
            return FormatValidationResult.Invalid(new ValidationError
            {
                Message = ex.Message
            });
        }
    }
}
