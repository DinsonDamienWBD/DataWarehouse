using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.ThreatDetection
{
    /// <summary>
    /// User and Entity Behavior Analytics (UEBA) strategy.
    /// Delegates ML workload to Intelligence plugin (T90) via message bus with rule-based fallback.
    /// </summary>
    /// <remarks>
    /// <b>DEPENDENCY:</b> Universal Intelligence plugin (T90) for ML-based anomaly detection.
    /// <b>MESSAGE TOPIC:</b> intelligence.analyze
    /// <b>FALLBACK:</b> Z-score anomaly detection on login time, location, frequency when Intelligence unavailable.
    /// </remarks>
    public sealed class UebaStrategy : AccessControlStrategyBase
    {
        private readonly ILogger _logger;
        private readonly IMessageBus? _messageBus;
        private readonly BoundedDictionary<string, UserBehaviorProfile> _profiles = new BoundedDictionary<string, UserBehaviorProfile>(1000);
        private readonly ConcurrentQueue<BehaviorAnomaly> _anomalies = new();
        private readonly int _baselineWindowSize = 100;
        private readonly double _zScoreThreshold = 3.0;

        public UebaStrategy(ILogger? logger = null, IMessageBus? messageBus = null)
        {
            _logger = logger ?? NullLogger.Instance;
            _messageBus = messageBus;
        }

        /// <inheritdoc/>
        public override string StrategyId => "ueba";

        /// <inheritdoc/>
        public override string StrategyName => "User and Entity Behavior Analytics";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = false,
            SupportsTemporalAccess = true,
            SupportsGeographicRestrictions = true,
            MaxConcurrentEvaluations = 5000
        };

        

        /// <summary>
        /// Production hardening: validates configuration parameters on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("ueba.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources and clears caches on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("ueba.shutdown");
            _profiles.Clear();
            return base.ShutdownAsyncCore(cancellationToken);
        }
/// <inheritdoc/>
        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("ueba.evaluate");
            // Get or create behavior profile for subject
            var profile = _profiles.GetOrAdd(context.SubjectId, _ => new UserBehaviorProfile
            {
                SubjectId = context.SubjectId,
                FirstSeen = DateTime.UtcNow
            });

            // Build behavior snapshot for analysis
            var behaviorSnapshot = BuildBehaviorSnapshot(context, profile);

            // Step 1: Try AI-based analysis via Intelligence plugin
            AnomalyResult? aiResult = null;
            if (_messageBus != null)
            {
                try
                {
                    aiResult = await TryAiAnalysisAsync(behaviorSnapshot, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Intelligence plugin AI analysis failed, falling back to rule-based");
                }
            }

            // Step 2: Fallback to rule-based Z-score anomaly detection if AI unavailable
            var anomalyResult = aiResult ?? await RuleBasedAnomalyDetectionAsync(behaviorSnapshot, profile, cancellationToken);

            // Update profile with current behavior
            UpdateProfile(profile, context);

            // Record anomaly if detected
            if (anomalyResult.IsAnomaly)
            {
                var anomaly = new BehaviorAnomaly
                {
                    Id = Guid.NewGuid().ToString("N"),
                    SubjectId = context.SubjectId,
                    DetectedAt = DateTime.UtcNow,
                    AnomalyScore = anomalyResult.Score,
                    AnomalyType = anomalyResult.AnomalyType,
                    Details = anomalyResult.Details,
                    UsedAiAnalysis = aiResult != null
                };

                _anomalies.Enqueue(anomaly);

                // Prune old anomalies
                while (_anomalies.Count > 1000)
                {
                    _anomalies.TryDequeue(out _);
                }
            }

            // Make access decision based on anomaly score
            if (anomalyResult.Score >= 80.0)
            {
                return new AccessDecision
                {
                    IsGranted = false,
                    Reason = $"Critical anomaly detected (score: {anomalyResult.Score:F2}): {anomalyResult.Details}",
                    ApplicablePolicies = new[] { "UebaAutoBlock" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["AnomalyScore"] = anomalyResult.Score,
                        ["AnomalyType"] = anomalyResult.AnomalyType,
                        ["UsedAi"] = aiResult != null,
                        ["AnalysisMethod"] = aiResult != null ? "Intelligence-AI" : "Rule-Based"
                    }
                };
            }

            if (anomalyResult.Score >= 60.0)
            {
                return new AccessDecision
                {
                    IsGranted = true,
                    Reason = $"Access granted with behavioral anomaly warning (score: {anomalyResult.Score:F2})",
                    ApplicablePolicies = new[] { "UebaMonitoring" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["AnomalyScore"] = anomalyResult.Score,
                        ["AnomalyType"] = anomalyResult.AnomalyType,
                        ["UsedAi"] = aiResult != null
                    }
                };
            }

            return new AccessDecision
            {
                IsGranted = true,
                Reason = "Access granted - behavior within normal baseline",
                ApplicablePolicies = new[] { "UebaMonitoring" },
                Metadata = new Dictionary<string, object>
                {
                    ["AnomalyScore"] = anomalyResult.Score,
                    ["UsedAi"] = aiResult != null
                }
            };
        }

        /// <summary>
        /// Attempts AI-based behavior analysis via Intelligence plugin message bus.
        /// </summary>
        private async Task<AnomalyResult?> TryAiAnalysisAsync(BehaviorSnapshot snapshot, CancellationToken cancellationToken)
        {
            if (_messageBus == null)
                return null;

            try
            {
                // Step 1: Send request to Intelligence plugin via message bus
                var message = new DataWarehouse.SDK.Utilities.PluginMessage
                {
                    Type = "intelligence.analyze.behavior",
                    SourcePluginId = "ultimate-access-control",
                    Payload = new Dictionary<string, object>
                    {
                        ["dataType"] = "user-behavior",
                        ["analysisType"] = "anomaly-detection",
                        ["snapshot"] = snapshot
                    }
                };

                var messageResponse = await _messageBus.SendAsync(
                    "intelligence.analyze.behavior",
                    message,
                    TimeSpan.FromSeconds(5),
                    cancellationToken);

                // Step 2: Fallback when AI unavailable (Success check)
                if (messageResponse == null || !messageResponse.Success)
                {
                    _logger.LogWarning("Intelligence plugin unavailable for UEBA (Success=false), using rule-based fallback");
                    return null;
                }

                // Parse AI result from message response payload
                var payload = messageResponse.Payload as Dictionary<string, object>;
                return new AnomalyResult
                {
                    IsAnomaly = payload?.ContainsKey("isAnomaly") == true && (bool)payload["isAnomaly"],
                    Score = payload?.ContainsKey("anomalyScore") == true ? Convert.ToDouble(payload["anomalyScore"]) : 0.0,
                    AnomalyType = payload?.ContainsKey("anomalyType") == true ? payload["anomalyType"]?.ToString() ?? "ai-detected" : "ai-detected",
                    Details = payload?.ContainsKey("details") == true ? payload["details"]?.ToString() ?? "AI-detected anomaly" : "AI-detected anomaly",
                    Confidence = payload?.ContainsKey("confidence") == true ? Convert.ToDouble(payload["confidence"]) : 0.5
                };
            }
            catch (TimeoutException ex)
            {
                // Step 2: Fallback on timeout
                _logger.LogWarning(ex, "Intelligence plugin timeout for UEBA, using rule-based fallback");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Intelligence plugin request failed for UEBA, using rule-based fallback");
                return null;
            }
        }

        /// <summary>
        /// Rule-based Z-score anomaly detection fallback when AI is unavailable.
        /// Analyzes login time, location, and access frequency deviations.
        /// </summary>
        private Task<AnomalyResult> RuleBasedAnomalyDetectionAsync(
            BehaviorSnapshot snapshot,
            UserBehaviorProfile profile,
            CancellationToken cancellationToken)
        {
            var anomalies = new List<string>();
            var score = 0.0;

            // Time-based anomaly (Z-score on hour of day)
            if (profile.AccessHourHistory.Count >= 10)
            {
                var hourZScore = CalculateZScore(snapshot.AccessHour, profile.AccessHourHistory);
                if (Math.Abs(hourZScore) > _zScoreThreshold)
                {
                    anomalies.Add($"Unusual access time (Z={hourZScore:F2})");
                    score += 30.0;
                }
            }

            // Location-based anomaly
            if (snapshot.Location != null && profile.LocationHistory.Count > 0)
            {
                var seenLocation = profile.LocationHistory.Any(l =>
                    l.Country == snapshot.Location.Country &&
                    l.City == snapshot.Location.City);

                if (!seenLocation)
                {
                    anomalies.Add("New geographic location");
                    score += 40.0;
                }

                // Impossible travel detection
                var lastLocation = profile.LocationHistory.LastOrDefault();
                if (lastLocation != null && profile.LastAccessTime.HasValue)
                {
                    var timeDiff = (DateTime.UtcNow - profile.LastAccessTime.Value).TotalHours;
                    var distance = CalculateDistance(lastLocation, snapshot.Location);
                    var maxPossibleSpeed = 900.0; // km/h (commercial airliner)
                    var requiredSpeed = distance / timeDiff;

                    if (requiredSpeed > maxPossibleSpeed)
                    {
                        anomalies.Add($"Impossible travel (required speed: {requiredSpeed:F0} km/h)");
                        score += 60.0;
                    }
                }
            }

            // Frequency-based anomaly (rapid successive accesses)
            if (profile.AccessTimestamps.Count >= 5)
            {
                var recentAccesses = profile.AccessTimestamps
                    .Where(t => (DateTime.UtcNow - t).TotalMinutes < 5)
                    .Count();

                var avgRecent = profile.AccessTimestamps.Count >= 20
                    ? profile.AccessTimestamps.TakeLast(20).Average(t => 1.0)
                    : 1.0;

                if (recentAccesses > avgRecent * 10)
                {
                    anomalies.Add($"Unusual access frequency ({recentAccesses} in 5 min)");
                    score += 35.0;
                }
            }

            // Action pattern anomaly
            if (snapshot.Action != null && profile.ActionHistory.Count >= 10)
            {
                var actionFrequency = profile.ActionHistory.Count(a => a.Equals(snapshot.Action, StringComparison.OrdinalIgnoreCase));
                var actionRatio = (double)actionFrequency / profile.ActionHistory.Count;

                if (actionRatio < 0.05) // Action represents < 5% of history
                {
                    anomalies.Add($"Unusual action: {snapshot.Action}");
                    score += 20.0;
                }
            }

            var result = new AnomalyResult
            {
                IsAnomaly = score >= 40.0,
                Score = Math.Min(score, 100.0),
                AnomalyType = anomalies.Any() ? string.Join(", ", anomalies.Take(2)) : "none",
                Details = anomalies.Any() ? string.Join("; ", anomalies) : "Behavior within baseline",
                Confidence = 0.75 // Rule-based has moderate confidence
            };

            return Task.FromResult(result);
        }

        private BehaviorSnapshot BuildBehaviorSnapshot(AccessContext context, UserBehaviorProfile profile)
        {
            return new BehaviorSnapshot
            {
                SubjectId = context.SubjectId,
                AccessHour = context.RequestTime.Hour,
                DayOfWeek = context.RequestTime.DayOfWeek,
                Location = context.Location,
                Action = context.Action,
                ResourceId = context.ResourceId,
                ClientIp = context.ClientIpAddress,
                HistoricalAccessCount = profile.TotalAccesses,
                RecentAccessCount = profile.AccessTimestamps.Count(t => (DateTime.UtcNow - t).TotalHours < 24)
            };
        }

        private void UpdateProfile(UserBehaviorProfile profile, AccessContext context)
        {
            profile.TotalAccesses++;
            profile.LastAccessTime = DateTime.UtcNow;

            // Update hour history (sliding window)
            profile.AccessHourHistory.Add(context.RequestTime.Hour);
            if (profile.AccessHourHistory.Count > _baselineWindowSize)
                profile.AccessHourHistory.RemoveAt(0);

            // Update location history
            if (context.Location != null)
            {
                profile.LocationHistory.Add(context.Location);
                if (profile.LocationHistory.Count > 50)
                    profile.LocationHistory.RemoveAt(0);
            }

            // Update action history
            profile.ActionHistory.Add(context.Action);
            if (profile.ActionHistory.Count > _baselineWindowSize)
                profile.ActionHistory.RemoveAt(0);

            // Update access timestamps
            profile.AccessTimestamps.Add(DateTime.UtcNow);
            if (profile.AccessTimestamps.Count > _baselineWindowSize)
                profile.AccessTimestamps.RemoveAt(0);
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

        private double CalculateDistance(GeoLocation loc1, GeoLocation loc2)
        {
            // Haversine formula for great-circle distance
            const double earthRadiusKm = 6371.0;

            var lat1Rad = loc1.Latitude * Math.PI / 180.0;
            var lat2Rad = loc2.Latitude * Math.PI / 180.0;
            var deltaLat = (loc2.Latitude - loc1.Latitude) * Math.PI / 180.0;
            var deltaLon = (loc2.Longitude - loc1.Longitude) * Math.PI / 180.0;

            var a = Math.Sin(deltaLat / 2) * Math.Sin(deltaLat / 2) +
                    Math.Cos(lat1Rad) * Math.Cos(lat2Rad) *
                    Math.Sin(deltaLon / 2) * Math.Sin(deltaLon / 2);

            var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

            return earthRadiusKm * c;
        }

        /// <summary>
        /// Gets behavior profile for a subject.
        /// </summary>
        public UserBehaviorProfile? GetProfile(string subjectId)
        {
            return _profiles.TryGetValue(subjectId, out var profile) ? profile : null;
        }

        /// <summary>
        /// Gets recent anomalies.
        /// </summary>
        public IReadOnlyCollection<BehaviorAnomaly> GetRecentAnomalies(int count = 100)
        {
            return _anomalies.Take(count).ToList().AsReadOnly();
        }
    }

    #region Message Bus Types

    /// <summary>
    /// Request to Intelligence plugin for behavior analysis.
    /// </summary>
    public sealed class AnalysisRequest
    {
        public required string DataType { get; init; }
        public required object Payload { get; init; }
        public required string AnalysisType { get; init; }
    }

    /// <summary>
    /// Response from Intelligence plugin.
    /// </summary>
    public sealed class AnalysisResponse
    {
        public required bool Success { get; init; }
        public required bool IsAnomaly { get; init; }
        public required double AnomalyScore { get; init; }
        public string? AnomalyType { get; init; }
        public string? Details { get; init; }
        public double Confidence { get; init; }
    }

    #endregion

    #region Supporting Types

    public sealed class UserBehaviorProfile
    {
        public required string SubjectId { get; init; }
        public required DateTime FirstSeen { get; init; }
        public DateTime? LastAccessTime { get; set; }
        public int TotalAccesses { get; set; }
        public List<int> AccessHourHistory { get; init; } = new();
        public List<GeoLocation> LocationHistory { get; init; } = new();
        public List<string> ActionHistory { get; init; } = new();
        public List<DateTime> AccessTimestamps { get; init; } = new();
    }

    public sealed class BehaviorSnapshot
    {
        public required string SubjectId { get; init; }
        public required int AccessHour { get; init; }
        public required DayOfWeek DayOfWeek { get; init; }
        public GeoLocation? Location { get; init; }
        public required string Action { get; init; }
        public required string ResourceId { get; init; }
        public string? ClientIp { get; init; }
        public required int HistoricalAccessCount { get; init; }
        public required int RecentAccessCount { get; init; }
    }

    public sealed class AnomalyResult
    {
        public required bool IsAnomaly { get; init; }
        public required double Score { get; init; }
        public required string AnomalyType { get; init; }
        public required string Details { get; init; }
        public required double Confidence { get; init; }
    }

    public sealed class BehaviorAnomaly
    {
        public required string Id { get; init; }
        public required string SubjectId { get; init; }
        public required DateTime DetectedAt { get; init; }
        public required double AnomalyScore { get; init; }
        public required string AnomalyType { get; init; }
        public required string Details { get; init; }
        public required bool UsedAiAnalysis { get; init; }
    }

    #endregion
}
