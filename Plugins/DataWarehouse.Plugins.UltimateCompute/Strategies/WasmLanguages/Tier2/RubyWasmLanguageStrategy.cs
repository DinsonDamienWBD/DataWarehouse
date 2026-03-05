using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.WasmLanguages.Tier2;

/// <summary>
/// WASM language verification strategy for Ruby, using the interpreter-in-WASM approach
/// where CRuby is compiled to WebAssembly via the <c>ruby.wasm</c> project and runs
/// <c>.rb</c> source code at runtime.
/// </summary>
/// <remarks>
/// <para>
/// Ruby does not compile directly to WASM. The CRuby interpreter is compiled to a WASM module
/// using the WASI SDK, producing 15-30 MB binaries. The <c>ruby/ruby.wasm</c> GitHub project
/// provides pre-built WASM binaries of the Ruby interpreter.
/// </para>
/// <para>
/// Key characteristics:
/// </para>
/// <list type="bullet">
/// <item><description>Interpreter-in-WASM: CRuby compiled to WASM runs <c>.rb</c> source</description></item>
/// <item><description>Large binaries (15-30 MB) due to embedded CRuby runtime</description></item>
/// <item><description>Pre-built binaries available from <c>ruby/ruby.wasm</c> GitHub releases</description></item>
/// <item><description>Limited gem support: native extensions cannot be loaded</description></item>
/// <item><description>WASI Preview 1 partial support for file I/O and environment access</description></item>
/// </list>
/// </remarks>
internal sealed class RubyWasmLanguageStrategy : WasmLanguageStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.lang.ruby";

    /// <inheritdoc/>
    protected override string[] GetToolchainBinaryNames() => new[] { "ruby.wasm" };

    /// <inheritdoc/>
    public override string StrategyName => "Ruby WASM Language";

    /// <inheritdoc/>
    public override WasmLanguageInfo LanguageInfo => new(
        Language: "Ruby",
        LanguageVersion: "3.2+ (CRuby)",
        WasmTarget: "wasm32-wasi",
        Toolchain: "ruby.wasm project (CRuby compiled via WASI SDK)",
        CompileCommand: "Download pre-built ruby.wasm from ruby/ruby.wasm releases; bundle as interpreter + script",
        WasiSupport: WasiSupportLevel.Partial,
        ComponentModelSupport: false,
        BinarySize: WasmBinarySize.Large,
        PerformanceTier: PerformanceTier.Slow,
        Notes: "Interpreter-in-WASM: CRuby compiled to WASM. 15-30MB binaries. " +
               "Limited gem support. Uses ruby.wasm project from ruby/ruby.wasm GitHub."
    );

    /// <summary>
    /// Minimal valid WASM module representing a Ruby interpreter-in-WASM binary.
    /// Contains a single exported <c>_start</c> function that returns immediately (nop).
    /// </summary>
    /// <remarks>
    /// In production, this would be the full CRuby interpreter compiled to WASM (15-30 MB).
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
