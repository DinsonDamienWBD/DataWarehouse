namespace DataWarehouse.Plugins.Splunk
{
    /// <summary>
    /// Configuration options for the Splunk HTTP Event Collector (HEC) plugin.
    /// </summary>
    public sealed class SplunkConfiguration
    {
        /// <summary>
        /// Gets or sets the Splunk HEC endpoint URL (e.g., https://splunk.example.com:8088).
        /// </summary>
        public string HecUrl { get; set; } = "https://localhost:8088";

        /// <summary>
        /// Gets or sets the HEC authentication token.
        /// </summary>
        public string HecToken { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the Splunk index to send events to.
        /// </summary>
        public string? Index { get; set; }

        /// <summary>
        /// Gets or sets the source field for events.
        /// </summary>
        public string Source { get; set; } = "datawarehouse";

        /// <summary>
        /// Gets or sets the source type for events.
        /// </summary>
        public string SourceType { get; set; } = "_json";

        /// <summary>
        /// Gets or sets the host field for events (defaults to machine name).
        /// </summary>
        public string? Host { get; set; }

        /// <summary>
        /// Gets or sets whether to validate SSL certificates (default: true).
        /// </summary>
        public bool ValidateCertificates { get; set; } = true;

        /// <summary>
        /// Gets or sets the maximum batch size for event submissions.
        /// </summary>
        public int MaxBatchSize { get; set; } = 100;

        /// <summary>
        /// Gets or sets the batch flush interval in milliseconds.
        /// </summary>
        public int FlushIntervalMs { get; set; } = 5000;

        /// <summary>
        /// Gets or sets the maximum number of retries for failed submissions.
        /// </summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>
        /// Gets or sets the HTTP client timeout in seconds.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Gets or sets whether to include default fields (timestamp, host, source, sourcetype, index).
        /// </summary>
        public bool IncludeDefaultFields { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to enable automatic metric extraction from events.
        /// </summary>
        public bool EnableMetrics { get; set; } = true;

        /// <summary>
        /// Gets or sets custom fields to add to all events.
        /// </summary>
        public Dictionary<string, object>? CustomFields { get; set; }
    }
}
