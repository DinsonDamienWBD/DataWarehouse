namespace DataWarehouse.Plugins.UltimateRAID;

/// <summary>
/// Message bus topics for RAID operations and Intelligence integration.
/// Defines the protocol for communicating with the Ultimate RAID plugin via the message bus.
/// </summary>
/// <remarks>
/// <para>
/// The RAID communication protocol uses three categories of topics:
/// </para>
/// <list type="bullet">
///   <item><b>Operations</b>: Core RAID operations (write, read, rebuild)</item>
///   <item><b>Management</b>: Array management (health, stats, disk operations)</item>
///   <item><b>Intelligence</b>: AI-enhanced RAID features (predictions, optimizations)</item>
/// </list>
/// <para>
/// All topics follow the naming convention: <c>raid.ultimate.[category].[operation]</c>
/// </para>
/// </remarks>
public static class RaidTopics
{
    // ========================================
    // Base Prefix
    // ========================================

    /// <summary>
    /// Base topic prefix for all Ultimate RAID operations.
    /// </summary>
    public const string Prefix = "raid.ultimate";

    // ========================================
    // Core RAID Operations
    // ========================================

    /// <summary>
    /// Topic for write operations to RAID array.
    /// </summary>
    /// <remarks>
    /// Request payload:
    /// <list type="bullet">
    ///   <item><c>strategyId</c>: RAID strategy identifier</item>
    ///   <item><c>lba</c>: Logical block address</item>
    ///   <item><c>data</c>: Data to write (byte[])</item>
    /// </list>
    /// Response payload:
    /// <list type="bullet">
    ///   <item><c>bytesWritten</c>: Number of bytes written</item>
    ///   <item><c>duration</c>: Operation duration in milliseconds</item>
    /// </list>
    /// </remarks>
    public const string Write = $"{Prefix}.write";

    /// <summary>
    /// Topic for read operations from RAID array.
    /// </summary>
    /// <remarks>
    /// Request payload:
    /// <list type="bullet">
    ///   <item><c>strategyId</c>: RAID strategy identifier</item>
    ///   <item><c>lba</c>: Logical block address</item>
    ///   <item><c>length</c>: Number of bytes to read</item>
    /// </list>
    /// Response payload:
    /// <list type="bullet">
    ///   <item><c>data</c>: Read data (byte[])</item>
    ///   <item><c>bytesRead</c>: Number of bytes read</item>
    /// </list>
    /// </remarks>
    public const string Read = $"{Prefix}.read";

    /// <summary>
    /// Topic for rebuild operations on failed disk.
    /// </summary>
    /// <remarks>
    /// Request payload:
    /// <list type="bullet">
    ///   <item><c>strategyId</c>: RAID strategy identifier</item>
    ///   <item><c>diskIndex</c>: Index of failed disk to rebuild</item>
    /// </list>
    /// Response payload:
    /// <list type="bullet">
    ///   <item><c>success</c>: Boolean indicating rebuild success</item>
    ///   <item><c>duration</c>: Rebuild duration</item>
    /// </list>
    /// </remarks>
    public const string Rebuild = $"{Prefix}.rebuild";

    /// <summary>
    /// Topic for verification operations.
    /// </summary>
    public const string Verify = $"{Prefix}.verify";

    /// <summary>
    /// Topic for scrub operations.
    /// </summary>
    public const string Scrub = $"{Prefix}.scrub";

    // ========================================
    // Array Management
    // ========================================

    /// <summary>
    /// Topic for health check operations.
    /// </summary>
    /// <remarks>
    /// Request payload:
    /// <list type="bullet">
    ///   <item><c>strategyId</c>: RAID strategy identifier</item>
    /// </list>
    /// Response payload:
    /// <list type="bullet">
    ///   <item><c>state</c>: Current RAID state (Optimal, Degraded, Failed, etc.)</item>
    ///   <item><c>healthyDisks</c>: Number of healthy disks</item>
    ///   <item><c>failedDisks</c>: Number of failed disks</item>
    ///   <item><c>diskStatuses</c>: Array of individual disk statuses</item>
    /// </list>
    /// </remarks>
    public const string Health = $"{Prefix}.health";

    /// <summary>
    /// Topic for statistics retrieval.
    /// </summary>
    /// <remarks>
    /// Response includes performance metrics, I/O statistics, and uptime.
    /// </remarks>
    public const string Statistics = $"{Prefix}.stats";

    /// <summary>
    /// Topic for adding a disk to the array.
    /// </summary>
    public const string AddDisk = $"{Prefix}.add-disk";

    /// <summary>
    /// Topic for removing a disk from the array.
    /// </summary>
    public const string RemoveDisk = $"{Prefix}.remove-disk";

    /// <summary>
    /// Topic for replacing a failed disk.
    /// </summary>
    public const string ReplaceDisk = $"{Prefix}.replace-disk";

    // ========================================
    // Intelligence-Enhanced Features
    // ========================================

    /// <summary>
    /// Topic for requesting disk failure prediction via Intelligence.
    /// </summary>
    /// <remarks>
    /// Request payload:
    /// <list type="bullet">
    ///   <item><c>strategyId</c>: RAID strategy identifier</item>
    ///   <item><c>diskIndex</c>: Index of disk to analyze</item>
    ///   <item><c>smartData</c>: SMART attributes for analysis</item>
    ///   <item><c>historicalMetrics</c>: Optional historical performance data</item>
    /// </list>
    /// Response payload:
    /// <list type="bullet">
    ///   <item><c>failureProbability</c>: Probability of failure (0.0-1.0)</item>
    ///   <item><c>estimatedTimeToFailure</c>: Predicted time until failure</item>
    ///   <item><c>confidence</c>: Prediction confidence</item>
    ///   <item><c>recommendations</c>: Suggested actions</item>
    /// </list>
    /// </remarks>
    public const string PredictFailure = $"{Prefix}.predict.failure";

    /// <summary>
    /// Response topic for disk failure prediction requests.
    /// </summary>
    public const string PredictFailureResponse = $"{Prefix}.predict.failure.response";

    /// <summary>
    /// Topic for requesting optimal RAID level recommendation via Intelligence.
    /// </summary>
    /// <remarks>
    /// Request payload:
    /// <list type="bullet">
    ///   <item><c>workloadProfile</c>: Workload characteristics (read/write ratio, access patterns)</item>
    ///   <item><c>availableDisks</c>: Number of available disks</item>
    ///   <item><c>capacityRequirement</c>: Required usable capacity</item>
    ///   <item><c>priorityGoal</c>: "performance", "capacity", or "redundancy"</item>
    /// </list>
    /// Response payload:
    /// <list type="bullet">
    ///   <item><c>recommendedLevel</c>: Recommended RAID level</item>
    ///   <item><c>strategyId</c>: Specific strategy identifier</item>
    ///   <item><c>confidence</c>: Recommendation confidence</item>
    ///   <item><c>reasoning</c>: Explanation for recommendation</item>
    ///   <item><c>alternatives</c>: Alternative RAID levels with scores</item>
    /// </list>
    /// </remarks>
    public const string OptimizeLevel = $"{Prefix}.optimize.level";

    /// <summary>
    /// Response topic for RAID level optimization requests.
    /// </summary>
    public const string OptimizeLevelResponse = $"{Prefix}.optimize.level.response";

    /// <summary>
    /// Topic for selecting the best strategy for specific requirements.
    /// </summary>
    /// <remarks>
    /// Similar to optimize.level but more granular strategy selection.
    /// </remarks>
    public const string SelectStrategy = $"{Prefix}.select";

    /// <summary>
    /// Response topic for strategy selection requests.
    /// </summary>
    public const string SelectStrategyResponse = $"{Prefix}.select.response";

    /// <summary>
    /// Topic for reporting health metrics to Intelligence for learning.
    /// </summary>
    /// <remarks>
    /// This is a publish-only topic for feeding RAID health data to Intelligence
    /// for long-term pattern recognition and predictive modeling.
    /// Payload:
    /// <list type="bullet">
    ///   <item><c>strategyId</c>: RAID strategy identifier</item>
    ///   <item><c>timestamp</c>: Timestamp of health check</item>
    ///   <item><c>healthStatus</c>: Complete health status snapshot</item>
    ///   <item><c>performanceMetrics</c>: Recent performance statistics</item>
    /// </list>
    /// </remarks>
    public const string ReportHealth = $"{Prefix}.report.health";

    /// <summary>
    /// Topic for requesting workload prediction via Intelligence.
    /// </summary>
    /// <remarks>
    /// Predicts future I/O workload patterns to enable proactive optimizations.
    /// </remarks>
    public const string PredictWorkload = $"{Prefix}.predict.workload";

    /// <summary>
    /// Response topic for workload prediction requests.
    /// </summary>
    public const string PredictWorkloadResponse = $"{Prefix}.predict.workload.response";

    // ========================================
    // Strategy Discovery
    // ========================================

    /// <summary>
    /// Topic for listing available RAID strategies.
    /// </summary>
    public const string ListStrategies = $"{Prefix}.list-strategies";

    /// <summary>
    /// Topic for setting the default RAID strategy.
    /// </summary>
    public const string SetDefault = $"{Prefix}.set-default";

    // ========================================
    // Topic Patterns
    // ========================================

    /// <summary>
    /// Pattern for subscribing to all RAID operation topics.
    /// </summary>
    public const string AllOperationsPattern = $"{Prefix}.*";

    /// <summary>
    /// Pattern for subscribing to all Intelligence-enhanced RAID topics.
    /// </summary>
    public const string AllIntelligencePattern = $"{Prefix}.predict.*";
}
