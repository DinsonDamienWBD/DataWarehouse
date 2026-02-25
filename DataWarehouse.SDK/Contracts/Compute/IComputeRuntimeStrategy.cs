namespace DataWarehouse.SDK.Contracts.Compute;

/// <summary>
/// Strategy interface for compute runtime implementations.
/// Defines the contract for executing compute tasks in different runtime environments (WASM, Python, JavaScript, Native, etc.)
/// </summary>
/// <remarks>
/// <para>
/// This interface enables pluggable compute runtime strategies within the Ultimate DataWarehouse system.
/// Implementations can provide support for various execution environments while maintaining a consistent
/// abstraction layer for the warehouse infrastructure.
/// </para>
/// <para>
/// The interface supports task execution, resource management, and capability discovery to allow the
/// system to run code in isolated, sandboxed environments with appropriate resource limits.
/// </para>
/// <para>
/// Compute runtimes can be used for:
/// </para>
/// <list type="bullet">
/// <item><description>User-defined functions (UDFs) for data transformation</description></item>
/// <item><description>Custom analytics and ML inference</description></item>
/// <item><description>Data validation and enrichment logic</description></item>
/// <item><description>Plugin extensions and scripting</description></item>
/// <item><description>ETL processing and data pipelines</description></item>
/// </list>
/// </remarks>
public interface IComputeRuntimeStrategy
{
    /// <summary>
    /// Gets the runtime type this strategy implements.
    /// </summary>
    /// <value>
    /// The <see cref="ComputeRuntime"/> type (WASM, Python, JavaScript, etc.) that this strategy executes.
    /// </value>
    ComputeRuntime Runtime { get; }

    /// <summary>
    /// Gets the capabilities supported by this compute runtime strategy.
    /// </summary>
    /// <value>
    /// A <see cref="ComputeCapabilities"/> object describing what features this runtime supports,
    /// including streaming, sandboxing, resource limits, and supported languages.
    /// </value>
    ComputeCapabilities Capabilities { get; }

    /// <summary>
    /// Gets the list of supported runtime types this strategy can handle.
    /// </summary>
    /// <value>
    /// A read-only list of <see cref="ComputeRuntime"/> values. Most strategies support a single runtime,
    /// but some may handle multiple related runtimes (e.g., different versions or dialects).
    /// </value>
    IReadOnlyList<ComputeRuntime> SupportedRuntimes { get; }

    /// <summary>
    /// Asynchronously executes a compute task in the runtime environment.
    /// </summary>
    /// <param name="task">
    /// The <see cref="ComputeTask"/> containing the code, input data, and execution options.
    /// </param>
    /// <param name="cancellationToken">
    /// A token to cancel the execution if needed. Cancellation should cleanly terminate the compute task.
    /// </param>
    /// <returns>
    /// A task representing the asynchronous operation, containing a <see cref="ComputeResult"/> with the execution outcome.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This is the core method for executing code in the runtime. Implementations should:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Validate the compute task (check code syntax, resource limits, permissions)</description></item>
    /// <item><description>Initialize the runtime environment (load dependencies, set up sandbox)</description></item>
    /// <item><description>Execute the code with the provided input data</description></item>
    /// <item><description>Enforce resource limits (memory, CPU time, execution timeout)</description></item>
    /// <item><description>Capture output, errors, and execution metrics</description></item>
    /// <item><description>Clean up resources after execution</description></item>
    /// <item><description>Handle cancellation gracefully, stopping execution and releasing resources</description></item>
    /// </list>
    /// <para>
    /// Execution should be isolated and sandboxed to prevent:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Unauthorized file system access</description></item>
    /// <item><description>Network access (unless explicitly allowed)</description></item>
    /// <item><description>Process spawning</description></item>
    /// <item><description>Excessive resource consumption</description></item>
    /// <item><description>Access to sensitive system APIs</description></item>
    /// </list>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="task"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown if the task contains invalid code, unsupported language, or invalid resource limits.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the runtime is not in a valid state to execute tasks.
    /// </exception>
    /// <exception cref="TimeoutException">
    /// Thrown if the task execution exceeds the specified timeout.
    /// </exception>
    /// <exception cref="OutOfMemoryException">
    /// Thrown if the task exceeds memory limits during execution.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown if the operation is cancelled via the <paramref name="cancellationToken"/>.
    /// </exception>
    Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously initializes the compute runtime environment.
    /// </summary>
    /// <param name="cancellationToken">
    /// A token to cancel the initialization if needed.
    /// </param>
    /// <returns>
    /// A task representing the asynchronous initialization operation.
    /// </returns>
    /// <remarks>
    /// This method prepares the runtime for task execution. It may:
    /// <list type="bullet">
    /// <item><description>Load runtime binaries and libraries</description></item>
    /// <item><description>Initialize sandbox environments</description></item>
    /// <item><description>Warm up runtime caches</description></item>
    /// <item><description>Validate runtime configuration</description></item>
    /// </list>
    /// Implementations should be idempotent - multiple calls should not cause errors.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the runtime cannot be initialized due to configuration or dependency issues.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown if the operation is cancelled via the <paramref name="cancellationToken"/>.
    /// </exception>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously disposes of the compute runtime environment and releases all resources.
    /// </summary>
    /// <param name="cancellationToken">
    /// A token to cancel the disposal if needed.
    /// </param>
    /// <returns>
    /// A task representing the asynchronous disposal operation.
    /// </returns>
    /// <remarks>
    /// This method cleans up runtime resources. It should:
    /// <list type="bullet">
    /// <item><description>Terminate any running tasks gracefully</description></item>
    /// <item><description>Release memory and file handles</description></item>
    /// <item><description>Unload runtime binaries</description></item>
    /// <item><description>Clean up temporary files and caches</description></item>
    /// </list>
    /// After disposal, the runtime should not be used until re-initialized.
    /// </remarks>
    /// <exception cref="OperationCanceledException">
    /// Thrown if the operation is cancelled via the <paramref name="cancellationToken"/>.
    /// </exception>
    Task DisposeAsync(CancellationToken cancellationToken = default);
}
