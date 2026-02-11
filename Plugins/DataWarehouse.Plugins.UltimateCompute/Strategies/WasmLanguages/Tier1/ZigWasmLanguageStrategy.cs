using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.WasmLanguages.Tier1;

/// <summary>
/// WASM language verification strategy for Zig, a systems language that produces very small WASM
/// binaries (10KB-1MB) without requiring libc, with excellent WASI support and native-tier performance.
/// </summary>
/// <remarks>
/// <para>
/// Zig has first-class WASM support with a built-in <c>wasm32-wasi</c> target in its compiler.
/// Unlike C/C++, Zig does not require an external SDK or sysroot -- WASI support is built into
/// the Zig standard library. This produces very clean, small WASM binaries.
/// </para>
/// <para>
/// Key characteristics:
/// </para>
/// <list type="bullet">
/// <item><description>No libc required -- WASI support is built into Zig's standard library</description></item>
/// <item><description>Very small binaries (10KB-1MB) due to minimal runtime and aggressive optimization</description></item>
/// <item><description>Full WASI support via the built-in <c>wasm32-wasi</c> target</description></item>
/// <item><description>Native-tier performance (within 5-15% of native code)</description></item>
/// <item><description>Component Model support available via wit-bindgen Zig bindings</description></item>
/// <item><description><c>ReleaseSmall</c> optimization mode further minimizes binary size</description></item>
/// </list>
/// </remarks>
internal sealed class ZigWasmLanguageStrategy : WasmLanguageStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.lang.zig";

    /// <inheritdoc/>
    public override string StrategyName => "Zig WASM Language";

    /// <inheritdoc/>
    public override WasmLanguageInfo LanguageInfo => new(
        Language: "Zig",
        LanguageVersion: "0.12+",
        WasmTarget: "wasm32-wasi",
        Toolchain: "Download from ziglang.org",
        CompileCommand: "zig build-exe -target wasm32-wasi -O ReleaseSmall source.zig",
        WasiSupport: WasiSupportLevel.Full,
        ComponentModelSupport: false,
        BinarySize: WasmBinarySize.Small,
        PerformanceTier: PerformanceTier.Native,
        Notes: "No libc required. Produces very small binaries (10KB-1MB). Excellent WASI support."
    );

    /// <summary>
    /// Minimal valid WASM module representing a Zig-compiled WASI binary.
    /// Contains a single exported <c>_start</c> function that returns immediately.
    /// </summary>
    /// <remarks>
    /// Actual Zig WASM binaries are typically 10KB-1MB depending on program complexity
    /// and whether the standard library is used. <c>ReleaseSmall</c> optimization mode
    /// aggressively strips unused code.
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
