using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateResilience.Strategies.ChaosEngineering;

/// <summary>
/// Fault injection chaos engineering strategy.
/// </summary>
public sealed class FaultInjectionStrategy : ResilienceStrategyBase
{
    private readonly double _faultRate;
    private readonly Type[] _exceptionTypes;
    private readonly string[]? _faultMessages;
    private readonly Random _random = new();
    private bool _enabled = true;

    public FaultInjectionStrategy()
        : this(faultRate: 0.1, exceptionTypes: new[] { typeof(Exception) })
    {
    }

    public FaultInjectionStrategy(double faultRate, Type[] exceptionTypes, string[]? faultMessages = null)
    {
        _faultRate = faultRate;
        _exceptionTypes = exceptionTypes.Length > 0 ? exceptionTypes : new[] { typeof(Exception) };
        _faultMessages = faultMessages;
    }

    public override string StrategyId => "chaos-fault-injection";
    public override string StrategyName => "Fault Injection";
    public override string Category => "ChaosEngineering";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Fault Injection",
        Description = "Randomly injects faults to test system resilience - configurable fault rate and exception types",
        Category = "ChaosEngineering",
        ProvidesFaultTolerance = false,
        ProvidesLoadManagement = false,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.01,
        MemoryFootprint = "Low"
    };

    /// <summary>Gets or sets whether fault injection is enabled.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>Gets the configured fault rate.</summary>
    public double FaultRate => _faultRate;

    /// <summary>
    /// Creates a fault exception.
    /// </summary>
    private Exception CreateFault()
    {
        var exceptionType = _exceptionTypes[_random.Next(_exceptionTypes.Length)];
        var message = _faultMessages != null && _faultMessages.Length > 0
            ? _faultMessages[_random.Next(_faultMessages.Length)]
            : "Chaos engineering: injected fault";

        try
        {
            return (Exception)Activator.CreateInstance(exceptionType, message)!;
        }
        catch
        {
            return new Exception(message);
        }
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;

        if (_enabled && _random.NextDouble() < _faultRate)
        {
            var fault = CreateFault();
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = fault,
                Attempts = 0,
                TotalDuration = TimeSpan.Zero,
                Metadata =
                {
                    ["chaosType"] = "fault-injection",
                    ["injected"] = true,
                    ["faultType"] = fault.GetType().Name
                }
            };
        }

        try
        {
            var result = await operation(cancellationToken);

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata = { ["chaosType"] = "fault-injection", ["injected"] = false }
            };
        }
        catch (Exception ex)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime
            };
        }
    }

    protected override string? GetCurrentState() =>
        _enabled ? $"Enabled (rate: {_faultRate:P0})" : "Disabled";
}

/// <summary>
/// Latency injection chaos engineering strategy.
/// </summary>
public sealed class LatencyInjectionStrategy : ResilienceStrategyBase
{
    private readonly double _injectionRate;
    private readonly TimeSpan _minLatency;
    private readonly TimeSpan _maxLatency;
    private readonly Random _random = new();
    private bool _enabled = true;

    public LatencyInjectionStrategy()
        : this(injectionRate: 0.1, minLatency: TimeSpan.FromMilliseconds(100), maxLatency: TimeSpan.FromSeconds(2))
    {
    }

    public LatencyInjectionStrategy(double injectionRate, TimeSpan minLatency, TimeSpan maxLatency)
    {
        _injectionRate = injectionRate;
        _minLatency = minLatency;
        _maxLatency = maxLatency;
    }

    public override string StrategyId => "chaos-latency-injection";
    public override string StrategyName => "Latency Injection";
    public override string Category => "ChaosEngineering";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Latency Injection",
        Description = "Randomly injects latency to test timeout handling and slow dependency scenarios",
        Category = "ChaosEngineering",
        ProvidesFaultTolerance = false,
        ProvidesLoadManagement = false,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.0, // Variable
        MemoryFootprint = "Low"
    };

    /// <summary>Gets or sets whether latency injection is enabled.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>Gets the injection rate.</summary>
    public double InjectionRate => _injectionRate;

    private TimeSpan GetRandomLatency()
    {
        var range = _maxLatency.TotalMilliseconds - _minLatency.TotalMilliseconds;
        var latency = _minLatency.TotalMilliseconds + _random.NextDouble() * range;
        return TimeSpan.FromMilliseconds(latency);
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;
        TimeSpan? injectedLatency = null;

        if (_enabled && _random.NextDouble() < _injectionRate)
        {
            injectedLatency = GetRandomLatency();
            await Task.Delay(injectedLatency.Value, cancellationToken);
        }

        try
        {
            var result = await operation(cancellationToken);

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata =
                {
                    ["chaosType"] = "latency-injection",
                    ["injected"] = injectedLatency.HasValue,
                    ["injectedLatencyMs"] = injectedLatency?.TotalMilliseconds ?? 0
                }
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
                Metadata =
                {
                    ["chaosType"] = "latency-injection",
                    ["injected"] = injectedLatency.HasValue
                }
            };
        }
    }

    protected override string? GetCurrentState() =>
        _enabled ? $"Enabled (rate: {_injectionRate:P0}, latency: {_minLatency.TotalMs()}-{_maxLatency.TotalMs()}ms)" : "Disabled";
}

/// <summary>
/// Process termination chaos engineering strategy.
/// </summary>
public sealed class ProcessTerminationStrategy : ResilienceStrategyBase
{
    private readonly double _terminationRate;
    private readonly Random _random = new();
    private bool _enabled = true;
    private readonly Action? _onTermination;

    public ProcessTerminationStrategy()
        : this(terminationRate: 0.01, onTermination: null)
    {
    }

    public ProcessTerminationStrategy(double terminationRate, Action? onTermination = null)
    {
        _terminationRate = terminationRate;
        _onTermination = onTermination;
    }

    public override string StrategyId => "chaos-process-termination";
    public override string StrategyName => "Process Termination";
    public override string Category => "ChaosEngineering";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Process Termination",
        Description = "Simulates sudden process death to test recovery and restart scenarios - USE WITH CAUTION",
        Category = "ChaosEngineering",
        ProvidesFaultTolerance = false,
        ProvidesLoadManagement = false,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.01,
        MemoryFootprint = "Low"
    };

    /// <summary>Gets or sets whether termination is enabled.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;

        if (_enabled && _random.NextDouble() < _terminationRate)
        {
            // Execute callback instead of actually terminating
            _onTermination?.Invoke();

            return new ResilienceResult<T>
            {
                Success = false,
                Exception = new ChaosTerminationException("Chaos engineering: simulated process termination"),
                Attempts = 0,
                TotalDuration = TimeSpan.Zero,
                Metadata =
                {
                    ["chaosType"] = "process-termination",
                    ["simulated"] = true
                }
            };
        }

        try
        {
            var result = await operation(cancellationToken);

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata = { ["chaosType"] = "process-termination", ["terminated"] = false }
            };
        }
        catch (Exception ex)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime
            };
        }
    }

    protected override string? GetCurrentState() =>
        _enabled ? $"Enabled (rate: {_terminationRate:P1})" : "Disabled";
}

/// <summary>
/// Resource exhaustion chaos engineering strategy.
/// </summary>
public sealed class ResourceExhaustionStrategy : ResilienceStrategyBase
{
    private readonly double _exhaustionRate;
    private readonly string[] _resourceTypes;
    private readonly Random _random = new();
    private bool _enabled = true;

    public ResourceExhaustionStrategy()
        : this(exhaustionRate: 0.05, resourceTypes: new[] { "memory", "cpu", "connections", "disk" })
    {
    }

    public ResourceExhaustionStrategy(double exhaustionRate, string[] resourceTypes)
    {
        _exhaustionRate = exhaustionRate;
        _resourceTypes = resourceTypes;
    }

    public override string StrategyId => "chaos-resource-exhaustion";
    public override string StrategyName => "Resource Exhaustion";
    public override string Category => "ChaosEngineering";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Resource Exhaustion",
        Description = "Simulates resource exhaustion scenarios like out of memory, CPU saturation, or connection pool exhaustion",
        Category = "ChaosEngineering",
        ProvidesFaultTolerance = false,
        ProvidesLoadManagement = false,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.01,
        MemoryFootprint = "Low"
    };

    /// <summary>Gets or sets whether resource exhaustion is enabled.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;

        if (_enabled && _random.NextDouble() < _exhaustionRate)
        {
            var resourceType = _resourceTypes[_random.Next(_resourceTypes.Length)];
            var exception = resourceType switch
            {
                "memory" => new OutOfMemoryException("Chaos engineering: simulated memory exhaustion"),
                "cpu" => new InvalidOperationException("Chaos engineering: simulated CPU saturation"),
                "connections" => new InvalidOperationException("Chaos engineering: connection pool exhausted"),
                "disk" => new IOException("Chaos engineering: disk space exhausted"),
                _ => new Exception($"Chaos engineering: {resourceType} exhaustion")
            };

            return new ResilienceResult<T>
            {
                Success = false,
                Exception = exception,
                Attempts = 0,
                TotalDuration = TimeSpan.Zero,
                Metadata =
                {
                    ["chaosType"] = "resource-exhaustion",
                    ["resourceType"] = resourceType,
                    ["simulated"] = true
                }
            };
        }

        try
        {
            var result = await operation(cancellationToken);

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata = { ["chaosType"] = "resource-exhaustion", ["exhausted"] = false }
            };
        }
        catch (Exception ex)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime
            };
        }
    }

    protected override string? GetCurrentState() =>
        _enabled ? $"Enabled (rate: {_exhaustionRate:P0})" : "Disabled";
}

/// <summary>
/// Network partition chaos engineering strategy.
/// </summary>
public sealed class NetworkPartitionStrategy : ResilienceStrategyBase
{
    private readonly double _partitionRate;
    private readonly TimeSpan _partitionDuration;
    private readonly Random _random = new();
    private bool _enabled = true;
    private DateTimeOffset _partitionEnd = DateTimeOffset.MinValue;
    private readonly object _lock = new();

    public NetworkPartitionStrategy()
        : this(partitionRate: 0.02, partitionDuration: TimeSpan.FromSeconds(30))
    {
    }

    public NetworkPartitionStrategy(double partitionRate, TimeSpan partitionDuration)
    {
        _partitionRate = partitionRate;
        _partitionDuration = partitionDuration;
    }

    public override string StrategyId => "chaos-network-partition";
    public override string StrategyName => "Network Partition";
    public override string Category => "ChaosEngineering";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Network Partition",
        Description = "Simulates network partitions to test split-brain scenarios and partition tolerance",
        Category = "ChaosEngineering",
        ProvidesFaultTolerance = false,
        ProvidesLoadManagement = false,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = true,
        TypicalLatencyOverheadMs = 0.01,
        MemoryFootprint = "Low"
    };

    /// <summary>Gets or sets whether network partition is enabled.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>Gets whether the network is currently partitioned.</summary>
    public bool IsPartitioned
    {
        get
        {
            lock (_lock)
            {
                return DateTimeOffset.UtcNow < _partitionEnd;
            }
        }
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;

        // Check if we're in a partition
        lock (_lock)
        {
            if (IsPartitioned)
            {
                return new ResilienceResult<T>
                {
                    Success = false,
                    Exception = new ChaosNetworkPartitionException("Chaos engineering: network partition in effect"),
                    Attempts = 0,
                    TotalDuration = TimeSpan.Zero,
                    Metadata =
                    {
                        ["chaosType"] = "network-partition",
                        ["partitioned"] = true,
                        ["remainingMs"] = (_partitionEnd - DateTimeOffset.UtcNow).TotalMilliseconds
                    }
                };
            }

            // Randomly start a partition
            if (_enabled && _random.NextDouble() < _partitionRate)
            {
                _partitionEnd = DateTimeOffset.UtcNow + _partitionDuration;

                return new ResilienceResult<T>
                {
                    Success = false,
                    Exception = new ChaosNetworkPartitionException("Chaos engineering: network partition started"),
                    Attempts = 0,
                    TotalDuration = TimeSpan.Zero,
                    Metadata =
                    {
                        ["chaosType"] = "network-partition",
                        ["partitionStarted"] = true,
                        ["durationMs"] = _partitionDuration.TotalMilliseconds
                    }
                };
            }
        }

        try
        {
            var result = await operation(cancellationToken);

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata = { ["chaosType"] = "network-partition", ["partitioned"] = false }
            };
        }
        catch (Exception ex)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime
            };
        }
    }

    /// <summary>
    /// Manually ends the partition.
    /// </summary>
    public void EndPartition()
    {
        lock (_lock)
        {
            _partitionEnd = DateTimeOffset.MinValue;
        }
    }

    protected override string? GetCurrentState() =>
        IsPartitioned ? $"Partitioned (ends in {(_partitionEnd - DateTimeOffset.UtcNow).TotalSeconds:F0}s)" :
        _enabled ? $"Enabled (rate: {_partitionRate:P1})" : "Disabled";
}

/// <summary>
/// Chaos monkey strategy that randomly applies different chaos types.
/// </summary>
public sealed class ChaosMonkeyStrategy : ResilienceStrategyBase
{
    private readonly List<(string name, ResilienceStrategyBase strategy, double weight)> _chaosStrategies = new();
    private readonly Random _random = new();
    private bool _enabled = true;
    private readonly double _overallRate;

    public ChaosMonkeyStrategy()
        : this(overallRate: 0.1)
    {
    }

    public ChaosMonkeyStrategy(double overallRate)
    {
        _overallRate = overallRate;

        // Add default chaos strategies with weights
        _chaosStrategies.Add(("fault", new FaultInjectionStrategy(1.0, new[] { typeof(Exception) }), 0.4));
        _chaosStrategies.Add(("latency", new LatencyInjectionStrategy(1.0, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2)), 0.4));
        _chaosStrategies.Add(("resource", new ResourceExhaustionStrategy(1.0, new[] { "memory", "connections" }), 0.15));
        _chaosStrategies.Add(("partition", new NetworkPartitionStrategy(1.0, TimeSpan.FromSeconds(10)), 0.05));
    }

    public override string StrategyId => "chaos-monkey";
    public override string StrategyName => "Chaos Monkey";
    public override string Category => "ChaosEngineering";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Chaos Monkey",
        Description = "Randomly applies various chaos engineering tactics including faults, latency, and resource exhaustion",
        Category = "ChaosEngineering",
        ProvidesFaultTolerance = false,
        ProvidesLoadManagement = false,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.1,
        MemoryFootprint = "Medium"
    };

    /// <summary>Gets or sets whether chaos monkey is enabled.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>
    /// Adds a custom chaos strategy.
    /// </summary>
    public ChaosMonkeyStrategy AddStrategy(string name, ResilienceStrategyBase strategy, double weight)
    {
        _chaosStrategies.Add((name, strategy, weight));
        return this;
    }

    private ResilienceStrategyBase? SelectRandomChaos()
    {
        if (!_enabled || _random.NextDouble() >= _overallRate)
            return null;

        var totalWeight = _chaosStrategies.Sum(s => s.weight);
        var randomValue = _random.NextDouble() * totalWeight;
        var cumulative = 0.0;

        foreach (var (_, strategy, weight) in _chaosStrategies)
        {
            cumulative += weight;
            if (randomValue <= cumulative)
            {
                return strategy;
            }
        }

        return _chaosStrategies.LastOrDefault().strategy;
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;
        var selectedChaos = SelectRandomChaos();

        if (selectedChaos != null)
        {
            var chaosResult = await selectedChaos.ExecuteAsync(operation, context, cancellationToken);
            chaosResult.Metadata["chaosMonkey"] = true;
            chaosResult.Metadata["selectedStrategy"] = selectedChaos.StrategyName;
            return chaosResult;
        }

        try
        {
            var result = await operation(cancellationToken);

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata = { ["chaosMonkey"] = false }
            };
        }
        catch (Exception ex)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime
            };
        }
    }

    protected override string? GetCurrentState() =>
        _enabled ? $"Enabled (rate: {_overallRate:P0}, strategies: {_chaosStrategies.Count})" : "Disabled";
}

/// <summary>
/// Exception thrown during simulated process termination.
/// </summary>
public sealed class ChaosTerminationException : Exception
{
    public ChaosTerminationException(string message) : base(message) { }
}

/// <summary>
/// Exception thrown during simulated network partition.
/// </summary>
public sealed class ChaosNetworkPartitionException : Exception
{
    public ChaosNetworkPartitionException(string message) : base(message) { }
}

/// <summary>
/// IO exception for simulated disk issues.
/// </summary>
public sealed class IOException : Exception
{
    public IOException(string message) : base(message) { }
}

internal static class TimeSpanExtensions
{
    public static double TotalMs(this TimeSpan ts) => ts.TotalMilliseconds;
}
