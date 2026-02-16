using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Federation.Addressing;
using System;
using System.Collections.Generic;

namespace DataWarehouse.SDK.Federation.Catalog;

/// <summary>
/// Represents an object's location(s) in the federated storage cluster.
/// </summary>
/// <remarks>
/// <para>
/// ObjectLocationEntry is the authoritative record of where an object (identified by UUID)
/// is physically stored. It contains a list of node IDs hosting replicas, size metadata,
/// and content hash for integrity verification.
/// </para>
/// <para>
/// <strong>Replication:</strong> The NodeIds list contains all replicas. A replication factor
/// of 3 means the object is stored on 3 nodes. Routing logic can select any replica for reads.
/// </para>
/// <para>
/// <strong>Content Hash:</strong> Used for integrity verification during reads and replica
/// synchronization. Typically a SHA-256 hash of the object content.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Object location manifest entry")]
public sealed record ObjectLocationEntry
{
    /// <summary>
    /// Gets the unique identifier for the object.
    /// </summary>
    public required ObjectIdentity ObjectId { get; init; }

    /// <summary>
    /// Gets the list of node IDs hosting replicas of this object.
    /// </summary>
    /// <remarks>
    /// The list is ordered by preference (primary replica first). Routing logic can select
    /// any node for reads, but should prefer the first node for writes.
    /// </remarks>
    public required IReadOnlyList<string> NodeIds { get; init; }

    /// <summary>
    /// Gets the timestamp when this object was created.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Gets the timestamp when this entry was last updated.
    /// </summary>
    /// <remarks>
    /// Updated when replica locations change (e.g., during rebalancing or failure recovery).
    /// </remarks>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// Gets the size of the object in bytes.
    /// </summary>
    public required long SizeBytes { get; init; }

    /// <summary>
    /// Gets the content hash of the object (optional).
    /// </summary>
    /// <remarks>
    /// Typically a SHA-256 hash encoded as a hex string. Used for integrity verification.
    /// </remarks>
    public string? ContentHash { get; init; }

    /// <summary>
    /// Gets the replication factor for this object.
    /// </summary>
    /// <remarks>
    /// The replication factor indicates how many replicas should exist. It may not match
    /// the actual number of NodeIds if replication is in progress or a node has failed.
    /// </remarks>
    public int ReplicationFactor { get; init; } = 1;
}
