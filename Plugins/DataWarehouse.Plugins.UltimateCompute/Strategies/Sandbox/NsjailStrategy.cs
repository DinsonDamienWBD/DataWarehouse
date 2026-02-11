using System.Text;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Sandbox;

/// <summary>
/// nsjail sandbox strategy for process isolation with cgroup, namespace, and seccomp layering.
/// Generates protobuf-style configuration for comprehensive sandboxing.
/// </summary>
internal sealed class NsjailStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.sandbox.nsjail";
    /// <inheritdoc/>
    public override string StrategyName => "nsjail";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Native;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => ComputeCapabilities.CreateNativeDefaults();
    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.Native];

    /// <inheritdoc/>
    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await IsToolAvailableAsync("nsjail", "--help", cancellationToken);
    }

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var codePath = Path.GetTempFileName() + ".sh";
            var configPath = Path.GetTempFileName() + ".cfg";

            try
            {
                await File.WriteAllBytesAsync(codePath, task.Code.ToArray(), cancellationToken);

                var maxMem = GetMaxMemoryBytes(task, 256 * 1024 * 1024);
                var timeoutSec = (int)GetEffectiveTimeout(task).TotalSeconds;

                // Generate nsjail protobuf text config
                var config = new StringBuilder();
                config.AppendLine("name: \"datawarehouse-compute\"");
                config.AppendLine("mode: ONCE");
                config.AppendLine($"time_limit: {timeoutSec}");
                config.AppendLine("rlimit_as_type: HARD");
                config.AppendLine($"rlimit_as: {maxMem / (1024 * 1024)}");
                config.AppendLine("rlimit_fsize: 64");
                config.AppendLine("rlimit_nofile: 32");
                config.AppendLine("rlimit_nproc: 1");
                config.AppendLine("clone_newnet: true");
                config.AppendLine("clone_newuser: true");
                config.AppendLine("clone_newns: true");
                config.AppendLine("clone_newpid: true");
                config.AppendLine("clone_newipc: true");
                config.AppendLine("clone_newuts: true");
                config.AppendLine("clone_newcgroup: true");
                config.AppendLine("mount { src: \"/usr\" dst: \"/usr\" is_bind: true rw: false }");
                config.AppendLine("mount { src: \"/lib\" dst: \"/lib\" is_bind: true rw: false }");
                config.AppendLine("mount { src: \"/lib64\" dst: \"/lib64\" is_bind: true rw: false }");
                config.AppendLine("mount { src: \"/bin\" dst: \"/bin\" is_bind: true rw: false }");
                config.AppendLine("mount { src: \"/sbin\" dst: \"/sbin\" is_bind: true rw: false }");
                config.AppendLine($"mount {{ src: \"{codePath}\" dst: \"/code.sh\" is_bind: true rw: false }}");
                config.AppendLine("mount { dst: \"/tmp\" fstype: \"tmpfs\" rw: true }");
                config.AppendLine("mount { dst: \"/proc\" fstype: \"proc\" rw: false }");
                config.AppendLine("mount { dst: \"/dev\" fstype: \"tmpfs\" rw: true }");
                config.AppendLine("seccomp_string: \"ALLOW { read, write, open, close, stat, fstat, lstat, mmap, mprotect, munmap, brk, exit_group, openat, getpid, clock_gettime, getrandom, rt_sigaction, rt_sigprocmask, execve, wait4, pipe, dup2, fcntl, arch_prctl, set_tid_address, set_robust_list, futex, sigaltstack, newfstatat, pread64, pwrite64 }\"");
                config.AppendLine("exec_bin { path: \"/bin/sh\" arg: \"/code.sh\" }");

                await File.WriteAllTextAsync(configPath, config.ToString(), cancellationToken);

                var result = await RunProcessAsync("nsjail", $"--config \"{configPath}\"",
                    stdin: task.InputData.Length > 0 ? task.GetInputDataAsString() : null,
                    timeout: TimeSpan.FromSeconds(timeoutSec + 5),
                    cancellationToken: cancellationToken);

                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"nsjail exited with code {result.ExitCode}: {result.StandardError}");

                return (EncodeOutput(result.StandardOutput), $"nsjail sandbox completed in {result.Elapsed.TotalMilliseconds:F0}ms\n{result.StandardError}");
            }
            finally
            {
                try { File.Delete(codePath); } catch { }
                try { File.Delete(configPath); } catch { }
            }
        }, cancellationToken);
    }
}
