using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.WasmLanguages.Tier1;

/// <summary>
/// WASM language verification strategy for C++, compiling via the WASI SDK (wasi-sdk) Clang++
/// toolchain with full C++ standard library support and WASI system interface access.
/// </summary>
/// <remarks>
/// <para>
/// C++ compiles to WASM using the same WASI SDK as C, but with the C++ compiler frontend
/// (<c>clang++</c>) and C++ standard library support. Binaries are larger than C due to
/// C++ runtime inclusion (exceptions, RTTI, standard library).
/// </para>
/// <para>
/// Key characteristics:
/// </para>
/// <list type="bullet">
/// <item><description>Full C++ stdlib support via WASI SDK and libc++</description></item>
/// <item><description>Medium-sized binaries (2-10 MB) due to C++ runtime overhead</description></item>
/// <item><description>Full WASI support through wasi-libc sysroot</description></item>
/// <item><description>Native-tier performance for compute-intensive workloads</description></item>
/// <item><description>Exception handling adds binary size overhead</description></item>
/// </list>
/// </remarks>
internal sealed class CppWasmLanguageStrategy : WasmLanguageStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.lang.cpp";

    /// <inheritdoc/>
    public override string StrategyName => "C++ WASM Language";

    /// <inheritdoc/>
    public override WasmLanguageInfo LanguageInfo => new(
        Language: "C++",
        LanguageVersion: "C++17/C++20",
        WasmTarget: "wasm32-wasi",
        Toolchain: "WASI SDK (wasi-sdk)",
        CompileCommand: "$WASI_SDK_PATH/bin/clang++ --sysroot=$WASI_SDK_PATH/share/wasi-sysroot -o output.wasm input.cpp",
        WasiSupport: WasiSupportLevel.Full,
        ComponentModelSupport: false,
        BinarySize: WasmBinarySize.Medium,
        PerformanceTier: PerformanceTier.Native,
        Notes: "Full C++ stdlib support via WASI SDK. Larger binaries than C due to C++ runtime."
    );

    /// <summary>
    /// Minimal valid WASM module representing a C++-compiled WASI binary.
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
