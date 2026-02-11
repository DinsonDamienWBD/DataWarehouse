using System.Text;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Enclave;

/// <summary>
/// ARM TrustZone OP-TEE strategy for secure world execution.
/// Communicates with tee-supplicant and invokes trusted applications via /dev/tee0.
/// </summary>
internal sealed class TrustZoneStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.enclave.trustzone";
    /// <inheritdoc/>
    public override string StrategyName => "ARM TrustZone (OP-TEE)";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Native;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: false, SupportsSandboxing: true,
        MaxMemoryBytes: 32 * 1024 * 1024, MaxExecutionTime: TimeSpan.FromMinutes(5),
        SupportedLanguages: ["c", "c++"], SupportsMultiThreading: false,
        SupportsAsync: true, SupportsNetworkAccess: false, SupportsFileSystemAccess: false,
        MaxConcurrentTasks: 2, MemoryIsolation: MemoryIsolationLevel.VirtualMachine);
    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.Native];

    /// <inheritdoc/>
    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await IsToolAvailableAsync("tee-supplicant", "--help", cancellationToken);
    }

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var taPath = Path.GetTempFileName() + ".ta";
            try
            {
                await File.WriteAllBytesAsync(taPath, task.Code.ToArray(), cancellationToken);

                // Extract UUID from metadata or generate
                var taUuid = task.Metadata?.TryGetValue("ta_uuid", out var u) == true && u is string us
                    ? us
                    : Guid.NewGuid().ToString();

                // Copy TA to the standard OP-TEE directory
                var taDir = "/lib/optee_armtz";
                var destPath = Path.Combine(taDir, $"{taUuid}.ta");

                var args = new StringBuilder();
                args.Append($"--ta-dir {taDir} ");
                args.Append($"invoke {taUuid} ");

                if (task.EntryPoint != null)
                    args.Append($"--cmd {task.EntryPoint} ");

                // Pass input data as hex for secure world communication
                if (task.InputData.Length > 0)
                {
                    var inputHex = Convert.ToHexString(task.InputData.ToArray());
                    args.Append($"--input {inputHex} ");
                }

                var timeout = GetEffectiveTimeout(task, TimeSpan.FromMinutes(2));
                var result = await RunProcessAsync("optee_example_ta_invoke", args.ToString(),
                    timeout: timeout, cancellationToken: cancellationToken);

                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"TrustZone TA exited with code {result.ExitCode}: {result.StandardError}");

                return (EncodeOutput(result.StandardOutput), $"TrustZone OP-TEE (UUID={taUuid}) completed in {result.Elapsed.TotalMilliseconds:F0}ms\n{result.StandardError}");
            }
            finally
            {
                try { File.Delete(taPath); } catch { }
            }
        }, cancellationToken);
    }
}
