using System.ComponentModel;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// Defines the 7 morphing levels of the adaptive index engine.
/// Each level targets a different dataset size range with optimal data structure characteristics.
/// </summary>
/// <remarks>
/// The adaptive index automatically transitions between levels as the object count changes.
/// Lower levels use simpler structures with less overhead; higher levels handle larger datasets
/// with more sophisticated indexing (ART, B-epsilon trees, learned indexes, etc.).
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-01 Adaptive Index Engine")]
public enum MorphLevel : byte
{
    /// <summary>
    /// Level 0: Direct pointer for exactly 0-1 objects. O(1) lookup with zero overhead.
    /// </summary>
    [Description("Direct pointer index for 0-1 objects with O(1) lookup")]
    DirectPointer = 0,

    /// <summary>
    /// Level 1: Sorted array with binary search for up to ~10K objects. O(log n) lookup.
    /// </summary>
    [Description("Sorted array index with binary search for up to 10K objects")]
    SortedArray = 1,

    /// <summary>
    /// Level 2: Adaptive Radix Tree for up to ~1M objects. O(k) lookup where k is key length.
    /// </summary>
    [Description("Adaptive Radix Tree (ART) for up to 1M objects with O(k) lookup")]
    AdaptiveRadixTree = 2,

    /// <summary>
    /// Level 3: B-epsilon tree for write-heavy workloads up to ~10M objects.
    /// </summary>
    [Description("B-epsilon tree for write-heavy workloads up to 10M objects")]
    BeTree = 3,

    /// <summary>
    /// Level 4: Learned index with CDF model for read-heavy workloads up to ~100M objects.
    /// </summary>
    [Description("Learned index with CDF model for read-heavy workloads up to 100M objects")]
    LearnedIndex = 4,

    /// <summary>
    /// Level 5: B-epsilon tree forest with partitioned key ranges for up to ~1B objects.
    /// </summary>
    [Description("B-epsilon tree forest with partitioned key ranges up to 1B objects")]
    BeTreeForest = 5,

    /// <summary>
    /// Level 6: Distributed routing index for datasets exceeding 1B objects across nodes.
    /// </summary>
    [Description("Distributed routing index for 1B+ objects across federated nodes")]
    DistributedRouting = 6
}
