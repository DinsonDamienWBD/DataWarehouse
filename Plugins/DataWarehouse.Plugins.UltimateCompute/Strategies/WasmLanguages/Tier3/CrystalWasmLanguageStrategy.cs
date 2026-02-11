using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.WasmLanguages.Tier3;

/// <summary>
/// WASM language verification strategy for Crystal, using an experimental proof-of-concept
/// LLVM backend targeting wasm32-unknown-wasi that has not been merged to stable.
/// </summary>
/// <remarks>
/// <para>
/// Crystal's WASM support exists only as a proof-of-concept pull request in the Crystal main
/// repository. The Crystal compiler uses LLVM, which theoretically supports wasm32 targets,
/// but the Crystal runtime and garbage collector are not WASM-ready. Large binaries would result
/// from embedding the Crystal runtime. The POC is not merged to stable and should not be relied
/// upon for production use.
/// </para>
/// <para>
/// Feasibility: Low. POC PR exists but is not merged. Runtime/GC not WASM-ready. LLVM backend
/// could theoretically target WASM but significant engineering work remains.
/// </para>
/// <list type="bullet">
/// <item><description>POC proof-of-concept PR in Crystal main repository (not merged)</description></item>
/// <item><description>Large binaries expected due to Crystal runtime and Boehm GC</description></item>
/// <item><description>Experimental WASI support at proof-of-concept level only</description></item>
/// <item><description>Near-native performance potential via LLVM (if runtime issues resolved)</description></item>
/// <item><description>Runtime and garbage collector not adapted for WASM linear memory</description></item>
/// </list>
/// </remarks>
internal sealed class CrystalWasmLanguageStrategy : WasmLanguageStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.lang.crystal";

    /// <inheritdoc/>
    public override string StrategyName => "Crystal WASM Language";

    /// <inheritdoc/>
    public override WasmLanguageInfo LanguageInfo => new(
        Language: "Crystal",
        LanguageVersion: "1.10+",
        WasmTarget: "wasm32 (POC)",
        Toolchain: "Crystal compiler LLVM backend (POC PR)",
        CompileCommand: "crystal build --target wasm32-unknown-wasi source.cr (POC only)",
        WasiSupport: WasiSupportLevel.Experimental,
        ComponentModelSupport: false,
        BinarySize: WasmBinarySize.Large,
        PerformanceTier: PerformanceTier.NearNative,
        Notes: "POC proof-of-concept PR exists in Crystal main repo. Not merged to stable. " +
               "LLVM-based compiler could theoretically target WASM but runtime/GC are not " +
               "WASM-ready. Low feasibility."
    );

    /// <summary>
    /// Minimal valid WASM module representing a Crystal POC-compiled binary.
    /// Contains a single exported <c>_start</c> function that returns immediately (nop).
    /// </summary>
    /// <remarks>
    /// In production, this would be a Crystal LLVM-compiled module (if POC were merged).
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
