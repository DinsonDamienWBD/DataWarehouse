using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.Plugins.UltimateAccessControl.Features
{
    /// <summary>
    /// ML-based anomaly detection for access patterns.
    /// Delegates ML workload to Intelligence plugin via message bus with statistical fallback.
    /// </summary>
    /// <remarks>
    /// <b>DEPENDENCY:</b> Universal Intelligence plugin (T90) for ML-based anomaly detection.
    /// <b>MESSAGE TOPIC:</b> intelligence.analyze
    /// <b>FALLBACK:</b> Z-score statistical anomaly detection when Intelligence unavailable.
    /// </remarks>
    public sealed class MlAnomalyDetection
    {
        private readonly ILogger _logger;
        private readonly IMessageBus? _messageBus;
        private readonly ConcurrentDictionary<string, AccessPatternProfile> _profiles = new();
        private readonly ConcurrentQueue<DetectedAnomaly> _recentAnomalies = new();
        private readonly double _zScoreThreshold = 3.0;
        private readonly int _maxAnomalyHistorySize = 1000;

        /// <summary>
        /// Initializes a new instance of the <see cref="MlAnomalyDetection"/> class.
        /// </summary>
        /// <param name="messageBus">Message bus for Intelligence plugin communication (optional).</param>
        /// <param name="logger">Logger for diagnostic output (optional).</param>
        public MlAnomalyDetection(IMessageBus? messageBus = null, ILogger? logger = null)
        {
            _messageBus = messageBus;
            _logger = logger ?? NullLogger.Instance;
        }

        /// <summary>
        /// Analyzes access pattern for anomalies using ML (via Intelligence plugin) or statistical fallback.
        /// </summary>
        /// <param name="subjectId">Subject identifier (user/entity ID).</param>
        /// <param name="accessPattern">Current access pattern to analyze.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Anomaly detection result with score and details.</returns>
        public async Task<AnomalyDetectionResult> DetectAnomalyAsync(
            string subjectId,
            AccessPattern accessPattern,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(subjectId))
                throw new ArgumentException("Subject ID cannot be null or empty", nameof(subjectId));

            if (accessPattern == null)
                throw new ArgumentNullException(nameof(accessPattern));

            // Get or create profile
            var profile = _profiles.GetOrAdd(subjectId, _ => new AccessPatternProfile
            {
                SubjectId = subjectId,
                CreatedAt = DateTime.UtcNow
            });

            AnomalyDetectionResult result;

            // Try ML-based analysis via Intelligence plugin
            if (_messageBus != null)
            {
                try
                {
                    result = await TryMlAnalysisAsync(subjectId, accessPattern, profile, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ML analysis failed for subject {SubjectId}, using statistical fallback", subjectId);
                    result = await StatisticalAnomalyDetectionAsync(accessPattern, profile, cancellationToken);
                    result.AnalysisMethod = "Statistical-Fallback";
                }
            }
            else
            {
                // No message bus - use statistical fallback
                result = await StatisticalAnomalyDetectionAsync(accessPattern, profile, cancellationToken);
                result.AnalysisMethod = "Statistical";
            }

            // Update profile with current pattern
            UpdateProfile(profile, accessPattern);

            // Record anomaly if detected
            if (result.IsAnomaly)
            {
                var anomaly = new DetectedAnomaly
                {
                    Id = Guid.NewGuid().ToString("N"),
                    SubjectId = subjectId,
                    DetectedAt = DateTime.UtcNow,
                    AnomalyScore = result.AnomalyScore,
                    AnomalyTypes = result.AnomalyTypes,
                    AnalysisMethod = result.AnalysisMethod
                };

                _recentAnomalies.Enqueue(anomaly);

                // Prune old anomalies
                while (_recentAnomalies.Count > _maxAnomalyHistorySize)
                {
                    _recentAnomalies.TryDequeue(out _);
                }
            }

            return result;
        }

        /// <summary>
        /// Attempts ML-based anomaly detection via Intelligence plugin.
        /// </summary>
        private async Task<AnomalyDetectionResult> TryMlAnalysisAsync(
            string subjectId,
            AccessPattern pattern,
            AccessPatternProfile profile,
            CancellationToken cancellationToken)
        {
            if (_messageBus == null)
                throw new InvalidOperationException("Message bus is not available");

            // Build request for Intelligence plugin
            var request = new PluginMessage
            {
                Type = "analysis.request",
                Source = "UltimateAccessControl",
                Payload = new Dictionary<string, object>
                {
                    ["DataType"] = "access-pattern",
                    ["AnalysisType"] = "anomaly-detection",
                    ["SubjectId"] = subjectId,
                    ["CurrentPattern"] = new Dictionary<string, object>
                    {
                        ["Timestamp"] = pattern.Timestamp,
                        ["AccessCount"] = pattern.AccessCount,
                        ["Hour"] = pattern.Hour,
                        ["DayOfWeek"] = pattern.DayOfWeek.ToString(),
                        ["Location"] = pattern.Location ?? "unknown",
                        ["Action"] = pattern.Action,
                        ["ResourceType"] = pattern.ResourceType
                    },
                    ["HistoricalProfile"] = new Dictionary<string, object>
                    {
                        ["TotalAccesses"] = profile.TotalAccesses,
                        ["AverageAccessesPerDay"] = profile.AccessCountHistory.Any()
                            ? profile.AccessCountHistory.Average()
                            : 0,
                        ["CommonHours"] = profile.HourHistory.GroupBy(h => h)
                            .OrderByDescending(g => g.Count())
                            .Take(3)
                            .Select(g => g.Key)
                            .ToArray(),
                        ["CommonLocations"] = profile.LocationHistory.GroupBy(l => l)
                            .OrderByDescending(g => g.Count())
                            .Take(5)
                            .Select(g => g.Key)
                            .ToArray()
                    }
                }
            };

            // Send request to Intelligence plugin
            var response = await _messageBus.SendAsync(
                "intelligence.analyze",
                request,
                TimeSpan.FromSeconds(30),
                cancellationToken);

            if (!response.Success)
            {
                _logger.LogWarning("Intelligence plugin analysis failed: {Error}", response.ErrorMessage);
                // Fall back to statistical detection
                return await StatisticalAnomalyDetectionAsync(pattern, profile, cancellationToken);
            }

            // Parse response
            var payload = response.Payload as Dictionary<string, object>;
            if (payload == null)
            {
                _logger.LogWarning("Intelligence plugin returned invalid payload format");
                return await StatisticalAnomalyDetectionAsync(pattern, profile, cancellationToken);
            }

            var isAnomaly = payload.TryGetValue("IsAnomaly", out var anomalyObj) && anomalyObj is bool a && a;
            var score = payload.TryGetValue("AnomalyScore", out var scoreObj) && scoreObj is double s ? s : 0.0;
            var types = payload.TryGetValue("AnomalyTypes", out var typesObj) && typesObj is string[] t ? t : Array.Empty<string>();
            var confidence = payload.TryGetValue("Confidence", out var confObj) && confObj is double c ? c : 0.0;

            return new AnomalyDetectionResult
            {
                IsAnomaly = isAnomaly,
                AnomalyScore = score,
                AnomalyTypes = types,
                Confidence = confidence,
                AnalysisMethod = "ML-Intelligence",
                Details = $"ML analysis detected {types.Length} anomaly types with {confidence:P0} confidence"
            };
        }

        /// <summary>
        /// Statistical Z-score based anomaly detection fallback.
        /// </summary>
        private Task<AnomalyDetectionResult> StatisticalAnomalyDetectionAsync(
            AccessPattern pattern,
            AccessPatternProfile profile,
            CancellationToken cancellationToken)
        {
            var anomalyTypes = new List<string>();
            var score = 0.0;

            // Time-based anomaly (hour of day)
            if (profile.HourHistory.Count >= 10)
            {
                var hourZScore = CalculateZScore(pattern.Hour, profile.HourHistory);
                if (Math.Abs(hourZScore) > _zScoreThreshold)
                {
                    anomalyTypes.Add("unusual-access-time");
                    score += 30.0;
                }
            }

            // Location anomaly
            if (!string.IsNullOrEmpty(pattern.Location) && profile.LocationHistory.Count >= 5)
            {
                if (!profile.LocationHistory.Contains(pattern.Location))
                {
                    anomalyTypes.Add("new-location");
                    score += 35.0;
                }
            }

            // Access frequency anomaly
            if (profile.AccessCountHistory.Count >= 10)
            {
                var countZScore = CalculateZScore(pattern.AccessCount, profile.AccessCountHistory);
                if (countZScore > _zScoreThreshold)
                {
                    anomalyTypes.Add("high-frequency");
                    score += 40.0;
                }
            }

            // Day-of-week anomaly
            if (profile.DayOfWeekHistory.Count >= 7)
            {
                var commonDays = profile.DayOfWeekHistory.GroupBy(d => d)
                    .OrderByDescending(g => g.Count())
                    .Select(g => g.Key)
                    .Take(3)
                    .ToHashSet();

                if (!commonDays.Contains(pattern.DayOfWeek))
                {
                    anomalyTypes.Add("unusual-day");
                    score += 20.0;
                }
            }

            // Resource type anomaly
            if (profile.ResourceTypeHistory.Count >= 5)
            {
                var resourceFrequency = (double)profile.ResourceTypeHistory.Count(r => r == pattern.ResourceType) / profile.ResourceTypeHistory.Count;
                if (resourceFrequency < 0.05) // Less than 5% of historical accesses
                {
                    anomalyTypes.Add("unusual-resource-type");
                    score += 25.0;
                }
            }

            var isAnomaly = score >= 50.0 || anomalyTypes.Count >= 2;

            return Task.FromResult(new AnomalyDetectionResult
            {
                IsAnomaly = isAnomaly,
                AnomalyScore = Math.Min(score, 100.0),
                AnomalyTypes = anomalyTypes.ToArray(),
                Confidence = 0.75, // Statistical methods have moderate confidence
                AnalysisMethod = "Statistical",
                Details = isAnomaly
                    ? $"Detected {anomalyTypes.Count} anomalies: {string.Join(", ", anomalyTypes)}"
                    : "Access pattern within normal baseline"
            });
        }

        private void UpdateProfile(AccessPatternProfile profile, AccessPattern pattern)
        {
            profile.TotalAccesses++;
            profile.LastAccessAt = DateTime.UtcNow;

            // Update hour history (sliding window of 100)
            profile.HourHistory.Add(pattern.Hour);
            if (profile.HourHistory.Count > 100)
                profile.HourHistory.RemoveAt(0);

            // Update location history (sliding window of 50)
            if (!string.IsNullOrEmpty(pattern.Location))
            {
                profile.LocationHistory.Add(pattern.Location);
                if (profile.LocationHistory.Count > 50)
                    profile.LocationHistory.RemoveAt(0);
            }

            // Update access count history (sliding window of 100)
            profile.AccessCountHistory.Add(pattern.AccessCount);
            if (profile.AccessCountHistory.Count > 100)
                profile.AccessCountHistory.RemoveAt(0);

            // Update day of week history (sliding window of 50)
            profile.DayOfWeekHistory.Add(pattern.DayOfWeek);
            if (profile.DayOfWeekHistory.Count > 50)
                profile.DayOfWeekHistory.RemoveAt(0);

            // Update resource type history (sliding window of 100)
            profile.ResourceTypeHistory.Add(pattern.ResourceType);
            if (profile.ResourceTypeHistory.Count > 100)
                profile.ResourceTypeHistory.RemoveAt(0);
        }

        private double CalculateZScore(double value, List<int> history)
        {
            if (history.Count < 2)
                return 0.0;

            var mean = history.Average();
            var variance = history.Select(x => Math.Pow(x - mean, 2)).Average();
            var stdDev = Math.Sqrt(variance);

            if (stdDev < 0.01)
                return 0.0;

            return (value - mean) / stdDev;
        }

        private double CalculateZScore(int value, List<int> history)
        {
            return CalculateZScore((double)value, history);
        }

        /// <summary>
        /// Gets the access pattern profile for a subject.
        /// </summary>
        /// <param name="subjectId">Subject identifier.</param>
        /// <returns>Profile if exists, null otherwise.</returns>
        public AccessPatternProfile? GetProfile(string subjectId)
        {
            return _profiles.TryGetValue(subjectId, out var profile) ? profile : null;
        }

        /// <summary>
        /// Gets recent anomalies.
        /// </summary>
        /// <param name="maxCount">Maximum number of anomalies to return.</param>
        /// <returns>Recent anomalies.</returns>
        public IReadOnlyCollection<DetectedAnomaly> GetRecentAnomalies(int maxCount = 100)
        {
            return _recentAnomalies.Take(maxCount).ToList().AsReadOnly();
        }

        /// <summary>
        /// Clears profile history for a subject.
        /// </summary>
        /// <param name="subjectId">Subject identifier.</param>
        /// <returns>True if profile was found and cleared.</returns>
        public bool ClearProfile(string subjectId)
        {
            return _profiles.TryRemove(subjectId, out _);
        }
    }

    #region Supporting Types

    /// <summary>
    /// Access pattern snapshot for analysis.
    /// </summary>
    public sealed class AccessPattern
    {
        /// <summary>
        /// Access timestamp.
        /// </summary>
        public required DateTime Timestamp { get; init; }

        /// <summary>
        /// Number of accesses in current session/window.
        /// </summary>
        public required int AccessCount { get; init; }

        /// <summary>
        /// Hour of day (0-23).
        /// </summary>
        public required int Hour { get; init; }

        /// <summary>
        /// Day of week.
        /// </summary>
        public required DayOfWeek DayOfWeek { get; init; }

        /// <summary>
        /// Geographic location or IP address.
        /// </summary>
        public string? Location { get; init; }

        /// <summary>
        /// Action being performed.
        /// </summary>
        public required string Action { get; init; }

        /// <summary>
        /// Resource type being accessed.
        /// </summary>
        public required string ResourceType { get; init; }
    }

    /// <summary>
    /// Historical access pattern profile for a subject.
    /// </summary>
    public sealed class AccessPatternProfile
    {
        /// <summary>
        /// Subject identifier.
        /// </summary>
        public required string SubjectId { get; init; }

        /// <summary>
        /// Profile creation timestamp.
        /// </summary>
        public required DateTime CreatedAt { get; init; }

        /// <summary>
        /// Last access timestamp.
        /// </summary>
        public DateTime? LastAccessAt { get; set; }

        /// <summary>
        /// Total number of accesses.
        /// </summary>
        public int TotalAccesses { get; set; }

        /// <summary>
        /// Historical hour-of-day data.
        /// </summary>
        public List<int> HourHistory { get; init; } = new();

        /// <summary>
        /// Historical location data.
        /// </summary>
        public List<string> LocationHistory { get; init; } = new();

        /// <summary>
        /// Historical access count data.
        /// </summary>
        public List<int> AccessCountHistory { get; init; } = new();

        /// <summary>
        /// Historical day-of-week data.
        /// </summary>
        public List<DayOfWeek> DayOfWeekHistory { get; init; } = new();

        /// <summary>
        /// Historical resource type data.
        /// </summary>
        public List<string> ResourceTypeHistory { get; init; } = new();
    }

    /// <summary>
    /// Anomaly detection result.
    /// </summary>
    public sealed class AnomalyDetectionResult
    {
        /// <summary>
        /// Whether an anomaly was detected.
        /// </summary>
        public required bool IsAnomaly { get; init; }

        /// <summary>
        /// Anomaly score (0-100).
        /// </summary>
        public required double AnomalyScore { get; init; }

        /// <summary>
        /// Types of anomalies detected.
        /// </summary>
        public required string[] AnomalyTypes { get; init; }

        /// <summary>
        /// Confidence in detection (0.0-1.0).
        /// </summary>
        public required double Confidence { get; init; }

        /// <summary>
        /// Analysis method used (ML-Intelligence, Statistical, Statistical-Fallback).
        /// </summary>
        public required string AnalysisMethod { get; set; }

        /// <summary>
        /// Human-readable details.
        /// </summary>
        public required string Details { get; init; }
    }

    /// <summary>
    /// Detected anomaly record.
    /// </summary>
    public sealed class DetectedAnomaly
    {
        /// <summary>
        /// Anomaly identifier.
        /// </summary>
        public required string Id { get; init; }

        /// <summary>
        /// Subject identifier.
        /// </summary>
        public required string SubjectId { get; init; }

        /// <summary>
        /// Detection timestamp.
        /// </summary>
        public required DateTime DetectedAt { get; init; }

        /// <summary>
        /// Anomaly score.
        /// </summary>
        public required double AnomalyScore { get; init; }

        /// <summary>
        /// Anomaly types.
        /// </summary>
        public required string[] AnomalyTypes { get; init; }

        /// <summary>
        /// Analysis method used.
        /// </summary>
        public required string AnalysisMethod { get; init; }
    }

    #endregion
}
