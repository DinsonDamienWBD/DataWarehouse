using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Federation.Addressing;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Federation.Catalog;

/// <summary>
/// Defines the contract for the Manifest/Catalog Service, which provides authoritative
/// UUID-to-location mapping for federated object storage.
/// </summary>
/// <remarks>
/// <para>
/// IManifestService is the catalog for the federated storage cluster. It maintains the
/// mapping of object UUIDs to physical node locations, enabling routing decisions and
/// replica management.
/// </para>
/// <para>
/// <strong>Consistency:</strong> Manifest operations are backed by Raft consensus to ensure
/// all nodes in the cluster have a consistent view of object locations. This prevents
/// inconsistencies during network partitions or node failures.
/// </para>
/// <para>
/// <strong>Performance:</strong> The manifest is optimized for high read throughput via
/// in-memory caching. Writes (register, update, remove) go through Raft for consistency.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Manifest service interface")]
public interface IManifestService
{
    /// <summary>
    /// Gets the location(s) of an object by its UUID.
    /// </summary>
    /// <param name="objectId">The object identity.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The location entry, or null if the object is not found.</returns>
    /// <remarks>
    /// This method provides O(1) lookup via in-memory cache backed by Raft state machine.
    /// </remarks>
    Task<ObjectLocationEntry?> GetLocationAsync(ObjectIdentity objectId, CancellationToken ct = default);

    /// <summary>
    /// Gets the locations of multiple objects in a single batch request.
    /// </summary>
    /// <param name="objectIds">The object identities to look up.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of location entries for objects that were found.</returns>
    /// <remarks>
    /// Batch lookups are optimized to resolve 100K+ UUIDs/sec via cache and state machine.
    /// Objects not found in the manifest are omitted from the result.
    /// </remarks>
    Task<IReadOnlyList<ObjectLocationEntry>> GetLocationsBatchAsync(IEnumerable<ObjectIdentity> objectIds, CancellationToken ct = default);

    /// <summary>
    /// Queries objects created within a specific time range.
    /// </summary>
    /// <param name="start">The start of the time range (inclusive).</param>
    /// <param name="end">The end of the time range (inclusive).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of location entries for objects created in the time range.</returns>
    /// <remarks>
    /// <para>
    /// This method leverages the UUID v7 timestamp embedded in the object ID to perform
    /// time-range queries without a secondary index.
    /// </para>
    /// <para>
    /// Results are ordered by object ID (which preserves creation time order for UUID v7).
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<ObjectLocationEntry>> QueryByTimeRangeAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default);

    /// <summary>
    /// Registers a new object in the manifest.
    /// </summary>
    /// <param name="entry">The object location entry to register.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Registration is proposed via Raft to ensure all nodes in the cluster see the new object.
    /// </remarks>
    Task RegisterObjectAsync(ObjectLocationEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Updates the location(s) of an existing object.
    /// </summary>
    /// <param name="objectId">The object identity.</param>
    /// <param name="nodeIds">The new list of node IDs hosting replicas.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Updates are proposed via Raft to maintain consistency. Typically used during rebalancing
    /// or replica migration.
    /// </remarks>
    Task UpdateLocationAsync(ObjectIdentity objectId, IReadOnlyList<string> nodeIds, CancellationToken ct = default);

    /// <summary>
    /// Removes an object from the manifest.
    /// </summary>
    /// <param name="objectId">The object identity.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Removal is proposed via Raft to ensure all nodes agree the object no longer exists.
    /// </remarks>
    Task RemoveObjectAsync(ObjectIdentity objectId, CancellationToken ct = default);

    /// <summary>
    /// Gets manifest-wide statistics.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Statistics about the manifest (object count, total size, replication factor, etc.).</returns>
    Task<ManifestStatistics> GetStatisticsAsync(CancellationToken ct = default);
}

/// <summary>
/// Statistics about the manifest service.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Manifest statistics")]
public sealed record ManifestStatistics
{
    /// <summary>
    /// Gets the total number of objects in the manifest.
    /// </summary>
    public long TotalObjects { get; init; }

    /// <summary>
    /// Gets the total size of all objects in bytes.
    /// </summary>
    public long TotalBytes { get; init; }

    /// <summary>
    /// Gets the average replication factor across all objects.
    /// </summary>
    public double AverageReplication { get; init; }

    /// <summary>
    /// Gets the number of unique nodes hosting at least one object.
    /// </summary>
    public int UniqueNodes { get; init; }
}
