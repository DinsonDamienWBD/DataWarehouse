namespace DataWarehouse.SDK.Contracts.Compute;

/// <summary>
/// Describes the capabilities supported by a compute runtime strategy implementation.
/// </summary>
/// <remarks>
/// <para>
/// This record provides a declarative way to specify what features a compute runtime supports,
/// allowing the system to make intelligent task scheduling and resource allocation decisions at runtime.
/// </para>
/// <para>
/// Capabilities include execution features (streaming, sandboxing), resource limits (memory, CPU),
/// supported languages, and security features.
/// </para>
/// </remarks>
/// <param name="SupportsStreaming">
/// Indicates whether the runtime supports streaming input and output.
/// If true, the runtime can process data incrementally without loading everything into memory.
/// </param>
/// <param name="SupportsSandboxing">
/// Indicates whether the runtime provides execution sandboxing and isolation.
/// If true, code runs in an isolated environment with restricted access to system resources.
/// </param>
/// <param name="MaxMemoryBytes">
/// The maximum amount of memory (in bytes) that a single compute task can use.
/// Null indicates no specific memory limit enforced by the runtime.
/// </param>
/// <param name="MaxExecutionTime">
/// The maximum execution time allowed for a single compute task.
/// Null indicates no specific time limit enforced by the runtime.
/// </param>
/// <param name="SupportedLanguages">
/// A collection of programming languages or language versions supported by this runtime.
/// Examples: "python-3.11", "javascript-es2023", "wasm", "c#-12", "lua-5.4", etc.
/// </param>
/// <param name="SupportsMultiThreading">
/// Indicates whether the runtime supports multi-threaded execution within a single task.
/// If true, task code can spawn threads and execute in parallel.
/// </param>
/// <param name="SupportsAsync">
/// Indicates whether the runtime supports asynchronous/concurrent execution patterns.
/// If true, task code can use async/await or equivalent concurrency primitives.
/// </param>
/// <param name="SupportsNetworkAccess">
/// Indicates whether the runtime allows tasks to make network requests.
/// If false, network operations are blocked for security.
/// </param>
/// <param name="SupportsFileSystemAccess">
/// Indicates whether the runtime allows tasks to access the file system.
/// If false, file I/O operations are blocked for security.
/// </param>
/// <param name="MaxConcurrentTasks">
/// The maximum number of tasks that can execute concurrently in this runtime.
/// Null indicates no specific limit on concurrency.
/// </param>
/// <param name="SupportsNativeDependencies">
/// Indicates whether the runtime can load and use native libraries or dependencies.
/// If true, tasks can use compiled native code (C/C++ libraries, etc.).
/// </param>
/// <param name="SupportsPrecompilation">
/// Indicates whether the runtime supports precompiling code for faster subsequent executions.
/// If true, code can be compiled once and cached for reuse.
/// </param>
/// <param name="SupportsDynamicLoading">
/// Indicates whether the runtime supports dynamically loading code and dependencies at runtime.
/// If true, tasks can import modules and packages dynamically.
/// </param>
/// <param name="MemoryIsolation">
/// The level of memory isolation provided by the runtime.
/// Higher levels provide better security but may have performance overhead.
/// </param>
public record ComputeCapabilities(
    bool SupportsStreaming,
    bool SupportsSandboxing,
    long? MaxMemoryBytes,
    TimeSpan? MaxExecutionTime,
    IReadOnlyList<string> SupportedLanguages,
    bool SupportsMultiThreading = false,
    bool SupportsAsync = true,
    bool SupportsNetworkAccess = false,
    bool SupportsFileSystemAccess = false,
    int? MaxConcurrentTasks = null,
    bool SupportsNativeDependencies = false,
    bool SupportsPrecompilation = false,
    bool SupportsDynamicLoading = true,
    MemoryIsolationLevel MemoryIsolation = MemoryIsolationLevel.Process
)
{
    /// <summary>
    /// Creates a default set of capabilities suitable for WebAssembly (WASM) runtime.
    /// </summary>
    /// <returns>
    /// A <see cref="ComputeCapabilities"/> instance with standard WASM capabilities.
    /// </returns>
    public static ComputeCapabilities CreateWasmDefaults() => new(
        SupportsStreaming: true,
        SupportsSandboxing: true, // WASM is inherently sandboxed
        MaxMemoryBytes: 2L * 1024 * 1024 * 1024, // 2 GB (WASM 32-bit limit)
        MaxExecutionTime: TimeSpan.FromMinutes(5),
        SupportedLanguages: new[] { "wasm", "assemblyscript", "rust-wasm", "c-wasm", "go-wasm" },
        SupportsMultiThreading: true, // WASM threads
        SupportsAsync: true,
        SupportsNetworkAccess: false, // Typically blocked unless using WASI
        SupportsFileSystemAccess: false, // Blocked unless using WASI
        MaxConcurrentTasks: 100,
        SupportsNativeDependencies: false,
        SupportsPrecompilation: true, // AOT compilation supported
        SupportsDynamicLoading: true,
        MemoryIsolation: MemoryIsolationLevel.Sandbox
    );

    /// <summary>
    /// Creates a default set of capabilities suitable for Python runtime.
    /// </summary>
    /// <returns>
    /// A <see cref="ComputeCapabilities"/> instance with standard Python capabilities.
    /// </returns>
    public static ComputeCapabilities CreatePythonDefaults() => new(
        SupportsStreaming: true,
        SupportsSandboxing: true, // Requires RestrictedPython or similar
        MaxMemoryBytes: 4L * 1024 * 1024 * 1024, // 4 GB
        MaxExecutionTime: TimeSpan.FromMinutes(10),
        SupportedLanguages: new[] { "python-3.11", "python-3.12", "python-3.13" },
        SupportsMultiThreading: true, // With GIL limitations
        SupportsAsync: true,
        SupportsNetworkAccess: false, // Configurable per deployment
        SupportsFileSystemAccess: false, // Restricted
        MaxConcurrentTasks: 50,
        SupportsNativeDependencies: true, // Can use C extensions
        SupportsPrecompilation: true, // .pyc bytecode
        SupportsDynamicLoading: true,
        MemoryIsolation: MemoryIsolationLevel.Process
    );

    /// <summary>
    /// Creates a default set of capabilities suitable for JavaScript/Node.js runtime.
    /// </summary>
    /// <returns>
    /// A <see cref="ComputeCapabilities"/> instance with standard JavaScript capabilities.
    /// </returns>
    public static ComputeCapabilities CreateJavaScriptDefaults() => new(
        SupportsStreaming: true,
        SupportsSandboxing: true, // vm2 or isolated-vm
        MaxMemoryBytes: 2L * 1024 * 1024 * 1024, // 2 GB
        MaxExecutionTime: TimeSpan.FromMinutes(5),
        SupportedLanguages: new[] { "javascript-es2023", "typescript-5", "node-20" },
        SupportsMultiThreading: true, // Worker threads
        SupportsAsync: true,
        SupportsNetworkAccess: false,
        SupportsFileSystemAccess: false,
        MaxConcurrentTasks: 100,
        SupportsNativeDependencies: true, // Native Node modules
        SupportsPrecompilation: true, // V8 bytecode caching
        SupportsDynamicLoading: true,
        MemoryIsolation: MemoryIsolationLevel.Isolate
    );

    /// <summary>
    /// Creates a default set of capabilities suitable for native code execution.
    /// </summary>
    /// <returns>
    /// A <see cref="ComputeCapabilities"/> instance with standard native execution capabilities.
    /// </returns>
    public static ComputeCapabilities CreateNativeDefaults() => new(
        SupportsStreaming: true,
        SupportsSandboxing: true, // OS-level sandboxing (containers, seccomp, etc.)
        MaxMemoryBytes: 16L * 1024 * 1024 * 1024, // 16 GB
        MaxExecutionTime: TimeSpan.FromMinutes(30),
        SupportedLanguages: new[] { "c", "c++", "rust", "go", "zig" },
        SupportsMultiThreading: true,
        SupportsAsync: true,
        SupportsNetworkAccess: false,
        SupportsFileSystemAccess: false,
        MaxConcurrentTasks: 20,
        SupportsNativeDependencies: true,
        SupportsPrecompilation: true,
        SupportsDynamicLoading: true,
        MemoryIsolation: MemoryIsolationLevel.Process
    );

    /// <summary>
    /// Creates a default set of capabilities suitable for .NET runtime.
    /// </summary>
    /// <returns>
    /// A <see cref="ComputeCapabilities"/> instance with standard .NET capabilities.
    /// </returns>
    public static ComputeCapabilities CreateDotNetDefaults() => new(
        SupportsStreaming: true,
        SupportsSandboxing: true, // AppDomains (legacy) or AssemblyLoadContext isolation
        MaxMemoryBytes: 8L * 1024 * 1024 * 1024, // 8 GB
        MaxExecutionTime: TimeSpan.FromMinutes(15),
        SupportedLanguages: new[] { "csharp-12", "fsharp-8", "vb.net" },
        SupportsMultiThreading: true,
        SupportsAsync: true,
        SupportsNetworkAccess: false,
        SupportsFileSystemAccess: false,
        MaxConcurrentTasks: 50,
        SupportsNativeDependencies: true,
        SupportsPrecompilation: true, // AOT compilation
        SupportsDynamicLoading: true,
        MemoryIsolation: MemoryIsolationLevel.Process
    );

    /// <summary>
    /// Creates a default set of capabilities suitable for JVM-based languages.
    /// </summary>
    /// <returns>
    /// A <see cref="ComputeCapabilities"/> instance with standard JVM capabilities.
    /// </returns>
    public static ComputeCapabilities CreateJvmDefaults() => new(
        SupportsStreaming: true,
        SupportsSandboxing: true, // SecurityManager (deprecated) or custom classloaders
        MaxMemoryBytes: 8L * 1024 * 1024 * 1024, // 8 GB
        MaxExecutionTime: TimeSpan.FromMinutes(15),
        SupportedLanguages: new[] { "java-21", "kotlin-1.9", "scala-3", "groovy-4" },
        SupportsMultiThreading: true,
        SupportsAsync: true,
        SupportsNetworkAccess: false,
        SupportsFileSystemAccess: false,
        MaxConcurrentTasks: 50,
        SupportsNativeDependencies: true, // JNI
        SupportsPrecompilation: true, // JIT compilation
        SupportsDynamicLoading: true,
        MemoryIsolation: MemoryIsolationLevel.Process
    );

    /// <summary>
    /// Creates a default set of capabilities suitable for Lua runtime.
    /// </summary>
    /// <returns>
    /// A <see cref="ComputeCapabilities"/> instance with standard Lua capabilities.
    /// </returns>
    public static ComputeCapabilities CreateLuaDefaults() => new(
        SupportsStreaming: true,
        SupportsSandboxing: true, // Lua sandbox environments
        MaxMemoryBytes: 1L * 1024 * 1024 * 1024, // 1 GB
        MaxExecutionTime: TimeSpan.FromMinutes(5),
        SupportedLanguages: new[] { "lua-5.4", "luajit-2.1" },
        SupportsMultiThreading: false, // Lua doesn't have native threading
        SupportsAsync: true, // Coroutines
        SupportsNetworkAccess: false,
        SupportsFileSystemAccess: false,
        MaxConcurrentTasks: 200,
        SupportsNativeDependencies: true, // C modules
        SupportsPrecompilation: true, // Bytecode compilation
        SupportsDynamicLoading: true,
        MemoryIsolation: MemoryIsolationLevel.Sandbox
    );

    /// <summary>
    /// Creates a highly restricted set of capabilities for untrusted code.
    /// </summary>
    /// <returns>
    /// A <see cref="ComputeCapabilities"/> instance with maximum security restrictions.
    /// </returns>
    public static ComputeCapabilities CreateRestrictedDefaults() => new(
        SupportsStreaming: false,
        SupportsSandboxing: true,
        MaxMemoryBytes: 256 * 1024 * 1024, // 256 MB
        MaxExecutionTime: TimeSpan.FromSeconds(30),
        SupportedLanguages: new[] { "wasm" },
        SupportsMultiThreading: false,
        SupportsAsync: false,
        SupportsNetworkAccess: false,
        SupportsFileSystemAccess: false,
        MaxConcurrentTasks: 1000,
        SupportsNativeDependencies: false,
        SupportsPrecompilation: true,
        SupportsDynamicLoading: false,
        MemoryIsolation: MemoryIsolationLevel.Sandbox
    );
}

/// <summary>
/// Defines the level of memory isolation provided by a compute runtime.
/// </summary>
/// <remarks>
/// Higher isolation levels provide better security but may have performance overhead.
/// </remarks>
public enum MemoryIsolationLevel
{
    /// <summary>
    /// No memory isolation. Code runs in the same process with shared memory.
    /// Highest performance, lowest security. Suitable only for fully trusted code.
    /// </summary>
    None = 0,

    /// <summary>
    /// Sandbox-level isolation using runtime features (WASM sandbox, VM isolation).
    /// Good balance of performance and security. Memory is logically isolated but may share the process.
    /// </summary>
    Sandbox = 1,

    /// <summary>
    /// Isolate-level isolation (V8 isolates, .NET AssemblyLoadContext).
    /// Strong isolation within a process. Each isolate has its own heap.
    /// </summary>
    Isolate = 2,

    /// <summary>
    /// Process-level isolation. Each task runs in a separate OS process.
    /// Strong isolation with OS-enforced boundaries. Higher overhead.
    /// </summary>
    Process = 3,

    /// <summary>
    /// Container-level isolation using OS virtualization (Docker, Podman).
    /// Very strong isolation with resource limits. Significant overhead.
    /// </summary>
    Container = 4,

    /// <summary>
    /// Virtual machine level isolation.
    /// Maximum isolation with full OS separation. Highest overhead.
    /// </summary>
    VirtualMachine = 5
}
