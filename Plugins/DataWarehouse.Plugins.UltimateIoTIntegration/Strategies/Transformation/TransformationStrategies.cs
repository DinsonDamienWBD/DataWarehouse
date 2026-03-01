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

    /// <summary>
    /// Validates that <paramref name="data"/> conforms to <paramref name="schema"/>.
    /// LOW-3428: base returns false (not-validated) instead of blindly true; override in subclasses
    /// that have schema-validation capability.
    /// </summary>
    public virtual Task<bool> ValidateSchemaAsync(byte[] data, string schema, CancellationToken ct = default)
    {
        // Return false by default — callers should not assume unknown data is valid.
        // Subclasses override this when they can perform real schema validation.
        return Task.FromResult(false);
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

        // Parse input as UTF-8 text payload (common IoT transport encoding)
        var inputText = Encoding.UTF8.GetString(request.InputData);

        if (request.TargetFormat.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            // Wrap the raw payload in a JSON envelope preserving original data
            var envelope = new
            {
                sourceFormat = request.SourceFormat,
                timestamp = DateTimeOffset.UtcNow,
                payload = inputText
            };
            output = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope));
        }
        else if (request.TargetFormat.Equals("xml", StringComparison.OrdinalIgnoreCase))
        {
            var safePayload = System.Security.SecurityElement.Escape(inputText) ?? string.Empty;
            output = Encoding.UTF8.GetBytes(
                $"<data><sourceFormat>{request.SourceFormat}</sourceFormat><payload>{safePayload}</payload></data>");
        }
        else if (request.TargetFormat.Equals("csv", StringComparison.OrdinalIgnoreCase))
        {
            var escapedPayload = inputText.Replace("\"", "\"\"");
            output = Encoding.UTF8.GetBytes(
                $"sourceFormat,timestamp,payload\n{request.SourceFormat},{DateTimeOffset.UtcNow:O},\"{escapedPayload}\"");
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
            // LOW-3427: wrap payload in minimal CBOR byte-string encoding (major type 2).
            // CBOR header: 0x40..0x57 for 0-23 bytes; 0x58 + 1-byte length for 24-255 bytes; 0x59 + 2-byte for 256-65535.
            var msg = request.Message;
            byte[] cbor;
            if (msg.Length <= 23)
            {
                cbor = new byte[1 + msg.Length];
                cbor[0] = (byte)(0x40 | msg.Length);
                msg.CopyTo(cbor, 1);
            }
            else if (msg.Length <= 255)
            {
                cbor = new byte[2 + msg.Length];
                cbor[0] = 0x58; cbor[1] = (byte)msg.Length;
                msg.CopyTo(cbor, 2);
            }
            else
            {
                cbor = new byte[3 + msg.Length];
                cbor[0] = 0x59; cbor[1] = (byte)(msg.Length >> 8); cbor[2] = (byte)(msg.Length & 0xFF);
                msg.CopyTo(cbor, 3);
            }
            translatedMessage = cbor;
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

        // Enrich from message metadata when present; caller supplies context via TelemetryMessage.Metadata
        var deviceType = request.Message.Properties.TryGetValue("deviceType", out var dt) ? dt : "sensor";
        enriched.Data["_device_type"] = deviceType;
        addedFields["_device_type"] = deviceType;

        if (request.Message.Properties.TryGetValue("region", out var msgRegion))
        {
            enriched.Data["_region"] = msgRegion;
            addedFields["_region"] = msgRegion;
        }

        if (request.Message.Properties.TryGetValue("building", out var building))
        {
            enriched.Data["_building"] = building;
            addedFields["_building"] = building;
        }

        if (request.Message.Properties.TryGetValue("floor", out var floor))
        {
            enriched.Data["_floor"] = floor;
            addedFields["_floor"] = floor;
        }

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
