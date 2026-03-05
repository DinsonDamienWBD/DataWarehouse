using System.Text;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Wasm;

/// <summary>
/// Wazero compute runtime strategy using the Go-based wazero CLI for zero-dependency WASI execution.
/// Provides pure Go WebAssembly runtime with no CGO or external dependencies.
/// </summary>
internal sealed class WazeroStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.wazero";

    /// <inheritdoc/>
    public override string StrategyName => "Wazero";

    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.WASM;

    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => ComputeCapabilities.CreateWasmDefaults();

    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.WASM];

    /// <inheritdoc/>
    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await IsToolAvailableAsync("wazero", "version", cancellationToken);
    }

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var _wasmBase = Path.GetTempFileName(); var wasmPath = Path.ChangeExtension(_wasmBase, ".wasm"); File.Move(_wasmBase, wasmPath);
            try
            {
                await File.WriteAllBytesAsync(wasmPath, task.Code.ToArray(), cancellationToken);

                var args = new StringBuilder();
                args.Append($"run \"{wasmPath}\"");

                if (task.Arguments != null)
                {
                    foreach (var arg in task.Arguments)
                        args.Append($" {arg}");
                }

                var timeout = GetEffectiveTimeout(task);
                var result = await RunProcessAsync("wazero", args.ToString(),
                    stdin: task.InputData.Length > 0 ? task.GetInputDataAsString() : null,
                    environment: task.Environment,
                    timeout: timeout,
                    cancellationToken: cancellationToken);

                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"wazero exited with code {result.ExitCode}: {result.StandardError}");

                return (EncodeOutput(result.StandardOutput), $"wazero completed in {result.Elapsed.TotalMilliseconds:F0}ms\n{result.StandardError}");
            }
            finally
            {
                try { File.Delete(wasmPath); } catch { /* cleanup */ }
            }
        }, cancellationToken);
    }
}
