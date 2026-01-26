using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.Geckoboard
{
    /// <summary>
    /// Configuration for the Geckoboard plugin.
    /// </summary>
    public sealed class GeckoboardPluginConfig
    {
        /// <summary>
        /// Gets or sets the Geckoboard API key for authentication.
        /// </summary>
        [JsonPropertyName("apiKey")]
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the default dataset ID for metrics.
        /// </summary>
        [JsonPropertyName("datasetId")]
        public string DatasetId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the Geckoboard API base URL.
        /// </summary>
        [JsonPropertyName("apiBaseUrl")]
        public string ApiBaseUrl { get; set; } = "https://api.geckoboard.com";

        /// <summary>
        /// Gets or sets the batch size for dataset updates.
        /// </summary>
        [JsonPropertyName("batchSize")]
        public int BatchSize { get; set; } = 500;

        /// <summary>
        /// Gets or sets the timeout for API requests in seconds.
        /// </summary>
        [JsonPropertyName("timeoutSeconds")]
        public int TimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Gets or sets whether to enable automatic retries on failures.
        /// </summary>
        [JsonPropertyName("enableRetries")]
        public bool EnableRetries { get; set; } = true;

        /// <summary>
        /// Gets or sets the maximum number of retry attempts.
        /// </summary>
        [JsonPropertyName("maxRetries")]
        public int MaxRetries { get; set; } = 3;

        /// <summary>
        /// Gets or sets whether to validate API configuration on startup.
        /// </summary>
        [JsonPropertyName("validateOnStartup")]
        public bool ValidateOnStartup { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to enable verbose logging.
        /// </summary>
        [JsonPropertyName("enableVerboseLogging")]
        public bool EnableVerboseLogging { get; set; } = false;

        /// <summary>
        /// Validates the configuration.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when configuration is invalid.</exception>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(ApiKey))
            {
                throw new InvalidOperationException("Geckoboard API key is required.");
            }

            if (string.IsNullOrWhiteSpace(DatasetId))
            {
                throw new InvalidOperationException("Default dataset ID is required.");
            }

            if (string.IsNullOrWhiteSpace(ApiBaseUrl))
            {
                throw new InvalidOperationException("API base URL is required.");
            }

            if (BatchSize <= 0)
            {
                throw new InvalidOperationException("Batch size must be positive.");
            }

            if (TimeoutSeconds <= 0)
            {
                throw new InvalidOperationException("Timeout must be positive.");
            }

            if (MaxRetries < 0)
            {
                throw new InvalidOperationException("Max retries cannot be negative.");
            }
        }
    }
}
