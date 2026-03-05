using System.Text;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Wasm;

/// <summary>
/// WebAssembly Component Model strategy using wasmtime with the --wasm component-model flag.
/// Enables composable WASM components with typed interfaces and cross-module linking.
/// </summary>
internal sealed class WasmComponentStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.component-model";

    /// <inheritdoc/>
    public override string StrategyName => "WASM Component Model";

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
                args.Append("run --wasm component-model --wasi ");

                var maxMem = GetMaxMemoryBytes(task, 512 * 1024 * 1024);
                args.Append($"--max-memory {maxMem} ");

                args.Append($"\"{wasmPath}\"");

                if (task.EntryPoint != null)
                    args.Append($" --invoke \"{task.EntryPoint.Replace("\"", "\\\"")}\"");

                if (task.Arguments != null)
                {
                    foreach (var arg in task.Arguments)
                        args.Append($" \"{arg.Replace("\"", "\\\"")}\"");
                }

                var timeout = GetEffectiveTimeout(task);
                var result = await RunProcessAsync("wasmtime", args.ToString(),
                    stdin: task.InputData.Length > 0 ? task.GetInputDataAsString() : null,
                    environment: task.Environment,
                    timeout: timeout,
                    cancellationToken: cancellationToken);

                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"WASM Component Model execution failed with code {result.ExitCode}: {result.StandardError}");

                return (EncodeOutput(result.StandardOutput),
                    $"WASM Component Model completed in {result.Elapsed.TotalMilliseconds:F0}ms\n{result.StandardError}");
            }
            finally
            {
                try { File.Delete(wasmPath); } catch { /* cleanup */ }
            }
        }, cancellationToken);
    }
}
