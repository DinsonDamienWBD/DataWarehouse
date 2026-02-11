using System.Text;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Distributed;

/// <summary>
/// Ray distributed compute strategy for actor/task scheduling.
/// Uses ray CLI for job submission API and object store for I/O.
/// </summary>
internal sealed class RayStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.distributed.ray";
    /// <inheritdoc/>
    public override string StrategyName => "Ray";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Custom;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: true, SupportsSandboxing: false,
        MaxMemoryBytes: 64L * 1024 * 1024 * 1024, MaxExecutionTime: TimeSpan.FromHours(8),
        SupportedLanguages: ["python", "java", "c++"], SupportsMultiThreading: true,
        SupportsAsync: true, SupportsNetworkAccess: true, SupportsFileSystemAccess: true,
        MaxConcurrentTasks: 100, MemoryIsolation: MemoryIsolationLevel.Process);
    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.Custom];

    /// <inheritdoc/>
    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await IsToolAvailableAsync("ray", "--version", cancellationToken);
    }

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var codePath = Path.GetTempFileName() + ".py";
            try
            {
                await File.WriteAllBytesAsync(codePath, task.Code.ToArray(), cancellationToken);

                var rayAddress = "auto";
                if (task.Metadata?.TryGetValue("ray_address", out var ra) == true && ra is string ras)
                    rayAddress = ras;

                var numCpus = task.ResourceLimits?.MaxThreads ?? Environment.ProcessorCount;
                var objectStoreMemory = GetMaxMemoryBytes(task, 2L * 1024 * 1024 * 1024);

                var args = new StringBuilder();
                args.Append("job submit ");
                args.Append($"--address {rayAddress} ");
                args.Append($"--working-dir . ");

                if (task.Metadata?.TryGetValue("runtime_env", out var re) == true && re is string res)
                    args.Append($"--runtime-env-json '{res}' ");

                args.Append($"-- python3 \"{codePath}\"");

                if (task.Arguments != null)
                {
                    foreach (var arg in task.Arguments)
                        args.Append($" {arg}");
                }

                var timeout = GetEffectiveTimeout(task, TimeSpan.FromMinutes(30));
                var result = await RunProcessAsync("ray", args.ToString(),
                    stdin: task.InputData.Length > 0 ? task.GetInputDataAsString() : null,
                    environment: task.Environment, timeout: timeout, cancellationToken: cancellationToken);

                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"Ray job exited with code {result.ExitCode}: {result.StandardError}");

                return (EncodeOutput(result.StandardOutput), $"Ray ({rayAddress}, {numCpus} CPUs) completed in {result.Elapsed.TotalMilliseconds:F0}ms\n{result.StandardError}");
            }
            finally
            {
                try { File.Delete(codePath); } catch { }
            }
        }, cancellationToken);
    }
}
