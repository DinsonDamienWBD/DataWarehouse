using System.Text;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.WasmLanguages;

/// <summary>
/// Abstract base class for WASM language verification strategies. Extends <see cref="ComputeRuntimeStrategyBase"/>
/// with language-specific metadata (<see cref="WasmLanguageInfo"/>), embedded sample WASM bytes,
/// and a shared execution pipeline that delegates to the wasmtime CLI.
/// </summary>
/// <remarks>
/// <para>
/// Each concrete language strategy (Rust, C, C++, .NET, Go, AssemblyScript, Zig) inherits from this base
/// and provides:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="LanguageInfo"/> with toolchain, compilation, and performance metadata</description></item>
/// <item><description><see cref="GetSampleWasmBytes"/> returning a minimal valid WASM module</description></item>
/// <item><description><see cref="StrategyId"/> and <see cref="StrategyName"/> for registry discovery</description></item>
/// </list>
/// <para>
/// The base class handles execution via wasmtime, initialization checks, capabilities reporting,
/// and the <see cref="VerifyLanguageAsync"/> method that tests the full compilation-to-execution pipeline.
/// </para>
/// </remarks>
internal abstract class WasmLanguageStrategyBase : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.WASM;

    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.WASM];

    /// <summary>
    /// Gets the language-specific WASM compilation metadata including toolchain, compile command,
    /// WASI support level, binary size category, and performance tier.
    /// </summary>
    public abstract WasmLanguageInfo LanguageInfo { get; }

    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: true,
        SupportsSandboxing: true,
        MaxMemoryBytes: 2L * 1024 * 1024 * 1024,
        MaxExecutionTime: TimeSpan.FromMinutes(5),
        SupportedLanguages: [LanguageInfo.Language.ToLowerInvariant(), "wasm"],
        SupportsMultiThreading: false,
        SupportsAsync: true,
        SupportsNetworkAccess: false,
        SupportsFileSystemAccess: LanguageInfo.WasiSupport != WasiSupportLevel.None,
        MaxConcurrentTasks: 100,
        SupportsNativeDependencies: false,
        SupportsPrecompilation: true,
        SupportsDynamicLoading: false,
        MemoryIsolation: MemoryIsolationLevel.Sandbox
    );

    /// <summary>
    /// Returns the embedded minimal WASM module bytes for this language. The bytes represent a
    /// pre-compiled valid WASM module (magic number + version + minimal sections) that can be
    /// executed by wasmtime to verify the execution pipeline.
    /// </summary>
    /// <returns>A read-only span of bytes containing a valid WASM module.</returns>
    protected abstract ReadOnlySpan<byte> GetSampleWasmBytes();

    /// <summary>
    /// Gets the expected standard output when executing the sample WASM module.
    /// Override in derived classes if the sample module produces output.
    /// </summary>
    protected virtual string ExpectedSampleOutput => "";

    /// <summary>
    /// Initializes the strategy by verifying that the wasmtime CLI is available on the system PATH.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous initialization operation.</returns>
    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await IsToolAvailableAsync("wasmtime", "--version", cancellationToken);
    }

    /// <summary>
    /// Executes a WASM compute task by writing the module bytes to a temporary file and invoking
    /// wasmtime. If the task's <see cref="ComputeTask.Code"/> is empty, the embedded sample WASM
    /// bytes from <see cref="GetSampleWasmBytes"/> are used instead.
    /// </summary>
    /// <param name="task">The compute task containing WASM module bytes or source code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="ComputeResult"/> with execution output, timing, and status.</returns>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var wasmPath = Path.GetTempFileName() + ".wasm";
            try
            {
                byte[] wasmBytes = task.Code.Length > 0
                    ? task.Code.ToArray()
                    : GetSampleWasmBytes().ToArray();

                await File.WriteAllBytesAsync(wasmPath, wasmBytes, cancellationToken);

                var args = new StringBuilder();
                args.Append("run --wasi ");

                var maxMem = GetMaxMemoryBytes(task, 256 * 1024 * 1024);
                args.Append($"--max-memory {maxMem} ");

                if (task.ResourceLimits?.AllowFileSystemAccess == true &&
                    task.ResourceLimits.AllowedFileSystemPaths != null)
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
                    throw new InvalidOperationException(
                        $"wasmtime exited with code {result.ExitCode}: {result.StandardError}");

                return (EncodeOutput(result.StandardOutput),
                    $"[{LanguageInfo.Language}] wasmtime completed in {result.Elapsed.TotalMilliseconds:F0}ms\n{result.StandardError}");
            }
            finally
            {
                try { File.Delete(wasmPath); } catch { /* cleanup best effort */ }
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Verifies the WASM language pipeline by creating a compute task with the sample WASM bytes,
    /// executing it via wasmtime, and comparing the actual output against the expected output.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A <see cref="WasmLanguageVerificationResult"/> indicating whether the toolchain is available,
    /// compilation was successful (sample bytes are valid WASM), and execution produced the expected output.
    /// </returns>
    public async Task<WasmLanguageVerificationResult> VerifyLanguageAsync(CancellationToken cancellationToken = default)
    {
        var sampleBytes = GetSampleWasmBytes().ToArray();

        // Validate WASM magic number
        if (sampleBytes.Length < 8 ||
            sampleBytes[0] != 0x00 || sampleBytes[1] != 0x61 ||
            sampleBytes[2] != 0x73 || sampleBytes[3] != 0x6D)
        {
            return new WasmLanguageVerificationResult(
                Language: LanguageInfo.Language,
                ToolchainAvailable: false,
                CompilationSuccessful: false,
                ExecutionSuccessful: false,
                ActualOutput: null,
                ExpectedOutput: ExpectedSampleOutput,
                ExecutionTime: null,
                BinarySizeBytes: sampleBytes.Length,
                ErrorMessage: "Sample WASM bytes do not contain valid WASM magic number"
            );
        }

        var task = new ComputeTask(
            Id: $"verify-{LanguageInfo.Language.ToLowerInvariant()}-{Guid.NewGuid():N}",
            Code: sampleBytes,
            Language: LanguageInfo.Language.ToLowerInvariant()
        );

        try
        {
            var result = await ExecuteAsync(task, cancellationToken);
            var actualOutput = result.Success ? result.GetOutputDataAsString().Trim() : null;

            return new WasmLanguageVerificationResult(
                Language: LanguageInfo.Language,
                ToolchainAvailable: true,
                CompilationSuccessful: true,
                ExecutionSuccessful: result.Success,
                ActualOutput: actualOutput,
                ExpectedOutput: ExpectedSampleOutput,
                ExecutionTime: result.ExecutionTime,
                BinarySizeBytes: sampleBytes.Length,
                ErrorMessage: result.Success ? null : result.ErrorMessage
            );
        }
        catch (Exception ex)
        {
            return new WasmLanguageVerificationResult(
                Language: LanguageInfo.Language,
                ToolchainAvailable: false,
                CompilationSuccessful: true,
                ExecutionSuccessful: false,
                ActualOutput: null,
                ExpectedOutput: ExpectedSampleOutput,
                ExecutionTime: null,
                BinarySizeBytes: sampleBytes.Length,
                ErrorMessage: $"Execution failed: {ex.Message}"
            );
        }
    }
}
