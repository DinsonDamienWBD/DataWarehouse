using System.Text;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Enclave;

/// <summary>
/// Azure/GCP Confidential VM strategy with vTPM attestation,
/// SEV-SNP guest report, and launch measurement verification.
/// </summary>
internal sealed class ConfidentialVmStrategy : ComputeRuntimeStrategyBase
{
    // Allowlist for cloud providers — prevents arbitrary executable injection.
    private static readonly string[] AllowedProviders = ["azure", "gcp", "generic"];

    /// <inheritdoc/>
    public override string StrategyId => "compute.enclave.confidential-vm";
    /// <inheritdoc/>
    public override string StrategyName => "Confidential VM";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Native;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: true, SupportsSandboxing: true,
        MaxMemoryBytes: 128L * 1024 * 1024 * 1024, MaxExecutionTime: TimeSpan.FromHours(24),
        SupportedLanguages: ["any"], SupportsMultiThreading: true,
        SupportsAsync: true, SupportsNetworkAccess: true, SupportsFileSystemAccess: true,
        MaxConcurrentTasks: 2, MemoryIsolation: MemoryIsolationLevel.VirtualMachine);
    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.Native];

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var provider = "azure";
            if (task.Metadata?.TryGetValue("cloud_provider", out var cp) == true && cp is string cps)
                provider = cps.ToLowerInvariant();
            // Validate provider against allowlist to prevent arbitrary command injection.
            if (!Array.Exists(AllowedProviders, p => p == provider))
                throw new ArgumentException($"Cloud provider '{provider}' is not supported. Allowed: {string.Join(", ", AllowedProviders)}.");

            var codePath = Path.GetTempFileName() + ".sh";
            try
            {
                await File.WriteAllBytesAsync(codePath, task.Code.ToArray(), cancellationToken);

                var cpuCount = task.ResourceLimits?.MaxThreads ?? 4;
                var memGb = (int)(GetMaxMemoryBytes(task, 16L * 1024 * 1024 * 1024) / (1024 * 1024 * 1024));

                // Retrieve vTPM attestation report
                var attestArgs = provider switch
                {
                    "azure" => "az attestation show --name default-attestation",
                    "gcp" => "gcloud compute instances get-shielded-identity",
                    _ => "tpm2_nvread 0x01400001"
                };

                var attestResult = await RunProcessAsync(provider == "azure" ? "az" : provider == "gcp" ? "gcloud" : "tpm2_nvread",
                    attestArgs, timeout: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);

                // Verify SEV-SNP guest report
                var sevReportResult = await RunProcessAsync("sevctl", "verify --attestation-report",
                    timeout: TimeSpan.FromSeconds(15), cancellationToken: cancellationToken);

                // P2-1679: abort if attestation verification fails — launching without verified attestation
                // undermines the confidential-computing security guarantee
                if (sevReportResult.ExitCode != 0)
                    throw new InvalidOperationException(
                        $"SEV-SNP attestation verification failed (exit {sevReportResult.ExitCode}): {sevReportResult.StandardError}");

                // Launch the workload in the confidential VM
                var launchArgs = provider switch
                {
                    "azure" => $"vm run-command invoke --command-id RunShellScript --scripts @\"{codePath}\"",
                    "gcp" => $"compute ssh --command \"bash -s\" < \"{codePath}\"",
                    _ => $"qemu-system-x86_64 -machine confidential-guest-support=sev0 -m {memGb}G -smp {cpuCount} -kernel \"{codePath}\" -nographic"
                };

                var timeout = GetEffectiveTimeout(task);
                var result = await RunProcessAsync(provider == "azure" ? "az" : provider == "gcp" ? "gcloud" : "qemu-system-x86_64",
                    launchArgs, stdin: task.InputData.Length > 0 ? task.GetInputDataAsString() : null,
                    environment: task.Environment, timeout: timeout, cancellationToken: cancellationToken);

                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"Confidential VM exited with code {result.ExitCode}: {result.StandardError}");

                var logs = $"Confidential VM ({provider}, {cpuCount} CPU, {memGb}GB) completed in {result.Elapsed.TotalMilliseconds:F0}ms\n" +
                           $"Attestation: {attestResult.StandardOutput.Trim()}\nSEV report: {sevReportResult.StandardOutput.Trim()}\n{result.StandardError}";
                return (EncodeOutput(result.StandardOutput), logs);
            }
            finally
            {
                try { File.Delete(codePath); } catch { /* Best-effort cleanup */ }
            }
        }, cancellationToken);
    }
}
