using System.Collections.Concurrent;

namespace DataWarehouse.SDK.Services;

/// <summary>
/// Centralized service manager for managing plugin and service lifecycle.
/// Provides dependency injection, health monitoring, and graceful shutdown.
/// </summary>
public sealed class ServiceManager : IDisposable, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ManagedService> _services = new();
    private readonly ConcurrentDictionary<Type, object> _singletons = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly List<string> _startOrder = new();
    private ServiceManagerState _state = ServiceManagerState.Stopped;
    private bool _disposed;

    /// <summary>
    /// Event raised when a service's health changes.
    /// </summary>
    public event EventHandler<ServiceHealthChangedEventArgs>? ServiceHealthChanged;

    /// <summary>
    /// Event raised when a service starts or stops.
    /// </summary>
    public event EventHandler<ServiceLifecycleEventArgs>? ServiceLifecycleChanged;

    /// <summary>
    /// Current state of the service manager.
    /// </summary>
    public ServiceManagerState State => _state;

    /// <summary>
    /// Registers a service with the manager.
    /// </summary>
    /// <typeparam name="TService">Service type.</typeparam>
    /// <param name="serviceId">Unique service identifier.</param>
    /// <param name="factory">Factory function to create the service.</param>
    /// <param name="options">Service registration options.</param>
    public void Register<TService>(
        string serviceId,
        Func<TService> factory,
        ServiceRegistrationOptions? options = null)
        where TService : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(serviceId))
            throw new ArgumentException("Service ID cannot be empty", nameof(serviceId));

        var managed = new ManagedService
        {
            ServiceId = serviceId,
            ServiceType = typeof(TService),
            Factory = () => factory(),
            Options = options ?? new ServiceRegistrationOptions(),
            State = ServiceState.Registered,
            RegisteredAt = DateTime.UtcNow
        };

        if (!_services.TryAdd(serviceId, managed))
            throw new InvalidOperationException($"Service '{serviceId}' is already registered");
    }

    /// <summary>
    /// Registers a singleton service.
    /// </summary>
    public void RegisterSingleton<TService>(TService instance) where TService : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _singletons[typeof(TService)] = instance;
    }

    /// <summary>
    /// Gets a singleton service.
    /// </summary>
    public TService? GetSingleton<TService>() where TService : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _singletons.TryGetValue(typeof(TService), out var service) ? (TService)service : null;
    }

    /// <summary>
    /// Resolves a service by ID.
    /// </summary>
    public object? Resolve(string serviceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_services.TryGetValue(serviceId, out var managed))
            return null;

        if (managed.Instance == null && managed.Factory != null)
        {
            managed.Instance = managed.Factory();
        }

        return managed.Instance;
    }

    /// <summary>
    /// Resolves a service by type.
    /// </summary>
    public TService? Resolve<TService>() where TService : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Check singletons first
        if (_singletons.TryGetValue(typeof(TService), out var singleton))
            return (TService)singleton;

        // Find registered service of type
        var managed = _services.Values.FirstOrDefault(s => s.ServiceType == typeof(TService));
        if (managed == null)
            return null;

        if (managed.Instance == null && managed.Factory != null)
        {
            managed.Instance = managed.Factory();
        }

        return managed.Instance as TService;
    }

    /// <summary>
    /// Starts all registered services in dependency order.
    /// </summary>
    public async Task StartAllAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lifecycleLock.WaitAsync(ct);
        try
        {
            if (_state == ServiceManagerState.Running)
                return;

            _state = ServiceManagerState.Starting;

            // Build start order based on dependencies
            var startOrder = BuildStartOrder();
            _startOrder.Clear();

            foreach (var serviceId in startOrder)
            {
                ct.ThrowIfCancellationRequested();

                if (!_services.TryGetValue(serviceId, out var managed))
                    continue;

                await StartServiceInternalAsync(managed, ct);
                _startOrder.Add(serviceId);
            }

            _state = ServiceManagerState.Running;
        }
        catch
        {
            _state = ServiceManagerState.Failed;
            throw;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// Stops all services in reverse dependency order.
    /// </summary>
    public async Task StopAllAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lifecycleLock.WaitAsync();
        try
        {
            if (_state == ServiceManagerState.Stopped)
                return;

            _state = ServiceManagerState.Stopping;

            // Stop in reverse order
            var stopOrder = _startOrder.AsEnumerable().Reverse().ToList();

            foreach (var serviceId in stopOrder)
            {
                if (!_services.TryGetValue(serviceId, out var managed))
                    continue;

                await StopServiceInternalAsync(managed);
            }

            _startOrder.Clear();
            _state = ServiceManagerState.Stopped;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// Starts a specific service.
    /// </summary>
    public async Task StartServiceAsync(string serviceId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_services.TryGetValue(serviceId, out var managed))
            throw new InvalidOperationException($"Service '{serviceId}' not found");

        await _lifecycleLock.WaitAsync(ct);
        try
        {
            await StartServiceInternalAsync(managed, ct);
            if (!_startOrder.Contains(serviceId))
                _startOrder.Add(serviceId);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// Stops a specific service.
    /// </summary>
    public async Task StopServiceAsync(string serviceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_services.TryGetValue(serviceId, out var managed))
            throw new InvalidOperationException($"Service '{serviceId}' not found");

        await _lifecycleLock.WaitAsync();
        try
        {
            await StopServiceInternalAsync(managed);
            _startOrder.Remove(serviceId);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// Gets the health status of a service.
    /// </summary>
    public ServiceHealthStatus GetServiceHealth(string serviceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_services.TryGetValue(serviceId, out var managed))
            return ServiceHealthStatus.Unknown;

        return managed.Health;
    }

    /// <summary>
    /// Gets information about all registered services.
    /// </summary>
    public IEnumerable<ServiceInfo> GetAllServices()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _services.Values.Select(m => new ServiceInfo
        {
            ServiceId = m.ServiceId,
            ServiceType = m.ServiceType.FullName ?? m.ServiceType.Name,
            State = m.State,
            Health = m.Health,
            StartedAt = m.StartedAt,
            StoppedAt = m.StoppedAt,
            LastHealthCheck = m.LastHealthCheck,
            Dependencies = m.Options.Dependencies?.ToArray() ?? Array.Empty<string>()
        }).ToList();
    }

    /// <summary>
    /// Performs health checks on all running services.
    /// </summary>
    public async Task<Dictionary<string, ServiceHealthStatus>> CheckHealthAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var results = new Dictionary<string, ServiceHealthStatus>();

        foreach (var kvp in _services)
        {
            ct.ThrowIfCancellationRequested();

            var managed = kvp.Value;
            if (managed.State != ServiceState.Running)
            {
                results[kvp.Key] = ServiceHealthStatus.Unknown;
                continue;
            }

            var previousHealth = managed.Health;
            managed.Health = await CheckServiceHealthAsync(managed, ct);
            managed.LastHealthCheck = DateTime.UtcNow;
            results[kvp.Key] = managed.Health;

            if (previousHealth != managed.Health)
            {
                ServiceHealthChanged?.Invoke(this, new ServiceHealthChangedEventArgs
                {
                    ServiceId = managed.ServiceId,
                    PreviousHealth = previousHealth,
                    CurrentHealth = managed.Health,
                    Timestamp = DateTime.UtcNow
                });
            }
        }

        return results;
    }

    private async Task StartServiceInternalAsync(ManagedService managed, CancellationToken ct)
    {
        if (managed.State == ServiceState.Running)
            return;

        managed.State = ServiceState.Starting;

        try
        {
            // Create instance if needed
            if (managed.Instance == null && managed.Factory != null)
            {
                managed.Instance = managed.Factory();
            }

            // Call StartAsync if the service implements it
            if (managed.Instance is IAsyncLifecycle lifecycle)
            {
                await lifecycle.StartAsync(ct);
            }

            managed.State = ServiceState.Running;
            managed.StartedAt = DateTime.UtcNow;
            managed.Health = ServiceHealthStatus.Healthy;

            ServiceLifecycleChanged?.Invoke(this, new ServiceLifecycleEventArgs
            {
                ServiceId = managed.ServiceId,
                Event = ServiceLifecycleEvent.Started,
                Timestamp = DateTime.UtcNow
            });
        }
        catch
        {
            managed.State = ServiceState.Failed;
            managed.Health = ServiceHealthStatus.Unhealthy;
            throw;
        }
    }

    private async Task StopServiceInternalAsync(ManagedService managed)
    {
        if (managed.State != ServiceState.Running)
            return;

        managed.State = ServiceState.Stopping;

        try
        {
            if (managed.Instance is IAsyncLifecycle lifecycle)
            {
                await lifecycle.StopAsync();
            }

            if (managed.Instance is IDisposable disposable)
            {
                disposable.Dispose();
            }
            else if (managed.Instance is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }

            managed.State = ServiceState.Stopped;
            managed.StoppedAt = DateTime.UtcNow;
            managed.Instance = null;

            ServiceLifecycleChanged?.Invoke(this, new ServiceLifecycleEventArgs
            {
                ServiceId = managed.ServiceId,
                Event = ServiceLifecycleEvent.Stopped,
                Timestamp = DateTime.UtcNow
            });
        }
        catch
        {
            managed.State = ServiceState.Failed;
            throw;
        }
    }

    private async Task<ServiceHealthStatus> CheckServiceHealthAsync(ManagedService managed, CancellationToken ct)
    {
        if (managed.Instance is IHealthCheckable healthCheckable)
        {
            try
            {
                var isHealthy = await healthCheckable.CheckHealthAsync(ct);
                return isHealthy ? ServiceHealthStatus.Healthy : ServiceHealthStatus.Unhealthy;
            }
            catch
            {
                return ServiceHealthStatus.Unhealthy;
            }
        }

        // No health check available - assume healthy if running
        return managed.State == ServiceState.Running ? ServiceHealthStatus.Healthy : ServiceHealthStatus.Unknown;
    }

    private List<string> BuildStartOrder()
    {
        var visited = new HashSet<string>();
        var order = new List<string>();
        var visiting = new HashSet<string>();

        foreach (var serviceId in _services.Keys)
        {
            Visit(serviceId, visited, visiting, order);
        }

        return order;
    }

    private void Visit(string serviceId, HashSet<string> visited, HashSet<string> visiting, List<string> order)
    {
        if (visited.Contains(serviceId))
            return;

        if (visiting.Contains(serviceId))
            throw new InvalidOperationException($"Circular dependency detected involving service '{serviceId}'");

        visiting.Add(serviceId);

        if (_services.TryGetValue(serviceId, out var managed) && managed.Options.Dependencies != null)
        {
            foreach (var dep in managed.Options.Dependencies)
            {
                Visit(dep, visited, visiting, order);
            }
        }

        visiting.Remove(serviceId);
        visited.Add(serviceId);
        order.Add(serviceId);
    }

    public void Dispose()
    {
        if (_disposed) return;

        foreach (var managed in _services.Values)
        {
            if (managed.Instance is IDisposable disposable)
            {
                try { disposable.Dispose(); } catch { }
            }
        }

        foreach (var singleton in _singletons.Values)
        {
            if (singleton is IDisposable disposable)
            {
                try { disposable.Dispose(); } catch { }
            }
        }

        _lifecycleLock.Dispose();
        _disposed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        await StopAllAsync();
        Dispose();
    }
}

/// <summary>
/// Interface for services with async lifecycle.
/// </summary>
public interface IAsyncLifecycle
{
    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}

/// <summary>
/// Interface for services that support health checks.
/// </summary>
public interface IHealthCheckable
{
    Task<bool> CheckHealthAsync(CancellationToken ct = default);
}

/// <summary>
/// Options for service registration.
/// </summary>
public class ServiceRegistrationOptions
{
    public List<string>? Dependencies { get; set; }
    public TimeSpan? StartTimeout { get; set; }
    public TimeSpan? StopTimeout { get; set; }
    public bool CriticalService { get; set; }
    public int RestartAttempts { get; set; } = 3;
}

/// <summary>
/// State of the service manager.
/// </summary>
public enum ServiceManagerState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Failed
}

/// <summary>
/// State of an individual service.
/// </summary>
public enum ServiceState
{
    Registered,
    Starting,
    Running,
    Stopping,
    Stopped,
    Failed
}

/// <summary>
/// Health status of a service.
/// </summary>
public enum ServiceHealthStatus
{
    Unknown,
    Healthy,
    Degraded,
    Unhealthy
}

/// <summary>
/// Lifecycle events for services.
/// </summary>
public enum ServiceLifecycleEvent
{
    Starting,
    Started,
    Stopping,
    Stopped,
    Failed,
    Restarting
}

/// <summary>
/// Information about a registered service.
/// </summary>
public class ServiceInfo
{
    public string ServiceId { get; set; } = string.Empty;
    public string ServiceType { get; set; } = string.Empty;
    public ServiceState State { get; set; }
    public ServiceHealthStatus Health { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? StoppedAt { get; set; }
    public DateTime? LastHealthCheck { get; set; }
    public string[] Dependencies { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Event args for service health changes.
/// </summary>
public class ServiceHealthChangedEventArgs : EventArgs
{
    public string ServiceId { get; set; } = string.Empty;
    public ServiceHealthStatus PreviousHealth { get; set; }
    public ServiceHealthStatus CurrentHealth { get; set; }
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// Event args for service lifecycle events.
/// </summary>
public class ServiceLifecycleEventArgs : EventArgs
{
    public string ServiceId { get; set; } = string.Empty;
    public ServiceLifecycleEvent Event { get; set; }
    public DateTime Timestamp { get; set; }
}

internal class ManagedService
{
    public string ServiceId { get; set; } = string.Empty;
    public Type ServiceType { get; set; } = typeof(object);
    public Func<object>? Factory { get; set; }
    public object? Instance { get; set; }
    public ServiceRegistrationOptions Options { get; set; } = new();
    public ServiceState State { get; set; }
    public ServiceHealthStatus Health { get; set; } = ServiceHealthStatus.Unknown;
    public DateTime RegisteredAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? StoppedAt { get; set; }
    public DateTime? LastHealthCheck { get; set; }
}
