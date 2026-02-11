using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.WasmLanguages.Tier3;

/// <summary>
/// WASM language verification strategy for Ada, using <c>adawebpack</c> which leverages
/// GNAT-LLVM to compile Ada source code to WebAssembly via Emscripten.
/// </summary>
/// <remarks>
/// <para>
/// <c>adawebpack</c> provides GNAT-LLVM to WASM compilation, enabling Ada code to run in
/// WebAssembly environments. The project is niche but functional, producing large binaries
/// due to the Ada runtime system (RTS). The primary use case is safety-critical embedded code
/// verification in a sandboxed WASM environment. Ada 2012+ features are supported through
/// GNAT-LLVM, but the WASM target has limited testing.
/// </para>
/// <para>
/// Feasibility: Low for general use. <c>adawebpack</c> is niche but functional. Large binaries
/// due to Ada RTS. Primarily for safety-critical code verification, not general compute.
/// </para>
/// <list type="bullet">
/// <item><description><c>adawebpack</c> provides GNAT-LLVM to WASM compilation</description></item>
/// <item><description>Large binaries due to Ada runtime system (RTS) inclusion</description></item>
/// <item><description>Experimental WASI support; Emscripten-oriented target</description></item>
/// <item><description>Near-native performance via LLVM optimization passes</description></item>
/// <item><description>Niche: safety-critical embedded code verification in WASM sandbox</description></item>
/// </list>
/// </remarks>
internal sealed class AdaWasmLanguageStrategy : WasmLanguageStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.lang.ada";

    /// <inheritdoc/>
    public override string StrategyName => "Ada WASM Language";

    /// <inheritdoc/>
    public override WasmLanguageInfo LanguageInfo => new(
        Language: "Ada",
        LanguageVersion: "Ada 2012+",
        WasmTarget: "wasm32 (via GNAT-LLVM)",
        Toolchain: "adawebpack (GNAT-LLVM to WASM)",
        CompileCommand: "gnatmake via adawebpack + Emscripten",
        WasiSupport: WasiSupportLevel.Experimental,
        ComponentModelSupport: false,
        BinarySize: WasmBinarySize.Large,
        PerformanceTier: PerformanceTier.NearNative,
        Notes: "adawebpack provides GNAT-LLVM to WASM compilation. Niche but functional. " +
               "Large binaries due to Ada runtime. Primarily for safety-critical embedded " +
               "code verification. Low feasibility for general use."
    );

    /// <summary>
    /// Minimal valid WASM module representing an adawebpack-compiled Ada binary.
    /// Contains a single exported <c>_start</c> function that returns immediately (nop).
    /// </summary>
    /// <remarks>
    /// In production, this would be an adawebpack-compiled module with Ada runtime.
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
