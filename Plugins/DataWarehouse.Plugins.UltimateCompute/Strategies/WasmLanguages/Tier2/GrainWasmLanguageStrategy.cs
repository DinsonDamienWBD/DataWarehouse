using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.WasmLanguages.Tier2;

/// <summary>
/// WASM language verification strategy for Grain, a functional programming language designed
/// from the ground up for WebAssembly with full WASI support and compact binary output.
/// </summary>
/// <remarks>
/// <para>
/// Grain is a WASM-native language that compiles directly to WebAssembly without an intermediate
/// representation or transpilation step. It produces small binaries (50 KB to 1 MB) with full WASI
/// Preview 1 support. As a purpose-built WASM language, Grain features pattern matching, algebraic
/// data types, and a functional programming model optimized for the WASM execution model.
/// </para>
/// <para>
/// Key characteristics:
/// </para>
/// <list type="bullet">
/// <item><description>WASM-native language designed specifically for WebAssembly</description></item>
/// <item><description>Small binaries (50 KB to 1 MB) with excellent startup performance</description></item>
/// <item><description>Full WASI Preview 1 support for file I/O, environment, clocks, and random</description></item>
/// <item><description>Functional language with pattern matching and algebraic data types</description></item>
/// <item><description>Near-native performance with no runtime overhead beyond GC</description></item>
/// </list>
/// </remarks>
internal sealed class GrainWasmLanguageStrategy : WasmLanguageStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.lang.grain";

    /// <inheritdoc/>
    protected override string[] GetToolchainBinaryNames() => new[] { "grain" };

    /// <inheritdoc/>
    public override string StrategyName => "Grain WASM Language";

    /// <inheritdoc/>
    public override WasmLanguageInfo LanguageInfo => new(
        Language: "Grain",
        LanguageVersion: "0.6+",
        WasmTarget: "wasm32-wasi",
        Toolchain: "Grain compiler (grain-lang.org)",
        CompileCommand: "grain compile source.gr -o output.wasm",
        WasiSupport: WasiSupportLevel.Full,
        ComponentModelSupport: false,
        BinarySize: WasmBinarySize.Small,
        PerformanceTier: PerformanceTier.NearNative,
        Notes: "Designed from the ground up for WebAssembly. Small binaries (50KB-1MB). " +
               "Full WASI support. Functional language with pattern matching, algebraic data " +
               "types. Excellent WASM integration."
    );

    /// <summary>
    /// Minimal valid WASM module representing a Grain-compiled binary.
    /// Contains a single exported <c>_start</c> function that returns immediately (nop).
    /// </summary>
    /// <remarks>
    /// In production, this would be a Grain-compiled module (50 KB to 1 MB) with Grain runtime.
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
