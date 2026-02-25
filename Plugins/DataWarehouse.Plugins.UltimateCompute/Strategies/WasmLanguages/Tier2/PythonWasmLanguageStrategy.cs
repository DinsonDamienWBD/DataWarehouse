using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.WasmLanguages.Tier2;

/// <summary>
/// WASM language verification strategy for Python, using the interpreter-in-WASM approach
/// where CPython is compiled to WebAssembly and runs <c>.py</c> source code at runtime.
/// </summary>
/// <remarks>
/// <para>
/// Python does not compile directly to WASM. Instead, the CPython interpreter itself is compiled
/// to a WASM module using <c>componentize-py</c> (WASI Preview 2 components), Pyodide (browser),
/// or <c>py2wasm</c>. The resulting binary includes the full CPython runtime, leading to
/// 10-30 MB binaries and 1-5 second cold starts.
/// </para>
/// <para>
/// Key characteristics:
/// </para>
/// <list type="bullet">
/// <item><description>Interpreter-in-WASM: CPython compiled to WASM runs <c>.py</c> source</description></item>
/// <item><description>Large binaries (10-30 MB) due to embedded CPython runtime</description></item>
/// <item><description><c>componentize-py</c> for WASI Preview 2 component generation</description></item>
/// <item><description>Limited stdlib: no threading, no subprocess, restricted filesystem</description></item>
/// <item><description>Cold start latency of 1-5 seconds due to interpreter initialization</description></item>
/// </list>
/// </remarks>
internal sealed class PythonWasmLanguageStrategy : WasmLanguageStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.lang.python";

    /// <inheritdoc/>
    public override string StrategyName => "Python WASM Language";

    /// <inheritdoc/>
    public override WasmLanguageInfo LanguageInfo => new(
        Language: "Python",
        LanguageVersion: "3.11+ (CPython)",
        WasmTarget: "wasm32-wasi",
        Toolchain: "pip install componentize-py (WASI P2 components) | Pyodide (browser) | py2wasm",
        CompileCommand: "componentize-py -d wit/world.wit -w my-world componentize app -o output.wasm",
        WasiSupport: WasiSupportLevel.Partial,
        ComponentModelSupport: false,
        BinarySize: WasmBinarySize.Large,
        PerformanceTier: PerformanceTier.Slow,
        Notes: "Interpreter-in-WASM approach: CPython compiled to WASM runs .py source. " +
               "10-30MB binaries. componentize-py for WASI Preview 2 components. " +
               "Limited stdlib (no threading, no subprocess). Cold start 1-5s."
    );

    /// <summary>
    /// Minimal valid WASM module representing a Python interpreter-in-WASM binary.
    /// Contains a single exported <c>_start</c> function that returns immediately (nop).
    /// </summary>
    /// <remarks>
    /// In production, this would be the full CPython interpreter compiled to WASM (10-30 MB).
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
