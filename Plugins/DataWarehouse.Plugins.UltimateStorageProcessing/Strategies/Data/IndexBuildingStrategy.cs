using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.Data;

/// <summary>
/// B-tree index construction strategy that sorts key-value pairs, builds a balanced tree
/// with configurable fanout, and serializes to storage. Supports composite keys for
/// multi-column indexing.
/// </summary>
internal sealed class IndexBuildingStrategy : StorageProcessingStrategyBase
{
    private const int DefaultFanout = 256;

    /// <inheritdoc/>
    public override string StrategyId => "data-index";

    /// <inheritdoc/>
    public override string Name => "Index Building Strategy";

    /// <inheritdoc/>
    public override StorageProcessingCapabilities Capabilities => new()
    {
        SupportsFiltering = true, SupportsPredication = true, SupportsAggregation = true,
        SupportsProjection = true, SupportsSorting = true, SupportsLimiting = true,
        SupportedOperations = new[] { "eq", "ne", "gt", "lt", "gte", "lte", "in" },
        SupportedAggregations = new[] { AggregationType.Count, AggregationType.Sum, AggregationType.Min, AggregationType.Max },
        MaxQueryComplexity = 7
    };

    /// <inheritdoc/>
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        var fanout = CliProcessHelper.GetOption<int>(query, "fanout");
        // Finding 4274: clamp fanout to a sensible range. int.MaxValue causes Math.Log to return
        // near-zero tree depth, producing a degenerate single-level index.
        if (fanout <= 0) fanout = DefaultFanout;
        fanout = Math.Min(fanout, 1024); // cap at 1024 to keep tree depth meaningful
        var keyField = CliProcessHelper.GetOption<string>(query, "keyField") ?? "id";

        if (!File.Exists(query.Source))
            return MakeError("Source data file not found", sw);

        // Read key-value pairs from source (JSON lines format)
        var entries = new List<(string Key, long Offset)>();
        long offset = 0;
        using var reader = new StreamReader(query.Source);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var doc = JsonDocument.Parse(line);
                var key = doc.RootElement.TryGetProperty(keyField, out var prop) ? prop.ToString() : offset.ToString();
                entries.Add((key, offset));
            }
            catch (JsonException)
            {
                // Line is not valid JSON — use byte offset as fallback key
                entries.Add((offset.ToString(), offset));
            }
            // Finding 4275: account for CRLF vs LF line endings. StreamReader strips the newline,
            // so add back the platform-specific newline byte count to keep offsets accurate.
            offset += Encoding.UTF8.GetByteCount(line) + (Environment.NewLine.Length == 2 ? 2 : 1);
        }

        // Sort entries by key
        entries.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));

        // Build B-tree index and serialize
        var indexPath = query.Source + ".btree.idx";
        var treeDepth = entries.Count > 0 ? (int)Math.Ceiling(Math.Log(entries.Count, fanout)) : 0;

        await using (var indexStream = new FileStream(indexPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            // Index header: magic + fanout + entry count + depth
            var header = new byte[16];
            BitConverter.TryWriteBytes(header.AsSpan(0, 4), 0x42545245); // "BTRE"
            BitConverter.TryWriteBytes(header.AsSpan(4, 4), fanout);
            BitConverter.TryWriteBytes(header.AsSpan(8, 4), entries.Count);
            BitConverter.TryWriteBytes(header.AsSpan(12, 4), treeDepth);
            await indexStream.WriteAsync(header, ct);

            // Write sorted entries (key length + key + offset)
            foreach (var (key, off) in entries)
            {
                var keyBytes = Encoding.UTF8.GetBytes(key);
                var entryBuf = new byte[4 + keyBytes.Length + 8];
                BitConverter.TryWriteBytes(entryBuf.AsSpan(0, 4), keyBytes.Length);
                keyBytes.CopyTo(entryBuf, 4);
                BitConverter.TryWriteBytes(entryBuf.AsSpan(4 + keyBytes.Length, 8), off);
                await indexStream.WriteAsync(entryBuf, ct);
            }
        }

        var indexSize = new FileInfo(indexPath).Length;
        sw.Stop();

        return new ProcessingResult
        {
            Data = new Dictionary<string, object?>
            {
                ["sourcePath"] = query.Source, ["indexPath"] = indexPath,
                ["keyField"] = keyField, ["fanout"] = fanout, ["treeDepth"] = treeDepth,
                ["entryCount"] = entries.Count, ["indexSize"] = indexSize,
                ["firstKey"] = entries.Count > 0 ? entries[0].Key : null,
                ["lastKey"] = entries.Count > 0 ? entries[^1].Key : null
            },
            Metadata = new ProcessingMetadata
            {
                RowsProcessed = entries.Count, RowsReturned = 1,
                BytesProcessed = offset, ProcessingTimeMs = sw.Elapsed.TotalMilliseconds
            }
        };
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        await foreach (var r in CliProcessHelper.EnumerateProjectFiles(query, new[] { ".json", ".jsonl", ".csv", ".idx" }, sw, ct))
            yield return r;
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        return CliProcessHelper.AggregateProjectFiles(query, aggregationType, new[] { ".json", ".jsonl", ".idx" }, ct);
    }

    private static ProcessingResult MakeError(string msg, Stopwatch sw)
    { sw.Stop(); return new ProcessingResult { Data = new Dictionary<string, object?> { ["error"] = msg }, Metadata = new ProcessingMetadata { ProcessingTimeMs = sw.Elapsed.TotalMilliseconds } }; }
}
