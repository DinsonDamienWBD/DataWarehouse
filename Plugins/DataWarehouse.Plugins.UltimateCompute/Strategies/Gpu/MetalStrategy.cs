using System.Text;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Gpu;

/// <summary>
/// Apple Metal compute shader strategy via metal CLI compiler.
/// Compiles .metal to .metallib and dispatches compute via command buffers (macOS only, graceful fallback).
/// </summary>
internal sealed class MetalStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.gpu.metal";
    /// <inheritdoc/>
    public override string StrategyName => "Apple Metal";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Custom;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: true, SupportsSandboxing: false,
        MaxMemoryBytes: 32L * 1024 * 1024 * 1024, MaxExecutionTime: TimeSpan.FromHours(1),
        SupportedLanguages: ["metal", "msl"], SupportsMultiThreading: true,
        SupportsAsync: true, SupportsNetworkAccess: false, SupportsFileSystemAccess: true,
        MaxConcurrentTasks: 4, MemoryIsolation: MemoryIsolationLevel.Process);
    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.Custom];

    /// <inheritdoc/>
    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS())
            return; // Graceful: Metal only on macOS
        await IsToolAvailableAsync("xcrun", "--version", cancellationToken);
    }

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);

        if (!OperatingSystem.IsMacOS())
            return ComputeResult.CreateFailure(task.Id, "Metal compute shaders are only supported on macOS");

        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var metalPath = Path.GetTempFileName() + ".metal";
            var airPath = Path.GetTempFileName() + ".air";
            var metalLibPath = Path.GetTempFileName() + ".metallib";
            try
            {
                await File.WriteAllBytesAsync(metalPath, task.Code.ToArray(), cancellationToken);

                // Compile Metal shader to AIR (Apple Intermediate Representation)
                var compileResult = await RunProcessAsync("xcrun", $"-sdk macosx metal -c \"{metalPath}\" -o \"{airPath}\"",
                    timeout: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);

                if (compileResult.ExitCode != 0)
                    throw new InvalidOperationException($"Metal compilation failed: {compileResult.StandardError}");

                // Link into metallib
                var linkResult = await RunProcessAsync("xcrun", $"-sdk macosx metallib \"{airPath}\" -o \"{metalLibPath}\"",
                    timeout: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);

                if (linkResult.ExitCode != 0)
                    throw new InvalidOperationException($"Metal linking failed: {linkResult.StandardError}");

                // Execute via host runner (requires custom Metal host app)
                var hostApp = task.Metadata?.TryGetValue("host_app", out var ha) == true && ha is string has ? has : "metal-compute-host";
                var timeout = GetEffectiveTimeout(task);
                var result = await RunProcessAsync(hostApp, $"--metallib \"{metalLibPath}\"",
                    stdin: task.InputData.Length > 0 ? task.GetInputDataAsString() : null,
                    timeout: timeout, cancellationToken: cancellationToken);

                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"Metal compute exited with code {result.ExitCode}: {result.StandardError}");

                return (EncodeOutput(result.StandardOutput), $"Metal compute completed in {result.Elapsed.TotalMilliseconds:F0}ms\n{result.StandardError}");
            }
            finally
            {
                try { File.Delete(metalPath); File.Delete(airPath); File.Delete(metalLibPath); } catch { /* Best-effort cleanup */ }
            }
        }, cancellationToken);
    }
}
