using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.WasmLanguages.Tier1;

/// <summary>
/// WASM language verification strategy for Rust, the premier WASM-first language with
/// the best ecosystem support, smallest binaries, and full WASI + Component Model support
/// via <c>cargo-component</c> and <c>wasm-bindgen</c>.
/// </summary>
/// <remarks>
/// <para>
/// Rust has first-class WASM support with the <c>wasm32-wasi</c> target built into the standard
/// toolchain. It produces the smallest compiled WASM binaries among systems languages (200KB-2MB)
/// thanks to zero-cost abstractions, no runtime, and aggressive dead-code elimination via LTO.
/// </para>
/// <para>
/// Key advantages:
/// </para>
/// <list type="bullet">
/// <item><description>Full WASI preview1/preview2 support via <c>wasm32-wasi</c> target</description></item>
/// <item><description>Component Model support via <c>cargo-component</c></description></item>
/// <item><description>Smallest binaries among systems languages</description></item>
/// <item><description>Native-tier performance (within 5-10% of native code)</description></item>
/// <item><description>Memory safety guarantees without garbage collector overhead</description></item>
/// </list>
/// </remarks>
internal sealed class RustWasmLanguageStrategy : WasmLanguageStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.lang.rust";

    /// <inheritdoc/>
    public override string StrategyName => "Rust WASM Language";

    /// <inheritdoc/>
    public override WasmLanguageInfo LanguageInfo => new(
        Language: "Rust",
        LanguageVersion: "1.75+",
        WasmTarget: "wasm32-wasi",
        Toolchain: "rustup target add wasm32-wasi",
        CompileCommand: "cargo build --target wasm32-wasi --release",
        WasiSupport: WasiSupportLevel.Full,
        ComponentModelSupport: true,
        BinarySize: WasmBinarySize.Small,
        PerformanceTier: PerformanceTier.Native,
        Notes: "Best WASM support. Smallest binaries. Full WASI + Component Model via cargo-component."
    );

    /// <summary>
    /// Minimal valid WASM module representing a Rust-compiled WASI binary.
    /// Contains a single exported <c>_start</c> function that returns immediately (nop).
    /// </summary>
    /// <remarks>
    /// Module structure: magic (4B) + version (4B) + type section (empty func type) +
    /// function section + export section (_start) + code section (nop + end).
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
