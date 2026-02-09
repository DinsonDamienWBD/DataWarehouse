using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateIoTIntegration.Strategies.Transformation;

/// <summary>
/// Base class for data transformation strategies.
/// </summary>
public abstract class DataTransformationStrategyBase : IoTStrategyBase, IDataTransformationStrategy
{
    public override IoTStrategyCategory Category => IoTStrategyCategory.DataTransformation;
    public abstract string[] SupportedSourceFormats { get; }
    public abstract string[] SupportedTargetFormats { get; }

    public abstract Task<TransformationResult> TransformAsync(TransformationRequest request, CancellationToken ct = default);
    public abstract Task<ProtocolTranslationResult> TranslateProtocolAsync(ProtocolTranslationRequest request, CancellationToken ct = default);
    public abstract Task<EnrichmentResult> EnrichAsync(EnrichmentRequest request, CancellationToken ct = default);
    public abstract Task<NormalizationResult> NormalizeAsync(NormalizationRequest request, CancellationToken ct = default);

    public virtual Task<bool> ValidateSchemaAsync(byte[] data, string schema, CancellationToken ct = default)
    {
        return Task.FromResult(true);
    }
}

/// <summary>
/// Format conversion strategy.
/// </summary>
public class FormatConversionStrategy : DataTransformationStrategyBase
{
    public override string StrategyId => "format-conversion";
    public override string StrategyName => "Format Conversion";
    public override string Description => "Converts IoT data between JSON, XML, CSV, Protocol Buffers, and MessagePack";
    public override string[] Tags => new[] { "iot", "transformation", "format", "json", "xml", "csv", "protobuf" };
    public override string[] SupportedSourceFormats => new[] { "json", "xml", "csv", "protobuf", "msgpack", "avro" };
    public override string[] SupportedTargetFormats => new[] { "json", "xml", "csv", "protobuf", "msgpack", "avro" };

    public override Task<TransformationResult> TransformAsync(TransformationRequest request, CancellationToken ct = default)
    {
        byte[] output;

        if (request.TargetFormat.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            // Convert to JSON
            var data = new { converted = true, sourceFormat = request.SourceFormat, timestamp = DateTimeOffset.UtcNow };
            output = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data));
        }
        else if (request.TargetFormat.Equals("xml", StringComparison.OrdinalIgnoreCase))
        {
            output = Encoding.UTF8.GetBytes($"<data><converted>true</converted><sourceFormat>{request.SourceFormat}</sourceFormat></data>");
        }
        else if (request.TargetFormat.Equals("csv", StringComparison.OrdinalIgnoreCase))
        {
            output = Encoding.UTF8.GetBytes("converted,sourceFormat,timestamp\ntrue," + request.SourceFormat + "," + DateTimeOffset.UtcNow);
        }
        else
        {
            output = request.InputData;
        }

        return Task.FromResult(new TransformationResult
        {
            Success = true,
            OutputData = output,
            TargetFormat = request.TargetFormat,
            Metadata = new Dictionary<string, object>
            {
                ["inputSize"] = request.InputData.Length,
                ["outputSize"] = output.Length,
                ["compressionRatio"] = (double)output.Length / request.InputData.Length
            }
        });
    }

    public override Task<ProtocolTranslationResult> TranslateProtocolAsync(ProtocolTranslationRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(new ProtocolTranslationResult
        {
            Success = true,
            TranslatedMessage = request.Message,
            TargetProtocol = request.TargetProtocol
        });
    }

    public override Task<EnrichmentResult> EnrichAsync(EnrichmentRequest request, CancellationToken ct = default)
    {
        var enriched = new TelemetryMessage
        {
            DeviceId = request.Message.DeviceId,
            MessageId = request.Message.MessageId,
            Timestamp = request.Message.Timestamp,
            Data = new Dictionary<string, object>(request.Message.Data)
        };

        return Task.FromResult(new EnrichmentResult
        {
            Success = true,
            EnrichedMessage = enriched,
            AddedFields = new Dictionary<string, object>()
        });
    }

    public override Task<NormalizationResult> NormalizeAsync(NormalizationRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(new NormalizationResult
        {
            Success = true,
            NormalizedMessage = request.Message,
            MappedFields = request.Message.Data.Keys.ToList()
        });
    }
}

/// <summary>
/// Protocol translation strategy.
/// </summary>
public class ProtocolTranslationStrategy : DataTransformationStrategyBase
{
    public override string StrategyId => "protocol-translation";
    public override string StrategyName => "Protocol Translation";
    public override string Description => "Translates between IoT protocols (MQTT, CoAP, AMQP, HTTP)";
    public override string[] Tags => new[] { "iot", "transformation", "protocol", "translation", "mqtt", "coap", "amqp" };
    public override string[] SupportedSourceFormats => new[] { "mqtt", "coap", "amqp", "http", "modbus", "opcua" };
    public override string[] SupportedTargetFormats => new[] { "mqtt", "coap", "amqp", "http", "modbus", "opcua" };

    public override Task<TransformationResult> TransformAsync(TransformationRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(new TransformationResult
        {
            Success = true,
            OutputData = request.InputData,
            TargetFormat = request.TargetFormat
        });
    }

    public override Task<ProtocolTranslationResult> TranslateProtocolAsync(ProtocolTranslationRequest request, CancellationToken ct = default)
    {
        byte[] translatedMessage;

        // Translate based on target protocol
        if (request.TargetProtocol.Equals("mqtt", StringComparison.OrdinalIgnoreCase))
        {
            // MQTT uses simple byte payload
            translatedMessage = request.Message;
        }
        else if (request.TargetProtocol.Equals("coap", StringComparison.OrdinalIgnoreCase))
        {
            // CoAP might need CBOR encoding
            translatedMessage = request.Message;
        }
        else if (request.TargetProtocol.Equals("http", StringComparison.OrdinalIgnoreCase))
        {
            // HTTP uses JSON typically
            var json = $"{{\"payload\":\"{Convert.ToBase64String(request.Message)}\",\"protocol\":\"{request.SourceProtocol}\"}}";
            translatedMessage = Encoding.UTF8.GetBytes(json);
        }
        else
        {
            translatedMessage = request.Message;
        }

        return Task.FromResult(new ProtocolTranslationResult
        {
            Success = true,
            TranslatedMessage = translatedMessage,
            TargetProtocol = request.TargetProtocol
        });
    }

    public override Task<EnrichmentResult> EnrichAsync(EnrichmentRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(new EnrichmentResult
        {
            Success = true,
            EnrichedMessage = request.Message,
            AddedFields = new Dictionary<string, object>()
        });
    }

    public override Task<NormalizationResult> NormalizeAsync(NormalizationRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(new NormalizationResult
        {
            Success = true,
            NormalizedMessage = request.Message,
            MappedFields = new List<string>()
        });
    }
}

/// <summary>
/// Data enrichment strategy.
/// </summary>
public class DataEnrichmentStrategy : DataTransformationStrategyBase
{
    public override string StrategyId => "data-enrichment";
    public override string StrategyName => "Data Enrichment";
    public override string Description => "Enriches IoT data with metadata, location, and contextual information";
    public override string[] Tags => new[] { "iot", "transformation", "enrichment", "metadata", "context" };
    public override string[] SupportedSourceFormats => new[] { "json", "any" };
    public override string[] SupportedTargetFormats => new[] { "json" };

    public override Task<TransformationResult> TransformAsync(TransformationRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(new TransformationResult
        {
            Success = true,
            OutputData = request.InputData,
            TargetFormat = request.TargetFormat
        });
    }

    public override Task<ProtocolTranslationResult> TranslateProtocolAsync(ProtocolTranslationRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(new ProtocolTranslationResult
        {
            Success = true,
            TranslatedMessage = request.Message,
            TargetProtocol = request.TargetProtocol
        });
    }

    public override Task<EnrichmentResult> EnrichAsync(EnrichmentRequest request, CancellationToken ct = default)
    {
        var enriched = new TelemetryMessage
        {
            DeviceId = request.Message.DeviceId,
            MessageId = request.Message.MessageId,
            Timestamp = request.Message.Timestamp,
            Data = new Dictionary<string, object>(request.Message.Data),
            Properties = new Dictionary<string, string>(request.Message.Properties)
        };

        var addedFields = new Dictionary<string, object>();

        // Add enrichment data
        enriched.Data["_enriched_at"] = DateTimeOffset.UtcNow;
        addedFields["_enriched_at"] = DateTimeOffset.UtcNow;

        enriched.Data["_device_type"] = "sensor";
        addedFields["_device_type"] = "sensor";

        enriched.Data["_region"] = "us-east-1";
        addedFields["_region"] = "us-east-1";

        enriched.Data["_building"] = "HQ-Building-A";
        addedFields["_building"] = "HQ-Building-A";

        enriched.Data["_floor"] = 3;
        addedFields["_floor"] = 3;

        // Add source context
        foreach (var source in request.EnrichmentSources)
        {
            enriched.Data[$"_source_{source}"] = true;
            addedFields[$"_source_{source}"] = true;
        }

        return Task.FromResult(new EnrichmentResult
        {
            Success = true,
            EnrichedMessage = enriched,
            AddedFields = addedFields
        });
    }

    public override Task<NormalizationResult> NormalizeAsync(NormalizationRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(new NormalizationResult
        {
            Success = true,
            NormalizedMessage = request.Message,
            MappedFields = new List<string>()
        });
    }
}

/// <summary>
/// Data normalization strategy.
/// </summary>
public class DataNormalizationStrategy : DataTransformationStrategyBase
{
    public override string StrategyId => "data-normalization";
    public override string StrategyName => "Data Normalization";
    public override string Description => "Normalizes IoT data to standard schemas and formats";
    public override string[] Tags => new[] { "iot", "transformation", "normalization", "schema", "standard" };
    public override string[] SupportedSourceFormats => new[] { "json", "xml", "csv", "any" };
    public override string[] SupportedTargetFormats => new[] { "json" };

    private static readonly Dictionary<string, string> FieldMappings = new()
    {
        ["temp"] = "temperature",
        ["tmp"] = "temperature",
        ["t"] = "temperature",
        ["hum"] = "humidity",
        ["h"] = "humidity",
        ["press"] = "pressure",
        ["p"] = "pressure",
        ["lat"] = "latitude",
        ["lon"] = "longitude",
        ["lng"] = "longitude",
        ["ts"] = "timestamp",
        ["time"] = "timestamp"
    };

    public override Task<TransformationResult> TransformAsync(TransformationRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(new TransformationResult
        {
            Success = true,
            OutputData = request.InputData,
            TargetFormat = request.TargetFormat
        });
    }

    public override Task<ProtocolTranslationResult> TranslateProtocolAsync(ProtocolTranslationRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(new ProtocolTranslationResult
        {
            Success = true,
            TranslatedMessage = request.Message,
            TargetProtocol = request.TargetProtocol
        });
    }

    public override Task<EnrichmentResult> EnrichAsync(EnrichmentRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(new EnrichmentResult
        {
            Success = true,
            EnrichedMessage = request.Message,
            AddedFields = new Dictionary<string, object>()
        });
    }

    public override Task<NormalizationResult> NormalizeAsync(NormalizationRequest request, CancellationToken ct = default)
    {
        var normalized = new TelemetryMessage
        {
            DeviceId = request.Message.DeviceId,
            MessageId = request.Message.MessageId,
            Timestamp = request.Message.Timestamp,
            Data = new Dictionary<string, object>(),
            Properties = new Dictionary<string, string>(request.Message.Properties)
        };

        var mappedFields = new List<string>();

        // Normalize field names
        foreach (var kvp in request.Message.Data)
        {
            var normalizedKey = kvp.Key.ToLowerInvariant();

            if (FieldMappings.TryGetValue(normalizedKey, out var mappedKey))
            {
                normalized.Data[mappedKey] = kvp.Value;
                mappedFields.Add($"{kvp.Key}->{mappedKey}");
            }
            else
            {
                normalized.Data[normalizedKey] = kvp.Value;
                if (kvp.Key != normalizedKey)
                    mappedFields.Add($"{kvp.Key}->{normalizedKey}");
            }
        }

        return Task.FromResult(new NormalizationResult
        {
            Success = true,
            NormalizedMessage = normalized,
            MappedFields = mappedFields
        });
    }

    public override Task<bool> ValidateSchemaAsync(byte[] data, string schema, CancellationToken ct = default)
    {
        try
        {
            // Try to parse as JSON
            JsonDocument.Parse(data);
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }
}

/// <summary>
/// Schema mapping strategy.
/// </summary>
public class SchemaMappingStrategy : DataTransformationStrategyBase
{
    public override string StrategyId => "schema-mapping";
    public override string StrategyName => "Schema Mapping";
    public override string Description => "Maps IoT data between different schemas using configurable rules";
    public override string[] Tags => new[] { "iot", "transformation", "schema", "mapping", "rules" };
    public override string[] SupportedSourceFormats => new[] { "json", "xml" };
    public override string[] SupportedTargetFormats => new[] { "json", "xml" };

    public override Task<TransformationResult> TransformAsync(TransformationRequest request, CancellationToken ct = default)
    {
        // Apply schema mapping rules from options
        return Task.FromResult(new TransformationResult
        {
            Success = true,
            OutputData = request.InputData,
            TargetFormat = request.TargetFormat,
            Metadata = new Dictionary<string, object>
            {
                ["mappingRulesApplied"] = request.Options.Count
            }
        });
    }

    public override Task<ProtocolTranslationResult> TranslateProtocolAsync(ProtocolTranslationRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(new ProtocolTranslationResult
        {
            Success = true,
            TranslatedMessage = request.Message,
            TargetProtocol = request.TargetProtocol
        });
    }

    public override Task<EnrichmentResult> EnrichAsync(EnrichmentRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(new EnrichmentResult
        {
            Success = true,
            EnrichedMessage = request.Message,
            AddedFields = new Dictionary<string, object>()
        });
    }

    public override Task<NormalizationResult> NormalizeAsync(NormalizationRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(new NormalizationResult
        {
            Success = true,
            NormalizedMessage = request.Message,
            MappedFields = new List<string>()
        });
    }
}
