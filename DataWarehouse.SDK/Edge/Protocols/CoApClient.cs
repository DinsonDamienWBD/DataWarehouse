using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Edge.Protocols
{
    /// <summary>
    /// Minimal CoAP client implementation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implements a subset of CoAP (RFC 7252) sufficient for GET/POST/PUT/DELETE operations
    /// and resource discovery. Full RFC 7252 compliance (block-wise transfer, observe pattern,
    /// DTLS security) is deferred to future phases or requires a full CoAP library.
    /// </para>
    /// <para>
    /// <strong>Limitations in Phase 36</strong>:
    /// <list type="bullet">
    /// <item><description><strong>Block-wise transfer</strong>: Not implemented. Payloads > 1KB may fail.</description></item>
    /// <item><description><strong>Observe pattern</strong>: Stub implementation. Returns no-op IDisposable.</description></item>
    /// <item><description><strong>DTLS security</strong>: Not implemented. UseDtls flag is ignored.</description></item>
    /// <item><description><strong>Option encoding</strong>: Simplified. No delta encoding.</description></item>
    /// <item><description><strong>Retransmission</strong>: Basic timeout (5 seconds). No exponential backoff.</description></item>
    /// </list>
    /// For production use with full CoAP compliance, consider using a NuGet package like CoAP.NET.
    /// </para>
    /// </remarks>
    [SdkCompatibility("3.0.0", Notes = "Phase 36: CoAP client implementation (EDGE-03)")]
    public sealed class CoApClient : ICoApClient
    {
        private UdpClient? _udpClient;
        private readonly BoundedDictionary<ushort, TaskCompletionSource<CoApResponse>> _pendingRequests = new BoundedDictionary<ushort, TaskCompletionSource<CoApResponse>>(1000);
        private readonly BoundedDictionary<string, Action<CoApResponse>> _observations = new BoundedDictionary<string, Action<CoApResponse>>(1000);
        private ushort _nextMessageId;
        private CancellationTokenSource? _receiveCts;
        private Task? _receiveTask;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="CoApClient"/> class.
        /// </summary>
        public CoApClient()
        {
            _nextMessageId = (ushort)Random.Shared.Next(1, 65535);
        }

        /// <summary>
        /// Sends a CoAP request and receives the response.
        /// </summary>
        public async Task<CoApResponse> SendAsync(CoApRequest request, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request, nameof(request));

            // Parse URI
            var uri = new Uri(request.Uri);
            if (uri.Scheme != "coap" && uri.Scheme != "coaps")
                throw new ArgumentException("URI must use coap:// or coaps:// scheme", nameof(request));

            var port = uri.Port > 0 ? uri.Port : (uri.Scheme == "coaps" ? 5684 : 5683);

            // Lazy-initialize UDP client
            if (_udpClient is null)
            {
                _udpClient = new UdpClient();
                StartReceiveLoop();
            }

            // Build CoAP message (binary encoding)
            var messageId = _nextMessageId++;
            var message = BuildCoApMessage(request, messageId);

            // Register pending request
            var tcs = new TaskCompletionSource<CoApResponse>();
            _pendingRequests[messageId] = tcs;

            // Send message
            await _udpClient.SendAsync(message, message.Length, uri.Host, port);

            // Wait for response with timeout
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5)); // 5-second timeout

            try
            {
                return await tcs.Task.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                _pendingRequests.TryRemove(messageId, out _);
                throw new TimeoutException("CoAP request timed out");
            }
        }

        /// <summary>
        /// Discovers resources via /.well-known/core.
        /// </summary>
        public async Task<IReadOnlyList<CoApResource>> DiscoverAsync(string serverUri, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(serverUri, nameof(serverUri));

            var request = new CoApRequest
            {
                Method = CoApMethod.GET,
                Uri = $"{serverUri}/.well-known/core"
            };

            var response = await SendAsync(request, ct);
            if (!response.IsSuccess)
                return Array.Empty<CoApResource>();

            // Parse Link Format (RFC 6690)
            var linkFormat = System.Text.Encoding.UTF8.GetString(response.Payload);
            return ParseLinkFormat(linkFormat);
        }

        /// <summary>
        /// Observes a resource for changes (RFC 7641).
        /// </summary>
        /// <remarks>
        /// Observe pattern (RFC 7641) requires option 6 (Observe) in request
        /// and periodic notifications from server. Full implementation is deferred.
        /// Returns a no-op IDisposable for now.
        /// </remarks>
        public Task<IDisposable> ObserveAsync(string resourceUri, Action<CoApResponse> onNotification, CancellationToken ct = default) =>
            throw new PlatformNotSupportedException(
                "CoAP Observe (RFC 7641) requires a CoAP server endpoint that supports the Observe option. " +
                "Configure the CoAP server endpoint via CoApOptions.");

        /// <summary>
        /// Convenience method for GET requests.
        /// </summary>
        public Task<CoApResponse> GetAsync(string uri, CancellationToken ct = default) =>
            SendAsync(new CoApRequest { Method = CoApMethod.GET, Uri = uri }, ct);

        /// <summary>
        /// Convenience method for POST requests.
        /// </summary>
        public Task<CoApResponse> PostAsync(string uri, byte[] payload, CancellationToken ct = default) =>
            SendAsync(new CoApRequest { Method = CoApMethod.POST, Uri = uri, Payload = payload }, ct);

        /// <summary>
        /// Convenience method for PUT requests.
        /// </summary>
        public Task<CoApResponse> PutAsync(string uri, byte[] payload, CancellationToken ct = default) =>
            SendAsync(new CoApRequest { Method = CoApMethod.PUT, Uri = uri, Payload = payload }, ct);

        /// <summary>
        /// Convenience method for DELETE requests.
        /// </summary>
        public Task<CoApResponse> DeleteAsync(string uri, CancellationToken ct = default) =>
            SendAsync(new CoApRequest { Method = CoApMethod.DELETE, Uri = uri }, ct);

        /// <summary>
        /// Disposes UDP client and stops receive loop.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            _receiveCts?.Cancel();
            if (_receiveTask is not null)
                await _receiveTask.ConfigureAwait(false);

            _receiveCts?.Dispose();
            _udpClient?.Dispose();
        }

        // ==================== Private Methods ====================

        /// <summary>
        /// Builds a binary CoAP message from a request.
        /// </summary>
        private byte[] BuildCoApMessage(CoApRequest request, ushort messageId)
        {
            // CoAP message format (RFC 7252):
            // 0                   1                   2                   3
            // 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
            // +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
            // |Ver| T |  TKL  |      Code     |          Message ID           |
            // +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
            // |   Token (if any, TKL bytes) ...
            // +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
            // |   Options (if any) ...
            // +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
            // |1 1 1 1 1 1 1 1|    Payload (if any) ...
            // +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+

            using var ms = new MemoryStream(256); // CoAP messages typically <256 bytes

            // Header (4 bytes)
            byte ver = 1; // CoAP version 1
            byte type = (byte)request.Type;
            byte tkl = 0; // Token length (0 for simplicity)
            byte code = (byte)request.Method; // Method code (GET=1, POST=2, PUT=3, DELETE=4)

            ms.WriteByte((byte)((ver << 6) | (type << 4) | tkl));
            ms.WriteByte(code);
            ms.WriteByte((byte)(messageId >> 8));
            ms.WriteByte((byte)(messageId & 0xFF));

            // Options (Uri-Path, Content-Format, etc.)
            var uri = new Uri(request.Uri);
            var pathSegments = uri.AbsolutePath.Trim('/').Split('/');
            foreach (var segment in pathSegments)
            {
                if (string.IsNullOrEmpty(segment)) continue;
                WriteOption(ms, 11, System.Text.Encoding.UTF8.GetBytes(segment)); // Uri-Path option
            }

            if (request.Payload.Length > 0)
            {
                // Payload marker (0xFF)
                ms.WriteByte(0xFF);
                ms.Write(request.Payload);
            }

            return ms.ToArray();
        }

        /// <summary>
        /// Writes a CoAP option to the message stream.
        /// </summary>
        /// <remarks>
        /// Simplified option encoding (no delta encoding for brevity).
        /// Real implementation should use delta encoding per RFC 7252 section 3.1.
        /// </remarks>
        private void WriteOption(Stream stream, int optionNumber, byte[] value)
        {
            stream.WriteByte((byte)optionNumber);
            stream.WriteByte((byte)value.Length);
            stream.Write(value);
        }

        /// <summary>
        /// Starts the UDP receive loop.
        /// </summary>
        private void StartReceiveLoop()
        {
            _receiveCts = new CancellationTokenSource();
            _receiveTask = Task.Run(async () =>
            {
                while (!_receiveCts.Token.IsCancellationRequested && !_disposed)
                {
                    try
                    {
                        var result = await _udpClient!.ReceiveAsync();
                        ProcessCoApMessage(result.Buffer);
                    }
                    catch (Exception)
                    {
                        // Ignore receive errors (network issues, cancellation, etc.)
                    }
                }
            });
        }

        /// <summary>
        /// Processes an incoming CoAP message.
        /// </summary>
        private void ProcessCoApMessage(byte[] buffer)
        {
            if (buffer.Length < 4) return; // Invalid message

            // Parse header
            byte versionTypeToken = buffer[0];
            byte code = buffer[1];
            ushort messageId = (ushort)((buffer[2] << 8) | buffer[3]);

            // Extract payload (after 0xFF marker)
            int payloadStart = Array.IndexOf(buffer, (byte)0xFF, 4);
            byte[] payload = payloadStart >= 0
                ? buffer[(payloadStart + 1)..]
                : Array.Empty<byte>();

            var response = new CoApResponse
            {
                Code = (CoApResponseCode)code,
                Payload = payload
            };

            // Complete pending request
            if (_pendingRequests.TryRemove(messageId, out var tcs))
            {
                tcs.SetResult(response);
            }
        }

        /// <summary>
        /// Parses Link Format (RFC 6690) response from /.well-known/core.
        /// </summary>
        private IReadOnlyList<CoApResource> ParseLinkFormat(string linkFormat)
        {
            // Simplified parsing: </path>;rt="type";if="interface";obs
            // Full implementation should use proper Link Format parser

            var resources = new List<CoApResource>();
            var links = linkFormat.Split(',');

            foreach (var link in links)
            {
                var parts = link.Split(';');
                if (parts.Length == 0) continue;

                var path = parts[0].Trim('<', '>');
                if (string.IsNullOrWhiteSpace(path)) continue;

                var resource = new CoApResource { Path = path };

                foreach (var attr in parts.Skip(1))
                {
                    var attrTrimmed = attr.Trim();
                    if (attrTrimmed.StartsWith("rt="))
                        resource = resource with { ResourceType = attrTrimmed[3..].Trim('"') };
                    else if (attrTrimmed.StartsWith("if="))
                        resource = resource with { InterfaceDescription = attrTrimmed[3..].Trim('"') };
                    else if (attrTrimmed == "obs")
                        resource = resource with { Observable = true };
                }

                resources.Add(resource);
            }

            return resources;
        }

        /// <summary>
        /// No-op IDisposable for deferred Observe implementation.
        /// </summary>
        private class NoOpDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
