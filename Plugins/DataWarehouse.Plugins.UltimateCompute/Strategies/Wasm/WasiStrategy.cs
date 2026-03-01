using System.Text;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Wasm;

/// <summary>
/// Pure WASI runtime strategy using wasmtime with WASI support.
/// Provides stdin/stdout data passing and filesystem pre-opens for sandboxed I/O.
/// </summary>
internal sealed class WasiStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.wasi";

    /// <inheritdoc/>
    public override string StrategyName => "WASI";

    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.WASM;

    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: true,
        SupportsSandboxing: true,
        MaxMemoryBytes: 2L * 1024 * 1024 * 1024,
        MaxExecutionTime: TimeSpan.FromMinutes(5),
        SupportedLanguages: ["wasm", "wasi"],
        SupportsMultiThreading: false,
        SupportsAsync: true,
        SupportsNetworkAccess: false,
        SupportsFileSystemAccess: true,
        MaxConcurrentTasks: 100,
        MemoryIsolation: MemoryIsolationLevel.Sandbox
    );

    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.WASM];

    /// <inheritdoc/>
    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await IsToolAvailableAsync("wasmtime", "--version", cancellationToken);
    }

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var wasmPath = Path.GetTempFileName() + ".wasm";
            var tmpDir = Path.Combine(Path.GetTempPath(), $"wasi_{task.Id}");
            Directory.CreateDirectory(tmpDir);

            try
            {
                await File.WriteAllBytesAsync(wasmPath, task.Code.ToArray(), cancellationToken);

                var args = new StringBuilder();
                args.Append("run --wasi ");
                // Quote paths to handle spaces and special characters in directory names.
                args.Append($"--dir \"{tmpDir}\"::/tmp ");

                if (task.ResourceLimits?.AllowedFileSystemPaths != null)
                {
                    foreach (var preOpen in task.ResourceLimits.AllowedFileSystemPaths)
                        args.Append($"--dir \"{preOpen}\"::\"{preOpen}\" ");
                }

                args.Append($"\"{wasmPath}\"");

                if (task.Arguments != null)
                {
                    foreach (var arg in task.Arguments)
                        args.Append($" \"{arg.Replace("\"", "\\\"")}\"");
                }

                var timeout = GetEffectiveTimeout(task);
                var stdinData = task.InputData.Length > 0 ? task.GetInputDataAsString() : null;
                var result = await RunProcessAsync("wasmtime", args.ToString(),
                    stdin: stdinData,
                    environment: task.Environment,
                    timeout: timeout,
                    cancellationToken: cancellationToken);

                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"WASI execution failed with code {result.ExitCode}: {result.StandardError}");

                return (EncodeOutput(result.StandardOutput), $"WASI completed in {result.Elapsed.TotalMilliseconds:F0}ms\n{result.StandardError}");
            }
            finally
            {
                try { File.Delete(wasmPath); } catch { /* cleanup */ }
                try { Directory.Delete(tmpDir, recursive: true); } catch { /* cleanup */ }
            }
        }, cancellationToken);
    }
}
