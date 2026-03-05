using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.SemanticSync;

namespace DataWarehouse.Plugins.SemanticSync.Strategies.Routing;

/// <summary>
/// Progressively strips non-essential fields from data as fidelity decreases.
/// Each fidelity transition removes specific categories of content:
/// <list type="bullet">
/// <item><description>Full to Detailed: Strips audit trails, access logs, version history (~80% of original).</description></item>
/// <item><description>Detailed to Standard: Strips previews, thumbnails, redundant indexes (~50% of original).</description></item>
/// <item><description>Standard to Summarized: Delegates to <see cref="SummaryGenerator"/> (~15% of original).</description></item>
/// <item><description>Summarized to Metadata: Keeps only top-level keys, types, and sizes (~2% of original).</description></item>
/// </list>
/// </summary>
/// <remarks>
/// For JSON data, fields are removed by parsing, stripping, and re-serializing.
/// For binary data, progressive truncation preserves headers while removing trailing content.
/// Malformed JSON is returned unmodified to avoid data loss.
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Summary routing")]
internal sealed class FidelityDownsampler
{
    /// <summary>
    /// Target size ratios for each fidelity level relative to the original data.
    /// </summary>
    internal static readonly IReadOnlyDictionary<SyncFidelity, double> TargetRatios =
        new Dictionary<SyncFidelity, double>
        {
            [SyncFidelity.Full] = 1.00,
            [SyncFidelity.Detailed] = 0.80,
            [SyncFidelity.Standard] = 0.50,
            [SyncFidelity.Summarized] = 0.15,
            [SyncFidelity.Metadata] = 0.02
        };

    // JSON property names typically removed at each transition
    private static readonly HashSet<string> AuditFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "audit", "auditTrail", "audit_trail", "auditLog", "audit_log",
        "history", "versionHistory", "version_history", "versions",
        "accessLog", "access_log", "accessLogs", "access_logs",
        "changelog", "changeLog", "change_log",
        "revisions", "modifications", "modificationHistory"
    };

    private static readonly HashSet<string> PreviewFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "preview", "previews", "thumbnail", "thumbnails",
        "thumb", "thumbs", "embedded_preview", "embeddedPreview",
        "icon", "icons", "snapshot", "snapshots",
        "index", "indexes", "indices", "redundantIndex", "redundant_index",
        "cache", "cached", "cachedData", "cached_data",
        "temp", "temporary", "tempData"
    };

    /// <summary>
    /// Downsamples data from one fidelity level to a lower one by progressively stripping fields.
    /// </summary>
    /// <param name="data">The raw data to downsample.</param>
    /// <param name="from">The current fidelity level of the data.</param>
    /// <param name="to">The target fidelity level (must be lower than <paramref name="from"/>).</param>
    /// <param name="metadata">Optional metadata dictionary providing hints about the data format.</param>
    /// <returns>The downsampled data at the target fidelity level.</returns>
    public ReadOnlyMemory<byte> Downsample(
        ReadOnlyMemory<byte> data,
        SyncFidelity from,
        SyncFidelity to,
        IDictionary<string, string>? metadata = null)
    {
        if (data.IsEmpty) return data;
        if (to >= from) return data; // No reduction needed

        var current = data;
        var currentFidelity = from;

        // Apply each transition step sequentially
        while (currentFidelity < to)
        {
            // Move one step at a time: Full(0)->Detailed(1)->Standard(2)->Summarized(3)->Metadata(4)
            // Since enum values increase as fidelity decreases, increment
            var nextFidelity = currentFidelity + 1;
            current = ApplyTransition(current, currentFidelity, nextFidelity, metadata);
            currentFidelity = nextFidelity;
        }

        return current;
    }

    private ReadOnlyMemory<byte> ApplyTransition(
        ReadOnlyMemory<byte> data,
        SyncFidelity from,
        SyncFidelity to,
        IDictionary<string, string>? metadata)
    {
        // Cat 1 (finding 1021): use metadata content-type hint to skip TryParseJson on known binary data,
        // making the previously-dead metadata parameter meaningful.
        var contentType = metadata != null && metadata.TryGetValue("content-type", out var ct) ? ct : null;
        bool knownBinary = contentType != null && !contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
                                               && !contentType.Contains("text", StringComparison.OrdinalIgnoreCase);
        JsonDocument? doc = null;
        bool isJson = !knownBinary && TryParseJson(data.Span, out doc);

        try
        {
            return (from, to) switch
            {
                (SyncFidelity.Full, SyncFidelity.Detailed) =>
                    isJson ? StripJsonFields(doc!, AuditFields) : StripBinaryTrailing(data, 0.80),

                (SyncFidelity.Detailed, SyncFidelity.Standard) =>
                    isJson ? StripJsonFields(doc!, PreviewFields) : StripBinaryTrailing(data, 0.625),
                    // 0.625 because: Standard = 50% of original, Detailed = 80%, so 50/80 = 0.625

                (SyncFidelity.Standard, SyncFidelity.Summarized) =>
                    isJson ? ExtractSummarizedJson(doc!) : StripBinaryTrailing(data, 0.30),
                    // 0.30 because: Summarized = 15% of original, Standard = 50%, so 15/50 = 0.30

                (SyncFidelity.Summarized, SyncFidelity.Metadata) =>
                    isJson ? ExtractMetadataJson(doc!) : ExtractBinaryMetadata(data),

                _ => data
            };
        }
        finally
        {
            if (isJson) doc?.Dispose();
        }
    }

    private static ReadOnlyMemory<byte> StripJsonFields(JsonDocument doc, HashSet<string> fieldsToStrip)
    {
        try
        {
            using var stream = new System.IO.MemoryStream();
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });

            WriteElementWithout(writer, doc.RootElement, fieldsToStrip, depth: 0, maxDepth: 16);
            writer.Flush();

            return stream.ToArray();
        }
        catch
        {
            // If stripping fails, return original data unmodified
            return JsonSerializer.SerializeToUtf8Bytes(doc.RootElement);
        }
    }

    private static void WriteElementWithout(
        Utf8JsonWriter writer,
        JsonElement element,
        HashSet<string> fieldsToStrip,
        int depth,
        int maxDepth)
    {
        if (depth > maxDepth)
        {
            element.WriteTo(writer);
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject())
                {
                    if (fieldsToStrip.Contains(prop.Name))
                        continue;

                    writer.WritePropertyName(prop.Name);
                    WriteElementWithout(writer, prop.Value, fieldsToStrip, depth + 1, maxDepth);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElementWithout(writer, item, fieldsToStrip, depth + 1, maxDepth);
                }
                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static ReadOnlyMemory<byte> ExtractSummarizedJson(JsonDocument doc)
    {
        // Keep only top-level scalar fields and first-level object/array summaries
        try
        {
            using var stream = new System.IO.MemoryStream();
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });

            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    writer.WritePropertyName(prop.Name);
                    switch (prop.Value.ValueKind)
                    {
                        case JsonValueKind.String:
                            string strVal = prop.Value.GetString() ?? "";
                            // Truncate long strings
                            writer.WriteStringValue(strVal.Length > 200 ? strVal[..200] + "..." : strVal);
                            break;
                        case JsonValueKind.Number:
                        case JsonValueKind.True:
                        case JsonValueKind.False:
                        case JsonValueKind.Null:
                            prop.Value.WriteTo(writer);
                            break;
                        case JsonValueKind.Array:
                            writer.WriteStringValue($"[array:{prop.Value.GetArrayLength()} items]");
                            break;
                        case JsonValueKind.Object:
                            int fieldCount = 0;
                            foreach (var _ in prop.Value.EnumerateObject()) fieldCount++;
                            writer.WriteStringValue($"[object:{fieldCount} fields]");
                            break;
                        default:
                            prop.Value.WriteTo(writer);
                            break;
                    }
                }
                writer.WriteEndObject();
            }
            else
            {
                doc.RootElement.WriteTo(writer);
            }

            writer.Flush();
            return stream.ToArray();
        }
        catch
        {
            return JsonSerializer.SerializeToUtf8Bytes(doc.RootElement);
        }
    }

    private static ReadOnlyMemory<byte> ExtractMetadataJson(JsonDocument doc)
    {
        // Keep only key names, value types, and sizes
        try
        {
            using var stream = new System.IO.MemoryStream();
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });

            writer.WriteStartObject();
            writer.WriteString("_type", "metadata-only");

            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject("schema");
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    string typeLabel = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => "string",
                        JsonValueKind.Number => "number",
                        JsonValueKind.True or JsonValueKind.False => "boolean",
                        JsonValueKind.Array => $"array[{prop.Value.GetArrayLength()}]",
                        JsonValueKind.Object => "object",
                        JsonValueKind.Null => "null",
                        _ => "unknown"
                    };
                    writer.WriteString(prop.Name, typeLabel);
                }
                writer.WriteEndObject();
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                writer.WriteNumber("arrayLength", doc.RootElement.GetArrayLength());
                if (doc.RootElement.GetArrayLength() > 0)
                {
                    writer.WriteString("itemType", doc.RootElement[0].ValueKind.ToString());
                }
            }

            writer.WriteEndObject();
            writer.Flush();
            return stream.ToArray();
        }
        catch
        {
            return Encoding.UTF8.GetBytes("{\"_type\":\"metadata-only\",\"error\":\"extraction-failed\"}");
        }
    }

    private static ReadOnlyMemory<byte> StripBinaryTrailing(ReadOnlyMemory<byte> data, double keepRatio)
    {
        int keepBytes = Math.Max(1, (int)(data.Length * keepRatio));
        keepBytes = Math.Min(keepBytes, data.Length);
        return data[..keepBytes];
    }

    private static ReadOnlyMemory<byte> ExtractBinaryMetadata(ReadOnlyMemory<byte> data)
    {
        // For binary: extract header (up to 256 bytes) + encode size metadata
        int headerSize = Math.Min(256, data.Length);
        var sb = new StringBuilder(headerSize * 3 + 128);
        sb.Append($"{{\"_type\":\"binary-metadata\",\"size\":{data.Length},\"headerBytes\":\"");

        var header = data.Span[..headerSize];
        for (int i = 0; i < headerSize; i++)
        {
            sb.Append(header[i].ToString("X2"));
        }

        sb.Append("\"}");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static bool TryParseJson(ReadOnlySpan<byte> data, out JsonDocument? doc)
    {
        doc = null;
        if (data.IsEmpty) return false;

        // Quick check: must start with { or [ (after optional whitespace)
        int start = 0;
        while (start < data.Length && (data[start] == ' ' || data[start] == '\t' || data[start] == '\n' || data[start] == '\r'))
            start++;

        if (start >= data.Length) return false;
        if (data[start] != '{' && data[start] != '[') return false;

        try
        {
            doc = JsonDocument.Parse(data.ToArray());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
