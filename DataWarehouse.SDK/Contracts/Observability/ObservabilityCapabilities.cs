namespace DataWarehouse.SDK.Contracts.Observability;

/// <summary>
/// Describes the observability capabilities supported by an observability strategy implementation.
/// </summary>
/// <param name="SupportsMetrics">Whether the strategy supports metrics collection.</param>
/// <param name="SupportsTracing">Whether the strategy supports distributed tracing.</param>
/// <param name="SupportsLogging">Whether the strategy supports structured logging.</param>
/// <param name="SupportsDistributedTracing">Whether the strategy supports cross-service distributed tracing with context propagation.</param>
/// <param name="SupportsAlerting">Whether the strategy supports alerting based on observability data.</param>
/// <param name="SupportedExporters">List of supported exporter formats (e.g., "OpenTelemetry", "Prometheus", "Jaeger", "Zipkin").</param>
/// <remarks>
/// <para>
/// This record provides discovery capabilities for consumers to determine what observability
/// features are available before attempting to use them.
/// </para>
/// <para>
/// Common exporter formats include:
/// <list type="bullet">
/// <item><term>OpenTelemetry</term><description>OTLP protocol for metrics, traces, and logs</description></item>
/// <item><term>Prometheus</term><description>Pull-based metrics collection</description></item>
/// <item><term>Jaeger</term><description>Distributed tracing visualization</description></item>
/// <item><term>Zipkin</term><description>Distributed tracing system</description></item>
/// <item><term>StatsD</term><description>Metrics aggregation daemon</description></item>
/// <item><term>Elasticsearch</term><description>Search and analytics for logs</description></item>
/// </list>
/// </para>
/// </remarks>
public record ObservabilityCapabilities(
    bool SupportsMetrics,
    bool SupportsTracing,
    bool SupportsLogging,
    bool SupportsDistributedTracing,
    bool SupportsAlerting,
    IReadOnlyList<string> SupportedExporters)
{
    /// <summary>
    /// Gets a value indicating whether this strategy supports any observability features.
    /// </summary>
    public bool HasAnyCapability => SupportsMetrics || SupportsTracing || SupportsLogging;

    /// <summary>
    /// Gets a value indicating whether this strategy supports all core observability features.
    /// </summary>
    public bool HasFullObservability => SupportsMetrics && SupportsTracing && SupportsLogging;

    /// <summary>
    /// Creates a minimal observability capabilities instance (no features supported).
    /// </summary>
    /// <returns>A capabilities instance with all features disabled.</returns>
    public static ObservabilityCapabilities None() => new(
        SupportsMetrics: false,
        SupportsTracing: false,
        SupportsLogging: false,
        SupportsDistributedTracing: false,
        SupportsAlerting: false,
        SupportedExporters: Array.Empty<string>());

    /// <summary>
    /// Creates a full observability capabilities instance (all features supported).
    /// </summary>
    /// <param name="exporters">Supported exporter formats.</param>
    /// <returns>A capabilities instance with all features enabled.</returns>
    public static ObservabilityCapabilities Full(params string[] exporters) => new(
        SupportsMetrics: true,
        SupportsTracing: true,
        SupportsLogging: true,
        SupportsDistributedTracing: true,
        SupportsAlerting: true,
        SupportedExporters: exporters);

    /// <summary>
    /// Checks if a specific exporter format is supported.
    /// </summary>
    /// <param name="exporterName">Name of the exporter to check (case-insensitive).</param>
    /// <returns>True if the exporter is supported; otherwise, false.</returns>
    public bool SupportsExporter(string exporterName) =>
        SupportedExporters.Any(e => e.Equals(exporterName, StringComparison.OrdinalIgnoreCase));
}
