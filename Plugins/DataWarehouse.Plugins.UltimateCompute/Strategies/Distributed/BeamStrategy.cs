using System.Text;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Distributed;

/// <summary>
/// Apache Beam pipeline submission strategy supporting DirectRunner, FlinkRunner, and SparkRunner.
/// Configures the pipeline via --runner flag and standard Beam pipeline options.
/// </summary>
internal sealed class BeamStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.distributed.beam";
    /// <inheritdoc/>
    public override string StrategyName => "Apache Beam";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Custom;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: true, SupportsSandboxing: false,
        MaxMemoryBytes: 32L * 1024 * 1024 * 1024, MaxExecutionTime: TimeSpan.FromHours(8),
        SupportedLanguages: ["java", "python", "go"], SupportsMultiThreading: true,
        SupportsAsync: true, SupportsNetworkAccess: true, SupportsFileSystemAccess: true,
        MaxConcurrentTasks: 20, MemoryIsolation: MemoryIsolationLevel.Process);
    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.Custom];

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var isPython = task.Language.Contains("python", StringComparison.OrdinalIgnoreCase);
            var ext = isPython ? ".py" : ".jar";
            var codePath = Path.GetTempFileName() + ext;

            try
            {
                await File.WriteAllBytesAsync(codePath, task.Code.ToArray(), cancellationToken);

                // Validate runner against the known safe set to prevent shell metacharacter injection.
                static readonly HashSet<string> AllowedRunners = new(StringComparer.OrdinalIgnoreCase)
                {
                    "DirectRunner", "FlinkRunner", "SparkRunner", "DataflowRunner",
                    "SamzaRunner", "NemoRunner", "PrismRunner", "TwisterRunner"
                };
                var runner = "DirectRunner";
                if (task.Metadata?.TryGetValue("beam_runner", out var r) == true && r is string rs)
                {
                    if (!AllowedRunners.Contains(rs))
                        throw new ArgumentException(
                            $"Unknown Beam runner '{rs}'. Allowed: {string.Join(", ", AllowedRunners)}");
                    runner = rs;
                }

                var args = new StringBuilder();

                if (isPython)
                {
                    // P2-1682: Beam Python pipelines are executed directly as scripts, not via -m.
                    // The -m flag with apache_beam.runners.* is incorrect — those modules are not
                    // runnable entry points. The correct invocation is: python3 pipeline.py --runner=...
                    args.Append($"\"{codePath}\" ");
                    args.Append($"--runner={runner} ");
                }
                else
                {
                    args.Append($"-jar \"{codePath}\" ");
                    args.Append($"--runner={runner} ");
                }

                if (task.Metadata?.TryGetValue("beam_options", out var opts) == true && opts is Dictionary<string, object> optDict)
                {
                    foreach (var (key, value) in optDict)
                        args.Append($"--{key}={value} ");
                }

                if (task.Arguments != null)
                {
                    foreach (var arg in task.Arguments)
                        args.Append($" {arg}");
                }

                var executable = isPython ? "python3" : "java";
                var timeout = GetEffectiveTimeout(task, TimeSpan.FromMinutes(30));
                var result = await RunProcessAsync(executable, args.ToString(),
                    stdin: task.InputData.Length > 0 ? task.GetInputDataAsString() : null,
                    environment: task.Environment, timeout: timeout, cancellationToken: cancellationToken);

                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"Beam pipeline exited with code {result.ExitCode}: {result.StandardError}");

                return (EncodeOutput(result.StandardOutput), $"Beam ({runner}) completed in {result.Elapsed.TotalMilliseconds:F0}ms\n{result.StandardError}");
            }
            finally
            {
                try { File.Delete(codePath); } catch { /* Best-effort cleanup */ }
            }
        }, cancellationToken);
    }
}
