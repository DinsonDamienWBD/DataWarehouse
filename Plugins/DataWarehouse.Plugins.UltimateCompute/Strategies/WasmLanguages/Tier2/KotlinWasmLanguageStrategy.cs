using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.WasmLanguages.Tier2;

/// <summary>
/// WASM language verification strategy for Kotlin, using the official Kotlin/Wasm target
/// from JetBrains that compiles Kotlin code to WebAssembly leveraging the wasm-gc proposal.
/// </summary>
/// <remarks>
/// <para>
/// Kotlin/Wasm is an official compilation target introduced in Kotlin 1.9+ by JetBrains.
/// It compiles Kotlin source code to WebAssembly using the wasm-gc (garbage collection) proposal,
/// producing 5-15 MB binaries. The target is rapidly evolving and WASI support is partial
/// and experimental.
/// </para>
/// <para>
/// Key characteristics:
/// </para>
/// <list type="bullet">
/// <item><description>Official JetBrains Kotlin/Wasm target (2023+)</description></item>
/// <item><description>Targets the wasm-gc proposal for garbage collection</description></item>
/// <item><description>Medium binaries (5-15 MB) with near-native performance</description></item>
/// <item><description>Rapidly evolving: API and toolchain may change between releases</description></item>
/// <item><description>Partial and experimental WASI support</description></item>
/// </list>
/// </remarks>
internal sealed class KotlinWasmLanguageStrategy : WasmLanguageStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.lang.kotlin";

    /// <inheritdoc/>
    public override string StrategyName => "Kotlin WASM Language";

    /// <inheritdoc/>
    public override WasmLanguageInfo LanguageInfo => new(
        Language: "Kotlin",
        LanguageVersion: "1.9+ (Kotlin/Wasm)",
        WasmTarget: "wasm32",
        Toolchain: "Kotlin compiler with Wasm target (official JetBrains)",
        CompileCommand: "kotlin-wasm compiler target or Gradle: kotlin { wasm { ... } }",
        WasiSupport: WasiSupportLevel.Partial,
        ComponentModelSupport: false,
        BinarySize: WasmBinarySize.Medium,
        PerformanceTier: PerformanceTier.NearNative,
        Notes: "Official Kotlin/Wasm target from JetBrains (2023+). " +
               "Currently targets wasm-gc proposal. 5-15MB binaries. " +
               "Evolving rapidly. WASI support is partial and experimental."
    );

    /// <summary>
    /// Minimal valid WASM module representing a Kotlin/Wasm-compiled binary.
    /// Contains a single exported <c>_start</c> function that returns immediately (nop).
    /// </summary>
    /// <remarks>
    /// In production, this would be a Kotlin/Wasm-compiled module (5-15 MB) using wasm-gc.
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
