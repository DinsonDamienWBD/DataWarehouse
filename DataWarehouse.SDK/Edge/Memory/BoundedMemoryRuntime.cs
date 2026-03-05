using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Edge.Memory;

/// <summary>
/// Bounded memory runtime with ceiling enforcement and ArrayPool integration.
/// </summary>
/// <remarks>
/// <para>
/// Singleton providing global memory management for edge deployments. Enforces memory ceiling,
/// provides pooled buffer allocations, and monitors GC pressure. Designed for devices with
/// 64MB-256MB RAM constraints.
/// </para>
/// <para>
/// <strong>Initialization:</strong> Call Initialize() at startup with MemorySettings. If not
/// initialized or disabled, operations bypass tracking (zero overhead). Initialize() is idempotent.
/// </para>
/// <para>
/// <strong>Usage Pattern:</strong>
/// <code>
/// var buffer = BoundedMemoryRuntime.Instance.RentBuffer(8192);
/// try {
///     // Use buffer
/// } finally {
///     BoundedMemoryRuntime.Instance.ReturnBuffer(buffer);
/// }
/// </code>
/// </para>
/// <para>
/// <strong>Monitoring:</strong> Periodic timer (10s interval) checks GC pressure and triggers
/// proactive Gen1 collection when above threshold. Prevents sudden Gen2 pauses.
/// </para>
/// </remarks>
// SECURITY NOTE (ISO-03, CVSS 5.9): BoundedMemoryRuntime is a process-wide singleton shared
// across all plugins. Its _settings and _tracker are mutable shared state. While Lazy<T>
// ensures thread-safe initialization, concurrent plugin access to Initialize() could race.
// The singleton pattern is intentional (memory budgets must be global), but callers should
// be aware that plugins in different AssemblyLoadContexts share this instance. Budget
// manipulation by a malicious plugin could affect other plugins' memory allocations.
// Mitigation: Initialize() is called once by the kernel before plugins load.
[SdkCompatibility("3.0.0", Notes = "Phase 36: Bounded memory runtime (EDGE-06)")]
public sealed class BoundedMemoryRuntime : IDisposable
{
    private static readonly Lazy<BoundedMemoryRuntime> _instance = new(() => new BoundedMemoryRuntime());

    /// <summary>
    /// Gets the singleton instance.
    /// </summary>
    public static BoundedMemoryRuntime Instance => _instance.Value;

    private volatile MemoryBudgetTracker? _tracker;
    private MemorySettings _settings = new();
    private Timer? _monitorTimer;
    // ISO-03 (CVSS 5.9): Lock protects Initialize() against concurrent calls from
    // multiple plugins. The singleton is process-wide shared state; while the kernel
    // calls Initialize() once before plugin load, this lock prevents races if a
    // plugin also calls Initialize().
    private readonly object _initLock = new();

    private BoundedMemoryRuntime() { }

    /// <summary>
    /// Initializes the bounded memory runtime with settings.
    /// Idempotent -- safe to call multiple times (uses latest settings).
    /// Thread-safe: protected by lock to prevent concurrent initialization races.
    /// </summary>
    /// <param name="settings">Memory configuration.</param>
    public void Initialize(MemorySettings settings)
    {
        lock (_initLock)
        {
            _settings = settings;
            if (!settings.Enabled) return;

            _tracker = new MemoryBudgetTracker(settings);

            // Dispose previous timer if re-initializing
            _monitorTimer?.Dispose();

            // Start periodic memory monitoring (Gen1 GC when above threshold)
            _monitorTimer = new Timer(
                callback: _ => _tracker?.TriggerProactiveCleanup(),
                state: null,
                dueTime: TimeSpan.FromSeconds(10),
                period: TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>
    /// Rents a buffer from the pool. If bounded mode disabled, allocates directly.
    /// </summary>
    /// <param name="size">Requested buffer size.</param>
    /// <returns>Byte array of at least the requested size.</returns>
    /// <exception cref="OutOfMemoryException">Thrown if allocation would exceed memory ceiling (when enabled).</exception>
    public byte[] RentBuffer(int size) => _tracker?.Rent(size) ?? new byte[size];

    /// <summary>
    /// Returns a buffer to the pool. If bounded mode disabled, no-op.
    /// </summary>
    /// <param name="buffer">Buffer to return.</param>
    /// <param name="clear">Whether to clear buffer contents before returning.</param>
    public void ReturnBuffer(byte[] buffer, bool clear = false) => _tracker?.Return(buffer, clear);

    /// <summary>
    /// Checks if an allocation of the given size can be satisfied without exceeding ceiling.
    /// </summary>
    /// <param name="size">Requested allocation size.</param>
    /// <returns>True if allocation fits within budget; otherwise false.</returns>
    public bool CanAllocate(int size)
    {
        if (!_settings.Enabled) return true;
        var tracker = _tracker; // capture volatile reference once to avoid TOCTOU
        if (tracker == null) return true;
        return tracker.CurrentUsage + size <= tracker.Ceiling;
    }

    /// <summary>
    /// Gets the current process memory usage.
    /// </summary>
    public long CurrentMemoryUsage => _tracker?.CurrentUsage ?? GC.GetTotalMemory(false);

    /// <summary>
    /// Gets whether bounded memory mode is enabled.
    /// </summary>
    public bool IsEnabled => _settings.Enabled;

    /// <summary>
    /// Gets the current memory usage ratio (0.0-1.0) relative to ceiling.
    /// Returns 0.0 if disabled.
    /// </summary>
    public double UsageRatio => _tracker?.UsageRatio ?? 0.0;

    /// <summary>
    /// Disposes the runtime and stops monitoring timer.
    /// </summary>
    public void Dispose()
    {
        lock (_initLock)
        {
            var timer = _monitorTimer;
            _monitorTimer = null;
            timer?.Dispose();
            _tracker = null; // prevent post-dispose callbacks from referencing tracker
        }
    }
}
