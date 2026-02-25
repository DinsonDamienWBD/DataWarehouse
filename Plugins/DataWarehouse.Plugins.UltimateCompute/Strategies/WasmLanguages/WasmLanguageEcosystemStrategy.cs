using System.Reflection;
using System.Text;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.WasmLanguages;

/// <summary>
/// Aggregation strategy serving as the "table of contents" for the entire WASM language ecosystem.
/// Discovers all 31 language strategies, classifies them by tier, and generates a comprehensive
/// ecosystem summary including language metadata, WASI support levels, performance tiers, and
/// binary size categories.
/// </summary>
/// <remarks>
/// <para>
/// This meta-strategy does not represent a specific language but rather provides ecosystem-level
/// reporting. It discovers all <see cref="WasmLanguageStrategyBase"/> subclasses in the current
/// assembly via reflection and aggregates their <see cref="WasmLanguageInfo"/> metadata into a
/// unified catalog.
/// </para>
/// <para>
/// The ecosystem report includes:
/// </para>
/// <list type="bullet">
/// <item><description>Total language count with per-tier breakdown</description></item>
/// <item><description>Per-language metadata: WASI support, performance tier, binary size, compile command</description></item>
/// <item><description>Markdown-formatted summary table for human consumption</description></item>
/// </list>
/// </remarks>
internal sealed class WasmLanguageEcosystemStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.ecosystem";

    /// <inheritdoc/>
    public override string StrategyName => "WASM Language Ecosystem";

    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.WASM;

    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.WASM];

    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: true,
        SupportsSandboxing: true,
        MaxMemoryBytes: 2L * 1024 * 1024 * 1024,
        MaxExecutionTime: TimeSpan.FromMinutes(5),
        SupportedLanguages: [
            "rust", "c", "cpp", "dotnet", "go", "assemblyscript", "zig",
            "python", "ruby", "javascript", "php", "lua",
            "typescript", "kotlin", "swift", "java", "dart",
            "haskell", "ocaml", "grain", "moonbit",
            "nim", "v", "crystal", "perl", "r",
            "fortran", "scala", "elixir", "prolog", "ada"
        ],
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
    /// Represents the aggregated ecosystem report with total counts, tier breakdowns,
    /// and per-language summary information.
    /// </summary>
    /// <param name="TotalLanguages">The total number of supported WASM languages.</param>
    /// <param name="Tier1Count">The number of Tier 1 (production-ready, native performance) languages.</param>
    /// <param name="Tier2Count">The number of Tier 2 (mature toolchain support) languages.</param>
    /// <param name="Tier3Count">The number of Tier 3 (experimental/emerging) languages.</param>
    /// <param name="Languages">The detailed per-language summary list.</param>
    internal record EcosystemReport(
        int TotalLanguages,
        int Tier1Count,
        int Tier2Count,
        int Tier3Count,
        IReadOnlyList<LanguageSummary> Languages
    );

    /// <summary>
    /// Represents a summary of a single language's WASM ecosystem status.
    /// </summary>
    /// <param name="Language">The programming language name.</param>
    /// <param name="Tier">The tier classification string (e.g., "Tier 1").</param>
    /// <param name="WasiSupport">The WASI support level.</param>
    /// <param name="Performance">The performance tier classification.</param>
    /// <param name="BinarySize">The typical WASM binary size category.</param>
    /// <param name="CompileCommand">The CLI command to compile this language to WASM.</param>
    /// <param name="Notes">Additional notes about the language's WASM support.</param>
    internal record LanguageSummary(
        string Language,
        string Tier,
        WasiSupportLevel WasiSupport,
        PerformanceTier Performance,
        WasmBinarySize BinarySize,
        string CompileCommand,
        string Notes
    );

    /// <summary>
    /// Generates the full ecosystem report by discovering all <see cref="WasmLanguageStrategyBase"/>
    /// subclasses, instantiating each, reading its <see cref="WasmLanguageInfo"/>, and classifying
    /// by tier based on the strategy's namespace.
    /// </summary>
    /// <returns>An <see cref="EcosystemReport"/> containing the full language catalog.</returns>
    public EcosystemReport GetEcosystemReport()
    {
        var summaries = new List<LanguageSummary>();
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
                if (Activator.CreateInstance(type) is not WasmLanguageStrategyBase strategy)
                    continue;

                var info = strategy.LanguageInfo;
                var tier = ClassifyTier(type);

                summaries.Add(new LanguageSummary(
                    Language: info.Language,
                    Tier: tier,
                    WasiSupport: info.WasiSupport,
                    Performance: info.PerformanceTier,
                    BinarySize: info.BinarySize,
                    CompileCommand: info.CompileCommand,
                    Notes: info.Notes
                ));
            }
            catch
            {

                // Skip types that cannot be instantiated
                System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
            }
        }

        // Sort by tier then by language name
        summaries.Sort((a, b) =>
        {
            var tierCompare = GetTierOrder(a.Tier).CompareTo(GetTierOrder(b.Tier));
            return tierCompare != 0 ? tierCompare : string.Compare(a.Language, b.Language, StringComparison.OrdinalIgnoreCase);
        });

        return new EcosystemReport(
            TotalLanguages: summaries.Count,
            Tier1Count: summaries.Count(s => s.Tier == "Tier 1"),
            Tier2Count: summaries.Count(s => s.Tier == "Tier 2"),
            Tier3Count: summaries.Count(s => s.Tier == "Tier 3"),
            Languages: summaries
        );
    }

    /// <summary>
    /// Generates a human-readable markdown summary of the entire WASM language ecosystem.
    /// Includes all 31 languages with their tier, WASI support, performance tier, binary size,
    /// and compilation command, formatted as a structured markdown document.
    /// </summary>
    /// <returns>A markdown-formatted string containing the complete ecosystem summary.</returns>
    public string GenerateEcosystemSummary()
    {
        var report = GetEcosystemReport();
        var sb = new StringBuilder();

        sb.AppendLine("# WASM Language Ecosystem Summary");
        sb.AppendLine();
        sb.AppendLine($"**Total Languages:** {report.TotalLanguages}");
        sb.AppendLine($"**Tier 1 (Production-Ready):** {report.Tier1Count}");
        sb.AppendLine($"**Tier 2 (Mature Toolchain):** {report.Tier2Count}");
        sb.AppendLine($"**Tier 3 (Experimental):** {report.Tier3Count}");
        sb.AppendLine();

        foreach (var tierName in new[] { "Tier 1", "Tier 2", "Tier 3" })
        {
            var tierLanguages = report.Languages.Where(l => l.Tier == tierName).ToList();
            if (tierLanguages.Count == 0) continue;

            sb.AppendLine($"## {tierName}");
            sb.AppendLine();
            sb.AppendLine("| Language | WASI Support | Performance | Binary Size | Compile Command |");
            sb.AppendLine("|----------|:------------:|:-----------:|:-----------:|-----------------|");

            foreach (var lang in tierLanguages)
            {
                sb.AppendLine($"| {lang.Language,-12} | {lang.WasiSupport,-12} | {lang.Performance,-11} | {lang.BinarySize,-11} | `{lang.CompileCommand}` |");
            }

            sb.AppendLine();

            // Notes per language
            sb.AppendLine($"### {tierName} Notes");
            sb.AppendLine();
            foreach (var lang in tierLanguages)
            {
                sb.AppendLine($"- **{lang.Language}:** {lang.Notes}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Executes the ecosystem strategy by generating the full ecosystem summary and returning
    /// it as UTF-8 bytes in the <see cref="ComputeResult"/>.
    /// </summary>
    /// <param name="task">The compute task (used for task ID tracking).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="ComputeResult"/> containing the ecosystem summary as UTF-8 output data.</returns>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            await Task.CompletedTask; // Ecosystem report is synchronous but ExecuteAsync requires async
            var summary = GenerateEcosystemSummary();
            return (EncodeOutput(summary), $"Generated ecosystem summary for {GetEcosystemReport().TotalLanguages} languages");
        }, cancellationToken);
    }

    /// <summary>
    /// Classifies a strategy type into a tier based on its namespace.
    /// </summary>
    private static string ClassifyTier(Type type)
    {
        var ns = type.Namespace ?? string.Empty;
        if (ns.Contains(".Tier1", StringComparison.OrdinalIgnoreCase)) return "Tier 1";
        if (ns.Contains(".Tier2", StringComparison.OrdinalIgnoreCase)) return "Tier 2";
        if (ns.Contains(".Tier3", StringComparison.OrdinalIgnoreCase)) return "Tier 3";
        return "Unknown";
    }

    /// <summary>
    /// Returns a sort order for tier classification strings.
    /// </summary>
    private static int GetTierOrder(string tier) => tier switch
    {
        "Tier 1" => 1,
        "Tier 2" => 2,
        "Tier 3" => 3,
        _ => 99
    };
}
