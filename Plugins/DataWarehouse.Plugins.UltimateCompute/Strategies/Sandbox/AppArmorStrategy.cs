using System.Text;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Sandbox;

/// <summary>
/// AppArmor sandbox strategy that generates and loads AppArmor profiles.
/// Confines processes to mandatory access control profiles loaded via apparmor_parser.
/// </summary>
internal sealed class AppArmorStrategy : ComputeRuntimeStrategyBase
{
    // Allowlist for filesystem paths in AppArmor profiles — prevents profile injection.
    private static readonly Regex SafeProfilePathRegex = new(@"^[a-zA-Z0-9/_.\-]+/?$", RegexOptions.Compiled);

    /// <inheritdoc/>
    public override string StrategyId => "compute.sandbox.apparmor";
    /// <inheritdoc/>
    public override string StrategyName => "AppArmor";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Native;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => ComputeCapabilities.CreateNativeDefaults();
    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.Native];

    /// <inheritdoc/>
    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await IsToolAvailableAsync("apparmor_parser", "--version", cancellationToken);
    }

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var codePath = Path.GetTempFileName() + ".sh";
            var profilePath = Path.GetTempFileName() + ".apparmor";
            // AppArmor profile names are restricted to [a-zA-Z0-9_.-]. Sanitize task.Id to avoid
            // characters that would break the profile grammar (hyphens are allowed per the spec).
            var sanitizedId = Regex.Replace(task.Id, @"[^a-zA-Z0-9_.\-]", "_");
            var profileName = $"datawarehouse_compute_{sanitizedId[..Math.Min(32, sanitizedId.Length)]}";

            try
            {
                await File.WriteAllBytesAsync(codePath, task.Code.ToArray(), cancellationToken);

                var allowedPaths = task.ResourceLimits?.AllowedFileSystemPaths ?? ["/tmp/", "/usr/", "/lib/", "/bin/"];
                // Validate codePath and allowed paths to prevent AppArmor profile injection.
                if (!SafeProfilePathRegex.IsMatch(codePath))
                    throw new ArgumentException($"Code path '{codePath}' contains invalid characters for AppArmor profile.");
                var profile = new StringBuilder();
                profile.AppendLine($"#include <tunables/global>");
                profile.AppendLine($"profile {profileName} {{");
                profile.AppendLine("  #include <abstractions/base>");
                profile.AppendLine($"  {codePath} rix,");

                foreach (var path in allowedPaths)
                {
                    if (!SafeProfilePathRegex.IsMatch(path))
                        throw new ArgumentException($"Allowed path '{path}' contains invalid characters for AppArmor profile.");
                    profile.AppendLine($"  {path}** r,");
                }

                profile.AppendLine("  /tmp/** rw,");
                profile.AppendLine("  deny /proc/** w,");
                profile.AppendLine("  deny /sys/** w,");
                profile.AppendLine("  deny network,");
                profile.AppendLine("}");

                await File.WriteAllTextAsync(profilePath, profile.ToString(), cancellationToken);

                // Load the profile
                await RunProcessAsync("apparmor_parser", $"-r \"{profilePath}\"", timeout: TimeSpan.FromSeconds(10), cancellationToken: cancellationToken);

                // Execute under the profile
                var timeout = GetEffectiveTimeout(task);
                var result = await RunProcessAsync("aa-exec", $"-p {profileName} -- sh \"{codePath}\"",
                    stdin: task.InputData.Length > 0 ? task.GetInputDataAsString() : null,
                    environment: task.Environment,
                    timeout: timeout, cancellationToken: cancellationToken);

                // Remove profile
                try { await RunProcessAsync("apparmor_parser", $"-R \"{profilePath}\"", timeout: TimeSpan.FromSeconds(10)); } catch { /* Best-effort cleanup */ }

                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"AppArmor sandbox exited with code {result.ExitCode}: {result.StandardError}");

                return (EncodeOutput(result.StandardOutput), $"AppArmor ({profileName}) completed in {result.Elapsed.TotalMilliseconds:F0}ms\n{result.StandardError}");
            }
            finally
            {
                try { File.Delete(codePath); } catch { /* Best-effort cleanup */ }
                try { File.Delete(profilePath); } catch { /* Best-effort cleanup */ }
            }
        }, cancellationToken);
    }
}
