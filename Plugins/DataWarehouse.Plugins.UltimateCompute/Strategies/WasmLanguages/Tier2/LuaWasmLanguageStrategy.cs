using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.WasmLanguages.Tier2;

/// <summary>
/// WASM language verification strategy for Lua, using the interpreter-in-WASM approach
/// where the Lua VM is compiled to WebAssembly using the WASI SDK. Lua's minimal runtime
/// makes it well-suited for WASM with compact 500 KB - 3 MB binaries.
/// </summary>
/// <remarks>
/// <para>
/// Lua does not compile directly to WASM. The Lua interpreter (written in C) is compiled to
/// WASM using the WASI SDK's Clang compiler. Due to Lua's exceptionally lightweight runtime
/// (~250 KB C source), the resulting WASM binaries are much smaller than Python, Ruby, or PHP.
/// Nelua (a compiled Lua subset) can also target WASM directly for AOT compilation.
/// </para>
/// <para>
/// Key characteristics:
/// </para>
/// <list type="bullet">
/// <item><description>Interpreter-in-WASM: Lua VM compiled to WASM via WASI SDK</description></item>
/// <item><description>Small binaries (500 KB - 3 MB) due to Lua's minimal runtime</description></item>
/// <item><description>Lightweight and fast startup compared to Python/Ruby/PHP</description></item>
/// <item><description>Nelua alternative for compiled Lua subset targeting WASM</description></item>
/// <item><description>WASI Preview 1 partial support for basic system interface</description></item>
/// </list>
/// </remarks>
internal sealed class LuaWasmLanguageStrategy : WasmLanguageStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.wasm.lang.lua";

    /// <inheritdoc/>
    public override string StrategyName => "Lua WASM Language";

    /// <inheritdoc/>
    public override WasmLanguageInfo LanguageInfo => new(
        Language: "Lua",
        LanguageVersion: "5.4",
        WasmTarget: "wasm32-wasi",
        Toolchain: "Compile Lua interpreter with WASI SDK or use wasmoon/wasm_lua",
        CompileCommand: "$WASI_SDK_PATH/bin/clang --sysroot=$WASI_SDK_PATH/share/wasi-sysroot -o lua.wasm lua_sources/*.c",
        WasiSupport: WasiSupportLevel.Partial,
        ComponentModelSupport: false,
        BinarySize: WasmBinarySize.Small,
        PerformanceTier: PerformanceTier.Interpreted,
        Notes: "Lightweight interpreter-in-WASM. 500KB-3MB binaries (much smaller than Python/Ruby). " +
               "Lua's minimal runtime is well-suited for WASM. " +
               "Nelua (compiled Lua subset) can also target WASM directly."
    );

    /// <summary>
    /// Minimal valid WASM module representing a Lua VM-in-WASM binary.
    /// Contains a single exported <c>_start</c> function that returns immediately (nop).
    /// </summary>
    /// <remarks>
    /// In production, this would be the Lua interpreter compiled to WASM (500 KB - 3 MB).
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
