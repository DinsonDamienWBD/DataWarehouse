using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.WasmLanguages.Tier3;

/// <summary>
/// WASM language verification strategy for Scala, using indirect compilation paths through
/// TeaVM (JVM bytecode to WASM) or Scala.js + Javy (JavaScript to WASM).
/// </summary>
/// <remarks>
/// <para>
/// Scala has no direct compiler targeting WebAssembly. Two indirect paths exist: (1) Compile Scala
/// to JVM bytecode, then use TeaVM to convert bytecode to WASM; (2) Compile Scala to JavaScript
/// via Scala.js, then embed the JavaScript in WASM using Javy. Both approaches add overhead and
/// produce large binaries. TeaVM is the more practical path but does not support all JVM features.
/// </para>
/// <para>
/// Feasibility: Medium. Indirect paths work but add complexity and overhead. No direct
/// Scala-to-WASM compiler exists. TeaVM is the most viable option.
/// </para>
/// <list type="bullet">
/// <item><description>No direct Scala-to-WASM compiler; indirect paths only</description></item>
/// <item><description>TeaVM path: Scala -> JVM bytecode -> WASM (most practical)</description></item>
/// <item><description>Scala.js + Javy path: Scala -> JS -> WASM (higher overhead)</description></item>
/// <item><description>Large binaries due to runtime embedding in both paths</description></item>
/// <item><description>Experimental WASI support via TeaVM WASM output</description></item>
/// </list>
/// </remarks>
internal sealed class ScalaWasmLanguageStrategy : WasmLanguageStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.lang.scala";

    /// <inheritdoc/>
    public override string StrategyName => "Scala WASM Language";

    /// <inheritdoc/>
    public override WasmLanguageInfo LanguageInfo => new(
        Language: "Scala",
        LanguageVersion: "3.x",
        WasmTarget: "wasm32 (indirect via Scala.js or TeaVM)",
        Toolchain: "TeaVM (JVM bytecode to WASM) or Scala.js + Javy",
        CompileCommand: "scalac to .class files then java -jar teavm-cli.jar --target wasm (TeaVM path)",
        WasiSupport: WasiSupportLevel.Experimental,
        ComponentModelSupport: false,
        BinarySize: WasmBinarySize.Large,
        PerformanceTier: PerformanceTier.NearNative,
        Notes: "No direct Scala-to-WASM compiler. Indirect paths: (1) Scala bytecode via " +
               "TeaVM to WASM, (2) Scala.js to JS then Javy to WASM. Both add overhead. " +
               "Medium feasibility."
    );

    /// <summary>
    /// Minimal valid WASM module representing a TeaVM-compiled Scala binary.
    /// Contains a single exported <c>_start</c> function that returns immediately (nop).
    /// </summary>
    /// <remarks>
    /// In production, this would be a TeaVM-compiled module from Scala bytecode.
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
