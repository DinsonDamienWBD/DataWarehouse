using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.WasmLanguages.Tier2;

/// <summary>
/// WASM language verification strategy for TypeScript, supporting two compilation paths:
/// AssemblyScript for native WASM compilation of a TypeScript subset, and Javy for
/// full TypeScript via transpilation to JavaScript then QuickJS-in-WASM.
/// </summary>
/// <remarks>
/// <para>
/// TypeScript has two distinct paths to WASM:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>AssemblyScript (native WASM):</b> A strict TypeScript subset that compiles directly to
/// optimized WASM via the <c>asc</c> compiler. Produces tiny, fast binaries (50 KB - 2 MB)
/// with native-tier performance but no WASI support and restricted language features.
/// </description></item>
/// <item><description>
/// <b>Javy / QuickJS (interpreted):</b> Full TypeScript is transpiled to JavaScript via <c>tsc</c>,
/// then compiled to QuickJS bytecode in WASM via Javy. Larger binaries (2-5 MB) with
/// interpreted-tier performance but full TypeScript language support and WASI access.
/// </description></item>
/// </list>
/// <para>
/// The choice between paths depends on WASI requirements and performance constraints:
/// AssemblyScript for performance-critical pure computation, Javy for WASI-dependent workloads.
/// </para>
/// </remarks>
internal sealed class TypeScriptWasmLanguageStrategy : WasmLanguageStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.lang.typescript";

    /// <inheritdoc/>
    protected override string[] GetToolchainBinaryNames() => new[] { "javy", "tsc" };

    /// <inheritdoc/>
    public override string StrategyName => "TypeScript WASM Language";

    /// <inheritdoc/>
    public override WasmLanguageInfo LanguageInfo => new(
        Language: "TypeScript",
        LanguageVersion: "5.0+",
        WasmTarget: "Varies (AssemblyScript path or QuickJS path)",
        Toolchain: "Path 1: AssemblyScript (asc) for native WASM; Path 2: Javy for QuickJS-in-WASM",
        CompileCommand: "Path 1 (native): npx asc entry.ts -o output.wasm --optimize; " +
                        "Path 2 (interpreted): tsc entry.ts && javy compile entry.js -o output.wasm",
        WasiSupport: WasiSupportLevel.Partial,
        ComponentModelSupport: false,
        BinarySize: WasmBinarySize.Medium,
        PerformanceTier: PerformanceTier.NearNative,
        Notes: "Two paths: AssemblyScript compiles TS-subset to native WASM (tiny, fast, no WASI) " +
               "or compile via tsc+Javy for full TS with QuickJS (larger, slower, WASI). " +
               "Choose based on WASI requirement."
    );

    /// <summary>
    /// Minimal valid WASM module representing a TypeScript-compiled WASM binary.
    /// Contains a single exported <c>_start</c> function that returns immediately (nop).
    /// </summary>
    /// <remarks>
    /// In production, this would be either an AssemblyScript-compiled module (50 KB - 2 MB)
    /// or a Javy/QuickJS bundle (2-5 MB) depending on the chosen compilation path.
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
