using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Container;

/// <summary>
/// gVisor compute runtime strategy using runsc as an OCI runtime.
/// Creates container specs with resource limits and supports rootless mode.
/// </summary>
internal sealed class GvisorStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.container.gvisor";
    /// <inheritdoc/>
    public override string StrategyName => "gVisor (runsc)";
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
        await IsToolAvailableAsync("runsc", "--version", cancellationToken);
    }

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var bundleDir = Path.Combine(Path.GetTempPath(), $"gvisor_{task.Id}");
            var rootfsDir = Path.Combine(bundleDir, "rootfs");
            Directory.CreateDirectory(rootfsDir);

            try
            {
                var codePath = Path.Combine(rootfsDir, "code");
                await File.WriteAllBytesAsync(codePath, task.Code.ToArray(), cancellationToken);

                var maxMem = GetMaxMemoryBytes(task, 512 * 1024 * 1024);
                var spec = new
                {
                    ociVersion = "1.0.0",
                    process = new { args = new[] { "/code" }, cwd = "/" },
                    root = new { path = "rootfs", @readonly = true },
                    linux = new
                    {
                        resources = new
                        {
                            memory = new { limit = maxMem },
                            cpu = new { shares = 1024UL }
                        }
                    }
                };

                var specPath = Path.Combine(bundleDir, "config.json");
                await File.WriteAllTextAsync(specPath, JsonSerializer.Serialize(spec), cancellationToken);

                var containerId = $"dw-{task.Id[..Math.Min(12, task.Id.Length)]}";
                var args = $"--rootless run --bundle \"{bundleDir}\" {containerId}";

                var timeout = GetEffectiveTimeout(task);
                var result = await RunProcessAsync("runsc", args,
                    stdin: task.InputData.Length > 0 ? task.GetInputDataAsString() : null,
                    timeout: timeout, cancellationToken: cancellationToken);

                // Cleanup container
                try { await RunProcessAsync("runsc", $"--rootless delete {containerId}", timeout: TimeSpan.FromSeconds(10)); } catch { /* Best-effort cleanup */ }

                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"gVisor exited with code {result.ExitCode}: {result.StandardError}");

                return (EncodeOutput(result.StandardOutput), $"gVisor (runsc) completed in {result.Elapsed.TotalMilliseconds:F0}ms\n{result.StandardError}");
            }
            finally
            {
                try { Directory.Delete(bundleDir, true); } catch { /* Best-effort cleanup */ }
            }
        }, cancellationToken);
    }
}
