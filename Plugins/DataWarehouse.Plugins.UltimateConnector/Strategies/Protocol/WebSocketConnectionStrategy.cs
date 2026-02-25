using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Protocol
{
    /// <summary>
    /// Connection strategy for WebSocket endpoints.
    /// Establishes bidirectional communication channel over WebSocket protocol.
    /// </summary>
    public class WebSocketConnectionStrategy : ConnectionStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "websocket";

        /// <inheritdoc/>
        public override string DisplayName => "WebSocket";

        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Protocol;

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new();

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Connects via WebSocket protocol for real-time bidirectional communication";

        /// <inheritdoc/>
        public override string[] Tags => new[] { "websocket", "ws", "realtime", "protocol", "streaming" };

        /// <summary>
        /// Initializes a new instance of <see cref="WebSocketConnectionStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger.</param>
        public WebSocketConnectionStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var endpoint = config.ConnectionString;
            if (string.IsNullOrWhiteSpace(endpoint))
                throw new ArgumentException("WebSocket URL is required in ConnectionString");

            var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

            var subprotocol = GetConfiguration(config, "Subprotocol", string.Empty);
            if (!string.IsNullOrEmpty(subprotocol))
                ws.Options.AddSubProtocol(subprotocol);

            await ws.ConnectAsync(new Uri(endpoint), ct);

            if (ws.State != WebSocketState.Open)
                throw new InvalidOperationException($"WebSocket connection failed. State: {ws.State}");

            var info = new Dictionary<string, object>
            {
                ["endpoint"] = endpoint,
                ["protocol"] = "WebSocket",
                ["state"] = ws.State.ToString(),
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(ws, info);
        }

        /// <inheritdoc/>
        protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var ws = handle.GetConnection<ClientWebSocket>();
            return Task.FromResult(ws.State == WebSocketState.Open);
        }

        /// <inheritdoc/>
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var ws = handle.GetConnection<ClientWebSocket>();
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Connection closed", ct);
            ws.Dispose();
        }

        /// <inheritdoc/>
        protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var ws = handle.GetConnection<ClientWebSocket>();
            var isHealthy = ws.State == WebSocketState.Open;

            return Task.FromResult(new ConnectionHealth(
                IsHealthy: isHealthy,
                StatusMessage: $"WebSocket state: {ws.State}",
                Latency: TimeSpan.Zero,
                CheckedAt: DateTimeOffset.UtcNow));
        }
    }
}
