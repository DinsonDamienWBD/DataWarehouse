using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.CopyOnWrite;

/// <summary>
/// Configuration options that control the behavior of an instant clone operation.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: VOPT-47 instant clone options")]
public sealed class CloneOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether inline extended attributes are copied to the clone.
    /// Default: <see langword="true"/>.
    /// </summary>
    public bool CloneExtendedAttributes { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether owner, group, and permission metadata are
    /// copied to the clone. Default: <see langword="true"/>.
    /// </summary>
    public bool ClonePermissions { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the original timestamps (created, modified,
    /// accessed) are preserved on the clone. When <see langword="false"/> (default), the
    /// clone receives the current UTC time as its creation and modification timestamp.
    /// </summary>
    public bool PreserveTimestamps { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether a directory clone should recursively clone
    /// all child inodes. When <see langword="false"/> (default), only the directory inode
    /// itself is cloned (shallow clone). When <see langword="true"/>, all children are
    /// cloned depth-first, producing an independent writable copy of the entire sub-tree.
    /// </summary>
    public bool DeepClone { get; set; } = false;
}
