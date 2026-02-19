using DataWarehouse.SDK.Contracts;
using MessagePack;
using DataWarehouse.SDK.Contracts.DataFormat;

namespace DataWarehouse.Plugins.UltimateDataFormat.Strategies.Binary;

/// <summary>
/// MessagePack format strategy using MessagePack-CSharp library.
/// Supports efficient binary serialization with streaming capability.
/// </summary>
public sealed class MessagePackStrategy : DataFormatStrategyBase
{
    public override string StrategyId => "msgpack";

    public override string DisplayName => "MessagePack";

    /// <summary>Production hardening: initialization with counter tracking.</summary>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken) { IncrementCounter("msgpack.init"); return base.InitializeAsyncCore(cancellationToken); }
    /// <summary>Production hardening: graceful shutdown.</summary>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken) { IncrementCounter("msgpack.shutdown"); return base.ShutdownAsyncCore(cancellationToken); }
    /// <summary>Production hardening: cached health check.</summary>
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default) =>
        GetCachedHealthAsync(async (c) => new StrategyHealthCheckResult(true, "MessagePack strategy ready", new Dictionary<string, object> { ["ParseOps"] = GetCounter("msgpack.parse"), ["SerializeOps"] = GetCounter("msgpack.serialize") }), TimeSpan.FromSeconds(60), ct);

    public override DataFormatCapabilities Capabilities => new()
    {
        Bidirectional = true,
        Streaming = true,
        SchemaAware = false,
        CompressionAware = false,
        RandomAccess = false,
        SelfDescribing = false,
        SupportsHierarchicalData = true,
        SupportsBinaryData = true
    };

    public override FormatInfo FormatInfo => new()
    {
        FormatId = "msgpack",
        Extensions = new[] { ".msgpack", ".mp" },
        MimeTypes = new[] { "application/msgpack", "application/x-msgpack" },
        DomainFamily = DomainFamily.General,
        Description = "MessagePack - efficient binary serialization format",
        SpecificationUrl = "https://msgpack.org/index.html"
    };

    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[16];
        var bytesRead = await stream.ReadAsync(buffer, ct);

        if (bytesRead < 1)
            return false;

        byte firstByte = buffer[0];

        // MessagePack format markers
        // fixmap: 0x80-0x8f
        // fixarray: 0x90-0x9f
        // fixstr: 0xa0-0xbf
        // nil: 0xc0
        // false/true: 0xc2-0xc3
        // bin8/16/32: 0xc4-0xc6
        // ext8/16/32: 0xc7-0xc9
        // float32/64: 0xca-0xcb
        // uint8/16/32/64: 0xcc-0xcf
        // int8/16/32/64: 0xd0-0xd3
        // str8/16/32: 0xd9-0xdb
        // array16/32: 0xdc-0xdd
        // map16/32: 0xde-0xdf
        // negative fixint: 0xe0-0xff

        return (firstByte >= 0x80 && firstByte <= 0x8f) ||  // fixmap
               (firstByte >= 0x90 && firstByte <= 0x9f) ||  // fixarray
               (firstByte >= 0xa0 && firstByte <= 0xbf) ||  // fixstr
               (firstByte >= 0xc0 && firstByte <= 0xcb) ||  // various types
               (firstByte >= 0xcc && firstByte <= 0xdf) ||  // ints, strings, arrays, maps
               (firstByte >= 0xe0);                          // negative fixint
    }

    public override async Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default)
    {
        if (input == null)
            throw new ArgumentNullException(nameof(input));

        try
        {
            var startPosition = input.Position;

            // MessagePack requires deserializing to a specific type
            // For generic parsing, use MessagePackSerializer.Typeless
            var data = await MessagePackSerializer.Typeless.DeserializeAsync(input, cancellationToken: ct);

            var bytesProcessed = input.Position - startPosition;
            return DataFormatResult.Ok(data, bytesProcessed);
        }
        catch (Exception ex)
        {
            return DataFormatResult.Fail($"MessagePack parsing failed: {ex.Message}");
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

            await MessagePackSerializer.Typeless.SerializeAsync(output, data, cancellationToken: ct);

            var bytesProcessed = output.Position - startPosition;
            return DataFormatResult.Ok(null, bytesProcessed);
        }
        catch (Exception ex)
        {
            return DataFormatResult.Fail($"MessagePack serialization failed: {ex.Message}");
        }
    }

    protected override async Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct)
    {
        if (stream == null)
            throw new ArgumentNullException(nameof(stream));

        try
        {
            // Attempt to deserialize to validate format
            await MessagePackSerializer.Typeless.DeserializeAsync(stream, cancellationToken: ct);
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
