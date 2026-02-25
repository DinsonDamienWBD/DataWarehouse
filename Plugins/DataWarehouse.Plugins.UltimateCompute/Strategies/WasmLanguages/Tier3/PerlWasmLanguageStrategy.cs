using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.WasmLanguages.Tier3;

/// <summary>
/// WASM language verification strategy for Perl, using the webperl project which compiles the
/// Perl interpreter itself to WebAssembly via Emscripten (interpreter-in-WASM approach).
/// </summary>
/// <remarks>
/// <para>
/// Perl does not compile to WASM. The webperl project takes the Perl interpreter (written in C)
/// and compiles it to WebAssembly using Emscripten. This interpreter-in-WASM approach results in
/// large binaries and slow execution due to double interpretation (WASM runtime running the Perl
/// interpreter running Perl scripts). The project has limited maintenance and WASI is not
/// supported (Emscripten target only).
/// </para>
/// <para>
/// Feasibility: Low. Interpreter-in-WASM with large binaries, no WASI support, and limited
/// maintenance. Not suitable for production WASM workloads.
/// </para>
/// <list type="bullet">
/// <item><description>Interpreter-in-WASM via webperl (Perl interpreter compiled with Emscripten)</description></item>
/// <item><description>Large binaries due to embedded Perl interpreter and standard library</description></item>
/// <item><description>No WASI support (Emscripten target only, browser-oriented)</description></item>
/// <item><description>Slow performance due to double interpretation overhead</description></item>
/// <item><description>Limited maintenance; webperl project not actively developed</description></item>
/// </list>
/// </remarks>
internal sealed class PerlWasmLanguageStrategy : WasmLanguageStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.lang.perl";

    /// <inheritdoc/>
    public override string StrategyName => "Perl WASM Language";

    /// <inheritdoc/>
    public override WasmLanguageInfo LanguageInfo => new(
        Language: "Perl",
        LanguageVersion: "5.36+",
        WasmTarget: "wasm32 (interpreter-in-WASM)",
        Toolchain: "webperl project (Perl interpreter compiled to WASM via Emscripten)",
        CompileCommand: "Use pre-built webperl.wasm or compile Perl source with Emscripten",
        WasiSupport: WasiSupportLevel.None,
        ComponentModelSupport: false,
        BinarySize: WasmBinarySize.Large,
        PerformanceTier: PerformanceTier.Slow,
        Notes: "Interpreter-in-WASM via webperl project. Limited maintenance. Large binaries. " +
               "WASI not supported (Emscripten target only). Low feasibility for production use."
    );

    /// <summary>
    /// Minimal valid WASM module representing a webperl-compiled Perl interpreter binary.
    /// Contains a single exported <c>_start</c> function that returns immediately (nop).
    /// </summary>
    /// <remarks>
    /// In production, this would be the webperl distribution (Perl interpreter compiled to WASM).
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
