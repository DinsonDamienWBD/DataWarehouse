using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.WasmLanguages.Tier2;

/// <summary>
/// WASM language verification strategy for Dart, using the official <c>dart2wasm</c> compiler
/// built into the Dart SDK that compiles Dart source code to WebAssembly leveraging the
/// wasm-gc (garbage collection) proposal.
/// </summary>
/// <remarks>
/// <para>
/// Dart has official WASM support via the <c>dart compile wasm</c> command in the Dart SDK 3.0+.
/// The <c>dart2wasm</c> compiler produces standalone WASM binaries (3-10 MB) that leverage the
/// wasm-gc proposal for garbage collection. This is a separate compilation path from Flutter's
/// web target, enabling server-side Dart WASM execution.
/// </para>
/// <para>
/// Key characteristics:
/// </para>
/// <list type="bullet">
/// <item><description>Official <c>dart2wasm</c> compiler built into Dart SDK 3.0+</description></item>
/// <item><description>Medium binaries (3-10 MB) with near-native performance</description></item>
/// <item><description>Leverages the wasm-gc proposal for garbage collection</description></item>
/// <item><description>Standalone WASM compilation (not just Flutter web)</description></item>
/// <item><description>Partial WASI Preview 1 support for system interface access</description></item>
/// </list>
/// </remarks>
internal sealed class DartWasmLanguageStrategy : WasmLanguageStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.lang.dart";

    /// <inheritdoc/>
    protected override string[] GetToolchainBinaryNames() => new[] { "dart" };

    /// <inheritdoc/>
    public override string StrategyName => "Dart WASM Language";

    /// <inheritdoc/>
    public override WasmLanguageInfo LanguageInfo => new(
        Language: "Dart",
        LanguageVersion: "3.0+ (dart2wasm)",
        WasmTarget: "wasm32",
        Toolchain: "Dart SDK (built-in)",
        CompileCommand: "dart compile wasm source.dart -o output.wasm",
        WasiSupport: WasiSupportLevel.Partial,
        ComponentModelSupport: false,
        BinarySize: WasmBinarySize.Medium,
        PerformanceTier: PerformanceTier.NearNative,
        Notes: "Official dart2wasm compiler in Dart SDK. 3-10MB binaries. " +
               "Standalone WASM compilation (not just Flutter). " +
               "GC support via wasm-gc proposal."
    );

    /// <summary>
    /// Minimal valid WASM module representing a dart2wasm-compiled binary.
    /// Contains a single exported <c>_start</c> function that returns immediately (nop).
    /// </summary>
    /// <remarks>
    /// In production, this would be a dart2wasm-compiled module (3-10 MB) using wasm-gc.
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
