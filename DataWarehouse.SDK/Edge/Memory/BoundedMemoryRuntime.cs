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
[SdkCompatibility("3.0.0", Notes = "Phase 36: Bounded memory runtime (EDGE-06)")]
public sealed class BoundedMemoryRuntime : IDisposable
{
    private static readonly Lazy<BoundedMemoryRuntime> _instance = new(() => new BoundedMemoryRuntime());

    /// <summary>
    /// Gets the singleton instance.
    /// </summary>
    public static BoundedMemoryRuntime Instance => _instance.Value;

    private MemoryBudgetTracker? _tracker;
    private MemorySettings _settings = new();
    private Timer? _monitorTimer;

    private BoundedMemoryRuntime() { }

    /// <summary>
    /// Initializes the bounded memory runtime with settings.
    /// Idempotent -- safe to call multiple times (uses latest settings).
    /// </summary>
    /// <param name="settings">Memory configuration.</param>
    public void Initialize(MemorySettings settings)
    {
        _settings = settings;
        if (!settings.Enabled) return;

        _tracker = new MemoryBudgetTracker(settings);

        // Start periodic memory monitoring (Gen1 GC when above threshold)
        _monitorTimer = new Timer(
            callback: _ => _tracker?.TriggerProactiveCleanup(),
            state: null,
            dueTime: TimeSpan.FromSeconds(10),
            period: TimeSpan.FromSeconds(10));
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
        return _tracker!.CurrentUsage + size <= _tracker.Ceiling;
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
        _monitorTimer?.Dispose();
    }
}
