using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.WasmLanguages.Tier2;

/// <summary>
/// WASM language verification strategy for JavaScript, using the interpreter-in-WASM approach
/// where the QuickJS engine is compiled to WebAssembly via Javy (Bytecode Alliance) and
/// executes JavaScript source code at runtime.
/// </summary>
/// <remarks>
/// <para>
/// JavaScript does not compile directly to WASM. The Javy project from the Bytecode Alliance
/// compiles JavaScript source to QuickJS bytecode and bundles it with the QuickJS engine compiled
/// to WASM, producing 2-5 MB binaries. This enables server-side JavaScript execution in a
/// WASM sandbox without Node.js.
/// </para>
/// <para>
/// Key characteristics:
/// </para>
/// <list type="bullet">
/// <item><description>Interpreter-in-WASM: QuickJS engine compiled to WASM via Javy</description></item>
/// <item><description>Medium binaries (2-5 MB) due to lightweight QuickJS runtime</description></item>
/// <item><description>Pure ES2023 support: no Node.js APIs, no Web APIs</description></item>
/// <item><description>Bytecode Alliance project with active maintenance</description></item>
/// <item><description>WASI Preview 1 partial support for basic system interface</description></item>
/// </list>
/// </remarks>
internal sealed class JavaScriptWasmLanguageStrategy : WasmLanguageStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.lang.javascript";

    /// <inheritdoc/>
    protected override string[] GetToolchainBinaryNames() => new[] { "javy" };

    /// <inheritdoc/>
    public override string StrategyName => "JavaScript WASM Language";

    /// <inheritdoc/>
    public override WasmLanguageInfo LanguageInfo => new(
        Language: "JavaScript",
        LanguageVersion: "ES2023 (QuickJS)",
        WasmTarget: "wasm32-wasi",
        Toolchain: "Javy (Bytecode Alliance) - compiles JS to QuickJS bytecode in WASM",
        CompileCommand: "javy compile input.js -o output.wasm",
        WasiSupport: WasiSupportLevel.Partial,
        ComponentModelSupport: false,
        BinarySize: WasmBinarySize.Medium,
        PerformanceTier: PerformanceTier.Interpreted,
        Notes: "QuickJS engine compiled to WASM via Javy (Bytecode Alliance). " +
               "2-5MB binaries. Server-side JS execution in WASM sandbox. " +
               "No Node.js APIs - pure ES2023."
    );

    /// <summary>
    /// Minimal valid WASM module representing a JavaScript/QuickJS-in-WASM binary.
    /// Contains a single exported <c>_start</c> function that returns immediately (nop).
    /// </summary>
    /// <remarks>
    /// In production, this would be the QuickJS engine compiled to WASM (2-5 MB).
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
