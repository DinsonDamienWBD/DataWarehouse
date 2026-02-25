namespace DataWarehouse.SDK.Primitives.Configuration;

/// <summary>
/// Represents the detected deployment environment and available resources.
/// </summary>
/// <param name="DeploymentType">The type of deployment environment.</param>
/// <param name="CloudProvider">The cloud provider (if applicable).</param>
/// <param name="ContainerRuntime">The container runtime (if containerized).</param>
/// <param name="OperatingSystem">The operating system.</param>
/// <param name="CpuCores">Number of available CPU cores.</param>
/// <param name="MemoryBytes">Available memory in bytes.</param>
/// <param name="DiskType">The type of disk storage (SSD, HDD, NVMe, etc.).</param>
/// <param name="NetworkBandwidthMbps">Network bandwidth in Mbps (if detectable).</param>
/// <param name="IsProduction">True if this appears to be a production environment.</param>
/// <param name="Capabilities">Additional capabilities detected (e.g., GPU, special hardware).</param>
public record EnvironmentProfile(
    DeploymentEnvironment DeploymentType,
    CloudProvider? CloudProvider,
    string? ContainerRuntime,
    string OperatingSystem,
    int CpuCores,
    long MemoryBytes,
    DiskType DiskType,
    int? NetworkBandwidthMbps,
    bool IsProduction,
    IReadOnlyDictionary<string, string>? Capabilities = null);

/// <summary>
/// Defines the type of deployment environment.
/// </summary>
public enum DeploymentEnvironment
{
    /// <summary>Unknown or undetected environment.</summary>
    Unknown = 0,

    /// <summary>Local development machine.</summary>
    Development = 1,

    /// <summary>Continuous integration environment.</summary>
    CI = 2,

    /// <summary>Testing or QA environment.</summary>
    Testing = 3,

    /// <summary>Staging or pre-production environment.</summary>
    Staging = 4,

    /// <summary>Production environment.</summary>
    Production = 5,

    /// <summary>Docker container.</summary>
    Docker = 10,

    /// <summary>Kubernetes cluster.</summary>
    Kubernetes = 11,

    /// <summary>Serverless/Lambda function.</summary>
    Serverless = 12,

    /// <summary>Edge computing environment.</summary>
    Edge = 13
}

/// <summary>
/// Identifies major cloud providers.
/// </summary>
public enum CloudProvider
{
    /// <summary>Not running in a cloud environment.</summary>
    None = 0,

    /// <summary>Amazon Web Services.</summary>
    AWS = 1,

    /// <summary>Microsoft Azure.</summary>
    Azure = 2,

    /// <summary>Google Cloud Platform.</summary>
    GCP = 3,

    /// <summary>Alibaba Cloud.</summary>
    AlibabaCloud = 4,

    /// <summary>Oracle Cloud Infrastructure.</summary>
    OCI = 5,

    /// <summary>IBM Cloud.</summary>
    IBM = 6,

    /// <summary>DigitalOcean.</summary>
    DigitalOcean = 7,

    /// <summary>Other or private cloud.</summary>
    Other = 100
}

/// <summary>
/// Identifies the type of disk storage.
/// </summary>
public enum DiskType
{
    /// <summary>Unknown disk type.</summary>
    Unknown = 0,

    /// <summary>Traditional hard disk drive.</summary>
    HDD = 1,

    /// <summary>Solid-state drive.</summary>
    SSD = 2,

    /// <summary>NVMe (Non-Volatile Memory Express) drive.</summary>
    NVMe = 3,

    /// <summary>Network-attached storage.</summary>
    NAS = 4,

    /// <summary>Cloud object storage (e.g., S3, Azure Blob).</summary>
    CloudObject = 5,

    /// <summary>In-memory storage.</summary>
    Memory = 6
}

/// <summary>
/// Defines the expected workload type for optimization.
/// </summary>
public enum WorkloadType
{
    /// <summary>Balanced workload (default).</summary>
    Balanced = 0,

    /// <summary>Read-heavy workload.</summary>
    ReadHeavy = 1,

    /// <summary>Write-heavy workload.</summary>
    WriteHeavy = 2,

    /// <summary>Analytics/OLAP workload.</summary>
    Analytics = 3,

    /// <summary>Transactional/OLTP workload.</summary>
    Transactional = 4,

    /// <summary>Batch processing workload.</summary>
    Batch = 5,

    /// <summary>Real-time/streaming workload.</summary>
    Streaming = 6,

    /// <summary>Mixed workload with variable patterns.</summary>
    Mixed = 7
}

/// <summary>
/// Represents suggested configuration settings.
/// </summary>
/// <param name="Settings">The suggested configuration key-value pairs.</param>
/// <param name="Rationale">Explanation for each suggested setting.</param>
/// <param name="Confidence">Confidence level in the suggestions (0.0 to 1.0).</param>
/// <param name="Warnings">Any warnings about the environment or suggestions.</param>
public record ConfigurationSuggestion(
    IReadOnlyDictionary<string, object> Settings,
    IReadOnlyDictionary<string, string> Rationale,
    double Confidence,
    IReadOnlyList<string>? Warnings = null);

/// <summary>
/// Represents the result of configuration validation.
/// </summary>
/// <param name="IsValid">True if the configuration is valid; false otherwise.</param>
/// <param name="Errors">Critical errors that prevent operation.</param>
/// <param name="Warnings">Non-critical warnings that may affect performance or reliability.</param>
/// <param name="Suggestions">Suggestions for improvement.</param>
public record ConfigurationValidation(
    bool IsValid,
    IReadOnlyList<ConfigurationIssue> Errors,
    IReadOnlyList<ConfigurationIssue> Warnings,
    IReadOnlyList<ConfigurationIssue> Suggestions);

/// <summary>
/// Represents a configuration issue (error, warning, or suggestion).
/// </summary>
/// <param name="Key">The configuration key related to this issue.</param>
/// <param name="Message">Description of the issue.</param>
/// <param name="Severity">Severity level of the issue.</param>
/// <param name="SuggestedValue">Suggested value to fix the issue (optional).</param>
public record ConfigurationIssue(
    string Key,
    string Message,
    IssueSeverity Severity,
    object? SuggestedValue = null);

/// <summary>
/// Severity level for configuration issues.
/// </summary>
public enum IssueSeverity
{
    /// <summary>Informational message.</summary>
    Info = 0,

    /// <summary>Warning that may affect performance or reliability.</summary>
    Warning = 1,

    /// <summary>Error that prevents operation or causes failures.</summary>
    Error = 2,

    /// <summary>Critical error that may cause data loss or security issues.</summary>
    Critical = 3
}

/// <summary>
/// Represents an optimized configuration with explanations.
/// </summary>
/// <param name="OriginalSettings">The original configuration.</param>
/// <param name="OptimizedSettings">The optimized configuration.</param>
/// <param name="Changes">List of changes made with rationale.</param>
/// <param name="ExpectedImprovement">Expected improvement description.</param>
/// <param name="Goal">The optimization goal that was used.</param>
public record ConfigurationOptimization(
    IReadOnlyDictionary<string, object> OriginalSettings,
    IReadOnlyDictionary<string, object> OptimizedSettings,
    IReadOnlyList<ConfigurationChange> Changes,
    string ExpectedImprovement,
    OptimizationGoal Goal);

/// <summary>
/// Represents a configuration change with rationale.
/// </summary>
/// <param name="Key">The configuration key that changed.</param>
/// <param name="OldValue">The original value.</param>
/// <param name="NewValue">The new value.</param>
/// <param name="Reason">Explanation for the change.</param>
public record ConfigurationChange(
    string Key,
    object? OldValue,
    object NewValue,
    string Reason);

/// <summary>
/// Defines optimization goals for configuration tuning.
/// </summary>
public enum OptimizationGoal
{
    /// <summary>Balanced optimization for general use.</summary>
    Balanced = 0,

    /// <summary>Optimize for maximum performance.</summary>
    Performance = 1,

    /// <summary>Optimize for minimum resource usage and cost.</summary>
    Cost = 2,

    /// <summary>Optimize for reliability and fault tolerance.</summary>
    Reliability = 3,

    /// <summary>Optimize for minimum latency.</summary>
    Latency = 4,

    /// <summary>Optimize for maximum throughput.</summary>
    Throughput = 5,

    /// <summary>Optimize for energy efficiency.</summary>
    Energy = 6
}
