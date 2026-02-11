using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.WasmLanguages.Tier1;

/// <summary>
/// WASM language verification strategy for C, producing the smallest possible WASM binaries
/// via the WASI SDK (wasi-sdk) Clang toolchain with full WASI system interface support.
/// </summary>
/// <remarks>
/// <para>
/// C compiles to WASM via the WASI SDK, which bundles a Clang/LLVM cross-compiler targeting
/// <c>wasm32-wasi</c> with a WASI-compatible sysroot and libc (wasi-libc). Alternatively,
/// Emscripten targets browser environments with <c>wasm32-emscripten</c>.
/// </para>
/// <para>
/// Key characteristics:
/// </para>
/// <list type="bullet">
/// <item><description>Smallest possible WASM binaries (often under 100KB for simple programs)</description></item>
/// <item><description>Full WASI support via wasi-libc and WASI SDK sysroot</description></item>
/// <item><description>Native-tier performance with zero runtime overhead</description></item>
/// <item><description>Component Model support available via wit-bindgen C bindings</description></item>
/// <item><description>Emscripten provides alternative browser-focused compilation</description></item>
/// </list>
/// </remarks>
internal sealed class CWasmLanguageStrategy : WasmLanguageStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.lang.c";

    /// <inheritdoc/>
    public override string StrategyName => "C WASM Language";

    /// <inheritdoc/>
    public override WasmLanguageInfo LanguageInfo => new(
        Language: "C",
        LanguageVersion: "C11/C17",
        WasmTarget: "wasm32-wasi",
        Toolchain: "WASI SDK (wasi-sdk)",
        CompileCommand: "$WASI_SDK_PATH/bin/clang --sysroot=$WASI_SDK_PATH/share/wasi-sysroot -o output.wasm input.c",
        WasiSupport: WasiSupportLevel.Full,
        ComponentModelSupport: false,
        BinarySize: WasmBinarySize.Small,
        PerformanceTier: PerformanceTier.Native,
        Notes: "Smallest possible binaries. Full WASI via WASI SDK. Emscripten for browser targets."
    );

    /// <summary>
    /// Minimal valid WASM module representing a C-compiled WASI binary.
    /// Contains a single exported <c>_start</c> function that returns immediately.
    /// </summary>
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
