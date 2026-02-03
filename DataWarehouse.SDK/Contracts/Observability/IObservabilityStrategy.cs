namespace DataWarehouse.SDK.Contracts.Observability;

/// <summary>
/// Defines the contract for observability strategy implementations providing metrics, tracing, and logging capabilities.
/// </summary>
/// <remarks>
/// <para>
/// Observability strategies enable monitoring, debugging, and understanding system behavior through:
/// - Metrics: Quantitative measurements (counters, gauges, histograms)
/// - Tracing: Request flow tracking across distributed systems
/// - Logging: Structured event recording
/// </para>
/// <para>
/// Implementations should support industry-standard formats (OpenTelemetry, Prometheus, etc.).
/// </para>
/// </remarks>
public interface IObservabilityStrategy : IDisposable
{
    /// <summary>
    /// Gets the capabilities supported by this observability strategy.
    /// </summary>
    /// <value>
    /// An <see cref="ObservabilityCapabilities"/> instance describing supported features.
    /// </value>
    ObservabilityCapabilities Capabilities { get; }

    /// <summary>
    /// Records metrics data asynchronously.
    /// </summary>
    /// <param name="metrics">Collection of metrics to record.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="metrics"/> is null.</exception>
    /// <exception cref="NotSupportedException">Thrown when metrics are not supported (see <see cref="Capabilities"/>).</exception>
    /// <exception cref="InvalidOperationException">Thrown when the strategy is not initialized.</exception>
    Task MetricsAsync(IEnumerable<MetricValue> metrics, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records distributed tracing spans asynchronously.
    /// </summary>
    /// <param name="spans">Collection of trace spans to record.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="spans"/> is null.</exception>
    /// <exception cref="NotSupportedException">Thrown when tracing is not supported (see <see cref="Capabilities"/>).</exception>
    /// <exception cref="InvalidOperationException">Thrown when the strategy is not initialized.</exception>
    Task TracingAsync(IEnumerable<SpanContext> spans, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records structured log entries asynchronously.
    /// </summary>
    /// <param name="logEntries">Collection of log entries to record.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="logEntries"/> is null.</exception>
    /// <exception cref="NotSupportedException">Thrown when logging is not supported (see <see cref="Capabilities"/>).</exception>
    /// <exception cref="InvalidOperationException">Thrown when the strategy is not initialized.</exception>
    Task LoggingAsync(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a health check on the observability infrastructure.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>
    /// A task that resolves to a <see cref="HealthCheckResult"/> indicating the health status.
    /// </returns>
    /// <remarks>
    /// Health checks verify connectivity to backend systems (metric stores, trace collectors, log aggregators).
    /// </remarks>
    Task<HealthCheckResult> HealthCheckAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a structured log entry.
/// </summary>
/// <param name="Timestamp">When the log entry was created.</param>
/// <param name="Level">Severity level of the log entry.</param>
/// <param name="Message">Log message content.</param>
/// <param name="Properties">Additional structured properties.</param>
/// <param name="Exception">Associated exception, if any.</param>
public record LogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Message,
    IReadOnlyDictionary<string, object>? Properties = null,
    Exception? Exception = null);

/// <summary>
/// Defines log severity levels.
/// </summary>
public enum LogLevel
{
    /// <summary>Verbose debugging information.</summary>
    Trace = 0,

    /// <summary>Detailed debugging information.</summary>
    Debug = 1,

    /// <summary>Informational messages.</summary>
    Information = 2,

    /// <summary>Warning messages.</summary>
    Warning = 3,

    /// <summary>Error messages.</summary>
    Error = 4,

    /// <summary>Critical failures.</summary>
    Critical = 5,

    /// <summary>Logging is disabled.</summary>
    None = 6
}

/// <summary>
/// Represents the result of a health check operation.
/// </summary>
/// <param name="IsHealthy">Whether the system is healthy.</param>
/// <param name="Description">Human-readable description of the health status.</param>
/// <param name="Data">Additional diagnostic data.</param>
public record HealthCheckResult(
    bool IsHealthy,
    string Description,
    IReadOnlyDictionary<string, object>? Data = null);
