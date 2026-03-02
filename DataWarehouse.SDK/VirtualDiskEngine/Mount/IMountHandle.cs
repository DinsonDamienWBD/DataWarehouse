using DataWarehouse.SDK.Contracts;
using System;

namespace DataWarehouse.SDK.VirtualDiskEngine.Mount;

/// <summary>
/// Handle representing an active VDE mount session.
/// Disposing the handle triggers an unmount operation.
/// </summary>
/// <remarks>
/// Implementations must ensure that <see cref="IAsyncDisposable.DisposeAsync"/>
/// performs a clean unmount, flushing all cached data and releasing OS resources.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VDE mount provider abstraction (VOPT-83)")]
public interface IMountHandle : IAsyncDisposable
{
    /// <summary>
    /// The OS path or drive letter where the VDE volume is mounted.
    /// </summary>
    string MountPoint { get; }

    /// <summary>
    /// The file path to the VDE volume file.
    /// </summary>
    string VdePath { get; }

    /// <summary>
    /// Whether the mount is currently active and serving filesystem operations.
    /// Returns false after unmount or disposal.
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    /// UTC timestamp when the mount was established.
    /// </summary>
    DateTimeOffset MountedAtUtc { get; }

    /// <summary>
    /// The options used when this mount was created.
    /// </summary>
    MountOptions Options { get; }
}
