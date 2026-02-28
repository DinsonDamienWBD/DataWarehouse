using System.Linq;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Sandbox;

/// <summary>
/// Seccomp BPF sandbox strategy that generates and applies syscall filter profiles.
/// Uses --security-opt seccomp=profile.json to restrict system calls to an allowlist.
/// </summary>
internal sealed class SeccompStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.sandbox.seccomp";
    /// <inheritdoc/>
    public override string StrategyName => "Seccomp BPF";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Native;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => ComputeCapabilities.CreateNativeDefaults();
    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.Native];

    private static readonly string[] DefaultAllowedSyscalls =
    [
        "read", "write", "open", "close", "stat", "fstat", "lstat", "poll", "lseek",
        "mmap", "mprotect", "munmap", "brk", "ioctl", "access", "pipe", "dup", "dup2",
        "clone", "execve", "exit", "wait4", "kill", "uname", "fcntl", "flock",
        "getpid", "getuid", "getgid", "geteuid", "getegid", "getppid",
        "arch_prctl", "set_tid_address", "set_robust_list", "futex",
        "clock_gettime", "clock_getres", "exit_group", "openat", "newfstatat",
        "getrandom", "rt_sigaction", "rt_sigprocmask", "sigaltstack", "pread64", "pwrite64"
    ];

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var profilePath = Path.GetTempFileName() + ".json";
            var codePath = Path.GetTempFileName() + ".sh";

            try
            {
                var allowedSyscalls = DefaultAllowedSyscalls;
                if (task.Metadata?.TryGetValue("allowed_syscalls", out var sc) == true && sc != null)
                {
                    // Accept string[], List<string>, or any IEnumerable<string> to avoid silent cast failure.
                    allowedSyscalls = sc switch
                    {
                        string[] arr => arr,
                        System.Collections.Generic.IEnumerable<string> enumerable => enumerable.ToArray(),
                        _ => DefaultAllowedSyscalls
                    };
                }

                var profile = new
                {
                    defaultAction = "SCMP_ACT_ERRNO",
                    architectures = new[] { "SCMP_ARCH_X86_64", "SCMP_ARCH_X86", "SCMP_ARCH_AARCH64" },
                    syscalls = new[]
                    {
                        new { names = allowedSyscalls, action = "SCMP_ACT_ALLOW" }
                    }
                };

                await File.WriteAllTextAsync(profilePath, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
                await File.WriteAllBytesAsync(codePath, task.Code.ToArray(), cancellationToken);

                var image = "alpine:latest";
                if (task.Metadata?.TryGetValue("image", out var img) == true && img is string imgs)
                    image = imgs;

                var args = new StringBuilder();
                args.Append($"run --rm -i ");
                args.Append($"--security-opt seccomp={profilePath} ");
                args.Append($"-v {codePath}:/code.sh:ro ");
                args.Append($"{image} sh /code.sh");

                var timeout = GetEffectiveTimeout(task);
                var result = await RunProcessAsync("docker", args.ToString(),
                    stdin: task.InputData.Length > 0 ? task.GetInputDataAsString() : null,
                    timeout: timeout, cancellationToken: cancellationToken);

                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"Seccomp sandbox exited with code {result.ExitCode}: {result.StandardError}");

                return (EncodeOutput(result.StandardOutput), $"Seccomp ({allowedSyscalls.Length} syscalls allowed) completed in {result.Elapsed.TotalMilliseconds:F0}ms\n{result.StandardError}");
            }
            finally
            {
                try { File.Delete(profilePath); } catch { /* Best-effort cleanup */ }
                try { File.Delete(codePath); } catch { /* Best-effort cleanup */ }
            }
        }, cancellationToken);
    }
}
