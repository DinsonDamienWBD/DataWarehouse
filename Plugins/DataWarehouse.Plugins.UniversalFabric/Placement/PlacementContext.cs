using System.Collections.Generic;

namespace DataWarehouse.Plugins.UniversalFabric.Placement;

/// <summary>
/// Context describing an object being placed, used by <see cref="PlacementCondition"/>
/// to evaluate whether a <see cref="PlacementRule"/> applies.
/// </summary>
public record PlacementContext
{
    /// <summary>
    /// Gets the content type of the object (e.g., "image/jpeg", "application/json").
    /// Null if unknown.
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// Gets the size of the object in bytes. Null if unknown.
    /// </summary>
    public long? ObjectSize { get; init; }

    /// <summary>
    /// Gets the metadata key-value pairs associated with the object.
    /// Null if no metadata is available.
    /// </summary>
    public IDictionary<string, string>? Metadata { get; init; }

    /// <summary>
    /// Gets the bucket name the object is being placed into. Null if not bucket-based.
    /// </summary>
    public string? BucketName { get; init; }

    /// <summary>
    /// Gets the object key or path. Null if not yet determined.
    /// </summary>
    public string? ObjectKey { get; init; }
}
