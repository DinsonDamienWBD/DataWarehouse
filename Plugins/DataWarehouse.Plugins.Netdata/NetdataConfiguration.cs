namespace DataWarehouse.Plugins.Netdata
{
    /// <summary>
    /// Configuration options for the Netdata monitoring plugin.
    /// </summary>
    public sealed class NetdataConfiguration
    {
        /// <summary>
        /// Gets or sets the Netdata base URL for HTTP API.
        /// </summary>
        /// <remarks>WARNING: Default value is for development only. Configure for production.</remarks>
        public string NetdataUrl { get; set; } = "http://localhost:19999";

        /// <summary>
        /// Gets or sets the StatsD host for metric submission.
        /// </summary>
        /// <remarks>WARNING: Default value is for development only. Configure for production.</remarks>
        public string StatsdHost { get; set; } = "localhost";

        /// <summary>
        /// Gets or sets the StatsD port for metric submission.
        /// </summary>
        public int StatsdPort { get; set; } = 8125;

        /// <summary>
        /// Gets or sets the metric prefix for all metrics.
        /// </summary>
        public string? MetricPrefix { get; set; }

        /// <summary>
        /// Gets or sets whether to enable HTTP API integration for custom charts.
        /// </summary>
        public bool EnableHttpApi { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to enable StatsD protocol for metric submission.
        /// </summary>
        public bool EnableStatsd { get; set; } = true;

        /// <summary>
        /// Gets or sets the maximum UDP packet size for StatsD (bytes).
        /// </summary>
        public int MaxUdpPacketSize { get; set; } = 1400;

        /// <summary>
        /// Gets or sets whether to batch StatsD metrics.
        /// </summary>
        public bool BatchStatsdMetrics { get; set; } = true;

        /// <summary>
        /// Gets or sets the batch flush interval for StatsD metrics (milliseconds).
        /// </summary>
        public int BatchFlushIntervalMs { get; set; } = 1000;

        /// <summary>
        /// Gets or sets whether to include process metrics.
        /// </summary>
        public bool IncludeProcessMetrics { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to include runtime metrics.
        /// </summary>
        public bool IncludeRuntimeMetrics { get; set; } = true;

        /// <summary>
        /// Gets or sets the chart type for custom charts (line, area, stacked).
        /// </summary>
        public string DefaultChartType { get; set; } = "line";

        /// <summary>
        /// Gets or sets the update interval for charts (seconds).
        /// </summary>
        public int ChartUpdateIntervalSeconds { get; set; } = 1;

        /// <summary>
        /// Gets or sets the chart family/category for all custom charts.
        /// </summary>
        public string ChartFamily { get; set; } = "datawarehouse";
    }
}
