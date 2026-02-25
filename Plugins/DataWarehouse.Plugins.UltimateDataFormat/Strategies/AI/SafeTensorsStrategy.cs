using DataWarehouse.SDK.Contracts;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.DataFormat;

namespace DataWarehouse.Plugins.UltimateDataFormat.Strategies.AI;

/// <summary>
/// SafeTensors format strategy from Hugging Face.
/// SafeTensors is a safe tensor serialization format with header metadata.
/// </summary>
public sealed class SafeTensorsStrategy : DataFormatStrategyBase
{
    public override string StrategyId => "safetensors";

    public override string DisplayName => "SafeTensors";

    /// <summary>Production hardening: initialization with counter tracking.</summary>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken) { IncrementCounter("safetensors.init"); return base.InitializeAsyncCore(cancellationToken); }
    /// <summary>Production hardening: graceful shutdown.</summary>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken) { IncrementCounter("safetensors.shutdown"); return base.ShutdownAsyncCore(cancellationToken); }
    /// <summary>Production hardening: cached health check.</summary>
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default) =>
        GetCachedHealthAsync(async (c) => new StrategyHealthCheckResult(true, "SafeTensors strategy ready", new Dictionary<string, object> { ["ParseOps"] = GetCounter("safetensors.parse"), ["SerializeOps"] = GetCounter("safetensors.serialize") }), TimeSpan.FromSeconds(60), ct);

    public override DataFormatCapabilities Capabilities => new()
    {
        Bidirectional = true,
        Streaming = false,
        SchemaAware = true,
        CompressionAware = false,
        RandomAccess = true,
        SelfDescribing = true,
        SupportsBinaryData = true,
        SupportsHierarchicalData = false
    };

    public override FormatInfo FormatInfo => new()
    {
        FormatId = "safetensors",
        Extensions = new[] { ".safetensors" },
        MimeTypes = new[] { "application/safetensors", "application/octet-stream" },
        DomainFamily = DomainFamily.MachineLearning,
        Description = "SafeTensors - safe tensor serialization format from Hugging Face",
        SpecificationVersion = "0.4",
        SpecificationUrl = "https://github.com/huggingface/safetensors"
    };

    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct)
    {
        // SafeTensors format:
        // [8 bytes: header length as little-endian u64]
        // [N bytes: JSON header]
        // [remaining: tensor data]

        if (stream.Length < 8)
            return false;

        var lengthBytes = new byte[8];
        await stream.ReadExactlyAsync(lengthBytes, 0, 8, ct);

        // Read header length (little-endian)
        long headerLength = BitConverter.ToInt64(lengthBytes, 0);

        // Sanity check: header should be between 100 bytes and 10MB
        if (headerLength < 100 || headerLength > 10_000_000)
            return false;

        // Check if we can read the header
        if (stream.Length < 8 + headerLength)
            return false;

        // Read header JSON
        var headerBytes = new byte[headerLength];
        await stream.ReadExactlyAsync(headerBytes, 0, (int)headerLength, ct);

        try
        {
            var headerJson = Encoding.UTF8.GetString(headerBytes);
            var header = JsonSerializer.Deserialize<JsonElement>(headerJson);

            // SafeTensors header has "__metadata__" and tensor entries
            // Each tensor entry has: "dtype", "shape", "data_offsets"
            bool hasTensorEntry = false;
            foreach (var property in header.EnumerateObject())
            {
                if (property.Name != "__metadata__")
                {
                    var value = property.Value;
                    if (value.TryGetProperty("dtype", out _) &&
                        value.TryGetProperty("shape", out _) &&
                        value.TryGetProperty("data_offsets", out _))
                    {
                        hasTensorEntry = true;
                        break;
                    }
                }
            }

            return hasTensorEntry;
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
            // Read header length
            var lengthBytes = new byte[8];
            await input.ReadExactlyAsync(lengthBytes, 0, 8, ct);
            long headerLength = BitConverter.ToInt64(lengthBytes, 0);

            // Read header JSON
            var headerBytes = new byte[headerLength];
            await input.ReadExactlyAsync(headerBytes, 0, (int)headerLength, ct);
            var headerJson = Encoding.UTF8.GetString(headerBytes);

            var header = JsonSerializer.Deserialize<JsonElement>(headerJson);
            var tensors = new Dictionary<string, SafeTensor>();

            // Parse tensor metadata
            foreach (var property in header.EnumerateObject())
            {
                if (property.Name == "__metadata__")
                    continue;

                var tensorName = property.Name;
                var tensorInfo = property.Value;

                var dtype = tensorInfo.GetProperty("dtype").GetString() ?? "unknown";
                var shape = tensorInfo.GetProperty("shape")
                    .EnumerateArray()
                    .Select(e => e.GetInt64())
                    .ToArray();
                var dataOffsets = tensorInfo.GetProperty("data_offsets")
                    .EnumerateArray()
                    .Select(e => e.GetInt64())
                    .ToArray();

                if (dataOffsets.Length != 2)
                    continue;

                long startOffset = dataOffsets[0];
                long endOffset = dataOffsets[1];
                long tensorSize = endOffset - startOffset;

                // Read tensor data
                var tensorData = new byte[tensorSize];
                input.Position = 8 + headerLength + startOffset;
                await input.ReadExactlyAsync(tensorData, 0, (int)tensorSize, ct);

                tensors[tensorName] = new SafeTensor
                {
                    Name = tensorName,
                    Dtype = dtype,
                    Shape = shape,
                    Data = tensorData
                };
            }

            return DataFormatResult.Ok(tensors, input.Length, tensors.Count);
        }
        catch (Exception ex)
        {
            return DataFormatResult.Fail($"SafeTensors parsing failed: {ex.Message}");
        }
    }

    public override async Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default)
    {
        try
        {
            if (data is not Dictionary<string, SafeTensor> tensors)
                return DataFormatResult.Fail("Data must be Dictionary<string, SafeTensor>");

            // Build header JSON
            var headerDict = new Dictionary<string, object>();

            long currentOffset = 0;
            foreach (var (name, tensor) in tensors)
            {
                long tensorSize = tensor.Data.Length;
                headerDict[name] = new
                {
                    dtype = tensor.Dtype,
                    shape = tensor.Shape,
                    data_offsets = new[] { currentOffset, currentOffset + tensorSize }
                };
                currentOffset += tensorSize;
            }

            var headerJson = JsonSerializer.Serialize(headerDict);
            var headerBytes = Encoding.UTF8.GetBytes(headerJson);
            long headerLength = headerBytes.Length;

            // Write header length (8 bytes little-endian)
            var lengthBytes = BitConverter.GetBytes(headerLength);
            await output.WriteAsync(lengthBytes, 0, 8, ct);

            // Write header JSON
            await output.WriteAsync(headerBytes, 0, headerBytes.Length, ct);

            // Write tensor data
            foreach (var (_, tensor) in tensors)
            {
                await output.WriteAsync(tensor.Data, 0, tensor.Data.Length, ct);
            }

            return DataFormatResult.Ok(null, output.Length, tensors.Count);
        }
        catch (Exception ex)
        {
            return DataFormatResult.Fail($"SafeTensors serialization failed: {ex.Message}");
        }
    }

    protected override async Task<FormatSchema?> ExtractSchemaCoreAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            // Read header length
            var lengthBytes = new byte[8];
            await stream.ReadExactlyAsync(lengthBytes, 0, 8, ct);
            long headerLength = BitConverter.ToInt64(lengthBytes, 0);

            // Read header JSON
            var headerBytes = new byte[headerLength];
            await stream.ReadExactlyAsync(headerBytes, 0, (int)headerLength, ct);
            var headerJson = Encoding.UTF8.GetString(headerBytes);

            var header = JsonSerializer.Deserialize<JsonElement>(headerJson);
            var fields = new List<SchemaField>();

            // Extract tensor schema
            foreach (var property in header.EnumerateObject())
            {
                if (property.Name == "__metadata__")
                    continue;

                var tensorName = property.Name;
                var tensorInfo = property.Value;

                var dtype = tensorInfo.GetProperty("dtype").GetString() ?? "unknown";
                var shape = tensorInfo.GetProperty("shape")
                    .EnumerateArray()
                    .Select(e => e.GetInt64().ToString())
                    .ToArray();

                fields.Add(new SchemaField
                {
                    Name = tensorName,
                    DataType = $"{dtype}[{string.Join(",", shape)}]",
                    Nullable = false,
                    Description = $"Tensor with shape [{string.Join(", ", shape)}]"
                });
            }

            return new FormatSchema
            {
                Name = "SafeTensors Model",
                SchemaType = "safetensors",
                Fields = fields
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
            var errors = new List<ValidationError>();

            // Read header length
            if (stream.Length < 8)
            {
                errors.Add(new ValidationError { Message = "File too small (< 8 bytes)" });
                return FormatValidationResult.Invalid(errors.ToArray());
            }

            var lengthBytes = new byte[8];
            await stream.ReadExactlyAsync(lengthBytes, 0, 8, ct);
            long headerLength = BitConverter.ToInt64(lengthBytes, 0);

            // Validate header length
            if (headerLength < 10 || headerLength > 100_000_000)
                errors.Add(new ValidationError { Message = $"Invalid header length: {headerLength}" });

            if (stream.Length < 8 + headerLength)
                errors.Add(new ValidationError { Message = "File truncated (header extends past EOF)" });

            if (errors.Count > 0)
                return FormatValidationResult.Invalid(errors.ToArray());

            // Read and validate header JSON
            var headerBytes = new byte[headerLength];
            await stream.ReadExactlyAsync(headerBytes, 0, (int)headerLength, ct);

            try
            {
                var headerJson = Encoding.UTF8.GetString(headerBytes);
                var header = JsonSerializer.Deserialize<JsonElement>(headerJson);

                // Validate tensor entries
                bool hasTensors = false;
                foreach (var property in header.EnumerateObject())
                {
                    if (property.Name == "__metadata__")
                        continue;

                    hasTensors = true;
                    var tensorInfo = property.Value;

                    if (!tensorInfo.TryGetProperty("dtype", out _))
                        errors.Add(new ValidationError { Message = $"Tensor '{property.Name}' missing dtype" });

                    if (!tensorInfo.TryGetProperty("shape", out _))
                        errors.Add(new ValidationError { Message = $"Tensor '{property.Name}' missing shape" });

                    if (!tensorInfo.TryGetProperty("data_offsets", out var offsets))
                        errors.Add(new ValidationError { Message = $"Tensor '{property.Name}' missing data_offsets" });
                    else if (offsets.GetArrayLength() != 2)
                        errors.Add(new ValidationError { Message = $"Tensor '{property.Name}' data_offsets must have 2 elements" });
                }

                if (!hasTensors)
                    errors.Add(new ValidationError { Message = "No tensors found in header" });
            }
            catch (JsonException ex)
            {
                errors.Add(new ValidationError { Message = $"Invalid JSON header: {ex.Message}" });
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
}

/// <summary>
/// Represents a SafeTensors tensor.
/// </summary>
public sealed class SafeTensor
{
    public required string Name { get; init; }
    public required string Dtype { get; init; }
    public required long[] Shape { get; init; }
    public required byte[] Data { get; init; }
}
