using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.WasmLanguages.Tier2;

/// <summary>
/// WASM language verification strategy for Swift, using the community-maintained SwiftWasm
/// toolchain fork that compiles Swift source code to WebAssembly targeting WASI Preview 1.
/// </summary>
/// <remarks>
/// <para>
/// Swift does not have official Apple WASM support. The SwiftWasm project is a community-maintained
/// fork of the Swift compiler that adds the <c>wasm32-unknown-wasi</c> target. Compiled binaries
/// are 10-30 MB due to the Swift runtime being included. The project is under active development.
/// </para>
/// <para>
/// Key characteristics:
/// </para>
/// <list type="bullet">
/// <item><description>Community-maintained SwiftWasm fork (not official Apple toolchain)</description></item>
/// <item><description>Large binaries (10-30 MB) due to embedded Swift runtime</description></item>
/// <item><description>WASI Preview 1 partial support for system interface</description></item>
/// <item><description>Near-native performance via AOT compilation to WASM</description></item>
/// <item><description>Active development with growing ecosystem support</description></item>
/// </list>
/// </remarks>
internal sealed class SwiftWasmLanguageStrategy : WasmLanguageStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.lang.swift";

    /// <inheritdoc/>
    public override string StrategyName => "Swift WASM Language";

    /// <inheritdoc/>
    public override WasmLanguageInfo LanguageInfo => new(
        Language: "Swift",
        LanguageVersion: "5.9+ (SwiftWasm)",
        WasmTarget: "wasm32-wasi",
        Toolchain: "SwiftWasm toolchain (community fork)",
        CompileCommand: "swiftc -target wasm32-unknown-wasi source.swift -o output.wasm",
        WasiSupport: WasiSupportLevel.Partial,
        ComponentModelSupport: false,
        BinarySize: WasmBinarySize.Large,
        PerformanceTier: PerformanceTier.NearNative,
        Notes: "Community-maintained SwiftWasm fork. 10-30MB binaries due to Swift runtime. " +
               "WASI Preview 1 support. Not official Apple toolchain. Active development."
    );

    /// <summary>
    /// Minimal valid WASM module representing a SwiftWasm-compiled binary.
    /// Contains a single exported <c>_start</c> function that returns immediately (nop).
    /// </summary>
    /// <remarks>
    /// In production, this would be a SwiftWasm-compiled module (10-30 MB) including the Swift runtime.
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
