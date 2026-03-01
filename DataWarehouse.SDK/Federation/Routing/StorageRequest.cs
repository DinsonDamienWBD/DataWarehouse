using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Storage;
using System;
using System.Collections.Generic;

namespace DataWarehouse.SDK.Federation.Routing;

/// <summary>
/// Represents a storage request for routing through the federated object storage system.
/// </summary>
/// <remarks>
/// StorageRequest models all information needed to classify, route, and execute a storage operation.
/// Requests contain addressing, operation type, optional metadata, and optional language hints
/// for override scenarios.
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Storage request model for routing")]
public sealed class StorageRequest
{
    /// <summary>
    /// Gets the unique identifier for this request.
    /// Used for tracing, logging, and correlation across distributed routing.
    /// </summary>
    public required string RequestId { get; init; }

    /// <summary>
    /// Gets the storage address being accessed.
    /// Address kind (ObjectKey, FilePath, etc.) is a primary classification signal.
    /// </summary>
    public required StorageAddress Address { get; init; }

    /// <summary>
    /// Gets the storage operation to perform.
    /// </summary>
    public required StorageOperation Operation { get; init; }

    /// <summary>
    /// Gets optional metadata key-value pairs for the request.
    /// Metadata can provide classification signals (e.g., "object-id", "path") and
    /// routing hints (e.g., "preferred-node", "replication-factor").
    /// </summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>
    /// Gets an optional language hint that overrides automatic classification.
    /// When set, the router prefers this language over inferred classification
    /// (configurable via <see cref="PatternClassifierConfiguration.PreferHints"/>).
    /// </summary>
    public RequestLanguage? LanguageHint { get; init; }

    /// <summary>
    /// Gets the user ID for permission checks.
    /// Used by permission-aware routing layers to enforce ACLs.
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>
    /// Gets the UTC timestamp when this request was created.
    /// </summary>
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets the estimated payload size in bytes for this request.
    /// </summary>
    /// <remarks>
    /// Capacity-based routing (e.g. ThroughputOptimized policy) uses this value to
    /// score candidate nodes by available free space vs request size. Callers should set this to the actual
    /// or estimated write payload size; defaults to -1 (unknown) to distinguish "not set" from "zero-byte
    /// request", preventing capacity-based routing from silently treating every un-annotated request as a
    /// zero-byte write and routing to the lowest-capacity node.
    /// </remarks>
    public long EstimatedSizeBytes { get; init; } = -1;
}

/// <summary>
/// Storage operations supported by the federated routing system.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Storage operation types")]
public enum StorageOperation
{
    /// <summary>Read data from storage.</summary>
    Read,

    /// <summary>Write data to storage.</summary>
    Write,

    /// <summary>Delete data from storage.</summary>
    Delete,

    /// <summary>List contents (directory listing or object listing).</summary>
    List,

    /// <summary>Get metadata for an object or file.</summary>
    GetMetadata,

    /// <summary>Set metadata for an object or file.</summary>
    SetMetadata,

    /// <summary>Query storage using metadata predicates (Object language only).</summary>
    Query
}
