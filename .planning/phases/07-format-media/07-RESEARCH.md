# Phase 7: Format & Media Processing - Research

**Researched:** 2026-02-11
**Domain:** Data format parsing, streaming data processing, and media transcoding
**Confidence:** HIGH

## Summary

Phase 7 focuses on three major plugin areas: data format handling (UltimateDataFormat), streaming data processing (UltimateStreaming/UltimateStreamingData), and media transcoding (UltimateMedia).

**Key Finding:** The roadmap references three Ultimate plugins (UltimateDataFormat, UltimateStreaming, UltimateMedia) that **do not currently exist** as separate plugins. Instead, the codebase contains:
1. **UltimateStreamingData** plugin - implements streaming data processing features (corresponds to roadmap's "UltimateStreaming")
2. **Transcoding.Media** plugin - implements media transcoding features (corresponds to roadmap's "UltimateMedia")
3. **No UltimateDataFormat plugin exists** - data format contracts are defined in SDK only

**SDK Foundation:** Comprehensive contracts already exist for all three domains:
- `DataWarehouse.SDK/Contracts/DataFormat/DataFormatStrategy.cs` - Complete interface and base class
- `DataWarehouse.SDK/Contracts/Streaming/StreamingStrategy.cs` - Complete streaming interface and types
- `DataWarehouse.SDK/Contracts/Media/IMediaStrategy.cs` - Complete media strategy interface

The research confirms this phase requires creating the missing UltimateDataFormat plugin and verifying/completing the two existing plugins.

**Primary recommendation:** Create UltimateDataFormat plugin from scratch using SDK contracts; verify UltimateStreamingData and Transcoding.Media plugins are complete per roadmap requirements.

## Standard Stack

### Core Libraries
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| None currently | N/A | No external NuGet packages | Plugins use only SDK contracts and .NET 10.0 |

### Data Format Parsing (Recommended for UltimateDataFormat)
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| Newtonsoft.Json / System.Text.Json | Latest | JSON parsing | Text formats (JSON, GeoJSON) |
| Apache.Parquet.Net | Latest | Parquet columnar format | Columnar data formats |
| Apache.Avro | Latest | Avro schema format | Schema-based formats |
| NetCDF | Latest | NetCDF scientific data | Scientific formats |
| NReco.PdfReader | Latest | PDF parsing | Document formats |
| HDF.PInvoke | Latest | HDF5 scientific data | Hierarchical scientific data |
| System.Xml | Built-in | XML parsing | Schema and document formats |
| CsvHelper | Latest | CSV parsing | Text formats |

### Streaming Processing (For UltimateStreamingData)
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| Confluent.Kafka | Latest | Kafka client | Message queue strategies |
| NATS.Client | Latest | NATS messaging | Lightweight messaging |
| RabbitMQ.Client | Latest | RabbitMQ AMQP | Message broker integration |
| StackExchange.Redis | Latest | Redis Streams | Fast in-memory streaming |

### Media Processing (For Transcoding.Media)
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| FFMpegCore | Latest | FFmpeg wrapper | Video/audio transcoding |
| Xabe.FFmpeg | Latest | Alternative FFmpeg wrapper | Media processing |
| SixLabors.ImageSharp | Latest | Image processing | Thumbnail generation |
| TagLibSharp | Latest | Media metadata extraction | Audio/video metadata |

**Installation:**
```bash
# Data Format
dotnet add package Newtonsoft.Json
dotnet add package Apache.Parquet.Net
dotnet add package Apache.Avro
dotnet add package CsvHelper

# Streaming (if needed)
dotnet add package Confluent.Kafka
dotnet add package NATS.Client
dotnet add package RabbitMQ.Client
dotnet add package StackExchange.Redis

# Media (if needed)
dotnet add package FFMpegCore
dotnet add package SixLabors.ImageSharp
dotnet add package TagLibSharp
```

## Architecture Patterns

### Current Plugin Structure (UltimateStreamingData)
```
Plugins/DataWarehouse.Plugins.UltimateStreamingData/
├── UltimateStreamingDataPlugin.cs      # Orchestrator with auto-discovery
├── Strategies/
│   ├── StreamProcessing/
│   │   └── StreamProcessingEngineStrategies.cs
│   ├── Windowing/
│   │   └── WindowingStrategies.cs
│   ├── State/
│   │   └── StateManagementStrategies.cs
│   ├── FaultTolerance/
│   │   └── FaultToleranceStrategies.cs
│   ├── Analytics/
│   │   └── StreamAnalyticsStrategies.cs
│   ├── Pipelines/
│   │   └── RealTimePipelineStrategies.cs
│   ├── EventDriven/
│   │   └── EventDrivenArchitectureStrategies.cs
│   └── Scalability/
│       └── ScalabilityStrategies.cs
└── DataWarehouse.Plugins.UltimateStreamingData.csproj
```

### Current Plugin Structure (Transcoding.Media)
```
Plugins/DataWarehouse.Plugins.Transcoding.Media/
├── MediaTranscodingPlugin.cs           # Orchestrator
└── DataWarehouse.Plugins.Transcoding.Media.csproj
```

### Recommended Project Structure (UltimateDataFormat - TO BE CREATED)
```
Plugins/DataWarehouse.Plugins.UltimateDataFormat/
├── UltimateDataFormatPlugin.cs         # Orchestrator with auto-discovery
├── Strategies/
│   ├── Text/                           # T110.B2: CSV, JSON, XML, YAML, TOML, INI, Properties
│   │   ├── JsonStrategy.cs
│   │   ├── XmlStrategy.cs
│   │   ├── CsvStrategy.cs
│   │   └── ...
│   ├── Binary/                         # T110.B3: Protobuf, MessagePack, CBOR, BSON, Smile
│   │   ├── ProtobufStrategy.cs
│   │   └── ...
│   ├── Schema/                         # T110.B4: Avro, Thrift, JSON Schema, XSD, ASN.1
│   │   ├── AvroStrategy.cs
│   │   └── ...
│   ├── Columnar/                       # T110.B5: Parquet, ORC, Arrow, ClickHouse, Iceberg
│   │   ├── ParquetStrategy.cs
│   │   └── ...
│   ├── Scientific/                     # T110.B6: HDF5, NetCDF, FITS, ENDF, ROOT
│   │   ├── Hdf5Strategy.cs
│   │   └── ...
│   ├── Geo/                            # T110.B7: GeoJSON, Shapefile, GeoTIFF, KML, GeoPackage
│   │   ├── GeoJsonStrategy.cs
│   │   └── ...
│   ├── Graph/                          # T110.B8: RDF, GraphML, GML, DOT, GEXF
│   │   └── ...
│   ├── Lakehouse/                      # T110.B9: Delta Lake, Iceberg, Hudi
│   │   └── ...
│   ├── AI/                             # T110.B10: ONNX, SafeTensors, TensorFlow
│   │   └── ...
│   ├── ML/                             # T110.B11: H2O, PMML, MLmodel
│   │   └── ...
│   └── Simulation/                     # T110.B12: VTK, CGNS, OpenFOAM
│       └── ...
└── DataWarehouse.Plugins.UltimateDataFormat.csproj
```

### Pattern 1: Strategy Auto-Discovery Pattern
**What:** Orchestrator scans assembly for implementations of strategy interface and registers them automatically
**When to use:** All three Ultimate plugins use this pattern
**Example:**
```csharp
// Source: UltimateStreamingData plugin
private void DiscoverAndRegisterStrategies()
{
    var assembly = Assembly.GetExecutingAssembly();
    var strategyTypes = assembly.GetTypes()
        .Where(t => !t.IsAbstract && typeof(IStreamingDataStrategy).IsAssignableFrom(t));

    foreach (var type in strategyTypes)
    {
        try
        {
            if (Activator.CreateInstance(type) is IStreamingDataStrategy strategy)
            {
                _registry.Register(strategy);
            }
        }
        catch
        {
            // Skip strategies that fail to instantiate
        }
    }
}
```

### Pattern 2: SDK Base Class with Intelligence Integration
**What:** All strategy base classes inherit from SDK contracts and include Intelligence integration hooks
**When to use:** Every format, streaming, and media strategy
**Example:**
```csharp
// Source: SDK/Contracts/DataFormat/DataFormatStrategy.cs
public abstract class DataFormatStrategyBase : IDataFormatStrategy
{
    protected IMessageBus? MessageBus { get; private set; }

    public virtual void ConfigureIntelligence(IMessageBus? messageBus)
    {
        MessageBus = messageBus;
    }

    protected bool IsIntelligenceAvailable => MessageBus != null;

    public virtual KnowledgeObject GetStrategyKnowledge() { /* ... */ }
    public virtual RegisteredCapability GetStrategyCapability() { /* ... */ }
}
```

### Pattern 3: Format Detection with Stream Reset
**What:** Detect format by reading stream header, then reset position
**When to use:** Format strategies that need to identify file types
**Example:**
```csharp
// Source: SDK/Contracts/DataFormat/DataFormatStrategy.cs
public virtual async Task<bool> DetectFormatAsync(Stream stream, CancellationToken ct = default)
{
    if (!stream.CanSeek)
        throw new ArgumentException("Stream must support seeking for format detection.");

    var originalPosition = stream.Position;
    try
    {
        return await DetectFormatCoreAsync(stream, ct);
    }
    finally
    {
        stream.Position = originalPosition;  // Always reset position
    }
}
```

### Pattern 4: Streaming Strategy Registry
**What:** Centralized registry for strategy lookup by ID or category
**When to use:** All Ultimate plugins with multiple strategies
**Example:**
```csharp
// Source: UltimateStreamingData plugin
public sealed class StreamingStrategyRegistry
{
    private readonly ConcurrentDictionary<string, IStreamingDataStrategy> _strategies = new();

    public void Register(IStreamingDataStrategy strategy)
    {
        _strategies[strategy.StrategyId] = strategy;
    }

    public IStreamingDataStrategy? GetStrategy(string strategyId) =>
        _strategies.TryGetValue(strategyId, out var strategy) ? strategy : null;

    public IEnumerable<IStreamingDataStrategy> GetByCategory(StreamingCategory category) =>
        _strategies.Values.Where(s => s.Category == category);
}
```

### Anti-Patterns to Avoid
- **Don't create plugins without SDK contracts** - SDK contracts must exist first (already done for Phase 7)
- **Don't hard-code strategy lists** - Use reflection-based auto-discovery
- **Don't skip Intelligence integration** - All strategies should expose KnowledgeObject and RegisteredCapability
- **Don't reference other plugins directly** - Use message bus for cross-plugin communication

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Video transcoding | Custom FFmpeg process management | FFMpegCore or Xabe.FFmpeg | Handles process lifecycle, progress reporting, error handling, format detection |
| Image processing | Raw pixel manipulation | SixLabors.ImageSharp | Hardware-accelerated, memory-efficient, format-aware |
| Parquet parsing | Binary Parquet reader | Apache.Parquet.Net | Handles column compression, encoding schemes, schema evolution |
| Kafka client | Raw TCP socket protocol | Confluent.Kafka | Manages partitions, offsets, consumer groups, rebalancing |
| JSON parsing | String manipulation | System.Text.Json or Newtonsoft.Json | Handles escaping, UTF-8, streaming, schema validation |
| HDF5 reading | Binary chunk parsing | HDF.PInvoke | Manages datasets, groups, attributes, compression filters |
| Stream windowing | Manual timestamp tracking | Built into streaming engine libraries | Handles watermarks, late data, trigger strategies |

**Key insight:** Format parsing, streaming engines, and media codecs are extremely complex with subtle edge cases. Standard libraries have years of production hardening and optimization. Building custom implementations is a maintenance nightmare.

## Common Pitfalls

### Pitfall 1: Missing Plugin Directory Structure
**What goes wrong:** Roadmap references UltimateDataFormat, UltimateStreaming, UltimateMedia but only UltimateStreamingData and Transcoding.Media exist
**Why it happens:** Naming inconsistency between roadmap planning and actual implementation
**How to avoid:**
- Verify plugin names match roadmap exactly or update roadmap to reflect actual names
- UltimateStreamingData should likely be renamed to UltimateStreaming
- Transcoding.Media should likely be renamed to UltimateMedia
- UltimateDataFormat needs to be created from scratch
**Warning signs:** Grep for "UltimateDataFormat" returns no plugin directory

### Pitfall 2: Assuming Streaming = IStreamingStrategy
**What goes wrong:** SDK has both IStreamingStrategy (SDK/Contracts/Streaming) and IStreamingDataStrategy (plugin-specific)
**Why it happens:** Two different concepts: message streaming (pub/sub) vs data stream processing (windowing, state)
**How to avoid:**
- IStreamingStrategy = message broker integration (Kafka, RabbitMQ publish/subscribe)
- IStreamingDataStrategy = stream processing engines (Flink, Spark Streaming operations)
- UltimateStreamingData plugin uses IStreamingDataStrategy, NOT IStreamingStrategy
- If implementing T113 (message queue/IoT strategies), use SDK's IStreamingStrategy
**Warning signs:** Mixing pub/sub patterns with windowing/state management patterns

### Pitfall 3: Media Strategy Without FFmpeg
**What goes wrong:** IMediaStrategy requires transcoding, streaming, thumbnail generation but no FFmpeg integration
**Why it happens:** Assuming .NET can natively handle all video codecs
**How to avoid:**
- IMediaStrategy.TranscodeAsync requires external tools (FFmpeg is industry standard)
- FFMpegCore provides .NET wrapper with process management
- Must handle FFmpeg installation, path resolution, version compatibility
- Hardware acceleration (GPU encoding) requires FFmpeg with GPU support
**Warning signs:** Media transcoding throws "codec not found" errors

### Pitfall 4: Format Conversion Memory Explosion
**What goes wrong:** Loading entire file into memory for parse → serialize conversion
**Why it happens:** Default ConvertToAsync in DataFormatStrategyBase uses parse → serialize pattern
**How to avoid:**
- Override ConvertToAsync for streaming formats (Parquet, Avro)
- Use chunked processing for large files
- Don't materialize entire dataset in memory
- Stream directly from input format to output format where possible
**Warning signs:** OutOfMemoryException on large Parquet/HDF5 conversions

### Pitfall 5: Schema Detection Without Sampling
**What goes wrong:** ExtractSchemaAsync reads entire file to infer schema
**Why it happens:** Assuming all records have identical structure
**How to avoid:**
- Sample first N records (e.g., 1000) for schema inference
- Handle schema evolution (fields added/removed mid-file)
- Use explicit schema when available (Parquet, Avro have embedded schemas)
- CSV/JSON require heuristic type detection from samples
**Warning signs:** Slow schema extraction on multi-GB files

### Pitfall 6: Stream Position Not Reset
**What goes wrong:** DetectFormatAsync moves stream position, breaks subsequent reads
**Why it happens:** Forgetting to reset stream after format detection
**How to avoid:**
- Always use try/finally to restore stream.Position
- SDK base class DataFormatStrategyBase.DetectFormatAsync does this correctly
- Don't skip base class implementation
**Warning signs:** "Stream is not readable" exceptions after format detection

## Code Examples

Verified patterns from SDK and existing plugins:

### Data Format Strategy Implementation
```csharp
// Source: SDK/Contracts/DataFormat/DataFormatStrategy.cs (pattern to follow)
public class JsonStrategy : DataFormatStrategyBase
{
    public override string StrategyId => "json";
    public override string DisplayName => "JSON";

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
        MimeTypes = new[] { "application/json" },
        DomainFamily = DomainFamily.General,
        Description = "JavaScript Object Notation",
        SpecificationUrl = "https://www.json.org/"
    };

    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[1];
        var read = await stream.ReadAsync(buffer, ct);
        return read > 0 && (buffer[0] == '{' || buffer[0] == '[');
    }

    public override async Task<DataFormatResult> ParseAsync(
        Stream input,
        DataFormatContext context,
        CancellationToken ct)
    {
        using var reader = new StreamReader(input, leaveOpen: true);
        var json = await reader.ReadToEndAsync(ct);
        var data = System.Text.Json.JsonSerializer.Deserialize<object>(json);

        return DataFormatResult.Ok(
            data: data,
            bytesProcessed: input.Length
        );
    }

    public override async Task<DataFormatResult> SerializeAsync(
        object data,
        Stream output,
        DataFormatContext context,
        CancellationToken ct)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(data);
        await using var writer = new StreamWriter(output, leaveOpen: true);
        await writer.WriteAsync(json);

        return DataFormatResult.Ok(bytesProcessed: output.Length);
    }

    protected override Task<FormatValidationResult> ValidateCoreAsync(
        Stream stream,
        FormatSchema? schema,
        CancellationToken ct)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(stream);
            return Task.FromResult(FormatValidationResult.Valid);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return Task.FromResult(FormatValidationResult.Invalid(
                new ValidationError { Message = ex.Message }
            ));
        }
    }
}
```

### Streaming Strategy with Capabilities
```csharp
// Source: SDK/Contracts/Streaming/StreamingStrategy.cs
public class KafkaStreamingStrategy : StreamingStrategyBase
{
    public override string StrategyId => "kafka";
    public override string Name => "Apache Kafka";

    public override StreamingCapabilities Capabilities => new()
    {
        SupportsOrdering = true,
        SupportsPartitioning = true,
        SupportsExactlyOnce = true,
        SupportsTransactions = true,
        SupportsReplay = true,
        SupportsPersistence = true,
        SupportsConsumerGroups = true,
        SupportsDeadLetterQueue = true,
        SupportsAcknowledgment = true,
        SupportsHeaders = true,
        SupportsCompression = true,
        SupportsMessageFiltering = false,
        MaxMessageSize = 1_048_576, // 1MB default
        MaxRetention = TimeSpan.FromDays(7),
        DefaultDeliveryGuarantee = DeliveryGuarantee.AtLeastOnce,
        SupportedDeliveryGuarantees = new[]
        {
            DeliveryGuarantee.AtMostOnce,
            DeliveryGuarantee.AtLeastOnce,
            DeliveryGuarantee.ExactlyOnce
        }
    };

    public override IReadOnlyList<string> SupportedProtocols =>
        new[] { "kafka", "kafka-ssl" };

    public override async Task<PublishResult> PublishAsync(
        string streamName,
        StreamMessage message,
        CancellationToken ct = default)
    {
        ValidateStreamName(streamName);
        ValidateMessage(message);

        // Kafka client integration here
        // Example: await _producer.ProduceAsync(streamName, message);

        return new PublishResult
        {
            MessageId = Guid.NewGuid().ToString(),
            Success = true,
            Timestamp = DateTime.UtcNow
        };
    }

    // Implement other IStreamingStrategy methods...
}
```

### Media Strategy with Hardware Acceleration
```csharp
// Source: SDK/Contracts/Media pattern
public class FFmpegMediaStrategy : MediaStrategyBase
{
    public override string StrategyId => "ffmpeg";
    public override string Name => "FFmpeg Transcoder";

    public FFmpegMediaStrategy() : base(CreateCapabilities())
    {
    }

    private static MediaCapabilities CreateCapabilities() => new(
        SupportedInputFormats: new HashSet<MediaFormat>
        {
            MediaFormat.MP4, MediaFormat.WebM, MediaFormat.MKV,
            MediaFormat.AVI, MediaFormat.MOV, MediaFormat.FLV
        },
        SupportedOutputFormats: new HashSet<MediaFormat>
        {
            MediaFormat.MP4, MediaFormat.WebM,
            MediaFormat.HLS, MediaFormat.DASH
        },
        SupportsStreaming: true,
        SupportsAdaptiveBitrate: true,
        MaxResolution: Resolution.EightK,
        MaxBitrate: null, // No limit
        SupportedCodecs: new HashSet<string>
        {
            "h264", "h265", "vp9", "av1",
            "aac", "opus", "mp3"
        },
        SupportsThumbnailGeneration: true,
        SupportsMetadataExtraction: true,
        SupportsHardwareAcceleration: true
    );

    protected override async Task<Stream> TranscodeAsyncCore(
        Stream inputStream,
        TranscodeOptions options,
        CancellationToken cancellationToken)
    {
        // FFmpeg integration here
        // Example using FFMpegCore:
        // var outputStream = new MemoryStream();
        // await FFMpegArguments
        //     .FromPipeInput(new StreamPipeSource(inputStream))
        //     .OutputToPipe(new StreamPipeSink(outputStream), options => options
        //         .WithVideoCodec(options.VideoCodec)
        //         .WithAudioCodec(options.AudioCodec))
        //     .ProcessAsynchronously();
        // return outputStream;

        throw new NotImplementedException();
    }

    // Implement other MediaStrategyBase abstract methods...
}
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Separate format plugins | Unified UltimateDataFormat with 230+ strategies | Phase 7 (current) | Centralized format handling, consistent API |
| Message queue per vendor | IStreamingStrategy with protocol strategies | Phase 1 (SDK) | Protocol-agnostic streaming |
| FFmpeg CLI process spawning | FFMpegCore library wrapper | Industry standard | Safer process management, progress tracking |
| Manual Parquet parsing | Apache.Parquet.Net | Current ecosystem | Column pruning, predicate pushdown |
| JSON.NET only | System.Text.Json primary | .NET 5+ | Better performance, source generation |

**Deprecated/outdated:**
- **Separate streaming plugins**: Old approach had one plugin per message broker; UltimateStreaming consolidates all protocols
- **Individual format plugins**: Old approach had DataWarehouse.Plugins.ContentProcessing and separate parsers; UltimateDataFormat replaces all
- **CLI-based transcoding**: Direct FFmpeg CLI calls are brittle; use library wrappers

## Open Questions

1. **Plugin Naming Mismatch**
   - What we know: Roadmap says UltimateDataFormat, UltimateStreaming, UltimateMedia
   - What's unclear: Actual plugins are named UltimateStreamingData, Transcoding.Media, and UltimateDataFormat doesn't exist
   - Recommendation: Clarify if plugins should be renamed to match roadmap or roadmap updated to match plugins

2. **Streaming vs StreamingData Scope**
   - What we know: SDK has IStreamingStrategy (pub/sub) and UltimateStreamingData uses IStreamingDataStrategy (processing)
   - What's unclear: Which interface should T113 (streaming protocols) use? Roadmap mentions "message queues, IoT protocols"
   - Recommendation: T113 likely needs BOTH - use IStreamingStrategy for pub/sub, IStreamingDataStrategy for processing pipelines

3. **Media Transcoding FFmpeg Dependency**
   - What we know: IMediaStrategy requires transcoding capability
   - What's unclear: How to handle FFmpeg installation/distribution (system dependency vs bundled binary)
   - Recommendation: Use FFMpegCore with auto-download capability or document FFmpeg as system prerequisite

4. **Format Strategy Count**
   - What we know: Roadmap says 230+ formats for T110
   - What's unclear: Which formats are highest priority vs nice-to-have
   - Recommendation: Start with common formats per domain (JSON/CSV/Parquet for general, HDF5/NetCDF for scientific, etc.) and expand based on usage

5. **Existing Strategy Completeness**
   - What we know: UltimateStreamingData has 8 strategy category files
   - What's unclear: How many actual strategy implementations exist in each file vs stubbed placeholders
   - Recommendation: Audit each strategy file to count complete implementations before creating plans

## Sources

### Primary (HIGH confidence)
- SDK Contracts (DataFormat/DataFormatStrategy.cs) - Complete interface with base class implementation
- SDK Contracts (Streaming/StreamingStrategy.cs) - Complete streaming interface with capabilities model
- SDK Contracts (Media/IMediaStrategy.cs, MediaStrategyBase.cs) - Complete media strategy contracts
- UltimateStreamingData plugin - 8 strategy category files, complete orchestrator with auto-discovery
- Transcoding.Media plugin - Orchestrator present, strategy count unknown (file too large to read)
- .planning/ROADMAP.md - Phase 7 requirements and success criteria

### Secondary (MEDIUM confidence)
- Metadata/TODO.md - Task IDs T110, T113, T118 mentioned but details not in Incomplete Tasks.txt
- Industry standard libraries - FFMpegCore, Apache.Parquet.Net, SixLabors.ImageSharp (documented external dependencies)

### Tertiary (LOW confidence - NEEDS VERIFICATION)
- Incomplete Tasks.txt - Searched for T110/T113/T118 but no matches found (tasks may be completed or numbered differently)
- Plugin count - 230+ formats, 75+ streaming protocols, 80+ media formats (from TODO.md grep)

## Metadata

**Confidence breakdown:**
- Standard stack: MEDIUM - Libraries identified from industry standards but not yet referenced in csproj files
- Architecture: HIGH - Existing plugins demonstrate clear patterns (auto-discovery, SDK base classes, registry pattern)
- Pitfalls: HIGH - Identified from SDK contract design and existing plugin structure

**Research date:** 2026-02-11
**Valid until:** 2026-03-13 (30 days - stable domain, SDK contracts unlikely to change)

**Critical Gap:** UltimateDataFormat plugin does not exist and must be created from scratch. This is a major finding that impacts plan creation for sub-tasks 07-01, 07-02, 07-03.

**Recommended First Steps:**
1. Create UltimateDataFormat plugin skeleton with orchestrator
2. Audit UltimateStreamingData strategy files to count actual implementations
3. Audit Transcoding.Media plugin to verify completeness
4. Determine if plugin renaming is needed (UltimateStreamingData → UltimateStreaming, Transcoding.Media → UltimateMedia)
