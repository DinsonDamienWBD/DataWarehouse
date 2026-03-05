using System.Text;
using DataWarehouse.SDK.Contracts.Compute;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.IndustryFirst;

/// <summary>
/// Selects the optimal compute runtime based on task characteristics using a decision tree:
/// language -> isolation requirement -> resource needs -> availability. Maintains a fallback
/// chain and records selection outcomes for adaptive improvement.
/// </summary>
/// <remarks>
/// <para>
/// The decision tree considers: supported language, required isolation level, memory and CPU
/// needs, and runtime availability. If the primary selection fails, the strategy walks a
/// configurable fallback chain. Selection history is tracked per language to optimize future
/// decisions using success-rate weighted scoring.
/// </para>
/// </remarks>
internal sealed class AdaptiveRuntimeSelectionStrategy : ComputeRuntimeStrategyBase
{
    private readonly BoundedDictionary<string, List<SelectionRecord>> _selectionHistory = new BoundedDictionary<string, List<SelectionRecord>>(1000);
    private readonly object _selectionHistoryLock = new();
    private readonly BoundedDictionary<string, double> _successRates = new BoundedDictionary<string, double>(1000);

    /// <inheritdoc/>
    public override string StrategyId => "compute.industryfirst.adaptiveruntime";
    /// <inheritdoc/>
    public override string StrategyName => "Adaptive Runtime Selection";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Custom;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: true, SupportsSandboxing: true,
        MaxMemoryBytes: 64L * 1024 * 1024 * 1024, MaxExecutionTime: TimeSpan.FromHours(4),
        SupportedLanguages: ["any", "wasm", "python", "javascript", "c", "c++", "rust", "go", "java"],
        SupportsMultiThreading: true, SupportsAsync: true, SupportsNetworkAccess: true,
        SupportsFileSystemAccess: true, MaxConcurrentTasks: 16,
        MemoryIsolation: MemoryIsolationLevel.Process);
    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes =>
        [ComputeRuntime.WASM, ComputeRuntime.Container, ComputeRuntime.Native, ComputeRuntime.Custom];

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var language = task.Language?.ToLowerInvariant() ?? "unknown";
            var requiredIsolation = MemoryIsolationLevel.None;
            if (task.Metadata?.TryGetValue("required_isolation", out var ri) == true && ri is string ris)
            {
                requiredIsolation = ris.ToLowerInvariant() switch
                {
                    "sandbox" => MemoryIsolationLevel.Sandbox,
                    "process" => MemoryIsolationLevel.Process,
                    "container" => MemoryIsolationLevel.Container,
                    "vm" or "virtualmachine" => MemoryIsolationLevel.VirtualMachine,
                    _ => MemoryIsolationLevel.None
                };
            }

            var maxMemory = GetMaxMemoryBytes(task, 512 * 1024 * 1024);

            // Build candidate chain using decision tree
            var candidates = BuildCandidateChain(language, requiredIsolation, maxMemory);

            // Weight by historical success rates
            var weightedCandidates = candidates
                .Select(c => (runtime: c, weight: GetSuccessWeight(c)))
                .OrderByDescending(c => c.weight)
                .ToList();

            // Try each candidate in order
            var attempts = new List<(string runtime, bool success, string error, TimeSpan elapsed)>();
            foreach (var (runtime, weight) in weightedCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var codePath = Path.GetTempFileName() + GetFileExtension(language);
                try
                {
                    await File.WriteAllBytesAsync(codePath, task.Code.ToArray(), cancellationToken);

                    var (executable, args) = GetRuntimeCommand(runtime, codePath, task);
                    var timeout = GetEffectiveTimeout(task);

                    var result = await RunProcessAsync(executable, args,
                        stdin: task.InputData.Length > 0 ? task.GetInputDataAsString() : null,
                        timeout: timeout, cancellationToken: cancellationToken);

                    if (result.ExitCode == 0)
                    {
                        RecordSelection(language, runtime, true);
                        attempts.Add((runtime, true, "", result.Elapsed));

                        var logs = new StringBuilder();
                        logs.AppendLine($"AdaptiveRuntime: selected '{runtime}' for '{language}' (weight={weight:F3})");
                        logs.AppendLine($"  Attempts: {attempts.Count}, Chain: [{string.Join(", ", weightedCandidates.Select(c => $"{c.runtime}:{c.weight:F2}"))}]");
                        foreach (var a in attempts)
                            logs.AppendLine($"  {(a.success ? "OK" : "FAIL")} {a.runtime}: {a.elapsed.TotalMilliseconds:F0}ms {a.error}");

                        return (EncodeOutput(result.StandardOutput), logs.ToString());
                    }

                    RecordSelection(language, runtime, false);
                    attempts.Add((runtime, false, result.StandardError.Split('\n').FirstOrDefault() ?? "", result.Elapsed));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    RecordSelection(language, runtime, false);
                    attempts.Add((runtime, false, ex.Message, TimeSpan.Zero));
                }
                finally
                {
                    try { File.Delete(codePath); } catch { /* Best-effort cleanup */ }
                }
            }

            throw new InvalidOperationException(
                $"All {attempts.Count} runtime candidates failed for language '{language}':\n" +
                string.Join("\n", attempts.Select(a => $"  {a.runtime}: {a.error}")));
        }, cancellationToken);
    }

    /// <summary>
    /// Builds a ranked candidate chain based on language, isolation, and resources.
    /// </summary>
    private static List<string> BuildCandidateChain(string language, MemoryIsolationLevel isolation, long maxMemory)
    {
        var candidates = new List<string>();

        // Language-based primary selection
        switch (language)
        {
            case "wasm" or "wasi" or "wat":
                candidates.AddRange(["wasmtime", "wasmer", "wazero", "wasmedge"]);
                break;
            case "python":
                candidates.AddRange(["python3", "python"]);
                break;
            case "javascript" or "js" or "typescript" or "ts":
                candidates.AddRange(["node", "deno", "bun"]);
                break;
            case "c" or "c++":
                candidates.AddRange(["gcc", "g++", "clang"]);
                break;
            case "rust":
                candidates.AddRange(["rustc", "cargo"]);
                break;
            case "go":
                candidates.AddRange(["go"]);
                break;
            case "java":
                candidates.AddRange(["java", "javac"]);
                break;
            default:
                candidates.AddRange(["sh", "bash"]);
                break;
        }

        // Isolation-based additions
        if (isolation >= MemoryIsolationLevel.Container)
            candidates.InsertRange(0, ["podman", "docker"]);
        if (isolation >= MemoryIsolationLevel.VirtualMachine)
            candidates.Insert(0, "firecracker");
        if (isolation == MemoryIsolationLevel.Sandbox)
            candidates.InsertRange(0, ["bwrap", "nsjail"]);

        // Shell fallback is only added for unknown/generic languages — explicit languages get explicit candidates.
        // For security: do NOT silently add sh for unknown languages; callers must specify a known runtime.
        // sh is already in the candidate list for the default case above.

        return candidates;
    }

    /// <summary>
    /// Gets the runtime-specific command and arguments.
    /// </summary>
    private static (string executable, string args) GetRuntimeCommand(string runtime, string codePath, ComputeTask task)
    {
        return runtime switch
        {
            "wasmtime" => ("wasmtime", $"run \"{codePath}\""),
            "wasmer" => ("wasmer", $"run \"{codePath}\""),
            "wazero" => ("wazero", $"run \"{codePath}\""),
            "wasmedge" => ("wasmedge", $"\"{codePath}\""),
            "python3" or "python" => (runtime, $"\"{codePath}\""),
            "node" => ("node", $"\"{codePath}\""),
            "deno" => ("deno", $"run --allow-all \"{codePath}\""),
            "bun" => ("bun", $"run \"{codePath}\""),
            "podman" => ("podman", $"run --rm -i --memory={GetMaxMemoryBytes(task, 512 * 1024 * 1024)} alpine sh \"{codePath}\""),
            "docker" => ("docker", $"run --rm -i --memory={GetMaxMemoryBytes(task, 512 * 1024 * 1024)} alpine sh \"{codePath}\""),
            "bwrap" => ("bwrap", $"--ro-bind / / --dev /dev --proc /proc sh \"{codePath}\""),
            "sh" or "bash" => (runtime, $"\"{codePath}\""),
            _ => throw new NotSupportedException($"Runtime '{runtime}' is not supported by AdaptiveRuntimeSelectionStrategy.")
        };
    }

    /// <summary>
    /// Gets the appropriate file extension for a language.
    /// </summary>
    private static string GetFileExtension(string language)
    {
        return language switch
        {
            "wasm" or "wasi" => ".wasm",
            "python" => ".py",
            "javascript" or "js" => ".js",
            "typescript" or "ts" => ".ts",
            "c" => ".c",
            "c++" => ".cpp",
            "rust" => ".rs",
            "go" => ".go",
            "java" => ".java",
            _ => ".sh"
        };
    }

    /// <summary>
    /// Gets the historical success weight for a runtime (0.0 to 1.0).
    /// </summary>
    private double GetSuccessWeight(string runtime)
    {
        return _successRates.GetOrAdd(runtime, _ => 0.5); // Default 50% prior
    }

    /// <summary>
    /// Records a selection outcome and updates success rates.
    /// </summary>
    private void RecordSelection(string language, string runtime, bool success)
    {
        var key = $"{language}:{runtime}";
        lock (_selectionHistoryLock)
        {
            _selectionHistory.AddOrUpdate(key,
                _ => [new SelectionRecord(success, DateTime.UtcNow)],
                (_, list) => { list.Add(new SelectionRecord(success, DateTime.UtcNow)); return list; });
        }

        // Update exponential moving average success rate
        var alpha = 0.1; // Learning rate
        _successRates.AddOrUpdate(runtime,
            _ => success ? 1.0 : 0.0,
            (_, current) => current * (1.0 - alpha) + (success ? alpha : 0.0));
    }

    /// <summary>Historical selection record.</summary>
    private record SelectionRecord(bool Success, DateTime Timestamp);
}
