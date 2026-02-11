using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.WasmLanguages.Tier1;

/// <summary>
/// WASM language verification strategy for AssemblyScript, a TypeScript-like language that compiles
/// directly to WebAssembly without WASI support, producing the smallest possible binaries (1KB-200KB).
/// </summary>
/// <remarks>
/// <para>
/// AssemblyScript is a strict subset of TypeScript designed specifically for WebAssembly compilation.
/// It compiles directly to WASM bytecode without an intermediate runtime, producing extremely small
/// binaries optimized for edge computing and embedded scenarios.
/// </para>
/// <para>
/// <b>IMPORTANT:</b> AssemblyScript does NOT support WASI. It dropped WASI support in favor of
/// direct WASM imports/exports for host interaction. This means it cannot use standard WASI APIs
/// for file I/O, environment variables, or clocks. Host interaction is provided via custom
/// <c>env</c> import functions defined in the host runtime.
/// </para>
/// <para>
/// Key characteristics:
/// </para>
/// <list type="bullet">
/// <item><description>WASM-native language with TypeScript-like syntax</description></item>
/// <item><description>Smallest possible binaries (1KB-200KB)</description></item>
/// <item><description>NO WASI support (uses env imports for host interaction)</description></item>
/// <item><description>Near-native performance via direct WASM compilation</description></item>
/// <item><description>No Component Model support</description></item>
/// </list>
/// </remarks>
internal sealed class AssemblyScriptWasmLanguageStrategy : WasmLanguageStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.lang.assemblyscript";

    /// <inheritdoc/>
    public override string StrategyName => "AssemblyScript WASM Language";

    /// <inheritdoc/>
    public override WasmLanguageInfo LanguageInfo => new(
        Language: "AssemblyScript",
        LanguageVersion: "0.27+",
        WasmTarget: "wasm32 (pure WASM, NOT WASI)",
        Toolchain: "npm install assemblyscript",
        CompileCommand: "npx asc entry.ts -o output.wasm --optimize",
        WasiSupport: WasiSupportLevel.None,
        ComponentModelSupport: false,
        BinarySize: WasmBinarySize.Tiny,
        PerformanceTier: PerformanceTier.NearNative,
        Notes: "WASM-native language with TypeScript-like syntax. Does NOT support WASI (dropped support). Uses env imports for host interaction. Produces smallest possible binaries (1KB-200KB)."
    );

    /// <summary>
    /// Minimal valid WASM module representing an AssemblyScript-compiled binary.
    /// Contains a single exported <c>_start</c> function that returns immediately.
    /// </summary>
    /// <remarks>
    /// AssemblyScript modules typically export named functions rather than <c>_start</c>,
    /// but the minimal module validates the execution pipeline format.
    /// Actual AssemblyScript binaries are 1KB-200KB depending on program complexity.
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
