using System.Text;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Wasm;

/// <summary>
/// WASI-NN extension strategy for ML inference via WebAssembly.
/// Supports model loading with --nn-* flags and tensor I/O for neural network execution.
/// </summary>
internal sealed class WasiNnStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.wasi-nn";

    /// <inheritdoc/>
    public override string StrategyName => "WASI-NN";

    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.WASM;

    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: true,
        SupportsSandboxing: true,
        MaxMemoryBytes: 4L * 1024 * 1024 * 1024,
        MaxExecutionTime: TimeSpan.FromMinutes(10),
        SupportedLanguages: ["wasm", "wasi-nn"],
        SupportsMultiThreading: true,
        SupportsAsync: true,
        SupportsNetworkAccess: false,
        SupportsFileSystemAccess: true,
        MaxConcurrentTasks: 20,
        MemoryIsolation: MemoryIsolationLevel.Sandbox
    );

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

                var nnBackend = "OpenVINO";
                if (task.Metadata?.TryGetValue("nn_backend", out var b) == true && b is string bs)
                    nnBackend = bs;

                var nnDevice = "CPU";
                if (task.Metadata?.TryGetValue("nn_device", out var d) == true && d is string ds)
                    nnDevice = ds;

                var modelPath = "model.bin";
                if (task.Metadata?.TryGetValue("model_path", out var mp) == true && mp is string mps)
                    modelPath = mps;

                var args = new StringBuilder();
                args.Append($"--dir .:. ");
                args.Append($"--nn-preload default:{nnBackend}:{nnDevice}:{modelPath} ");
                args.Append($"\"{wasmPath}\"");

                if (task.Arguments != null)
                {
                    foreach (var arg in task.Arguments)
                        args.Append($" \"{arg.Replace("\"", "\\\"")}\"");
                }

                var timeout = GetEffectiveTimeout(task, TimeSpan.FromMinutes(10));
                var stdinData = task.InputData.Length > 0 ? task.GetInputDataAsString() : null;
                var result = await RunProcessAsync("wasmedge", args.ToString(),
                    stdin: stdinData,
                    environment: task.Environment,
                    timeout: timeout,
                    cancellationToken: cancellationToken);

                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"WASI-NN inference failed with code {result.ExitCode}: {result.StandardError}");

                return (EncodeOutput(result.StandardOutput),
                    $"WASI-NN inference ({nnBackend}/{nnDevice}) completed in {result.Elapsed.TotalMilliseconds:F0}ms\n{result.StandardError}");
            }
            finally
            {
                try { File.Delete(wasmPath); } catch { /* cleanup */ }
            }
        }, cancellationToken);
    }
}
