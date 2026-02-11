using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.WasmLanguages.Tier3;

/// <summary>
/// WASM language verification strategy for Fortran, using <c>flang-wasm</c> (LLVM flang
/// compiled for WASM) to compile Fortran numerical code to WebAssembly.
/// </summary>
/// <remarks>
/// <para>
/// <c>flang-wasm</c> is an experimental port of the LLVM Fortran compiler targeting WebAssembly.
/// It exists primarily to support the webR project's Fortran dependencies (BLAS, LAPACK).
/// Standalone Fortran-to-WASM compilation is experimental and many runtime libraries are missing.
/// Large binaries result from embedding the Fortran runtime. The primary use case is numerical
/// computing where Fortran code must run in a WASM sandbox.
/// </para>
/// <para>
/// Feasibility: Medium for numerical code. <c>flang-wasm</c> exists but is missing many runtime
/// libraries. Primarily serves as a dependency for webR rather than standalone use.
/// </para>
/// <list type="bullet">
/// <item><description><c>flang-wasm</c> (LLVM flang) compiled for WASM targets</description></item>
/// <item><description>Large binaries due to Fortran runtime inclusion</description></item>
/// <item><description>Experimental WASI support; many runtime libraries missing</description></item>
/// <item><description>Near-native performance for numerical computation via LLVM</description></item>
/// <item><description>Primarily used by webR for R's Fortran dependencies (BLAS/LAPACK)</description></item>
/// </list>
/// </remarks>
internal sealed class FortranWasmLanguageStrategy : WasmLanguageStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.lang.fortran";

    /// <inheritdoc/>
    public override string StrategyName => "Fortran WASM Language";

    /// <inheritdoc/>
    public override WasmLanguageInfo LanguageInfo => new(
        Language: "Fortran",
        LanguageVersion: "F90/F95",
        WasmTarget: "wasm32",
        Toolchain: "flang-wasm (LLVM flang compiled for WASM)",
        CompileCommand: "flang-wasm source.f90 -o output.wasm",
        WasiSupport: WasiSupportLevel.Experimental,
        ComponentModelSupport: false,
        BinarySize: WasmBinarySize.Large,
        PerformanceTier: PerformanceTier.NearNative,
        Notes: "flang-wasm exists but missing many runtime libraries. Primarily used by webR " +
               "for R's Fortran dependencies. Standalone Fortran-to-WASM is experimental. " +
               "Medium feasibility for numerical code."
    );

    /// <summary>
    /// Minimal valid WASM module representing a flang-wasm-compiled Fortran binary.
    /// Contains a single exported <c>_start</c> function that returns immediately (nop).
    /// </summary>
    /// <remarks>
    /// In production, this would be a flang-wasm compiled module with Fortran runtime.
    /// The minimal module validates the wasmtime execution pipeline.
    /// </remarks>
    private static readonly byte[] SampleWasm =
    [
        // WASM magic number
        0x00, 0x61, 0x73, 0x6D,
        // WASM version 1
        0x01, 0x00, 0x00, 0x00,
        // Type section (id=1, 1 type: func () -> ())
        0x01, 0x04, 0x01, 0x60, 0x00, 0x00,
        // Function section (id=3, 1 function using type 0)
        0x03, 0x02, 0x01, 0x00,
        // Export section (id=7, 1 export: "_start" -> func 0)
        0x07, 0x0A, 0x01, 0x06, 0x5F, 0x73, 0x74, 0x61, 0x72, 0x74, 0x00, 0x00,
        // Code section (id=10, 1 body: 2 bytes, 0 locals, end)
        0x0A, 0x04, 0x01, 0x02, 0x00, 0x0B
    ];

    /// <inheritdoc/>
    protected override ReadOnlySpan<byte> GetSampleWasmBytes() => SampleWasm;
}
