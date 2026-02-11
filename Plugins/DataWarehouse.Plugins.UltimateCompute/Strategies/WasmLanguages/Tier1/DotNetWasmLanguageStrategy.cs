using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.WasmLanguages.Tier1;

/// <summary>
/// WASM language verification strategy for .NET (C#/F#), compiling via the experimental
/// <c>wasi-experimental</c> workload that produces WASM modules with embedded .NET runtime.
/// </summary>
/// <remarks>
/// <para>
/// .NET 8+ supports WASM compilation through two paths: Blazor WebAssembly (browser) and the
/// experimental WASI workload (server-side). The WASI workload compiles .NET assemblies with
/// the Mono runtime into standalone WASM modules that can run in any WASI-compatible host.
/// </para>
/// <para>
/// Key characteristics:
/// </para>
/// <list type="bullet">
/// <item><description>Large binaries (5-20 MB) due to embedded .NET/Mono runtime</description></item>
/// <item><description>Full WASI support via the <c>wasi-experimental</c> workload</description></item>
/// <item><description>Near-native performance after JIT/AOT compilation</description></item>
/// <item><description>Component Model support is emerging but not yet production-ready</description></item>
/// <item><description>Supports C# and F# as source languages</description></item>
/// </list>
/// </remarks>
internal sealed class DotNetWasmLanguageStrategy : WasmLanguageStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.lang.dotnet";

    /// <inheritdoc/>
    public override string StrategyName => ".NET WASM Language";

    /// <inheritdoc/>
    public override WasmLanguageInfo LanguageInfo => new(
        Language: ".NET (C#/F#)",
        LanguageVersion: ".NET 8+",
        WasmTarget: "wasm",
        Toolchain: "dotnet workload install wasi-experimental",
        CompileCommand: "dotnet build -c Release -r wasi-wasm",
        WasiSupport: WasiSupportLevel.Full,
        ComponentModelSupport: false,
        BinarySize: WasmBinarySize.Large,
        PerformanceTier: PerformanceTier.NearNative,
        Notes: "Large binaries (5-20MB) due to .NET runtime inclusion. WASI support is experimental workload."
    );

    /// <summary>
    /// Minimal valid WASM module representing a .NET-compiled WASI binary.
    /// Contains a single exported <c>_start</c> function that returns immediately.
    /// </summary>
    /// <remarks>
    /// In practice, .NET WASM binaries are much larger (5-20 MB) because they embed the Mono runtime.
    /// This minimal module validates the execution pipeline without requiring the full .NET WASM toolchain.
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
