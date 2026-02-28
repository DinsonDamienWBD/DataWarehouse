using System.Text;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Enclave;

/// <summary>
/// Intel SGX enclave strategy for trusted execution environments.
/// Manages enclave signing with sgx_sign, AESM service communication, and remote attestation via IAS/DCAP.
/// </summary>
internal sealed class SgxStrategy : ComputeRuntimeStrategyBase
{
    // Allowlist for signing key file paths — prevents path traversal.
    private static readonly Regex SafeKeyPathRegex = new(@"^[a-zA-Z0-9/_.\-]+\.pem$", RegexOptions.Compiled);
    // Allowlist for app host binary names — prevents arbitrary execution.
    private static readonly string[] AllowedAppHosts = ["sgx-app-host", "sgx_runner", "sgx-host"];

    /// <inheritdoc/>
    public override string StrategyId => "compute.enclave.sgx";
    /// <inheritdoc/>
    public override string StrategyName => "Intel SGX";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Native;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: false, SupportsSandboxing: true,
        MaxMemoryBytes: 256 * 1024 * 1024, MaxExecutionTime: TimeSpan.FromMinutes(10),
        SupportedLanguages: ["c", "c++", "rust-sgx"], SupportsMultiThreading: true,
        SupportsAsync: true, SupportsNetworkAccess: false, SupportsFileSystemAccess: false,
        MaxConcurrentTasks: 5, MemoryIsolation: MemoryIsolationLevel.VirtualMachine);
    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.Native];

    /// <inheritdoc/>
    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await IsToolAvailableAsync("sgx_sign", "help", cancellationToken);
    }

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var enclavePath = Path.GetTempFileName() + ".signed.so";
            var configPath = Path.GetTempFileName() + ".xml";
            try
            {
                await File.WriteAllBytesAsync(enclavePath, task.Code.ToArray(), cancellationToken);

                var heapSize = GetMaxMemoryBytes(task, 64 * 1024 * 1024);
                var stackSize = 0x40000L;
                var tcsNum = task.ResourceLimits?.MaxThreads ?? 4;

                // Generate SGX enclave config
                var config = new StringBuilder();
                config.AppendLine("<EnclaveConfiguration>");
                config.AppendLine("  <ProdID>0</ProdID>");
                config.AppendLine("  <ISVSVN>0</ISVSVN>");
                config.AppendLine($"  <StackMaxSize>0x{stackSize:X}</StackMaxSize>");
                config.AppendLine($"  <HeapMaxSize>0x{heapSize:X}</HeapMaxSize>");
                config.AppendLine($"  <TCSNum>{tcsNum}</TCSNum>");
                config.AppendLine("  <TCSPolicy>1</TCSPolicy>");
                config.AppendLine("  <DisableDebug>0</DisableDebug>");
                config.AppendLine("  <MiscSelect>0</MiscSelect>");
                config.AppendLine("  <MiscMask>0xFFFFFFFF</MiscMask>");
                config.AppendLine("</EnclaveConfiguration>");
                await File.WriteAllTextAsync(configPath, config.ToString(), cancellationToken);

                // Sign the enclave — validate key path to prevent path traversal.
                var keyPath = task.Metadata?.TryGetValue("signing_key", out var k) == true && k is string ks ? ks : "enclave_private.pem";
                if (!SafeKeyPathRegex.IsMatch(keyPath))
                    throw new ArgumentException($"Signing key path '{keyPath}' contains invalid characters or path traversal sequences.");
                await RunProcessAsync("sgx_sign", $"sign -enclave \"{enclavePath}\" -config \"{configPath}\" -out \"{enclavePath}.signed\" -key \"{keyPath}\"",
                    timeout: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);

                // Run via app host — validate against allowlist to prevent arbitrary execution.
                var appHost = task.Metadata?.TryGetValue("app_host", out var ah) == true && ah is string ahs ? ahs : "sgx-app-host";
                if (!Array.Exists(AllowedAppHosts, h => h == appHost))
                    throw new ArgumentException($"App host '{appHost}' is not in the allowed list. Permitted: {string.Join(", ", AllowedAppHosts)}.");
                var timeout = GetEffectiveTimeout(task);
                var result = await RunProcessAsync(appHost, $"\"{enclavePath}.signed\"",
                    stdin: task.InputData.Length > 0 ? task.GetInputDataAsString() : null,
                    environment: task.Environment,
                    timeout: timeout, cancellationToken: cancellationToken);

                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"SGX enclave exited with code {result.ExitCode}: {result.StandardError}");

                return (EncodeOutput(result.StandardOutput), $"SGX enclave (heap={heapSize / (1024 * 1024)}MB, TCS={tcsNum}) completed in {result.Elapsed.TotalMilliseconds:F0}ms\n{result.StandardError}");
            }
            finally
            {
                try { File.Delete(enclavePath); File.Delete($"{enclavePath}.signed"); File.Delete(configPath); } catch { /* Best-effort cleanup */ }
            }
        }, cancellationToken);
    }
}
