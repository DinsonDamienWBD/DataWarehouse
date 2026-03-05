using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.WasmLanguages.Tier2;

/// <summary>
/// WASM language verification strategy for MoonBit, a WASM-native language producing
/// industry-leading compact binaries with a Rust-like type system and static typing.
/// </summary>
/// <remarks>
/// <para>
/// MoonBit is designed specifically for WebAssembly and produces the smallest WASM output of any
/// high-level language (1 KB to 100 KB). It features a Rust-like type system with static typing,
/// pattern matching, and garbage collection via the wasm-gc proposal. The MoonBit toolchain
/// (<c>moon build</c>) compiles directly to WASM with full WASI support.
/// </para>
/// <para>
/// Key characteristics:
/// </para>
/// <list type="bullet">
/// <item><description>WASM-native language designed specifically for WebAssembly</description></item>
/// <item><description>Tiny binaries (1 KB to 100 KB) -- smallest of any high-level language</description></item>
/// <item><description>Full WASI support with native system interface integration</description></item>
/// <item><description>Native-equivalent performance with zero-overhead abstractions</description></item>
/// <item><description>Rust-like type system with pattern matching and wasm-gc support</description></item>
/// </list>
/// </remarks>
internal sealed class MoonBitWasmLanguageStrategy : WasmLanguageStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.lang.moonbit";

    /// <inheritdoc/>
    protected override string[] GetToolchainBinaryNames() => new[] { "moon" };

    /// <inheritdoc/>
    public override string StrategyName => "MoonBit WASM Language";

    /// <inheritdoc/>
    public override WasmLanguageInfo LanguageInfo => new(
        Language: "MoonBit",
        LanguageVersion: "2024+",
        WasmTarget: "wasm32",
        Toolchain: "MoonBit toolchain (moonbitlang.com)",
        CompileCommand: "moon build --target wasm",
        WasiSupport: WasiSupportLevel.Full,
        ComponentModelSupport: false,
        BinarySize: WasmBinarySize.Tiny,
        PerformanceTier: PerformanceTier.Native,
        Notes: "WASM-native language producing industry-leading compact binaries (1KB-100KB). " +
               "Designed specifically for WASM with Rust-like type system. Static typing, " +
               "pattern matching, GC via wasm-gc. Smallest WASM output of any high-level language."
    );

    /// <summary>
    /// Minimal valid WASM module representing a MoonBit-compiled binary.
    /// Contains a single exported <c>_start</c> function that returns immediately (nop).
    /// </summary>
    /// <remarks>
    /// In production, this would be a MoonBit-compiled module (1-100 KB) with minimal runtime.
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
