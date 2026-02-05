using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.IoT
{
    /// <summary>
    /// Connection strategy for AWS IoT Core.
    /// Tests connectivity via HTTPS to AWS IoT endpoint.
    /// </summary>
    public class AwsIoTCoreConnectionStrategy : IoTConnectionStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "aws-iot-core";

        /// <inheritdoc/>
        public override string DisplayName => "AWS IoT Core";

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new();

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Connects to AWS IoT Core for managed IoT device connectivity and messaging";

        /// <inheritdoc/>
        public override string[] Tags => new[] { "aws", "iot", "cloud", "mqtt", "managed" };

        /// <summary>
        /// Initializes a new instance of <see cref="AwsIoTCoreConnectionStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger.</param>
        public AwsIoTCoreConnectionStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var endpoint = config.ConnectionString;
            if (string.IsNullOrWhiteSpace(endpoint) || !endpoint.Contains(".iot.") || !endpoint.Contains(".amazonaws.com"))
                throw new ArgumentException("AWS IoT Core endpoint URL is required (*.iot.*.amazonaws.com)");

            var client = new HttpClient { BaseAddress = new Uri($"https://{endpoint}") };

            var response = await client.GetAsync("/", ct);

            var info = new Dictionary<string, object>
            {
                ["endpoint"] = endpoint,
                ["protocol"] = "AWS IoT Core",
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(client, info);
        }

        /// <inheritdoc/>
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            var response = await client.GetAsync("/", ct);
            return response.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable;
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
                StatusMessage: isHealthy ? "AWS IoT Core endpoint responsive" : "AWS IoT Core endpoint unreachable",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow);
        }

        /// <inheritdoc/>
        public override Task<Dictionary<string, object>> ReadTelemetryAsync(IConnectionHandle handle, string deviceId, CancellationToken ct = default)
        {
            throw new NotSupportedException("ReadTelemetryAsync requires AWS IoT SDK implementation");
        }

        /// <inheritdoc/>
        public override Task<string> SendCommandAsync(IConnectionHandle handle, string deviceId, string command, Dictionary<string, object>? parameters = null, CancellationToken ct = default)
        {
            throw new NotSupportedException("SendCommandAsync requires AWS IoT SDK implementation");
        }
    }
}
