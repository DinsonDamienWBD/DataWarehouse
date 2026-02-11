using System.Collections.Concurrent;

namespace DataWarehouse.SDK.Services;

/// <summary>
/// Unified instance pooling service for managing reusable object instances.
/// Provides connection pooling, object pooling, and resource management.
/// Supports multiple pool types with configurable policies.
/// </summary>
public sealed class InstancePoolingService : IDisposable, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, IObjectPool> _pools = new();
    private readonly ConcurrentDictionary<string, PoolConfiguration> _configs = new();
    private readonly SemaphoreSlim _createLock = new(1, 1);
    private readonly Timer _maintenanceTimer;
    private bool _disposed;

    /// <summary>
    /// Event raised when a pool's health status changes.
    /// </summary>
    public event EventHandler<PoolHealthEventArgs>? PoolHealthChanged;

    /// <summary>
    /// Creates a new instance pooling service.
    /// </summary>
    /// <param name="maintenanceIntervalSeconds">Interval for pool maintenance in seconds.</param>
    public InstancePoolingService(int maintenanceIntervalSeconds = 30)
    {
        _maintenanceTimer = new Timer(
            PerformMaintenance,
            null,
            TimeSpan.FromSeconds(maintenanceIntervalSeconds),
            TimeSpan.FromSeconds(maintenanceIntervalSeconds));
    }

    /// <summary>
    /// Creates and registers a new object pool.
    /// </summary>
    /// <typeparam name="T">Type of objects in the pool.</typeparam>
    /// <param name="poolName">Unique name for the pool.</param>
    /// <param name="factory">Factory function to create new instances.</param>
    /// <param name="config">Pool configuration options.</param>
    public ObjectPool<T> CreatePool<T>(string poolName, Func<T> factory, PoolConfiguration? config = null)
        where T : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(poolName))
            throw new ArgumentException("Pool name cannot be empty", nameof(poolName));

        config ??= new PoolConfiguration();
        _configs[poolName] = config;

        var pool = new ObjectPool<T>(poolName, factory, config);
        if (!_pools.TryAdd(poolName, pool))
            throw new InvalidOperationException($"Pool '{poolName}' already exists");

        return pool;
    }

    /// <summary>
    /// Creates an async object pool with async factory.
    /// </summary>
    public AsyncObjectPool<T> CreateAsyncPool<T>(string poolName, Func<CancellationToken, Task<T>> factory, PoolConfiguration? config = null)
        where T : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(poolName))
            throw new ArgumentException("Pool name cannot be empty", nameof(poolName));

        config ??= new PoolConfiguration();
        _configs[poolName] = config;

        var pool = new AsyncObjectPool<T>(poolName, factory, config);
        if (!_pools.TryAdd(poolName, pool))
            throw new InvalidOperationException($"Pool '{poolName}' already exists");

        return pool;
    }

    /// <summary>
    /// Gets an existing pool by name.
    /// </summary>
    public ObjectPool<T>? GetPool<T>(string poolName) where T : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _pools.TryGetValue(poolName, out var pool) ? pool as ObjectPool<T> : null;
    }

    /// <summary>
    /// Gets an existing async pool by name.
    /// </summary>
    public AsyncObjectPool<T>? GetAsyncPool<T>(string poolName) where T : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _pools.TryGetValue(poolName, out var pool) ? pool as AsyncObjectPool<T> : null;
    }

    /// <summary>
    /// Removes a pool and disposes all its instances.
    /// </summary>
    public async Task<bool> RemovePoolAsync(string poolName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_pools.TryRemove(poolName, out var pool))
        {
            _configs.TryRemove(poolName, out _);
            await pool.DisposeAsync();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Gets statistics for all pools.
    /// </summary>
    public Dictionary<string, PoolStats> GetAllStats()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var stats = new Dictionary<string, PoolStats>();
        foreach (var kvp in _pools)
        {
            stats[kvp.Key] = kvp.Value.GetStats();
        }
        return stats;
    }

    /// <summary>
    /// Gets statistics for a specific pool.
    /// </summary>
    public PoolStats? GetPoolStats(string poolName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _pools.TryGetValue(poolName, out var pool) ? pool.GetStats() : null;
    }

    /// <summary>
    /// Lists all registered pool names.
    /// </summary>
    public IEnumerable<string> ListPools()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _pools.Keys.ToList();
    }

    /// <summary>
    /// Clears all idle instances from all pools.
    /// </summary>
    public void ClearIdleInstances()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        foreach (var pool in _pools.Values)
        {
            pool.ClearIdle();
        }
    }

    private void PerformMaintenance(object? state)
    {
        if (_disposed) return;

        foreach (var kvp in _pools)
        {
            try
            {
                var previousHealth = kvp.Value.Health;
                kvp.Value.PerformMaintenance();
                var currentHealth = kvp.Value.Health;

                if (previousHealth != currentHealth)
                {
                    PoolHealthChanged?.Invoke(this, new PoolHealthEventArgs
                    {
                        PoolName = kvp.Key,
                        PreviousHealth = previousHealth,
                        CurrentHealth = currentHealth,
                        Timestamp = DateTime.UtcNow
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError(
                    "[InstancePoolingService] Maintenance error for pool '{0}': {1}",
                    kvp.Key, ex.Message);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _maintenanceTimer.Dispose();

        foreach (var pool in _pools.Values)
        {
            (pool as IDisposable)?.Dispose();
        }

        _createLock.Dispose();
        _disposed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        _maintenanceTimer.Dispose();

        foreach (var pool in _pools.Values)
        {
            await pool.DisposeAsync();
        }

        _createLock.Dispose();
        _disposed = true;
    }
}

/// <summary>
/// Interface for object pools.
/// </summary>
public interface IObjectPool : IAsyncDisposable
{
    string Name { get; }
    PoolHealth Health { get; }
    PoolStats GetStats();
    void ClearIdle();
    void PerformMaintenance();
}

/// <summary>
/// Generic synchronous object pool.
/// </summary>
public sealed class ObjectPool<T> : IObjectPool, IDisposable where T : class
{
    private readonly ConcurrentBag<PooledInstance<T>> _available = new();
    private readonly ConcurrentDictionary<int, PooledInstance<T>> _inUse = new();
    private readonly Func<T> _factory;
    private readonly PoolConfiguration _config;
    private readonly SemaphoreSlim _semaphore;
    private int _totalCreated;
    private int _totalAcquired;
    private int _totalReleased;
    private int _totalDisposed;
    private bool _disposed;

    public string Name { get; }
    public PoolHealth Health { get; private set; } = PoolHealth.Healthy;

    internal ObjectPool(string name, Func<T> factory, PoolConfiguration config)
    {
        Name = name;
        _factory = factory;
        _config = config;
        _semaphore = new SemaphoreSlim(config.MaxSize, config.MaxSize);

        // Pre-warm the pool
        for (int i = 0; i < config.MinSize; i++)
        {
            var instance = CreateInstance();
            _available.Add(instance);
        }
    }

    /// <summary>
    /// Acquires an instance from the pool.
    /// </summary>
    public PooledInstance<T> Acquire(TimeSpan? timeout = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var waitTime = timeout ?? _config.AcquireTimeout;
        if (!_semaphore.Wait(waitTime))
            throw new TimeoutException($"Failed to acquire instance from pool '{Name}' within timeout");

        Interlocked.Increment(ref _totalAcquired);

        if (_available.TryTake(out var instance))
        {
            instance.LastUsed = DateTime.UtcNow;
            instance.UseCount++;
            _inUse[instance.Id] = instance;
            return instance;
        }

        // Create new instance
        instance = CreateInstance();
        instance.UseCount = 1;
        _inUse[instance.Id] = instance;
        return instance;
    }

    /// <summary>
    /// Releases an instance back to the pool.
    /// </summary>
    public void Release(PooledInstance<T> instance)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_inUse.TryRemove(instance.Id, out _))
            return;

        Interlocked.Increment(ref _totalReleased);

        // Check if instance should be disposed
        if (_config.MaxUseCount > 0 && instance.UseCount >= _config.MaxUseCount)
        {
            DisposeInstance(instance);
        }
        else if (_config.MaxIdleTime.HasValue &&
                 DateTime.UtcNow - instance.LastUsed > _config.MaxIdleTime.Value)
        {
            DisposeInstance(instance);
        }
        else
        {
            instance.LastUsed = DateTime.UtcNow;
            _available.Add(instance);
        }

        _semaphore.Release();
    }

    /// <summary>
    /// Gets a pooled instance using a scoped pattern.
    /// </summary>
    public PooledScope<T> GetScoped(TimeSpan? timeout = null)
    {
        return new PooledScope<T>(this, Acquire(timeout));
    }

    public PoolStats GetStats()
    {
        return new PoolStats
        {
            PoolName = Name,
            AvailableCount = _available.Count,
            InUseCount = _inUse.Count,
            TotalCreated = _totalCreated,
            TotalAcquired = _totalAcquired,
            TotalReleased = _totalReleased,
            TotalDisposed = _totalDisposed,
            MinSize = _config.MinSize,
            MaxSize = _config.MaxSize,
            Health = Health
        };
    }

    public void ClearIdle()
    {
        while (_available.TryTake(out var instance))
        {
            if (_available.Count < _config.MinSize)
            {
                _available.Add(instance);
                break;
            }
            DisposeInstance(instance);
        }
    }

    public void PerformMaintenance()
    {
        // Check health
        var stats = GetStats();
        if (stats.InUseCount >= _config.MaxSize * 0.9)
        {
            Health = PoolHealth.Stressed;
        }
        else if (stats.AvailableCount == 0 && stats.InUseCount > 0)
        {
            Health = PoolHealth.Degraded;
        }
        else
        {
            Health = PoolHealth.Healthy;
        }

        // Remove expired idle instances
        if (_config.MaxIdleTime.HasValue)
        {
            var expiredInstances = new List<PooledInstance<T>>();
            var tempList = new List<PooledInstance<T>>();

            while (_available.TryTake(out var instance))
            {
                if (DateTime.UtcNow - instance.LastUsed > _config.MaxIdleTime.Value &&
                    _available.Count + tempList.Count >= _config.MinSize)
                {
                    expiredInstances.Add(instance);
                }
                else
                {
                    tempList.Add(instance);
                }
            }

            foreach (var instance in tempList)
            {
                _available.Add(instance);
            }

            foreach (var instance in expiredInstances)
            {
                DisposeInstance(instance);
            }
        }
    }

    private PooledInstance<T> CreateInstance()
    {
        Interlocked.Increment(ref _totalCreated);

        return new PooledInstance<T>
        {
            Id = _totalCreated,
            Instance = _factory(),
            CreatedAt = DateTime.UtcNow,
            LastUsed = DateTime.UtcNow
        };
    }

    private void DisposeInstance(PooledInstance<T> instance)
    {
        Interlocked.Increment(ref _totalDisposed);

        if (instance.Instance is IDisposable disposable)
        {
            try { disposable.Dispose(); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        while (_available.TryTake(out var instance))
        {
            DisposeInstance(instance);
        }

        foreach (var instance in _inUse.Values)
        {
            DisposeInstance(instance);
        }

        _semaphore.Dispose();
        _disposed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        while (_available.TryTake(out var instance))
        {
            if (instance.Instance is IAsyncDisposable asyncDisposable)
            {
                try { await asyncDisposable.DisposeAsync(); } catch { }
            }
            else
            {
                DisposeInstance(instance);
            }
        }

        foreach (var instance in _inUse.Values)
        {
            if (instance.Instance is IAsyncDisposable asyncDisposable)
            {
                try { await asyncDisposable.DisposeAsync(); } catch { }
            }
            else
            {
                DisposeInstance(instance);
            }
        }

        _semaphore.Dispose();
        _disposed = true;
    }
}

/// <summary>
/// Generic asynchronous object pool.
/// </summary>
public sealed class AsyncObjectPool<T> : IObjectPool where T : class
{
    private readonly ConcurrentBag<PooledInstance<T>> _available = new();
    private readonly ConcurrentDictionary<int, PooledInstance<T>> _inUse = new();
    private readonly Func<CancellationToken, Task<T>> _factory;
    private readonly PoolConfiguration _config;
    private readonly SemaphoreSlim _semaphore;
    private int _totalCreated;
    private int _totalAcquired;
    private int _totalReleased;
    private int _totalDisposed;
    private bool _disposed;

    public string Name { get; }
    public PoolHealth Health { get; private set; } = PoolHealth.Healthy;

    internal AsyncObjectPool(string name, Func<CancellationToken, Task<T>> factory, PoolConfiguration config)
    {
        Name = name;
        _factory = factory;
        _config = config;
        _semaphore = new SemaphoreSlim(config.MaxSize, config.MaxSize);
    }

    /// <summary>
    /// Pre-warms the pool with instances.
    /// </summary>
    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        for (int i = 0; i < _config.MinSize; i++)
        {
            var instance = await CreateInstanceAsync(ct);
            _available.Add(instance);
        }
    }

    /// <summary>
    /// Acquires an instance from the pool asynchronously.
    /// </summary>
    public async Task<PooledInstance<T>> AcquireAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var timeoutCts = new CancellationTokenSource(_config.AcquireTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await _semaphore.WaitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException($"Failed to acquire instance from pool '{Name}' within timeout");
        }

        Interlocked.Increment(ref _totalAcquired);

        if (_available.TryTake(out var instance))
        {
            instance.LastUsed = DateTime.UtcNow;
            instance.UseCount++;
            _inUse[instance.Id] = instance;
            return instance;
        }

        // Create new instance
        instance = await CreateInstanceAsync(ct);
        instance.UseCount = 1;
        _inUse[instance.Id] = instance;
        return instance;
    }

    /// <summary>
    /// Releases an instance back to the pool.
    /// </summary>
    public void Release(PooledInstance<T> instance)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_inUse.TryRemove(instance.Id, out _))
            return;

        Interlocked.Increment(ref _totalReleased);

        // Check if instance should be disposed
        if (_config.MaxUseCount > 0 && instance.UseCount >= _config.MaxUseCount)
        {
            DisposeInstance(instance);
        }
        else if (_config.MaxIdleTime.HasValue &&
                 DateTime.UtcNow - instance.LastUsed > _config.MaxIdleTime.Value)
        {
            DisposeInstance(instance);
        }
        else
        {
            instance.LastUsed = DateTime.UtcNow;
            _available.Add(instance);
        }

        _semaphore.Release();
    }

    /// <summary>
    /// Gets a pooled instance using a scoped pattern.
    /// </summary>
    public async Task<AsyncPooledScope<T>> GetScopedAsync(CancellationToken ct = default)
    {
        return new AsyncPooledScope<T>(this, await AcquireAsync(ct));
    }

    public PoolStats GetStats()
    {
        return new PoolStats
        {
            PoolName = Name,
            AvailableCount = _available.Count,
            InUseCount = _inUse.Count,
            TotalCreated = _totalCreated,
            TotalAcquired = _totalAcquired,
            TotalReleased = _totalReleased,
            TotalDisposed = _totalDisposed,
            MinSize = _config.MinSize,
            MaxSize = _config.MaxSize,
            Health = Health
        };
    }

    public void ClearIdle()
    {
        while (_available.TryTake(out var instance))
        {
            if (_available.Count < _config.MinSize)
            {
                _available.Add(instance);
                break;
            }
            DisposeInstance(instance);
        }
    }

    public void PerformMaintenance()
    {
        var stats = GetStats();
        if (stats.InUseCount >= _config.MaxSize * 0.9)
        {
            Health = PoolHealth.Stressed;
        }
        else if (stats.AvailableCount == 0 && stats.InUseCount > 0)
        {
            Health = PoolHealth.Degraded;
        }
        else
        {
            Health = PoolHealth.Healthy;
        }
    }

    private async Task<PooledInstance<T>> CreateInstanceAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _totalCreated);

        return new PooledInstance<T>
        {
            Id = _totalCreated,
            Instance = await _factory(ct),
            CreatedAt = DateTime.UtcNow,
            LastUsed = DateTime.UtcNow
        };
    }

    private void DisposeInstance(PooledInstance<T> instance)
    {
        Interlocked.Increment(ref _totalDisposed);

        if (instance.Instance is IDisposable disposable)
        {
            try { disposable.Dispose(); } catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        while (_available.TryTake(out var instance))
        {
            if (instance.Instance is IAsyncDisposable asyncDisposable)
            {
                try { await asyncDisposable.DisposeAsync(); } catch { }
            }
            else
            {
                DisposeInstance(instance);
            }
        }

        foreach (var instance in _inUse.Values)
        {
            if (instance.Instance is IAsyncDisposable asyncDisposable)
            {
                try { await asyncDisposable.DisposeAsync(); } catch { }
            }
            else
            {
                DisposeInstance(instance);
            }
        }

        _semaphore.Dispose();
        _disposed = true;
    }
}

/// <summary>
/// Configuration for object pools.
/// </summary>
public class PoolConfiguration
{
    public int MinSize { get; set; } = 2;
    public int MaxSize { get; set; } = 20;
    public int MaxUseCount { get; set; } = 0; // 0 = unlimited
    public TimeSpan? MaxIdleTime { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan AcquireTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public bool ValidateOnAcquire { get; set; } = false;
}

/// <summary>
/// Health status of a pool.
/// </summary>
public enum PoolHealth
{
    Healthy,
    Degraded,
    Stressed,
    Unhealthy
}

/// <summary>
/// A pooled instance wrapper.
/// </summary>
public class PooledInstance<T> where T : class
{
    public int Id { get; set; }
    public required T Instance { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastUsed { get; set; }
    public int UseCount { get; set; }
}

/// <summary>
/// Statistics for an object pool.
/// </summary>
public class PoolStats
{
    public string PoolName { get; set; } = string.Empty;
    public int AvailableCount { get; set; }
    public int InUseCount { get; set; }
    public int TotalCreated { get; set; }
    public int TotalAcquired { get; set; }
    public int TotalReleased { get; set; }
    public int TotalDisposed { get; set; }
    public int MinSize { get; set; }
    public int MaxSize { get; set; }
    public PoolHealth Health { get; set; }
}

/// <summary>
/// Event args for pool health changes.
/// </summary>
public class PoolHealthEventArgs : EventArgs
{
    public string PoolName { get; set; } = string.Empty;
    public PoolHealth PreviousHealth { get; set; }
    public PoolHealth CurrentHealth { get; set; }
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// Scoped wrapper for automatic release of pooled instances.
/// </summary>
public readonly struct PooledScope<T> : IDisposable where T : class
{
    private readonly ObjectPool<T> _pool;
    private readonly PooledInstance<T> _instance;

    internal PooledScope(ObjectPool<T> pool, PooledInstance<T> instance)
    {
        _pool = pool;
        _instance = instance;
    }

    public T Instance => _instance.Instance;

    public void Dispose()
    {
        _pool.Release(_instance);
    }
}

/// <summary>
/// Async scoped wrapper for automatic release of pooled instances.
/// </summary>
public readonly struct AsyncPooledScope<T> : IDisposable where T : class
{
    private readonly AsyncObjectPool<T> _pool;
    private readonly PooledInstance<T> _instance;

    internal AsyncPooledScope(AsyncObjectPool<T> pool, PooledInstance<T> instance)
    {
        _pool = pool;
        _instance = instance;
    }

    public T Instance => _instance.Instance;

    public void Dispose()
    {
        _pool.Release(_instance);
    }
}
