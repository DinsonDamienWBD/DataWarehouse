namespace DataWarehouse.Plugins.UltimateCompute.Strategies.WasmLanguages;

/// <summary>
/// Defines the level of WASI (WebAssembly System Interface) support provided by a language's WASM toolchain.
/// </summary>
/// <remarks>
/// WASI provides a standardized system interface for WebAssembly modules, enabling file I/O,
/// environment variables, clocks, and random number generation in a capability-based security model.
/// </remarks>
internal enum WasiSupportLevel
{
    /// <summary>
    /// Full WASI support with complete system interface coverage (preview1 and/or preview2).
    /// Languages with full support can access files, environment, clocks, and random APIs.
    /// </summary>
    Full,

    /// <summary>
    /// Partial WASI support with some system interface features available.
    /// Some WASI APIs may be missing or incomplete.
    /// </summary>
    Partial,

    /// <summary>
    /// Experimental WASI support that may be unstable or incomplete.
    /// Not recommended for production workloads.
    /// </summary>
    Experimental,

    /// <summary>
    /// No WASI support. The language compiles to pure WASM without system interface access.
    /// Host interaction must be provided via custom import functions.
    /// </summary>
    None
}

/// <summary>
/// Categorizes the typical compiled WASM binary size produced by a language's toolchain.
/// </summary>
/// <remarks>
/// Binary size affects cold-start latency, memory usage, and deployment bandwidth.
/// Smaller binaries are preferred for serverless and edge computing scenarios.
/// </remarks>
internal enum WasmBinarySize
{
    /// <summary>
    /// Tiny binaries, typically under 200 KB (e.g., AssemblyScript, hand-written WAT).
    /// </summary>
    Tiny,

    /// <summary>
    /// Small binaries, typically 200 KB to 2 MB (e.g., Rust, C, Zig).
    /// </summary>
    Small,

    /// <summary>
    /// Medium binaries, typically 2 MB to 10 MB (e.g., C++, TinyGo).
    /// </summary>
    Medium,

    /// <summary>
    /// Large binaries, typically 10 MB to 30 MB (e.g., .NET WASM).
    /// </summary>
    Large,

    /// <summary>
    /// Very large binaries, typically over 30 MB (e.g., standard Go WASM).
    /// </summary>
    VeryLarge
}

/// <summary>
/// Categorizes the runtime performance tier of a language's WASM output compared to native execution.
/// </summary>
/// <remarks>
/// Performance tier is determined by how close the WASM output runs to equivalent native code,
/// accounting for AOT/JIT compilation capabilities of the target runtime (wasmtime, wasmer, etc.).
/// </remarks>
internal enum PerformanceTier
{
    /// <summary>
    /// Native-equivalent performance. The compiled WASM runs within 5-15% of native code.
    /// Achieved by languages with zero-cost abstractions and no runtime overhead (Rust, C, C++, Zig).
    /// </summary>
    Native,

    /// <summary>
    /// Near-native performance. The compiled WASM runs within 15-40% of native code.
    /// Typical for languages with managed runtimes or garbage collectors compiled to WASM (.NET, Go, AssemblyScript).
    /// </summary>
    NearNative,

    /// <summary>
    /// Interpreted-level performance. The language runs an interpreter inside WASM.
    /// Significant overhead due to double interpretation (WASM runtime interpreting a language interpreter).
    /// </summary>
    Interpreted,

    /// <summary>
    /// Slow performance tier. The WASM output has significant overhead compared to native execution.
    /// </summary>
    Slow
}

/// <summary>
/// Contains metadata about a programming language's WebAssembly compilation support,
/// including toolchain information, compilation commands, and runtime characteristics.
/// </summary>
/// <remarks>
/// <para>
/// Each WASM language strategy provides a <see cref="WasmLanguageInfo"/> record describing
/// how to compile the language to WASM, what WASI support is available, expected binary sizes,
/// and performance characteristics.
/// </para>
/// <para>
/// This metadata enables the system to make informed decisions about which language to use
/// for specific workloads based on binary size, performance, and WASI requirements.
/// </para>
/// </remarks>
/// <param name="Language">The programming language name (e.g., "Rust", "C", "Zig").</param>
/// <param name="LanguageVersion">The minimum language version required for WASM support (e.g., "1.75+", "C11/C17").</param>
/// <param name="WasmTarget">The WASM compilation target triple (e.g., "wasm32-wasi", "wasm32").</param>
/// <param name="Toolchain">The installation command or toolchain name required to compile to WASM.</param>
/// <param name="CompileCommand">The CLI command to compile source code to a .wasm binary.</param>
/// <param name="WasiSupport">The level of WASI support provided by the language's WASM toolchain.</param>
/// <param name="ComponentModelSupport">Whether the language supports the WASM Component Model for composable modules.</param>
/// <param name="BinarySize">The typical compiled WASM binary size category.</param>
/// <param name="PerformanceTier">The expected runtime performance tier of the compiled WASM output.</param>
/// <param name="Notes">Additional notes about the language's WASM support, limitations, or recommendations.</param>
internal sealed record WasmLanguageInfo(
    string Language,
    string LanguageVersion,
    string WasmTarget,
    string Toolchain,
    string CompileCommand,
    WasiSupportLevel WasiSupport,
    bool ComponentModelSupport,
    WasmBinarySize BinarySize,
    PerformanceTier PerformanceTier,
    string Notes
);

/// <summary>
/// Represents the result of verifying a WASM language's compilation and execution pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Verification tests the entire pipeline: toolchain availability, compilation to .wasm,
/// and execution via the wasmtime runtime. Each step is independently tracked to pinpoint failures.
/// </para>
/// </remarks>
/// <param name="Language">The language that was verified.</param>
/// <param name="ToolchainAvailable">Whether the language's WASM toolchain is installed and accessible.</param>
/// <param name="CompilationSuccessful">Whether the sample source compiled to a valid .wasm binary.</param>
/// <param name="ExecutionSuccessful">Whether the compiled .wasm module executed successfully via wasmtime.</param>
/// <param name="ActualOutput">The actual stdout output from executing the WASM module, or null if execution failed.</param>
/// <param name="ExpectedOutput">The expected output for comparison, or null if not applicable.</param>
/// <param name="ExecutionTime">The wall-clock time taken to execute the WASM module, or null if execution failed.</param>
/// <param name="BinarySizeBytes">The size of the compiled .wasm binary in bytes, or null if compilation failed.</param>
/// <param name="ErrorMessage">A human-readable error message if any step failed, or null on success.</param>
internal sealed record WasmLanguageVerificationResult(
    string Language,
    bool ToolchainAvailable,
    bool CompilationSuccessful,
    bool ExecutionSuccessful,
    string? ActualOutput,
    string? ExpectedOutput,
    TimeSpan? ExecutionTime,
    long? BinarySizeBytes,
    string? ErrorMessage
);
