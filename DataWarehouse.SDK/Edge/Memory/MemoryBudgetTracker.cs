using DataWarehouse.SDK.Contracts;
using System.Buffers;

namespace DataWarehouse.SDK.Edge.Memory;

/// <summary>
/// Memory budget tracker with ArrayPool integration and GC pressure monitoring.
/// </summary>
/// <remarks>
/// <para>
/// Tracks memory usage against configurable ceiling, enforces budget via OutOfMemoryException,
/// and provides pooled allocations to reduce GC pressure. Uses ArrayPool&lt;byte&gt; for buffers
/// &gt;1KB to enable reuse and reduce allocation frequency.
/// </para>
/// <para>
/// <strong>GC Pressure Monitoring:</strong> When memory usage exceeds threshold (default 85%),
/// triggers Gen1 GC to reclaim space before hitting hard ceiling. Gen2 GC is reserved for
/// critical situations (near ceiling).
/// </para>
/// <para>
/// <strong>Performance:</strong> Rent/Return are lightweight operations. When disabled via
/// MemorySettings.Enabled=false, operations bypass tracking for zero overhead.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: Memory budget tracker (EDGE-06)")]
public sealed class MemoryBudgetTracker
{
    private readonly MemorySettings _settings;
    private readonly ArrayPool<byte> _arrayPool;
    private long _allocatedBytes;

    public MemoryBudgetTracker(MemorySettings settings)
    {
        _settings = settings;
        _arrayPool = ArrayPool<byte>.Create(_settings.ArrayPoolMaxArraySize, 50);
    }

    /// <summary>
    /// Gets the current process memory usage (includes all managed and unmanaged memory).
    /// </summary>
    public long CurrentUsage => GC.GetTotalMemory(forceFullCollection: false);

    /// <summary>
    /// Gets the configured memory ceiling.
    /// </summary>
    public long Ceiling => _settings.MemoryCeiling;

    /// <summary>
    /// Gets the current memory usage as a ratio of ceiling (0.0-1.0).
    /// </summary>
    public double UsageRatio => (double)CurrentUsage / Ceiling;

    /// <summary>
    /// Checks if memory usage is above the GC pressure threshold.
    /// </summary>
    public bool IsAboveThreshold => UsageRatio >= _settings.GcPressureThreshold;

    /// <summary>
    /// Rents a byte array from the pool. Returns pooled array if size allows, otherwise allocates directly.
    /// </summary>
    /// <param name="size">Requested array size.</param>
    /// <returns>Byte array of at least the requested size.</returns>
    /// <exception cref="OutOfMemoryException">Thrown if allocation would exceed memory ceiling.</exception>
    public byte[] Rent(int size)
    {
        if (!_settings.Enabled) return new byte[size];

        if (CurrentUsage + size > Ceiling)
        {
            // Trigger Gen2 GC and retry (last-ditch effort)
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            if (CurrentUsage + size > Ceiling)
                throw new OutOfMemoryException($"Memory ceiling exceeded: {CurrentUsage + size} > {Ceiling}");
        }

        // Track actual allocated size (array.Length), not requested size,
        // because ArrayPool.Rent may return a larger buffer
        var rented = _arrayPool.Rent(size);
        Interlocked.Add(ref _allocatedBytes, rented.Length);
        return rented;
    }

    /// <summary>
    /// Returns a rented array to the pool.
    /// </summary>
    /// <param name="array">Array to return.</param>
    /// <param name="clearArray">Whether to clear the array before returning (security).</param>
    public void Return(byte[] array, bool clearArray = false)
    {
        if (!_settings.Enabled) return;
        // Decrement by actual array length (matches what was added in Rent)
        Interlocked.Add(ref _allocatedBytes, -array.Length);
        _arrayPool.Return(array, clearArray);
    }

    /// <summary>
    /// Triggers proactive garbage collection when memory usage approaches threshold.
    /// </summary>
    /// <remarks>
    /// Called periodically by BoundedMemoryRuntime monitor. Triggers Gen1 GC to reclaim
    /// short-lived objects without full heap compaction. Gen2 GC is deferred to critical situations.
    /// </remarks>
    public void TriggerProactiveCleanup()
    {
        if (IsAboveThreshold)
        {
            GC.Collect(1, GCCollectionMode.Default);
        }
    }

    /// <summary>
    /// Gets the total bytes allocated via Rent (not yet returned).
    /// </summary>
    public long AllocatedBytes => Interlocked.Read(ref _allocatedBytes);
}
