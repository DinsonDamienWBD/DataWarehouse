using System.Text;
using DataWarehouse.SDK.Contracts.DataFormat;

namespace DataWarehouse.Plugins.UltimateDataFormat.Strategies.AI;

/// <summary>
/// ONNX (Open Neural Network Exchange) format strategy.
/// ONNX is an open format for AI models using Protocol Buffers.
/// </summary>
public sealed class OnnxStrategy : DataFormatStrategyBase
{
    public override string StrategyId => "onnx";

    public override string DisplayName => "ONNX";

    public override DataFormatCapabilities Capabilities => new()
    {
        Bidirectional = false, // Read-only without ONNX library
        Streaming = false,
        SchemaAware = true,
        CompressionAware = false,
        RandomAccess = false,
        SelfDescribing = true,
        SupportsBinaryData = true,
        SupportsHierarchicalData = true
    };

    public override FormatInfo FormatInfo => new()
    {
        FormatId = "onnx",
        Extensions = new[] { ".onnx" },
        MimeTypes = new[] { "application/onnx", "application/octet-stream" },
        DomainFamily = DomainFamily.MachineLearning,
        Description = "Open Neural Network Exchange - interoperable AI model format",
        SpecificationVersion = "1.14",
        SpecificationUrl = "https://onnx.ai/onnx/intro/"
    };

    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct)
    {
        // ONNX uses Protocol Buffers encoding
        // Check for protobuf field tags and "onnx" string in early bytes
        var buffer = new byte[512];
        var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
        if (bytesRead < 10)
            return false;

        // ONNX models start with protobuf ModelProto structure
        // Look for:
        // 1. Field tag 0x0A (field 1, wire type 2 = length-delimited) for ir_version
        // 2. String "onnx" in producer name or domain
        // 3. Field tags for graph (field 7)

        // Check for protobuf field tags (0x08, 0x0A, 0x12, 0x1A, etc.)
        bool hasProtobufTags = false;
        for (int i = 0; i < Math.Min(50, bytesRead - 1); i++)
        {
            byte b = buffer[i];
            // Check for valid protobuf field tags (field number 1-15, wire type 0-2)
            if ((b >= 0x08 && b <= 0x0F) || (b >= 0x10 && b <= 0x1F) || (b >= 0x30 && b <= 0x3F))
            {
                hasProtobufTags = true;
                break;
            }
        }

        if (!hasProtobufTags)
            return false;

        // Look for "onnx" string
        var text = Encoding.ASCII.GetString(buffer, 0, bytesRead);
        if (text.Contains("onnx", StringComparison.OrdinalIgnoreCase))
            return true;

        // Look for common ONNX operator strings
        if (text.Contains("Conv") || text.Contains("MatMul") || text.Contains("Relu") || text.Contains("Gemm"))
            return true;

        return false;
    }

    public override Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default)
    {
        // Stub: Full implementation requires Microsoft.ML.OnnxRuntime or ONNX.NET library
        // Would need to:
        // 1. Deserialize ModelProto from protobuf
        // 2. Extract graph (nodes, inputs, outputs, initializers)
        // 3. Extract metadata (producer, version, doc_string)
        // 4. Return model structure

        return Task.FromResult(DataFormatResult.Fail(
            "ONNX parsing requires Microsoft.ML.OnnxRuntime library. " +
            "This strategy provides format detection and schema extraction only."));
    }

    public override Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default)
    {
        return Task.FromResult(DataFormatResult.Fail(
            "ONNX serialization requires Microsoft.ML.OnnxRuntime library."));
    }

    protected override async Task<FormatSchema?> ExtractSchemaCoreAsync(Stream stream, CancellationToken ct)
    {
        // Extract schema from ONNX model
        // We can parse basic protobuf structure without full ONNX library
        try
        {
            var buffer = new byte[stream.Length];
            await stream.ReadExactlyAsync(buffer, 0, buffer.Length, ct);

            var fields = new List<SchemaField>();

            // Parse protobuf to find graph inputs/outputs
            // This is a simplified parser - production would use ONNX library
            var inputsFound = ExtractProtobufStrings(buffer, "input");
            var outputsFound = ExtractProtobufStrings(buffer, "output");

            foreach (var input in inputsFound)
            {
                fields.Add(new SchemaField
                {
                    Name = input,
                    DataType = "tensor",
                    Nullable = false,
                    Description = "Model input"
                });
            }

            foreach (var output in outputsFound)
            {
                fields.Add(new SchemaField
                {
                    Name = output,
                    DataType = "tensor",
                    Nullable = false,
                    Description = "Model output"
                });
            }

            return new FormatSchema
            {
                Name = "ONNX Model",
                SchemaType = "onnx",
                Fields = fields,
                RawSchema = "Extracted from ONNX model metadata"
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
            var buffer = new byte[Math.Min(4096, stream.Length)];
            await stream.ReadExactlyAsync(buffer, 0, buffer.Length, ct);

            var errors = new List<ValidationError>();

            // Check for protobuf structure
            bool hasValidProtobuf = false;
            for (int i = 0; i < Math.Min(100, buffer.Length - 1); i++)
            {
                byte b = buffer[i];
                if ((b >= 0x08 && b <= 0x3F) && i + 1 < buffer.Length)
                {
                    hasValidProtobuf = true;
                    break;
                }
            }

            if (!hasValidProtobuf)
                errors.Add(new ValidationError { Message = "Invalid protobuf structure" });

            // Check for ONNX markers
            var text = Encoding.ASCII.GetString(buffer);
            if (!text.Contains("onnx", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("Conv") && !text.Contains("MatMul") && !text.Contains("Gemm"))
            {
                errors.Add(new ValidationError { Message = "Missing ONNX model markers" });
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

    private List<string> ExtractProtobufStrings(byte[] buffer, string prefix)
    {
        var strings = new List<string>();
        var text = Encoding.ASCII.GetString(buffer);

        // Simple heuristic: look for ASCII strings that match pattern
        for (int i = 0; i < buffer.Length - 10; i++)
        {
            // Protobuf strings are preceded by length byte
            if (buffer[i] > 0 && buffer[i] < 100)
            {
                int len = buffer[i];
                if (i + 1 + len < buffer.Length)
                {
                    var str = Encoding.ASCII.GetString(buffer, i + 1, Math.Min(len, 50));
                    if (str.Contains(prefix, StringComparison.OrdinalIgnoreCase) &&
                        str.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '.'))
                    {
                        strings.Add(str.Trim('\0'));
                    }
                }
            }
        }

        return strings.Distinct().Take(20).ToList();
    }
}
