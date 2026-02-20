using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateServerless;

#region Enums and Core Types

/// <summary>
/// Serverless platform provider.
/// </summary>
public enum ServerlessPlatform
{
    /// <summary>AWS Lambda.</summary>
    AwsLambda,
    /// <summary>Azure Functions.</summary>
    AzureFunctions,
    /// <summary>Google Cloud Functions.</summary>
    GoogleCloudFunctions,
    /// <summary>Google Cloud Run.</summary>
    GoogleCloudRun,
    /// <summary>Cloudflare Workers.</summary>
    CloudflareWorkers,
    /// <summary>Vercel Functions.</summary>
    VercelFunctions,
    /// <summary>Netlify Functions.</summary>
    NetlifyFunctions,
    /// <summary>AWS App Runner.</summary>
    AwsAppRunner,
    /// <summary>Azure Container Apps.</summary>
    AzureContainerApps,
    /// <summary>Alibaba Function Compute.</summary>
    AlibabaFunctionCompute,
    /// <summary>IBM Cloud Functions.</summary>
    IbmCloudFunctions,
    /// <summary>Oracle Functions.</summary>
    OracleFunctions,
    /// <summary>OpenFaaS.</summary>
    OpenFaaS,
    /// <summary>Knative.</summary>
    Knative,
    /// <summary>Fission.</summary>
    Fission,
    /// <summary>Nuclio.</summary>
    Nuclio,
    /// <summary>Custom platform.</summary>
    Custom
}

/// <summary>
/// Serverless strategy category.
/// </summary>
public enum ServerlessCategory
{
    /// <summary>Function-as-a-Service integration.</summary>
    FaaS,
    /// <summary>Event-driven triggers.</summary>
    EventTriggers,
    /// <summary>State management.</summary>
    StateManagement,
    /// <summary>Cold start optimization.</summary>
    ColdStartOptimization,
    /// <summary>Serverless scaling.</summary>
    Scaling,
    /// <summary>Security.</summary>
    Security,
    /// <summary>Monitoring and observability.</summary>
    Monitoring,
    /// <summary>Cost tracking and optimization.</summary>
    CostTracking
}

/// <summary>
/// Serverless function runtime.
/// </summary>
public enum ServerlessRuntime
{
    /// <summary>.NET 6/7/8.</summary>
    DotNet,
    /// <summary>Node.js.</summary>
    NodeJs,
    /// <summary>Python.</summary>
    Python,
    /// <summary>Go.</summary>
    Go,
    /// <summary>Java.</summary>
    Java,
    /// <summary>Ruby.</summary>
    Ruby,
    /// <summary>Rust.</summary>
    Rust,
    /// <summary>Custom runtime.</summary>
    Custom,
    /// <summary>Container-based.</summary>
    Container
}

/// <summary>
/// Function execution status.
/// </summary>
public enum ExecutionStatus
{
    /// <summary>Pending execution.</summary>
    Pending,
    /// <summary>Currently running.</summary>
    Running,
    /// <summary>Completed successfully.</summary>
    Succeeded,
    /// <summary>Failed with error.</summary>
    Failed,
    /// <summary>Timed out.</summary>
    TimedOut,
    /// <summary>Throttled.</summary>
    Throttled,
    /// <summary>Cold start in progress.</summary>
    ColdStarting
}

#endregion

#region Configuration Types

/// <summary>
/// Serverless function configuration.
/// </summary>
public sealed record ServerlessFunctionConfig
{
    /// <summary>Function unique identifier.</summary>
    public required string FunctionId { get; init; }
    /// <summary>Function display name.</summary>
    public required string Name { get; init; }
    /// <summary>Function description.</summary>
    public string? Description { get; init; }
    /// <summary>Target platform.</summary>
    public required ServerlessPlatform Platform { get; init; }
    /// <summary>Runtime environment.</summary>
    public required ServerlessRuntime Runtime { get; init; }
    /// <summary>Handler entry point.</summary>
    public required string Handler { get; init; }
    /// <summary>Memory allocation in MB.</summary>
    public int MemoryMb { get; init; } = 256;
    /// <summary>Timeout in seconds.</summary>
    public int TimeoutSeconds { get; init; } = 30;
    /// <summary>Environment variables.</summary>
    public Dictionary<string, string> EnvironmentVariables { get; init; } = new();
    /// <summary>Reserved concurrency (0 = unreserved).</summary>
    public int ReservedConcurrency { get; init; }
    /// <summary>Provisioned concurrency for cold start reduction.</summary>
    public int ProvisionedConcurrency { get; init; }
    /// <summary>VPC configuration.</summary>
    public VpcConfig? VpcConfig { get; init; }
    /// <summary>Tracing enabled.</summary>
    public bool EnableTracing { get; init; } = true;
    /// <summary>Dead letter queue ARN/URL.</summary>
    public string? DeadLetterQueue { get; init; }
    /// <summary>Tags/labels.</summary>
    public Dictionary<string, string> Tags { get; init; } = new();
}

/// <summary>
/// VPC configuration for serverless functions.
/// </summary>
public sealed record VpcConfig
{
    /// <summary>VPC ID.</summary>
    public required string VpcId { get; init; }
    /// <summary>Subnet IDs.</summary>
    public required IReadOnlyList<string> SubnetIds { get; init; }
    /// <summary>Security group IDs.</summary>
    public required IReadOnlyList<string> SecurityGroupIds { get; init; }
}

/// <summary>
/// Function invocation request.
/// </summary>
public sealed record InvocationRequest
{
    /// <summary>Function identifier.</summary>
    public required string FunctionId { get; init; }
    /// <summary>Invocation payload.</summary>
    public object? Payload { get; init; }
    /// <summary>Invocation type (sync/async).</summary>
    public InvocationType InvocationType { get; init; } = InvocationType.RequestResponse;
    /// <summary>Client context.</summary>
    public Dictionary<string, string>? ClientContext { get; init; }
    /// <summary>Log type (None, Tail).</summary>
    public string LogType { get; init; } = "None";
    /// <summary>Qualifier (version/alias).</summary>
    public string? Qualifier { get; init; }
}

/// <summary>
/// Invocation type.
/// </summary>
public enum InvocationType
{
    /// <summary>Synchronous request-response.</summary>
    RequestResponse,
    /// <summary>Asynchronous event.</summary>
    Event,
    /// <summary>Dry run for validation.</summary>
    DryRun
}

/// <summary>
/// Function invocation result.
/// </summary>
public sealed record InvocationResult
{
    /// <summary>Request ID.</summary>
    public required string RequestId { get; init; }
    /// <summary>Execution status.</summary>
    public required ExecutionStatus Status { get; init; }
    /// <summary>Response payload.</summary>
    public object? Payload { get; init; }
    /// <summary>Error message if failed.</summary>
    public string? Error { get; init; }
    /// <summary>Error type if failed.</summary>
    public string? ErrorType { get; init; }
    /// <summary>Duration in milliseconds.</summary>
    public double DurationMs { get; init; }
    /// <summary>Billed duration in milliseconds.</summary>
    public double BilledDurationMs { get; init; }
    /// <summary>Memory used in MB.</summary>
    public int MemoryUsedMb { get; init; }
    /// <summary>Was this a cold start?</summary>
    public bool WasColdStart { get; init; }
    /// <summary>Init duration for cold starts.</summary>
    public double? InitDurationMs { get; init; }
    /// <summary>Log output (if requested).</summary>
    public string? LogOutput { get; init; }
    /// <summary>Timestamp.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

#endregion

#region Strategy Capabilities

/// <summary>
/// Capabilities of a serverless strategy.
/// </summary>
public sealed record ServerlessStrategyCapabilities
{
    /// <summary>Supports synchronous invocation.</summary>
    public bool SupportsSyncInvocation { get; init; } = true;
    /// <summary>Supports asynchronous invocation.</summary>
    public bool SupportsAsyncInvocation { get; init; } = true;
    /// <summary>Supports event triggers.</summary>
    public bool SupportsEventTriggers { get; init; }
    /// <summary>Supports provisioned concurrency.</summary>
    public bool SupportsProvisionedConcurrency { get; init; }
    /// <summary>Supports VPC integration.</summary>
    public bool SupportsVpc { get; init; }
    /// <summary>Supports container images.</summary>
    public bool SupportsContainerImages { get; init; }
    /// <summary>Maximum memory in MB.</summary>
    public int MaxMemoryMb { get; init; } = 10240;
    /// <summary>Maximum timeout in seconds.</summary>
    public int MaxTimeoutSeconds { get; init; } = 900;
    /// <summary>Maximum payload size in bytes.</summary>
    public int MaxPayloadBytes { get; init; } = 6_291_456;
    /// <summary>Supported runtimes.</summary>
    public IReadOnlyList<ServerlessRuntime> SupportedRuntimes { get; init; } = Array.Empty<ServerlessRuntime>();
    /// <summary>Cold start typical latency in ms.</summary>
    public double TypicalColdStartMs { get; init; } = 500;
    /// <summary>Minimum billing increment in ms.</summary>
    public int BillingIncrementMs { get; init; } = 1;
}

#endregion

#region Strategy Base

/// <summary>
/// Abstract base class for serverless strategies.
/// </summary>
public abstract class ServerlessStrategyBase
{
    private readonly BoundedDictionary<string, long> _operationCounts = new BoundedDictionary<string, long>(1000);
    private long _totalOperations;

    /// <summary>Strategy unique identifier.</summary>
    public abstract string StrategyId { get; }

    /// <summary>Strategy display name.</summary>
    public abstract string DisplayName { get; }

    /// <summary>Strategy category.</summary>
    public abstract ServerlessCategory Category { get; }

    /// <summary>Strategy capabilities.</summary>
    public abstract ServerlessStrategyCapabilities Capabilities { get; }

    /// <summary>Semantic description for AI discovery.</summary>
    public abstract string SemanticDescription { get; }

    /// <summary>Tags for categorization.</summary>
    public abstract string[] Tags { get; }

    /// <summary>Target platform (if platform-specific).</summary>
    public virtual ServerlessPlatform? TargetPlatform => null;

    /// <summary>
    /// Records an operation for statistics.
    /// </summary>
    protected void RecordOperation(string operationType = "default")
    {
        Interlocked.Increment(ref _totalOperations);
        _operationCounts.AddOrUpdate(operationType, 1, (_, c) => c + 1);
    }

    /// <summary>
    /// Gets operation statistics.
    /// </summary>
    public IReadOnlyDictionary<string, long> GetOperationStats() =>
        new Dictionary<string, long>(_operationCounts) { ["total"] = Interlocked.Read(ref _totalOperations) };

    /// <summary>
    /// Gets knowledge object for AI integration.
    /// </summary>
    public virtual KnowledgeObject GetKnowledge()
    {
        return new KnowledgeObject
        {
            Id = $"serverless.strategy.{StrategyId}",
            Topic = "serverless.strategy",
            SourcePluginId = "com.datawarehouse.serverless.ultimate",
            SourcePluginName = DisplayName,
            KnowledgeType = "capability",
            Description = SemanticDescription,
            Payload = new Dictionary<string, object>
            {
                ["strategyId"] = StrategyId,
                ["displayName"] = DisplayName,
                ["category"] = Category.ToString(),
                ["targetPlatform"] = TargetPlatform?.ToString() ?? "any",
                ["capabilities"] = new Dictionary<string, object>
                {
                    ["syncInvocation"] = Capabilities.SupportsSyncInvocation,
                    ["asyncInvocation"] = Capabilities.SupportsAsyncInvocation,
                    ["eventTriggers"] = Capabilities.SupportsEventTriggers,
                    ["provisionedConcurrency"] = Capabilities.SupportsProvisionedConcurrency,
                    ["vpc"] = Capabilities.SupportsVpc,
                    ["containerImages"] = Capabilities.SupportsContainerImages,
                    ["maxMemoryMb"] = Capabilities.MaxMemoryMb,
                    ["maxTimeoutSeconds"] = Capabilities.MaxTimeoutSeconds
                }
            },
            Tags = Tags
        };
    }

    /// <summary>
    /// Gets registered capability for the strategy.
    /// </summary>
    public virtual RegisteredCapability GetCapability()
    {
        return new RegisteredCapability
        {
            CapabilityId = $"serverless.strategy.{StrategyId}",
            DisplayName = DisplayName,
            Description = SemanticDescription,
            Category = SDK.Contracts.CapabilityCategory.Compute,
            SubCategory = Category.ToString(),
            PluginId = "com.datawarehouse.serverless.ultimate",
            PluginName = "Ultimate Serverless",
            PluginVersion = "1.0.0",
            Tags = Tags,
            SemanticDescription = SemanticDescription
        };
    }
}

#endregion
