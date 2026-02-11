using System.Text;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Wasm;

/// <summary>
/// Wasmtime compute runtime strategy that invokes the wasmtime CLI to execute WebAssembly modules.
/// Supports WASI, memory limits via --max-memory, and stdout/stderr capture.
/// </summary>
internal sealed class WasmtimeStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.wasmtime";

    /// <inheritdoc/>
    public override string StrategyName => "Wasmtime";

    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.WASM;

    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => ComputeCapabilities.CreateWasmDefaults();

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
            try
            {
                await File.WriteAllBytesAsync(wasmPath, task.Code.ToArray(), cancellationToken);

                var args = new StringBuilder();
                args.Append($"run --wasi ");

                var maxMem = GetMaxMemoryBytes(task, 256 * 1024 * 1024);
                args.Append($"--max-memory {maxMem} ");

                if (task.ResourceLimits?.AllowFileSystemAccess == true && task.ResourceLimits.AllowedFileSystemPaths != null)
                {
                    foreach (var path in task.ResourceLimits.AllowedFileSystemPaths)
                        args.Append($"--dir {path} ");
                }

                args.Append($"\"{wasmPath}\"");

                if (task.Arguments != null)
                {
                    foreach (var arg in task.Arguments)
                        args.Append($" {arg}");
                }

                var timeout = GetEffectiveTimeout(task);
                var result = await RunProcessAsync("wasmtime", args.ToString(),
                    stdin: task.InputData.Length > 0 ? task.GetInputDataAsString() : null,
                    environment: task.Environment,
                    timeout: timeout,
                    cancellationToken: cancellationToken);

                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"wasmtime exited with code {result.ExitCode}: {result.StandardError}");

                return (EncodeOutput(result.StandardOutput), $"wasmtime completed in {result.Elapsed.TotalMilliseconds:F0}ms\n{result.StandardError}");
            }
            finally
            {
                try { File.Delete(wasmPath); } catch { /* cleanup */ }
            }
        }, cancellationToken);
    }
}
