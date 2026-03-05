using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;

namespace DataWarehouse.SDK.Federation.Orchestration;

/// <summary>
/// Represents a node registration request with topology and capacity metadata.
/// </summary>
/// <remarks>
/// <para>
/// NodeRegistration is sent when a storage node joins the federation. It contains
/// the node's identity, network address, topology location (rack/datacenter/region),
/// geographic coordinates, and storage capacity.
/// </para>
/// <para>
/// <strong>Topology Metadata:</strong> Rack, Datacenter, and Region enable location-aware
/// routing. Geographic coordinates (Latitude/Longitude) provide fallback proximity
/// calculations when logical topology is insufficient.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Node registration metadata")]
public sealed record NodeRegistration
{
    /// <summary>
    /// Gets the unique identifier for the node being registered.
    /// </summary>
    public required string NodeId { get; init; }

    /// <summary>
    /// Gets the network address for the node (hostname or IP).
    /// </summary>
    public required string Address { get; init; }

    /// <summary>
    /// Gets the network port for the node.
    /// </summary>
    public required int Port { get; init; }

    /// <summary>
    /// Gets the rack identifier for the node (optional).
    /// </summary>
    public string? Rack { get; init; }

    /// <summary>
    /// Gets the datacenter identifier for the node (optional).
    /// </summary>
    public string? Datacenter { get; init; }

    /// <summary>
    /// Gets the region identifier for the node (optional).
    /// </summary>
    public string? Region { get; init; }

    /// <summary>
    /// Gets the latitude coordinate for the node (optional).
    /// </summary>
    public double? Latitude { get; init; }

    /// <summary>
    /// Gets the longitude coordinate for the node (optional).
    /// </summary>
    public double? Longitude { get; init; }

    /// <summary>
    /// Gets the total storage capacity in bytes.
    /// </summary>
    public long TotalBytes { get; init; }

    /// <summary>
    /// Gets the free storage in bytes at the time of registration.
    /// </summary>
    /// <remarks>
    /// Should be set by re-joining nodes to report their current free space.
    /// A value of 0 means "not provided" — the orchestrator will default to TotalBytes
    /// (assuming a fresh/empty node). For re-joining nodes with existing data, always
    /// set this to the actual free space to avoid write-routing to nearly-full nodes.
    /// </remarks>
    public long FreeBytes { get; init; }

    /// <summary>
    /// Gets the timestamp when this registration was created.
    /// </summary>
    public DateTimeOffset RegisteredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets additional metadata tags for the node (optional).
    /// </summary>
    /// <remarks>
    /// Tags can include custom metadata such as hardware type, storage tier, or deployment environment.
    /// </remarks>
    public IReadOnlyDictionary<string, string>? Tags { get; init; }
}
