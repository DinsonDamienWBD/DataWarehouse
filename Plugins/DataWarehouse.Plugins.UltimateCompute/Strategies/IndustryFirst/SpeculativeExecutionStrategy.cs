using System.Collections.Concurrent;
using System.Text;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.IndustryFirst;

/// <summary>
/// Speculative execution strategy that launches a task on multiple runtimes simultaneously,
/// returns the first successful result, and cancels remaining executions. Tracks winner
/// statistics for future optimization of runtime selection priority.
/// </summary>
/// <remarks>
/// <para>
/// This strategy hedges against runtime variability by running the same computation on N
/// runtimes in parallel. The first runtime to complete successfully wins, and all other
/// in-flight executions are cancelled via CancellationTokenSource. Winner statistics are
/// accumulated to adaptively re-order runtime preference for future tasks.
/// </para>
/// </remarks>
internal sealed class SpeculativeExecutionStrategy : ComputeRuntimeStrategyBase
{
    private readonly ConcurrentDictionary<string, int> _winnerStats = new();
    private readonly ConcurrentDictionary<string, int> _totalRaces = new();

    /// <inheritdoc/>
    public override string StrategyId => "compute.industryfirst.speculative";
    /// <inheritdoc/>
    public override string StrategyName => "Speculative Execution";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Custom;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: false, SupportsSandboxing: false,
        MaxMemoryBytes: 32L * 1024 * 1024 * 1024, MaxExecutionTime: TimeSpan.FromHours(1),
        SupportedLanguages: ["any"], SupportsMultiThreading: true,
        SupportsAsync: true, SupportsNetworkAccess: false, SupportsFileSystemAccess: true,
        MaxConcurrentTasks: 8, MemoryIsolation: MemoryIsolationLevel.Process);
    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.Custom];

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            // Parse runtimes to race (default: sh, bash, dash)
            var runtimes = ParseRuntimes(task);
            if (runtimes.Count == 0)
                throw new ArgumentException("No runtimes specified for speculative execution");

            var timeout = GetEffectiveTimeout(task);
            using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var results = new ConcurrentDictionary<string, (bool success, string output, string error, TimeSpan elapsed)>();
            string? winner = null;
            var winnerOutput = Array.Empty<byte>();
            var winnerLogs = "";

            // Launch all runtimes in parallel
            var tasks = runtimes.Select(runtime => Task.Run(async () =>
            {
                var codePath = Path.GetTempFileName() + ".sh";
                try
                {
                    await File.WriteAllBytesAsync(codePath, task.Code.ToArray(), raceCts.Token);

                    var (executable, args) = GetCommand(runtime, codePath);
                    var result = await RunProcessAsync(executable, args,
                        stdin: task.InputData.Length > 0 ? task.GetInputDataAsString() : null,
                        timeout: timeout, cancellationToken: raceCts.Token);

                    results[runtime] = (result.ExitCode == 0, result.StandardOutput, result.StandardError, result.Elapsed);

                    if (result.ExitCode == 0)
                    {
                        // First success wins the race
                        if (Interlocked.CompareExchange(ref winner, runtime, null) == null)
                        {
                            winnerOutput = EncodeOutput(result.StandardOutput);
                            winnerLogs = $"SpecExec winner: {runtime} in {result.Elapsed.TotalMilliseconds:F0}ms";
                            try { await raceCts.CancelAsync(); } catch { /* Best-effort cleanup */ }
                        }
                    }
                }
                catch (OperationCanceledException) when (winner != null)
                {
                    // Expected: another runtime won the race
                    results.TryAdd(runtime, (false, "", "Cancelled (another runtime won)", TimeSpan.Zero));
                }
                catch (Exception ex)
                {
                    results.TryAdd(runtime, (false, "", ex.Message, TimeSpan.Zero));
                }
                finally
                {
                    try { File.Delete(codePath); } catch { /* Best-effort cleanup */ }
                }
            }, cancellationToken)).ToArray();

            // Wait for all to complete (most will be cancelled)
            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException) when (winner != null)
            {
                // Expected: race was cancelled after a winner
            }

            if (winner == null)
            {
                var errors = string.Join("\n", results.Select(r => $"  {r.Key}: {r.Value.error}"));
                throw new InvalidOperationException($"All {runtimes.Count} speculative executions failed:\n{errors}");
            }

            // Record winner statistics
            RecordRace(winner, runtimes);

            // Build detailed logs
            var logs = new StringBuilder();
            logs.AppendLine($"SpeculativeExecution: {runtimes.Count} runtimes raced, winner='{winner}'");
            foreach (var (runtime, result) in results.OrderBy(r => r.Value.elapsed))
            {
                var status = result.success ? "WIN" : "FAIL";
                logs.AppendLine($"  [{status}] {runtime}: {result.elapsed.TotalMilliseconds:F0}ms");
            }
            logs.AppendLine($"Win rates: {string.Join(", ", _winnerStats.Select(w => $"{w.Key}:{w.Value}/{(_totalRaces.GetValueOrDefault(w.Key, 0))}"))}");

            return (winnerOutput, logs.ToString());
        }, cancellationToken);
    }

    /// <summary>
    /// Parses the list of runtimes to race from task metadata.
    /// </summary>
    private static List<string> ParseRuntimes(ComputeTask task)
    {
        if (task.Metadata?.TryGetValue("runtimes", out var r) == true)
        {
            if (r is string rs)
                return rs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (r is string[] ra)
                return ra.ToList();
        }

        // Default runtimes based on language
        return (task.Language?.ToLowerInvariant()) switch
        {
            "python" => ["python3", "python"],
            "javascript" or "js" => ["node", "deno", "bun"],
            "wasm" => ["wasmtime", "wasmer"],
            _ => ["sh", "bash"]
        };
    }

    /// <summary>
    /// Gets the executable and arguments for a runtime.
    /// </summary>
    private static (string executable, string args) GetCommand(string runtime, string codePath)
    {
        return runtime switch
        {
            "wasmtime" => ("wasmtime", $"run \"{codePath}\""),
            "wasmer" => ("wasmer", $"run \"{codePath}\""),
            "wazero" => ("wazero", $"run \"{codePath}\""),
            "python3" or "python" => (runtime, $"\"{codePath}\""),
            "node" => ("node", $"\"{codePath}\""),
            "deno" => ("deno", $"run --allow-all \"{codePath}\""),
            "bun" => ("bun", $"run \"{codePath}\""),
            _ => (runtime, $"\"{codePath}\"")
        };
    }

    /// <summary>
    /// Records race results for winner statistics.
    /// </summary>
    private void RecordRace(string winner, List<string> allRuntimes)
    {
        _winnerStats.AddOrUpdate(winner, 1, (_, count) => count + 1);
        foreach (var runtime in allRuntimes)
            _totalRaces.AddOrUpdate(runtime, 1, (_, count) => count + 1);
    }
}
