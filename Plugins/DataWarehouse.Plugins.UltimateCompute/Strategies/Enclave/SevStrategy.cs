using System.Text;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Enclave;

/// <summary>
/// AMD SEV/SEV-ES/SEV-SNP memory encryption strategy.
/// Uses sevctl CLI for platform management and guest attestation report parsing.
/// </summary>
internal sealed class SevStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.enclave.sev";
    /// <inheritdoc/>
    public override string StrategyName => "AMD SEV/SEV-SNP";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Native;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: true, SupportsSandboxing: true,
        MaxMemoryBytes: 64L * 1024 * 1024 * 1024, MaxExecutionTime: TimeSpan.FromHours(1),
        SupportedLanguages: ["any"], SupportsMultiThreading: true,
        SupportsAsync: true, SupportsNetworkAccess: true, SupportsFileSystemAccess: true,
        MaxConcurrentTasks: 5, MemoryIsolation: MemoryIsolationLevel.VirtualMachine);
    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.Native];

    /// <inheritdoc/>
    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await IsToolAvailableAsync("sevctl", "--version", cancellationToken);
    }

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            // Verify platform supports SEV
            var platformResult = await RunProcessAsync("sevctl", "export --full", timeout: TimeSpan.FromSeconds(10), cancellationToken: cancellationToken);

            // P2-1680: abort if SEV is not available — continuing without SEV would run the guest unprotected
            if (platformResult.ExitCode != 0)
                throw new InvalidOperationException(
                    $"SEV platform check failed (exit {platformResult.ExitCode}) — SEV may not be available on this host: {platformResult.StandardError}");

            var sevMode = "sev-snp";
            if (task.Metadata?.TryGetValue("sev_mode", out var m) == true && m is string ms)
                sevMode = ms;

            // Generate the launch measurement
            var codePath = Path.GetTempFileName() + ".bin";
            try
            {
                await File.WriteAllBytesAsync(codePath, task.Code.ToArray(), cancellationToken);

                // Measure the enclave image
                var measureResult = await RunProcessAsync("sevctl", $"measurement build --api {sevMode} --firmware \"{codePath}\"",
                    timeout: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);

                // P2-1681: abort if measurement fails — launching with an unverified image is a security violation
                if (measureResult.ExitCode != 0)
                    throw new InvalidOperationException(
                        $"SEV launch measurement failed (exit {measureResult.ExitCode}) — enclave image may be untrusted: {measureResult.StandardError}");

                // Launch the guest with SEV-encrypted memory
                var qemuArgs = new StringBuilder();
                qemuArgs.Append($"-machine confidential-guest-support=sev0 ");
                qemuArgs.Append($"-object sev-guest,id=sev0,cbitpos=51,reduced-phys-bits=1,policy=0x5 ");
                qemuArgs.Append($"-m {GetMaxMemoryBytes(task, 4L * 1024 * 1024 * 1024) / (1024 * 1024)}M ");
                qemuArgs.Append($"-smp {task.ResourceLimits?.MaxThreads ?? 2} ");
                qemuArgs.Append($"-kernel \"{codePath}\" ");
                qemuArgs.Append("-nographic -no-reboot");

                var timeout = GetEffectiveTimeout(task);
                var result = await RunProcessAsync("qemu-system-x86_64", qemuArgs.ToString(),
                    stdin: task.InputData.Length > 0 ? task.GetInputDataAsString() : null,
                    timeout: timeout, cancellationToken: cancellationToken);

                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"SEV guest exited with code {result.ExitCode}: {result.StandardError}");

                var logs = $"SEV ({sevMode}) completed in {result.Elapsed.TotalMilliseconds:F0}ms\nPlatform: {platformResult.StandardOutput.Trim()}\nMeasurement: {measureResult.StandardOutput.Trim()}\n{result.StandardError}";
                return (EncodeOutput(result.StandardOutput), logs);
            }
            finally
            {
                try { File.Delete(codePath); } catch { /* Best-effort cleanup */ }
            }
        }, cancellationToken);
    }
}
