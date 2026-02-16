using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;

namespace DataWarehouse.SDK.Edge.Protocols
{
    /// <summary>
    /// CoAP request record.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Represents a CoAP (Constrained Application Protocol) request per RFC 7252.
    /// CoAP is a lightweight REST-like protocol designed for constrained devices
    /// and low-bandwidth networks (IoT, edge computing).
    /// </para>
    /// <para>
    /// <strong>Message Types</strong>:
    /// <list type="bullet">
    /// <item><description><strong>Confirmable (CON)</strong>: Requires acknowledgment from recipient. Reliable delivery.</description></item>
    /// <item><description><strong>Non-Confirmable (NON)</strong>: Fire-and-forget. No acknowledgment required.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Options</strong>: CoAP options extend request metadata (URI path, content format, etc.).
    /// Options are encoded as (option number, value) pairs. Common options include:
    /// <list type="bullet">
    /// <item><description>Option 11 (Uri-Path): Path segment</description></item>
    /// <item><description>Option 12 (Content-Format): Media type (0 = text/plain, 40 = application/link-format, 50 = application/json)</description></item>
    /// <item><description>Option 15 (Uri-Query): Query parameter</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    [SdkCompatibility("3.0.0", Notes = "Phase 36: CoAP request record (EDGE-03)")]
    public sealed record CoApRequest
    {
        /// <summary>Gets or sets the CoAP method (GET, POST, PUT, DELETE).</summary>
        public required CoApMethod Method { get; init; }

        /// <summary>Gets or sets the CoAP URI (coap://host:port/path or coaps://host:port/path).</summary>
        public required string Uri { get; init; }

        /// <summary>Gets or sets the request payload. Empty for GET/DELETE.</summary>
        public byte[] Payload { get; init; } = Array.Empty<byte>();

        /// <summary>Gets or sets the message type (Confirmable or Non-Confirmable).</summary>
        public CoApMessageType Type { get; init; } = CoApMessageType.Confirmable;

        /// <summary>Gets or sets CoAP options (option number -> value). Optional.</summary>
        public IReadOnlyDictionary<int, byte[]>? Options { get; init; }

        /// <summary>Gets or sets whether to use DTLS 1.2 for secure communication (coaps://).</summary>
        public bool UseDtls { get; init; } = false;
    }

    /// <summary>
    /// CoAP method codes.
    /// </summary>
    public enum CoApMethod
    {
        /// <summary>GET method (retrieve resource).</summary>
        GET = 1,

        /// <summary>POST method (create resource or trigger action).</summary>
        POST = 2,

        /// <summary>PUT method (update or create resource).</summary>
        PUT = 3,

        /// <summary>DELETE method (delete resource).</summary>
        DELETE = 4
    }

    /// <summary>
    /// CoAP message types.
    /// </summary>
    public enum CoApMessageType
    {
        /// <summary>Confirmable message (requires acknowledgment).</summary>
        Confirmable = 0,

        /// <summary>Non-confirmable message (fire-and-forget).</summary>
        NonConfirmable = 1,

        /// <summary>Acknowledgement message.</summary>
        Acknowledgement = 2,

        /// <summary>Reset message.</summary>
        Reset = 3
    }
}
