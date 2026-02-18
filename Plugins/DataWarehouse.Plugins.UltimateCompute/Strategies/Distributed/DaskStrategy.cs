using System.Text;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Distributed;

/// <summary>
/// Dask distributed scheduler strategy for Python-based parallel computing.
/// Manages task graph serialization, worker processes, and dashboard monitoring.
/// </summary>
internal sealed class DaskStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.distributed.dask";
    /// <inheritdoc/>
    public override string StrategyName => "Dask";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Custom;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: true, SupportsSandboxing: false,
        MaxMemoryBytes: 64L * 1024 * 1024 * 1024, MaxExecutionTime: TimeSpan.FromHours(4),
        SupportedLanguages: ["python"], SupportsMultiThreading: true,
        SupportsAsync: true, SupportsNetworkAccess: true, SupportsFileSystemAccess: true,
        MaxConcurrentTasks: 50, MemoryIsolation: MemoryIsolationLevel.Process);
    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.Custom];

    /// <inheritdoc/>
    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await IsToolAvailableAsync("dask", "--version", cancellationToken);
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

                var schedulerAddr = "tcp://localhost:8786";
                if (task.Metadata?.TryGetValue("scheduler_address", out var sa) == true && sa is string sas)
                    schedulerAddr = sas;

                var nWorkers = task.ResourceLimits?.MaxThreads ?? Environment.ProcessorCount;
                var memPerWorker = GetMaxMemoryBytes(task, 4L * 1024 * 1024 * 1024) / nWorkers;
                var memLimit = $"{memPerWorker / (1024 * 1024 * 1024)}GB";

                // Wrap user code with Dask client connection
                var wrapperCode = $$"""
                    import dask
                    from dask.distributed import Client
                    import sys
                    client = Client('{{schedulerAddr}}', timeout=30)
                    print(f"Connected to Dask: {client.dashboard_link}")
                    exec(open('{{codePath.Replace("\\", "/")}}').read())
                    client.close()
                    """;

                var wrapperPath = Path.GetTempFileName() + ".py";
                await File.WriteAllTextAsync(wrapperPath, wrapperCode, cancellationToken);

                var timeout = GetEffectiveTimeout(task, TimeSpan.FromMinutes(30));
                var result = await RunProcessAsync("python3", $"\"{wrapperPath}\"",
                    stdin: task.InputData.Length > 0 ? task.GetInputDataAsString() : null,
                    environment: task.Environment, timeout: timeout, cancellationToken: cancellationToken);

                try { File.Delete(wrapperPath); } catch { /* Best-effort cleanup */ }

                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"Dask exited with code {result.ExitCode}: {result.StandardError}");

                return (EncodeOutput(result.StandardOutput), $"Dask ({schedulerAddr}, {nWorkers} workers, {memLimit}/worker) completed in {result.Elapsed.TotalMilliseconds:F0}ms\n{result.StandardError}");
            }
            finally
            {
                try { File.Delete(codePath); } catch { /* Best-effort cleanup */ }
            }
        }, cancellationToken);
    }
}
