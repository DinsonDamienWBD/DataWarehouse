using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;

namespace DataWarehouse.SDK.Edge.Protocols
{
    /// <summary>
    /// CoAP response record.
    /// </summary>
    /// <remarks>
    /// Represents a CoAP response per RFC 7252. Contains status code, payload, and options.
    /// </remarks>
    [SdkCompatibility("3.0.0", Notes = "Phase 36: CoAP response record (EDGE-03)")]
    public sealed record CoApResponse
    {
        /// <summary>Gets or sets the CoAP response code (2.xx for success, 4.xx for client error, 5.xx for server error).</summary>
        public required CoApResponseCode Code { get; init; }

        /// <summary>Gets or sets the response payload.</summary>
        public byte[] Payload { get; init; } = Array.Empty<byte>();

        /// <summary>Gets or sets CoAP options (option number -> value). Optional.</summary>
        public IReadOnlyDictionary<int, byte[]>? Options { get; init; }

        /// <summary>
        /// Gets whether the response indicates success (2.xx response code).
        /// </summary>
        /// <remarks>
        /// CoAP wire-encoded codes use a compact bit format: high 3 bits = class, low 5 bits = detail
        /// (e.g., 0x45 = 2.05 Content). This property checks the 3-bit class field for class 2 (success),
        /// and also accepts HTTP-style integer values 200-299 for interoperability with the typed enum.
        /// </remarks>
        public bool IsSuccess
        {
            get
            {
                int code = (int)Code;
                // HTTP-style integers (enum values 200-299): standard success range
                if (code >= 200 && code < 300) return true;
                // CoAP wire-encoded byte: high 3 bits = class (0-7), low 5 bits = detail (0-31)
                // Class 2 = success (0x40..0x5F range)
                if (code is >= 0x40 and <= 0x5F) return true;
                return false;
            }
        }
    }

    /// <summary>
    /// CoAP response codes.
    /// </summary>
    public enum CoApResponseCode
    {
        // Success 2.xx
        /// <summary>Resource created (2.01).</summary>
        Created = 201,

        /// <summary>Resource deleted (2.02).</summary>
        Deleted = 202,

        /// <summary>Resource valid (2.03).</summary>
        Valid = 203,

        /// <summary>Resource changed (2.04).</summary>
        Changed = 204,

        /// <summary>Resource content returned (2.05).</summary>
        Content = 205,

        // Client Error 4.xx
        /// <summary>Bad request (4.00).</summary>
        BadRequest = 400,

        /// <summary>Unauthorized (4.01).</summary>
        Unauthorized = 401,

        /// <summary>Bad option (4.02).</summary>
        BadOption = 402,

        /// <summary>Forbidden (4.03).</summary>
        Forbidden = 403,

        /// <summary>Not found (4.04).</summary>
        NotFound = 404,

        /// <summary>Method not allowed (4.05).</summary>
        MethodNotAllowed = 405,

        /// <summary>Not acceptable (4.06).</summary>
        NotAcceptable = 406,

        /// <summary>Precondition failed (4.12).</summary>
        PreconditionFailed = 412,

        /// <summary>Request entity too large (4.13).</summary>
        RequestEntityTooLarge = 413,

        /// <summary>Unsupported content format (4.15).</summary>
        UnsupportedContentFormat = 415,

        // Server Error 5.xx
        /// <summary>Internal server error (5.00).</summary>
        InternalServerError = 500,

        /// <summary>Not implemented (5.01).</summary>
        NotImplemented = 501,

        /// <summary>Bad gateway (5.02).</summary>
        BadGateway = 502,

        /// <summary>Service unavailable (5.03).</summary>
        ServiceUnavailable = 503,

        /// <summary>Gateway timeout (5.04).</summary>
        GatewayTimeout = 504,

        /// <summary>Proxying not supported (5.05).</summary>
        ProxyingNotSupported = 505
    }
}
