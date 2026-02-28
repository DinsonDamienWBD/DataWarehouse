using System.Text;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Wasm;

/// <summary>
/// Wasmer compute runtime strategy that invokes the wasmer CLI to execute WebAssembly modules.
/// Supports cranelift, singlepass, and LLVM compiler backends via configuration.
/// </summary>
internal sealed class WasmerStrategy : ComputeRuntimeStrategyBase
{
    // Allowlist for Wasmer compiler backends — prevents argument injection.
    private static readonly string[] AllowedBackends = ["cranelift", "singlepass", "llvm"];

    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.wasmer";

    /// <inheritdoc/>
    public override string StrategyName => "Wasmer";

    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.WASM;

    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => ComputeCapabilities.CreateWasmDefaults();

    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.WASM];

    /// <inheritdoc/>
    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await IsToolAvailableAsync("wasmer", "--version", cancellationToken);
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
                args.Append("run ");

                // Select backend from metadata or default to cranelift — validate against allowlist.
                var backend = "cranelift";
                if (task.Metadata?.TryGetValue("backend", out var b) == true && b is string bs)
                    backend = bs;
                if (!Array.Exists(AllowedBackends, ab => ab == backend))
                    throw new ArgumentException($"Wasmer backend '{backend}' is not allowed. Permitted: {string.Join(", ", AllowedBackends)}.");
                args.Append($"--{backend} ");

                if (task.EntryPoint != null)
                    args.Append($"--invoke {task.EntryPoint} ");

                args.Append($"\"{wasmPath}\"");

                if (task.Arguments != null)
                {
                    args.Append(" -- ");
                    foreach (var arg in task.Arguments)
                        args.Append($" {arg}");
                }

                var timeout = GetEffectiveTimeout(task);
                var result = await RunProcessAsync("wasmer", args.ToString(),
                    stdin: task.InputData.Length > 0 ? task.GetInputDataAsString() : null,
                    environment: task.Environment,
                    timeout: timeout,
                    cancellationToken: cancellationToken);

                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"wasmer exited with code {result.ExitCode}: {result.StandardError}");

                return (EncodeOutput(result.StandardOutput), $"wasmer ({backend}) completed in {result.Elapsed.TotalMilliseconds:F0}ms\n{result.StandardError}");
            }
            finally
            {
                try { File.Delete(wasmPath); } catch { /* cleanup */ }
            }
        }, cancellationToken);
    }
}
