using System.Security.Cryptography;
using System.Text;

namespace DataWarehouse.Plugins.LogicMonitor
{
    #region Configuration

    /// <summary>
    /// Configuration options for the LogicMonitor plugin.
    /// </summary>
    public sealed class LogicMonitorConfiguration
    {
        /// <summary>
        /// Gets or sets the LogicMonitor account name (company subdomain).
        /// </summary>
        public string AccountName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the API Access ID for authentication.
        /// </summary>
        public string AccessId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the API Access Key for authentication.
        /// </summary>
        public string AccessKey { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the resource ID or name for metric grouping.
        /// </summary>
        public string ResourceId { get; set; } = "datawarehouse_metrics";

        /// <summary>
        /// Gets or sets the resource name for display in LogicMonitor.
        /// </summary>
        public string ResourceName { get; set; } = "DataWarehouse";

        /// <summary>
        /// Gets or sets the resource description.
        /// </summary>
        public string ResourceDescription { get; set; } = "DataWarehouse telemetry metrics";

        /// <summary>
        /// Gets or sets the metric push interval in seconds.
        /// </summary>
        public int PushIntervalSeconds { get; set; } = 60;

        /// <summary>
        /// Gets or sets whether to use Bearer token authentication (true) or LMv1 signature (false).
        /// </summary>
        public bool UseBearerAuth { get; set; } = false;

        /// <summary>
        /// Gets or sets the Bearer token for authentication (if UseBearerAuth is true).
        /// </summary>
        public string? BearerToken { get; set; }

        /// <summary>
        /// Gets or sets additional resource properties as key-value pairs.
        /// </summary>
        public Dictionary<string, string>? ResourceProperties { get; set; }

        /// <summary>
        /// Gets or sets the maximum batch size for metric ingestion.
        /// </summary>
        public int MaxBatchSize { get; set; } = 100;

        /// <summary>
        /// Gets or sets the HTTP timeout in seconds.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Gets the LogicMonitor API base URL.
        /// </summary>
        public string ApiBaseUrl => $"https://{AccountName}.logicmonitor.com/rest";

        /// <summary>
        /// Validates the configuration.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when configuration is invalid.</exception>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(AccountName))
            {
                throw new InvalidOperationException("AccountName is required.");
            }

            if (UseBearerAuth)
            {
                if (string.IsNullOrWhiteSpace(BearerToken))
                {
                    throw new InvalidOperationException("BearerToken is required when UseBearerAuth is true.");
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(AccessId))
                {
                    throw new InvalidOperationException("AccessId is required for LMv1 authentication.");
                }
                if (string.IsNullOrWhiteSpace(AccessKey))
                {
                    throw new InvalidOperationException("AccessKey is required for LMv1 authentication.");
                }
            }

            if (string.IsNullOrWhiteSpace(ResourceId))
            {
                throw new InvalidOperationException("ResourceId is required.");
            }

            if (PushIntervalSeconds < 10)
            {
                throw new InvalidOperationException("PushIntervalSeconds must be at least 10 seconds.");
            }
        }
    }

    #endregion

    #region Metric Data Models

    /// <summary>
    /// Represents a single metric datapoint for LogicMonitor ingestion.
    /// </summary>
    public sealed class LogicMonitorDataPoint
    {
        /// <summary>
        /// Gets or sets the metric name.
        /// </summary>
        public string DataSource { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the datapoint name within the datasource.
        /// </summary>
        public string DataPointName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the metric value.
        /// </summary>
        public double Value { get; set; }

        /// <summary>
        /// Gets or sets the timestamp in epoch seconds (UTC).
        /// </summary>
        public long Timestamp { get; set; }

        /// <summary>
        /// Gets or sets optional dimensions (tags).
        /// </summary>
        public Dictionary<string, string>? Dimensions { get; set; }
    }

    /// <summary>
    /// Represents a metric ingestion request to LogicMonitor API v3.
    /// </summary>
    public sealed class MetricIngestRequest
    {
        /// <summary>
        /// Gets or sets the resource identifier.
        /// </summary>
        public ResourceIdentity? Resource { get; set; }

        /// <summary>
        /// Gets or sets the list of datapoints.
        /// </summary>
        public List<DataPointPayload>? DataPoints { get; set; }
    }

    /// <summary>
    /// Represents the resource identity for metric ingestion.
    /// </summary>
    public sealed class ResourceIdentity
    {
        /// <summary>
        /// Gets or sets the system display name.
        /// </summary>
        public string? System_DisplayName { get; set; }

        /// <summary>
        /// Gets or sets the system hostname.
        /// </summary>
        public string? System_Hostname { get; set; }

        /// <summary>
        /// Gets or sets resource properties.
        /// </summary>
        public Dictionary<string, string>? Properties { get; set; }
    }

    /// <summary>
    /// Represents a single datapoint payload.
    /// </summary>
    public sealed class DataPointPayload
    {
        /// <summary>
        /// Gets or sets the datasource name.
        /// </summary>
        public string? DataSource { get; set; }

        /// <summary>
        /// Gets or sets the datasource display name.
        /// </summary>
        public string? DataSourceDisplayName { get; set; }

        /// <summary>
        /// Gets or sets the datapoint name.
        /// </summary>
        public string? DataPointName { get; set; }

        /// <summary>
        /// Gets or sets the aggregation type (avg, sum, min, max).
        /// </summary>
        public string? DataPointAggregationType { get; set; }

        /// <summary>
        /// Gets or sets the instance name (for multi-instance datasources).
        /// </summary>
        public string? InstanceName { get; set; }

        /// <summary>
        /// Gets or sets the timestamp-value pairs.
        /// </summary>
        public Dictionary<string, string>? Values { get; set; }
    }

    #endregion

    #region Authentication

    /// <summary>
    /// Provides LogicMonitor LMv1 authentication signature generation.
    /// </summary>
    public static class LogicMonitorAuth
    {
        /// <summary>
        /// Generates the LMv1 authorization header value.
        /// </summary>
        /// <param name="accessId">The API Access ID.</param>
        /// <param name="accessKey">The API Access Key.</param>
        /// <param name="httpVerb">The HTTP verb (GET, POST, etc.).</param>
        /// <param name="resourcePath">The resource path (e.g., /metric/ingest).</param>
        /// <param name="data">The request body data (optional).</param>
        /// <returns>The LMv1 authorization header value.</returns>
        public static string GenerateLMv1Signature(
            string accessId,
            string accessKey,
            string httpVerb,
            string resourcePath,
            string data = "")
        {
            var epoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Construct the string to sign
            var stringToSign = $"{httpVerb}\n{epoch}\n{data}\n{resourcePath}";

            // Generate HMAC-SHA256 signature
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(accessKey));
            var signatureBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign));
            var signature = Convert.ToBase64String(signatureBytes);

            // Return the authorization header value
            return $"LMv1 {accessId}:{signature}:{epoch}";
        }

        /// <summary>
        /// Generates the Bearer authorization header value.
        /// </summary>
        /// <param name="bearerToken">The Bearer token.</param>
        /// <returns>The Bearer authorization header value.</returns>
        public static string GenerateBearerAuth(string bearerToken)
        {
            return $"Bearer {bearerToken}";
        }
    }

    #endregion
}
