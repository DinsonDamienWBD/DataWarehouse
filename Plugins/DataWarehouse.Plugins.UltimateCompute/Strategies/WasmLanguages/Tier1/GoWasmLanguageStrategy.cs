using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.WasmLanguages.Tier1;

/// <summary>
/// WASM language verification strategy for Go, supporting both TinyGo (recommended, 100KB-2MB binaries)
/// and standard Go (50MB+ binaries via <c>GOOS=wasip1 GOARCH=wasm</c>).
/// </summary>
/// <remarks>
/// <para>
/// Go has two paths to WASM compilation:
/// </para>
/// <list type="bullet">
/// <item><description><b>TinyGo (recommended):</b> Alternative Go compiler optimized for small targets.
/// Produces 100KB-2MB WASM binaries by using a different runtime and garbage collector.
/// Full WASI support via <c>-target=wasi</c>.</description></item>
/// <item><description><b>Standard Go:</b> Official Go compiler with <c>GOOS=wasip1 GOARCH=wasm</c>.
/// Produces 50MB+ binaries because it embeds the entire Go runtime and garbage collector.
/// Added WASI support in Go 1.21.</description></item>
/// </list>
/// <para>
/// TinyGo is strongly recommended for WASM workloads due to orders-of-magnitude smaller
/// binary sizes. Standard Go should only be used when TinyGo compatibility is insufficient
/// (some Go packages are not supported by TinyGo).
/// </para>
/// </remarks>
internal sealed class GoWasmLanguageStrategy : WasmLanguageStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.lang.go";

    /// <inheritdoc/>
    public override string StrategyName => "Go WASM Language";

    /// <inheritdoc/>
    public override WasmLanguageInfo LanguageInfo => new(
        Language: "Go (TinyGo recommended)",
        LanguageVersion: "TinyGo 0.30+ / Go 1.21+",
        WasmTarget: "wasm32-wasi",
        Toolchain: "TinyGo: brew install tinygo; Std Go: built-in",
        CompileCommand: "tinygo build -target=wasi -o output.wasm main.go (recommended) | GOOS=wasip1 GOARCH=wasm go build -o output.wasm (std, produces 50MB+ binary)",
        WasiSupport: WasiSupportLevel.Full,
        ComponentModelSupport: false,
        BinarySize: WasmBinarySize.Medium,
        PerformanceTier: PerformanceTier.NearNative,
        Notes: "TinyGo produces 100KB-2MB binaries vs std Go 50MB+. Always prefer TinyGo for WASM targets."
    );

    /// <summary>
    /// Minimal valid WASM module representing a TinyGo-compiled WASI binary.
    /// Contains a single exported <c>_start</c> function that returns immediately.
    /// </summary>
    /// <remarks>
    /// Actual TinyGo WASM binaries are 100KB-2MB due to the TinyGo runtime and GC.
    /// Standard Go WASM binaries are 50MB+ due to the full Go runtime.
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
