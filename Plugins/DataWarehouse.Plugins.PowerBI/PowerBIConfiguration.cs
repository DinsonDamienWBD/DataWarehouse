namespace DataWarehouse.Plugins.PowerBI
{
    /// <summary>
    /// Configuration options for the Power BI plugin.
    /// </summary>
    public sealed class PowerBIConfiguration
    {
        /// <summary>
        /// Gets or sets the Power BI Workspace ID (Group ID).
        /// </summary>
        public string WorkspaceId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the Power BI Dataset ID.
        /// </summary>
        public string DatasetId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the Azure AD Client ID (Application ID).
        /// </summary>
        public string ClientId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the Azure AD Client Secret.
        /// </summary>
        public string ClientSecret { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the Azure AD Tenant ID.
        /// </summary>
        public string TenantId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the Power BI API base URL.
        /// </summary>
        public string ApiBaseUrl { get; set; } = "https://api.powerbi.com/v1.0/myorg";

        /// <summary>
        /// Gets or sets the Azure AD authority URL.
        /// </summary>
        public string AuthorityUrl { get; set; } = "https://login.microsoftonline.com";

        /// <summary>
        /// Gets or sets the Power BI resource URL for authentication.
        /// </summary>
        public string ResourceUrl { get; set; } = "https://analysis.windows.net/powerbi/api";

        /// <summary>
        /// Gets or sets whether to use streaming datasets (true) or push datasets (false).
        /// </summary>
        public bool UseStreamingDataset { get; set; } = false;

        /// <summary>
        /// Gets or sets the batch size for push operations.
        /// </summary>
        public int BatchSize { get; set; } = 100;

        /// <summary>
        /// Gets or sets the timeout in seconds for API requests.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Gets or sets the maximum number of retries for failed requests.
        /// </summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>
        /// Gets or sets the retry delay in milliseconds.
        /// </summary>
        public int RetryDelayMs { get; set; } = 1000;

        /// <summary>
        /// Gets or sets whether to validate configuration on startup.
        /// </summary>
        public bool ValidateOnStartup { get; set; } = true;

        /// <summary>
        /// Gets or sets the table name for push operations.
        /// </summary>
        public string TableName { get; set; } = "RealTimeData";

        /// <summary>
        /// Gets or sets whether to enable automatic token refresh.
        /// </summary>
        public bool EnableAutoTokenRefresh { get; set; } = true;

        /// <summary>
        /// Validates the configuration.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when configuration is invalid.</exception>
        public void Validate()
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(WorkspaceId))
                errors.Add("WorkspaceId is required");

            if (string.IsNullOrWhiteSpace(DatasetId))
                errors.Add("DatasetId is required");

            if (string.IsNullOrWhiteSpace(ClientId))
                errors.Add("ClientId is required");

            if (string.IsNullOrWhiteSpace(ClientSecret))
                errors.Add("ClientSecret is required");

            if (string.IsNullOrWhiteSpace(TenantId))
                errors.Add("TenantId is required");

            if (BatchSize <= 0 || BatchSize > 10000)
                errors.Add("BatchSize must be between 1 and 10000");

            if (TimeoutSeconds <= 0)
                errors.Add("TimeoutSeconds must be positive");

            if (MaxRetries < 0)
                errors.Add("MaxRetries must be non-negative");

            if (errors.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Power BI configuration is invalid:\n- {string.Join("\n- ", errors)}");
            }
        }
    }
}
