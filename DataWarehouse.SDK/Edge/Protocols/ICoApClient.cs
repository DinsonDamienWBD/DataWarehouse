using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Edge.Protocols
{
    /// <summary>
    /// CoAP client interface for constrained application protocol operations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// CoAP (Constrained Application Protocol) is a lightweight RESTful protocol designed for
    /// IoT devices and constrained networks. It runs over UDP and provides similar semantics
    /// to HTTP (GET, POST, PUT, DELETE) with much lower overhead.
    /// </para>
    /// <para>
    /// <strong>Key Features</strong>:
    /// <list type="bullet">
    /// <item><description>REST-like methods (GET, POST, PUT, DELETE)</description></item>
    /// <item><description>Resource discovery via /.well-known/core</description></item>
    /// <item><description>Observable resources (push notifications via RFC 7641)</description></item>
    /// <item><description>Block-wise transfer for large payloads (RFC 7959)</description></item>
    /// <item><description>DTLS 1.2 for secure communication</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    [SdkCompatibility("3.0.0", Notes = "Phase 36: CoAP client interface (EDGE-03)")]
    public interface ICoApClient : IAsyncDisposable
    {
        /// <summary>
        /// Sends a CoAP request and receives the response.
        /// </summary>
        /// <param name="request">CoAP request to send.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>CoAP response from the server.</returns>
        /// <exception cref="TimeoutException">Thrown when request times out (default 5 seconds).</exception>
        /// <exception cref="ArgumentException">Thrown when URI scheme is not coap:// or coaps://.</exception>
        Task<CoApResponse> SendAsync(CoApRequest request, CancellationToken ct = default);

        /// <summary>
        /// Discovers resources via /.well-known/core (RFC 6690).
        /// </summary>
        /// <param name="serverUri">CoAP server URI (e.g., "coap://192.168.1.10:5683").</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>List of discovered resources with metadata.</returns>
        /// <remarks>
        /// Sends a GET request to /.well-known/core and parses the Link Format response.
        /// Returns empty list if discovery fails or server does not support resource discovery.
        /// </remarks>
        Task<IReadOnlyList<CoApResource>> DiscoverAsync(string serverUri, CancellationToken ct = default);

        /// <summary>
        /// Observes a resource for changes (RFC 7641 Observe pattern).
        /// </summary>
        /// <param name="resourceUri">Resource URI to observe.</param>
        /// <param name="onNotification">Callback invoked when resource changes (push notification).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Observation token for cancellation. Dispose to stop observing.</returns>
        /// <remarks>
        /// CoAP Observe allows servers to push notifications when resources change, avoiding
        /// polling. The callback is invoked on a background thread whenever the server sends
        /// an updated resource representation.
        /// </remarks>
        Task<IDisposable> ObserveAsync(string resourceUri, Action<CoApResponse> onNotification, CancellationToken ct = default);

        /// <summary>
        /// Convenience method for GET requests.
        /// </summary>
        /// <param name="uri">Resource URI.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>CoAP response.</returns>
        Task<CoApResponse> GetAsync(string uri, CancellationToken ct = default);

        /// <summary>
        /// Convenience method for POST requests.
        /// </summary>
        /// <param name="uri">Resource URI.</param>
        /// <param name="payload">Request payload.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>CoAP response.</returns>
        Task<CoApResponse> PostAsync(string uri, byte[] payload, CancellationToken ct = default);

        /// <summary>
        /// Convenience method for PUT requests.
        /// </summary>
        /// <param name="uri">Resource URI.</param>
        /// <param name="payload">Request payload.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>CoAP response.</returns>
        Task<CoApResponse> PutAsync(string uri, byte[] payload, CancellationToken ct = default);

        /// <summary>
        /// Convenience method for DELETE requests.
        /// </summary>
        /// <param name="uri">Resource URI.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>CoAP response.</returns>
        Task<CoApResponse> DeleteAsync(string uri, CancellationToken ct = default);
    }
}
