using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Container;

/// <summary>
/// Direct runsc (gVisor standalone) invocation as a sandbox runtime.
/// Generates OCI spec.json and applies seccomp profiles for system call filtering.
/// </summary>
internal sealed class RunscStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.container.runsc";
    /// <inheritdoc/>
    public override string StrategyName => "runsc (standalone)";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Container;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: true, SupportsSandboxing: true,
        MaxMemoryBytes: 4L * 1024 * 1024 * 1024, MaxExecutionTime: TimeSpan.FromMinutes(15),
        SupportedLanguages: ["any"], SupportsMultiThreading: true, SupportsAsync: true,
        SupportsNetworkAccess: false, SupportsFileSystemAccess: true,
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
            var bundleDir = Path.Combine(Path.GetTempPath(), $"runsc_{task.Id}");
            var rootfsDir = Path.Combine(bundleDir, "rootfs");
            Directory.CreateDirectory(rootfsDir);

            try
            {
                await File.WriteAllBytesAsync(Path.Combine(rootfsDir, "run.sh"), task.Code.ToArray(), cancellationToken);

                var maxMem = GetMaxMemoryBytes(task, 256 * 1024 * 1024);
                var seccompProfile = new
                {
                    defaultAction = "SCMP_ACT_ERRNO",
                    architectures = new[] { "SCMP_ARCH_X86_64" },
                    syscalls = new[]
                    {
                        new { names = new[] { "read", "write", "close", "fstat", "mmap", "mprotect", "munmap", "brk", "exit_group", "openat", "getpid", "clock_gettime" }, action = "SCMP_ACT_ALLOW" }
                    }
                };

                var spec = new
                {
                    ociVersion = "1.0.0",
                    process = new { args = new[] { "/bin/sh", "/run.sh" }, cwd = "/" },
                    root = new { path = "rootfs", @readonly = false },
                    linux = new
                    {
                        resources = new { memory = new { limit = maxMem } },
                        seccomp = seccompProfile
                    }
                };

                await File.WriteAllTextAsync(Path.Combine(bundleDir, "config.json"),
                    JsonSerializer.Serialize(spec, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);

                // Use full GUID to avoid ID collisions from sequential UUIDs with a shared prefix
                var containerId = $"dw-runsc-{Guid.NewGuid():N}";
                var timeout = GetEffectiveTimeout(task);
                var result = await RunProcessAsync("runsc", $"--rootless --network=none run --bundle \"{bundleDir}\" {containerId}",
                    stdin: task.InputData.Length > 0 ? task.GetInputDataAsString() : null,
                    timeout: timeout, cancellationToken: cancellationToken);

                try { await RunProcessAsync("runsc", $"--rootless delete {containerId}", timeout: TimeSpan.FromSeconds(10)); } catch { /* Best-effort cleanup */ }

                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"runsc exited with code {result.ExitCode}: {result.StandardError}");

                return (EncodeOutput(result.StandardOutput), $"runsc standalone completed in {result.Elapsed.TotalMilliseconds:F0}ms\n{result.StandardError}");
            }
            finally
            {
                try { Directory.Delete(bundleDir, true); } catch { /* Best-effort cleanup */ }
            }
        }, cancellationToken);
    }
}
