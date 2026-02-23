using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Allocation;

/// <summary>
/// Defines the block allocation strategy used within an allocation group.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Allocation group policy (VOPT-01)")]
public enum AllocationPolicy : byte
{
    /// <summary>
    /// Sequential scan from the beginning of the bitmap. Good for streaming writes
    /// where locality and sequential layout are preferred.
    /// </summary>
    FirstFit = 0,

    /// <summary>
    /// Finds the smallest gap that fits the requested size. Good for random writes
    /// where minimizing fragmentation is preferred.
    /// </summary>
    BestFit = 1
}
