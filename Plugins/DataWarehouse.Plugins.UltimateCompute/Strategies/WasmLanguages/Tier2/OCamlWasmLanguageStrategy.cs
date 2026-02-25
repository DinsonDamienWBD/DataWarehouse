using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.WasmLanguages.Tier2;

/// <summary>
/// WASM language verification strategy for OCaml, using the <c>wasocaml</c> compiler fork
/// that compiles OCaml source code to WebAssembly with experimental support.
/// </summary>
/// <remarks>
/// <para>
/// <c>wasocaml</c> is an active OCaml-to-WASM compiler that produces standalone WebAssembly
/// binaries (5-15 MB). It is the primary direct compilation path for OCaml to WASM. An
/// alternative approach exists via <c>wasm_of_ocaml</c> which transpiles OCaml to JavaScript
/// first, then embeds it in a WASM module (JS-in-WASM path), but that approach has significant
/// overhead. OCaml 5.0 multicore effects are not available in WASM builds.
/// </para>
/// <para>
/// Key characteristics:
/// </para>
/// <list type="bullet">
/// <item><description><c>wasocaml</c> compiler fork for direct OCaml-to-WASM compilation</description></item>
/// <item><description>Medium binaries (5-15 MB) including OCaml runtime and GC</description></item>
/// <item><description>Experimental WASI support; not all system interfaces mapped</description></item>
/// <item><description>Near-native performance for functional computation patterns</description></item>
/// <item><description>OCaml 5.0 multicore/effects not available in WASM target</description></item>
/// </list>
/// </remarks>
internal sealed class OCamlWasmLanguageStrategy : WasmLanguageStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.lang.ocaml";

    /// <inheritdoc/>
    public override string StrategyName => "OCaml WASM Language";

    /// <inheritdoc/>
    public override WasmLanguageInfo LanguageInfo => new(
        Language: "OCaml",
        LanguageVersion: "5.0+ (wasocaml)",
        WasmTarget: "wasm32",
        Toolchain: "wasocaml (OCaml to WASM compiler fork)",
        CompileCommand: "wasocaml source.ml -o output.wasm",
        WasiSupport: WasiSupportLevel.Experimental,
        ComponentModelSupport: false,
        BinarySize: WasmBinarySize.Medium,
        PerformanceTier: PerformanceTier.NearNative,
        Notes: "wasocaml is an active OCaml-to-WASM compiler. 5-15MB binaries. Emerging support. " +
               "Alternative: wasm_of_ocaml (JS-in-WASM path). OCaml 5.0 multicore not available " +
               "in WASM builds."
    );

    /// <summary>
    /// Minimal valid WASM module representing a wasocaml-compiled OCaml binary.
    /// Contains a single exported <c>_start</c> function that returns immediately (nop).
    /// </summary>
    /// <remarks>
    /// In production, this would be a wasocaml-compiled module (5-15 MB) including the OCaml runtime.
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
