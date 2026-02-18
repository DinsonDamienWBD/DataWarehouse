using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Container;

/// <summary>
/// Youki compute strategy using the Rust-based OCI container runtime.
/// Leverages cgroups v2 for resource management and provides low-overhead isolation.
/// </summary>
internal sealed class YoukiStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.container.youki";
    /// <inheritdoc/>
    public override string StrategyName => "Youki";
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
        await IsToolAvailableAsync("youki", "--version", cancellationToken);
    }

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var bundleDir = Path.Combine(Path.GetTempPath(), $"youki_{task.Id}");
            var rootfsDir = Path.Combine(bundleDir, "rootfs");
            Directory.CreateDirectory(rootfsDir);

            try
            {
                await File.WriteAllBytesAsync(Path.Combine(rootfsDir, "code"), task.Code.ToArray(), cancellationToken);

                var maxMem = GetMaxMemoryBytes(task, 512 * 1024 * 1024);
                var spec = new
                {
                    ociVersion = "1.0.0",
                    process = new
                    {
                        args = new[] { "/bin/sh", "-c", "chmod +x /code && /code" },
                        cwd = "/",
                        env = task.Environment?.Select(kv => $"{kv.Key}={kv.Value}").ToArray() ?? Array.Empty<string>()
                    },
                    root = new { path = "rootfs", @readonly = false },
                    linux = new
                    {
                        resources = new
                        {
                            memory = new { limit = maxMem },
                            unified = new Dictionary<string, string>
                            {
                                ["memory.max"] = maxMem.ToString(),
                                ["memory.swap.max"] = maxMem.ToString()
                            }
                        }
                    }
                };

                await File.WriteAllTextAsync(Path.Combine(bundleDir, "config.json"),
                    JsonSerializer.Serialize(spec, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);

                var containerId = $"dw-youki-{task.Id[..Math.Min(10, task.Id.Length)]}";
                var timeout = GetEffectiveTimeout(task);

                // Create and start container
                await RunProcessAsync("youki", $"create --bundle \"{bundleDir}\" {containerId}", timeout: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);
                var result = await RunProcessAsync("youki", $"start {containerId}",
                    stdin: task.InputData.Length > 0 ? task.GetInputDataAsString() : null,
                    timeout: timeout, cancellationToken: cancellationToken);

                // Cleanup
                try { await RunProcessAsync("youki", $"delete --force {containerId}", timeout: TimeSpan.FromSeconds(10)); } catch { /* Best-effort cleanup */ }

                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"Youki exited with code {result.ExitCode}: {result.StandardError}");

                return (EncodeOutput(result.StandardOutput), $"Youki (cgroups v2) completed in {result.Elapsed.TotalMilliseconds:F0}ms\n{result.StandardError}");
            }
            finally
            {
                try { Directory.Delete(bundleDir, true); } catch { /* Best-effort cleanup */ }
            }
        }, cancellationToken);
    }
}
