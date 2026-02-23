using System;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Policy;

/// <summary>
/// Contract for plugins that participate in the AI observation/recommendation pipeline.
/// Implemented by IntelligenceAwarePluginBase to enable AI policy tuning.
/// UltimateIntelligencePlugin does NOT implement this (SDKF-12: prevents AI-observing-AI loops).
/// </summary>
public interface IAiHook
{
    /// <summary>
    /// Called when the AI observation pipeline has a recommendation for this plugin.
    /// </summary>
    /// <param name="recommendation">The policy recommendation from the AI pipeline.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    Task OnRecommendationReceivedAsync(PolicyRecommendation recommendation, CancellationToken ct = default);

    /// <summary>
    /// Get the observation emitter for this plugin. Used to push metrics/observations to the AI pipeline.
    /// </summary>
    ObservationEmitter Observations { get; }

    /// <summary>
    /// Get the recommendation receiver for this plugin. Used to subscribe to AI recommendations.
    /// </summary>
    RecommendationReceiver Recommendations { get; }
}

/// <summary>
/// Emits observations (metrics, events, anomalies) from a plugin to the AI observation pipeline.
/// Observations are lock-free and async — zero hot-path impact (AIPI-01 contract placeholder).
/// </summary>
public sealed class ObservationEmitter
{
    private readonly string _pluginId;
    private readonly IMessageBus? _messageBus;

    /// <summary>
    /// Creates a new observation emitter for the specified plugin.
    /// </summary>
    /// <param name="pluginId">The plugin ID that owns this emitter.</param>
    /// <param name="messageBus">The message bus for publishing observations, or null if unavailable.</param>
    public ObservationEmitter(string pluginId, IMessageBus? messageBus)
    {
        _pluginId = pluginId;
        _messageBus = messageBus;
    }

    /// <summary>
    /// Emit a metric observation (e.g., compression ratio, encryption latency).
    /// </summary>
    /// <param name="metricName">Name of the metric being observed.</param>
    /// <param name="value">The metric value.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    public Task EmitMetricAsync(string metricName, double value, CancellationToken ct = default)
    {
        // Phase 77 (AI Policy Intelligence) will implement the full pipeline.
        // For now, this is a no-op that compiles clean — the contract is what matters in Phase 68.
        return Task.CompletedTask;
    }

    /// <summary>
    /// Emit an anomaly observation (e.g., unexpected pattern, performance degradation).
    /// </summary>
    /// <param name="anomalyType">Type of anomaly detected.</param>
    /// <param name="description">Human-readable description of the anomaly.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    public Task EmitAnomalyAsync(string anomalyType, string description, CancellationToken ct = default)
    {
        // Phase 77 (AI Policy Intelligence) will implement the full pipeline.
        return Task.CompletedTask;
    }
}

/// <summary>
/// Receives AI-generated policy recommendations for a plugin.
/// </summary>
public sealed class RecommendationReceiver
{
    private readonly string _pluginId;

    /// <summary>
    /// Creates a new recommendation receiver for the specified plugin.
    /// </summary>
    /// <param name="pluginId">The plugin ID that owns this receiver.</param>
    public RecommendationReceiver(string pluginId)
    {
        _pluginId = pluginId;
    }

    /// <summary>
    /// Subscribe to recommendations. Returns a disposable subscription handle.
    /// </summary>
    /// <param name="handler">The handler to invoke when a recommendation is received.</param>
    /// <returns>A disposable subscription handle.</returns>
    public IDisposable Subscribe(Func<PolicyRecommendation, CancellationToken, Task> handler)
    {
        // Phase 77 will implement the full subscription pipeline.
        return new NoOpDisposable();
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }
}

/// <summary>
/// An AI-generated recommendation to adjust a feature policy.
/// </summary>
/// <param name="FeatureId">The feature ID this recommendation targets.</param>
/// <param name="Rationale">Human-readable rationale for the recommendation.</param>
/// <param name="SuggestedPolicy">The suggested policy to apply.</param>
/// <param name="RequiredAutonomy">The minimum AI autonomy level required to auto-apply this recommendation.</param>
/// <param name="ConfidenceScore">Confidence score (0.0-1.0) for this recommendation.</param>
/// <param name="GeneratedAt">When this recommendation was generated.</param>
public sealed record PolicyRecommendation(
    string FeatureId,
    string Rationale,
    FeaturePolicy SuggestedPolicy,
    AiAutonomyLevel RequiredAutonomy,
    double ConfidenceScore,
    DateTimeOffset GeneratedAt
);
