namespace DataWarehouse.Plugins.Tableau
{
    /// <summary>
    /// Configuration options for the Tableau BI plugin.
    /// </summary>
    public sealed class TableauConfiguration
    {
        /// <summary>
        /// Gets or sets the Tableau Server URL.
        /// </summary>
        public string TableauServerUrl { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the site ID (empty for default site).
        /// </summary>
        public string SiteId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the token name for authentication.
        /// </summary>
        public string TokenName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the token secret for authentication.
        /// </summary>
        public string TokenSecret { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the project ID for publishing extracts.
        /// </summary>
        public string ProjectId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the default extract name prefix.
        /// </summary>
        public string ExtractNamePrefix { get; set; } = "DataWarehouse";

        /// <summary>
        /// Gets or sets the maximum number of rows per extract batch.
        /// </summary>
        public int MaxRowsPerBatch { get; set; } = 10000;

        /// <summary>
        /// Gets or sets whether to enable automatic publishing.
        /// </summary>
        public bool EnableAutoPublish { get; set; } = false;

        /// <summary>
        /// Gets or sets the publish interval in minutes (for auto-publish).
        /// </summary>
        public int PublishIntervalMinutes { get; set; } = 60;

        /// <summary>
        /// Gets or sets the temporary directory for extract files.
        /// </summary>
        public string TempDirectory { get; set; } = Path.GetTempPath();

        /// <summary>
        /// Gets or sets whether to delete local extracts after publishing.
        /// </summary>
        public bool DeleteAfterPublish { get; set; } = true;

        /// <summary>
        /// Gets or sets the API version to use.
        /// </summary>
        public string ApiVersion { get; set; } = "3.20";

        /// <summary>
        /// Gets or sets the connection timeout in seconds.
        /// </summary>
        public int ConnectionTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Gets or sets whether to verify SSL certificates.
        /// </summary>
        public bool VerifySslCertificate { get; set; } = true;

        /// <summary>
        /// Gets or sets the maximum number of concurrent uploads.
        /// </summary>
        public int MaxConcurrentUploads { get; set; } = 3;

        /// <summary>
        /// Gets or sets whether to enable compression for REST API requests.
        /// </summary>
        public bool EnableCompression { get; set; } = true;

        /// <summary>
        /// Gets or sets the Web Data Connector URL (if used).
        /// </summary>
        public string? WebDataConnectorUrl { get; set; }

        /// <summary>
        /// Gets or sets custom tags for published datasources.
        /// </summary>
        public List<string> CustomTags { get; set; } = new();

        /// <summary>
        /// Gets or sets whether to enable incremental refresh.
        /// </summary>
        public bool EnableIncrementalRefresh { get; set; } = false;

        /// <summary>
        /// Validates the configuration.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when configuration is invalid.</exception>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(TableauServerUrl))
            {
                throw new InvalidOperationException("TableauServerUrl is required.");
            }

            if (!Uri.TryCreate(TableauServerUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException($"TableauServerUrl '{TableauServerUrl}' is not a valid HTTP/HTTPS URL.");
            }

            if (string.IsNullOrWhiteSpace(TokenName))
            {
                throw new InvalidOperationException("TokenName is required.");
            }

            if (string.IsNullOrWhiteSpace(TokenSecret))
            {
                throw new InvalidOperationException("TokenSecret is required.");
            }

            if (MaxRowsPerBatch <= 0)
            {
                throw new InvalidOperationException("MaxRowsPerBatch must be positive.");
            }

            if (PublishIntervalMinutes <= 0)
            {
                throw new InvalidOperationException("PublishIntervalMinutes must be positive.");
            }

            if (ConnectionTimeoutSeconds <= 0)
            {
                throw new InvalidOperationException("ConnectionTimeoutSeconds must be positive.");
            }

            if (MaxConcurrentUploads <= 0)
            {
                throw new InvalidOperationException("MaxConcurrentUploads must be positive.");
            }

            if (!Directory.Exists(TempDirectory))
            {
                try
                {
                    Directory.CreateDirectory(TempDirectory);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Failed to create temp directory '{TempDirectory}': {ex.Message}", ex);
                }
            }
        }
    }
}
