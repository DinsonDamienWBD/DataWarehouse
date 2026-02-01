using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.Plugins.Compute.Wasm;

#region Function Versioning

/// <summary>
/// Represents a specific version of a deployed function.
/// Used for version history and rollback support.
/// </summary>
public class FunctionVersion
{
    /// <summary>
    /// Unique identifier for this version.
    /// </summary>
    public string VersionId { get; init; } = string.Empty;

    /// <summary>
    /// ID of the function this version belongs to.
    /// </summary>
    public string FunctionId { get; init; } = string.Empty;

    /// <summary>
    /// When this version was deployed.
    /// </summary>
    public DateTime DeployedAt { get; init; }

    /// <summary>
    /// SHA-256 hash of the WASM module bytes.
    /// </summary>
    public string ModuleHash { get; init; } = string.Empty;

    /// <summary>
    /// Size of the module in bytes.
    /// </summary>
    public long SizeBytes { get; init; }

    /// <summary>
    /// ID of the previous version, if any.
    /// </summary>
    public string? PreviousVersionId { get; init; }
}

#endregion

#region Function Chaining

/// <summary>
/// Represents a chain of functions that execute in sequence.
/// Output of each function becomes input to the next.
/// </summary>
public class WasmFunctionChain
{
    /// <summary>
    /// Unique identifier for the chain.
    /// </summary>
    public string ChainId { get; init; } = string.Empty;

    /// <summary>
    /// Ordered list of function IDs in the chain.
    /// </summary>
    public IReadOnlyList<string> FunctionIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// When the chain was created.
    /// </summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// When the chain was last executed.
    /// </summary>
    public DateTime? LastExecuted { get; set; }

    /// <summary>
    /// Total number of times the chain has been executed.
    /// </summary>
    public long ExecutionCount { get; set; }
}

/// <summary>
/// Result of executing a function chain.
/// </summary>
public class ChainExecutionResult
{
    /// <summary>
    /// ID of the executed chain.
    /// </summary>
    public string ChainId { get; init; } = string.Empty;

    /// <summary>
    /// Whether all functions in the chain executed successfully.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Error message if the chain failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Output data from the last function in the chain.
    /// </summary>
    public byte[] OutputData { get; init; } = Array.Empty<byte>();

    /// <summary>
    /// Results from each step in the chain.
    /// </summary>
    public IReadOnlyList<ChainStepResult> StepResults { get; init; } = Array.Empty<ChainStepResult>();

    /// <summary>
    /// Total execution time for the entire chain.
    /// </summary>
    public TimeSpan TotalDuration { get; init; }
}

/// <summary>
/// Result of a single step in a function chain.
/// </summary>
public class ChainStepResult
{
    /// <summary>
    /// ID of the function executed in this step.
    /// </summary>
    public string FunctionId { get; init; } = string.Empty;

    /// <summary>
    /// Execution status for this step.
    /// </summary>
    public WasmExecutionStatus Status { get; init; }

    /// <summary>
    /// Duration of this step.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Error message if this step failed.
    /// </summary>
    public string? ErrorMessage { get; init; }
}

#endregion

#region Scheduling

/// <summary>
/// Represents a function scheduled for periodic execution.
/// </summary>
public class ScheduledFunction
{
    /// <summary>
    /// ID of the function to execute.
    /// </summary>
    public string FunctionId { get; init; } = string.Empty;

    /// <summary>
    /// Cron expression defining the schedule.
    /// </summary>
    public string CronExpression { get; init; } = string.Empty;

    /// <summary>
    /// When the function was last executed.
    /// </summary>
    public DateTime? LastExecuted { get; set; }

    /// <summary>
    /// Calculated next execution time.
    /// </summary>
    public DateTime NextExecution { get; set; }

    /// <summary>
    /// Whether the schedule is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Represents a subscription to events for triggering functions.
/// </summary>
public class EventSubscription
{
    /// <summary>
    /// Unique subscription identifier.
    /// </summary>
    public string SubscriptionId { get; init; } = string.Empty;

    /// <summary>
    /// ID of the function to trigger.
    /// </summary>
    public string FunctionId { get; init; } = string.Empty;

    /// <summary>
    /// Event pattern to match (supports wildcards).
    /// </summary>
    public string EventPattern { get; init; } = string.Empty;

    /// <summary>
    /// When the subscription was created.
    /// </summary>
    public DateTime CreatedAt { get; init; }
}

#endregion

#region Execution Queue

/// <summary>
/// Execution priority levels.
/// </summary>
public enum ExecutionPriority
{
    /// <summary>
    /// Low priority - executed after normal and high priority.
    /// </summary>
    Low = 0,

    /// <summary>
    /// Normal priority - default for most executions.
    /// </summary>
    Normal = 1,

    /// <summary>
    /// High priority - executed before normal and low priority.
    /// </summary>
    High = 2,

    /// <summary>
    /// Critical priority - executed immediately.
    /// </summary>
    Critical = 3
}

/// <summary>
/// Represents a queued execution with priority.
/// </summary>
public class PrioritizedExecution
{
    /// <summary>
    /// ID of the function to execute.
    /// </summary>
    public string FunctionId { get; init; } = string.Empty;

    /// <summary>
    /// Execution priority.
    /// </summary>
    public ExecutionPriority Priority { get; init; }

    /// <summary>
    /// Execution context.
    /// </summary>
    public WasmExecutionContext Context { get; init; } = new();

    /// <summary>
    /// When the execution was queued.
    /// </summary>
    public DateTime QueuedAt { get; init; } = DateTime.UtcNow;
}

#endregion

#region Metrics

/// <summary>
/// Detailed execution metrics for a function.
/// </summary>
public class ExecutionMetrics
{
    /// <summary>
    /// ID of the function.
    /// </summary>
    public string FunctionId { get; init; } = string.Empty;

    /// <summary>
    /// Total number of executions.
    /// </summary>
    public long TotalExecutions { get; init; }

    /// <summary>
    /// Number of successful executions.
    /// </summary>
    public long SuccessfulExecutions { get; init; }

    /// <summary>
    /// Number of failed executions.
    /// </summary>
    public long FailedExecutions { get; init; }

    /// <summary>
    /// Number of executions that timed out.
    /// </summary>
    public long TimeoutExecutions { get; init; }

    /// <summary>
    /// Last execution timestamp.
    /// </summary>
    public DateTime? LastExecutionTime { get; init; }

    /// <summary>
    /// Average execution time in milliseconds.
    /// </summary>
    public double AverageExecutionTimeMs { get; init; }

    /// <summary>
    /// Minimum execution time in milliseconds.
    /// </summary>
    public double MinExecutionTimeMs { get; init; }

    /// <summary>
    /// Maximum execution time in milliseconds.
    /// </summary>
    public double MaxExecutionTimeMs { get; init; }

    /// <summary>
    /// Peak memory usage in bytes.
    /// </summary>
    public long PeakMemoryBytes { get; init; }

    /// <summary>
    /// Total WASM instructions executed across all invocations.
    /// </summary>
    public long TotalInstructionsExecuted { get; init; }

    /// <summary>
    /// Success rate as a percentage (0-100).
    /// </summary>
    public double SuccessRate => TotalExecutions > 0
        ? (double)SuccessfulExecutions / TotalExecutions * 100
        : 0;
}

/// <summary>
/// Aggregate metrics across all functions.
/// </summary>
public class AggregateMetrics
{
    /// <summary>
    /// Total number of deployed functions.
    /// </summary>
    public int TotalFunctions { get; init; }

    /// <summary>
    /// Total executions across all functions.
    /// </summary>
    public long TotalExecutions { get; init; }

    /// <summary>
    /// Total successful executions.
    /// </summary>
    public long TotalSuccessfulExecutions { get; init; }

    /// <summary>
    /// Total failed executions.
    /// </summary>
    public long TotalFailedExecutions { get; init; }

    /// <summary>
    /// Total timed out executions.
    /// </summary>
    public long TotalTimeoutExecutions { get; init; }

    /// <summary>
    /// Average execution time across all functions in milliseconds.
    /// </summary>
    public double AverageExecutionTimeMs { get; init; }

    /// <summary>
    /// Total memory used by all functions in bytes.
    /// </summary>
    public long TotalMemoryBytesUsed { get; init; }

    /// <summary>
    /// Total instructions executed across all functions.
    /// </summary>
    public long TotalInstructionsExecuted { get; init; }

    /// <summary>
    /// Number of currently active executions.
    /// </summary>
    public int ActiveExecutions { get; init; }

    /// <summary>
    /// Overall success rate as a percentage.
    /// </summary>
    public double OverallSuccessRate => TotalExecutions > 0
        ? (double)TotalSuccessfulExecutions / TotalExecutions * 100
        : 0;
}

/// <summary>
/// Log entry for a function execution.
/// </summary>
public class ExecutionLogEntry
{
    /// <summary>
    /// Execution ID.
    /// </summary>
    public string ExecutionId { get; init; } = string.Empty;

    /// <summary>
    /// Function ID.
    /// </summary>
    public string FunctionId { get; init; } = string.Empty;

    /// <summary>
    /// Trigger type that initiated the execution.
    /// </summary>
    public FunctionTrigger Trigger { get; init; }

    /// <summary>
    /// Path that triggered the execution (for OnWrite/OnRead).
    /// </summary>
    public string? TriggerPath { get; init; }

    /// <summary>
    /// Event that triggered the execution (for OnEvent).
    /// </summary>
    public string? TriggerEvent { get; init; }

    /// <summary>
    /// Timestamp of the execution.
    /// </summary>
    public DateTime Timestamp { get; init; }

    /// <summary>
    /// Duration of the execution in milliseconds.
    /// </summary>
    public double DurationMs { get; init; }

    /// <summary>
    /// Whether the execution was successful.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Error message if the execution failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Size of the input data in bytes.
    /// </summary>
    public long InputSizeBytes { get; init; }

    /// <summary>
    /// User or service that initiated the execution.
    /// </summary>
    public string? InitiatedBy { get; init; }

    /// <summary>
    /// Correlation ID for tracing.
    /// </summary>
    public string? CorrelationId { get; init; }
}

#endregion

#region Templates

/// <summary>
/// Category of function templates.
/// </summary>
public enum TemplateCategory
{
    /// <summary>
    /// Data transformation operations.
    /// </summary>
    DataTransformation,

    /// <summary>
    /// Data validation functions.
    /// </summary>
    Validation,

    /// <summary>
    /// Media processing (images, video, audio).
    /// </summary>
    MediaProcessing,

    /// <summary>
    /// Security-related functions.
    /// </summary>
    Security,

    /// <summary>
    /// Data integration and ETL.
    /// </summary>
    DataIntegration,

    /// <summary>
    /// Analytics and aggregation.
    /// </summary>
    Analytics,

    /// <summary>
    /// External integrations.
    /// </summary>
    Integration,

    /// <summary>
    /// Utility functions.
    /// </summary>
    Utility
}

/// <summary>
/// Pre-built function template.
/// </summary>
public class FunctionTemplate
{
    /// <summary>
    /// Unique template identifier.
    /// </summary>
    public string TemplateId { get; init; } = string.Empty;

    /// <summary>
    /// Human-readable template name.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Description of what the template does.
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Template category.
    /// </summary>
    public TemplateCategory Category { get; init; }

    /// <summary>
    /// Source language the template is written in.
    /// </summary>
    public string SourceLanguage { get; init; } = string.Empty;

    /// <summary>
    /// Required input parameters for the template.
    /// </summary>
    public IReadOnlyList<string> RequiredInputs { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Default resource limits for the template.
    /// </summary>
    public WasmResourceLimits DefaultResourceLimits { get; init; } = WasmResourceLimits.Default;

    /// <summary>
    /// WASM bytes of the pre-compiled template (if available).
    /// </summary>
    public byte[]? CompiledWasm { get; init; }
}

#endregion

#region Multi-language Support

/// <summary>
/// Information about a supported source language.
/// </summary>
public class LanguageInfo
{
    /// <summary>
    /// Name of the language.
    /// </summary>
    public string Language { get; init; } = string.Empty;

    /// <summary>
    /// Minimum version required.
    /// </summary>
    public string LanguageVersion { get; init; } = string.Empty;

    /// <summary>
    /// Required toolchain or compiler.
    /// </summary>
    public string Toolchain { get; init; } = string.Empty;

    /// <summary>
    /// Command to compile to WASM.
    /// </summary>
    public string CompilerCommand { get; init; } = string.Empty;

    /// <summary>
    /// Additional notes about the language support.
    /// </summary>
    public string Notes { get; init; } = string.Empty;
}

#endregion
