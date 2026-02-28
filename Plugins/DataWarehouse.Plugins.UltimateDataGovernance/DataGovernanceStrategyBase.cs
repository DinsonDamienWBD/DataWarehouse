using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataGovernance;

/// <summary>
/// Governance category types corresponding to T123 sub-tasks.
/// </summary>
public enum GovernanceCategory
{
    /// <summary>123.1: Policy Management</summary>
    PolicyManagement,
    /// <summary>123.2: Data Ownership</summary>
    DataOwnership,
    /// <summary>123.3: Data Stewardship</summary>
    DataStewardship,
    /// <summary>123.4: Data Classification</summary>
    DataClassification,
    /// <summary>123.5: Lineage Tracking</summary>
    LineageTracking,
    /// <summary>123.6: Retention Management</summary>
    RetentionManagement,
    /// <summary>123.7: Regulatory Compliance</summary>
    RegulatoryCompliance,
    /// <summary>123.8: Audit and Reporting</summary>
    AuditReporting
}

/// <summary>
/// Capabilities of a data governance strategy.
/// </summary>
public sealed record DataGovernanceCapabilities
{
    public bool SupportsAsync { get; init; }
    public bool SupportsBatch { get; init; }
    public bool SupportsRealTime { get; init; }
    public bool SupportsAudit { get; init; }
    public bool SupportsVersioning { get; init; }
}

/// <summary>
/// Interface for data governance strategies.
/// </summary>
public interface IDataGovernanceStrategy
{
    string StrategyId { get; }
    string DisplayName { get; }
    GovernanceCategory Category { get; }
    DataGovernanceCapabilities Capabilities { get; }
    string SemanticDescription { get; }
    string[] Tags { get; }
}

/// <summary>
/// Base class for data governance strategies.
/// Provides production infrastructure via StrategyBase: lifecycle management, health checks, counters, graceful shutdown.
/// </summary>
public abstract class DataGovernanceStrategyBase : StrategyBase, IDataGovernanceStrategy
{
    public abstract override string StrategyId { get; }
    public abstract string DisplayName { get; }
    public override string Name => DisplayName;
    public abstract GovernanceCategory Category { get; }
    public abstract DataGovernanceCapabilities Capabilities { get; }
    public abstract string SemanticDescription { get; }
    public abstract string[] Tags { get; }

    /// <summary>Initializes the strategy. Idempotent.</summary>
    protected override async Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("initialized");
        await Task.CompletedTask;
    }

    /// <summary>Shuts down the strategy gracefully.</summary>
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("shutdown");
        await Task.CompletedTask;
    }

    /// <summary>Gets a cached health status, refreshing every 60 seconds.</summary>
    public HealthStatus GetHealth()
    {
        // Avoid sync-over-async deadlock: compute synchronously without async context capture.
        var status = IsInitialized ? HealthStatus.Healthy : HealthStatus.NotInitialized;
        return status;
    }

    /// <summary>Gets a cached health status asynchronously, refreshing every 60 seconds.</summary>
    public async Task<HealthStatus> GetHealthAsync(CancellationToken ct = default)
    {
        var result = await GetCachedHealthAsync(innerCt =>
        {
            var status = IsInitialized ? HealthStatus.Healthy : HealthStatus.NotInitialized;
            return Task.FromResult(new StrategyHealthCheckResult(
                status == HealthStatus.Healthy,
                status.ToString()));
        }, TimeSpan.FromSeconds(60));

        return result.IsHealthy ? HealthStatus.Healthy : HealthStatus.NotInitialized;
    }

    /// <summary>Gets all counter values.</summary>
    public IReadOnlyDictionary<string, long> GetCounters() => GetAllCounters();
}

/// <summary>Health status for governance strategies.</summary>
public enum HealthStatus
{
    /// <summary>Strategy is healthy and operational.</summary>
    Healthy,
    /// <summary>Strategy has not been initialized.</summary>
    NotInitialized,
    /// <summary>Strategy is degraded.</summary>
    Degraded,
    /// <summary>Strategy is unhealthy.</summary>
    Unhealthy
}

