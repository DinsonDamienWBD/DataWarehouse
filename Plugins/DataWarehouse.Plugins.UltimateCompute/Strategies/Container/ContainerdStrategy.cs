using System.Text;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Container;

/// <summary>
/// containerd compute strategy using the ctr CLI for container management.
/// Supports namespaced container creation and snapshot-based execution.
/// </summary>
internal sealed class ContainerdStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.container.containerd";
    /// <inheritdoc/>
    public override string StrategyName => "containerd";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Container;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: true, SupportsSandboxing: true,
        MaxMemoryBytes: 8L * 1024 * 1024 * 1024, MaxExecutionTime: TimeSpan.FromMinutes(30),
        SupportedLanguages: ["any"], SupportsMultiThreading: true, SupportsAsync: true,
        SupportsNetworkAccess: true, SupportsFileSystemAccess: true,
        MaxConcurrentTasks: 20, MemoryIsolation: MemoryIsolationLevel.Container);
    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.Container];

    /// <inheritdoc/>
    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await IsToolAvailableAsync("ctr", "--version", cancellationToken);
    }

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var ns = "datawarehouse";
            var containerId = $"dw-ctr-{task.Id[..Math.Min(12, task.Id.Length)]}";

            var image = "docker.io/library/alpine:latest";
            if (task.Metadata?.TryGetValue("image", out var img) == true && img is string imgs)
                image = imgs;

            // Pull image if needed
            await RunProcessAsync("ctr", $"-n {ns} images pull {image}", timeout: TimeSpan.FromMinutes(5), cancellationToken: cancellationToken);

            var codeStr = task.GetCodeAsString();
            var maxMem = GetMaxMemoryBytes(task, 512 * 1024 * 1024);

            var args = new StringBuilder();
            args.Append($"-n {ns} run --rm ");
            args.Append($"--memory-limit {maxMem} ");
            args.Append($"{image} {containerId} ");
            args.Append($"sh -c \"{codeStr.Replace("\"", "\\\"")}\"");

            var timeout = GetEffectiveTimeout(task);
            var result = await RunProcessAsync("ctr", args.ToString(),
                stdin: task.InputData.Length > 0 ? task.GetInputDataAsString() : null,
                timeout: timeout, cancellationToken: cancellationToken);

            // Cleanup
            try { await RunProcessAsync("ctr", $"-n {ns} containers delete {containerId}", timeout: TimeSpan.FromSeconds(10)); } catch { }

            if (result.ExitCode != 0)
                throw new InvalidOperationException($"containerd exited with code {result.ExitCode}: {result.StandardError}");

            return (EncodeOutput(result.StandardOutput), $"containerd completed in {result.Elapsed.TotalMilliseconds:F0}ms\n{result.StandardError}");
        }, cancellationToken);
    }
}
