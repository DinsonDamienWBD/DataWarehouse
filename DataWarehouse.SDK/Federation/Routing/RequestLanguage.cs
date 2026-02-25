using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Federation.Routing;

/// <summary>
/// Discriminates between Object-based addressing (UUIDs, metadata queries, object operations)
/// and FilePath-based addressing (paths, directory listings, filesystem operations).
/// </summary>
/// <remarks>
/// This enum is the foundational classification for federated object storage routing.
/// All subsequent routing intelligence (permission-aware, location-aware, replication-aware)
/// builds on top of this language classification.
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Federated object storage language classification")]
public enum RequestLanguage
{
    /// <summary>
    /// Object-based addressing: UUIDs, metadata queries, object operations.
    /// Used for S3-style object storage, AD-04 canonical keys, and metadata-driven queries.
    /// </summary>
    Object = 0,

    /// <summary>
    /// FilePath-based addressing: paths, directory listings, filesystem operations.
    /// Used for traditional filesystem operations, VDE integration, and path-based access.
    /// </summary>
    FilePath = 1
}
