using System.Text;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Distributed;

/// <summary>
/// Apache Spark distributed compute strategy using spark-submit CLI.
/// Generates job configuration, monitors via Spark REST API, and parses logs.
/// </summary>
internal sealed class SparkStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.distributed.spark";
    /// <inheritdoc/>
    public override string StrategyName => "Apache Spark";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Custom;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: true, SupportsSandboxing: false,
        MaxMemoryBytes: 128L * 1024 * 1024 * 1024, MaxExecutionTime: TimeSpan.FromHours(8),
        SupportedLanguages: ["scala", "java", "python", "r", "sql"], SupportsMultiThreading: true,
        SupportsAsync: true, SupportsNetworkAccess: true, SupportsFileSystemAccess: true,
        MaxConcurrentTasks: 50, MemoryIsolation: MemoryIsolationLevel.Process);
    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.Custom];

    /// <inheritdoc/>
    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await IsToolAvailableAsync("spark-submit", "--version", cancellationToken);
    }

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var tmpBase = Path.GetTempFileName();
            var ext = task.Language.Contains("python", StringComparison.OrdinalIgnoreCase) ? ".py" : ".jar";
            var codePath = Path.ChangeExtension(tmpBase, ext);
            File.Move(tmpBase, codePath); // rename avoids orphaned zero-byte .tmp file

            try
            {
                await File.WriteAllBytesAsync(codePath, task.Code.ToArray(), cancellationToken);

                var master = "local[*]";
                if (task.Metadata?.TryGetValue("spark_master", out var m) == true && m is string ms)
                    master = ms;

                var driverMemory = $"{Math.Max(1, GetMaxMemoryBytes(task, 2L * 1024 * 1024 * 1024) / (1024 * 1024 * 1024))}g";
                var executorMemory = driverMemory;
                var executorCores = task.ResourceLimits?.MaxThreads ?? 2;

                var args = new StringBuilder();
                args.Append($"--master {master} ");
                args.Append($"--driver-memory {driverMemory} ");
                args.Append($"--executor-memory {executorMemory} ");
                args.Append($"--executor-cores {executorCores} ");
                args.Append($"--name dw-compute-{task.Id[..Math.Min(12, task.Id.Length)]} ");
                args.Append($"--conf spark.ui.port=0 ");
                args.Append($"--conf spark.driver.bindAddress=127.0.0.1 ");

                if (task.Metadata?.TryGetValue("spark_conf", out var conf) == true && conf is Dictionary<string, object> confDict)
                {
                    foreach (var (key, value) in confDict)
                        args.Append($"--conf {key}={value} ");
                }

                args.Append($"\"{codePath}\"");

                if (task.Arguments != null)
                {
                    foreach (var arg in task.Arguments)
                        args.Append($" {arg}");
                }

                var timeout = GetEffectiveTimeout(task, TimeSpan.FromMinutes(30));
                var result = await RunProcessAsync("spark-submit", args.ToString(),
                    stdin: task.InputData.Length > 0 ? task.GetInputDataAsString() : null,
                    environment: task.Environment,
                    timeout: timeout, cancellationToken: cancellationToken);

                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"spark-submit exited with code {result.ExitCode}: {result.StandardError}");

                return (EncodeOutput(result.StandardOutput), $"Spark ({master}, {driverMemory} driver) completed in {result.Elapsed.TotalMilliseconds:F0}ms\n{result.StandardError}");
            }
            finally
            {
                try { File.Delete(codePath); } catch { /* Best-effort cleanup */ }
            }
        }, cancellationToken);
    }
}
