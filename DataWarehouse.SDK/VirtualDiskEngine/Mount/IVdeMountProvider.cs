using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.Mount;

/// <summary>
/// Flags indicating which operating system platforms a mount provider supports.
/// </summary>
[Flags]
[SdkCompatibility("6.0.0", Notes = "Phase 87: VDE mount provider abstraction (VOPT-83)")]
public enum PlatformFlags
{
    /// <summary>No platform support.</summary>
    None = 0,

    /// <summary>Microsoft Windows (via WinFsp).</summary>
    Windows = 1,

    /// <summary>Linux (via FUSE3/libfuse).</summary>
    Linux = 2,

    /// <summary>macOS (via macFUSE/OSXFUSE).</summary>
    MacOs = 4,

    /// <summary>FreeBSD (via FUSE).</summary>
    FreeBsd = 8
}

/// <summary>
/// Abstract mount provider contract for mounting VDE volumes as native OS filesystems.
/// Platform-specific providers (WinFsp, FUSE3, macFUSE) implement this interface and
/// delegate all storage logic to <see cref="VdeFilesystemAdapter"/>.
/// </summary>
/// <remarks>
/// <para>
/// Per AD-51, VDE files must mount as native OS drives. This interface defines the
/// platform-agnostic mount/unmount/list contract. Each platform provider handles only
/// the OS binding layer while sharing the common VdeFilesystemAdapter for all storage operations.
/// </para>
/// <para>
/// Implementations should check <see cref="IsAvailable"/> before attempting mount operations
/// to verify that required platform libraries (e.g., WinFsp DLL, libfuse3.so) are installed.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VDE mount provider abstraction (VOPT-83)")]
public interface IVdeMountProvider
{
    /// <summary>
    /// Mounts a VDE volume at the specified mount point as a native filesystem.
    /// </summary>
    /// <param name="vdePath">Path to the VDE volume file.</param>
    /// <param name="mountPoint">OS path or drive letter where the volume should appear.</param>
    /// <param name="options">Mount configuration options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A handle representing the active mount session.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the provider is not available on this platform.</exception>
    /// <exception cref="System.IO.FileNotFoundException">Thrown if the VDE file does not exist.</exception>
    Task<IMountHandle> MountAsync(string vdePath, string mountPoint, MountOptions options, CancellationToken ct = default);

    /// <summary>
    /// Unmounts a previously mounted VDE volume, flushing all cached data.
    /// </summary>
    /// <param name="handle">The mount handle returned by <see cref="MountAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UnmountAsync(IMountHandle handle, CancellationToken ct = default);

    /// <summary>
    /// Lists all active VDE mounts managed by this provider.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Read-only list of mount information for all active mounts.</returns>
    Task<IReadOnlyList<MountInfo>> ListMountsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the platforms supported by this mount provider.
    /// </summary>
    PlatformFlags SupportedPlatforms { get; }

    /// <summary>
    /// Gets whether the mount provider is available on the current platform.
    /// Checks for required native libraries (e.g., WinFsp, libfuse3, macFUSE).
    /// </summary>
    bool IsAvailable { get; }
}
