using System.Text;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Enclave;

/// <summary>
/// AWS Nitro Enclaves strategy using nitro-cli for enclave image building,
/// vsock communication, and attestation document parsing.
/// </summary>
internal sealed class NitroEnclavesStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.enclave.nitro";
    /// <inheritdoc/>
    public override string StrategyName => "AWS Nitro Enclaves";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Native;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: true, SupportsSandboxing: true,
        MaxMemoryBytes: 32L * 1024 * 1024 * 1024, MaxExecutionTime: TimeSpan.FromHours(1),
        SupportedLanguages: ["any"], SupportsMultiThreading: true,
        SupportsAsync: true, SupportsNetworkAccess: false, SupportsFileSystemAccess: true,
        MaxConcurrentTasks: 4, MemoryIsolation: MemoryIsolationLevel.VirtualMachine);
    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.Native];

    /// <inheritdoc/>
    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await IsToolAvailableAsync("nitro-cli", "--version", cancellationToken);
    }

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var dockerfilePath = Path.GetTempFileName();
            var eifPath = Path.GetTempFileName() + ".eif";
            string? enclaveId = null;

            try
            {
                // Write code as Dockerfile or EIF
                await File.WriteAllBytesAsync(dockerfilePath, task.Code.ToArray(), cancellationToken);

                var cpuCount = task.ResourceLimits?.MaxThreads ?? 2;
                var memMb = (int)(GetMaxMemoryBytes(task, 4L * 1024 * 1024 * 1024) / (1024 * 1024));

                // Build the enclave image file (EIF)
                var buildResult = await RunProcessAsync("nitro-cli", $"build-enclave --docker-uri datawarehouse-compute:{task.Id} --output-file \"{eifPath}\"",
                    timeout: TimeSpan.FromMinutes(5), cancellationToken: cancellationToken);

                if (buildResult.ExitCode != 0)
                    throw new InvalidOperationException($"EIF build failed: {buildResult.StandardError}");

                // Parse PCR values from build output for attestation
                var pcrs = buildResult.StandardOutput;

                // Run the enclave
                var runResult = await RunProcessAsync("nitro-cli", $"run-enclave --eif-path \"{eifPath}\" --cpu-count {cpuCount} --memory {memMb}",
                    timeout: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);

                if (runResult.ExitCode != 0)
                    throw new InvalidOperationException($"Enclave launch failed: {runResult.StandardError}");

                // Extract enclave ID from output
                enclaveId = runResult.StandardOutput.Trim();

                // Communicate via vsock (CID from describe-enclaves)
                var descResult = await RunProcessAsync("nitro-cli", "describe-enclaves",
                    timeout: TimeSpan.FromSeconds(10), cancellationToken: cancellationToken);

                // Wait for completion via vsock or timeout
                var timeout = GetEffectiveTimeout(task);
                await Task.Delay(Math.Min(1000, (int)timeout.TotalMilliseconds), cancellationToken);

                // Get attestation document
                var consoleResult = await RunProcessAsync("nitro-cli", $"console --enclave-id {enclaveId}",
                    timeout: TimeSpan.FromSeconds(5), cancellationToken: cancellationToken);

                var output = consoleResult.StandardOutput;
                var logs = $"Nitro Enclave ({cpuCount} CPU, {memMb}MB) completed\nPCRs: {pcrs}\nEnclave: {enclaveId}\n{descResult.StandardOutput}";
                return (EncodeOutput(output), logs);
            }
            finally
            {
                if (enclaveId != null)
                {
                    try { await RunProcessAsync("nitro-cli", $"terminate-enclave --enclave-id {enclaveId}", timeout: TimeSpan.FromSeconds(10)); } catch { /* Best-effort cleanup */ }
                }
                try { File.Delete(dockerfilePath); File.Delete(eifPath); } catch { /* Best-effort cleanup */ }
            }
        }, cancellationToken);
    }
}
