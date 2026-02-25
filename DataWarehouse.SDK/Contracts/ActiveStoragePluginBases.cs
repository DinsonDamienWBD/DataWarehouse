using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.SDK.Contracts.Hierarchy;

namespace DataWarehouse.SDK.Contracts;

#region WASM Compute-on-Storage (Task 70)

/// <summary>
/// Specifies when a WASM function should be triggered for execution.
/// </summary>
public enum FunctionTrigger
{
    /// <summary>
    /// Trigger function when data is written to storage.
    /// </summary>
    OnWrite,

    /// <summary>
    /// Trigger function when data is read from storage.
    /// </summary>
    OnRead,

    /// <summary>
    /// Trigger function on a scheduled interval.
    /// </summary>
    OnSchedule,

    /// <summary>
    /// Trigger function in response to an external event.
    /// </summary>
    OnEvent,

    /// <summary>
    /// Function is triggered manually by explicit invocation.
    /// </summary>
    Manual
}

/// <summary>
/// Specifies the execution status of a WASM function invocation.
/// </summary>
public enum WasmExecutionStatus
{
    /// <summary>
    /// Function executed successfully.
    /// </summary>
    Success,

    /// <summary>
    /// Function execution failed with an error.
    /// </summary>
    Failure,

    /// <summary>
    /// Function execution exceeded the time limit.
    /// </summary>
    Timeout,

    /// <summary>
    /// Function execution was cancelled.
    /// </summary>
    Cancelled,

    /// <summary>
    /// Function execution exceeded memory limits.
    /// </summary>
    MemoryLimit,

    /// <summary>
    /// Function execution ran out of resources (CPU, I/O, etc.).
    /// </summary>
    OutOfResources
}

/// <summary>
/// Defines resource limits for WASM function execution.
/// Prevents runaway functions from consuming excessive resources.
/// </summary>
public class WasmResourceLimits
{
    /// <summary>
    /// Maximum memory in bytes the function can allocate.
    /// Default is 64 MB.
    /// </summary>
    public long MaxMemoryBytes { get; init; } = 64 * 1024 * 1024;

    /// <summary>
    /// Maximum CPU time in milliseconds.
    /// Default is 30 seconds.
    /// </summary>
    public int MaxCpuTimeMs { get; init; } = 30000;

    /// <summary>
    /// Maximum execution wall-clock time.
    /// Default is 60 seconds.
    /// </summary>
    public TimeSpan MaxExecutionTime { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Maximum number of instructions that can be executed.
    /// Used for metering. Default is 100 million.
    /// </summary>
    public long MaxInstructions { get; init; } = 100_000_000;

    /// <summary>
    /// Maximum stack size in bytes.
    /// Default is 1 MB.
    /// </summary>
    public int MaxStackSizeBytes { get; init; } = 1024 * 1024;

    /// <summary>
    /// Maximum number of concurrent instances of this function.
    /// Default is 10.
    /// </summary>
    public int MaxConcurrentInstances { get; init; } = 10;

    /// <summary>
    /// Maximum input payload size in bytes.
    /// Default is 10 MB.
    /// </summary>
    public long MaxInputSizeBytes { get; init; } = 10 * 1024 * 1024;

    /// <summary>
    /// Maximum output payload size in bytes.
    /// Default is 10 MB.
    /// </summary>
    public long MaxOutputSizeBytes { get; init; } = 10 * 1024 * 1024;

    /// <summary>
    /// Gets the default resource limits.
    /// </summary>
    public static WasmResourceLimits Default => new();

    /// <summary>
    /// Gets minimal resource limits for lightweight functions.
    /// </summary>
    public static WasmResourceLimits Minimal => new()
    {
        MaxMemoryBytes = 16 * 1024 * 1024,
        MaxCpuTimeMs = 5000,
        MaxExecutionTime = TimeSpan.FromSeconds(10),
        MaxInstructions = 10_000_000,
        MaxStackSizeBytes = 256 * 1024,
        MaxConcurrentInstances = 5,
        MaxInputSizeBytes = 1024 * 1024,
        MaxOutputSizeBytes = 1024 * 1024
    };

    /// <summary>
    /// Gets generous resource limits for compute-intensive functions.
    /// </summary>
    public static WasmResourceLimits Generous => new()
    {
        MaxMemoryBytes = 256 * 1024 * 1024,
        MaxCpuTimeMs = 120000,
        MaxExecutionTime = TimeSpan.FromMinutes(5),
        MaxInstructions = 1_000_000_000,
        MaxStackSizeBytes = 4 * 1024 * 1024,
        MaxConcurrentInstances = 20,
        MaxInputSizeBytes = 100 * 1024 * 1024,
        MaxOutputSizeBytes = 100 * 1024 * 1024
    };
}

/// <summary>
/// Metadata describing a deployed WASM function.
/// Contains all information needed to invoke and manage the function.
/// </summary>
public class WasmFunctionMetadata
{
    /// <summary>
    /// Unique identifier for the function.
    /// </summary>
    public string FunctionId { get; init; } = string.Empty;

    /// <summary>
    /// Human-readable name of the function.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Semantic version of the function.
    /// </summary>
    public string Version { get; init; } = "1.0.0";

    /// <summary>
    /// Description of what the function does.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Author or owner of the function.
    /// </summary>
    public string? Author { get; init; }

    /// <summary>
    /// Name of the entry point function in the WASM module.
    /// Default is "_start".
    /// </summary>
    public string EntryPoint { get; init; } = "_start";

    /// <summary>
    /// Triggers that will invoke this function.
    /// </summary>
    public IReadOnlyList<FunctionTrigger> Triggers { get; init; } = Array.Empty<FunctionTrigger>();

    /// <summary>
    /// Schedule expression for OnSchedule trigger (cron format).
    /// </summary>
    public string? ScheduleExpression { get; init; }

    /// <summary>
    /// Event patterns to match for OnEvent trigger.
    /// </summary>
    public IReadOnlyList<string> EventPatterns { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Resource limits for function execution.
    /// </summary>
    public WasmResourceLimits ResourceLimits { get; init; } = WasmResourceLimits.Default;

    /// <summary>
    /// Environment variables available to the function.
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Storage paths the function is allowed to access.
    /// Empty means no storage access.
    /// </summary>
    public IReadOnlyList<string> AllowedPaths { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Whether the function can make network calls.
    /// Default is false for security.
    /// </summary>
    public bool AllowNetworkAccess { get; init; }

    /// <summary>
    /// When the function was deployed.
    /// </summary>
    public DateTime DeployedAt { get; init; }

    /// <summary>
    /// When the function was last invoked.
    /// </summary>
    public DateTime? LastInvokedAt { get; init; }

    /// <summary>
    /// Total number of successful invocations.
    /// </summary>
    public long InvocationCount { get; init; }

    /// <summary>
    /// Custom tags for organization and filtering.
    /// </summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Execution context passed to a WASM function.
/// Contains input data and contextual information.
/// </summary>
public class WasmExecutionContext
{
    /// <summary>
    /// Unique identifier for this execution.
    /// </summary>
    public string ExecutionId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// ID of the function being executed.
    /// </summary>
    public string FunctionId { get; init; } = string.Empty;

    /// <summary>
    /// Trigger that caused this execution.
    /// </summary>
    public FunctionTrigger Trigger { get; init; }

    /// <summary>
    /// Input data for the function.
    /// </summary>
    public byte[] InputData { get; init; } = Array.Empty<byte>();

    /// <summary>
    /// Input data as a dictionary for structured inputs.
    /// </summary>
    public IReadOnlyDictionary<string, object>? InputParameters { get; init; }

    /// <summary>
    /// Path of the object that triggered execution (for OnWrite/OnRead).
    /// </summary>
    public string? TriggerPath { get; init; }

    /// <summary>
    /// Event name that triggered execution (for OnEvent).
    /// </summary>
    public string? TriggerEvent { get; init; }

    /// <summary>
    /// Correlation ID for tracing across systems.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// User or service that initiated the execution.
    /// </summary>
    public string? InitiatedBy { get; init; }

    /// <summary>
    /// Timestamp when execution started.
    /// </summary>
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Additional context metadata.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Result of a WASM function execution.
/// Contains output data, status, and performance metrics.
/// </summary>
public class WasmExecutionResult
{
    /// <summary>
    /// Execution ID from the context.
    /// </summary>
    public string ExecutionId { get; init; } = string.Empty;

    /// <summary>
    /// Function ID that was executed.
    /// </summary>
    public string FunctionId { get; init; } = string.Empty;

    /// <summary>
    /// Execution status.
    /// </summary>
    public WasmExecutionStatus Status { get; init; }

    /// <summary>
    /// Output data from the function.
    /// </summary>
    public byte[] OutputData { get; init; } = Array.Empty<byte>();

    /// <summary>
    /// Output as structured data (if applicable).
    /// </summary>
    public IReadOnlyDictionary<string, object>? OutputParameters { get; init; }

    /// <summary>
    /// Error message if execution failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Stack trace if execution failed.
    /// </summary>
    public string? StackTrace { get; init; }

    /// <summary>
    /// Total execution time.
    /// </summary>
    public TimeSpan ExecutionTime { get; init; }

    /// <summary>
    /// Peak memory usage in bytes.
    /// </summary>
    public long PeakMemoryBytes { get; init; }

    /// <summary>
    /// Number of WASM instructions executed.
    /// </summary>
    public long InstructionsExecuted { get; init; }

    /// <summary>
    /// CPU time consumed in milliseconds.
    /// </summary>
    public int CpuTimeMs { get; init; }

    /// <summary>
    /// Timestamp when execution completed.
    /// </summary>
    public DateTime CompletedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Log output from the function.
    /// </summary>
    public IReadOnlyList<string> Logs { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static WasmExecutionResult Success(string executionId, string functionId, byte[] output, TimeSpan executionTime) => new()
    {
        ExecutionId = executionId,
        FunctionId = functionId,
        Status = WasmExecutionStatus.Success,
        OutputData = output,
        ExecutionTime = executionTime
    };

    /// <summary>
    /// Creates a failure result.
    /// </summary>
    public static WasmExecutionResult Failure(string executionId, string functionId, string error, string? stackTrace = null) => new()
    {
        ExecutionId = executionId,
        FunctionId = functionId,
        Status = WasmExecutionStatus.Failure,
        ErrorMessage = error,
        StackTrace = stackTrace
    };
}

/// <summary>
/// Interface for WASM runtime providers.
/// Supports deploying, executing, and managing WASM functions for compute-on-storage.
/// </summary>
public interface IWasmRuntime : IPlugin
{
    /// <summary>
    /// Gets the list of supported WASM features (e.g., "simd", "threads", "bulk-memory").
    /// </summary>
    IReadOnlyList<string> SupportedFeatures { get; }

    /// <summary>
    /// Gets the maximum number of concurrent function executions.
    /// </summary>
    int MaxConcurrentExecutions { get; }

    /// <summary>
    /// Deploys a WASM function to the runtime.
    /// </summary>
    /// <param name="wasmBytes">Compiled WASM module bytes.</param>
    /// <param name="metadata">Function metadata.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Deployed function metadata with assigned ID.</returns>
    Task<WasmFunctionMetadata> DeployFunctionAsync(
        byte[] wasmBytes,
        WasmFunctionMetadata metadata,
        CancellationToken ct = default);

    /// <summary>
    /// Updates an existing function with new WASM code.
    /// </summary>
    /// <param name="functionId">ID of the function to update.</param>
    /// <param name="wasmBytes">New WASM module bytes.</param>
    /// <param name="metadata">Updated metadata.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Updated function metadata.</returns>
    Task<WasmFunctionMetadata> UpdateFunctionAsync(
        string functionId,
        byte[] wasmBytes,
        WasmFunctionMetadata metadata,
        CancellationToken ct = default);

    /// <summary>
    /// Undeploys a function from the runtime.
    /// </summary>
    /// <param name="functionId">ID of the function to undeploy.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the function was undeployed.</returns>
    Task<bool> UndeployFunctionAsync(string functionId, CancellationToken ct = default);

    /// <summary>
    /// Gets metadata for a deployed function.
    /// </summary>
    /// <param name="functionId">ID of the function.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Function metadata, or null if not found.</returns>
    Task<WasmFunctionMetadata?> GetFunctionAsync(string functionId, CancellationToken ct = default);

    /// <summary>
    /// Lists all deployed functions.
    /// </summary>
    /// <param name="filter">Optional filter by trigger type.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of function metadata.</returns>
    Task<IReadOnlyList<WasmFunctionMetadata>> ListFunctionsAsync(
        FunctionTrigger? filter = null,
        CancellationToken ct = default);

    /// <summary>
    /// Executes a function with the given context.
    /// </summary>
    /// <param name="functionId">ID of the function to execute.</param>
    /// <param name="context">Execution context with input data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Execution result.</returns>
    Task<WasmExecutionResult> ExecuteFunctionAsync(
        string functionId,
        WasmExecutionContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Executes a function with simple input/output.
    /// </summary>
    /// <param name="functionId">ID of the function to execute.</param>
    /// <param name="input">Input data bytes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Output data bytes.</returns>
    Task<byte[]> InvokeFunctionAsync(
        string functionId,
        byte[] input,
        CancellationToken ct = default);

    /// <summary>
    /// Gets execution statistics for a function.
    /// </summary>
    /// <param name="functionId">ID of the function.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Execution statistics.</returns>
    Task<WasmFunctionStatistics> GetStatisticsAsync(string functionId, CancellationToken ct = default);

    /// <summary>
    /// Validates a WASM module without deploying it.
    /// </summary>
    /// <param name="wasmBytes">WASM module bytes to validate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Validation result.</returns>
    Task<WasmValidationResult> ValidateModuleAsync(byte[] wasmBytes, CancellationToken ct = default);
}

/// <summary>
/// Statistics for a WASM function.
/// </summary>
public class WasmFunctionStatistics
{
    /// <summary>
    /// Function ID.
    /// </summary>
    public string FunctionId { get; init; } = string.Empty;

    /// <summary>
    /// Total number of invocations.
    /// </summary>
    public long TotalInvocations { get; init; }

    /// <summary>
    /// Number of successful invocations.
    /// </summary>
    public long SuccessfulInvocations { get; init; }

    /// <summary>
    /// Number of failed invocations.
    /// </summary>
    public long FailedInvocations { get; init; }

    /// <summary>
    /// Average execution time.
    /// </summary>
    public TimeSpan AverageExecutionTime { get; init; }

    /// <summary>
    /// Maximum execution time observed.
    /// </summary>
    public TimeSpan MaxExecutionTime { get; init; }

    /// <summary>
    /// Minimum execution time observed.
    /// </summary>
    public TimeSpan MinExecutionTime { get; init; }

    /// <summary>
    /// Average memory usage in bytes.
    /// </summary>
    public long AverageMemoryBytes { get; init; }

    /// <summary>
    /// Peak memory usage in bytes.
    /// </summary>
    public long PeakMemoryBytes { get; init; }

    /// <summary>
    /// Last invocation timestamp.
    /// </summary>
    public DateTime? LastInvocationAt { get; init; }
}

/// <summary>
/// Result of WASM module validation.
/// </summary>
public class WasmValidationResult
{
    /// <summary>
    /// Whether the module is valid.
    /// </summary>
    public bool IsValid { get; init; }

    /// <summary>
    /// Validation errors if not valid.
    /// </summary>
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Warnings that don't prevent deployment.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Detected WASM features used by the module.
    /// </summary>
    public IReadOnlyList<string> DetectedFeatures { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Exported functions in the module.
    /// </summary>
    public IReadOnlyList<string> ExportedFunctions { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Estimated memory requirements.
    /// </summary>
    public long EstimatedMemoryBytes { get; init; }
}

/// <summary>
/// Abstract base class for WASM function plugin implementations.
/// Provides compute-on-storage capabilities using WebAssembly.
/// Intelligence-aware: Supports AI-driven function optimization and execution planning.
/// </summary>
public abstract class WasmFunctionPluginBase : ComputePluginBase, IWasmRuntime
{
    private readonly BoundedDictionary<string, WasmFunctionMetadata> _functions = new BoundedDictionary<string, WasmFunctionMetadata>(1000);
    private readonly BoundedDictionary<string, WasmFunctionStatistics> _statistics = new BoundedDictionary<string, WasmFunctionStatistics>(1000);
    private int _activeExecutions;

    /// <inheritdoc/>
    public override string RuntimeType => "WASM";

    /// <inheritdoc/>
    public override Task<Dictionary<string, object>> ExecuteWorkloadAsync(Dictionary<string, object> workload, CancellationToken ct = default)
        => Task.FromResult(new Dictionary<string, object> { ["status"] = "delegated-to-wasm-runtime" });

    /// <summary>
    /// Gets the plugin category. Always DataTransformationProvider for WASM compute plugins.
    /// </summary>
    public override PluginCategory Category => PluginCategory.DataTransformationProvider;

    /// <summary>
    /// Gets the list of supported WASM features.
    /// Override to specify runtime capabilities.
    /// </summary>
    public virtual IReadOnlyList<string> SupportedFeatures => new[] { "bulk-memory", "mutable-global", "sign-ext" };

    /// <summary>
    /// Gets the maximum number of concurrent executions.
    /// Default is 100.
    /// </summary>
    public virtual int MaxConcurrentExecutions => 100;

    #region Intelligence Integration

    /// <summary>
    /// Capabilities declared by this WASM runtime provider.
    /// </summary>
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
    {
        new RegisteredCapability
        {
            CapabilityId = $"{Id}.wasm-runtime",
            DisplayName = $"{Name} - WASM Compute-on-Storage",
            Description = "WebAssembly function execution with sandboxed compute-on-storage",
            Category = CapabilityCategory.Pipeline,
            SubCategory = "ComputeOnStorage",
            PluginId = Id,
            PluginName = Name,
            PluginVersion = Version,
            Tags = new[] { "wasm", "compute", "serverless", "functions" },
            SemanticDescription = "Use this for executing custom logic directly on stored data using WebAssembly",
            Metadata = new Dictionary<string, object>
            {
                ["supportedFeatures"] = SupportedFeatures,
                ["maxConcurrentExecutions"] = MaxConcurrentExecutions
            }
        }
    };

    /// <summary>
    /// Gets static knowledge for Intelligence registration.
    /// </summary>
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        var knowledge = new List<KnowledgeObject>(base.GetStaticKnowledge());

        knowledge.Add(new KnowledgeObject
        {
            Id = $"{Id}.wasm.capability",
            Topic = "wasm-compute",
            SourcePluginId = Id,
            SourcePluginName = Name,
            KnowledgeType = "capability",
            Description = $"WASM runtime supporting {string.Join(", ", SupportedFeatures)}",
            Payload = new Dictionary<string, object>
            {
                ["supportedFeatures"] = SupportedFeatures,
                ["maxConcurrentExecutions"] = MaxConcurrentExecutions,
                ["supportsServerless"] = true,
                ["supportsEventTriggers"] = true
            },
            Tags = new[] { "wasm", "compute", "serverless" },
            Confidence = 1.0f,
            Timestamp = DateTimeOffset.UtcNow
        });

        return knowledge;
    }

    /// <summary>
    /// Requests AI-driven resource limit recommendation for a function.
    /// </summary>
    /// <param name="metadata">Function metadata.</param>
    /// <param name="historicalStats">Historical execution statistics.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Recommended resource limits.</returns>
    protected async Task<WasmResourceLimits?> RequestOptimalResourceLimitsAsync(
        WasmFunctionMetadata metadata,
        WasmFunctionStatistics? historicalStats,
        CancellationToken ct = default)
    {
        if (MessageBus == null) return null;

        try
        {
            var request = new PluginMessage
            {
                Type = "intelligence.recommend.request",
                CorrelationId = Guid.NewGuid().ToString("N"),
                Source = Id,
                Payload = new Dictionary<string, object>
                {
                    ["recommendationType"] = "wasm_resources",
                    ["functionId"] = metadata.FunctionId,
                    ["avgExecutionTime"] = historicalStats?.AverageExecutionTime.TotalMilliseconds ?? 0,
                    ["peakMemory"] = historicalStats?.PeakMemoryBytes ?? 0
                }
            };

            await MessageBus.PublishAsync("intelligence.recommend", request, ct);
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Requests AI-driven execution scheduling recommendation.
    /// </summary>
    /// <param name="functionId">Function to schedule.</param>
    /// <param name="systemLoad">Current system load metrics.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Recommended execution time.</returns>
    protected async Task<DateTimeOffset?> RequestOptimalExecutionTimeAsync(
        string functionId,
        Dictionary<string, double> systemLoad,
        CancellationToken ct = default)
    {
        if (MessageBus == null) return null;

        try
        {
            var request = new PluginMessage
            {
                Type = "intelligence.recommend.request",
                CorrelationId = Guid.NewGuid().ToString("N"),
                Source = Id,
                Payload = new Dictionary<string, object>
                {
                    ["recommendationType"] = "execution_time",
                    ["functionId"] = functionId,
                    ["systemLoad"] = systemLoad
                }
            };

            await MessageBus.PublishAsync("intelligence.recommend", request, ct);
            return null;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    /// <summary>
    /// Deploys a WASM function to the runtime.
    /// </summary>
    public virtual async Task<WasmFunctionMetadata> DeployFunctionAsync(
        byte[] wasmBytes,
        WasmFunctionMetadata metadata,
        CancellationToken ct = default)
    {
        // Validate the module first
        var validation = await ValidateModuleAsync(wasmBytes, ct);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"WASM module validation failed: {string.Join(", ", validation.Errors)}");
        }

        // Generate function ID if not provided
        var functionId = string.IsNullOrEmpty(metadata.FunctionId)
            ? $"func-{Guid.NewGuid():N}"
            : metadata.FunctionId;

        var deployedMetadata = new WasmFunctionMetadata
        {
            FunctionId = functionId,
            Name = metadata.Name,
            Version = metadata.Version,
            Description = metadata.Description,
            Author = metadata.Author,
            EntryPoint = metadata.EntryPoint,
            Triggers = metadata.Triggers,
            ScheduleExpression = metadata.ScheduleExpression,
            EventPatterns = metadata.EventPatterns,
            ResourceLimits = metadata.ResourceLimits,
            Environment = metadata.Environment,
            AllowedPaths = metadata.AllowedPaths,
            AllowNetworkAccess = metadata.AllowNetworkAccess,
            DeployedAt = DateTime.UtcNow,
            Tags = metadata.Tags
        };

        // Store the WASM bytes and metadata
        await StoreModuleAsync(functionId, wasmBytes, ct);
        _functions[functionId] = deployedMetadata;
        _statistics[functionId] = new WasmFunctionStatistics { FunctionId = functionId };

        return deployedMetadata;
    }

    /// <summary>
    /// Updates an existing function with new WASM code.
    /// </summary>
    public virtual async Task<WasmFunctionMetadata> UpdateFunctionAsync(
        string functionId,
        byte[] wasmBytes,
        WasmFunctionMetadata metadata,
        CancellationToken ct = default)
    {
        if (!_functions.ContainsKey(functionId))
        {
            throw new KeyNotFoundException($"Function {functionId} not found");
        }

        var validation = await ValidateModuleAsync(wasmBytes, ct);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"WASM module validation failed: {string.Join(", ", validation.Errors)}");
        }

        await StoreModuleAsync(functionId, wasmBytes, ct);

        var updatedMetadata = new WasmFunctionMetadata
        {
            FunctionId = functionId,
            Name = metadata.Name,
            Version = metadata.Version,
            Description = metadata.Description,
            Author = metadata.Author,
            EntryPoint = metadata.EntryPoint,
            Triggers = metadata.Triggers,
            ScheduleExpression = metadata.ScheduleExpression,
            EventPatterns = metadata.EventPatterns,
            ResourceLimits = metadata.ResourceLimits,
            Environment = metadata.Environment,
            AllowedPaths = metadata.AllowedPaths,
            AllowNetworkAccess = metadata.AllowNetworkAccess,
            DeployedAt = _functions[functionId].DeployedAt,
            LastInvokedAt = _functions[functionId].LastInvokedAt,
            InvocationCount = _functions[functionId].InvocationCount,
            Tags = metadata.Tags
        };

        _functions[functionId] = updatedMetadata;
        return updatedMetadata;
    }

    /// <summary>
    /// Undeploys a function from the runtime.
    /// </summary>
    public virtual async Task<bool> UndeployFunctionAsync(string functionId, CancellationToken ct = default)
    {
        if (_functions.TryRemove(functionId, out _))
        {
            _statistics.TryRemove(functionId, out _);
            await RemoveModuleAsync(functionId, ct);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Gets metadata for a deployed function.
    /// </summary>
    public virtual Task<WasmFunctionMetadata?> GetFunctionAsync(string functionId, CancellationToken ct = default)
    {
        _functions.TryGetValue(functionId, out var metadata);
        return Task.FromResult<WasmFunctionMetadata?>(metadata);
    }

    /// <summary>
    /// Lists all deployed functions.
    /// </summary>
    public virtual Task<IReadOnlyList<WasmFunctionMetadata>> ListFunctionsAsync(
        FunctionTrigger? filter = null,
        CancellationToken ct = default)
    {
        IEnumerable<WasmFunctionMetadata> functions = _functions.Values;

        if (filter.HasValue)
        {
            functions = functions.Where(f => f.Triggers.Contains(filter.Value));
        }

        return Task.FromResult<IReadOnlyList<WasmFunctionMetadata>>(functions.ToList());
    }

    /// <summary>
    /// Executes a function with the given context.
    /// </summary>
    public virtual async Task<WasmExecutionResult> ExecuteFunctionAsync(
        string functionId,
        WasmExecutionContext context,
        CancellationToken ct = default)
    {
        if (!_functions.TryGetValue(functionId, out var metadata))
        {
            return WasmExecutionResult.Failure(context.ExecutionId, functionId, "Function not found");
        }

        // Check concurrency limits
        if (Interlocked.Increment(ref _activeExecutions) > MaxConcurrentExecutions)
        {
            Interlocked.Decrement(ref _activeExecutions);
            return new WasmExecutionResult
            {
                ExecutionId = context.ExecutionId,
                FunctionId = functionId,
                Status = WasmExecutionStatus.OutOfResources,
                ErrorMessage = "Maximum concurrent executions exceeded"
            };
        }

        try
        {
            // Load the module
            var wasmBytes = await LoadModuleAsync(functionId, ct);
            if (wasmBytes == null)
            {
                return WasmExecutionResult.Failure(context.ExecutionId, functionId, "Module not found");
            }

            // Execute with timeout
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(metadata.ResourceLimits.MaxExecutionTime);

            var startTime = DateTime.UtcNow;
            var result = await ExecuteWasmModuleAsync(wasmBytes, metadata, context, cts.Token);
            var executionTime = DateTime.UtcNow - startTime;

            // Update statistics
            UpdateStatistics(functionId, result, executionTime);

            return result;
        }
        catch (OperationCanceledException)
        {
            return new WasmExecutionResult
            {
                ExecutionId = context.ExecutionId,
                FunctionId = functionId,
                Status = WasmExecutionStatus.Timeout,
                ErrorMessage = "Execution timed out"
            };
        }
        catch (Exception ex)
        {
            return WasmExecutionResult.Failure(context.ExecutionId, functionId, ex.Message, ex.StackTrace);
        }
        finally
        {
            Interlocked.Decrement(ref _activeExecutions);
        }
    }

    /// <summary>
    /// Executes a function with simple input/output.
    /// </summary>
    public virtual async Task<byte[]> InvokeFunctionAsync(
        string functionId,
        byte[] input,
        CancellationToken ct = default)
    {
        var context = new WasmExecutionContext
        {
            FunctionId = functionId,
            Trigger = FunctionTrigger.Manual,
            InputData = input
        };

        var result = await ExecuteFunctionAsync(functionId, context, ct);

        if (result.Status != WasmExecutionStatus.Success)
        {
            throw new InvalidOperationException(result.ErrorMessage ?? "Function execution failed");
        }

        return result.OutputData;
    }

    /// <summary>
    /// Gets execution statistics for a function.
    /// </summary>
    public virtual Task<WasmFunctionStatistics> GetStatisticsAsync(string functionId, CancellationToken ct = default)
    {
        if (_statistics.TryGetValue(functionId, out var stats))
        {
            return Task.FromResult(stats);
        }
        return Task.FromResult(new WasmFunctionStatistics { FunctionId = functionId });
    }

    /// <summary>
    /// Validates a WASM module without deploying it.
    /// Override to implement actual validation.
    /// </summary>
    public virtual Task<WasmValidationResult> ValidateModuleAsync(byte[] wasmBytes, CancellationToken ct = default)
    {
        // Basic validation - check WASM magic number
        if (wasmBytes.Length < 8)
        {
            return Task.FromResult(new WasmValidationResult
            {
                IsValid = false,
                Errors = new[] { "Module too small to be valid WASM" }
            });
        }

        // WASM magic: \0asm
        if (wasmBytes[0] != 0x00 || wasmBytes[1] != 0x61 ||
            wasmBytes[2] != 0x73 || wasmBytes[3] != 0x6D)
        {
            return Task.FromResult(new WasmValidationResult
            {
                IsValid = false,
                Errors = new[] { "Invalid WASM magic number" }
            });
        }

        return Task.FromResult(new WasmValidationResult
        {
            IsValid = true,
            DetectedFeatures = SupportedFeatures
        });
    }

    /// <summary>
    /// Stores WASM module bytes. Must be implemented by derived classes.
    /// </summary>
    protected abstract Task StoreModuleAsync(string functionId, byte[] wasmBytes, CancellationToken ct);

    /// <summary>
    /// Loads WASM module bytes. Must be implemented by derived classes.
    /// </summary>
    protected abstract Task<byte[]?> LoadModuleAsync(string functionId, CancellationToken ct);

    /// <summary>
    /// Removes WASM module. Must be implemented by derived classes.
    /// </summary>
    protected abstract Task RemoveModuleAsync(string functionId, CancellationToken ct);

    /// <summary>
    /// Executes a WASM module. Must be implemented by derived classes.
    /// </summary>
    protected abstract Task<WasmExecutionResult> ExecuteWasmModuleAsync(
        byte[] wasmBytes,
        WasmFunctionMetadata metadata,
        WasmExecutionContext context,
        CancellationToken ct);

    /// <summary>
    /// Updates execution statistics for a function.
    /// </summary>
    private void UpdateStatistics(string functionId, WasmExecutionResult result, TimeSpan executionTime)
    {
        _statistics.AddOrUpdate(
            functionId,
            _ => new WasmFunctionStatistics
            {
                FunctionId = functionId,
                TotalInvocations = 1,
                SuccessfulInvocations = result.Status == WasmExecutionStatus.Success ? 1 : 0,
                FailedInvocations = result.Status != WasmExecutionStatus.Success ? 1 : 0,
                AverageExecutionTime = executionTime,
                MaxExecutionTime = executionTime,
                MinExecutionTime = executionTime,
                AverageMemoryBytes = result.PeakMemoryBytes,
                PeakMemoryBytes = result.PeakMemoryBytes,
                LastInvocationAt = DateTime.UtcNow
            },
            (_, existing) =>
            {
                var total = existing.TotalInvocations + 1;
                var avgTime = TimeSpan.FromTicks(
                    (existing.AverageExecutionTime.Ticks * existing.TotalInvocations + executionTime.Ticks) / total);

                return new WasmFunctionStatistics
                {
                    FunctionId = functionId,
                    TotalInvocations = total,
                    SuccessfulInvocations = existing.SuccessfulInvocations +
                                            (result.Status == WasmExecutionStatus.Success ? 1 : 0),
                    FailedInvocations = existing.FailedInvocations +
                                        (result.Status != WasmExecutionStatus.Success ? 1 : 0),
                    AverageExecutionTime = avgTime,
                    MaxExecutionTime = executionTime > existing.MaxExecutionTime
                        ? executionTime
                        : existing.MaxExecutionTime,
                    MinExecutionTime = executionTime < existing.MinExecutionTime
                        ? executionTime
                        : existing.MinExecutionTime,
                    AverageMemoryBytes =
                        (existing.AverageMemoryBytes * existing.TotalInvocations + result.PeakMemoryBytes) / total,
                    PeakMemoryBytes = Math.Max(existing.PeakMemoryBytes, result.PeakMemoryBytes),
                    LastInvocationAt = DateTime.UtcNow
                };
            });
    }

    /// <summary>
    /// Starts the WASM runtime.
    /// </summary>
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Stops the WASM runtime.
    /// </summary>
    public override Task StopAsync() => Task.CompletedTask;

    /// <summary>
    /// Gets plugin metadata.
    /// </summary>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "WasmComputeOnStorage";
        metadata["SupportedFeatures"] = SupportedFeatures;
        metadata["MaxConcurrentExecutions"] = MaxConcurrentExecutions;
        metadata["SupportsServerless"] = true;
        metadata["SupportsEventTriggers"] = true;
        return metadata;
    }
}

#endregion

#region SQL-over-Object Data Virtualization (Task 71)

/// <summary>
/// Specifies the format of virtual table data.
/// </summary>
public enum VirtualTableFormat
{
    /// <summary>
    /// Comma-separated values format.
    /// </summary>
    Csv,

    /// <summary>
    /// JSON format (array of objects).
    /// </summary>
    Json,

    /// <summary>
    /// Newline-delimited JSON format.
    /// </summary>
    NdJson,

    /// <summary>
    /// Apache Parquet columnar format.
    /// </summary>
    Parquet,

    /// <summary>
    /// Apache Avro format.
    /// </summary>
    Avro,

    /// <summary>
    /// Apache ORC format.
    /// </summary>
    Orc
}

/// <summary>
/// Specifies the type of SQL join operation.
/// </summary>
public enum JoinType
{
    /// <summary>
    /// Inner join - returns matching rows from both tables.
    /// </summary>
    Inner,

    /// <summary>
    /// Left outer join - returns all rows from left table.
    /// </summary>
    Left,

    /// <summary>
    /// Right outer join - returns all rows from right table.
    /// </summary>
    Right,

    /// <summary>
    /// Full outer join - returns all rows from both tables.
    /// </summary>
    Full,

    /// <summary>
    /// Cross join - returns Cartesian product of both tables.
    /// </summary>
    Cross
}

/// <summary>
/// SQL data types for virtual table columns.
/// </summary>
public enum SqlDataType
{
    /// <summary>
    /// Unknown or undetected type.
    /// </summary>
    Unknown,

    /// <summary>
    /// Boolean type.
    /// </summary>
    Boolean,

    /// <summary>
    /// 32-bit integer.
    /// </summary>
    Integer,

    /// <summary>
    /// 64-bit integer.
    /// </summary>
    BigInt,

    /// <summary>
    /// Single-precision floating point.
    /// </summary>
    Float,

    /// <summary>
    /// Double-precision floating point.
    /// </summary>
    Double,

    /// <summary>
    /// Fixed-precision decimal.
    /// </summary>
    Decimal,

    /// <summary>
    /// Variable-length string.
    /// </summary>
    VarChar,

    /// <summary>
    /// Text/CLOB type.
    /// </summary>
    Text,

    /// <summary>
    /// Date without time.
    /// </summary>
    Date,

    /// <summary>
    /// Time without date.
    /// </summary>
    Time,

    /// <summary>
    /// Date and time.
    /// </summary>
    Timestamp,

    /// <summary>
    /// Binary data.
    /// </summary>
    Binary,

    /// <summary>
    /// JSON data.
    /// </summary>
    Json,

    /// <summary>
    /// Array type.
    /// </summary>
    Array
}

/// <summary>
/// Represents a column in a virtual table schema.
/// </summary>
public class VirtualTableColumn
{
    /// <summary>
    /// Column name.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// SQL data type of the column.
    /// </summary>
    public SqlDataType DataType { get; init; }

    /// <summary>
    /// Whether the column allows null values.
    /// </summary>
    public bool IsNullable { get; init; } = true;

    /// <summary>
    /// Maximum length for string/binary types.
    /// </summary>
    public int? MaxLength { get; init; }

    /// <summary>
    /// Precision for decimal types.
    /// </summary>
    public int? Precision { get; init; }

    /// <summary>
    /// Scale for decimal types.
    /// </summary>
    public int? Scale { get; init; }

    /// <summary>
    /// Whether this column is part of the primary key.
    /// </summary>
    public bool IsPrimaryKey { get; init; }

    /// <summary>
    /// Column position (0-based).
    /// </summary>
    public int Ordinal { get; init; }

    /// <summary>
    /// Default value expression.
    /// </summary>
    public string? DefaultValue { get; init; }

    /// <summary>
    /// Description of the column.
    /// </summary>
    public string? Description { get; init; }
}

/// <summary>
/// Schema definition for a virtual table.
/// </summary>
public class VirtualTableSchema
{
    /// <summary>
    /// Table name.
    /// </summary>
    public string TableName { get; init; } = string.Empty;

    /// <summary>
    /// Schema name (e.g., "dbo", "public").
    /// </summary>
    public string? SchemaName { get; init; }

    /// <summary>
    /// Column definitions.
    /// </summary>
    public IReadOnlyList<VirtualTableColumn> Columns { get; init; } = Array.Empty<VirtualTableColumn>();

    /// <summary>
    /// Primary key column names.
    /// </summary>
    public IReadOnlyList<string> PrimaryKeyColumns { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Source format of the underlying data.
    /// </summary>
    public VirtualTableFormat SourceFormat { get; init; }

    /// <summary>
    /// URI or path to the source data.
    /// </summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>
    /// Estimated row count (for query planning).
    /// </summary>
    public long? EstimatedRowCount { get; init; }

    /// <summary>
    /// Whether the schema was auto-inferred.
    /// </summary>
    public bool IsInferred { get; init; }

    /// <summary>
    /// When the schema was last updated.
    /// </summary>
    public DateTime LastUpdated { get; init; }

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Execution plan for a SQL query.
/// </summary>
public class QueryExecutionPlan
{
    /// <summary>
    /// Unique plan identifier.
    /// </summary>
    public string PlanId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Original SQL query.
    /// </summary>
    public string Query { get; init; } = string.Empty;

    /// <summary>
    /// Optimized query plan as a tree.
    /// </summary>
    public QueryPlanNode RootNode { get; init; } = new();

    /// <summary>
    /// Tables involved in the query.
    /// </summary>
    public IReadOnlyList<string> TablesAccessed { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Estimated cost of execution.
    /// </summary>
    public double EstimatedCost { get; init; }

    /// <summary>
    /// Estimated rows to be returned.
    /// </summary>
    public long EstimatedRows { get; init; }

    /// <summary>
    /// Whether the plan uses indexes.
    /// </summary>
    public bool UsesIndexes { get; init; }

    /// <summary>
    /// Whether the plan can be parallelized.
    /// </summary>
    public bool CanParallelize { get; init; }

    /// <summary>
    /// Warnings about the plan.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Time taken to generate the plan.
    /// </summary>
    public TimeSpan PlanningTime { get; init; }
}

/// <summary>
/// A node in the query execution plan tree.
/// </summary>
public class QueryPlanNode
{
    /// <summary>
    /// Node operation type (e.g., "Scan", "Filter", "Join", "Sort").
    /// </summary>
    public string Operation { get; init; } = string.Empty;

    /// <summary>
    /// Estimated cost for this node.
    /// </summary>
    public double EstimatedCost { get; init; }

    /// <summary>
    /// Estimated rows output by this node.
    /// </summary>
    public long EstimatedRows { get; init; }

    /// <summary>
    /// Actual rows (populated after execution).
    /// </summary>
    public long? ActualRows { get; init; }

    /// <summary>
    /// Child nodes in the plan tree.
    /// </summary>
    public IReadOnlyList<QueryPlanNode> Children { get; init; } = Array.Empty<QueryPlanNode>();

    /// <summary>
    /// Additional properties for this node.
    /// </summary>
    public IReadOnlyDictionary<string, object> Properties { get; init; } = new Dictionary<string, object>();
}

/// <summary>
/// Result of a SQL query execution.
/// </summary>
public class SqlQueryResult
{
    /// <summary>
    /// Column schema of the result.
    /// </summary>
    public IReadOnlyList<VirtualTableColumn> Columns { get; init; } = Array.Empty<VirtualTableColumn>();

    /// <summary>
    /// Result rows as arrays of objects.
    /// </summary>
    public IReadOnlyList<object?[]> Rows { get; init; } = Array.Empty<object?[]>();

    /// <summary>
    /// Total number of rows returned.
    /// </summary>
    public long RowCount { get; init; }

    /// <summary>
    /// Total number of rows affected (for DML).
    /// </summary>
    public long RowsAffected { get; init; }

    /// <summary>
    /// Whether more rows are available.
    /// </summary>
    public bool HasMoreRows { get; init; }

    /// <summary>
    /// Continuation token for pagination.
    /// </summary>
    public string? ContinuationToken { get; init; }

    /// <summary>
    /// Query execution time.
    /// </summary>
    public TimeSpan ExecutionTime { get; init; }

    /// <summary>
    /// Bytes scanned during execution.
    /// </summary>
    public long BytesScanned { get; init; }

    /// <summary>
    /// The execution plan used.
    /// </summary>
    public QueryExecutionPlan? ExecutionPlan { get; init; }

    /// <summary>
    /// Warnings generated during execution.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Interface for virtual table providers.
/// Enables SQL queries over object storage data.
/// </summary>
public interface IVirtualTableProvider : IPlugin
{
    /// <summary>
    /// Gets supported data formats.
    /// </summary>
    IReadOnlyList<VirtualTableFormat> SupportedFormats { get; }

    /// <summary>
    /// Gets the SQL dialect supported (e.g., "ANSI", "PostgreSQL", "Presto").
    /// </summary>
    string SqlDialect { get; }

    /// <summary>
    /// Registers a virtual table from a data source.
    /// </summary>
    /// <param name="tableName">Name for the virtual table.</param>
    /// <param name="sourcePath">Path to the source data.</param>
    /// <param name="format">Data format.</param>
    /// <param name="schema">Optional explicit schema (auto-inferred if not provided).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Registered table schema.</returns>
    Task<VirtualTableSchema> RegisterTableAsync(
        string tableName,
        string sourcePath,
        VirtualTableFormat format,
        VirtualTableSchema? schema = null,
        CancellationToken ct = default);

    /// <summary>
    /// Unregisters a virtual table.
    /// </summary>
    /// <param name="tableName">Name of the table to unregister.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if unregistered.</returns>
    Task<bool> UnregisterTableAsync(string tableName, CancellationToken ct = default);

    /// <summary>
    /// Gets the schema for a registered table.
    /// </summary>
    /// <param name="tableName">Table name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Table schema, or null if not found.</returns>
    Task<VirtualTableSchema?> GetTableSchemaAsync(string tableName, CancellationToken ct = default);

    /// <summary>
    /// Lists all registered tables.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of table schemas.</returns>
    Task<IReadOnlyList<VirtualTableSchema>> ListTablesAsync(CancellationToken ct = default);

    /// <summary>
    /// Executes a SQL query.
    /// </summary>
    /// <param name="sql">SQL query string.</param>
    /// <param name="parameters">Query parameters.</param>
    /// <param name="maxRows">Maximum rows to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Query result.</returns>
    Task<SqlQueryResult> ExecuteQueryAsync(
        string sql,
        IReadOnlyDictionary<string, object>? parameters = null,
        int maxRows = 10000,
        CancellationToken ct = default);

    /// <summary>
    /// Gets the execution plan for a query without executing it.
    /// </summary>
    /// <param name="sql">SQL query string.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Execution plan.</returns>
    Task<QueryExecutionPlan> ExplainQueryAsync(string sql, CancellationToken ct = default);

    /// <summary>
    /// Refreshes the schema for a table by re-reading source data.
    /// </summary>
    /// <param name="tableName">Table name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Updated schema.</returns>
    Task<VirtualTableSchema> RefreshSchemaAsync(string tableName, CancellationToken ct = default);
}

/// <summary>
/// Utility class for automatic schema inference from data samples.
/// </summary>
public static class SchemaInference
{
    /// <summary>
    /// Infers SQL data type from a sample value.
    /// </summary>
    /// <param name="value">Sample value.</param>
    /// <returns>Inferred SQL data type.</returns>
    public static SqlDataType InferType(object? value)
    {
        return value switch
        {
            null => SqlDataType.Unknown,
            bool => SqlDataType.Boolean,
            byte or sbyte or short or ushort or int or uint => SqlDataType.Integer,
            long or ulong => SqlDataType.BigInt,
            float => SqlDataType.Float,
            double => SqlDataType.Double,
            decimal => SqlDataType.Decimal,
            string s => InferStringType(s),
            DateTime => SqlDataType.Timestamp,
            DateTimeOffset => SqlDataType.Timestamp,
            TimeSpan => SqlDataType.Time,
            byte[] => SqlDataType.Binary,
            JsonElement je => InferJsonElementType(je),
            System.Collections.IEnumerable => SqlDataType.Array,
            _ => SqlDataType.Text
        };
    }

    /// <summary>
    /// Infers type from a string value by attempting parsing.
    /// </summary>
    private static SqlDataType InferStringType(string s)
    {
        if (string.IsNullOrEmpty(s)) return SqlDataType.VarChar;
        if (bool.TryParse(s, out _)) return SqlDataType.Boolean;
        if (int.TryParse(s, out _)) return SqlDataType.Integer;
        if (long.TryParse(s, out _)) return SqlDataType.BigInt;
        if (double.TryParse(s, out _)) return SqlDataType.Double;
        if (DateTime.TryParse(s, out _)) return SqlDataType.Timestamp;
        if (s.Length > 255) return SqlDataType.Text;
        return SqlDataType.VarChar;
    }

    /// <summary>
    /// Infers type from a JSON element.
    /// </summary>
    private static SqlDataType InferJsonElementType(JsonElement je)
    {
        return je.ValueKind switch
        {
            JsonValueKind.True or JsonValueKind.False => SqlDataType.Boolean,
            JsonValueKind.Number when je.TryGetInt32(out _) => SqlDataType.Integer,
            JsonValueKind.Number when je.TryGetInt64(out _) => SqlDataType.BigInt,
            JsonValueKind.Number => SqlDataType.Double,
            JsonValueKind.String when DateTime.TryParse(je.GetString(), out _) => SqlDataType.Timestamp,
            JsonValueKind.String => SqlDataType.VarChar,
            JsonValueKind.Array => SqlDataType.Array,
            JsonValueKind.Object => SqlDataType.Json,
            _ => SqlDataType.Unknown
        };
    }

    /// <summary>
    /// Infers a complete schema from sample data rows.
    /// </summary>
    /// <param name="tableName">Name for the table.</param>
    /// <param name="headers">Column headers.</param>
    /// <param name="sampleRows">Sample data rows.</param>
    /// <param name="sourcePath">Source data path.</param>
    /// <param name="format">Source data format.</param>
    /// <returns>Inferred schema.</returns>
    public static VirtualTableSchema InferSchema(
        string tableName,
        IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<object?>> sampleRows,
        string sourcePath,
        VirtualTableFormat format)
    {
        var columns = new List<VirtualTableColumn>();

        for (int i = 0; i < headers.Count; i++)
        {
            var columnName = headers[i];
            var types = new List<SqlDataType>();
            bool hasNull = false;

            foreach (var row in sampleRows)
            {
                if (i < row.Count)
                {
                    var value = row[i];
                    if (value == null)
                    {
                        hasNull = true;
                    }
                    else
                    {
                        types.Add(InferType(value));
                    }
                }
            }

            // Determine the most common type
            var inferredType = types.Count > 0
                ? types.GroupBy(t => t).OrderByDescending(g => g.Count()).First().Key
                : SqlDataType.VarChar;

            columns.Add(new VirtualTableColumn
            {
                Name = columnName,
                DataType = inferredType,
                IsNullable = hasNull || types.Count < sampleRows.Count,
                Ordinal = i
            });
        }

        return new VirtualTableSchema
        {
            TableName = tableName,
            Columns = columns,
            SourceFormat = format,
            SourcePath = sourcePath,
            IsInferred = true,
            LastUpdated = DateTime.UtcNow,
            EstimatedRowCount = sampleRows.Count
        };
    }

    /// <summary>
    /// Merges two column type observations, preferring the more general type.
    /// </summary>
    /// <param name="type1">First type.</param>
    /// <param name="type2">Second type.</param>
    /// <returns>Merged type.</returns>
    public static SqlDataType MergeTypes(SqlDataType type1, SqlDataType type2)
    {
        if (type1 == type2) return type1;
        if (type1 == SqlDataType.Unknown) return type2;
        if (type2 == SqlDataType.Unknown) return type1;

        // Numeric type promotion
        if (IsNumeric(type1) && IsNumeric(type2))
        {
            var numericOrder = new[] { SqlDataType.Integer, SqlDataType.BigInt, SqlDataType.Float, SqlDataType.Double, SqlDataType.Decimal };
            return numericOrder[Math.Max(Array.IndexOf(numericOrder, type1), Array.IndexOf(numericOrder, type2))];
        }

        // String type promotion
        if (IsString(type1) && IsString(type2))
        {
            return SqlDataType.Text;
        }

        // Default to text for mixed types
        return SqlDataType.Text;
    }

    private static bool IsNumeric(SqlDataType type) =>
        type is SqlDataType.Integer or SqlDataType.BigInt or SqlDataType.Float or SqlDataType.Double or SqlDataType.Decimal;

    private static bool IsString(SqlDataType type) =>
        type is SqlDataType.VarChar or SqlDataType.Text;
}

/// <summary>
/// Abstract base class for data virtualization plugin implementations.
/// Enables SQL queries over object storage data.
/// Intelligence-aware: Supports AI-driven query optimization and schema inference.
/// </summary>
public abstract class DataVirtualizationPluginBase : InterfacePluginBase, IVirtualTableProvider
{
    private readonly BoundedDictionary<string, VirtualTableSchema> _tables = new BoundedDictionary<string, VirtualTableSchema>(1000);

    /// <inheritdoc/>
    public override string Protocol => "SQL";

    /// <summary>
    /// Gets the plugin category. Always InterfaceProvider for virtualization plugins.
    /// </summary>
    public override PluginCategory Category => PluginCategory.InterfaceProvider;

    /// <summary>
    /// Gets supported data formats.
    /// Override to specify runtime capabilities.
    /// </summary>
    public virtual IReadOnlyList<VirtualTableFormat> SupportedFormats =>
        new[] { VirtualTableFormat.Csv, VirtualTableFormat.Json, VirtualTableFormat.NdJson, VirtualTableFormat.Parquet };

    /// <summary>
    /// Gets the SQL dialect supported.
    /// Default is ANSI SQL.
    /// </summary>
    public virtual string SqlDialect => "ANSI";

    #region Intelligence Integration

    /// <summary>
    /// Capabilities declared by this data virtualization provider.
    /// </summary>
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
    {
        new RegisteredCapability
        {
            CapabilityId = $"{Id}.data-virtualization",
            DisplayName = $"{Name} - SQL Data Virtualization",
            Description = "SQL queries over object storage with schema inference and query optimization",
            Category = CapabilityCategory.Pipeline,
            SubCategory = "DataVirtualization",
            PluginId = Id,
            PluginName = Name,
            PluginVersion = Version,
            Tags = new[] { "sql", "virtualization", "query", "analytics" },
            SemanticDescription = "Use this for running SQL queries directly on object storage data without ETL",
            Metadata = new Dictionary<string, object>
            {
                ["supportedFormats"] = SupportedFormats.Select(f => f.ToString()).ToArray(),
                ["sqlDialect"] = SqlDialect
            }
        }
    };

    /// <summary>
    /// Gets static knowledge for Intelligence registration.
    /// </summary>
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        var knowledge = new List<KnowledgeObject>(base.GetStaticKnowledge());

        knowledge.Add(new KnowledgeObject
        {
            Id = $"{Id}.virtualization.capability",
            Topic = "data-virtualization",
            SourcePluginId = Id,
            SourcePluginName = Name,
            KnowledgeType = "capability",
            Description = $"Data virtualization with {SqlDialect} SQL support",
            Payload = new Dictionary<string, object>
            {
                ["supportedFormats"] = SupportedFormats.Select(f => f.ToString()).ToArray(),
                ["sqlDialect"] = SqlDialect,
                ["supportsSchemaInference"] = true,
                ["supportsQueryOptimization"] = true
            },
            Tags = new[] { "sql", "virtualization", SqlDialect.ToLowerInvariant() },
            Confidence = 1.0f,
            Timestamp = DateTimeOffset.UtcNow
        });

        return knowledge;
    }

    /// <summary>
    /// Requests AI-driven query optimization suggestions.
    /// </summary>
    /// <param name="sql">SQL query to optimize.</param>
    /// <param name="plan">Current execution plan.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Optimized SQL query, or null if unavailable.</returns>
    protected async Task<string?> RequestQueryOptimizationAsync(
        string sql,
        QueryExecutionPlan plan,
        CancellationToken ct = default)
    {
        if (MessageBus == null) return null;

        try
        {
            var request = new PluginMessage
            {
                Type = "intelligence.optimize.request",
                CorrelationId = Guid.NewGuid().ToString("N"),
                Source = Id,
                Payload = new Dictionary<string, object>
                {
                    ["optimizationType"] = "sql_query",
                    ["sql"] = sql,
                    ["estimatedCost"] = plan.EstimatedCost,
                    ["tablesAccessed"] = plan.TablesAccessed
                }
            };

            await MessageBus.PublishAsync("intelligence.optimize", request, ct);
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Requests AI-driven schema enhancement with semantic annotations.
    /// </summary>
    /// <param name="schema">Schema to enhance.</param>
    /// <param name="sampleData">Sample data for analysis.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Enhanced schema with descriptions.</returns>
    protected async Task<VirtualTableSchema?> RequestSchemaEnhancementAsync(
        VirtualTableSchema schema,
        IReadOnlyList<object?[]> sampleData,
        CancellationToken ct = default)
    {
        if (MessageBus == null) return null;

        try
        {
            var request = new PluginMessage
            {
                Type = "intelligence.enhance.request",
                CorrelationId = Guid.NewGuid().ToString("N"),
                Source = Id,
                Payload = new Dictionary<string, object>
                {
                    ["enhancementType"] = "schema",
                    ["tableName"] = schema.TableName,
                    ["columnCount"] = schema.Columns.Count,
                    ["sampleRowCount"] = sampleData.Count
                }
            };

            await MessageBus.PublishAsync("intelligence.enhance", request, ct);
            return null;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    /// <summary>
    /// Registers a virtual table from a data source.
    /// </summary>
    public virtual async Task<VirtualTableSchema> RegisterTableAsync(
        string tableName,
        string sourcePath,
        VirtualTableFormat format,
        VirtualTableSchema? schema = null,
        CancellationToken ct = default)
    {
        if (!SupportedFormats.Contains(format))
        {
            throw new NotSupportedException($"Format {format} is not supported");
        }

        var tableSchema = schema ?? await InferSchemaFromSourceAsync(tableName, sourcePath, format, ct);
        _tables[tableName] = tableSchema;
        return tableSchema;
    }

    /// <summary>
    /// Unregisters a virtual table.
    /// </summary>
    public virtual Task<bool> UnregisterTableAsync(string tableName, CancellationToken ct = default)
    {
        return Task.FromResult(_tables.TryRemove(tableName, out _));
    }

    /// <summary>
    /// Gets the schema for a registered table.
    /// </summary>
    public virtual Task<VirtualTableSchema?> GetTableSchemaAsync(string tableName, CancellationToken ct = default)
    {
        _tables.TryGetValue(tableName, out var schema);
        return Task.FromResult<VirtualTableSchema?>(schema);
    }

    /// <summary>
    /// Lists all registered tables.
    /// </summary>
    public virtual Task<IReadOnlyList<VirtualTableSchema>> ListTablesAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<VirtualTableSchema>>(_tables.Values.ToList());
    }

    /// <summary>
    /// Executes a SQL query.
    /// </summary>
    public virtual async Task<SqlQueryResult> ExecuteQueryAsync(
        string sql,
        IReadOnlyDictionary<string, object>? parameters = null,
        int maxRows = 10000,
        CancellationToken ct = default)
    {
        var startTime = DateTime.UtcNow;

        // Parse and validate the query
        var plan = await ExplainQueryAsync(sql, ct);

        // Verify all tables exist
        foreach (var table in plan.TablesAccessed)
        {
            if (!_tables.ContainsKey(table))
            {
                throw new InvalidOperationException($"Table '{table}' is not registered");
            }
        }

        // Execute the query
        var result = await ExecuteQueryInternalAsync(sql, parameters, maxRows, plan, ct);

        return new SqlQueryResult
        {
            Columns = result.Columns,
            Rows = result.Rows,
            RowCount = result.Rows.Count,
            HasMoreRows = result.HasMoreRows,
            ContinuationToken = result.ContinuationToken,
            ExecutionTime = DateTime.UtcNow - startTime,
            BytesScanned = result.BytesScanned,
            ExecutionPlan = plan,
            Warnings = plan.Warnings
        };
    }

    /// <summary>
    /// Gets the execution plan for a query without executing it.
    /// </summary>
    public virtual Task<QueryExecutionPlan> ExplainQueryAsync(string sql, CancellationToken ct = default)
    {
        var startTime = DateTime.UtcNow;
        var tables = ExtractTableNames(sql);

        var plan = new QueryExecutionPlan
        {
            Query = sql,
            TablesAccessed = tables,
            RootNode = new QueryPlanNode
            {
                Operation = "Query",
                EstimatedCost = 1.0,
                EstimatedRows = 1000
            },
            EstimatedCost = 1.0,
            EstimatedRows = 1000,
            PlanningTime = DateTime.UtcNow - startTime
        };

        return Task.FromResult(plan);
    }

    /// <summary>
    /// Refreshes the schema for a table by re-reading source data.
    /// </summary>
    public virtual async Task<VirtualTableSchema> RefreshSchemaAsync(string tableName, CancellationToken ct = default)
    {
        if (!_tables.TryGetValue(tableName, out var existingSchema))
        {
            throw new KeyNotFoundException($"Table '{tableName}' is not registered");
        }

        var newSchema = await InferSchemaFromSourceAsync(
            tableName,
            existingSchema.SourcePath,
            existingSchema.SourceFormat,
            ct);

        _tables[tableName] = newSchema;
        return newSchema;
    }

    /// <summary>
    /// Infers schema from a data source. Must be implemented by derived classes.
    /// </summary>
    protected abstract Task<VirtualTableSchema> InferSchemaFromSourceAsync(
        string tableName,
        string sourcePath,
        VirtualTableFormat format,
        CancellationToken ct);

    /// <summary>
    /// Executes a query and returns results. Must be implemented by derived classes.
    /// </summary>
    protected abstract Task<SqlQueryResult> ExecuteQueryInternalAsync(
        string sql,
        IReadOnlyDictionary<string, object>? parameters,
        int maxRows,
        QueryExecutionPlan plan,
        CancellationToken ct);

    /// <summary>
    /// Extracts table names from a SQL query.
    /// Basic implementation - override for more sophisticated parsing.
    /// </summary>
    protected virtual IReadOnlyList<string> ExtractTableNames(string sql)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var words = sql.Split(' ', '\t', '\n', '\r', ',', '(', ')', ';')
            .Select(w => w.Trim())
            .Where(w => !string.IsNullOrEmpty(w))
            .ToList();

        for (int i = 0; i < words.Count - 1; i++)
        {
            if (words[i].Equals("FROM", StringComparison.OrdinalIgnoreCase) ||
                words[i].Equals("JOIN", StringComparison.OrdinalIgnoreCase) ||
                words[i].Equals("INTO", StringComparison.OrdinalIgnoreCase))
            {
                var tableName = words[i + 1];
                if (!IsKeyword(tableName))
                {
                    tables.Add(tableName);
                }
            }
        }

        return tables.ToList();
    }

    /// <summary>
    /// Checks if a word is a SQL keyword.
    /// </summary>
    private static bool IsKeyword(string word)
    {
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "IN", "LIKE", "BETWEEN",
            "ORDER", "BY", "GROUP", "HAVING", "LIMIT", "OFFSET", "AS", "ON",
            "LEFT", "RIGHT", "INNER", "OUTER", "FULL", "CROSS", "JOIN",
            "INSERT", "UPDATE", "DELETE", "CREATE", "DROP", "ALTER", "TABLE"
        };
        return keywords.Contains(word);
    }

    /// <summary>
    /// Starts the virtualization engine.
    /// </summary>
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Stops the virtualization engine.
    /// </summary>
    public override Task StopAsync() => Task.CompletedTask;

    /// <summary>
    /// Gets plugin metadata.
    /// </summary>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "DataVirtualization";
        metadata["SupportedFormats"] = SupportedFormats.Select(f => f.ToString()).ToArray();
        metadata["SqlDialect"] = SqlDialect;
        metadata["SupportsSqlOverObjects"] = true;
        metadata["SupportsSchemaInference"] = true;
        return metadata;
    }
}

#endregion

#region Auto-Transcoding Pipeline (Task 72)

/// <summary>
/// Specifies media formats for transcoding.
/// </summary>
public enum MediaFormat
{
    // Video formats
    /// <summary>H.264/AVC video format.</summary>
    H264,
    /// <summary>H.265/HEVC video format.</summary>
    H265,
    /// <summary>VP8 video format.</summary>
    VP8,
    /// <summary>VP9 video format.</summary>
    VP9,
    /// <summary>AV1 video format.</summary>
    AV1,
    /// <summary>MPEG-4 video format.</summary>
    Mpeg4,
    /// <summary>WebM container format.</summary>
    WebM,
    /// <summary>MKV container format.</summary>
    Mkv,
    /// <summary>MP4 container format.</summary>
    Mp4,
    /// <summary>AVI container format.</summary>
    Avi,
    /// <summary>MOV container format.</summary>
    Mov,

    // Audio formats
    /// <summary>AAC audio format.</summary>
    Aac,
    /// <summary>MP3 audio format.</summary>
    Mp3,
    /// <summary>Opus audio format.</summary>
    Opus,
    /// <summary>Vorbis audio format.</summary>
    Vorbis,
    /// <summary>FLAC audio format.</summary>
    Flac,
    /// <summary>WAV audio format.</summary>
    Wav,
    /// <summary>OGG container format.</summary>
    Ogg,

    // Image formats
    /// <summary>JPEG image format.</summary>
    Jpeg,
    /// <summary>PNG image format.</summary>
    Png,
    /// <summary>WebP image format.</summary>
    WebP,
    /// <summary>GIF image format.</summary>
    Gif,
    /// <summary>BMP image format.</summary>
    Bmp,
    /// <summary>TIFF image format.</summary>
    Tiff,
    /// <summary>AVIF image format.</summary>
    Avif,
    /// <summary>HEIF/HEIC image format.</summary>
    Heif,

    // Document formats
    /// <summary>PDF document format.</summary>
    Pdf,
    /// <summary>DOCX document format.</summary>
    Docx,
    /// <summary>XLSX spreadsheet format.</summary>
    Xlsx,
    /// <summary>PPTX presentation format.</summary>
    Pptx,
    /// <summary>Plain text format.</summary>
    Txt,
    /// <summary>HTML format.</summary>
    Html,
    /// <summary>Markdown format.</summary>
    Markdown,

    // Raw/Unknown
    /// <summary>Unknown or raw format.</summary>
    Unknown
}

/// <summary>
/// Specifies the status of a transcoding job.
/// </summary>
public enum TranscodingStatus
{
    /// <summary>Job is queued and waiting to start.</summary>
    Queued,
    /// <summary>Job is currently being processed.</summary>
    InProgress,
    /// <summary>Job completed successfully.</summary>
    Completed,
    /// <summary>Job failed with an error.</summary>
    Failed,
    /// <summary>Job was cancelled.</summary>
    Cancelled
}

/// <summary>
/// Quality presets for transcoding.
/// </summary>
public enum QualityPreset
{
    /// <summary>Draft quality - fastest processing.</summary>
    Draft,
    /// <summary>Low quality - fast processing.</summary>
    Low,
    /// <summary>Medium quality - balanced.</summary>
    Medium,
    /// <summary>High quality - slower processing.</summary>
    High,
    /// <summary>Maximum quality - slowest processing.</summary>
    Maximum,
    /// <summary>Lossless - no quality loss.</summary>
    Lossless
}

/// <summary>
/// Profile defining transcoding settings.
/// </summary>
public class TranscodingProfile
{
    /// <summary>
    /// Profile identifier.
    /// </summary>
    public string ProfileId { get; init; } = string.Empty;

    /// <summary>
    /// Human-readable profile name.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Description of the profile.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Target output format.
    /// </summary>
    public MediaFormat OutputFormat { get; init; }

    /// <summary>
    /// Quality preset.
    /// </summary>
    public QualityPreset Quality { get; init; } = QualityPreset.Medium;

    // Video settings
    /// <summary>Video codec to use.</summary>
    public string? VideoCodec { get; init; }
    /// <summary>Video bitrate in kbps.</summary>
    public int? VideoBitrate { get; init; }
    /// <summary>Video width in pixels.</summary>
    public int? Width { get; init; }
    /// <summary>Video height in pixels.</summary>
    public int? Height { get; init; }
    /// <summary>Frame rate (fps).</summary>
    public double? FrameRate { get; init; }
    /// <summary>Whether to maintain aspect ratio when resizing.</summary>
    public bool MaintainAspectRatio { get; init; } = true;

    // Audio settings
    /// <summary>Audio codec to use.</summary>
    public string? AudioCodec { get; init; }
    /// <summary>Audio bitrate in kbps.</summary>
    public int? AudioBitrate { get; init; }
    /// <summary>Audio sample rate in Hz.</summary>
    public int? SampleRate { get; init; }
    /// <summary>Number of audio channels.</summary>
    public int? Channels { get; init; }

    // Image settings
    /// <summary>Image quality (1-100) for lossy formats.</summary>
    public int? ImageQuality { get; init; }
    /// <summary>Whether to strip metadata from images.</summary>
    public bool StripMetadata { get; init; }

    // Processing options
    /// <summary>Whether to use hardware acceleration.</summary>
    public bool UseHardwareAcceleration { get; init; } = true;
    /// <summary>Maximum concurrent jobs for this profile.</summary>
    public int MaxConcurrentJobs { get; init; } = 5;
    /// <summary>Priority level (higher = more important).</summary>
    public int Priority { get; init; } = 5;

    /// <summary>
    /// Custom encoder options.
    /// </summary>
    public IReadOnlyDictionary<string, string> CustomOptions { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Gets a web-optimized H.264 profile.
    /// </summary>
    public static TranscodingProfile WebOptimizedVideo => new()
    {
        ProfileId = "web-h264",
        Name = "Web Optimized (H.264)",
        Description = "H.264 video optimized for web streaming",
        OutputFormat = MediaFormat.Mp4,
        Quality = QualityPreset.Medium,
        VideoCodec = "h264",
        VideoBitrate = 2500,
        Width = 1920,
        Height = 1080,
        FrameRate = 30,
        AudioCodec = "aac",
        AudioBitrate = 128,
        SampleRate = 48000,
        Channels = 2
    };

    /// <summary>
    /// Gets a mobile-optimized profile.
    /// </summary>
    public static TranscodingProfile MobileOptimized => new()
    {
        ProfileId = "mobile",
        Name = "Mobile Optimized",
        Description = "Optimized for mobile devices",
        OutputFormat = MediaFormat.Mp4,
        Quality = QualityPreset.Low,
        VideoCodec = "h264",
        VideoBitrate = 1000,
        Width = 720,
        Height = 480,
        FrameRate = 30,
        AudioCodec = "aac",
        AudioBitrate = 96,
        SampleRate = 44100,
        Channels = 2
    };

    /// <summary>
    /// Gets a thumbnail extraction profile.
    /// </summary>
    public static TranscodingProfile Thumbnail => new()
    {
        ProfileId = "thumbnail",
        Name = "Thumbnail",
        Description = "Extract thumbnail images",
        OutputFormat = MediaFormat.Jpeg,
        Quality = QualityPreset.Medium,
        Width = 320,
        Height = 180,
        ImageQuality = 80,
        StripMetadata = true
    };

    /// <summary>
    /// Gets a WebP optimization profile.
    /// </summary>
    public static TranscodingProfile WebPOptimized => new()
    {
        ProfileId = "webp",
        Name = "WebP Optimized",
        Description = "Convert images to WebP format",
        OutputFormat = MediaFormat.WebP,
        Quality = QualityPreset.High,
        ImageQuality = 85,
        StripMetadata = true
    };
}

/// <summary>
/// Represents a transcoding job.
/// </summary>
public class TranscodingJob
{
    /// <summary>
    /// Unique job identifier.
    /// </summary>
    public string JobId { get; init; } = string.Empty;

    /// <summary>
    /// Source file path or URI.
    /// </summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>
    /// Target output path or URI.
    /// </summary>
    public string TargetPath { get; init; } = string.Empty;

    /// <summary>
    /// Detected source format.
    /// </summary>
    public MediaFormat SourceFormat { get; init; }

    /// <summary>
    /// Target output format.
    /// </summary>
    public MediaFormat TargetFormat { get; init; }

    /// <summary>
    /// Transcoding profile being used.
    /// </summary>
    public TranscodingProfile Profile { get; init; } = new();

    /// <summary>
    /// Current job status.
    /// </summary>
    public TranscodingStatus Status { get; init; }

    /// <summary>
    /// Progress percentage (0-100).
    /// </summary>
    public double Progress { get; init; }

    /// <summary>
    /// Source file size in bytes.
    /// </summary>
    public long SourceSizeBytes { get; init; }

    /// <summary>
    /// Output file size in bytes (populated when complete).
    /// </summary>
    public long? OutputSizeBytes { get; init; }

    /// <summary>
    /// When the job was created.
    /// </summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// Alias for CreatedAt for compatibility.
    /// </summary>
    public DateTimeOffset Timestamp { get => new DateTimeOffset(CreatedAt); init => CreatedAt = value.DateTime; }

    /// <summary>
    /// When processing started.
    /// </summary>
    public DateTime? StartedAt { get; init; }

    /// <summary>
    /// When processing completed.
    /// </summary>
    public DateTime? CompletedAt { get; init; }

    /// <summary>
    /// Estimated time remaining.
    /// </summary>
    public TimeSpan? EstimatedTimeRemaining { get; init; }

    /// <summary>
    /// Current processing speed (e.g., "2.5x" for video).
    /// </summary>
    public string? ProcessingSpeed { get; init; }

    /// <summary>
    /// Error message if failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Warnings generated during processing.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Custom metadata for the job.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Result of a completed transcoding job.
/// </summary>
public class TranscodingResult
{
    /// <summary>
    /// Job identifier.
    /// </summary>
    public string JobId { get; init; } = string.Empty;

    /// <summary>
    /// Whether transcoding succeeded.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Output file path or URI.
    /// </summary>
    public string OutputPath { get; init; } = string.Empty;

    /// <summary>
    /// Output file size in bytes.
    /// </summary>
    public long OutputSizeBytes { get; init; }

    /// <summary>
    /// Total processing duration.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Source file size in bytes.
    /// </summary>
    public long SourceSizeBytes { get; init; }

    /// <summary>
    /// Compression ratio (source/output).
    /// </summary>
    public double CompressionRatio => SourceSizeBytes > 0 ? (double)SourceSizeBytes / OutputSizeBytes : 1.0;

    /// <summary>
    /// Space saved in bytes.
    /// </summary>
    public long SpaceSavedBytes => SourceSizeBytes - OutputSizeBytes;

    /// <summary>
    /// Media duration for video/audio (if applicable).
    /// </summary>
    public TimeSpan? MediaDuration { get; init; }

    /// <summary>
    /// Output video dimensions (if applicable).
    /// </summary>
    public (int Width, int Height)? OutputDimensions { get; init; }

    /// <summary>
    /// Error message if failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Warnings generated during processing.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Media info extracted from the output.
    /// </summary>
    public MediaInfo? OutputMediaInfo { get; init; }
}

/// <summary>
/// Information about a media file.
/// </summary>
public class MediaInfo
{
    /// <summary>
    /// Detected format.
    /// </summary>
    public MediaFormat Format { get; init; }

    /// <summary>
    /// MIME type.
    /// </summary>
    public string MimeType { get; init; } = string.Empty;

    /// <summary>
    /// File size in bytes.
    /// </summary>
    public long SizeBytes { get; init; }

    /// <summary>
    /// Duration for video/audio.
    /// </summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>
    /// Video width in pixels.
    /// </summary>
    public int? Width { get; init; }

    /// <summary>
    /// Video height in pixels.
    /// </summary>
    public int? Height { get; init; }

    /// <summary>
    /// Video frame rate.
    /// </summary>
    public double? FrameRate { get; init; }

    /// <summary>
    /// Video codec.
    /// </summary>
    public string? VideoCodec { get; init; }

    /// <summary>
    /// Video bitrate in kbps.
    /// </summary>
    public int? VideoBitrate { get; init; }

    /// <summary>
    /// Audio codec.
    /// </summary>
    public string? AudioCodec { get; init; }

    /// <summary>
    /// Audio bitrate in kbps.
    /// </summary>
    public int? AudioBitrate { get; init; }

    /// <summary>
    /// Audio sample rate in Hz.
    /// </summary>
    public int? SampleRate { get; init; }

    /// <summary>
    /// Number of audio channels.
    /// </summary>
    public int? Channels { get; init; }

    /// <summary>
    /// Extracted metadata.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Interface for transcoding providers.
/// Supports automatic media format conversion.
/// </summary>
public interface ITranscodingProvider : IPlugin
{
    /// <summary>
    /// Gets supported input formats.
    /// </summary>
    IReadOnlyList<MediaFormat> SupportedInputFormats { get; }

    /// <summary>
    /// Gets supported output formats.
    /// </summary>
    IReadOnlyList<MediaFormat> SupportedOutputFormats { get; }

    /// <summary>
    /// Whether hardware acceleration is available.
    /// </summary>
    bool HardwareAccelerationAvailable { get; }

    /// <summary>
    /// Maximum concurrent transcoding jobs.
    /// </summary>
    int MaxConcurrentJobs { get; }

    /// <summary>
    /// Submits a transcoding job.
    /// </summary>
    /// <param name="sourcePath">Source file path.</param>
    /// <param name="targetPath">Target output path.</param>
    /// <param name="profile">Transcoding profile to use.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Submitted job information.</returns>
    Task<TranscodingJob> SubmitJobAsync(
        string sourcePath,
        string targetPath,
        TranscodingProfile profile,
        CancellationToken ct = default);

    /// <summary>
    /// Gets the status of a job.
    /// </summary>
    /// <param name="jobId">Job identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Job information, or null if not found.</returns>
    Task<TranscodingJob?> GetJobStatusAsync(string jobId, CancellationToken ct = default);

    /// <summary>
    /// Lists all jobs with optional filtering.
    /// </summary>
    /// <param name="status">Filter by status.</param>
    /// <param name="limit">Maximum jobs to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of jobs.</returns>
    Task<IReadOnlyList<TranscodingJob>> ListJobsAsync(
        TranscodingStatus? status = null,
        int limit = 100,
        CancellationToken ct = default);

    /// <summary>
    /// Cancels a running or queued job.
    /// </summary>
    /// <param name="jobId">Job identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if cancelled.</returns>
    Task<bool> CancelJobAsync(string jobId, CancellationToken ct = default);

    /// <summary>
    /// Gets the result of a completed job.
    /// </summary>
    /// <param name="jobId">Job identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Transcoding result.</returns>
    Task<TranscodingResult?> GetResultAsync(string jobId, CancellationToken ct = default);

    /// <summary>
    /// Probes a media file to get information.
    /// </summary>
    /// <param name="path">File path.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Media information.</returns>
    Task<MediaInfo> ProbeAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Detects the format of a media file.
    /// </summary>
    /// <param name="path">File path.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Detected format.</returns>
    Task<MediaFormat> DetectFormatAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Gets or creates a transcoding profile.
    /// </summary>
    /// <param name="profileId">Profile identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Profile, or null if not found.</returns>
    Task<TranscodingProfile?> GetProfileAsync(string profileId, CancellationToken ct = default);

    /// <summary>
    /// Registers a custom transcoding profile.
    /// </summary>
    /// <param name="profile">Profile to register.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Registered profile.</returns>
    Task<TranscodingProfile> RegisterProfileAsync(TranscodingProfile profile, CancellationToken ct = default);

    /// <summary>
    /// Gets transcoding statistics.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Transcoding statistics.</returns>
    Task<TranscodingStatistics> GetStatisticsAsync(CancellationToken ct = default);
}

/// <summary>
/// Transcoding service statistics.
/// </summary>
public class TranscodingStatistics
{
    /// <summary>
    /// Total jobs processed.
    /// </summary>
    public long TotalJobs { get; init; }

    /// <summary>
    /// Successful jobs.
    /// </summary>
    public long SuccessfulJobs { get; init; }

    /// <summary>
    /// Failed jobs.
    /// </summary>
    public long FailedJobs { get; init; }

    /// <summary>
    /// Currently active jobs.
    /// </summary>
    public int ActiveJobs { get; init; }

    /// <summary>
    /// Queued jobs waiting to start.
    /// </summary>
    public int QueuedJobs { get; init; }

    /// <summary>
    /// Total bytes processed.
    /// </summary>
    public long TotalBytesProcessed { get; init; }

    /// <summary>
    /// Total bytes saved through compression.
    /// </summary>
    public long TotalBytesSaved { get; init; }

    /// <summary>
    /// Average processing time per job.
    /// </summary>
    public TimeSpan AverageProcessingTime { get; init; }

    /// <summary>
    /// Last job completion time.
    /// </summary>
    public DateTime? LastJobCompletedAt { get; init; }
}

/// <summary>
/// Abstract base class for media transcoding plugin implementations.
/// Provides automatic media format conversion capabilities.
/// Intelligence-aware: Supports AI-driven quality optimization and format selection.
/// </summary>
public abstract class MediaTranscodingPluginBase : MediaPluginBase, ITranscodingProvider
{
    /// <inheritdoc/>
    public override string MediaType => "MultiMedia";

    private readonly BoundedDictionary<string, TranscodingJob> _jobs = new BoundedDictionary<string, TranscodingJob>(1000);
    private readonly BoundedDictionary<string, TranscodingResult> _results = new BoundedDictionary<string, TranscodingResult>(1000);
    private readonly BoundedDictionary<string, TranscodingProfile> _profiles = new BoundedDictionary<string, TranscodingProfile>(1000);
    private long _totalJobs;
    private long _successfulJobs;
    private long _failedJobs;
    private long _totalBytesProcessed;
    private long _totalBytesSaved;

    /// <summary>
    /// Gets the plugin category. Always DataTransformationProvider for transcoding plugins.
    /// </summary>
    public override PluginCategory Category => PluginCategory.DataTransformationProvider;

    /// <summary>
    /// Gets supported input formats.
    /// Override to specify runtime capabilities.
    /// </summary>
    public virtual IReadOnlyList<MediaFormat> SupportedInputFormats => new[]
    {
        MediaFormat.H264, MediaFormat.H265, MediaFormat.VP8, MediaFormat.VP9,
        MediaFormat.Mp4, MediaFormat.Mkv, MediaFormat.WebM, MediaFormat.Avi, MediaFormat.Mov,
        MediaFormat.Aac, MediaFormat.Mp3, MediaFormat.Opus, MediaFormat.Flac, MediaFormat.Wav,
        MediaFormat.Jpeg, MediaFormat.Png, MediaFormat.WebP, MediaFormat.Gif, MediaFormat.Bmp
    };

    /// <summary>
    /// Gets supported output formats.
    /// Override to specify runtime capabilities.
    /// </summary>
    public virtual IReadOnlyList<MediaFormat> SupportedOutputFormats => new[]
    {
        MediaFormat.H264, MediaFormat.H265, MediaFormat.VP9, MediaFormat.AV1,
        MediaFormat.Mp4, MediaFormat.WebM, MediaFormat.Mkv,
        MediaFormat.Aac, MediaFormat.Mp3, MediaFormat.Opus,
        MediaFormat.Jpeg, MediaFormat.Png, MediaFormat.WebP, MediaFormat.Avif
    };

    /// <summary>
    /// Whether hardware acceleration is available.
    /// Override to detect actual hardware capabilities.
    /// </summary>
    public virtual bool HardwareAccelerationAvailable => false;

    /// <summary>
    /// Maximum concurrent transcoding jobs.
    /// Default is 4.
    /// </summary>
    public virtual int MaxConcurrentJobs => 4;

    #region Intelligence Integration

    /// <summary>
    /// Capabilities declared by this transcoding provider.
    /// </summary>
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
    {
        new RegisteredCapability
        {
            CapabilityId = $"{Id}.transcoding",
            DisplayName = $"{Name} - Media Transcoding",
            Description = "Automatic media format conversion with quality optimization",
            Category = CapabilityCategory.Pipeline,
            SubCategory = "MediaTranscoding",
            PluginId = Id,
            PluginName = Name,
            PluginVersion = Version,
            Tags = new[] { "media", "transcoding", "video", "audio", "image" },
            SemanticDescription = "Use this for converting media files between formats with quality optimization",
            Metadata = new Dictionary<string, object>
            {
                ["supportedInputFormats"] = SupportedInputFormats.Select(f => f.ToString()).ToArray(),
                ["supportedOutputFormats"] = SupportedOutputFormats.Select(f => f.ToString()).ToArray(),
                ["hardwareAcceleration"] = HardwareAccelerationAvailable,
                ["maxConcurrentJobs"] = MaxConcurrentJobs
            }
        }
    };

    /// <summary>
    /// Gets static knowledge for Intelligence registration.
    /// </summary>
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        var knowledge = new List<KnowledgeObject>(base.GetStaticKnowledge());

        knowledge.Add(new KnowledgeObject
        {
            Id = $"{Id}.transcoding.capability",
            Topic = "media-transcoding",
            SourcePluginId = Id,
            SourcePluginName = Name,
            KnowledgeType = "capability",
            Description = $"Media transcoding with {(HardwareAccelerationAvailable ? "hardware" : "software")} acceleration",
            Payload = new Dictionary<string, object>
            {
                ["supportedInputFormats"] = SupportedInputFormats.Select(f => f.ToString()).ToArray(),
                ["supportedOutputFormats"] = SupportedOutputFormats.Select(f => f.ToString()).ToArray(),
                ["hardwareAcceleration"] = HardwareAccelerationAvailable,
                ["maxConcurrentJobs"] = MaxConcurrentJobs
            },
            Tags = new[] { "media", "transcoding" },
            Confidence = 1.0f,
            Timestamp = DateTimeOffset.UtcNow
        });

        return knowledge;
    }

    /// <summary>
    /// Requests AI-driven optimal format recommendation based on target use case.
    /// </summary>
    /// <param name="sourceInfo">Source media information.</param>
    /// <param name="targetUseCase">Target use case (web, mobile, archive, etc.).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Recommended format and profile.</returns>
    protected async Task<(MediaFormat Format, TranscodingProfile Profile)?> RequestOptimalFormatAsync(
        MediaInfo sourceInfo,
        string targetUseCase,
        CancellationToken ct = default)
    {
        if (MessageBus == null) return null;

        try
        {
            var request = new PluginMessage
            {
                Type = "intelligence.recommend.request",
                CorrelationId = Guid.NewGuid().ToString("N"),
                Source = Id,
                Payload = new Dictionary<string, object>
                {
                    ["recommendationType"] = "media_format",
                    ["sourceFormat"] = sourceInfo.Format.ToString(),
                    ["sourceWidth"] = sourceInfo.Width ?? 0,
                    ["sourceHeight"] = sourceInfo.Height ?? 0,
                    ["targetUseCase"] = targetUseCase
                }
            };

            await MessageBus.PublishAsync("intelligence.recommend", request, ct);
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Requests AI-driven quality settings optimization.
    /// </summary>
    /// <param name="profile">Current profile.</param>
    /// <param name="sourceInfo">Source media information.</param>
    /// <param name="targetQuality">Target perceptual quality (0.0-1.0).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Optimized profile settings.</returns>
    protected async Task<TranscodingProfile?> RequestQualityOptimizationAsync(
        TranscodingProfile profile,
        MediaInfo sourceInfo,
        double targetQuality,
        CancellationToken ct = default)
    {
        if (MessageBus == null) return null;

        try
        {
            var request = new PluginMessage
            {
                Type = "intelligence.optimize.request",
                CorrelationId = Guid.NewGuid().ToString("N"),
                Source = Id,
                Payload = new Dictionary<string, object>
                {
                    ["optimizationType"] = "media_quality",
                    ["currentProfile"] = profile.ProfileId,
                    ["targetQuality"] = targetQuality,
                    ["sourceFormat"] = sourceInfo.Format.ToString()
                }
            };

            await MessageBus.PublishAsync("intelligence.optimize", request, ct);
            return null;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    /// <summary>
    /// Constructor - registers default profiles.
    /// </summary>
    protected MediaTranscodingPluginBase()
    {
        // Register default profiles
        _profiles["web-h264"] = TranscodingProfile.WebOptimizedVideo;
        _profiles["mobile"] = TranscodingProfile.MobileOptimized;
        _profiles["thumbnail"] = TranscodingProfile.Thumbnail;
        _profiles["webp"] = TranscodingProfile.WebPOptimized;
    }

    /// <summary>
    /// Submits a transcoding job.
    /// </summary>
    public virtual async Task<TranscodingJob> SubmitJobAsync(
        string sourcePath,
        string targetPath,
        TranscodingProfile profile,
        CancellationToken ct = default)
    {
        // Detect source format
        var sourceFormat = await DetectFormatAsync(sourcePath, ct);

        if (!SupportedInputFormats.Contains(sourceFormat))
        {
            throw new NotSupportedException($"Input format {sourceFormat} is not supported");
        }

        if (!SupportedOutputFormats.Contains(profile.OutputFormat))
        {
            throw new NotSupportedException($"Output format {profile.OutputFormat} is not supported");
        }

        var jobId = $"transcode-{Guid.NewGuid():N}";
        var sourceInfo = await ProbeAsync(sourcePath, ct);

        var job = new TranscodingJob
        {
            JobId = jobId,
            SourcePath = sourcePath,
            TargetPath = targetPath,
            SourceFormat = sourceFormat,
            TargetFormat = profile.OutputFormat,
            Profile = profile,
            Status = TranscodingStatus.Queued,
            Progress = 0,
            SourceSizeBytes = sourceInfo.SizeBytes,
            Timestamp = DateTimeOffset.UtcNow
        };

        _jobs[jobId] = job;
        Interlocked.Increment(ref _totalJobs);

        // Start processing (implementation-specific)
        _ = ProcessJobAsync(job, ct);

        return job;
    }

    /// <summary>
    /// Gets the status of a job.
    /// </summary>
    public virtual Task<TranscodingJob?> GetJobStatusAsync(string jobId, CancellationToken ct = default)
    {
        _jobs.TryGetValue(jobId, out var job);
        return Task.FromResult<TranscodingJob?>(job);
    }

    /// <summary>
    /// Lists all jobs with optional filtering.
    /// </summary>
    public virtual Task<IReadOnlyList<TranscodingJob>> ListJobsAsync(
        TranscodingStatus? status = null,
        int limit = 100,
        CancellationToken ct = default)
    {
        IEnumerable<TranscodingJob> jobs = _jobs.Values;

        if (status.HasValue)
        {
            jobs = jobs.Where(j => j.Status == status.Value);
        }

        return Task.FromResult<IReadOnlyList<TranscodingJob>>(
            jobs.OrderByDescending(j => j.CreatedAt).Take(limit).ToList());
    }

    /// <summary>
    /// Cancels a running or queued job.
    /// </summary>
    public virtual async Task<bool> CancelJobAsync(string jobId, CancellationToken ct = default)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            if (job.Status == TranscodingStatus.Queued || job.Status == TranscodingStatus.InProgress)
            {
                await CancelProcessingAsync(jobId, ct);

                _jobs[jobId] = new TranscodingJob
                {
                    JobId = job.JobId,
                    SourcePath = job.SourcePath,
                    TargetPath = job.TargetPath,
                    SourceFormat = job.SourceFormat,
                    TargetFormat = job.TargetFormat,
                    Profile = job.Profile,
                    Status = TranscodingStatus.Cancelled,
                    Progress = job.Progress,
                    SourceSizeBytes = job.SourceSizeBytes,
                    CreatedAt = job.CreatedAt,
                    StartedAt = job.StartedAt,
                    CompletedAt = DateTime.UtcNow
                };

                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Gets the result of a completed job.
    /// </summary>
    public virtual Task<TranscodingResult?> GetResultAsync(string jobId, CancellationToken ct = default)
    {
        _results.TryGetValue(jobId, out var result);
        return Task.FromResult<TranscodingResult?>(result);
    }

    /// <summary>
    /// Probes a media file to get information.
    /// Must be implemented by derived classes.
    /// </summary>
    public abstract Task<MediaInfo> ProbeAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Detects the format of a media file.
    /// Override for more sophisticated detection.
    /// </summary>
    public virtual Task<MediaFormat> DetectFormatAsync(string path, CancellationToken ct = default)
    {
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        var format = extension switch
        {
            ".mp4" or ".m4v" => MediaFormat.Mp4,
            ".mkv" => MediaFormat.Mkv,
            ".webm" => MediaFormat.WebM,
            ".avi" => MediaFormat.Avi,
            ".mov" => MediaFormat.Mov,
            ".mp3" => MediaFormat.Mp3,
            ".aac" or ".m4a" => MediaFormat.Aac,
            ".wav" => MediaFormat.Wav,
            ".flac" => MediaFormat.Flac,
            ".ogg" or ".oga" => MediaFormat.Ogg,
            ".opus" => MediaFormat.Opus,
            ".jpg" or ".jpeg" => MediaFormat.Jpeg,
            ".png" => MediaFormat.Png,
            ".webp" => MediaFormat.WebP,
            ".gif" => MediaFormat.Gif,
            ".bmp" => MediaFormat.Bmp,
            ".tiff" or ".tif" => MediaFormat.Tiff,
            ".avif" => MediaFormat.Avif,
            ".heif" or ".heic" => MediaFormat.Heif,
            ".pdf" => MediaFormat.Pdf,
            ".docx" => MediaFormat.Docx,
            ".xlsx" => MediaFormat.Xlsx,
            ".pptx" => MediaFormat.Pptx,
            ".txt" => MediaFormat.Txt,
            ".html" or ".htm" => MediaFormat.Html,
            ".md" => MediaFormat.Markdown,
            _ => MediaFormat.Unknown
        };

        return Task.FromResult(format);
    }

    /// <summary>
    /// Gets a transcoding profile.
    /// </summary>
    public virtual Task<TranscodingProfile?> GetProfileAsync(string profileId, CancellationToken ct = default)
    {
        _profiles.TryGetValue(profileId, out var profile);
        return Task.FromResult<TranscodingProfile?>(profile);
    }

    /// <summary>
    /// Registers a custom transcoding profile.
    /// </summary>
    public virtual Task<TranscodingProfile> RegisterProfileAsync(TranscodingProfile profile, CancellationToken ct = default)
    {
        var profileId = string.IsNullOrEmpty(profile.ProfileId)
            ? $"custom-{Guid.NewGuid():N}"
            : profile.ProfileId;

        var registeredProfile = new TranscodingProfile
        {
            ProfileId = profileId,
            Name = profile.Name,
            Description = profile.Description,
            OutputFormat = profile.OutputFormat,
            Quality = profile.Quality,
            VideoCodec = profile.VideoCodec,
            VideoBitrate = profile.VideoBitrate,
            Width = profile.Width,
            Height = profile.Height,
            FrameRate = profile.FrameRate,
            MaintainAspectRatio = profile.MaintainAspectRatio,
            AudioCodec = profile.AudioCodec,
            AudioBitrate = profile.AudioBitrate,
            SampleRate = profile.SampleRate,
            Channels = profile.Channels,
            ImageQuality = profile.ImageQuality,
            StripMetadata = profile.StripMetadata,
            UseHardwareAcceleration = profile.UseHardwareAcceleration,
            MaxConcurrentJobs = profile.MaxConcurrentJobs,
            Priority = profile.Priority,
            CustomOptions = profile.CustomOptions
        };

        _profiles[profileId] = registeredProfile;
        return Task.FromResult(registeredProfile);
    }

    /// <summary>
    /// Gets transcoding statistics.
    /// </summary>
    public virtual Task<TranscodingStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        var activeJobs = _jobs.Values.Count(j => j.Status == TranscodingStatus.InProgress);
        var queuedJobs = _jobs.Values.Count(j => j.Status == TranscodingStatus.Queued);

        return Task.FromResult(new TranscodingStatistics
        {
            TotalJobs = Interlocked.Read(ref _totalJobs),
            SuccessfulJobs = Interlocked.Read(ref _successfulJobs),
            FailedJobs = Interlocked.Read(ref _failedJobs),
            ActiveJobs = activeJobs,
            QueuedJobs = queuedJobs,
            TotalBytesProcessed = Interlocked.Read(ref _totalBytesProcessed),
            TotalBytesSaved = Interlocked.Read(ref _totalBytesSaved),
            LastJobCompletedAt = _results.Values
                .OrderByDescending(r => r.Duration)
                .FirstOrDefault()
                ?.Duration != default
                    ? DateTime.UtcNow
                    : null
        });
    }

    /// <summary>
    /// Processes a transcoding job. Must be implemented by derived classes.
    /// </summary>
    protected abstract Task ProcessJobAsync(TranscodingJob job, CancellationToken ct);

    /// <summary>
    /// Cancels an in-progress job. Must be implemented by derived classes.
    /// </summary>
    protected abstract Task CancelProcessingAsync(string jobId, CancellationToken ct);

    /// <summary>
    /// Updates job status during processing.
    /// </summary>
    protected void UpdateJobStatus(string jobId, TranscodingStatus status, double progress,
        string? error = null, long? outputSize = null)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            _jobs[jobId] = new TranscodingJob
            {
                JobId = job.JobId,
                SourcePath = job.SourcePath,
                TargetPath = job.TargetPath,
                SourceFormat = job.SourceFormat,
                TargetFormat = job.TargetFormat,
                Profile = job.Profile,
                Status = status,
                Progress = progress,
                SourceSizeBytes = job.SourceSizeBytes,
                OutputSizeBytes = outputSize,
                CreatedAt = job.CreatedAt,
                StartedAt = job.StartedAt ?? (status == TranscodingStatus.InProgress ? DateTime.UtcNow : null),
                CompletedAt = status is TranscodingStatus.Completed or TranscodingStatus.Failed or TranscodingStatus.Cancelled
                    ? DateTime.UtcNow
                    : null,
                ErrorMessage = error
            };
        }
    }

    /// <summary>
    /// Records job completion.
    /// </summary>
    protected void RecordCompletion(string jobId, TranscodingResult result)
    {
        _results[jobId] = result;

        if (result.Success)
        {
            Interlocked.Increment(ref _successfulJobs);
            Interlocked.Add(ref _totalBytesProcessed, result.SourceSizeBytes);
            Interlocked.Add(ref _totalBytesSaved, result.SpaceSavedBytes);
        }
        else
        {
            Interlocked.Increment(ref _failedJobs);
        }
    }

    /// <summary>
    /// Starts the transcoding service.
    /// </summary>
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Stops the transcoding service.
    /// </summary>
    public override Task StopAsync() => Task.CompletedTask;

    /// <summary>
    /// Gets plugin metadata.
    /// </summary>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "MediaTranscoding";
        metadata["SupportedInputFormats"] = SupportedInputFormats.Select(f => f.ToString()).ToArray();
        metadata["SupportedOutputFormats"] = SupportedOutputFormats.Select(f => f.ToString()).ToArray();
        metadata["HardwareAcceleration"] = HardwareAccelerationAvailable;
        metadata["MaxConcurrentJobs"] = MaxConcurrentJobs;
        metadata["SupportsAutoTranscoding"] = true;
        return metadata;
    }
}

#endregion
