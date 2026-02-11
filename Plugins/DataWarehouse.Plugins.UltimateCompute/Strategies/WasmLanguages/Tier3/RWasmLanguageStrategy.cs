using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.WasmLanguages.Tier3;

/// <summary>
/// WASM language verification strategy for R, using the webR project which compiles the
/// R interpreter and its math libraries to WebAssembly for statistical computing in WASM.
/// </summary>
/// <remarks>
/// <para>
/// The webR project (maintained by Posit/RStudio) compiles the R interpreter to WebAssembly,
/// enabling R statistical computing in browser and server-side WASM environments. Binaries are
/// very large (50 MB+) due to the R runtime and math libraries (BLAS, LAPACK). Fortran
/// dependencies are handled via <c>flang-wasm</c>. The project is actively maintained but
/// the large binary size limits practical deployment.
/// </para>
/// <para>
/// Feasibility: Medium. Actively maintained by Posit (RStudio). Useful for statistical
/// compute-on-data scenarios but impractical for general-purpose due to 50 MB+ binary size.
/// </para>
/// <list type="bullet">
/// <item><description>webR project actively maintained by Posit (RStudio)</description></item>
/// <item><description>Very large binaries (50 MB+) due to R runtime, BLAS, LAPACK</description></item>
/// <item><description>Experimental WASI support for system interface access</description></item>
/// <item><description>Slow performance due to interpreter-in-WASM overhead</description></item>
/// <item><description>Fortran dependencies handled via <c>flang-wasm</c></description></item>
/// </list>
/// </remarks>
internal sealed class RWasmLanguageStrategy : WasmLanguageStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.lang.r";

    /// <inheritdoc/>
    public override string StrategyName => "R WASM Language";

    /// <inheritdoc/>
    public override WasmLanguageInfo LanguageInfo => new(
        Language: "R",
        LanguageVersion: "4.3+",
        WasmTarget: "wasm32 (interpreter-in-WASM)",
        Toolchain: "webR project (R interpreter compiled to WASM)",
        CompileCommand: "Use webR distribution (pre-compiled R + flang-wasm for Fortran libs)",
        WasiSupport: WasiSupportLevel.Experimental,
        ComponentModelSupport: false,
        BinarySize: WasmBinarySize.VeryLarge,
        PerformanceTier: PerformanceTier.Slow,
        Notes: "webR project actively maintained by Posit (RStudio). Large binaries (50MB+) " +
               "due to R runtime + math libs. Fortran dependencies via flang-wasm. Useful for " +
               "statistical compute-on-data but heavy. Medium feasibility."
    );

    /// <summary>
    /// Minimal valid WASM module representing a webR-compiled R interpreter binary.
    /// Contains a single exported <c>_start</c> function that returns immediately (nop).
    /// </summary>
    /// <remarks>
    /// In production, this would be the webR distribution (50 MB+ including R runtime and math libs).
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
