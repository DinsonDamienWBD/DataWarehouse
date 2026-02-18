using System.Text;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Sandbox;

/// <summary>
/// Linux Landlock LSM strategy for filesystem access control.
/// Uses landlock_create_ruleset and landlock_add_rule to restrict filesystem paths.
/// </summary>
internal sealed class LandlockStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.sandbox.landlock";
    /// <inheritdoc/>
    public override string StrategyName => "Landlock LSM";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Native;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => ComputeCapabilities.CreateNativeDefaults();
    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.Native];

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

                // Build a Landlock wrapper script that restricts filesystem access
                var allowedPaths = task.ResourceLimits?.AllowedFileSystemPaths ?? ["/tmp", "/usr", "/lib", "/bin"];
                var landlockScript = new StringBuilder();
                landlockScript.AppendLine("#!/bin/bash");
                landlockScript.AppendLine("# Landlock filesystem restriction wrapper");
                landlockScript.AppendLine("# Uses LL_FS_RO and LL_FS_RW environment variables");
                landlockScript.Append("LL_FS_RO=");
                landlockScript.AppendLine(string.Join(":", allowedPaths.Where(p => !p.StartsWith("/tmp"))));
                landlockScript.Append("LL_FS_RW=");
                landlockScript.AppendLine(string.Join(":", allowedPaths.Where(p => p.StartsWith("/tmp"))));
                landlockScript.AppendLine("export LL_FS_RO LL_FS_RW");
                landlockScript.AppendLine($"exec sandboxer sh \"{codePath}\"");

                var wrapperPath = Path.GetTempFileName() + "_ll.sh";
                await File.WriteAllTextAsync(wrapperPath, landlockScript.ToString(), cancellationToken);

                var timeout = GetEffectiveTimeout(task);
                var result = await RunProcessAsync("bash", $"\"{wrapperPath}\"",
                    stdin: task.InputData.Length > 0 ? task.GetInputDataAsString() : null,
                    environment: task.Environment,
                    timeout: timeout, cancellationToken: cancellationToken);

                try { File.Delete(wrapperPath); } catch { /* Best-effort cleanup */ }

                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"Landlock sandbox exited with code {result.ExitCode}: {result.StandardError}");

                return (EncodeOutput(result.StandardOutput), $"Landlock ({allowedPaths.Count} paths allowed) completed in {result.Elapsed.TotalMilliseconds:F0}ms\n{result.StandardError}");
            }
            finally
            {
                try { File.Delete(codePath); } catch { /* Best-effort cleanup */ }
            }
        }, cancellationToken);
    }
}
