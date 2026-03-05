using System.Text;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Sandbox;

/// <summary>
/// Bubblewrap (bwrap) sandbox strategy for unprivileged user-namespace isolation.
/// Uses --ro-bind, --tmpfs, --proc, --dev options for fine-grained filesystem control.
/// </summary>
internal sealed class BubbleWrapStrategy : ComputeRuntimeStrategyBase
{
    // Allowlist for bind-mount paths — prevents path injection via bwrap arguments.
    private static readonly Regex SafeBindPathRegex = new(@"^[a-zA-Z0-9/_.\-]+$", RegexOptions.Compiled);

    /// <inheritdoc/>
    public override string StrategyId => "compute.sandbox.bwrap";
    /// <inheritdoc/>
    public override string StrategyName => "Bubblewrap (bwrap)";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Native;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => ComputeCapabilities.CreateNativeDefaults();
    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.Native];

    /// <inheritdoc/>
    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await IsToolAvailableAsync("bwrap", "--version", cancellationToken);
    }

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var codePath = Path.GetTempFileName() + ".sh";
            var tmpDir = Path.Combine(Path.GetTempPath(), $"bwrap_{task.Id}");
            Directory.CreateDirectory(tmpDir);

            try
            {
                await File.WriteAllBytesAsync(codePath, task.Code.ToArray(), cancellationToken);

                var args = new StringBuilder();
                // Minimal filesystem bindings
                args.Append("--ro-bind /usr /usr ");
                args.Append("--ro-bind /lib /lib ");
                args.Append("--ro-bind /lib64 /lib64 ");
                args.Append("--ro-bind /bin /bin ");
                args.Append("--ro-bind /sbin /sbin ");
                args.Append("--ro-bind /etc/alternatives /etc/alternatives ");
                args.Append("--ro-bind /etc/ld.so.cache /etc/ld.so.cache ");

                // Sandboxed directories
                args.Append("--tmpfs /tmp ");
                args.Append("--proc /proc ");
                args.Append("--dev /dev ");

                // Bind code file
                args.Append($"--ro-bind \"{codePath}\" /code.sh ");

                // Optional additional paths — validate to prevent path injection.
                if (task.ResourceLimits?.AllowedFileSystemPaths != null)
                {
                    foreach (var path in task.ResourceLimits.AllowedFileSystemPaths)
                    {
                        if (!SafeBindPathRegex.IsMatch(path))
                            throw new ArgumentException($"Bind-mount path '{path}' contains invalid characters.");
                        args.Append($"--bind \"{path}\" \"{path}\" ");
                    }
                }

                // Unshare namespaces
                args.Append("--unshare-all ");

                if (task.ResourceLimits?.AllowNetworkAccess != true)
                    args.Append("--unshare-net ");

                args.Append("--die-with-parent ");
                args.Append("-- sh /code.sh");

                var timeout = GetEffectiveTimeout(task);
                var result = await RunProcessAsync("bwrap", args.ToString(),
                    stdin: task.InputData.Length > 0 ? task.GetInputDataAsString() : null,
                    environment: task.Environment,
                    timeout: timeout, cancellationToken: cancellationToken);

                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"Bubblewrap exited with code {result.ExitCode}: {result.StandardError}");

                return (EncodeOutput(result.StandardOutput), $"bwrap sandbox completed in {result.Elapsed.TotalMilliseconds:F0}ms\n{result.StandardError}");
            }
            finally
            {
                try { File.Delete(codePath); } catch { /* Best-effort cleanup */ }
                try { Directory.Delete(tmpDir, true); } catch { /* Best-effort cleanup */ }
            }
        }, cancellationToken);
    }
}
