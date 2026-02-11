using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.WasmLanguages.Tier3;

/// <summary>
/// WASM language verification strategy for Nim, using the LLVM-based <c>nlvm</c> compiler
/// with an experimental wasm32 target for direct Nim-to-WebAssembly compilation.
/// </summary>
/// <remarks>
/// <para>
/// Nim's WASM support is experimental and requires the <c>nlvm</c> compiler (LLVM-based alternative
/// to the default C-backend Nim compiler). The direct WASM target is work-in-progress. An indirect
/// path exists: compile Nim to C, then use the WASI SDK to compile C to WASM. The <c>nlvm</c> compiler
/// produces medium-sized binaries but the WASM target lacks full standard library support.
/// </para>
/// <para>
/// Feasibility: Medium. Direct WASM via nlvm is incomplete. Indirect path (Nim -> C -> WASM) is
/// functional but adds complexity. Not recommended for production WASM workloads.
/// </para>
/// <list type="bullet">
/// <item><description>LLVM-based <c>nlvm</c> compiler with experimental wasm32 target</description></item>
/// <item><description>Medium binaries via nlvm; indirect C path adds overhead</description></item>
/// <item><description>Experimental WASI support; standard library partially available</description></item>
/// <item><description>Near-native performance potential via LLVM optimization passes</description></item>
/// <item><description>Indirect Nim -> C -> WASI SDK path as fallback</description></item>
/// </list>
/// </remarks>
internal sealed class NimWasmLanguageStrategy : WasmLanguageStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.lang.nim";

    /// <inheritdoc/>
    public override string StrategyName => "Nim WASM Language";

    /// <inheritdoc/>
    public override WasmLanguageInfo LanguageInfo => new(
        Language: "Nim",
        LanguageVersion: "2.0+",
        WasmTarget: "wasm32",
        Toolchain: "nlvm (LLVM-based Nim compiler) with wasm32 target",
        CompileCommand: "nlvm c --cpu:wasm32 --os:standalone source.nim",
        WasiSupport: WasiSupportLevel.Experimental,
        ComponentModelSupport: false,
        BinarySize: WasmBinarySize.Medium,
        PerformanceTier: PerformanceTier.NearNative,
        Notes: "LLVM backend exists but WASM target is work-in-progress. Nim can also compile " +
               "to C then use WASI SDK (indirect path). nlvm compiler needed for direct WASM. " +
               "Medium feasibility."
    );

    /// <summary>
    /// Minimal valid WASM module representing an nlvm-compiled Nim binary.
    /// Contains a single exported <c>_start</c> function that returns immediately (nop).
    /// </summary>
    /// <remarks>
    /// In production, this would be an nlvm-compiled module with Nim runtime.
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
