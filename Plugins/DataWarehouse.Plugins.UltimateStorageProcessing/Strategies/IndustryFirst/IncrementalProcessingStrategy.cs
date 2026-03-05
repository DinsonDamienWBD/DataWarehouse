using System.Diagnostics;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts.StorageProcessing;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.IndustryFirst;

/// <summary>
/// Incremental processing strategy that tracks file modification timestamps to only reprocess
/// changed files. Maintains a dependency graph for cascade detection and supports differential
/// result merging to avoid redundant work.
/// </summary>
internal sealed class IncrementalProcessingStrategy : StorageProcessingStrategyBase
{
    private readonly BoundedDictionary<string, DateTimeOffset> _lastProcessed = new BoundedDictionary<string, DateTimeOffset>(1000);
    private readonly BoundedDictionary<string, HashSet<string>> _dependencies = new BoundedDictionary<string, HashSet<string>>(1000);

    /// <inheritdoc/>
    public override string StrategyId => "industryfirst-incremental";

    /// <inheritdoc/>
    public override string Name => "Incremental Processing Strategy";

    /// <inheritdoc/>
    public override StorageProcessingCapabilities Capabilities => new()
    {
        SupportsFiltering = true, SupportsAggregation = true, SupportsProjection = true, SupportsLimiting = true,
        SupportedOperations = new[] { "eq", "ne", "gt", "lt", "gte", "lte" },
        SupportedAggregations = new[] { AggregationType.Count, AggregationType.Sum, AggregationType.Average },
        MaxQueryComplexity = 7
    };

    /// <inheritdoc/>
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();

        if (!Directory.Exists(query.Source))
            return MakeError("Source directory not found", sw);

        // Use HashSet for O(1) Contains checks (finding 4292: List.Contains is O(n)).
        var changedFilesSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cascadedFilesSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unchangedFiles = new List<string>();

        foreach (var file in Directory.EnumerateFiles(query.Source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            var lastModified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);

            if (_lastProcessed.TryGetValue(file, out var lastProcessedTime))
            {
                if (lastModified > lastProcessedTime)
                {
                    changedFilesSet.Add(file);
                    // Check for cascaded dependencies (O(1) set Contains per finding 4292)
                    if (_dependencies.TryGetValue(file, out var deps))
                    {
                        foreach (var dep in deps)
                        {
                            if (!changedFilesSet.Contains(dep) && !cascadedFilesSet.Contains(dep))
                                cascadedFilesSet.Add(dep);
                        }
                    }
                }
                else
                {
                    unchangedFiles.Add(file);
                }
            }
            else
            {
                // Never processed before
                changedFilesSet.Add(file);
            }

            // Update tracking
            _lastProcessed[file] = DateTimeOffset.UtcNow;
        }

        var totalToProcess = changedFilesSet.Count + cascadedFilesSet.Count;
        var totalSkipped = unchangedFiles.Count;
        var totalFiles = totalToProcess + totalSkipped;

        sw.Stop();
        return new ProcessingResult
        {
            Data = new Dictionary<string, object?>
            {
                ["sourcePath"] = query.Source,
                ["changedFiles"] = changedFilesSet.Count,
                ["cascadedFiles"] = cascadedFilesSet.Count,
                ["unchangedFiles"] = unchangedFiles.Count,
                ["totalToProcess"] = totalToProcess,
                ["totalSkipped"] = totalSkipped,
                ["processingReduction"] = totalFiles > 0 ? Math.Round((double)totalSkipped / totalFiles * 100.0, 2) : 0.0,
                ["changedFileList"] = changedFilesSet.Take(50).ToList(),
                ["cascadedFileList"] = cascadedFilesSet.Take(20).ToList()
            },
            Metadata = new ProcessingMetadata
            {
                RowsProcessed = totalFiles, RowsReturned = totalToProcess,
                ProcessingTimeMs = sw.Elapsed.TotalMilliseconds
            }
        };
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        await foreach (var r in CliProcessHelper.EnumerateProjectFiles(query, Array.Empty<string>(), sw, ct))
            yield return r;
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        return CliProcessHelper.AggregateProjectFiles(query, aggregationType, Array.Empty<string>(), ct);
    }

    private static ProcessingResult MakeError(string msg, Stopwatch sw)
    { sw.Stop(); return new ProcessingResult { Data = new Dictionary<string, object?> { ["error"] = msg }, Metadata = new ProcessingMetadata { ProcessingTimeMs = sw.Elapsed.TotalMilliseconds } }; }
}
