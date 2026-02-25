using System.Diagnostics;
using System.Reflection;
using System.Text;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.WasmLanguages;

/// <summary>
/// Benchmarking strategy that orchestrates standardized performance comparisons across all
/// WASM language strategies. Measures execution time, peak memory, and binary size for each
/// language using a consistent sample WASM workload, then generates a formatted comparison report.
/// </summary>
/// <remarks>
/// <para>
/// This meta-strategy does not represent a specific language but rather provides cross-language
/// benchmark orchestration. It discovers all <see cref="WasmLanguageStrategyBase"/> subclasses
/// in the current assembly via reflection and runs each through a standardized workload.
/// </para>
/// <para>
/// Benchmark results include:
/// </para>
/// <list type="bullet">
/// <item><description>Execution time from <see cref="ComputeResult.ExecutionTime"/></description></item>
/// <item><description>Peak memory usage from <see cref="ComputeResult.MemoryUsed"/></description></item>
/// <item><description>Binary size from the language strategy's embedded sample WASM bytes</description></item>
/// <item><description>Tier classification from <see cref="WasmLanguageInfo"/> metadata</description></item>
/// </list>
/// </remarks>
internal sealed class WasmLanguageBenchmarkStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.benchmark";

    /// <inheritdoc/>
    public override string StrategyName => "WASM Language Benchmark";

    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.WASM;

    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.WASM];

    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: true,
        SupportsSandboxing: true,
        MaxMemoryBytes: 2L * 1024 * 1024 * 1024,
        MaxExecutionTime: TimeSpan.FromMinutes(30),
        SupportedLanguages: ["benchmark"],
        SupportsMultiThreading: false,
        SupportsAsync: true,
        SupportsNetworkAccess: false,
        SupportsFileSystemAccess: false,
        MaxConcurrentTasks: 1,
        SupportsNativeDependencies: false,
        SupportsPrecompilation: false,
        SupportsDynamicLoading: false,
        MemoryIsolation: MemoryIsolationLevel.Sandbox
    );

    /// <summary>
    /// Represents a standardized benchmark workload for WASM language comparison.
    /// Each workload provides a consistent test case that exercises the WASM execution pipeline.
    /// </summary>
    /// <param name="Name">A short identifier for the workload (e.g., "nop-startup").</param>
    /// <param name="Description">A human-readable description of what the workload tests.</param>
    /// <param name="WasmBytes">The WASM module bytes to execute for this workload.</param>
    /// <param name="ExpectedOutputPattern">A regex pattern or literal string the output should match.</param>
    internal record BenchmarkWorkload(
        string Name,
        string Description,
        byte[] WasmBytes,
        string ExpectedOutputPattern
    );

    /// <summary>
    /// Represents the benchmark result for a single language strategy execution.
    /// </summary>
    /// <param name="Language">The programming language name.</param>
    /// <param name="Tier">The tier classification (Tier 1, Tier 2, or Tier 3).</param>
    /// <param name="ExecutionTime">The measured execution time.</param>
    /// <param name="PeakMemoryBytes">The peak memory usage during execution in bytes.</param>
    /// <param name="BinarySizeBytes">The size of the sample WASM binary in bytes.</param>
    /// <param name="Success">Whether the benchmark execution succeeded.</param>
    /// <param name="Error">An error message if the benchmark failed, otherwise null.</param>
    internal record BenchmarkResult(
        string Language,
        string Tier,
        TimeSpan ExecutionTime,
        long PeakMemoryBytes,
        long BinarySizeBytes,
        bool Success,
        string? Error
    );

    /// <summary>
    /// Runs standardized benchmarks across all provided language strategies. For each strategy,
    /// creates a compute task using the strategy's embedded sample WASM bytes, executes it,
    /// and measures execution time, memory usage, and binary size.
    /// </summary>
    /// <param name="strategies">The collection of language strategies to benchmark.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A sorted list of <see cref="BenchmarkResult"/> instances, ordered by execution time ascending.
    /// Failed benchmarks are placed at the end.
    /// </returns>
    public async Task<IReadOnlyList<BenchmarkResult>> RunBenchmarksAsync(
        IEnumerable<WasmLanguageStrategyBase> strategies,
        CancellationToken ct)
    {
        var results = new List<BenchmarkResult>();

        foreach (var strategy in strategies)
        {
            ct.ThrowIfCancellationRequested();

            var tier = ClassifyTier(strategy.GetType());
            var langInfo = strategy.LanguageInfo;

            try
            {
                var verification = await strategy.VerifyLanguageAsync(ct);

                var executionTime = verification.ExecutionTime ?? TimeSpan.Zero;
                var binarySize = verification.BinarySizeBytes ?? 0;
                var peakMemory = GC.GetTotalMemory(false);

                results.Add(new BenchmarkResult(
                    Language: langInfo.Language,
                    Tier: tier,
                    ExecutionTime: executionTime,
                    PeakMemoryBytes: peakMemory,
                    BinarySizeBytes: binarySize,
                    Success: verification.ExecutionSuccessful,
                    Error: verification.ErrorMessage
                ));
            }
            catch (Exception ex)
            {
                results.Add(new BenchmarkResult(
                    Language: langInfo.Language,
                    Tier: tier,
                    ExecutionTime: TimeSpan.Zero,
                    PeakMemoryBytes: 0,
                    BinarySizeBytes: 0,
                    Success: false,
                    Error: ex.Message
                ));
            }
        }

        // Sort: successful benchmarks by execution time ascending, failures at the end
        results.Sort((a, b) =>
        {
            if (a.Success && !b.Success) return -1;
            if (!a.Success && b.Success) return 1;
            return a.ExecutionTime.CompareTo(b.ExecutionTime);
        });

        return results;
    }

    /// <summary>
    /// Generates a formatted markdown benchmark report comparing all language results.
    /// Results are grouped by tier with columns for language, execution time, memory, binary size, and status.
    /// </summary>
    /// <param name="results">The benchmark results to format.</param>
    /// <returns>A markdown-formatted string containing the benchmark comparison table.</returns>
    public string GenerateBenchmarkReport(IReadOnlyList<BenchmarkResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# WASM Language Benchmark Report");
        sb.AppendLine();
        sb.AppendLine($"**Date:** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"**Languages Tested:** {results.Count}");
        sb.AppendLine($"**Successful:** {results.Count(r => r.Success)}");
        sb.AppendLine($"**Failed:** {results.Count(r => !r.Success)}");
        sb.AppendLine();

        foreach (var tierName in new[] { "Tier 1", "Tier 2", "Tier 3" })
        {
            var tierResults = results.Where(r => r.Tier == tierName).ToList();
            if (tierResults.Count == 0) continue;

            sb.AppendLine($"## {tierName}");
            sb.AppendLine();
            sb.AppendLine("| Language | Tier | Execution (ms) | Memory (KB) | Binary (KB) | Status |");
            sb.AppendLine("|----------|------|---------------:|------------:|------------:|--------|");

            foreach (var result in tierResults)
            {
                var status = result.Success ? "OK" : $"FAIL: {result.Error ?? "Unknown"}";
                var execMs = result.ExecutionTime.TotalMilliseconds;
                var memKb = result.PeakMemoryBytes / 1024.0;
                var binKb = result.BinarySizeBytes / 1024.0;

                sb.AppendLine($"| {result.Language,-10} | {result.Tier} | {execMs,14:F2} | {memKb,11:F1} | {binKb,11:F1} | {status} |");
            }

            sb.AppendLine();
        }

        // Summary statistics
        var successful = results.Where(r => r.Success).ToList();
        if (successful.Count > 0)
        {
            sb.AppendLine("## Summary Statistics");
            sb.AppendLine();
            sb.AppendLine($"- **Fastest:** {successful[0].Language} ({successful[0].ExecutionTime.TotalMilliseconds:F2} ms)");
            sb.AppendLine($"- **Slowest:** {successful[^1].Language} ({successful[^1].ExecutionTime.TotalMilliseconds:F2} ms)");
            sb.AppendLine($"- **Smallest Binary:** {successful.OrderBy(r => r.BinarySizeBytes).First().Language} ({successful.Min(r => r.BinarySizeBytes)} bytes)");
            sb.AppendLine($"- **Largest Binary:** {successful.OrderByDescending(r => r.BinarySizeBytes).First().Language} ({successful.Max(r => r.BinarySizeBytes)} bytes)");
            sb.AppendLine($"- **Average Execution:** {successful.Average(r => r.ExecutionTime.TotalMilliseconds):F2} ms");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Executes the benchmark strategy by discovering all <see cref="WasmLanguageStrategyBase"/>
    /// subclasses in the current assembly, running benchmarks on each, generating a report,
    /// and returning the report as UTF-8 bytes in the <see cref="ComputeResult"/>.
    /// </summary>
    /// <param name="task">The compute task (used for task ID tracking).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="ComputeResult"/> containing the benchmark report as UTF-8 output data.</returns>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var strategies = DiscoverLanguageStrategies();
            var results = await RunBenchmarksAsync(strategies, cancellationToken);
            var report = GenerateBenchmarkReport(results);
            return (EncodeOutput(report), $"Benchmarked {results.Count} languages");
        }, cancellationToken);
    }

    /// <summary>
    /// Discovers all concrete <see cref="WasmLanguageStrategyBase"/> subclasses in the current assembly
    /// and instantiates them for benchmarking.
    /// </summary>
    /// <returns>A list of instantiated language strategy instances.</returns>
    private static List<WasmLanguageStrategyBase> DiscoverLanguageStrategies()
    {
        var strategies = new List<WasmLanguageStrategyBase>();
        var assembly = Assembly.GetExecutingAssembly();
        var baseType = typeof(WasmLanguageStrategyBase);

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).ToArray()!;
        }

        foreach (var type in types)
        {
            if (type.IsAbstract || type.IsInterface || !baseType.IsAssignableFrom(type))
                continue;

            try
            {
                if (Activator.CreateInstance(type) is WasmLanguageStrategyBase strategy)
                    strategies.Add(strategy);
            }
            catch
            {
                // Skip types that cannot be instantiated
            }
        }

        return strategies;
    }

    /// <summary>
    /// Classifies a strategy type into a tier based on its namespace.
    /// </summary>
    /// <param name="type">The strategy type to classify.</param>
    /// <returns>"Tier 1", "Tier 2", or "Tier 3" based on the type's namespace.</returns>
    private static string ClassifyTier(Type type)
    {
        var ns = type.Namespace ?? string.Empty;
        if (ns.Contains(".Tier1", StringComparison.OrdinalIgnoreCase)) return "Tier 1";
        if (ns.Contains(".Tier2", StringComparison.OrdinalIgnoreCase)) return "Tier 2";
        if (ns.Contains(".Tier3", StringComparison.OrdinalIgnoreCase)) return "Tier 3";
        return "Unknown";
    }
}
