using System.Text;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Gpu;

/// <summary>
/// NVIDIA TensorRT inference strategy for optimized model execution.
/// Imports ONNX models via trtexec, builds TensorRT engines, and runs batch inference.
/// </summary>
internal sealed class TensorRtStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.gpu.tensorrt";
    /// <inheritdoc/>
    public override string StrategyName => "NVIDIA TensorRT";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Custom;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: true, SupportsSandboxing: false,
        MaxMemoryBytes: 80L * 1024 * 1024 * 1024, MaxExecutionTime: TimeSpan.FromHours(4),
        SupportedLanguages: ["onnx", "tensorrt", "uff"], SupportsMultiThreading: true,
        SupportsAsync: true, SupportsNetworkAccess: false, SupportsFileSystemAccess: true,
        MaxConcurrentTasks: 4, MemoryIsolation: MemoryIsolationLevel.Process);
    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.Custom];

    /// <inheritdoc/>
    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await IsToolAvailableAsync("trtexec", "--help", cancellationToken);
    }

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var _onnxBase = Path.GetTempFileName();
            var modelPath = Path.ChangeExtension(_onnxBase, ".onnx");
            File.Move(_onnxBase, modelPath);
            var _engineBase = Path.GetTempFileName();
            var enginePath = Path.ChangeExtension(_engineBase, ".engine");
            File.Move(_engineBase, enginePath);
            string? inputPath = null; // hoisted so finally block can delete it
            try
            {
                await File.WriteAllBytesAsync(modelPath, task.Code.ToArray(), cancellationToken);

                var batchSize = 1;
                if (task.Metadata?.TryGetValue("batch_size", out var bs) == true && bs is int bsi) batchSize = bsi;

                var precision = "fp16";
                if (task.Metadata?.TryGetValue("precision", out var p) == true && p is string ps) precision = ps;

                var workspaceSize = (int)(GetMaxMemoryBytes(task, 4L * 1024 * 1024 * 1024) / (1024 * 1024));

                // Build TensorRT engine from ONNX
                var buildArgs = new StringBuilder();
                buildArgs.Append($"--onnx=\"{modelPath}\" ");
                buildArgs.Append($"--saveEngine=\"{enginePath}\" ");
                buildArgs.Append($"--workspace={workspaceSize} ");
                buildArgs.Append($"--batch={batchSize} ");

                switch (precision)
                {
                    case "fp16": buildArgs.Append("--fp16 "); break;
                    case "int8": buildArgs.Append("--int8 "); break;
                    case "tf32": buildArgs.Append("--tf32 "); break;
                }

                if (task.Metadata?.TryGetValue("dynamic_shapes", out var ds) == true && ds is string dss)
                    buildArgs.Append($"--minShapes={dss} --optShapes={dss} --maxShapes={dss} ");

                var buildResult = await RunProcessAsync("trtexec", buildArgs.ToString(),
                    timeout: TimeSpan.FromMinutes(10), cancellationToken: cancellationToken);

                if (buildResult.ExitCode != 0)
                    throw new InvalidOperationException($"TensorRT engine build failed: {buildResult.StandardError}");

                // Run inference with the built engine
                var inferArgs = new StringBuilder();
                inferArgs.Append($"--loadEngine=\"{enginePath}\" ");
                inferArgs.Append($"--batch={batchSize} ");
                inferArgs.Append("--iterations=1 ");
                inferArgs.Append("--warmUp=100 ");
                inferArgs.Append("--verbose ");

                if (task.InputData.Length > 0)
                {
                    var _binBase = Path.GetTempFileName();
                    inputPath = Path.ChangeExtension(_binBase, ".bin");
                    File.Move(_binBase, inputPath);
                    await File.WriteAllBytesAsync(inputPath, task.InputData.ToArray(), cancellationToken);
                    inferArgs.Append($"--loadInputs={inputPath} ");
                }

                var timeout = GetEffectiveTimeout(task);
                var result = await RunProcessAsync("trtexec", inferArgs.ToString(),
                    timeout: timeout, cancellationToken: cancellationToken);

                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"TensorRT inference failed: {result.StandardError}");

                // Parse performance metrics from output
                var perfLine = result.StandardOutput.Split('\n').LastOrDefault(l => l.Contains("Throughput")) ?? "";

                return (EncodeOutput(result.StandardOutput), $"TensorRT ({precision}, batch={batchSize}) completed in {result.Elapsed.TotalMilliseconds:F0}ms\nPerf: {perfLine}\n{result.StandardError}");
            }
            finally
            {
                try { File.Delete(modelPath); File.Delete(enginePath); if (inputPath != null) File.Delete(inputPath); } catch { /* Best-effort cleanup */ }
            }
        }, cancellationToken);
    }
}
