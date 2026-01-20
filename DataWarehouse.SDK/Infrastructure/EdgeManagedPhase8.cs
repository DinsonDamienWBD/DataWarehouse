using System.Collections.Concurrent;

namespace DataWarehouse.SDK.Infrastructure;

// ============================================================================
// PHASE 8: Edge & Managed Services
// EM1: Edge Locations, EM2: Managed Services Platform
// ============================================================================

#region EM1: Edge Locations

/// <summary>
/// Detects and classifies edge nodes based on latency.
/// </summary>
public sealed class EdgeNodeDetector : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, EdgeNodeInfo> _edgeNodes = new();
    private readonly Timer _detectionTimer;
    private readonly EdgeDetectionConfig _config;
    private volatile bool _disposed;

    public event EventHandler<EdgeNodeEventArgs>? EdgeNodeDetected;
    public event EventHandler<EdgeNodeEventArgs>? EdgeNodeLost;

    public EdgeNodeDetector(EdgeDetectionConfig? config = null)
    {
        _config = config ?? new EdgeDetectionConfig();
        _detectionTimer = new Timer(DetectEdgeNodes, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public void RegisterNode(string nodeId, double latencyMs, string location)
    {
        var classification = ClassifyNode(latencyMs);
        var node = new EdgeNodeInfo
        {
            NodeId = nodeId,
            LatencyMs = latencyMs,
            Location = location,
            Classification = classification,
            DetectedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow
        };

        var isNew = !_edgeNodes.ContainsKey(nodeId);
        _edgeNodes[nodeId] = node;

        if (isNew && classification == EdgeClassification.Edge)
            EdgeNodeDetected?.Invoke(this, new EdgeNodeEventArgs { Node = node });
    }

    public void UpdateLatency(string nodeId, double latencyMs)
    {
        if (_edgeNodes.TryGetValue(nodeId, out var node))
        {
            node.LatencyMs = latencyMs;
            node.LastSeenAt = DateTime.UtcNow;
            node.Classification = ClassifyNode(latencyMs);
        }
    }

    public IReadOnlyList<EdgeNodeInfo> GetEdgeNodes() =>
        _edgeNodes.Values.Where(n => n.Classification == EdgeClassification.Edge).ToList();

    public IReadOnlyList<EdgeNodeInfo> GetAllNodes() => _edgeNodes.Values.ToList();

    public EdgeNodeInfo? GetNearestEdge(string clientLocation)
    {
        return _edgeNodes.Values
            .Where(n => n.Classification == EdgeClassification.Edge)
            .OrderBy(n => n.LatencyMs)
            .FirstOrDefault();
    }

    private EdgeClassification ClassifyNode(double latencyMs) => latencyMs switch
    {
        < 20 => EdgeClassification.Edge,
        < 100 => EdgeClassification.Regional,
        < 300 => EdgeClassification.Origin,
        _ => EdgeClassification.Remote
    };

    private void DetectEdgeNodes(object? state)
    {
        if (_disposed) return;

        var staleThreshold = DateTime.UtcNow.AddMinutes(-5);
        foreach (var kvp in _edgeNodes)
        {
            if (kvp.Value.LastSeenAt < staleThreshold)
            {
                if (_edgeNodes.TryRemove(kvp.Key, out var node))
                    EdgeNodeLost?.Invoke(this, new EdgeNodeEventArgs { Node = node });
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _detectionTimer.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Synchronizes origin data to edge caches.
/// </summary>
public sealed class EdgeOriginSync : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, EdgeCacheEntry> _cache = new();
    private readonly ConcurrentQueue<SyncTask> _syncQueue = new();
    private readonly Timer _syncTimer;
    private readonly EdgeOriginSyncConfig _config;
    private volatile bool _disposed;

    public event EventHandler<SyncCompletedEventArgs>? SyncCompleted;

    public EdgeOriginSync(EdgeOriginSyncConfig? config = null)
    {
        _config = config ?? new EdgeOriginSyncConfig();
        _syncTimer = new Timer(ProcessSyncQueue, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void QueueSync(string objectId, byte[] data, SyncPriority priority = SyncPriority.Normal)
    {
        _syncQueue.Enqueue(new SyncTask
        {
            ObjectId = objectId,
            Data = data,
            Priority = priority,
            QueuedAt = DateTime.UtcNow
        });
    }

    public async Task<bool> SyncToEdgeAsync(string edgeNodeId, string objectId, byte[] data, CancellationToken ct = default)
    {
        var entry = new EdgeCacheEntry
        {
            ObjectId = objectId,
            EdgeNodeId = edgeNodeId,
            Data = data,
            SyncedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(_config.DefaultTtl)
        };

        _cache[$"{edgeNodeId}:{objectId}"] = entry;

        SyncCompleted?.Invoke(this, new SyncCompletedEventArgs
        {
            ObjectId = objectId,
            EdgeNodeId = edgeNodeId,
            BytesSynced = data.Length
        });

        return true;
    }

    public EdgeCacheEntry? GetCachedEntry(string edgeNodeId, string objectId)
    {
        var key = $"{edgeNodeId}:{objectId}";
        if (_cache.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTime.UtcNow)
            return entry;
        return null;
    }

    public void InvalidateCache(string objectId)
    {
        foreach (var key in _cache.Keys.Where(k => k.EndsWith($":{objectId}")))
            _cache.TryRemove(key, out _);
    }

    private void ProcessSyncQueue(object? state)
    {
        if (_disposed) return;

        while (_syncQueue.TryDequeue(out var task))
        {
            // In production, would sync to actual edge nodes
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _syncTimer.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Cache invalidation protocol using pub/sub.
/// </summary>
public sealed class CacheInvalidationProtocol : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, List<Action<InvalidationMessage>>> _subscribers = new();
    private readonly ConcurrentQueue<InvalidationMessage> _messageQueue = new();
    private readonly Timer _processTimer;
    private volatile bool _disposed;

    public CacheInvalidationProtocol()
    {
        _processTimer = new Timer(ProcessMessages, null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
    }

    public void Subscribe(string topic, Action<InvalidationMessage> handler)
    {
        var handlers = _subscribers.GetOrAdd(topic, _ => new List<Action<InvalidationMessage>>());
        lock (handlers) handlers.Add(handler);
    }

    public void Unsubscribe(string topic, Action<InvalidationMessage> handler)
    {
        if (_subscribers.TryGetValue(topic, out var handlers))
            lock (handlers) handlers.Remove(handler);
    }

    public void Publish(InvalidationMessage message)
    {
        _messageQueue.Enqueue(message);
    }

    public void PublishInvalidation(string objectId, InvalidationReason reason)
    {
        Publish(new InvalidationMessage
        {
            ObjectId = objectId,
            Reason = reason,
            Timestamp = DateTime.UtcNow,
            Topic = "invalidation"
        });
    }

    private void ProcessMessages(object? state)
    {
        if (_disposed) return;

        while (_messageQueue.TryDequeue(out var message))
        {
            if (_subscribers.TryGetValue(message.Topic, out var handlers))
            {
                lock (handlers)
                {
                    foreach (var handler in handlers)
                        handler(message);
                }
            }

            // Wildcard subscribers
            if (_subscribers.TryGetValue("*", out var wildcardHandlers))
            {
                lock (wildcardHandlers)
                {
                    foreach (var handler in wildcardHandlers)
                        handler(message);
                }
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _processTimer.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Monitors edge node health with failover support.
/// </summary>
public sealed class EdgeHealthMonitor : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, EdgeHealthStatus> _healthStatus = new();
    private readonly Timer _healthCheckTimer;
    private readonly EdgeHealthConfig _config;
    private volatile bool _disposed;

    public event EventHandler<EdgeFailoverEventArgs>? FailoverTriggered;

    public EdgeHealthMonitor(EdgeHealthConfig? config = null)
    {
        _config = config ?? new EdgeHealthConfig();
        _healthCheckTimer = new Timer(CheckHealth, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public void ReportHealth(string edgeNodeId, EdgeHealthReport report)
    {
        var status = _healthStatus.GetOrAdd(edgeNodeId, _ => new EdgeHealthStatus { EdgeNodeId = edgeNodeId });
        status.LastReport = report;
        status.LastReportedAt = DateTime.UtcNow;
        status.HealthScore = CalculateHealthScore(report);
        status.IsHealthy = status.HealthScore >= _config.HealthThreshold;
    }

    public EdgeHealthStatus? GetHealthStatus(string edgeNodeId) =>
        _healthStatus.TryGetValue(edgeNodeId, out var status) ? status : null;

    public IReadOnlyList<EdgeHealthStatus> GetAllHealthStatus() => _healthStatus.Values.ToList();

    public async Task<FailoverResult> TriggerFailoverAsync(string fromEdgeId, string toEdgeId, CancellationToken ct = default)
    {
        if (!_healthStatus.TryGetValue(toEdgeId, out var targetStatus) || !targetStatus.IsHealthy)
            return new FailoverResult { Success = false, Error = "Target edge node is not healthy" };

        // Perform failover
        if (_healthStatus.TryGetValue(fromEdgeId, out var sourceStatus))
            sourceStatus.IsHealthy = false;

        FailoverTriggered?.Invoke(this, new EdgeFailoverEventArgs
        {
            FromEdgeId = fromEdgeId,
            ToEdgeId = toEdgeId,
            Reason = "Manual failover"
        });

        return new FailoverResult { Success = true, NewActiveEdge = toEdgeId };
    }

    private double CalculateHealthScore(EdgeHealthReport report)
    {
        var latencyScore = Math.Max(0, 1 - report.LatencyMs / 500);
        var errorScore = Math.Max(0, 1 - report.ErrorRate);
        var uptimeScore = report.UptimeSeconds > 86400 ? 1.0 : report.UptimeSeconds / 86400.0;
        return (latencyScore + errorScore + uptimeScore) / 3.0;
    }

    private void CheckHealth(object? state)
    {
        if (_disposed) return;

        var staleThreshold = DateTime.UtcNow.AddSeconds(-30);
        foreach (var status in _healthStatus.Values)
        {
            if (status.LastReportedAt < staleThreshold)
                status.IsHealthy = false;
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _healthCheckTimer.Dispose();
        return ValueTask.CompletedTask;
    }
}

#endregion

#region EM2: Managed Services Platform

/// <summary>
/// Provisions services for tenant onboarding.
/// </summary>
public sealed class ServiceProvisioner
{
    private readonly ConcurrentDictionary<string, ProvisionedService> _services = new();

    public async Task<ProvisioningResult> ProvisionTenantAsync(TenantProvisionRequest request, CancellationToken ct = default)
    {
        var tenantId = request.TenantId ?? Guid.NewGuid().ToString("N");

        var service = new ProvisionedService
        {
            ServiceId = Guid.NewGuid().ToString("N"),
            TenantId = tenantId,
            Plan = request.Plan,
            Status = ServiceStatus.Provisioning,
            CreatedAt = DateTime.UtcNow,
            Configuration = request.Configuration ?? new()
        };

        _services[service.ServiceId] = service;

        // Simulate provisioning steps
        await Task.Delay(100, ct);
        service.Status = ServiceStatus.Active;
        service.ActivatedAt = DateTime.UtcNow;
        service.Endpoint = $"https://{tenantId}.datawarehouse.cloud";

        return new ProvisioningResult
        {
            Success = true,
            ServiceId = service.ServiceId,
            TenantId = tenantId,
            Endpoint = service.Endpoint
        };
    }

    public async Task<bool> DeprovisionAsync(string serviceId, CancellationToken ct = default)
    {
        if (_services.TryGetValue(serviceId, out var service))
        {
            service.Status = ServiceStatus.Deprovisioning;
            await Task.Delay(50, ct);
            service.Status = ServiceStatus.Terminated;
            service.TerminatedAt = DateTime.UtcNow;
            return true;
        }
        return false;
    }

    public ProvisionedService? GetService(string serviceId) =>
        _services.TryGetValue(serviceId, out var service) ? service : null;

    public IReadOnlyList<ProvisionedService> GetTenantServices(string tenantId) =>
        _services.Values.Where(s => s.TenantId == tenantId).ToList();
}

/// <summary>
/// Manages tenant lifecycle operations.
/// </summary>
public sealed class TenantLifecycleManager
{
    private readonly ConcurrentDictionary<string, TenantInfo> _tenants = new();
    private readonly ServiceProvisioner _provisioner;

    public event EventHandler<TenantEventArgs>? TenantCreated;
    public event EventHandler<TenantEventArgs>? TenantSuspended;
    public event EventHandler<TenantEventArgs>? TenantDeleted;

    public TenantLifecycleManager(ServiceProvisioner provisioner)
    {
        _provisioner = provisioner;
    }

    public async Task<TenantInfo> CreateTenantAsync(CreateTenantRequest request, CancellationToken ct = default)
    {
        var tenant = new TenantInfo
        {
            TenantId = Guid.NewGuid().ToString("N"),
            Name = request.Name,
            Email = request.Email,
            Plan = request.Plan,
            Status = TenantStatus.Active,
            CreatedAt = DateTime.UtcNow
        };

        _tenants[tenant.TenantId] = tenant;

        // Provision resources
        var provisionResult = await _provisioner.ProvisionTenantAsync(new TenantProvisionRequest
        {
            TenantId = tenant.TenantId,
            Plan = request.Plan
        }, ct);

        tenant.ServiceId = provisionResult.ServiceId;
        tenant.Endpoint = provisionResult.Endpoint;

        TenantCreated?.Invoke(this, new TenantEventArgs { Tenant = tenant });
        return tenant;
    }

    public async Task<bool> SuspendTenantAsync(string tenantId, string reason, CancellationToken ct = default)
    {
        if (_tenants.TryGetValue(tenantId, out var tenant))
        {
            tenant.Status = TenantStatus.Suspended;
            tenant.SuspendedAt = DateTime.UtcNow;
            tenant.SuspensionReason = reason;

            TenantSuspended?.Invoke(this, new TenantEventArgs { Tenant = tenant });
            return true;
        }
        return false;
    }

    public async Task<bool> ReactivateTenantAsync(string tenantId, CancellationToken ct = default)
    {
        if (_tenants.TryGetValue(tenantId, out var tenant) && tenant.Status == TenantStatus.Suspended)
        {
            tenant.Status = TenantStatus.Active;
            tenant.SuspendedAt = null;
            tenant.SuspensionReason = null;
            return true;
        }
        return false;
    }

    public async Task<bool> DeleteTenantAsync(string tenantId, CancellationToken ct = default)
    {
        if (_tenants.TryGetValue(tenantId, out var tenant))
        {
            tenant.Status = TenantStatus.PendingDeletion;

            if (!string.IsNullOrEmpty(tenant.ServiceId))
                await _provisioner.DeprovisionAsync(tenant.ServiceId, ct);

            tenant.Status = TenantStatus.Deleted;
            tenant.DeletedAt = DateTime.UtcNow;

            TenantDeleted?.Invoke(this, new TenantEventArgs { Tenant = tenant });
            return true;
        }
        return false;
    }

    public TenantInfo? GetTenant(string tenantId) =>
        _tenants.TryGetValue(tenantId, out var tenant) ? tenant : null;

    public IReadOnlyList<TenantInfo> ListTenants(TenantStatus? statusFilter = null)
    {
        var query = _tenants.Values.AsEnumerable();
        if (statusFilter.HasValue)
            query = query.Where(t => t.Status == statusFilter);
        return query.ToList();
    }
}

/// <summary>
/// Tracks resource usage for metering.
/// </summary>
public sealed class UsageMeteringService : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, UsageRecord> _currentPeriod = new();
    private readonly ConcurrentDictionary<string, List<UsageRecord>> _history = new();
    private readonly Timer _aggregationTimer;
    private volatile bool _disposed;

    public UsageMeteringService()
    {
        _aggregationTimer = new Timer(AggregateUsage, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public void RecordUsage(string tenantId, UsageMetric metric, double value)
    {
        var record = _currentPeriod.GetOrAdd(tenantId, _ => new UsageRecord
        {
            TenantId = tenantId,
            PeriodStart = DateTime.UtcNow.Date,
            PeriodEnd = DateTime.UtcNow.Date.AddDays(1)
        });

        record.Metrics.AddOrUpdate(metric.ToString(), value, (_, existing) => existing + value);
    }

    public UsageReport GetCurrentUsage(string tenantId)
    {
        if (!_currentPeriod.TryGetValue(tenantId, out var record))
            return new UsageReport { TenantId = tenantId };

        return new UsageReport
        {
            TenantId = tenantId,
            PeriodStart = record.PeriodStart,
            PeriodEnd = record.PeriodEnd,
            StorageBytes = (long)record.Metrics.GetValueOrDefault("StorageBytes", 0),
            RequestCount = (long)record.Metrics.GetValueOrDefault("Requests", 0),
            BandwidthBytes = (long)record.Metrics.GetValueOrDefault("Bandwidth", 0),
            ComputeMinutes = record.Metrics.GetValueOrDefault("ComputeMinutes", 0)
        };
    }

    public IReadOnlyList<UsageReport> GetUsageHistory(string tenantId, int periods = 12)
    {
        if (!_history.TryGetValue(tenantId, out var history))
            return new List<UsageReport>();

        return history.TakeLast(periods).Select(r => new UsageReport
        {
            TenantId = tenantId,
            PeriodStart = r.PeriodStart,
            PeriodEnd = r.PeriodEnd,
            StorageBytes = (long)r.Metrics.GetValueOrDefault("StorageBytes", 0),
            RequestCount = (long)r.Metrics.GetValueOrDefault("Requests", 0),
            BandwidthBytes = (long)r.Metrics.GetValueOrDefault("Bandwidth", 0),
            ComputeMinutes = r.Metrics.GetValueOrDefault("ComputeMinutes", 0)
        }).ToList();
    }

    private void AggregateUsage(object? state)
    {
        if (_disposed) return;

        foreach (var kvp in _currentPeriod)
        {
            if (kvp.Value.PeriodEnd <= DateTime.UtcNow)
            {
                var history = _history.GetOrAdd(kvp.Key, _ => new List<UsageRecord>());
                lock (history) history.Add(kvp.Value);

                _currentPeriod.TryRemove(kvp.Key, out _);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _aggregationTimer.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Integration hooks for billing systems.
/// </summary>
public sealed class BillingIntegration
{
    private readonly ConcurrentDictionary<string, BillingConfig> _configs = new();
    private readonly HttpClient _httpClient = new();

    public void ConfigureBillingProvider(string tenantId, BillingConfig config)
    {
        _configs[tenantId] = config;
    }

    public async Task<InvoiceResult> GenerateInvoiceAsync(string tenantId, UsageReport usage, CancellationToken ct = default)
    {
        if (!_configs.TryGetValue(tenantId, out var config))
            return new InvoiceResult { Success = false, Error = "Billing not configured" };

        var lineItems = new List<InvoiceLineItem>
        {
            new() { Description = "Storage", Quantity = usage.StorageBytes / (1024.0 * 1024 * 1024), UnitPrice = config.StoragePricePerGb, Unit = "GB" },
            new() { Description = "Requests", Quantity = usage.RequestCount / 10000.0, UnitPrice = config.RequestPricePer10k, Unit = "10K requests" },
            new() { Description = "Bandwidth", Quantity = usage.BandwidthBytes / (1024.0 * 1024 * 1024), UnitPrice = config.BandwidthPricePerGb, Unit = "GB" }
        };

        var total = lineItems.Sum(i => i.Quantity * i.UnitPrice);

        return new InvoiceResult
        {
            Success = true,
            InvoiceId = Guid.NewGuid().ToString("N"),
            TenantId = tenantId,
            PeriodStart = usage.PeriodStart,
            PeriodEnd = usage.PeriodEnd,
            LineItems = lineItems,
            Total = total,
            Currency = config.Currency
        };
    }

    public async Task<bool> SubmitToStripeAsync(InvoiceResult invoice, CancellationToken ct = default)
    {
        if (!_configs.TryGetValue(invoice.TenantId, out var config) || config.Provider != BillingProvider.Stripe)
            return false;

        // Would call Stripe API
        return true;
    }

    public async Task<bool> SubmitToAwsMarketplaceAsync(InvoiceResult invoice, CancellationToken ct = default)
    {
        if (!_configs.TryGetValue(invoice.TenantId, out var config) || config.Provider != BillingProvider.AwsMarketplace)
            return false;

        // Would call AWS Marketplace Metering API
        return true;
    }
}

#endregion

#region Types

public sealed class EdgeDetectionConfig
{
    public double EdgeLatencyThresholdMs { get; set; } = 20;
    public double RegionalLatencyThresholdMs { get; set; } = 100;
}

public sealed class EdgeNodeInfo
{
    public string NodeId { get; init; } = string.Empty;
    public double LatencyMs { get; set; }
    public string Location { get; init; } = string.Empty;
    public EdgeClassification Classification { get; set; }
    public DateTime DetectedAt { get; init; }
    public DateTime LastSeenAt { get; set; }
}

public enum EdgeClassification { Edge, Regional, Origin, Remote }

public sealed class EdgeNodeEventArgs : EventArgs
{
    public EdgeNodeInfo Node { get; init; } = null!;
}

public sealed class EdgeOriginSyncConfig
{
    public TimeSpan DefaultTtl { get; set; } = TimeSpan.FromHours(1);
    public int MaxConcurrentSyncs { get; set; } = 10;
}

public sealed class SyncTask
{
    public string ObjectId { get; init; } = string.Empty;
    public byte[] Data { get; init; } = Array.Empty<byte>();
    public SyncPriority Priority { get; init; }
    public DateTime QueuedAt { get; init; }
}

public enum SyncPriority { Low, Normal, High, Critical }

public sealed class EdgeCacheEntry
{
    public string ObjectId { get; init; } = string.Empty;
    public string EdgeNodeId { get; init; } = string.Empty;
    public byte[] Data { get; init; } = Array.Empty<byte>();
    public DateTime SyncedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
}

public sealed class SyncCompletedEventArgs : EventArgs
{
    public string ObjectId { get; init; } = string.Empty;
    public string EdgeNodeId { get; init; } = string.Empty;
    public long BytesSynced { get; init; }
}

public sealed class InvalidationMessage
{
    public string ObjectId { get; init; } = string.Empty;
    public InvalidationReason Reason { get; init; }
    public DateTime Timestamp { get; init; }
    public string Topic { get; init; } = string.Empty;
}

public enum InvalidationReason { Updated, Deleted, Expired, Manual }

public sealed class EdgeHealthConfig { public double HealthThreshold { get; set; } = 0.7; }

public sealed class EdgeHealthStatus
{
    public string EdgeNodeId { get; init; } = string.Empty;
    public EdgeHealthReport? LastReport { get; set; }
    public DateTime LastReportedAt { get; set; }
    public double HealthScore { get; set; }
    public bool IsHealthy { get; set; }
}

public sealed class EdgeHealthReport
{
    public double LatencyMs { get; init; }
    public double ErrorRate { get; init; }
    public long UptimeSeconds { get; init; }
    public double CpuUsage { get; init; }
    public double MemoryUsage { get; init; }
}

public sealed class EdgeFailoverEventArgs : EventArgs
{
    public string FromEdgeId { get; init; } = string.Empty;
    public string ToEdgeId { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

public sealed class FailoverResult
{
    public bool Success { get; init; }
    public string? NewActiveEdge { get; init; }
    public string? Error { get; init; }
}

public sealed class TenantProvisionRequest
{
    public string? TenantId { get; init; }
    public ServicePlan Plan { get; init; }
    public Dictionary<string, string>? Configuration { get; init; }
}

public sealed class ProvisionedService
{
    public string ServiceId { get; init; } = string.Empty;
    public string TenantId { get; init; } = string.Empty;
    public ServicePlan Plan { get; init; }
    public ServiceStatus Status { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime? ActivatedAt { get; set; }
    public DateTime? TerminatedAt { get; set; }
    public string? Endpoint { get; set; }
    public Dictionary<string, string> Configuration { get; init; } = new();
}

public enum ServicePlan { Free, Starter, Professional, Enterprise }
public enum ServiceStatus { Provisioning, Active, Suspended, Deprovisioning, Terminated }

public sealed class ProvisioningResult
{
    public bool Success { get; init; }
    public string ServiceId { get; init; } = string.Empty;
    public string TenantId { get; init; } = string.Empty;
    public string? Endpoint { get; init; }
    public string? Error { get; init; }
}

public sealed class CreateTenantRequest
{
    public required string Name { get; init; }
    public required string Email { get; init; }
    public ServicePlan Plan { get; init; } = ServicePlan.Free;
}

public sealed class TenantInfo
{
    public string TenantId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public ServicePlan Plan { get; init; }
    public TenantStatus Status { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime? SuspendedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string? SuspensionReason { get; set; }
    public string? ServiceId { get; set; }
    public string? Endpoint { get; set; }
}

public enum TenantStatus { Active, Suspended, PendingDeletion, Deleted }

public sealed class TenantEventArgs : EventArgs
{
    public TenantInfo Tenant { get; init; } = null!;
}

public sealed class UsageRecord
{
    public string TenantId { get; init; } = string.Empty;
    public DateTime PeriodStart { get; init; }
    public DateTime PeriodEnd { get; init; }
    public ConcurrentDictionary<string, double> Metrics { get; } = new();
}

public enum UsageMetric { StorageBytes, Requests, Bandwidth, ComputeMinutes }

public sealed class UsageReport
{
    public string TenantId { get; init; } = string.Empty;
    public DateTime PeriodStart { get; init; }
    public DateTime PeriodEnd { get; init; }
    public long StorageBytes { get; init; }
    public long RequestCount { get; init; }
    public long BandwidthBytes { get; init; }
    public double ComputeMinutes { get; init; }
}

public sealed class BillingConfig
{
    public BillingProvider Provider { get; init; }
    public string ApiKey { get; init; } = string.Empty;
    public double StoragePricePerGb { get; init; } = 0.023;
    public double RequestPricePer10k { get; init; } = 0.004;
    public double BandwidthPricePerGb { get; init; } = 0.09;
    public string Currency { get; init; } = "USD";
}

public enum BillingProvider { Stripe, AwsMarketplace, AzureMarketplace, Manual }

public sealed class InvoiceResult
{
    public bool Success { get; init; }
    public string InvoiceId { get; init; } = string.Empty;
    public string TenantId { get; init; } = string.Empty;
    public DateTime PeriodStart { get; init; }
    public DateTime PeriodEnd { get; init; }
    public List<InvoiceLineItem> LineItems { get; init; } = new();
    public double Total { get; init; }
    public string Currency { get; init; } = string.Empty;
    public string? Error { get; init; }
}

public sealed class InvoiceLineItem
{
    public string Description { get; init; } = string.Empty;
    public double Quantity { get; init; }
    public double UnitPrice { get; init; }
    public string Unit { get; init; } = string.Empty;
}

#endregion
