using System.Text;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Container;

/// <summary>
/// Podman compute strategy for rootless container execution.
/// Uses podman CLI with --memory/--cpus limits and stdin piping for code.
/// </summary>
internal sealed class PodmanStrategy : ComputeRuntimeStrategyBase
{
    // Allowlist for container names and env var keys — prevents command injection.
    private static readonly Regex SafeNameRegex = new(@"^[a-zA-Z0-9_.\-]+$", RegexOptions.Compiled);

    /// <inheritdoc/>
    public override string StrategyId => "compute.container.podman";
    /// <inheritdoc/>
    public override string StrategyName => "Podman";
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
        await IsToolAvailableAsync("podman", "--version", cancellationToken);
    }

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var image = "docker.io/library/alpine:latest";
            if (task.Metadata?.TryGetValue("image", out var img) == true && img is string imgs)
                image = imgs;

            var maxMem = GetMaxMemoryBytes(task, 512 * 1024 * 1024);
            var memMb = maxMem / (1024 * 1024);
            var cpus = task.ResourceLimits?.MaxThreads ?? 2;
            var codeStr = task.GetCodeAsString();

            var args = new StringBuilder();
            args.Append($"run --rm -i ");
            args.Append($"--memory {memMb}m ");
            args.Append($"--cpus {cpus} ");

            if (task.ResourceLimits?.AllowNetworkAccess != true)
                args.Append("--network none ");

            if (task.Environment != null)
            {
                foreach (var (key, value) in task.Environment)
                {
                    // Validate env var key to prevent command injection.
                    if (!SafeNameRegex.IsMatch(key))
                        throw new ArgumentException($"Environment variable key '{key}' contains invalid characters.");
                    args.Append($"-e \"{key}={value.Replace("\"", "\\\"")}\" ");
                }
            }

            args.Append($"{image} sh -c \"{codeStr.Replace("\"", "\\\"")}\"");

            var timeout = GetEffectiveTimeout(task);
            var result = await RunProcessAsync("podman", args.ToString(),
                stdin: task.InputData.Length > 0 ? task.GetInputDataAsString() : null,
                timeout: timeout, cancellationToken: cancellationToken);

            if (result.ExitCode != 0)
                throw new InvalidOperationException($"Podman exited with code {result.ExitCode}: {result.StandardError}");

            return (EncodeOutput(result.StandardOutput), $"Podman ({memMb}MB, {cpus} CPUs) completed in {result.Elapsed.TotalMilliseconds:F0}ms\n{result.StandardError}");
        }, cancellationToken);
    }
}
