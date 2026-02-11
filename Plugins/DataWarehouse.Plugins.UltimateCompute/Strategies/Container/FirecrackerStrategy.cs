using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Container;

/// <summary>
/// Firecracker microVM compute strategy using firectl or firecracker-ctr.
/// Creates lightweight microVMs with configurable vCPU and memory via API socket communication.
/// </summary>
internal sealed class FirecrackerStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.container.firecracker";
    /// <inheritdoc/>
    public override string StrategyName => "Firecracker";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Container;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: true, SupportsSandboxing: true,
        MaxMemoryBytes: 16L * 1024 * 1024 * 1024, MaxExecutionTime: TimeSpan.FromHours(1),
        SupportedLanguages: ["any"], SupportsMultiThreading: true, SupportsAsync: true,
        SupportsNetworkAccess: true, SupportsFileSystemAccess: true,
        MaxConcurrentTasks: 10, MemoryIsolation: MemoryIsolationLevel.VirtualMachine);
    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.Container];

    /// <inheritdoc/>
    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await IsToolAvailableAsync("firectl", "--help", cancellationToken);
    }

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var socketPath = Path.Combine(Path.GetTempPath(), $"fc_{task.Id}.sock");
            var vcpuCount = task.ResourceLimits?.MaxThreads ?? 2;
            var memMb = (int)(GetMaxMemoryBytes(task, 512 * 1024 * 1024) / (1024 * 1024));

            var kernelImage = "vmlinux";
            if (task.Metadata?.TryGetValue("kernel_image", out var ki) == true && ki is string kis)
                kernelImage = kis;

            var rootDrive = "rootfs.ext4";
            if (task.Metadata?.TryGetValue("root_drive", out var rd) == true && rd is string rds)
                rootDrive = rds;

            var args = new StringBuilder();
            args.Append($"--socket-path=\"{socketPath}\" ");
            args.Append($"--kernel=\"{kernelImage}\" ");
            args.Append($"--root-drive=\"{rootDrive}\" ");
            args.Append($"--vcpu-count={vcpuCount} ");
            args.Append($"--mem-size-mib={memMb} ");
            args.Append("--kernel-opts=\"console=ttyS0 reboot=k panic=1 pci=off\" ");

            var timeout = GetEffectiveTimeout(task, TimeSpan.FromMinutes(10));
            var result = await RunProcessAsync("firectl", args.ToString(),
                stdin: task.InputData.Length > 0 ? task.GetInputDataAsString() : null,
                timeout: timeout, cancellationToken: cancellationToken);

            try { File.Delete(socketPath); } catch { }

            if (result.ExitCode != 0)
                throw new InvalidOperationException($"Firecracker exited with code {result.ExitCode}: {result.StandardError}");

            return (EncodeOutput(result.StandardOutput), $"Firecracker microVM ({vcpuCount} vCPU, {memMb}MB) completed in {result.Elapsed.TotalMilliseconds:F0}ms\n{result.StandardError}");
        }, cancellationToken);
    }
}
