# Phase 20: WASM/WASI Language Ecosystem - Research

**Researched:** 2026-02-11
**Domain:** WebAssembly/WASI language compilation ecosystem + DW compute runtime integration
**Confidence:** HIGH

## Summary

Phase 20 verifies that the DataWarehouse's existing WASM/WASI compute-on-data runtime supports all 30+ languages that can compile to WebAssembly. The codebase already has substantial WASM infrastructure: the `Compute.Wasm` plugin (T70) provides a full WASM interpreter with function registry, trigger system, chaining, hot reload, and storage API bindings; the `UltimateCompute` plugin (T111) provides 7 WASM runtime strategies (Wasmtime, Wasmer, Wazero, WasmEdge, WASI, WASI-NN, Component Model) that invoke external CLI runtimes. The `UltimateSDKPorts` plugin (T136) already has binding code generators for Rust (FFI, Tokio, Tonic, WASM), Python, JavaScript, and Go.

The core work in this phase is NOT building new runtime infrastructure -- that exists. It is: (1) creating per-language verification strategies that define how each language compiles to WASM and executes in DW's runtime, (2) providing sample compute-on-data functions per language, (3) adding language-specific SDK binding documentation, and (4) running performance benchmarks. The verification strategies should follow the existing `ComputeRuntimeStrategyBase` pattern from `UltimateCompute` and will live as new strategy classes.

**Primary recommendation:** Create language verification strategy classes in `UltimateCompute/Strategies/WasmLanguages/` following the `internal sealed class : ComputeRuntimeStrategyBase` pattern. Each strategy encapsulates: compiler toolchain info, compilation command, sample WASM module bytes (or generation), and execution via the existing Wasmtime/Wasmer/WasmEdge CLI runners. The `LanguageInfo` records already defined in `WasmComputePlugin.SupportedLanguages` provide the template for what each language entry needs.

## Standard Stack

### Core (Already Exists)
| Component | Location | Purpose | Status |
|-----------|----------|---------|--------|
| `ComputeRuntimeStrategyBase` | `UltimateCompute/ComputeRuntimeStrategyBase.cs` | Base class for all compute strategies | Complete |
| `ComputeRuntimeStrategyRegistry` | `UltimateCompute/ComputeRuntimeStrategyRegistry.cs` | Auto-discovery via assembly scanning | Complete |
| `WasmtimeStrategy` | `UltimateCompute/Strategies/Wasm/WasmtimeStrategy.cs` | Wasmtime CLI execution | Complete |
| `WasmerStrategy` | `UltimateCompute/Strategies/Wasm/WasmerStrategy.cs` | Wasmer CLI execution | Complete |
| `WasmEdgeStrategy` | `UltimateCompute/Strategies/Wasm/WasmEdgeStrategy.cs` | WasmEdge CLI execution | Complete |
| `WazeroStrategy` | `UltimateCompute/Strategies/Wasm/WazeroStrategy.cs` | Wazero CLI execution | Complete |
| `WasiStrategy` | `UltimateCompute/Strategies/Wasm/WasiStrategy.cs` | WASI runtime | Complete |
| `WasiNnStrategy` | `UltimateCompute/Strategies/Wasm/WasiNnStrategy.cs` | WASI-NN ML inference | Complete |
| `WasmComponentStrategy` | `UltimateCompute/Strategies/Wasm/WasmComponentStrategy.cs` | Component Model | Complete |
| `WasmComputePlugin` | `Compute.Wasm/WasmComputePlugin.cs` | Full WASM interpreter + function registry (T70) | Complete |
| `IComputeRuntimeStrategy` | `SDK/Contracts/Compute/IComputeRuntimeStrategy.cs` | Strategy interface | Complete |
| `ComputeTask` / `ComputeResult` | `SDK/Contracts/Compute/ComputeTypes.cs` | Task/Result records | Complete |
| `ComputeCapabilities` | `SDK/Contracts/Compute/ComputeCapabilities.cs` | Capability descriptors | Complete |

### SDK Contracts for WASM
| Contract | Location | Purpose |
|----------|----------|---------|
| `ComputeRuntime` enum | `ComputeTypes.cs` | Runtime type identifier (WASM = 1) |
| `ResourceLimits` record | `ComputeTypes.cs` | Memory/CPU/time limits |
| `ComputeCapabilities` record | `ComputeCapabilities.cs` | Language list, sandbox, streaming flags |
| `MemoryIsolationLevel` enum | `ComputeCapabilities.cs` | Sandbox/Isolate/Process/Container/VM |
| `WasmFunctionPluginBase` | `ActiveStoragePluginBases.cs` | SDK base for WASM compute plugins |
| `LanguageInfo` class | `WasmComputePlugin.cs` | Language, version, toolchain, compiler command |

### Existing SDK Ports (Language Bindings)
| Plugin | Languages | Status |
|--------|-----------|--------|
| `UltimateSDKPorts/Strategies/RustBindings/` | Rust (FFI, Tokio, Tonic, WASM) | Complete |
| `UltimateSDKPorts/Strategies/PythonBindings/` | Python | Complete |
| `UltimateSDKPorts/Strategies/JavaScriptBindings/` | JavaScript | Complete |
| `UltimateSDKPorts/Strategies/GoBindings/` | Go | Complete |
| `UltimateSDKPorts/Strategies/CrossLanguage/` | Cross-language interop | Complete |

## Architecture Patterns

### Recommended Project Structure for New Code
```
Plugins/DataWarehouse.Plugins.UltimateCompute/
  Strategies/
    WasmLanguages/
      Tier1/
        RustWasmLanguageStrategy.cs
        CWasmLanguageStrategy.cs
        CppWasmLanguageStrategy.cs
        DotNetWasmLanguageStrategy.cs
        GoWasmLanguageStrategy.cs
        AssemblyScriptWasmLanguageStrategy.cs
        ZigWasmLanguageStrategy.cs
      Tier2/
        PythonWasmLanguageStrategy.cs
        RubyWasmLanguageStrategy.cs
        JavaScriptWasmLanguageStrategy.cs
        TypeScriptWasmLanguageStrategy.cs
        KotlinWasmLanguageStrategy.cs
        SwiftWasmLanguageStrategy.cs
        JavaWasmLanguageStrategy.cs
        DartWasmLanguageStrategy.cs
        PhpWasmLanguageStrategy.cs
        LuaWasmLanguageStrategy.cs
        HaskellWasmLanguageStrategy.cs
        OCamlWasmLanguageStrategy.cs
        GrainWasmLanguageStrategy.cs
        MoonBitWasmLanguageStrategy.cs
      Tier3/
        NimWasmLanguageStrategy.cs
        VWasmLanguageStrategy.cs
        CrystalWasmLanguageStrategy.cs
        PerlWasmLanguageStrategy.cs
        RWasmLanguageStrategy.cs
        FortranWasmLanguageStrategy.cs
        ScalaWasmLanguageStrategy.cs
        ElixirWasmLanguageStrategy.cs
        PrologWasmLanguageStrategy.cs
        AdaWasmLanguageStrategy.cs
    WasmLanguages/
      WasmLanguageStrategyBase.cs         # Shared base (extends ComputeRuntimeStrategyBase)
      WasmLanguageVerificationResult.cs   # Verification result types
      WasmLanguageBenchmark.cs            # Benchmark framework
```

### Pattern 1: Language Verification Strategy
**What:** Each language gets a strategy class that: identifies the toolchain, provides compilation metadata, generates/stores a sample WASM module, and executes it via the existing WASM runtime strategies.
**When to use:** For every language in Tier 1, 2, 3.

```csharp
// Pattern based on existing WasmtimeStrategy.cs and WasmComputePlugin.SupportedLanguages
internal sealed class RustWasmLanguageStrategy : ComputeRuntimeStrategyBase
{
    public override string StrategyId => "compute.wasm.lang.rust";
    public override string StrategyName => "Rust WASM Language";
    public override ComputeRuntime Runtime => ComputeRuntime.WASM;

    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: true,
        SupportsSandboxing: true,
        MaxMemoryBytes: 2L * 1024 * 1024 * 1024,
        MaxExecutionTime: TimeSpan.FromMinutes(5),
        SupportedLanguages: ["rust", "rust-wasm", "wasm32-unknown-unknown", "wasm32-wasi"],
        SupportsMultiThreading: true,
        SupportsAsync: true,
        SupportsNetworkAccess: false,
        SupportsFileSystemAccess: true,
        MaxConcurrentTasks: 100,
        SupportsPrecompilation: true,
        MemoryIsolation: MemoryIsolationLevel.Sandbox
    );

    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.WASM];

    // Language metadata
    public WasmLanguageInfo LanguageInfo => new(
        Language: "Rust",
        LanguageVersion: "1.75+",
        WasmTarget: "wasm32-wasi",
        Toolchain: "rustup target add wasm32-wasi",
        CompileCommand: "cargo build --target wasm32-wasi --release",
        WasiSupport: WasiSupportLevel.Full,
        ComponentModelSupport: true,
        BinarySize: WasmBinarySize.Small,
        PerformanceTier: PerformanceTier.Native,
        Notes: "Best WASM support. Smallest binaries. Full WASI + Component Model."
    );

    public override async Task InitializeAsync(CancellationToken ct = default)
    {
        // Verify Rust WASM toolchain available
        await IsToolAvailableAsync("rustc", "--version", ct);
        await IsToolAvailableAsync("wasmtime", "--version", ct);
    }

    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken ct = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            // Rust WASM modules come pre-compiled as .wasm bytes
            var wasmPath = Path.GetTempFileName() + ".wasm";
            try
            {
                await File.WriteAllBytesAsync(wasmPath, task.Code.ToArray(), ct);
                var args = $"run --wasi \"{wasmPath}\"";
                // ... standard wasmtime execution pattern
            }
            finally { /* cleanup */ }
        }, ct);
    }
}
```

### Pattern 2: WasmLanguageInfo Record
**What:** A record type that encapsulates all language-to-WASM compilation metadata.
**When to use:** Every language strategy exposes this.

```csharp
// New record type for language verification metadata
public record WasmLanguageInfo(
    string Language,
    string LanguageVersion,
    string WasmTarget,           // "wasm32-wasi", "wasm32-unknown-unknown", etc.
    string Toolchain,            // How to install the toolchain
    string CompileCommand,       // How to compile to .wasm
    WasiSupportLevel WasiSupport,
    bool ComponentModelSupport,
    WasmBinarySize BinarySize,
    PerformanceTier PerformanceTier,
    string Notes
);

public enum WasiSupportLevel { Full, Partial, Experimental, None }
public enum WasmBinarySize { Tiny, Small, Medium, Large, VeryLarge }
public enum PerformanceTier { Native, NearNative, Interpreted, Slow }
```

### Pattern 3: Sample Compute-on-Data Function Pattern
**What:** Each language needs a standardized sample function that reads input, processes it, and writes output using DW's storage API.
**When to use:** For verification of each language.

```csharp
// Standardized sample function template for each language
// Each language strategy provides:
// 1. Source code for a "hello-dw" function in that language
// 2. Pre-compiled .wasm bytes (embedded or generated)
// 3. Expected output for a known input

// The sample function does:
// - Accept JSON input via stdin: { "data": [1, 2, 3, 4, 5] }
// - Sum the array values
// - Return JSON output via stdout: { "result": 15, "language": "rust" }
```

### Pattern 4: Benchmark Strategy
**What:** Standardized benchmark that runs the same workload across all languages.
**When to use:** Plan 20-04 for performance comparison.

```csharp
// Benchmark workload: compute SHA-256 hash of 1MB data
// Metrics: execution time (ms), memory peak (bytes), binary size (bytes)
// Run on: Wasmtime, Wasmer, WasmEdge
```

### Anti-Patterns to Avoid
- **Building a new WASM runtime:** The runtime infrastructure already exists. Do NOT create new interpreter code. Use existing `WasmtimeStrategy`/`WasmerStrategy` CLI runners.
- **Direct plugin references:** All inter-plugin communication via message bus. Language strategies in UltimateCompute must NOT reference Compute.Wasm or UltimateSDKPorts directly.
- **Stub implementations:** Rule 13 requires production-ready code. Each language strategy must have real compilation metadata and real sample WASM bytes.
- **Adding NuGet packages for WASM runtimes:** The existing strategies use CLI invocation (`wasmtime`, `wasmer`, `wasmedge`), not embedded .NET WASM libraries. Keep this pattern.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| WASM execution | New interpreter or runtime | Existing `WasmtimeStrategy`, `WasmerStrategy`, `WasmEdgeStrategy` | 7 strategies already handle all WASM execution via CLI |
| Module validation | Custom WASM validator | Existing `WasmComputePlugin.ValidateModuleAsync()` | Already parses magic number, sections, imports, exports |
| Process runner | New process execution code | `ComputeRuntimeStrategyBase.RunProcessAsync()` | Base class already handles stdin/stdout, timeouts, cancellation |
| SDK bindings for Rust/Python/JS/Go | New binding generators | `UltimateSDKPorts` existing strategies | T136 already generates bindings for 4 languages |
| Strategy registration | Manual registry code | `ComputeRuntimeStrategyRegistry.DiscoverStrategies()` | Auto-discovers via assembly scanning |

**Key insight:** The existing infrastructure is comprehensive. Phase 20 is about populating language-specific metadata, sample functions, and verification -- NOT building new runtime plumbing.

## Common Pitfalls

### Pitfall 1: Assuming All Languages Have Equal WASM Support
**What goes wrong:** Treating Tier 3 languages (Nim, Prolog, Fortran) with the same confidence as Tier 1 (Rust, C).
**Why it happens:** Optimism about WASM ecosystem completeness.
**How to avoid:** Use the maturity tiers from awesome-wasm-langs as the source of truth. Tier 3 strategies should set `WasiSupportLevel.Experimental` or `WasiSupportLevel.None` and document known limitations explicitly.
**Warning signs:** No official WASI target, no maintained compiler, requires interpreter-in-WASM approach.

### Pitfall 2: AssemblyScript WASI Status
**What goes wrong:** Assuming AssemblyScript fully supports WASI because it was designed for WASM.
**Why it happens:** AssemblyScript is WASM-native, so WASI support seems guaranteed.
**How to avoid:** AssemblyScript team has criticized WASI standardization; WASI support was once available but was dropped. AssemblyScript targets `wasm32-unknown-unknown` (pure WASM) not WASI. The strategy should note this: `WasiSupportLevel.None` with note about using `--env` imports instead.
**Warning signs:** Missing `wasi_snapshot_preview1` imports in compiled output.

### Pitfall 3: Go vs TinyGo Binary Size
**What goes wrong:** Using standard Go compiler produces very large WASM binaries (50MB+).
**Why it happens:** Standard Go includes full runtime, garbage collector, goroutine scheduler.
**How to avoid:** Use TinyGo for WASM targets. TinyGo produces 100KB-1MB binaries vs Go's 50MB+. Standard Go `GOOS=wasip1` works but is impractical for compute-on-data. Document both paths but recommend TinyGo.
**Warning signs:** WASM binary exceeds 10MB.

### Pitfall 4: Python/Ruby/PHP Require Interpreter-in-WASM
**What goes wrong:** Expecting Python source code to compile directly to WASM like Rust does.
**Why it happens:** Confusing "language compiles to WASM" with "language interpreter runs in WASM."
**How to avoid:** For interpreted languages, the approach is: compile CPython/MRuby/PHP interpreter to WASM, then the interpreter runs the source code. This means: larger WASM binaries (10-50MB), slower startup, limited stdlib. Strategies for these languages must use `py2wasm`, `componentize-py`, `mruby`, or `php-wasm` toolchains and document the interpreter-in-WASM model.
**Warning signs:** Attempting to "compile" a .py file directly to .wasm without an interpreter.

### Pitfall 5: Forgetting Rule 13 (No Stubs)
**What goes wrong:** Creating language strategy classes with placeholder compile commands or empty sample functions.
**Why it happens:** 30+ languages is a lot of verification work; temptation to stub some out.
**How to avoid:** Every strategy must have: (a) real, verified compile command, (b) real toolchain installation instructions, (c) pre-compiled or generatable sample WASM bytes, (d) expected output verification. For Tier 3 languages where WASM support is truly experimental, the strategy should honestly report `WasiSupportLevel.Experimental` with documented limitations rather than faking success.

### Pitfall 6: WASI Preview 2 vs Preview 1 Confusion
**What goes wrong:** Code assumes WASI Preview 1 interfaces when newer runtimes expect Preview 2.
**Why it happens:** WASI is evolving rapidly; Preview 2 (0.2) became stable in 2024, Preview 3 (0.3) is in development.
**How to avoid:** Existing strategies use `--wasi` flag which defaults to Preview 1. For Component Model strategy, `--wasm component-model` is already used. Language strategies should document which WASI preview their toolchain targets. Most Tier 1 languages now target WASI Preview 1 with Preview 2 emerging.

## Code Examples

### Example 1: Existing Strategy Pattern (Source: WasmtimeStrategy.cs)
```csharp
// All strategies follow this exact pattern
internal sealed class WasmtimeStrategy : ComputeRuntimeStrategyBase
{
    public override string StrategyId => "compute.wasm.wasmtime";
    public override string StrategyName => "Wasmtime";
    public override ComputeRuntime Runtime => ComputeRuntime.WASM;
    public override ComputeCapabilities Capabilities => ComputeCapabilities.CreateWasmDefaults();
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.WASM];

    public override async Task InitializeAsync(CancellationToken ct = default)
    {
        await IsToolAvailableAsync("wasmtime", "--version", ct);
    }

    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken ct = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var wasmPath = Path.GetTempFileName() + ".wasm";
            try
            {
                await File.WriteAllBytesAsync(wasmPath, task.Code.ToArray(), ct);
                // Build args, run process, return result
                var result = await RunProcessAsync("wasmtime", args.ToString(), ...);
                return (EncodeOutput(result.StandardOutput), logs);
            }
            finally { File.Delete(wasmPath); }
        }, ct);
    }
}
```

### Example 2: Existing Language Info Pattern (Source: WasmComputePlugin.cs)
```csharp
// The WasmComputePlugin already defines 6 languages with this structure
new LanguageInfo
{
    Language = "Rust",
    LanguageVersion = "1.70+",
    Toolchain = "wasm32-unknown-unknown target",
    CompilerCommand = "cargo build --target wasm32-unknown-unknown --release",
    Notes = "Best performance, full WASM feature support"
}
```

### Example 3: Existing Capabilities Pattern (Source: ComputeCapabilities.cs)
```csharp
// CreateWasmDefaults() already declares supported languages
public static ComputeCapabilities CreateWasmDefaults() => new(
    SupportsStreaming: true,
    SupportsSandboxing: true,
    MaxMemoryBytes: 2L * 1024 * 1024 * 1024,
    MaxExecutionTime: TimeSpan.FromMinutes(5),
    SupportedLanguages: new[] { "wasm", "assemblyscript", "rust-wasm", "c-wasm", "go-wasm" },
    // ...
    MemoryIsolation: MemoryIsolationLevel.Sandbox
);
```

### Example 4: ComputeTask Creation (Source: ComputeTypes.cs)
```csharp
// How tasks are created for WASM execution
var task = ComputeTask.FromStrings(
    id: "verify-rust-wasm",
    code: sourceCode,  // or use ReadOnlyMemory<byte> for pre-compiled .wasm
    language: "rust-wasm",
    entryPoint: "_start",
    inputData: "{\"data\": [1, 2, 3, 4, 5]}"
);
```

## WASM Language Ecosystem: Detailed Language Analysis

### Tier 1: Production-Ready WASM Support (HIGH confidence)

| Language | WASM Target | WASI Support | Toolchain | Binary Size | Performance | Component Model |
|----------|-------------|--------------|-----------|-------------|-------------|-----------------|
| **Rust** | `wasm32-wasi` | Full | `rustup target add wasm32-wasi` | Small (50KB-2MB) | Native | Yes (via cargo-component) |
| **C** | `wasm32-wasi` | Full | WASI SDK / Emscripten | Small (10KB-500KB) | Native | Via wit-bindgen |
| **C++** | `wasm32-wasi` | Full | WASI SDK / Emscripten | Medium (100KB-5MB) | Native | Via wit-bindgen |
| **.NET (C#/F#)** | `wasm` | Full | `dotnet workload install wasi-experimental` | Large (5-20MB) | Near-Native | Emerging |
| **Go (TinyGo)** | `wasm32-wasi` | Full | TinyGo `tinygo build -target=wasi` | Medium (100KB-2MB) | Near-Native | Limited |
| **Go (std)** | `wasip1` | Full | `GOOS=wasip1 GOARCH=wasm go build` | Very Large (50MB+) | Near-Native | No |
| **AssemblyScript** | `wasm32` | None* | `asc entry.ts -o output.wasm` | Tiny (1KB-200KB) | Near-Native | No |
| **Zig** | `wasm32-freestanding` / `wasm32-wasi` | Full | `zig build-exe -target wasm32-wasi` | Small (10KB-1MB) | Native | Via wit-bindgen |

*AssemblyScript targets pure WASM (`wasm32-unknown-unknown`), not WASI. It can interact with host via env imports.

### Tier 2: Usable WASM Support (MEDIUM confidence)

| Language | WASM Approach | WASI Support | Toolchain | Binary Size | Performance | Notes |
|----------|---------------|--------------|-----------|-------------|-------------|-------|
| **Python** | Interpreter-in-WASM | Preview 1 | `componentize-py` / `py2wasm` / Pyodide | Large (10-30MB) | Slow | CPython compiled to WASM |
| **Ruby** | Interpreter-in-WASM | Preview 1 | `ruby.wasm` (CRuby to WASM) | Large (15-30MB) | Slow | Uses CRuby compiled via WASI SDK |
| **JavaScript** | QuickJS-in-WASM | Preview 1 | Javy (Bytecode Alliance) | Medium (2-5MB) | Interpreted | QuickJS engine compiled to WASM |
| **TypeScript** | AssemblyScript or QuickJS | Varies | `asc` or Javy | Varies | Varies | Two paths: AS (native) or QuickJS (interpreted) |
| **Kotlin** | Kotlin/Wasm | Partial | `kotlin-wasm` compiler | Medium (5-15MB) | Near-Native | Official Kotlin target, evolving |
| **Swift** | SwiftWasm | Preview 1 | `swiftwasm` toolchain | Large (10-30MB) | Near-Native | Community-maintained fork |
| **Java** | TeaVM / j2wasm | Partial | TeaVM compiler | Medium (5-20MB) | Near-Native | Bytecode to WASM transpiler |
| **Dart** | dart2wasm | Preview 1 | Dart SDK `dart compile wasm` | Medium (3-10MB) | Near-Native | Official Dart compiler target |
| **PHP** | Interpreter-in-WASM | Partial | `php-wasm` | Large (15-30MB) | Slow | Zend engine compiled to WASM |
| **Lua** | Interpreter-in-WASM | Preview 1 | Nelua / wasm_lua | Small (500KB-3MB) | Interpreted | Lightweight interpreter |
| **Haskell** | GHC WASM backend | Experimental | GHC WASM backend | Large (10-50MB) | Near-Native | Work in progress, GHC 9.10+ |
| **OCaml** | wasocaml / ocaml-wasm | Experimental | OCaml to WASM fork | Medium (5-15MB) | Near-Native | Active development |
| **Grain** | Native WASM | Full | `grain compile` | Small (50KB-1MB) | Near-Native | Designed for WASM |
| **MoonBit** | Native WASM | Full | `moon build` | Tiny (1KB-100KB) | Native | Designed for WASM, very compact |

### Tier 3: Experimental/Unstable (LOW confidence)

| Language | WASM Status | Approach | Toolchain | Feasibility |
|----------|-------------|----------|-----------|-------------|
| **Nim** | Work in progress | LLVM backend `--cpu:wasm32` | nlvm compiler | Medium - LLVM backend exists |
| **V** | Unstable | C backend + Emscripten | `v -o output.c && emcc` | Medium - indirect via C |
| **Crystal** | Unstable (POC PR) | LLVM backend | Main Crystal repo | Low - POC only |
| **Perl** | Unstable | Interpreter-in-WASM | `webperl` | Low - limited maintenance |
| **R** | Unstable | Interpreter-in-WASM | `webR` / `flang-wasm` | Medium - webR project active |
| **Fortran** | Experimental | flang to WASM | `flang-wasm` (LLVM flang) | Medium - missing runtime libs |
| **Scala** | Not direct | Via Scala.js or TeaVM | TeaVM (JVM bytecode) | Medium - indirect paths |
| **Elixir/Erlang** | Experimental | Lumen / Orb (Elixir) | Lumen (unmaintained), Orb | Low - Lumen unmaintained |
| **Prolog** | Unstable | Interpreter-in-WASM | SWI-Prolog WASM port, Ciao | Low - limited use case |
| **Ada** | Unstable | GNAT-LLVM to WASM | adawebpack | Low - niche but functional |

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| WASI Preview 1 only | WASI Preview 2 (0.2) stable | 2024 | Component model support, typed interfaces |
| Go can't target WASM/WASI | Go 1.21+ `GOOS=wasip1 GOARCH=wasm` | 2023 | Native Go WASI support (but large binaries) |
| Python WASM = Pyodide only | `componentize-py` for WASI P2 components | 2024 | Python as WASI component, not just browser |
| JavaScript WASM = browser only | Javy (Bytecode Alliance) for server-side | 2023 | QuickJS in WASM for server/edge |
| Kotlin no WASM | Kotlin/Wasm official target | 2023 | JetBrains official WASM compiler target |
| Dart WASM = Flutter only | `dart compile wasm` standalone | 2024 | Dart standalone WASM compilation |
| WASM Component Model proposal | Component Model stable in Wasmtime | 2024 | Cross-language component composition |
| AssemblyScript had WASI | AssemblyScript dropped WASI | 2023 | Must use env imports, not wasi_snapshot_preview1 |

**Deprecated/outdated:**
- **Emscripten-only approach for C/C++:** WASI SDK is now the standard for server-side WASM; Emscripten remains for browser targets
- **Lumen (Elixir/Erlang WASM):** Project unmaintained; Orb is the current alternative for Elixir
- **Speedy.js, TurboScript, Walt:** All deprecated WASM languages

## Open Questions

1. **Pre-compiled WASM Samples: Embedded vs Generated?**
   - What we know: Rule 13 requires production-ready code. Each language needs real sample WASM bytes.
   - What's unclear: Should sample .wasm bytes be embedded as byte arrays in the strategy classes, or should strategies invoke the compiler toolchain at initialization to generate them?
   - Recommendation: Embed minimal pre-compiled WASM bytes as `ReadOnlySpan<byte>` constants. The bytes represent a minimal "sum array" function compiled from each language. This avoids requiring all 30+ language toolchains to be installed on the build machine. The `CompileCommand` field documents how to rebuild from source.

2. **Where Do Language Strategies Live?**
   - What we know: Existing WASM strategies are in `UltimateCompute/Strategies/Wasm/`. The registry auto-discovers via assembly scanning.
   - What's unclear: Should language verification strategies go in the same `Wasm/` folder, or a new `WasmLanguages/` subfolder?
   - Recommendation: New subfolder `WasmLanguages/Tier1/`, `WasmLanguages/Tier2/`, `WasmLanguages/Tier3/` to keep the existing 7 runtime strategies separate from the 30+ language strategies.

3. **SDK Bindings Scope for Phase 20 vs T136**
   - What we know: `UltimateSDKPorts` (T136) already has Rust, Python, JS, Go bindings. Phase 20 requires "language-specific SDK bindings or documentation."
   - What's unclear: Should Phase 20 add new binding strategies to UltimateSDKPorts, or is documentation sufficient?
   - Recommendation: For Tier 1 languages already in UltimateSDKPorts (Rust, Go), verify existing bindings work with WASM. For other Tier 1/2 languages, provide documentation in the strategy class (compilation instructions, DW host function imports). Full binding code generation for new languages is beyond Phase 20 scope.

4. **Benchmark Standardization**
   - What we know: Success criteria #6 requires performance benchmarks comparing execution speed.
   - What's unclear: What workload? Which runtimes?
   - Recommendation: Standard workload: SHA-256 hash of 1MB buffer, 1000 iterations. Measure: wall-clock time, peak memory, binary size. Run on: Wasmtime (primary), Wasmer, WasmEdge. Expose via a `WasmLanguageBenchmarkStrategy` that orchestrates runs across all verified languages.

## Project Rules Compliance

| Rule | How This Phase Complies |
|------|------------------------|
| **SDK only refs** | All new strategies in UltimateCompute reference SDK only (`DataWarehouse.SDK.csproj`) |
| **Strategy pattern** | Every language is a `ComputeRuntimeStrategyBase` subclass with `internal sealed class` |
| **Message bus** | Inter-plugin communication (if needed) via `IMessageBus` already wired in base class |
| **Rule 13** | No stubs: real compilation metadata, real WASM bytes, real execution paths |
| **internal sealed** | All strategy classes use `internal sealed class` per project convention |
| **Auto-discovery** | `ComputeRuntimeStrategyRegistry.DiscoverStrategies()` finds them automatically |

## Sources

### Primary (HIGH confidence)
- Codebase analysis: `UltimateCompute/Strategies/Wasm/` - 7 existing WASM runtime strategies
- Codebase analysis: `Compute.Wasm/WasmComputePlugin.cs` - existing WASM interpreter + T70 implementation
- Codebase analysis: `SDK/Contracts/Compute/` - ComputeTypes, ComputeCapabilities, IComputeRuntimeStrategy
- Codebase analysis: `UltimateSDKPorts/` - existing language bindings for Rust, Python, JS, Go
- Codebase analysis: `ROADMAP.md` Phase 20 section - success criteria and plan structure

### Secondary (MEDIUM confidence)
- [awesome-wasm-langs](https://github.com/appcypher/awesome-wasm-langs) - Language maturity tiers and toolchain references
- [Fermyon: Complex World of Wasm Language Support](https://www.fermyon.com/blog/complex-world-of-wasm-language-support) - WASI language support details
- [Kotlin/Wasm Documentation](https://kotlinlang.org/docs/wasm-overview.html) - Official Kotlin WASM target
- [componentize-py](https://component-model.bytecodealliance.org/language-support/python.html) - Python WASI component model
- [Bytecode Alliance Component Model](https://component-model.bytecodealliance.org/) - Component model specification
- [WASI Roadmap](https://wasi.dev/roadmap) - WASI 0.2, 0.3 timeline
- [State of WebAssembly 2025/2026](https://platform.uno/blog/the-state-of-webassembly-2025-2026/) - Ecosystem overview

### Tertiary (LOW confidence)
- Fortran WASM: [flang-wasm](https://github.com/r-wasm/flang-wasm) - missing runtime library, experimental
- Elixir WASM: [Orb](https://github.com/RoyalIcing/Orb) - community project, not official
- Crystal WASM: POC PR in main repo only
- Prolog WASM: SWI-Prolog port, limited maintenance

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH - based on direct codebase analysis, infrastructure is well-established
- Architecture: HIGH - patterns are clear from existing 7 WASM strategies and 51+ total strategies
- Language tiers: MEDIUM - Tier 1 is HIGH confidence, Tier 2 is MEDIUM, Tier 3 is LOW
- Pitfalls: HIGH - based on documented ecosystem issues and codebase constraints

**Research date:** 2026-02-11
**Valid until:** 2026-03-11 (WASM ecosystem evolves but language toolchains are stable)
