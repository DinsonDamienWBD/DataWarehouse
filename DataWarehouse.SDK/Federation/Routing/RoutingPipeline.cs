using DataWarehouse.SDK.Contracts;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Federation.Routing;

/// <summary>
/// Abstract base class for routing pipelines that execute storage operations.
/// </summary>
/// <remarks>
/// <para>
/// RoutingPipeline provides the abstraction for language-specific routing logic.
/// Concrete implementations (ObjectPipeline, FilePathPipeline) handle the execution
/// of storage operations for their respective language.
/// </para>
/// <para>
/// Future routing layers (permission-aware, location-aware, replication-aware) will
/// extend these pipelines with additional routing intelligence.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Abstract routing pipeline")]
public abstract class RoutingPipeline
{
    /// <summary>
    /// Gets the request language handled by this pipeline.
    /// </summary>
    public RequestLanguage Language { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="RoutingPipeline"/> class.
    /// </summary>
    /// <param name="language">The request language handled by this pipeline.</param>
    protected RoutingPipeline(RequestLanguage language)
    {
        Language = language;
    }

    /// <summary>
    /// Executes the storage operation through this pipeline.
    /// </summary>
    /// <param name="request">The storage request to execute.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The storage response containing operation result and metadata.</returns>
    public abstract Task<StorageResponse> ExecuteAsync(StorageRequest request, CancellationToken ct);
}

/// <summary>
/// Routing pipeline for Object language requests (UUID-based, metadata-driven operations).
/// </summary>
/// <remarks>
/// This is a stub implementation for Phase 34-01. Real implementation will be added
/// in Phase 34-02 with UUID-based object routing, metadata queries, and object storage integration.
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Object language routing pipeline (stub)")]
internal sealed class ObjectPipeline : RoutingPipeline
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ObjectPipeline"/> class.
    /// </summary>
    public ObjectPipeline() : base(RequestLanguage.Object)
    {
    }

    /// <inheritdoc />
    public override Task<StorageResponse> ExecuteAsync(StorageRequest request, CancellationToken ct)
    {
        // Phase 34-02: Requires UUID-based object routing with:
        // - Object storage adapter integration
        // - Metadata query execution
        // - AD-04 canonical key resolution
        // - Replication-aware routing
        throw new NotSupportedException(
            "ObjectPipeline is not yet implemented. UUID-based object routing requires Phase 34-02.");
    }
}

/// <summary>
/// Routing pipeline for FilePath language requests (path-based, filesystem operations).
/// </summary>
/// <remarks>
/// This is a stub implementation for Phase 34-01. Real implementation will be added
/// in Phase 34-02 with FilePath routing to VDE (Virtual Disk Engine), filesystem adapters,
/// and directory listing support.
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: FilePath language routing pipeline (stub)")]
internal sealed class FilePathPipeline : RoutingPipeline
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FilePathPipeline"/> class.
    /// </summary>
    public FilePathPipeline() : base(RequestLanguage.FilePath)
    {
    }

    /// <inheritdoc />
    public override Task<StorageResponse> ExecuteAsync(StorageRequest request, CancellationToken ct)
    {
        // Phase 34-02: Requires FilePath routing to:
        // - VDE (Virtual Disk Engine) for virtual disk paths
        // - Filesystem storage adapters for physical paths
        // - Network path resolution (SMB, NFS)
        // - Directory listing and filesystem metadata
        throw new NotSupportedException(
            "FilePathPipeline is not yet implemented. FilePath routing requires Phase 34-02.");
    }
}
