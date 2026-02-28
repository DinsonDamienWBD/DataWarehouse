using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Infrastructure;

#region K1: Hot Plugin Reload Infrastructure

/// <summary>
/// Result of a plugin reload operation.
/// </summary>
public sealed class ReloadResult
{
    /// <summary>
    /// Whether the reload operation succeeded.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// The plugin ID that was reloaded.
    /// </summary>
    public string? PluginId { get; init; }

    /// <summary>
    /// Error message if the reload failed.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Previous version of the plugin.
    /// </summary>
    public Version? PreviousVersion { get; init; }

    /// <summary>
    /// New version of the plugin after reload.
    /// </summary>
    public Version? NewVersion { get; init; }

    /// <summary>
    /// Duration of the reload operation.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Whether state was preserved during reload.
    /// </summary>
    public bool StatePreserved { get; init; }
}

/// <summary>
/// Event data for plugin reload operations.
/// </summary>
public sealed class PluginReloadEvent
{
    /// <summary>
    /// The plugin ID being reloaded.
    /// </summary>
    public required string PluginId { get; init; }

    /// <summary>
    /// The phase of the reload operation.
    /// </summary>
    public ReloadPhase Phase { get; init; }

    /// <summary>
    /// Timestamp of the event.
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Additional context data.
    /// </summary>
    public Dictionary<string, object> Data { get; init; } = new();
}

/// <summary>
/// Phases of the plugin reload process.
/// </summary>
public enum ReloadPhase
{
    /// <summary>Beginning reload process.</summary>
    Starting,
    /// <summary>Draining active connections.</summary>
    Draining,
    /// <summary>Preserving plugin state.</summary>
    PreservingState,
    /// <summary>Unloading old assembly.</summary>
    Unloading,
    /// <summary>Loading new assembly.</summary>
    Loading,
    /// <summary>Restoring plugin state.</summary>
    RestoringState,
    /// <summary>Reload completed successfully.</summary>
    Completed,
    /// <summary>Rolling back due to failure.</summary>
    RollingBack,
    /// <summary>Reload failed.</summary>
    Failed
}

/// <summary>
/// Interface for hot-reloading plugins without system restart.
/// Provides assembly-level isolation and state preservation.
/// </summary>
public interface IPluginReloader
{
    /// <summary>
    /// Reloads a specific plugin by ID.
    /// </summary>
    /// <param name="pluginId">The unique identifier of the plugin.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the reload operation.</returns>
    Task<ReloadResult> ReloadPluginAsync(string pluginId, CancellationToken ct = default);

    /// <summary>
    /// Reloads all registered plugins.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Aggregate result of all reload operations.</returns>
    Task<ReloadResult> ReloadAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Checks if a plugin can be safely reloaded.
    /// </summary>
    /// <param name="pluginId">The plugin ID to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the plugin can be reloaded.</returns>
    Task<bool> CanReloadAsync(string pluginId, CancellationToken ct = default);

    /// <summary>
    /// Gets the current reload status for a plugin.
    /// </summary>
    /// <param name="pluginId">The plugin ID.</param>
    /// <returns>Current reload phase or null if not reloading.</returns>
    ReloadPhase? GetReloadStatus(string pluginId);

    /// <summary>
    /// Event fired before a plugin begins reloading.
    /// </summary>
    event Action<PluginReloadEvent>? OnPluginReloading;

    /// <summary>
    /// Event fired after a plugin completes reloading.
    /// </summary>
    event Action<PluginReloadEvent>? OnPluginReloaded;
}

/// <summary>
/// Interface for plugins that support state preservation during hot reload.
/// </summary>
public interface IReloadablePlugin
{
    /// <summary>
    /// Exports the current state for preservation.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Serialized state data.</returns>
    Task<byte[]> ExportStateAsync(CancellationToken ct = default);

    /// <summary>
    /// Imports previously exported state after reload.
    /// </summary>
    /// <param name="state">The serialized state data.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ImportStateAsync(byte[] state, CancellationToken ct = default);

    /// <summary>
    /// Prepares the plugin for unloading (drain connections, flush buffers).
    /// </summary>
    /// <param name="timeout">Maximum time to wait for draining.</param>
    /// <param name="ct">Cancellation token.</param>
    Task PrepareForUnloadAsync(TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>
/// Isolated assembly load context for plugin isolation.
/// </summary>
public sealed class PluginAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    /// <summary>
    /// Gets the plugin ID associated with this context.
    /// </summary>
    public string PluginId { get; }

    /// <summary>
    /// Gets the assembly path for this plugin.
    /// </summary>
    public string AssemblyPath { get; }

    /// <summary>
    /// Creates a new plugin assembly load context.
    /// </summary>
    /// <param name="pluginId">The plugin identifier.</param>
    /// <param name="assemblyPath">Path to the plugin assembly.</param>
    public PluginAssemblyLoadContext(string pluginId, string assemblyPath)
        : base(name: pluginId, isCollectible: true)
    {
        PluginId = pluginId;
        AssemblyPath = assemblyPath;
        _resolver = new AssemblyDependencyResolver(assemblyPath);
    }

    /// <inheritdoc/>
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        return assemblyPath != null ? LoadFromAssemblyPath(assemblyPath) : null;
    }

    /// <inheritdoc/>
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return libraryPath != null ? LoadUnmanagedDllFromPath(libraryPath) : IntPtr.Zero;
    }
}

/// <summary>
/// Version compatibility check result.
/// </summary>
public sealed class VersionCompatibility
{
    /// <summary>Whether versions are compatible.</summary>
    public bool IsCompatible { get; init; }
    /// <summary>Reason for incompatibility.</summary>
    public string? IncompatibilityReason { get; init; }
    /// <summary>Required minimum version.</summary>
    public Version? RequiredMinVersion { get; init; }
    /// <summary>Current version.</summary>
    public Version? CurrentVersion { get; init; }
}

/// <summary>
/// Manages hot plugin reload with assembly isolation and state preservation.
/// </summary>
public sealed class PluginReloadManager : IPluginReloader, IAsyncDisposable
{
    private readonly BoundedDictionary<string, PluginAssemblyLoadContext> _contexts = new BoundedDictionary<string, PluginAssemblyLoadContext>(1000);
    private readonly BoundedDictionary<string, ReloadPhase> _reloadStatus = new BoundedDictionary<string, ReloadPhase>(1000);
    private readonly BoundedDictionary<string, byte[]> _preservedState = new BoundedDictionary<string, byte[]>(1000);
    private readonly BoundedDictionary<string, WeakReference<Assembly>> _loadedAssemblies = new BoundedDictionary<string, WeakReference<Assembly>>(1000);
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private readonly TimeSpan _drainTimeout;
    private readonly int _maxRetryAttempts;
    private volatile bool _disposed;

    /// <inheritdoc/>
    public event Action<PluginReloadEvent>? OnPluginReloading;

    /// <inheritdoc/>
    public event Action<PluginReloadEvent>? OnPluginReloaded;

    /// <summary>
    /// Creates a new PluginReloadManager.
    /// </summary>
    /// <param name="drainTimeout">Timeout for draining connections during reload.</param>
    /// <param name="maxRetryAttempts">Maximum retry attempts for failed reloads.</param>
    public PluginReloadManager(TimeSpan? drainTimeout = null, int maxRetryAttempts = 3)
    {
        _drainTimeout = drainTimeout ?? TimeSpan.FromSeconds(30);
        _maxRetryAttempts = maxRetryAttempts;
    }

    /// <summary>
    /// Registers a plugin for reload management.
    /// </summary>
    /// <param name="pluginId">The plugin identifier.</param>
    /// <param name="assemblyPath">Path to the plugin assembly.</param>
    /// <returns>The loaded assembly.</returns>
    public Assembly RegisterPlugin(string pluginId, string assemblyPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!File.Exists(assemblyPath))
            throw new FileNotFoundException($"Plugin assembly not found: {assemblyPath}");

        var context = new PluginAssemblyLoadContext(pluginId, assemblyPath);
        var assembly = context.LoadFromAssemblyPath(assemblyPath);

        _contexts[pluginId] = context;
        _loadedAssemblies[pluginId] = new WeakReference<Assembly>(assembly);

        return assembly;
    }

    /// <inheritdoc/>
    public async Task<ReloadResult> ReloadPluginAsync(string pluginId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        if (!_contexts.TryGetValue(pluginId, out var oldContext))
        {
            return new ReloadResult
            {
                Success = false,
                PluginId = pluginId,
                Error = $"Plugin '{pluginId}' not registered for reload management"
            };
        }

        await _reloadLock.WaitAsync(ct);
        try
        {
            return await ReloadPluginInternalAsync(pluginId, oldContext, sw, ct);
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    private async Task<ReloadResult> ReloadPluginInternalAsync(
        string pluginId,
        PluginAssemblyLoadContext oldContext,
        System.Diagnostics.Stopwatch sw,
        CancellationToken ct)
    {
        Version? previousVersion = null;
        Version? newVersion = null;
        var statePreserved = false;

        try
        {
            // Phase: Starting
            UpdatePhase(pluginId, ReloadPhase.Starting);
            RaiseReloading(pluginId, ReloadPhase.Starting);

            // Get previous version
            if (_loadedAssemblies.TryGetValue(pluginId, out var weakRef) &&
                weakRef.TryGetTarget(out var oldAssembly))
            {
                previousVersion = oldAssembly.GetName().Version;
            }

            // Phase: Version compatibility check
            var compatibility = await CheckVersionCompatibilityAsync(pluginId, oldContext.AssemblyPath, ct);
            if (!compatibility.IsCompatible)
            {
                throw new InvalidOperationException(
                    $"Version incompatibility: {compatibility.IncompatibilityReason}");
            }

            // Phase: Draining
            UpdatePhase(pluginId, ReloadPhase.Draining);
            await DrainPluginConnectionsAsync(pluginId, ct);

            // Phase: Preserving State
            UpdatePhase(pluginId, ReloadPhase.PreservingState);
            statePreserved = await TryPreserveStateAsync(pluginId, ct);

            // Phase: Unloading
            UpdatePhase(pluginId, ReloadPhase.Unloading);
            _contexts.TryRemove(pluginId, out _);
            oldContext.Unload();

            // Wait for GC to collect the old assembly
            for (int i = 0; i < 10 && oldContext.IsAlive(); i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(100, ct);
            }

            // Phase: Loading
            UpdatePhase(pluginId, ReloadPhase.Loading);
            var newContext = new PluginAssemblyLoadContext(pluginId, oldContext.AssemblyPath);
            var newAssembly = newContext.LoadFromAssemblyPath(oldContext.AssemblyPath);
            newVersion = newAssembly.GetName().Version;

            _contexts[pluginId] = newContext;
            _loadedAssemblies[pluginId] = new WeakReference<Assembly>(newAssembly);

            // Phase: Restoring State
            if (statePreserved)
            {
                UpdatePhase(pluginId, ReloadPhase.RestoringState);
                await TryRestoreStateAsync(pluginId, ct);
            }

            // Phase: Completed
            UpdatePhase(pluginId, ReloadPhase.Completed);
            RaiseReloaded(pluginId, ReloadPhase.Completed);
            _reloadStatus.TryRemove(pluginId, out _);

            return new ReloadResult
            {
                Success = true,
                PluginId = pluginId,
                PreviousVersion = previousVersion,
                NewVersion = newVersion,
                Duration = sw.Elapsed,
                StatePreserved = statePreserved
            };
        }
        catch (Exception ex)
        {
            // Phase: Rolling Back
            UpdatePhase(pluginId, ReloadPhase.RollingBack);
            await TryRollbackAsync(pluginId, oldContext, ct);

            // Phase: Failed
            UpdatePhase(pluginId, ReloadPhase.Failed);
            RaiseReloaded(pluginId, ReloadPhase.Failed);
            _reloadStatus.TryRemove(pluginId, out _);

            return new ReloadResult
            {
                Success = false,
                PluginId = pluginId,
                Error = ex.Message,
                PreviousVersion = previousVersion,
                Duration = sw.Elapsed,
                StatePreserved = false
            };
        }
    }

    /// <inheritdoc/>
    public async Task<ReloadResult> ReloadAllAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var pluginIds = _contexts.Keys.ToList();
        var failures = new List<string>();
        var successes = 0;

        foreach (var pluginId in pluginIds)
        {
            ct.ThrowIfCancellationRequested();
            var result = await ReloadPluginAsync(pluginId, ct);

            if (result.Success)
                successes++;
            else
                failures.Add($"{pluginId}: {result.Error}");
        }

        return new ReloadResult
        {
            Success = failures.Count == 0,
            PluginId = "*",
            Duration = sw.Elapsed,
            Error = failures.Count > 0 ? string.Join("; ", failures) : null
        };
    }

    /// <inheritdoc/>
    public Task<bool> CanReloadAsync(string pluginId, CancellationToken ct = default)
    {
        if (!_contexts.ContainsKey(pluginId))
            return Task.FromResult(false);

        if (_reloadStatus.ContainsKey(pluginId))
            return Task.FromResult(false); // Already reloading

        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    public ReloadPhase? GetReloadStatus(string pluginId)
    {
        return _reloadStatus.TryGetValue(pluginId, out var phase) ? phase : null;
    }

    private Task<VersionCompatibility> CheckVersionCompatibilityAsync(
        string pluginId, string assemblyPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            // Load assembly metadata via reflection-only context to check SDK version compatibility
            // without loading the assembly into the execution context
            var assemblyName = AssemblyName.GetAssemblyName(assemblyPath);
            if (assemblyName.Version == null)
            {
                return Task.FromResult(new VersionCompatibility
                {
                    IsCompatible = false,
                    IncompatibilityReason = $"Plugin '{pluginId}' assembly has no version metadata."
                });
            }

            // Check if existing loaded assembly has same or older version (backward compat)
            if (_loadedAssemblies.TryGetValue(pluginId, out var weakRef) &&
                weakRef.TryGetTarget(out var currentAssembly))
            {
                var currentVersion = currentAssembly.GetName().Version;
                if (currentVersion != null && assemblyName.Version < currentVersion)
                {
                    return Task.FromResult(new VersionCompatibility
                    {
                        IsCompatible = false,
                        IncompatibilityReason = $"Cannot downgrade plugin '{pluginId}' from v{currentVersion} to v{assemblyName.Version}."
                    });
                }
            }

            return Task.FromResult(new VersionCompatibility { IsCompatible = true });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new VersionCompatibility
            {
                IsCompatible = false,
                IncompatibilityReason = $"Failed to read assembly metadata: {ex.Message}"
            });
        }
    }

    private async Task DrainPluginConnectionsAsync(string pluginId, CancellationToken ct)
    {
        // Find IReloadablePlugin instance from loaded assembly and call PrepareForUnloadAsync
        var plugin = FindReloadablePlugin(pluginId);
        if (plugin != null)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_drainTimeout);
            try
            {
                await plugin.PrepareForUnloadAsync(_drainTimeout, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                System.Diagnostics.Debug.WriteLine($"Plugin '{pluginId}' drain timed out after {_drainTimeout}; proceeding with unload.");
            }
        }
        else
        {
            // No IReloadablePlugin — brief delay for graceful shutdown
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }

    private async Task<bool> TryPreserveStateAsync(string pluginId, CancellationToken ct)
    {
        try
        {
            var plugin = FindReloadablePlugin(pluginId);
            if (plugin != null)
            {
                var state = await plugin.ExportStateAsync(ct).ConfigureAwait(false);
                _preservedState[pluginId] = state;
            }
            else
            {
                _preservedState[pluginId] = Array.Empty<byte>();
            }
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to preserve state for plugin '{pluginId}': {ex.Message}");
            return false;
        }
    }

    private async Task TryRestoreStateAsync(string pluginId, CancellationToken ct)
    {
        if (_preservedState.TryRemove(pluginId, out var state) && state.Length > 0)
        {
            var plugin = FindReloadablePlugin(pluginId);
            if (plugin != null)
            {
                await plugin.ImportStateAsync(state, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Finds an IReloadablePlugin instance from the loaded assembly for the given plugin ID.
    /// Returns null if the plugin doesn't implement IReloadablePlugin.
    /// </summary>
    private IReloadablePlugin? FindReloadablePlugin(string pluginId)
    {
        if (_loadedAssemblies.TryGetValue(pluginId, out var weakRef) &&
            weakRef.TryGetTarget(out var assembly))
        {
            try
            {
                var reloadableType = assembly.GetTypes()
                    .FirstOrDefault(t => typeof(IReloadablePlugin).IsAssignableFrom(t) && !t.IsAbstract);
                if (reloadableType != null)
                {
                    return Activator.CreateInstance(reloadableType) as IReloadablePlugin;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to find IReloadablePlugin in '{pluginId}': {ex.Message}");
            }
        }
        return null;
    }

    private async Task TryRollbackAsync(string pluginId, PluginAssemblyLoadContext oldContext, CancellationToken ct)
    {
        try
        {
            // Re-register the old context if rollback is needed
            if (!_contexts.ContainsKey(pluginId))
            {
                _contexts[pluginId] = oldContext;
            }
            await Task.CompletedTask;
        }
        catch
        {
            // Rollback failed - log but don't throw
        }
    }

    private void UpdatePhase(string pluginId, ReloadPhase phase)
    {
        _reloadStatus[pluginId] = phase;
    }

    private void RaiseReloading(string pluginId, ReloadPhase phase)
    {
        OnPluginReloading?.Invoke(new PluginReloadEvent
        {
            PluginId = pluginId,
            Phase = phase
        });
    }

    private void RaiseReloaded(string pluginId, ReloadPhase phase)
    {
        OnPluginReloaded?.Invoke(new PluginReloadEvent
        {
            PluginId = pluginId,
            Phase = phase
        });
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var context in _contexts.Values)
        {
            try { context.Unload(); }
            catch { /* Ignore disposal errors */ }
        }

        _contexts.Clear();
        _reloadLock.Dispose();
        await Task.CompletedTask;
    }
}

/// <summary>
/// Extension methods for AssemblyLoadContext.
/// </summary>
public static class AssemblyLoadContextExtensions
{
    /// <summary>
    /// Checks if the AssemblyLoadContext is still alive (not fully unloaded).
    /// </summary>
    public static bool IsAlive(this AssemblyLoadContext context)
    {
        var weakRef = new WeakReference(context);
        return weakRef.IsAlive;
    }
}

#endregion

#region K3: Memory Pressure Management

/// <summary>
/// Levels of memory pressure indicating system memory status.
/// </summary>
public enum MemoryPressureLevel
{
    /// <summary>Memory usage is normal (below 60%).</summary>
    Normal,
    /// <summary>Memory usage is elevated (60-75%).</summary>
    Elevated,
    /// <summary>Memory usage is high (75-90%).</summary>
    High,
    /// <summary>Memory usage is critical (above 90%).</summary>
    Critical
}

/// <summary>
/// Interface for monitoring and responding to memory pressure.
/// Kernel monitors memory, plugins respond by releasing resources.
/// </summary>
public interface IMemoryPressureMonitor
{
    /// <summary>
    /// Gets the current memory pressure level.
    /// </summary>
    MemoryPressureLevel CurrentLevel { get; }

    /// <summary>
    /// Gets whether request throttling is recommended.
    /// </summary>
    bool ShouldThrottle { get; }

    /// <summary>
    /// Gets current memory statistics.
    /// </summary>
    MemoryStats GetStats();

    /// <summary>
    /// Event fired when memory pressure level changes.
    /// </summary>
    event Action<MemoryPressureLevel>? OnPressureChanged;

    /// <summary>
    /// Registers a handler to be called when memory pressure occurs.
    /// </summary>
    /// <param name="handler">The handler to register.</param>
    /// <returns>Disposable to unregister the handler.</returns>
    IDisposable RegisterPressureHandler(Func<MemoryPressureLevel, Task> handler);

    /// <summary>
    /// Forces a memory collection if pressure is elevated.
    /// </summary>
    /// <param name="aggressive">If true, performs a full blocking GC.</param>
    void RequestCollection(bool aggressive = false);
}

/// <summary>
/// Current memory statistics.
/// </summary>
public sealed class MemoryStats
{
    /// <summary>Total allocated bytes in managed heap.</summary>
    public long TotalAllocatedBytes { get; init; }
    /// <summary>Working set size in bytes.</summary>
    public long WorkingSetBytes { get; init; }
    /// <summary>Available physical memory in bytes.</summary>
    public long AvailableMemoryBytes { get; init; }
    /// <summary>Memory usage as a percentage (0-100).</summary>
    public double MemoryUsagePercent { get; init; }
    /// <summary>GC generation 0 collection count.</summary>
    public int Gen0Collections { get; init; }
    /// <summary>GC generation 1 collection count.</summary>
    public int Gen1Collections { get; init; }
    /// <summary>GC generation 2 collection count.</summary>
    public int Gen2Collections { get; init; }
    /// <summary>Time spent in GC as a percentage.</summary>
    public double GCTimePercent { get; init; }
    /// <summary>Current pressure level.</summary>
    public MemoryPressureLevel Level { get; init; }
}

/// <summary>
/// Monitors memory pressure and notifies components to release resources.
/// Uses GC notifications and periodic sampling for accurate pressure detection.
/// </summary>
public sealed class MemoryPressureMonitor : IMemoryPressureMonitor, IDisposable
{
    private readonly List<Func<MemoryPressureLevel, Task>> _handlers = new();
    private readonly object _handlersLock = new();
    private readonly Timer _monitorTimer;
    private readonly double _elevatedThreshold;
    private readonly double _highThreshold;
    private readonly double _criticalThreshold;

    private MemoryPressureLevel _currentLevel = MemoryPressureLevel.Normal;
    private bool _disposed;

    /// <inheritdoc/>
    public event Action<MemoryPressureLevel>? OnPressureChanged;

    /// <inheritdoc/>
    public MemoryPressureLevel CurrentLevel => _currentLevel;

    /// <inheritdoc/>
    public bool ShouldThrottle => _currentLevel >= MemoryPressureLevel.High;

    /// <summary>
    /// Creates a new MemoryPressureMonitor with default thresholds.
    /// </summary>
    /// <param name="elevatedThreshold">Percentage threshold for elevated pressure (default 60%).</param>
    /// <param name="highThreshold">Percentage threshold for high pressure (default 80%).</param>
    /// <param name="criticalThreshold">Percentage threshold for critical pressure (default 90%).</param>
    /// <param name="checkIntervalMs">Interval between pressure checks in milliseconds.</param>
    public MemoryPressureMonitor(
        double elevatedThreshold = 60.0,
        double highThreshold = 80.0,
        double criticalThreshold = 90.0,
        int checkIntervalMs = 5000)
    {
        _elevatedThreshold = elevatedThreshold;
        _highThreshold = highThreshold;
        _criticalThreshold = criticalThreshold;

        // Register for full GC notifications
        try
        {
            GC.RegisterForFullGCNotification(10, 10);
        }
        catch
        {
            // GC notification registration may not be supported
        }

        _monitorTimer = new Timer(CheckMemoryPressure, null, 0, checkIntervalMs);
    }

    /// <inheritdoc/>
    public MemoryStats GetStats()
    {
        var gcInfo = GC.GetGCMemoryInfo();
        // Dispose the Process handle to release OS resources — GetCurrentProcess() returns a new handle each call
        using var process = System.Diagnostics.Process.GetCurrentProcess();

        var totalMemory = gcInfo.TotalAvailableMemoryBytes;
        var usedMemory = process.WorkingSet64;
        var usagePercent = totalMemory > 0 ? (usedMemory * 100.0 / totalMemory) : 0;

        return new MemoryStats
        {
            TotalAllocatedBytes = GC.GetTotalMemory(forceFullCollection: false),
            WorkingSetBytes = usedMemory,
            AvailableMemoryBytes = gcInfo.TotalAvailableMemoryBytes - usedMemory,
            MemoryUsagePercent = usagePercent,
            Gen0Collections = GC.CollectionCount(0),
            Gen1Collections = GC.CollectionCount(1),
            Gen2Collections = GC.CollectionCount(2),
            GCTimePercent = gcInfo.PauseTimePercentage,
            Level = _currentLevel
        };
    }

    /// <inheritdoc/>
    public IDisposable RegisterPressureHandler(Func<MemoryPressureLevel, Task> handler)
    {
        lock (_handlersLock)
        {
            _handlers.Add(handler);
        }

        return new HandlerUnsubscriber(() =>
        {
            lock (_handlersLock)
            {
                _handlers.Remove(handler);
            }
        });
    }

    /// <inheritdoc/>
    public void RequestCollection(bool aggressive = false)
    {
        if (aggressive)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
        else
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);
        }
    }

    private void CheckMemoryPressure(object? state)
    {
        if (_disposed) return;

        try
        {
            var stats = GetStats();
            var newLevel = DetermineLevel(stats.MemoryUsagePercent);

            if (newLevel != _currentLevel)
            {
                var oldLevel = _currentLevel;
                _currentLevel = newLevel;

                OnPressureChanged?.Invoke(newLevel);
                NotifyHandlersAsync(newLevel).ConfigureAwait(false);

                // Auto-trigger collection on escalation
                if (newLevel > oldLevel && newLevel >= MemoryPressureLevel.High)
                {
                    RequestCollection(aggressive: newLevel == MemoryPressureLevel.Critical);
                }
            }
        }
        catch
        {
            // Ignore monitoring errors
        }
    }

    private MemoryPressureLevel DetermineLevel(double usagePercent)
    {
        if (usagePercent >= _criticalThreshold) return MemoryPressureLevel.Critical;
        if (usagePercent >= _highThreshold) return MemoryPressureLevel.High;
        if (usagePercent >= _elevatedThreshold) return MemoryPressureLevel.Elevated;
        return MemoryPressureLevel.Normal;
    }

    private async Task NotifyHandlersAsync(MemoryPressureLevel level)
    {
        List<Func<MemoryPressureLevel, Task>> handlersCopy;
        lock (_handlersLock)
        {
            handlersCopy = _handlers.ToList();
        }

        foreach (var handler in handlersCopy)
        {
            try
            {
                await handler(level).ConfigureAwait(false);
            }
            catch
            {
                // Ignore handler errors
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _monitorTimer.Dispose();

        try
        {
            GC.CancelFullGCNotification();
        }
        catch
        {
            // Ignore cancellation errors
        }
    }

    private sealed class HandlerUnsubscriber : IDisposable
    {
        private readonly Action _unsubscribe;
        public HandlerUnsubscriber(Action unsubscribe) => _unsubscribe = unsubscribe;
        public void Dispose() => _unsubscribe();
    }
}

#endregion

#region K5: Health Check Aggregation

/// <summary>
/// Health status of a component.
/// </summary>
public enum HealthStatus
{
    /// <summary>Component is fully operational.</summary>
    Healthy,
    /// <summary>Component is operational but with reduced capacity or warnings.</summary>
    Degraded,
    /// <summary>Component is not operational.</summary>
    Unhealthy
}

/// <summary>
/// Result of a health check operation.
/// </summary>
public sealed class HealthCheckResult
{
    /// <summary>Overall health status.</summary>
    public HealthStatus Status { get; init; }

    /// <summary>Human-readable status message.</summary>
    public string? Message { get; init; }

    /// <summary>Additional health check data.</summary>
    public IReadOnlyDictionary<string, object> Data { get; init; } = new Dictionary<string, object>();

    /// <summary>Time taken to perform the health check.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Exception if the check failed.</summary>
    public Exception? Exception { get; init; }

    /// <summary>Timestamp when the check was performed.</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>Creates a healthy result.</summary>
    public static HealthCheckResult Healthy(string? message = null) =>
        new() { Status = HealthStatus.Healthy, Message = message ?? "Healthy" };

    /// <summary>Creates a degraded result.</summary>
    public static HealthCheckResult Degraded(string message) =>
        new() { Status = HealthStatus.Degraded, Message = message };

    /// <summary>Creates an unhealthy result.</summary>
    public static HealthCheckResult Unhealthy(string message, Exception? ex = null) =>
        new() { Status = HealthStatus.Unhealthy, Message = message, Exception = ex };
}

/// <summary>
/// Interface for components that provide health checks.
/// Plugins implement this interface and kernel aggregates results.
/// </summary>
public interface IHealthCheck
{
    /// <summary>
    /// Gets the unique name of this health check.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets optional tags for categorizing this health check.
    /// </summary>
    IEnumerable<string> Tags => Array.Empty<string>();

    /// <summary>
    /// Performs the health check.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The health check result.</returns>
    Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct = default);
}

/// <summary>
/// Aggregated health check results from all registered components.
/// </summary>
public sealed class AggregatedHealthResult
{
    /// <summary>Overall system health status.</summary>
    public HealthStatus OverallStatus { get; init; }

    /// <summary>Individual component health results.</summary>
    public IReadOnlyDictionary<string, HealthCheckResult> Results { get; init; }
        = new Dictionary<string, HealthCheckResult>();

    /// <summary>Total duration of all health checks.</summary>
    public TimeSpan TotalDuration { get; init; }

    /// <summary>Timestamp of the aggregated check.</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Options for health check aggregation.
/// </summary>
public sealed class HealthCheckOptions
{
    /// <summary>Timeout for individual health checks.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Whether to run health checks in parallel.</summary>
    public bool Parallel { get; set; } = true;

    /// <summary>Cache duration for health check results.</summary>
    public TimeSpan? CacheDuration { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Filter to specific tags.</summary>
    public IEnumerable<string>? FilterTags { get; set; }
}

/// <summary>
/// Aggregates health checks from all registered components.
/// Provides liveness and readiness endpoints for Kubernetes-style deployments.
/// </summary>
public sealed class HealthCheckAggregator : IAsyncDisposable
{
    private readonly BoundedDictionary<string, IHealthCheck> _checks = new BoundedDictionary<string, IHealthCheck>(1000);
    private readonly BoundedDictionary<string, (HealthCheckResult Result, DateTime CachedAt)> _cache = new BoundedDictionary<string, (HealthCheckResult Result, DateTime CachedAt)>(1000);
    private readonly HealthCheckOptions _options;
    private volatile bool _disposed;

    /// <summary>
    /// Creates a new HealthCheckAggregator.
    /// </summary>
    /// <param name="options">Health check options.</param>
    public HealthCheckAggregator(HealthCheckOptions? options = null)
    {
        _options = options ?? new HealthCheckOptions();
    }

    /// <summary>
    /// Registers a health check.
    /// </summary>
    /// <param name="check">The health check to register.</param>
    public void Register(IHealthCheck check)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _checks[check.Name] = check;
    }

    /// <summary>
    /// Unregisters a health check.
    /// </summary>
    /// <param name="name">Name of the health check to remove.</param>
    public void Unregister(string name)
    {
        _checks.TryRemove(name, out _);
        _cache.TryRemove(name, out _);
    }

    /// <summary>
    /// Runs all registered health checks and aggregates results.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Aggregated health results.</returns>
    public async Task<AggregatedHealthResult> CheckAllAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var checks = GetFilteredChecks().ToList();
        var results = new Dictionary<string, HealthCheckResult>();

        if (_options.Parallel)
        {
            var tasks = checks.Select(c => RunCheckAsync(c, ct)).ToList();
            var completed = await Task.WhenAll(tasks);

            foreach (var (name, result) in completed)
            {
                results[name] = result;
            }
        }
        else
        {
            foreach (var check in checks)
            {
                ct.ThrowIfCancellationRequested();
                var (name, result) = await RunCheckAsync(check, ct);
                results[name] = result;
            }
        }

        var overallStatus = DetermineOverallStatus(results.Values);

        return new AggregatedHealthResult
        {
            OverallStatus = overallStatus,
            Results = results,
            TotalDuration = sw.Elapsed
        };
    }

    /// <summary>
    /// Quick liveness check - returns true if any check is healthy.
    /// Used for Kubernetes liveness probes.
    /// </summary>
    public async Task<bool> IsLiveAsync(CancellationToken ct = default)
    {
        var result = await CheckAllAsync(ct);
        return result.OverallStatus != HealthStatus.Unhealthy;
    }

    /// <summary>
    /// Readiness check - returns true if all checks are healthy or degraded.
    /// Used for Kubernetes readiness probes.
    /// </summary>
    public async Task<bool> IsReadyAsync(CancellationToken ct = default)
    {
        var result = await CheckAllAsync(ct);
        return result.OverallStatus == HealthStatus.Healthy;
    }

    private IEnumerable<IHealthCheck> GetFilteredChecks()
    {
        var checks = _checks.Values.AsEnumerable();

        if (_options.FilterTags?.Any() == true)
        {
            var filterTags = _options.FilterTags.ToHashSet();
            checks = checks.Where(c => c.Tags.Any(t => filterTags.Contains(t)));
        }

        return checks;
    }

    private async Task<(string Name, HealthCheckResult Result)> RunCheckAsync(
        IHealthCheck check, CancellationToken ct)
    {
        // Check cache first
        if (_options.CacheDuration.HasValue &&
            _cache.TryGetValue(check.Name, out var cached) &&
            DateTime.UtcNow - cached.CachedAt < _options.CacheDuration.Value)
        {
            return (check.Name, cached.Result);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_options.Timeout);

            var result = await check.CheckHealthAsync(timeoutCts.Token);
            var finalResult = new HealthCheckResult
            {
                Status = result.Status,
                Message = result.Message,
                Data = result.Data,
                Duration = sw.Elapsed,
                Exception = result.Exception,
                Timestamp = result.Timestamp
            };

            // Cache result
            if (_options.CacheDuration.HasValue)
            {
                _cache[check.Name] = (finalResult, DateTime.UtcNow);
            }

            return (check.Name, finalResult);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (check.Name, HealthCheckResult.Unhealthy($"Health check timed out after {_options.Timeout}"));
        }
        catch (Exception ex)
        {
            return (check.Name, HealthCheckResult.Unhealthy($"Health check failed: {ex.Message}", ex));
        }
    }

    private static HealthStatus DetermineOverallStatus(IEnumerable<HealthCheckResult> results)
    {
        var resultList = results.ToList();

        if (resultList.Count == 0)
            return HealthStatus.Healthy;

        if (resultList.Any(r => r.Status == HealthStatus.Unhealthy))
            return HealthStatus.Unhealthy;

        if (resultList.Any(r => r.Status == HealthStatus.Degraded))
            return HealthStatus.Degraded;

        return HealthStatus.Healthy;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _checks.Clear();
        _cache.Clear();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Built-in kernel health check for system-level metrics.
/// </summary>
public sealed class KernelHealthCheck : IHealthCheck
{
    private readonly IMemoryPressureMonitor _memoryMonitor;

    /// <inheritdoc/>
    public string Name => "kernel";

    /// <inheritdoc/>
    public IEnumerable<string> Tags => new[] { "system", "kernel" };

    /// <summary>
    /// Creates a new KernelHealthCheck.
    /// </summary>
    /// <param name="memoryMonitor">Memory pressure monitor instance.</param>
    public KernelHealthCheck(IMemoryPressureMonitor memoryMonitor)
    {
        _memoryMonitor = memoryMonitor;
    }

    /// <inheritdoc/>
    public Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct = default)
    {
        var memStats = _memoryMonitor.GetStats();
        var threadPool = ThreadPool.PendingWorkItemCount;

        var data = new Dictionary<string, object>
        {
            ["memory_usage_percent"] = memStats.MemoryUsagePercent,
            ["memory_pressure_level"] = memStats.Level.ToString(),
            ["gc_gen0_collections"] = memStats.Gen0Collections,
            ["gc_gen1_collections"] = memStats.Gen1Collections,
            ["gc_gen2_collections"] = memStats.Gen2Collections,
            ["threadpool_pending"] = threadPool
        };

        var status = memStats.Level switch
        {
            MemoryPressureLevel.Critical => HealthStatus.Unhealthy,
            MemoryPressureLevel.High => HealthStatus.Degraded,
            _ => HealthStatus.Healthy
        };

        var message = status switch
        {
            HealthStatus.Unhealthy => $"Memory pressure critical: {memStats.MemoryUsagePercent:F1}%",
            HealthStatus.Degraded => $"Memory pressure high: {memStats.MemoryUsagePercent:F1}%",
            _ => $"Kernel healthy, memory: {memStats.MemoryUsagePercent:F1}%"
        };

        return Task.FromResult(new HealthCheckResult
        {
            Status = status,
            Message = message,
            Data = data
        });
    }
}

#endregion

#region K6: Configuration Hot Reload

/// <summary>
/// Event data for configuration changes.
/// </summary>
public sealed class ConfigurationChangeEvent
{
    /// <summary>The configuration section that changed.</summary>
    public required string Section { get; init; }

    /// <summary>Keys that were added.</summary>
    public IReadOnlyList<string> AddedKeys { get; init; } = Array.Empty<string>();

    /// <summary>Keys that were modified.</summary>
    public IReadOnlyList<string> ModifiedKeys { get; init; } = Array.Empty<string>();

    /// <summary>Keys that were removed.</summary>
    public IReadOnlyList<string> RemovedKeys { get; init; } = Array.Empty<string>();

    /// <summary>Timestamp of the change.</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Interface for notifying components of configuration changes.
/// </summary>
public interface IConfigurationChangeNotifier
{
    /// <summary>
    /// Event fired when configuration changes are detected.
    /// </summary>
    event Action<ConfigurationChangeEvent>? OnConfigurationChanged;

    /// <summary>
    /// Registers a handler for configuration changes.
    /// </summary>
    /// <param name="section">Configuration section to watch (null for all).</param>
    /// <param name="handler">Handler to call when changes occur.</param>
    /// <returns>Disposable to unregister the handler.</returns>
    IDisposable RegisterChangeHandler(string? section, Func<ConfigurationChangeEvent, Task> handler);

    /// <summary>
    /// Forces a reload of configuration from source.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task ReloadAsync(CancellationToken ct = default);
}

/// <summary>
/// Result of configuration validation.
/// </summary>
public sealed class ConfigValidationResult
{
    /// <summary>Whether validation passed.</summary>
    public bool IsValid { get; init; }

    /// <summary>Validation errors.</summary>
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    /// <summary>Validation warnings.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Interface for validating configuration before applying.
/// </summary>
public interface IConfigurationValidator
{
    /// <summary>
    /// Validates configuration data.
    /// </summary>
    /// <param name="configData">The configuration data to validate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Validation result.</returns>
    Task<ConfigValidationResult> ValidateAsync(
        IReadOnlyDictionary<string, object> configData,
        CancellationToken ct = default);
}

/// <summary>
/// Monitors configuration files and reloads them when changes are detected.
/// Validates configuration before applying and supports rollback on failure.
/// </summary>
public sealed class ConfigurationHotReloader : IConfigurationChangeNotifier, IAsyncDisposable
{
    private readonly BoundedDictionary<string, object> _currentConfig = new BoundedDictionary<string, object>(1000);
    private readonly BoundedDictionary<string, object> _previousConfig = new BoundedDictionary<string, object>(1000);
    private readonly List<(string? Section, Func<ConfigurationChangeEvent, Task> Handler)> _handlers = new();
    private readonly object _handlersLock = new();
    private readonly FileSystemWatcher? _watcher;
    private readonly IConfigurationValidator? _validator;
    private readonly string? _configPath;
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private volatile bool _disposed;

    /// <inheritdoc/>
    public event Action<ConfigurationChangeEvent>? OnConfigurationChanged;

    /// <summary>
    /// Creates a new ConfigurationHotReloader.
    /// </summary>
    /// <param name="configPath">Path to the configuration file to watch.</param>
    /// <param name="validator">Optional validator for configuration changes.</param>
    public ConfigurationHotReloader(string? configPath = null, IConfigurationValidator? validator = null)
    {
        _configPath = configPath;
        _validator = validator;

        if (!string.IsNullOrEmpty(configPath) && File.Exists(configPath))
        {
            var directory = Path.GetDirectoryName(configPath)!;
            var fileName = Path.GetFileName(configPath);

            _watcher = new FileSystemWatcher(directory, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;

            // Load initial configuration
            _ = LoadConfigurationAsync();
        }
    }

    /// <inheritdoc/>
    public IDisposable RegisterChangeHandler(string? section, Func<ConfigurationChangeEvent, Task> handler)
    {
        lock (_handlersLock)
        {
            _handlers.Add((section, handler));
        }

        return new HandlerUnsubscriber(() =>
        {
            lock (_handlersLock)
            {
                _handlers.RemoveAll(h => h.Handler == handler);
            }
        });
    }

    /// <inheritdoc/>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _reloadLock.WaitAsync(ct);
        try
        {
            await LoadConfigurationAsync(ct);
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    /// <summary>
    /// Gets the current value of a configuration key.
    /// </summary>
    /// <typeparam name="T">Type to convert the value to.</typeparam>
    /// <param name="key">Configuration key.</param>
    /// <param name="defaultValue">Default value if key not found.</param>
    /// <returns>The configuration value or default.</returns>
    public T? GetValue<T>(string key, T? defaultValue = default)
    {
        if (_currentConfig.TryGetValue(key, out var value))
        {
            try
            {
                if (value is T typedValue)
                    return typedValue;

                if (value is JsonElement element)
                    return JsonSerializer.Deserialize<T>(element.GetRawText());

                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                return defaultValue;
            }
        }

        return defaultValue;
    }

    /// <summary>
    /// Sets a configuration value programmatically.
    /// </summary>
    /// <param name="key">Configuration key.</param>
    /// <param name="value">Value to set.</param>
    public void SetValue(string key, object value)
    {
        var section = key.Split(':').FirstOrDefault() ?? "root";
        var oldValue = _currentConfig.TryGetValue(key, out var existing) ? existing : null;

        _currentConfig[key] = value;

        var changeEvent = new ConfigurationChangeEvent
        {
            Section = section,
            ModifiedKeys = oldValue != null ? new[] { key } : Array.Empty<string>(),
            AddedKeys = oldValue == null ? new[] { key } : Array.Empty<string>()
        };

        NotifyHandlersAsync(changeEvent).ConfigureAwait(false);
        OnConfigurationChanged?.Invoke(changeEvent);
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce file changes
        Task.Delay(100).ContinueWith(_ => ReloadAsync());
    }

    private async Task LoadConfigurationAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_configPath) || !File.Exists(_configPath))
            return;

        try
        {
            // Read new configuration
            var json = await File.ReadAllTextAsync(_configPath, ct);
            var newConfig = JsonSerializer.Deserialize<Dictionary<string, object>>(json)
                ?? new Dictionary<string, object>();

            // Validate before applying
            if (_validator != null)
            {
                var validation = await _validator.ValidateAsync(newConfig, ct);
                if (!validation.IsValid)
                {
                    throw new InvalidOperationException(
                        $"Configuration validation failed: {string.Join(", ", validation.Errors)}");
                }
            }

            // Calculate changes
            var added = newConfig.Keys.Except(_currentConfig.Keys).ToList();
            var removed = _currentConfig.Keys.Except(newConfig.Keys).ToList();
            var modified = newConfig.Keys
                .Intersect(_currentConfig.Keys)
                .Where(k => !Equals(newConfig[k], _currentConfig[k]))
                .ToList();

            // Preserve previous config for rollback
            _previousConfig.Clear();
            foreach (var kvp in _currentConfig)
            {
                _previousConfig[kvp.Key] = kvp.Value;
            }

            // Apply new config
            _currentConfig.Clear();
            foreach (var kvp in newConfig)
            {
                _currentConfig[kvp.Key] = kvp.Value;
            }

            // Notify changes
            if (added.Any() || removed.Any() || modified.Any())
            {
                var changeEvent = new ConfigurationChangeEvent
                {
                    Section = "root",
                    AddedKeys = added,
                    RemovedKeys = removed,
                    ModifiedKeys = modified
                };

                await NotifyHandlersAsync(changeEvent);
                OnConfigurationChanged?.Invoke(changeEvent);
            }
        }
        catch (Exception ex)
        {
            // Rollback on failure
            _currentConfig.Clear();
            foreach (var kvp in _previousConfig)
            {
                _currentConfig[kvp.Key] = kvp.Value;
            }

            throw new InvalidOperationException($"Configuration reload failed, rolled back: {ex.Message}", ex);
        }
    }

    private async Task NotifyHandlersAsync(ConfigurationChangeEvent changeEvent)
    {
        List<(string? Section, Func<ConfigurationChangeEvent, Task> Handler)> handlersCopy;
        lock (_handlersLock)
        {
            handlersCopy = _handlers
                .Where(h => h.Section == null || h.Section == changeEvent.Section)
                .ToList();
        }

        foreach (var (_, handler) in handlersCopy)
        {
            try
            {
                await handler(changeEvent).ConfigureAwait(false);
            }
            catch
            {
                // Ignore handler errors
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFileChanged;
            _watcher.Created -= OnFileChanged;
            _watcher.Dispose();
        }

        _reloadLock.Dispose();
        await Task.CompletedTask;
    }

    private sealed class HandlerUnsubscriber : IDisposable
    {
        private readonly Action _unsubscribe;
        public HandlerUnsubscriber(Action unsubscribe) => _unsubscribe = unsubscribe;
        public void Dispose() => _unsubscribe();
    }
}

#endregion

#region K8: AI Provider Registry

/// <summary>
/// Capabilities supported by AI providers.
/// </summary>
[Flags]
public enum AICapability
{
    /// <summary>No capabilities.</summary>
    None = 0,
    /// <summary>Text generation/completion.</summary>
    TextGeneration = 1,
    /// <summary>Text embeddings.</summary>
    Embedding = 2,
    /// <summary>Image generation.</summary>
    ImageGeneration = 4,
    /// <summary>Image understanding/vision.</summary>
    Vision = 8,
    /// <summary>Speech to text.</summary>
    SpeechToText = 16,
    /// <summary>Text to speech.</summary>
    TextToSpeech = 32,
    /// <summary>Streaming responses.</summary>
    Streaming = 64,
    /// <summary>Function/tool calling.</summary>
    FunctionCalling = 128,
    /// <summary>Code generation.</summary>
    CodeGeneration = 256,
    /// <summary>Fine-tuning support.</summary>
    FineTuning = 512
}

/// <summary>
/// Metadata about an AI provider.
/// </summary>
public sealed class AIProviderInfo
{
    /// <summary>Unique provider identifier.</summary>
    public required string ProviderId { get; init; }

    /// <summary>Human-readable provider name.</summary>
    public required string Name { get; init; }

    /// <summary>Provider capabilities.</summary>
    public AICapability Capabilities { get; init; }

    /// <summary>Relative cost tier (1=cheapest, 5=most expensive).</summary>
    public int CostTier { get; init; } = 3;

    /// <summary>Average latency tier (1=fastest, 5=slowest).</summary>
    public int LatencyTier { get; init; } = 3;

    /// <summary>Maximum context window size in tokens.</summary>
    public int MaxContextTokens { get; init; }

    /// <summary>Whether the provider is currently available.</summary>
    public bool IsAvailable { get; set; } = true;

    /// <summary>Priority for selection (higher = preferred).</summary>
    public int Priority { get; init; } = 0;

    /// <summary>Provider-specific metadata.</summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; }
        = new Dictionary<string, object>();
}

/// <summary>
/// Interface for AI providers to implement.
/// </summary>
public interface IAIProviderRegistration
{
    /// <summary>Gets the provider information.</summary>
    AIProviderInfo Info { get; }

    /// <summary>Checks if the provider is currently available.</summary>
    Task<bool> CheckAvailabilityAsync(CancellationToken ct = default);
}

/// <summary>
/// Options for selecting an AI provider.
/// </summary>
public sealed class AIProviderSelectionOptions
{
    /// <summary>Required capabilities.</summary>
    public AICapability RequiredCapabilities { get; set; } = AICapability.None;

    /// <summary>Maximum cost tier (1-5).</summary>
    public int? MaxCostTier { get; set; }

    /// <summary>Maximum latency tier (1-5).</summary>
    public int? MaxLatencyTier { get; set; }

    /// <summary>Minimum context window size.</summary>
    public int? MinContextTokens { get; set; }

    /// <summary>Preferred provider IDs (in order of preference).</summary>
    public IList<string>? PreferredProviders { get; set; }

    /// <summary>Provider IDs to exclude.</summary>
    public ISet<string>? ExcludedProviders { get; set; }
}

/// <summary>
/// Registry for AI providers with capability-based selection.
/// Supports fallback chains and cost-aware selection.
/// </summary>
public interface IAIProviderRegistry
{
    /// <summary>
    /// Registers an AI provider.
    /// </summary>
    /// <param name="provider">Provider to register.</param>
    void Register(IAIProviderRegistration provider);

    /// <summary>
    /// Unregisters an AI provider.
    /// </summary>
    /// <param name="providerId">Provider ID to remove.</param>
    void Unregister(string providerId);

    /// <summary>
    /// Gets a provider by ID.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>The provider or null if not found.</returns>
    IAIProviderRegistration? GetProvider(string providerId);

    /// <summary>
    /// Gets the best provider matching the required capabilities.
    /// </summary>
    /// <param name="options">Selection options.</param>
    /// <returns>Best matching provider or null.</returns>
    IAIProviderRegistration? GetBestProvider(AIProviderSelectionOptions options);

    /// <summary>
    /// Gets all providers matching the required capabilities.
    /// </summary>
    /// <param name="capability">Required capability.</param>
    /// <returns>Matching providers ordered by priority.</returns>
    IEnumerable<IAIProviderRegistration> GetProviders(AICapability capability);

    /// <summary>
    /// Gets a fallback chain of providers for a capability.
    /// </summary>
    /// <param name="capability">Required capability.</param>
    /// <returns>Providers in fallback order.</returns>
    IEnumerable<IAIProviderRegistration> GetFallbackChain(AICapability capability);

    /// <summary>
    /// Event fired when a provider is registered.
    /// </summary>
    event Action<AIProviderInfo>? OnProviderRegistered;

    /// <summary>
    /// Event fired when a provider is unregistered.
    /// </summary>
    event Action<string>? OnProviderUnregistered;

    /// <summary>
    /// Event fired when a provider becomes unavailable.
    /// </summary>
    event Action<string>? OnProviderUnavailable;
}

/// <summary>
/// Default implementation of the AI Provider Registry.
/// </summary>
public sealed class AIProviderRegistry : IAIProviderRegistry, IAsyncDisposable
{
    private readonly BoundedDictionary<string, IAIProviderRegistration> _providers = new BoundedDictionary<string, IAIProviderRegistration>(1000);
    private readonly Timer _availabilityCheckTimer;
    private readonly TimeSpan _availabilityCheckInterval;
    private volatile bool _disposed;

    /// <inheritdoc/>
    public event Action<AIProviderInfo>? OnProviderRegistered;

    /// <inheritdoc/>
    public event Action<string>? OnProviderUnregistered;

    /// <inheritdoc/>
    public event Action<string>? OnProviderUnavailable;

    /// <summary>
    /// Creates a new AIProviderRegistry.
    /// </summary>
    /// <param name="availabilityCheckInterval">Interval for checking provider availability.</param>
    public AIProviderRegistry(TimeSpan? availabilityCheckInterval = null)
    {
        _availabilityCheckInterval = availabilityCheckInterval ?? TimeSpan.FromMinutes(1);
        _availabilityCheckTimer = new Timer(CheckAvailability, null, _availabilityCheckInterval, _availabilityCheckInterval);
    }

    /// <inheritdoc/>
    public void Register(IAIProviderRegistration provider)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _providers[provider.Info.ProviderId] = provider;
        OnProviderRegistered?.Invoke(provider.Info);
    }

    /// <inheritdoc/>
    public void Unregister(string providerId)
    {
        if (_providers.TryRemove(providerId, out _))
        {
            OnProviderUnregistered?.Invoke(providerId);
        }
    }

    /// <inheritdoc/>
    public IAIProviderRegistration? GetProvider(string providerId)
    {
        return _providers.TryGetValue(providerId, out var provider) ? provider : null;
    }

    /// <inheritdoc/>
    public IAIProviderRegistration? GetBestProvider(AIProviderSelectionOptions options)
    {
        var candidates = _providers.Values
            .Where(p => p.Info.IsAvailable)
            .Where(p => (p.Info.Capabilities & options.RequiredCapabilities) == options.RequiredCapabilities)
            .Where(p => !options.MaxCostTier.HasValue || p.Info.CostTier <= options.MaxCostTier)
            .Where(p => !options.MaxLatencyTier.HasValue || p.Info.LatencyTier <= options.MaxLatencyTier)
            .Where(p => !options.MinContextTokens.HasValue || p.Info.MaxContextTokens >= options.MinContextTokens)
            .Where(p => options.ExcludedProviders?.Contains(p.Info.ProviderId) != true)
            .ToList();

        if (candidates.Count == 0)
            return null;

        // Check preferred providers first
        if (options.PreferredProviders?.Any() == true)
        {
            foreach (var preferred in options.PreferredProviders)
            {
                var match = candidates.FirstOrDefault(c => c.Info.ProviderId == preferred);
                if (match != null)
                    return match;
            }
        }

        // Return highest priority provider
        return candidates.OrderByDescending(p => p.Info.Priority)
            .ThenBy(p => p.Info.CostTier)
            .ThenBy(p => p.Info.LatencyTier)
            .FirstOrDefault();
    }

    /// <inheritdoc/>
    public IEnumerable<IAIProviderRegistration> GetProviders(AICapability capability)
    {
        return _providers.Values
            .Where(p => p.Info.IsAvailable)
            .Where(p => (p.Info.Capabilities & capability) == capability)
            .OrderByDescending(p => p.Info.Priority)
            .ThenBy(p => p.Info.CostTier);
    }

    /// <inheritdoc/>
    public IEnumerable<IAIProviderRegistration> GetFallbackChain(AICapability capability)
    {
        return _providers.Values
            .Where(p => (p.Info.Capabilities & capability) == capability)
            .OrderByDescending(p => p.Info.IsAvailable)
            .ThenByDescending(p => p.Info.Priority)
            .ThenBy(p => p.Info.CostTier)
            .ThenBy(p => p.Info.LatencyTier);
    }

    private void CheckAvailability(object? state)
    {
        // Timer callbacks must be void, so we use Task.Run for async work
        _ = Task.Run(async () =>
        {
            if (_disposed) return;

            foreach (var provider in _providers.Values)
            {
                try
                {
                    var wasAvailable = provider.Info.IsAvailable;
                    var isAvailable = await provider.CheckAvailabilityAsync();

                    provider.Info.IsAvailable = isAvailable;

                    if (wasAvailable && !isAvailable)
                    {
                        OnProviderUnavailable?.Invoke(provider.Info.ProviderId);
                    }
                }
                catch
                {
                    provider.Info.IsAvailable = false;
                    OnProviderUnavailable?.Invoke(provider.Info.ProviderId);
                }
            }
        });
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _availabilityCheckTimer.DisposeAsync();
        _providers.Clear();
    }
}

#endregion

#region K10: Rate Limiting Framework

/// <summary>
/// Result of a rate limit check.
/// </summary>
public sealed class RateLimitResult
{
    /// <summary>Whether the request is allowed.</summary>
    public bool IsAllowed { get; init; }

    /// <summary>Number of tokens remaining.</summary>
    public int RemainingTokens { get; init; }

    /// <summary>Time until tokens reset.</summary>
    public TimeSpan? ResetAfter { get; init; }

    /// <summary>Retry after this duration if rate limited.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>Name of the rate limit policy that was applied.</summary>
    public string? PolicyName { get; init; }
}

/// <summary>
/// Configuration for a rate limit policy.
/// </summary>
public sealed class RateLimitPolicy
{
    /// <summary>Policy name.</summary>
    public required string Name { get; init; }

    /// <summary>Maximum tokens in the bucket.</summary>
    public int MaxTokens { get; init; } = 100;

    /// <summary>Tokens added per refill interval.</summary>
    public int RefillTokens { get; init; } = 10;

    /// <summary>Interval between token refills.</summary>
    public TimeSpan RefillInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Tokens consumed per request.</summary>
    public int TokensPerRequest { get; init; } = 1;

    /// <summary>Whether to queue requests when rate limited.</summary>
    public bool QueueExcessRequests { get; init; }

    /// <summary>Maximum queue wait time.</summary>
    public TimeSpan MaxQueueWait { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Interface for rate limiting operations.
/// Kernel enforces limits, plugins configure policies.
/// </summary>
public interface IRateLimiter
{
    /// <summary>
    /// Acquires tokens from the rate limiter.
    /// </summary>
    /// <param name="key">The rate limit key (e.g., user ID, operation type).</param>
    /// <param name="tokens">Number of tokens to acquire.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Rate limit result.</returns>
    Task<RateLimitResult> AcquireAsync(string key, int tokens = 1, CancellationToken ct = default);

    /// <summary>
    /// Checks if a request would be allowed without consuming tokens.
    /// </summary>
    /// <param name="key">The rate limit key.</param>
    /// <param name="tokens">Number of tokens to check.</param>
    /// <returns>Rate limit result.</returns>
    RateLimitResult Check(string key, int tokens = 1);

    /// <summary>
    /// Resets the rate limit for a key.
    /// </summary>
    /// <param name="key">The rate limit key to reset.</param>
    void Reset(string key);

    /// <summary>
    /// Gets the current status for a rate limit key.
    /// </summary>
    /// <param name="key">The rate limit key.</param>
    /// <returns>Current rate limit status.</returns>
    RateLimitResult GetStatus(string key);

    /// <summary>
    /// Event fired when a rate limit is exceeded.
    /// </summary>
    event Action<string, RateLimitResult>? OnRateLimitExceeded;
}

/// <summary>
/// Token bucket state for a rate limit key.
/// </summary>
internal sealed class TokenBucket
{
    public int Tokens { get; set; }
    public DateTime LastRefill { get; set; }
    public readonly object Lock = new();
}

/// <summary>
/// Token bucket rate limiter implementation.
/// Provides per-key rate limiting with configurable refill rates.
/// </summary>
public sealed class TokenBucketRateLimiter : IRateLimiter, IDisposable
{
    private readonly BoundedDictionary<string, TokenBucket> _buckets = new BoundedDictionary<string, TokenBucket>(1000);
    private readonly ConcurrentDictionary<string, Channel<TaskCompletionSource<RateLimitResult>>> _queues = new();
    private readonly RateLimitPolicy _policy;
    private readonly Timer _cleanupTimer;
    private volatile bool _disposed;

    /// <inheritdoc/>
    public event Action<string, RateLimitResult>? OnRateLimitExceeded;

    /// <summary>
    /// Creates a new TokenBucketRateLimiter.
    /// </summary>
    /// <param name="policy">Rate limit policy to apply.</param>
    public TokenBucketRateLimiter(RateLimitPolicy policy)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));

        // Cleanup stale buckets every minute
        _cleanupTimer = new Timer(CleanupStaleBuckets, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <inheritdoc/>
    public async Task<RateLimitResult> AcquireAsync(string key, int tokens = 1, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        tokens = Math.Max(tokens, _policy.TokensPerRequest);
        var bucket = GetOrCreateBucket(key);

        lock (bucket.Lock)
        {
            RefillBucket(bucket);

            if (bucket.Tokens >= tokens)
            {
                bucket.Tokens -= tokens;
                return CreateResult(true, bucket);
            }
        }

        // Rate limited
        var result = CreateResult(false, bucket);
        OnRateLimitExceeded?.Invoke(key, result);

        // Queue if configured
        if (_policy.QueueExcessRequests)
        {
            return await WaitInQueueAsync(key, tokens, ct);
        }

        return result;
    }

    /// <inheritdoc/>
    public RateLimitResult Check(string key, int tokens = 1)
    {
        var bucket = GetOrCreateBucket(key);

        lock (bucket.Lock)
        {
            RefillBucket(bucket);
            return CreateResult(bucket.Tokens >= tokens, bucket);
        }
    }

    /// <inheritdoc/>
    public void Reset(string key)
    {
        if (_buckets.TryGetValue(key, out var bucket))
        {
            lock (bucket.Lock)
            {
                bucket.Tokens = _policy.MaxTokens;
                bucket.LastRefill = DateTime.UtcNow;
            }
        }
    }

    /// <inheritdoc/>
    public RateLimitResult GetStatus(string key)
    {
        var bucket = GetOrCreateBucket(key);

        lock (bucket.Lock)
        {
            RefillBucket(bucket);
            return CreateResult(bucket.Tokens > 0, bucket);
        }
    }

    private TokenBucket GetOrCreateBucket(string key)
    {
        return _buckets.GetOrAdd(key, _ => new TokenBucket
        {
            Tokens = _policy.MaxTokens,
            LastRefill = DateTime.UtcNow
        });
    }

    private void RefillBucket(TokenBucket bucket)
    {
        var now = DateTime.UtcNow;
        var elapsed = now - bucket.LastRefill;
        var intervalsElapsed = (int)(elapsed / _policy.RefillInterval);

        if (intervalsElapsed > 0)
        {
            var tokensToAdd = intervalsElapsed * _policy.RefillTokens;
            bucket.Tokens = Math.Min(bucket.Tokens + tokensToAdd, _policy.MaxTokens);
            bucket.LastRefill = now;
        }
    }

    private RateLimitResult CreateResult(bool isAllowed, TokenBucket bucket)
    {
        var timeUntilRefill = _policy.RefillInterval - (DateTime.UtcNow - bucket.LastRefill);

        return new RateLimitResult
        {
            IsAllowed = isAllowed,
            RemainingTokens = bucket.Tokens,
            ResetAfter = timeUntilRefill > TimeSpan.Zero ? timeUntilRefill : null,
            RetryAfter = !isAllowed ? timeUntilRefill : null,
            PolicyName = _policy.Name
        };
    }

    private async Task<RateLimitResult> WaitInQueueAsync(string key, int tokens, CancellationToken ct)
    {
        var queue = _queues.GetOrAdd(key, _ =>
            Channel.CreateUnbounded<TaskCompletionSource<RateLimitResult>>());

        var tcs = new TaskCompletionSource<RateLimitResult>();
        await queue.Writer.WriteAsync(tcs, ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_policy.MaxQueueWait);

        try
        {
            // Start background processing for this queue
            _ = ProcessQueueAsync(key, tokens);

            return await tcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new RateLimitResult
            {
                IsAllowed = false,
                RemainingTokens = 0,
                RetryAfter = _policy.MaxQueueWait,
                PolicyName = _policy.Name
            };
        }
    }

    private async Task ProcessQueueAsync(string key, int tokens)
    {
        if (!_queues.TryGetValue(key, out var queue))
            return;

        while (queue.Reader.TryRead(out var tcs))
        {
            var bucket = GetOrCreateBucket(key);

            while (true)
            {
                lock (bucket.Lock)
                {
                    RefillBucket(bucket);
                    if (bucket.Tokens >= tokens)
                    {
                        bucket.Tokens -= tokens;
                        tcs.TrySetResult(CreateResult(true, bucket));
                        break;
                    }
                }

                await Task.Delay(_policy.RefillInterval);
            }
        }
    }

    private void CleanupStaleBuckets(object? state)
    {
        if (_disposed) return;

        var staleThreshold = DateTime.UtcNow - TimeSpan.FromMinutes(10);

        foreach (var key in _buckets.Keys.ToList())
        {
            if (_buckets.TryGetValue(key, out var bucket))
            {
                lock (bucket.Lock)
                {
                    if (bucket.LastRefill < staleThreshold && bucket.Tokens == _policy.MaxTokens)
                    {
                        _buckets.TryRemove(key, out _);
                    }
                }
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cleanupTimer.Dispose();
        _buckets.Clear();

        foreach (var queue in _queues.Values)
        {
            queue.Writer.Complete();
        }
        _queues.Clear();
    }
}

/// <summary>
/// Composite rate limiter that combines multiple policies.
/// </summary>
public sealed class CompositeRateLimiter : IRateLimiter, IDisposable
{
    private readonly List<(string KeyPrefix, IRateLimiter Limiter)> _limiters = new();
    private readonly IRateLimiter _defaultLimiter;
    private volatile bool _disposed;

    /// <inheritdoc/>
    public event Action<string, RateLimitResult>? OnRateLimitExceeded;

    /// <summary>
    /// Creates a new CompositeRateLimiter.
    /// </summary>
    /// <param name="defaultPolicy">Default policy for keys without specific policies.</param>
    public CompositeRateLimiter(RateLimitPolicy defaultPolicy)
    {
        _defaultLimiter = new TokenBucketRateLimiter(defaultPolicy);
        _defaultLimiter.OnRateLimitExceeded += (k, r) => OnRateLimitExceeded?.Invoke(k, r);
    }

    /// <summary>
    /// Adds a rate limiter for keys with a specific prefix.
    /// </summary>
    /// <param name="keyPrefix">Key prefix to match.</param>
    /// <param name="policy">Policy for matching keys.</param>
    public void AddPolicy(string keyPrefix, RateLimitPolicy policy)
    {
        var limiter = new TokenBucketRateLimiter(policy);
        limiter.OnRateLimitExceeded += (k, r) => OnRateLimitExceeded?.Invoke(k, r);
        _limiters.Add((keyPrefix, limiter));
    }

    /// <inheritdoc/>
    public Task<RateLimitResult> AcquireAsync(string key, int tokens = 1, CancellationToken ct = default)
    {
        return GetLimiterForKey(key).AcquireAsync(key, tokens, ct);
    }

    /// <inheritdoc/>
    public RateLimitResult Check(string key, int tokens = 1)
    {
        return GetLimiterForKey(key).Check(key, tokens);
    }

    /// <inheritdoc/>
    public void Reset(string key)
    {
        GetLimiterForKey(key).Reset(key);
    }

    /// <inheritdoc/>
    public RateLimitResult GetStatus(string key)
    {
        return GetLimiterForKey(key).GetStatus(key);
    }

    private IRateLimiter GetLimiterForKey(string key)
    {
        foreach (var (prefix, limiter) in _limiters)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return limiter;
        }
        return _defaultLimiter;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        (_defaultLimiter as IDisposable)?.Dispose();
        foreach (var (_, limiter) in _limiters)
        {
            (limiter as IDisposable)?.Dispose();
        }
        _limiters.Clear();
    }
}

#endregion
