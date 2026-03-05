using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.WasmLanguages.Tier2;

/// <summary>
/// WASM language verification strategy for PHP, using the interpreter-in-WASM approach
/// where the Zend engine is compiled to WebAssembly and runs <c>.php</c> source code at runtime.
/// </summary>
/// <remarks>
/// <para>
/// PHP does not compile directly to WASM. The Zend engine (PHP's core runtime) is compiled to
/// a WASM module using the community-maintained <c>php-wasm</c> project, producing 15-30 MB
/// binaries. Extension support is limited since native extensions cannot be loaded in WASM.
/// </para>
/// <para>
/// Key characteristics:
/// </para>
/// <list type="bullet">
/// <item><description>Interpreter-in-WASM: Zend engine compiled to WASM runs <c>.php</c> source</description></item>
/// <item><description>Large binaries (15-30 MB) due to embedded Zend engine</description></item>
/// <item><description>Community-maintained <c>php-wasm</c> project</description></item>
/// <item><description>Limited extension support: no native PHP extensions</description></item>
/// <item><description>Partial WASI support for file I/O and environment variables</description></item>
/// </list>
/// </remarks>
internal sealed class PhpWasmLanguageStrategy : WasmLanguageStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.lang.php";

    /// <inheritdoc/>
    protected override string[] GetToolchainBinaryNames() => new[] { "php" };

    /// <inheritdoc/>
    public override string StrategyName => "PHP WASM Language";

    /// <inheritdoc/>
    public override WasmLanguageInfo LanguageInfo => new(
        Language: "PHP",
        LanguageVersion: "8.2+ (Zend Engine)",
        WasmTarget: "wasm32-wasi",
        Toolchain: "php-wasm project (Zend engine compiled to WASM)",
        CompileCommand: "Build php-wasm from source or use pre-compiled php-wasm binary + script bundling",
        WasiSupport: WasiSupportLevel.Partial,
        ComponentModelSupport: false,
        BinarySize: WasmBinarySize.Large,
        PerformanceTier: PerformanceTier.Slow,
        Notes: "Interpreter-in-WASM: Zend engine compiled to WASM. 15-30MB binaries. " +
               "Limited extension support. Community-maintained php-wasm project."
    );

    /// <summary>
    /// Minimal valid WASM module representing a PHP/Zend-in-WASM binary.
    /// Contains a single exported <c>_start</c> function that returns immediately (nop).
    /// </summary>
    /// <remarks>
    /// In production, this would be the Zend engine compiled to WASM (15-30 MB).
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
