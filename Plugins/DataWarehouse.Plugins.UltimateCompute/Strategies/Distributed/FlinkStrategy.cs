using System.Text;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Distributed;

/// <summary>
/// Apache Flink distributed streaming compute strategy.
/// Uses flink CLI for job JAR submission, savepoint management, and REST API status polling.
/// </summary>
internal sealed class FlinkStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.distributed.flink";
    /// <inheritdoc/>
    public override string StrategyName => "Apache Flink";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Custom;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: true, SupportsSandboxing: false,
        MaxMemoryBytes: 64L * 1024 * 1024 * 1024, MaxExecutionTime: TimeSpan.FromHours(24),
        SupportedLanguages: ["java", "scala", "python", "sql"], SupportsMultiThreading: true,
        SupportsAsync: true, SupportsNetworkAccess: true, SupportsFileSystemAccess: true,
        MaxConcurrentTasks: 50, MemoryIsolation: MemoryIsolationLevel.Process);
    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.Custom];

    /// <inheritdoc/>
    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await IsToolAvailableAsync("flink", "--version", cancellationToken);
    }

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var jarPath = Path.GetTempFileName() + ".jar";
            try
            {
                await File.WriteAllBytesAsync(jarPath, task.Code.ToArray(), cancellationToken);

                var parallelism = task.ResourceLimits?.MaxThreads ?? 4;
                var memMb = (int)(GetMaxMemoryBytes(task, 2L * 1024 * 1024 * 1024) / (1024 * 1024));

                var args = new StringBuilder();
                args.Append($"run ");

                // P2-1693: JSON deserialization can produce JsonElement(true) or string "true",
                // not boxed bool. Use Convert.ToBoolean for robust cross-type truthiness check.
                if (task.Metadata?.TryGetValue("detached", out var d) == true
                    && (d is true || (d is string ds && string.Equals(ds, "true", StringComparison.OrdinalIgnoreCase))
                        || (d is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.True)))
                    args.Append("-d ");

                args.Append($"-p {parallelism} ");
                args.Append($"-yjm {memMb}m ");
                args.Append($"-ytm {memMb}m ");

                if (task.EntryPoint != null)
                    args.Append($"-c {task.EntryPoint} ");

                var savepointPath = task.Metadata?.TryGetValue("savepoint_path", out var sp) == true && sp is string sps ? sps : null;
                if (savepointPath != null)
                    args.Append($"-s \"{savepointPath}\" ");

                args.Append($"\"{jarPath}\"");

                if (task.Arguments != null)
                {
                    foreach (var arg in task.Arguments)
                        args.Append($" {arg}");
                }

                var timeout = GetEffectiveTimeout(task, TimeSpan.FromMinutes(30));
                var result = await RunProcessAsync("flink", args.ToString(),
                    environment: task.Environment, timeout: timeout, cancellationToken: cancellationToken);

                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"flink exited with code {result.ExitCode}: {result.StandardError}");

                // Check job status via REST
                var jobId = ExtractJobId(result.StandardOutput);
                string? statusLog = null;
                if (jobId != null)
                {
                    var statusResult = await RunProcessAsync("flink", $"list -r", timeout: TimeSpan.FromSeconds(15), cancellationToken: cancellationToken);
                    statusLog = statusResult.StandardOutput;
                }

                return (EncodeOutput(result.StandardOutput), $"Flink (parallelism={parallelism}) completed in {result.Elapsed.TotalMilliseconds:F0}ms\nJob: {jobId}\n{statusLog}\n{result.StandardError}");
            }
            finally
            {
                try { File.Delete(jarPath); } catch { /* Best-effort cleanup */ }
            }
        }, cancellationToken);
    }

    private static string? ExtractJobId(string output)
    {
        // Flink outputs "Job has been submitted with JobID <hex>"
        var marker = "JobID ";
        var idx = output.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var start = idx + marker.Length;
        var end = output.IndexOfAny([' ', '\n', '\r'], start);
        return end > start ? output[start..end] : output[start..];
    }
}
