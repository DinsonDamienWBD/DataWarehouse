namespace DataWarehouse.SDK.Contracts.Compute;

/// <summary>
/// Represents a compute task to be executed by a runtime strategy.
/// </summary>
/// <remarks>
/// <para>
/// This record encapsulates all information needed to execute code in a compute runtime.
/// It includes the code itself, input data, execution options, and resource limits.
/// </para>
/// </remarks>
/// <param name="Id">
/// A unique identifier for this compute task. Used for tracking and logging.
/// </param>
/// <param name="Code">
/// The source code or compiled bytecode to execute. The format depends on the runtime.
/// For interpreted languages (Python, JavaScript), this is source code.
/// For compiled languages or WASM, this may be binary code.
/// </param>
/// <param name="Language">
/// The programming language or runtime version of the code.
/// Examples: "python-3.11", "javascript-es2023", "wasm", "c#-12".
/// </param>
/// <param name="EntryPoint">
/// The entry point function or method to execute.
/// For scripts, this might be "main" or null (execute entire script).
/// For modules, this specifies which function to call.
/// </param>
/// <param name="InputData">
/// The input data to pass to the compute task.
/// The format and interpretation depend on the code being executed.
/// </param>
/// <param name="Arguments">
/// Command-line arguments or function parameters to pass to the code.
/// </param>
/// <param name="Environment">
/// Environment variables to set for the execution context.
/// These are available to the code during execution.
/// </param>
/// <param name="ResourceLimits">
/// Resource constraints to enforce during execution (memory, CPU, time).
/// </param>
/// <param name="Dependencies">
/// External dependencies or libraries required by the code.
/// The format depends on the runtime (Python packages, npm modules, etc.).
/// </param>
/// <param name="Timeout">
/// The maximum time allowed for this task to execute.
/// If execution exceeds this duration, the task is cancelled.
/// Null uses the runtime's default timeout.
/// </param>
/// <param name="Metadata">
/// Additional metadata about the task (user, source, version, etc.).
/// Used for logging, auditing, and debugging.
/// </param>
public record ComputeTask(
    string Id,
    ReadOnlyMemory<byte> Code,
    string Language,
    string? EntryPoint = null,
    ReadOnlyMemory<byte> InputData = default,
    IReadOnlyList<string>? Arguments = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    ResourceLimits? ResourceLimits = null,
    IReadOnlyList<string>? Dependencies = null,
    TimeSpan? Timeout = null,
    IReadOnlyDictionary<string, object>? Metadata = null
)
{
    /// <summary>
    /// Gets the code as a UTF-8 string (for source code languages).
    /// </summary>
    /// <returns>
    /// The code decoded as a UTF-8 string.
    /// </returns>
    /// <remarks>
    /// Invalid UTF-8 byte sequences are replaced with U+FFFD (replacement character)
    /// per the default UTF-8 encoding behavior.
    /// </remarks>
    public string GetCodeAsString() => System.Text.Encoding.UTF8.GetString(Code.Span);

    /// <summary>
    /// Gets the input data as a UTF-8 string.
    /// </summary>
    /// <returns>
    /// The input data decoded as a UTF-8 string.
    /// </returns>
    /// <remarks>
    /// Invalid UTF-8 byte sequences are replaced with U+FFFD (replacement character)
    /// per the default UTF-8 encoding behavior.
    /// </remarks>
    public string GetInputDataAsString() => System.Text.Encoding.UTF8.GetString(InputData.Span);

    /// <summary>
    /// Creates a compute task from source code strings.
    /// </summary>
    /// <param name="id">Unique task identifier.</param>
    /// <param name="code">Source code as a string.</param>
    /// <param name="language">Programming language.</param>
    /// <param name="entryPoint">Entry point function/method.</param>
    /// <param name="inputData">Input data as a string.</param>
    /// <returns>
    /// A <see cref="ComputeTask"/> instance with UTF-8 encoded code and input.
    /// </returns>
    public static ComputeTask FromStrings(
        string id,
        string code,
        string language,
        string? entryPoint = null,
        string? inputData = null) => new(
            Id: id,
            Code: System.Text.Encoding.UTF8.GetBytes(code),
            Language: language,
            EntryPoint: entryPoint,
            InputData: inputData != null ? System.Text.Encoding.UTF8.GetBytes(inputData) : default
        );
}

/// <summary>
/// Represents the result of executing a compute task.
/// </summary>
/// <remarks>
/// <para>
/// This record encapsulates the outcome of task execution, including output data,
/// error information, execution metrics, and resource usage.
/// </para>
/// </remarks>
/// <param name="TaskId">
/// The unique identifier of the task that was executed.
/// </param>
/// <param name="Success">
/// Indicates whether the task executed successfully without errors.
/// </param>
/// <param name="OutputData">
/// The output data produced by the task execution.
/// The format depends on the code that was executed.
/// </param>
/// <param name="ErrorMessage">
/// A human-readable error message if the task failed.
/// Null if the task succeeded.
/// </param>
/// <param name="ErrorDetails">
/// Detailed error information (stack trace, error code, etc.).
/// Null if the task succeeded.
/// </param>
/// <param name="ExecutionTime">
/// The actual time taken to execute the task.
/// </param>
/// <param name="MemoryUsed">
/// The peak memory usage (in bytes) during task execution.
/// Null if not measured.
/// </param>
/// <param name="CpuTime">
/// The CPU time consumed by the task.
/// Null if not measured.
/// </param>
/// <param name="ExitCode">
/// The exit code returned by the task (if applicable).
/// Typically 0 for success, non-zero for errors.
/// </param>
/// <param name="Logs">
/// Console output, logging messages, or debug information from the task execution.
/// </param>
/// <param name="Metadata">
/// Additional runtime-specific metadata about the execution.
/// </param>
public record ComputeResult(
    string TaskId,
    bool Success,
    ReadOnlyMemory<byte> OutputData,
    string? ErrorMessage = null,
    string? ErrorDetails = null,
    TimeSpan? ExecutionTime = null,
    long? MemoryUsed = null,
    TimeSpan? CpuTime = null,
    int? ExitCode = null,
    string? Logs = null,
    IReadOnlyDictionary<string, object>? Metadata = null
)
{
    /// <summary>
    /// Gets the output data as a UTF-8 string.
    /// </summary>
    /// <returns>
    /// The output data decoded as a UTF-8 string.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the output data is not valid UTF-8 text.
    /// </exception>
    public string GetOutputDataAsString() => System.Text.Encoding.UTF8.GetString(OutputData.Span);

    /// <summary>
    /// Creates a successful result with output data.
    /// </summary>
    /// <param name="taskId">The task identifier.</param>
    /// <param name="outputData">The output data as bytes.</param>
    /// <param name="executionTime">The execution time.</param>
    /// <returns>
    /// A <see cref="ComputeResult"/> indicating success.
    /// </returns>
    public static ComputeResult CreateSuccess(
        string taskId,
        ReadOnlyMemory<byte> outputData,
        TimeSpan? executionTime = null) => new(
            TaskId: taskId,
            Success: true,
            OutputData: outputData,
            ExecutionTime: executionTime,
            ExitCode: 0
        );

    /// <summary>
    /// Creates a successful result with output data from a string.
    /// </summary>
    /// <param name="taskId">The task identifier.</param>
    /// <param name="outputData">The output data as a string.</param>
    /// <param name="executionTime">The execution time.</param>
    /// <returns>
    /// A <see cref="ComputeResult"/> indicating success with UTF-8 encoded output.
    /// </returns>
    public static ComputeResult CreateSuccess(
        string taskId,
        string outputData,
        TimeSpan? executionTime = null) => CreateSuccess(
            taskId,
            System.Text.Encoding.UTF8.GetBytes(outputData),
            executionTime
        );

    /// <summary>
    /// Creates a failed result with error information.
    /// </summary>
    /// <param name="taskId">The task identifier.</param>
    /// <param name="errorMessage">A human-readable error message.</param>
    /// <param name="errorDetails">Detailed error information (stack trace, etc.).</param>
    /// <param name="executionTime">The execution time before failure.</param>
    /// <param name="exitCode">The exit code (typically non-zero for errors).</param>
    /// <returns>
    /// A <see cref="ComputeResult"/> indicating failure.
    /// </returns>
    public static ComputeResult CreateFailure(
        string taskId,
        string errorMessage,
        string? errorDetails = null,
        TimeSpan? executionTime = null,
        int? exitCode = null) => new(
            TaskId: taskId,
            Success: false,
            OutputData: ReadOnlyMemory<byte>.Empty,
            ErrorMessage: errorMessage,
            ErrorDetails: errorDetails,
            ExecutionTime: executionTime,
            ExitCode: exitCode ?? -1
        );

    /// <summary>
    /// Creates a result indicating the task was cancelled.
    /// </summary>
    /// <param name="taskId">The task identifier.</param>
    /// <param name="executionTime">The execution time before cancellation.</param>
    /// <returns>
    /// A <see cref="ComputeResult"/> indicating cancellation.
    /// </returns>
    public static ComputeResult CreateCancelled(
        string taskId,
        TimeSpan? executionTime = null) => new(
            TaskId: taskId,
            Success: false,
            OutputData: ReadOnlyMemory<byte>.Empty,
            ErrorMessage: "Task was cancelled",
            ExecutionTime: executionTime,
            ExitCode: -2
        );

    /// <summary>
    /// Creates a result indicating the task exceeded its timeout.
    /// </summary>
    /// <param name="taskId">The task identifier.</param>
    /// <param name="timeout">The timeout that was exceeded.</param>
    /// <returns>
    /// A <see cref="ComputeResult"/> indicating timeout.
    /// </returns>
    public static ComputeResult CreateTimeout(
        string taskId,
        TimeSpan timeout) => new(
            TaskId: taskId,
            Success: false,
            OutputData: ReadOnlyMemory<byte>.Empty,
            ErrorMessage: $"Task exceeded timeout of {timeout.TotalSeconds:F1} seconds",
            ExecutionTime: timeout,
            ExitCode: -3
        );
}

/// <summary>
/// Defines resource limits for compute task execution.
/// </summary>
/// <remarks>
/// These limits help prevent resource exhaustion and ensure fair resource allocation
/// across multiple concurrent tasks.
/// </remarks>
/// <param name="MaxMemoryBytes">
/// The maximum memory (in bytes) the task can use.
/// If exceeded, the task is terminated. Null for no limit.
/// </param>
/// <param name="MaxCpuTime">
/// The maximum CPU time the task can consume.
/// This is actual execution time, not wall-clock time. Null for no limit.
/// </param>
/// <param name="MaxExecutionTime">
/// The maximum wall-clock time the task can run.
/// This includes time spent waiting for I/O. Null for no limit.
/// </param>
/// <param name="MaxOutputSize">
/// The maximum size (in bytes) of output data the task can produce.
/// Prevents excessive memory usage from output buffering. Null for no limit.
/// </param>
/// <param name="MaxThreads">
/// The maximum number of threads the task can spawn.
/// Null for no limit (or runtime default).
/// </param>
/// <param name="AllowNetworkAccess">
/// Whether the task is allowed to make network requests.
/// If false, network operations are blocked.
/// </param>
/// <param name="AllowFileSystemAccess">
/// Whether the task is allowed to access the file system.
/// If false, file I/O operations are blocked.
/// </param>
/// <param name="AllowedFileSystemPaths">
/// Specific file system paths the task is allowed to access.
/// Only relevant if <see cref="AllowFileSystemAccess"/> is true.
/// Null or empty for no access.
/// </param>
public record ResourceLimits(
    long? MaxMemoryBytes = null,
    TimeSpan? MaxCpuTime = null,
    TimeSpan? MaxExecutionTime = null,
    long? MaxOutputSize = null,
    int? MaxThreads = null,
    bool AllowNetworkAccess = false,
    bool AllowFileSystemAccess = false,
    IReadOnlyList<string>? AllowedFileSystemPaths = null
)
{
    /// <summary>
    /// Creates default resource limits suitable for untrusted code.
    /// </summary>
    /// <returns>
    /// A <see cref="ResourceLimits"/> instance with conservative limits for security.
    /// </returns>
    public static ResourceLimits Restricted => new(
        MaxMemoryBytes: 256 * 1024 * 1024, // 256 MB
        MaxCpuTime: TimeSpan.FromSeconds(30),
        MaxExecutionTime: TimeSpan.FromMinutes(1),
        MaxOutputSize: 10 * 1024 * 1024, // 10 MB
        MaxThreads: 1,
        AllowNetworkAccess: false,
        AllowFileSystemAccess: false
    );

    /// <summary>
    /// Creates default resource limits suitable for standard tasks.
    /// </summary>
    /// <returns>
    /// A <see cref="ResourceLimits"/> instance with moderate limits.
    /// </returns>
    public static ResourceLimits Standard => new(
        MaxMemoryBytes: 2L * 1024 * 1024 * 1024, // 2 GB
        MaxCpuTime: TimeSpan.FromMinutes(5),
        MaxExecutionTime: TimeSpan.FromMinutes(10),
        MaxOutputSize: 100 * 1024 * 1024, // 100 MB
        MaxThreads: 4,
        AllowNetworkAccess: false,
        AllowFileSystemAccess: false
    );

    /// <summary>
    /// Creates default resource limits suitable for heavy computational tasks.
    /// </summary>
    /// <returns>
    /// A <see cref="ResourceLimits"/> instance with generous limits.
    /// </returns>
    public static ResourceLimits Heavy => new(
        MaxMemoryBytes: 16L * 1024 * 1024 * 1024, // 16 GB
        MaxCpuTime: TimeSpan.FromMinutes(30),
        MaxExecutionTime: TimeSpan.FromHours(1),
        MaxOutputSize: 1L * 1024 * 1024 * 1024, // 1 GB
        MaxThreads: 16,
        AllowNetworkAccess: false,
        AllowFileSystemAccess: false
    );

    /// <summary>
    /// Creates unlimited resource limits (use with caution!).
    /// </summary>
    /// <returns>
    /// A <see cref="ResourceLimits"/> instance with no limits.
    /// </returns>
    public static ResourceLimits Unlimited => new(
        MaxMemoryBytes: null,
        MaxCpuTime: null,
        MaxExecutionTime: null,
        MaxOutputSize: null,
        MaxThreads: null,
        AllowNetworkAccess: true,
        AllowFileSystemAccess: true
    );
}

/// <summary>
/// Enumerates the supported compute runtime types.
/// </summary>
/// <remarks>
/// This enumeration allows the system to identify and route tasks to appropriate runtime strategies.
/// Additional runtimes can be added as new strategies are implemented.
/// </remarks>
public enum ComputeRuntime
{
    /// <summary>
    /// Unknown or unspecified runtime.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// WebAssembly (WASM) runtime.
    /// Sandboxed, portable, near-native performance.
    /// </summary>
    WASM = 1,

    /// <summary>
    /// Python runtime (CPython, PyPy, etc.).
    /// Dynamic, interpreted, extensive ecosystem.
    /// </summary>
    Python = 2,

    /// <summary>
    /// JavaScript/Node.js runtime (V8, SpiderMonkey, etc.).
    /// Dynamic, event-driven, asynchronous.
    /// </summary>
    JavaScript = 3,

    /// <summary>
    /// Native compiled code (C, C++, Rust, Go, etc.).
    /// Maximum performance, requires careful sandboxing.
    /// </summary>
    Native = 4,

    /// <summary>
    /// .NET runtime (CoreCLR, Mono).
    /// Managed, JIT-compiled, cross-platform.
    /// </summary>
    DotNet = 5,

    /// <summary>
    /// Java Virtual Machine (JVM).
    /// Managed, JIT-compiled, mature ecosystem.
    /// </summary>
    JVM = 6,

    /// <summary>
    /// Lua runtime (Lua, LuaJIT).
    /// Lightweight, embeddable, scripting language.
    /// </summary>
    Lua = 7,

    /// <summary>
    /// Ruby runtime (MRI, JRuby, TruffleRuby).
    /// Dynamic, object-oriented, expressive.
    /// </summary>
    Ruby = 8,

    /// <summary>
    /// PHP runtime (Zend Engine).
    /// Web-oriented, dynamic, widely deployed.
    /// </summary>
    PHP = 9,

    /// <summary>
    /// R runtime for statistical computing.
    /// Data analysis, statistics, visualization.
    /// </summary>
    R = 10,

    /// <summary>
    /// Julia runtime for technical computing.
    /// High-performance, dynamic, designed for numerical analysis.
    /// </summary>
    Julia = 11,

    /// <summary>
    /// Erlang/Elixir BEAM VM.
    /// Concurrent, distributed, fault-tolerant.
    /// </summary>
    BEAM = 12,

    /// <summary>
    /// Deno runtime (secure TypeScript/JavaScript).
    /// Secure by default, modern JavaScript/TypeScript.
    /// </summary>
    Deno = 13,

    /// <summary>
    /// Container-based runtime (Docker, Podman).
    /// Full isolation with OS-level virtualization.
    /// </summary>
    Container = 14,

    /// <summary>
    /// Custom or proprietary runtime.
    /// Used when the runtime doesn't match any standard types.
    /// </summary>
    Custom = 99
}
