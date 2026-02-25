using DataWarehouse.SDK.Contracts;
using System;

namespace DataWarehouse.SDK.Edge.Protocols
{
    /// <summary>
    /// CoAP resource metadata from discovery.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Represents a resource discovered via CoAP's /.well-known/core endpoint (RFC 6690 Link Format).
    /// CoAP resource discovery returns a list of resources with attributes describing their capabilities.
    /// </para>
    /// <para>
    /// <strong>Link Format Attributes</strong>:
    /// <list type="bullet">
    /// <item><description><strong>rt</strong> (Resource Type): Semantic type of the resource (e.g., "temperature", "light")</description></item>
    /// <item><description><strong>if</strong> (Interface Description): Interaction pattern (e.g., "sensor", "actuator")</description></item>
    /// <item><description><strong>sz</strong> (Size): Estimated resource size in bytes</description></item>
    /// <item><description><strong>title</strong>: Human-readable title</description></item>
    /// <item><description><strong>obs</strong>: Resource is observable (supports CoAP Observe pattern per RFC 7641)</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    [SdkCompatibility("3.0.0", Notes = "Phase 36: CoAP resource metadata (EDGE-03)")]
    public sealed record CoApResource
    {
        /// <summary>Gets or sets the resource path (e.g., "/sensors/temp").</summary>
        public required string Path { get; init; }

        /// <summary>Gets or sets the resource type (rt= attribute).</summary>
        public string? ResourceType { get; init; }

        /// <summary>Gets or sets the interface description (if= attribute).</summary>
        public string? InterfaceDescription { get; init; }

        /// <summary>Gets or sets the estimated size in bytes (sz= attribute).</summary>
        public int? MaxSizeEstimate { get; init; }

        /// <summary>Gets or sets the human-readable title (title= attribute).</summary>
        public string? Title { get; init; }

        /// <summary>Gets or sets whether the resource is observable (obs attribute).</summary>
        public bool Observable { get; init; }
    }
}
