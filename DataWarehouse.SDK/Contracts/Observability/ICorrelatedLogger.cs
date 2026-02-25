using System;

namespace DataWarehouse.SDK.Contracts.Observability
{
    /// <summary>
    /// Contract for structured logging with mandatory correlation IDs (OBS-02).
    /// Provides an SDK-level logging abstraction that automatically attaches
    /// correlation IDs to all log entries for distributed tracing correlation.
    /// <para>
    /// This is an SDK abstraction -- implementations bridge to the underlying
    /// logging infrastructure (e.g., Microsoft.Extensions.Logging.ILogger).
    /// </para>
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Structured logging with correlation")]
    public interface ICorrelatedLogger
    {
        /// <summary>
        /// Gets the current correlation ID.
        /// Auto-generated if not explicitly set.
        /// </summary>
        string CorrelationId { get; }

        /// <summary>
        /// Creates a new correlated logger with the specified correlation ID.
        /// </summary>
        /// <param name="correlationId">The correlation ID to use.</param>
        /// <returns>A new correlated logger instance with the specified correlation ID.</returns>
        ICorrelatedLogger WithCorrelationId(string correlationId);

        /// <summary>
        /// Creates a new correlated logger with an additional structured property.
        /// </summary>
        /// <param name="key">The property key.</param>
        /// <param name="value">The property value.</param>
        /// <returns>A new correlated logger instance with the property attached.</returns>
        ICorrelatedLogger WithProperty(string key, object? value);

        /// <summary>
        /// Logs a trace-level message.
        /// </summary>
        /// <param name="message">The message template.</param>
        /// <param name="args">The message arguments.</param>
        void LogTrace(string message, params object[] args);

        /// <summary>
        /// Logs a debug-level message.
        /// </summary>
        /// <param name="message">The message template.</param>
        /// <param name="args">The message arguments.</param>
        void LogDebug(string message, params object[] args);

        /// <summary>
        /// Logs an information-level message.
        /// </summary>
        /// <param name="message">The message template.</param>
        /// <param name="args">The message arguments.</param>
        void LogInformation(string message, params object[] args);

        /// <summary>
        /// Logs a warning-level message.
        /// </summary>
        /// <param name="message">The message template.</param>
        /// <param name="args">The message arguments.</param>
        void LogWarning(string message, params object[] args);

        /// <summary>
        /// Logs an error-level message with an optional exception.
        /// </summary>
        /// <param name="exception">The exception to log, if any.</param>
        /// <param name="message">The message template.</param>
        /// <param name="args">The message arguments.</param>
        void LogError(Exception? exception, string message, params object[] args);

        /// <summary>
        /// Logs a critical-level message with an optional exception.
        /// </summary>
        /// <param name="exception">The exception to log, if any.</param>
        /// <param name="message">The message template.</param>
        /// <param name="args">The message arguments.</param>
        void LogCritical(Exception? exception, string message, params object[] args);

        /// <summary>
        /// Begins a logging scope with the current correlation ID.
        /// All log entries within the scope inherit the correlation ID.
        /// </summary>
        /// <param name="scopeName">The name of the scope.</param>
        /// <returns>A disposable that ends the scope when disposed.</returns>
        IDisposable BeginScope(string scopeName);
    }
}
