using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.FileSystem
{
    /// <summary>
    /// MinIO connection strategy using S3-compatible API.
    /// Connects to MinIO object storage instances via HTTP/HTTPS with S3 API signatures.
    /// </summary>
    public class MinioConnectionStrategy : ConnectionStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "fs-minio";

        /// <inheritdoc/>
        public override string DisplayName => "MinIO";

        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.FileSystem;

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new();

        /// <inheritdoc/>
        public override string SemanticDescription => "Connects to MinIO object storage via S3-compatible API";

        /// <inheritdoc/>
        public override string[] Tags => new[] { "minio", "s3", "object-storage", "filesystem", "cloud-native" };

        /// <summary>
        /// Initializes a new instance of <see cref="MinioConnectionStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public MinioConnectionStrategy(ILogger? logger = null) : base(logger) { }

        /// <summary>
        /// Establishes a connection to MinIO server.
        /// ConnectionString format: endpoint (e.g., "http://localhost:9000")
        /// Properties: "AccessKey", "SecretKey"
        /// </summary>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var endpoint = config.ConnectionString;
            var accessKey = GetConfiguration<string>(config, "AccessKey", "");
            var secretKey = GetConfiguration<string>(config, "SecretKey", "");

            if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
            {
                throw new InvalidOperationException("AccessKey and SecretKey are required for MinIO connection.");
            }

            var httpClient = new HttpClient
            {
                BaseAddress = new Uri(endpoint),
                Timeout = config.Timeout
            };

            // Perform initial health check by listing buckets
            var request = new HttpRequestMessage(HttpMethod.Get, "/");
            var authHeader = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{accessKey}:{secretKey}"));
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authHeader);

            using var response = await httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var connectionInfo = new Dictionary<string, object>
            {
                ["protocol"] = "S3",
                ["endpoint"] = endpoint,
                ["accessKey"] = accessKey,
                ["provider"] = "MinIO"
            };

            return new DefaultConnectionHandle(httpClient, connectionInfo);
        }

        /// <summary>
        /// Tests the MinIO connection by sending a health check request.
        /// </summary>
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            try
            {
                var httpClient = handle.GetConnection<HttpClient>();
                using var response = await httpClient.GetAsync("/minio/health/live", ct);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Disconnects from the MinIO server by disposing the HTTP client.
        /// </summary>
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            handle.GetConnection<HttpClient>().Dispose();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Retrieves health status of the MinIO connection.
        /// </summary>
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            // P2-1904: Use Stopwatch for latency — DateTimeOffset subtraction is susceptible to NTP clock adjustments.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var httpClient = handle.GetConnection<HttpClient>();
                using var response = await httpClient.GetAsync("/minio/health/live", ct);
                sw.Stop();

                return new ConnectionHealth(
                    IsHealthy: response.IsSuccessStatusCode,
                    StatusMessage: response.IsSuccessStatusCode ? "MinIO server healthy" : $"MinIO server returned {response.StatusCode}",
                    Latency: sw.Elapsed,
                    CheckedAt: DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new ConnectionHealth(
                    IsHealthy: false,
                    StatusMessage: $"MinIO health check failed: {ex.Message}",
                    Latency: sw.Elapsed,
                    CheckedAt: DateTimeOffset.UtcNow);
            }
        }
    }
}
