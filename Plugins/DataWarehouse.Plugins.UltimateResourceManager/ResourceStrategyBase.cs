using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.UltimateResourceManager;

/// <summary>
/// Defines the category of resource management strategy.
/// </summary>
public enum ResourceCategory
{
    /// <summary>CPU scheduling and affinity management.</summary>
    Cpu,
    /// <summary>Memory allocation and management.</summary>
    Memory,
    /// <summary>I/O throttling and scheduling.</summary>
    IO,
    /// <summary>GPU allocation and compute management.</summary>
    Gpu,
    /// <summary>Network bandwidth management.</summary>
    Network,
    /// <summary>Combined/hybrid resource management.</summary>
    Composite,
    /// <summary>Quota and limit enforcement.</summary>
    Quota
}

/// <summary>
/// Represents resource usage metrics.
/// </summary>
public sealed record ResourceMetrics
{
    /// <summary>Current CPU usage percentage (0-100).</summary>
    public double CpuPercent { get; init; }
    /// <summary>Current memory usage in bytes.</summary>
    public long MemoryBytes { get; init; }
    /// <summary>Current I/O operations per second.</summary>
    public long IopsRate { get; init; }
    /// <summary>Current I/O bandwidth in bytes per second.</summary>
    public long IoBandwidth { get; init; }
    /// <summary>Current GPU utilization percentage (0-100).</summary>
    public double GpuPercent { get; init; }
    /// <summary>GPU memory usage in bytes.</summary>
    public long GpuMemoryBytes { get; init; }
    /// <summary>Network bandwidth in bytes per second.</summary>
    public long NetworkBandwidth { get; init; }
    /// <summary>Timestamp of measurement.</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Represents a resource quota configuration.
/// </summary>
public sealed record ResourceQuota
{
    /// <summary>Unique quota identifier.</summary>
    public required string QuotaId { get; init; }
    /// <summary>Maximum CPU cores (0 = unlimited).</summary>
    public double MaxCpuCores { get; init; }
    /// <summary>Maximum memory in bytes (0 = unlimited).</summary>
    public long MaxMemoryBytes { get; init; }
    /// <summary>Maximum IOPS (0 = unlimited).</summary>
    public long MaxIops { get; init; }
    /// <summary>Maximum I/O bandwidth in bytes/sec (0 = unlimited).</summary>
    public long MaxIoBandwidth { get; init; }
    /// <summary>Maximum GPU percentage (0 = none, 100 = full).</summary>
    public double MaxGpuPercent { get; init; }
    /// <summary>Maximum network bandwidth in bytes/sec (0 = unlimited).</summary>
    public long MaxNetworkBandwidth { get; init; }
    /// <summary>Priority level (higher = more priority).</summary>
    public int Priority { get; init; } = 50;
    /// <summary>Whether to enforce hard limits.</summary>
    public bool HardLimit { get; init; } = true;
}

/// <summary>
/// Represents a resource allocation request.
/// </summary>
public sealed record ResourceRequest
{
    /// <summary>Request identifier.</summary>
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");
    /// <summary>Requesting entity (plugin, tenant, process).</summary>
    public required string RequesterId { get; init; }
    /// <summary>Requested CPU cores.</summary>
    public double CpuCores { get; init; }
    /// <summary>Requested memory in bytes.</summary>
    public long MemoryBytes { get; init; }
    /// <summary>Requested IOPS.</summary>
    public long Iops { get; init; }
    /// <summary>Requested I/O bandwidth.</summary>
    public long IoBandwidth { get; init; }
    /// <summary>Requested GPU percentage.</summary>
    public double GpuPercent { get; init; }
    /// <summary>Priority of this request.</summary>
    public int Priority { get; init; } = 50;
    /// <summary>Maximum time to wait for allocation.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Represents a resource allocation result.
/// </summary>
public sealed record ResourceAllocation
{
    /// <summary>Original request ID.</summary>
    public required string RequestId { get; init; }
    /// <summary>Whether allocation succeeded.</summary>
    public bool Success { get; init; }
    /// <summary>Allocated CPU cores.</summary>
    public double AllocatedCpuCores { get; init; }
    /// <summary>Allocated memory in bytes.</summary>
    public long AllocatedMemoryBytes { get; init; }
    /// <summary>Allocated IOPS.</summary>
    public long AllocatedIops { get; init; }
    /// <summary>Allocated I/O bandwidth.</summary>
    public long AllocatedIoBandwidth { get; init; }
    /// <summary>Allocated GPU percentage.</summary>
    public double AllocatedGpuPercent { get; init; }
    /// <summary>Allocation handle for release.</summary>
    public string? AllocationHandle { get; init; }
    /// <summary>Reason if allocation failed.</summary>
    public string? FailureReason { get; init; }
    /// <summary>Allocation timestamp.</summary>
    public DateTime AllocatedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Capabilities of a resource management strategy.
/// </summary>
public sealed record ResourceStrategyCapabilities
{
    /// <summary>Whether strategy supports CPU management.</summary>
    public required bool SupportsCpu { get; init; }
    /// <summary>Whether strategy supports memory management.</summary>
    public required bool SupportsMemory { get; init; }
    /// <summary>Whether strategy supports I/O management.</summary>
    public required bool SupportsIO { get; init; }
    /// <summary>Whether strategy supports GPU management.</summary>
    public required bool SupportsGpu { get; init; }
    /// <summary>Whether strategy supports network management.</summary>
    public required bool SupportsNetwork { get; init; }
    /// <summary>Whether strategy supports quotas.</summary>
    public required bool SupportsQuotas { get; init; }
    /// <summary>Whether strategy supports hierarchical quotas.</summary>
    public required bool SupportsHierarchicalQuotas { get; init; }
    /// <summary>Whether strategy supports preemption.</summary>
    public required bool SupportsPreemption { get; init; }
}

/// <summary>
/// Interface for resource management strategies.
/// </summary>
public interface IResourceStrategy
{
    /// <summary>Unique identifier for this strategy.</summary>
    string StrategyId { get; }
    /// <summary>Human-readable display name.</summary>
    string DisplayName { get; }
    /// <summary>Category of this strategy.</summary>
    ResourceCategory Category { get; }
    /// <summary>Capabilities of this strategy.</summary>
    ResourceStrategyCapabilities Capabilities { get; }
    /// <summary>Semantic description for AI discovery.</summary>
    string SemanticDescription { get; }
    /// <summary>Tags for categorization.</summary>
    string[] Tags { get; }
    /// <summary>Gets current resource metrics.</summary>
    Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);
    /// <summary>Requests resource allocation.</summary>
    Task<ResourceAllocation> AllocateAsync(ResourceRequest request, CancellationToken ct = default);
    /// <summary>Releases a resource allocation.</summary>
    Task<bool> ReleaseAsync(string allocationHandle, CancellationToken ct = default);
    /// <summary>Applies a quota.</summary>
    Task<bool> ApplyQuotaAsync(ResourceQuota quota, CancellationToken ct = default);
    /// <summary>Initializes the strategy.</summary>
    Task InitializeAsync(CancellationToken ct = default);
    /// <summary>Disposes of the strategy resources.</summary>
    Task DisposeAsync();
}

/// <summary>
/// Abstract base class for resource management strategies.
/// </summary>
public abstract class ResourceStrategyBase : IResourceStrategy
{
    private readonly ConcurrentDictionary<string, ResourceAllocation> _allocations = new();
    private readonly ConcurrentDictionary<string, ResourceQuota> _quotas = new();
    private bool _initialized;

    /// <inheritdoc/>
    public abstract string StrategyId { get; }
    /// <inheritdoc/>
    public abstract string DisplayName { get; }
    /// <inheritdoc/>
    public abstract ResourceCategory Category { get; }
    /// <inheritdoc/>
    public abstract ResourceStrategyCapabilities Capabilities { get; }
    /// <inheritdoc/>
    public abstract string SemanticDescription { get; }
    /// <inheritdoc/>
    public abstract string[] Tags { get; }

    /// <summary>Gets whether the strategy is initialized.</summary>
    protected bool IsInitialized => _initialized;
    /// <summary>Gets active allocations.</summary>
    protected IReadOnlyDictionary<string, ResourceAllocation> ActiveAllocations => _allocations;
    /// <summary>Gets active quotas.</summary>
    protected IReadOnlyDictionary<string, ResourceQuota> ActiveQuotas => _quotas;

    /// <inheritdoc/>
    public virtual async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        await InitializeCoreAsync(ct);
        _initialized = true;
    }

    /// <inheritdoc/>
    public virtual async Task DisposeAsync()
    {
        if (!_initialized) return;
        await DisposeCoreAsync();
        _allocations.Clear();
        _quotas.Clear();
        _initialized = false;
    }

    /// <summary>Core initialization logic.</summary>
    protected virtual Task InitializeCoreAsync(CancellationToken ct) => Task.CompletedTask;
    /// <summary>Core disposal logic.</summary>
    protected virtual Task DisposeCoreAsync() => Task.CompletedTask;

    /// <inheritdoc/>
    public abstract Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default);

    /// <inheritdoc/>
    public virtual async Task<ResourceAllocation> AllocateAsync(ResourceRequest request, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        var allocation = await AllocateCoreAsync(request, ct);
        if (allocation.Success && allocation.AllocationHandle != null)
        {
            _allocations[allocation.AllocationHandle] = allocation;
        }
        return allocation;
    }

    /// <inheritdoc/>
    public virtual async Task<bool> ReleaseAsync(string allocationHandle, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        if (!_allocations.TryRemove(allocationHandle, out var allocation))
            return false;
        return await ReleaseCoreAsync(allocation, ct);
    }

    /// <inheritdoc/>
    public virtual Task<bool> ApplyQuotaAsync(ResourceQuota quota, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        _quotas[quota.QuotaId] = quota;
        return ApplyQuotaCoreAsync(quota, ct);
    }

    /// <summary>Core allocation logic.</summary>
    protected abstract Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct);
    /// <summary>Core release logic.</summary>
    protected virtual Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct) => Task.FromResult(true);
    /// <summary>Core quota application logic.</summary>
    protected virtual Task<bool> ApplyQuotaCoreAsync(ResourceQuota quota, CancellationToken ct) => Task.FromResult(true);

    /// <summary>Throws if not initialized.</summary>
    protected void ThrowIfNotInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException($"Strategy '{StrategyId}' has not been initialized.");
    }
}

/// <summary>
/// Thread-safe registry for resource strategies.
/// </summary>
public sealed class ResourceStrategyRegistry
{
    private readonly ConcurrentDictionary<string, IResourceStrategy> _strategies = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers a strategy.</summary>
    public void Register(IResourceStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        _strategies[strategy.StrategyId] = strategy;
    }

    /// <summary>Unregisters a strategy by ID.</summary>
    public bool Unregister(string strategyId) => _strategies.TryRemove(strategyId, out _);

    /// <summary>Gets a strategy by ID.</summary>
    public IResourceStrategy? Get(string strategyId) =>
        _strategies.TryGetValue(strategyId, out var strategy) ? strategy : null;

    /// <summary>Gets all registered strategies.</summary>
    public IReadOnlyCollection<IResourceStrategy> GetAll() => _strategies.Values.ToList().AsReadOnly();

    /// <summary>Gets strategies by category.</summary>
    public IReadOnlyCollection<IResourceStrategy> GetByCategory(ResourceCategory category) =>
        _strategies.Values.Where(s => s.Category == category).OrderBy(s => s.DisplayName).ToList().AsReadOnly();

    /// <summary>Gets the count of registered strategies.</summary>
    public int Count => _strategies.Count;

    /// <summary>Auto-discovers and registers strategies from assemblies.</summary>
    public int AutoDiscover(params System.Reflection.Assembly[] assemblies)
    {
        var strategyType = typeof(IResourceStrategy);
        int discovered = 0;

        foreach (var assembly in assemblies)
        {
            try
            {
                var types = assembly.GetTypes()
                    .Where(t => !t.IsAbstract && !t.IsInterface && strategyType.IsAssignableFrom(t));

                foreach (var type in types)
                {
                    try
                    {
                        if (Activator.CreateInstance(type) is IResourceStrategy strategy)
                        {
                            Register(strategy);
                            discovered++;
                        }
                    }
                    catch { /* Skip types that cannot be instantiated */ }
                }
            }
            catch { /* Skip assemblies that cannot be scanned */ }
        }

        return discovered;
    }
}
