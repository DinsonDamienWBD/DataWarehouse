using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateMultiCloud.Strategies.Failover;

/// <summary>
/// 118.3: Cloud Provider Failover Strategies
/// Automatic and manual failover between cloud providers.
/// </summary>

/// <summary>
/// Automatic failover based on health checks and SLA thresholds.
/// </summary>
public sealed class AutomaticCloudFailoverStrategy : MultiCloudStrategyBase
{
    private readonly BoundedDictionary<string, ProviderHealthState> _providerHealth = new BoundedDictionary<string, ProviderHealthState>(1000);
    private readonly BoundedDictionary<string, FailoverConfiguration> _configs = new BoundedDictionary<string, FailoverConfiguration>(1000);
    private string? _activeProvider;

    public override string StrategyId => "failover-automatic";
    public override string StrategyName => "Automatic Cloud Failover";
    public override string Category => "Failover";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Automatic failover between cloud providers based on health checks and SLA thresholds",
        Category = Category,
        SupportsCrossCloudReplication = false,
        SupportsAutomaticFailover = true,
        TypicalLatencyOverheadMs = 5.0,
        MemoryFootprint = "Low"
    };

    /// <summary>Active provider ID.</summary>
    public string? ActiveProvider => _activeProvider;

    /// <summary>Configures failover for a provider.</summary>
    public void Configure(string providerId, FailoverConfiguration config)
    {
        _configs[providerId] = config;
        _providerHealth[providerId] = new ProviderHealthState
        {
            ProviderId = providerId,
            IsHealthy = true,
            LastCheck = DateTimeOffset.UtcNow
        };

        _activeProvider ??= providerId;
    }

    /// <summary>Reports health check result.</summary>
    public FailoverDecision ReportHealthCheck(string providerId, bool isHealthy, double latencyMs, double errorRate)
    {
        if (!_providerHealth.TryGetValue(providerId, out var state))
            return new FailoverDecision { ShouldFailover = false };

        state.IsHealthy = isHealthy;
        state.LastLatencyMs = latencyMs;
        state.LastErrorRate = errorRate;
        state.LastCheck = DateTimeOffset.UtcNow;

        if (!isHealthy) state.ConsecutiveFailures++;
        else state.ConsecutiveFailures = 0;

        // Check if failover is needed
        if (_configs.TryGetValue(providerId, out var config) && providerId == _activeProvider)
        {
            var shouldFailover = !isHealthy && state.ConsecutiveFailures >= config.FailureThreshold;

            if (shouldFailover)
            {
                var nextProvider = FindNextHealthyProvider(providerId);
                if (nextProvider != null)
                {
                    var previousProvider = _activeProvider;
                    _activeProvider = nextProvider;
                    RecordSuccess();

                    return new FailoverDecision
                    {
                        ShouldFailover = true,
                        FromProvider = previousProvider,
                        ToProvider = nextProvider,
                        Reason = $"Provider {providerId} failed {state.ConsecutiveFailures} times"
                    };
                }
            }
        }

        return new FailoverDecision { ShouldFailover = false };
    }

    private string? FindNextHealthyProvider(string excludeProvider)
    {
        return _providerHealth.Values
            .Where(p => p.ProviderId != excludeProvider && p.IsHealthy)
            .OrderBy(p => _configs.TryGetValue(p.ProviderId, out var c) ? c.Priority : int.MaxValue)
            .Select(p => p.ProviderId)
            .FirstOrDefault();
    }

    protected override string? GetCurrentState() => $"Active: {_activeProvider ?? "none"}, Providers: {_providerHealth.Count}";
}

/// <summary>
/// Active-active multi-cloud with automatic traffic distribution.
/// </summary>
public sealed class ActiveActiveCloudStrategy : MultiCloudStrategyBase
{
    private readonly BoundedDictionary<string, ProviderWeight> _weights = new BoundedDictionary<string, ProviderWeight>(1000);
    // Cat 2 (finding 3629): Random.Shared is thread-safe (.NET 6+); instance Random is not.
    private readonly Random _random = Random.Shared;

    public override string StrategyId => "failover-active-active";
    public override string StrategyName => "Active-Active Multi-Cloud";
    public override string Category => "Failover";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Distributes traffic across multiple active cloud providers with weighted routing",
        Category = Category,
        SupportsAutomaticFailover = true,
        SupportsCostOptimization = true,
        TypicalLatencyOverheadMs = 2.0,
        MemoryFootprint = "Low"
    };

    /// <summary>Configures provider weight.</summary>
    public void SetWeight(string providerId, int weight, bool isHealthy = true)
    {
        _weights[providerId] = new ProviderWeight
        {
            ProviderId = providerId,
            Weight = weight,
            IsHealthy = isHealthy
        };
    }

    /// <summary>Selects provider based on weights.</summary>
    public string SelectProvider()
    {
        var healthyProviders = _weights.Values.Where(p => p.IsHealthy).ToList();
        if (!healthyProviders.Any())
            throw new InvalidOperationException("No healthy providers available");

        var totalWeight = healthyProviders.Sum(p => p.Weight);
        var random = _random.Next(totalWeight);

        var cumulative = 0;
        foreach (var provider in healthyProviders)
        {
            cumulative += provider.Weight;
            if (random < cumulative)
            {
                RecordSuccess();
                return provider.ProviderId;
            }
        }

        return healthyProviders.Last().ProviderId;
    }

    /// <summary>Gets current traffic distribution.</summary>
    public IReadOnlyDictionary<string, double> GetDistribution()
    {
        var totalWeight = _weights.Values.Where(p => p.IsHealthy).Sum(p => p.Weight);
        return _weights.Values
            .Where(p => p.IsHealthy)
            .ToDictionary(p => p.ProviderId, p => (double)p.Weight / totalWeight * 100);
    }

    protected override string? GetCurrentState() => $"Providers: {_weights.Count}";
}

/// <summary>
/// Active-passive with standby cloud provider.
/// </summary>
public sealed class ActivePassiveCloudStrategy : MultiCloudStrategyBase
{
    private string? _primaryProvider;
    private readonly List<string> _standbyProviders = new();
    private readonly BoundedDictionary<string, bool> _providerHealth = new BoundedDictionary<string, bool>(1000);

    public override string StrategyId => "failover-active-passive";
    public override string StrategyName => "Active-Passive Multi-Cloud";
    public override string Category => "Failover";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Primary cloud with standby providers for disaster recovery",
        Category = Category,
        SupportsAutomaticFailover = true,
        SupportsCostOptimization = true,
        TypicalLatencyOverheadMs = 1.0,
        MemoryFootprint = "Low"
    };

    /// <summary>Primary provider ID.</summary>
    public string? PrimaryProvider => _primaryProvider;

    /// <summary>Sets primary provider.</summary>
    public void SetPrimary(string providerId)
    {
        _primaryProvider = providerId;
        _providerHealth[providerId] = true;
    }

    /// <summary>Adds standby provider.</summary>
    public void AddStandby(string providerId)
    {
        if (!_standbyProviders.Contains(providerId))
        {
            _standbyProviders.Add(providerId);
            _providerHealth[providerId] = true;
        }
    }

    /// <summary>Promotes standby to primary.</summary>
    public FailoverResult PromoteStandby(string? preferredStandby = null)
    {
        var standby = preferredStandby ?? _standbyProviders.FirstOrDefault(s => _providerHealth.GetValueOrDefault(s, false));

        if (standby == null)
        {
            RecordFailure();
            return new FailoverResult { Success = false, ErrorMessage = "No healthy standby available" };
        }

        var previousPrimary = _primaryProvider;
        _primaryProvider = standby;
        _standbyProviders.Remove(standby);

        if (previousPrimary != null)
            _standbyProviders.Add(previousPrimary);

        RecordSuccess();
        return new FailoverResult
        {
            Success = true,
            PreviousPrimary = previousPrimary,
            NewPrimary = standby
        };
    }

    /// <summary>Updates provider health.</summary>
    public void UpdateHealth(string providerId, bool isHealthy)
    {
        _providerHealth[providerId] = isHealthy;
    }

    protected override string? GetCurrentState() => $"Primary: {_primaryProvider ?? "none"}, Standby: {_standbyProviders.Count}";
}

/// <summary>
/// Health-based routing with circuit breaker pattern.
/// </summary>
public sealed class HealthBasedRoutingStrategy : MultiCloudStrategyBase
{
    private readonly BoundedDictionary<string, CircuitBreakerState> _circuits = new BoundedDictionary<string, CircuitBreakerState>(1000);

    public override string StrategyId => "failover-health-routing";
    public override string StrategyName => "Health-Based Routing";
    public override string Category => "Failover";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Routes requests based on real-time health with circuit breaker protection",
        Category = Category,
        SupportsAutomaticFailover = true,
        TypicalLatencyOverheadMs = 3.0,
        MemoryFootprint = "Low"
    };

    /// <summary>Registers a provider circuit.</summary>
    public void RegisterProvider(string providerId, int failureThreshold = 5, TimeSpan? resetTimeout = null)
    {
        _circuits[providerId] = new CircuitBreakerState
        {
            ProviderId = providerId,
            FailureThreshold = failureThreshold,
            ResetTimeout = resetTimeout ?? TimeSpan.FromSeconds(30),
            State = CircuitState.Closed
        };
    }

    /// <summary>Gets available provider.</summary>
    public string? GetAvailableProvider()
    {
        var available = _circuits.Values
            .Where(c => c.State != CircuitState.Open ||
                        (c.OpenedAt.HasValue && DateTimeOffset.UtcNow - c.OpenedAt.Value > c.ResetTimeout))
            .OrderBy(c => c.FailureCount)
            .FirstOrDefault();

        return available?.ProviderId;
    }

    /// <summary>Reports success.</summary>
    public void ReportSuccess(string providerId)
    {
        if (_circuits.TryGetValue(providerId, out var circuit))
        {
            circuit.FailureCount = 0;
            circuit.State = CircuitState.Closed;
            circuit.LastSuccess = DateTimeOffset.UtcNow;
        }
        RecordSuccess();
    }

    /// <summary>Reports failure.</summary>
    public void ReportFailure(string providerId)
    {
        if (_circuits.TryGetValue(providerId, out var circuit))
        {
            circuit.FailureCount++;
            circuit.LastFailure = DateTimeOffset.UtcNow;

            if (circuit.FailureCount >= circuit.FailureThreshold)
            {
                circuit.State = CircuitState.Open;
                circuit.OpenedAt = DateTimeOffset.UtcNow;
            }
        }
        RecordFailure();
    }

    /// <summary>Gets circuit status.</summary>
    public IReadOnlyDictionary<string, CircuitState> GetCircuitStatus()
    {
        return _circuits.ToDictionary(c => c.Key, c => c.Value.State);
    }

    protected override string? GetCurrentState()
    {
        var open = _circuits.Values.Count(c => c.State == CircuitState.Open);
        return $"Circuits: {_circuits.Count}, Open: {open}";
    }
}

/// <summary>
/// DNS-based failover with multi-cloud endpoints.
/// </summary>
public sealed class DnsFailoverStrategy : MultiCloudStrategyBase
{
    private readonly BoundedDictionary<string, DnsRecord> _records = new BoundedDictionary<string, DnsRecord>(1000);

    public override string StrategyId => "failover-dns";
    public override string StrategyName => "DNS-Based Failover";
    public override string Category => "Failover";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "DNS failover with health-checked endpoints across cloud providers",
        Category = Category,
        SupportsAutomaticFailover = true,
        TypicalLatencyOverheadMs = 1.0,
        MemoryFootprint = "Low"
    };

    /// <summary>Registers a DNS record.</summary>
    public void RegisterRecord(string hostname, string providerId, string endpoint, int priority = 100, int ttl = 60)
    {
        var key = $"{hostname}:{providerId}";
        _records[key] = new DnsRecord
        {
            Hostname = hostname,
            ProviderId = providerId,
            Endpoint = endpoint,
            Priority = priority,
            Ttl = ttl,
            IsHealthy = true
        };
    }

    /// <summary>Resolves hostname to healthy endpoint.</summary>
    public string? Resolve(string hostname)
    {
        var records = _records.Values
            .Where(r => r.Hostname == hostname && r.IsHealthy)
            .OrderBy(r => r.Priority)
            .ToList();

        return records.FirstOrDefault()?.Endpoint;
    }

    /// <summary>Updates health for a record.</summary>
    public void UpdateHealth(string hostname, string providerId, bool isHealthy)
    {
        var key = $"{hostname}:{providerId}";
        if (_records.TryGetValue(key, out var record))
        {
            record.IsHealthy = isHealthy;
            record.LastCheck = DateTimeOffset.UtcNow;
        }
    }

    protected override string? GetCurrentState() => $"Records: {_records.Count}";
}

/// <summary>
/// Latency-based failover selecting lowest-latency provider.
/// </summary>
public sealed class LatencyBasedFailoverStrategy : MultiCloudStrategyBase
{
    private readonly BoundedDictionary<string, LatencyMeasurement> _latencies = new BoundedDictionary<string, LatencyMeasurement>(1000);

    public override string StrategyId => "failover-latency";
    public override string StrategyName => "Latency-Based Failover";
    public override string Category => "Failover";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Selects provider with lowest measured latency for optimal performance",
        Category = Category,
        SupportsAutomaticFailover = true,
        TypicalLatencyOverheadMs = 0.5,
        MemoryFootprint = "Low"
    };

    /// <summary>Records latency measurement.</summary>
    public void RecordLatency(string providerId, double latencyMs)
    {
        _latencies.AddOrUpdate(
            providerId,
            new LatencyMeasurement
            {
                ProviderId = providerId,
                CurrentLatencyMs = latencyMs,
                AverageLatencyMs = latencyMs,
                SampleCount = 1,
                LastUpdate = DateTimeOffset.UtcNow
            },
            (_, existing) =>
            {
                existing.CurrentLatencyMs = latencyMs;
                existing.AverageLatencyMs = (existing.AverageLatencyMs * existing.SampleCount + latencyMs) / (existing.SampleCount + 1);
                existing.SampleCount++;
                existing.LastUpdate = DateTimeOffset.UtcNow;
                return existing;
            });
    }

    /// <summary>Gets provider with lowest latency.</summary>
    public string? GetLowestLatencyProvider()
    {
        var best = _latencies.Values
            .OrderBy(l => l.AverageLatencyMs)
            .FirstOrDefault();

        if (best != null) RecordSuccess();
        return best?.ProviderId;
    }

    /// <summary>Gets latency stats.</summary>
    public IReadOnlyDictionary<string, double> GetLatencyStats()
    {
        return _latencies.ToDictionary(l => l.Key, l => l.Value.AverageLatencyMs);
    }

    protected override string? GetCurrentState()
    {
        var best = _latencies.Values.MinBy(l => l.AverageLatencyMs);
        return $"Best: {best?.ProviderId ?? "none"} ({best?.AverageLatencyMs:F1}ms)";
    }
}

#region Supporting Types

public sealed class ProviderHealthState
{
    public required string ProviderId { get; init; }
    public bool IsHealthy { get; set; }
    public int ConsecutiveFailures { get; set; }
    public double LastLatencyMs { get; set; }
    public double LastErrorRate { get; set; }
    public DateTimeOffset LastCheck { get; set; }
}

public sealed class FailoverConfiguration
{
    public int Priority { get; init; } = 100;
    public int FailureThreshold { get; init; } = 3;
    public TimeSpan HealthCheckInterval { get; init; } = TimeSpan.FromSeconds(30);
    public double MaxLatencyMs { get; init; } = 1000;
    public double MaxErrorRate { get; init; } = 0.05;
}

public sealed class FailoverDecision
{
    public bool ShouldFailover { get; init; }
    public string? FromProvider { get; init; }
    public string? ToProvider { get; init; }
    public string? Reason { get; init; }
}

public sealed class ProviderWeight
{
    public required string ProviderId { get; init; }
    public int Weight { get; set; }
    public bool IsHealthy { get; set; }
}

public sealed class FailoverResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string? PreviousPrimary { get; init; }
    public string? NewPrimary { get; init; }
}

public enum CircuitState { Closed, Open, HalfOpen }

public sealed class CircuitBreakerState
{
    public required string ProviderId { get; init; }
    public int FailureThreshold { get; init; }
    public TimeSpan ResetTimeout { get; init; }
    public CircuitState State { get; set; }
    public int FailureCount { get; set; }
    public DateTimeOffset? OpenedAt { get; set; }
    public DateTimeOffset? LastSuccess { get; set; }
    public DateTimeOffset? LastFailure { get; set; }
}

public sealed class DnsRecord
{
    public required string Hostname { get; init; }
    public required string ProviderId { get; init; }
    public required string Endpoint { get; init; }
    public int Priority { get; init; }
    public int Ttl { get; init; }
    public bool IsHealthy { get; set; }
    public DateTimeOffset LastCheck { get; set; }
}

public sealed class LatencyMeasurement
{
    public required string ProviderId { get; init; }
    public double CurrentLatencyMs { get; set; }
    public double AverageLatencyMs { get; set; }
    public int SampleCount { get; set; }
    public DateTimeOffset LastUpdate { get; set; }
}

#endregion
