using System.Text;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Wasm;

/// <summary>
/// WasmEdge compute runtime strategy for WASI and WASI-NN ML inference.
/// Supports TensorFlow and PyTorch plugin backends for machine learning workloads.
/// </summary>
internal sealed class WasmEdgeStrategy : ComputeRuntimeStrategyBase
{
    // Allowlist for WASI-NN backends — prevents injection via backend flag.
    private static readonly string[] AllowedNnBackends = ["TensorFlow", "TensorFlowLite", "PyTorch", "OpenVINO", "ONNX"];

    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.wasmedge";

    /// <inheritdoc/>
    public override string StrategyName => "WasmEdge";

    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.WASM;

    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => ComputeCapabilities.CreateWasmDefaults();

    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.WASM];

    /// <inheritdoc/>
    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await IsToolAvailableAsync("wasmedge", "--version", cancellationToken);
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

                // Enable WASI-NN if specified in metadata
                var enableNn = task.Metadata?.TryGetValue("enable_nn", out var nn) == true && nn is true;
                if (enableNn)
                {
                    var nnBackend = "TensorFlow";
                    if (task.Metadata?.TryGetValue("nn_backend", out var nnb) == true && nnb is string nnbs)
                        nnBackend = nnbs;
                    // Validate backend against allowlist to prevent argument injection.
                    if (!Array.Exists(AllowedNnBackends, b => b == nnBackend))
                        throw new ArgumentException($"WASI-NN backend '{nnBackend}' is not allowed. Permitted: {string.Join(", ", AllowedNnBackends)}.");
                    args.Append($"--dir .:. --nn-preload default:{nnBackend}:CPU:model.bin ");
                }

                if (task.ResourceLimits?.AllowFileSystemAccess == true)
                    args.Append("--dir .:. ");

                args.Append($"\"{wasmPath}\"");

                if (task.EntryPoint != null)
                    args.Append($" --reactor --invoke {task.EntryPoint}");

                if (task.Arguments != null)
                {
                    foreach (var arg in task.Arguments)
                        args.Append($" {arg}");
                }

                var timeout = GetEffectiveTimeout(task);
                var result = await RunProcessAsync("wasmedge", args.ToString(),
                    stdin: task.InputData.Length > 0 ? task.GetInputDataAsString() : null,
                    environment: task.Environment,
                    timeout: timeout,
                    cancellationToken: cancellationToken);

                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"wasmedge exited with code {result.ExitCode}: {result.StandardError}");

                return (EncodeOutput(result.StandardOutput), $"wasmedge{(enableNn ? " (WASI-NN)" : "")} completed in {result.Elapsed.TotalMilliseconds:F0}ms\n{result.StandardError}");
            }
            finally
            {
                try { File.Delete(wasmPath); } catch { /* cleanup */ }
            }
        }, cancellationToken);
    }
}
