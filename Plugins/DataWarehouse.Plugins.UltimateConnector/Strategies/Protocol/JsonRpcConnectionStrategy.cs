using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Protocol
{
    /// <summary>
    /// Connection strategy for JSON-RPC 2.0 endpoints.
    /// Tests connectivity via HTTP POST with JSON-RPC request.
    /// </summary>
    public class JsonRpcConnectionStrategy : ConnectionStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "jsonrpc";

        /// <inheritdoc/>
        public override string DisplayName => "JSON-RPC";

        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Protocol;

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new();

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Connects to JSON-RPC 2.0 services for remote procedure calls over HTTP";

        /// <inheritdoc/>
        public override string[] Tags => new[] { "jsonrpc", "rpc", "json", "protocol", "http" };

        /// <summary>
        /// Initializes a new instance of <see cref="JsonRpcConnectionStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger.</param>
        public JsonRpcConnectionStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var endpoint = config.ConnectionString;
            if (string.IsNullOrWhiteSpace(endpoint))
                throw new ArgumentException("JSON-RPC endpoint URL is required in ConnectionString");

            var client = new HttpClient { BaseAddress = new Uri(endpoint) };

            var rpcRequest = @"{""jsonrpc"":""2.0"",""method"":""ping"",""params"":[],""id"":1}";
            var content = new StringContent(rpcRequest, Encoding.UTF8, "application/json");
            var response = await client.PostAsync("", content, ct);
            response.EnsureSuccessStatusCode();

            var info = new Dictionary<string, object>
            {
                ["endpoint"] = endpoint,
                ["protocol"] = "JSON-RPC 2.0",
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(client, info);
        }

        /// <inheritdoc/>
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            var rpcRequest = @"{""jsonrpc"":""2.0"",""method"":""ping"",""params"":[],""id"":1}";
            var content = new StringContent(rpcRequest, Encoding.UTF8, "application/json");
            var response = await client.PostAsync("", content, ct);
            return response.IsSuccessStatusCode;
        }

        /// <inheritdoc/>
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            client.Dispose();
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();

            return new ConnectionHealth(
                IsHealthy: isHealthy,
                StatusMessage: isHealthy ? "JSON-RPC endpoint responsive" : "JSON-RPC endpoint unreachable",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow);
        }
    }
}
