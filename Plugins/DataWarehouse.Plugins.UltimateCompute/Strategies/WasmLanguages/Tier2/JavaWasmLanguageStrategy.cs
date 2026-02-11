using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.WasmLanguages.Tier2;

/// <summary>
/// WASM language verification strategy for Java, using TeaVM to transpile JVM bytecode
/// to WebAssembly. TeaVM compiles <c>.class</c> files (from any JVM language) to WASM
/// with near-native performance but limited JVM feature support.
/// </summary>
/// <remarks>
/// <para>
/// Java does not have a native WASM target in the JDK. TeaVM is a third-party AOT compiler
/// that transpiles JVM bytecode to WebAssembly, producing 5-20 MB binaries. It does not
/// include a full JVM: reflection is limited, dynamic class loading is not supported,
/// and not all standard library classes are available.
/// </para>
/// <para>
/// Key characteristics:
/// </para>
/// <list type="bullet">
/// <item><description>TeaVM transpiles JVM bytecode to WASM (not source-level compilation)</description></item>
/// <item><description>Medium binaries (5-20 MB) depending on application complexity</description></item>
/// <item><description>Limited JVM features: no reflection, no dynamic class loading</description></item>
/// <item><description>j2wasm (Google) is an alternative Java-to-WASM compiler</description></item>
/// <item><description>Kotlin/Wasm is preferred for JVM-family WASM workloads</description></item>
/// </list>
/// </remarks>
internal sealed class JavaWasmLanguageStrategy : WasmLanguageStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.lang.java";

    /// <inheritdoc/>
    public override string StrategyName => "Java WASM Language";

    /// <inheritdoc/>
    public override WasmLanguageInfo LanguageInfo => new(
        Language: "Java",
        LanguageVersion: "17+ (via TeaVM)",
        WasmTarget: "wasm32",
        Toolchain: "TeaVM compiler (bytecode to WASM transpiler)",
        CompileCommand: "java -jar teavm-cli.jar --target wasm -d output/ MyClass.class",
        WasiSupport: WasiSupportLevel.Partial,
        ComponentModelSupport: false,
        BinarySize: WasmBinarySize.Medium,
        PerformanceTier: PerformanceTier.NearNative,
        Notes: "TeaVM transpiles JVM bytecode to WASM. 5-20MB binaries. " +
               "Not full JVM -- limited reflection, no dynamic class loading. " +
               "j2wasm (Google) is an alternative. Kotlin/Wasm is preferred for JVM-family WASM."
    );

    /// <summary>
    /// Minimal valid WASM module representing a TeaVM-compiled Java binary.
    /// Contains a single exported <c>_start</c> function that returns immediately (nop).
    /// </summary>
    /// <remarks>
    /// In production, this would be a TeaVM-compiled module (5-20 MB) from JVM bytecode.
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
