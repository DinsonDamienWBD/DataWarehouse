using System.Text;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Sandbox;

/// <summary>
/// SELinux sandbox strategy for mandatory access control via security contexts.
/// Uses chcon/runcon to set MLS/MCS labels and confine process execution.
/// </summary>
internal sealed class SeLinuxStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.sandbox.selinux";
    /// <inheritdoc/>
    public override string StrategyName => "SELinux";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Native;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => ComputeCapabilities.CreateNativeDefaults();
    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.Native];

    /// <inheritdoc/>
    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await IsToolAvailableAsync("runcon", "--help", cancellationToken);
    }

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var codePath = Path.GetTempFileName() + ".sh";
            try
            {
                await File.WriteAllBytesAsync(codePath, task.Code.ToArray(), cancellationToken);

                // Set SELinux context on the script
                var seContext = "system_u:system_r:sandbox_t:s0";
                if (task.Metadata?.TryGetValue("selinux_context", out var ctx) == true && ctx is string ctxs)
                    seContext = ctxs;

                // Set file context
                await RunProcessAsync("chcon", $"{seContext} \"{codePath}\"", timeout: TimeSpan.FromSeconds(5), cancellationToken: cancellationToken);

                // Parse MLS/MCS label components
                var parts = seContext.Split(':');
                var seUser = parts.Length > 0 ? parts[0] : "system_u";
                var seRole = parts.Length > 1 ? parts[1] : "system_r";
                var seType = parts.Length > 2 ? parts[2] : "sandbox_t";
                var seRange = parts.Length > 3 ? parts[3] : "s0";

                var args = new StringBuilder();
                args.Append($"-u {seUser} -r {seRole} -t {seType} -l {seRange} ");
                args.Append($"sh \"{codePath}\"");

                var timeout = GetEffectiveTimeout(task);
                var result = await RunProcessAsync("runcon", args.ToString(),
                    stdin: task.InputData.Length > 0 ? task.GetInputDataAsString() : null,
                    environment: task.Environment,
                    timeout: timeout, cancellationToken: cancellationToken);

                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"SELinux sandbox exited with code {result.ExitCode}: {result.StandardError}");

                return (EncodeOutput(result.StandardOutput), $"SELinux ({seContext}) completed in {result.Elapsed.TotalMilliseconds:F0}ms\n{result.StandardError}");
            }
            finally
            {
                try { File.Delete(codePath); } catch { }
            }
        }, cancellationToken);
    }
}
