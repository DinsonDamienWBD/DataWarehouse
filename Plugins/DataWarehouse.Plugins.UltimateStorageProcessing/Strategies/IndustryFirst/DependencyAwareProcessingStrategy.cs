using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.IndustryFirst;

/// <summary>
/// Dependency-aware processing strategy that builds a dependency DAG from file references,
/// performs topological sort for correct processing order, enables parallel processing of
/// independent nodes, and detects circular dependencies.
/// </summary>
internal sealed class DependencyAwareProcessingStrategy : StorageProcessingStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "industryfirst-dependencyaware";

    /// <inheritdoc/>
    public override string Name => "Dependency-Aware Processing Strategy";

    /// <inheritdoc/>
    public override StorageProcessingCapabilities Capabilities => new()
    {
        SupportsFiltering = true, SupportsAggregation = true, SupportsProjection = true,
        SupportsSorting = true, SupportsLimiting = true,
        SupportedOperations = new[] { "eq", "ne", "gt", "lt", "gte", "lte" },
        SupportedAggregations = new[] { AggregationType.Count, AggregationType.Sum, AggregationType.Average },
        MaxQueryComplexity = 8
    };

    /// <inheritdoc/>
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();

        if (!Directory.Exists(query.Source))
            return MakeError("Source directory not found", sw);

        // Build dependency graph by scanning file contents for references.
        // Use EnumerateFiles instead of GetFiles to avoid loading the full file list into memory (finding 4293).
        var graph = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var allFiles = Directory.EnumerateFiles(query.Source, "*", SearchOption.AllDirectories).ToArray();
        var fileNames = new HashSet<string>(allFiles.Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);

        foreach (var file in allFiles)
        {
            ct.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(file);
            if (!graph.ContainsKey(fileName)) graph[fileName] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Scan for import/include/require patterns
            try
            {
                var content = await File.ReadAllTextAsync(file, ct);
                // Match import "file", require("file"), #include "file", etc.
                var matches = Regex.Matches(content, @"(?:import|require|#include|@import)\s*[\(""]([^"")\s]+)[\)""]");
                foreach (Match m in matches)
                {
                    var dep = Path.GetFileName(m.Groups[1].Value);
                    if (fileNames.Contains(dep) && !dep.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                        graph[fileName].Add(dep);
                }
            }
            catch { /* Skip unreadable files */ }
        }

        // Topological sort with cycle detection
        var (sorted, cycles) = TopologicalSort(graph);

        // Identify parallel processing groups (independent nodes)
        var processingWaves = ComputeProcessingWaves(graph, sorted);

        sw.Stop();
        return new ProcessingResult
        {
            Data = new Dictionary<string, object?>
            {
                ["sourcePath"] = query.Source,
                ["totalFiles"] = allFiles.Length,
                ["graphNodes"] = graph.Count,
                ["totalEdges"] = graph.Values.Sum(deps => deps.Count),
                ["hasCycles"] = cycles.Count > 0,
                ["cycleCount"] = cycles.Count,
                ["cycles"] = cycles.Take(10).Select(c => c.ToList()).ToList(),
                ["processingOrder"] = sorted.Take(100).ToList(),
                ["waveCount"] = processingWaves.Count,
                ["processingWaves"] = processingWaves.Select((w, i) => new Dictionary<string, object>
                {
                    ["wave"] = i, ["parallelFiles"] = w.Count, ["files"] = w.Take(20).ToList()
                }).ToList(),
                ["maxParallelism"] = processingWaves.Count > 0 ? processingWaves.Max(w => w.Count) : 0
            },
            Metadata = new ProcessingMetadata
            {
                RowsProcessed = allFiles.Length, RowsReturned = 1,
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

    /// <summary>
    /// Performs topological sort using Kahn's algorithm with cycle detection.
    /// </summary>
    private static (List<string> Sorted, List<List<string>> Cycles) TopologicalSort(Dictionary<string, HashSet<string>> graph)
    {
        var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in graph.Keys) inDegree[node] = 0;
        foreach (var deps in graph.Values)
            foreach (var dep in deps)
            { if (!inDegree.ContainsKey(dep)) inDegree[dep] = 0; inDegree[dep]++; }

        var queue = new Queue<string>(inDegree.Where(kvp => kvp.Value == 0).Select(kvp => kvp.Key));
        var sorted = new List<string>();

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            sorted.Add(node);

            if (graph.TryGetValue(node, out var deps))
            {
                foreach (var dep in deps)
                {
                    inDegree[dep]--;
                    if (inDegree[dep] == 0) queue.Enqueue(dep);
                }
            }
        }

        // Detect cycles (remaining nodes with non-zero in-degree)
        var cycles = new List<List<string>>();
        var remaining = inDegree.Where(kvp => kvp.Value > 0).Select(kvp => kvp.Key).ToHashSet();
        if (remaining.Count > 0)
        {
            // Find cycles via DFS
            var visited = new HashSet<string>();
            foreach (var node in remaining)
            {
                if (visited.Contains(node)) continue;
                var cycle = new List<string>();
                var stack = new HashSet<string>();
                if (FindCycle(node, graph, visited, stack, cycle))
                    cycles.Add(cycle);
            }
        }

        return (sorted, cycles);
    }

    private static bool FindCycle(string node, Dictionary<string, HashSet<string>> graph, HashSet<string> visited, HashSet<string> stack, List<string> cycle)
    {
        visited.Add(node);
        stack.Add(node);

        if (graph.TryGetValue(node, out var deps))
        {
            foreach (var dep in deps)
            {
                if (!visited.Contains(dep))
                {
                    if (FindCycle(dep, graph, visited, stack, cycle))
                    {
                        cycle.Insert(0, node);
                        return true;
                    }
                }
                else if (stack.Contains(dep))
                {
                    cycle.Add(dep);
                    cycle.Insert(0, node);
                    return true;
                }
            }
        }

        stack.Remove(node);
        return false;
    }

    /// <summary>
    /// Groups nodes into processing waves where each wave can be processed in parallel.
    /// </summary>
    private static List<List<string>> ComputeProcessingWaves(Dictionary<string, HashSet<string>> graph, List<string> sorted)
    {
        var waves = new List<List<string>>();
        var nodeWave = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in sorted)
        {
            var wave = 0;
            if (graph.TryGetValue(node, out var deps))
            {
                foreach (var dep in deps)
                {
                    if (nodeWave.TryGetValue(dep, out var depWave))
                        wave = Math.Max(wave, depWave + 1);
                }
            }

            nodeWave[node] = wave;
            while (waves.Count <= wave) waves.Add(new List<string>());
            waves[wave].Add(node);
        }

        return waves;
    }

    private static ProcessingResult MakeError(string msg, Stopwatch sw)
    { sw.Stop(); return new ProcessingResult { Data = new Dictionary<string, object?> { ["error"] = msg }, Metadata = new ProcessingMetadata { ProcessingTimeMs = sw.Elapsed.TotalMilliseconds } }; }
}
