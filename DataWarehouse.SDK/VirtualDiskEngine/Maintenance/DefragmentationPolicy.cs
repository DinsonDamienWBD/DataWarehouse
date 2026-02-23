using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Maintenance;

/// <summary>
/// Priority levels for online defragmentation operations.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Online defragmentation policy (VOPT-28)")]
public enum DefragPriority : byte
{
    /// <summary>Lowest priority; yields aggressively to normal I/O.</summary>
    Background = 0,

    /// <summary>Normal priority; shares I/O bandwidth with user operations.</summary>
    Normal = 1,

    /// <summary>Highest priority; uses maximum I/O budget for fastest compaction.</summary>
    Aggressive = 2
}

/// <summary>
/// Configurable policy for online defragmentation operations.
/// Controls thresholds, I/O budgets, and behavior of the <see cref="OnlineDefragmenter"/>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Online defragmentation policy (VOPT-28)")]
public sealed class DefragmentationPolicy
{
    /// <summary>
    /// Trigger defrag when fragmentation ratio exceeds this threshold.
    /// Range: 0.0 (always defrag) to 1.0 (never defrag). Default: 0.3.
    /// </summary>
    public double FragmentationThreshold { get; set; } = 0.3;

    /// <summary>
    /// Maximum I/O budget in bytes per second for defrag operations. Default: 50 MB/s.
    /// </summary>
    public long MaxIoBudgetBytesPerSecond { get; set; } = 50L * 1024 * 1024;

    /// <summary>
    /// Maximum number of block moves per defrag cycle. Default: 256.
    /// </summary>
    public int MaxBlockMovesPerCycle { get; set; } = 256;

    /// <summary>
    /// Time between defrag cycles. Default: 30 seconds.
    /// </summary>
    public TimeSpan CycleInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether to merge adjacent free extents within allocation groups. Default: true.
    /// </summary>
    public bool CompactFreeExtents { get; set; } = true;

    /// <summary>
    /// Whether to relocate fragmented files to create contiguous extents. Default: true.
    /// </summary>
    public bool RelocateFragmentedFiles { get; set; } = true;

    /// <summary>
    /// Only defrag files with more than this many extents. Default: 4.
    /// </summary>
    public int MinExtentsToDefrag { get; set; } = 4;

    /// <summary>
    /// Defragmentation priority level. Default: <see cref="DefragPriority.Background"/>.
    /// </summary>
    public DefragPriority Priority { get; set; } = DefragPriority.Background;
}
