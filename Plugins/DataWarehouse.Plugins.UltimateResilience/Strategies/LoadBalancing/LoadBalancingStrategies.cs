using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateResilience.Strategies.LoadBalancing;

/// <summary>
/// Represents a backend endpoint for load balancing.
/// </summary>
public sealed class LoadBalancerEndpoint
{
    /// <summary>Unique identifier for this endpoint.</summary>
    public required string EndpointId { get; init; }

    /// <summary>Display name.</summary>
    public string? Name { get; init; }

    /// <summary>Endpoint address (URL, host:port, etc.).</summary>
    public required string Address { get; init; }

    /// <summary>Weight for weighted algorithms (higher = more traffic).</summary>
    public int Weight { get; set; } = 1;

    /// <summary>Current health status.</summary>
    public bool IsHealthy { get; set; } = true;

    /// <summary>Current active connection count (backing field for thread-safe access).</summary>
    private int _activeConnections;

    /// <summary>Current active connection count.</summary>
    public int ActiveConnections
    {
        get => _activeConnections;
        set => _activeConnections = value;
    }

    /// <summary>Increments connection count atomically.</summary>
    public int IncrementConnections() => Interlocked.Increment(ref _activeConnections);

    /// <summary>Decrements connection count atomically.</summary>
    public int DecrementConnections() => Interlocked.Decrement(ref _activeConnections);

    /// <summary>Last response time.</summary>
    public TimeSpan LastResponseTime { get; set; }

    /// <summary>Metadata for the endpoint.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Base class for load balancing strategies.
/// </summary>
public abstract class LoadBalancingStrategyBase : ResilienceStrategyBase
{
    protected readonly List<LoadBalancerEndpoint> _endpoints = new();
    protected readonly object _endpointsLock = new();

    public override string Category => "LoadBalancing";

    /// <summary>
    /// Adds an endpoint to the load balancer.
    /// </summary>
    public virtual void AddEndpoint(LoadBalancerEndpoint endpoint)
    {
        lock (_endpointsLock)
        {
            _endpoints.Add(endpoint);
        }
    }

    /// <summary>
    /// Removes an endpoint from the load balancer.
    /// </summary>
    public virtual bool RemoveEndpoint(string endpointId)
    {
        lock (_endpointsLock)
        {
            return _endpoints.RemoveAll(e => e.EndpointId == endpointId) > 0;
        }
    }

    /// <summary>
    /// Gets all endpoints.
    /// </summary>
    public virtual IReadOnlyList<LoadBalancerEndpoint> GetEndpoints()
    {
        lock (_endpointsLock)
        {
            return _endpoints.ToList();
        }
    }

    /// <summary>
    /// Gets only healthy endpoints.
    /// </summary>
    protected virtual IReadOnlyList<LoadBalancerEndpoint> GetHealthyEndpoints()
    {
        lock (_endpointsLock)
        {
            return _endpoints.Where(e => e.IsHealthy).ToList();
        }
    }

    /// <summary>
    /// Selects the next endpoint for a request.
    /// </summary>
    public abstract LoadBalancerEndpoint? SelectEndpoint(ResilienceContext? context = null);
}

/// <summary>
/// Round-robin load balancing strategy.
/// </summary>
public sealed class RoundRobinLoadBalancingStrategy : LoadBalancingStrategyBase
{
    private int _currentIndex = -1;

    public override string StrategyId => "lb-round-robin";
    public override string StrategyName => "Round Robin Load Balancer";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Round Robin Load Balancer",
        Description = "Distributes requests evenly across all healthy endpoints in a circular fashion",
        Category = "LoadBalancing",
        ProvidesFaultTolerance = false,
        ProvidesLoadManagement = true,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.01,
        MemoryFootprint = "Low"
    };

    public override LoadBalancerEndpoint? SelectEndpoint(ResilienceContext? context = null)
    {
        var healthy = GetHealthyEndpoints();
        if (healthy.Count == 0) return null;

        var index = Interlocked.Increment(ref _currentIndex) % healthy.Count;
        return healthy[index];
    }

    protected override Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        // Load balancing strategies typically wrap operations that use SelectEndpoint
        return ExecuteWithEndpointAsync(operation, context, cancellationToken);
    }

    private async Task<ResilienceResult<T>> ExecuteWithEndpointAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;
        var endpoint = SelectEndpoint(context);

        if (endpoint == null)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = new InvalidOperationException("No healthy endpoints available"),
                Attempts = 0,
                TotalDuration = TimeSpan.Zero
            };
        }

        try
        {
            context?.Data.TryAdd("SelectedEndpoint", endpoint);
            var result = await operation(cancellationToken);

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata = { ["endpoint"] = endpoint.EndpointId }
            };
        }
        catch (Exception ex)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata = { ["endpoint"] = endpoint.EndpointId }
            };
        }
    }
}

/// <summary>
/// Weighted round-robin load balancing strategy.
/// </summary>
public sealed class WeightedRoundRobinLoadBalancingStrategy : LoadBalancingStrategyBase
{
    private int _currentIndex = -1;
    private int _currentWeight;
    private int _maxWeight;
    private int _gcd = 1;

    public override string StrategyId => "lb-weighted-round-robin";
    public override string StrategyName => "Weighted Round Robin Load Balancer";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Weighted Round Robin Load Balancer",
        Description = "Distributes requests based on endpoint weights - higher weight endpoints receive more traffic",
        Category = "LoadBalancing",
        ProvidesFaultTolerance = false,
        ProvidesLoadManagement = true,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.02,
        MemoryFootprint = "Low"
    };

    public override void AddEndpoint(LoadBalancerEndpoint endpoint)
    {
        base.AddEndpoint(endpoint);
        RecalculateWeights();
    }

    public override bool RemoveEndpoint(string endpointId)
    {
        var removed = base.RemoveEndpoint(endpointId);
        if (removed) RecalculateWeights();
        return removed;
    }

    private void RecalculateWeights()
    {
        lock (_endpointsLock)
        {
            if (_endpoints.Count == 0)
            {
                _maxWeight = 0;
                _gcd = 1;
                return;
            }

            _maxWeight = _endpoints.Max(e => e.Weight);
            _gcd = _endpoints.Aggregate(_endpoints[0].Weight, (acc, e) => Gcd(acc, e.Weight));
        }
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            var temp = b;
            b = a % b;
            a = temp;
        }
        return a;
    }

    public override LoadBalancerEndpoint? SelectEndpoint(ResilienceContext? context = null)
    {
        var healthy = GetHealthyEndpoints();
        if (healthy.Count == 0) return null;

        // Weighted round-robin algorithm
        while (true)
        {
            _currentIndex = (_currentIndex + 1) % healthy.Count;

            if (_currentIndex == 0)
            {
                _currentWeight -= _gcd;
                if (_currentWeight <= 0)
                {
                    _currentWeight = _maxWeight;
                    if (_currentWeight == 0)
                        return null;
                }
            }

            if (healthy[_currentIndex].Weight >= _currentWeight)
            {
                return healthy[_currentIndex];
            }
        }
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;
        var endpoint = SelectEndpoint(context);

        if (endpoint == null)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = new InvalidOperationException("No healthy endpoints available"),
                Attempts = 0,
                TotalDuration = TimeSpan.Zero
            };
        }

        try
        {
            context?.Data.TryAdd("SelectedEndpoint", endpoint);
            var result = await operation(cancellationToken);

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata = { ["endpoint"] = endpoint.EndpointId }
            };
        }
        catch (Exception ex)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata = { ["endpoint"] = endpoint.EndpointId }
            };
        }
    }
}

/// <summary>
/// Least connections load balancing strategy.
/// </summary>
public sealed class LeastConnectionsLoadBalancingStrategy : LoadBalancingStrategyBase
{
    public override string StrategyId => "lb-least-connections";
    public override string StrategyName => "Least Connections Load Balancer";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Least Connections Load Balancer",
        Description = "Routes requests to the endpoint with the fewest active connections for optimal load distribution",
        Category = "LoadBalancing",
        ProvidesFaultTolerance = false,
        ProvidesLoadManagement = true,
        SupportsAdaptiveBehavior = true,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.03,
        MemoryFootprint = "Low"
    };

    public override LoadBalancerEndpoint? SelectEndpoint(ResilienceContext? context = null)
    {
        var healthy = GetHealthyEndpoints();
        if (healthy.Count == 0) return null;

        return healthy.OrderBy(e => e.ActiveConnections).First();
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;
        var endpoint = SelectEndpoint(context);

        if (endpoint == null)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = new InvalidOperationException("No healthy endpoints available"),
                Attempts = 0,
                TotalDuration = TimeSpan.Zero
            };
        }

        try
        {
            endpoint.IncrementConnections();
            context?.Data.TryAdd("SelectedEndpoint", endpoint);

            var result = await operation(cancellationToken);

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata = { ["endpoint"] = endpoint.EndpointId, ["activeConnections"] = endpoint.ActiveConnections }
            };
        }
        catch (Exception ex)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata = { ["endpoint"] = endpoint.EndpointId }
            };
        }
        finally
        {
            endpoint.DecrementConnections();
        }
    }
}

/// <summary>
/// Random load balancing strategy.
/// </summary>
public sealed class RandomLoadBalancingStrategy : LoadBalancingStrategyBase
{
    private readonly Random _random = new();

    public override string StrategyId => "lb-random";
    public override string StrategyName => "Random Load Balancer";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Random Load Balancer",
        Description = "Randomly selects an endpoint for each request - simple but statistically fair distribution",
        Category = "LoadBalancing",
        ProvidesFaultTolerance = false,
        ProvidesLoadManagement = true,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.01,
        MemoryFootprint = "Low"
    };

    public override LoadBalancerEndpoint? SelectEndpoint(ResilienceContext? context = null)
    {
        var healthy = GetHealthyEndpoints();
        if (healthy.Count == 0) return null;

        lock (_random)
        {
            return healthy[_random.Next(healthy.Count)];
        }
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;
        var endpoint = SelectEndpoint(context);

        if (endpoint == null)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = new InvalidOperationException("No healthy endpoints available"),
                Attempts = 0,
                TotalDuration = TimeSpan.Zero
            };
        }

        try
        {
            context?.Data.TryAdd("SelectedEndpoint", endpoint);
            var result = await operation(cancellationToken);

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata = { ["endpoint"] = endpoint.EndpointId }
            };
        }
        catch (Exception ex)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata = { ["endpoint"] = endpoint.EndpointId }
            };
        }
    }
}

/// <summary>
/// IP hash load balancing strategy for session affinity.
/// </summary>
public sealed class IpHashLoadBalancingStrategy : LoadBalancingStrategyBase
{
    public override string StrategyId => "lb-ip-hash";
    public override string StrategyName => "IP Hash Load Balancer";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "IP Hash Load Balancer",
        Description = "Hashes client IP to select endpoint, ensuring same client always reaches same endpoint for session affinity",
        Category = "LoadBalancing",
        ProvidesFaultTolerance = false,
        ProvidesLoadManagement = true,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.02,
        MemoryFootprint = "Low"
    };

    public override LoadBalancerEndpoint? SelectEndpoint(ResilienceContext? context = null)
    {
        var healthy = GetHealthyEndpoints();
        if (healthy.Count == 0) return null;

        var clientKey = context?.Data.TryGetValue("ClientIP", out var ip) == true
            ? ip?.ToString() ?? "default"
            : "default";

        var hash = ComputeHash(clientKey);
        var index = (int)(hash % (uint)healthy.Count);
        return healthy[index];
    }

    private static uint ComputeHash(string key)
    {
        var bytes = Encoding.UTF8.GetBytes(key);
        var hash = MD5.HashData(bytes);
        return BitConverter.ToUInt32(hash, 0);
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;
        var endpoint = SelectEndpoint(context);

        if (endpoint == null)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = new InvalidOperationException("No healthy endpoints available"),
                Attempts = 0,
                TotalDuration = TimeSpan.Zero
            };
        }

        try
        {
            context?.Data.TryAdd("SelectedEndpoint", endpoint);
            var result = await operation(cancellationToken);

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata = { ["endpoint"] = endpoint.EndpointId }
            };
        }
        catch (Exception ex)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata = { ["endpoint"] = endpoint.EndpointId }
            };
        }
    }
}

/// <summary>
/// Consistent hashing load balancing strategy.
/// </summary>
public sealed class ConsistentHashingLoadBalancingStrategy : LoadBalancingStrategyBase
{
    private readonly SortedDictionary<uint, LoadBalancerEndpoint> _ring = new();
    private readonly int _virtualNodes;
    private readonly object _ringLock = new();

    public ConsistentHashingLoadBalancingStrategy()
        : this(virtualNodes: 150)
    {
    }

    public ConsistentHashingLoadBalancingStrategy(int virtualNodes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(virtualNodes);
        _virtualNodes = virtualNodes;
    }

    public override string StrategyId => "lb-consistent-hashing";
    public override string StrategyName => "Consistent Hashing Load Balancer";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Consistent Hashing Load Balancer",
        Description = "Uses consistent hashing ring for minimal redistribution when endpoints change - ideal for caching scenarios",
        Category = "LoadBalancing",
        ProvidesFaultTolerance = false,
        ProvidesLoadManagement = true,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = true,
        TypicalLatencyOverheadMs = 0.05,
        MemoryFootprint = "Medium"
    };

    public override void AddEndpoint(LoadBalancerEndpoint endpoint)
    {
        base.AddEndpoint(endpoint);

        lock (_ringLock)
        {
            for (int i = 0; i < _virtualNodes; i++)
            {
                var hash = ComputeHash($"{endpoint.EndpointId}:{i}");
                _ring[hash] = endpoint;
            }
        }
    }

    public override bool RemoveEndpoint(string endpointId)
    {
        var removed = base.RemoveEndpoint(endpointId);

        if (removed)
        {
            lock (_ringLock)
            {
                var keysToRemove = _ring.Where(kvp => kvp.Value.EndpointId == endpointId)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in keysToRemove)
                {
                    _ring.Remove(key);
                }
            }
        }

        return removed;
    }

    public override LoadBalancerEndpoint? SelectEndpoint(ResilienceContext? context = null)
    {
        lock (_ringLock)
        {
            if (_ring.Count == 0) return null;

            var key = context?.Data.TryGetValue("HashKey", out var hashKey) == true
                ? hashKey?.ToString() ?? context.OperationId
                : context?.OperationId ?? Guid.NewGuid().ToString();

            var hash = ComputeHash(key);

            // Find the first node clockwise from the hash
            foreach (var kvp in _ring)
            {
                if (kvp.Key >= hash && kvp.Value.IsHealthy)
                {
                    return kvp.Value;
                }
            }

            // Wrap around to the first healthy node
            return _ring.Values.FirstOrDefault(e => e.IsHealthy);
        }
    }

    private static uint ComputeHash(string key)
    {
        var bytes = Encoding.UTF8.GetBytes(key);
        var hash = MD5.HashData(bytes);
        return BitConverter.ToUInt32(hash, 0);
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;
        var endpoint = SelectEndpoint(context);

        if (endpoint == null)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = new InvalidOperationException("No healthy endpoints available"),
                Attempts = 0,
                TotalDuration = TimeSpan.Zero
            };
        }

        try
        {
            context?.Data.TryAdd("SelectedEndpoint", endpoint);
            var result = await operation(cancellationToken);

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata = { ["endpoint"] = endpoint.EndpointId }
            };
        }
        catch (Exception ex)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata = { ["endpoint"] = endpoint.EndpointId }
            };
        }
    }
}

/// <summary>
/// Least response time load balancing strategy.
/// </summary>
public sealed class LeastResponseTimeLoadBalancingStrategy : LoadBalancingStrategyBase
{
    public override string StrategyId => "lb-least-response-time";
    public override string StrategyName => "Least Response Time Load Balancer";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Least Response Time Load Balancer",
        Description = "Routes requests to the endpoint with the lowest recent response time for optimal performance",
        Category = "LoadBalancing",
        ProvidesFaultTolerance = false,
        ProvidesLoadManagement = true,
        SupportsAdaptiveBehavior = true,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.03,
        MemoryFootprint = "Low"
    };

    public override LoadBalancerEndpoint? SelectEndpoint(ResilienceContext? context = null)
    {
        var healthy = GetHealthyEndpoints();
        if (healthy.Count == 0) return null;

        return healthy.OrderBy(e => e.LastResponseTime).First();
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;
        var endpoint = SelectEndpoint(context);

        if (endpoint == null)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = new InvalidOperationException("No healthy endpoints available"),
                Attempts = 0,
                TotalDuration = TimeSpan.Zero
            };
        }

        try
        {
            endpoint.IncrementConnections();
            context?.Data.TryAdd("SelectedEndpoint", endpoint);

            var opStart = DateTimeOffset.UtcNow;
            var result = await operation(cancellationToken);
            endpoint.LastResponseTime = DateTimeOffset.UtcNow - opStart;

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata =
                {
                    ["endpoint"] = endpoint.EndpointId,
                    ["responseTimeMs"] = endpoint.LastResponseTime.TotalMilliseconds
                }
            };
        }
        catch (Exception ex)
        {
            endpoint.LastResponseTime = TimeSpan.FromSeconds(30); // Penalty for failure

            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata = { ["endpoint"] = endpoint.EndpointId }
            };
        }
        finally
        {
            endpoint.DecrementConnections();
        }
    }
}

/// <summary>
/// Power of two choices load balancing strategy.
/// </summary>
public sealed class PowerOfTwoChoicesLoadBalancingStrategy : LoadBalancingStrategyBase
{
    private readonly Random _random = new();

    public override string StrategyId => "lb-power-of-two";
    public override string StrategyName => "Power of Two Choices Load Balancer";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Power of Two Choices Load Balancer",
        Description = "Randomly selects two endpoints and picks the one with fewer connections - balances simplicity and fairness",
        Category = "LoadBalancing",
        ProvidesFaultTolerance = false,
        ProvidesLoadManagement = true,
        SupportsAdaptiveBehavior = true,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.02,
        MemoryFootprint = "Low"
    };

    public override LoadBalancerEndpoint? SelectEndpoint(ResilienceContext? context = null)
    {
        var healthy = GetHealthyEndpoints();
        if (healthy.Count == 0) return null;
        if (healthy.Count == 1) return healthy[0];

        LoadBalancerEndpoint choice1, choice2;
        lock (_random)
        {
            choice1 = healthy[_random.Next(healthy.Count)];
            do
            {
                choice2 = healthy[_random.Next(healthy.Count)];
            } while (choice2 == choice1 && healthy.Count > 1);
        }

        return choice1.ActiveConnections <= choice2.ActiveConnections ? choice1 : choice2;
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;
        var endpoint = SelectEndpoint(context);

        if (endpoint == null)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = new InvalidOperationException("No healthy endpoints available"),
                Attempts = 0,
                TotalDuration = TimeSpan.Zero
            };
        }

        try
        {
            endpoint.IncrementConnections();
            context?.Data.TryAdd("SelectedEndpoint", endpoint);

            var result = await operation(cancellationToken);

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata = { ["endpoint"] = endpoint.EndpointId, ["activeConnections"] = endpoint.ActiveConnections }
            };
        }
        catch (Exception ex)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata = { ["endpoint"] = endpoint.EndpointId }
            };
        }
        finally
        {
            endpoint.DecrementConnections();
        }
    }
}
