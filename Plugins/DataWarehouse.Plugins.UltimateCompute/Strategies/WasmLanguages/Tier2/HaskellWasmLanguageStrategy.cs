using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.WasmLanguages.Tier2;

/// <summary>
/// WASM language verification strategy for Haskell, using the GHC WASM backend
/// (<c>ghc-wasm-meta</c>) that compiles Haskell source code to WebAssembly targeting
/// wasm32-wasi with experimental WASI Preview 1 support.
/// </summary>
/// <remarks>
/// <para>
/// The GHC WASM backend (available from GHC 9.10+) is an active development effort to add
/// a native WebAssembly code generation backend to GHC. It requires a custom GHC build
/// configured with the WASM backend via the <c>ghc-wasm-meta</c> tooling. Compiled binaries
/// are large (10-50 MB) due to the Haskell runtime system (RTS) being embedded, including
/// the garbage collector and lazy evaluation machinery.
/// </para>
/// <para>
/// Key characteristics:
/// </para>
/// <list type="bullet">
/// <item><description>GHC 9.10+ WASM backend in active development via <c>ghc-wasm-meta</c></description></item>
/// <item><description>Large binaries (10-50 MB) due to Haskell RTS, GC, and lazy evaluation runtime</description></item>
/// <item><description>Experimental WASI support; not all system calls are mapped</description></item>
/// <item><description>Near-native performance for pure computation; GC is heap-intensive in WASM linear memory</description></item>
/// <item><description>Requires custom GHC build with WASM cross-compilation target</description></item>
/// </list>
/// </remarks>
internal sealed class HaskellWasmLanguageStrategy : WasmLanguageStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.lang.haskell";

    /// <inheritdoc/>
    public override string StrategyName => "Haskell WASM Language";

    /// <inheritdoc/>
    public override WasmLanguageInfo LanguageInfo => new(
        Language: "Haskell",
        LanguageVersion: "GHC 9.10+",
        WasmTarget: "wasm32-wasi",
        Toolchain: "GHC WASM backend (ghc-wasm-meta)",
        CompileCommand: "wasm32-wasi-ghc -o output.wasm Main.hs",
        WasiSupport: WasiSupportLevel.Experimental,
        ComponentModelSupport: false,
        BinarySize: WasmBinarySize.Large,
        PerformanceTier: PerformanceTier.NearNative,
        Notes: "GHC WASM backend in active development (GHC 9.10+). 10-50MB binaries due to " +
               "Haskell runtime. Requires custom GHC build with WASM backend. Lazy evaluation " +
               "works but GC is heap-intensive in WASM linear memory."
    );

    /// <summary>
    /// Minimal valid WASM module representing a GHC WASM backend-compiled Haskell binary.
    /// Contains a single exported <c>_start</c> function that returns immediately (nop).
    /// </summary>
    /// <remarks>
    /// In production, this would be a GHC-compiled module (10-50 MB) including the Haskell RTS.
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
