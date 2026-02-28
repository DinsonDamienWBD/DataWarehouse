using System.Text;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Container;

/// <summary>
/// Kata Containers compute strategy using kata-runtime as an OCI runtime.
/// Provides hypervisor-based isolation with QEMU or Cloud-Hypervisor backends.
/// </summary>
internal sealed class KataContainersStrategy : ComputeRuntimeStrategyBase
{
    // Allowlist for container names — prevents command injection via name interpolation.
    private static readonly Regex ContainerNameRegex = new(@"^[a-zA-Z0-9_.\-]+$", RegexOptions.Compiled);

    /// <inheritdoc/>
    public override string StrategyId => "compute.container.kata";
    /// <inheritdoc/>
    public override string StrategyName => "Kata Containers";
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
        await IsToolAvailableAsync("kata-runtime", "--version", cancellationToken);
    }

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var hypervisor = "qemu";
            if (task.Metadata?.TryGetValue("hypervisor", out var h) == true && h is string hs)
                hypervisor = hs;

            var image = "ubuntu:latest";
            if (task.Metadata?.TryGetValue("image", out var img) == true && img is string imgs)
                image = imgs;

            var containerId = $"dw-kata-{task.Id[..Math.Min(12, task.Id.Length)]}";
            // Validate container name to prevent command injection.
            if (!ContainerNameRegex.IsMatch(containerId))
                throw new ArgumentException($"Derived container name '{containerId}' contains invalid characters.");
            var maxMem = GetMaxMemoryBytes(task, 1024 * 1024 * 1024);
            var memMb = maxMem / (1024 * 1024);

            var codeStr = task.GetCodeAsString();
            var args = new StringBuilder();
            args.Append($"run --runtime kata-{hypervisor} ");
            args.Append($"--memory {memMb}m ");
            args.Append($"--name {containerId} --rm ");
            args.Append($"{image} sh -c \"{codeStr.Replace("\"", "\\\"")}\"");

            var timeout = GetEffectiveTimeout(task);
            var result = await RunProcessAsync("docker", args.ToString(),
                stdin: task.InputData.Length > 0 ? task.GetInputDataAsString() : null,
                timeout: timeout, cancellationToken: cancellationToken);

            if (result.ExitCode != 0)
                throw new InvalidOperationException($"Kata Containers exited with code {result.ExitCode}: {result.StandardError}");

            return (EncodeOutput(result.StandardOutput), $"Kata ({hypervisor}) completed in {result.Elapsed.TotalMilliseconds:F0}ms\n{result.StandardError}");
        }, cancellationToken);
    }
}
