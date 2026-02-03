namespace DataWarehouse.SDK.Contracts.Observability;

/// <summary>
/// Represents a metric value with associated metadata.
/// </summary>
/// <param name="Name">Metric name (e.g., "http_requests_total").</param>
/// <param name="Value">Numeric value of the metric.</param>
/// <param name="Type">Type of metric (Counter, Gauge, Histogram, Summary).</param>
/// <param name="Labels">Collection of labels for metric dimensions.</param>
/// <param name="Timestamp">When the metric was recorded.</param>
/// <param name="Unit">Unit of measurement (e.g., "bytes", "seconds", "requests").</param>
/// <remarks>
/// <para>
/// Metrics follow the OpenTelemetry and Prometheus data model conventions.
/// Metric names should use snake_case and include a unit suffix where appropriate.
/// </para>
/// <para>
/// Example metric names:
/// <list type="bullet">
/// <item><description>http_requests_total (counter)</description></item>
/// <item><description>memory_usage_bytes (gauge)</description></item>
/// <item><description>request_duration_seconds (histogram)</description></item>
/// <item><description>database_query_time_ms (summary)</description></item>
/// </list>
/// </para>
/// </remarks>
public record MetricValue(
    string Name,
    double Value,
    MetricType Type,
    IReadOnlyList<MetricLabel> Labels,
    DateTimeOffset Timestamp,
    string? Unit = null)
{
    /// <summary>
    /// Creates a counter metric (monotonically increasing value).
    /// </summary>
    /// <param name="name">Metric name.</param>
    /// <param name="value">Counter value.</param>
    /// <param name="labels">Optional labels.</param>
    /// <param name="unit">Optional unit of measurement.</param>
    /// <returns>A counter metric value.</returns>
    public static MetricValue Counter(string name, double value, IReadOnlyList<MetricLabel>? labels = null, string? unit = null) =>
        new(name, value, MetricType.Counter, labels ?? Array.Empty<MetricLabel>(), DateTimeOffset.UtcNow, unit);

    /// <summary>
    /// Creates a gauge metric (value that can go up or down).
    /// </summary>
    /// <param name="name">Metric name.</param>
    /// <param name="value">Gauge value.</param>
    /// <param name="labels">Optional labels.</param>
    /// <param name="unit">Optional unit of measurement.</param>
    /// <returns>A gauge metric value.</returns>
    public static MetricValue Gauge(string name, double value, IReadOnlyList<MetricLabel>? labels = null, string? unit = null) =>
        new(name, value, MetricType.Gauge, labels ?? Array.Empty<MetricLabel>(), DateTimeOffset.UtcNow, unit);

    /// <summary>
    /// Creates a histogram metric (distribution of values).
    /// </summary>
    /// <param name="name">Metric name.</param>
    /// <param name="value">Observed value.</param>
    /// <param name="labels">Optional labels.</param>
    /// <param name="unit">Optional unit of measurement.</param>
    /// <returns>A histogram metric value.</returns>
    public static MetricValue Histogram(string name, double value, IReadOnlyList<MetricLabel>? labels = null, string? unit = null) =>
        new(name, value, MetricType.Histogram, labels ?? Array.Empty<MetricLabel>(), DateTimeOffset.UtcNow, unit);

    /// <summary>
    /// Creates a summary metric (statistical summary of observed values).
    /// </summary>
    /// <param name="name">Metric name.</param>
    /// <param name="value">Observed value.</param>
    /// <param name="labels">Optional labels.</param>
    /// <param name="unit">Optional unit of measurement.</param>
    /// <returns>A summary metric value.</returns>
    public static MetricValue Summary(string name, double value, IReadOnlyList<MetricLabel>? labels = null, string? unit = null) =>
        new(name, value, MetricType.Summary, labels ?? Array.Empty<MetricLabel>(), DateTimeOffset.UtcNow, unit);
}

/// <summary>
/// Defines the type of metric being recorded.
/// </summary>
public enum MetricType
{
    /// <summary>
    /// Counter: A monotonically increasing value (e.g., total requests, errors).
    /// Counters can only increase or be reset to zero.
    /// </summary>
    Counter = 0,

    /// <summary>
    /// Gauge: A value that can arbitrarily go up or down (e.g., memory usage, temperature, active connections).
    /// </summary>
    Gauge = 1,

    /// <summary>
    /// Histogram: Samples observations and counts them in configurable buckets (e.g., request durations, response sizes).
    /// Useful for calculating percentiles and distributions.
    /// </summary>
    Histogram = 2,

    /// <summary>
    /// Summary: Similar to histogram but calculates quantiles over a sliding time window on the client side.
    /// Provides sum and count of observations plus configurable quantiles (e.g., 0.5, 0.9, 0.99).
    /// </summary>
    Summary = 3
}

/// <summary>
/// Represents a label (dimension) for a metric, enabling filtering and aggregation.
/// </summary>
/// <param name="Name">Label name (e.g., "method", "status_code", "endpoint").</param>
/// <param name="Value">Label value (e.g., "GET", "200", "/api/users").</param>
/// <remarks>
/// <para>
/// Labels enable multi-dimensional metrics. For example, an HTTP request counter might have:
/// <list type="bullet">
/// <item><description>method: GET, POST, PUT, DELETE</description></item>
/// <item><description>status_code: 200, 404, 500</description></item>
/// <item><description>endpoint: /api/users, /api/products</description></item>
/// </list>
/// </para>
/// <para>
/// Label cardinality should be carefully managed - high cardinality labels (e.g., user IDs, request IDs)
/// can cause performance and storage issues in metric systems.
/// </para>
/// </remarks>
public record MetricLabel(string Name, string Value)
{
    /// <summary>
    /// Creates a collection of metric labels from key-value pairs.
    /// </summary>
    /// <param name="labels">Dictionary of label names and values.</param>
    /// <returns>A read-only list of metric labels.</returns>
    public static IReadOnlyList<MetricLabel> FromDictionary(IReadOnlyDictionary<string, string> labels) =>
        labels.Select(kvp => new MetricLabel(kvp.Key, kvp.Value)).ToList();

    /// <summary>
    /// Creates a collection of metric labels from params.
    /// </summary>
    /// <param name="labels">Array of tuples containing label name-value pairs.</param>
    /// <returns>A read-only list of metric labels.</returns>
    public static IReadOnlyList<MetricLabel> Create(params (string Name, string Value)[] labels) =>
        labels.Select(l => new MetricLabel(l.Name, l.Value)).ToList();
}
