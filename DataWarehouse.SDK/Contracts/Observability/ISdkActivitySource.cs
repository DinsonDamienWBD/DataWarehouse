using System;
using System.Diagnostics;

namespace DataWarehouse.SDK.Contracts.Observability
{
    /// <summary>
    /// Contract for SDK-level distributed tracing via System.Diagnostics.ActivitySource (OBS-01).
    /// Bridges the SDK to the standard .NET distributed tracing infrastructure,
    /// enabling integration with OpenTelemetry and other .NET tracing tools.
    /// <para>
    /// This complements the existing <see cref="IObservabilityStrategy"/> -- it does not replace it.
    /// ISdkActivitySource provides standard .NET ActivitySource integration while
    /// IObservabilityStrategy provides the plugin-level observability contract.
    /// </para>
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: ActivitySource integration for distributed tracing")]
    public interface ISdkActivitySource
    {
        /// <summary>
        /// Gets the name of the activity source (e.g., "DataWarehouse.SDK").
        /// </summary>
        string SourceName { get; }

        /// <summary>
        /// Gets the version of the activity source, if specified.
        /// </summary>
        string? SourceVersion { get; }

        /// <summary>
        /// Starts a new activity (span) with the specified name and kind.
        /// Returns null if no listeners are registered for this activity source.
        /// </summary>
        /// <param name="name">The name of the activity (e.g., "ProcessMessage", "QueryData").</param>
        /// <param name="kind">The kind of activity (Internal, Server, Client, Producer, Consumer).</param>
        /// <returns>The started activity, or null if no listeners are active.</returns>
        Activity? StartActivity(string name, ActivityKind kind = ActivityKind.Internal);

        /// <summary>
        /// Starts a new activity with an explicit parent context.
        /// Used for distributed tracing across process/service boundaries.
        /// </summary>
        /// <param name="name">The name of the activity.</param>
        /// <param name="kind">The kind of activity.</param>
        /// <param name="parentContext">The parent activity context from a remote source.</param>
        /// <returns>The started activity, or null if no listeners are active.</returns>
        Activity? StartActivity(string name, ActivityKind kind, ActivityContext parentContext);

        /// <summary>
        /// Records an exception on the specified activity.
        /// Adds the exception as an activity event with standard attributes.
        /// </summary>
        /// <param name="activity">The activity to record the exception on.</param>
        /// <param name="exception">The exception to record.</param>
        void RecordException(Activity activity, Exception exception);

        /// <summary>
        /// Sets a tag (attribute) on the specified activity.
        /// </summary>
        /// <param name="activity">The activity to set the tag on.</param>
        /// <param name="key">The tag key.</param>
        /// <param name="value">The tag value.</param>
        void SetTag(Activity activity, string key, object? value);
    }
}
