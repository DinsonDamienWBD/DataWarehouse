using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.WasmLanguages.Tier3;

/// <summary>
/// WASM language verification strategy for Prolog, using interpreter-in-WASM approaches
/// with SWI-Prolog or Ciao Prolog WASM ports for logic programming in WebAssembly.
/// </summary>
/// <remarks>
/// <para>
/// Prolog WASM support relies on compiling existing Prolog interpreters (SWI-Prolog, Ciao Prolog)
/// to WebAssembly. SWI-Prolog has a WASM port but with limited maintenance. Ciao Prolog has
/// experimental WASM support. Both approaches embed the interpreter in WASM, resulting in large
/// binaries and slow performance. The niche use case is logic programming on data within a
/// WASM sandbox.
/// </para>
/// <para>
/// Feasibility: Low. Interpreter-in-WASM with limited maintenance. Niche use case for logic
/// programming. Not suitable for general-purpose WASM production workloads.
/// </para>
/// <list type="bullet">
/// <item><description>SWI-Prolog WASM port with limited maintenance</description></item>
/// <item><description>Ciao Prolog experimental WASM support as alternative</description></item>
/// <item><description>Large binaries due to embedded Prolog interpreter and libraries</description></item>
/// <item><description>Experimental WASI support; limited system interface access</description></item>
/// <item><description>Slow performance due to interpreter-in-WASM double interpretation</description></item>
/// </list>
/// </remarks>
internal sealed class PrologWasmLanguageStrategy : WasmLanguageStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.lang.prolog";

    /// <inheritdoc/>
    public override string StrategyName => "Prolog WASM Language";

    /// <inheritdoc/>
    public override WasmLanguageInfo LanguageInfo => new(
        Language: "Prolog",
        LanguageVersion: "ISO Prolog",
        WasmTarget: "wasm32 (interpreter-in-WASM)",
        Toolchain: "SWI-Prolog WASM port or Ciao Prolog WASM",
        CompileCommand: "Use pre-compiled SWI-Prolog WASM distribution or Ciao WASM build",
        WasiSupport: WasiSupportLevel.Experimental,
        ComponentModelSupport: false,
        BinarySize: WasmBinarySize.Large,
        PerformanceTier: PerformanceTier.Slow,
        Notes: "SWI-Prolog has a WASM port but limited maintenance. Ciao Prolog has " +
               "experimental WASM support. Interpreter-in-WASM approach. Niche use case for " +
               "logic programming on data. Low feasibility."
    );

    /// <summary>
    /// Minimal valid WASM module representing a SWI-Prolog/Ciao WASM distribution.
    /// Contains a single exported <c>_start</c> function that returns immediately (nop).
    /// </summary>
    /// <remarks>
    /// In production, this would be a pre-compiled SWI-Prolog or Ciao Prolog WASM distribution.
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
