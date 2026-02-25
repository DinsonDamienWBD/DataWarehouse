using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateResilience.Strategies.HealthChecks;

/// <summary>
/// Health check status.
/// </summary>
public enum HealthStatus
{
    /// <summary>Service is healthy.</summary>
    Healthy,
    /// <summary>Service is degraded but functional.</summary>
    Degraded,
    /// <summary>Service is unhealthy.</summary>
    Unhealthy
}

/// <summary>
/// Result of a health check.
/// </summary>
public sealed class HealthCheckResult
{
    /// <summary>Overall health status.</summary>
    public HealthStatus Status { get; init; }

    /// <summary>Description of the health state.</summary>
    public string? Description { get; init; }

    /// <summary>Duration of the health check.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>When the check was performed.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Exception if the check failed.</summary>
    public Exception? Exception { get; init; }

    /// <summary>Additional data from the check.</summary>
    public Dictionary<string, object> Data { get; init; } = new();

    /// <summary>Individual component results.</summary>
    public Dictionary<string, HealthCheckResult> Components { get; init; } = new();
}

/// <summary>
/// Liveness health check strategy.
/// </summary>
public sealed class LivenessHealthCheckStrategy : ResilienceStrategyBase
{
    private readonly List<Func<CancellationToken, Task<bool>>> _checks = new();
    private HealthCheckResult? _lastResult;
    private readonly object _lock = new();

    public LivenessHealthCheckStrategy()
    {
    }

    public override string StrategyId => "health-liveness";
    public override string StrategyName => "Liveness Health Check";
    public override string Category => "HealthCheck";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Liveness Health Check",
        Description = "Determines if the service is running and responsive - used by orchestrators to restart unhealthy instances",
        Category = "HealthCheck",
        ProvidesFaultTolerance = false,
        ProvidesLoadManagement = false,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 1.0,
        MemoryFootprint = "Low"
    };

    /// <summary>Gets the last health check result.</summary>
    public HealthCheckResult? LastResult => _lastResult;

    /// <summary>
    /// Adds a liveness check.
    /// </summary>
    public LivenessHealthCheckStrategy AddCheck(Func<CancellationToken, Task<bool>> check)
    {
        _checks.Add(check);
        return this;
    }

    /// <summary>
    /// Performs the liveness check.
    /// </summary>
    public async Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var startTime = DateTimeOffset.UtcNow;

        try
        {
            if (_checks.Count == 0)
            {
                // No checks configured - assume healthy
                return UpdateResult(new HealthCheckResult
                {
                    Status = HealthStatus.Healthy,
                    Description = "No checks configured",
                    Duration = TimeSpan.Zero
                });
            }

            foreach (var check in _checks)
            {
                if (!await check(cancellationToken))
                {
                    return UpdateResult(new HealthCheckResult
                    {
                        Status = HealthStatus.Unhealthy,
                        Description = "Liveness check failed",
                        Duration = DateTimeOffset.UtcNow - startTime
                    });
                }
            }

            return UpdateResult(new HealthCheckResult
            {
                Status = HealthStatus.Healthy,
                Description = "All liveness checks passed",
                Duration = DateTimeOffset.UtcNow - startTime
            });
        }
        catch (Exception ex)
        {
            return UpdateResult(new HealthCheckResult
            {
                Status = HealthStatus.Unhealthy,
                Description = $"Liveness check threw exception: {ex.Message}",
                Duration = DateTimeOffset.UtcNow - startTime,
                Exception = ex
            });
        }
    }

    private HealthCheckResult UpdateResult(HealthCheckResult result)
    {
        lock (_lock)
        {
            _lastResult = result;
        }
        return result;
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;
        var healthResult = await CheckAsync(cancellationToken);

        if (healthResult.Status == HealthStatus.Unhealthy)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = new HealthCheckFailedException($"Liveness check failed: {healthResult.Description}"),
                Attempts = 0,
                TotalDuration = healthResult.Duration,
                Metadata =
                {
                    ["healthStatus"] = healthResult.Status.ToString(),
                    ["healthDescription"] = healthResult.Description ?? ""
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
                Metadata = { ["healthStatus"] = healthResult.Status.ToString() }
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
        _lastResult != null ? $"{_lastResult.Status} ({_lastResult.Duration.TotalMilliseconds:F1}ms)" : "Not checked";
}

/// <summary>
/// Readiness health check strategy.
/// </summary>
public sealed class ReadinessHealthCheckStrategy : ResilienceStrategyBase
{
    private readonly List<(string name, Func<CancellationToken, Task<HealthCheckResult>> check)> _checks = new();
    private HealthCheckResult? _lastResult;
    private readonly object _lock = new();

    public ReadinessHealthCheckStrategy()
    {
    }

    public override string StrategyId => "health-readiness";
    public override string StrategyName => "Readiness Health Check";
    public override string Category => "HealthCheck";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Readiness Health Check",
        Description = "Determines if the service is ready to accept traffic - checks dependencies and initialization state",
        Category = "HealthCheck",
        ProvidesFaultTolerance = false,
        ProvidesLoadManagement = true,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 10.0,
        MemoryFootprint = "Low"
    };

    /// <summary>Gets the last health check result.</summary>
    public HealthCheckResult? LastResult => _lastResult;

    /// <summary>
    /// Adds a readiness check.
    /// </summary>
    public ReadinessHealthCheckStrategy AddCheck(string name, Func<CancellationToken, Task<HealthCheckResult>> check)
    {
        _checks.Add((name, check));
        return this;
    }

    /// <summary>
    /// Adds a simple boolean readiness check.
    /// </summary>
    public ReadinessHealthCheckStrategy AddCheck(string name, Func<CancellationToken, Task<bool>> check)
    {
        _checks.Add((name, async ct =>
        {
            var start = DateTimeOffset.UtcNow;
            try
            {
                var result = await check(ct);
                return new HealthCheckResult
                {
                    Status = result ? HealthStatus.Healthy : HealthStatus.Unhealthy,
                    Description = result ? "Check passed" : "Check failed",
                    Duration = DateTimeOffset.UtcNow - start
                };
            }
            catch (Exception ex)
            {
                return new HealthCheckResult
                {
                    Status = HealthStatus.Unhealthy,
                    Description = ex.Message,
                    Duration = DateTimeOffset.UtcNow - start,
                    Exception = ex
                };
            }
        }));
        return this;
    }

    /// <summary>
    /// Performs the readiness check.
    /// </summary>
    public async Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var startTime = DateTimeOffset.UtcNow;
        var components = new Dictionary<string, HealthCheckResult>();
        var overallStatus = HealthStatus.Healthy;

        foreach (var (name, check) in _checks)
        {
            try
            {
                var result = await check(cancellationToken);
                components[name] = result;

                if (result.Status == HealthStatus.Unhealthy)
                    overallStatus = HealthStatus.Unhealthy;
                else if (result.Status == HealthStatus.Degraded && overallStatus == HealthStatus.Healthy)
                    overallStatus = HealthStatus.Degraded;
            }
            catch (Exception ex)
            {
                components[name] = new HealthCheckResult
                {
                    Status = HealthStatus.Unhealthy,
                    Description = ex.Message,
                    Exception = ex,
                    Duration = DateTimeOffset.UtcNow - startTime
                };
                overallStatus = HealthStatus.Unhealthy;
            }
        }

        var finalResult = new HealthCheckResult
        {
            Status = overallStatus,
            Description = $"{components.Count(c => c.Value.Status == HealthStatus.Healthy)}/{components.Count} checks passed",
            Duration = DateTimeOffset.UtcNow - startTime,
            Components = components
        };

        lock (_lock)
        {
            _lastResult = finalResult;
        }

        return finalResult;
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;
        var healthResult = await CheckAsync(cancellationToken);

        if (healthResult.Status == HealthStatus.Unhealthy)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = new HealthCheckFailedException($"Readiness check failed: {healthResult.Description}"),
                Attempts = 0,
                TotalDuration = healthResult.Duration,
                Metadata =
                {
                    ["healthStatus"] = healthResult.Status.ToString(),
                    ["healthDescription"] = healthResult.Description ?? "",
                    ["failedChecks"] = healthResult.Components.Where(c => c.Value.Status == HealthStatus.Unhealthy).Select(c => c.Key).ToArray()
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
                Metadata = { ["healthStatus"] = healthResult.Status.ToString() }
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

    protected override string? GetCurrentState()
    {
        if (_lastResult == null) return "Not checked";
        var healthy = _lastResult.Components.Count(c => c.Value.Status == HealthStatus.Healthy);
        return $"{_lastResult.Status} ({healthy}/{_lastResult.Components.Count} healthy)";
    }
}

/// <summary>
/// Startup probe health check strategy.
/// </summary>
public sealed class StartupProbeHealthCheckStrategy : ResilienceStrategyBase
{
    private bool _startupComplete;
    private HealthCheckResult? _lastResult;
    private readonly Func<CancellationToken, Task<bool>> _startupCheck;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _checkInterval;
    private readonly int _maxAttempts;
    private readonly object _lock = new();

    public StartupProbeHealthCheckStrategy()
        : this(
            startupCheck: _ => Task.FromResult(true),
            timeout: TimeSpan.FromMinutes(5),
            checkInterval: TimeSpan.FromSeconds(10),
            maxAttempts: 30)
    {
    }

    public StartupProbeHealthCheckStrategy(
        Func<CancellationToken, Task<bool>> startupCheck,
        TimeSpan timeout,
        TimeSpan checkInterval,
        int maxAttempts)
    {
        _startupCheck = startupCheck;
        _timeout = timeout;
        _checkInterval = checkInterval;
        _maxAttempts = maxAttempts;
    }

    public override string StrategyId => "health-startup-probe";
    public override string StrategyName => "Startup Probe";
    public override string Category => "HealthCheck";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Startup Probe",
        Description = "Monitors service startup and delays liveness/readiness checks until startup is complete",
        Category = "HealthCheck",
        ProvidesFaultTolerance = false,
        ProvidesLoadManagement = true,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 5.0,
        MemoryFootprint = "Low"
    };

    /// <summary>Gets whether startup is complete.</summary>
    public bool StartupComplete => _startupComplete;

    /// <summary>
    /// Waits for startup to complete.
    /// </summary>
    public async Task<HealthCheckResult> WaitForStartupAsync(CancellationToken cancellationToken = default)
    {
        var startTime = DateTimeOffset.UtcNow;
        var attempts = 0;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);

        while (!_startupComplete && attempts < _maxAttempts)
        {
            try
            {
                attempts++;
                var result = await _startupCheck(timeoutCts.Token);

                if (result)
                {
                    lock (_lock)
                    {
                        _startupComplete = true;
                    }

                    var successResult = new HealthCheckResult
                    {
                        Status = HealthStatus.Healthy,
                        Description = $"Startup completed after {attempts} attempts",
                        Duration = DateTimeOffset.UtcNow - startTime,
                        Data = { ["attempts"] = attempts }
                    };

                    lock (_lock)
                    {
                        _lastResult = successResult;
                    }

                    return successResult;
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Ignore individual check failures during startup
            }

            await Task.Delay(_checkInterval, cancellationToken);
        }

        var failureResult = new HealthCheckResult
        {
            Status = HealthStatus.Unhealthy,
            Description = $"Startup failed after {attempts} attempts",
            Duration = DateTimeOffset.UtcNow - startTime,
            Data = { ["attempts"] = attempts }
        };

        lock (_lock)
        {
            _lastResult = failureResult;
        }

        return failureResult;
    }

    /// <summary>
    /// Marks startup as complete manually.
    /// </summary>
    public void MarkStartupComplete()
    {
        lock (_lock)
        {
            _startupComplete = true;
            _lastResult = new HealthCheckResult
            {
                Status = HealthStatus.Healthy,
                Description = "Startup marked complete manually"
            };
        }
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;

        if (!_startupComplete)
        {
            var startupResult = await WaitForStartupAsync(cancellationToken);

            if (startupResult.Status != HealthStatus.Healthy)
            {
                return new ResilienceResult<T>
                {
                    Success = false,
                    Exception = new HealthCheckFailedException($"Startup probe failed: {startupResult.Description}"),
                    Attempts = 0,
                    TotalDuration = startupResult.Duration,
                    Metadata =
                    {
                        ["startupComplete"] = false,
                        ["startupDescription"] = startupResult.Description ?? ""
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
                Metadata = { ["startupComplete"] = true }
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
        _startupComplete ? "Startup complete" : "Starting...";
}

/// <summary>
/// Deep health check strategy that checks all dependencies.
/// </summary>
public sealed class DeepHealthCheckStrategy : ResilienceStrategyBase
{
    private readonly List<(string name, Func<CancellationToken, Task<HealthCheckResult>> check, bool critical)> _checks = new();
    private HealthCheckResult? _lastResult;
    private readonly TimeSpan _cacheValidity;
    private DateTimeOffset _lastCheckTime = DateTimeOffset.MinValue;
    private readonly object _lock = new();

    public DeepHealthCheckStrategy()
        : this(cacheValidity: TimeSpan.FromSeconds(30))
    {
    }

    public DeepHealthCheckStrategy(TimeSpan cacheValidity)
    {
        if (cacheValidity <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(cacheValidity), "Cache validity must be positive.");
        _cacheValidity = cacheValidity;
    }

    public override string StrategyId => "health-deep";
    public override string StrategyName => "Deep Health Check";
    public override string Category => "HealthCheck";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Deep Health Check",
        Description = "Comprehensive health check that validates all dependencies, resources, and system state",
        Category = "HealthCheck",
        ProvidesFaultTolerance = false,
        ProvidesLoadManagement = false,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 100.0,
        MemoryFootprint = "Medium"
    };

    /// <summary>Gets the last health check result.</summary>
    public HealthCheckResult? LastResult => _lastResult;

    /// <summary>
    /// Adds a dependency check.
    /// </summary>
    public DeepHealthCheckStrategy AddDependency(string name, Func<CancellationToken, Task<HealthCheckResult>> check, bool critical = true)
    {
        _checks.Add((name, check, critical));
        return this;
    }

    /// <summary>
    /// Adds a simple boolean dependency check.
    /// </summary>
    public DeepHealthCheckStrategy AddDependency(string name, Func<CancellationToken, Task<bool>> check, bool critical = true)
    {
        _checks.Add((name, async ct =>
        {
            var start = DateTimeOffset.UtcNow;
            try
            {
                var result = await check(ct);
                return new HealthCheckResult
                {
                    Status = result ? HealthStatus.Healthy : HealthStatus.Unhealthy,
                    Description = result ? "Dependency check passed" : "Dependency check failed",
                    Duration = DateTimeOffset.UtcNow - start
                };
            }
            catch (Exception ex)
            {
                return new HealthCheckResult
                {
                    Status = HealthStatus.Unhealthy,
                    Description = ex.Message,
                    Duration = DateTimeOffset.UtcNow - start,
                    Exception = ex
                };
            }
        }, critical));
        return this;
    }

    /// <summary>
    /// Performs the deep health check.
    /// </summary>
    public async Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        // Check cache
        lock (_lock)
        {
            if (_lastResult != null && DateTimeOffset.UtcNow - _lastCheckTime < _cacheValidity)
            {
                return _lastResult;
            }
        }

        var startTime = DateTimeOffset.UtcNow;
        var components = new Dictionary<string, HealthCheckResult>();
        var overallStatus = HealthStatus.Healthy;
        var criticalFailure = false;

        // Run all checks in parallel
        var checkTasks = _checks.Select(async check =>
        {
            try
            {
                var result = await check.check(cancellationToken);
                return (check.name, result, check.critical);
            }
            catch (Exception ex)
            {
                return (check.name, new HealthCheckResult
                {
                    Status = HealthStatus.Unhealthy,
                    Description = ex.Message,
                    Exception = ex,
                    Duration = DateTimeOffset.UtcNow - startTime
                }, check.critical);
            }
        });

        var results = await Task.WhenAll(checkTasks);

        foreach (var (name, result, critical) in results)
        {
            components[name] = result;

            if (result.Status == HealthStatus.Unhealthy)
            {
                if (critical)
                {
                    criticalFailure = true;
                    overallStatus = HealthStatus.Unhealthy;
                }
                else if (overallStatus == HealthStatus.Healthy)
                {
                    overallStatus = HealthStatus.Degraded;
                }
            }
            else if (result.Status == HealthStatus.Degraded && overallStatus == HealthStatus.Healthy)
            {
                overallStatus = HealthStatus.Degraded;
            }
        }

        var healthyCount = components.Count(c => c.Value.Status == HealthStatus.Healthy);
        var finalResult = new HealthCheckResult
        {
            Status = overallStatus,
            Description = criticalFailure
                ? "Critical dependency failure"
                : $"{healthyCount}/{components.Count} dependencies healthy",
            Duration = DateTimeOffset.UtcNow - startTime,
            Components = components,
            Data =
            {
                ["healthyCount"] = healthyCount,
                ["degradedCount"] = components.Count(c => c.Value.Status == HealthStatus.Degraded),
                ["unhealthyCount"] = components.Count(c => c.Value.Status == HealthStatus.Unhealthy)
            }
        };

        lock (_lock)
        {
            _lastResult = finalResult;
            _lastCheckTime = DateTimeOffset.UtcNow;
        }

        return finalResult;
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;
        var healthResult = await CheckAsync(cancellationToken);

        if (healthResult.Status == HealthStatus.Unhealthy)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = new HealthCheckFailedException($"Deep health check failed: {healthResult.Description}"),
                Attempts = 0,
                TotalDuration = healthResult.Duration,
                Metadata =
                {
                    ["healthStatus"] = healthResult.Status.ToString(),
                    ["healthDescription"] = healthResult.Description ?? "",
                    ["unhealthyDependencies"] = healthResult.Components.Where(c => c.Value.Status == HealthStatus.Unhealthy).Select(c => c.Key).ToArray()
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
                Metadata = { ["healthStatus"] = healthResult.Status.ToString() }
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

    protected override string? GetCurrentState()
    {
        if (_lastResult == null) return "Not checked";
        var healthy = _lastResult.Components.Count(c => c.Value.Status == HealthStatus.Healthy);
        return $"{_lastResult.Status} ({healthy}/{_lastResult.Components.Count} healthy)";
    }
}

/// <summary>
/// Exception thrown when a health check fails.
/// </summary>
public sealed class HealthCheckFailedException : Exception
{
    public HealthCheckFailedException(string message) : base(message) { }
    public HealthCheckFailedException(string message, Exception innerException) : base(message, innerException) { }
}
