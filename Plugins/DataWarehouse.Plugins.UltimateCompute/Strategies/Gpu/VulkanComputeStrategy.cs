using System.Text;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Gpu;

/// <summary>
/// Vulkan compute shader strategy with SPIR-V compilation via glslangValidator.
/// Manages descriptor sets and dispatches compute workloads.
/// </summary>
internal sealed class VulkanComputeStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.gpu.vulkan";
    /// <inheritdoc/>
    public override string StrategyName => "Vulkan Compute";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Custom;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: true, SupportsSandboxing: false,
        MaxMemoryBytes: 32L * 1024 * 1024 * 1024, MaxExecutionTime: TimeSpan.FromHours(2),
        SupportedLanguages: ["glsl", "hlsl", "spirv"], SupportsMultiThreading: true,
        SupportsAsync: true, SupportsNetworkAccess: false, SupportsFileSystemAccess: true,
        MaxConcurrentTasks: 8, MemoryIsolation: MemoryIsolationLevel.Process);
    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.Custom];

    /// <inheritdoc/>
    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await IsToolAvailableAsync("glslangValidator", "--version", cancellationToken);
    }

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var shaderPath = Path.GetTempFileName() + ".comp";
            var spirvPath = Path.GetTempFileName() + ".spv";
            try
            {
                await File.WriteAllBytesAsync(shaderPath, task.Code.ToArray(), cancellationToken);

                // Compile GLSL compute shader to SPIR-V
                var compileResult = await RunProcessAsync("glslangValidator", $"-V -S comp -o \"{spirvPath}\" \"{shaderPath}\"",
                    timeout: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);

                if (compileResult.ExitCode != 0)
                    throw new InvalidOperationException($"SPIR-V compilation failed: {compileResult.StandardError}");

                // Validate SPIR-V
                var validateResult = await RunProcessAsync("spirv-val", $"\"{spirvPath}\"",
                    timeout: TimeSpan.FromSeconds(10), cancellationToken: cancellationToken);

                // Execute via Vulkan compute host
                var workGroupX = 256;
                var workGroupY = 1;
                var workGroupZ = 1;
                if (task.Metadata?.TryGetValue("work_group_size", out var wgs) == true && wgs is int[] wgsArr && wgsArr.Length >= 3)
                {
                    workGroupX = wgsArr[0]; workGroupY = wgsArr[1]; workGroupZ = wgsArr[2];
                }

                var hostApp = task.Metadata?.TryGetValue("host_app", out var ha) == true && ha is string has ? has : "vulkan-compute-host";
                var args = $"--spirv \"{spirvPath}\" --dispatch {workGroupX},{workGroupY},{workGroupZ}";

                var timeout = GetEffectiveTimeout(task);
                var result = await RunProcessAsync(hostApp, args,
                    stdin: task.InputData.Length > 0 ? task.GetInputDataAsString() : null,
                    timeout: timeout, cancellationToken: cancellationToken);

                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"Vulkan compute exited with code {result.ExitCode}: {result.StandardError}");

                return (EncodeOutput(result.StandardOutput), $"Vulkan ({workGroupX}x{workGroupY}x{workGroupZ}) completed in {result.Elapsed.TotalMilliseconds:F0}ms\nValidation: {validateResult.StandardOutput.Trim()}\n{result.StandardError}");
            }
            finally
            {
                try { File.Delete(shaderPath); File.Delete(spirvPath); } catch { }
            }
        }, cancellationToken);
    }
}
