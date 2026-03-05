using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.Data;

/// <summary>
/// Schema inference strategy that automatically infers data schemas from data samples.
/// Performs type detection (int/float/string/bool/date), cardinality estimation,
/// nullable detection, and nested structure inference from JSON data.
/// </summary>
internal sealed class SchemaInferenceStrategy : StorageProcessingStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "data-schema";

    /// <inheritdoc/>
    public override string Name => "Schema Inference Strategy";

    /// <inheritdoc/>
    public override StorageProcessingCapabilities Capabilities => new()
    {
        SupportsFiltering = true, SupportsAggregation = true, SupportsProjection = true, SupportsLimiting = true,
        SupportedOperations = new[] { "eq", "ne", "gt", "lt", "gte", "lte" },
        SupportedAggregations = new[] { AggregationType.Count, AggregationType.Sum },
        MaxQueryComplexity = 6
    };

    /// <inheritdoc/>
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        var sampleSize = CliProcessHelper.GetOption<int>(query, "sampleSize");
        if (sampleSize <= 0) sampleSize = 1000;

        if (!File.Exists(query.Source))
            return MakeError("Source file not found", sw);

        // Read sample rows
        var fields = new Dictionary<string, FieldStats>(StringComparer.OrdinalIgnoreCase);
        var rowCount = 0;

        using var reader = new StreamReader(query.Source);
        while (await reader.ReadLineAsync(ct) is { } line && rowCount < sampleSize)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                InferFields(doc.RootElement, "", fields);
                rowCount++;
            }
            catch { /* Skip unparseable lines */ }
        }

        // Build schema
        var schema = fields.Select(kvp =>
        {
            var f = kvp.Value;
            var inferred = InferType(f);
            return new Dictionary<string, object>
            {
                ["field"] = kvp.Key,
                ["inferredType"] = inferred,
                ["nullable"] = f.NullCount > 0,
                ["nullRate"] = rowCount > 0 ? Math.Round((double)f.NullCount / rowCount * 100.0, 2) : 0.0,
                ["cardinality"] = f.DistinctValues.Count,
                ["sampleValues"] = f.DistinctValues.Take(5).ToList(),
                ["isNested"] = f.IsNested,
                ["occurrences"] = f.OccurrenceCount
            };
        }).OrderBy(d => d["field"]).ToList();

        sw.Stop();
        return new ProcessingResult
        {
            Data = new Dictionary<string, object?>
            {
                ["sourcePath"] = query.Source, ["sampleSize"] = rowCount,
                ["fieldCount"] = fields.Count, ["schema"] = schema
            },
            Metadata = new ProcessingMetadata
            {
                RowsProcessed = rowCount, RowsReturned = 1,
                BytesProcessed = new FileInfo(query.Source).Length,
                ProcessingTimeMs = sw.Elapsed.TotalMilliseconds
            }
        };
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        await foreach (var r in CliProcessHelper.EnumerateProjectFiles(query, new[] { ".json", ".jsonl", ".csv" }, sw, ct))
            yield return r;
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        return CliProcessHelper.AggregateProjectFiles(query, aggregationType, new[] { ".json", ".jsonl" }, ct);
    }

    private static void InferFields(JsonElement element, string prefix, Dictionary<string, FieldStats> fields)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                var fieldName = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";
                if (!fields.ContainsKey(fieldName)) fields[fieldName] = new FieldStats();
                var stats = fields[fieldName];
                stats.OccurrenceCount++;

                switch (prop.Value.ValueKind)
                {
                    case JsonValueKind.Null or JsonValueKind.Undefined:
                        stats.NullCount++;
                        break;
                    case JsonValueKind.String:
                        var strVal = prop.Value.GetString() ?? "";
                        stats.StringCount++;
                        // Finding 4280: use InvariantCulture for deterministic schema inference across locales.
                        if (DateTime.TryParse(strVal, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _)) stats.DateCount++;
                        if (stats.DistinctValues.Count < 100) stats.DistinctValues.Add(strVal);
                        break;
                    case JsonValueKind.Number:
                        stats.NumberCount++;
                        var numStr = prop.Value.GetRawText();
                        if (numStr.Contains('.')) stats.FloatCount++;
                        else stats.IntCount++;
                        if (stats.DistinctValues.Count < 100) stats.DistinctValues.Add(numStr);
                        break;
                    case JsonValueKind.True or JsonValueKind.False:
                        stats.BoolCount++;
                        if (stats.DistinctValues.Count < 100) stats.DistinctValues.Add(prop.Value.GetBoolean().ToString());
                        break;
                    case JsonValueKind.Object:
                        stats.IsNested = true;
                        InferFields(prop.Value, fieldName, fields);
                        break;
                    case JsonValueKind.Array:
                        stats.ArrayCount++;
                        stats.IsNested = true;
                        break;
                }
            }
        }
    }

    private static string InferType(FieldStats f)
    {
        var total = f.OccurrenceCount - f.NullCount;
        if (total == 0) return "null";
        if (f.BoolCount == total) return "boolean";
        if (f.IntCount == total) return "integer";
        if (f.FloatCount == total || f.NumberCount == total) return "float";
        if (f.DateCount > total * 0.8 && f.StringCount == total) return "datetime";
        if (f.StringCount == total) return "string";
        if (f.ArrayCount == total) return "array";
        if (f.IsNested) return "object";
        return "mixed";
    }

    private sealed class FieldStats
    {
        public int OccurrenceCount;
        public int NullCount;
        public int StringCount;
        public int NumberCount;
        public int IntCount;
        public int FloatCount;
        public int BoolCount;
        public int DateCount;
        public int ArrayCount;
        public bool IsNested;
        public readonly HashSet<string> DistinctValues = new(StringComparer.OrdinalIgnoreCase);
    }

    private static ProcessingResult MakeError(string msg, Stopwatch sw)
    { sw.Stop(); return new ProcessingResult { Data = new Dictionary<string, object?> { ["error"] = msg }, Metadata = new ProcessingMetadata { ProcessingTimeMs = sw.Elapsed.TotalMilliseconds } }; }
}
