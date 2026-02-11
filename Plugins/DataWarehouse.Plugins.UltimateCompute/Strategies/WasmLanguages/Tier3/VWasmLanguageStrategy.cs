using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.WasmLanguages.Tier3;

/// <summary>
/// WASM language verification strategy for V (Vlang), using an indirect compilation path
/// through V's C backend and Emscripten/WASI SDK to produce WebAssembly output.
/// </summary>
/// <remarks>
/// <para>
/// V does not have a direct WebAssembly backend. The compilation path is indirect: V compiles
/// source code to C, then Emscripten or the WASI SDK compiles the C output to WASM. This
/// two-step process is unstable and introduces overhead from the C intermediary. Medium-sized
/// binaries are produced depending on the Emscripten optimization level.
/// </para>
/// <para>
/// Feasibility: Medium via C intermediary. No direct WASM backend exists. The V -> C -> WASM
/// path is functional but fragile and requires both the V compiler and Emscripten toolchain.
/// </para>
/// <list type="bullet">
/// <item><description>Indirect path: V compiles to C, then Emscripten/WASI SDK compiles C to WASM</description></item>
/// <item><description>Medium binaries depending on Emscripten optimization settings</description></item>
/// <item><description>Experimental WASI support via WASI SDK C compilation</description></item>
/// <item><description>Near-native performance potential through C intermediary optimization</description></item>
/// <item><description>No direct WASM backend; requires two-step compilation</description></item>
/// </list>
/// </remarks>
internal sealed class VWasmLanguageStrategy : WasmLanguageStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.lang.v";

    /// <inheritdoc/>
    public override string StrategyName => "V WASM Language";

    /// <inheritdoc/>
    public override WasmLanguageInfo LanguageInfo => new(
        Language: "V (Vlang)",
        LanguageVersion: "0.4+",
        WasmTarget: "wasm32 (via C backend + Emscripten)",
        Toolchain: "V compiler + Emscripten",
        CompileCommand: "v -o output.c source.v && emcc output.c -o output.wasm",
        WasiSupport: WasiSupportLevel.Experimental,
        ComponentModelSupport: false,
        BinarySize: WasmBinarySize.Medium,
        PerformanceTier: PerformanceTier.NearNative,
        Notes: "Indirect path: V compiles to C, then Emscripten/WASI SDK compiles C to WASM. " +
               "Unstable. No direct WASM backend. Medium feasibility via C intermediary."
    );

    /// <summary>
    /// Minimal valid WASM module representing a V-compiled binary via the C/Emscripten path.
    /// Contains a single exported <c>_start</c> function that returns immediately (nop).
    /// </summary>
    /// <remarks>
    /// In production, this would be a V -> C -> Emscripten compiled module.
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
