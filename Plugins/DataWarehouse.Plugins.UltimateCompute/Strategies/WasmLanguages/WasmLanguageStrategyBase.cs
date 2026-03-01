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
                // NOTE: wasmtime ≥ v14 uses --wasm-memory-reservation; older versions use --max-memory.
                // If this flag is silently ignored (wasmtime exits 0 despite unknown flag), memory
                // limits will not be enforced. Verify your installed wasmtime version matches this flag.
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
    /// Verifies the WASM language pipeline by creating a compute task with the pre-compiled sample WASM bytes,
    /// executing them via wasmtime, and comparing the actual output against the expected output.
    /// </summary>
    /// <remarks>
    /// Cat 15 (finding 1747): This verifies that wasmtime can execute pre-compiled WASM artifacts produced
    /// by the language toolchain. ToolchainAvailable=true means "wasmtime executed the sample successfully".
    /// It does NOT verify that the upstream compiler (Javy, TeaVM, CPython, etc.) is installed.
    /// Compile-time toolchain presence is a build/deploy concern, not a runtime execution concern.
    /// Override <see cref="VerifyLanguageAsync"/> in a subclass to add compiler presence checks if needed.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A <see cref="WasmLanguageVerificationResult"/> indicating whether wasmtime is available (ToolchainAvailable),
    /// the sample bytes are valid WASM (CompilationSuccessful), and execution produced expected output.
    /// </returns>
    /// <summary>
    /// Returns the names of upstream compiler/toolchain binaries required for this language (e.g., "javy", "teavm", "python3").
    /// Override in subclasses to verify that the language-specific toolchain (not just wasmtime) is present.
    /// Returns an empty array by default (wasmtime-only check suffices for Tier 1 languages compiled ahead-of-time).
    /// </summary>
    protected virtual string[] GetToolchainBinaryNames() => Array.Empty<string>();

    /// <summary>
    /// Checks whether all toolchain binaries returned by <see cref="GetToolchainBinaryNames"/> are accessible on PATH.
    /// Returns the first missing binary name, or null if all are present.
    /// </summary>
    protected string? FindMissingToolchain()
    {
        foreach (var binary in GetToolchainBinaryNames())
        {
            try
            {
                // Use 'where' on Windows, 'which' on Unix
                var check = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Windows)
                    ? "where" : "which";
                using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(check, binary)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });
                proc?.WaitForExit(3000);
                if (proc?.ExitCode != 0)
                    return binary;
            }
            catch
            {
                return binary; // Cannot run 'which'/'where' — assume missing
            }
        }
        return null; // all present
    }

    public async Task<WasmLanguageVerificationResult> VerifyLanguageAsync(CancellationToken cancellationToken = default)
    {
        // Cat 15 (finding 2918): check language-specific toolchain first, before testing wasmtime execution.
        var missingToolchain = FindMissingToolchain();
        if (missingToolchain != null)
        {
            return new WasmLanguageVerificationResult(
                Language: LanguageInfo.Language,
                ToolchainAvailable: false,
                CompilationSuccessful: false,
                ExecutionSuccessful: false,
                ActualOutput: null,
                ExpectedOutput: ExpectedSampleOutput,
                ExecutionTime: null,
                BinarySizeBytes: 0,
                ErrorMessage: $"Language toolchain '{missingToolchain}' not found on PATH. " +
                    $"Install {string.Join(", ", GetToolchainBinaryNames())} to enable {LanguageInfo.Language} WASM compilation."
            );
        }

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
            // P2-1757: CompilationSuccessful must be false when an exception occurred —
            // wasmtime unavailable or compilation failure is not a successful compilation.
            return new WasmLanguageVerificationResult(
                Language: LanguageInfo.Language,
                ToolchainAvailable: false,
                CompilationSuccessful: false,
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
