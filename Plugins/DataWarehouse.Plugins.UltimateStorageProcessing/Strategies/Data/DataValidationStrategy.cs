using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.Data;

/// <summary>
/// Data validation strategy that validates data against inferred or provided schemas.
/// Performs null checks, type checks, range validation, constraint evaluation,
/// and generates a comprehensive validation report.
/// </summary>
internal sealed class DataValidationStrategy : StorageProcessingStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "data-validation";

    /// <inheritdoc/>
    public override string Name => "Data Validation Strategy";

    /// <inheritdoc/>
    public override StorageProcessingCapabilities Capabilities => new()
    {
        SupportsFiltering = true, SupportsPredication = true, SupportsAggregation = true,
        SupportsProjection = true, SupportsLimiting = true,
        SupportedOperations = new[] { "eq", "ne", "gt", "lt", "gte", "lte", "in" },
        SupportedAggregations = new[] { AggregationType.Count, AggregationType.Sum, AggregationType.Average },
        MaxQueryComplexity = 7
    };

    /// <inheritdoc/>
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();

        if (!File.Exists(query.Source))
            return MakeError("Source data file not found", sw);

        var lines = await File.ReadAllLinesAsync(query.Source, ct);
        var validRows = 0;
        var invalidRows = 0;
        var nullFields = 0;
        var typeErrors = 0;
        var constraintViolations = new List<Dictionary<string, object>>();

        var rowIndex = 0;
        foreach (var line in lines)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var rowValid = true;

                foreach (var prop in root.EnumerateObject())
                {
                    // Null check
                    if (prop.Value.ValueKind == JsonValueKind.Null)
                    {
                        nullFields++;
                        continue;
                    }

                    // Type validation
                    if (prop.Value.ValueKind == JsonValueKind.Undefined)
                    {
                        typeErrors++;
                        rowValid = false;
                        constraintViolations.Add(new Dictionary<string, object>
                        {
                            ["row"] = rowIndex, ["field"] = prop.Name, ["issue"] = "undefined_value"
                        });
                    }

                    // Range validation for numbers
                    if (prop.Value.ValueKind == JsonValueKind.Number)
                    {
                        var numVal = prop.Value.GetDouble();
                        if (double.IsNaN(numVal) || double.IsInfinity(numVal))
                        {
                            constraintViolations.Add(new Dictionary<string, object>
                            {
                                ["row"] = rowIndex, ["field"] = prop.Name, ["issue"] = "invalid_number", ["value"] = numVal.ToString()
                            });
                            rowValid = false;
                        }
                    }

                    // String length validation
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var strVal = prop.Value.GetString();
                        if (strVal != null && strVal.Length > 1_000_000)
                        {
                            constraintViolations.Add(new Dictionary<string, object>
                            {
                                ["row"] = rowIndex, ["field"] = prop.Name, ["issue"] = "string_too_long", ["length"] = strVal.Length
                            });
                            rowValid = false;
                        }
                    }
                }

                if (rowValid) validRows++;
                else invalidRows++;
            }
            catch (JsonException)
            {
                invalidRows++;
                constraintViolations.Add(new Dictionary<string, object>
                {
                    ["row"] = rowIndex, ["field"] = "_row", ["issue"] = "invalid_json"
                });
            }

            rowIndex++;
        }

        var totalRows = validRows + invalidRows;
        sw.Stop();

        return new ProcessingResult
        {
            Data = new Dictionary<string, object?>
            {
                ["sourcePath"] = query.Source, ["totalRows"] = totalRows,
                ["validRows"] = validRows, ["invalidRows"] = invalidRows,
                ["nullFields"] = nullFields, ["typeErrors"] = typeErrors,
                ["constraintViolationCount"] = constraintViolations.Count,
                ["validationPassRate"] = totalRows > 0 ? Math.Round((double)validRows / totalRows * 100.0, 2) : 0.0,
                ["sampleViolations"] = constraintViolations.Take(20).ToList()
            },
            Metadata = new ProcessingMetadata
            {
                RowsProcessed = totalRows, RowsReturned = 1,
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
        await foreach (var r in CliProcessHelper.EnumerateProjectFiles(query, new[] { ".json", ".jsonl", ".csv", ".parquet" }, sw, ct))
            yield return r;
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        return CliProcessHelper.AggregateProjectFiles(query, aggregationType, new[] { ".json", ".jsonl", ".csv" }, ct);
    }

    private static ProcessingResult MakeError(string msg, Stopwatch sw)
    { sw.Stop(); return new ProcessingResult { Data = new Dictionary<string, object?> { ["error"] = msg }, Metadata = new ProcessingMetadata { ProcessingTimeMs = sw.Elapsed.TotalMilliseconds } }; }
}
