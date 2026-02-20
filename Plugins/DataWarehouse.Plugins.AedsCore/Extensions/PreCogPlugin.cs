using DataWarehouse.SDK;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Distribution;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.AedsCore.Extensions;

/// <summary>
/// Predicted content with confidence score.
/// </summary>
/// <param name="PayloadId">Predicted payload ID.</param>
/// <param name="Confidence">Confidence score (0.0-1.0).</param>
/// <param name="Reason">Prediction reason.</param>
public record PredictedContent(
    string PayloadId,
    double Confidence,
    string Reason
);

/// <summary>
/// PreCog Plugin: Predictive content prefetching.
/// Prefetches content before explicit manifest received using heuristics + AI predictions.
/// </summary>
/// <remarks>
/// Heuristic fallback: time-of-day patterns, historical frequency, user behavior analysis.
/// AI delegation: publishes to UniversalIntelligence (T90) for ML-based predictions.
/// </remarks>
public sealed class PreCogPlugin : DataManagementPluginBase
{
    private readonly BoundedDictionary<string, List<DateTimeOffset>> _downloadHistory = new BoundedDictionary<string, List<DateTimeOffset>>(1000);
    private readonly BoundedDictionary<string, int> _prefetchHits = new BoundedDictionary<string, int>(1000);
    private readonly BoundedDictionary<string, int> _prefetchMisses = new BoundedDictionary<string, int>(1000);

    /// <summary>
    /// Gets the plugin identifier.
    /// </summary>
    public override string Id => "aeds.precog";

    /// <summary>
    /// Gets the plugin name.
    /// </summary>
    public override string Name => "PreCogPlugin";

    /// <summary>
    /// Gets the plugin version.
    /// </summary>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override string DataManagementDomain => "PredictiveAnalytics";

    /// <summary>
    /// Gets the plugin category.
    /// </summary>
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <summary>
    /// Predicts content to prefetch using heuristics + AI.
    /// </summary>
    /// <param name="context">Prediction context (time, user ID, recent downloads, capabilities).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of predicted content sorted by confidence.</returns>
    public async Task<List<PredictedContent>> PredictContentAsync(
        Dictionary<string, object> context,
        CancellationToken ct = default)
    {
        if (context == null)
            throw new ArgumentNullException(nameof(context));

        var predictions = new List<PredictedContent>();

        // Heuristic predictions
        predictions.AddRange(await GetHeuristicPredictionsAsync(context, ct));

        // AI predictions (if available)
        var aiPredictions = await GetAIPredictionsAsync(context, ct);
        if (aiPredictions.Count > 0)
        {
            predictions.AddRange(aiPredictions);
        }

        // Sort by confidence descending
        return predictions.OrderByDescending(p => p.Confidence).Take(10).ToList();
    }

    /// <summary>
    /// Prefetches content in background.
    /// </summary>
    /// <param name="payloadId">Payload ID to prefetch.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task PrefetchAsync(string payloadId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(payloadId))
            throw new ArgumentException("Payload ID cannot be null or empty.", nameof(payloadId));

        var prefetchRequest = new PluginMessage
        {
            Type = "aeds.prefetch",
            SourcePluginId = Id,
            Payload = new Dictionary<string, object>
            {
                ["payloadId"] = payloadId,
                ["action"] = "passive",
                ["priority"] = 10,
                ["requestedAt"] = DateTimeOffset.UtcNow
            }
        };

        if (MessageBus != null)
        {
            await MessageBus.PublishAsync("aeds.prefetch-request", prefetchRequest, ct);
        }
    }

    /// <summary>
    /// Records download for pattern learning.
    /// </summary>
    /// <param name="payloadId">Payload ID that was downloaded.</param>
    /// <param name="timestamp">Download timestamp.</param>
    /// <param name="wasUseful">Whether the prefetch was useful (payload was actually used).</param>
    public void LearnFromDownload(string payloadId, DateTimeOffset timestamp, bool wasUseful)
    {
        if (string.IsNullOrEmpty(payloadId))
            throw new ArgumentException("Payload ID cannot be null or empty.", nameof(payloadId));

        var history = _downloadHistory.GetOrAdd(payloadId, _ => new List<DateTimeOffset>());
        history.Add(timestamp);

        if (wasUseful)
        {
            _prefetchHits.AddOrUpdate(payloadId, 1, (_, count) => count + 1);
        }
        else
        {
            _prefetchMisses.AddOrUpdate(payloadId, 1, (_, count) => count + 1);
        }

        // Send feedback to AI if applicable
        var feedback = new PluginMessage
        {
            Type = "intelligence.feedback",
            SourcePluginId = Id,
            Payload = new Dictionary<string, object>
            {
                ["payloadId"] = payloadId,
                ["timestamp"] = timestamp,
                ["wasUseful"] = wasUseful,
                ["accuracy"] = CalculateAccuracy(payloadId)
            }
        };

        if (MessageBus != null)
        {
            _ = MessageBus.PublishAsync("intelligence.feedback", feedback, CancellationToken.None);
        }
    }

    private async Task<List<PredictedContent>> GetHeuristicPredictionsAsync(
        Dictionary<string, object> context,
        CancellationToken ct)
    {
        var predictions = new List<PredictedContent>();
        var now = DateTimeOffset.UtcNow;

        // Time-of-day patterns (e.g., software updates at 2 AM)
        if (now.Hour >= 2 && now.Hour <= 4)
        {
            predictions.Add(new PredictedContent(
                "system-update-payload",
                0.7,
                "Time-of-day: typical update window"
            ));
        }

        // Historical frequency analysis
        foreach (var kvp in _downloadHistory)
        {
            var payloadId = kvp.Key;
            var downloads = kvp.Value;

            if (downloads.Count < 2)
                continue;

            // Calculate average interval between downloads
            var intervals = new List<TimeSpan>();
            for (int i = 1; i < downloads.Count; i++)
            {
                intervals.Add(downloads[i] - downloads[i - 1]);
            }

            var avgInterval = TimeSpan.FromTicks((long)intervals.Average(ts => ts.Ticks));
            var lastDownload = downloads.Last();
            var timeSinceLastDownload = now - lastDownload;

            // Predict if we're near the average interval
            if (timeSinceLastDownload >= avgInterval * 0.8 && timeSinceLastDownload <= avgInterval * 1.2)
            {
                var confidence = 0.6 * (1.0 - Math.Abs((timeSinceLastDownload - avgInterval).TotalHours / avgInterval.TotalHours));
                predictions.Add(new PredictedContent(
                    payloadId,
                    Math.Max(0.3, confidence),
                    $"Historical pattern: avg interval {avgInterval.TotalHours:F1}h"
                ));
            }
        }

        return await Task.FromResult(predictions);
    }

    private async Task<List<PredictedContent>> GetAIPredictionsAsync(
        Dictionary<string, object> context,
        CancellationToken ct)
    {
        var predictions = new List<PredictedContent>();

        var request = new PluginMessage
        {
            Type = "intelligence.predict",
            SourcePluginId = Id,
            Payload = new Dictionary<string, object>
            {
                ["context"] = context,
                ["downloadHistory"] = _downloadHistory.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray()),
                ["requestedAt"] = DateTimeOffset.UtcNow
            }
        };

        if (MessageBus != null)
        {
            await MessageBus.PublishAsync("intelligence.predict", request, ct);
            // AI response will be handled via subscription in production
        }

        return predictions;
    }

    private double CalculateAccuracy(string payloadId)
    {
        var hits = _prefetchHits.TryGetValue(payloadId, out var h) ? h : 0;
        var misses = _prefetchMisses.TryGetValue(payloadId, out var m) ? m : 0;
        var total = hits + misses;

        return total > 0 ? hits / (double)total : 0.0;
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task StopAsync()
    {
        _downloadHistory.Clear();
        _prefetchHits.Clear();
        _prefetchMisses.Clear();
        return Task.CompletedTask;
    }
}
