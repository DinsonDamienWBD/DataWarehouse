namespace DataWarehouse.SDK.Primitives.Configuration;

/// <summary>
/// Provides automatic configuration capabilities for zero-configuration deployments.
/// </summary>
/// <remarks>
/// Auto-configuration providers enable intelligent detection of the deployment environment
/// and automatic suggestion of optimal configuration settings based on available resources,
/// detected platform capabilities, and deployment context.
/// </remarks>
public interface IAutoConfiguration
{
    /// <summary>
    /// Detects the current deployment environment and available resources.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A profile describing the detected environment.</returns>
    /// <remarks>
    /// This method analyzes system resources (CPU, memory, disk), cloud provider metadata,
    /// containerization, and other environmental factors to build a comprehensive profile.
    /// </remarks>
    Task<EnvironmentProfile> DetectEnvironmentAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Suggests optimal configuration settings based on the detected environment.
    /// </summary>
    /// <param name="profile">The environment profile (from DetectEnvironmentAsync).</param>
    /// <param name="workloadType">The expected workload type for optimization.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>Suggested configuration settings.</returns>
    /// <remarks>
    /// Configuration suggestions are optimized for the specific workload type and environment.
    /// Suggestions include connection pools, cache sizes, thread counts, and other tuning parameters.
    /// </remarks>
    Task<ConfigurationSuggestion> SuggestConfigurationAsync(
        EnvironmentProfile profile,
        WorkloadType workloadType = WorkloadType.Balanced,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies default configuration settings suitable for the current environment.
    /// </summary>
    /// <param name="profile">The environment profile (optional, will detect if null).</param>
    /// <param name="workloadType">The expected workload type.</param>
    /// <param name="dryRun">If true, return settings without applying them.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The configuration that was applied (or would be applied in dry-run mode).</returns>
    Task<IReadOnlyDictionary<string, object>> ApplyDefaultsAsync(
        EnvironmentProfile? profile = null,
        WorkloadType workloadType = WorkloadType.Balanced,
        bool dryRun = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a configuration against best practices and the current environment.
    /// </summary>
    /// <param name="configuration">The configuration to validate.</param>
    /// <param name="profile">The environment profile (optional).</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>Validation results with warnings and errors.</returns>
    Task<ConfigurationValidation> ValidateAsync(
        IReadOnlyDictionary<string, object> configuration,
        EnvironmentProfile? profile = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Optimizes an existing configuration for better performance or resource usage.
    /// </summary>
    /// <param name="currentConfiguration">The current configuration to optimize.</param>
    /// <param name="optimizationGoal">The optimization goal (performance, cost, etc.).</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>An optimized configuration with explanation of changes.</returns>
    Task<ConfigurationOptimization> OptimizeAsync(
        IReadOnlyDictionary<string, object> currentConfiguration,
        OptimizationGoal optimizationGoal = OptimizationGoal.Balanced,
        CancellationToken cancellationToken = default);
}
