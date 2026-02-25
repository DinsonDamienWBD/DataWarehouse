using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.WasmLanguages.Tier3;

/// <summary>
/// WASM language verification strategy for Elixir/Erlang, using experimental approaches
/// including the Orb DSL (Elixir DSL for WASM generation) and the unmaintained Lumen compiler
/// (BEAM VM to WASM).
/// </summary>
/// <remarks>
/// <para>
/// Elixir runs on the BEAM virtual machine (Erlang VM), which makes direct WASM compilation
/// fundamentally challenging. Two approaches exist: (1) Lumen, a BEAM-to-WASM compiler that
/// is now unmaintained; (2) Orb, an Elixir DSL that generates WASM bytecode directly but is
/// not a full Elixir compiler -- it only supports a WASM-oriented subset. Compiling the full
/// BEAM VM to WASM is impractical due to its process-based concurrency model.
/// </para>
/// <para>
/// Feasibility: Low for general Elixir code. Lumen is unmaintained. Orb is a DSL, not a
/// compiler. BEAM VM in WASM is impractical. Not suitable for production use.
/// </para>
/// <list type="bullet">
/// <item><description>Lumen (BEAM-to-WASM compiler) is unmaintained and incomplete</description></item>
/// <item><description>Orb is an Elixir DSL that generates WASM, not a full compiler</description></item>
/// <item><description>Large binaries if BEAM VM were embedded in WASM</description></item>
/// <item><description>Experimental WASI support at best; BEAM concurrency model incompatible</description></item>
/// <item><description>Slow performance due to VM-in-WASM overhead (Lumen) or DSL limitations (Orb)</description></item>
/// </list>
/// </remarks>
internal sealed class ElixirWasmLanguageStrategy : WasmLanguageStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.lang.elixir";

    /// <inheritdoc/>
    public override string StrategyName => "Elixir WASM Language";

    /// <inheritdoc/>
    public override WasmLanguageInfo LanguageInfo => new(
        Language: "Elixir/Erlang",
        LanguageVersion: "1.15+ / OTP 26+",
        WasmTarget: "wasm32 (experimental)",
        Toolchain: "Orb (Elixir DSL for WASM) or Lumen (BEAM to WASM, unmaintained)",
        CompileCommand: "Orb: DSL generates WASM directly; Lumen: lumen compile source.ex -o output.wasm (unmaintained)",
        WasiSupport: WasiSupportLevel.Experimental,
        ComponentModelSupport: false,
        BinarySize: WasmBinarySize.Large,
        PerformanceTier: PerformanceTier.Slow,
        Notes: "Lumen (BEAM-to-WASM compiler) is unmaintained. Orb is an Elixir DSL that " +
               "generates WASM but is not a full Elixir compiler. BEAM VM in WASM is " +
               "impractical. Low feasibility for general Elixir code."
    );

    /// <summary>
    /// Minimal valid WASM module representing an Elixir/BEAM-compiled WASM binary.
    /// Contains a single exported <c>_start</c> function that returns immediately (nop).
    /// </summary>
    /// <remarks>
    /// In production, this would be a Lumen/Orb-generated module.
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
